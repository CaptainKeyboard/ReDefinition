using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using UnityEngine;

namespace ReDefinition.Tests.Atmospheres.T10
{
    // The settings of the own cloud system (package T10): the completeness of the rows (default, tooltip, range, reason, matrix row), the
    // defaults against the compiled defaults of the option structs, the quality-reducing choices off, the wiring into the structs and the
    // greyed-out chain of the window.
    [TestClass]
    public class OwnCloudSettingsTests
    {
        // The keys this package declares.
        private static readonly string[] OwnKeys =
        {
            "ownClouds", "layerCap", "layerStepQuality", "layerStepGrowth", "cloudNoise", "cloudNoiseUntile", "cloudNoiseCurl", "cloudNoiseTier",
            "cloudNoiseDistanceLod", "cloudFineDetail", "cloudEntryRefinement", "cloudHashDither", "cloudLightQuality", "cloudLightOctaves", "cloudPowder",
            "cloudSunTint", "cloudMoonLights", "cloudAmbientTaps", "cloudAmbientReuse", "cloudLightCache", "cloudShadows", "cloudShadowQuality",
            "cloudShadowFloor", "cloudReconMode", "cloudReconBlock", "cloudBaseVariation", "cloudBasePhysics", "cloudTopFromUpdraft", "baseRagged"
        };

        private static Func<string, double> Defaults(params object[] pairs)
        {
            var overrides = new Dictionary<string, double>(StringComparer.Ordinal);
            for (int i = 0; i < pairs.Length; i += 2) overrides[(string)pairs[i]] = Convert.ToDouble(pairs[i + 1]);
            return key =>
            {
                double value;
                if (overrides.TryGetValue(key, out value)) return value;
                SettingDef def = AtmospheresSettings.Definition(key);
                return def.Kind == SettingKind.Text ? 0.0 : def.Default;
            };
        }

        private static string Reason(string key, Func<string, double> v, WindowEnvironment env)
        {
            RowState state = Dependencies.Evaluate(AtmospheresSettings.Definition(key), v, env);
            return state.Enabled ? null : state.ReasonId;
        }

        // The tuning table swaps its overrides in at a frame boundary: a test moves the boundary on by hand.
        private static int tuningFrame = 900000;

        private static void NextFrame()
        {
            Tuning.Instance.BeginFrame(++tuningFrame);
        }

        [TestCleanup]
        public void Clean()
        {
            Tuning.ClearOverrides();
            NextFrame();
        }

        [TestMethod]
        public void EveryOwnCloudKeyIsDeclaredOnceWithTheCloudsCategoryAHelpTextAndAContractRow()
        {
            string contract = File.ReadAllText(Path.Combine(RepoRoot.Root("src", "ReDefinition.csproj"), "src", "Atmospheres", "CONTRACT.md"));
            foreach (string key in OwnKeys)
            {
                SettingDef def = AtmospheresSettings.Definition(key);
                Assert.IsNotNull(def, key);
                Assert.AreEqual(AtmospheresSettings.Categories.Clouds, def.Category, key);
                Assert.IsTrue(SettingHelp.For(key).Length >= 40, key + " has a short help text");
                Assert.IsTrue(Regex.IsMatch(contract, @"^\| `" + key + @"` \|", RegexOptions.Multiline), key + " is not in CONTRACT.md");
                Assert.IsTrue(def.GreyReason.Length > 0, key + " has no greyed-out reason");
            }
            Assert.AreEqual(OwnKeys.Length, OwnKeys.Distinct().Count());
        }

