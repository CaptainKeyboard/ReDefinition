// A moon's light on EVE's raymarched clouds, per pixel (src/Raytracing/Ksp/EveCloudRelight.cs):
// EVE's reconstructed cloud buffer for the frame -- the clouds in the sun's light, as Scatterer's
// raymarching shader lights them -- copied with the moon's light added at each pixel's cloud, a
// second sun with the moon's values. The copy then stands in for EVE's buffer for everything
// that reads it this frame (colorBuffer, scattererReconstructedCloud); EVE's own buffer, its
// history, stays as EVE wrote it. Built into redefinition.shaders by BundleBuilder.
//
// Read off Scatterer-EVE/RaymarchCloud (Scatterer 0.908, scatterershaders-directx,
// disassembled), which lights the clouds once per pixel at the weighted mean point of the
// cloud along the ray: the light the sun gives there in through the atmosphere (Bruneton's
// analytic optical depth times Scatterer's transmittance table), the planet's shadow with its
// soft edge (the sun as a disc 20 planet radii out), and EVE's two phase functions, direct and
// multiple (directPhaseFunctionProperties, multiplePhaseFunctionProperties: two
// Henyey-Greenstein lobes each); and the sky's light the sun gives the cloud, from
// Scatterer's irradiance table at the cloud's height and the sun's height over its horizon,
// times EVE's skylight multiplier and tint and Scatterer's exposure. The same here with the
// moon's direction. The two get a gain each, from the light probe's calibration against the
// raymarching's own moonlight, which holds what is not repeated here. No extinction between
// the camera and the cloud: with it the moon's light came out red where the raymarching's is
// blue (light probe, 2026-10-23, night with the Mun at 41 degrees: blue over red 0.52 with it,
// 1.57 without, the raymarching's 1.50), and the direct light alone, without it, matches the
// raymarching's moonlight within 2 per cent in each colour. The light's way through the cloud
// itself, which the raymarching takes from EVE's light volume along the sun, comes from the
// layer's coverage map along the moon's (RtEveClouds' packed map, as the rays' cloud
// shadows read it). Where EVE's reconstruction writes them (Scatterer-EVE/
// ReconstructRaymarchedClouds, its third target): the distance from the camera to that point
// in hundreds of metres, below 0 where there is no cloud.
Shader "Hidden/ReDefinition/CloudMoonlight"
{
    Properties
    {
        _MainTex ("EVE's reconstructed clouds", 2D) = "black" {}
    }
    SubShader
    {
        ZTest Always Cull Off ZWrite Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            // rgb: the clouds' light, premultiplied, before Scatterer's tone mapping; a: the
            // transmittance.
            sampler2D _MainTex;
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
            // The layer's packed coverage map (RtEveClouds): r coverage, g the density curve;
            // from the planet's centre to the map in the rows; x the coverage fade, y the
            // density fade, z the other fades together, w how strongly the cloud shadows itself.
            sampler2D _ReDefinitionCloudMap;
            float4 _ReDefinitionCloudMapRow0;
            float4 _ReDefinitionCloudMapRow1;
            float4 _ReDefinitionCloudMapRow2;
            float4 _ReDefinitionCloudMapFades;
            // 0: the clouds lit; 1: where each pixel's point lies -- green inside the layer's
            // shell, red outside it, blue without a distance; 2: the moon's direct light alone;
            // 3: the sky's light it gives alone; 4 to 7 the parts of the light (frag).
            float _ReDefinitionCloudDebug;

            static const float kPi = 3.14159265f;

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
            // ground stands in it, 1 above the atmosphere.
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
            // horizon mu, as Scatterer's cloud shader takes it from the irradiance table: times
            // 200, its colour drawn towards grey by EVE's tint multiplier.
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

            // Scatterer's planet shadow at a point: 1 lit, 0 in the shadow, with the light as a
            // disc 20 planet radii out from the centre.
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

            float Phase(float4 lobes, float cosine)
            {
                return Lobe(lobes.x, cosine) * lobes.y + Lobe(lobes.z, cosine) * lobes.w;
            }

            // The layer's density at a point relative to the planet's centre, as the rays'
            // cloud shadows read it (RtCommon.hlsli, CloudTransmittanceAt).
            float Density(float3 position)
            {
                float4 at = float4(position, 1.0f);
                float3 q = float3(dot(_ReDefinitionCloudMapRow0, at), dot(_ReDefinitionCloudMapRow1, at), dot(_ReDefinitionCloudMapRow2, at));
                float3 n = q / max(length(q), 1e-3f);
                float2 uv = float2(0.5f + atan2(n.x, n.z) * (0.5f / kPi), acos(clamp(n.y, -1.0f, 1.0f)) / kPi);
                float4 map = tex2Dlod(_ReDefinitionCloudMap, float4(uv, 0.0f, 0.0f));
                float fade = _ReDefinitionCloudMapFades.x;
                float keep = 1.0f - saturate(fade);
                float coverage = (min(max(map.r, keep), 1.0f) - keep) / max(fade, 1e-3f);
                return map.g < 0.01f || coverage < 0.01f ? 0.0f : map.g * coverage * _ReDefinitionCloudMapFades.y;
            }

            // How much of the moon's light reaches the point through the layer above it: six
            // samples towards the moon to the layer's top, each a slice of a column whose whole
            // lets 1 / (1 + 7 d + 26 d^2) through, as the rays' cloud shadow takes it.
            float SelfShadow(float3 position, float3 towards)
            {
                float inner = _ReDefinitionCloudHeights.z, outer = _ReDefinitionCloudHeights.w;
                float thickness = max(outer - inner, 1.0f);
                float b = dot(position, towards);
                float c = dot(position, position) - outer * outer;
                float span = min(max(-b + sqrt(max(b * b - c, 0.0f)), 0.0f), 4.0f * thickness);
                float depth = 0.0f;
                for (int i = 0; i < 6; i++)
                {
                    float d = Density(position + towards * (span * (i + 0.5f) / 6.0f));
                    depth += log(1.0f + 7.0f * d + 26.0f * d * d);
                }
                return exp(-depth * (span / 6.0f) / thickness * _ReDefinitionCloudMapFades.w * _ReDefinitionCloudMapFades.z);
            }

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
                if (lit > 0.0f)
                {
                    float3 through = Transmittance(r, dot(position, towards) / r);
                    float cosine = dot(towards, ray);
                    float shadow = SelfShadow(position, towards);
                    float scattered = Phase(_ReDefinitionCloudDirectPhase, cosine) * shadow
                                      + Phase(_ReDefinitionCloudMultiplePhase, cosine) * sqrt(shadow);
                    light = _ReDefinitionCloudMoonColour.rgb * (lit * opacity * scattered) * through;
                }
                // The sky's light the moon gives, which the table already holds in the planet's
                // shadow; without a gain (0) not read.
                float3 sky = 0.0f;
                if (any(_ReDefinitionCloudSkyColour.rgb > 0.0f))
                    sky = _ReDefinitionCloudSkyColour.rgb * opacity * SkyLight(r, dot(position, towards) / r);
                // 4 to 7, for the probe's means, each times the opacity: the transmittance towards
                // the moon, the table's part of it, the planet's shadow times the cloud's own, and
                // the opacity.
                if (_ReDefinitionCloudDebug > 3.5f)
                {
                    float mu = dot(position, towards) / r;
                    float3 part = _ReDefinitionCloudDebug < 4.5f ? Transmittance(r, mu)
                                : _ReDefinitionCloudDebug < 5.5f ? Table(r, mu)
                                : _ReDefinitionCloudDebug < 6.5f ? (float3)(lit * SelfShadow(position, towards)) : 1.0f;
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
