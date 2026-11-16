using System;
using System.Collections.Generic;

namespace ReDefinition.Atmospheres.Viewer
{
    // When a reader takes a tuning key's value, as the code reads it (the cloud viewer shows it next to each key).
    internal enum KeyLiveness
    {
        // Read in every frame the pass draws (OwnCloudModule.FillFrame, OwnCloudOptions.Apply, Record): a change shows in the next frame.
        Frame,
        // Read every frame, and the pass keeps a history the change makes stale: the viewer asks for a history reset when it changes the key.
        FrameHistory,
        // Read again when the tuning table changes (CloudLookTuning, CloudCoverShift, CloudSoftDensity): the layers' numbers are uploaded again and the maps fetched
        // again at once, so the change shows within a frame or a map round.
        Change
    }

    // One tuning key of the cloud pass: the key (the constant of the class that reads it), its compiled default, when it is read and a short note.
    internal sealed class CloudKey
    {
        internal CloudKey(string key, float compiledDefault, KeyLiveness liveness, bool flag, string note)
        {
            Key = key;
            Default = compiledDefault;
            Liveness = liveness;
            Flag = flag;
            Note = note ?? "";
        }

        internal string Key { get; }
        internal float Default { get; }
        internal KeyLiveness Liveness { get; }
        // A switch (Tuning.Flag: other than 0 is on).
        internal bool Flag { get; }
        internal string Note { get; }

        // The first part of the key (cloud, layers, light, cull, recon, shadow).
        internal string Group
        {
            get
            {
                int dot = Key.IndexOf('.');
                return dot > 0 ? Key.Substring(0, dot) : Key;
            }
        }
    }

    // A key that exists once per cloud layer: the prefix and the layer's name (cloud.mapOverride.<name>).
    internal sealed class CloudLayerKey
    {
        internal CloudLayerKey(string prefix, float compiledDefault, string note)
        {
            Prefix = prefix;
            Default = compiledDefault;
            Note = note;
        }

        internal string Prefix { get; }
        internal float Default { get; }
        internal string Note { get; }
    }

    // Every tuning key of the own cloud pass (cloud.*, layers.*, light.*, cull.*, recon.*, shadow.*), each named by the constant its reader declares and with the
    // compiled default that reader uses, so the viewer lists, edits and resets the keys without a second copy of either. Where a reader passes a literal default
    // the literal is repeated here; CloudKeyRegistryTests checks that every key the source reads is listed.
    internal static class CloudKeyRegistry
    {
        private static readonly CloudLightSettings Light = new CloudLightSettings();
        private static readonly LayerFrame Frame0 = new LayerFrame();
        private static readonly CullSettings Cull = new CullSettings();

        private static CloudKey N(string key, float d, KeyLiveness live = KeyLiveness.Frame, string note = "")
        {
            return new CloudKey(key, d, live, false, note);
        }

        private static CloudKey F(string key, bool d, KeyLiveness live = KeyLiveness.Frame, string note = "")
        {
            return new CloudKey(key, d ? 1f : 0f, live, true, note);
        }

