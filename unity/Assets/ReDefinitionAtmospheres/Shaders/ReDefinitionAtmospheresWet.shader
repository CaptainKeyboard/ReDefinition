// Surface wetness of ReDefinition Atmospheres (package T08, src/Atmospheres/Cover/Wet): wet ground is darker and glossier, puddles stand
// in the hollows and flatten the surface, rain makes rings on them. The pass writes into Unity's deferred G-buffer after the opaque
// geometry is in it and before the lights read it (CameraEvent.BeforeLighting, the place Unity's own deferred-decal sample uses), so
// that whatever shader a surface uses (Parallax terrain, scenery, vessel parts) is lit with the wet values and no foreign shader is
// edited: an overlay by depth.
//
//   pass 0  G-buffer overlay (three render targets, blended, no read of the G-buffer):
//             target 0 (diffuse, occlusion)   rgb multiplied by the diffuse factor of the wet ground or the puddle
//             target 1 (specular, smoothness) smoothness (alpha) raised; the specular colour is untouched (colour mask A)
//             target 2 (normal)               rgb blended towards the flat puddle normal with the rings (alpha = the puddle weight)
//             target 3 (camera target)       rgb multiplied by the same diffuse factor: at this event the camera's target holds the
//                                             emission and the ambient light times the albedo that the geometry pass baked in (the
//                                             weather player measured that the G-buffer's own fourth texture is not it at this event),
//                                             so without this the wet ground would darken in the sun's light only (a self-lit part on
//                                             wet ground is darkened with it; ground has no emission) [VERIFY with the installed
//                                             Deferred mod, whose lighting shaders are not in the player]
//   pass 1  aid map: the same arithmetic written as a picture, R = local wetness, G = puddle share, B = up-facing share, A = ripples
//           (the weather player's second view of every wetness picture; the in-game contribution mask is atmoDebug = contrib).
//
// The world position comes from the depth texture and the camera's projection (the reconstruction of the screen-space vehicle cover,
// measured in the player), the face from the derivatives of that position, "up" from the body's centre so that puddles lie on level
// ground on a round body. The depression field of the puddles is world-fixed noise in the body-fixed east/north frame, so it does not
// slide when the floating origin moves. Pixels where the surface turns away from up (slopes) and depth jumps (silhouettes) get no
// puddle and a reduced wetness.
//
// Motion vectors: none, only colours of the G-buffer change; the upscaler's vectors are the surfaces' own. Depth: read, not written.
// Draw order: before lighting. Masks: none for DLSS (a surface texture changes slowly with the weather). Frame generation: the change is
// part of the lit picture. Raytracing: the raytracer's G-buffer copy is taken after the G-buffer and BEFORE this pass (the camera
// event BeforeLighting is later), so rays see dry ground [report in T08]. Exposure: the factors are relative, no light is added.
Shader "ReDefinition/AtmospheresWet"
{
    Properties
    {
    }

    CGINCLUDE
    #include "UnityCG.cginc"

    sampler2D_float _CameraDepthTexture;

    // x = diffuse factor of wet ground (1 dry), y = smoothness added, z = the depression field's threshold under which water stands
    // (0: no puddles), w = ripples 0..1.
    float4 _WetA;
    // x = diffuse factor in a puddle, y = smoothness of a puddle, z = frozen share, w = the clock in seconds (the rings).
    float4 _WetB;
    // xyz = the body's centre relative to the camera (m), w = unused.
    float4 _WetCentre;
    // xyz = body-fixed east axis in the world, w = camera's body-fixed east position mod 8192 m.
    float4 _WetEast;
    // xyz = body-fixed north axis in the world, w = camera's body-fixed north position mod 8192 m.
    float4 _WetNorth;
    // x = scale of the depression noise (cycles per m for the biggest octave), y = puddle edge softness, z = least up-facing share a
    // puddle needs, w = global strength 0..1 (the A/B switch writes 0).
    float4 _WetC;
    // 1 leaves a G-buffer target as it is (x diffuse, y smoothness, z normal, w emission and ambient); 0 writes it. The weather player's
    // diagnosis of which target carries which part of the picture; the mod always writes 0.
    float4 _WetT;

    #ifndef REDEFINITION_ATMO_CONTRIBUTION_INCLUDED
    #define REDEFINITION_ATMO_CONTRIBUTION_INCLUDED
    float _AtmoDebug;
    #endif

    struct v2f
    {
        float4 pos : SV_POSITION;
    };

    v2f vertProc(uint vid : SV_VertexID)
    {
        v2f o;
        float2 p = float2((vid << 1) & 2, vid & 2);
        o.pos = float4(p * 2 - 1, 0.5, 1);
        return o;
    }

    // The 32-bit finaliser of MurmurHash3 (Austin Appleby, public domain) as the integer hash of a lattice cell; the cell index is the
    // integer part of the argument, so the pattern is the same on every platform.
    uint Mix32(uint h)
    {
        h ^= h >> 16;
        h *= 0x85ebca6bu;
        h ^= h >> 13;
        h *= 0xc2b2ae35u;
        h ^= h >> 16;
        return h;
    }

    float Hash12(float2 p)
    {
        uint2 q = (uint2)((int2)floor(p));
        return Mix32(q.x + Mix32(q.y ^ 0x9e3779b9u)) * (1.0 / 4294967296.0);
    }

    float2 Hash22(float2 p)
    {
        uint2 q = (uint2)((int2)floor(p));
        uint a = Mix32(q.x + Mix32(q.y ^ 0x9e3779b9u));
        uint b = Mix32(a + 0x7f4a7c15u);
        return float2(a, b) * (1.0 / 4294967296.0);
    }

    // Value noise whose lattice repeats every `period` cells, so that the pattern is the same on both sides of the wrap of the
    // body-fixed offset (the offset wraps every 8192 m, which is a whole number of cells of every octave below).
    float ValueNoise(float2 p, float period)
    {
        float2 i = floor(p), f = frac(p);
        float2 u = f * f * (3.0 - 2.0 * f);
        float2 i0 = i - period * floor(i / period);
        float2 i1 = i + 1.0;
        i1 = i1 - period * floor(i1 / period);
        float a = Hash12(i0), b = Hash12(float2(i1.x, i0.y)), c = Hash12(float2(i0.x, i1.y)), d = Hash12(i1);
        return lerp(lerp(a, b, u.x), lerp(c, d, u.x), u.y);
    }

    // The depression field 0..1: low values are hollows. Three octaves at 1, 4 and 16 times the base frequency (32 m, 8 m and 2 m
    // features); the lattice periods are the cells in 8192 m.
    float Depression(float2 p)
    {
        float s = _WetC.x;
        float period = round(8192.0 * s);
        float n = 0.55 * ValueNoise(p * s, period)
                + 0.30 * ValueNoise(p * s * 4.0 + 17.0, period * 4.0)
                + 0.15 * ValueNoise(p * s * 16.0 + 41.0, period * 16.0);
        return n;
    }

    // Rain rings on the water: a ring per cell that grows and fades over a second; returns the slope (xy) of the surface.
    float2 Rings(float2 p, float t)
    {
        float2 slope = float2(0, 0);
        for (int layer = 0; layer < 2; layer++)
        {
            float cellSize = layer == 0 ? 0.55 : 0.31;
            float2 q = p / cellSize + layer * 7.3;
            float2 cell = floor(q);
            float2 f = frac(q) - 0.5;
            float2 h = Hash22(cell + layer * 3.1);
            float phase = frac(t * (0.8 + 0.4 * h.x) + h.y);
            // The ring stays inside its cell (centre within 0.2 of the middle, radius at most 0.22, a tail of 0.06), so that no ring is
            // cut off by the lattice.
            float2 c = (h - 0.5) * 0.4;
            float2 d = f - c;
            float r = length(d);
            float radius = phase * 0.22;
            float ring = exp(-pow((r - radius) * 18.0, 2.0)) * (1.0 - phase) * (1.0 - phase);
            slope += (r > 1e-4 ? d / r : float2(0, 0)) * ring * 0.55;
        }
        return slope;
    }

    // The wet state of the pixel under the camera, or false where it is the sky. Everything the passes need.
    struct WetPixel
    {
        float wet;      // local wetness 0..1
        float puddle;   // puddle share of the pixel 0..1
        float upFacing; // 0..1
        float3 normal;  // the normal of the puddle surface in the world (flat, with rings)
        float ripples;
    };

    bool Evaluate(float2 uv, out WetPixel w)
    {
        w.wet = 0; w.puddle = 0; w.upFacing = 0; w.normal = float3(0, 1, 0); w.ripples = 0;
        float raw = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, uv);
        #if defined(UNITY_REVERSED_Z)
        bool sky = raw <= 0.0;
        #else
        bool sky = raw >= 1.0;
        #endif
        float d = LinearEyeDepth(raw);
        float ndcX = uv.x * 2.0 - 1.0;
        #if defined(UNITY_UV_STARTS_AT_TOP)
        float ndcY = (1.0 - uv.y * 2.0) * _ProjectionParams.x;
        #else
        float ndcY = uv.y * 2.0 - 1.0;
        #endif
        float3 viewPos = float3((ndcX + unity_CameraProjection._m02) * d / unity_CameraProjection._m00,
                                (ndcY + unity_CameraProjection._m12) * d / unity_CameraProjection._m11, -d);
        // The world offset from the camera (the camera's axes are the rows of the view matrix).
        float3 rel = UNITY_MATRIX_V[0].xyz * viewPos.x + UNITY_MATRIX_V[1].xyz * viewPos.y - UNITY_MATRIX_V[2].xyz * d;

        float3 dx = ddx(rel);
        float3 dy = ddy(rel);
        float3 n = normalize(cross(dy, dx));
        if (dot(n, rel) > 0.0) n = -n;
        float jump = max(abs(ddx(d)), abs(ddy(d)));
        // Derivatives are taken above, before the sky returns, so that every pixel of a quad runs them.
        if (sky) return false;
        float edgeOk = step(jump, max(0.5, 0.25 * d));
        float3 up = normalize(rel - _WetCentre.xyz);
        float upn = saturate(dot(n, up));
        float upFacing = lerp(0.6, upn, edgeOk);

        // Water lands on faces that look up fully and on walls half as much.
        float exposure = 0.55 + 0.45 * upFacing;
        w.wet = exposure * _WetC.w;
        w.upFacing = upFacing;

        // Puddles: where the depression field is under the puddle share, on level ground that is not a depth edge.
        float2 pm = float2(dot(rel, _WetEast.xyz) + _WetEast.w, dot(rel, _WetNorth.xyz) + _WetNorth.w);
        float field = Depression(pm);
        float soft = max(_WetC.y, 0.005);
        float inPuddle = 1.0 - smoothstep(_WetA.z - soft, _WetA.z + soft, field);
        float level = smoothstep(_WetC.z, min(_WetC.z + 0.04, 0.999), upn) * edgeOk;
        w.puddle = inPuddle * level * step(0.001, _WetA.z) * _WetC.w;

        // The puddle surface: flat (up), with the rings' slope when it rains. The aid map's ripple channel is the rings' slope itself.
        float3 east = normalize(_WetEast.xyz), north = normalize(_WetNorth.xyz);
        float2 slope = Rings(pm, _WetB.w) * _WetA.w * (1.0 - _WetB.z);
        w.ripples = saturate(length(slope) * 4.0);
        w.normal = normalize(up - east * slope.x - north * slope.y);
        return true;
    }
    ENDCG

    SubShader
    {
        Tags { "Queue" = "Overlay" "RenderType" = "Opaque" "IgnoreProjector" = "True" }
        Cull Off
        ZWrite Off
        ZTest Always

        // 0: the G-buffer overlay.
        Pass
        {
            Name "WetGBuffer"
            Blend 0 DstColor Zero
            Blend 1 One One
            Blend 2 SrcAlpha OneMinusSrcAlpha
            Blend 3 DstColor Zero
            ColorMask RGB 0
            ColorMask A 1
            ColorMask RGB 2
            ColorMask RGB 3

            CGPROGRAM
            #pragma target 4.0
            #pragma vertex vertProc
            #pragma fragment frag

            struct Out
            {
                float4 gb0 : SV_Target0;
                float4 gb1 : SV_Target1;
                float4 gb2 : SV_Target2;
                float4 gb3 : SV_Target3;
            };

            Out frag(v2f i)
            {
                Out o;
                float2 uv = i.pos.xy / _ScreenParams.xy;
                WetPixel w;
                if (!Evaluate(uv, w)) discard;
                float ground = lerp(1.0, _WetA.x, w.wet);
                float diffuse = lerp(ground, ground * _WetB.x, w.puddle);
                float smoothAdd = _WetA.y * w.wet;
                smoothAdd = lerp(smoothAdd, _WetB.y, w.puddle);
                float d0 = lerp(diffuse, 1.0, _WetT.x);
                o.gb0 = float4(d0, d0, d0, 1);
                o.gb1 = float4(0, 0, 0, smoothAdd * (1.0 - _WetT.y));
                o.gb2 = float4(w.normal * 0.5 + 0.5, w.puddle * 0.92 * (1.0 - _WetT.z));
                // The fourth target holds the emission and the ambient light the geometry pass already added (the indirect diffuse is
                // baked into it times the albedo); the wet ground reflects less of that light too, so it is scaled by the same factor.
                float d3 = lerp(diffuse, 1.0, _WetT.w);
                o.gb3 = float4(d3, d3, d3, 1);
                return o;
            }
            ENDCG
        }

        // 1: the aid map.
        Pass
        {
            Name "WetMap"
            Blend Off

            CGPROGRAM
            #pragma target 4.0
            #pragma vertex vertProc
            #pragma fragment frag

            float4 frag(v2f i) : SV_Target
            {
                float2 uv = i.pos.xy / _ScreenParams.xy;
                WetPixel w;
                if (!Evaluate(uv, w)) return float4(0, 0, 0, 0);
                return float4(w.wet, w.puddle, w.upFacing, w.ripples * w.puddle);
            }
            ENDCG
        }
    }
    Fallback Off
}
