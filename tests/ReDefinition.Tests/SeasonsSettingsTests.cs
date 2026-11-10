using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Seasons;

namespace ReDefinition.Tests
{
    // The settings of the seasons: every value is brought into its range or ignored, the file lists the keys the player guide lists,
    // with the same defaults and ranges, and the file is made when it is missing.
    [TestClass]
    public class SeasonsSettingsTests
    {
        private static readonly Regex TableRow = new Regex(@"^\| `(?<key>\w+)` \| `(?<default>[^`]*)` \| (?<range>.*?) \| .* \|[ \t\r]*$", RegexOptions.Multiline);
        private static readonly Regex FileLine = new Regex(@"^\s*(?<key>\w+)\s*=\s*(?<value>.*?)\s*$", RegexOptions.Multiline);

        private static string Repo()
        {
            string repo = AppContext.BaseDirectory;
            while (repo != null && !File.Exists(Path.Combine(repo, "ReDefinition.sln"))) repo = Path.GetDirectoryName(repo);
            Assert.IsNotNull(repo, "the repository root");
            return repo;
        }

        // The settings after reading a file with that one line, written out again as key to value.
        private static Dictionary<string, string> ReadBack(string line)
        {
            string folder = Path.Combine(Path.GetTempPath(), "seasons-table-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            ConfigNode saved = SeasonsSettings.Snapshot();
            try
            {
                SeasonsSettings.ResetToDefaults();
                string input = Path.Combine(folder, "in.cfg");
                File.WriteAllText(input, line + "\n");
                SeasonsSettings.Read(input);
                string output = Path.Combine(folder, "out.cfg");
                SeasonsSettings.Write(output);
                return Parse(File.ReadAllText(output));
            }
            finally
            {
                SeasonsSettings.Restore(saved);
                Directory.Delete(folder, true);
            }
        }

        private static Dictionary<string, string> Parse(string text)
        {
            var values = new Dictionary<string, string>();
            foreach (Match m in FileLine.Matches(text)) values[m.Groups["key"].Value] = m.Groups["value"].Value;
            return values;
        }

        [DataTestMethod]
        [DataRow("yearLengthFactor", "0", "0.01")]
        [DataRow("yearLengthFactor", "99", "10")]
        [DataRow("yearLengthFactor", "NaN", "1")]
        [DataRow("yearLengthFactor", "Infinity", "1")]
        [DataRow("yearLengthFactor", "abc", "1")]
        [DataRow("snowStrength", "-1", "0")]
        [DataRow("snowStrength", "5", "1")]
        [DataRow("snowStrength", "NaN", "1")]
        [DataRow("snowStrength", "abc", "1")]
        [DataRow("winterSnowLine", "0", "30")]
        [DataRow("winterSnowLine", "99", "60")]
        [DataRow("winterSnowLine", "NaN", "42")]
        [DataRow("winterSnowLine", "abc", "42")]
        [DataRow("refreshSeconds", "0", "1")]
        [DataRow("refreshSeconds", "9999", "120")]
        [DataRow("refreshSeconds", "NaN", "5")]
        [DataRow("refreshSeconds", "abc", "5")]
        [DataRow("stormFrequency", "-2", "0")]
        [DataRow("stormFrequency", "9", "2")]
        [DataRow("stormFrequency", "NaN", "1")]
        [DataRow("stormFrequency", "abc", "1")]
        [DataRow("stormSeed", "12", "12")]
        [DataRow("stormSeed", "-7", "-7")]
        [DataRow("stormSeed", "abc", "1")]
        [DataRow("stormVisibility", "1", "20")]
        [DataRow("stormVisibility", "9999", "500")]
        [DataRow("stormVisibility", "NaN", "60")]
        [DataRow("stormVisibility", "abc", "60")]
        [DataRow("stormVeil", "-1", "0")]
        [DataRow("stormVeil", "9", "0.95")]
        [DataRow("stormVeil", "NaN", "0.85")]
        [DataRow("stormVeil", "abc", "0.85")]
        [DataRow("stormSkyDarkening", "-1", "0")]
        [DataRow("stormSkyDarkening", "9", "1")]
        [DataRow("stormSkyDarkening", "NaN", "0.6")]
        [DataRow("stormSkyDarkening", "abc", "0.6")]
        [DataRow("stormCellRadiusKm", "1", "5")]
        [DataRow("stormCellRadiusKm", "99", "25")]
        [DataRow("stormCellRadiusKm", "NaN", "12")]
        [DataRow("stormCellRadiusKm", "abc", "12")]
        [DataRow("stormVolumeSteps", "1", "16")]
        [DataRow("stormVolumeSteps", "999", "48")]
        [DataRow("stormVolumeSteps", "NaN", "28")]
        [DataRow("stormVolumeSteps", "abc", "28")]
        [DataRow("stormMaxCells", "0", "1")]
        [DataRow("stormMaxCells", "99", "12")]
        [DataRow("stormMaxCells", "NaN", "6")]
        [DataRow("stormMaxCells", "abc", "6")]
        [DataRow("orbitSnowWidth", "1", "1024")]
        [DataRow("orbitSnowWidth", "99999", "4096")]
        [DataRow("orbitSnowWidth", "NaN", "2048")]
        [DataRow("orbitSnowWidth", "abc", "2048")]
        [DataRow("forceStorm", "-5", "-1")]
        [DataRow("forceStorm", "9", "1")]
        [DataRow("forceStorm", "NaN", "-1")]
        [DataRow("forceStorm", "abc", "-1")]
        [DataRow("forcePhase", "-5", "-1")]
        [DataRow("forcePhase", "1.25", "0.25")]
        [DataRow("forcePhase", "NaN", "-1")]
        [DataRow("forcePhase", "abc", "-1")]
        [DataRow("enabled", "maybe", "True")]
        [DataRow("storms", "0", "True")]
        [DataRow("groundRefreshMode", "nonsense", "Live")]
        [DataRow("groundRefreshMode", "Off", "Off")]
        [DataRow("stormClouds", "nonsense", "Existing")]
        [DataRow("stormClouds", "Own", "Own")]
        [DataRow("forceStormCell", "nonsense", "off")]
        [DataRow("forceStormCell", "99,200,90", "90,-160,25")]
        public void EveryValueIsBroughtIntoItsRangeOrIgnored(string key, string input, string expected)
        {
            Dictionary<string, string> written = ReadBack(key + " = " + input);
            Assert.AreEqual(expected, written[key], key + " = " + input);
        }

        [TestMethod]
        public void ThePlayerGuideListsTheKeysTheFileWritesAndNoOthers()
        {
            string guide = File.ReadAllText(Path.Combine(Repo(), "docs", "player", "seasons.md"));
            var documented = new SortedSet<string>();
            foreach (Match m in TableRow.Matches(guide)) documented.Add(m.Groups["key"].Value);

            string folder = Path.Combine(Path.GetTempPath(), "seasons-keys-" + Guid.NewGuid().ToString("N"));
            try
            {
                string file = Path.Combine(folder, "settings.cfg");
                Assert.IsTrue(SeasonsSettings.Write(file));
                var written = new SortedSet<string>(Parse(File.ReadAllText(file)).Keys);
                CollectionAssert.AreEqual(new List<string>(written), new List<string>(documented),
                    "written: " + string.Join(", ", written) + "; documented: " + string.Join(", ", documented));
            }
            finally
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        }

        [TestMethod]
        public void TheDefaultsInThePlayerGuideAreTheDefaultsTheFileWrites()
        {
            string guide = File.ReadAllText(Path.Combine(Repo(), "docs", "player", "seasons.md"));
            Dictionary<string, string> defaults;
            ConfigNode saved = SeasonsSettings.Snapshot();
            string folder = Path.Combine(Path.GetTempPath(), "seasons-defaults-" + Guid.NewGuid().ToString("N"));
            try
            {
                SeasonsSettings.ResetToDefaults();
                string file = Path.Combine(folder, "settings.cfg");
                SeasonsSettings.Write(file);
                defaults = Parse(File.ReadAllText(file));
            }
            finally
            {
                SeasonsSettings.Restore(saved);
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
            int checkedKeys = 0;
            foreach (Match m in TableRow.Matches(guide))
            {
                string key = m.Groups["key"].Value, documented = m.Groups["default"].Value;
                double a, b = 0;
                bool numbers = double.TryParse(documented, NumberStyles.Float, CultureInfo.InvariantCulture, out a);
                if (numbers) numbers = double.TryParse(defaults[key], NumberStyles.Float, CultureInfo.InvariantCulture, out b);
                if (numbers)
                {
                    Assert.AreEqual(b, a, 1e-9, key + ": the guide says " + documented + ", the file " + defaults[key]);
                }
                else
                {
                    Assert.AreEqual(documented, defaults[key], key);
                }
                checkedKeys++;
            }
            Assert.IsTrue(checkedKeys >= 24, "keys checked: " + checkedKeys);
        }

        [TestMethod]
        public void TheRangesInThePlayerGuideAreTheLimitsInTheCode()
        {
            var limits = new Dictionary<string, string>
            {
                { "yearLengthFactor", Range(SeasonsSettings.YearLengthFactorMin, SeasonsSettings.YearLengthFactorMax) },
                { "snowStrength", Range(0f, 1f) },
                { "stormFrequency", Range(0f, SeasonsSettings.StormFrequencyMax) },
                { "stormVisibility", Range(StormLook.MinVisibilityLow, StormLook.MinVisibilityHigh) },
                { "stormVeil", Range(0f, StormLook.VeilMax) },
                { "stormSkyDarkening", Range(0f, 1f) },
                { "stormCellRadiusKm", Range(SeasonsSettings.StormCellRadiusMinKm, SeasonsSettings.StormCellRadiusMaxKm) },
                { "stormVolumeSteps", Range(SeasonsSettings.StormVolumeStepsMin, SeasonsSettings.StormVolumeStepsMax) },
                { "stormMaxCells", Range(SeasonsSettings.StormMaxCellsMin, SeasonsSettings.StormMaxCellsMax) },
                { "orbitSnowWidth", Range(SeasonsSettings.OrbitSnowWidthMin, SeasonsSettings.OrbitSnowWidthMax) },
                { "winterSnowLine", Range((float)SnowMask.LowestWinterLineDegrees, (float)SnowMask.HighestWinterLineDegrees) },
                { "refreshSeconds", Range(SeasonsSettings.RefreshSecondsMin, SeasonsSettings.RefreshSecondsMax) }
            };
            string guide = File.ReadAllText(Path.Combine(Repo(), "docs", "player", "seasons.md"));
            var plainRange = new Regex(@"^-?[\d.]+ to [\d.]+$");
            int checkedKeys = 0;
            foreach (Match m in TableRow.Matches(guide))
            {
                string key = m.Groups["key"].Value, range = m.Groups["range"].Value;
                if (!plainRange.IsMatch(range)) continue;
                Assert.IsTrue(limits.ContainsKey(key), key + " has the range " + range + " in the guide and none in this test");
                Assert.AreEqual(limits[key], range, key);
                checkedKeys++;
            }
            Assert.AreEqual(limits.Count, checkedKeys, "every limit of the code is in the guide");
        }

        private static string Range(float min, float max)
        {
            return min.ToString("R", CultureInfo.InvariantCulture) + " to " + max.ToString("R", CultureInfo.InvariantCulture);
        }

        private static string Range(int min, int max)
        {
            return min.ToString(CultureInfo.InvariantCulture) + " to " + max.ToString(CultureInfo.InvariantCulture);
        }

        [TestMethod]
        public void AFileThatIsMissingIsMadeWithTheDefaultsAndOneThatExistsIsNotTouched()
        {
            string folder = Path.Combine(Path.GetTempPath(), "seasons-load-" + Guid.NewGuid().ToString("N"));
            ConfigNode saved = SeasonsSettings.Snapshot();
            try
            {
                string file = Path.Combine(folder, "PluginData", "settings.cfg");
                SeasonsSettings.ResetToDefaults();
                Assert.IsTrue(SeasonsSettings.LoadFrom(file), "made");
                Assert.IsTrue(File.Exists(file));
                Assert.AreEqual("2048", Parse(File.ReadAllText(file))["orbitSnowWidth"]);

                File.WriteAllText(file, "snowStrength = 0.25\n");
                Assert.IsFalse(SeasonsSettings.LoadFrom(file), "read, not made");
                Assert.AreEqual(0.25f, SeasonsSettings.SnowStrength);
                Assert.AreEqual("snowStrength = 0.25\n", File.ReadAllText(file), "the player's file is left as it is");
            }
            finally
            {
                SeasonsSettings.Restore(saved);
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        }

        [TestMethod]
        public void SnapshotAndRestoreBringBackEverythingAndTheDefaultsAreTheDefaults()
        {
            ConfigNode before = SeasonsSettings.Snapshot();
            try
            {
                SeasonsSettings.ResetToDefaults();
                Assert.IsTrue(SeasonsSettings.Enabled);
                Assert.AreEqual(2048, SeasonsSettings.OrbitSnowWidth);
                Assert.AreEqual(SeasonsSettings.GroundRefresh.Live, SeasonsSettings.GroundRefreshMode);
                Assert.IsFalse(SeasonsSettings.ForceCellOn);
                Assert.AreEqual(-1f, SeasonsSettings.ForcePhase);

                SeasonsSettings.StormSeed = 99;
                SeasonsSettings.OrbitSnowWidth = 1024;
                SeasonsSettings.ForceCellOn = true;
                SeasonsSettings.ForceCellLatitude = 50.5;
                ConfigNode changed = SeasonsSettings.Snapshot();
                SeasonsSettings.ResetToDefaults();
                Assert.AreEqual(1, SeasonsSettings.StormSeed);
                SeasonsSettings.Restore(changed);
                Assert.AreEqual(99, SeasonsSettings.StormSeed);
                Assert.AreEqual(1024, SeasonsSettings.OrbitSnowWidth);
                Assert.IsTrue(SeasonsSettings.ForceCellOn);
                Assert.AreEqual(50.5, SeasonsSettings.ForceCellLatitude);
            }
            finally
            {
                SeasonsSettings.Restore(before);
            }
        }

        [TestMethod]
        public void TheIntegerLimitIsTheSameClampAsTheFloatOne()
        {
            Assert.AreEqual(5, SeasonsSettings.Limit(2, 5, 9));
            Assert.AreEqual(9, SeasonsSettings.Limit(20, 5, 9));
            Assert.AreEqual(7, SeasonsSettings.Limit(7, 5, 9));
            Assert.AreEqual(5f, SeasonsSettings.Limit(2f, 5f, 9f));
        }
    }
}
