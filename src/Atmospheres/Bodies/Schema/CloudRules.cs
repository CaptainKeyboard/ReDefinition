using System;
using System.Collections.Generic;

namespace ReDefinition.Atmospheres
{
    // The layer model of the own cloud system (package T01). The class CloudRules of the CLOUDS node (which source renders, how EVE's
    // layers are found) stays in LookRules.cs; this file holds what a planet-wide layer shell is made of: how a layer is lit and
    // marched (LayerKind), a cloud type (a vertical density profile between a base and a top), a layer (a shell between two altitudes
    // with a type table and two maps) and the read model other packages use (ICloudLayerSource: cloud shadows, far view, the aerial
    // perspective of the integration and the raytracing pack).

    // How a layer is marched and lit.
    internal enum LayerKind
    {
        // Lit by the sun (a short march towards it) and by the sky: the clouds.
        Weather = 0,
        // Lit by the sky and one sun tap, no march towards the sun: thin high layers and flat decks that need no self-shadow.
        Cheap = 1,
        // A fog layer on the ground: lit by the sky and a fixed share of the sun, no march towards the sun.
        Fog = 2,
        // Shines by itself (a colour times the density): no light at all is marched.
        Emissive = 3
    }

    // One cloud type of a layer's type table. Heights are shares of the layer's thickness (0 at the layer's inner altitude, 1 at the
    // outer); the profile is the density over the type's own height (0 at its base, 1 at its top), 0 to 1 at full coverage.
    internal sealed class CloudTypeDef
    {
        internal CloudTypeDef(string name, float baseShare, float topShare, Curve profile, float erosion = 0.3f)
        {
            if (profile == null) throw new ArgumentNullException("profile");
            if (!(topShare > baseShare) || baseShare < 0f || topShare > 1f) throw new ArgumentException("A type needs 0 <= base < top <= 1.");
            Name = name ?? "";
            BaseShare = baseShare;
            TopShare = topShare;
            Profile = profile;
            Erosion = erosion < 0f ? 0f : erosion > 1f ? 1f : erosion;
        }

        internal string Name { get; }
        internal float BaseShare { get; }
        internal float TopShare { get; }
        // Density over the type's own height, x from 0 to 1.
        internal Curve Profile { get; }
        // How far the detail noise eats into the type, 0 to 1 (the table's erodeness).
        internal float Erosion { get; }
        // The soft water content's numbers of the type (E40 soft density, cfg keys baseGain, riseShare, densityScale): the gain of the density at the type's base and the
        // share of its height over which it rises to 1 (0: no rise), and a factor on the whole density. All three are baked into the type's tabulated profile
        // (LayerTypeTable), so the shader needs no code for them. The defaults are the values measured in step (a); densityScale 1 changes nothing.
        internal float BaseGain { get; set; } = 0.3f;
        internal float RiseShare { get; set; } = 0.5f;
        internal float DensityScale { get; set; } = 1f;

        // The type's light response by height (E40 B3; cfg keys baseLight, headLight, baseAmbient, headAmbient): factors on the direct light and on the sky's fill at the type's base
        // and at its top, smoothly between (1: the global fit). A cumulus gets a shaded flat base and bright hard-lit heads, a stratus a flat dark base. Baked into the type's
        // tabulated profile texture (channels g and b, LayerTypeTable.LightAt), read by the layer shader at the sample.
        internal float BaseLight { get; set; } = 1f;
        internal float HeadLight { get; set; } = 1f;
        internal float BaseAmbient { get; set; } = 1f;
        internal float HeadAmbient { get; set; } = 1f;

        // The type's height-shaped threshold (E40 L2 D1; cfg node LIFT { key0 = height lift, ... }, null: none): at the height h of the type (0 base, 1 top) the coverage threshold is lifted by
        // lift(h) in map units, so the cloud's outline is narrower there than where the lift is 0 (Schneider's height gradient, as data per type). A cumulus lifts towards the top (a dome over
        // a flat base), a cumulonimbus lifts its middle and lets the top stand wide (the anvil). Never negative: the lift only lowers the map value, so the coverage bounds of the march
        // and of the layer drop (LayerSpanBound.CoverageEmpty) stay exact. Baked into the type's tabulated profile texture (channel a, LayerTypeTable.LiftData).
        internal Curve Lift { get; set; }

        internal double LiftAt(double h)
        {
            return Lift == null ? 0.0 : Lift.Evaluate((float)(h <= 0.0 ? 0.0 : h >= 1.0 ? 1.0 : h));
        }

