// The edge-adaptive reconstruction of the own cloud system (package T12, the plan's T05b): the edge mask and the reconstruction that reads it.
//
// The coarse march draws one pixel of every 4 x 4 block of the full target per frame (ReDefinitionAtmospheresLayers.shader, _ReconP). This
// shader flags the blocks that hold a cloud contour; the layer pass then marches a 2 x 2 pattern of rays in the flagged blocks only
// (MarchStep.cginc), and the reconstruction fills each pixel from the pattern of its block: 2 x 2 samples in a flagged block, 4 x 4 samples
// elsewhere, both with the reprojected, variance-clipped history of package T05 (ReDefinitionAtmospheresReconstruct.shader, the same maths).
//
// Mask: a block is an edge where the alpha of its coarse sample and of the four neighbouring samples differs by more than a threshold, or
// where the alpha in the last full picture differs by more than the threshold inside the block (nine taps). A flagged block stays flagged
// for a number of frames after its last detection (the hysteresis: a block that flips between the two patterns would shimmer), and the
// flagged blocks are widened by the dilation radius (two separable passes). The detection reads the CURRENT coarse samples, so a contour
// that enters the picture is flagged in the frame it is first sampled.
//
// Passes: 0 detect and persist (_StateTex: the last frame's state; writes the new state), 1 dilate in x, 2 dilate in y, 3 reconstruct.
Shader "ReDefinition/AtmospheresEdge"
{
    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        CGINCLUDE
        #include "UnityCG.cginc"

        sampler2D _StateTex;      // the source of a mask pass, set by the caller before each pass (a command buffer's Blit does not bind _MainTex for us)
        sampler2D _CurTex;        // the coarse samples: one texel per 4 x 4 block
        float4 _CurTex_TexelSize;
        sampler2D _EdgeTex;       // the edge pattern's samples: one texel per 2 x 2 block, zero outside the flagged blocks
        float4 _EdgeTex_TexelSize;
        sampler2D _HistTex;       // the last full picture
        sampler2D _EdgeMask;      // the final mask: one texel per 4 x 4 block
        float4 _EdgeK;            // x: alpha threshold, y: dilation radius in blocks, z: frames a block stays flagged, w: the history and the state are valid (1) or not (0)
        float4 _EdgeD;            // the mask's width and height in blocks, the full target's width and height / 4 (unused), 0
        float4 _ReconP;           // coarse block size (4), its offset x and y this frame, debug (1: new samples only, 2: history only)
        float4 _ReconE;           // the edge pattern's offset x and y this frame, 0, 0
        float4 _ReconD;           // the full target's width and height
        float4 _ReconK;           // blend weight of a pixel with a new sample, of a pixel without, the clip width in standard deviations, 0
        float4 _CamPos, _CamForward, _CamRight, _CamUp;
        float4 _PrevPos, _PrevF, _PrevR, _PrevU;
        float4 _ShellR;
        float4 _Drift;

        struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

        v2f vert(appdata_img v)
        {
            v2f o;
            o.pos = UnityObjectToClipPos(v.vertex);
            o.uv = v.texcoord;
            return o;
        }

        float CoarseAlpha(float2 b)
        {
            return tex2Dlod(_CurTex, float4((clamp(b, 0.0, _EdgeD.xy - 1.0) + 0.5) * _CurTex_TexelSize.xy, 0, 0)).a;
        }

        float4 Detect(v2f i)
        {
            float2 b = floor(i.uv * _EdgeD.xy);
            float a0 = CoarseAlpha(b), a1 = CoarseAlpha(b + float2(-1, 0)), a2 = CoarseAlpha(b + float2(1, 0)),
                  a3 = CoarseAlpha(b + float2(0, -1)), a4 = CoarseAlpha(b + float2(0, 1));
            float lo = min(a0, min(min(a1, a2), min(a3, a4))), hi = max(a0, max(max(a1, a2), max(a3, a4)));
            float range = hi - lo;
            float prev = 0.0;
            if (_EdgeK.w > 0.5)
            {
                // The last picture inside the block: nine taps two pixels apart around the block's centre.
                float2 centre = b * 4.0 + 2.0;
                float hlo = 1.0, hhi = 0.0;
                [unroll] for (int k = 0; k < 9; k++)
                {
                    float2 px = centre + float2(k % 3 - 1, k / 3 - 1) * 2.0;
                    float a = tex2Dlod(_HistTex, float4(px / _ReconD.xy, 0, 0)).a;
                    hlo = min(hlo, a);
                    hhi = max(hhi, a);
                }
                range = max(range, hhi - hlo);
                prev = tex2Dlod(_StateTex, float4((b + 0.5) / _EdgeD.xy, 0, 0)).r;
            }
            float level = range > _EdgeK.x ? 1.0 : max(prev - 1.0 / max(_EdgeK.z, 1.0), 0.0);
            return float4(level, 0, 0, 1);
        }

        // Dilation: the maximum of the flag over the radius, one axis per pass (_StateTex holds the state; a texel is flagged where its level is above zero).
        float4 DilateAxis(v2f i, float2 axis)
        {
            float m = 0.0;
            int r = (int)_EdgeK.y;
            [loop] for (int d = -8; d <= 8; d++)
            {
                float2 uv = i.uv + axis * d / _EdgeD.xy;
                float hit = tex2Dlod(_StateTex, float4(uv, 0, 0)).r > 0.001 ? 1.0 : 0.0;
                m = max(m, abs(d) <= r ? hit : 0.0);
            }
            return float4(m, 0, 0, 1);
        }

        float2 PixelDim() { return _ReconD.xy; }

        float4 CoarseLow(float2 q)
        {
            return tex2Dlod(_CurTex, float4((q + 0.5) * _CurTex_TexelSize.xy, 0, 0));
        }

        // The sample of the edge pattern at index q (the pixel it stands for is q * 2 + the pattern's offset): the edge march's value where its
        // block is flagged, the bilinear upsample of the coarse samples there otherwise.
        float4 EdgeSample(float2 q)
        {
            float2 pixel = clamp(q * 2.0 + _ReconE.xy, 0.0, _ReconD.xy - 1.0);
            float2 blockUv = (floor(pixel / 4.0) + 0.5) / _EdgeD.xy;
            if (tex2Dlod(_EdgeMask, float4(blockUv, 0, 0)).r > 0.5)
                return tex2Dlod(_EdgeTex, float4((q + 0.5) * _EdgeTex_TexelSize.xy, 0, 0));
            return CoarseLow((pixel - _ReconP.yz) / 4.0);
        }

        float3 RayDir(float2 uv)
        {
            float2 ndc = uv * 2.0 - 1.0;
            return normalize(_CamForward.xyz + ndc.x * _CamRight.xyz + ndc.y * _CamUp.xyz);
        }

        float4 Reconstruct(v2f i)
        {
            float2 pix = floor(i.uv * _ReconD.xy);
            bool edge = tex2Dlod(_EdgeMask, float4((floor(pix / 4.0) + 0.5) / _EdgeD.xy, 0, 0)).r > 0.5;
            float4 cur, m1 = 0, m2 = 0;
            bool fresh;
            if (edge)
            {
                float2 q = (pix - _ReconE.xy) / 2.0;
                fresh = all(abs(fmod(pix, 2.0) - _ReconE.xy) < 0.5);
                float2 base = floor(q), f = q - base;
                cur = lerp(lerp(EdgeSample(base), EdgeSample(base + float2(1, 0)), f.x),
                           lerp(EdgeSample(base + float2(0, 1)), EdgeSample(base + float2(1, 1)), f.x), f.y);
                float2 q0 = floor(q + 0.5);
                [unroll] for (int k = 0; k < 9; k++)
                {
                    float4 s = EdgeSample(q0 + float2(k % 3 - 1, k / 3 - 1));
                    m1 += s;
                    m2 += s * s;
                }
            }
            else
            {
                float2 q = (pix - _ReconP.yz) / 4.0;
                fresh = all(abs(fmod(pix, 4.0) - _ReconP.yz) < 0.5);
                cur = CoarseLow(q);
                float2 q0 = floor(q + 0.5);
                [unroll] for (int k = 0; k < 9; k++)
                {
                    float4 s = CoarseLow(q0 + float2(k % 3 - 1, k / 3 - 1));
                    m1 += s;
                    m2 += s * s;
                }
            }
            if (_ReconP.w > 0.5 && _ReconP.w < 1.5) return cur;
            m1 /= 9.0;
            float4 sd = sqrt(max(m2 / 9.0 - m1 * m1, 0.0));

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

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            float4 frag(v2f i) : SV_Target { return Detect(i); }
            ENDCG
        }

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            float4 frag(v2f i) : SV_Target { return DilateAxis(i, float2(1, 0)); }
            ENDCG
        }

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            float4 frag(v2f i) : SV_Target { return DilateAxis(i, float2(0, 1)); }
            ENDCG
        }

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            float4 frag(v2f i) : SV_Target { return Reconstruct(i); }
            ENDCG
        }
    }
}
