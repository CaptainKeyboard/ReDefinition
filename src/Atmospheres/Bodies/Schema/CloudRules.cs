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
        internal float EffectiveCoverage
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
        internal float EffectiveEdgeSoftness { get { Look(); return lookSoftness == 1f ? EdgeSoftness : EdgeSoftness * lookSoftness; } }
        internal float EffectiveNoiseAmount { get { Look(); return lookNoiseAmount == 1f ? NoiseAmount : NoiseAmount * lookNoiseAmount; } }
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