        // The two factors (direct light, sky fill) at a height share h of the type.
        internal void LightFactors(double h, out double direct, out double ambient)
        {
            double x = h <= 0.0 ? 0.0 : h >= 1.0 ? 1.0 : h;
            double s = x * x * (3.0 - 2.0 * x);
            direct = BaseLight + (HeadLight - BaseLight) * s;
            ambient = BaseAmbient + (HeadAmbient - BaseAmbient) * s;
        }

        // The factor the type's profile is multiplied by at a height share h of the type (0 base, 1 top): its density scale, with the rise by height where the soft density is on.
        internal double ProfileFactor(double h, bool soft)
        {
            double f = DensityScale;
            if (soft) f *= CloudWater.Rise(h, new CloudWaterNumbers { Width = 0.5, Power = 2.0, BaseGain = BaseGain, RiseShare = RiseShare });
            return f;
        }

        // A type with a constant density between its base and its top: the type of the analytic test scenes.
        internal static CloudTypeDef Slab(string name, float baseShare, float topShare)
        {
            return new CloudTypeDef(name, baseShare, topShare, new Curve(new[] { 0f, 1f }, new[] { 1f, 1f }), 0f);
        }
    }

    // One layer: a shell around the body between two altitudes above sea level, with a table of types and the two maps that choose
    // where it is and which type is there.
    internal sealed class CloudLayerDef
    {
        // The most layers one pass draws, and the most types one table holds.
        internal const int MaxLayers = 4;
        internal const int MaxTypes = 8;

        internal CloudLayerDef(string name, int bodyIndex, float innerAltitudeM, float outerAltitudeM, IList<CloudTypeDef> types,
                               float sigmaPerM, LayerKind kind = LayerKind.Weather)
        {
            if (!(outerAltitudeM > innerAltitudeM)) throw new ArgumentException("A layer needs inner < outer.");
            if (types == null || types.Count == 0 || types.Count > MaxTypes) throw new ArgumentException("A layer needs 1 to " + MaxTypes + " types.");
            Name = name ?? "";
            BodyIndex = bodyIndex;
            InnerAltitudeM = innerAltitudeM;
            OuterAltitudeM = outerAltitudeM;
            Types = new List<CloudTypeDef>(types).AsReadOnly();
            SigmaPerM = sigmaPerM;
            Kind = kind;
            Coverage = 1f;
            EdgeSoftness = 0.15f;
            FadeFarM = float.PositiveInfinity;
            ShadowStrength = 1f;
            CoverageMapHandle = -1;
            TypeMapHandle = -1;
        }

        internal string Name { get; }
        internal int BodyIndex { get; }
        // The shell's inner and outer altitude above sea level, m.
        internal float InnerAltitudeM { get; }
        internal float OuterAltitudeM { get; }
        internal float ThicknessM { get { return OuterAltitudeM - InnerAltitudeM; } }
        internal IList<CloudTypeDef> Types { get; }
        // The extinction at full density, per m (a vertical optical depth of sigma times the type's height at profile 1).
        internal float SigmaPerM { get; }
        internal LayerKind Kind { get; }

        // The share of the map that clouds: a column is cloud where the coverage map is above 1 - Coverage (a ranked map gives the
        // share Coverage of the body), 0 to 1.
        internal float Coverage { get; set; }
        // The group of the layer's spec (members of a group never show together), or empty; the layer-selective coverage shift acts on one group.
        internal string Group { get; set; } = "";
        // The threshold the readers use: Coverage with the runtime shift (CloudCoverShift; tuning cloud.coverageShift, default 0 = Coverage). Read this, never Coverage.
        // Also the layer's own shift (CloudLookTuning, cloud.layerCoverageShift.<name>), default 0.
        // With the soft density on (CloudSoftDensity, the default) and a noise amount above 0 this is the coverage constant of the soft water content (see Soft below).
        internal float EffectiveCoverage
        {
            get
            {
                if (!Soft) return ThresholdCoverage;
                double w = SoftWidth, a = SoftAmount;
                double c = CloudSoftDensity.CoverMeaning ? 0.5 + (ThresholdCoverage - Coverage) + (SoftClosed - 0.5) * w
                                                          : ThresholdCoverage + 0.5 * OldEdge + (SoftClosed - 0.5) * a * w;
                return (float)c;
            }
        }
        // The old threshold: Coverage with the runtime shift, the share of the soft edge's centre (the outline of the old density).
        internal float ThresholdCoverage
        {
            get
            {
                Look();
                float c = CloudCoverShift.Apply(Coverage, Group);
                return lookShift == 0f ? c : Math.Max(0f, Math.Min(1f, c + lookShift));
            }
        }
        // The numbers of the layer with the look knobs applied (CloudLookTuning; every default is a factor of 1 or no change): the readers use these, never the configured ones.
        internal float EffectiveSigmaPerM { get { Look(); return lookSigma == 1f ? SigmaPerM : SigmaPerM * lookSigma; } }
        internal float EffectiveEdgeSoftness { get { return Soft ? (float)(SoftAmount * SoftWidth) : OldEdge; } }
        internal float EffectiveNoiseAmount { get { return Soft ? (float)SoftAmount : ConfiguredNoiseAmount; } }
        private float OldEdge { get { Look(); return lookSoftness == 1f ? EdgeSoftness : EdgeSoftness * lookSoftness; } }
        private float ConfiguredNoiseAmount { get { Look(); return lookNoiseAmount == 1f ? NoiseAmount : NoiseAmount * lookNoiseAmount; } }