        internal static readonly IReadOnlyList<CloudKey> All = new List<CloudKey>
        {
            // The output stage, the sun and the moons (OwnCloudModule.FillFrame).
            N(OwnCloudModule.TuneSunGain, 1f, note: "factor on the pass's sun"),
            N(OwnCloudModule.TuneSunUnit, CloudOutputStage.SunUnitDefault, note: "radiometric unit of the sun"),
            N(OwnCloudModule.TuneExposure, CloudOutputStage.ExposureDefault),
            N(OwnCloudModule.TuneOutputGamma, CloudOutputStage.GammaDefault),
            N(OwnCloudModule.TuneWhitePoint, 1f, note: "white point follows the sun, 0 to 1"),
            N(OwnCloudModule.TuneWhiteAltitudeM, -1f, note: "-1: lowest layer"),
            N(OwnCloudModule.TuneMoonGain, 1f),
            F(OwnCloudModule.TuneSunAirWithScatterer, false),
            F(OwnCloudModule.TuneAfterScatterer, true, note: "read before each render"),
            N(OwnCloudModule.TuneCameraEvent, 0f, note: "0 BeforeForwardAlpha, 1 AfterForwardAlpha, 2 BeforeImageEffects; buffers move"),
            F(OwnCloudModule.TuneReaddEveryFrame, false),
            N(OwnCloudModule.TuneStatsS, 30f, note: "seconds between statistics lines"),
            N(CloudMotion.TuneScale, CloudMotion.DefaultScale, note: "cloud speed, 0 to 50"),
            // The haze (OwnCloudModule.FillFrame, ResolveAir).
            N(CloudHazePlan.TuneAirSource, -1f, note: "-1 auto, re-read when it changes"),
            N(CloudHazePlan.TuneColour, 0f),
            N(CloudHazePlan.TuneShare, CloudBridgeAir.HorizonShare),
            N(CloudHazePlan.TuneR, CloudHazePlan.DefaultDisplayColour.r),
            N(CloudHazePlan.TuneG, CloudHazePlan.DefaultDisplayColour.g),
            N(CloudHazePlan.TuneB, CloudHazePlan.DefaultDisplayColour.b),
            N(CloudHazePlan.TuneSky, 1f),
            // The look knobs (CloudLookTuning, CloudCoverShift, CloudSoftDensity).
            N(CloudLookTuning.TuneSigma, 1f, KeyLiveness.Change),
            N(CloudLookTuning.TuneSoftness, 1f, KeyLiveness.Change),
            N(CloudLookTuning.TuneNoiseAmount, 1f, KeyLiveness.Change),
            N(CloudLookTuning.TuneNoiseSize, 1f, KeyLiveness.Change),
            N(CloudLookTuning.TuneDensityRemap, 0f, KeyLiveness.Change),
            N(CloudLookTuning.TuneCoverCombine, CloudLookTuning.DefaultCoverCombine, KeyLiveness.Change, "a map round follows"),
            N(CloudLookTuning.TuneFadeFar, 0f, KeyLiveness.Change),
            N(CloudLookTuning.TuneFadeNearShare, 0.5f, KeyLiveness.Change),
            N(CloudLookTuning.TuneFarHaze, -1f, KeyLiveness.Change, "-1 setting, 0 off, 1 on, 2 auto"),
            N(CloudLookTuning.TuneHazeStrength, 1f, KeyLiveness.Change),
            N(CloudLookTuning.TuneSoloLayer, 0f, KeyLiveness.FrameHistory, "solo: the 1-based layer drawn alone, 0 every layer"),
            N(CloudLookTuning.TuneCellKm, 0f, KeyLiveness.Change, "a forced map as one cell: e-folding radius in km, 0 a constant map"),
            N(CloudLookTuning.TuneCellLat, 0f, KeyLiveness.Change, "centre of the forced cell, degrees"),
            N(CloudLookTuning.TuneCellLon, 0f, KeyLiveness.Change, "centre of the forced cell, degrees"),
            N(CloudCoverShift.TuneShift, 0f, KeyLiveness.Change),
            N(CloudCoverShift.TuneShiftRain, 0f, KeyLiveness.Change),
            F(CloudSoftDensity.TuneCoverMeaning, CloudSoftDensity.DefaultCoverMeaning, KeyLiveness.Change),
            // The billow detail (CloudLookTuning; read in LayerNoise.Bind every frame).
            N(CloudLookTuning.TuneBillowLight, 0f, KeyLiveness.FrameHistory, "billow shading of the direct light, 0 off"),
            N(CloudLookTuning.TuneBillowContrast, 1f, KeyLiveness.FrameHistory, "contrast of the erosion detail about its mean"),
            N(CloudLookTuning.TuneBillowTop, 0f, KeyLiveness.FrameHistory, "erosion gain toward the cloud's top"),
            N(CloudLookTuning.TuneBillowFine, -1f, KeyLiveness.FrameHistory, "weight of the fine octave, -1 compiled"),
            N(CloudLookTuning.TuneBillowFineFreq, -1f, KeyLiveness.FrameHistory, "frequency of the fine octave, -1 compiled"),
            N(CloudLookTuning.TuneBillowSharpen, 0f, KeyLiveness.FrameHistory, "spline upsampling of the march target, 0 bilinear"),
            // CLOUDR1: the heap volume, the exponent of the density rim and the cavity (CloudLookTuning; the per-type cfg keys heap, edgePower, cavity are overridden for every type), the first hit.
            N(CloudLookTuning.TuneHeap, -1f, KeyLiveness.FrameHistory, "heap preset for every type: -1 each type's key, 0 none, 1 billow, 2 rough"),
            N(CloudLookTuning.TuneEdgePower, 0f, KeyLiveness.FrameHistory, "exponent of the density rim for every type, 0 each type's key"),
            N(CloudLookTuning.TuneCavity, -1f, KeyLiveness.FrameHistory, "cavity strength for every heap type, -1 each type's key"),
            F(CloudLookTuning.TuneHeapFootLod, true, KeyLiveness.FrameHistory),
            F(CloudLookTuning.TuneHeapIso, true, KeyLiveness.FrameHistory),
            N(CloudLookTuning.TuneHeapWarp, 0.5f, KeyLiveness.FrameHistory, "share of the untiling displacement's horizontal part the heap keeps, -1 the whole displacement"),
            F(CloudLookTuning.TuneNoiseBlend, false, KeyLiveness.FrameHistory),
            N(CloudLookTuning.TuneNoiseBlendExp, 8f, KeyLiveness.FrameHistory, "exponent of the blend weights"),
            N(CloudLookTuning.TuneNoiseBlendCell, 1f, KeyLiveness.FrameHistory, "blend lattice cell in periods"),
            N(CloudLookTuning.TuneHeapDetail, 0f, KeyLiveness.Change, "Hurst exponent of the erosion detail octaves, 0 compiled; acts when the volumes are generated"),
            N(CloudLookTuning.TuneFirstHit, 0f, KeyLiveness.FrameHistory, "bisection evaluations on the entered span, 0 off"),
            N(CloudLookTuning.TuneFirstHitThin, 0.25f, KeyLiveness.FrameHistory, "first steps inside as a share of the grid step"),
            N(CloudLookTuning.TuneFirstHitShell, 32f, KeyLiveness.FrameHistory, "thin steps after the entry (the outer shell)"),
            N(CloudLookTuning.TuneFirstHitNear, 0f, KeyLiveness.FrameHistory, "distance in m up to which the thin steps are taken, 0 everywhere"),
            // The march's cost controls (OwnCloudOptions.Apply(LayerFrame)).
            F(OwnCloudOptions.TuneCoverageBound, Frame0.CoverageBound),
            F(OwnCloudOptions.TuneEmptyReach, OwnCloudOptions.EmptyReachDefault),
            N(OwnCloudOptions.TuneSpanSpacing, OwnCloudOptions.SpanSpacingDefault),
            N(OwnCloudOptions.TuneSpanSpacingMin, OwnCloudOptions.SpanSpacingMinDefault),
            N(OwnCloudOptions.TuneLeapScale, OwnCloudOptions.LeapScaleDefault),
            N(OwnCloudOptions.TuneWarpTable, OwnCloudOptions.WarpTableDefault, note: "a table bake follows"),
            N(OwnCloudOptions.TuneLightStride, Frame0.LightStride),
            N(OwnCloudOptions.TuneLightCutoff, Frame0.LightCutoff),
            N(OwnCloudOptions.TuneEmptyLeap, Frame0.EmptyLeap),
            N(OwnCloudOptions.TuneCheapShare, Frame0.CheapShare),
            N(OwnCloudOptions.TuneFogShare, Frame0.FogShare),
            N(OwnCloudOptions.TuneGridFine, OwnCloudOptions.GridFineDefault),
            F(OwnCloudOptions.TuneSpanTight, OwnCloudOptions.SpanTightDefault),
            N(OwnCloudOptions.TuneDitherSequence, OwnCloudOptions.DitherSequenceDefault, KeyLiveness.FrameHistory, "1 hash, 2 gradient, 3 blue noise"),
            F(OwnCloudOptions.TuneDitherFix, OwnCloudOptions.DitherFixDefault, KeyLiveness.FrameHistory),
            // The reconstruction (OwnCloudOptions.Apply(ReconSettings), LayerReconstructDriver.Configure).
            N(OwnCloudOptions.TuneAccumulate, OwnCloudOptions.AccumulateDefault, KeyLiveness.FrameHistory),
            N(OwnCloudOptions.TuneReconResolution, OwnCloudOptions.ReconResolutionDefault, KeyLiveness.FrameHistory),
            F(OwnCloudOptions.TuneLayerMotion, OwnCloudOptions.LayerMotionDefault, KeyLiveness.FrameHistory, "cloud motion in the history and the motion vectors"),
            F(OwnCloudOptions.TuneMotionVectors, OwnCloudOptions.MotionVectorsDefault, note: "motion vectors for the upscalers"),
            F(OwnCloudOptions.TuneMotionAudit, OwnCloudOptions.MotionAuditDefault, note: "logs the vectors' error"),
            F(EdgeSettings.TuneReconstructor, EdgeSettings.ReconstructorDefault, KeyLiveness.FrameHistory),
            F(EdgeSettings.TuneEdgeAdaptive, EdgeSettings.AdaptiveDefault, KeyLiveness.FrameHistory),
            N(EdgeSettings.TuneThreshold, EdgeSettings.ThresholdDefault),
            N(EdgeSettings.TuneDilation, EdgeSettings.DilationDefault),
            N(EdgeSettings.TunePersist, EdgeSettings.PersistDefault),
            // The light (OwnCloudOptions.Apply(CloudLightSettings), LightWriter).
            N(OwnCloudOptions.TuneMoonMinShare, OwnCloudOptions.MoonMinShareDefault),
            N(OwnCloudOptions.TuneOctaveA, Light.OctaveA),
            N(OwnCloudOptions.TuneOctaveB, Light.OctaveB),
            N(OwnCloudOptions.TuneOctaveC, Light.OctaveC),
            N(OwnCloudOptions.TuneMsBrightness, Light.MsBrightness),
            N(OwnCloudOptions.TuneDiffusion, Light.Diffusion),
            N(OwnCloudOptions.TuneDiffusionTau, Light.DiffusionTau),
            N(OwnCloudOptions.TuneMsIsotropy, Light.MsIsotropy),
            N(OwnCloudOptions.TuneAmbientOcclusion, Light.AmbientOcclusion),
            N(OwnCloudOptions.TuneAmbientGain, Light.AmbientGain),
            N(OwnCloudOptions.TuneSkyTint, Light.SkyTint),
            N(OwnCloudOptions.TuneAmbientFloor, Light.AmbientFloor),
            F(OwnCloudOptions.TuneAmbientColumnTop, Light.AmbientColumnToTypeTop),
            N(OwnCloudOptions.TuneCellGain, Light.CellGain),
            N(OwnCloudOptions.TuneCellBeta, Light.CellBeta),
            N(OwnCloudOptions.TuneCellExtrapolation, Light.CellExtrapolation),
            N(OwnCloudOptions.TuneCellSide, Light.CellSide),
            N(OwnCloudOptions.TuneCellOctaves, Light.CellOctaves),
            N(OwnCloudOptions.TuneCellIncidencePower, Light.CellIncidencePower),
            N(OwnCloudOptions.TuneCellIncidenceRef, Light.CellIncidenceRef),
            N(OwnCloudOptions.TuneCellIncidenceEps, Light.CellIncidenceEps),
            N(LightWriter.TuneSunDimWeight, LightWriter.SunDimWeightDefault, note: "sun dimming by the clouds"),
            N(LightWriter.TuneSunDimWeightEve, LightWriter.SunDimWeightEveDefault, note: "the same with an EVE layer"),
            // The culling and the distance LOD (OwnCloudOptions.Apply(CullSettings)).
            N(OwnCloudOptions.TuneCutWeight, Cull.CutWeight),
            N(OwnCloudOptions.TuneStepGrowth, Cull.StepGrowthPerM),
            N(OwnCloudOptions.TuneTapStart, Cull.TapStartM),
            N(OwnCloudOptions.TuneTapEnd, Cull.TapEndM),
            N(OwnCloudOptions.TuneTapFactor, Cull.TapFactor),
            N(OwnCloudOptions.TuneFootScale, Cull.FootScale),
            N(OwnCloudOptions.TuneCapKeep, Cull.CapKeepSpans),
            // The cloud shadow (OwnCloudOptions.Apply(CloudShadowFrame)).
            N(CloudShadowStepPolicy.TuneKey, (float)CloudShadowStepPolicy.Default, note: "the CPU twin takes it at the next stack build"),
            F(CloudShadowShapeNoise.TuneKey, CloudShadowShapeNoise.Default)
        };

