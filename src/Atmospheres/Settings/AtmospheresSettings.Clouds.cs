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
            internal const string CloudResolution = "cloudResolution";
            internal const string CloudRefineAtContours = "cloudRefineAtContours";
            internal const string CloudRefineBisect = "cloudRefineBisect";
            internal const string CloudRefineEdgeMask = "cloudRefineEdgeMask";
            // Own clouds: the visibility culling and the distance LOD (T16)
            internal const string CloudSkipEmpty = "cloudSkipEmpty";
            internal const string CloudSkipClosed = "cloudSkipClosed";
            internal const string CloudLayerDistanceCut = "cloudLayerDistanceCut";
            internal const string CloudStepGrowth = "cloudStepGrowth";
            internal const string CloudTapLod = "cloudTapLod";
            internal const string CloudDetailLod = "cloudDetailLod";
            internal const string CloudLayerCapDistance = "cloudLayerCapDistance";
            // Own clouds: the look fixes of T03v2 and the far view (T06), the switches of the review of wave 4
            internal const string CloudSunMarchLong = "cloudSunMarchLong";
            internal const string CloudSunWhiteBalance = "cloudSunWhiteBalance";
            internal const string CloudDiffusionDecay = "cloudDiffusionDecay";
            internal const string CloudFarHaze = "cloudFarHaze";
            internal const string CloudFarShapeMean = "cloudFarShapeMean";
            internal const string CloudFarMinSamples = "cloudFarMinSamples";
            internal const string CloudFarPolicy = "cloudFarPolicy";
            internal const string CloudFarMapFade = "cloudFarMapFade";
            // Own clouds: the height variation of bases and tops (T13)
            internal const string CloudBaseVariation = "cloudBaseVariation";
            internal const string CloudBasePhysics = "cloudBasePhysics";
            internal const string CloudTopFromUpdraft = "cloudTopFromUpdraft";
            internal const string BaseRagged = "baseRagged";
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
        internal static int CloudResolution => I(Keys.CloudResolution);
        internal static bool CloudRefineAtContours => B(Keys.CloudRefineAtContours);
        internal static int CloudRefineBisect => I(Keys.CloudRefineBisect);
        internal static bool CloudRefineEdgeMask => B(Keys.CloudRefineEdgeMask);
        internal static bool CloudSkipEmpty => B(Keys.CloudSkipEmpty);
        internal static bool CloudSkipClosed => B(Keys.CloudSkipClosed);
        internal static bool CloudLayerDistanceCut => B(Keys.CloudLayerDistanceCut);
        internal static bool CloudStepGrowth => B(Keys.CloudStepGrowth);
        internal static bool CloudTapLod => B(Keys.CloudTapLod);
        internal static bool CloudDetailLod => B(Keys.CloudDetailLod);
        internal static int CloudLayerCapDistance => I(Keys.CloudLayerCapDistance);

        internal static bool CloudSunMarchLong => B(Keys.CloudSunMarchLong);
        internal static bool CloudSunWhiteBalance => B(Keys.CloudSunWhiteBalance);
        internal static float CloudDiffusionDecay => F(Keys.CloudDiffusionDecay);
        internal static bool CloudFarHaze => B(Keys.CloudFarHaze);
        internal static bool CloudFarShapeMean => B(Keys.CloudFarShapeMean);
        internal static int CloudFarMinSamples => I(Keys.CloudFarMinSamples);
        internal static bool CloudFarPolicy => B(Keys.CloudFarPolicy);
        internal static bool CloudFarMapFade => B(Keys.CloudFarMapFade);
        internal static bool CloudBaseVariation => B(Keys.CloudBaseVariation);
        internal static bool CloudBasePhysics => B(Keys.CloudBasePhysics);
        internal static bool CloudTopFromUpdraft => B(Keys.CloudTopFromUpdraft);
        internal static bool BaseRagged => B(Keys.BaseRagged);
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
            const string REFINE = AtmospheresSettings.GreyReasons.NeedsEntryRefinement;
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
                SettingDef.Bool("cloudRefineAtContours", false, CL, "Cloud Refine Only At Contours", REFINE),
                // Package T15: two cheap forms of the entry refinement, both off until the owner agrees (the first replaces the second march by a few
                // density reads at an entry, the second restricts the full refinement to the blocks of the edge mode that the edge mask flags).
                SettingDef.Int("cloudRefineBisect", 0, 0, 4, CL, "Cloud Entry Bisection Steps", OWN, RV),
                SettingDef.Bool("cloudRefineEdgeMask", false, CL, "Cloud Refine At Edge Mask", OWN, RV),
                // Package T16: the visibility culling and the distance LOD, all off until the owner agrees (each lowers the cost at some error, measured in
                // tools/weather_metrics/cull.py).
                SettingDef.Bool("cloudSkipEmpty", false, CL, "Cloud Skip Empty Sky", OWN, RV),
                SettingDef.Bool("cloudSkipClosed", false, CL, "Cloud Skip Behind Closed Decks", OWN, RV),
                SettingDef.Bool("cloudLayerDistanceCut", false, CL, "Cloud Layer Distance Cut", OWN, RV),
                SettingDef.Bool("cloudStepGrowth", false, CL, "Cloud Step Growth With Distance", OWN, RV),
                SettingDef.Bool("cloudTapLod", false, CL, "Cloud Light Taps By Distance", OWN, RV),
                SettingDef.Bool("cloudDetailLod", false, CL, "Cloud Detail By Distance", OWN, RV),
                SettingDef.Int("cloudLayerCapDistance", 0, 0, 400, CL, "Cloud Layer Cap Distance (km)", OWN, RV),
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

                // The temporal reconstruction (T05) and the resolution slider (T14): level 1 marches the half-size target every frame (LayerFrame.Resolution 0.5: a
                // quarter of the screen's pixels, stretched by the depth-aware composite), 2 to 4 march one pixel of every 2 x 2, 3 x 3 or 4 x 4 block of the full-size
                // picture per frame, so level 2 marches as many pixels as level 1 (K-T2 start 46: recon2 costs 1.05 of plain) and 3 and 4 a ninth and a sixteenth;
                // 1 until the owner agrees to a lower level. The mode's Low is prototype B and
                // overrides the slider. The tier mapping is open for the owner (TierRows.NotSet).
                SettingDef.Choice("cloudReconMode", typeof(ReconMode), ReconMode.Full, CL, "Cloud Reconstruction", OWN),
                SettingDef.Int("cloudResolution", 1, 1, 4, CL, "Cloud Resolution", OWN, RV),

                // The sun march and the look fixes of T03v2: the short march (4 km) is what the parity rows measured; the white balance and the decay are the
                // fitted look and stay on at their fitted values (off / 0 keep the earlier behaviour for the comparison tables)
                SettingDef.Bool("cloudSunMarchLong", false, CL, "Cloud Long Sun March", OWN),
                SettingDef.Bool("cloudSunWhiteBalance", true, CL, "Cloud Sun White Balance", OWN),
                SettingDef.Float("cloudDiffusionDecay", 0.02, 0.0, 0.2, CL, "Cloud Diffusion Decay", OWN),

                // The far view (T06): all off until the owner agrees; the quality reducing march policy and the map fade included
                SettingDef.Bool("cloudFarHaze", false, CL, "Cloud Far Haze", OWN),
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