        // ---- The soft water content (E40 soft density, package B1): ONE place that turns the layer's cfg numbers into the numbers every reader uses. ----
        // The layer shader, the cloud shadow and every CPU twin evaluate the share saturate((map + (n - 0.5) A + cov - 1) / edge + 0.5), eroded, to a power. With
        //   A = the layer's noise amount, w = the owner's softness x SoftnessScale, edge = A w, cov = C + 0.5 e + (closed - 0.5) A w
        // (C the threshold coverage, e the old edge softness, closed the water content at which a column of the layer's types is just closed), the edge hardness off and the exponent
        // WaterPower this IS CloudWater's water content whose outline is where the old share began (CloudWaterTests). The thin water below the outline must not appear where the old
        // cloud did not exist: the map value is moved before the share, map' = min(map, 2 map - K) (ClearSkyMap), K = m0 + closed A w with m0 = 1 - C - 0.5 e - 0.5 A.
        // Where the layer has no noise (A = 0) it keeps the old density.
        internal bool Soft { get { return CloudSoftDensity.Enabled && ConfiguredNoiseAmount > 0f; } }
        // The factor on the owner's softness: the layer's cfg key softnessScale (a weather deck softer, a cirrus sheet softest).
        internal float SoftnessScale { get; set; } = 1f;
        // The exponent of the water content in the density: the layer's cfg key waterPower (2: the density grows with the square of the water; 1: thin sheets have no core).
        internal float WaterPower { get; set; } = 2f;
        // The layer's light response (cfg keys lightMs, lightPowder, lightAmbient; 1 = the global fit): factors on the multiple-scattering brightness, the powder term and the sky's
        // fill. CloudLightSettings.LayerScale carries them to the shader's _LightL.
        internal float LightMs { get; set; } = 1f;
        internal float LightPowder { get; set; } = 1f;
        internal float LightAmbient { get; set; } = 1f;
        // The type value of the layer at full precipitation (cfg key rainType, 1: the last type of the table; see CloudLookTuning.TypeValue).
        internal float RainType { get; set; } = 1f;
        // How much longer the layer's noise features are along the planet's axis than across it (cfg key fibreStretch, 1 and above; 1: isotropic): the lookup is compressed along the
        // axis by 1 / FibreStretch (E40 L2 D1; Noise.cginc NoiseLookup). Cirrus fibres; the axis is the body's pole, which is horizontal at the equator and tilts into the vertical
        // towards the poles (where the stretch then lies in the vertical and does not show in a thin layer).
        internal float FibreStretch { get; set; } = 1f;
        // The mid-level sheet's rule (E40 L2 D1; cfg keys altoType, altoFrom, altoFullAt): AltoType is the type value of the table's mid-level sheet (an altocumulus), -1: none. Where the
        // generator makes more cloud at that type's height than at the fair-weather type's height (the difference of the two layer maps, from AltoFrom to AltoFullAt), the type map
        // shows the sheet instead of the precipitation's type (CloudLookTuning.ApplyAlto).
        internal float AltoType { get; set; } = -1f;
        internal float AltoFrom { get; set; } = 0.05f;
        internal float AltoFull { get; set; } = 0.3f;

        // The height above sea level of the middle of the type at a type value (the type at the nearest index of the table), m.
        internal double TypeMidHeightM(float typeValue)
        {
            int index = Types.Count <= 1 ? 0 : (int)Math.Round(Math.Max(0f, Math.Min(1f, typeValue)) * (Types.Count - 1));
            CloudTypeDef t = Types[index];
            return InnerAltitudeM + 0.5 * (t.BaseShare + t.TopShare) * ThicknessM;
        }

