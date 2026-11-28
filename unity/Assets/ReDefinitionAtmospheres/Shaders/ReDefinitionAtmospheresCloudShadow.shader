// The cloud shadow of the own cloud system (package T04): the share of the sun's direct light that the layers let through, per pixel, into the
// sun's shadow mask (deferred) or as a colour multiply (forward), at half resolution, plus the map view's disc.
//
// Per pixel: the world position comes from the (downscaled) depth; the sun ray from there goes up through every layer's shell (the spans of
// LayerGeometry.ShellSpans); the optical depth is the midpoint sum of the density along it (the CPU mirror CloudShadowField), averaged over a few
// directions across the sun's disc (the penumbra h x 9.31 mrad / sin^2(e)); the factor is exp(-tau) (Beer-Lambert). Everything the sun lights takes
// the factor because the mask is the sun's own.
//
// The coverage share of a sample is the map's threshold, or, where a layer's noise amount (_CsNoiseD.x) is above 0, the shape noise of the layer pass:
// ReDefinitionAtmospheresNoise.cginc is included and its NoiseShareNear called with the erosion 0, which is how the pass's own light march reads the
// shape (gNoiseLod). One definition, the same volumes, offsets and drift (LayerNoise.Bind on this material); the CPU twin is CloudShadowShapeNoise.
// The switch shadow.shapeNoise puts the amounts at 0, which is the threshold alone.
//
// Credit: the passes, their order and the depth-aware upsample are adapted from the shaders of EnvironmentalVisualEnhancements (EVE Redux,
// MIT): Assets/Shaders/DepthDownscaling/DownscaleDepth.shader, FindNearestDepth.cginc and BlendScreenSpaceShadows.shader (the downscale of the
// depth to half size keeping the farthest value, the nearest-depth choice of the low-resolution texel, the multiply into the sun mask in pass
// 2 and into the colour in pass 3, sky pixels 1, and the world position from the depth along the pixel's ray). Technique: Hillaire, "Physically Based Sky, Atmosphere and Cloud Rendering in
// Frostbite", 2016, section 5.9 (the cloud shadow as the transmittance along the sun, read by every surface).
//
// Passes: 0 downscale the depth (farthest of the block), 1 the shadow at half size (R8), 2 multiply into the sun's shadow mask, 3 multiply into
// the colour (forward), 4 the planet's disc in scaled space (multiply into the colour).
Shader "ReDefinition/AtmospheresCloudShadow"
{
    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        CGINCLUDE
        #include "UnityCG.cginc"
        #include "ReDefinitionAtmospheresSceneDepth.cginc"
        #include "MapSlew.cginc"

        static const int MAXL = 4;
        static const int MAXT = 8;
        static const int MAXSTEPS = 64;   // CloudShadowStepPolicy.MaxScaledSteps
        static const int PROFILE_SAMPLES = 128;

        float4 _CsPlanet;        // planet centre (world or scaled space), sea-level radius in the same unit
        float4x4 _CsWorldToMap;  // rotation from that space into the map frame (x towards longitude 0, y towards 90 east, z north)
        float4 _CsSun;           // towards the sun (unit), w: metres per unit of the space (1 in the scene, 6000 in scaled space)
        float4 _CsParams;        // steps per span (the per-layer count is in _CsLayerD.x), sun disc taps, floor (share of the direct light that always remains), layer count
        float _CsDirectOnly;     // 1: the direct beam alone, no diffuse lift and no floor (Scatterer's ocean texture, a global its buffer sets); 0 otherwise
        float4 _CsForced;        // x: a forced shadow factor for measurements (below 0: none; shadow.testFactor, read only in the CLOUD_DEV variant)
        float4 _CsParams2;       // x: weight of the diffuse floor in the mask (0: the pure direct beam), y: share of the lit colour that is the sun's direct light (forward path), z: 1 where the shadow reads the pass's shape (the types' LIFT with the shape noise), 0 for the map alone,
                                 // w: 1 where the march leaps over the empty reach (OPT2: the layer pass's empty-reach maps are bound and the body has a surface), 0 every midpoint is read
        float4 _CsLayerA[4];     // inner altitude, outer altitude, tight inner altitude, tight outer altitude (m)
        float4 _CsLayerB[4];     // sigma per m, coverage, edge softness, shadow strength
        float4 _CsLayerC[4];     // clear-sky K of the map value (0: off), water exponent minus 1, map slice (< 0: no map), type count
        float4 _CsLayerD[4];     // x steps per span of the layer, y, z unused, w map shift (degrees)
        sampler2D _CsTypeProfile;
        sampler2D _CsTypeInfo;
        UNITY_DECLARE_TEX2DARRAY(_CsCovMaps);
        UNITY_DECLARE_TEX2DARRAY(_CsTypeMaps);
        float4 _CsReachCov, _CsReachType;   // the maps' slew reach per layer (MapSlew.cginc)
        float4 _CsMapDim;
        // OPT2: the layer pass's empty-reach maps (EmptyReachMaps: per layer slice, the map's size, a texel the number of texels to the nearest texel that can hold cloud), read by Load (point, no sampler).
        Texture2DArray<float> _CsCovReach;
        float _CsDebug;

        Texture2D _CameraDepthTexture;
        float4 _CameraDepthTexture_TexelSize;
        // What pass 5 downscales: the ocean's depth for Scatterer's ocean shadow texture (CloudShadowRenderer.RecordOceanShadows).
        Texture2D _CsDepthSource;
        float4 _CsDepthSource_TexelSize;
        SamplerState cs_point_clamp_sampler;
        sampler2D _CsDepthHalf;                // the downscaled depth
        float4 _CsDepthHalf_TexelSize;
        sampler2D _CsShadowHalf;               // the half-size shadow

        struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

        v2f vert(appdata_img v)
        {
            v2f o;
            o.pos = UnityObjectToClipPos(v.vertex);
            o.uv = v.texcoord;
            return o;
        }

        // ---- geometry (LayerGeometry.HitSphere, ShellSpans) ----
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

        void LayerSpans(int li, float3 o, float3 d, float tmax, float radius, out float4 sp)
        {
            sp = 0;
            float inner = _CsLayerA[li].z, outer = _CsLayerA[li].w;
            float o0, o1;
            if (!HitSphere(o, d, radius + outer, o0, o1)) return;
            float lo = max(o0, 0), hi = min(o1, tmax);
            if (hi <= lo) return;
            float i0, i1;
            if (inner <= 0 || !HitSphere(o, d, radius + inner, i0, i1))
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

        // ---- the density (LayerDensity.Extinction with the shape noise, without the erosion and without the distance fade) ----
        float2 MapUv(float3 pc, float shiftDeg)
        {
            float3 q = mul((float3x3)_CsWorldToMap, pc);
            float3 n = normalize(q);
            float lat = asin(clamp(n.z, -1.0, 1.0));
            float lon = atan2(n.y, n.x);
            return float2((lon * 57.29577951 - shiftDeg + 180.0) / 360.0, (90.0 - lat * 57.29577951) / 180.0);
        }

        // ---- the shape noise of the layer pass (the noise file reads these names of the layer shader) ----
        float4 _CsNoiseD[4];   // per layer: noise amount (0: the threshold alone), noise scale (1 / m), erosion (0 here: the shape alone), map shift (degrees)
        float _CsPlanetR;      // the sea-level radius in metres
        #define _LayerA _CsLayerA
        #define _LayerD _CsNoiseD
        #define _WorldToMap _CsWorldToMap
        #define _PlanetR _CsPlanetR
        #include "ReDefinitionAtmospheresNoise.cginc"
        #include "CloudCells.cginc"

        // The layer pass's four profile channels (master plan 43): r the type's density profile at the height share h, g its noise scale factor, b its extra noise amount, a its LIFT
        // (the height-shaped threshold of the type, in units of the type's headroom).
        float4 TypeProfile(int li, int type, float h)
        {
            float u = (saturate(h) * (PROFILE_SAMPLES - 1) + 0.5) / PROFILE_SAMPLES;
            float v = (li * MAXT + type + 0.5) / (MAXL * MAXT);
            return tex2Dlod(_CsTypeProfile, float4(u, v, 0, 0));
        }
        float4 _CsFormP[4];   // per layer: x the largest extra noise amount of its types (the empty-space bounds; the layer pass's _LayerE.w)

        // x base, y top, z the exponent of the type's density rim (0: the layer's; CLOUDR1 P1), w the type's cumuliform preset (CLOUDR1 P2).
        float4 TypeInfo(int li, int type)
        {
            return tex2Dlod(_CsTypeInfo, float4((type + 0.5) / MAXT, (li + 0.5) / (2.0 * MAXL), 0, 0));
        }

        // x the type's shapeSmoothing in noise scales (the shape volume's mip, the pass's gNoiseSm).
        float TypeSmoothing(int li, int type)
        {
            return tex2Dlod(_CsTypeInfo, float4((type + 0.5) / MAXT, (li + MAXL + 0.5) / (2.0 * MAXL), 0, 0)).x;
        }

        // The type's form slot (the second row's w, CloudShadowRenderer; 0: none; TypeForm reads the slot's numbers): the CLOUD FIELD's clouds (CloudField.cs) as the layer pass draws them.
        // Plus a half where the type has an outline curve (LayerTypeTable.FormInfoOfType).
        float TypeFormInfo(int li, int type)
        {
            return tex2Dlod(_CsTypeInfo, float4((type + 0.5) / MAXT, (li + MAXL + 0.5) / (2.0 * MAXL), 0, 0)).w;
        }

        // OPT2: the length along any straight line (m) within which the map is empty around a point whose map texel this is: the layer pass's EmptyReach (the same arithmetic as
        // EmptyReachMath.ReachMetres; the chord at the planet's radius holds because every sample of the shadow lies in a layer's shell, above the surface). 0 where the leap is off.
        float EmptyReachM(int li, float2 uv)
        {
            if (_CsParams2.w < 0.5) return 0;
            int x = (int)floor(frac(uv.x) * _CsMapDim.x);
            int y = clamp((int)floor(uv.y * _CsMapDim.y), 0, (int)_CsMapDim.y - 1);
            float e = _CsCovReach.Load(int4(x, y, (int)_CsLayerC[li].z, 0)) * 255.0;
            float texelLat = 3.14159265 / _CsMapDim.y;
            float latMax = min(abs(0.5 - uv.y) * 3.14159265 + e * texelLat, 1.5707);
            return _CsPlanetR * sin(min(e * min(texelLat, 2.0 * 3.14159265 / _CsMapDim.x * cos(latMax)), 1.5707));
        }

        // reachM: where the coverage bound found the map empty, the empty reach there (the midpoints within it add nothing either); 0 otherwise.
        float LayerExtinction(int li, float3 pc, float alt, out float reachM)
        {
            reachM = 0;
            gCellX = -1.0;
            float inner = _CsLayerA[li].x, outer = _CsLayerA[li].y;
            float share = (alt - inner) / (outer - inner);
            if (share < 0 || share > 1) return 0;
            float slice = _CsLayerC[li].z;
            float cov = 1.0, typeValue = 0.0;
            float edge = max(_CsLayerB[li].z, 1e-4);
            // OPT2: the most the shape noise can lift the map (n <= 1 in NoiseShareNear: the volume's value, the blend's normal CDF), the bound of the layer pass (CoverageNoiseBound).
            float noiseBound = 0.5 * abs(_CsNoiseD[li].x) * (1.0 + _CsFormP[li].x);
            float2 uvField = 0.0;
            if (slice >= 0)
            {
                float2 uv = MapUv(pc, _CsLayerD[li].w);
                float2 uvRaw = uv;
                uvField = uvRaw;
                float vMin = 0.5 / _CsMapDim.y;
                uv.y = clamp(uv.y, vMin, 1.0 - vMin);
                cov = SlewedMap(UNITY_SAMPLE_TEX2DARRAY_LOD(_CsCovMaps, float3(uv, slice), 0).rg, _CsReachCov, slice);
                cov = min(cov, 2.0 * cov - _CsLayerC[li].x);   // E40 soft density: the clear-sky rule of the layer pass (K = 0 is off)
                // OPT2: the coverage bound of the layer pass (its COST note in LayerDensityEx): where even the noise's highest value cannot lift the map above the threshold, the share is 0
                // whatever the type, so the type map, the type tables and the noise are not read. The lift below only lowers the map, so the result is the same.
                if (cov + _CsLayerB[li].y - 1.0 + noiseBound + 0.5 * edge <= 0.0)
                {
                    reachM = EmptyReachM(li, uv);
                    return 0;
                }
                // The clouds of a type with a form are placed below, once the type is known (FieldCloud).
                typeValue = SlewedMap(UNITY_SAMPLE_TEX2DARRAY_LOD(_CsTypeMaps, float3(uv, slice), 0).rg, _CsReachType, slice);
            }
            float typeCount = _CsLayerC[li].w;
            float x2 = saturate(typeValue) * (typeCount - 1);
            int ta = min((int)floor(x2), (int)typeCount - 1);
            int tb = min(ta + 1, (int)typeCount - 1);
            float f = x2 - ta;
            float4 ia = TypeInfo(li, ta), ib = TypeInfo(li, tb);
            gCumuliformPreset = (int)(f < 0.5 ? ia.w : ib.w);
            gNoiseSm = lerp(TypeSmoothing(li, ta), TypeSmoothing(li, tb), f);
            float b = lerp(ia.x, ib.x, f), tp = lerp(ia.y, ib.y, f);
            if (share < b || share > tp || tp <= b) return 0;
            // The macro shape as the pass reads it: the type's LIFT lowers the map at this height by its share of the layer's headroom (the pass's cov -= lift * (coverage + noise bound + 0.5 edge);
            // _CsParams2.z is 1 where the shadow reads the pass's shape, 0 for the map alone).
            float h = (share - b) / (tp - b);
            float4 pf = lerp(TypeProfile(li, ta, h), TypeProfile(li, tb, h), f);
            // The type's own bound at this height (the layer pass's TypeNoiseBound) and its noise amount (master plan 43; channel g, the erosion gain, has no erosion to act on here).
            float typeBound = 0.5 * abs(_CsNoiseD[li].x) * (1.0 + pf.b);
            float typeAmount = _CsNoiseD[li].x * (1.0 + pf.b);
            // CLOUD FIELD: the layer pass's clouds (FieldCloud, CloudCells.cginc), so the shadow on the ground is cast by the clouds the sky shows. The shadow's points are far from
            // the march's spacing (no foot).
            float4 form = TypeForm(TypeFormInfo(li, ta), TypeFormInfo(li, tb), f);
            [branch] if (form.x > 0.0 && slice >= 0)
            {
                if (FieldCloud(li, form, uvField, cov, _CsLayerB[li].y, edge, _CsNoiseD[li].x, 0.0, _CsPlanetR + 0.5 * (inner + outer), (tp - b) * (outer - inner)) == 0) return 0;
                // The point's height in the body by the type's outline, as the layer pass takes it (master plan 47.U stage 3).
                float hv = h / gFieldTop;
                [branch] if (gFormOutline > 0.5) hv = TypeProfile(li, f < 0.5 ? ta : tb, min(hv, 1.0)).a + max(hv - 1.0, 0.0);
                if (FieldBody(li, hv, (tp - b) * (outer - inner)) == 0) return 0;
            }
            cov -= _CsParams2.z * pf.a * (_CsLayerB[li].y + typeBound + 0.5 * edge);
            // THE BODY of a cloud the field placed, where the field's read says a body draws, as the layer pass draws it (CellBody on the bulges' rank: the threshold at the noise's mean, a unit edge, the
            // full swing); it replaces the type's lift, so the lift's bound is not asked.
            bool body = gCellX >= 0.0;
            // OPT2: the same bound after the lift (the layer pass's second one): no noise read where the lifted map cannot reach the threshold.
            if (!body && cov + _CsLayerB[li].y - 1.0 + typeBound + 0.5 * edge <= 0.0) return 0;
            // The coverage share: the map's threshold, or the layer pass's shape noise moving it (NoiseShareNear is the threshold itself where the amount is 0 or the volumes are not up).
            float s;
            [branch] if (body)
            {
                float rank = NoiseShareNear(li, pc, 1.0 - _CsLayerB[li].y, _CsLayerB[li].y, 1.0, _CsNoiseD[li].x > 0.0 ? 1.0 : 0.0, 0.0);
                // The finer bulges are not read (PRUEFPROTOKOLL 408: the shadow pass cost the dense sky's view from above 2 fps, and two of a body sample's reads were these):
                // the shadow reads the body as the layer pass's light march reads it (its taps take no finer bulges either), the heads on the heads are below the shadow's size.
                s = CellBody(li, rank, float2(0.0, 0.0), (tp - b) * (outer - inner));
            }
            else s = NoiseShareNear(li, pc, cov, _CsLayerB[li].y, edge, typeAmount, 0.0);
            if (s <= 0) return 0;
            float pw = 1.0 + _CsLayerC[li].y;   // E40 soft density: the layer's water exponent (stored minus 1: 0 = none)
            pw = lerp(ia.z > 0 ? ia.z : pw, ib.z > 0 ? ib.z : pw, f);   // CLOUDR1 P1: the type's own exponent
            if (pw != 1.0) s = pow(s, pw);
            return _CsLayerB[li].x * s * pf.x;
        }

        // The optical depth along the ray o + t d (o relative to the planet's centre, metres) to tmax, all layers, each times its shadow strength.
        float PathTau(float3 o, float3 d, float tmax, float radius)
        {
            float tau = 0;
            int layers = (int)_CsParams.w;
            [loop] for (int li = 0; li < MAXL; li++)
            {
                if (li >= layers) break;
                float strength = _CsLayerB[li].w;
                if (strength <= 0) continue;
                // The steps of a span of this layer (CloudShadowStepPolicy.Count on the CPU: the steps per span scaled by the widening of the layer's
                // interval, up to MAXSTEPS), set per layer by the renderer.
                int steps = max(1, (int)(_CsLayerD[li].x + 0.5));
                float4 sp;
                LayerSpans(li, o, d, tmax, radius, sp);
                float layerTau = 0;
                [loop] for (int k = 0; k < 2; k++)
                {
                    float a = k == 0 ? sp.x : sp.z, b = k == 0 ? sp.y : sp.w;
                    if (b <= a) continue;
                    float dt = (b - a) / steps;
                    [loop] for (int i = 0; i < MAXSTEPS; i++)
                    {
                        if (i >= steps) break;
                        float3 q = o + d * (a + dt * (i + 0.5));
                        float reachM;
                        layerTau += LayerExtinction(li, q, length(q) - radius, reachM) * dt;
                        // OPT2: the midpoints within the empty reach of an empty one add nothing: skipped, as the layer pass leaps over its grid points (the sum is the same).
                        // A reach that is not a number (or one beyond the span) must not throw the counter: NaN cast to int is undefined and could set it far below 0.
                        if (reachM > 0.0) i += (int)min(floor(reachM / dt), (float)MAXSTEPS);
                    }
                }
                tau += layerTau * strength;
            }
            return tau;
        }

        // The sun's total irradiance under a slab of vertical optical depth tauV as a share of the open ground's, beam and scattered light
        // together: 1 / (1 + A tauV^P) (CloudShadowMath.FittedTransmission: the two-stream form of a droplet's asymmetry 0.85 without absorption, which the slab-flux
        // photon walk of the parity reference follows to 2 % from tau 0.1 to 100).
        float DiffuseShare(float tauV)
        {
            return tauV <= 0 ? 1.0 : 1.0 / (1.0 + 0.11 * tauV);
        }

        // The sun's light that reaches a point (pc relative to the planet's centre, in metres): the mean of exp(-tau) over the taps across the
        // sun's disc (the direct beam), lifted to the light a cloud scatters down (DiffuseShare of the vertical optical depth, which a
        // plane-parallel cloud has as tau along the sun x cos of the zenith angle) by the weight _CsParams2.x, and the floor under it. The
        // taps are a fixed pattern (no per-frame or per-pixel noise: nothing for the upscaler to chase). Sky ambient is not in this factor:
        // the deferred mask scales the sun's term only, and the forward colour multiply keeps the ambient share of the colour (pass 3).
        float SunFactor(float3 pc, float radius)
        {
            float3 s = _CsSun.xyz;
            float t0, t1;
            float tmax = 1e30;
            // A sun below the horizon: the ray meets the planet first and the clouds beyond do not count; a point below sea level (a
            // valley, the sea floor) leaves the sphere upwards and counts the clouds above.
            if (HitSphere(pc, s, radius, t0, t1) && t0 > 0) tmax = t0;
            // One tap is the sun's centre. The march has ONE call site: fxc inlines every call, so a second one would put the whole density into the shader twice.
            int taps = clamp((int)_CsParams.y, 1, 8);
            float sum = 0;
            float tauSum = 0;
            float3 u = abs(s.z) < 0.9 ? normalize(float3(-s.y, s.x, 0)) : normalize(float3(0, -s.z, s.y));
            float3 v = cross(s, u);
            [loop] for (int i = 0; i < 8; i++)
            {
                if (i >= taps) break;
                float3 d = s;
                if (taps > 1)
                {
                    float r = 0.5 * 9.31e-3 * sqrt((i + 0.5) / taps);
                    float a = i * 2.399963229728653;
                    d = normalize(s + u * (r * cos(a)) + v * (r * sin(a)));
                }
                float tau = PathTau(pc, d, tmax, radius);
                tauSum += tau;
                sum += exp(-tau);
            }
            sum /= taps;
            tauSum /= taps;
            // The vertical optical depth from the sun path: a sun on or under the horizon has no plane-parallel reading, the share stays at the
            // direct beam there (the sun's light at the horizon through a cloud is the beam's business).
            float cosZ = dot(normalize(pc), s);
            float diffuse = cosZ > 0.05 ? DiffuseShare(tauSum * cosZ) : 0.0;
            // Scatterer's ocean texture takes the direct beam alone (_CsDirectOnly 1, set by its buffer; PRUEFPROTOKOLL 300): the sea's glint is the sun disc's
            // mirror image, which only the direct beam carries; the light the clouds scatter down lights a matte surface, not a specular one.
            // Measurement (shadow.testFactor, PRUEFPROTOKOLL 289): a forced factor everywhere shows which pixels the mask reaches; a constant in the product.
            float forced = DEV(_CsForced.x, -1.0);
            if (forced >= 0.0) return forced;
            if (_CsDirectOnly > 0.5) return saturate(sum);
            sum = max(sum, _CsParams2.x * diffuse);
            return lerp(_CsParams.z, 1.0, saturate(sum));
        }

        // ---- the world position from the depth (EVE's getPreciseWorldPosFromDepth: the pixel's ray scaled to the linear depth) ----
        bool IsSky(float rawDepth)
        {
#if defined(UNITY_REVERSED_Z)
            return rawDepth == 0.0;
#else
            return rawDepth == 1.0;
#endif
        }

        float3 WorldPosFromDepth(float2 uv, float rawDepth)
        {
            // The distance before the eye by the projection the camera renders with (ReDefinitionAtmospheresSceneDepth.cginc): Linear01Depth times the far plane
            // read a far ground 3 to 8 % too near, a point kilometres above the sea, which the low clouds do not shade.
            float eye = SceneEyeDepth(rawDepth);
#if defined(UNITY_REVERSED_Z)
            rawDepth = 1 - rawDepth;
#endif
            float4 clipPos = float4(uv, rawDepth, 1.0);
            clipPos.xyz = 2.0f * clipPos.xyz - 1.0f;
            float4 camPos = mul(unity_CameraInvProjection, clipPos);
            camPos.xyz /= camPos.w;
            float3 rayDirection = normalize(camPos.xyz);
            float aa = dot(rayDirection, float3(0.0, 0.0, -1.0));
            camPos.xyz = rayDirection * eye / aa;
            camPos.z = -camPos.z;
            float4 worldPos = mul(unity_CameraToWorld, float4(camPos.xyz, 1.0));
            return worldPos.xyz / worldPos.w;
        }

        // ---- the nearest low-resolution texel by depth (EVE's FindNearestDepthUV) ----
        float2 FindNearestDepthUV(float2 uv, float fullRawDepth)
        {
            float fullLinear = Linear01Depth(fullRawDepth);
            float smallest = 1.0;
            float2 nearestUv = uv;
            bool bilinear = true;
            [unroll(2)] for (float u = -0.5; u <= 0.5; u += 1.0)
            {
                [unroll(2)] for (float v = -0.5; v <= 0.5; v += 1.0)
                {
                    float2 cuv = uv + float2(u, v) * _CsDepthHalf_TexelSize.xy;
                    cuv = (floor(cuv * _CsDepthHalf_TexelSize.zw) + 0.5.xx) * _CsDepthHalf_TexelSize.xy;
                    float cd = Linear01Depth(tex2Dlod(_CsDepthHalf, float4(cuv, 0, 0)).r);
                    float diff = abs(cd - fullLinear);
                    if (diff < smallest) { smallest = diff; nearestUv = cuv; }
                    if (diff > 0.1 * fullLinear || diff > 0.1 * cd) bilinear = false;
                }
            }
            return bilinear ? uv : nearestUv;
        }
        ENDCG

        Pass // 0: the depth at half size, the farthest value of the block
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.0

            float4 frag(v2f i) : SV_Target
            {
                float2 inv = 1.0.xx / max(1.0.xx, _ScreenParams.xy);
                int2 a = (i.uv - 0.5 * inv) * _CameraDepthTexture_TexelSize.zw;
                int2 b = (i.uv + 0.5 * inv) * _CameraDepthTexture_TexelSize.zw;
                a = clamp(a, 0, _CameraDepthTexture_TexelSize.zw);
                b = clamp(b, 0, _CameraDepthTexture_TexelSize.zw);
#if defined(UNITY_REVERSED_Z)
                float result = 1.0;
#else
                float result = 0.0;
#endif
                for (int y = a.y; y < b.y; y++)
                    for (int x = a.x; x < b.x; x++)
                    {
                        float d = _CameraDepthTexture.Load(int3(x, y, 0)).r;
#if defined(UNITY_REVERSED_Z)
                        result = min(result, d);
#else
                        result = max(result, d);
#endif
                    }
                return result;
            }
            ENDCG
        }

        Pass // 1: the shadow at half size (R8)
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.0
            #pragma multi_compile_local _ CLOUD_DEV

            float4 frag(v2f i) : SV_Target
            {
                float raw = tex2Dlod(_CsDepthHalf, float4(i.uv, 0, 0)).r;
                if (IsSky(raw)) return 1.0;
                float3 world = WorldPosFromDepth(i.uv, raw);
                // A point half a metre above the surface, so that a ray from the ground does not start inside it (a float at 6e5 m steps by 6 cm).
                float3 pc = (world - _CsPlanet.xyz) * _CsSun.w;
                float3 up = normalize(pc);
                return SunFactor(pc + up * 0.5, _CsPlanet.w * _CsSun.w);
            }
            ENDCG
        }

        Pass // 2: multiply into the sun's shadow mask (deferred)
        {
            Blend DstColor Zero
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #pragma target 4.0

            float4 frag(v2f i) : SV_Target
            {
                float raw = _CameraDepthTexture.SampleLevel(cs_point_clamp_sampler, i.uv, 0).r;
                if (IsSky(raw)) return 1.0;
                float2 nearestUv = FindNearestDepthUV(i.uv, raw);
                return tex2Dlod(_CsShadowHalf, float4(nearestUv, 0, 0)).r;
            }
            ENDCG
        }

        Pass // 3: multiply into the colour (forward rendering; the camera's buffer after the opaque geometry)
        {
            Blend DstColor Zero
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.0

            float4 frag(v2f i) : SV_Target
            {
                float raw = _CameraDepthTexture.SampleLevel(cs_point_clamp_sampler, i.uv, 0).r;
                // Diagnostic outputs of the probe (the scene sets _CsDebug; 0 is the product): 1 a constant, 2 the camera's depth, 3 the
                // downscaled depth, 4 the height of the reconstructed point, 5 the half-size shadow without the depth-aware choice.
                if (_CsDebug == 1) return 0.5;
                if (_CsDebug == 2) return saturate(Linear01Depth(raw) * 20.0);
                if (_CsDebug == 3) return saturate(Linear01Depth(tex2Dlod(_CsDepthHalf, float4(i.uv, 0, 0)).r) * 20.0);
                if (_CsDebug == 4) return saturate(WorldPosFromDepth(i.uv, raw).y / 3000.0 + 0.5);
                if (_CsDebug == 5) return tex2Dlod(_CsShadowHalf, float4(i.uv, 0, 0)).r;
                if (IsSky(raw)) return 1.0;
                float2 nearestUv = FindNearestDepthUV(i.uv, raw);
                float s = tex2Dlod(_CsShadowHalf, float4(nearestUv, 0, 0)).r;
                // Forward shading has no separate sun term: the factor scales the sun's share of the colour only, the ambient share stays
                // (without it the ground under a dense cloud turns black although the sky still lights it).
                s = 1.0 - _CsParams2.y * (1.0 - s);
                return s.xxxx;
            }
            ENDCG
        }

        Pass // 4: the planet's disc in scaled space (map view, orbit): the same factor at the sea-level surface point of every pixel
        {
            Blend DstColor Zero
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.0
            #pragma multi_compile_local _ CLOUD_DEV

            float4 frag(v2f i) : SV_Target
            {
                float2 ndc = i.uv * 2.0 - 1.0;
                float4 v = mul(unity_CameraInvProjection, float4(ndc, 1.0, 1.0));
                v.xyz /= v.w;
                float3 d = normalize(mul((float3x3)unity_CameraToWorld, float3(v.x, v.y, -v.z)));
                float3 o = _WorldSpaceCameraPos - _CsPlanet.xyz;
                float t0, t1;
                if (!HitSphere(o, d, _CsPlanet.w, t0, t1) || t1 <= 0) return 1.0;
                if (t0 <= 0) return 1.0;
                // Another body in front of the planet hides the point.
                float raw = _CameraDepthTexture.SampleLevel(cs_point_clamp_sampler, i.uv, 0).r;
                if (!IsSky(raw) && length(WorldPosFromDepth(i.uv, raw) - _WorldSpaceCameraPos) < t0 * 0.99) return 1.0;
                float3 pc = (o + d * t0) * _CsSun.w;
                return SunFactor(pc, _CsPlanet.w * _CsSun.w).xxxx;
            }
            ENDCG
        }

        Pass // 5: the same as pass 0 for the ocean's depth texture (_CsDepthSource, set by the ocean buffer): Scatterer's ocean shadow texture
             // (CloudShadowRenderer.RecordOceanShadows, master plan 46.1). Last, so that the pass numbers of the renderer are the file order.
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.0

            float4 frag(v2f i) : SV_Target
            {
                float2 inv = 1.0.xx / max(1.0.xx, _ScreenParams.xy);
                int2 a = (i.uv - 0.5 * inv) * _CsDepthSource_TexelSize.zw;
                int2 b = (i.uv + 0.5 * inv) * _CsDepthSource_TexelSize.zw;
                a = clamp(a, 0, _CsDepthSource_TexelSize.zw);
                b = clamp(b, 0, _CsDepthSource_TexelSize.zw);
#if defined(UNITY_REVERSED_Z)
                float result = 1.0;
#else
                float result = 0.0;
#endif
                for (int y = a.y; y < b.y; y++)
                    for (int x = a.x; x < b.x; x++)
                    {
                        float d = _CsDepthSource.Load(int3(x, y, 0)).r;
#if defined(UNITY_REVERSED_Z)
                        result = min(result, d);
#else
                        result = max(result, d);
#endif
                    }
                return result;
            }
            ENDCG
        }
    }
    Fallback Off
}
