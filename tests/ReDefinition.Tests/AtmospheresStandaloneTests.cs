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
                AtmospheresSettings.StormsEnabled = false;
                AtmospheresSettings.YearLengthFactor = 0.25f;
                AtmospheresSettings.SnowStrength = 0.5f;
                AtmospheresSettings.StormFrequency = 1.5f;
                AtmospheresSettings.WinterSnowLine = 38.5f;
                AtmospheresSettings.Write(file);
                Defaults();
                AtmospheresSettings.Read(file);
                Assert.IsFalse(AtmospheresSettings.Enabled);
                Assert.IsFalse(AtmospheresSettings.StormsEnabled);
                Assert.AreEqual(0.25f, AtmospheresSettings.YearLengthFactor);
                Assert.AreEqual(0.5f, AtmospheresSettings.SnowStrength);
                Assert.AreEqual(1.5f, AtmospheresSettings.StormFrequency);
                Assert.AreEqual(38.5f, AtmospheresSettings.WinterSnowLine);
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
                File.WriteAllText(file, "yearLengthFactor = 500\nsnowStrength = -3\nstormFrequency = 9\nwinterSnowLine = 5\n");
                AtmospheresSettings.Read(file);
                Assert.AreEqual(AtmospheresSettings.YearLengthFactorMax, AtmospheresSettings.YearLengthFactor);
                Assert.AreEqual(0f, AtmospheresSettings.SnowStrength);
                Assert.AreEqual(AtmospheresSettings.StormFrequencyMax, AtmospheresSettings.StormFrequency);
                Assert.AreEqual((float)SnowMask.LowestWinterLineDegrees, AtmospheresSettings.WinterSnowLine);

                Defaults();
                File.WriteAllText(file, "yearLengthFactor = NaN\nsnowStrength = Infinity\n");
                AtmospheresSettings.Read(file);
                Assert.AreEqual(1f, AtmospheresSettings.YearLengthFactor);
                Assert.AreEqual(1f, AtmospheresSettings.SnowStrength);
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