        // The keys that exist once per layer (CloudLookTuning; each is read again when the tuning table changes).
        internal static readonly IReadOnlyList<CloudLayerKey> PerLayer = new List<CloudLayerKey>
        {
            new CloudLayerKey(CloudLookTuning.TuneMapOverride, -1f, "constant map value, -1 the field's"),
            new CloudLayerKey(CloudLookTuning.TuneTypeOverride, -1f, "constant type value, -1 none"),
            new CloudLayerKey(CloudLookTuning.TuneTypeFair, -1f, "fair-weather type, -1 none"),
            new CloudLayerKey(CloudLookTuning.TuneDeckRule, -1f, "-1 cfg, 0 off, 1 on"),
            new CloudLayerKey(CloudLookTuning.TuneDeckPrecip, -1f, "deck rule's precipitation, -1 the field's"),
            new CloudLayerKey(CloudLookTuning.TuneDeckFog, -1f, "deck rule's fog share, -1 the field's"),
            new CloudLayerKey(CloudLookTuning.TuneLayerShift, 0f, "coverage shift of the layer"),
            new CloudLayerKey(CloudLookTuning.TuneHeapLayer, -1f, "heap preset of the layer, -1 the global key"),
            new CloudLayerKey(CloudLookTuning.TuneEdgePowerLayer, 0f, "rim exponent of the layer, 0 the global key"),
            new CloudLayerKey(CloudLookTuning.TuneCavityLayer, -1f, "cavity strength of the layer, -1 the global key"),
            new CloudLayerKey(CloudLookTuning.TuneSigma + ".", 1f, "sigma factor of the layer"),
            new CloudLayerKey(CloudLookTuning.TuneSoftness + ".", 1f, "softness factor of the layer"),
            new CloudLayerKey(CloudLookTuning.TuneNoiseAmount + ".", 1f, "noise amount factor of the layer"),
            new CloudLayerKey(CloudLookTuning.TuneNoiseSize + ".", 1f, "noise size factor of the layer")
        };

