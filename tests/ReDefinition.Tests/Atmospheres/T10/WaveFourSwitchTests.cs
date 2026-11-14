using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests.Atmospheres.T10
{
    // The switches of the review of wave 4: the sun march, the look fixes of T03v2, the far view of T06, the wet overlay and the droplets of T08
    // and the lightning of T07 have a key, a help text, a contract row, a matrix rendering, and they are off where they are a quality or cost change.
    [TestClass]
    public class WaveFourSwitchTests
    {
        private static readonly string[] CloudKeys =
        {
            "cloudSunMarchLong", "cloudSunWhiteBalance", "cloudDiffusionDecay", "cloudFarHaze", "cloudFarShapeMean", "cloudFarMinSamples", "cloudFarPolicy",
            "cloudFarMapFade"
        };

        private static readonly string[] EffectKeys =
        {
            "wetSurfaces", "windowDroplets", "lightning", "lightningMaxPerSecond", "lightningBrightness", "lightningBolts", "lightningTerrainLight",
            "lightningCloudFlash", "lightningThunder", "lightningRealisticDelay"
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

        [TestCleanup]
        public void Clean()
        {
            LightningSwitches.Reset();
        }

        [TestMethod]
        public void EveryKeyHasAHelpTextAContractRowAndAMatrixRendering()
        {
            string root = RepoRoot.Root("src", "ReDefinition.csproj");
            string contract = File.ReadAllText(Path.Combine(root, "src", "Atmospheres", "CONTRACT.md"));
            string matrix = File.ReadAllText(Path.Combine(root, "tools", "benchmark_matrix.json"));
            var gaps = new List<string>();
            foreach (string key in CloudKeys.Concat(EffectKeys))
            {
                SettingDef def = AtmospheresSettings.Definition(key);
                Assert.IsNotNull(def, key);
                if (SettingHelp.For(key).Length < 40) gaps.Add(key + " (help)");
                if (!Regex.IsMatch(contract, @"^\| `" + key + @"` \|", RegexOptions.Multiline)) gaps.Add(key + " (contract)");
                if (!Regex.IsMatch(matrix, "\"id\": \"weather-" + key + "\"")) gaps.Add(key + " (matrix id)");
                if (matrix.IndexOf("\"atmo." + key + "\": \"", StringComparison.Ordinal) < 0) gaps.Add(key + " (matrix key)");
            }
            Assert.AreEqual(0, gaps.Count, string.Join(", ", gaps));
        }

        [TestMethod]
        public void TheQualityAndCostSwitchesAreOffByDefault()
        {
            foreach (string key in new[]
            {
                "cloudSunMarchLong", "cloudFarHaze", "cloudFarShapeMean", "cloudFarPolicy", "cloudFarMapFade", "wetSurfaces", "windowDroplets", "lightning"
            })
                Assert.AreEqual(0.0, AtmospheresSettings.Definition(key).Default, key + " waits for the owner's agreement");
            Assert.AreEqual(0.0, AtmospheresSettings.Definition("cloudFarMinSamples").Default);
            // The fitted look stays at its fitted values.
            Assert.AreEqual(0.0, AtmospheresSettings.Definition("cloudSunWhiteBalance").Default, "the sun colour is the sun at the cloud's altitude (E40): no white-balance compensation");
            Assert.AreEqual(new CloudLightSettings().DiffusionDecay, (float)AtmospheresSettings.Definition("cloudDiffusionDecay").Default, 1e-6f);
        }

        [TestMethod]
        public void TheSunMarchIsTheMeasuredFourKilometresUntilTheSwitchIsOn()
        {
            Assert.AreEqual(4000f, new LayerFrame().SunMarchM, "the shipped march is the one the parity rows measured");
            var f = new LayerFrame();
            OwnCloudOptions.Apply(f, Defaults());
            Assert.AreEqual(LayerFrame.SunMarchShortM, f.SunMarchM);
            OwnCloudOptions.Apply(f, Defaults("cloudSunMarchLong", 1));
            Assert.AreEqual(LayerFrame.SunMarchLongM, f.SunMarchM);
        }

        [TestMethod]
        public void TheLightSwitchesReachTheStruct()
        {
            var s = new CloudLightSettings();
            OwnCloudOptions.Apply(s, Defaults("cloudSunWhiteBalance", 1, "cloudDiffusionDecay", 0));
            Assert.IsTrue(s.SunAirWhiteBalance);
            Assert.AreEqual(0f, s.DiffusionDecay);
            OwnCloudOptions.Apply(s, Defaults());
            Assert.IsFalse(s.SunAirWhiteBalance, "off by default: the cloud gets the sun through the whole air above it");
            Assert.AreEqual(0.08f, s.DiffusionDecay, 1e-6f);
        }

        [TestMethod]
        public void TheFarViewSwitchesReachTheSettings()
        {
            var far = new FarViewSettings();
            OwnCloudOptions.Apply(far, Defaults());
            Assert.IsFalse(far.Haze);
            Assert.IsFalse(far.ShapeMean);
            Assert.AreEqual(0, far.MinSamples);
            Assert.IsFalse(far.Policy);
            OwnCloudOptions.Apply(far, Defaults("cloudFarHaze", 1, "cloudFarShapeMean", 1, "cloudFarMinSamples", 4, "cloudFarPolicy", 1));
            Assert.IsTrue(far.Haze);
            Assert.IsTrue(far.ShapeMean);
            Assert.AreEqual(4, far.MinSamples);
            Assert.IsTrue(far.Policy);
            Assert.IsFalse(OwnCloudOptions.MapFade(Defaults()));
            Assert.IsTrue(OwnCloudOptions.MapFade(Defaults("cloudFarMapFade", 1)));
        }

        [TestMethod]
        public void TheLightningKeysReachTheSwitchesUnlessAProbeHoldsThem()
        {
            LightningSwitches.Apply(Defaults());
            Assert.IsFalse(LightningSwitches.Enabled, "lightning is off by default");
            LightningSwitches.Apply(Defaults("lightning", 1, "lightningMaxPerSecond", 2, "lightningBrightness", 0.5, "lightningBolts", 0, "lightningThunder", 0,
                "lightningRealisticDelay", 0));
            Assert.IsTrue(LightningSwitches.Enabled);
            Assert.AreEqual(2, LightningSwitches.MaxBumpsPerSecond);
            Assert.AreEqual(0.5f, LightningSwitches.Brightness, 1e-6f);
            Assert.IsFalse(LightningSwitches.Bolts);
            Assert.IsFalse(LightningSwitches.Thunder);
            Assert.IsFalse(LightningSwitches.ThunderSettings.RealisticDelay);
            Assert.IsTrue(LightningSwitches.TerrainLight);
            Assert.IsTrue(LightningSwitches.CloudFlash);

            LightningSwitches.Reset();
            LightningSwitches.Manual = true;
            LightningSwitches.Enabled = true;
            LightningSwitches.Apply(Defaults());
            Assert.IsTrue(LightningSwitches.Enabled, "a probe's fields are left alone");
        }

        [TestMethod]
        public void TheLightningGreyReasonHangsOnTheLightningSwitch()
        {
            WindowEnvironment env = WindowEnvironment.Alone();
            RowState off = Dependencies.Evaluate(AtmospheresSettings.Definition("lightningBolts"), Defaults("weatherEffects", 1), env);
            Assert.IsFalse(off.Enabled);
            Assert.AreEqual(AtmospheresSettings.GreyReasons.NeedsLightning, off.ReasonId);
            Assert.IsTrue(Dependencies.Evaluate(AtmospheresSettings.Definition("lightningBolts"), Defaults("weatherEffects", 1, "lightning", 1), env).Enabled);
        }
    }
}
