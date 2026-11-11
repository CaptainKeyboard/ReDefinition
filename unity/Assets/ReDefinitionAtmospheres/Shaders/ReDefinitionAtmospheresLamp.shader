// The lamp scatter of ReDefinition Atmospheres: the beam and glow a vehicle lamp shows in air that is not clear (snow, fog, cloud, dust).
//
// One draw per lamp: a proxy mesh (a cone for a spot light, a sphere for a point light) around the lamp's reach, back faces only,
// additive, no depth test, no depth or motion vector written. The fragment shader marches the view ray through the lamp's reach and
// adds the single-scattered light: albedo x sigma x emission x Henyey-Greenstein phase x exp(-attenScale x (sigmaCamera t + sigma d)),
// with t the distance from the camera, d the distance from the lamp. The ray is clipped at the scene's depth (a copy of the depth
// texture made at BeforeForwardAlpha, or the host's render-size depth), so a wall hides the beam behind it. The maths is
// LampMath.BeamRadiance / LampMath.BeamInterval of src/Atmospheres/Light/Lamp/LampMath.cs, which the unit tests check; the two
// repeat each other.
//
// Rendering statement: no motion vectors, no depth written, no reactive mask (the draw is a command buffer, not a scene object),
// the Scene mode is in the picture before the upscaler and the Host mode after it with the host's depth; not an input of the
// raytracing; the colour is in scene units (Scene) or scaled by the caller (Host).
Shader "ReDefinition/AtmospheresLamp"
{
    Properties
    {
    }

    SubShader
    {
        Tags { "Queue" = "Transparent+10" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Pass
        {
            Name "LampScatter"
            Cull Front
            ZWrite Off
            ZTest Always
            Blend One One

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #include "UnityCG.cginc"

            // The contribution mask of atmoDebug (a copy of the function of Include/AtmoContribution.cginc, feature id 3 =
            // ATMO_FEATURE_LAMP_SCATTER: the bundle project resolves no include from the Shaders folder, and the weather player
            // carries the shaders flat). The global is the same, so the debug switch reaches this pass.
            float _AtmoDebug;
            inline float4 LampContribOutput(float4 normalColour, float contribution)
            {
                if (_AtmoDebug > 0.5 && _AtmoDebug < 1.5)
                {
                    float c = saturate(contribution);
                    return float4(float3(0.0, 0.0, 1.0) * c, c);
                }
                return normalColour;
            }

            // xyz = the lamp in the world, w = its range in metres.
            float4 _LampPos;
            // xyz = the unit axis, w = the cosine of the outer half angle (0 or less: a point light).
            float4 _LampDir;
            // x = cosine of the inner half angle, y = the lens fade distance, z = the phase anisotropy g, w = the albedo.
            float4 _LampShape;
            // x = sigma at the lamp, y = sigma at the camera (1/m), z = the transmittance exponent scale, w = the steps.
            float4 _LampMedium;
            // rgb = the lamp's colour times intensity, gain and fade.
            float4 _LampColour;
            // x = glint depth, y = glint size in m, z = the temporal jitter offset, w = 1 where the depth is used.
            float4 _LampGlint;
            // xyz = the flakes' drift since the start (world, m).
            float4 _LampDrift;
            // The camera: position, forward (unit); and the depth set-up: x = flip y, y = reversed z, zw = unused.
            float4 _LampCamPos;
            float4 _LampCamFwd;
            float4 _LampDepthMode;
            // Unity's _ZBufferParams for the camera, computed by the caller (the host draws after the engine set them for the frame).
            float4 _LampZ;
            sampler2D _ReDefLampDepth;

            struct appdata
            {
                float4 vertex : POSITION;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 worldPos : TEXCOORD0;
                float4 screen : TEXCOORD1;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.screen = ComputeScreenPos(o.pos);
                return o;
            }

            static const float FAR_T = 1e30;

            // Adds the interval [lo, hi] clipped to the lamp's reach and the forward half into the running union [outLo, outHi].
            void ClipInterval(float aLo, float aHi, float lo, float hi, float gLo, float gHi, inout float outLo, inout float outHi)
            {
                float l = max(max(aLo, lo), gLo);
                float h = min(min(aHi, hi), gHi);
                if (h > l)
                {
                    outLo = min(outLo, l);
                    outHi = max(outHi, h);
                }
            }

            // LampMath.BeamInterval: the ray against the lamp's sphere and, for a spot, its forward cone.
            bool BeamInterval(float3 origin, float3 dir, float tMax, out float t0, out float t1)
            {
                t0 = 0; t1 = 0;
                float3 pos = _LampPos.xyz;
                float range = _LampPos.w;
                float3 axis = _LampDir.xyz;
                float cosOuter = _LampDir.w;
                float3 w = origin - pos;
                float b = dot(w, dir);
                float c = dot(w, w) - range * range;
                float disc = b * b - c;
                if (disc <= 0) return false;
                float root = sqrt(disc);
                float lo = max(0, -b - root);
                float hi = min(tMax, -b + root);
                if (hi <= lo) return false;
                if (cosOuter <= 0)
                {
                    t0 = lo; t1 = hi;
                    return true;
                }
                float c2 = cosOuter * cosOuter;
                float ad = dot(axis, dir);
                float aw = dot(axis, w);
                float qa = ad * ad - c2;
                float qb = 2 * (aw * ad - c2 * dot(w, dir));
                float qc = aw * aw - c2 * dot(w, w);
                float aLo = 0, aHi = 0, bLo = 0, bHi = 0;
                bool haveA = false, haveB = false;
                if (abs(qa) < 1e-9)
                {
                    if (abs(qb) < 1e-12)
                    {
                        if (qc >= 0) { aLo = lo; aHi = hi; haveA = true; }
                    }
                    else if (qb > 0) { aLo = -qc / qb; aHi = FAR_T; haveA = true; }
                    else { aLo = -FAR_T; aHi = -qc / qb; haveA = true; }
                }
                else
                {
                    float d = qb * qb - 4 * qa * qc;
                    if (d < 0)
                    {
                        if (qa > 0) { aLo = -FAR_T; aHi = FAR_T; haveA = true; }
                    }
                    else
                    {
                        float sq = sqrt(d);
                        float r1 = (-qb - sq) / (2 * qa);
                        float r2 = (-qb + sq) / (2 * qa);
                        if (r1 > r2) { float tmp = r1; r1 = r2; r2 = tmp; }
                        if (qa < 0) { aLo = r1; aHi = r2; haveA = true; }
                        else { aLo = -FAR_T; aHi = r1; haveA = true; bLo = r2; bHi = FAR_T; haveB = true; }
                    }
                }
                float gLo = -FAR_T, gHi = FAR_T;
                if (abs(ad) < 1e-9)
                {
                    if (aw < 0) return false;
                }
                else if (ad > 0) gLo = -aw / ad;
                else gHi = -aw / ad;
                float outLo = FAR_T, outHi = -FAR_T;
                if (haveA) ClipInterval(aLo, aHi, lo, hi, gLo, gHi, outLo, outHi);
                if (haveB) ClipInterval(bLo, bHi, lo, hi, gLo, gHi, outLo, outHi);
                if (outHi <= outLo) return false;
                t0 = outLo; t1 = outHi;
                return true;
            }

            float Emission(float dist, float cosAngle)
            {
                float range = _LampPos.w;
                float ratio = dist / range;
                float r2 = ratio * ratio;
                float window = 1 - r2 * r2;
                if (window <= 0) return 0;
                window *= window;
                float spot = 1;
                float cosOuter = _LampDir.w;
                if (cosOuter > 0)
                {
                    float u = (cosAngle - cosOuter) / max(_LampShape.x - cosOuter, 1e-4);
                    if (u <= 0) return 0;
                    u = min(u, 1);
                    spot = u * u * (3 - 2 * u);
                }
                float nearFade = _LampShape.y;
                float nr = nearFade > 0 ? min(dist / nearFade, 1) : 1;
                nr = nr * nr * (3 - 2 * nr);
                return window * spot * nr / (1 + 25 * r2);
            }

            float HenyeyGreenstein(float cosTheta, float g)
            {
                float g2 = g * g;
                float denom = 1 + g2 - 2 * g * cosTheta;
                return (1 - g2) / (12.566371 * denom * sqrt(denom));
            }

            // Trilinear value noise, 0 to 1, from one lookup in a 256 x 256 table (LampPass.MakeNoise): the lattice value of (x, y, z) is
            // the red channel at (x + 37 z, y + 17 z), the green channel holds the value one z step on, and the bilinear filter does the
            // two axes of the interpolation (the smoothstep is put into the coordinate).
            sampler2D _LampNoise;

            float ValueNoise(float3 p)
            {
                float3 i = floor(p);
                float3 f = frac(p);
                f = f * f * (3 - 2 * f);
                float2 uv = (i.xy + float2(37.0, 17.0) * i.z + f.xy + 0.5) / 256.0;
                float2 rg = tex2Dlod(_LampNoise, float4(uv, 0, 0)).xy;
                return lerp(rg.x, rg.y, f.z);
            }

            // The distance along the view ray to the scene, from the depth copy; a large number where the depth is not used or the
            // pixel shows the sky.
            float SceneDistance(float2 uv, float3 dir)
            {
                if (_LampGlint.w < 0.5) return 1e6;
                if (_LampDepthMode.x > 0.5) uv.y = 1 - uv.y;
                float raw = tex2Dlod(_ReDefLampDepth, float4(uv, 0, 0)).r;
                float eye;
                if (_LampDepthMode.y > 0.5)
                {
                    if (raw <= 1e-7) return 1e6;
                }
                else
                {
                    if (raw >= 1 - 1e-7) return 1e6;
                }
                eye = 1.0 / (_LampZ.z * raw + _LampZ.w);
                float cosView = max(dot(dir, _LampCamFwd.xyz), 1e-3);
                return eye / cosView;
            }

            float4 frag(v2f i) : SV_Target
            {
                float3 origin = _LampCamPos.xyz;
                float3 dir = normalize(i.worldPos - origin);
                float2 uv = i.screen.xy / i.screen.w;
                float tScene = SceneDistance(uv, dir);

                float t0, t1;
                if (!BeamInterval(origin, dir, tScene, t0, t1)) return LampContribOutput(float4(0, 0, 0, 0), 0);

                float sigma = _LampMedium.x;
                float sigmaCam = _LampMedium.y;
                float atten = _LampMedium.z;
                int steps = (int)_LampMedium.w;
                float g = _LampShape.z;
                float albedo = _LampShape.w;
                float3 pos = _LampPos.xyz;
                float3 axis = _LampDir.xyz;

                // A 4 x 4 ordered dither of the pixel (the Bayer matrix, whose offsets average 0.5), shifted by the temporal offset
                // (0 = the same every frame): the start of the march moves by a fraction of one step from pixel to pixel.
                static const float bayer[16] = { 0.0, 8.0, 2.0, 10.0, 12.0, 4.0, 14.0, 6.0, 3.0, 11.0, 1.0, 9.0, 15.0, 7.0, 13.0, 5.0 };
                int2 px = (int2)i.pos.xy;
                float jitter = frac((bayer[(px.x & 3) + 4 * (px.y & 3)] + 0.5) / 16.0 + _LampGlint.z * 0.618034);
                float h = (t1 - t0) / steps;
                float sum = 0;
                [loop]
                for (int k = 0; k < 16; k++)
                {
                    if (k >= steps) break;
                    float t = t0 + h * (k + jitter);
                    float3 x = origin + dir * t;
                    float3 l = x - pos;
                    float d = length(l);
                    if (d < 1e-4) continue;
                    float3 travel = l / d;
                    float e = Emission(d, dot(travel, axis));
                    if (e <= 0) continue;
                    float phase = HenyeyGreenstein(dot(travel, -dir), g);
                    float tau = atten * (sigmaCam * t + sigma * d);
                    float s = e * phase * exp(-tau);
                    if (_LampGlint.x > 0)
                    {
                        // Single flakes catch the light: the noise is cubed and scaled to a mean of 1 (the mean of the cubed trilinear
                        // smoothstep value noise is 0.1762, so 1 / 0.1762 = 5.675), the depth is the level's.
                        // The drift is how far the flakes have moved since the start: a feature sits where the flake went, so the
                        // lookup goes back by it.
                        float n = ValueNoise((x - _LampDrift.xyz) / max(_LampGlint.y, 0.05));
                        s *= lerp(1.0, 5.675 * n * n * n, _LampGlint.x);
                    }
                    sum += s;
                }
                float radiance = albedo * sigma * sum * h;
                float3 colour = _LampColour.rgb * radiance;
                float contribution = max(colour.r, max(colour.g, colour.b));
                return LampContribOutput(float4(colour, 0), contribution);
            }
            ENDCG
        }

        // Pass 1: the copy of the scene depth. A full-screen blit whose fragment reads the global _CameraDepthTexture as a shader
        // would and writes the raw depth to a float target, so that the copy holds what the global holds at the event it runs in (a
        // blit that names the global as its source reads the camera's colour in the weather player's forward camera).
        Pass
        {
            Name "DepthCopy"
            Cull Off
            ZWrite Off
            ZTest Always
            Blend Off

            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment fragDepth
            #include "UnityCG.cginc"

            sampler2D_float _CameraDepthTexture;

            float4 fragDepth(v2f_img i) : SV_Target
            {
                return SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, i.uv).xxxx;
            }
            ENDCG
        }
    }
    Fallback Off
}
