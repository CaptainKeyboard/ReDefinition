// The precipitation renderer's shader: the veil, the flakes, the splashes and the two small passes that read the image's luminance.
// One look implementation for both ways of composing: Scene draws it in the scene before the upscaler (hardware depth test,
// _PrecipDepthTexel.w = 0), Host draws it after the upscaler (depth from the host's render-size copy, nearest of 3x3 taps). Only the
// depth test and the colour mode of the veil differ.
//
// Flakes are procedural instanced billboards: a flake needs no buffer, its place comes from a hash of its instance number and the
// seed (the same hash as PrecipMath.Hash in the mod), in a box centred on the camera that wraps. Nothing is random per frame.
// No motion vectors are written for the flakes: the upscaler must not track them.
Shader "ReDefinition/AtmospheresPrecip"
{
    Properties
    {
        [Enum(UnityEngine.Rendering.CompareFunction)] _ZTest ("ZTest", Float) = 4
    }

    CGINCLUDE
    #include "UnityCG.cginc"

    sampler2D _MainTex;
    float4 _MainTex_TexelSize;
    sampler2D _PrecipSheet;
    sampler2D _PrecipDepth;
    sampler2D _PrecipLuma;
    sampler2D _PrecipPrev;
    float _PrecipBlend;

    float4x4 _PrecipV;
    float4x4 _PrecipP;
    float _PrecipUseMat;
    // xy = the size of the target in pixels, z = flip the depth lookup in y, w = 1 where the depth texture is used.
    float4 _PrecipTarget;
    // xy = one texel of the depth texture.
    float4 _PrecipDepthTexel;

    // The box frame in the world: east, up, north.
    float4 _PrecipAxisE;
    float4 _PrecipAxisU;
    float4 _PrecipAxisN;

    // x = box width, y = box height, z = edge fade share, w = seed.
    float4 _PrecipLayerBox;
    // x = size min, y = size max, z = opacity, w = layer salt.
    float4 _PrecipLayerSize;
    // The drift of the pattern in box units.
    float4 _PrecipDrift;
    // The camera's place in the box in box units; w = time mod 60 seconds.
    float4 _PrecipCamBox;
    // The velocity in the frame (east, up, north) in m/s; w = sway in metres.
    float4 _PrecipVel;
    // rgb = colour times light; a = the weight of this composite (cross-fade) times the type's opacity.
    float4 _PrecipColour;
    // x = streak length per m/s, y = floor in pixels at 1080 lines, z = lowest contrast of a floored flake, w = sheet cell.
    float4 _PrecipLook;

    // x = ground alpha, y = sky alpha, z = sin(sky grey start), w = sin(sky grey full).
    float4 _PrecipVeilA;
    float4 _PrecipVeilGround;
    float4 _PrecipVeilSky;
    // x = storm grade share, y = lowest luminance of a luminance relative veil, z = 1 where the veil colour is relative to the image,
    // w = gain on the image luminance.
    float4 _PrecipVeilGrade;

    // x = ground distance below the camera, y = range (half width) of the splash field, z = ring size, w = rings per second.
    float4 _PrecipSplash;
    // x, y = the camera's place on the ground field in field units; z = seed.
    float4 _PrecipSplashCam;

    static const float2 Q[6] = { float2(-1, -1), float2(1, -1), float2(1, 1), float2(-1, -1), float2(1, 1), float2(-1, 1) };

    // The lowbias32 mixer of Chris Wellons' hash-prospector (The Unlicense), as PrecipMath.Hash.
    uint H(uint x)
    {
        x ^= x >> 16;
        x *= 0x7feb352du;
        x ^= x >> 15;
        x *= 0x846ca68bu;
        x ^= x >> 16;
        return x;
    }

    uint H3(uint cell, uint seed, uint salt)
    {
        return H(H(cell + 0x9e3779b9u * (salt + 1u)) ^ (seed * 0x85ebca6bu));
    }

    float U(uint h)
    {
        return (h >> 8) * (1.0 / 16777216.0);
    }

    float4x4 ViewM() { return _PrecipUseMat > 0.5 ? _PrecipV : UNITY_MATRIX_V; }
    float4x4 ProjM() { return _PrecipUseMat > 0.5 ? _PrecipP : UNITY_MATRIX_P; }

    float Luma(float3 c) { return dot(c, float3(0.2126, 0.7152, 0.0722)); }

    // The nearest of 3x3 taps of the host's render-size depth: the closest surface around the pixel, so that a silhouette edge hides
    // a flake rather than lets it through. Raw hardware depth, so reversed on this platform where UNITY_REVERSED_Z is set.
    float NearestDepth(float2 uv)
    {
        float2 t = _PrecipDepthTexel.xy;
        float d = tex2Dlod(_PrecipDepth, float4(uv, 0, 0)).r;
        for (int y = -1; y <= 1; y++)
        {
            for (int x = -1; x <= 1; x++)
            {
                float s = tex2Dlod(_PrecipDepth, float4(uv + float2(x, y) * t, 0, 0)).r;
                #if defined(UNITY_REVERSED_Z)
                d = max(d, s);
                #else
                d = min(d, s);
                #endif
            }
        }
        return d;
    }

    // 1 where the flake at this pixel and depth is in front of the scene (or the scene's depth is not used), 0 where it is hidden.
    float Visible(float4 pos)
    {
        if (_PrecipTarget.w < 0.5) return 1;
        float2 uv = pos.xy / _PrecipTarget.xy;
        if (_PrecipTarget.z > 0.5) uv.y = 1 - uv.y;
        float scene = NearestDepth(uv);
        #if defined(UNITY_REVERSED_Z)
        return pos.z >= scene ? 1 : 0;
        #else
        return pos.z <= scene ? 1 : 0;
        #endif
    }

    float EdgeFade(float w, float share)
    {
        float d = min(w, 1 - w) / max(share, 1e-4);
        d = saturate(d);
        return d * d * (3 - 2 * d);
    }

    struct pv
    {
        float4 pos : SV_POSITION;
        float2 uv : TEXCOORD0;
        float alpha : TEXCOORD1;
    };

    float2 SheetUV(float2 q, float cell)
    {
        float2 origin = float2(fmod(cell, 2.0), floor(cell * 0.5)) * 0.5;
        return (q * 0.5 + 0.5) * 0.5 + origin;
    }
    ENDCG

    SubShader
    {
        Tags { "Queue" = "Overlay" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Cull Off
        ZWrite Off

        // 0: the veil. A full-screen triangle, alpha by the elevation of the ray: the ground alpha toward the horizon and below, the
        // sky alpha toward the zenith. No depth. The storm grade (grey, cool, flat) is the blend itself plus a desaturation of the colour
        // by the veil's alpha, so no volume of the post-processing is edited.
        Pass
        {
            Name "Veil"
            ZTest Always
            Blend SrcAlpha OneMinusSrcAlpha

            CGPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment frag

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 ndc : TEXCOORD0;
            };

            v2f vert(uint vid : SV_VertexID)
            {
                v2f o;
                float2 p = float2((vid << 1) & 2, vid & 2);
                o.ndc = p * 2 - 1;
                o.pos = float4(o.ndc, UNITY_NEAR_CLIP_VALUE, 1);
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                float4x4 P = ProjM();
                float4x4 V = ViewM();
                float3 dirView = float3(i.ndc.x / P[0][0], i.ndc.y / P[1][1], -1);
                float3 dirWorld = mul(transpose((float3x3)V), dirView);
                float sinEl = dot(normalize(dirWorld), _PrecipAxisU.xyz);
                float share = smoothstep(_PrecipVeilA.z, _PrecipVeilA.w, sinEl);
                float alpha = lerp(_PrecipVeilA.x, _PrecipVeilA.y, share) * _PrecipColour.a;
                float3 colour = lerp(_PrecipVeilGround.rgb, _PrecipVeilSky.rgb, share);
                if (_PrecipVeilGrade.z > 0.5)
                {
                    // Relative to the image: the hue of the veil colour at the luminance of the picture (smoothed on the CPU side of
                    // the capture), because the exposure was applied before the upscaler and a colour in scene units would be wrong.
                    float luma = tex2Dlod(_PrecipLuma, float4(0.5, 0.5, 0, 0)).r * _PrecipVeilGrade.w;
                    colour = colour / max(Luma(colour), 1e-3) * max(luma, _PrecipVeilGrade.y);
                }
                float grade = _PrecipVeilGrade.x * saturate(alpha);
                float l = Luma(colour);
                colour = lerp(colour, float3(l, l, l), grade) * lerp(float3(1, 1, 1), float3(0.93, 0.98, 1.05), grade);
                return float4(colour, saturate(alpha));
            }
            ENDCG
        }

        // 1: the flakes. One quad per instance, placed by the hash in the wrapped box, stretched along its velocity, at least the floor
        // size on screen.
        Pass
        {
            Name "Particles"
            ZTest [_ZTest]
            Blend SrcAlpha OneMinusSrcAlpha

            CGPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment frag

            pv vert(uint vid : SV_VertexID, uint iid : SV_InstanceID)
            {
                pv o;
                // The same cell, seed and salts as PrecipMath.Place: the layer's offset keeps the three layers' patterns apart.
                uint seed = (uint)_PrecipLayerBox.w;
                uint cell = iid + (uint)_PrecipLayerSize.w;
                float3 place = float3(U(H3(cell, seed, 0u)), U(H3(cell, seed, 1u)), U(H3(cell, seed, 2u)));
                float sz = U(H3(cell, seed, 3u));
                float h4 = U(H3(cell, seed, 4u));
                float h5 = U(H3(cell, seed, 5u));

                float3 wrapped = frac(place + _PrecipDrift.xyz - _PrecipCamBox.xyz);
                float3 rel = (wrapped - 0.5) * float3(_PrecipLayerBox.x, _PrecipLayerBox.y, _PrecipLayerBox.x);
                float fade = EdgeFade(wrapped.x, _PrecipLayerBox.z) * EdgeFade(wrapped.y, _PrecipLayerBox.z) * EdgeFade(wrapped.z, _PrecipLayerBox.z);

                // The sway across the fall: a slow wander with a period of 3 s, 1.5 s or 1 s per flake, whole periods in 60 s.
                float swayRate = 6.2831853 / 3.0 * (1.0 + floor(h4 * 3.0));
                float ph = h5 * 6.2831853;
                rel.x += sin(_PrecipCamBox.w * swayRate + ph) * _PrecipVel.w;
                rel.z += cos(_PrecipCamBox.w * swayRate + ph * 1.7) * _PrecipVel.w;

                float3 relWorld = _PrecipAxisE.xyz * rel.x + _PrecipAxisU.xyz * rel.y + _PrecipAxisN.xyz * rel.z;
                float4x4 V = ViewM();
                float4x4 P = ProjM();
                float3 vpos = mul((float3x3)V, relWorld);
                float dist = max(-vpos.z, 0.05);

                float size = lerp(_PrecipLayerSize.x, _PrecipLayerSize.y, sz);
                float tanHalf = 1.0 / abs(P[1][1]);
                float floorM = _PrecipLook.y * 2.0 * dist * tanHalf / 1080.0;
                float sized = max(size, floorM);
                float contrast = size < floorM ? max((size / floorM) * (size / floorM), _PrecipLook.z) : 1.0;

                // The streak: along the velocity as it appears on the screen.
                float3 velW = _PrecipAxisE.xyz * _PrecipVel.x + _PrecipAxisU.xyz * _PrecipVel.y + _PrecipAxisN.xyz * _PrecipVel.z;
                float3 vv = mul((float3x3)V, velW);
                float speed = length(velW);
                float2 dir2 = dot(vv.xy, vv.xy) > 1e-6 ? normalize(vv.xy) : float2(0, -1);
                float2 perp = float2(-dir2.y, dir2.x);
                float len = 1.0 + _PrecipLook.x * speed;

                float2 q = Q[vid];
                float2 offset = (perp * q.x + dir2 * q.y * len) * sized * 0.5;
                float4 clip = mul(P, float4(vpos + float3(offset, 0), 1));
                o.pos = clip;
                o.uv = SheetUV(q, _PrecipLook.w);
                float nearFade = saturate((dist - 0.25) * 4.0);
                o.alpha = _PrecipLayerSize.z * contrast * fade * nearFade * _PrecipColour.a;
                return o;
            }

            float4 frag(pv i) : SV_Target
            {
                float a = tex2D(_PrecipSheet, i.uv).a * i.alpha;
                a *= Visible(i.pos);
                return float4(_PrecipColour.rgb, a);
            }
            ENDCG
        }

        // 2: the splashes of drops on the ground: rings that grow and fade on a plane below the camera.
        Pass
        {
            Name "Splashes"
            ZTest [_ZTest]
            Blend SrcAlpha OneMinusSrcAlpha

            CGPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment frag

            pv vert(uint vid : SV_VertexID, uint iid : SV_InstanceID)
            {
                pv o;
                uint seed = (uint)_PrecipSplashCam.z;
                float2 place = float2(U(H3(iid, seed, 10u)), U(H3(iid, seed, 11u)));
                float h = U(H3(iid, seed, 12u));
                float2 wrapped = frac(place - _PrecipSplashCam.xy);
                float phase = frac(_PrecipCamBox.w * _PrecipSplash.w + h);
                float3 rel = float3((wrapped.x - 0.5) * 2.0 * _PrecipSplash.y, -_PrecipSplash.x + 0.02, (wrapped.y - 0.5) * 2.0 * _PrecipSplash.y);
                float2 q = Q[vid];
                float radius = _PrecipSplash.z * (0.15 + phase);
                float3 relWorld = _PrecipAxisE.xyz * (rel.x + q.x * radius) + _PrecipAxisU.xyz * rel.y + _PrecipAxisN.xyz * (rel.z + q.y * radius);
                float4x4 V = ViewM();
                float4x4 P = ProjM();
                float3 vpos = mul((float3x3)V, relWorld);
                o.pos = mul(P, float4(vpos, 1));
                o.uv = q;
                float edge = EdgeFade(wrapped.x, 0.1) * EdgeFade(wrapped.y, 0.1);
                float dist = max(-vpos.z, 0.05);
                o.alpha = (1 - phase) * (1 - phase) * edge * saturate(1.5 - dist / max(_PrecipSplash.y, 1.0)) * _PrecipColour.a;
                return o;
            }

            float4 frag(pv i) : SV_Target
            {
                float r = length(i.uv);
                float ring = smoothstep(0.45, 0.7, r) * (1 - smoothstep(0.8, 1.0, r));
                return float4(_PrecipColour.rgb, ring * i.alpha * Visible(i.pos));
            }
            ENDCG
        }

        // 3: the luminance of the image, in a small target: 4 x 4 taps per output pixel across the whole picture. The target has a
        // mip chain; the next pass reads its smallest level.
        Pass
        {
            Name "LumaDown"
            ZTest Always
            Blend Off

            CGPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment frag

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            v2f vert(uint vid : SV_VertexID)
            {
                v2f o;
                float2 p = float2((vid << 1) & 2, vid & 2);
                o.uv = p;
                o.pos = float4(p * 2 - 1, UNITY_NEAR_CLIP_VALUE, 1);
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                // The output is 16 x 16; each pixel covers 1/16 of the picture, sampled on a 4 x 4 grid.
                float2 cell = floor(i.uv * 16.0);
                float sum = 0;
                for (int y = 0; y < 4; y++)
                {
                    for (int x = 0; x < 4; x++)
                    {
                        float2 uv = (cell + (float2(x, y) + 0.5) / 4.0) / 16.0;
                        float l = Luma(tex2Dlod(_MainTex, float4(uv, 0, 0)).rgb);
                        sum += min(l, 8.0);
                    }
                }
                float v = sum / 16.0;
                return float4(v, v, v, 1);
            }
            ENDCG
        }

        // 4: the smoothing: the smallest mip of the capture mixed into the previous value by _PrecipBlend (1 - exp(-dt / 0.5 s)).
        Pass
        {
            Name "LumaSmooth"
            ZTest Always
            Blend Off

            CGPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment frag

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            v2f vert(uint vid : SV_VertexID)
            {
                v2f o;
                float2 p = float2((vid << 1) & 2, vid & 2);
                o.uv = p;
                o.pos = float4(p * 2 - 1, UNITY_NEAR_CLIP_VALUE, 1);
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                float cur = tex2Dlod(_MainTex, float4(0.5, 0.5, 0, 4)).r;
                float prev = tex2Dlod(_PrecipPrev, float4(0.5, 0.5, 0, 0)).r;
                float v = lerp(prev, cur, _PrecipBlend);
                return float4(v, v, v, 1);
            }
            ENDCG
        }
    }
    Fallback Off
}
