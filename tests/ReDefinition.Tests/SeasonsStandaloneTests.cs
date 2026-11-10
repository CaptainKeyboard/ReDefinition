using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Core;
using ReDefinition.Seasons;

namespace ReDefinition.Tests
{
    // The seasons on their own: where they keep their file, what they do with a damaged one, and the Common version check.
    [TestClass]
    public class SeasonsStandaloneTests
    {
        private static ConfigNode Save()
        {
            return SeasonsSettings.Snapshot();
        }

        private static void Restore(ConfigNode saved)
        {
            SeasonsSettings.Restore(saved);
        }

        private static void Defaults()
        {
            SeasonsSettings.ResetToDefaults();
        }

        [TestMethod]
        public void TheSeasonsKeepTheirFileInTheirOwnPluginData()
        {
            string path = ModFolder.PluginFile("root", SeasonsSettings.Mod, "settings.cfg");
            Assert.AreEqual(Path.Combine("root", "GameData", "ReDefinition/Seasons", "PluginData", "settings.cfg"), path);
            Assert.AreNotEqual(path, ModFolder.PluginFile("root", "ReDefinition/Dashboard", "settings.cfg"));
        }

        [TestMethod]
        public void TheSettingsComeBackAsTheyWereSaved()
        {
            string folder = Path.Combine(Path.GetTempPath(), "seasons-settings-" + Guid.NewGuid().ToString("N"));
            string file = Path.Combine(folder, "settings.cfg");
            ConfigNode saved = Save();
            try
            {
                SeasonsSettings.Enabled = false;
                SeasonsSettings.StormsEnabled = false;
                SeasonsSettings.YearLengthFactor = 0.25f;
                SeasonsSettings.SnowStrength = 0.5f;
                SeasonsSettings.StormFrequency = 1.5f;
                SeasonsSettings.WinterSnowLine = 38.5f;
                SeasonsSettings.Write(file);
                Defaults();
                SeasonsSettings.Read(file);
                Assert.IsFalse(SeasonsSettings.Enabled);
                Assert.IsFalse(SeasonsSettings.StormsEnabled);
                Assert.AreEqual(0.25f, SeasonsSettings.YearLengthFactor);
                Assert.AreEqual(0.5f, SeasonsSettings.SnowStrength);
                Assert.AreEqual(1.5f, SeasonsSettings.StormFrequency);
                Assert.AreEqual(38.5f, SeasonsSettings.WinterSnowLine);
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
            string folder = Path.Combine(Path.GetTempPath(), "seasons-settings-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            ConfigNode saved = Save();
            try
            {
                Defaults();
                SeasonsSettings.YearLengthFactor = 3f;
                SeasonsSettings.Read(Path.Combine(folder, "missing.cfg"));
                Assert.AreEqual(3f, SeasonsSettings.YearLengthFactor);

                string damaged = Path.Combine(folder, "damaged.cfg");
                File.WriteAllText(damaged, "enabled = maybe\nyearLengthFactor = abc\n{{{");
                SeasonsSettings.Read(damaged);
                Assert.IsTrue(SeasonsSettings.Enabled);
                Assert.AreEqual(3f, SeasonsSettings.YearLengthFactor);
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
            string folder = Path.Combine(Path.GetTempPath(), "seasons-settings-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            ConfigNode saved = Save();
            try
            {
                Defaults();
                string file = Path.Combine(folder, "range.cfg");
                File.WriteAllText(file, "yearLengthFactor = 500\nsnowStrength = -3\nstormFrequency = 9\nwinterSnowLine = 5\n");
                SeasonsSettings.Read(file);
                Assert.AreEqual(SeasonsSettings.YearLengthFactorMax, SeasonsSettings.YearLengthFactor);
                Assert.AreEqual(0f, SeasonsSettings.SnowStrength);
                Assert.AreEqual(SeasonsSettings.StormFrequencyMax, SeasonsSettings.StormFrequency);
                Assert.AreEqual((float)SnowMask.LowestWinterLineDegrees, SeasonsSettings.WinterSnowLine);

                Defaults();
                File.WriteAllText(file, "yearLengthFactor = NaN\nsnowStrength = Infinity\n");
                SeasonsSettings.Read(file);
                Assert.AreEqual(1f, SeasonsSettings.YearLengthFactor);
                Assert.AreEqual(1f, SeasonsSettings.SnowStrength);
            }
            finally
            {
                Restore(saved);
                Directory.Delete(folder, true);
            }
        }

        [TestMethod]
        public void AnOlderCommonThanTheOneTheSeasonsWereBuiltAgainstIsNamed()
        {
            Assert.IsNull(CommonVersionCheck.Problem("ReDefinition Seasons", new Version(0, 1, 15, 0), new Version(0, 1, 15, 0)));
            Assert.IsNull(CommonVersionCheck.Problem("ReDefinition Seasons", new Version(0, 1, 15, 0), new Version(0, 1, 16, 0)));
            StringAssert.Contains(CommonVersionCheck.Problem("ReDefinition Seasons", new Version(0, 1, 15, 0), new Version(0, 1, 14, 0)),
                "ReDefinition Seasons was made against 0.1.15");
        }
    }
}
