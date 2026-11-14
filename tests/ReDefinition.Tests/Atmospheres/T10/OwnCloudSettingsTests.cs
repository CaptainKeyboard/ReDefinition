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
            "cloudNoiseDistanceLod", "cloudFineDetail", "cloudLightQuality", "cloudLightOctaves", "cloudPowder",
            "cloudSunTint", "cloudMoonLights", "cloudAmbientTaps", "cloudAmbientReuse", "cloudLightCache", "cloudShadows", "cloudShadowQuality",
            "cloudShadowFloor", "cloudReconMode", "cloudResolution", "cloudSkipEmpty", "cloudSkipClosed", "cloudLayerDistanceCut", "cloudStepGrowth", "cloudTapLod", "cloudDetailLod", "cloudLayerCapDistance", "cloudBaseVariation", "cloudBasePhysics", "cloudTopFromUpdraft", "baseRagged"
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
            // The product marches the span cut to the types present with the grid multiplier of OwnCloudOptions (E40 grain against TVC); the struct keeps the full count.
            Assert.AreEqual(Math.Round(expected.MaxSteps * OwnCloudOptions.GridFineDefault), frame.MaxSteps);
            Assert.AreEqual(expected.SunSteps, frame.SunSteps);
            Assert.AreEqual(expected.MinStepM, frame.MinStepM);
            Assert.AreEqual(expected.TransmittanceGrowth, frame.TransmittanceGrowth, "the product's step growth");
            Assert.AreEqual(expected.CoverageBound, frame.CoverageBound);
            Assert.AreEqual(expected.LightStride, frame.LightStride);
            Assert.AreEqual(expected.LightCutoff, frame.LightCutoff);
            Assert.AreEqual(expected.EmptyLeap, frame.EmptyLeap);
            Assert.AreEqual(expected.CheapShare, frame.CheapShare);
            Assert.AreEqual(expected.FogShare, frame.FogShare);

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
            Assert.AreEqual(0.0, AtmospheresSettings.Definition("ownClouds").Default, "the system is off until T09 accepts it");
            Assert.AreEqual(0.0, AtmospheresSettings.Definition("cloudLightCache").Default, "the light cache is a cost experiment");
        }

        [TestMethod]
        public void TheSwitchesReachTheStructs()
        {
            var v = Defaults("cloudNoise", 0, "cloudNoiseUntile", 0, "cloudNoiseCurl", 0, "cloudNoiseTier", 0, "cloudNoiseDistanceLod", 0, "cloudFineDetail", 0,
                "layerStepQuality", 0, "layerStepGrowth", 0.5, "cloudLightQuality", 0, "cloudLightOctaves", 1,
                "cloudPowder", 0.25, "cloudSunTint", 0, "cloudAmbientTaps", 1, "cloudAmbientReuse", 2, "cloudShadowQuality", 0, "cloudShadowFloor", 0.4,
                "cloudReconMode", (int)ReconMode.Full, "cloudResolution", 3);
            var noise = new NoiseSwitches();
            OwnCloudOptions.Apply(noise, v);
            Assert.IsFalse(noise.Enabled || noise.Untile || noise.Curl || noise.DistanceLod || noise.FineDetail);
            Assert.AreEqual(0, noise.SizeLevel);

            var frame = new LayerFrame();
            OwnCloudOptions.Apply(frame, v);
            Assert.AreEqual(Math.Round(32 * OwnCloudOptions.GridFineDefault), frame.MaxSteps);
            Assert.AreEqual(2, frame.SunSteps);
            Assert.AreEqual(40f, frame.MinStepM);
            Assert.AreEqual(0.5f, frame.TransmittanceGrowth);

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

        // E40 lead investigation: the ambient's analytic occlusion measures the column to the cloud type's top by default; the tuning key light.ambientColumnTop 0
        // gives the earlier column to the layer's outer altitude (the comparison row).
        [TestMethod]
        public void TheAmbientColumnEndsAtTheTypeTopUnlessTheTuningKeySaysOtherwise()
        {
            Tuning.ClearOverrides();
            Tuning.Instance.BeginFrame(5100001);
            var light = new CloudLightSettings { AmbientColumnToTypeTop = false };
            OwnCloudOptions.Apply(light, Defaults());
            Assert.IsTrue(light.AmbientColumnToTypeTop, "the default");
            try
            {
                Tuning.SetOverride(OwnCloudOptions.TuneAmbientColumnTop, 0f);
                Tuning.Instance.BeginFrame(5100002);
                OwnCloudOptions.Apply(light, Defaults());
                Assert.IsFalse(light.AmbientColumnToTypeTop);
            }
            finally
            {
                Tuning.ClearOverrides();
                Tuning.Instance.BeginFrame(5100003);
            }
            OwnCloudOptions.Apply(light, Defaults());
            Assert.IsTrue(light.AmbientColumnToTypeTop);
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
        public void TheResolutionSliderPicksTheReconstructionLevel()
        {
            SettingDef slider = AtmospheresSettings.Definition("cloudResolution");
            Assert.AreEqual(1.0, slider.Default, "full resolution until the owner agrees to a lower level");
            Assert.AreEqual(1.0, slider.Min);
            Assert.AreEqual((double)ReconPattern.MaxLevel, slider.Max, "the slider offers the levels the pattern has a stride for");
            var recon = new ReconSettings();
            Tuning.ClearOverrides();
            NextFrame();
            // Level 1 runs the half-size march with the history of recon.accumulate (the default 0.2) through the mode Low; at 0 it is the plain pass (ReconMode.Full).
            OwnCloudOptions.Apply(recon, Defaults());
            Assert.AreEqual(ReconMode.Low, recon.Mode);
            Tuning.SetOverride(OwnCloudOptions.TuneAccumulate, 0f);
            NextFrame();
            OwnCloudOptions.Apply(recon, Defaults());
            Assert.AreEqual(ReconMode.Full, recon.Mode);
            for (int level = 2; level <= ReconPattern.MaxLevel; level++)
            {
                OwnCloudOptions.Apply(recon, Defaults("cloudResolution", level));
                Assert.AreEqual(ReconMode.Block, recon.Mode, "level " + level);
                Assert.AreEqual(level, recon.Block, "level " + level);
            }
            // The mode setting's Block is no longer the way to a block: the slider at 1 means every pixel.
            OwnCloudOptions.Apply(recon, Defaults("cloudReconMode", (int)ReconMode.Block, "cloudResolution", 1));
            Assert.AreEqual(ReconMode.Full, recon.Mode);
            // Low (prototype B) overrides the slider.
            OwnCloudOptions.Apply(recon, Defaults("cloudReconMode", (int)ReconMode.Low, "cloudResolution", 3));
            Assert.AreEqual(ReconMode.Low, recon.Mode);
            Assert.AreEqual(1, ReconPattern.ClampLevel(-5));
            Assert.AreEqual(ReconPattern.MaxLevel, ReconPattern.ClampLevel(99));
            Assert.IsTrue(TierRows.NotSet.ContainsKey("cloudResolution"), "the tier mapping is open and says why");
            Assert.AreEqual(AtmospheresSettings.ProfileRows.Volume, slider.ProfileRow);
        }

        [TestMethod]
        public void EveryLevelOfTheSliderHasAStrideThatVisitsAllCells()
        {
            for (int n = 2; n <= ReconPattern.MaxLevel; n++)
            {
                int cells = n * n, stride = -1;
                // BlockOffset(n, 1) is the cell after the stride: its linear index is the stride.
                int x, y;
                ReconPattern.BlockOffset(n, 1, out x, out y);
                stride = y * n + x;
                Assert.IsTrue(stride > 1 && Gcd(stride, cells) == 1, "level " + n + " stride " + stride + " is coprime with " + cells);
                var seen = new HashSet<int>();
                for (int i = 0; i < cells; i++)
                {
                    ReconPattern.BlockOffset(n, i, out x, out y);
                    seen.Add(y * n + x);
                }
                Assert.AreEqual(cells, seen.Count, "level " + n);
            }
        }

        private static int Gcd(int a, int b)
        {
            return b == 0 ? a : Gcd(b, a % b);
        }

        [TestMethod]
        public void TheReconstructionRunsOnTheModeAndTheLevelAlone()
        {
            var recon = new ReconSettings();
            OwnCloudOptions.Apply(recon, Defaults());
            var driver = new LayerReconstructDriver();
            Assert.IsTrue(driver.Configure((key, fallback) => fallback, recon), "the history (recon.accumulate) runs the reconstruction");
            Tuning.ClearOverrides();
            Tuning.SetOverride(OwnCloudOptions.TuneAccumulate, 0f);
            NextFrame();
            OwnCloudOptions.Apply(recon, Defaults());
            Assert.IsFalse(driver.Configure((key, fallback) => fallback, recon), "no history, level 1: the pass draws as it did");
            Tuning.ClearOverrides();
            NextFrame();
        }

        [TestMethod]
        public void TheTuningKeysMoveTheCostControlsAndTheLookConstants()
        {
            Tuning.SetOverride(OwnCloudOptions.TuneLightStride, 1f);
            Tuning.SetOverride(OwnCloudOptions.TuneCoverageBound, 0f);
            Tuning.SetOverride(OwnCloudOptions.TuneLightCutoff, 0f);
            Tuning.SetOverride(OwnCloudOptions.TuneEmptyLeap, 4f);
            Tuning.SetOverride(OwnCloudOptions.TuneMsBrightness, 2f);
            NextFrame();
            var frame = new LayerFrame();
            OwnCloudOptions.Apply(frame, Defaults());
            Assert.AreEqual(1, frame.LightStride);
            Assert.IsFalse(frame.CoverageBound);
            Assert.AreEqual(0f, frame.LightCutoff);
            Assert.AreEqual(4f, frame.EmptyLeap);
            var light = new CloudLightSettings();
            OwnCloudOptions.Apply(light, Defaults());
            Assert.AreEqual(2f, light.MsBrightness);
            Tuning.ClearOverrides();
            NextFrame();
            OwnCloudOptions.Apply(frame, Defaults());
            Assert.AreEqual(new LayerFrame().LightStride, frame.LightStride);
        }

        // E40 ground cost: the size of the history path's march target and its sub-pixel jitter are tuning keys with the compiled values as defaults.
        [TestMethod]
        public void TheReconKeysSetTheMarchTargetsSizeAndJitterWithoutChangingTheDefault()
        {
            Tuning.ClearOverrides();
            NextFrame();
            var recon = new ReconSettings();
            OwnCloudOptions.Apply(recon, Defaults());
            Assert.AreEqual(ReconMode.Low, recon.Mode);
            Assert.AreEqual(new LayerFrame().Resolution, recon.Resolution);
            Assert.AreEqual(0.5f, recon.Resolution);
            Assert.IsFalse(recon.Jitter);
            Tuning.SetOverride(OwnCloudOptions.TuneReconResolution, 0.4f);
            Tuning.SetOverride(OwnCloudOptions.TuneReconJitter, 1f);
            NextFrame();
            OwnCloudOptions.Apply(recon, Defaults());
            Assert.AreEqual(ReconMode.Low, recon.Mode);
            Assert.AreEqual(0.4f, recon.Resolution);
            Assert.IsTrue(recon.Jitter);
            Assert.AreEqual(0.2f, recon.Accumulate);
            // The mode setting Low reads the same keys; the numbers stay inside 0.1 to 1.
            OwnCloudOptions.Apply(recon, Defaults("cloudReconMode", (int)ReconMode.Low));
            Assert.AreEqual(0.4f, recon.Resolution);
            Assert.IsTrue(recon.Jitter);
            Tuning.SetOverride(OwnCloudOptions.TuneReconResolution, 0.01f);
            NextFrame();
            OwnCloudOptions.Apply(recon, Defaults());
            Assert.AreEqual(0.1f, recon.Resolution);
            Tuning.SetOverride(OwnCloudOptions.TuneReconResolution, 7f);
            NextFrame();
            OwnCloudOptions.Apply(recon, Defaults());
            Assert.AreEqual(1f, recon.Resolution);
            // The block levels have their own pattern and ignore both keys.
            OwnCloudOptions.Apply(recon, Defaults("cloudResolution", 2));
            Assert.AreEqual(ReconMode.Block, recon.Mode);
            Assert.AreEqual(0f, recon.Accumulate);
            Tuning.ClearOverrides();
            NextFrame();
            OwnCloudOptions.Apply(recon, Defaults());
            Assert.AreEqual(0.5f, recon.Resolution);
            Assert.IsFalse(recon.Jitter);
        }

        // E40 grain: the keys of the grain package reach the frame and the reconstruction; without them both are as they were.
        [TestMethod]
        public void TheGrainKeysMoveTheGridAndTheHistory()
        {
            Tuning.ClearOverrides();
            NextFrame();
            var frame = new LayerFrame();
            OwnCloudOptions.Apply(frame, Defaults());
            Assert.AreEqual(Math.Round(48 * OwnCloudOptions.GridFineDefault), frame.MaxSteps);
            var recon = new ReconSettings();
            OwnCloudOptions.Apply(recon, Defaults());
            // The default is the history of weight 0.2 (the owner delegated the choice after the E40 grain measurement); the finer grid stays off.
            Assert.AreEqual(0.2f, OwnCloudOptions.AccumulateDefault);
            Assert.AreEqual(ReconMode.Low, recon.Mode);
            Assert.AreEqual(0.2f, recon.Accumulate);

            Tuning.SetOverride(OwnCloudOptions.TuneGridFine, 4f);
            Tuning.SetOverride(OwnCloudOptions.TuneAccumulate, 0.2f);
            NextFrame();
            OwnCloudOptions.Apply(frame, Defaults());
            Assert.AreEqual(192, frame.MaxSteps);
            // The history belongs to level 1: it runs the half-size march through the mode Low; the block levels keep their own.
            OwnCloudOptions.Apply(recon, Defaults());
            Assert.AreEqual(ReconMode.Low, recon.Mode);
            Assert.AreEqual(0.2f, recon.Accumulate);
            Assert.AreEqual(new LayerFrame().Resolution, recon.Resolution);
            Assert.IsFalse(recon.Jitter);
            OwnCloudOptions.Apply(recon, Defaults("cloudResolution", 2));
            Assert.AreEqual(ReconMode.Block, recon.Mode);
            Assert.AreEqual(0f, recon.Accumulate);
            // The grid's multiplier stops at the shader's 256 grid points.
            Tuning.SetOverride(OwnCloudOptions.TuneGridFine, 8f);
            NextFrame();
            OwnCloudOptions.Apply(frame, Defaults());
            Assert.AreEqual(256, frame.MaxSteps);
            // The history off by its key, whatever the default is.
            Tuning.SetOverride(OwnCloudOptions.TuneAccumulate, 0f);
            NextFrame();
            OwnCloudOptions.Apply(recon, Defaults());
            Assert.AreEqual(ReconMode.Full, recon.Mode);
            Assert.AreEqual(0f, recon.Accumulate);
            Tuning.ClearOverrides();
            NextFrame();
        }

        // E40 grain against TVC: the span cut is off by default and its key switches it on; the grid multiplier reaches below 1.
        // E40 step (c): the step offset is the gradient noise by default (2); the key layers.ditherSequence 1 gives the white hash back; values outside 1 to 2 are clamped.
        [TestMethod]
        public void TheDitherSequenceIsTheGradientNoiseByDefaultWithAKeyForTheHash()
        {
            Tuning.ClearOverrides();
            NextFrame();
            var frame = new LayerFrame();
            Assert.AreEqual(2, frame.DitherSequence);
            OwnCloudOptions.Apply(frame, Defaults());
            Assert.AreEqual(OwnCloudOptions.DitherSequenceDefault, frame.DitherSequence);
            Tuning.SetOverride(OwnCloudOptions.TuneDitherSequence, 1f);
            NextFrame();
            OwnCloudOptions.Apply(frame, Defaults());
            Assert.AreEqual(1, frame.DitherSequence);
            Tuning.SetOverride(OwnCloudOptions.TuneDitherSequence, 0f);
            NextFrame();
            OwnCloudOptions.Apply(frame, Defaults());
            Assert.AreEqual(1, frame.DitherSequence, "0 (a new offset per frame) is gone: clamped to 1");
            Tuning.ClearOverrides();
            NextFrame();
        }

        [TestMethod]
        public void TheSpanCutIsOffByDefaultWithAKeyOn()
        {
            Tuning.ClearOverrides();
            NextFrame();
            var frame = new LayerFrame();
            Assert.IsFalse(frame.SpanTight);
            OwnCloudOptions.Apply(frame, Defaults());
            Assert.AreEqual(OwnCloudOptions.SpanTightDefault, frame.SpanTight);
            Assert.AreEqual(Math.Round(48 * OwnCloudOptions.GridFineDefault), frame.MaxSteps);

            Tuning.SetOverride(OwnCloudOptions.TuneSpanTight, 1f);
            Tuning.SetOverride(OwnCloudOptions.TuneGridFine, 1f);
            NextFrame();
            OwnCloudOptions.Apply(frame, Defaults());
            Assert.IsTrue(frame.SpanTight);
            Assert.AreEqual(48, frame.MaxSteps);

            Tuning.SetOverride(OwnCloudOptions.TuneGridFine, 0.5f);
            NextFrame();
            OwnCloudOptions.Apply(frame, Defaults());
            Assert.AreEqual(24, frame.MaxSteps);
            Tuning.SetOverride(OwnCloudOptions.TuneGridFine, 0.01f);
            NextFrame();
            OwnCloudOptions.Apply(frame, Defaults());
            Assert.AreEqual(12, frame.MaxSteps);
            Tuning.ClearOverrides();
            NextFrame();
        }

        // E40 L2 package A2: layers.dropEmpty is a default (a layer whose map cannot reach the threshold in view is not marched), off by its key; the compiled frame is the pass as it was.
        [TestMethod]
        public void TheDropEmptyKeyIsADefaultWithAKeyBack()
        {
            Tuning.ClearOverrides();
            NextFrame();
            var frame = new LayerFrame();
            Assert.IsFalse(frame.DropEmpty);
            Assert.AreEqual("layers.dropEmpty", OwnCloudOptions.TuneDropEmpty);
            OwnCloudOptions.Apply(frame, Defaults());
            Assert.IsTrue(OwnCloudOptions.DropEmptyDefault);
            Assert.AreEqual(OwnCloudOptions.DropEmptyDefault, frame.DropEmpty);
            Tuning.SetOverride(OwnCloudOptions.TuneDropEmpty, 0f);
            NextFrame();
            OwnCloudOptions.Apply(frame, Defaults());
            Assert.IsFalse(frame.DropEmpty);
            // Independent of the span cut.
            Tuning.SetOverride(OwnCloudOptions.TuneDropEmpty, 1f);
            Tuning.SetOverride(OwnCloudOptions.TuneSpanTight, 0f);
            NextFrame();
            OwnCloudOptions.Apply(frame, Defaults());
            Assert.IsTrue(frame.DropEmpty);
            Assert.IsFalse(frame.SpanTight);
            Tuning.ClearOverrides();
            NextFrame();
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
            Assert.IsNull(Reason("cloudResolution", own, eve));
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
            foreach (string tuning in new[] { OwnCloudOptions.TuneCoverageBound, OwnCloudOptions.TuneLightStride, OwnCloudOptions.TuneLightCutoff, OwnCloudOptions.TuneEmptyLeap,
                                              OwnCloudOptions.TuneGridFine, OwnCloudOptions.TuneAccumulate, OwnCloudOptions.TuneSpanTight, OwnCloudOptions.TuneDitherSequence })
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
