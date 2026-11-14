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
// The shape noise moves the coverage threshold and the detail erodes the edge (ReDefinitionAtmospheresNoise.cginc, package T02). The light is
// ReDefinitionAtmospheresCloudLight.cginc (package T03): dual-lobe phase, octave multiple scattering, powder, light march with cone taps,
// ambient, extra lights, the light cache.
//
// Techniques (no code taken): the sphere-shell ray intervals and the type-by-height table follow the common shell cloud renderers (Schneider,
// "The real-time volumetric cloudscapes of Horizon Zero Dawn", SIGGRAPH 2015; Hillaire, "Physically Based Sky, Atmosphere and Cloud
// Rendering in Frostbite", 2016); the energy-conserving integration of a step is Hillaire's (section 5.6.3).
//
// Cost (package T01c): the leap over empty samples with the step back to the grid point after the last empty one follows Schneider's
// cheap-sample march (SIGGRAPH 2015, same reference); the coverage bound lets the noise run only where the map can reach the threshold.
//
// Passes: 0 march into a premultiplied (radiance, alpha) target, 1 depth-aware composite over the camera target, 2 aux output
// (alpha, tau, steps, first hit distance) for the parity checks and the aid pictures, 3 light cache fill (T03).
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
        float4 _OutP;        // display stage of the final composite (pass 4): exposure, 1 / gamma, white (the sun's display value), on (1)
        float4 _OutW;        // the white point's colour (E40 B3): the per-channel share of the white of a sunlit cloud (1, 1, 1: the white is grey); w unused
        // MARCH cost controls (package T01c; the march section below reads them, the density and light sections only the bound).
        float4 _SkipP;       // longest leap in steps over empty samples (1: none), light cut-off (weight of a step below which the last sun transmittance is reused; 0: always march), step share of cheap layers, step share of fog layers
        float4 _CostP;       // coverage bound on (1) or off (0), cost-attribution mask (bit 0 no light, bit 1 no noise, bit 2 no erosion), lit steps per sun march (>= 1), step growth with the ray's lost transmittance (T05, 0: none)
        // The step offset's sequence in time (E40 grain against TVC): x the frame's phase, frac(frame number x golden ratio), computed in double precision by the
        // caller. Each pixel keeps one offset and every frame adds the phase to it. y 1: the light cone's rotation is the same
        // for every pixel (a probe), 0: the product's per-pixel rotation. z the per-pixel offset (layers.ditherSequence): 1 an integer hash (white), 2 the interleaved
        // gradient noise. w 0.
        float4 _DitherP;
        // The temporal reconstruction (package T05): the march target holds one pixel of every N x N block of the full target.
        float4 _ReconP;      // block size N (1: the target is the full target), the block offset x and y in full pixels, 0
        float4 _ReconD;      // the full target's width and height in pixels, the march target's width and height in pixels
        sampler2D_float _SceneDepth;
        sampler2D _MarchTex;
        float4 _MarchTex_TexelSize;

        // The light's uniforms and the parity test cell (package T03).
        #define CLOUDLIGHT_DECLARE
        #include "ReDefinitionAtmospheresCloudLight.cginc"
        #undef CLOUDLIGHT_DECLARE

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
            // A tight outer altitude below zero: the layer holds no cloud the camera can see this frame (LayerSpanBound, layers.spanTight).
            if (outer < 0) return;
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

        // ---- shape and erosion (package T02) ----
        #include "ReDefinitionAtmospheresNoise.cginc"
        // ---- far view (package T06: aerial perspective, the shape's mean over the noise at wide spacing) ----
        #include "ReDefinitionAtmospheresFarView.cginc"
        // ---- height variation (package T13: base and top vary over the sky) ----
        #include "ReDefinitionAtmospheresBaseHeight.cginc"
        // ---- culling and distance LOD (package T16: the tile map the march reads, the fades and the LOD functions) ----
        #include "CloudCull.cginc"

        // The largest amount the coverage noise moves the map value by: the shape value is 0 to 1, so the threshold moves by half the amount.
        float CoverageNoiseBound(int li)
        {
            return 0.5 * _LayerD[li].x;
        }

        // ---- the type table ----
        float TypeProfile(int li, int type, float h)
        {
            float u = (saturate(h) * (PROFILE_SAMPLES - 1) + 0.5) / PROFILE_SAMPLES;
            float v = (li * MAXT + type + 0.5) / (MAXL * MAXT);
            return tex2Dlod(_TypeProfile, float4(u, v, 0, 0)).r;
        }

        // The whole texel of the profile: r the density, g the direct light factor, b the sky-fill factor of the type at the height h (E40 B3).
        float4 TypeProfile4(int li, int type, float h)
        {
            float u = (saturate(h) * (PROFILE_SAMPLES - 1) + 0.5) / PROFILE_SAMPLES;
            float v = (li * MAXT + type + 0.5) / (MAXL * MAXT);
            return tex2Dlod(_TypeProfile, float4(u, v, 0, 0));
        }

        float3 TypeInfo(int li, int type)
        {
            return tex2Dlod(_TypeInfo, float4((type + 0.5) / MAXT, (li + 0.5) / MAXL, 0, 0)).rgb;
        }

        // The density of layer li at a point (pc relative to the planet's centre, alt its altitude), distance t along the ray from the camera.
        // share: the height share of the layer; erosionOut: the interpolated type's erosion.
        // The altitude of the top of the cloud type at the last point LayerDensity was asked for (the layer's outer altitude where no type was reached, and for the
        // parity cell). The march copies it right after its own density read; the ambient's analytic occlusion measures the cloud above a sample to this top.
        static float gTopAlt = 0;
        // The light factors of the type at the last point LayerDensity was asked for (x direct light, y sky fill; 1: neutral); copied by the march like gTopAlt.
        static float2 gTypeLight = float2(1.0, 1.0);

        float LayerDensity(int li, float3 pc, float alt, float t, out float share)
        {
            share = 0;
            float inner = _LayerA[li].x, outer = _LayerA[li].y;
            gTopAlt = outer;
            share = (alt - inner) / (outer - inner);
            // The height variation (T13) lets a base reach below the inner altitude and a top above the outer one, by the layer's reach.
            bool heightOn = _LayerH2[li].y > 0;
            float reachDown = heightOn ? HeightReachDown(li) / (outer - inner) : 0.0;
            float reachUp = heightOn ? HeightReachUp(li) / (outer - inner) : 0.0;
            if (share < -reachDown || share > 1 + reachUp) return 0;
            if (_CellA.w > 0) return li == 0 ? CellDensity(pc) : 0;   // the parity test cell (T03)
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
            float2 uv = 0, uvRaw = 0;
            if (slice >= 0 || heightOn)
            {
                uv = MapUv(pc, _LayerD[li].w);
                uvRaw = uv;
            }
            if (slice >= 0)
            {
                float vMin = 0.5 / _MapDim.y;
                uv.y = clamp(uv.y, vMin, 1.0 - vMin);
                cov = UNITY_SAMPLE_TEX2DARRAY_LOD(_CovMaps, float3(uv, slice), 0).r;
                cov = min(cov, 2.0 * cov - _CullL[li].y);   // E40 soft density: the clear-sky rule of the water content (CloudLayerDef.ClearSkyMap); K = 0 is off
                // COST (E40 grain against TVC): the coverage bound of below, asked before the type table and the height variation are read. layers.profileShape only
                // lowers the map value, so a map that fails here fails there too: the result is the same, the reads of an empty sample are fewer.
                if (_CostP.x > 0.5 && cov + _LayerB[li].y - 1.0 + CoverageNoiseBound(li) + 0.5 * max(_LayerB[li].z, 1e-4) <= 0.0) return 0;
                typeValue = UNITY_SAMPLE_TEX2DARRAY_LOD(_TypeMaps, float3(uv, slice), 0).r;
            }
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
            float erosionType = lerp(ia.z, ib.z, f);
            if (heightOn)
            {
                float localBase, localTop;
                if (!HeightLocalBaseTop(li, pc, uvRaw, b, tp, erosionType, outer - inner, share, localBase, localTop)) return 0;
                b = localBase;
                tp = localTop;
            }
            if (share < b || share > tp || tp <= b) return 0;
            gTopAlt = inner + tp * (outer - inner);
            erosion = erosionType * _LayerD[li].z;
            float coverage = _LayerB[li].y;
            float edge = max(_LayerB[li].z, 1e-4);
            // E40 L2 D1: the type's height-shaped threshold. The profile texture's channel a (cfg node LIFT of the type, 0 where there is none) lifts the coverage threshold at this height
            // by up to 1 in map units, times layers.profileShape (_NoiseT.w, 1: as the cfg says, 0: the column shape), so a cloud is narrower where the lift is high: a cumulus dome over a
            // flat base, a cumulonimbus tower under a wide anvil. The lift is never negative, so the coverage bounds of above and below stay exact. The technique is the height
            // gradient of Schneider's cloudscapes (SIGGRAPH 2015: the coverage is reduced by the height signal of the cloud type); the code is the project's own.
            float h = (share - b) / (tp - b);
            float4 pf = lerp(TypeProfile4(li, ta, h), TypeProfile4(li, tb, h), f);
            cov -= _NoiseT.w * pf.a;
            // COST (T01c): the coverage shape is zero where even the noise's highest value cannot lift the map above the threshold; the
            // noise is evaluated only where this bound passes. The bound is the same expression as the shape below, so the result is exact.
            if (_CostP.x > 0.5 && cov + coverage - 1.0 + CoverageNoiseBound(li) + 0.5 * edge <= 0.0) return 0;
            // The cost debug bits switch the shape noise (2) and the erosion (4) off.
            float noiseAmount = fmod(floor(_CostP.y * 0.5), 2.0) < 0.5 ? _LayerD[li].x : 0.0;
            if (fmod(floor(_CostP.y * 0.25), 2.0) >= 0.5) erosion = 0;
            // The coverage share: the map moved by the shape noise, the soft edge, the erosion (read only in the edge band) and the edge hardness (T02).
            float s = NoiseShare(li, pc, cov, coverage, edge, noiseAmount, erosion);
            if (s <= 0) return 0;
            float pw = 1.0 + _CullL[li].z;   // the layer's water exponent (stored minus 1: 0 = none)
            if (pw > 0.05 && pw != 1.0) s = pow(s, pw);
            gTypeLight = pf.yz;
            return _LayerB[li].x * s * pf.x * fade;
        }

        // ---- light (package T03: the phase, the light march, the octaves, the ambient, the lights, the light cache) ----
        #define CLOUDLIGHT_FUNCS
        #include "ReDefinitionAtmospheresCloudLight.cginc"
        #undef CLOUDLIGHT_FUNCS


        struct MarchOut
        {
            float3 rgb;
            float alpha;
            float tau;
            float steps;
            float first;
            float4 tauLayer;
            float3 direct;
            float3 ambient;
            float lightTaps;
            float distSum;    // sum over the steps of (weight in the ray's colour) x distance, for the mean distance of the cloud (T06)
            float weightSum;
        };

        float Ign(float2 p, float frame)
        {
            p += 5.588238 * frame;
            return frac(52.9829189 * frac(dot(p, float2(0.06711056, 0.00583715))));
        }

        // The per-pixel white step offset of layers.ditherSequence 1: an integer hash of the pixel.
        float DitherHash(float2 p)
        {
            uint2 u = uint2(asuint((int)floor(p.x)), asuint((int)floor(p.y)));
            uint h = u.x * 1664525u + u.y * 1013904223u;
            h ^= h >> 16; h *= 0x7FEB352Du; h ^= h >> 15; h *= 0x846CA68Bu; h ^= h >> 16;
            return (h >> 8) * (1.0 / 16777216.0);
        }

        MarchOut March(float3 o, float3 d, float tmax, float2 pixel)
        {
            MarchOut r;
            r.rgb = 0; r.alpha = 0; r.tau = 0; r.steps = 0; r.first = -1;
            r.tauLayer = 0;
            r.direct = 0; r.ambient = 0; r.lightTaps = 0;
            r.distSum = 0; r.weightSum = 0;
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
                if (!CullLayerOn(li)) continue;
                float4 sp;
                LayerSpans(li, o, d, tmax, sp);
                CellClip(o, d, sp);
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
            // The step offset: the pixel keeps one offset (the interleaved gradient noise, a blue-noise pattern in space) and every frame adds the golden ratio to it
            // (Roberts 2018, the R1 sequence): any run of frames spreads its offsets evenly over the grid step, so the history of the pass integrates the step by
            // strata instead of by chance (the cloud's entry, its light at the sample's depth and the hit or miss of a thin rim all depend on that offset).
            float jitterValue = frac((_DitherP.z < 1.5 ? DitherHash(pixel) : Ign(pixel, 0.0)) + _DitherP.x);
            float jitter = lerp(0.5, jitterValue, _StepP.y);
            // The cone's rotation of the light taps (_DitherP.y 1: a fixed rotation, a probe): the same pattern at another place, advanced in time by the same
            // phase (T03v2).
            float rot = frac(Ign(pixel + 17.0, 0.0) + _DitherP.x) * 6.2831853 * (_DitherP.y > 0.5 ? 0.0 : 1.0);
            int maxSteps = (int)_MarchP.x;
            float leapMax = max(_SkipP.x, 1.0);
            float lightCut = _SkipP.y;
            bool noLight = fmod(floor(_CostP.y), 2.0) > 0.5;
            [loop] for (int k = 0; k < 8; k++)
            {
                if (k >= n) break;
                if (T < _MarchP.w) break;
                int li = sl[k];
                // T16: the distance cut and the layer cap thin a span by a smooth factor and drop it where the factor is zero.
                float spanF = 1.0;
                if (_CullB.x > 0.5 || _CullB.z > 0.0)
                {
                    spanF = CullDistanceFade(o, d, s0[k]) * CullCapFade(k, s0[k]);
                    if (spanF <= 0.0) continue;
                }
                float len = s1[k] - s0[k];
                float kind = _LayerB[li].w;
                // Cheap and fog layers take a share of the step budget (they carry no detail the full count would resolve).
                float share01 = kind > 1.5 && kind < 2.5 ? _SkipP.w : (kind > 0.5 && kind < 1.5 ? _SkipP.z : 1.0);
                float stepLen = max(_StepP.x * (1.0 + s0[k] * _MarchP.z), 1e-3);
                // The far view (T06) keeps at least _FarD.w samples in a span: a thin layer seen from far away has a step longer than itself, and one
                // sample at a jittered place is a coin toss of the cloud's coverage that changes with the step count (the coverage of an orbit view
                // jumped by 14 percent between two altitudes where the count went from two to one).
                float minCount = max(1.0, _FarD.w);
                int count = (int)clamp(ceil(len / stepLen), minCount, max(minCount, floor(maxSteps * share01 + 0.5)));
                float dt = len / count;
                gNoiseFoot = dt;   // the sample spacing: the erosion detail's distance LOD (T02)
                // The samples lie on the grid s0 + dt * (i + jitter). An empty sample lets the next ones leap over 1, 2 .. leapMax grid
                // points; a leap that lands on cloud goes back to the grid point after the last empty one, so a cloud is always entered
                // at the grid's own resolution and only features narrower than the leap can be passed over.
                float tEnd = s1[k];
                float t = s0[k] + dt * jitter;
                float tLast = t - dt;
                float leap = 1.0, arrived = 1.0;
                float sunTau = 0.0, upTau = 0.0, tauSince = 0.0;
                bool haveSun = false;
                float sinceLight = 0.0;
                [loop] for (int i = 0; i < 512; i++)
                {
                    if (t >= tEnd) break;
                    float3 pc = o + d * t;
                    float alt = length(pc) - _PlanetR;
                    float share;
                    // T16: the step grows along the span; the detail's footprint is the pixel's size at the distance.
                    float dtv = _CullC.x > 0.0 ? dt * CullStepScale(t - s0[k]) : dt;
                    if (_CullD.x > 0.5) gNoiseFoot = CullFoot(t, dtv);
                    float dens = LayerDensity(li, pc, alt, t, share) * spanF;
                    float topAlt = gTopAlt;   // before the light march asks LayerDensity for other points
                    float2 typeLight = gTypeLight;
                    r.steps += 1;
                    if (dens <= 0)
                    {
                        tLast = t;
                        arrived = leap;
                        t += dtv * leap;
                        leap = min(leap + 1.0, leapMax);
                        continue;
                    }
                    if (arrived > 1.5)
                    {
                        t = tLast + dtv;
                        leap = 1.0;
                        arrived = 1.0;
                        continue;
                    }
                    leap = 1.0;
                    tLast = t;
                    // COST (T05): inside a cloud the step grows with what the ray has already lost (_CostP.w): a ray that is nearly opaque
                    // needs no fine sampling of what lies behind. The sample stands for its whole step, so the optical depth uses it.
                    float dts = min(dtv * (1.0 + _CostP.w * (1.0 - T)), max(tEnd - t, dtv));
                    t += dts;
                    if (r.first < 0) r.first = t - dts;
                    float dtau = dens * dts;
                    float ext = 1.0 - exp(-dtau);
                    float3 src, dirPart = 0, ambPart = 0;
                    if (kind > 2.5)
                    {
                        src = _LayerE[li].rgb;
                    }
                    else if (kind < 0.5)
                    {
                        // The light march costs most: a step that weighs less than the cut-off, or that follows a march by fewer than _CostP.z
                        // lit steps, reuses the optical depth towards the sun of the last march of this span (package T01c).
                        float reuse = -1.0;
                        float tapK = CullTapFactor(t);
                        if (noLight) reuse = 0.0;
                        else if (haveSun && !(T * ext >= lightCut * tapK && sinceLight + 1.0 >= _CostP.z * tapK)) { reuse = sunTau; sinceLight += 1.0; }
                        float taps, tauUsed, upUsed;
                        // Up mode 3 moves the reused optical depth straight up by what the ray has crossed since: the column above a point that is lower
                        // by h holds the density integral over h more, which along the ray is the optical depth since times the ray's vertical share.
                        float upReuse = -1.0;
                        if (reuse >= 0.0)
                        {
                            upReuse = upTau;
                            if (_LightT.w > 2.5) upReuse = max(upTau - dot(d, normalize(pc)) * tauSince, 0.0);
                        }
                        // The cone's rotation advances by the golden angle from one lit sample to the next, so that the per-pixel pattern averages out along the ray (FX-T03).
                        src = LightSource(pc, d, dens, share, li, t - dts, rot + r.steps * 2.3999632, reuse, upReuse, topAlt, dirPart, ambPart, taps, tauUsed, upUsed);
                        if (reuse < 0.0) { sunTau = tauUsed; upTau = upUsed; haveSun = true; sinceLight = 0.0; tauSince = 0.0; }
                        r.lightTaps += taps;
                    }
                    else
                    {
                        // Cheap and fog layers: no march towards the light (a local estimate and a fixed share).
                        float3 sky = lerp(_AmbientBottom.rgb, _AmbientTop.rgb, saturate(share));
                        sky = lerp(Luma(sky).xxx, sky, _LightR.z) * _LightS.z * _LightL[li].z;
                        ambPart = max(sky * _PhaseP.w, (_LightQ.z * Luma(_SunColor.rgb) * LIGHT_INV4PI).xxx);
                        float lit = kind < 1.5 ? exp(-dens * dt * 4.0) : 0.25;
                        dirPart = _SunColor.rgb * SunAir(pc, _Sun.xyz) * lit * ph * _PhaseP.w;
                        src = dirPart + ambPart;
                    }
                    // The type's light response by height (E40 B3): the direct light and the sky's fill of the sample, scaled by the type's factors.
                    src += dirPart * (typeLight.x - 1.0) + ambPart * (typeLight.y - 1.0);
                    dirPart *= typeLight.x;
                    ambPart *= typeLight.y;
                    // The colour map's tint of the layer (P15; one when the layer has none).
                    float3 tint = NoiseTint(li, pc);
                    src *= tint;
                    dirPart *= tint;
                    ambPart *= tint;
                    r.direct += T * dirPart * ext;
                    r.ambient += T * ambPart * ext;
                    r.rgb += T * src * ext;
                    r.distSum += T * ext * (t - 0.5 * dts);
                    r.weightSum += T * ext;
                    T *= 1.0 - ext;
                    r.tau += dtau;
                    tauSince += dtau;
                    r.tauLayer += dtau * float4(li == 0, li == 1, li == 2, li == 3);
                    if (T < _MarchP.w) break;
                }
            }
            // A ray that ended on the early-out is opaque: the remaining transmittance (up to the threshold, 1 %) would show a bright background (the
            // sun's or the moon's disc, a lit planet) as a dot through a thick cloud (T03v2).
            r.alpha = T < _MarchP.w ? 1.0 : 1.0 - exp(-r.tau);
            // Aerial perspective (T06): the air between the camera and the cloud's mean distance thins the cloud's colour towards the horizon sky.
            if (_FarA.x > 0.5 && r.weightSum > 1e-9)
            {
                float3 airT = exp(-FarAirTau(o, d, r.distSum / r.weightSum));
                r.rgb = r.rgb * airT + r.alpha * _FarC.rgb * (1.0 - airT);
            }
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

        // The uv of the full-target pixel a march texel stands for (the texel's own uv when the target is not a block sample, T05).
        float2 FullUv(float2 uv)
        {
            if (_ReconP.x < 1.5) return uv;
            // The march target's own size: a command buffer's blit does not update _ScreenParams to the target it draws into.
            float2 low = floor(uv * _ReconD.zw);
            return (low * _ReconP.x + _ReconP.yz + 0.5) / _ReconD.xy;
        }

        float4 Pass0(v2f i)
        {
            i.uv = FullUv(i.uv);
            CullReadTile(i.uv);   // T16: the layers this pixel's tile marches
            // Aid 3: the phase function over mu = 2 u - 1 (r: octave 0, g: octave 1), the parity check's normalisation probe.
            if (_LimitP.y > 2.5 && _LimitP.y < 3.5)
            {
                float w = 1.0 / max(abs(ddx(i.uv.x)), 1e-6);
                float m = floor(i.uv.x * w + 0.01) / max(w - 1.0, 1.0) * 2.0 - 1.0;
                return float4(Phase(m), Phase(m, _LightP.w), 0, 1);
            }
            float3 d = RayDir(i.uv);
            float3 o = _CamPos.xyz - _PlanetCenter.xyz;
            float tmax = SceneDistance(i.uv, d);
            float2 pixel = i.uv * (_ReconP.x < 1.5 ? _ScreenParams.xy : _ReconD.xy);
            MarchOut r = March(o, d, tmax, pixel);
            if (_LimitP.y > 1.5 && _LimitP.y < 2.5)
            {
                float3 c = r.tauLayer.x * float3(0.9, 0.3, 0.2) + r.tauLayer.y * float3(0.2, 0.7, 0.9) + r.tauLayer.z * float3(0.9, 0.8, 0.2) + r.tauLayer.w * float3(0.4, 0.9, 0.3);
                float s = max(r.tau, 1e-6);
                return float4(c / s * r.alpha, r.alpha);
            }
            // Aid 4: the direct light (r) against the ambient (b) and the step count's share of the budget (g); aid 5: the step map.
            if (_LimitP.y > 3.5 && _LimitP.y < 4.5) return float4(Luma(r.direct), r.lightTaps, Luma(r.ambient), r.alpha);
            if (_LimitP.y > 4.5 && _LimitP.y < 5.5) return float4(Heat(r.steps / max(_MarchP.x * 2.0, 1.0)) * step(0.5, r.steps), 1.0);
            // Aid 6 (T16): the skipped-block map. Blue the maps, green the terrain, grey no shell, orange behind a closed deck, yellow some layers dropped,
            // dark red scale the marched steps where the whole tile marches.
            if (_LimitP.y > 5.5 && _LimitP.y < 6.5)
            {
                float full = exp2(_LayerCount) - 1.0;
                float3 col = Heat(r.steps / max(_MarchP.x * 2.0, 1.0)) * 0.35 * step(0.5, r.steps);
                bool a1 = fmod(gCullAid, 2.0) > 0.5, a2 = fmod(floor(gCullAid / 2.0), 2.0) > 0.5, a4 = fmod(floor(gCullAid / 4.0), 2.0) > 0.5, a8 = gCullAid > 7.5;
                if (gCullBits < 0.5) col = a1 ? float3(0.1, 0.3, 0.95) : (a2 ? float3(0.1, 0.8, 0.2) : (a4 ? float3(0.45, 0.45, 0.6) : float3(0.8, 0.1, 0.1)));
                else if (a8) col = float3(1.0, 0.55, 0.1);
                else if (gCullBits < full - 0.5) col = float3(0.95, 0.85, 0.15);
                return float4(col, 1.0);
            }
            // Aid 7 (T16): the LOD map at the ray's first dense sample: red the tap factor over 1, green the step scale over 1, blue the detail footprint share over 1.
            if (_LimitP.y > 6.5)
                return float4(saturate((CullTapFactor(r.first) - 1.0) * 0.5), saturate(_CullC.x * max(r.first, 0.0) * 0.5), saturate(CullFoot(r.first, 1.0) * 0.001), 1.0) * step(0.5, r.steps);
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
        // The display stage (CloudOutputStage.cs is its CPU twin and the test's reference): the march's radiance is physical, the target it is
        // written into holds display values of a Gamma-space scene whose white is the sun's own display value W. Per channel, on the colour
        // without the coverage (rgb / alpha): D = W * (c * exposure / W) ^ (1 / gamma), then back to premultiplied. Natural frames only.
        float4 OutputStage(float4 c)
        {
            if (_OutP.w < 0.5 || _LimitP.y > 0.5) return c;
            float w = max(_OutP.z, 1e-4);
            float a = max(c.a, 1e-4);
            float3 u = max(c.rgb / a * _OutP.x / (w * max(_OutW.rgb, 1e-3)), 0.0);
            c.rgb = w * pow(u, _OutP.y) * c.a;
            return c;
        }
        #include "MarchStep.cginc"
        // The tile pass of the culling (T16; pass 5).
        #define CLOUDCULL_TILE
        #include "CloudCull.cginc"
        #undef CLOUDCULL_TILE
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

        // Pass 3 (T03, light cache prototype): fills the atlas with the optical depth towards the sun (r) and straight up (g) at the voxel centres.
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            float4 frag(v2f i) : SV_Target
            {
                int tilesX = (int)_CacheB.w;
                int rows = (CACHE_NZ + tilesX - 1) / tilesX;
                float2 pixel = floor(i.uv * float2(tilesX * CACHE_N, rows * CACHE_N));
                float3 p = CacheVoxelCentre(pixel);
                // The march reads a voxel only through the trilinear fetch at a sample inside a layer's shell (LayerSpans: _LayerA.z to .w above the surface), and such a
                // sample lies within one voxel diagonal of every voxel centre the fetch reads. A voxel farther than that from every shell is never read: K-T2 start 46 filled
                // all 131072 voxels of the 32 km box every frame, most of them in clear air or under the ground, and the cache saved nothing (1.03 of plain).
                float alt = length(p) - _PlanetR;
                float reach = length(_CacheB.xyz / float3(CACHE_N, CACHE_N, CACHE_NZ));
                bool read = false;
                [loop] for (int lj = 0; lj < MAXL; lj++)
                {
                    if (lj >= (int)_LayerCount) break;
                    float inner = _LayerA[lj].z, outer = _LayerA[lj].w;
                    // A shell with no inner radius reaches down to the surface (the ray ends on the sea-level sphere of a body with a surface, _StepP.w) or without bound.
                    float lowest = inner > 0 ? inner - reach : (_StepP.w > 0.5 ? -reach : -1e30);
                    if (alt <= outer + reach && alt >= lowest) read = true;
                }
                if (!read) return float4(0, 0, 0, 0);
                int steps = max(1, (int)_LightT.z);
                float tauSun = LightTau(p, _Sun.xyz, -1, steps, 0.0, 0.0, 0.0);
                float tauUp = LightTau(p, normalize(p), -1, max(1, (int)_LightS.y), 0.0, 0.0, 0.0);
                return float4(tauSun, tauUp, 0, 0);
            }
            ENDCG
        }

        // Pass 4: the final composite over the camera target, with the display stage (_OutP; identical to pass 1 while it is off).
        Pass
        {
            Blend One OneMinusSrcAlpha
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            float4 frag(v2f i) : SV_Target { return OutputStage(PassComposite(i)); }
            ENDCG
        }

        // Pass 5 (T16): the tile map of the culling, one texel per tile of the full target (the layers each tile marches).
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            float4 frag(v2f i) : SV_Target { return CullTilePass(i); }
            ENDCG
        }
    }
}
