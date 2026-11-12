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
            TuneRefineBudget = "layers.refineBudget", TuneRefineFraction = "layers.refineFraction", TuneEdgePower = "layers.edgePower";

        // The tuning keys of the light's look constants (T03); the compiled defaults are the fields of CloudLightSettings.
        internal const string TuneOctaveA = "light.octaveA", TuneOctaveB = "light.octaveB", TuneOctaveC = "light.octaveC",
            TuneMsBrightness = "light.msBrightness", TuneDiffusion = "light.diffusion", TuneDiffusionTau = "light.diffusionTau",
            TuneMsIsotropy = "light.msIsotropy", TuneAmbientOcclusion = "light.ambientOcclusion", TuneAmbientGain = "light.ambientGain",
            TuneSkyTint = "light.skyTint", TuneAmbientFloor = "light.ambientFloor";

        // The optical depth one step may add where a ray enters a cloud when cloudEntryRefinement is on (the product budget of the look sheets).
        internal const float EntryRefineBudget = 0.5f;

        private static readonly CloudLightSettings Compiled = new CloudLightSettings();
        private static readonly LayerFrame CompiledFrame = new LayerFrame();

        private static Func<string, double> Live
        {
            get { return AtmospheresSettings.Number; }
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
            f.RefineBudget = On(v, AtmospheresSettings.Keys.CloudEntryRefinement) ? Mathf.Max(0f, Tuning.Get(TuneRefineBudget, EntryRefineBudget)) : 0f;
            f.RefineFraction = Mathf.Clamp(Tuning.Get(TuneRefineFraction, CompiledFrame.RefineFraction), 0.0625f, 1f);
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

        // T04: the cloud shadow's numbers of a frame.
        internal static void Apply(CloudShadowFrame f, Func<string, double> v)
        {
            int steps, taps;
            CloudShadowSwitches.Quality((int)Level(v, AtmospheresSettings.Keys.CloudShadowQuality), out steps, out taps);
            f.StepsPerSpan = steps;
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
            s.ShapeMean = On(v, AtmospheresSettings.Keys.CloudFarShapeMean);
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

        // T05: the reconstruction. Mode Full marches every pixel every frame (the setting's default); the block is read in Block mode only.
        internal static void Apply(ReconSettings s, Func<string, double> v)
        {
            s.Mode = (ReconMode)Math.Max(0, Math.Min(2, (int)v(AtmospheresSettings.Keys.CloudReconMode)));
            s.Block = Math.Max(2, Math.Min(4, (int)v(AtmospheresSettings.Keys.CloudReconBlock)));
        }

        internal static void Apply(ReconSettings s)
        {
            Apply(s, Live);
        }
    }
}
