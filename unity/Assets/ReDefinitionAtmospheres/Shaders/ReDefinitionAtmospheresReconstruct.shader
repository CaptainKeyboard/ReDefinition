// The temporal reconstruction of the own cloud system (package T05, prototype A): the layer march draws one pixel of every N x N block of the
// full target per frame (ReDefinitionAtmospheresLayers.shader, _ReconP); this pass builds the full target from the new samples and the
// reprojected history of the last full target.
//
// Reprojection: a pixel's ray meets the middle sphere of the first layer; the point is moved back by the cloud's drift over one frame (the wind,
// planet frame) and seen from the previous camera, so that both the camera's motion and the wind's carry the history. Camera positions are
// planet-relative, so a floating-origin shift of the world (the planet's centre and the camera move together) changes nothing.
// History control: variance clipping of the history to the mean and spread of the 3 x 3 new samples around the pixel (Salvi, "An excursion in
// temporal supersampling", GDC 2016; Karis, "High Quality Temporal Supersampling", SIGGRAPH 2014), a larger blend weight for the pixel that
// has a new sample this frame, a rejection of history that left the target.
// The volumetric-cloud arrangement (a quarter of the pixels per frame, reprojected history, clip to the neighbourhood of the new samples)
// follows Schneider, "The real-time volumetric cloudscapes of Horizon Zero Dawn", SIGGRAPH 2015, and Hillaire, "Physically Based Sky,
// Atmosphere and Cloud Rendering in Frostbite", 2016 (the cloud-only history keyed to the camera).
//
// Passes: 0 reconstruct into the new history, 1 depth-aware composite of the full target over the camera target is the layer shader's pass 1.
Shader "ReDefinition/AtmospheresReconstruct"
{
    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
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

            float4 Low(float2 q)
            {
                return tex2Dlod(_CurTex, float4((q + 0.5) * _CurTex_TexelSize.xy, 0, 0));
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
                float b = dot(o, d);
                float c = dot(o, o) - _ShellR.x * _ShellR.x;
                float disc = b * b - c;
                float t = _ShellR.y;
                if (disc > 0)
                {
                    float sq = sqrt(disc);
                    float t0 = -b - sq, t1 = -b + sq;
                    t = t0 > 0 ? t0 : (t1 > 0 ? t1 : _ShellR.y);
                }
                float3 v = o + d * t - _Drift.xyz - _PrevPos.xyz;
                float f = dot(v, _PrevF.xyz);
                float2 puv = float2(dot(v, _PrevR.xyz), dot(v, _PrevU.xyz)) / max(f, 1e-3) * 0.5 + 0.5;
                bool valid = _Drift.w > 0.5 && f > 1e-3 && all(puv > 0.0) && all(puv < 1.0);
                if (!valid) return cur;
                float4 h = tex2Dlod(_HistTex, float4(puv, 0, 0));
                if (_ReconP.w > 1.5) return h;
                float4 lo = m1 - _ReconK.z * sd, hi = m1 + _ReconK.z * sd;
                h = clamp(h, lo, hi);
                return lerp(h, cur, fresh ? _ReconK.x : _ReconK.y);
            }
            ENDCG
        }
    }
}
