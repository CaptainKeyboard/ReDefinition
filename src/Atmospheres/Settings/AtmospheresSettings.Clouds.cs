using System.Collections.Generic;

namespace ReDefinition.Atmospheres
{
    // A three-level quality choice of the own cloud system: the march (layerStepQuality), the noise volumes (cloudNoiseTier), the light
    // (cloudLightQuality) and the shadow (cloudShadowQuality) each pick one. The numbers behind a level live with the code that reads it
    // (LayerSwitches.StepQuality, NoiseSizes.ForLevel, CloudLightSettings.ForQuality, CloudShadowSwitches.Quality).
    internal enum CloudDetailLevel
    {
        Low = 0,
        Medium = 1,
        High = 2
    }

    // The settings of the own cloud system (stream T of the Atmospheres plan, package T10): the switches of the layer core (T01), the shape
    // noise (T02), the light (T03), the shadow (T04) and the temporal reconstruction (T05). The keys are declared here (Keys is partial),
    // the rows are appended to the table by SettingsTable, and the typed readers follow. The switches are wired to the option structs by
    // OwnCloudOptions; the cost controls without a player value (the light stride, the sun march cutoff and so on) are tuning keys read
    // through Tuning.Get with the compiled defaults of the structs, and the look constants of the light (octave weights, diffusion,
    // ambient) are tuning keys too.
    internal static partial class AtmospheresSettings
    {
        internal static partial class Keys
        {
            // Own clouds: the layer core (T01) and its cost
            internal const string OwnClouds = "ownClouds";
            internal const string LayerCap = "layerCap";
            internal const string LayerStepQuality = "layerStepQuality";
            internal const string LayerStepGrowth = "layerStepGrowth";
            // Own clouds: the shape noise (T02)
            internal const string CloudNoise = "cloudNoise";
            internal const string CloudNoiseUntile = "cloudNoiseUntile";
            internal const string CloudNoiseCurl = "cloudNoiseCurl";
            internal const string CloudNoiseTier = "cloudNoiseTier";
            internal const string CloudNoiseDistanceLod = "cloudNoiseDistanceLod";
            internal const string CloudFineDetail = "cloudFineDetail";
            internal const string CloudEntryRefinement = "cloudEntryRefinement";
            internal const string CloudHashDither = "cloudHashDither";
            // Own clouds: the light (T03)
            internal const string CloudLightQuality = "cloudLightQuality";
            internal const string CloudLightOctaves = "cloudLightOctaves";
            internal const string CloudPowder = "cloudPowder";
            internal const string CloudSunTint = "cloudSunTint";
            internal const string CloudMoonLights = "cloudMoonLights";
            internal const string CloudAmbientTaps = "cloudAmbientTaps";
            internal const string CloudAmbientReuse = "cloudAmbientReuse";
            internal const string CloudLightCache = "cloudLightCache";
            // Own clouds: the shadow (T04)
            internal const string CloudShadows = "cloudShadows";
            internal const string CloudShadowQuality = "cloudShadowQuality";
            internal const string CloudShadowFloor = "cloudShadowFloor";
            // Own clouds: the temporal reconstruction (T05)
            internal const string CloudReconMode = "cloudReconMode";
            internal const string CloudReconBlock = "cloudReconBlock";
        }

        // --- Own clouds ---
        internal static bool OwnClouds => B(Keys.OwnClouds);
        internal static int LayerCap => I(Keys.LayerCap);
        internal static CloudDetailLevel LayerStepQuality => E<CloudDetailLevel>(Keys.LayerStepQuality);
        internal static float LayerStepGrowth => F(Keys.LayerStepGrowth);

        internal static bool CloudNoise => B(Keys.CloudNoise);
        internal static bool CloudNoiseUntile => B(Keys.CloudNoiseUntile);
        internal static bool CloudNoiseCurl => B(Keys.CloudNoiseCurl);
        internal static CloudDetailLevel CloudNoiseTier => E<CloudDetailLevel>(Keys.CloudNoiseTier);
        internal static bool CloudNoiseDistanceLod => B(Keys.CloudNoiseDistanceLod);
        internal static bool CloudFineDetail => B(Keys.CloudFineDetail);
        internal static bool CloudEntryRefinement => B(Keys.CloudEntryRefinement);
        internal static bool CloudHashDither => B(Keys.CloudHashDither);

        internal static CloudDetailLevel CloudLightQuality => E<CloudDetailLevel>(Keys.CloudLightQuality);
        internal static int CloudLightOctaves => I(Keys.CloudLightOctaves);
        internal static float CloudPowder => F(Keys.CloudPowder);
        internal static bool CloudSunTint => B(Keys.CloudSunTint);
        internal static int CloudMoonLights => I(Keys.CloudMoonLights);
        internal static bool CloudAmbientTaps => B(Keys.CloudAmbientTaps);
        internal static int CloudAmbientReuse => I(Keys.CloudAmbientReuse);
        internal static bool CloudLightCache => B(Keys.CloudLightCache);

        internal static bool CloudShadows => B(Keys.CloudShadows);
        internal static CloudDetailLevel CloudShadowQuality => E<CloudDetailLevel>(Keys.CloudShadowQuality);
        internal static float CloudShadowFloor => F(Keys.CloudShadowFloor);

