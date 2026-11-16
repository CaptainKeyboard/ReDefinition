// The temporal reconstruction of the own cloud system (package T05, prototype A): the layer march draws one pixel of every N x N block of the
// full target per frame (ReDefinitionAtmospheresLayers.shader, _ReconP); this pass builds the full target from the new samples and the
// reprojected history of the last full target.
//
// Reprojection: a pixel's ray meets the middle sphere of every layer; each point is moved back by the cloud's drift over one frame (the wind,
// planet frame) and seen from the previous camera, and the places are blended by the share of the pixel's cloud each layer holds
// (ReDefinitionAtmospheresCloudMotion.cginc; where it holds no layer, or with _MvN.x = 0, the middle sphere of the first layer, as before E40 MVCLOUD), so that both the
// camera's motion and the wind's carry the history. Camera positions are planet-relative, so a floating-origin shift of the world (the planet's centre and the camera move
// together) changes nothing.
// History control: variance clipping of the history to the mean and spread of the 3 x 3 new samples around the pixel (Salvi, "An excursion in
// temporal supersampling", GDC 2016; Karis, "High Quality Temporal Supersampling", SIGGRAPH 2014), a larger blend weight for the pixel that
// has a new sample this frame, a rejection of history that left the target.
// The volumetric-cloud arrangement (a quarter of the pixels per frame, reprojected history, clip to the neighbourhood of the new samples)
// follows Schneider, "The real-time volumetric cloudscapes of Horizon Zero Dawn", SIGGRAPH 2015, and Hillaire, "Physically Based Sky,
// Atmosphere and Cloud Rendering in Frostbite", 2016 (the cloud-only history keyed to the camera).
//
// Passes: 0 reconstruct into the new history, 1 the cloud's motion vector over the host's motion vectors (E40 MVCLOUD), blended by the cloud's alpha;
// the depth-aware composite of the full target over the camera target is the layer shader's pass 1.
Shader "ReDefinition/AtmospheresReconstruct"
{
    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        CGINCLUDE
        #include "UnityCG.cginc"

        sampler2D _CurTex;
        float4 _CurTex_TexelSize;   // 1 / w, 1 / h, w, h of the new samples (set by the caller)
        sampler2D _HistTex;
        float4 _ReconP;      // block size N, the block offset x and y that was marched this frame, debug (1: new samples only, 2: history only)
        float4 _ReconD;      // the full target's width and height
        float4 _ReconK;      // blend weight of a pixel with a new sample, of a pixel without, the clip width in standard deviations, 0
        float4 _CamPos, _CamForward, _CamRight, _CamUp;   // current camera (planet-relative position; right and up scaled by the tangent of the half field of view)
        float4 _PrevPos, _PrevF, _PrevR, _PrevU;          // previous camera (planet-relative position, unit forward, unit right / tan x, unit up / tan y)
        float4 _ShellR;      // radius of the middle sphere of the first layer, the sample distance when the ray misses it, 0, 0
        float4 _Drift;       // the cloud's drift over one frame in the planet frame (m), w: the history is valid (1) or not (0)

        #include "ReDefinitionAtmospheresCloudMotion.cginc"

        struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

        v2f vert(appdata_img v)
        {
            v2f o;
            o.pos = UnityObjectToClipPos(v.vertex);
            o.uv = v.texcoord;
            return o;
        }

        float3 RayDir(float2 uv)
        {
            float2 ndc = uv * 2.0 - 1.0;
            return normalize(_CamForward.xyz + ndc.x * _CamRight.xyz + ndc.y * _CamUp.xyz);
        }

        // The previous place of the cloud on the first layer's middle sphere (the sample distance where the ray misses it). Out: the distance along the ray, and the depth of the
        // point in front of the previous camera (1e-3 or less: it was behind it).
        float2 FirstShellPrevious(float3 o, float3 d, out float t, out float f)
        {
            float b = dot(o, d);
            float c = dot(o, o) - _ShellR.x * _ShellR.x;
            float disc = b * b - c;
            t = _ShellR.y;
            if (disc > 0)
            {
                float sq = sqrt(disc);
                float t0 = -b - sq, t1 = -b + sq;
                t = t0 > 0 ? t0 : (t1 > 0 ? t1 : _ShellR.y);
            }
            float3 v = o + d * t - _Drift.xyz - _PrevPos.xyz;
            f = dot(v, _PrevF.xyz);
            return float2(dot(v, _PrevR.xyz), dot(v, _PrevU.xyz)) / max(f, 1e-3) * 0.5 + 0.5;
        }

        float4 Low(float2 q)
        {
            return tex2Dlod(_CurTex, float4((q + 0.5) * _CurTex_TexelSize.xy, 0, 0));
        }

        // The history with a Catmull-Rom filter in five bilinear taps (the 9-tap filter folded onto the bilinear hardware; Jimenez, "Filmic SMAA: Sharp
        // morphological and temporal antialiasing", SIGGRAPH 2016). _ReconK.w selects it (CLOUDR3 P7): the bilinear fetch blurs the history a little with every sub-pixel move, which a
        // history kept over twenty frames adds up. The negative lobes can overshoot; the clip box that follows bounds the result.
        float4 HistoryCatmull(float2 uv)
        {
            float2 size = _ReconD.xy;
            float2 pos = uv * size;
            float2 c = floor(pos - 0.5) + 0.5;
            float2 f = pos - c;
            float2 w0 = f * (-0.5 + f * (1.0 - 0.5 * f));
            float2 w1 = 1.0 + f * f * (-2.5 + 1.5 * f);
            float2 w2 = f * (0.5 + f * (2.0 - 1.5 * f));
            float2 w3 = f * f * (-0.5 + 0.5 * f);
            float2 w12 = w1 + w2;
            float2 t0 = (c - 1.0) / size;
            float2 t3 = (c + 2.0) / size;
            float2 t12 = (c + w2 / w12) / size;
            float4 r = tex2Dlod(_HistTex, float4(t12.x, t0.y, 0, 0)) * (w12.x * w0.y)
                     + tex2Dlod(_HistTex, float4(t0.x, t12.y, 0, 0)) * (w0.x * w12.y)
                     + tex2Dlod(_HistTex, float4(t12.x, t12.y, 0, 0)) * (w12.x * w12.y)
                     + tex2Dlod(_HistTex, float4(t3.x, t12.y, 0, 0)) * (w3.x * w12.y)
                     + tex2Dlod(_HistTex, float4(t12.x, t3.y, 0, 0)) * (w12.x * w3.y);
            float wsum = w12.x * w0.y + w0.x * w12.y + w12.x * w12.y + w3.x * w12.y + w12.x * w3.y;
            return max(r / wsum, 0.0);
        }

        float4 frag(v2f i) : SV_Target
        {
            float n = max(_ReconP.x, 1.0);
            float2 pix = floor(i.uv * _ReconD.xy);
            float2 q = (pix - _ReconP.yz) / n;          // the pixel's place among the new samples (a sample stands at every whole number)
            bool fresh = all(abs(fmod(pix, n) - _ReconP.yz) < 0.5);
            float4 cur = Low(q);
            if (_ReconP.w > 0.5 && _ReconP.w < 1.5) return cur;

            // The 3 x 3 new samples around the pixel: the clip box of the history.
            float2 q0 = floor(q + 0.5);
            float4 m1 = 0, m2 = 0;
            [unroll] for (int k = 0; k < 9; k++)
            {
                float4 s = Low(q0 + float2(k % 3 - 1, k / 3 - 1));
                m1 += s;
                m2 += s * s;
            }
            m1 /= 9.0;
            float4 sd = sqrt(max(m2 / 9.0 - m1 * m1, 0.0));

            // The previous place of this pixel's cloud.
            float3 o = _CamPos.xyz;
            float3 d = RayDir(i.uv);
            float2 puv;
            float depth;
            bool valid;
            if (CloudPrevious(o, d, puv, depth))
            {
                valid = _Drift.w > 0.5 && all(puv > 0.0) && all(puv < 1.0);
            }
            else
            {
                float f;
                puv = FirstShellPrevious(o, d, depth, f);
                valid = _Drift.w > 0.5 && f > 1e-3 && all(puv > 0.0) && all(puv < 1.0);
            }
            if (!valid) return cur;
            float4 h = _ReconK.w > 0.5 ? HistoryCatmull(puv) : tex2Dlod(_HistTex, float4(puv, 0, 0));
            if (_ReconP.w > 1.5) return h;
            float4 lo = m1 - _ReconK.z * sd, hi = m1 + _ReconK.z * sd;
            h = clamp(h, lo, hi);
            return lerp(h, cur, fresh ? _ReconK.x : _ReconK.y);
        }

        sampler2D _MvCloud;        // the frame's full target: premultiplied radiance, alpha
        sampler2D_float _MvDepth;  // the host's copy of the scene's raw depth at the render size

        // The cloud's motion vector (the host's encoding: current minus previous viewport position, y up) with the cloud's alpha, in front of the scene, as the blend weight:
        // the output is blended over what the host's motion vectors hold, so the clear sky and the geometry in front of the cloud keep theirs.
        float4 fragMotion(v2f i) : SV_Target
        {
            float alpha = tex2Dlod(_MvCloud, float4(i.uv, 0, 0)).a;
            if (alpha <= 1e-3 || _MvN.z < 0.5) return 0;
            float3 o = _CamPos.xyz;
            float3 d = RayDir(i.uv);
            float2 puv;
            float depth;
            if (!CloudPrevious(o, d, puv, depth))
            {
                float f;
                puv = FirstShellPrevious(o, d, depth, f);
                if (f <= 1e-3) return 0;
            }
            float raw = tex2Dlod(_MvDepth, float4(i.uv, 0, 0)).r;
        #if defined(UNITY_REVERSED_Z)
            bool sky = raw <= 0.0;
        #else
            bool sky = raw >= 1.0;
        #endif
            float visible = 1.0;
            if (!sky)
            {
                float scene = LinearEyeDepth(raw);
                float cloud = depth * dot(d, _CamForward.xyz);
                visible = saturate((scene - cloud) / max(0.05 * scene, 1.0) + 0.5);
            }
            return float4(i.uv - puv, 0, saturate(alpha * visible));
        }
        ENDCG

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            ENDCG
        }

        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha
            ColorMask RG

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragMotion
            #pragma target 3.5
            ENDCG
        }
    }
}
