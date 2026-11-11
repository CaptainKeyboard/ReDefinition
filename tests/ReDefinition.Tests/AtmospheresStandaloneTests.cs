using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Core;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests
{
    // The atmospheres on their own: where they keep their file, what they do with a damaged one, and the Common version check.
    [TestClass]
    public class AtmospheresStandaloneTests
    {
        private static ConfigNode Save()
        {
            return AtmospheresSettings.Snapshot();
        }

        private static void Restore(ConfigNode saved)
        {
            AtmospheresSettings.Restore(saved);
        }

        private static void Defaults()
        {
            AtmospheresSettings.ResetToDefaults();
        }

        [TestMethod]
        public void TheAtmospheresKeepTheirFileInTheirOwnPluginData()
        {
            string path = ModFolder.PluginFile("root", AtmospheresSettings.Mod, "settings.cfg");
            Assert.AreEqual(Path.Combine("root", "GameData", "ReDefinition/Atmospheres", "PluginData", "settings.cfg"), path);
            Assert.AreNotEqual(path, ModFolder.PluginFile("root", "ReDefinition/Dashboard", "settings.cfg"));
        }

        [TestMethod]
        public void TheSettingsComeBackAsTheyWereSaved()
        {
            string folder = Path.Combine(Path.GetTempPath(), "atmospheres-settings-" + Guid.NewGuid().ToString("N"));
            string file = Path.Combine(folder, "settings.cfg");
            ConfigNode saved = Save();
            try
            {
                AtmospheresSettings.Enabled = false;
                AtmospheresSettings.SetNumber("weatherEffects", 0);
                AtmospheresSettings.YearLengthFactor = 0.25f;
                AtmospheresSettings.SetNumber("coverStrength", 0.5);
                AtmospheresSettings.SetNumber("heavyShare", 1.5);
                AtmospheresSettings.SetNumber("cloudSource", (double)CloudSourceKind.Ours);
                AtmospheresSettings.SetText("alertSites", "Pad");
                AtmospheresSettings.Write(file);
                Defaults();
                AtmospheresSettings.Read(file);
                Assert.IsFalse(AtmospheresSettings.Enabled);
                Assert.IsFalse(AtmospheresSettings.WeatherEffects);
                Assert.AreEqual(0.25f, AtmospheresSettings.YearLengthFactor);
                Assert.AreEqual(0.5f, AtmospheresSettings.CoverStrength);
                Assert.AreEqual(1.5f, AtmospheresSettings.HeavyShare);
                Assert.AreEqual(CloudSourceKind.Ours, AtmospheresSettings.CloudSource);
                Assert.AreEqual("Pad", AtmospheresSettings.AlertSites);
            }
            finally
            {
                Restore(saved);
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        }

        [TestMethod]
        public void AMissingOrDamagedFileLeavesTheSettingsAlone()
        {
            string folder = Path.Combine(Path.GetTempPath(), "atmospheres-settings-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            ConfigNode saved = Save();
            try
            {
                Defaults();
                AtmospheresSettings.YearLengthFactor = 3f;
                AtmospheresSettings.Read(Path.Combine(folder, "missing.cfg"));
                Assert.AreEqual(3f, AtmospheresSettings.YearLengthFactor);

                string damaged = Path.Combine(folder, "damaged.cfg");
                File.WriteAllText(damaged, "enabled = maybe\nyearLengthFactor = abc\n{{{");
                AtmospheresSettings.Read(damaged);
                Assert.IsTrue(AtmospheresSettings.Enabled);
                Assert.AreEqual(3f, AtmospheresSettings.YearLengthFactor);
            }
            finally
            {
                Restore(saved);
                Directory.Delete(folder, true);
            }
        }

        [TestMethod]
        public void ValuesOutsideTheirRangeAreBroughtInAndNotANumberIsIgnored()
        {
            string folder = Path.Combine(Path.GetTempPath(), "atmospheres-settings-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            ConfigNode saved = Save();
            try
            {
                Defaults();
                string file = Path.Combine(folder, "range.cfg");
                File.WriteAllText(file, "yearLengthFactor = 500\ncoverStrength = -3\nheavyShare = 9\nlightCount = 99\n");
                AtmospheresSettings.Read(file);
                Assert.AreEqual(10f, AtmospheresSettings.YearLengthFactor);
                Assert.AreEqual(0f, AtmospheresSettings.CoverStrength);
                Assert.AreEqual(2f, AtmospheresSettings.HeavyShare);
                Assert.AreEqual(8, AtmospheresSettings.LightCount);

                Defaults();
                File.WriteAllText(file, "yearLengthFactor = NaN\ncoverStrength = Infinity\n");
                AtmospheresSettings.Read(file);
                Assert.AreEqual(1f, AtmospheresSettings.YearLengthFactor);
                Assert.AreEqual(1f, AtmospheresSettings.CoverStrength);
            }
            finally
            {
                Restore(saved);
                Directory.Delete(folder, true);
            }
        }

        [TestMethod]
        public void AnOlderCommonThanTheOneTheAtmospheresWereBuiltAgainstIsNamed()
        {
            Assert.IsNull(CommonVersionCheck.Problem("ReDefinition Atmospheres", new Version(0, 1, 15, 0), new Version(0, 1, 15, 0)));
            Assert.IsNull(CommonVersionCheck.Problem("ReDefinition Atmospheres", new Version(0, 1, 15, 0), new Version(0, 1, 16, 0)));
            StringAssert.Contains(CommonVersionCheck.Problem("ReDefinition Atmospheres", new Version(0, 1, 15, 0), new Version(0, 1, 14, 0)),
                "ReDefinition Atmospheres was made against 0.1.15");
        }
    }
}
