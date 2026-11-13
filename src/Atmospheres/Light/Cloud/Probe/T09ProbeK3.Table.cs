using System.Collections.Generic;
using K = ReDefinition.Atmospheres.AtmospheresSettings.Keys;

namespace ReDefinition.Atmospheres
{
    // What a K-T3 variant needs besides settings and tuning keys.
    internal enum K3Special
    {
        None,
        // A forced storm 12 km east of the site (the lightning module's ForcedSource), flashes counted over the window.
        Storm,
        // The sheet layer of the stack (the highest map outside a group) gets a key of the look family: coverage shift, fair-weather type, constant type.
        SheetShift,
        SheetTypeFair,
        SheetTypeOverride
    }

    // One variant of series K-T3: a name, the view it is measured in and the switches it changes against the plain own pass. tools/weather_metrics/kt3.py holds the
    // expectation of each id; its --selftest reads the ids from this file and fails when the two lists differ.
    internal sealed class K3Variant
    {
        // Mode: what the pass reports while the variant draws; level 1 reads "reconstruct" under the default recon.accumulate (0.2), "plain" if the default were 0.
        internal string Id, View, Group, Mode = OwnCloudOptions.AccumulateDefault > 0f ? "reconstruct" : "plain";
        // Off: the cloud source is None (the cost baseline of a view). Readout: the numeric frame (T09ProbeDiag.DiagTake) beside the pictures; sky3 has no horizon in the frame and takes the plain pictures.
        internal bool Off, Readout = true, Noise, Cache;
        // Seconds the variant waits after its keys are set, before the window (wetness and the maps need time).
        internal double ExtraSettleS;
        internal K3Special Special;
        internal readonly List<KeyValuePair<string, double>> Sets = new List<KeyValuePair<string, double>>();
        internal readonly List<KeyValuePair<string, float>> Tunes = new List<KeyValuePair<string, float>>();

        internal K3Variant Set(string key, double value)
        {
            Sets.Add(new KeyValuePair<string, double>(key, value));
            return this;
        }

        internal K3Variant Tune(string key, float value)
        {
            Tunes.Add(new KeyValuePair<string, float>(key, value));
            return this;
        }
    }

    // The table. Ids end in the view's letter (g ground, a above, h horizon, s sky3), so that one id is one (recipe, view) and a start lists each once.
    internal static partial class K3Variants
    {
        internal const string Ground = "ground", Above = "above", Horizon = "horizon", Sky3 = "sky3";

        private static readonly Dictionary<string, K3Variant> ById = new Dictionary<string, K3Variant>();
        private static readonly List<string> Order = new List<string>();

        static K3Variants()
        {
            Build();
        }

        internal static IList<string> AllIds
        {
            get { return Order; }
        }

        internal static bool TryGet(string id, out K3Variant variant)
        {
            return ById.TryGetValue(id, out variant);
        }

        // Every settings key any variant sets: the probe puts these back to the start's values before it applies a variant.
        internal static IEnumerable<string> SettingKeys()
        {
            var keys = new List<string>();
            foreach (string id in Order)
                foreach (KeyValuePair<string, double> s in ById[id].Sets)
                    if (!keys.Contains(s.Key)) keys.Add(s.Key);
            return keys;
        }

        private static K3Variant Add(string id, string view, string group)
        {
            var v = new K3Variant { Id = id, View = view, Group = group, Readout = view != Sky3 };
            ById.Add(id, v);
            Order.Add(id);
            return v;
        }

        private static K3Variant OffOf(string id, string view)
        {
            K3Variant v = Add(id, view, "base");
            v.Off = true;
            return v;
        }

