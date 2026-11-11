// The aurora shell (package D38): a camera-centred dome mesh that carries no geometry of its own. Every pixel of it casts its view ray
// into a thin spherical layer around the body (base to top altitude), steps through the layer a few times, and adds light where the
// ray passes through a curtain. The curtains are a function of latitude and longitude only (vertical rays by construction), so the
// earlier layer's artefacts cannot occur: a hard lid (the layer's lower boundary seen at a grazing angle), vertical drips (step
// aliasing of a ray march with jittered starts), and a bright band on the horizon (the path length through the layer at 0 to 3 degrees).
//
//   * The lid is removed by a soft base (smoothstep over the first tenth of the layer) and by the horizon fade below.
//   * The drips are removed by deterministic midpoint steps (no jitter, so nothing shimmers under DLSS), a step count that grows with
//     the path, and band-limiting of the stripe noise by the pixel footprint (the stripes fade to their mean where a pixel is wider
//     than a stripe).
//   * The horizon band is removed by an explicit fade of the whole shell below 3 degrees of elevation (full from 10 degrees).
//
// Draw order: recorded by AuroraPass into a command buffer on the flight camera at BeforeForwardAlpha (after the opaque scene and the
// sky, before the transparent queue where the cloud composite draws). The depth test is made here from a copy of the depth taken in the
// same buffer: the dome is at the far plane, so a pixel is drawn only where the copy holds the far value (nothing in front).
// No motion vectors are written: the aurora is slow emissive light, only the camera moves it on the screen, and the upscaler's
// camera-motion reprojection of the sky is right for it. The shell adds light before the upscaler, so the HUD-less copy contains it.
//
// Technique references (no code taken): SPVE and EVEVolumetricsAurorae (a ring around the pole, emission by a negative sky tint);
// nimitz, Shadertoy "Auroras" (raymarched curtains from triangle-noise fbm; licence on the Shadertoy page).
Shader "ReDefinition/AtmospheresAurora"
{
    SubShader
    {
        Tags { "Queue" = "Transparent-10" "RenderType" = "Transparent" "IgnoreProjector" = "True" }

        Pass
        {
            Name "Shell"
            Blend One One
            ZWrite Off
            ZTest Always
            Cull Off

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            sampler2D _AuroraNoise;
            sampler2D _AuroraDepth;

            // xyz = the body's centre relative to the camera, w = the body's radius.
            float4 _AuroraPlanet;
            // xyz = the body's pole axis, w = unused.
            float4 _AuroraPole;
            // xyz = the longitude reference (perpendicular to the pole), w = the drift of the pattern in texture units.
            float4 _AuroraRefA;
            // x = the oval's latitude (rad), y = its half width (rad), z = the emissive scale, w = the cap on the path factor.
            float4 _AuroraRing;
            // x = base altitude, y = top altitude (m), z = sin(horizon fade start), w = sin(horizon fade full).
            float4 _AuroraLayer;
            // x = target step length (m), y = most steps, z = stripe texture span (m), w = stripe contrast.
            float4 _AuroraLook;
            // x = fold of the ribbons (phase), y = height share where the colour turns red, z = pixel footprint (rad), w = 1 where the
            // depth buffer is reversed.
            float4 _AuroraLook2;
            float4 _AuroraColourA;
            float4 _AuroraColourB;
            // x = 1 where the depth copy is tested.
            float4 _AuroraScreen;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 dir : TEXCOORD0;
            };

            v2f vert(appdata_base v)
            {
                v2f o;
                float3 world = mul(unity_ObjectToWorld, float4(v.vertex.xyz, 1.0)).xyz;
                o.dir = world - _WorldSpaceCameraPos;
                float4 clip = mul(UNITY_MATRIX_VP, float4(world, 1.0));
                // On the far plane (a hair inside it): the dome is the sky, not geometry.
                clip.z = _AuroraLook2.w > 0.5 ? 1e-6 * clip.w : clip.w * (1.0 - 1e-6);
                o.pos = clip;
                return o;
            }

            // The two hits of the ray (from the origin, direction d) with a sphere of radius r at c; false where it misses.
            bool Sphere(float3 c, float r, float3 d, out float tn, out float tf)
            {
                float b = dot(d, c);
                float disc = b * b - (dot(c, c) - r * r);
                tn = 0.0;
                tf = 0.0;
                if (disc < 0.0) return false;
                float s = sqrt(disc);
                tn = b - s;
                tf = b + s;
                return true;
            }

            float4 Noise(float2 uv, float lod)
            {
                return tex2Dlod(_AuroraNoise, float4(uv, 0.0, lod));
            }

            // The latitude of a direction and its distance from the oval in half widths.
            float RingU(float3 n)
            {
                float lat = asin(clamp(abs(dot(n, _AuroraPole.xyz)), 0.0, 1.0));
                return (lat - _AuroraRing.x) / max(_AuroraRing.y, 1e-4);
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float3 d = normalize(i.dir);

                // Nothing in front of the sky: the depth copy holds the far value only where the dome may draw.
                if (_AuroraScreen.x > 0.5)
                {
                    float2 uvs = i.pos.xy / _ScreenParams.xy;
                    float depth = tex2D(_AuroraDepth, uvs).r;
                    bool sky = _AuroraLook2.w > 0.5 ? depth <= 1e-9 : depth >= 1.0 - 1e-6;
                    if (!sky) discard;
                }

                float3 c = _AuroraPlanet.xyz;
                float R = _AuroraPlanet.w;
                float baseAlt = _AuroraLayer.x;
                float top = _AuroraLayer.y;
                float cl = length(c);
                float camAlt = cl - R;
                float3 upCam = -c / max(cl, 1.0);

                // The segment of the ray inside the layer.
                float tn1, tf1, tn0, tf0;
                bool hitOuter = Sphere(c, R + top, d, tn1, tf1);
                if (!hitOuter || tf1 <= 0.0) discard;
                bool hitInner = Sphere(c, R + baseAlt, d, tn0, tf0);
                float t0, t1;
                if (camAlt < baseAlt)
                {
                    // Below the layer: from where the ray leaves the inner sphere to where it leaves the outer one.
                    t0 = tf0;
                    t1 = tf1;
                }
                else if (camAlt <= top)
                {
                    // Inside the layer: to the inner sphere if the ray dives into it, else to the far side of the outer one.
                    t0 = 0.0;
                    t1 = (hitInner && tn0 > 0.0) ? tn0 : tf1;
                }
                else
                {
                    // Above the layer: from the outer sphere's near side to the inner sphere or the far side.
                    t0 = max(tn1, 0.0);
                    t1 = (hitInner && tn0 > t0) ? tn0 : tf1;
                }
                if (t1 <= t0) discard;

                // The elevation fade: by the elevation over the horizon seen from the ground, by the angle to the layer from above it.
                float3 pMid = d * (0.5 * (t0 + t1)) - c;
                float3 nMid = normalize(pMid);
                float lift = camAlt <= top ? dot(d, upCam) : abs(dot(d, nMid));
                float fade = smoothstep(_AuroraLayer.z, _AuroraLayer.w, lift);
                if (fade <= 0.0) discard;

                // Nothing near the oval at either end or in the middle: nothing to march.
                float uA = RingU(normalize(d * t0 - c));
                float uB = RingU(normalize(d * t1 - c));
                float uC = RingU(nMid);
                float nearest = min(abs(uA), min(abs(uB), abs(uC)));
                bool crosses = (uA < 0.0) != (uB < 0.0) || (uA < 0.0) != (uC < 0.0);
                if (nearest > 2.4 && !crosses) discard;

                float path = t1 - t0;
                float steps = clamp(ceil(path / max(_AuroraLook.x, 100.0)), 3.0, _AuroraLook.y);
                float dt = path / steps;
                float footprintM = (0.5 * (t0 + t1)) * _AuroraLook2.z;
                float span = max(_AuroraLook.z, 1000.0);
                // Mip level at which one texel of the stripe texture (span / 128 m) is about one pixel wide.
                float lod = clamp(log2(max(footprintM, 1.0) / (span / 128.0)), 0.0, 7.0);
                float rayGate = saturate(1.0 - lod / 3.5);
                float drift = _AuroraRefA.w;
                float3 refA = _AuroraRefA.xyz;
                float3 refB = cross(_AuroraPole.xyz, refA);

                float3 sum = float3(0.0, 0.0, 0.0);
                [loop]
                for (int k = 0; k < 24; k++)
                {
                    if (float(k) >= steps) break;
                    float t = t0 + (float(k) + 0.5) * dt;
                    float3 p = d * t - c;
                    float pl = length(p);
                    float3 n = p / pl;
                    float hf = saturate((pl - R - baseAlt) / max(top - baseAlt, 1.0));

                    float u = RingU(n);
                    float lon01 = atan2(dot(n, refB), dot(n, refA)) * 0.15915494 + 0.5;
                    // The folds of the curtains and the brightness of the ribbons along the oval.
                    float fold = Noise(float2(lon01 + drift, 0.31), 0.0).r - 0.5;
                    float ribbon = Noise(float2(lon01 * 2.0 - drift * 1.7, 0.67), 0.0).g;
                    float pulse = 0.72 + 0.28 * Noise(float2(lon01 * 2.0 + drift * 5.0, drift * 9.0), 0.0).a;
                    float phase = (u + fold * _AuroraLook2.x * 2.0) * 0.95;
                    float ridge = pow(0.5 + 0.5 * cos(6.2831853 * phase), 5.0);
                    float envelope = exp(-1.8 * u * u);
                    float lumpy = 0.35 + 0.65 * smoothstep(0.25, 0.75, ribbon);

                    // The vertical rays: stripes that depend on the horizontal place only, so they stand upright at any view.
                    // Narrow along the oval (about 1 to 2 km) and long across it (about a third of the span), so that they read as rays.
                    float latRad = asin(clamp(abs(dot(n, _AuroraPole.xyz)), 0.0, 1.0));
                    float along = (lon01 - 0.5) * 6.2831853 * R * cos(latRad);
                    float2 q = float2(along / span, latRad * R / (span * 0.35));
                    float stripe = Noise(q + float2(drift * 0.5, 0.0), lod).b;
                    float ray = lerp(0.6, smoothstep(0.30, 0.85, stripe) * 1.3, rayGate * _AuroraLook.w);

                    // The height profile: a soft base (no lid), a bright lower part that fades upwards.
                    float profile = smoothstep(0.0, 0.10, hf) * exp(-hf * 2.4);
                    float3 colour = lerp(_AuroraColourA.rgb, _AuroraColourB.rgb, smoothstep(_AuroraLook2.y, 1.0, hf));
                    sum += colour * (envelope * ridge * lumpy * pulse * ray * profile);
                }
                // The mean along the path, scaled up with the path through the layer (capped, so a grazing ray is not blinding).
                float thick = max(top - baseAlt, 1.0);
                float3 emission = sum / steps * min(path / thick, _AuroraRing.w);
                return fixed4(emission * (_AuroraRing.z * fade), 1.0);
            }
            ENDCG
        }

        // The copy of the scene's depth into a float target of the camera's size. A pixel-for-pixel copy in window coordinates (no
        // uv flip is guessed: the dome pass reads the copy at its own window coordinates), made with a draw and not a Blit so that the
        // camera's matrices stay as they are.
        Pass
        {
            Name "DepthCopy"
            Blend Off
            ZWrite Off
            ZTest Always
            Cull Off

            CGPROGRAM
            #pragma vertex vertCopy
            #pragma fragment fragCopy
            #pragma target 3.5
            #include "UnityCG.cginc"

            sampler2D _AuroraDepthSource;
            sampler2D _CameraDepthTexture;
            // 1 where the source is the engine's global _CameraDepthTexture (the player), 0 where it is _AuroraDepthSource (the game).
            float _AuroraSourceGlobal;

            struct v2fCopy
            {
                float4 pos : SV_POSITION;
            };

            v2fCopy vertCopy(uint id : SV_VertexID)
            {
                v2fCopy o;
                float2 corner = float2((id << 1) & 2, id & 2);
                o.pos = float4(corner * 2.0 - 1.0, 0.0, 1.0);
                return o;
            }

            float4 fragCopy(v2fCopy i) : SV_Target
            {
                float4 uv = float4(i.pos.xy / _ScreenParams.xy, 0.0, 0.0);
                float d = _AuroraSourceGlobal > 0.5 ? tex2Dlod(_CameraDepthTexture, uv).r : tex2Dlod(_AuroraDepthSource, uv).r;
                return float4(d, d, d, d);
            }
            ENDCG
        }
    }
    Fallback Off
}
