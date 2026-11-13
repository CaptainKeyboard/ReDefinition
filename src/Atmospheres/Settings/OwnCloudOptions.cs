using System;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // Wires the settings of the own cloud system (AtmospheresSettings.Clouds.cs) to the option structs of the packages T01 to T05 and
    // names the tuning keys of the cost controls and the look constants. Each method reads through a value reader (a key to its number),
    // so that the tests and the settings window's model run it on any set of values; the overloads without a reader read the live
    // settings. The integration package (T09) calls them once per frame on the objects it keeps: the methods write every field they own,
    // so a call after a settings change needs no reset first.
    internal static class OwnCloudOptions
    {
        // The tuning keys (ATMO_TUNING { key = value }, or a matrix row atmo.<key>) of the cost controls of the march (T01c). The values
        // are the compiled defaults of LayerFrame.
        internal const string TuneCoverageBound = "layers.coverageBound", TuneLightStride = "layers.lightStride", TuneLightCutoff = "layers.lightCutoff",
            TuneEmptyLeap = "layers.emptyLeap", TuneCheapShare = "layers.cheapShare", TuneFogShare = "layers.fogShare",
            TuneRefineBudget = "layers.refineBudget", TuneRefineFraction = "layers.refineFraction", TuneEdgePower = "layers.edgePower",
            TuneRefineGate = "layers.refineGate", TuneRefineGateFirst = "layers.refineGateFirst";

        // The tuning keys of the light's look constants (T03); the compiled defaults are the fields of CloudLightSettings.
        internal const string TuneOctaveA = "light.octaveA", TuneOctaveB = "light.octaveB", TuneOctaveC = "light.octaveC",
            TuneMsBrightness = "light.msBrightness", TuneDiffusion = "light.diffusion", TuneDiffusionTau = "light.diffusionTau",
            TuneMsIsotropy = "light.msIsotropy", TuneAmbientOcclusion = "light.ambientOcclusion", TuneAmbientGain = "light.ambientGain",
            TuneSkyTint = "light.skyTint", TuneAmbientFloor = "light.ambientFloor";

        // The optical depth one step may add where a ray enters a cloud when cloudEntryRefinement is on (the product budget of the look sheets).
        internal const float EntryRefineBudget = 0.5f;

        // The contour gate of the refinement (T14, cloudRefineAtContours): the alpha step to a neighbouring pixel above which a ray refines, and the relative
        // first-hit step (0: not used). The values are the sweep's pick of tools/weather_metrics/refine.py.
        internal const float RefineGateAlpha = 0.12f, RefineGateFirstStep = 0f;

        private static readonly CloudLightSettings Compiled = new CloudLightSettings();
        private static readonly LayerFrame CompiledFrame = new LayerFrame();

        // One delegate for the process: a method group made at each call is a new object each time (about eight Apply calls a frame).
        private static readonly Func<string, double> LiveNumber = AtmospheresSettings.Number;

        private static Func<string, double> Live
        {
            get { return LiveNumber; }
        }

        // The same delegate for callers outside this class that pass the live numbers on every frame.
        internal static Func<string, double> LiveSettings
        {
            get { return LiveNumber; }
        }

        private static bool On(Func<string, double> v, string key)
        {
            return v(key) != 0.0;
        }

        private static CloudDetailLevel Level(Func<string, double> v, string key)
        {
            return (CloudDetailLevel)Math.Max(0, Math.Min(2, (int)v(key)));
        }

        // Whether the own cloud system draws for a body: the switch is on and the source in force is Ours (LayerSwitches.Active).
        internal static bool Active(Func<string, double> v, CloudSourceKind source)
        {
            return LayerSwitches.Active(On(v, AtmospheresSettings.Keys.OwnClouds), source);
        }

        internal static bool Active(CloudSourceKind source)
        {
            return Active(Live, source);
        }

        // The layers a pass draws for a stack of that many layers.
        internal static int LayersToDraw(Func<string, double> v, int stackCount)
        {
            return LayerSwitches.LayersToDraw(stackCount, (int)v(AtmospheresSettings.Keys.LayerCap));
        }

        // T02: the shape model's switches. SphereScale and Seed are per body and stay as the caller set them.
        internal static void Apply(NoiseSwitches s, Func<string, double> v)
        {
            s.Enabled = On(v, AtmospheresSettings.Keys.CloudNoise);
            s.Untile = On(v, AtmospheresSettings.Keys.CloudNoiseUntile);
            s.Curl = On(v, AtmospheresSettings.Keys.CloudNoiseCurl);
            s.DistanceLod = On(v, AtmospheresSettings.Keys.CloudNoiseDistanceLod);
            s.FineDetail = On(v, AtmospheresSettings.Keys.CloudFineDetail);
            s.SizeLevel = (int)Level(v, AtmospheresSettings.Keys.CloudNoiseTier);
        }

        internal static void Apply(NoiseSwitches s)
        {
            Apply(s, Live);
        }

        // T13: the four switches of the height variation; the numbers are the body's (HeightTuning.Parse) and stay as the caller set them.
        internal static void Apply(HeightTuning t, Func<string, double> v)
        {
            t.BaseVariation = On(v, AtmospheresSettings.Keys.CloudBaseVariation);
            t.BasePhysics = On(v, AtmospheresSettings.Keys.CloudBasePhysics);
            t.TopFromUpdraft = On(v, AtmospheresSettings.Keys.CloudTopFromUpdraft);
            t.BaseRagged = On(v, AtmospheresSettings.Keys.BaseRagged);
        }

        internal static void Apply(HeightTuning t)
        {
            Apply(t, Live);
        }

        // The tuning keys of the culling and the distance LOD (T16); the compiled values are the fields of CullSettings.
        internal const string TuneCutWeight = "cull.cutWeight", TuneStepGrowth = "cull.stepGrowth", TuneTapStart = "cull.tapStart", TuneTapEnd = "cull.tapEnd",
            TuneTapFactor = "cull.tapFactor", TuneFootScale = "cull.footScale", TuneCapKeep = "cull.capKeep";

        private static readonly CullSettings CompiledCull = new CullSettings();

        // T16: the switches of the visibility culling and the distance LOD; the numbers are tuning keys with the compiled values of the struct.
        internal static void Apply(CullSettings c, Func<string, double> v)
        {
            c.SkipEmpty = On(v, AtmospheresSettings.Keys.CloudSkipEmpty);
            c.SkipClosed = On(v, AtmospheresSettings.Keys.CloudSkipClosed);
            c.DistanceCut = On(v, AtmospheresSettings.Keys.CloudLayerDistanceCut);
            c.StepGrowth = On(v, AtmospheresSettings.Keys.CloudStepGrowth);
            c.TapLod = On(v, AtmospheresSettings.Keys.CloudTapLod);
            c.DetailLod = On(v, AtmospheresSettings.Keys.CloudDetailLod);
            c.CapDistanceM = Math.Max(0, Math.Min(400, (int)v(AtmospheresSettings.Keys.CloudLayerCapDistance))) * 1000f;
            c.CutWeight = Mathf.Clamp(Tuning.Get(TuneCutWeight, CompiledCull.CutWeight), 0.001f, 0.5f);
            c.StepGrowthPerM = Mathf.Max(0f, Tuning.Get(TuneStepGrowth, CompiledCull.StepGrowthPerM));
            c.TapStartM = Mathf.Max(0f, Tuning.Get(TuneTapStart, CompiledCull.TapStartM));
            c.TapEndM = Mathf.Max(c.TapStartM + 1f, Tuning.Get(TuneTapEnd, CompiledCull.TapEndM));
            c.TapFactor = Mathf.Clamp(Tuning.Get(TuneTapFactor, CompiledCull.TapFactor), 1f, 8f);
            c.FootScale = Mathf.Clamp(Tuning.Get(TuneFootScale, CompiledCull.FootScale), 0f, 8f);
            c.CapKeepSpans = Mathf.Clamp(Mathf.RoundToInt(Tuning.Get(TuneCapKeep, CompiledCull.CapKeepSpans)), 1, 8);
        }

        // T01, T01c, T05: the march of a frame.
        internal static void Apply(LayerFrame f, Func<string, double> v)
        {
            int maxSteps, sunSteps;
            float minStep;
            LayerSwitches.StepQuality((int)Level(v, AtmospheresSettings.Keys.LayerStepQuality), out maxSteps, out sunSteps, out minStep);
            f.MaxSteps = maxSteps;
            f.SunSteps = sunSteps;
            f.MinStepM = minStep;
            f.TransmittanceGrowth = (float)v(AtmospheresSettings.Keys.LayerStepGrowth);
            f.HashDither = On(v, AtmospheresSettings.Keys.CloudHashDither);
            f.SunMarchM = On(v, AtmospheresSettings.Keys.CloudSunMarchLong) ? LayerFrame.SunMarchLongM : LayerFrame.SunMarchShortM;
            // The cost controls and the entry's refinement: tuning keys with the compiled values of the frame.
            f.CoverageBound = Tuning.Flag(TuneCoverageBound, CompiledFrame.CoverageBound);
            f.LightStride = Mathf.Clamp(Mathf.RoundToInt(Tuning.Get(TuneLightStride, CompiledFrame.LightStride)), 1, 8);
            f.LightCutoff = Mathf.Max(0f, Tuning.Get(TuneLightCutoff, CompiledFrame.LightCutoff));
            f.EmptyLeap = Mathf.Max(1f, Tuning.Get(TuneEmptyLeap, CompiledFrame.EmptyLeap));
            f.CheapShare = Mathf.Clamp01(Tuning.Get(TuneCheapShare, CompiledFrame.CheapShare));
            f.FogShare = Mathf.Clamp01(Tuning.Get(TuneFogShare, CompiledFrame.FogShare));
            // T15: the edge mask restriction needs the refinement in the flagged blocks, so it switches the budget on by itself; the bisection steps
            // act where no full refinement runs (the whole picture, or the blocks the mask does not flag).
            bool maskOn = On(v, AtmospheresSettings.Keys.CloudRefineEdgeMask)
                          && (ReconMode)Math.Max(0, Math.Min(2, (int)v(AtmospheresSettings.Keys.CloudReconMode))) != ReconMode.Low;
            bool refineOn = On(v, AtmospheresSettings.Keys.CloudEntryRefinement) || maskOn;
            f.RefineBudget = refineOn ? Mathf.Max(0f, Tuning.Get(TuneRefineBudget, EntryRefineBudget)) : 0f;
            f.RefineBisect = Math.Max(0, Math.Min(LayerFrame.MaxRefineBisect, (int)v(AtmospheresSettings.Keys.CloudRefineBisect)));
            Apply(f.Cull, v);
            f.RefineFraction = Mathf.Clamp(Tuning.Get(TuneRefineFraction, CompiledFrame.RefineFraction), 0.0625f, 1f);
            // T14: the contour gate of the refinement (the alpha step of the 2 x 2 quad above which a ray refines; the first-hit step is off).
            bool atContours = On(v, AtmospheresSettings.Keys.CloudRefineAtContours);
            bool gate = f.RefineBudget > 0f && atContours;
            f.RefineGate = gate ? Mathf.Max(0.001f, Tuning.Get(TuneRefineGate, RefineGateAlpha)) : 0f;
            f.RefineGateFirst = gate ? Mathf.Max(0f, Tuning.Get(TuneRefineGateFirst, RefineGateFirstStep)) : 0f;
            f.EdgePower = Mathf.Max(1f, Tuning.Get(TuneEdgePower, CompiledFrame.EdgePower));
        }

        internal static void Apply(LayerFrame f)
        {
            Apply(f, Live);
        }

        // T03: the light. The quality level sets the taps and the octaves, cloudLightOctaves caps the octaves, the look constants are tuning keys.
        // The extra lights are limited by LimitMoonLights after the caller has filled them.
        internal static void Apply(CloudLightSettings s, Func<string, double> v)
        {
            CloudLightSettings q = CloudLightSettings.ForQuality((int)Level(v, AtmospheresSettings.Keys.CloudLightQuality));
            s.SunStepsOverride = q.SunStepsOverride;
            s.MoonSteps = q.MoonSteps;
            s.UpSteps = q.UpSteps;
            s.Octaves = Math.Max(1, Math.Min(q.Octaves, (int)v(AtmospheresSettings.Keys.CloudLightOctaves)));
            s.PowderStrength = (float)v(AtmospheresSettings.Keys.CloudPowder);
            s.SunAirHeightM = On(v, AtmospheresSettings.Keys.CloudSunTint) ? Compiled.SunAirHeightM : 0f;
            s.AmbientOcclusionTaps = On(v, AtmospheresSettings.Keys.CloudAmbientTaps);
            s.SunAirWhiteBalance = On(v, AtmospheresSettings.Keys.CloudSunWhiteBalance);
            s.DiffusionDecay = Math.Max(0f, (float)v(AtmospheresSettings.Keys.CloudDiffusionDecay));
            s.UpReuse = (int)v(AtmospheresSettings.Keys.CloudAmbientReuse);
            if (s.Cache != null) s.Cache.Enabled = On(v, AtmospheresSettings.Keys.CloudLightCache);
            s.OctaveA = Tuning.Get(TuneOctaveA, Compiled.OctaveA);
            s.OctaveB = Tuning.Get(TuneOctaveB, Compiled.OctaveB);
            s.OctaveC = Tuning.Get(TuneOctaveC, Compiled.OctaveC);
            s.MsBrightness = Tuning.Get(TuneMsBrightness, Compiled.MsBrightness);
            s.Diffusion = Tuning.Get(TuneDiffusion, Compiled.Diffusion);
            s.DiffusionTau = Tuning.Get(TuneDiffusionTau, Compiled.DiffusionTau);
            s.MsIsotropy = Tuning.Get(TuneMsIsotropy, Compiled.MsIsotropy);
            s.AmbientOcclusion = Tuning.Get(TuneAmbientOcclusion, Compiled.AmbientOcclusion);
            s.AmbientGain = Tuning.Get(TuneAmbientGain, Compiled.AmbientGain);
            s.SkyTint = Tuning.Get(TuneSkyTint, Compiled.SkyTint);
            // The floor is read through EffectiveFloor, which never lets it fall below the parity gate's 3 percent.
            s.AmbientFloor = Tuning.Get(TuneAmbientFloor, Compiled.AmbientFloor);
            LimitMoonLights(s, v);
        }

        internal static void Apply(CloudLightSettings s)
        {
            Apply(s, Live);
        }

        // The moons and planets that light the clouds: the lights above the setting's count are switched off (a zero colour is skipped by the shader).
        // Apply(CloudLightSettings) calls it for the lights as they stand; a caller that fills s.Lights afterwards calls it again after the fill.
        internal static void LimitMoonLights(CloudLightSettings s, Func<string, double> v)
        {
            int count = Math.Max(0, Math.Min(CloudLightSettings.MaxLights, (int)v(AtmospheresSettings.Keys.CloudMoonLights)));
            for (int i = count; i < CloudLightSettings.MaxLights; i++) s.Lights[i].Colour = new Color(0f, 0f, 0f, 0f);
        }

        // T04: how the shadow's steps scale with a layer's interval (CloudShadowStepPolicy): the tuning key shadow.stepPolicy, else the compiled default.
        internal static CloudShadowSteps ShadowStepPolicy
        {
            get { return CloudShadowStepPolicy.FromNumber(Tuning.Get(CloudShadowStepPolicy.TuneKey, (float)CloudShadowStepPolicy.Default)); }
        }

        // T04: the cloud shadow's numbers of a frame.
        internal static void Apply(CloudShadowFrame f, Func<string, double> v)
        {
            int steps, taps;
            CloudShadowSwitches.Quality((int)Level(v, AtmospheresSettings.Keys.CloudShadowQuality), out steps, out taps);
            f.StepsPerSpan = steps;
            f.StepPolicy = ShadowStepPolicy;
            f.DiscTaps = taps;
            f.Floor = Mathf.Clamp01((float)v(AtmospheresSettings.Keys.CloudShadowFloor));
        }

        // Whether the cloud shadow is drawn for a body with that many own layers (CloudShadowSwitches.Active).
        internal static bool ShadowActive(Func<string, double> v, CloudSourceKind source, int layerCount)
        {
            return CloudShadowSwitches.Active(On(v, AtmospheresSettings.Keys.CloudShadows), Active(v, source), layerCount);
        }

        // T06: the far view's switches. The air's coefficients, the colour of the in-scattered light and the lod range are not settings: the integration
        // (T09) supplies them from the body and the sky.
        internal static void Apply(FarViewSettings s, Func<string, double> v)
        {
            s.Haze = On(v, AtmospheresSettings.Keys.CloudFarHaze);
            // The look knobs (CloudLookTuning): cloud.farHaze forces the aerial perspective off / on (default -1: the setting decides), cloud.hazeStrength scales it (default 1).
            float forceHaze = CloudLookTuning.FarHazeForce;
            if (forceHaze >= 0f) s.Haze = forceHaze > 0f;
            s.HazeStrength = CloudLookTuning.HazeStrengthScale;
            // T16: the detail LOD by distance takes the shape mean with it (the footprint only lifts the spacing the mean is chosen by).
            s.ShapeMean = On(v, AtmospheresSettings.Keys.CloudFarShapeMean) || On(v, AtmospheresSettings.Keys.CloudDetailLod);
            s.MinSamples = Math.Max(0, Math.Min(16, (int)v(AtmospheresSettings.Keys.CloudFarMinSamples)));
            s.Policy = On(v, AtmospheresSettings.Keys.CloudFarPolicy);
        }

        internal static void Apply(FarViewSettings s)
        {
            Apply(s, Live);
        }

        // Whether the map view fades the cloud shell with the camera's distance (OwnShell.MapFadeEnabled).
        internal static bool MapFade(Func<string, double> v)
        {
            return On(v, AtmospheresSettings.Keys.CloudFarMapFade);
        }

        // T05, T14: the reconstruction. The slider cloudResolution is the level: 1 marches the half-size target of the plain pass (the default, ReconMode.Full; LayerFrame.Resolution), 2 to 4 march one
        // pixel of every 2 x 2, 3 x 3 or 4 x 4 block (ReconMode.Block). The mode setting's Low (prototype B, a smaller march target) overrides the slider.
        internal static void Apply(ReconSettings s, Func<string, double> v)
        {
            int level = ReconPattern.ClampLevel((int)v(AtmospheresSettings.Keys.CloudResolution));
            bool low = (ReconMode)Math.Max(0, Math.Min(2, (int)v(AtmospheresSettings.Keys.CloudReconMode))) == ReconMode.Low;
            s.Mode = low ? ReconMode.Low : (level > 1 ? ReconMode.Block : ReconMode.Full);
            s.Block = Math.Max(2, level);
            // The mask restriction runs the edge mode, which marches the 4 x 4 coarse block; the mode Low overrides it.
            s.RefineAtMask = On(v, AtmospheresSettings.Keys.CloudRefineEdgeMask) && !low;
        }

        internal static void Apply(ReconSettings s)
        {
            Apply(s, Live);
        }
    }
}
