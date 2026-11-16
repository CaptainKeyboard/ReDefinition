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
            : this(key, compiledDefault, liveness, flag, note, KeySection.Other, float.NaN, float.NaN, true, KeyMarker.Live)
        {
        }

        internal CloudKey(string key, float compiledDefault, KeyLiveness liveness, bool flag, string note, KeySection section, float min, float max, bool rangeEstimated, KeyMarker marker)
        {
            Key = key;
            Default = compiledDefault;
            Liveness = liveness;
            Flag = flag;
            Note = note ?? "";
            Section = section;
            if (float.IsNaN(min) || float.IsNaN(max)) CloudKeyLayout.Range(key, compiledDefault, flag, out min, out max, out rangeEstimated);
            Min = min;
            Max = max;
            RangeEstimated = rangeEstimated;
            Marker = marker;
        }

        internal string Key { get; }
        internal float Default { get; }
        internal KeyLiveness Liveness { get; }
        // A switch (Tuning.Flag: other than 0 is on).
        internal bool Flag { get; }
        internal string Note { get; }
        // The group of the viewer's table, the slider's range (typing a value is never limited to it) and whether the range is estimated from the default (the reader does not clamp).
        internal KeySection Section { get; }
        internal float Min { get; }
        internal float Max { get; }
        internal bool RangeEstimated { get; }
        // When a change of the value shows: the next frame, only after Rebuild Weather, only after a scene change.
        internal KeyMarker Marker { get; }

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
            : this(prefix, "", compiledDefault, note, CloudKeyLayout.SectionOfLayerKey(prefix), KeyLiveness.Change, KeyMarker.Live, false)
        {
        }

        // A key whose layer name stands between a prefix and a suffix (cloud.heap.layer.<name>.edgePower): the suffix is empty for the keys that end with the name.
        internal CloudLayerKey(string prefix, string suffix, float compiledDefault, string note, KeySection section, KeyLiveness liveness, KeyMarker marker, bool flag)
        {
            Prefix = prefix;
            Suffix = suffix ?? "";
            Default = compiledDefault;
            Note = note ?? "";
            Section = section;
            Liveness = liveness;
            Marker = marker;
            Flag = flag;
        }

        internal string Prefix { get; }
        internal string Suffix { get; }
        internal float Default { get; }
        internal string Note { get; }
        internal KeySection Section { get; }
        internal KeyLiveness Liveness { get; }
        internal KeyMarker Marker { get; }
        internal bool Flag { get; }

        // The key of a layer.
        internal CloudKey For(string layerName)
        {
            return new CloudKey(Prefix + layerName + Suffix, Default, Liveness, Flag, Note, Section, float.NaN, float.NaN, true, Marker);
        }

        // Whether the key is one of this pattern, for any layer name (a name has no dot).
        internal bool Matches(string key)
        {
            if (key.Length <= Prefix.Length + Suffix.Length || !key.StartsWith(Prefix, StringComparison.Ordinal) || !key.EndsWith(Suffix, StringComparison.Ordinal)) return false;
            return key.IndexOf('.', Prefix.Length, key.Length - Prefix.Length - Suffix.Length) < 0;
        }
    }

    // What a package registers for the viewer's key table without editing this file: a static method with this attribute and the signature void (CloudKeyRegistrations) in any class of
    // the assembly is called once when the registry is made. The keys it lists appear in the table, the key checks, A/B and the presets.
    [AttributeUsage(AttributeTargets.Method)]
    internal sealed class CloudKeySourceAttribute : Attribute
    {
    }

    // The list a CloudKeySource method fills.
    internal sealed class CloudKeyRegistrations
    {
        internal readonly List<CloudKey> Keys = new List<CloudKey>();
        internal readonly List<CloudLayerKey> LayerKeys = new List<CloudLayerKey>();
        internal readonly List<NamedKeyPreset> Presets = new List<NamedKeyPreset>();

        // A number key: its range is stated (min, max) or estimated from the default where both are NaN.
        internal void Key(string key, float compiledDefault, KeySection section, string note, KeyLiveness liveness = KeyLiveness.Frame, KeyMarker marker = KeyMarker.Live,
                          float min = float.NaN, float max = float.NaN)
        {
            Keys.Add(new CloudKey(key, compiledDefault, liveness, false, note, section, min, max, float.IsNaN(min) || float.IsNaN(max), marker));
        }

        // A switch.
        internal void Flag(string key, bool compiledDefault, KeySection section, string note, KeyLiveness liveness = KeyLiveness.Frame, KeyMarker marker = KeyMarker.Live)
        {
            Keys.Add(new CloudKey(key, compiledDefault ? 1f : 0f, liveness, true, note, section, 0f, 1f, false, marker));
        }

        // A key per layer, the layer's name between prefix and suffix.
        internal void LayerKey(string prefix, string suffix, float compiledDefault, KeySection section, string note, bool flag = false, KeyLiveness liveness = KeyLiveness.Change,
                               KeyMarker marker = KeyMarker.Live)
        {
            LayerKeys.Add(new CloudLayerKey(prefix, suffix, compiledDefault, note, section, liveness, marker, flag));
        }

        // A built-in preset slot: named values of tuning keys (and settings) the viewer's preset list shows next to Today. Registering a name that an empty slot holds fills the slot.
        internal void Preset(string name, string note, IDictionary<string, float> keys, IDictionary<string, double> settings = null)
        {
            Presets.Add(new NamedKeyPreset(name, note, keys, settings));
        }

        // The presets with one entry per name: a filled slot replaces an empty one; two filled ones of a name are a conflict (the first stays). Returns the conflicts.
        internal List<string> MergePresets()
        {
            var conflicts = new List<string>();
            var merged = new List<NamedKeyPreset>();
            foreach (NamedKeyPreset p in Presets)
            {
                int at = merged.FindIndex(m => string.Equals(m.Name, p.Name, StringComparison.OrdinalIgnoreCase));
                if (at < 0) merged.Add(p);
                else if (merged[at].IsEmpty) merged[at] = p;
                else if (!p.IsEmpty) conflicts.Add("preset " + p.Name + " is filled twice");
            }
            Presets.Clear();
            Presets.AddRange(merged);
            return conflicts;
        }
    }

    // A preset that code provides: values of tuning keys and settings. It is never written to a file and never changes a default; loading it sets the viewer's overrides like any preset.
    internal sealed class NamedKeyPreset
    {
        internal NamedKeyPreset(string name, string note, IDictionary<string, float> keys, IDictionary<string, double> settings)
        {
            Name = name;
            Note = note ?? "";
            Keys = keys == null ? new Dictionary<string, float>() : new Dictionary<string, float>(keys);
            Settings = settings == null ? new Dictionary<string, double>() : new Dictionary<string, double>(settings);
        }

        internal string Name { get; }
        internal string Note { get; }
        internal Dictionary<string, float> Keys { get; }
        internal Dictionary<string, double> Settings { get; }

        // A slot that holds nothing yet (a package fills it by registering a preset of the same name that does).
        internal bool IsEmpty
        {
            get { return Keys.Count == 0 && Settings.Count == 0; }
        }
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

        // The keys of the pass as its readers declare them; All is this list placed in the viewer's groups (CloudKeyLayout) plus what the packages register (CloudKeySource).
        private static readonly List<CloudKey> Core = new List<CloudKey>
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
            // CLOUDR1 (the new cloud model). Heap, edgePower and cavity are read into the type tables when the layers are uploaded (a change of the tuning table uploads them again within a frame:
            // Change); the heap volume itself is built on a worker the first time a type asks for it (about one second at Medium; until then every type reads the shape volume). The per-layer
            // forms are below (PerLayer). Ranges are in the notes.
            N(CloudLookTuning.TuneHeap, -1f, KeyLiveness.Change, "heap preset for every type, -1..2: -1 each type's cfg key, 0 none (today), 1 billow, 2 rough; builds the volume on first use"),
            N(CloudLookTuning.TuneEdgePower, 0f, KeyLiveness.Change, "exponent of the density rim for every type of a soft layer, 0..6: 0 each type's key, below 1 a crisper rim"),
            N(CloudLookTuning.TuneCavity, -1f, KeyLiveness.Change, "cavity strength (darkens the creases between lobes), -1..1: -1 each type's key, 0 off; needs a heap"),
            F(CloudLookTuning.TuneHeapFootLod, true, KeyLiveness.FrameHistory),
            F(CloudLookTuning.TuneHeapIso, true, KeyLiveness.FrameHistory),
            N(CloudLookTuning.TuneHeapWarp, 0.5f, KeyLiveness.FrameHistory, "share of the untiling displacement's horizontal part the heap keeps, 0..1; -1 the whole displacement (the pulled lookup)"),
            F(CloudLookTuning.TuneNoiseBlend, false, KeyLiveness.FrameHistory),   // CPU twin only today: the GPU path crashes the 2019.4 shader compiler
            N(CloudLookTuning.TuneNoiseBlendExp, 8f, KeyLiveness.FrameHistory, "exponent of the blend weights, 1..32"),
            N(CloudLookTuning.TuneNoiseBlendCell, 1f, KeyLiveness.FrameHistory, "blend lattice cell in periods, 0.25..16"),
            N(CloudLookTuning.TuneHeapDetail, 0f, KeyLiveness.Change, "RELOAD: Hurst exponent of the erosion detail octaves, 0..2, 0 compiled; read only when the volumes are generated (a scene change or a size tier change), not live"),
            N(CloudLookTuning.TuneFirstHit, 0f, KeyLiveness.FrameHistory, "bisection evaluations on the entered span, 0..8, 0 off (a measured dead end: smooth look, +3.9 ms)"),
            N(CloudLookTuning.TuneFirstHitThin, 0.25f, KeyLiveness.FrameHistory, "first hit: thin steps as a share of the grid step, 0.02..1"),
            N(CloudLookTuning.TuneFirstHitShell, 32f, KeyLiveness.FrameHistory, "first hit: thin steps after the entry, 0..512"),
            N(CloudLookTuning.TuneFirstHitNear, 0f, KeyLiveness.FrameHistory, "first hit: distance in m up to which the thin steps are taken, 0 everywhere"),
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
        private static readonly List<CloudLayerKey> CorePerLayer = new List<CloudLayerKey>
        {
            new CloudLayerKey(CloudLookTuning.TuneMapOverride, -1f, "constant map value, -1 the field's"),
            new CloudLayerKey(CloudLookTuning.TuneTypeOverride, -1f, "constant type value, -1 none"),
            new CloudLayerKey(CloudLookTuning.TuneTypeFair, -1f, "fair-weather type, -1 none"),
            new CloudLayerKey(CloudLookTuning.TuneDeckRule, -1f, "-1 cfg, 0 off, 1 on"),
            new CloudLayerKey(CloudLookTuning.TuneDeckPrecip, -1f, "deck rule's precipitation, -1 the field's"),
            new CloudLayerKey(CloudLookTuning.TuneDeckFog, -1f, "deck rule's fog share, -1 the field's"),
            new CloudLayerKey(CloudLookTuning.TuneLayerShift, 0f, "coverage shift of the layer"),
            new CloudLayerKey(CloudLookTuning.TuneHeapLayer, -1f, "heap preset of the layer, -1..2 (-1 the global key, 0 none, 1 billow, 2 rough)"),
            new CloudLayerKey(CloudLookTuning.TuneEdgePowerLayer, 0f, "rim exponent of the layer, 0..6 (0 the global key; below 1 a crisper rim)"),
            new CloudLayerKey(CloudLookTuning.TuneCavityLayer, -1f, "cavity strength of the layer, -1..1 (-1 the global key, 0 off)"),
            new CloudLayerKey(CloudLookTuning.TuneSigma + ".", 1f, "sigma factor of the layer"),
            new CloudLayerKey(CloudLookTuning.TuneSoftness + ".", 1f, "softness factor of the layer"),
            new CloudLayerKey(CloudLookTuning.TuneNoiseAmount + ".", 1f, "noise amount factor of the layer"),
            new CloudLayerKey(CloudLookTuning.TuneNoiseSize + ".", 1f, "noise size factor of the layer")
        };

        // What the packages register (CloudKeySource methods): collected once. A key named twice, or a key that is a per-layer key of another pattern, is kept once (the first) and listed
        // in Conflicts, which a test requires to be empty.
        internal static readonly List<string> Conflicts = new List<string>();
        private static readonly CloudKeyRegistrations Registered = Collect();

        internal static readonly IReadOnlyList<CloudKey> All = Place();
        internal static readonly IReadOnlyList<CloudLayerKey> PerLayer = JoinLayers();
        // The presets the packages provide for the viewer's preset list (the registered slots), in the order they were registered.
        internal static IReadOnlyList<NamedKeyPreset> Presets
        {
            get { return Registered.Presets; }
        }

        private static CloudKeyRegistrations Collect()
        {
            Type[] types;
            try
            {
                types = typeof(CloudKeyRegistry).Assembly.GetTypes();
            }
            catch (System.Reflection.ReflectionTypeLoadException e)
            {
                types = Array.FindAll(e.Types, t => t != null);
            }
            CloudKeyRegistrations found = CollectFrom(types);
            Conflicts.AddRange(found.MergePresets());
            return found;
        }

        // What the CloudKeySource methods of these types register (the registry takes the types of its own assembly; a test passes its own).
        internal static CloudKeyRegistrations CollectFrom(IEnumerable<Type> types)
        {
            var found = new CloudKeyRegistrations();
            foreach (Type type in types)
            {
                foreach (System.Reflection.MethodInfo m in type.GetMethods(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
                {
                    if (!Attribute.IsDefined(m, typeof(CloudKeySourceAttribute))) continue;
                    System.Reflection.ParameterInfo[] ps = m.GetParameters();
                    if (ps.Length != 1 || ps[0].ParameterType != typeof(CloudKeyRegistrations)) continue;
                    try
                    {
                        m.Invoke(null, new object[] { found });
                    }
                    catch (Exception e)
                    {
                        AtmospheresLog.Failed("cloud viewer", "the key source " + type.Name + "." + m.Name + " failed", e);
                    }
                }
            }
            return found;
        }

        // The core keys placed in their groups, then the registered ones.
        private static List<CloudKey> Place()
        {
            return Place(Core, Registered, Conflicts);
        }

        // The keys listed in the order: core keys placed in their groups, then the registered ones; a key named twice is a conflict and kept once.
        internal static List<CloudKey> Place(IEnumerable<CloudKey> core, CloudKeyRegistrations registered, List<string> conflicts)
        {
            var placed = new List<CloudKey>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (CloudKey k in core)
            {
                KeySection section;
                if (!CloudKeyLayout.TryGetSection(k.Key, out section)) section = KeySection.Other;
                placed.Add(new CloudKey(k.Key, k.Default, k.Liveness, k.Flag, k.Note, section, float.NaN, float.NaN, true, KeyMarker.Live));
                seen.Add(k.Key);
            }
            foreach (CloudKey k in registered.Keys)
            {
                if (!seen.Add(k.Key)) { conflicts.Add("key " + k.Key + " is registered twice"); continue; }
                placed.Add(k);
            }
            return placed;
        }

        private static List<CloudLayerKey> JoinLayers()
        {
            return JoinLayers(CorePerLayer, Registered, Conflicts);
        }

        internal static List<CloudLayerKey> JoinLayers(IEnumerable<CloudLayerKey> core, CloudKeyRegistrations registered, List<string> conflicts)
        {
            var all = new List<CloudLayerKey>(core);
            foreach (CloudLayerKey t in registered.LayerKeys)
            {
                bool twice = all.Exists(o => o.Prefix == t.Prefix && o.Suffix == t.Suffix);
                if (twice) conflicts.Add("layer key " + t.Prefix + "<name>" + t.Suffix + " is registered twice");
                else all.Add(t);
            }
            return all;
        }

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
                if (t.Matches(key)) return t.For(key.Substring(t.Prefix.Length, key.Length - t.Prefix.Length - t.Suffix.Length));
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
                foreach (CloudLayerKey t in PerLayer) keys.Add(t.For(name));
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

        // The title of a group of the table.
        internal static string SectionTitle(KeySection section)
        {
            switch (section)
            {
                case KeySection.Shape: return "Shape";
                case KeySection.Edge: return "Edge";
                case KeySection.Light: return "Light";
                case KeySection.Quality: return "March And Quality";
                case KeySection.Weather: return "Weather";
                case KeySection.Modes: return "Modes";
                default: return "Other";
            }
        }

        // The words the viewer shows for when a change of a key shows: a key that is read every frame is live; one that needs a rebuild or a scene change says so.
        internal static string MarkerText(KeyMarker marker)
        {
            switch (marker)
            {
                case KeyMarker.Live: return "Live";
                case KeyMarker.AfterRebuildWeather: return "After Rebuild Weather";
                case KeyMarker.AfterSceneChange: return "After Scene Change";
                default: return "Not Checked";
            }
        }

        // The short form of the marker for the table's column.
        internal static string MarkerShort(KeyMarker marker)
        {
            switch (marker)
            {
                case KeyMarker.Live: return "live";
                case KeyMarker.AfterRebuildWeather: return "rebuild";
                case KeyMarker.AfterSceneChange: return "scene";
                default: return "?";
            }
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