        internal static ReconMode CloudReconMode => E<ReconMode>(Keys.CloudReconMode);
        internal static int CloudReconBlock => I(Keys.CloudReconBlock);
    }

    // The rows of the own cloud system in the key table, in the order of the window (they follow the other Clouds rows). A default is the
    // compiled default of the option struct it feeds, with these exceptions: the step growth is the product's value (LayerFrame.UseProductCost),
    // the light quality is High (the compiled sun steps and octaves lie between its levels Medium and High), the reconstruction block is the
    // 4 x 4 block that package T05 measured, and the quality-reducing choices (reconstruction, entry refinement) are off until the owner agrees.
    internal static class CloudSettingRows
    {
        internal static IEnumerable<SettingDef> Rows()
        {
            const string CL = AtmospheresSettings.Categories.Clouds;
            const string SRC = AtmospheresSettings.GreyReasons.NeedsOwnSource;
            const string OWN = AtmospheresSettings.GreyReasons.NeedsOwnClouds;
            const string NOISE = AtmospheresSettings.GreyReasons.NeedsCloudNoise;
            const string SHADOW = AtmospheresSettings.GreyReasons.NeedsCloudShadows;
            const string TAPS = AtmospheresSettings.GreyReasons.NeedsAmbientTaps;
            const string RECON = AtmospheresSettings.GreyReasons.NeedsReconstruction;
            const string RV = AtmospheresSettings.ProfileRows.Volume;

            return new[]
            {
                // The layer core (T01, T01c, T05)
                SettingDef.Bool("ownClouds", false, CL, "Own Volumetric Clouds", SRC),
                SettingDef.Int("layerCap", CloudLayerDef.MaxLayers, 1, CloudLayerDef.MaxLayers, CL, "Own Cloud Layer Cap", OWN, RV),
                SettingDef.Choice("layerStepQuality", typeof(CloudDetailLevel), CloudDetailLevel.Medium, CL, "Own Cloud Step Quality", OWN, RV),
                SettingDef.Float("layerStepGrowth", 2.0, 0.0, 4.0, CL, "Own Cloud Step Growth", OWN),

                // The shape noise (T02)
                SettingDef.Bool("cloudNoise", true, CL, "Cloud Shape Noise", OWN),
                SettingDef.Bool("cloudNoiseUntile", true, CL, "Cloud Noise Untiling", NOISE),
                SettingDef.Bool("cloudNoiseCurl", true, CL, "Cloud Noise Curl", NOISE),
                SettingDef.Choice("cloudNoiseTier", typeof(CloudDetailLevel), CloudDetailLevel.Medium, CL, "Cloud Noise Size", NOISE, RV),
                SettingDef.Bool("cloudNoiseDistanceLod", true, CL, "Cloud Noise Distance Detail", NOISE),
                SettingDef.Bool("cloudFineDetail", true, CL, "Cloud Fine Detail Octave", NOISE),
                SettingDef.Bool("cloudEntryRefinement", false, CL, "Cloud Entry Refinement", OWN),
                SettingDef.Bool("cloudHashDither", true, CL, "Cloud White Dither", OWN),

                // The light (T03)
                SettingDef.Choice("cloudLightQuality", typeof(CloudDetailLevel), CloudDetailLevel.High, CL, "Cloud Light Quality", OWN, RV),
                SettingDef.Int("cloudLightOctaves", 4, 1, 4, CL, "Cloud Scattering Octaves", OWN),
                SettingDef.Float("cloudPowder", 0.8075, 0.0, 1.0, CL, "Cloud Powder Effect", OWN),
                SettingDef.Bool("cloudSunTint", true, CL, "Cloud Sun Colour Through Air", OWN),
                SettingDef.Int("cloudMoonLights", CloudLightSettings.MaxLights, 0, CloudLightSettings.MaxLights, CL, "Cloud Moon Lights", OWN),
                SettingDef.Bool("cloudAmbientTaps", false, CL, "Cloud Ambient Taps", OWN),
                SettingDef.Int("cloudAmbientReuse", 0, 0, 2, CL, "Cloud Ambient Tap Reuse", TAPS),
                SettingDef.Bool("cloudLightCache", false, CL, "Cloud Light Cache", OWN),

                // The shadow (T04)
                SettingDef.Bool("cloudShadows", true, CL, "Cloud Shadows", OWN),
                SettingDef.Choice("cloudShadowQuality", typeof(CloudDetailLevel), CloudDetailLevel.Medium, CL, "Cloud Shadow Quality", SHADOW, RV),
                SettingDef.Float("cloudShadowFloor", 0.0, 0.0, 1.0, CL, "Cloud Shadow Floor", SHADOW),

                // The temporal reconstruction (T05): off (every pixel marched every frame) until the owner agrees to the 4 x 4 block look (a sixteenth of the pixels per frame)
                SettingDef.Choice("cloudReconMode", typeof(ReconMode), ReconMode.Full, CL, "Cloud Reconstruction", OWN),
                SettingDef.Int("cloudReconBlock", 4, 2, 4, CL, "Cloud Reconstruction Block", RECON)
            };
        }
    }
}
