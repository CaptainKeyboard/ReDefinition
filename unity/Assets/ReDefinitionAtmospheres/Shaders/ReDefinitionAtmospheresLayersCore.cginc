// The layer pass's shared code (master plan 45.3): the product shader ReDefinitionAtmospheresLayers.shader and the tools' lattice shader
// ReDefinitionAtmospheresLayersLattice.shader include it in their CGINCLUDE; the lattice shader defines CLOUD_LATTICE before it, so the product's
// shader holds no lattice variant. The file's description of the march stands at the head of ReDefinitionAtmospheresLayers.shader.
        #include "UnityCG.cginc"
        #include "CloudDev.cginc"
        #include "MapSlew.cginc"

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
        float4 _LayerE[4];   // rgb: emission colour (kind 3); w: the largest extra noise amount of the layer's types (master plan 43, FORM AMOUNT; 0: none)
        sampler2D _TypeProfile;
        sampler2D _TypeInfo;   // MAXL rows of the types' base, top, erosion and smoothing, then MAXL rows (CLOUDR1) of x the type's cumuliform preset (0: none), y the exponent of its density rim (0: the layer's), z its cavity strength (0: off)
        UNITY_DECLARE_TEX2DARRAY(_CovMaps);
        UNITY_DECLARE_TEX2DARRAY(_TypeMaps);
        // The two maps are RG16 arrays: r the value the map showed when its newest goal arrived, g the goal. The shown value is r moved toward g by at most the layer's reach
        // (x, y, z, w: layers 0 to 3; a share of full scale, the store's rate times the seconds since the goal arrived; LayerMapStore.GpuSlew.cs, MapSlewField.cs is the CPU twin).
        // The old path uploads the shown map in both channels with a reach of zero.
        float4 _MapReachCov, _MapReachType;
        UNITY_DECLARE_TEX2DARRAY(_CovReach);  // E40 C1: per layer and map texel the texels to the nearest texel that can hold cloud, less a margin (EmptyReachMaps.cs; zero: no reach)
        float4 _MapDim;      // map width, map height
        float4 _Sun;         // towards the sun, w: the longest sun march in m
        float4 _SunColor, _AmbientTop, _AmbientBottom;
        float4 _MarchP;      // max steps per span, sun steps, unused, early-out transmittance
        float4 _StepP;       // shortest step in m, jitter 0..1, frame number, has surface (1) or gas giant (0)
        float4 _LimitP;      // ray length limit in m (<= 0: none), aid mode (0 natural, 1 optical depth, 2 layer colours), layer mask, use scene depth
        float4 _PhaseP;      // g forward, g back, forward weight, albedo
        float4 _OutP;        // display stage of the final composite (pass 4): exposure, 1 / gamma, white (the sun's display value), on (1)
        float4 _OutW;        // the white point's colour (E40 B3): the per-channel share of the white of a sunlit cloud (1, 1, 1: the white is grey); w unused
        // MARCH cost controls (package T01c; the march section below reads them, the density and light sections only the bound).
        float4 _SkipP;       // longest leap in steps over empty samples (1: none), light cut-off (weight of a step below which the last sun transmittance is reused; 0: always march), step share of cheap layers, step share of fog layers
        float4 _SpacingP;    // x, y unused; z the longest leap over empty grid points in the layer's noise scales (layers.leapScale); w the distance in m beyond which the step grows (layers.spanGrowthFromM)
        float4 _ReuseP;      // x unused; y: cost attribution, the march ends at this distance in m (0: off, layers.costCutM); z unused; w: a span's points by distance, the step's growth per m of distance from the camera (0: off, layers.spanGrowthPerM)
        float4 _LeapP;       // unused (kept so that the uniform layout stays)
        float4 _CostP;       // coverage bound on (1) or off (0), cost-attribution mask (bit 0 no light, bit 1 no noise, bit 2 no erosion), lit steps per sun march (>= 1), step growth with the ray's lost transmittance (T05, 0: none)
        // The step offset's sequence in time (E40 grain against TVC): x the frame's phase, frac(frame number x golden ratio), computed in double precision by the
        // caller. Each pixel keeps one offset and every frame adds the phase to it. y 1: the light cone's rotation is the same
        // for every pixel (a probe), 0: the product's per-pixel rotation (the gradient noise), 2 its integer hash, 3 the blue-noise tile. z the per-pixel offset (layers.ditherSequence):
        // 1 an integer hash (white), 2 the interleaved gradient noise, 3 the blue-noise tile. w 1: the pattern's pixel is the march target's own (a low-resolution target draws texel k as pixel k); 0: the camera's size scales the uv, so a
        // half-size target reads the pattern at every second pixel and the neighbours' offsets are strongly correlated. The gradient noise's pattern stands still.
        float4 _DitherP;
        float4 _DitherM[64];   // E40 F2: the 16 x 16 blue-noise tile of the step offset 3 (ranks over 256, a texel per component)
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
        // ---- single clouds from the map's share (CELLS) ----
        #include "CloudCells.cginc"
        // ---- far view (package T06: aerial perspective, the shape's mean over the noise at wide spacing) ----
        #include "ReDefinitionAtmospheresFarView.cginc"
        // ---- height variation (package T13: base and top vary over the sky) ----
        #include "ReDefinitionAtmospheresBaseHeight.cginc"
        // ---- culling and distance LOD (package T16: the tile map the march reads, the fades and the LOD functions) ----
        #include "CloudCull.cginc"

        // The largest amount the coverage noise moves the map value by: the shape value is 0 to 1, so the threshold moves by half the amount. Master plan 43: a type's FORM AMOUNT raises
        // the amount by (1 + b) at its heights (the profile texture's channel b), so the layer's bound takes the largest b of its types (_LayerE[li].w; 0: the layer of before).
        float CoverageNoiseBound(int li)
        {
            return 0.5 * _LayerD[li].x * (1.0 + _LayerE[li].w);
        }

        // The same bound for a type at a height whose extra amount is b (the lift's headroom and the bound after the lift take it, so a lift of 1 still closes every column).
        float TypeNoiseBound(int li, float b)
        {
            return 0.5 * _LayerD[li].x * (1.0 + b);
        }

        // ---- M2c: the undulations of the stratiform types (variety undulatus) ----
        float4 _TypeUndulatus[8];   // each type's undulatus depth (CloudTypeDef.UndulatusAmount): layer li's type t at slot li * MAXT + t, four to a vector
        float4 _UndulatusP;         // x the frame's switch (cloud.undulatus, 0: off), y the wavelength as a share of the layer's depth, z the top roughness' wavelength share
        float4 _TypeTopRough[8];    // master plan 43: each type's short-wave swing of its top (CloudTypeDef.TopRoughness), the same slots

        float TypeUndulatusOf(int li, int type)
        {
            int slot = li * MAXT + type;
            return _TypeUndulatus[slot >> 2][slot & 3];
        }

        float TypeTopRoughOf(int li, int type)
        {
            int slot = li * MAXT + type;
            return _TypeTopRough[slot >> 2][slot & 3];
        }

        // Master plan 43, the form from above: the thickness factor of the top's short waves (WMO, stratus from an aircraft: "undulations (usually of short wavelengths)"), a
        // two-octave value noise (HeightRagged, -1 to 1) with features of _UndulatusP.z of the layer's depth, moving with the layer's offset; amount the share the thickness swings by.
        float TopRoughFactor(int li, float3 pc, float amount, float depthM)
        {
            float len = max(_UndulatusP.z * depthM, 1.0);
            return max(1.0 + amount * HeightRagged((pc + _NoiseOffset[li].xyz) / len, (uint)_NoiseOffset[li].w + 9173u), 0.0);
        }

        // The thickness factor of the undulations at a point: long waves across the level's wind (_NoiseAxis, the layer's wind; the waves move with the layer's offset like
        // its pattern), their crests bent by a value noise of twice their wavelength so that no crest is a straight line; amount the share the thickness swings by.
        float UndulatusFactor(int li, float3 pc, float amount, float depthM)
        {
            float len = max(_UndulatusP.y * depthM, 1.0);
            float3 q = pc + _NoiseOffset[li].xyz;
            float bend = HeightValueNoise(q / (2.0 * len), (uint)_NoiseOffset[li].w + 7019u);
            float phase = dot(q, _NoiseAxis[li].xyz) / len + 1.5 * bend;
            return max(1.0 + amount * sin(6.2831853 * phase), 0.0);
        }

        // ---- the type table ----
        float TypeProfile(int li, int type, float h)
        {
            float u = (saturate(h) * (PROFILE_SAMPLES - 1) + 0.5) / PROFILE_SAMPLES;
            float v = (li * MAXT + type + 0.5) / (MAXL * MAXT);
            return tex2Dlod(_TypeProfile, float4(u, v, 0, 0)).r;
        }

        // The whole texel of the profile (master plan 43, one form description per type): r the density, g the erosion gain (0: none), b the extra noise amount (0: none),
        // a the lift of the coverage threshold, of the type at the height h.
        float4 TypeProfile4(int li, int type, float h)
        {
            float u = (saturate(h) * (PROFILE_SAMPLES - 1) + 0.5) / PROFILE_SAMPLES;
            float v = (li * MAXT + type + 0.5) / (MAXL * MAXT);
            return tex2Dlod(_TypeProfile, float4(u, v, 0, 0));
        }

        float4 TypeInfo(int li, int type)
        {
            return tex2Dlod(_TypeInfo, float4((type + 0.5) / MAXT, (li + 0.5) / (2.0 * MAXL), 0, 0));
        }

        float4 TypeInfo2(int li, int type)
        {
            return tex2Dlod(_TypeInfo, float4((type + 0.5) / MAXT, (li + MAXL + 0.5) / (2.0 * MAXL), 0, 0));
        }

        // The density of layer li at a point (pc relative to the planet's centre, alt its altitude), distance t along the ray from the camera.
        // share: the height share of the layer; erosionOut: the interpolated type's erosion.
        // What one density read hands the march besides the density (master plan 44 step 3: until 2026-11-25 these were static globals the march read right after the call).
        struct DensityRead
        {
            // The altitude of the top of the cloud type at the point (the layer's outer altitude where no type was reached, and for the parity cell): the ambient's analytic
            // occlusion measures the cloud above a sample to this top, the far path of the light volume reads above it.
            float topAlt;
            // The altitude of the base of the cloud type at the point (the layer's inner where no type was reached; E40 LM2: the type's height for the light's far depth).
            float baseAlt;
            // E40 C1: the length along the ray (m) around the point within which the layer's map is certainly empty (0: no statement); the march leaps by it.
            float emptyM;
            // The vertical distance in m from an empty sample to the altitudes where cloud of the layer can begin there: the layer's reach, the height variation's band, the
            // type's slab, whichever test made the sample empty. Positive: the sample lies above them; negative: below; 0: the sample is not empty for that reason. The march
            // ends a leap there (with layers.spanGrowthPerM).
            float slabGap;
            // The thickness in m of the cloud slab at the point when it reached the slab test (the type's base to top after the height variation and the undulations); 0 where
            // the sample was made empty before (the map, the layer's reach): the march then takes the layer's thinnest slab (_CullL[li].x).
            float slabM;
            // What the noise and cell helpers left at the point (their own scratch globals, copied once in LayerDensityEx): the erosion detail's value (PUFF's bulge light),
            // the cumuliform volume's cavity channel and its type strength (CLOUDR1 P3), whether a single cloud's body was read, the least horizontal distance to another
            // body (CellProfile), and the depth the march may leap inside a body's shell without meeting its surface (CellShellGap; 0 where no body was read).
            float noiseD;
            float cavity;
            float cavityK;
            bool bodyRead;
            float cellOther;
            float shellGap;
        };

        // The empty reach at a map point (EmptyReachMath.ReachMetres is its CPU twin): the texels to the nearest texel that can hold cloud as an angle on the planet (a texel is pi / height across
        // and 2 pi / width times the cosine of the latitude along a parallel), as the chord at the planet's radius (a straight line keeps at least that far from the centre), on a body with a surface.
        float EmptyReach(int li, float2 uv)
        {
            float e = UNITY_SAMPLE_TEX2DARRAY_LOD(_CovReach, float3(uv, _LayerC[li].z), 0).r * 255.0;
            float texelLat = PI / _MapDim.y;
            float latMax = min(abs(0.5 - uv.y) * PI + e * texelLat, 1.5707);
            return _StepP.w * _PlanetR * sin(min(e * min(texelLat, 2.0 * PI / _MapDim.x * cos(latMax)), 1.5707));
        }

        // ---- the density read in its parts (master plan 44 step 4: LayerDensityCore calls them in order; the statements are the former single function's) ----
        // No part takes a struct as out or inout: fxc's copy-back of such a parameter read components whose writes the optimiser had dropped (a field no later code reads,
        // e.g. TypeRead.puff in the lattice variant), and its validator failed the variant with X8000 "source component in temp not initialized" (PRUEFPROTOKOLL 259).
        // A part returns its struct, or takes the struct by value and moves single values inout.

        // What the maps say at the point: the coverage after the clear-sky rule (and the cells' put-down in the lattice variant), the type value, the map's uv clamped for the
        // read and raw (the cells and the height maps take it), the map slice (below 0: the layer has no maps); empty: the coverage bound (or the lattice's cells) already
        // make the point empty.
        struct MapRead
        {
            float cov;
            float typeValue;
            float2 uv;
            float2 uvRaw;
            float slice;
            bool empty;
        };

        // The maps. Where they make the point empty, emptyM holds the empty reach the march leaps by.
        MapRead ReadMaps(int li, float3 pc, bool heightOn, float inner, float outer, int light, inout float emptyM)
        {
            MapRead m;
            m.empty = false;
            m.slice = _LayerC[li].z;
            m.cov = 1.0;
            m.typeValue = 0.0;
            m.uv = 0;
            m.uvRaw = 0;
            if (m.slice >= 0 || heightOn)
            {
                m.uv = MapUv(pc, _LayerD[li].w);
                m.uvRaw = m.uv;
            }
            if (m.slice >= 0)
            {
                float vMin = 0.5 / _MapDim.y;
                m.uv.y = clamp(m.uv.y, vMin, 1.0 - vMin);
                m.cov = SlewedMap(UNITY_SAMPLE_TEX2DARRAY_LOD(_CovMaps, float3(m.uv, m.slice), 0).rg, _MapReachCov, m.slice);
                m.cov = min(m.cov, 2.0 * m.cov - _CullL[li].y);  // E40 soft density: the clear-sky rule of the water content (CloudLayerDef.ClearSkyMap); K = 0 is off
                // COST (E40 grain against TVC): the coverage bound of below, asked before the type table and the height variation are read. the type's lift only
                // lowers the map value, so a map that fails here fails there too: the result is the same, the reads of an empty sample are fewer.
                if (DEV(_CostP.x, 1.0) > 0.5 && m.cov + _LayerB[li].y - 1.0 + CoverageNoiseBound(li) + 0.5 * max(_LayerB[li].z, 1e-4) <= 0.0)
                {
                    emptyM = EmptyReach(li, m.uv);
                    m.empty = true;
                    return m;
                }
                // CELLS: the map's share put down as single clouds (CloudCells.cginc). Where the bound above failed the share is 0, so the map's empty reach stays exact; here the discs give
                // their own: no disc begins within it, and outside every disc the value cannot reach the threshold (the same bound). _CullL[li].w is the pitch (0: no cells).
                // The map-driven sky (_CellS per layer, zero where a round draws no sheet): the type is read before the cells and a stratiform type is drawn connected (CellStratiformWeight).
                m.typeValue = SlewedMap(UNITY_SAMPLE_TEX2DARRAY_LOD(_TypeMaps, float3(m.uv, m.slice), 0).rg, _MapReachType, m.slice);
                float stratiformW = CellStratiformWeight(li, m.typeValue);
                // The lattice (cloud.field off) is a test function for single cloud types, never the product: its code is only in the tools' shader
                // ReDefinitionAtmospheresLayersLattice.shader, which defines CLOUD_LATTICE (master plan 45.3; LayerRenderer switches the material to it while cloud.field
                // is off; without it the march pass is 19 % shorter and holds 10 fewer registers).
#if defined(CLOUD_LATTICE)
                [branch] if (_CullL[li].w > 0.0)
                {
                    float cellReach, wMap;
                    float edgeC = max(_LayerB[li].z, 1e-4);
                    gCellBodyX = CellBodyReachX(li);
                    m.cov = CellCover(m.cov, m.uvRaw, _CullL[li].w, _PlanetR + 0.5 * (inner + outer), _LayerB[li].y, edgeC, _LayerD[li].x, light != 0 ? 0.0 : gNoiseFoot,
                                      (uint)_NoiseOffset[li].w, stratiformW, cellReach, wMap);
                    if (DEV(_CostP.x, 1.0) > 0.5 && m.cov + _LayerB[li].y - 1.0 + CoverageNoiseBound(li) + 0.5 * edgeC <= 0.0)
                    {
                        emptyM = wMap <= 0.0 ? 0.7 * cellReach : 0.0;
                        m.empty = true;
                        return m;
                    }
                }
#endif
            }
            return m;
        }

        // The cloud type at the point: the two neighbouring types of the table and the weight between them, the type's base and top (shares of the layer), its erosion, the
        // water exponent and the single-cloud preset (puff, the nearer type's).
        struct TypeRead
        {
            int ta;
            int tb;
            float f;
            float b;
            float tp;
            float erosionType;
            float waterPow;
            int puff;
        };

        // The type. Also sets the noise helpers' gNoiseSm, gCumuliformPreset and gCumuliformCavK for the shape read (their scratch globals).
        TypeRead ReadType(int li, float typeValue)
        {
            TypeRead ty;
            float typeCount = _LayerC[li].w;
            // The type value spans the table; two neighbouring types are interpolated (heights and profile).
            float x2 = saturate(typeValue) * (typeCount - 1);
            ty.ta = (int)floor(x2);
            ty.ta = min(ty.ta, (int)typeCount - 1);
            ty.tb = min(ty.ta + 1, (int)typeCount - 1);
            ty.f = x2 - ty.ta;
            float4 ia = TypeInfo(li, ty.ta), ib = TypeInfo(li, ty.tb);
            ty.b = lerp(ia.x, ib.x, ty.f);
            ty.tp = lerp(ia.y, ib.y, ty.f);
            gNoiseSm = lerp(ia.w, ib.w, ty.f);
            // CLOUDR1: the type's cumuliform preset (the nearer of the two types), the exponent of its density rim and its cavity strength; not read where no type has one.
            ty.waterPow = 1.0 + _CullL[li].z;   // the layer's water exponent (stored minus 1: 0 = none)
            ty.puff = 0;
            [branch] if (_CumuliformP.w > 0.5 || _FieldP.x > 0.5)
            {
                float4 ja = TypeInfo2(li, ty.ta), jb = TypeInfo2(li, ty.tb);
                ty.puff = (int)(ty.f < 0.5 ? ja.w : jb.w);
                gCumuliformPreset = (int)(ty.f < 0.5 ? ja.x : jb.x);
                gCumuliformCavK = lerp(ja.z, jb.z, ty.f);
                ty.waterPow = lerp(ja.y > 0 ? ja.y : ty.waterPow, jb.y > 0 ? jb.y : ty.waterPow, ty.f);
            }
            ty.erosionType = lerp(ia.z, ib.z, ty.f);
            return ty;
        }

        // The type's slab at the point: the height maps' local base and top, the undulatus and the top's short waves move the type's base b and top tp (shares of the layer).
        // False where the point lies outside the slab; slabGap and slabM then hold the slab's gap and thickness, topAlt and baseAlt its top and base where the point is inside.
        bool TypeSlab(int li, float3 pc, float2 uvRaw, bool heightOn, float inner, float outer, float share, TypeRead ty, inout float b, inout float tp, inout float slabGap,
                      inout float slabM, inout float topAlt, inout float baseAlt)
        {
            if (heightOn)
            {
                float localBase, localTop;
                if (!HeightLocalBaseTop(li, pc, uvRaw, b, tp, ty.erosionType, outer - inner, share, localBase, localTop))
                {
                    if (localTop > localBase) slabGap = (share > localTop ? share - localTop : share - localBase) * (outer - inner);
                    return false;
                }
                b = localBase;
                tp = localTop;
            }
            // M2c: the stratiform types' undulations swing the type's THICKNESS (its top; the base stays even), before the height test so that the leap's gap and the light
            // path see the same shape (off while _UndulatusP.x is 0 or the type has no undulatus depth: the layer of before, bit for bit).
            float undulatusAmount = _UndulatusP.x * lerp(TypeUndulatusOf(li, ty.ta), TypeUndulatusOf(li, ty.tb), ty.f);
            [branch] if (undulatusAmount > 0.0) tp = b + (tp - b) * UndulatusFactor(li, pc, undulatusAmount, outer - inner);
            // Master plan 43: the top's short waves (0 for a type without topRoughness: the layer of before, bit for bit).
            float topRough = lerp(TypeTopRoughOf(li, ty.ta), TypeTopRoughOf(li, ty.tb), ty.f);
            [branch] if (topRough > 0.0) tp = b + (tp - b) * TopRoughFactor(li, pc, topRough, outer - inner);
            slabM = max(tp - b, 0.0) * (outer - inner);
            if (share < b || share > tp || tp <= b)
            {
                if (tp > b) slabGap = (share > tp ? share - tp : share - b) * (outer - inner);
                return false;
            }
            topAlt = inner + tp * (outer - inner);
            baseAlt = inner + b * (outer - inner);
            return true;
        }

        // The share of the point that is cloud (0: none): the type's lift of the threshold, the field's single clouds, the coverage bound, the shape noise and the erosion, the
        // cells' bodies. pf: the type's profile texel at the height (r the density, g the erosion gain, b the extra noise amount, a the lift); h the height share in the type.
        float ShapeShare(int li, float3 pc, float share, float cov, float slice, float2 uvRaw, float inner, float outer, int light, TypeRead ty, out float4 pf, out float h)
        {
            float erosion = ty.erosionType * _LayerD[li].z;
            float coverage = _LayerB[li].y;
            float edge = max(_LayerB[li].z, 1e-4);
            // E40 L2 D1: the type's height-shaped threshold. The profile texture's channel a (cfg node LIFT of the type, 0 where there is none) lifts the coverage threshold at this height
            // by up to the layer's headroom, so a cloud is narrower where the lift is high and closed where it is 1 (the top of every type closes, CloudTypeDef.TopClose): a cumulus dome over a
            // flat base, a cumulonimbus tower under a wide anvil. The lift is never negative, so the coverage bounds of above and below stay exact. The technique is the height
            // gradient of Schneider's cloudscapes (SIGGRAPH 2015: the coverage is reduced by the height signal of the cloud type); the code is the project's own.
            h = (share - ty.b) / (ty.tp - ty.b);
            pf = lerp(TypeProfile4(li, ty.ta, h), TypeProfile4(li, ty.tb, h), ty.f);
            // CLOUD FIELD: a puffy type's single cloud from the field (FieldCloud, CloudCells.cginc; the cloud shadow reads the same function).
            bool fieldBody = false;
#if !defined(CLOUD_LATTICE)
            [branch] if (ty.puff > 0 && slice >= 0)
            {
                int fieldCloud = FieldCloud(li, ty.puff, uvRaw, cov, coverage, edge, _LayerD[li].x, light != 0 ? 0.0 : gNoiseFoot, _PlanetR + 0.5 * (inner + outer), h,
                                            (ty.tp - ty.b) * (outer - inner));
                if (fieldCloud == 0) return 0;
                fieldBody = fieldCloud > 0;
            }
#endif
            cov -= pf.a * (coverage + TypeNoiseBound(li, pf.b) + 0.5 * edge);   // E40 CB step 0: the lift in units of the type's headroom at h (LayerTypeTable.LiftHeadroom), 1 closes every column
            // CELLS: the body of a single cloud (CellBody) where the cells alone draw (not where the map's own value takes part: the closed cloud layer and the far view): its own height shape
            // replaces the type's lift, so the lift's bound below is not asked; outside the body the share is 0.
            bool body = gCellX >= 0.0 && gCellW <= 0.0;
            // COST (T01c): the coverage shape is zero where even the noise's highest value cannot lift the map above the threshold; the
            // noise is evaluated only where this bound passes. The bound is the same expression as the shape below, so the result is exact.
            if (!body && DEV(_CostP.x, 1.0) > 0.5 && cov + coverage - 1.0 + TypeNoiseBound(li, pf.b) + 0.5 * edge <= 0.0) return 0;
            // The cost debug bits switch the shape noise (2) and the erosion (4) off. Master plan 43: the type's FORM AMOUNT (channel b) raises the amount at this height.
            float noiseAmount = fmod(floor(DEV(_CostP.y, 0.0) * 0.5), 2.0) < 0.5 ? _LayerD[li].x * (1.0 + pf.b) : 0.0;
            if (fmod(floor(DEV(_CostP.y, 0.0) * 0.25), 2.0) >= 0.5) erosion = 0;
            // PUFF: the erosion grows toward the top of the cloud (the heads of a cumulus are the cauliflower, its flat base is not): erosion (1 + gain h).
            erosion *= 1.0 + _BulgeP.z * saturate(h);
            // The coverage share: the map moved by the shape noise, the soft edge and the erosion (read only in the edge band). The body reads the bulge noise itself through the
            // same call (the threshold at the noise's mean, a unit edge, the full swing, no erosion: the share is the noise's rank), so the noise keeps ONE call site (CLOUDR2 P4).
            float covN = cov, edgeN = edge, amountN = noiseAmount, erosionN = erosion;
            if (body)
            {
                covN = 1.0 - coverage;
                edgeN = 1.0;
                amountN = noiseAmount > 0.0 ? 1.0 : 0.0;
                erosionN = 0.0;
            }
            // Master plan 43: the profile texture's channel g is the type's erosion gain at this height (FORM EROSION; the pannus' EROSION while cloud.pannus is on; 0: none): the fine
            // detail eats the outline and the top harder there. Not a scale of pc: pc counts from the planet's centre, and a factor on it that changes with height tore the top into steps.
            erosionN *= 1.0 + pf.g;
            float s = NoiseShare(li, pc, covN, coverage, edgeN, amountN, erosionN, light);
            [branch] if (body)
            {
                float2 fine = _CellE[li].z > 0.0 ? CellFineBulges(li, pc) : float2(0.0, 0.0);
                s = CellBody(li, h, s, fine, (ty.tp - ty.b) * (outer - inner));
                // The field's distance in m is estimated from the gradient, not exact: the march does not leap over a body's shell on it.
                if (fieldBody) gBodyRead = false;
            }
            return s;
        }

        // light 1: the light march's read (the shape only: no erosion detail, no far-view mean; the inlined copy of the light path is the slim one, CLOUDR2 P4).
        float LayerDensityCore(int li, float3 pc, float alt, float t, int light, out float share, out DensityRead dr)
        {
            share = 0;
            dr.emptyM = 0;
            dr.slabGap = 0;
            dr.slabM = 0;
            // The helpers' values: LayerDensityEx copies them after this returns.
            dr.noiseD = 0;
            dr.cavity = 1;
            dr.cavityK = 0;
            dr.bodyRead = false;
            dr.cellOther = 1e9;
            dr.shellGap = 0;
            gCellX = -1.0;
            gCellW = 1.0;
            gBodyRead = false;
            gNoiseD = _NoiseS.w;
            gCumuliformPreset = 0;
            gCumuliformCav = 1;
            gCumuliformCavK = 0;
            float inner = _LayerA[li].x, outer = _LayerA[li].y;
            dr.topAlt = outer;
            dr.baseAlt = inner;
            share = (alt - inner) / (outer - inner);
            // The height variation (T13) lets a base reach below the inner altitude and a top above the outer one, by the layer's reach.
            bool heightOn = _LayerH2[li].y > 0;
            float reachDown = heightOn ? HeightReachDown(li) / (outer - inner) : 0.0;
            float reachUp = heightOn ? HeightReachUp(li) / (outer - inner) : 0.0;
            if (share < -reachDown || share > 1 + reachUp)
            {
                dr.slabGap = (share > 0.0 ? share - 1.0 - reachUp : share + reachDown) * (outer - inner);
                return 0;
            }
            if (DEV(_CellA.w, 0.0) > 0) return li == 0 ? CellDensity(pc) : 0;   // the parity test cell (T03)
            float fade = 1.0;
            float fadeNear = _LayerC[li].x, fadeFar = _LayerC[li].y;
            if (fadeFar > fadeNear)
            {
                float x = saturate((fadeFar - t) / (fadeFar - fadeNear));
                fade = x * x * (3.0 - 2.0 * x);
                if (fade <= 0) return 0;
            }
            // The parts in order: the maps, the type, the type's slab, the shape. Each part that finds the point empty says so and the read ends (the values it set in dr stay).
            MapRead m = ReadMaps(li, pc, heightOn, inner, outer, light, dr.emptyM);
            if (m.empty) return 0;
            TypeRead ty = ReadType(li, m.typeValue);
            if (!TypeSlab(li, pc, m.uvRaw, heightOn, inner, outer, share, ty, ty.b, ty.tp, dr.slabGap, dr.slabM, dr.topAlt, dr.baseAlt)) return 0;
            float4 pf;
            float h;
            float s = ShapeShare(li, pc, share, m.cov, m.slice, m.uvRaw, inner, outer, light, ty, pf, h);
            if (s <= 0) return 0;
            float pw = ty.waterPow;
            if (pw > 0.05 && pw != 1.0) s = pow(s, pw);
            return _LayerB[li].x * s * pf.x * fade;
        }

        // The density read with everything the march takes from it in dr (the helpers' scratch globals copied at this one place).
        float LayerDensityEx(int li, float3 pc, float alt, float t, int light, out float share, out DensityRead dr)
        {
            float density = LayerDensityCore(li, pc, alt, t, light, share, dr);
            dr.noiseD = gNoiseD;
            dr.cavity = gCumuliformCav;
            dr.cavityK = gCumuliformCavK;
            dr.bodyRead = gBodyRead;
            dr.cellOther = gCellOther;
            dr.shellGap = CellShellGap(li);
            return density;
        }

        float LayerDensity(int li, float3 pc, float alt, float t, out float share, out DensityRead dr)
        {
            return LayerDensityEx(li, pc, alt, t, 0, share, dr);
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

        // The per-pixel step offset of layers.ditherSequence 3: a 16 x 16 tile of blue noise (_DitherM, the ranks of a void-and-cluster arrangement over 256, DitherMatrix.cs).
        float DitherBlue(float2 p)
        {
            int2 q = int2(floor(p)) & 15;
            int i = q.y * 16 + q.x;
            return _DitherM[i >> 2][i & 3];
        }

        MarchOut March(float3 o, float3 d, float tmax, float2 pixel)
        {
            MarchOut r;
            r.rgb = 0; r.alpha = 0; r.tau = 0; r.steps = 0; r.first = -1;
            r.tauLayer = 0;
            r.direct = 0; r.ambient = 0;
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
                if (DEV(_LimitP.z, 0.0) > 0 && fmod(floor(DEV(_LimitP.z, 0.0) / pow(2.0, (float)li)), 2.0) < 0.5) continue;
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
            // The step offset: the pixel keeps one offset (the blue-noise tile; the interleaved gradient noise puts a few percent of its power into single spatial lines, which show) and every frame adds the golden ratio to it
            // (Roberts 2018, the R1 sequence): any run of frames spreads its offsets evenly over the grid step, so the history of the pass integrates the step by
            // strata instead of by chance (the cloud's entry, its light at the sample's depth and the hit or miss of a thin rim all depend on that offset).
            float jitterValue = frac((DEV(_DitherP.z, 3.0) < 1.5 ? DitherHash(pixel) : (DEV(_DitherP.z, 3.0) < 2.5 ? Ign(pixel, 0.0) : DitherBlue(pixel))) + _DitherP.x);
            float jitter = lerp(0.5, jitterValue, _StepP.y);
            int maxSteps = (int)_MarchP.x;
            float leapMax = max(_SkipP.x, 1.0);
            float lightCut = _SkipP.y;
            bool noLight = fmod(floor(DEV(_CostP.y, 0.0)), 2.0) > 0.5;
            // With the points by distance (layers.spanGrowthPerM > 0) a leap ends, in whole grid steps, where the ray reaches the altitude at which cloud can begin (the type's slab,
            // the band of the height variation, the layer's reach), and a leap so ended starts again from one grid step. Without them the march is as it was.
            bool lbGap = _ReuseP.w > 0.0, lbRestart = lbGap;
            [loop] for (int k = 0; k < 8; k++)
            {
                if (k >= n) break;
                // TDR (K-T2 2026-11-16/17: the driver reset at the first frames inside the low stratiform rain layer): every stop is written so that a value that is not a number stops the ray. A NaN fails
                // every comparison, so "T < threshold" never ended a ray whose transmittance was NaN, and "t >= end" never ended one whose position was: it ran all 8 x 512 steps with a
                // light march at each, seconds per frame.
                if (!(T >= _MarchP.w)) break;
                int li = sl[k];
                // T16: the layer cap thins a span by a smooth factor and drops it where the factor is zero.
                float spanF = 1.0;
                if (_CullB.z > 0.0)
                {
                    spanF = CullCapFade(k, s0[k]);
                    if (spanF <= 0.0) continue;
                }
                float len = s1[k] - s0[k];
                float kind = _LayerB[li].w;
                // Cheap and fog layers take a share of the step budget (they carry no detail the full count would resolve).
                float share01 = kind > 1.5 && kind < 2.5 ? _SkipP.w : (kind > 0.5 && kind < 1.5 ? _SkipP.z : 1.0);
                float stepLen = max(_StepP.x, 1e-3);
                // The far view (T06) keeps at least _FarD.w samples in a span: a thin layer seen from far away has a step longer than itself, and one
                // sample at a jittered place is a coin toss of the cloud's coverage that changes with the step count (the coverage of an orbit view
                // jumped by 14 percent between two altitudes where the count went from two to one).
                float minCount = max(1.0, _FarD.w);
                // layers.spanGrowthPerM (_ReuseP.w, 0: off): a span's points by distance. The step at the distance t is dt (1 + g t), so a long span is fine near the camera and coarse far
                // away instead of evenly spaced; lenU is the span's length in units of that step at the camera (the integral of ds / (1 + g s)), and a short or near span keeps today's
                // count (ln(1 + x) -> x).
                // _SpacingP.w (layers.spanGrowthFromM, 0: from the camera): the step grows only beyond this distance, dt (1 + g max(t - from, 0)); the span's part before it is evenly spaced.
                float spanG = _ReuseP.w;
                float growFrom = _SpacingP.w;
                float grow0 = max(s0[k] - growFrom, 0.0), grow1 = max(s1[k] - growFrom, 0.0);
                float lenU = spanG > 0.0 ? (len - (grow1 - grow0)) + log((1.0 + spanG * grow1) / (1.0 + spanG * grow0)) / spanG : len;
                int count = (int)clamp(ceil(lenU / stepLen), minCount, max(minCount, floor(maxSteps * share01 + 0.5)));
                float dt = lenU / count;
                float dtStart = spanG > 0.0 ? dt * (1.0 + spanG * grow0) : dt;
                gNoiseFoot = spanG > 0.0 ? max(dt, _CullD.y * s0[k]) : dt;   // the sample spacing: the erosion detail's distance LOD (T02)
                // The samples lie on the grid s0 + dt * (i + jitter). An empty sample lets the next ones leap over 1, 2 .. leapMax grid
                // points; a leap that lands on cloud goes back to the grid point after the last empty one, so a cloud is always entered
                // at the grid's own resolution and only features narrower than the leap can be passed over.
                // E40 C2: a layer with coarse noise leaps up to _SpacingP.z of its noise scales over empty grid points (the leap's growth is capped there); 0: the leap of _SkipP.x for every layer.
                float leapCap = (DEV(_SpacingP.z, 1.0) > 0.0 && _LayerD[li].y > 0.0) ? max(leapMax, floor(DEV(_SpacingP.z, 1.0) / (_LayerD[li].y * dt))) : leapMax;
                // Cost attribution (layers.costCutM, _ReuseP.y): the span ends at the cut, so the time saved is what the clouds beyond it cost; the picture is wrong while set.
                float tEnd = DEV(_ReuseP.y, 0.0) > 0.0 ? min(s1[k], DEV(_ReuseP.y, 0.0)) : s1[k];
                float t = s0[k] + dtStart * jitter;
                float tLast = t - dtStart;
                float leap = 1.0, arrived = 1.0;
                float sunTau = 0.0, upTau = 0.0, tauSince = 0.0;
                bool haveSun = false;
                float sinceLight = 0.0;
                float shellLeft = (float)count;   // A4: the fine steps a span may take inside bodies' shells (cloud.cellShell)
                [loop] for (int i = 0; i < 512; i++)
                {
                    if (!(t < tEnd)) break;
                    float3 pc = o + d * t;
                    float alt = length(pc) - _PlanetR;
                    float share;
                    float dtv = dt;
                    if (spanG > 0.0) dtv = dt * (1.0 + spanG * max(t - growFrom, 0.0));
                    // With the points by distance the footprint of the shape is the pixel's size at the sample, not the step along the ray: the step is long far away, and a footprint
                    // of that length would replace a body by the map's mean (CellCover) where the pixel still resolves it. The jittered grid and the history integrate along the ray.
                    if (spanG > 0.0) gNoiseFoot = max(dt, _CullD.y * t);
                    DensityRead dr;
                    float dens = LayerDensity(li, pc, alt, t, share, dr) * spanF;
                    // A4 (owner audit 2026-11-17, cloud.cellTrace): beyond the reach of a single cloud's bulges the body's depth changes by at most 1 per m, so no surface lies within that
                    // depth (dr.shellGap) nor within the reach of a neighbour's body (dr.cellOther, less the band). The march leaps the whole grid steps that fit in it, so the grid's phase (its
                    // jitter, which spreads the entry over the frames) is kept and the picture is the grid march's, only cheaper; the entry is then found as before. A sphere trace on the
                    // bulges' slope bound (about 55 m per m for a 1 km body) crept up to every cloud over hundreds of steps and ran out of its loop (the ceiling march lost 80 % of its cover).
                    float traceGap = -1.0;
                    [branch] if (fmod(_CellE[li].w, 2.0) > 0.5 && dr.bodyRead && !(dens > 0))
                    {
                        float band = CellBand(li);
                        float gap = min(dr.shellGap, dr.cellOther - 4.0 * band);
                        // Only where it leaps further than the empty reach of the coverage (the grid's own skip below) would.
                        if (gap >= 2.0 * dtv && floor(gap / dtv) > floor(dr.emptyM / dtv)) traceGap = dtv * floor(gap / dtv);
                    }
                    float2 topAlt = float2(dr.topAlt, dr.baseAlt);   // the type's top and base at the sample
                    float slabM = dr.slabM > 0.0 ? dr.slabM : _CullL[li].x;
                    float bulgeD = dr.noiseD;
                    // CLOUDR1 P3: the cumuliform volume's cavity channel at the sample to the type's strength: 1 on tops and flat parts, below 1 in the creases between lobes (the light below takes it).
                    float cavity = 1.0;
                    [branch] if (dr.cavityK > 0.0) cavity = pow(max(dr.cavity, 0.02), 4.0 * dr.cavityK);
                    r.steps += 1;
                    // A4 (cloud.cellShell): within the bulges' reach of a body (no shell gap) an empty sample is followed by a fine one: the pixel's size there, at least CELL_SHELL_STEP_MIN,
                    // at most the grid step. A head smaller than the grid step lies between two grid points and was missed; the fine steps are spent where a surface can lie, the leap
                    // above saves them elsewhere. A span takes at most as many fine steps as it has grid points (a grazing ray runs kilometres in a shell), then the grid takes over.
                    // The fine samples follow the jittered grid point they start from, so the entry still spreads over the frames.
                    float shellStep = -1.0;
                    [branch] if (fmod(floor(_CellE[li].w * 0.25), 2.0) > 0.5 && dr.bodyRead && !(dens > 0) && traceGap <= 0.0 && shellLeft > 0.0 && dr.shellGap <= 0.0)
                    {
                        float fine = clamp(_CullD.y * t, CELL_SHELL_STEP_MIN, dtv);
                        if (fine < dtv)
                        {
                            shellStep = fine;
                            shellLeft -= 1.0;
                        }
                    }
                    if (shellStep > 0.0)
                    {
                        tLast = t;
                        arrived = 1.0;
                        leap = 1.0;
                        t += shellStep;
                        continue;
                    }
                    if (traceGap > 0.0)
                    {
                        // A4: the grid points within the gap are empty; the last of them is where an entry after the leap is walked back to (as the reach skip below).
                        tLast = t + traceGap - dtv;
                        arrived = 1.0;
                        leap = 1.0;
                        t += traceGap;
                        continue;
                    }
                    if (!(dens > 0))
                    {
                        // E40 C1: the grid points within the empty reach of this sample are empty too: leap over them (dr.emptyM is 0 where no reach was read).
                        float reachSkip = dtv * floor(dr.emptyM / dtv);
                        tLast = t + reachSkip;
                        arrived = leap;
                        float jump = dtv * leap + reachSkip;
                        // 8: above or below the altitudes where cloud can begin (the layer's reach, the height variation's band, the type's slab) a leap ends where the ray reaches
                        // them, when it moves towards them (a descending ray's vertical share only falls towards its lowest point, so the end lies at or before the altitude). In
                        // whole grid steps, as the reach skip: an end at the altitude itself is the same place for every pixel of a row, which drops the step offset's per-pixel
                        // phase and draws the clouds behind it in horizontal streaks (ground view, FAR7). 128: such a leap starts again from one grid step (cloud can begin right
                        // there; the altitude is the type's at the leap's start, another type along the leap may reach higher, so a landing on cloud is still walked back).
                        bool capped = false;
                        [branch] if (lbGap && dr.slabGap * dot(d, pc) < 0.0)
                        {
                            float toGap = dtv * max(1.0, floor(abs(dr.slabGap) * length(pc) / max(abs(dot(d, pc)), 1e-3 * length(pc)) / dtv));
                            capped = toGap < jump;
                            jump = min(jump, toGap);
                        }
                        // The leap beyond the certain empty reach is a guess that the sky stays empty; it is walked back only where it lands on cloud, so a leap that climbs or sinks
                        // through a whole slab between two samples passes over it. Its length in noise scales says nothing about the vertical: a ray at a shallow angle crosses a slab
                        // a few hundred metres thick in a few far steps, and far away the leaps of a row did so at one distance and drew a band of sky or sea through the low
                        // stratiform layer under the horizon, seen from above (PRUEFPROTOKOLL 166-167). So that part of the leap climbs or sinks at most half the slab's thickness
                        // (at least one sample inside any slab it crosses): the slab at the sample, or the layer's thinnest where the sample never reached the slab test (a sample
                        // the map made empty), in whole grid steps. layers.costDebug bit 16 leaves it out (cost attribution only).
                        [branch] if (slabM > 0.0 && fmod(floor(DEV(_CostP.y, 0.0) / 16.0), 2.0) < 0.5)
                        {
                            float vertical = max(abs(dot(d, pc)) / length(pc), 1e-3);
                            float capLen = dtv * max(1.0, floor(0.5 * slabM / vertical / dtv));
                            if (capLen < jump - reachSkip) jump = reachSkip + capLen;
                        }
                        t += jump;
                        // With the points by distance the leap's cap in grid points follows the step at the sample (the cap is a length in the layer's noise scales; dt is the
                        // step at the camera, and a cap counted in it would let a far leap pass over whole clouds).
                        float leapCapHere = (spanG > 0.0 && DEV(_SpacingP.z, 1.0) > 0.0 && _LayerD[li].y > 0.0) ? max(leapMax, floor(DEV(_SpacingP.z, 1.0) / (_LayerD[li].y * dtv))) : leapCap;
                        leap = (lbRestart && capped) ? 1.0 : min(leap + 1.0, leapCapHere);
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
                        if (noLight) reuse = 0.0;
                        else if (haveSun && !(T * ext >= lightCut && sinceLight + 1.0 >= _CostP.z))
                        {
                            reuse = sunTau;
                            sinceLight += 1.0;
                        }
                        float taps, tauUsed, upUsed;
                        // Up mode 3 moves the reused optical depth straight up by what the ray has crossed since: the column above a point that is lower
                        // by h holds the density integral over h more, which along the ray is the optical depth since times the ray's vertical share.
                        float upReuse = -1.0;
                        if (reuse >= 0.0)
                        {
                            upReuse = upTau;
                            if (_LightT.w > 2.5) upReuse = max(upTau - dot(d, normalize(pc)) * tauSince, 0.0);
                        }
                        src = LightSource(pc, d, dens, share, li, t - dts, reuse, upReuse, topAlt, dirPart, ambPart, taps, tauUsed, upUsed);
                        if (reuse < 0.0) { sunTau = tauUsed; upTau = upUsed; haveSun = true; sinceLight = 0.0; tauSince = 0.0; }
                    }
                    else
                    {
                        // Cheap and fog layers: no march towards the light (a local estimate and a fixed share).
                        float3 sky = lerp(_AmbientBottom.rgb, _AmbientTop.rgb, saturate(share));
                        sky = lerp(Luma(sky).xxx, sky, _LightR.z) * _LightS.z * _LightL[li].z;
                        ambPart = max(sky * _PhaseP.w, (_LightQ.z * Luma(_SunColor.rgb) * LIGHT_INV4PI).xxx);
                        float lit = kind < 1.5 ? exp(-dens * (spanG > 0.0 ? dtv : dt) * 4.0) : 0.25;
                        dirPart = _SunColor.rgb * SunAir(pc, _Sun.xyz) * lit * (lerp(ph, LIGHT_INV4PI, _LightL[li].w) + _LightM[li].z * LIGHT_INV4PI) * _PhaseP.w;
                        src = dirPart + ambPart;
                    }
                    // PUFF (_BulgeP.x): bulge shading, a sample's direct light follows the erosion detail there (the bumps catch the light, the crevices between them are shaded).
                    float bulgeShade = saturate(1.0 + _BulgeP.x * 2.0 * (bulgeD - _NoiseS.w));
                    src += dirPart * (bulgeShade - 1.0);
                    dirPart *= bulgeShade;
                    // The colour map's tint of the layer (P15; one when the layer has none).
                    // CLOUDR1 P3: the creases between lobes are lit less, by the direct light with its multiple scattering octaves and by the sky's fill (a factor, no extra light path).
                    src += (dirPart + ambPart) * (cavity - 1.0);
                    dirPart *= cavity;
                    ambPart *= cavity;
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
                    if (!(T >= _MarchP.w)) break;
                }
            }
            // A ray that ended on the early-out is opaque: the remaining transmittance (up to the threshold, 1 %) would show a bright background (the
            // sun's or the moon's disc, a lit planet) as a dot through a thick cloud (T03v2).
            r.alpha = T < _MarchP.w ? 1.0 : 1.0 - exp(-r.tau);
            // Aerial perspective (T06): the air between the camera and the cloud's mean distance thins the cloud's colour towards the horizon sky.
            if (_FarA.x > 0.5 && r.weightSum > 1e-9)
            {
                float3 airT = exp(-FarAirTau(o, d, r.distSum / r.weightSum));
                // PRUEFPROTOKOLL 272: the air's in-scatter is the clear air's where the sun reaches it; under a deck the air lies in the deck's shadow and gets only the share of the
                // sun's flux the clouds pass on (two-stream), read at the camera (_FarC.w, the CPU's cloud field along the sun; 1 under a clear sky).
                r.rgb = r.rgb * airT + r.alpha * _FarC.w * lerp(_FarE.rgb, _FarC.rgb, 1.0 - airT.z) * (1.0 - airT);   // E40 T1: the in-scatter of a short path is the sun's own colour, of a long one the horizon's
            }
            return r;
        }

        // The longest ray of a pixel: the scene's depth at the pixel (as a distance along the ray) and the measurement limit. A limit below
        // zero is a ramp: the ray's length grows with the pixel's x from 0 to -limit (one render samples a profile along a ray).
        float SceneDistance(float2 uv, float3 d)
        {
            float limX = DEV(_LimitP.x, 0.0);
            float lim = limX < 0 ? -limX * uv.x : (limX > 0 ? limX : 1e30);
            if (DEV(_LimitP.w, 1.0) < 0.5) return lim;
            float depth = tex2Dlod(_SceneDepth, float4(uv, 0, 0)).r;
            if (depth <= 0 || depth > 1e9) return lim;
            return min(lim, depth / max(dot(d, normalize(_CamForward.xyz)), 1e-3));
        }

        // The stretch of a ray (o planet-relative) in which a cloud can lie: from where it first enters the shell between the lowest layer's tight inner altitude and the highest
        // layer's tight outer altitude to where it leaves that shell for the last time; on a body with a surface (_StepP.w) a ray that meets the ground leaves it where it goes
        // below the shell's inner sphere, since nothing follows the ground. tOut <= tIn: the ray never enters the shell. The Reconstruct shader's CloudWindow is its twin.
        void CloudWindow(float3 o, float3 d, out float tIn, out float tOut)
        {
            tIn = 0.0;
            tOut = 0.0;
            float lowM = 1e9, highM = -1e9;
            [loop] for (int li = 0; li < 4; li++)
            {
                if (li >= (int)_LayerCount) break;
                lowM = min(lowM, _LayerA[li].z);
                highM = max(highM, _LayerA[li].w);
            }
            if (highM <= lowM) return;
            float rIn = _PlanetR + max(lowM, 0.0), rOut = _PlanetR + highM;
            float o0, o1;
            if (!HitSphere(o, d, rOut, o0, o1) || o1 <= 0.0) return;
            float p0, p1, i0, i1;
            bool ground = _StepP.w > 0.5 && HitSphere(o, d, _PlanetR, p0, p1) && p0 > 0.0;
            bool inner = HitSphere(o, d, rIn, i0, i1);
            if (length(o) < rIn)
            {
                if (ground) return;
                tIn = i1;
                tOut = o1;
                return;
            }
            tIn = max(o0, 0.0);
            tOut = ground ? (inner ? max(i0, tIn) : p0) : o1;
        }

        // The scene's distance the depth-aware weights compare (the composite below, the reconstruction's SceneZ): the distance along the ray to the scene, held within the
        // stretch where a cloud can lie (CloudWindow). Two scene points beyond that stretch end their rays after every cloud, so they march the same clouds whatever their depths;
        // two before it march none. Compared raw, the sea seen from above, whose depth grows by several percent from row to row near the horizon and ends at the near ocean mesh's
        // edge 50 to 60 km away (the sky's depth 0 beyond it), split the four texels into the nearest one and drew thin straight lines across the low stratiform layer under the
        // horizon (PRUEFPROTOKOLL 172-175). 0: the ray never meets the shell. The composite holds every tap within the pixel's own stretch, so two taps beyond it compare equal.
        float CompositeDistance(float2 uv, float tIn, float tOut)
        {
            if (tOut <= tIn) return 0.0;
            float3 d = RayDir(uv);
            float depth = tex2Dlod(_SceneDepth, float4(uv, 0, 0)).r;
            float t = depth <= 0 || depth > 1e9 ? 1e30 : depth / max(dot(d, normalize(_CamForward.xyz)), 1e-3);
            return clamp(t, tIn, tOut);
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

        // dist: the mean distance along the ray of the cloud the pixel shows (the march's weights in the ray's colour, MarchOut.distSum / weightSum), 0 where the pixel holds no
        // cloud or the pass draws an aid. The reconstruction's history is moved by it (recon.trueDistance, pass 6).
        float4 Pass0D(v2f i, out float dist)
        {
            dist = 0.0;
            i.uv = FullUv(i.uv);
            CullReadTile(i.uv);   // T16: the layers this pixel's tile marches
            float3 d = RayDir(i.uv);
            float3 o = _CamPos.xyz - _PlanetCenter.xyz;
            float tmax = SceneDistance(i.uv, d);
            // The pixel grid of the dither: the pass's own sizes (_ReconD: the full target x, y and the march target z, w; the direct path sets x, y to the camera's
            // pixels and z, w to 0). Unity's _ScreenParams only where no caller set them (0, the weather player's own scenes): a command buffer executed outside a
            // camera's render (the pinned replay) does not have the camera's size in it.
            float2 grid = _ReconD.x > 0.5 ? _ReconD.xy : _ScreenParams.xy;
            float2 pixel = i.uv * (_ReconP.x < 1.5 ? (_ReconD.z > 1.5 ? _ReconD.zw : grid) : _ReconD.xy);
            MarchOut r = March(o, d, tmax, pixel);
            float aid = DEV(_LimitP.y, 0.0);
            if (aid > 1.5 && aid < 2.5)
            {
                float3 c = r.tauLayer.x * float3(0.9, 0.3, 0.2) + r.tauLayer.y * float3(0.2, 0.7, 0.9) + r.tauLayer.z * float3(0.9, 0.8, 0.2) + r.tauLayer.w * float3(0.4, 0.9, 0.3);
                float s = max(r.tau, 1e-6);
                return float4(c / s * r.alpha, r.alpha);
            }
            if (aid > 0.5 && aid < 1.5) return float4(Heat((log10(max(r.tau, 1e-4)) + 3.0) / 4.5) * step(1e-4, r.tau), r.alpha);
            if (r.weightSum > 1e-9) dist = r.distSum / r.weightSum;
            return float4(r.rgb, r.alpha);
        }

        float4 Pass0(v2f i)
        {
            float dist;
            return Pass0D(i, dist);
        }

        // The march drawn as one procedural triangle into two targets (pass 6): a command buffer's Blit binds one target only. The uv is the Blit's (v = 0 is the target's
        // first row, which is the top of the clip space where the uv starts at the top).
        v2f vertProc(uint vid : SV_VertexID)
        {
            v2f o;
            float2 p = float2((vid << 1) & 2, vid & 2);
            o.pos = float4(p * 2.0 - 1.0, UNITY_NEAR_CLIP_VALUE, 1.0);
        #if UNITY_UV_STARTS_AT_TOP
            o.uv = float2(p.x, 1.0 - p.y);
        #else
            o.uv = p;
        #endif
            return o;
        }

        struct MarchTargets
        {
            float4 colour : SV_Target0;
            float4 dist : SV_Target1;
        };

        MarchTargets Pass0Targets(v2f i)
        {
            MarchTargets m;
            float dist;
            m.colour = Pass0D(i, dist);
            m.dist = float4(dist, 0.0, 0.0, 0.0);
            return m;
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
            float tIn = 0.0, tOut = 0.0;
            if (DEV(_LimitP.w, 1.0) > 0.5) CloudWindow(_CamPos.xyz - _PlanetCenter.xyz, d0, tIn, tOut);
            float dist0 = DEV(_LimitP.w, 1.0) > 0.5 ? CompositeDistance(i.uv, tIn, tOut) : 0;
            float4 sum = 0;
            float wsum = 0;
            [unroll] for (int k = 0; k < 4; k++)
            {
                float2 o = float2(k & 1, k >> 1);
                float2 uvc = (base + o + 0.5) * texel;
                float w = (o.x > 0.5 ? f.x : 1.0 - f.x) * (o.y > 0.5 ? f.y : 1.0 - f.y);
                if (DEV(_LimitP.w, 1.0) > 0.5)
                {
                    float dc = CompositeDistance(uvc, tIn, tOut);
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
            if (DEV(_OutP.w, 1.0) < 0.5 || DEV(_LimitP.y, 0.0) > 0.5) return c;
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
