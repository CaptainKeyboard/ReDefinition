using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests.Atmospheres.T18
{
    // The switches of the ray-side pack (package T18): off by default, a help text, a tier reason, a matrix row, the tuning numbers within their ranges.
    [TestClass]
    public class RtOwnCloudsOptionsTests
    {
        [TestMethod]
        public void BothKeysAreOffByDefaultAndHaveHelpAndNoTierRow()
        {
            foreach (string key in new[] { "rtOwnClouds", "rtOwnCloudsLayerCap" })
            {
                SettingDef def = AtmospheresSettings.Definition(key);
                Assert.AreEqual(0.0, def.Default, key + " is off until the owner agrees");
                // No profile row: no tier sets either key, and TierRows.NotSet names only keys that have a row.
                Assert.IsFalse(TierRows.NotSet.ContainsKey(key), key);
                Assert.AreEqual("", def.ProfileRow ?? "", key);
                string help = SettingHelp.For(key);
                Assert.IsTrue(help != null && help.Length > 20, key + " has a help text");
                Assert.AreEqual(AtmospheresSettings.GreyReasons.NeedsOwnClouds, def.GreyReason, key);
            }
            Assert.AreEqual(SettingKind.Bool, AtmospheresSettings.Definition("rtOwnClouds").Kind);
            Assert.AreEqual(CloudLayerDef.MaxLayers, AtmospheresSettings.Definition("rtOwnCloudsLayerCap").Max);
        }

        [TestMethod]
        public void TheSettingsReachThePackSettingsWithinTheirRanges()
        {
            var compiled = RtCloudPackSettings.From(false, 0, (key, value) => value);
            Assert.IsFalse(compiled.Enabled);
            Assert.AreEqual(512, compiled.Width, "the weather maps' own lattice");
            Assert.AreEqual(256, compiled.Height);
            Assert.AreEqual(8, compiled.Steps);
            Assert.AreEqual(0.25f, compiled.BudgetMs);
            Assert.AreEqual(2, compiled.DepthStride);
            Assert.AreEqual(10f, compiled.RebakeIntervalS);
            var extreme = RtCloudPackSettings.From(true, 0, (key, value) => key == RtCloudPackSettings.TuneDepthStride ? 99f : key == RtCloudPackSettings.TuneRebakeIntervalS ? -4f : value);
            Assert.AreEqual(4, extreme.DepthStride);
            Assert.AreEqual(0f, extreme.RebakeIntervalS);
            var on = RtCloudPackSettings.From(true, 9, (key, value) => value);
            Assert.IsTrue(on.Enabled);
            Assert.AreEqual(CloudLayerDef.MaxLayers, on.LayerCap, "clamped to the stack's own limit");
            var wild = RtCloudPackSettings.From(true, -3, (key, value) =>
                key == RtCloudPackSettings.TunePackWidth ? 99999f : key == RtCloudPackSettings.TuneBakeSteps ? 0f : -5f);
            Assert.AreEqual(0, wild.LayerCap);
            Assert.AreEqual(1024, wild.Width);
            Assert.AreEqual(2, wild.Steps);
            Assert.AreEqual(0.02f, wild.BudgetMs);
            var odd = RtCloudPackSettings.From(true, 0, (key, value) => key == RtCloudPackSettings.TunePackWidth ? 201f : value);
            Assert.AreEqual(0, odd.Width % 2, "an even width, so that the height is half of it");
        }

        [TestMethod]
        public void EveryNewKeyHasARenderingInTheBenchmarkMatrix()
        {
            string root = RepoRoot.Root("src", "ReDefinition.csproj");
            string json = File.ReadAllText(Path.Combine(root, "tools", "benchmark_matrix.json"));
            foreach (string key in new[] { "rtOwnClouds", "rtOwnCloudsLayerCap" })
                Assert.IsTrue(json.IndexOf("\"atmo." + key + "\"", StringComparison.Ordinal) >= 0, key);
            Assert.IsTrue(json.IndexOf("\"id\": \"weather-rtOwnClouds\", \"name\"", StringComparison.Ordinal) >= 0);
        }
    }
}
