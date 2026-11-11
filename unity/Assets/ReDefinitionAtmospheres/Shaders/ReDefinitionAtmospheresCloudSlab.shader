// The weather slab of ReDefinition's own cloud renderer (spike of the atmosphere plan, tests/WeatherPlayer): a cloud deck raymarched
// between a base and a top above the ground, around a cell's centre. Three density layers (a broad deck, a mid layer, sparse
// towers) are cut from one tileable 3D noise by their height profiles, eroded by a fine noise, and bounded sideways by a lobed,
// wind-stretched outline. The underside is dark, the sun's light comes through three taps towards the sun with a two-lobe
// Henyey-Greenstein phase, and the sky's light is the ambient, brighter towards the top.
//
// The code is ours; the technique is the usual one for volumetric weather decks (layered density profiles, erosion, a short
// light march, dual-lobe phase). Everything is in the cell's frame like the storm curtain (ReDefinitionAtmospheresStormVolume.shader):
// up, right, forward = up x right, and the ground is curved, the height above it is y + curvature * r^2.
//
// Pass 0 marches the view rays and writes the premultiplied colour and the opacity into a texture that may be smaller than the
// screen (the spike measures the full and the half size). Pass 1 lays that texture over the scene (Blend One OneMinusSrcAlpha),
// bilinearly. The rays end at what is drawn: _CameraDepthTexture. The rays are built from the camera's basis and the jitter, so
// that they are the rays of the jittered frame an upscaler gets.
Shader "ReDefinition/AtmospheresCloudSlab"
{
    Properties
    {
        _Noise ("Noise", 3D) = "white" {}
        _MainTex ("Slab", 2D) = "black" {}
    }
    SubShader
    {
        Tags { "Queue" = "Transparent-6" "RenderType" = "Transparent" "IgnoreProjector" = "True" "ForceNoShadowCasting" = "True" }
        Cull Off
        ZWrite Off
        ZTest Always

        CGINCLUDE
        #include "UnityCG.cginc"

        sampler3D _Noise;
        sampler2D _MainTex;
        UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);

        // xyz: the cell's centre at the ground, world space; w: the radius, m.
        float4 _Cell;
        // xyz: up; w: the planet's curvature, 1 / (2 * radius), per m.
        float4 _Up;
        // xyz: right (a horizontal direction fixed for the cell).
        float4 _Right;
        // x: the base above the ground, m; y: the top above the ground, m; z: the share of the base that is dark, 0 to 1;
        // w: the cell's strength, 0 to 1.
        float4 _Slab;
        // xy: the pattern's offset in x and z, m (wrapped by 6000); z: the shear in x per m of height; w: the shear in z per m.
        float4 _Wind;
        // x: the steps; y: the longest distance a ray is marched, m; z: the frame number (for the animated dither); w: 1 where the
        // dither changes from frame to frame.
        float4 _Look;
        // xyz: towards the sun, world space; w: the sun's share of the light.
        float4 _Sun;
        float4 _SunColour;
        float4 _AmbientColour;
        // The camera's basis for the rays: right and up scaled by the tangent of half the field of view, and forward; w of the
        // first: the jitter's x in NDC, w of the second: its y.
        float4 _CamRight;
        float4 _CamUp;
        float4 _CamForward;
        float2 _TargetSize;

        // The extinction per metre at full density, the least light the sun's taps let through, and the most opaque a ray gets.
        static const float SlabSigma = 0.010;
        // How the layers are cut: tiles of the base noise, of the erosion, in m, and the vertical tile of the base noise.
        static const float TileBase = 6000.0;
        static const float TileMid = 3500.0;
        static const float TileTower = 2400.0;
        static const float TileErosion = 520.0;
        // No colour channel goes above this: the scene is HDR and anything over 1 glows.
        static const float Ceiling = 1.0;

        struct appdata
        {
            float4 vertex : POSITION;
            float2 uv : TEXCOORD0;
        };

        struct v2f
        {
            float4 pos : SV_POSITION;
            float2 uv : TEXCOORD0;
        };

        v2f vert(appdata v)
        {
            v2f o;
            o.pos = UnityObjectToClipPos(v.vertex);
            o.uv = v.uv;
            return o;
        }

        // Interleaved gradient noise of the pixel, moved on every frame where _Look.w says so.
        float Dither(float2 pixel)
        {
            pixel += _Look.w * 5.588238 * fmod(_Look.z, 64.0);
            return frac(52.9829189 * frac(dot(pixel, float2(0.06711056, 0.00583715))));
        }

        float Noise(float3 p, float lod)
        {
            return tex3Dlod(_Noise, float4(p, lod)).r;
        }

        float HG(float cosine, float g)
        {
            float g2 = g * g;
            return (1.0 - g2) / (4.0 * 3.14159265 * pow(max(1.0 + g2 - 2.0 * g * cosine, 1e-3), 1.5));
        }

        // The extinction per metre at a place in the cell's frame; u gets the place's height in the slab, 0 to 1, density
        // the density before the extinction's scale, 0 to 1.
        float Density(float lx, float lz, float h, float lod, out float u, out float density)
        {
            float base_ = _Slab.x;
            float thick = max(_Slab.y - _Slab.x, 1.0);
            u = (h - base_) / thick;
            density = 0.0;
            if (u < -0.02 || u > 1.02) return 0.0;

            // The outline: the radius at the bearing, with lobes, stretched along the wind.
            float R = _Cell.w;
            float theta = atan2(lz, lx);
            float lobes = 1.0 + 0.18 * sin(2.0 * theta + 0.7) + 0.12 * sin(3.0 * theta + 2.1) + 0.08 * sin(5.0 * theta + 4.0);
            float2 along = normalize(float2(_Wind.z + 1e-4, _Wind.w + 1e-4));
            float stretch = 1.0 + 0.3 * abs(dot(normalize(float2(lx, lz) + 1e-3), along));
            float rho = sqrt(lx * lx + lz * lz) / (R * lobes * stretch);

            // The pattern is sheared downwind with height and moved by the wind.
            float sx = lx - _Wind.x + _Wind.z * h;
            float sz = lz - _Wind.y + _Wind.w * h;
            float3 pb = float3(sx / TileBase, h / 3000.0, sz / TileBase);
            float nb = Noise(pb, lod);
            float edge = 1.0 - smoothstep(0.55, 1.0, rho + (nb - 0.5) * 0.35);
            if (edge <= 0.0) return 0.0;

            // Layer 1, the deck: wide, low, nearly everywhere in the cell.
            float profile1 = smoothstep(0.0, 0.10, u) * (1.0 - smoothstep(0.35, 0.58, u));
            float cover1 = smoothstep(0.30, 0.46, nb);
            // Layer 2, the mid layer: lumps.
            float n2 = Noise(float3(sx / TileMid + 0.31, h / 2200.0 + 0.17, sz / TileMid + 0.53), lod);
            float profile2 = smoothstep(0.18, 0.45, u) * (1.0 - smoothstep(0.62, 0.88, u));
            float cover2 = smoothstep(0.42, 0.60, n2);
            // Layer 3, the towers: sparse, on top.
            float n3 = Noise(float3(sx / TileTower + 0.73, h / 1500.0 + 0.41, sz / TileTower + 0.11), lod);
            float profile3 = smoothstep(0.55, 0.78, u) * (1.0 - smoothstep(0.90, 1.0, u));
            float cover3 = smoothstep(0.56, 0.72, n3) * smoothstep(0.35, 0.6, nb);

            float d = profile1 * cover1 + 0.85 * profile2 * cover2 + 0.8 * profile3 * cover3;
            d = saturate(d) * edge;
            if (d <= 0.0) return 0.0;

            // The erosion: the fine noise takes the thin parts away first and leaves ragged edges.
            float e = Noise(float3(sx / TileErosion, h / (TileErosion * 1.3) + 0.07, sz / TileErosion), lod);
            d = saturate((d - e * 0.40) / (1.0 - e * 0.40));
            density = d;
            return d * SlabSigma * _Slab.w;
        }

        // The ray's colour and opacity: premultiplied colour in rgb, opacity in a.
        float4 March(float2 uv, float2 pixel)
        {
            float3 ro = _WorldSpaceCameraPos;
            float2 ndc = uv * 2.0 - 1.0;
            ndc -= float2(_CamRight.w, _CamUp.w);
            float3 rd = normalize(_CamForward.xyz + ndc.x * _CamRight.xyz + ndc.y * _CamUp.xyz);

            float3 up = _Up.xyz;
            float R = _Cell.w * 1.55;
            float3 rel = ro - _Cell.xyz;
            float roy = dot(rel, up);
            float rdy = dot(rd, up);
            float3 roh = rel - up * roy;
            float3 rdh = rd - up * rdy;
            float a = dot(rdh, rdh);
            float b = dot(roh, rdh);
            float c = dot(roh, roh) - R * R;
            float tc0 = -1e9;
            float tc1 = 1e9;
            if (a > 1e-8)
            {
                float disc = b * b - a * c;
                if (disc <= 0.0) return 0.0;
                float root = sqrt(disc);
                tc0 = (-b - root) / a;
                tc1 = (-b + root) / a;
            }
            else if (c > 0.0)
                return 0.0;

            // The slab: the ground curves away, so the base and the top are taken at the cell's centre and a margin allows for the fall.
            float fall = _Up.w * R * R;
            float ylo = _Slab.x - 60.0;
            float yhi = _Slab.y + 60.0 + 0.0;
            float ty0 = -1e9;
            float ty1 = 1e9;
            if (abs(rdy) > 1e-5)
            {
                float ta = (ylo - roy) / rdy;
                float tb = (yhi - roy) / rdy;
                ty0 = min(ta, tb);
                ty1 = max(ta, tb);
            }
            else if (roy < ylo || roy > yhi)
                return 0.0;

            float t0 = max(max(tc0, ty0), _ProjectionParams.y);
            float t1 = min(min(tc1, ty1), min(_Look.y, _ProjectionParams.z));

            float2 suv = uv;
            float eye = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, suv));
            if (eye < _ProjectionParams.z * 0.99)
            {
                float forward = dot(rd, _CamForward.xyz);
                t1 = min(t1, eye / max(forward, 1e-3));
            }
            if (t1 <= t0) return 0.0;

            float steps = clamp(_Look.x, 8.0, 96.0);
            float dt = (t1 - t0) / steps;
            float t = t0 + dt * Dither(pixel);
            float lod = clamp(log2(max(dt, 1.0) / 60.0), 0.0, 4.0);

            float3 right = _Right.xyz;
            float3 forwardAxis = cross(up, right);
            float cosSun = dot(rd, _Sun.xyz);
            float phase = lerp(HG(cosSun, -0.15), HG(cosSun, 0.62), 0.45) * 4.0 * 3.14159265 * 0.5;

            float3 colour = 0.0;
            float transmittance = 1.0;
            [loop]
            for (int k = 0; k < 96; k++)
            {
                if (k >= (int)steps) break;
                float3 p = ro + rd * t;
                float3 r = p - _Cell.xyz;
                float lx = dot(r, right);
                float lz = dot(r, forwardAxis);
                float h = dot(r, up) + _Up.w * (lx * lx + lz * lz);

                float u, dens;
                float sigma = Density(lx, lz, h, lod, u, dens);
                if (sigma > 1e-7)
                {
                    float opacity = 1.0 - exp(-sigma * dt);

                    // The sun's light through three taps towards the sun, nearer ones denser.
                    float3 sp = p;
                    float depthSun = 0.0;
                    float stepSun = 120.0;
                    [unroll]
                    for (int s = 0; s < 3; s++)
                    {
                        sp += _Sun.xyz * stepSun;
                        float3 sr = sp - _Cell.xyz;
                        float slx = dot(sr, right);
                        float slz = dot(sr, forwardAxis);
                        float sh = dot(sr, up) + _Up.w * (slx * slx + slz * slz);
                        float su, sd;
                        depthSun += Density(slx, slz, sh, lod + 1.0, su, sd) * stepSun;
                        stepSun *= 2.2;
                    }
                    // Multiple scattering as three octaves with less extinction and a flatter phase.
                    float3 sun = 0.0;
                    float attenuation = 1.0;
                    float contribution = 1.0;
                    float flatten = 1.0;
                    [unroll]
                    for (int o = 0; o < 3; o++)
                    {
                        sun += contribution * exp(-depthSun * attenuation) * lerp(1.0, phase, flatten);
                        attenuation *= 0.45;
                        contribution *= 0.5;
                        flatten *= 0.5;
                    }
                    sun *= _SunColour.rgb * _Sun.w;

                    // The sky's light: brighter towards the top, a dark underside where the base is dark, and less deep inside
                    // a dense mass where the light does not reach.
                    float up01 = saturate(u);
                    float under = lerp(1.0 - _Slab.z * 0.85, 1.0, smoothstep(0.0, 0.55, up01));
                    float inner = lerp(1.0, 0.55, saturate(dens));
                    float3 ambient = _AmbientColour.rgb * (0.55 + 0.7 * up01) * under * inner;

                    float3 light = min(sun * under + ambient, Ceiling);
                    colour += transmittance * opacity * light;
                    transmittance *= 1.0 - opacity;
                    if (transmittance < 0.015) break;
                }
                t += dt;
            }
            return float4(colour, 1.0 - transmittance);
        }
        ENDCG

        // Pass 0: the march, into the slab texture.
        Pass
        {
            Name "MARCH"
            Blend Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragMarch
            #pragma target 3.5
            float4 fragMarch(v2f i) : SV_Target
            {
                return March(i.uv, i.pos.xy);
            }
            ENDCG
        }

        // Pass 1: the slab texture over the scene.
        Pass
        {
            Name "COMPOSITE"
            Blend One OneMinusSrcAlpha
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragComposite
            #pragma target 3.5
            float4 fragComposite(v2f i) : SV_Target
            {
                return tex2D(_MainTex, i.uv);
            }
            ENDCG
        }
    }
}
