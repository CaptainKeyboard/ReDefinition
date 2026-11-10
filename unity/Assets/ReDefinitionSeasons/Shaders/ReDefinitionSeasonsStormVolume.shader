// A snowstorm cell for ReDefinition Seasons (src/Seasons/StormVolume.cs): a veil of falling snow under a low, wide snow cloud,
// raymarched inside a cylinder around the cell's centre. The mesh it is drawn on is that cylinder; only its back faces are drawn
// (Cull Front), so that the same pixels are shaded from outside and from inside the cell. The shader does not take the mesh's surface
// as the volume: it intersects the view ray with the cylinder itself.
//
// The cloud is a nimbostratus: a thick, flat-topped blanket with a soft grey underside and feathered edges. Its noise is large-scale.
// _CloudWeight is 1 where this shader draws the cloud. It is 0 where EVE's volumetric clouds supply it (EveCellLayer), and then only
// the snow veil is drawn. The veil is broad and diffuse, slanted by the wind and thicker towards the axis. It fades in from the cloud
// base and is lit grey-white.
//
// Everything is in the cell's frame: up (the planet's radial at the centre), right, and forward = up x right. The ground is curved:
// the height above it is y + curvature * r^2, with r the distance from the axis. The density comes from one 3D noise texture
// (tileable, 64^3), read at two scales. The horizontal and the fall offsets the C# side passes are wrapped by whole noise tiles, so
// that the pattern does not jump when they wrap.
//
// Opaque things in front of the volume end the ray: with STORM_DEPTH the shader reads _CameraDepthTexture, without it the volume is
// drawn over everything. The result is premultiplied (Blend One OneMinusSrcAlpha).
Shader "ReDefinition/SeasonsStormVolume"
{
    Properties
    {
        _Noise ("Noise", 3D) = "white" {}
        _CloudWeight ("Cloud weight", Float) = 1
    }
    SubShader
    {
        Tags { "Queue" = "Transparent-5" "RenderType" = "Transparent" "IgnoreProjector" = "True" "ForceNoShadowCasting" = "True" }
        Pass
        {
            Name "STORM"
            Cull Front
            ZWrite Off
            ZTest Always
            Blend One OneMinusSrcAlpha

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #pragma multi_compile_local __ STORM_DEPTH
            #include "UnityCG.cginc"

            sampler3D _Noise;
            UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);

            // xyz: the cell's centre at the ground, world space; w: the radius, m.
            float4 _Cell;
            // xyz: up; w: the planet's curvature, 1 / (2 * radius), per m.
            float4 _Up;
            // xyz: right (a horizontal direction fixed for the cell).
            float4 _Right;
            // x: the cloud base above the ground, m; y: the cloud layer's thickness, m; z: the top of the volume above the centre, m;
            // w: how far the volume reaches below the centre, m.
            float4 _Heights;
            // xy: the pattern's offset in x and z, m (wrapped by 6000); z: the shear in x per m of height; w: the fall offset, m (wrapped by 14000).
            float4 _Wind;
            // x: the strength, 0 to 1; y: the number of steps; z: the shear in z per m of height; w: the longest distance a ray is marched, m.
            float4 _Look;
            // xyz: towards the sun, world space.
            float4 _Sun;
            float4 _SunColour;
            float4 _AmbientColour;
            // 1: the shader draws the snow cloud; 0: EVE's volumetric cloud is there, only the snow is drawn.
            float _CloudWeight;

            // The extinction per metre of the cloud blanket and of the snow veil, at full strength and full density.
            static const float CloudSigma = 0.0065;
            static const float SnowSigma = 0.0014;
            // The tiles, m: the cloud and the snow horizontally; the snow's streaks vertically.
            static const float CloudTile = 6000.0;
            static const float SnowTile = 1500.0;
            static const float StreakTile = 14000.0;
            // How far the veil's axis leans downwind per m of height, as a share of the pattern's shear.
            static const float VeilLean = 0.8;

            struct appdata
            {
                float4 vertex : POSITION;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 world : TEXCOORD0;
                float4 screen : TEXCOORD1;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.world = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.screen = ComputeScreenPos(o.pos);
                return o;
            }

            // Interleaved gradient noise of the pixel: a fixed pattern that does not change from frame to frame.
            float Dither(float2 pixel)
            {
                return frac(52.9829189 * frac(dot(pixel, float2(0.06711056, 0.00583715))));
            }

            float Noise(float3 p, float lod)
            {
                return tex3Dlod(_Noise, float4(p, lod)).r;
            }

            // The extinction of the cloud blanket and of the snow veil at a place in the cell's frame. The inputs are the horizontal
            // position, the height above the ground and the distance from the axis as a share of the radius. Also out: where the blanket ends.
            void Density(float lx, float lz, float h, float rho, float lod, float R, out float cloud, out float snow, out float top)
            {
                float base_ = _Heights.x;
                float thick = _Heights.y;
                float strength = _Look.x;

                // The pattern sheared downwind with height and moved by the wind.
                float sx = lx - _Wind.x + _Wind.z * h;
                float sz = lz - _Wind.y + _Look.z * h;

                cloud = 0.0;
                top = base_ + thick;
                if (_CloudWeight > 0.0)
                {
                    // Large, gentle noise only: one octave of the 3D noise with a half-weight second at twice the scale.
                    // A nimbostratus: a wide body in layers. The horizontal scale is large; the vertical one is short, so that the
                    // density banks in sheets, and a finer octave breaks the sheets up.
                    float3 pc = float3(sx / CloudTile, h / 1400.0, sz / CloudTile);
                    float large = Noise(float3(sx / (CloudTile * 2.0), 0.31, sz / (CloudTile * 2.0)), lod);
                    float n = Noise(pc, lod) * 0.55 + Noise(pc * 2.3 + 0.37, lod) * 0.30 + Noise(pc * 5.1 + 0.71, lod) * 0.15;
                    // The top undulates over a few hundred m; the underside rises softly and raggedly over 300 m.
                    top = base_ + thick - 500.0 * large;
                    float layer = smoothstep(base_ - 80.0 * n, base_ + 300.0, h) * (1.0 - smoothstep(top - 450.0, top, h));
                    if (layer > 0.0)
                    {
                        // A weak storm is a smaller, thinner cloud; the rim is feathered and ragged over most of the radius.
                        float reach = lerp(0.7, 1.0, strength);
                        float rim = rho + (large - 0.5) * 0.5 + (n - 0.5) * 0.25;
                        float edge = pow(1.0 - smoothstep(0.05 * reach, reach, rim), 1.8);
                        float body = saturate(0.25 + 1.2 * n);
                        cloud = layer * edge * body * CloudSigma * strength;
                    }
                }

                snow = 0.0;
                // Fades in below the cloud base over a quarter of the base's height (at least 120 m, at most 300 m) and out at the ground.
                float fadeIn = clamp(base_ * 0.25, 120.0, 300.0);
                float column = smoothstep(-60.0, 20.0, h) * (1.0 - smoothstep(base_ - fadeIn, base_ + 20.0, h));
                if (column > 0.0)
                {
                    // The veil leans downwind: the axis it is centred on moves with the height.
                    float lean = VeilLean * h;
                    float vx = lx - _Wind.z * lean;
                    float vz = lz - _Look.z * lean;
                    float veilRho = sqrt(vx * vx + vz * vz) / R;
                    float3 ps = float3(sx / SnowTile, h / StreakTile + _Wind.w / StreakTile, sz / SnowTile);
                    float s = Noise(ps, lod) * 0.6 + Noise(ps * float3(3.0, 2.0, 3.0) + 0.21, lod) * 0.4;
                    // Mostly diffuse, with softer streaks in it.
                    float streak = 0.1 + 0.9 * saturate((s - 0.36) * 3.6);
                    float radial = (1.0 - smoothstep(0.30, 1.0, veilRho + (s - 0.5) * 0.45)) * (0.55 + 1.1 * (1.0 - veilRho));
                    snow = column * streak * radial * SnowSigma * strength;
                }
            }

            float4 frag(v2f i) : SV_Target
            {
                float3 ro = _WorldSpaceCameraPos;
                float3 rd = normalize(i.world - ro);

                // The ray against the cylinder: the circle round the axis, then the slab between the lowest and the highest point.
                float3 up = _Up.xyz;
                float R = _Cell.w;
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
                    if (disc <= 0.0) return float4(0, 0, 0, 0);
                    float root = sqrt(disc);
                    tc0 = (-b - root) / a;
                    tc1 = (-b + root) / a;
                }
                else if (c > 0.0)
                    return float4(0, 0, 0, 0);

                float ylo = -_Heights.w;
                float yhi = _Heights.z;
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
                    return float4(0, 0, 0, 0);

                float t0 = max(max(tc0, ty0), _ProjectionParams.y);
                float t1 = min(min(tc1, ty1), min(_Look.w, _ProjectionParams.z));

                #if defined(STORM_DEPTH)
                    // Things already drawn end the ray; the sky (the far plane) does not.
                    float2 uv = i.screen.xy / i.screen.w;
                    float eye = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, uv));
                    if (eye < _ProjectionParams.z * 0.99)
                    {
                        float forward = dot(rd, -UNITY_MATRIX_V[2].xyz);
                        t1 = min(t1, eye / max(forward, 1e-3));
                    }
                #endif

                if (t1 <= t0) return float4(0, 0, 0, 0);

                float steps = clamp(_Look.y, 8.0, 64.0);
                float dt = (t1 - t0) / steps;
                float t = t0 + dt * Dither(i.pos.xy);
                // Far steps read the smaller levels of the noise, which keeps the picture calm.
                float lod = clamp(log2(max(dt, 1.0) / 30.0), 0.0, 4.0);

                float3 right = _Right.xyz;
                float3 forwardAxis = cross(up, right);
                float base_ = _Heights.x;
                float cosSun = saturate(dot(rd, _Sun.xyz));
                // A mild forward lobe: the cloud is milky, not hard-lit.
                float phase = 0.7 + 0.5 * pow(cosSun, 5.0);

                float3 colour = float3(0, 0, 0);
                float transmittance = 1.0;
                [loop]
                for (int k = 0; k < 64; k++)
                {
                    if (k >= (int)steps) break;
                    float3 p = ro + rd * t;
                    float3 r = p - _Cell.xyz;
                    float y = dot(r, up);
                    float lx = dot(r, right);
                    float lz = dot(r, forwardAxis);
                    float rr2 = lx * lx + lz * lz;
                    float h = y + _Up.w * rr2;
                    float rho = sqrt(rr2) / R;

                    float cloud;
                    float snow;
                    float top;
                    Density(lx, lz, h, rho, lod, R, cloud, snow, top);
                    float sigma = cloud + snow;
                    if (sigma > 1e-6)
                    {
                        float a_ = 1.0 - exp(-sigma * dt);

                        // Inside the blanket d is 0 at its top and 1 at its base: the underside is a mid grey, the top is lit.
                        float d = saturate((top - h) / max(_Heights.y, 1.0));
                        float lit = 1.0 - d;
                        float3 shade = _AmbientColour.rgb * (0.78 + 0.22 * lit)
                                     + _SunColour.rgb * phase * exp(-d * 2.6) * 0.42;
                        float3 cloudColour = shade * lerp(0.55, 1.0, lit);
                        // The snow reads light, bluish white; the sky's light on it, a little of the sun's.
                        float3 snowColour = (_AmbientColour.rgb * 1.15 + _SunColour.rgb * 0.22 * phase) * float3(1.0, 1.0, 1.0)
                                          * (0.62 + 0.22 * saturate(h / max(base_, 1.0)));

                        colour += transmittance * a_ * ((cloud * cloudColour + snow * snowColour) / sigma);
                        transmittance *= 1.0 - a_;
                        if (transmittance < 0.02) break;
                    }
                    t += dt;
                }
                return float4(colour, 1.0 - transmittance);
            }
            ENDCG
        }
    }
}