        [TestMethod]
        public void TheDefaultsEqualTheCompiledDefaultsOfTheOptionStructs()
        {
            var noise = new NoiseSwitches();
            OwnCloudOptions.Apply(noise, Defaults());
            Assert.AreEqual(new NoiseSwitches().Enabled, noise.Enabled);
            Assert.AreEqual(new NoiseSwitches().Untile, noise.Untile);
            Assert.AreEqual(new NoiseSwitches().Curl, noise.Curl);
            Assert.AreEqual(new NoiseSwitches().DistanceLod, noise.DistanceLod);
            Assert.AreEqual(new NoiseSwitches().FineDetail, noise.FineDetail);
            Assert.AreEqual(new NoiseSwitches().SizeLevel, noise.SizeLevel);

            var frame = new LayerFrame();
            var expected = new LayerFrame();
            expected.UseProductCost();
            OwnCloudOptions.Apply(frame, Defaults());
            Assert.AreEqual(expected.MaxSteps, frame.MaxSteps);
            Assert.AreEqual(expected.SunSteps, frame.SunSteps);
            Assert.AreEqual(expected.MinStepM, frame.MinStepM);
            Assert.AreEqual(expected.TransmittanceGrowth, frame.TransmittanceGrowth, "the product's step growth");
            Assert.AreEqual(expected.HashDither, frame.HashDither);
            Assert.AreEqual(expected.CoverageBound, frame.CoverageBound);
            Assert.AreEqual(expected.LightStride, frame.LightStride);
            Assert.AreEqual(expected.LightCutoff, frame.LightCutoff);
            Assert.AreEqual(expected.EmptyLeap, frame.EmptyLeap);
            Assert.AreEqual(expected.CheapShare, frame.CheapShare);
            Assert.AreEqual(expected.FogShare, frame.FogShare);
            Assert.AreEqual(0f, frame.RefineBudget, "the entry refinement is off until the owner agrees");

            var light = new CloudLightSettings();
            var lightExpected = new CloudLightSettings();
            OwnCloudOptions.Apply(light, Defaults());
            Assert.AreEqual(lightExpected.Octaves, light.Octaves);
            Assert.AreEqual(lightExpected.PowderStrength, light.PowderStrength, 1e-6f);
            Assert.AreEqual(lightExpected.SunAirHeightM, light.SunAirHeightM);
            Assert.AreEqual(lightExpected.AmbientOcclusionTaps, light.AmbientOcclusionTaps);
            Assert.AreEqual(lightExpected.UpReuse, light.UpReuse);
            Assert.AreEqual(lightExpected.OctaveA, light.OctaveA);
            Assert.AreEqual(lightExpected.MsBrightness, light.MsBrightness);
            Assert.AreEqual(lightExpected.AmbientGain, light.AmbientGain);
            Assert.AreEqual(4, light.SunStepsOverride, "High: 4 taps towards the sun");

            var shadow = new CloudShadowFrame();
            OwnCloudOptions.Apply(shadow, Defaults());
            Assert.AreEqual(new CloudShadowFrame().StepsPerSpan, shadow.StepsPerSpan);
            Assert.AreEqual(new CloudShadowFrame().DiscTaps, shadow.DiscTaps);
            Assert.AreEqual(CloudShadowSwitches.FloorDefault, shadow.Floor);
            Assert.AreEqual(CloudShadowSwitches.ShadowsDefault, AtmospheresSettings.Definition("cloudShadows").Default != 0.0);
            Assert.AreEqual(LayerSwitches.OwnCloudsDefault, AtmospheresSettings.Definition("ownClouds").Default != 0.0);
            Assert.AreEqual(LayerSwitches.LayerCapDefault, (int)AtmospheresSettings.Definition("layerCap").Default);
        }

        [TestMethod]
        public void TheDefaultQualityIsTheFrameDefaultAndTheQualityReducingChoicesAreOff()
        {
            // layerStepQuality Medium = the frame's own 48 steps, 3 sun steps, 25 m.
            int maxSteps, sunSteps;
            float minStep;
            LayerSwitches.StepQuality((int)AtmospheresSettings.Definition("layerStepQuality").Default, out maxSteps, out sunSteps, out minStep);
            var frame = new LayerFrame();
            Assert.AreEqual(frame.MaxSteps, maxSteps);
            Assert.AreEqual(frame.SunSteps, sunSteps);
            Assert.AreEqual(frame.MinStepM, minStep);
            Assert.AreEqual((double)(int)ReconMode.Full, AtmospheresSettings.Definition("cloudReconMode").Default, "no reconstruction by default");
            Assert.AreEqual(0.0, AtmospheresSettings.Definition("cloudEntryRefinement").Default, "no entry refinement by default");
            Assert.AreEqual(0.0, AtmospheresSettings.Definition("ownClouds").Default, "the system is off until T09 accepts it");
            Assert.AreEqual(0.0, AtmospheresSettings.Definition("cloudLightCache").Default, "the light cache is a cost experiment");
        }