        private double SoftAmount { get { return CloudSoftDensity.CoverMeaning ? 1.0 : ConfiguredNoiseAmount; } }
        // The width of the water content's rise in noise rank.
        private double SoftWidth { get { return Math.Max(0.02, CloudSoftDensity.Softness * SoftnessScale); } }

        // The exponent a reader applies to the eroded share (1 where the layer keeps the old density).
        internal float EffectiveWaterPower { get { return Soft ? WaterPower : 1f; } }

        // The water content at the outline: the mean over the layer's types of CloudWater.Closed (the types of one layer differ by under 0.02: MEASURED, E40 step (a)).
        internal double SoftClosed
        {
            get
            {
                Look();
                long stamp = CloudCoverShift.Stamp;
                // The key also holds the numbers a test may change after the first read (the types' data, the exponent).
                double key = lookSigma + WaterPower * 7.0;
                for (int i = 0; i < Types.Count; i++) key += Types[i].DensityScale * 3.1 + Types[i].BaseGain * 5.3 + Types[i].RiseShare * 11.7 + Types[i].Erosion * 13.9;
                if (closedStamp == stamp && closedKey == key) return closedValue;
                double sum = 0.0;
                for (int i = 0; i < Types.Count; i++)
                {
                    CloudTypeDef type = Types[i];
                    CloudWaterNumbers k = new CloudWaterNumbers { Width = SoftWidth, Power = WaterPower, BaseGain = type.BaseGain, RiseShare = type.RiseShare };
                    CloudTypeDef t = type;
                    double mean = CloudWater.ColumnMean(h => t.Profile.Evaluate((float)h), k, 128);
                    double tau = EffectiveSigmaPerM * (type.TopShare - type.BaseShare) * ThicknessM * mean * type.DensityScale;
                    sum += CloudWater.Closed(tau, type.Erosion, CloudSoftDensity.DetailMean, k);
                }
                closedValue = sum / Types.Count;
                closedKey = key;
                closedStamp = stamp;
                return closedValue;
            }
        }
        private double closedValue;
        private long closedStamp = -1;
        private double closedKey = double.NaN;

        // The least map value the layer's clear-sky rule keeps: 0 where the rule is off. See ClearSkyMap.
        internal float ClearSkyK
        {
            get
            {
                if (!Soft) return 0f;
                double w = SoftWidth, a = SoftAmount, k;
                if (CloudSoftDensity.CoverMeaning) k = SoftClosed * w - (ThresholdCoverage - Coverage);
                else k = (1.0 - ThresholdCoverage - 0.5 * OldEdge - 0.5 * a) + SoftClosed * a * w;
                return (float)Math.Max(0.0, k);
            }
        }

        // The coverage map value as the density reads it: the thin water below the outline does not appear where the old cloud did not exist (the shader's one line is the same).
        internal float ClearSkyMap(float map)
        {
            return ClearSky(map, ClearSkyK);
        }

        // The same in double precision (the CPU readers keep the map value as they have it where the rule is off).
        internal double ClearSkyMap(double map)
        {
            float k = ClearSkyK;
            return k > 0f ? Math.Min(map, 2.0 * map - k) : map;
        }

        // The same for a given K (the baked entries of the raytracing pack keep K, not the layer).
        internal static float ClearSky(float map, float k)
        {
            return k > 0f ? Math.Min(map, 2f * map - k) : map;
        }
        internal float EffectiveNoiseScaleM { get { Look(); return lookNoiseSize == 1f ? NoiseScaleM : NoiseScaleM * lookNoiseSize; } }
        // The distance fade: the layer's own where it has one, else the global one of cloud.fadeFarM (none by default).
        internal float EffectiveFadeNearM { get { Look(); return float.IsPositiveInfinity(FadeFarM) && lookFade ? lookFadeNear : FadeNearM; } }
        internal float EffectiveFadeFarM { get { Look(); return float.IsPositiveInfinity(FadeFarM) && lookFade ? lookFadeFar : FadeFarM; } }

        // The look knobs of this layer (CloudLookTuning), read again when the tuning table changed (CloudCoverShift.Stamp rises with it): the CPU readers ask these getters for every
        // sample, so the by-name lookups are done once per change, not once per call. The configured numbers are multiplied at the call, so a test that sets them still sees them.
        private long lookStamp = -1;
        private float lookShift, lookSigma = 1f, lookSoftness = 1f, lookNoiseAmount = 1f, lookNoiseSize = 1f, lookFadeNear, lookFadeFar;
        private bool lookFade;

