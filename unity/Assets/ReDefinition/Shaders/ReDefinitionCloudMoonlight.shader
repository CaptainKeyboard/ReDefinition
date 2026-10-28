// A moon's light on EVE's raymarched clouds, per pixel (src/Raytracing/Ksp/EveCloudRelight.cs).
// EVE's reconstructed cloud buffer holds the clouds in the sun's light. This shader copies it with the moon's light
// added at each pixel's cloud, a second sun with the moon's values. The copy then stands in for EVE's buffer for
// everything that reads it this frame. EVE's history stays as EVE wrote it.
//
// The model is read off Scatterer-EVE/RaymarchCloud (Scatterer 0.908, the variant with LIGHT_VOLUME_ON, disassembled).
// It steps along the ray. Each step receives L = Pd exp(-t) + B Pm s / (1 + 0.35 t + 0.065 t^2). Pd and Pm are EVE's
// two phase functions at the angle between light and ray, and t is the optical depth towards the light. B is the
// cloud type's multiple scattering brightness, and s is 0.05 + 0.95 smoothstep of the effective coverage. The steps
// count by their share of the ray's transmittance. Their sum, times 5, the colours of light and clouds, the light's way
// through the atmosphere (Transmittance) and the planet's shadow (Lit) is the direct light. The sky's light is the sum
// of the steps' sqrt(1 - coverage) times the colours, the skylight multiplier, the exposure and the irradiance table.
//
// A short march of our own stands for the steps, over the shell where EVE's mean distance puts the cloud. The
// maps and curves give each step's coverage, type, density and brightness. The optical depth is the extinction summed
// along the moon's way out of the layer, as EVE's light volume holds it for the sun only. EVE's opacity scales the
// mean of the steps' light. A scale on the optical depth (TauScale, measured by the probe) stands for the raymarching's noise.
Shader "Hidden/ReDefinition/CloudMoonlight"
{
    Properties
    {
        _MainTex ("EVE's reconstructed clouds", 2D) = "black" {}
    }
    SubShader
    {
        ZTest Always Cull Off ZWrite Off

        // The one pass: EVE's cloud buffer copied with the moon's light added, blitted by EveCloudRelight.
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 4.0
            #include "UnityCG.cginc"

            // rgb: the clouds' light, premultiplied, before Scatterer's tone mapping; a: the
            // transmittance.
            sampler2D _MainTex;
            // The distance from the camera to the cloud's weighted mean point in hundreds of metres, below 0
            // where there is no cloud. EVE's reconstruction writes it (Scatterer-EVE/
            // ReconstructRaymarchedClouds, its third target).
            sampler2D _ReDefinitionCloudDistance;
            // The camera's projection inverted (OpenGL's, as unity_CameraInvProjection) and
            // its camera-to-world matrix, as EVE reconstructs each pixel's ray with them.
            float4x4 _ReDefinitionCloudInvProjection;
            float4x4 _ReDefinitionCloudCameraToWorld;
            // xyz: the camera relative to the planet's centre; w: the planet's radius (Rg).
            float4 _ReDefinitionCloudCamera;
            // xyz: towards the moon from the planet's centre; w: Rt, the atmosphere's top.
            float4 _ReDefinitionCloudMoon;
            // rgb: the moon's light on the clouds, times their colour and the gain; 0 for none.
            float4 _ReDefinitionCloudMoonColour;
            // rgb: the same for the sky's light the moon gives, times EVE's skylight multiplier,
            // Scatterer's exposure and its own gain; a: EVE's skylight tint multiplier.
            float4 _ReDefinitionCloudSkyColour;
            // Scatterer's irradiance table's place in the atlas, and its size (SKY_W, _H) in zw
            // of _ReDefinitionCloudSkySize.
            float4 _ReDefinitionCloudIrradianceRect;
            float4 _ReDefinitionCloudSkySize;
            // x: HR, y: HM, the scale heights; z, w: the cloud layers' inner and outer radius.
            float4 _ReDefinitionCloudHeights;
            float4 _ReDefinitionCloudBetaR;
            float4 _ReDefinitionCloudBetaMEx;
            float4 _ReDefinitionCloudDirectPhase;
            float4 _ReDefinitionCloudMultiplePhase;
            // Scatterer's atmosphere atlas, its transmittance table's place in it, the
            // table's size (TRANSMITTANCE_W, _H) in zw and the atlas's in xy.
            sampler2D _ReDefinitionCloudAtlas;
            float4 _ReDefinitionCloudTransmittanceRect;
            float4 _ReDefinitionCloudAtlasSizes;
            // The layer's own maps (RtEveClouds): coverage and type as EVE's raymarching reads them, with the alpha masks
            // that pick their channel. The density curve's x is the cloud type and its y the height in the layer; r is
            // its coverage curve and g its density curve. The rows take a position from the planet's centre to the maps.
            // In the fades, x is the coverage fade, y the density fade and w the scale of the optical depth.
            sampler2D _ReDefinitionCloudCoverage;
            sampler2D _ReDefinitionCloudType;
            sampler2D _ReDefinitionCloudCurve;
            float _ReDefinitionCloudUseMask1;
            float _ReDefinitionCloudUseMask2;
            float4 _ReDefinitionCloudMask1;
            float4 _ReDefinitionCloudMask2;
            float4 _ReDefinitionCloudMapRow0;
            float4 _ReDefinitionCloudMapRow1;
            float4 _ReDefinitionCloudMapRow2;
            float4 _ReDefinitionCloudMapFades;
            // Per cloud type: the density (the x of cloudTypeProperties0) and the multiple scattering brightness, and how
            // many types there are.
            float _ReDefinitionCloudTypeDensity[12];
            float _ReDefinitionCloudTypeBrightness[12];
            float _ReDefinitionCloudTypes;
            // Mode 0 shows the clouds lit. Mode 1 shows where each pixel's point lies: green inside
            // the layer's shell, red outside it, blue without a distance. Mode 2 shows the moon's
            // direct light alone. Mode 3 shows the sky's light it gives alone. Modes 4 to 7 show
            // the parts of the light (frag).
            float _ReDefinitionCloudDebug;

            static const float kPi = 3.14159265f;
            // The factor the raymarching multiplies the sum of its steps' light with.
            static const float kRaymarchGain = 5.0f;

            // Bruneton's analytic optical depth over `d` from radius r at cosine mu, scale
            // height H, as Scatterer's cloud shader evaluates it.
            float OpticalDepth(float H, float r, float mu, float d, float Rg)
            {
                float a = sqrt((0.5f / H) * r);
                float2 a01 = a * float2(mu, mu + d / r);
                float2 a01s = sign(a01);
                float2 a01sq = a01 * a01;
                float x = a01s.y > a01s.x ? exp(a01sq.x) : 0.0f;
                float2 y = a01s / (2.3193f * abs(a01) + sqrt(1.52f * a01sq + 4.0f))
                           * float2(1.0f, exp(-d / H * (d / (2.0f * r) + mu)));
                return sqrt((6.2831f * H) * r) * exp((Rg - r) / H) * (x + dot(y, float2(1.0f, -1.0f)));
            }

            // Scatterer's transmittance table at radius r and cosine mu (the ozone's, where it
            // has one), which its shader multiplies into the analytic transmittance.
            float3 Table(float r, float mu)
            {
                float Rg = _ReDefinitionCloudCamera.w, Rt = _ReDefinitionCloudMoon.w;
                float2 uv = float2(atan((mu + 0.15f) / 1.15f * tan(1.5f)) / 1.5f, sqrt(max((r - Rg) / (Rt - Rg), 0.0f)));
                float2 size = _ReDefinitionCloudAtlasSizes.zw;
                uv = min(max(uv - 0.5f / size, 0.0f), 1.0f - 1.0f / size);
                uv = _ReDefinitionCloudTransmittanceRect.xy * uv + _ReDefinitionCloudTransmittanceRect.zw + 0.5f / _ReDefinitionCloudAtlasSizes.xy;
                return tex2Dlod(_ReDefinitionCloudAtlas, float4(uv, 0.0f, 0.0f)).rgb;
            }

            // The light's way to the atmosphere's top from radius r at cosine mu: 0 where the
            // ground stands in it, 1 above the atmosphere. It is Bruneton's analytic optical depth
            // (OpticalDepth) times Scatterer's transmittance table (Table).
            float3 Transmittance(float r, float mu)
            {
                float Rg = _ReDefinitionCloudCamera.w, Rt = _ReDefinitionCloudMoon.w;
                float rmu = r * mu;
                float top = rmu * rmu - r * r + Rt * Rt;
                // From above the atmosphere, from where the ray enters it.
                if (r > Rt && top > 0.0f && -rmu - sqrt(top) > 0.0f)
                {
                    float enter = -rmu - sqrt(top);
                    mu = (rmu + enter) / Rt;
                    r = Rt;
                    rmu = r * mu;
                    top = rmu * rmu - r * r + Rt * Rt;
                }
                if (r > Rt || top < 0.0f)
                    return 1.0f;
                float d = -rmu + sqrt(top);
                float ground = (Rg - 10.0f) * (Rg - 10.0f) + r * r * (mu * mu - 1.0f);
                if (ground >= 0.0f && -rmu - sqrt(ground) >= 0.0f)
                    d = min(d, -rmu - sqrt(ground));
                float below = mu < -sqrt(max(1.0f - (Rg - 10.0f) * (Rg - 10.0f) / (r * r), 0.0f)) ? 1.0e9f : 0.0f;
                float depthR = below > 0.0f ? below : OpticalDepth(_ReDefinitionCloudHeights.x, r, mu, d, Rg);
                float depthM = below > 0.0f ? below : OpticalDepth(_ReDefinitionCloudHeights.y, r, mu, d, Rg);
                float3 analytic = saturate(exp(-(_ReDefinitionCloudBetaR.rgb * depthR + _ReDefinitionCloudBetaMEx.rgb * depthM)));
                return min(analytic * Table(r, mu), 1.0f);
            }

            // The sky's light a light gives a cloud at radius r, its cosine over the cloud's
            // horizon mu. It comes from Scatterer's irradiance table, as Scatterer's cloud shader
            // takes it: times 200, its colour drawn towards grey by EVE's tint multiplier.
            float3 SkyLight(float r, float mu)
            {
                float Rg = _ReDefinitionCloudCamera.w, Rt = _ReDefinitionCloudMoon.w;
                float2 uv = float2(mu * 0.5f + 0.5f, saturate((r - Rg) / (Rt - Rg)));
                float2 size = _ReDefinitionCloudSkySize.zw;
                uv = min(max(uv - 0.5f / size, 0.0f), 1.0f - 1.0f / size);
                uv = _ReDefinitionCloudIrradianceRect.xy * uv + _ReDefinitionCloudIrradianceRect.zw + 0.5f / _ReDefinitionCloudAtlasSizes.xy;
                float3 irradiance = tex2Dlod(_ReDefinitionCloudAtlas, float4(uv, 0.0f, 0.0f)).rgb * 200.0f;
                float grey = length(irradiance);
                return grey + (irradiance - grey) * _ReDefinitionCloudSkyColour.a;
            }

            // Scatterer's planet shadow at a point, with its soft edge: 1 lit, 0 in the shadow, with the
            // light as a disc 20 planet radii out from the centre.
            float Lit(float3 position, float3 towards)
            {
                float Rg = _ReDefinitionCloudCamera.w;
                float r = length(position);
                float3 proxy = towards * (20.0f * Rg) - position;
                float distance = length(proxy);
                float3 l = proxy / distance;
                float3 down = -position / r;
                float apart = asin(min(length(cross(down, l)), 1.0f));
                float planet = asin(min(Rg / r, 1.0f));
                float shadow = smoothstep(0.0f, 1.0f, saturate(((planet - apart) * distance / Rg + 1.0f) * 0.5f));
                shadow *= smoothstep(0.0f, 1.0f, saturate(dot(l, down) / 0.2f));
                return 1.0f - shadow;
            }

            // EVE's Henyey-Greenstein lobe as its shader writes it.
            float Lobe(float g, float cosine)
            {
                return (1.0f - g * g) / pow(1.0f + g * g - 2.0f * g * cosine, 1.5f) / 4.0f * kPi;
            }

            // One of EVE's phase functions, direct or multiple (directPhaseFunctionProperties,
            // multiplePhaseFunctionProperties: two Henyey-Greenstein lobes each): its two lobes (g in x
            // and z) weighted by y and w.
            float Phase(float4 lobes, float cosine)
            {
                return Lobe(lobes.x, cosine) * lobes.y + Lobe(lobes.z, cosine) * lobes.w;
            }

            // What the layer holds at a point relative to the planet's centre, as the raymarching reads it: the effective
            // coverage (ce), the extinction per metre (sigma) and the multiple scattering brightness (brightness).
            struct Cloud
            {
                float ce;
                float sigma;
                float brightness;
            };

            Cloud Sample(float3 position)
            {
                Cloud c;
                c.ce = 0.0f;
                c.sigma = 0.0f;
                c.brightness = 1.0f;
                float4 at = float4(position, 1.0f);
                float3 q = float3(dot(_ReDefinitionCloudMapRow0, at), dot(_ReDefinitionCloudMapRow1, at), dot(_ReDefinitionCloudMapRow2, at));
                float3 n = q / max(length(q), 1e-3f);
                float2 uv = float2(0.5f + atan2(n.x, n.z) * (0.5f / kPi), acos(clamp(n.y, -1.0f, 1.0f)) / kPi);
                float4 map = tex2Dlod(_ReDefinitionCloudCoverage, float4(uv, 0.0f, 0.0f));
                float4 kind = tex2Dlod(_ReDefinitionCloudType, float4(uv, 0.0f, 0.0f));
                float coverage = _ReDefinitionCloudUseMask1 > 0.5f ? dot(map, _ReDefinitionCloudMask1) : map.a;
                float type = _ReDefinitionCloudUseMask2 > 0.5f ? dot(kind, _ReDefinitionCloudMask2) : kind.r;
                float height = (length(position) - _ReDefinitionCloudHeights.z) / max(_ReDefinitionCloudHeights.w - _ReDefinitionCloudHeights.z, 1.0f);
                if (height < 0.0f || height > 1.0f)
                    return c;
                float2 curve = tex2Dlod(_ReDefinitionCloudCurve, float4(type, height, 0.0f, 0.0f)).xy;
                float fade = max(_ReDefinitionCloudMapFades.x, 1e-3f);
                c.ce = curve.x + saturate((coverage - (1.0f - fade)) / fade) - 1.0f;
                if (c.ce < 0.01f)
                    return c;
                float index = type * (_ReDefinitionCloudTypes - 1.0f);
                int low = (int)index;
                int high = (int)min(_ReDefinitionCloudTypes - 1.0f, 1.0f + index);
                float blend = index - (float)low;
                low = clamp(low, 0, 11);
                high = clamp(high, 0, 11);
                float density = lerp(_ReDefinitionCloudTypeDensity[low], _ReDefinitionCloudTypeDensity[high], blend);
                c.brightness = lerp(_ReDefinitionCloudTypeBrightness[low], _ReDefinitionCloudTypeBrightness[high], blend);
                c.sigma = density * c.ce * _ReDefinitionCloudMapFades.y * curve.y;
                return c;
            }

            // The optical depth from a point towards the moon, out of the layer: the extinction summed over eight steps of
            // the way, times the scale for the noise the raymarching's own light volume holds (the fades' w).
            float LightDepth(float3 position, float3 towards)
            {
                float outer = _ReDefinitionCloudHeights.w;
                float b = dot(position, towards);
                float c = dot(position, position) - outer * outer;
                float span = max(-b + sqrt(max(b * b - c, 0.0f)), 0.0f);
                float sum = 0.0f;
                for (int i = 0; i < 8; i++)
                    sum += Sample(position + towards * (span * (i + 0.5f) / 8.0f)).sigma;
                return sum * (span / 8.0f) * _ReDefinitionCloudMapFades.w;
            }

            // The light a step receives, as the raymarching adds it up (see the head): the direct light and the multiple
            // scattering, from the phase functions at the cosine of the angle between the moon and the ray.
            float StepLight(Cloud c, float tau, float cosine)
            {
                float s = 0.05f + 0.95f * smoothstep(0.0f, 1.0f, saturate(c.ce));
                float direct = Phase(_ReDefinitionCloudDirectPhase, cosine) * exp(-tau);
                float multiple = c.brightness * Phase(_ReDefinitionCloudMultiplePhase, cosine) * s / (1.0f + 0.35f * tau + 0.065f * tau * tau);
                return direct + multiple;
            }

            // The mean light of the steps along a ray through the layer, each counting by its share of the ray's
            // transmittance as in the raymarching, and the opacity of the steps. The steps lie between the layer's shell and
            // 2.5 times EVE's mean distance, nearer ones closer together. It is -1 where our maps put no cloud on the ray.
            float MarchLight(float3 eye, float3 ray, float3 towards, float cosine, float meanDistance, out float opacity, out float skyMean)
            {
                opacity = 0.0f;
                skyMean = 0.0f;
                float outer = _ReDefinitionCloudHeights.w;
                float b = dot(eye, ray);
                float c = dot(eye, eye) - outer * outer;
                float discriminant = b * b - c;
                if (discriminant <= 0.0f)
                    return -1.0f;
                float root = sqrt(discriminant);
                float near = max(-b - root, 0.0f);
                float far = min(-b + root, 2.5f * meanDistance + 500.0f);
                if (far <= near)
                    return -1.0f;
                float transmittance = 1.0f;
                float weights = 0.0f;
                float light = 0.0f;
                float sky = 0.0f;
                float previous = near;
                for (int i = 0; i < 20; i++)
                {
                    float f = (i + 1.0f) / 20.0f;
                    float next = near + (far - near) * f * f;
                    float middle = 0.5f * (previous + next);
                    Cloud cloud = Sample(eye + ray * middle);
                    if (cloud.sigma > 0.0f)
                    {
                        float step = exp(-cloud.sigma * (next - previous));
                        float weight = transmittance * (1.0f - step);
                        float3 spot = eye + ray * middle;
                        light += weight * StepLight(cloud, LightDepth(spot, towards), cosine);
                        sky += weight * sqrt(max(1.0f - cloud.ce, 0.0f));
                        weights += weight;
                        transmittance *= step;
                    }
                    previous = next;
                }
                opacity = 1.0f - transmittance;
                skyMean = weights > 1e-5f ? sky / weights : 0.0f;
                return weights > 1e-5f ? light / weights : -1.0f;
            }

            // The pixel's clouds as EVE wrote them plus the moon's direct and sky light at the cloud's point; with
            // _ReDefinitionCloudDebug set, the probe's views instead. There is no extinction between the camera and
            // the cloud, because with it the moon's light came out red where the raymarching's is blue. The
            // measurement is the light probe's of 2026-10-23, night with the Mun at 41 degrees: blue over red
            // 0.52 with it, 1.57 without, the raymarching's 1.50. The direct light alone, without it, matches the raymarching's moonlight within
            // 2 per cent in each colour.
            float4 frag(v2f_img i) : SV_Target
            {
                float4 clouds = tex2Dlod(_MainTex, float4(i.uv, 0.0f, 0.0f));
                float opacity = 1.0f - clouds.a;
                float distance = tex2Dlod(_ReDefinitionCloudDistance, float4(i.uv, 0.0f, 0.0f)).r * 100.0f;
                if (_ReDefinitionCloudDebug > 0.5f && distance <= 0.0f)
                    return _ReDefinitionCloudDebug < 1.5f && opacity > 0.01f ? float4(0.0f, 0.0f, opacity, clouds.a) : clouds;
                if (opacity <= 0.001f || distance <= 0.0f)
                    return clouds;

                // The pixel's ray as EVE's reconstruction casts it.
                float4 view = mul(_ReDefinitionCloudInvProjection, float4(i.uv * 2.0f - 1.0f, 1.0f, 1.0f));
                float3 ray = normalize(mul((float3x3)_ReDefinitionCloudCameraToWorld, view.xyz / view.w));
                float3 eye = _ReDefinitionCloudCamera.xyz;
                float3 position = eye + ray * distance;
                float r = length(position);

                if (_ReDefinitionCloudDebug > 0.5f && _ReDefinitionCloudDebug < 1.5f)
                {
                    bool inside = r > _ReDefinitionCloudHeights.z - 200.0f && r < _ReDefinitionCloudHeights.w + 200.0f;
                    return float4(inside ? float3(0.0f, opacity, 0.0f) : float3(opacity, 0.0f, 0.0f), clouds.a);
                }

                float3 towards = _ReDefinitionCloudMoon.xyz;
                float lit = Lit(position, towards);
                float3 light = 0.0f;
                Cloud cloud = Sample(position);
                float tau = LightDepth(position, towards);
                float cosine = dot(towards, ray);
                // The mean light along the ray; the light at the mean point where our maps put no cloud on the ray.
                float ours, skyMean;
                float scattered = MarchLight(eye, ray, towards, cosine, distance, ours, skyMean);
                if (scattered < 0.0f)
                {
                    scattered = StepLight(cloud, tau, cosine);
                    skyMean = sqrt(max(1.0f - cloud.ce, 0.0f));
                }
                if (lit > 0.0f)
                {
                    float3 through = Transmittance(r, dot(position, towards) / r);
                    light = _ReDefinitionCloudMoonColour.rgb * (kRaymarchGain * lit * opacity * scattered) * through;
                }
                // The sky's light the moon gives, which the table already holds in the planet's shadow; without a
                // gain (0) not read.
                float3 sky = 0.0f;
                if (any(_ReDefinitionCloudSkyColour.rgb > 0.0f))
                    sky = _ReDefinitionCloudSkyColour.rgb * (opacity * skyMean) * SkyLight(r, dot(position, towards) / r);
                // 4 to 7, for the probe's means, each times the opacity: the transmittance towards
                // the moon, the table's part of it, the planet's shadow times the step's light, and
                // the opacity. 8: our opacity along the ray, the mean light of the steps and the optical depth at the mean point.
                if (_ReDefinitionCloudDebug > 7.5f)
                    return float4(ours * opacity, scattered * opacity, tau * opacity, clouds.a);
                if (_ReDefinitionCloudDebug > 3.5f)
                {
                    float mu = dot(position, towards) / r;
                    float3 part = _ReDefinitionCloudDebug < 4.5f ? Transmittance(r, mu)
                                : _ReDefinitionCloudDebug < 5.5f ? Table(r, mu)
                                : _ReDefinitionCloudDebug < 6.5f ? (float3)(lit * scattered) : 1.0f;
                    return float4(part * opacity, clouds.a);
                }
                // 2: the moon's direct light alone; 3: its sky's light alone.
                if (_ReDefinitionCloudDebug > 2.5f)
                    return float4(sky, clouds.a);
                if (_ReDefinitionCloudDebug > 1.5f)
                    return float4(light, clouds.a);
                return float4(clouds.rgb + light + sky, clouds.a);
            }
            ENDCG
        }
    }
    Fallback Off
}