        private static void Build()
        {
            const string Cloud = "cloud";

            // --- The baselines of every view: the cost of a variant is read against the "off" window of its view, and its delta against "plain". ---
            OffOf("off_g", Ground);
            Add("plain_g", Ground, "base").Noise = true;
            OffOf("off_a", Above);
            Add("plain_a", Above, "base");
            OffOf("off_h", Horizon);
            Add("plain_h", Horizon, "base");
            OffOf("off_s", Sky3);
            Add("plain_s", Sky3, "base");

            // --- Start k3near: the shape noise, the four T13 height switches, the hash dither, the look constants of CloudLookTuning (ground view) ---
            Add("noise_off_g", Ground, "noise").Set(K.CloudNoise, 0).Noise = true;
            Add("untile_off_g", Ground, "noise").Set(K.CloudNoiseUntile, 0);
            Add("curl_off_g", Ground, "noise").Set(K.CloudNoiseCurl, 0);
            Add("fine_off_g", Ground, "noise").Set(K.CloudFineDetail, 0);
            Add("dlod_off_g", Ground, "noise").Set(K.CloudNoiseDistanceLod, 0);
            Add("tier_low_g", Ground, "noise").Set(K.CloudNoiseTier, 0).Noise = true;
            Add("tier_high_g", Ground, "noise").Set(K.CloudNoiseTier, 2).Noise = true;
            Add("hash_off_g", Ground, "noise").Set(K.CloudHashDither, 0);
            AddHeightOff("h_all_off_g", Ground).Noise = true;
            Add("h_bvar_off_g", Ground, "t13").Set(K.CloudBaseVariation, 0);
            Add("h_bphys_off_g", Ground, "t13").Set(K.CloudBasePhysics, 0);
            Add("h_ragged_off_g", Ground, "t13").Set(K.BaseRagged, 0).Noise = true;
            Add("h_top_off_a", Above, "t13").Set(K.CloudTopFromUpdraft, 0);
            AddHeightOff("h_all_off_a", Above);
            Add("remap1_g", Ground, Cloud).Tune(CloudLookTuning.TuneDensityRemap, 1f);
            Add("combine1_g", Ground, Cloud).Tune(CloudLookTuning.TuneCoverCombine, 1f);
            Add("tint1_g", Ground, Cloud).Tune(CloudLookTuning.TuneSunTint, 1f);
            Add("sigma2_g", Ground, Cloud).Tune(CloudLookTuning.TuneSigma, 2f);
            Add("soft2_g", Ground, Cloud).Tune(CloudLookTuning.TuneSoftness, 2f);
            Add("namt2_g", Ground, Cloud).Tune(CloudLookTuning.TuneNoiseAmount, 2f);
            Add("nsize2_g", Ground, Cloud).Tune(CloudLookTuning.TuneNoiseSize, 2f);

            // --- Start k3light: light quality, octaves, powder, sun colour, moons, ambient, sun march, shadows, step quality, the light constants (ground view) ---
            Add("lightq_low_g", Ground, "light").Set(K.CloudLightQuality, 0);
            Add("lightq_med_g", Ground, "light").Set(K.CloudLightQuality, 1);
            Add("oct1_g", Ground, "light").Set(K.CloudLightOctaves, 1);
            Add("powder0_g", Ground, "light").Set(K.CloudPowder, 0);
            Add("suntint_off_g", Ground, "light").Set(K.CloudSunTint, 0);
            Add("moon0_g", Ground, "light").Set(K.CloudMoonLights, 0);
            Add("aotaps_g", Ground, "light").Set(K.CloudAmbientTaps, 1);
            Add("aoreuse2_g", Ground, "light").Set(K.CloudAmbientTaps, 1).Set(K.CloudAmbientReuse, 2);
            Add("wb_off_g", Ground, "light").Set(K.CloudSunWhiteBalance, 0);
            Add("sun9_g", Ground, "light").Set(K.CloudSunMarchLong, 1);
            Add("shadows_off_g", Ground, "shadow").Set(K.CloudShadows, 0);
            Add("shq_low_g", Ground, "shadow").Set(K.CloudShadowQuality, 0);
            Add("shq_high_g", Ground, "shadow").Set(K.CloudShadowQuality, 2);
            Add("shfloor_g", Ground, "shadow").Set(K.CloudShadowFloor, 0.5);
            Add("diffdecay_g", Ground, "light").Set(K.CloudDiffusionDecay, 0.1);
            Add("stepq_low_g", Ground, "step").Set(K.LayerStepQuality, 0);
            Add("stepq_high_g", Ground, "step").Set(K.LayerStepQuality, 2);
            Add("growth0_g", Ground, "step").Set(K.LayerStepGrowth, 0);
            Add("msb_hi_g", Ground, "light").Tune(OwnCloudOptions.TuneMsBrightness, 4.4f);
            Add("iso0_g", Ground, "light").Tune(OwnCloudOptions.TuneMsIsotropy, 0f);
            Add("amb0_g", Ground, "light").Tune(OwnCloudOptions.TuneAmbientGain, 0f);
            Add("floor_hi_g", Ground, "light").Tune(OwnCloudOptions.TuneAmbientFloor, 0.15f);
            Add("diff0_g", Ground, "light").Tune(OwnCloudOptions.TuneDiffusion, 0f);
            Add("skytint0_g", Ground, "light").Tune(OwnCloudOptions.TuneSkyTint, 0f);

            // --- Start k3far: the culling (T16) and the far view (T06) at the horizon, the closed-deck skip under the stack (sky3) ---
            Add("skip_empty_h", Horizon, "cull").Set(K.CloudSkipEmpty, 1);
            Add("dcut_h", Horizon, "cull").Set(K.CloudLayerDistanceCut, 1).Set(K.CloudLayerCapDistance, 100);
            Add("stepg_h", Horizon, "cull").Set(K.CloudStepGrowth, 1);
            Add("tap_h", Horizon, "cull").Set(K.CloudTapLod, 1);
            Add("detail_h", Horizon, "cull").Set(K.CloudDetailLod, 1);
            AddCullAll("cull_all_h", Horizon);
            AddCullAll("kcut_h", Horizon).Tune(OwnCloudOptions.TuneCutWeight, 0.1f);
            AddCullAll("kstepg_h", Horizon).Tune(OwnCloudOptions.TuneStepGrowth, 4e-4f);
            AddCullAll("ktapwin_h", Horizon).Tune(OwnCloudOptions.TuneTapStart, 5000f).Tune(OwnCloudOptions.TuneTapEnd, 30000f);
            AddCullAll("ktapf_h", Horizon).Tune(OwnCloudOptions.TuneTapFactor, 6f);
            AddCullAll("kfoot_h", Horizon).Tune(OwnCloudOptions.TuneFootScale, 3f);
            AddCullAll("kcapkeep_h", Horizon).Tune(OwnCloudOptions.TuneCapKeep, 1f);
            // The aerial perspective is on by default behind Scatterer's scattering, so the rows that switch it on draw the plain picture; the row that switches it off shows its effect.
            Add("nohaze_h", Horizon, "far").Tune(CloudLookTuning.TuneFarHaze, 0f);
            Add("haze_h", Horizon, "far").Set(K.CloudFarHaze, 1);
            Add("haze_tune_h", Horizon, "far").Tune(CloudLookTuning.TuneFarHaze, 1f);
            Add("hazestr2_h", Horizon, "far").Set(K.CloudFarHaze, 1).Tune(CloudLookTuning.TuneHazeStrength, 2f);
            Add("shapemean_h", Horizon, "far").Set(K.CloudFarShapeMean, 1);
            Add("minsamp_h", Horizon, "far").Set(K.CloudFarMinSamples, 4);
            Add("farpolicy_h", Horizon, "far").Set(K.CloudFarPolicy, 1);
            Add("mapfade_h", Horizon, "far").Set(K.CloudFarMapFade, 1);
            Add("fade40_h", Horizon, "far").Tune(CloudLookTuning.TuneFadeFar, 40000f);
            Add("skip_closed_s", Sky3, "cull").Set(K.CloudSkipClosed, 1);
            AddCullAll("cull_all_s", Sky3);

            // --- Start k3fx: the reconstruction options (ground), lightning (horizon, storm 12 km east), rain with the wet surfaces and the droplets (ground) ---
            Add("rlow_g", Ground, "recon").Set(K.CloudReconMode, 2).Mode = "reconstruct";
            Add("edge_g", Ground, "recon").Set(K.CloudResolution, 4).Tune(EdgeSettings.TuneEdgeAdaptive, 1f).Mode = "reconstruct";
            Add("bisect2_g", Ground, "recon").Set(K.CloudEntryRefinement, 1).Set(K.CloudRefineBisect, 2);
            Add("contours_g", Ground, "recon").Set(K.CloudEntryRefinement, 1).Set(K.CloudRefineAtContours, 1);
            Add("edgemask_g", Ground, "recon").Set(K.CloudRefineEdgeMask, 1).Mode = "reconstruct";
            Storm("ln_on_h");
            Storm("ln_bolts_off_h").Set(K.LightningBolts, 0);
            Storm("ln_terrain_off_h").Set(K.LightningTerrainLight, 0);
            Storm("ln_cloud_off_h").Set(K.LightningCloudFlash, 0);
            Storm("ln_bright05_h").Set(K.LightningBrightness, 0.5);
            Storm("ln_max1_h").Set(K.LightningMaxPerSecond, 1);
            // The rain block (letter r, the ground view again): its own off window (the rain without clouds) opens it, the rain with the plain pass is the reference of the rest.
            Rain(OffOf("off_r", Ground), 0);
            Rain(Add("rain_r", Ground, "rain"), 0);
            Rain(Add("rain_wet_r", Ground, "rain"), 20).Set(K.WetSurfaces, 1);
            Rain(Add("rain_drops_r", Ground, "rain"), 5).Set(K.WindowDroplets, 1);
            Rain(Add("rain_both_r", Ground, "rain"), 5).Set(K.WetSurfaces, 1).Set(K.WindowDroplets, 1);
            Rain(Add("wetdiff1_r", Ground, "rain"), 5).Set(K.WetSurfaces, 1).Tune("wet.wetDiffuse", 1f);
            Rain(Add("wetdiff03_r", Ground, "rain"), 5).Set(K.WetSurfaces, 1).Tune("wet.wetDiffuse", 0.3f);
            Rain(Add("dropref0_r", Ground, "rain"), 5).Set(K.WindowDroplets, 1).Tune("drop.refraction", 0f);
            Rain(Add("dropcover_r", Ground, "rain"), 5).Set(K.WindowDroplets, 1).Tune("drop.coverMax", 0.6f);
            Rain(Add("precip_lo_r", Ground, "rain"), 5).Tune("precip.maxTotal", 2000f);

            // --- Start k3res: 2560x1440, the reconstruction levels and the cost-bearing switches again, and the per-layer look keys ---
            Add("recon2_g", Ground, "recon").Set(K.CloudResolution, 2).Mode = "reconstruct";
            Add("recon3_g", Ground, "recon").Set(K.CloudResolution, 3).Mode = "reconstruct";
            Add("recon4_g", Ground, "recon").Set(K.CloudResolution, 4).Mode = "reconstruct";
            Add("recon2_a", Above, "recon").Set(K.CloudResolution, 2).Mode = "reconstruct";
            Add("recon3_a", Above, "recon").Set(K.CloudResolution, 3).Mode = "reconstruct";
            Add("recon4_a", Above, "recon").Set(K.CloudResolution, 4).Mode = "reconstruct";
            Add("entry_g", Ground, "recon").Set(K.CloudEntryRefinement, 1);
            Add("cap2_g", Ground, "step").Set(K.LayerCap, 2);
            Add("cache_g", Ground, "light").Set(K.CloudLightCache, 1).Cache = true;
            Add("lshift_g", Ground, Cloud).Special = K3Special.SheetShift;
            Add("tfair_g", Ground, Cloud).Special = K3Special.SheetTypeFair;
            Add("toverride_g", Ground, Cloud).Special = K3Special.SheetTypeOverride;
        }

