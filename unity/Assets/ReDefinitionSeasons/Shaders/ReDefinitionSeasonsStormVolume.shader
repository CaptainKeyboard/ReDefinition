// The snow veil of a storm cell for ReDefinition Seasons (src/Seasons/StormVolume.cs). It is a broad, translucent curtain of falling
// snow from the base of the cell's cloud to the ground, raymarched inside a cylinder around the cell's centre. The cloud itself is
// EVE's (EveLayerStorm, EveCellLayer); this shader draws the snow under it only. Only the back faces of the cylinder are drawn
// (Cull Front), so that the same pixels are shaded from outside and from inside the cell. The shader intersects the view ray with
// the cylinder itself.
//
// The curtain is soft vertical streaks: two scales of the 3D noise stretched along the vertical and slanted downwind with height. It is
// denser in the upper two thirds and in the middle, fades over the last 250 m to the ground and thins out at the rim. The extinction
// is low (Beer-Lambert) and the opacity of a ray is capped at MaxOpacity, so that the horizon and the trees behind stay partly
// visible. The colour is the sky's light on snow: light grey, a little brighter where the sun scatters forward, darker where the
// curtain is thick and under the cloud base. It never goes above SnowCeiling: the scene is HDR, and a value over 1 glows white.
//
// Everything is in the cell's frame: up (the planet's radial at the centre), right, and forward = up x right. The ground is curved:
// the height above it is y + curvature * r^2, with r the distance from the axis. The horizontal and the fall offsets the C# side
// passes are wrapped by whole noise tiles, so that the pattern does not jump when they wrap.
//
// Opaque things in front of the volume end the ray: with STORM_DEPTH the shader reads _CameraDepthTexture, without it the volume is
// drawn over everything. The result is premultiplied (Blend One OneMinusSrcAlpha).
Shader "ReDefinition/SeasonsStormVolume"
{
    Properties
    {
        _Noise ("Noise", 3D) = "white" {}
        _SnowBoost ("Snow extinction factor", Float) = 1
        _VeilTint ("Veil diagnostics tint", Color) = (0, 0, 0, 0)
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
            // x: the cloud base above the ground, m; y: the top of the volume above the centre, m;
            // z: how far the volume reaches below the centre, m.
            float4 _Heights;
            // xy: the pattern's offset in x and z, m (wrapped by 6000); z: the shear in x per m of height; w: the fall offset, m (wrapped by 14000).
            float4 _Wind;
            // x: the strength, 0 to 1; y: the number of steps; z: the shear in z per m of height; w: the longest distance a ray is marched, m.
            float4 _Look;
            // xyz: towards the sun, world space.
            float4 _Sun;
            float4 _SunColour;
            float4 _AmbientColour;
            // Diagnostics: a factor on the veil's extinction, and a tint (rgb, share in a) that replaces the veil's colour.
            float _SnowBoost;
            float4 _VeilTint;

            // The extinction per metre at full strength and full density, and the most opaque a ray can get.
            static const float SnowSigma = 0.0120;
            static const float MaxOpacity = 0.62;
            // No colour channel goes above this: the scene is HDR and anything over 1 glows.
            static const float SnowCeiling = 0.85;
            // The tiles, m: the broad and the fine streaks horizontally; the streaks vertically (the same for both, so they are long).
            static const float StreakTileBroad = 1500.0;
            static const float StreakTileFine = 500.0;
            static const float StreakTileV = 14000.0;
            // How far the veil's axis leans downwind per m of height, as a share of the pattern's shear.
            static const float VeilLean = 0.8;
            // The fade over the last metres to the ground, and the fade into the cloud base.
            static const float GroundFadeM = 250.0;

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

            // The extinction of the snow at a place in the cell's frame, and how thick it is there as a share of the densest (0 to 1).
            // The inputs are the horizontal position and the height above the ground.
            float Density(float lx, float lz, float h, float lod, float R, out float thick)
            {
                float base_ = _Heights.x;
                float strength = _Look.x;
                thick = 0.0;

                // From the cloud base (a soft edge a little into the cloud) down to the ground (thin over the last GroundFadeM).
                float top = 1.0 - smoothstep(base_ - 60.0, base_ + 60.0, h);
                float ground = smoothstep(-40.0, GroundFadeM, h);
                float column = top * ground;
                if (column <= 0.0) return 0.0;

                // The pattern is sheared downwind with height and moved by the wind; the axis of the veil leans the same way.
                float sx = lx - _Wind.x + _Wind.z * h;
                float sz = lz - _Wind.y + _Look.z * h;
                float lean = VeilLean * h;
                float vx = lx - _Wind.z * lean;
                float vz = lz - _Look.z * lean;
                float veilRho = sqrt(vx * vx + vz * vz) / R;

                // Long streaks: the noise is read at a horizontal tile and stretched along the vertical, so that a streak is a few hundred
                // metres wide and kilometres long. The fall offset moves it down.
                float v = h / StreakTileV + _Wind.w / StreakTileV;
                float3 pb = float3(sx / StreakTileBroad, v, sz / StreakTileBroad);
                float3 pf = float3(sx / StreakTileFine, v * 2.0 + 0.21, sz / StreakTileFine);
                float s = Noise(pb, lod) * 0.6 + Noise(pf, lod) * 0.4;
                float streak = 0.05 + 0.95 * smoothstep(0.38, 0.70, s);

                // Denser in the upper two thirds, thinner to the ground; dense in the middle and thin at the rim, with a ragged edge.
                float hn = saturate(h / max(base_, 1.0));
                float vertical = lerp(0.45, 1.0, smoothstep(0.0, 0.34, hn));
                float radial = (1.0 - smoothstep(0.30, 1.0, veilRho + (s - 0.5) * 0.4)) * (0.55 + 0.45 * (1.0 - veilRho));

                float d = column * streak * radial * vertical;
                thick = saturate(d);
                return d * SnowSigma * _SnowBoost * strength;
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

                float ylo = -_Heights.z;
                float yhi = _Heights.y;
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
                // A forward lobe: the snow is brighter looking towards the sun.
                float forwardLight = pow(cosSun, 6.0);

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
                    float h = y + _Up.w * (lx * lx + lz * lz);

                    float thick;
                    float sigma = Density(lx, lz, h, lod, R, thick);
                    if (sigma > 1e-7)
                    {
                        float a_ = 1.0 - exp(-sigma * dt);

                        // Light grey in an overcast storm: the sky's light, dimmer under the cloud base and where the curtain is thick
                        // (it shades itself), with a little of the sun where it scatters forward.
                        float hn = saturate(h / max(base_, 1.0));
                        float shade = lerp(0.70, 1.0, hn) * lerp(1.0, 0.62, thick);
                        float3 snowColour = (_AmbientColour.rgb * 1.0 + _SunColour.rgb * (0.04 + 0.30 * forwardLight)) * shade;
                        snowColour = lerp(snowColour, _VeilTint.rgb, _VeilTint.a);
                        snowColour = min(snowColour, SnowCeiling);

                        colour += transmittance * a_ * snowColour;
                        transmittance *= 1.0 - a_;
                        if (transmittance < 0.02) break;
                    }
                    t += dt;
                }
                // The colour is premultiplied by the same cap as the opacity, so that the average colour is what the ray's snow is.
                return float4(colour * MaxOpacity, (1.0 - transmittance) * MaxOpacity);
            }
            ENDCG
        }
    }
}