        private void Look()
        {
            long stamp = CloudCoverShift.Stamp;
            if (stamp == System.Threading.Volatile.Read(ref lookStamp)) return;
            lookShift = CloudLookTuning.ShiftOf(Name);
            lookSigma = CloudLookTuning.SigmaScaleOf(Name);
            lookSoftness = CloudLookTuning.SoftnessScaleOf(Name);
            lookNoiseAmount = CloudLookTuning.NoiseAmountScaleOf(Name);
            lookNoiseSize = CloudLookTuning.NoiseSizeScaleOf(Name);
            lookFade = CloudLookTuning.FadeFor(out lookFadeNear, out lookFadeFar);
            System.Threading.Volatile.Write(ref lookStamp, stamp);
        }
        // The width of the coverage threshold in map units: 0 is a hard edge, larger is softer (P12).
        internal float EdgeSoftness { get; set; }
        // The distance range over which the layer's density fades to zero, m (P64); Infinity: no fade.
        internal float FadeNearM { get; set; }
        internal float FadeFarM { get; set; }
        // How strongly the layer shades the ground and the other layers, 0 to 1 (read by the cloud shadows).
        internal float ShadowStrength { get; set; }
        // Handles of the coverage and the type map in the owner's map store (the painter's R8 targets); -1: no map (coverage 1 and the
        // first type everywhere).
        internal int CoverageMapHandle { get; set; }
        internal int TypeMapHandle { get; set; }
        // The layer's noise: the share of the coverage threshold the noise moves, its scale in m, and the erosion at the cloud's edge.
        internal float NoiseAmount { get; set; }
        internal float NoiseScaleM { get; set; }
        // The type value where no precipitation falls (CloudLayerSpec.FairType; -1: the precipitation level itself). The tuning key cloud.typeFair.<name> goes before it.
        internal float FairType { get; set; } = -1f;
        // What may draw the layer (WeatherDeckRule); null: the layer is drawn wherever its coverage map passes. The tuning key cloud.deckRule.<name> switches it off (0) or on (1).
        internal WeatherDeckRule Deck { get; set; }
        // The longitude the layer's maps are turned by against the body's frame, degrees (the layer turns with its wind).
        internal float MapShiftDeg { get; set; }

        // The smallest base and the largest top among the types, as altitudes: the interval the march needs (a tight interval).
        internal void TightAltitudes(out float tightInnerM, out float tightOuterM)
        {
            TableAltitudes(out tightInnerM, out tightOuterM);
            // The height variation (package T13) moves bases down and tops up by at most these many m: the interval widens by them, and
            // never reaches into the ground sphere of a layer that did not start there.
            if (HeightDownM > 0f || HeightUpM > 0f)
            {
                tightInnerM = tightInnerM > 0f ? Math.Max(0.5f, tightInnerM - HeightDownM) : 0f;
                tightOuterM += HeightUpM;
            }
        }

        // The same interval without the height variation's reach: the smallest base and the largest top of the type table (the cloud's own extent).
        internal void TableAltitudes(out float innerM, out float outerM)
        {
            float lo = 1f, hi = 0f;
            for (int i = 0; i < Types.Count; i++)
            {
                if (Types[i].BaseShare < lo) lo = Types[i].BaseShare;
                if (Types[i].TopShare > hi) hi = Types[i].TopShare;
            }
            innerM = InnerAltitudeM + lo * ThicknessM;
            outerM = InnerAltitudeM + hi * ThicknessM;
        }

        // The most the height variation lowers a base and raises a top beyond the type table's, in m (package T13; 0: none).
        internal float HeightDownM { get; set; }
        internal float HeightUpM { get; set; }
    }

    // The read model of the layer stack of the body the camera is at. T04 (cloud shadows), T06 (far view), T09 (integration) and the
    // raytracing pack read it; they never see the pass.
    internal interface ICloudLayerSource
    {
        // The body the layers belong to, its sea-level radius in m, and whether it has a surface (a gas giant has none: a ray that
        // enters the inner shell comes out on the far side).
        int BodyIndex { get; }
        double BodyRadiusM { get; }
        bool HasSurface { get; }
        // The layers from the lowest to the highest, and a counter that changes whenever any of them (or a map) changes.
        int LayerCount { get; }
        CloudLayerDef LayerAt(int index);
        long Version { get; }
        // The two map values of a layer at a place, 0 to 1 (the coverage map and the type map, with the layer's longitude shift
        // applied), for the CPU consumers; 0 and 0 where the layer has no map.
        void SampleMaps(int layerIndex, double latDeg, double lonDeg, out float coverage, out float type);
    }
}