        private static K3Variant AddHeightOff(string id, string view)
        {
            return Add(id, view, "t13").Set(K.CloudBaseVariation, 0).Set(K.CloudBasePhysics, 0).Set(K.CloudTopFromUpdraft, 0).Set(K.BaseRagged, 0);
        }

        // The six switches of T16 on, the distance cut at a cap distance of 100 km.
        private static K3Variant AddCullAll(string id, string view)
        {
            return Add(id, view, "cull").Set(K.CloudSkipEmpty, 1).Set(K.CloudSkipClosed, 1).Set(K.CloudLayerDistanceCut, 1)
                .Set(K.CloudStepGrowth, 1).Set(K.CloudTapLod, 1).Set(K.CloudDetailLod, 1).Set(K.CloudLayerCapDistance, 100);
        }

        private static K3Variant Storm(string id)
        {
            K3Variant v = Add(id, Horizon, "lightning").Set(K.Lightning, 1);
            v.Special = K3Special.Storm;
            return v;
        }

        // Rain at level 0.8 everywhere (the diagnostics' forceLevel and forceType), the variant's own switches on top.
        private static K3Variant Rain(K3Variant v, double settleS)
        {
            v.Set(K.ForceLevel, 0.8).Set(K.ForceType, (double)(int)ForceType.Rain);
            v.ExtraSettleS = settleS;
            return v;
        }
    }
}
