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

        // E40 grain: the entry ramp (LayerFrame.EntryRamp) as the bisection steps it searches an entry with; 0: off.
        internal const string TuneEntryRamp = "layers.entryRamp";
        internal const float EntryRampDefault = 0f;
        // E40 grain: the multiplier of the most grid points of a span (LayerFrame.MaxSteps); 1: the step quality's count.
        internal const string TuneGridFine = "layers.gridFine";
        internal const float GridFineDefault = 1f;
        // E40 grain: the factor of the step inside a cloud over the grid step (LayerFrame.CloudStep); 1: the grid step.
        internal const string TuneCloudStep = "layers.cloudStep";
        internal const float CloudStepDefault = 1f;
        // E40 grain: the weight of a new frame in the history of the half-size march (ReconSettings.Accumulate); 0: no history (the pass before the package).
        // Measured at 0.2: against no history the temporal noise of a pixel falls to 0.18 (ground) and 0.27 (above) of it in the game (K-T2d start 54, rows
        // grain_tstd of g0 and gacc) and to 0.22 in the player, the error against the converged picture falls under a still camera, a turn of 30 and 180 degrees a
        // second and a fly-by of 300 and 2400 m/s (series e40-gn-temporal), and the pass costs 0.02 ms more at 1707 x 960 (series e40-gn-cost). It is the default
        // (the owner delegated the choice): level 1 keeps the reprojected, clipped history and the pass reads "reconstruct"; recon.accumulate 0 is the former plain pass.
        // The finer grid (layers.gridFine, layers.emptyLeap), the entry ramp and layers.cloudStep stay off until their cost with all layers is measured in the game.
        internal const string TuneAccumulate = "recon.accumulate";
        internal const float AccumulateDefault = 0.2f;

        // E40 grain against TVC: layers.spanTight (LayerFrame.SpanTight; 1: the march searches only the altitudes the cloud types around the camera reach, with the
        // height variation's reach as the maps hold it there; 0, the default: the type table's whole interval plus the worst-case reach) and layers.ditherSequence
        // (LayerFrame.DitherSequence; 0: a new step offset per pixel and frame, the march as it was; 1, the default: a per-pixel offset advanced by the golden ratio).
        // Measured: in the player (one layer, the lowest type alone) the cut lowers the temporal noise of a cloud pixel from 0.0031 / 0.0101 (closed / rim) to
        // 0.0006 / 0.0014 and the error against the converged picture from 0.050 to 0.018 (series e40-gt-temporal). In the game the map around the camera holds most
        // types, the interval stays wide (cumulus layer 608 to 8042 m of 430 to 9990) and the noise falls by 13 percent (ground) and 2 percent (above) at +0.7 / 0.0 ms
        // (K-T2d start gt57), so the cut is not the default: it is the search interval of the per-ray march that follows this package. The sequence costs nothing
        // (player: 10 to 40 percent less temporal noise under the history; no measurable change in the game at rain, K-T2 start gt1707).
        internal const string TuneSpanTight = "layers.spanTight", TuneDitherSequence = "layers.ditherSequence";
        internal const bool SpanTightDefault = false;
        internal const float DitherSequenceDefault = 1f;

        // The tuning keys of the light's look constants (T03); the compiled defaults are the fields of CloudLightSettings.
        internal const string TuneOctaveA = "light.octaveA", TuneOctaveB = "light.octaveB", TuneOctaveC = "light.octaveC",
            TuneMsBrightness = "light.msBrightness", TuneDiffusion = "light.diffusion", TuneDiffusionTau = "light.diffusionTau",
            TuneMsIsotropy = "light.msIsotropy", TuneAmbientOcclusion = "light.ambientOcclusion", TuneAmbientGain = "light.ambientGain",
            TuneSkyTint = "light.skyTint", TuneAmbientFloor = "light.ambientFloor", TuneAmbientColumnTop = "light.ambientColumnTop";

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
            // E40 grain: layers.gridFine N (1 to 8; default 1) multiplies the most grid points of a span (up to the shader's 256); with layers.emptyLeap N the leap over
            // empty samples is as long as the product's grid step at most, so the search is N times finer where the clouds are at about the density reads of before.
            // E40 grain against TVC: the multiplier may be below 1 (down to 0.25): on a span cut to the types present (layers.spanTight) fewer grid points still lie closer
            // than the full count did over the type table's whole interval.
            f.MaxSteps = Math.Max(4, Math.Min(256, Mathf.RoundToInt(maxSteps * Mathf.Clamp(Tuning.Get(TuneGridFine, GridFineDefault), 0.25f, 8f))));
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
            // E40 grain: layers.entryRamp N (1 to 6; default 0, off) searches every entry with at least N bisection steps and ramps the step up from the entry.
            int ramp = Mathf.Clamp(Mathf.RoundToInt(Tuning.Get(TuneEntryRamp, EntryRampDefault)), 0, LayerFrame.MaxRefineBisect);
            f.CloudStep = Mathf.Clamp(Tuning.Get(TuneCloudStep, CloudStepDefault), 1f, 8f);
            f.EntryRamp = ramp > 0;
            if (ramp > 0) f.RefineBisect = Math.Max(f.RefineBisect, ramp);
            f.SpanTight = Tuning.Flag(TuneSpanTight, SpanTightDefault);
            f.DitherSequence = Mathf.Clamp(Mathf.RoundToInt(Tuning.Get(TuneDitherSequence, DitherSequenceDefault)), 0, 2);
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
            // light.ambientColumnTop (default 1): the analytic occlusion's column ends at the cloud type's top; 0: at the layer's outer altitude (the earlier behaviour).
            s.AmbientColumnToTypeTop = Tuning.Flag(TuneAmbientColumnTop, Compiled.AmbientColumnToTypeTop);
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

        // E40 FL-FIX: whether the cloud shadow (shader and CPU twin) reads the layer pass's shape noise: the tuning key shadow.shapeNoise, else the compiled default.
        internal static bool ShadowShapeNoise
        {
            get { return Tuning.Get(CloudShadowShapeNoise.TuneKey, CloudShadowShapeNoise.Default ? 1f : 0f) >= 0.5f; }
        }

        // T04: the cloud shadow's numbers of a frame.
        internal static void Apply(CloudShadowFrame f, Func<string, double> v)
        {
            int steps, taps;
            CloudShadowSwitches.Quality((int)Level(v, AtmospheresSettings.Keys.CloudShadowQuality), out steps, out taps);
            f.StepsPerSpan = steps;
            f.StepPolicy = ShadowStepPolicy;
            f.ShapeNoise = ShadowShapeNoise;
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
            // The look knobs (CloudLookTuning): cloud.farHaze forces the aerial perspective off / on / automatic (default -1: the setting decides), cloud.hazeStrength scales it (default 1).
            bool haze, auto;
            CloudHazePlan.Resolve(CloudLookTuning.FarHazeForce, On(v, AtmospheresSettings.Keys.CloudFarHaze), out haze, out auto);
            s.Haze = haze;
            s.HazeAuto = auto;
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
            // E40 grain: recon.accumulate W (0 to 1; default 0.2, 0 = off) gives the half-size march of level 1 a history of its own size: each frame enters it with the
            // weight W. It acts at level 1 only (the block levels have their own history) and runs the pass through the reconstructor's mode Low.
            float accumulate = Mathf.Clamp01(Tuning.Get(TuneAccumulate, AccumulateDefault));
            s.Accumulate = 0f;
            if (accumulate > 0f && s.Mode == ReconMode.Full && !s.RefineAtMask)
            {
                s.Mode = ReconMode.Low;
                s.Resolution = CompiledFrame.Resolution;
                s.Jitter = false;
                s.Accumulate = accumulate;
            }
            else if (low)
            {
                s.Accumulate = accumulate;
            }
        }

        internal static void Apply(ReconSettings s)
        {
            Apply(s, Live);
        }
    }
}
