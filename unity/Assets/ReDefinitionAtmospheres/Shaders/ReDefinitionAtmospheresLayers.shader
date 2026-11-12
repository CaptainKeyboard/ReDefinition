// The own cloud system's layer core (package T01): a planet-wide shell of up to four stacked cloud layers marched as one ray.
//
// Geometry: the planet's centre is _PlanetCenter (world space); a layer is the shell between two altitudes above sea level (the radius
// _PlanetR plus the altitude), seen from below, inside or above it. Each layer gives up to two spans along the ray (the near stretch and,
// where the ray enters the hollow inner sphere and the body has no surface, the far wall: the gas-giant case); the spans of all layers
// are sorted front to back and marched in turn with one transmittance, so two layers stack as 1 - (1 - a1)(1 - a2). The march ends at the
// scene's depth (the composite is depth limited) and at the horizon sphere of a body with a surface.
//
// Density at a point of a layer: the layer's extinction times the coverage shape (a threshold on the coverage map, soft over
// _LayerB[i].z) times the type table's profile, where the type value of the type map picks two neighbouring types of the layer's table
// and interpolates their base, top and profile. The maps are equirectangular, row 0 at the north pole, column 0 at longitude -180,
// read in the map frame (_WorldToMap: x towards longitude 0, y towards 90 east, z north) turned by the layer's longitude shift.
// Basic value noise moves the coverage threshold and erodes the edge; T02 replaces it. The lighting is a single-scattering stand-in
// (a short march towards the sun with dual-lobe Henyey-Greenstein, a sky gradient); T03 replaces it.
//
// Techniques (no code taken): the sphere-shell ray intervals and the type-by-height table follow the common shell cloud renderers (Schneider,
// "The real-time volumetric cloudscapes of Horizon Zero Dawn", SIGGRAPH 2015; Hillaire, "Physically Based Sky, Atmosphere and Cloud
// Rendering in Frostbite", 2016); the energy-conserving integration of a step is Hillaire's (section 5.6.3).
//
// Passes: 0 march into a premultiplied (radiance, alpha) target, 1 depth-aware composite over the camera target, 2 aux output
// (alpha, tau, steps, first hit distance) for the parity checks and the aid pictures.
Shader "ReDefinition/AtmospheresLayers"
{
    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        CGINCLUDE
        #include "UnityCG.cginc"

        static const float PI = 3.14159265;
        static const int MAXL = 4;
        static const int MAXT = 8;
        static const int PROFILE_SAMPLES = 128;

        float _PlanetR;
        float4 _PlanetCenter;
        float4x4 _WorldToMap;
        float4 _CamPos, _CamForward, _CamRight, _CamUp;
        float _LayerCount;
        float4 _LayerA[4];   // inner altitude, outer altitude, tight inner altitude, tight outer altitude
        float4 _LayerB[4];   // sigma per m, coverage, edge softness, kind (0 weather, 1 cheap, 2 fog, 3 emissive)
        float4 _LayerC[4];   // fade near, fade far, map slice (< 0: no map), type count
        float4 _LayerD[4];   // noise amount, noise scale (1 / m), erosion, map shift (degrees)
        float4 _LayerE[4];   // emission colour (kind 3)
        sampler2D _TypeProfile;
        sampler2D _TypeInfo;
        UNITY_DECLARE_TEX2DARRAY(_CovMaps);
        UNITY_DECLARE_TEX2DARRAY(_TypeMaps);
        float4 _MapDim;      // map width, map height
        float4 _Sun;         // towards the sun, w: the longest sun march in m
        float4 _SunColor, _AmbientTop, _AmbientBottom;
        float4 _MarchP;      // max steps per span, sun steps, step growth with distance, early-out transmittance
        float4 _StepP;       // shortest step in m, jitter 0..1, frame number, has surface (1) or gas giant (0)
        float4 _LimitP;      // ray length limit in m (<= 0: none), aid mode (0 natural, 1 optical depth, 2 layer colours), layer mask, use scene depth
        float4 _PhaseP;      // g forward, g back, forward weight, albedo
        sampler2D_float _SceneDepth;
        sampler2D _MarchTex;
        float4 _MarchTex_TexelSize;

        struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

        v2f vert(appdata_img v)
        {
            v2f o;
            o.pos = UnityObjectToClipPos(v.vertex);
            o.uv = v.texcoord;
            return o;
        }

        // The ray through a pixel of the target (uv 0..1), unit length.
        float3 RayDir(float2 uv)
        {
            float2 ndc = uv * 2.0 - 1.0;
            return normalize(_CamForward.xyz + (ndc.x + _CamRight.w) * _CamRight.xyz + (ndc.y + _CamUp.w) * _CamUp.xyz);
        }

        bool HitSphere(float3 o, float3 d, float r, out float t0, out float t1)
        {
            float b = dot(o, d);
            float len = length(o);
            float c = (len - r) * (len + r);
            float disc = b * b - c;
            t0 = 0; t1 = 0;
            if (disc <= 0) return false;
            float s = sqrt(disc);
            t0 = -b - s; t1 = -b + s;
            return true;
        }

        // The spans of one layer along the ray [0, tmax]: x0, x1 of the near stretch, y0, y1 of the far wall; empty where the end is not after the start.
        void LayerSpans(int li, float3 o, float3 d, float tmax, out float4 sp)
        {
            sp = 0;
            float inner = _LayerA[li].z, outer = _LayerA[li].w;
            float o0, o1;
            if (!HitSphere(o, d, _PlanetR + outer, o0, o1)) return;
            float lo = max(o0, 0), hi = min(o1, tmax);
            if (hi <= lo) return;
            float i0, i1;
            if (inner <= 0 || !HitSphere(o, d, _PlanetR + inner, i0, i1))
            {
                sp.xy = float2(lo, hi);
                return;
            }
            float aEnd = min(i0, hi);
            if (aEnd > lo) sp.xy = float2(lo, aEnd);
            float bStart = max(i1, lo);
            if (hi > bStart)
            {
                if (sp.y > sp.x) sp.zw = float2(bStart, hi);
                else sp.xy = float2(bStart, hi);
            }
        }

        // ---- value noise (basic; the noise package replaces it) ----
        float Hash3(float3 p)
        {
            return frac(sin(dot(p, float3(12.9898, 78.233, 37.719))) * 43758.5453);
        }

        float Noise3(float3 x)
        {
            float3 i = floor(x), f = frac(x);
            f = f * f * (3.0 - 2.0 * f);
            float n000 = Hash3(i), n100 = Hash3(i + float3(1, 0, 0)), n010 = Hash3(i + float3(0, 1, 0)), n110 = Hash3(i + float3(1, 1, 0));
            float n001 = Hash3(i + float3(0, 0, 1)), n101 = Hash3(i + float3(1, 0, 1)), n011 = Hash3(i + float3(0, 1, 1)), n111 = Hash3(i + float3(1, 1, 1));
            return lerp(lerp(lerp(n000, n100, f.x), lerp(n010, n110, f.x), f.y), lerp(lerp(n001, n101, f.x), lerp(n011, n111, f.x), f.y), f.z);
        }

        float Fbm3(float3 p)
        {
            return 0.5 * Noise3(p) + 0.3 * Noise3(p * 2.03 + 11.7) + 0.2 * Noise3(p * 4.11 + 3.1);
        }

        // ---- maps ----
        float2 MapUv(float3 pc, float shiftDeg)
        {
            float3 q = mul((float3x3)_WorldToMap, pc);
            float3 n = normalize(q);
            float lat = asin(clamp(n.z, -1.0, 1.0));
            float lon = atan2(n.y, n.x);
            float u = (lon * 57.29577951 - shiftDeg + 180.0) / 360.0;
            float v = (90.0 - lat * 57.29577951) / 180.0;
            return float2(u, v);
        }

        // ---- the type table ----
        float TypeProfile(int li, int type, float h)
        {
            float u = (saturate(h) * (PROFILE_SAMPLES - 1) + 0.5) / PROFILE_SAMPLES;
            float v = (li * MAXT + type + 0.5) / (MAXL * MAXT);
            return tex2Dlod(_TypeProfile, float4(u, v, 0, 0)).r;
        }

        float3 TypeInfo(int li, int type)
        {
            return tex2Dlod(_TypeInfo, float4((type + 0.5) / MAXT, (li + 0.5) / MAXL, 0, 0)).rgb;
        }

        // The density of layer li at a point (pc relative to the planet's centre, alt its altitude), distance t along the ray from the camera.
        // share: the height share of the layer; erosionOut: the interpolated type's erosion.
        float LayerDensity(int li, float3 pc, float alt, float t, out float share)
        {
            share = 0;
            float inner = _LayerA[li].x, outer = _LayerA[li].y;
            share = (alt - inner) / (outer - inner);
            if (share < 0 || share > 1) return 0;
            float fade = 1.0;
            float fadeNear = _LayerC[li].x, fadeFar = _LayerC[li].y;
            if (fadeFar > fadeNear)
            {
                float x = saturate((fadeFar - t) / (fadeFar - fadeNear));
                fade = x * x * (3.0 - 2.0 * x);
                if (fade <= 0) return 0;
            }
            float slice = _LayerC[li].z;
            float cov = 1.0, typeValue = 0.0;
            float2 uv = 0;
            if (slice >= 0)
            {
                uv = MapUv(pc, _LayerD[li].w);
                float vMin = 0.5 / _MapDim.y;
                uv.y = clamp(uv.y, vMin, 1.0 - vMin);
                cov = UNITY_SAMPLE_TEX2DARRAY_LOD(_CovMaps, float3(uv, slice), 0).r;
                typeValue = UNITY_SAMPLE_TEX2DARRAY_LOD(_TypeMaps, float3(uv, slice), 0).r;
            }
            float noiseAmount = _LayerD[li].x;
            float erosion = 0;
            float typeCount = _LayerC[li].w;
            // The type value spans the table; two neighbouring types are interpolated (heights and profile).
            float x2 = saturate(typeValue) * (typeCount - 1);
            int ta = (int)floor(x2);
            ta = min(ta, (int)typeCount - 1);
            int tb = min(ta + 1, (int)typeCount - 1);
            float f = x2 - ta;
            float3 ia = TypeInfo(li, ta), ib = TypeInfo(li, tb);
            float b = lerp(ia.x, ib.x, f), tp = lerp(ia.y, ib.y, f);
            if (share < b || share > tp || tp <= b) return 0;
            erosion = lerp(ia.z, ib.z, f) * _LayerD[li].z;
            float coverage = _LayerB[li].y;
            if (noiseAmount > 0)
            {
                float n = Fbm3(pc * _LayerD[li].y);
                cov += (n - 0.5) * noiseAmount;
            }
            float edge = max(_LayerB[li].z, 1e-4);
            float s = saturate((cov + coverage - 1.0) / edge + 0.5);
            if (s <= 0) return 0;
            float h = (share - b) / (tp - b);
            float prof = lerp(TypeProfile(li, ta, h), TypeProfile(li, tb, h), f);
            float dens = _LayerB[li].x * s * prof * fade;
            if (erosion > 0 && noiseAmount > 0)
            {
                float n2 = Fbm3(pc * _LayerD[li].y * 5.3 + 7.0);
                dens *= saturate(1.0 - erosion * (1.0 - s) * (1.5 - n2));
            }
            return dens;
        }

        // ---- light (single-scattering stand-in) ----
        float Hg(float mu, float g)
        {
            return (1.0 - g * g) / (4.0 * PI * pow(max(1.0 + g * g - 2.0 * g * mu, 1e-6), 1.5));
        }

        float Phase(float mu)
        {
            return _PhaseP.z * Hg(mu, _PhaseP.x) + (1.0 - _PhaseP.z) * Hg(mu, _PhaseP.y);
        }

        // The optical depth from a point towards the sun through all layers: the layer's own span with geometric steps up to _Sun.w, the others coarser.
        float SunTau(float3 pc, int own, float t)
        {
            float tau = 0;
            int steps = (int)_MarchP.y;
            [loop] for (int lj = 0; lj < MAXL; lj++)
            {
                if (lj >= (int)_LayerCount) break;
                float4 sp;
                LayerSpans(lj, pc, _Sun.xyz, 1e30, sp);
                int n = lj == own ? steps : max(1, steps / 2);
                [loop] for (int k = 0; k < 2; k++)
                {
                    float a = k == 0 ? sp.x : sp.z, b = k == 0 ? sp.y : sp.w;
                    if (b <= a) continue;
                    b = min(b, a + _Sun.w);
                    float norm = pow(2.0, (float)n) - 1.0;
                    float prev = a;
                    [loop] for (int i = 0; i < 8; i++)
                    {
                        if (i >= n) break;
                        float edge = a + (b - a) * (pow(2.0, (float)(i + 1)) - 1.0) / norm;
                        float mid = 0.5 * (prev + edge);
                        float3 q = pc + _Sun.xyz * mid;
                        float qa = length(q) - _PlanetR;
                        float qs;
                        tau += LayerDensity(lj, q, qa, t, qs) * (edge - prev);
                        prev = edge;
                    }
                }
            }
            return tau;
        }

        struct MarchOut
        {
            float3 rgb;
            float alpha;
            float tau;
            float steps;
            float first;
            float4 tauLayer;
        };

        float Ign(float2 p, float frame)
        {
            p += 5.588238 * frame;
            return frac(52.9829189 * frac(dot(p, float2(0.06711056, 0.00583715))));
        }

        MarchOut March(float3 o, float3 d, float tmax, float2 pixel)
        {
            MarchOut r;
            r.rgb = 0; r.alpha = 0; r.tau = 0; r.steps = 0; r.first = -1;
            r.tauLayer = 0;
            if (_StepP.w > 0.5)
            {
                float g0, g1;
                if (HitSphere(o, d, _PlanetR, g0, g1))
                {
                    float tg = g0 > 0 ? g0 : (g1 > 0 ? g1 : 1e30);
                    tmax = min(tmax, tg);
                }
            }
            float s0[8]; float s1[8]; int sl[8];
            int n = 0;
            [unroll] for (int li = 0; li < MAXL; li++)
            {
                if (li >= (int)_LayerCount) break;
                if (_LimitP.z > 0 && fmod(floor(_LimitP.z / pow(2.0, (float)li)), 2.0) < 0.5) continue;
                float4 sp;
                LayerSpans(li, o, d, tmax, sp);
                if (sp.y > sp.x) { s0[n] = sp.x; s1[n] = sp.y; sl[n] = li; n++; }
                if (sp.w > sp.z) { s0[n] = sp.z; s1[n] = sp.w; sl[n] = li; n++; }
            }
            // Front to back (a stable insertion sort).
            for (int a = 1; a < n; a++)
            {
                float k0 = s0[a], k1 = s1[a]; int kl = sl[a];
                int j = a - 1;
                [loop] while (j >= 0 && s0[j] > k0)
                {
                    s0[j + 1] = s0[j]; s1[j + 1] = s1[j]; sl[j + 1] = sl[j];
                    j--;
                }
                s0[j + 1] = k0; s1[j + 1] = k1; sl[j + 1] = kl;
            }
            float T = 1.0;
            float mu = dot(_Sun.xyz, d);
            float ph = Phase(mu);
            float jitter = lerp(0.5, Ign(pixel, _StepP.z), _StepP.y);
            int maxSteps = (int)_MarchP.x;
            [loop] for (int k = 0; k < 8; k++)
            {
                if (k >= n) break;
                if (T < _MarchP.w) break;
                int li = sl[k];
                float len = s1[k] - s0[k];
                float stepLen = max(_StepP.x * (1.0 + s0[k] * _MarchP.z), 1e-3);
                int count = (int)clamp(ceil(len / stepLen), 1.0, (float)maxSteps);
                float dt = len / count;
                float kind = _LayerB[li].w;
                [loop] for (int i = 0; i < 256; i++)
                {
                    if (i >= count) break;
                    float t = s0[k] + dt * (i + jitter);
                    float3 pc = o + d * t;
                    float alt = length(pc) - _PlanetR;
                    float share;
                    float dens = LayerDensity(li, pc, alt, t, share);
                    r.steps += 1;
                    if (dens <= 0) continue;
                    if (r.first < 0) r.first = t;
                    float dtau = dens * dt;
                    float3 src;
                    if (kind > 2.5)
                    {
                        src = _LayerE[li].rgb;
                    }
                    else
                    {
                        float3 amb = lerp(_AmbientBottom.rgb, _AmbientTop.rgb, saturate(share));
                        float3 sun = 0;
                        if (kind < 0.5) sun = _SunColor.rgb * exp(-SunTau(pc, li, t)) * ph;
                        else if (kind < 1.5) sun = _SunColor.rgb * exp(-dens * dt * 4.0) * ph;
                        else sun = _SunColor.rgb * 0.25 * ph;
                        src = _PhaseP.w * (sun + amb);
                    }
                    float ext = 1.0 - exp(-dtau);
                    r.rgb += T * src * ext;
                    T *= 1.0 - ext;
                    r.tau += dtau;
                    r.tauLayer += dtau * float4(li == 0, li == 1, li == 2, li == 3);
                    if (T < _MarchP.w) break;
                }
            }
            r.alpha = 1.0 - exp(-r.tau);
            return r;
        }

        // The longest ray of a pixel: the scene's depth at the pixel (as a distance along the ray) and the measurement limit. A limit below
        // zero is a ramp: the ray's length grows with the pixel's x from 0 to -limit (one render samples a profile along a ray).
        float SceneDistance(float2 uv, float3 d)
        {
            float lim = _LimitP.x < 0 ? -_LimitP.x * uv.x : (_LimitP.x > 0 ? _LimitP.x : 1e30);
            if (_LimitP.w < 0.5) return lim;
            float depth = tex2Dlod(_SceneDepth, float4(uv, 0, 0)).r;
            if (depth <= 0 || depth > 1e9) return lim;
            return min(lim, depth / max(dot(d, normalize(_CamForward.xyz)), 1e-3));
        }

        float3 Heat(float x)
        {
            float t = saturate(x);
            return t < 0.5 ? lerp(float3(0.05, 0.05, 0.30), float3(0.20, 0.80, 0.50), t * 2.0) : lerp(float3(0.20, 0.80, 0.50), float3(0.95, 0.20, 0.10), (t - 0.5) * 2.0);
        }

        float4 Pass0(v2f i)
        {
            float3 d = RayDir(i.uv);
            float3 o = _CamPos.xyz - _PlanetCenter.xyz;
            float tmax = SceneDistance(i.uv, d);
            float2 pixel = i.uv * _ScreenParams.xy;
            MarchOut r = March(o, d, tmax, pixel);
            if (_LimitP.y > 1.5)
            {
                float3 c = r.tauLayer.x * float3(0.9, 0.3, 0.2) + r.tauLayer.y * float3(0.2, 0.7, 0.9) + r.tauLayer.z * float3(0.9, 0.8, 0.2) + r.tauLayer.w * float3(0.4, 0.9, 0.3);
                float s = max(r.tau, 1e-6);
                return float4(c / s * r.alpha, r.alpha);
            }
            if (_LimitP.y > 0.5) return float4(Heat((log10(max(r.tau, 1e-4)) + 3.0) / 4.5) * step(1e-4, r.tau), r.alpha);
            return float4(r.rgb, r.alpha);
        }

        float4 PassAux(v2f i)
        {
            float3 d = RayDir(i.uv);
            float3 o = _CamPos.xyz - _PlanetCenter.xyz;
            float tmax = SceneDistance(i.uv, d);
            MarchOut r = March(o, d, tmax, i.uv * _ScreenParams.xy);
            return float4(r.alpha, r.tau, r.steps, r.first);
        }

        // The depth-aware composite: the four march texels around the pixel, weighted by their distance and by how well the scene depth at
        // the texel's centre matches the depth at the pixel (a cloud march that ended at a near surface does not bleed onto a far one).
        float4 PassComposite(v2f i)
        {
            float2 texel = _MarchTex_TexelSize.xy;
            float2 p = i.uv / texel - 0.5;
            float2 base = floor(p);
            float2 f = p - base;
            float3 d0 = RayDir(i.uv);
            float dist0 = _LimitP.w > 0.5 ? tex2Dlod(_SceneDepth, float4(i.uv, 0, 0)).r : 0;
            float4 sum = 0;
            float wsum = 0;
            [unroll] for (int k = 0; k < 4; k++)
            {
                float2 o = float2(k & 1, k >> 1);
                float2 uvc = (base + o + 0.5) * texel;
                float w = (o.x > 0.5 ? f.x : 1.0 - f.x) * (o.y > 0.5 ? f.y : 1.0 - f.y);
                if (_LimitP.w > 0.5)
                {
                    float dc = tex2Dlod(_SceneDepth, float4(uvc, 0, 0)).r;
                    float rel = abs(dc - dist0) / max(dist0, 1.0);
                    w *= 1.0 / (1e-3 + rel * 40.0);
                }
                sum += tex2Dlod(_MarchTex, float4(uvc, 0, 0)) * w;
                wsum += w;
            }
            return sum / max(wsum, 1e-6);
        }
        ENDCG

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            float4 frag(v2f i) : SV_Target { return Pass0(i); }
            ENDCG
        }

        Pass
        {
            Blend One OneMinusSrcAlpha
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            float4 frag(v2f i) : SV_Target { return PassComposite(i); }
            ENDCG
        }

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            float4 frag(v2f i) : SV_Target { return PassAux(i); }
            ENDCG
        }
    }
}