        [TestMethod]
        public void TheSwitchesReachTheStructs()
        {
            var v = Defaults("cloudNoise", 0, "cloudNoiseUntile", 0, "cloudNoiseCurl", 0, "cloudNoiseTier", 0, "cloudNoiseDistanceLod", 0, "cloudFineDetail", 0,
                "layerStepQuality", 0, "layerStepGrowth", 0.5, "cloudHashDither", 0, "cloudEntryRefinement", 1, "cloudLightQuality", 0, "cloudLightOctaves", 1,
                "cloudPowder", 0.25, "cloudSunTint", 0, "cloudAmbientTaps", 1, "cloudAmbientReuse", 2, "cloudShadowQuality", 0, "cloudShadowFloor", 0.4,
                "cloudReconMode", (int)ReconMode.Block, "cloudReconBlock", 3);
            var noise = new NoiseSwitches();
            OwnCloudOptions.Apply(noise, v);
            Assert.IsFalse(noise.Enabled || noise.Untile || noise.Curl || noise.DistanceLod || noise.FineDetail);
            Assert.AreEqual(0, noise.SizeLevel);

            var frame = new LayerFrame();
            OwnCloudOptions.Apply(frame, v);
            Assert.AreEqual(32, frame.MaxSteps);
            Assert.AreEqual(2, frame.SunSteps);
            Assert.AreEqual(40f, frame.MinStepM);
            Assert.AreEqual(0.5f, frame.TransmittanceGrowth);
            Assert.IsFalse(frame.HashDither);
            Assert.AreEqual(OwnCloudOptions.EntryRefineBudget, frame.RefineBudget);

            var light = new CloudLightSettings();
            OwnCloudOptions.Apply(light, v);
            Assert.AreEqual(1, light.Octaves, "the setting caps the quality level's octaves");
            Assert.AreEqual(2, light.SunStepsOverride);
            Assert.AreEqual(0.25f, light.PowderStrength, 1e-6f);
            Assert.AreEqual(0f, light.SunAirHeightM, "no sun tint: the frame's sun colour unchanged");
            Assert.IsTrue(light.AmbientOcclusionTaps);
            Assert.AreEqual(2, light.UpReuse);

            var shadow = new CloudShadowFrame();
            OwnCloudOptions.Apply(shadow, v);
            Assert.AreEqual(4, shadow.StepsPerSpan);
            Assert.AreEqual(1, shadow.DiscTaps);
            Assert.AreEqual(0.4f, shadow.Floor, 1e-6f);

            var recon = new ReconSettings();
            OwnCloudOptions.Apply(recon, v);
            Assert.AreEqual(ReconMode.Block, recon.Mode);
            Assert.AreEqual(3, recon.Block);
        }

        [TestMethod]
        public void TheOctavesNeverExceedTheQualityLevel()
        {
            var light = new CloudLightSettings();
            OwnCloudOptions.Apply(light, Defaults("cloudLightQuality", 0, "cloudLightOctaves", 4));
            Assert.AreEqual(2, light.Octaves, "Low allows 2 octaves");
            OwnCloudOptions.Apply(light, Defaults("cloudLightQuality", 1, "cloudLightOctaves", 4));
            Assert.AreEqual(3, light.Octaves);
        }

        [TestMethod]
        public void TheMoonLightsAboveTheCountAreSwitchedOff()
        {
            var light = new CloudLightSettings();
            for (int i = 0; i < CloudLightSettings.MaxLights; i++) light.Lights[i].Colour = new Color(1f, 1f, 1f, 1f);
            OwnCloudOptions.LimitMoonLights(light, Defaults("cloudMoonLights", 1));
            Assert.AreEqual(1f, light.Lights[0].Colour.r);
            Assert.AreEqual(0f, light.Lights[1].Colour.r);
            OwnCloudOptions.LimitMoonLights(light, Defaults("cloudMoonLights", 0));
            Assert.AreEqual(0f, light.Lights[0].Colour.r);
        }

