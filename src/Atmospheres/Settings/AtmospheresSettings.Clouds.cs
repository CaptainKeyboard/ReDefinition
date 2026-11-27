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
            // Own clouds: the soft water content (E40 soft density): the width of the rim and the most grid points of the march.
            internal const string CloudSoftness = "cloudSoftness";
            internal const string CloudSamples = "cloudSamples";
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
            internal const string CloudResolution = "cloudResolution";
            internal const string CloudRotation = "cloudRotation";
            internal const string SceneWhiteBalance = "sceneWhiteBalance";
            // Own clouds: the visibility culling and the distance LOD (T16)
            internal const string CloudSkipEmpty = "cloudSkipEmpty";
            internal const string CloudLayerCapDistance = "cloudLayerCapDistance";
            // Own clouds: the ray-side pack for the traced rays (T18, RtOwnClouds)
            internal const string RtOwnClouds = "rtOwnClouds";
            internal const string RtOwnCloudsLayerCap = "rtOwnCloudsLayerCap";
            // Own clouds: the look fixes of T03v2 and the far view (T06), the switches of the review of wave 4
            internal const string CloudShortSunMarch = "cloudShortSunMarch";
            internal const string CloudSunWhiteBalance = "cloudSunWhiteBalance";
            internal const string CloudDiffusionDecay = "cloudDiffusionDecay";
            internal const string CloudAerialPerspective = "cloudAerialPerspective";
            internal const string CloudFarShapeMean = "cloudFarShapeMean";
            internal const string SpaceAirLevel = "spaceAirLevel";
            internal const string CloudFarMinSamples = "cloudFarMinSamples";
            internal const string CloudFarPolicy = "cloudFarPolicy";
            internal const string CloudFarMapFade = "cloudFarMapFade";
            // Own clouds: the height variation of bases and tops (T13)
            internal const string CloudBaseVariation = "cloudBaseVariation";
            internal const string CloudBasePhysics = "cloudBasePhysics";
            internal const string CloudTopFromUpdraft = "cloudTopFromUpdraft";
            internal const string BaseRagged = "baseRagged";
        }

        // --- Own clouds --- (the other keys of the group are read by key, through OwnCloudOptions)
        internal static bool OwnClouds => B(Keys.OwnClouds);
        // The most grid points of a march (24 to 128), relative to the Medium step quality's 48; the profile sets it (Low 48, Medium 96, High and above 128).
        internal static int CloudSamples => I(Keys.CloudSamples);
        internal static bool CloudLightCache => B(Keys.CloudLightCache);
        internal static bool CloudShadows => B(Keys.CloudShadows);
        internal static float SceneWhiteBalance => F(Keys.SceneWhiteBalance);
        internal static float SpaceAirLevel => F(Keys.SpaceAirLevel);
        internal static bool RtOwnClouds => B(Keys.RtOwnClouds);
        internal static int RtOwnCloudsLayerCap => I(Keys.RtOwnCloudsLayerCap);
        internal static bool CloudFarMapFade => B(Keys.CloudFarMapFade);
    }

    // The rows of the own cloud system in the key table, in the order of the window (they follow the other Clouds rows). A default is the
    // compiled default of the option struct it feeds, with these exceptions: the step growth is the product's value (LayerFrame.UseProductCost),
    // the light quality is High (the compiled sun steps and octaves lie between its levels Medium and High), the reconstruction block is the
    // 4 x 4 block that package T05 measured, and the quality-reducing choices (reconstruction) are off until the owner agrees.
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
            const string RV = AtmospheresSettings.ProfileRows.Volume;

            return new[]
            {
                // The layer core (T01, T01c, T05)
                // On by default with the cloud source ReDefinition (the owner 2026-11-26: our clouds are the standard of a new install).
                SettingDef.Bool("ownClouds", true, CL, "Own Volumetric Clouds", SRC),
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
                // Package T16: the visibility culling, off until the owner agrees (each lowers the cost at some error, measured in
                // tools/weather_metrics/cull.py).
                SettingDef.Bool("cloudSkipEmpty", false, CL, "Cloud Skip Empty Sky", OWN, RV),
                SettingDef.Int("cloudLayerCapDistance", 0, 0, 400, CL, "Cloud Layer Cap Distance In Km", OWN, RV),
                // Package T18: the traced rays see the own layers (a visible feature: on; it bakes only while the raytracing traces). The layer cap of the
                // ray side is a quality reduction (0 is every layer).
                SettingDef.Bool("rtOwnClouds", true, CL, "Own Clouds In Ray Tracing", OWN),
                SettingDef.Int("rtOwnCloudsLayerCap", 0, 0, CloudLayerDef.MaxLayers, CL, "Ray Tracing Cloud Layer Cap", OWN),
                // E40 soft density: how wide the soft rim of a cloud is (noise rank, 0.3 to 1.0) and how many grid points the march takes at most (24 to 128; the quality profile sets it: Low 48, Medium 96, High and above 128).
                SettingDef.Float("cloudSoftness", 0.5, 0.3, 1.0, CL, "Cloud Softness", OWN),
                SettingDef.Int("cloudSamples", 256, 24, 256, CL, "Cloud Samples", OWN, RV),

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

                // The temporal reconstruction (T05) and the resolution slider (T14): level 1 marches the half-size target every frame (LayerFrame.Resolution 0.5: a
                // quarter of the screen's pixels, stretched by the depth-aware composite), 2 to 4 march one pixel of every 2 x 2, 3 x 3 or 4 x 4 block of the full-size
                // picture per frame, so level 2 marches as many pixels as level 1 (K-T2 start 46: recon2 costs 1.05 of plain) and 3 and 4 a ninth and a sixteenth;
                // 1 until the owner agrees to a lower level. The mode's Low is prototype B and
                // overrides the slider. The tier mapping is open for the owner (TierRows.NotSet).
                SettingDef.Choice("cloudReconMode", typeof(ReconMode), ReconMode.Full, CL, "Cloud Reconstruction", OWN),
                SettingDef.Int("cloudResolution", 1, 1, 4, CL, "Cloud Resolution", OWN, RV),
                // The rotation (owner 2026-11-19): with the march at full size and a history, one pixel of every 2 x 2 block is marched per frame and blended into its history;
                // OwnCloudOptions.Apply(ReconSettings), recon.rotate for other block sides.
                SettingDef.Bool("cloudRotation", true, CL, "Cloud Rotation", OWN),
                // The scene's white balance (owner 2026-11-19, audit A2): the whole picture balanced after the upscaler by the sun's tint raised to this strength
                // (0 none, 1 the full tint), in place of the clouds' own white point (SceneWhiteBalance). The owner found the full balance too strong on every body.
                SettingDef.Float("sceneWhiteBalance", 0.25, 0.0, 1.0, CL, "Scene White Balance", OWN),
                // The air and the sea seen from above the atmosphere (master plan 47.2S; owner 2026-11-27: a setting of ours, never a patch of Scatterer's config): the
                // share of Scatterer's own level its air and sky are drawn with from twice the atmosphere's height outward (1 up to the atmosphere's top, linear between;
                // ScattererAirExposure). 1 is Scatterer's own picture. The default is measured (PRUEFPROTOKOLL 399): the sea's level S of the limb view falls from 0.146 to
                // 0.083 and of the disc view from 0.125 to 0.062, inside the range of the reference pictures (0.035 to 0.106).
                SettingDef.Float("spaceAirLevel", 0.6, 0.3, 1.0, CL, "Air From Space", OWN),

                // The sun march and the look fixes of T03v2: off, the march is the owner's look of 2026-11-19 (12 km); on, the short one (4 km) the parity rows measured.
                // (It replaced cloudSunMarchLong on 2026-11-23: the old key's files say False, which would now mean 4 km; a new key reads its default on every install.)
                // The white balance and the decay are the fitted look and stay on at their fitted values (off / 0 keep the earlier behaviour for the comparison tables)
                SettingDef.Bool("cloudShortSunMarch", false, CL, "Cloud Short Sun March", OWN),
                SettingDef.Bool("cloudSunWhiteBalance", false, CL, "Cloud Sun White Balance", OWN),
                SettingDef.Float("cloudDiffusionDecay", 0.08,0.0, 0.2, CL, "Cloud Diffusion Decay", OWN),

                // The far view (T06): all off until the owner agrees; the quality reducing march policy and the map fade included
                SettingDef.Bool("cloudAerialPerspective", false, CL, "Cloud Aerial Perspective", OWN),
                SettingDef.Bool("cloudFarShapeMean", false, CL, "Cloud Far Shape Mean", OWN),
                SettingDef.Int("cloudFarMinSamples", 0, 0, 16, CL, "Cloud Far Minimum Samples", OWN),
                SettingDef.Bool("cloudFarPolicy", false, CL, "Cloud Far March Policy", OWN),
                SettingDef.Bool("cloudFarMapFade", false, CL, "Cloud Far Map Fade", OWN),

                // The height variation of bases and tops (T13): no quality cost, on once the check passed
                SettingDef.Bool("cloudBaseVariation", true, CL, "Cloud Base Variation", OWN),
                SettingDef.Bool("cloudBasePhysics", true, CL, "Cloud Base From Humidity", OWN),
                SettingDef.Bool("cloudTopFromUpdraft", true, CL, "Cloud Top From Updraft", OWN),
                SettingDef.Bool("baseRagged", true, CL, "Cloud Ragged Underside", OWN)
            };
        }
    }
}
