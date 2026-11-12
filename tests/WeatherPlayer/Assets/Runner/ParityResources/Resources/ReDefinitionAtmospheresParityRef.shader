// GPU reference path of the parity harness (package T11): a brute-force march of the SAME analytic density fields as tools/weather_metrics/
// parity_ref.py (homogeneous shell or plane layers, the smooth-edged cell, the hard-edged strip), with the reference's dual-lobe
// Henyey-Greenstein phase function, the sun term marched with _LightSteps steps per layer and the sky or the ground behind. Single
// scattering only (the order-1 term of the reference renderer); multiple scattering is the offline renderer's. Not shipped: the file
// lives in the weather player's own Resources (tests/WeatherPlayer/Assets/Runner/ParityResources), not in the bundle's shader folder,
// which the bundle builder takes whole. It writes (tau, cloud radiance, total radiance, active
// steps) into a float target; tests/WeatherPlayer/Assets/Runner/ParityScene.cs sets the uniforms from the scene table.
// Geometry: the planet's centre is the origin and z is up in sphere mode (_Flat 0, the planet radius _PlanetR); in flat mode the ground
// is the plane z = 0. Units are metres.
Shader "Hidden/ReDefinition/AtmospheresParityRef"
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

            static const float SUN_E = 3.14159265;
            static const float PI = 3.14159265;

            float _PlanetR, _Flat;
            float4 _CamPos, _Fwd, _Right, _UpC;
            float _HalfV, _Aspect;
            float4 _Sun;
            // Layers: x base, y top, z sigma (<= 0 unused), w shape mode (0 none, 1 cell, 2 strip edge).
            float4 _L0, _L1;
            // The cell: x centre altitude, y horizontal radius, z vertical radius; the strip: x edge position.
            float4 _Cell;
            float4 _Occ;
            float _ViewSteps, _LightSteps;
            float4 _Phase; // g1, g2, w1, albedo
            float4 _Env;   // zenith, horizon, ground (sky below the horizon), ground albedo
            float _GroundAlbedoAmbient;

            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            v2f vert(appdata_img v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.texcoord;
                return o;
            }

            float hg(float mu, float g)
            {
                return (1.0 - g * g) / (4.0 * PI * pow(max(1.0 + g * g - 2.0 * g * mu, 1e-6), 1.5));
            }

            float phase(float mu)
            {
                return _Phase.z * hg(mu, _Phase.x) + (1.0 - _Phase.z) * hg(mu, _Phase.y);
            }

            float altitude(float3 p)
            {
                return _Flat > 0.5 ? p.z : length(p) - _PlanetR;
            }

            float3 upAt(float3 p)
            {
                return _Flat > 0.5 ? float3(0, 0, 1) : normalize(p);
            }

            bool hitSphere(float3 o, float3 d, float3 c, float r, out float t0, out float t1)
            {
                float3 oc = o - c;
                float b = dot(oc, d);
                float cc = dot(oc, oc) - r * r;
                float disc = b * b - cc;
                t0 = 0; t1 = 0;
                if (disc <= 0) return false;
                float s = sqrt(disc);
                t0 = -b - s; t1 = -b + s;
                return true;
            }

            // The marching intervals of one layer: (a0, a1) and, for a hollow shell, (b0, b1); empty where the end is not after the start.
            void slots(float3 o, float3 d, float tmax, float4 layer, out float4 s)
            {
                s = 0;
                if (layer.z <= 0) return;
                if (_Flat > 0.5)
                {
                    float lo = layer.x - o.z, hi = layer.y - o.z;
                    if (abs(d.z) < 1e-6)
                    {
                        if (o.z >= layer.x && o.z <= layer.y) s.xy = float2(0, tmax);
                    }
                    else
                    {
                        float ta = lo / d.z, tb = hi / d.z;
                        s.xy = float2(max(min(ta, tb), 0), min(max(ta, tb), tmax));
                    }
                }
                else
                {
                    float to0, to1, ti0, ti1;
                    bool ho = hitSphere(o, d, float3(0, 0, 0), _PlanetR + layer.y, to0, to1);
                    bool hi = layer.x > 0 && hitSphere(o, d, float3(0, 0, 0), _PlanetR + layer.x, ti0, ti1);
                    if (ho)
                    {
                        s.x = max(to0, 0);
                        s.y = min(hi ? ti0 : to1, tmax);
                        if (hi) { s.z = max(ti1, 0); s.w = min(to1, tmax); }
                    }
                }
                // The cell is clipped to its bounding sphere.
                if (layer.w > 0.5 && layer.w < 1.5)
                {
                    float tb0, tb1;
                    float3 c = _Flat > 0.5 ? float3(0, 0, _Cell.x) : float3(0, 0, _PlanetR + _Cell.x);
                    if (hitSphere(o, d, c, _Cell.y * 1.05, tb0, tb1))
                    {
                        s.x = max(s.x, tb0); s.y = min(s.y, tb1);
                        s.z = max(s.z, tb0); s.w = min(s.w, tb1);
                    }
                    else s = 0;
                }
                s.y = max(s.y, s.x);
                s.w = max(s.w, s.z);
            }

            float shapeOf(float3 p, float mode)
            {
                if (mode < 0.5) return 1;
                if (mode < 1.5)
                {
                    float3 c = _Flat > 0.5 ? float3(0, 0, _Cell.x) : float3(0, 0, _PlanetR + _Cell.x);
                    float3 q = p - c;
                    float rh = _Cell.y, rv = _Cell.z;
                    float r = sqrt((q.x / rh) * (q.x / rh) + (q.y / rh) * (q.y / rh) + (q.z / rv) * (q.z / rv));
                    float x = saturate((1.0 - r) / 0.35);
                    float v = x * x * (3.0 - 2.0 * x);
                    return v * (q.z > -0.5 * rv ? 1.0 : 0.0);
                }
                return p.x < _Cell.x ? 1.0 : 0.0;
            }

            float layerDensity(float3 p, float4 layer)
            {
                if (layer.z <= 0) return 0;
                float a = altitude(p);
                if (a < layer.x || a > layer.y) return 0;
                return layer.z * shapeOf(p, layer.w);
            }

            float density(float3 p)
            {
                return layerDensity(p, _L0) + layerDensity(p, _L1);
            }

            // The optical depth of the two layers along a ray, by the midpoint rule, steps per slot.
            float tauLayer(float3 o, float3 d, float tmax, float4 layer, float steps)
            {
                float4 s;
                slots(o, d, tmax, layer, s);
                float tau = 0;
                for (int k = 0; k < 2; k++)
                {
                    float t0 = k == 0 ? s.x : s.z, t1 = k == 0 ? s.y : s.w;
                    float dt = (t1 - t0) / steps;
                    if (dt <= 0) continue;
                    [loop] for (int i = 0; i < 256; i++)
                    {
                        if (i >= (int)steps) break;
                        float3 p = o + d * (t0 + dt * (i + 0.5));
                        tau += layerDensity(p, layer) * dt;
                    }
                }
                return tau;
            }

            float tauSun(float3 p)
            {
                return tauLayer(p, _Sun.xyz, 1e30, _L0, _LightSteps) + tauLayer(p, _Sun.xyz, 1e30, _L1, _LightSteps);
            }

            float envRadiance(float3 d, float3 up)
            {
                float s = dot(d, up);
                return s >= 0 ? lerp(_Env.y, _Env.x, saturate(s)) : _Env.z;
            }

            float groundHit(float3 o, float3 d)
            {
                if (_Flat > 0.5) return d.z < -1e-6 ? -o.z / d.z : 1e30;
                float t0, t1;
                if (!hitSphere(o, d, float3(0, 0, 0), _PlanetR, t0, t1)) return 1e30;
                return t0 > 0 ? t0 : (t1 > 0 ? t1 : 1e30);
            }

            float4 frag(v2f i) : SV_Target
            {
                float2 ndc = i.uv * 2 - 1;
                float3 d = normalize(_Fwd.xyz + ndc.x * _HalfV * _Aspect * _Right.xyz + ndc.y * _HalfV * _UpC.xyz);
                float3 o = _CamPos.xyz;
                float tg = groundHit(o, d);
                if (_Occ.w > 0)
                {
                    float t0, t1;
                    if (hitSphere(o, d, _Occ.xyz, _Occ.w, t0, t1) && t0 > 0 && t0 < tg) tg = t0;
                }
                float tau = 0, cloud = 0, active = 0;
                // The lit march follows layer 0; the optical depth of both layers is summed.
                float4 s;
                slots(o, d, tg, _L0, s);
                float transBefore = 1;
                for (int k = 0; k < 2; k++)
                {
                    float t0 = k == 0 ? s.x : s.z, t1 = k == 0 ? s.y : s.w;
                    float dt = (t1 - t0) / _ViewSteps;
                    if (dt <= 0) continue;
                    [loop] for (int j = 0; j < 256; j++)
                    {
                        if (j >= (int)_ViewSteps) break;
                        float3 p = o + d * (t0 + dt * (j + 0.5));
                        float dens = layerDensity(p, _L0);
                        float dtau = dens * dt;
                        if (dens > 0)
                        {
                            active += 1;
                            float w = transBefore * (1.0 - exp(-dtau));
                            float mu = dot(_Sun.xyz, d);
                            float jr = _Phase.w * SUN_E * exp(-tauSun(p)) * phase(mu);
                            cloud += w * jr;
                        }
                        transBefore *= exp(-dtau);
                        tau += dtau;
                    }
                }
                float4 s1;
                slots(o, d, tg, _L1, s1);
                for (int k2 = 0; k2 < 2; k2++)
                {
                    float t0 = k2 == 0 ? s1.x : s1.z, t1 = k2 == 0 ? s1.y : s1.w;
                    float dt = (t1 - t0) / _ViewSteps;
                    if (dt <= 0) continue;
                    [loop] for (int j2 = 0; j2 < 256; j2++)
                    {
                        if (j2 >= (int)_ViewSteps) break;
                        float3 p = o + d * (t0 + dt * (j2 + 0.5));
                        tau += layerDensity(p, _L1) * dt;
                    }
                }
                float trans = exp(-tau);
                float3 up = upAt(o);
                float behind = envRadiance(d, up);
                if (tg < 1e29)
                {
                    float3 g = o + d * tg;
                    if (_Occ.w > 0 && length(g - _Occ.xyz) < _Occ.w * 1.0005)
                    {
                        behind = 0.05;
                    }
                    else
                    {
                        float3 gu = upAt(g);
                        float cs = max(dot(gu, _Sun.xyz), 0);
                        float ts = cs > 0 ? exp(-tauSun(g + gu * 0.01)) : 0;
                        behind = _Env.w * (SUN_E * cs * ts / PI + _GroundAlbedoAmbient);
                    }
                }
                return float4(tau, cloud, cloud + trans * behind, active);
            }
            ENDCG
        }
    }
}