        [TestMethod]
        public void TheTuningKeysMoveTheCostControlsAndTheLookConstants()
        {
            Tuning.SetOverride(OwnCloudOptions.TuneLightStride, 1f);
            Tuning.SetOverride(OwnCloudOptions.TuneCoverageBound, 0f);
            Tuning.SetOverride(OwnCloudOptions.TuneLightCutoff, 0f);
            Tuning.SetOverride(OwnCloudOptions.TuneEmptyLeap, 4f);
            Tuning.SetOverride(OwnCloudOptions.TuneMsBrightness, 2f);
            Tuning.SetOverride(OwnCloudOptions.TuneRefineBudget, 1f);
            NextFrame();
            var frame = new LayerFrame();
            OwnCloudOptions.Apply(frame, Defaults("cloudEntryRefinement", 1));
            Assert.AreEqual(1, frame.LightStride);
            Assert.IsFalse(frame.CoverageBound);
            Assert.AreEqual(0f, frame.LightCutoff);
            Assert.AreEqual(4f, frame.EmptyLeap);
            Assert.AreEqual(1f, frame.RefineBudget);
            var light = new CloudLightSettings();
            OwnCloudOptions.Apply(light, Defaults());
            Assert.AreEqual(2f, light.MsBrightness);
            Tuning.ClearOverrides();
            NextFrame();
            OwnCloudOptions.Apply(frame, Defaults());
            Assert.AreEqual(new LayerFrame().LightStride, frame.LightStride);
        }

        [TestMethod]
        public void ThePassDrawsOnlyWhereTheSwitchIsOnAndTheSourceIsOurs()
        {
            Func<string, double> off = Defaults();
            Func<string, double> on = Defaults("ownClouds", 1);
            Assert.IsFalse(OwnCloudOptions.Active(off, CloudSourceKind.Ours));
            Assert.IsTrue(OwnCloudOptions.Active(on, CloudSourceKind.Ours));
            Assert.IsFalse(OwnCloudOptions.Active(on, CloudSourceKind.Eve));
            Assert.IsFalse(OwnCloudOptions.Active(on, CloudSourceKind.Both));
            Assert.IsFalse(OwnCloudOptions.Active(on, CloudSourceKind.None));
            Assert.IsTrue(OwnCloudOptions.ShadowActive(on, CloudSourceKind.Ours, 3));
            Assert.IsFalse(OwnCloudOptions.ShadowActive(Defaults("ownClouds", 1, "cloudShadows", 0), CloudSourceKind.Ours, 3));
            Assert.IsFalse(OwnCloudOptions.ShadowActive(on, CloudSourceKind.Eve, 3));
            Assert.AreEqual(2, OwnCloudOptions.LayersToDraw(Defaults("layerCap", 2), 4));
            Assert.AreEqual(3, OwnCloudOptions.LayersToDraw(Defaults("layerCap", 4), 3));
        }

        [TestMethod]
        public void TheWindowGreysTheRowsRootFirst()
        {
            WindowEnvironment eve = WindowEnvironment.Full();
            WindowEnvironment alone = WindowEnvironment.Alone();
            Func<string, double> master = Defaults("weatherEffects", 1);
            // EVE installed and the source Eve: the system does not draw; every row names the source.
            Assert.AreEqual(AtmospheresSettings.GreyReasons.NeedsOwnSource, Reason("ownClouds", master, eve));
            Assert.AreEqual(AtmospheresSettings.GreyReasons.NeedsOwnSource, Reason("cloudNoise", master, eve));
            // Without EVE the source in force is Ours: the switch is open, the rows wait for the switch.
            Assert.IsNull(Reason("ownClouds", master, alone));
            Assert.AreEqual(AtmospheresSettings.GreyReasons.NeedsOwnClouds, Reason("cloudNoise", master, alone));
            Func<string, double> own = Defaults("weatherEffects", 1, "cloudSource", (int)CloudSourceKind.Ours, "ownClouds", 1);
            Assert.IsNull(Reason("ownClouds", own, eve));
            Assert.IsNull(Reason("cloudNoise", own, eve));
            Assert.IsNull(Reason("cloudNoiseTier", own, eve));
            // A branch switch off greys its rows with its own reason; the switch itself stays open.
            Func<string, double> noNoise = Defaults("weatherEffects", 1, "cloudSource", (int)CloudSourceKind.Ours, "ownClouds", 1, "cloudNoise", 0);
            Assert.IsNull(Reason("cloudNoise", noNoise, eve));
            Assert.AreEqual(AtmospheresSettings.GreyReasons.NeedsCloudNoise, Reason("cloudNoiseCurl", noNoise, eve));
            Assert.AreEqual(AtmospheresSettings.GreyReasons.NeedsCloudNoise, Reason("cloudFineDetail", noNoise, eve));
            Func<string, double> noShadow = Defaults("weatherEffects", 1, "cloudSource", (int)CloudSourceKind.Ours, "ownClouds", 1, "cloudShadows", 0);
            Assert.AreEqual(AtmospheresSettings.GreyReasons.NeedsCloudShadows, Reason("cloudShadowFloor", noShadow, eve));
            Assert.AreEqual(AtmospheresSettings.GreyReasons.NeedsAmbientTaps, Reason("cloudAmbientReuse", own, eve));
            Assert.IsNull(Reason("cloudAmbientReuse", Defaults("weatherEffects", 1, "cloudSource", (int)CloudSourceKind.Ours, "ownClouds", 1, "cloudAmbientTaps", 1), eve));
            Assert.AreEqual(AtmospheresSettings.GreyReasons.NeedsReconstruction, Reason("cloudReconBlock", own, eve));
            Assert.IsNull(Reason("cloudReconBlock", Defaults("weatherEffects", 1, "cloudSource", (int)CloudSourceKind.Ours, "ownClouds", 1, "cloudReconMode", (int)ReconMode.Block), eve));
            // The master switch is the root of all of them.
            Assert.AreEqual(AtmospheresSettings.GreyReasons.NeedsMaster, Reason("cloudNoise", Defaults(), eve));
        }

