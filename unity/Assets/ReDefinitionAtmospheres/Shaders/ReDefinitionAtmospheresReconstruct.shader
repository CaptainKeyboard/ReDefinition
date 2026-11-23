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
// Passes: 0 reconstruct into the new history, 1 the cloud's motion vector over the host's motion vectors (E40 MVCLOUD), blended by the cloud's alpha,
// 2 the distance to the cloud each pixel sees (for the far rotation), 3 pass 0 with the sample count and the cloud's distance in a second target
// (recon.age, recon.distanceReject), drawn as one procedural triangle;
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

        sampler2D_float _DistTex;   // the march's second target (the layer shader's pass 6): the mean distance of each new sample's cloud along its ray (m), 0 where it holds none
        float4 _ReconT;      // x: the history is moved by the march's own distance (1; recon.trueDistance) or by the layers' middle spheres (0); y: the sample count weighs the samples (1; recon.age); z: the history is dropped where its stored distance is more than this many octaves from the frame's (recon.distanceReject; 0: off); w: the scene's depth takes part (1; recon.sceneDepth)
        sampler2D_float _AgeTex;    // the history's sample count (pass 3's second target of the last frame): the marched samples each pixel's history holds

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
        // point in front of the previous camera (0 where the previous camera did not see it, PrevSees: behind it, beside it, or far outside its view).
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
            f = PrevSees(v) ? dot(v, _PrevF.xyz) : 0.0;
            return float2(dot(v, _PrevR.xyz), dot(v, _PrevU.xyz)) / max(f, 1e-3) * 0.5 + 0.5;
        }

        // The previous place of the cloud the pixel shows, from the distance the march measured (recon.trueDistance). The middle spheres of CloudPrevious place a cloud at the
        // layer's middle height; a cumulus seen at a shallow angle, or a cloud beside the camera, lies kilometres from there along the ray, and under a translating camera the
        // history is then moved by the wrong parallax. The distance is the nearest of the 3 x 3 new samples around the pixel that hold cloud (the nearest one's motion is the
        // edge's, as the dilated motion vectors of temporal antialiasing: Karis, "High Quality Temporal Supersampling", SIGGRAPH 2014), the drift the one of the layer whose
        // middle sphere is nearest to the point. False where no sample around holds cloud (the caller's model then places the pixel); a place the previous camera did not see
        // comes back outside the picture.
        // own: the distance is the nearest new sample's own (true) or a neighbour's (the dilation at a rim, false).
        bool TruePrevious(float3 o, float3 d, float2 q, float2 q0, out float2 puv, out float dist, out float prev, out bool own)
        {
            puv = -1.0;
            prev = 0.0;
            // The nearest new sample's own distance where it holds cloud; the nearest of the eight around only where it holds none (the edge's dilation). Taking the
            // nearest of all nine everywhere gave a far band at the horizon a nearer neighbour's distance: it led by 2-3 pixels in forward flight (HIST2 fwd300).
            dist = tex2Dlod(_DistTex, float4((q0 + 0.5) * _CurTex_TexelSize.xy, 0, 0)).r;
            own = dist > 0.0;
            // TRY (PRUEFPROTOKOLL 104): the distance varies from pixel to pixel as the position among the samples does (bilinear over the four samples around that hold cloud,
            // no limit on how far apart their distances are): a block-constant distance moves the history of neighbouring blocks by different parallaxes (the stair steps).
            {
                float2 qf = floor(q), fr = q - qf;
                float acc = 0.0, wsum = 0.0;
                [unroll] for (int j = 0; j < 4; j++)
                {
                    float2 oj = float2(j & 1, j >> 1);
                    float sj = tex2Dlod(_DistTex, float4((qf + oj + 0.5) * _CurTex_TexelSize.xy, 0, 0)).r;
                    float wj = (oj.x > 0.5 ? fr.x : 1.0 - fr.x) * (oj.y > 0.5 ? fr.y : 1.0 - fr.y);
                    if (sj > 0.0) { acc += sj * wj; wsum += wj; }
                }
                if (wsum > 1e-4) dist = acc / wsum;
            }
            if (dist <= 0.0)
            {
                dist = 1e30;
                [unroll] for (int k = 0; k < 9; k++)
                {
                    float s = tex2Dlod(_DistTex, float4((q0 + float2(k % 3 - 1, k / 3 - 1) + 0.5) * _CurTex_TexelSize.xy, 0, 0)).r;
                    if (s > 0.0) dist = min(dist, s);
                }
            }
            if (dist > 1e29)
            {
                dist = 0.0;
                return false;
            }
            float3 pc = o + d * dist;
            float3 drift = _Drift.xyz;
            float rp = length(pc), best = 1e30;
            [unroll] for (int l = 0; l < 4; l++)
            {
                float e = abs(rp - _MvA[l].x);
                if (l < (int)_MvN.x && e < best)
                {
                    best = e;
                    drift = _MvD[l].xyz;
                }
            }
            float3 v = pc - drift - _PrevPos.xyz;
            if (!PrevSees(v)) return true;
            puv = float2(dot(v, _PrevR.xyz), dot(v, _PrevU.xyz)) / dot(v, _PrevF.xyz) * 0.5 + 0.5;
            prev = length(v);   // the distance at which the previous camera saw this point
            return true;
        }

        float4 Low(float2 q)
        {
            return tex2Dlod(_CurTex, float4((q + 0.5) * _CurTex_TexelSize.xy, 0, 0));
        }

        // recon.sceneDepth (_ReconT.w): the scene in front of the cloud. The march ends at the scene's depth, so a pixel on the craft or the terrain holds only the cloud
        // in front of it; its neighbours beside the silhouette hold the whole cloud. Without the scene's depth the upsample of the new samples, the clip box and a
        // history moved by the cloud's motion carry that cloud onto the craft (thin parts dissolve in a turn) and the craft's "no cloud" onto the sky beside it (a
        // cloudless band). With it: a sample counts by how well the scene's depth at its own pixel agrees with the depth at this pixel (the weight of the layer shader's
        // depth-aware composite), and a history whose cloud lies behind this pixel's scene is dropped.
        sampler2D_float _SceneDepth;   // the scene's depth at the full target's size: metres along the camera's forward axis, 0 (or above 1e9) the sky

        float SceneZ(float2 uv)
        {
            float z = tex2Dlod(_SceneDepth, float4(uv, 0, 0)).r;
            return (z <= 0.0 || z > 1e9) ? 1e9 : z;
        }

        // The scene's depth at the full-target pixel a new sample was marched for.
        float LowZ(float2 texel, float n)
        {
            return SceneZ((texel * n + _ReconP.yz + 0.5) / _ReconD.xy);
        }

        float Agreement(float zSample, float zPixel)
        {
            return 1.0 / (1.0 + 40.0 * abs(zSample - zPixel) / max(min(zSample, zPixel), 1.0));
        }

        // The bilinear upsample of the new samples at q, each weighted by the agreement of its scene depth with the pixel's.
        float4 LowAware(float2 q, float zPixel, float n)
        {
            float2 b = floor(q);
            float2 f = q - b;
            float4 sum = 0;
            float ws = 0;
            [unroll] for (int k = 0; k < 4; k++)
            {
                float2 o = float2(k & 1, k >> 1);
                float4 s = tex2Dlod(_CurTex, float4((b + o + 0.5) * _CurTex_TexelSize.xy, 0, 0));
                if (s.a < -0.5) continue;
                float w = (o.x > 0.5 ? f.x : 1.0 - f.x) * (o.y > 0.5 ? f.y : 1.0 - f.y) * Agreement(LowZ(b + o, n), zPixel) + 1e-6;
                sum += s * w;
                ws += w;
            }
            return sum / max(ws, 1e-9);
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

        // age (recon.age, _ReconT.y): the number of marched samples the pixel's history holds after this frame. A history is the first frames' upsampled fill until the
        // pixel's own samples have replaced it; at a fixed weight w that takes about N x N / w frames (45 at the 3 x 3 rotation, 20 at 2 x 2), after every cut, at the
        // picture's edge in a turn and where a cloud uncovers another. With the count the n-th sample enters at max(w, 1 / n) (the running mean until it reaches the
        // fixed weight: the first sample replaces the fill whole), and a pixel without a marched sample shows the current upsample of the new samples instead of the
        // moved fill. The settled picture is the same as without the count.
        static float gStateDist;   // log2 of the distance (m) the frame placed the pixel's cloud at, 0 where it measured none: the count target's second channel

        float4 Reconstruct(v2f i, out float age)
        {
            age = 0.0;
            gStateDist = 0.0;
            float n = max(_ReconP.x, 1.0);
            float2 pix = floor(i.uv * _ReconD.xy);
            float2 q = (pix - _ReconP.yz) / n;          // the pixel's place among the new samples (a sample stands at every whole number)
            bool fresh = all(abs(fmod(pix, n) - _ReconP.yz) < 0.5);
            float4 cur = Low(q);
            // The far rotation's marker (alpha -1, the layer shader's Pass0): this pixel was not marched this frame, its history stands alone.
            bool kept = cur.a < -0.5;
            if (kept) { fresh = false; cur = 0; }
            if (_ReconP.w > 0.5 && _ReconP.w < 1.5) return cur;
            bool aware = _ReconT.w > 0.5;
            float zPixel = 1e9;
            [branch] if (aware)
            {
                zPixel = SceneZ(i.uv);
                if (!fresh && !kept) cur = LowAware(q, zPixel, n);
            }

            // The 3 x 3 new samples around the pixel: the clip box of the history (the marked ones left out; with the scene's depth, each by its agreement with the pixel's).
            float2 q0 = floor(q + 0.5);
            float4 m1 = 0, m2 = 0;
            float count = 0;
            [unroll] for (int k = 0; k < 9; k++)
            {
                float2 texel = q0 + float2(k % 3 - 1, k / 3 - 1);
                float4 s = Low(texel);
                if (s.a < -0.5) continue;
                float wk = 1.0;
                [branch] if (aware) wk = Agreement(LowZ(texel, n), zPixel);
                m1 += s * wk;
                m2 += s * s * wk;
                count += wk;
            }
            m1 /= max(count, 1.0);
            float4 sd = sqrt(max(m2 / max(count, 1.0) - m1 * m1, 0.0));

            // The previous place of this pixel's cloud.
            float3 o = _CamPos.xyz;
            float3 d = RayDir(i.uv);
            float2 puv = 0.0;
            float depth = 0.0;
            bool valid;
            bool measured = false;
            float prevDist = 0.0;
            bool ownDistance = false;
            [branch] if (_ReconT.x > 0.5) measured = TruePrevious(o, d, q, q0, puv, depth, prevDist, ownDistance);
            // The count target's second channel: log2 of the cloud's distance where the frame measured one; -1 where it measured none and the scene stands in the ray
            // (the craft, the terrain: "no cloud" there says nothing about the sky behind); 0 where it measured none under an open sky.
            gStateDist = measured ? log2(max(depth, 1.0)) : (zPixel < 1e8 ? -1.0 : 0.0);
            if (measured)
            {
                valid = _Drift.w > 0.5 && all(puv > 0.0) && all(puv < 1.0);
                // recon.distanceReject (_ReconT.z, in octaves): the history holds the distance of the cloud it showed (read at the nearest texel: a distance is not
                // blended across an edge). The history is dropped where it stood behind the scene (-1: a disocclusion from behind the craft), and where this pixel's OWN
                // sample measures a distance the history's is more than the tolerance from (another cloud in front or behind). A rim pixel's distance is a neighbour's
                // and changes with the block's offset from frame to frame, and a rim's history often holds no distance: neither drops the history. Dropping it there
                // left the rims on the upsample of the block grid for good: square steps at every cloud edge, also standing still (HIST2 r3, 2026-11-20).
                [branch] if (valid && _ReconT.z > 0.0)
                {
                    float stored = tex2Dlod(_AgeTex, float4((floor(puv * _ReconD.xy) + 0.5) / _ReconD.xy, 0, 0)).g;
                    if (stored < -0.5 || (stored > 0.0 && ownDistance && abs(stored - log2(max(prevDist, 1.0))) > _ReconT.z)) valid = false;
                }
            }
            else if (CloudPrevious(o, d, puv, depth))
            {
                valid = _Drift.w > 0.5 && all(puv > 0.0) && all(puv < 1.0);
            }
            else
            {
                float f;
                puv = FirstShellPrevious(o, d, depth, f);
                valid = _Drift.w > 0.5 && f > 1e-3 && all(puv > 0.0) && all(puv < 1.0);
            }
            // The scene in front of the history's cloud: the cloud the history would bring lies behind what this pixel shows now (2 % of slack for the depth's own steps).
            if (aware && valid && depth > zPixel / max(dot(d, _CamForward.xyz), 1e-3) * 1.02) valid = false;
            // A kept pixel without a history (the screen's edge after a turn, the first frames) takes the mean of the marched pixels around it (one of each block always is).
            if (!valid)
            {
                age = fresh ? 1.0 : 0.0;
                return kept ? m1 : cur;
            }
            float4 h = _ReconK.w > 0.5 ? HistoryCatmull(puv) : tex2Dlod(_HistTex, float4(puv, 0, 0));
            float held = _ReconT.y > 0.5 ? tex2Dlod(_AgeTex, float4(puv, 0, 0)).r : 0.0;
            age = held;
            if (_ReconP.w > 1.5 || kept) return h;
            // A clip width of 999 or more is no clip at all (a pixel-exact rotation keeps its history as it is; where the nine new samples are equal, sd = 0 would still pull it to them).
            // Fewer than three new samples around (a far rotation's marched pixel among kept ones) give no spread to clip by: the clip would pull the history onto the one sample.
            if (_ReconK.z < 999.0 && count > 2.5) h = clamp(h, m1 - _ReconK.z * sd, m1 + _ReconK.z * sd);
            float weight = fresh ? _ReconK.x : _ReconK.y;
            [branch] if (_ReconT.y > 0.5)
            {
                if (fresh)
                {
                    weight = max(weight, 1.0 / (held + 1.0));
                    age = min(held + 1.0, 255.0);
                }
                else if (held < 0.5)
                {
                    weight = 1.0;
                }
            }
            return lerp(h, cur, weight);
        }

        float4 frag(v2f i) : SV_Target
        {
            float age;
            return Reconstruct(i, age);
        }

        // Pass 3: the reconstruction with the sample count in a second target, drawn as one procedural triangle (a command buffer's Blit binds one target). The uv is the
        // Blit's, as in the layer shader's pass 6.
        v2f vertProc(uint vid : SV_VertexID)
        {
            v2f o;
            float2 p = float2((vid << 1) & 2, vid & 2);
            o.pos = float4(p * 2.0 - 1.0, UNITY_NEAR_CLIP_VALUE, 1.0);
        #if UNITY_UV_STARTS_AT_TOP
            o.uv = float2(p.x, 1.0 - p.y);
        #else
            o.uv = p;
        #endif
            return o;
        }

        struct ReconTargets
        {
            float4 colour : SV_Target0;
            float4 age : SV_Target1;
        };

        ReconTargets fragAge(v2f i)
        {
            ReconTargets t;
            float age;
            t.colour = Reconstruct(i, age);
            t.age = float4(age, gStateDist, 0.0, 0.0);
            return t;
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

        // Pass 2: the distance to the cloud each pixel sees (CloudDepth), for the far rotation of the next frame's march.
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragDepth
            #pragma target 3.5
            float4 fragDepth(v2f i) : SV_Target { return CloudDepth(_CamPos.xyz, RayDir(i.uv)); }
            ENDCG
        }

        // Pass 3: pass 0 with the sample count in a second target (recon.age).
        Pass
        {
            CGPROGRAM
            #pragma vertex vertProc
            #pragma fragment fragAge
            #pragma target 3.5
            ENDCG
        }
    }
}