        private static readonly Dictionary<string, CloudKey> ByKey = Index();

        private static Dictionary<string, CloudKey> Index()
        {
            var index = new Dictionary<string, CloudKey>(StringComparer.Ordinal);
            foreach (CloudKey k in All) index.Add(k.Key, k);
            return index;
        }

        // The entry of a key: a listed key, or a per-layer key of any layer name; null for a key that is not the cloud pass's.
        internal static CloudKey Find(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            CloudKey found;
            if (ByKey.TryGetValue(key, out found)) return found;
            foreach (CloudLayerKey t in PerLayer)
            {
                if (key.Length > t.Prefix.Length && key.StartsWith(t.Prefix, StringComparison.Ordinal) && key.IndexOf('.', t.Prefix.Length) < 0)
                    return new CloudKey(key, t.Default, KeyLiveness.Change, false, t.Note);
            }
            return null;
        }

        // The listed keys and the per-layer keys of the layers named, in the order of the list.
        internal static List<CloudKey> Expand(IEnumerable<string> layerNames)
        {
            var keys = new List<CloudKey>(All);
            if (layerNames == null) return keys;
            foreach (string name in layerNames)
            {
                if (string.IsNullOrEmpty(name)) continue;
                foreach (CloudLayerKey t in PerLayer) keys.Add(new CloudKey(t.Prefix + name, t.Default, KeyLiveness.Change, false, t.Note));
            }
            return keys;
        }

        // Whether a key matches the search text: every word of it is in the key or its note, ignoring case. An empty search matches everything.
        internal static bool Matches(CloudKey key, string search)
        {
            if (key == null) return false;
            if (string.IsNullOrEmpty(search)) return true;
            foreach (string word in search.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (key.Key.IndexOf(word, StringComparison.OrdinalIgnoreCase) < 0 && key.Note.IndexOf(word, StringComparison.OrdinalIgnoreCase) < 0) return false;
            }
            return true;
        }

        // The words the viewer shows for when a key is read.
        internal static string LivenessText(KeyLiveness liveness)
        {
            switch (liveness)
            {
                case KeyLiveness.FrameHistory: return "live, history reset";
                case KeyLiveness.Change: return "live on change";
                default: return "live every frame";
            }
        }
    }
}