        [TestMethod]
        public void TheRowsAreOutOfTheTierProfileForTheNamedReason()
        {
            foreach (SettingDef def in AtmospheresSettings.Definitions.Where(d => OwnKeys.Contains(d.Key) && d.ProfileRow.Length > 0))
            {
                string reason;
                Assert.IsTrue(TierRows.NotSet.TryGetValue(def.Key, out reason) && reason.Length > 0, def.Key);
            }
        }

        [TestMethod]
        public void EveryOwnCloudSettingAndTuningKeyHasARenderingInTheBenchmarkMatrix()
        {
            string json = File.ReadAllText(Path.Combine(RepoRoot.Root("src", "ReDefinition.csproj"), "tools", "benchmark_matrix.json"));
            var gaps = new List<string>();
            foreach (string key in OwnKeys)
            {
                if (json.IndexOf("\"id\": \"weather-" + key + "\"", StringComparison.Ordinal) < 0 && !Regex.IsMatch(json, "\"id\": \"weather-" + key + @"\w*""")) gaps.Add(key + " (id)");
                if (json.IndexOf("\"atmo." + key + "\": \"", StringComparison.Ordinal) < 0) gaps.Add(key + " (key)");
            }
            foreach (string tuning in new[] { OwnCloudOptions.TuneCoverageBound, OwnCloudOptions.TuneLightStride, OwnCloudOptions.TuneLightCutoff, OwnCloudOptions.TuneEmptyLeap, OwnCloudOptions.TuneRefineBudget })
            {
                if (json.IndexOf("\"atmo." + tuning + "\"", StringComparison.Ordinal) < 0) gaps.Add(tuning);
            }
            Assert.AreEqual(0, gaps.Count, "no rendering in tools/benchmark_matrix.json for: " + string.Join(", ", gaps));
            // A start of 20 minutes holds about 19 renderings: neither of the two groups is larger.
            foreach (string group in new[] { "weather-5", "weather-6" })
            {
                int at = json.IndexOf("\"id\": \"" + group + "\"", StringComparison.Ordinal);
                int end = json.IndexOf("\n    }", at, StringComparison.Ordinal);
                int rows = Regex.Matches(json.Substring(at, end - at), "\\{\"id\": \"weather-").Count;
                Assert.IsTrue(rows >= 10 && rows <= 19, group + " has " + rows + " renderings");
            }
        }

        [TestMethod]
        public void TheMatrixDefaultsOfTheOwnCloudKeysEqualTheTableDefaults()
        {
            string json = File.ReadAllText(Path.Combine(RepoRoot.Root("src", "ReDefinition.csproj"), "tools", "benchmark_matrix.json"));
            // The base of weather-5 and weather-6 is the system on, every other key at its table default.
            int at = json.IndexOf("\"id\": \"weather-6\"", StringComparison.Ordinal);
            string group = json.Substring(at, json.IndexOf("\"tweaks\"", at, StringComparison.Ordinal) - at);
            foreach (Match m in Regex.Matches(group, "\"atmo\\.(?<key>[A-Za-z]+)\": \"(?<value>[^\"]*)\""))
            {
                SettingDef def = AtmospheresSettings.Definition(m.Groups["key"].Value);
                if (def == null || !OwnKeys.Contains(def.Key) || def.Key == "ownClouds") continue;
                Assert.AreEqual(def.Format(def.Default), m.Groups["value"].Value, def.Key);
            }
        }
    }
}
