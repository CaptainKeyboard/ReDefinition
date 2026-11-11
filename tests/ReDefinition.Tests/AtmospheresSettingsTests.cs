using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests
{
    // The settings of the atmospheres: every value is brought into its range or ignored, the file lists the keys the player guide lists,
    // with the same defaults and ranges, and the file is made when it is missing.
    [TestClass]
    public class AtmospheresSettingsTests
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
            string folder = Path.Combine(Path.GetTempPath(), "atmospheres-table-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            ConfigNode saved = AtmospheresSettings.Snapshot();
            try
            {
                AtmospheresSettings.ResetToDefaults();
                string input = Path.Combine(folder, "in.cfg");
                File.WriteAllText(input, line + "\n");
                AtmospheresSettings.Read(input);
                string output = Path.Combine(folder, "out.cfg");
                AtmospheresSettings.Write(output);
                return Parse(File.ReadAllText(output));
            }
            finally
            {
                AtmospheresSettings.Restore(saved);
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

            string folder = Path.Combine(Path.GetTempPath(), "atmospheres-keys-" + Guid.NewGuid().ToString("N"));
            try
            {
                string file = Path.Combine(folder, "settings.cfg");
                Assert.IsTrue(AtmospheresSettings.Write(file));
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
            ConfigNode saved = AtmospheresSettings.Snapshot();
            string folder = Path.Combine(Path.GetTempPath(), "atmospheres-defaults-" + Guid.NewGuid().ToString("N"));
            try
            {
                AtmospheresSettings.ResetToDefaults();
                string file = Path.Combine(folder, "settings.cfg");
                AtmospheresSettings.Write(file);
                defaults = Parse(File.ReadAllText(file));
            }
            finally
            {
                AtmospheresSettings.Restore(saved);
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
                { "yearLengthFactor", Range(AtmospheresSettings.YearLengthFactorMin, AtmospheresSettings.YearLengthFactorMax) },
                { "snowStrength", Range(0f, 1f) },
                { "stormFrequency", Range(0f, AtmospheresSettings.StormFrequencyMax) },
                { "stormVisibility", Range(StormLook.MinVisibilityLow, StormLook.MinVisibilityHigh) },
                { "stormVeil", Range(0f, StormLook.VeilMax) },
                { "stormSkyDarkening", Range(0f, 1f) },
                { "stormCellRadiusKm", Range(AtmospheresSettings.StormCellRadiusMinKm, AtmospheresSettings.StormCellRadiusMaxKm) },
                { "stormVolumeSteps", Range(AtmospheresSettings.StormVolumeStepsMin, AtmospheresSettings.StormVolumeStepsMax) },
                { "stormMaxCells", Range(AtmospheresSettings.StormMaxCellsMin, AtmospheresSettings.StormMaxCellsMax) },
                { "orbitSnowWidth", Range(AtmospheresSettings.OrbitSnowWidthMin, AtmospheresSettings.OrbitSnowWidthMax) },
                { "winterSnowLine", Range((float)SnowMask.LowestWinterLineDegrees, (float)SnowMask.HighestWinterLineDegrees) },
                { "refreshSeconds", Range(AtmospheresSettings.RefreshSecondsMin, AtmospheresSettings.RefreshSecondsMax) }
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
            string folder = Path.Combine(Path.GetTempPath(), "atmospheres-load-" + Guid.NewGuid().ToString("N"));
            ConfigNode saved = AtmospheresSettings.Snapshot();
            try
            {
                string file = Path.Combine(folder, "PluginData", "settings.cfg");
                AtmospheresSettings.ResetToDefaults();
                Assert.IsTrue(AtmospheresSettings.LoadFrom(file), "made");
                Assert.IsTrue(File.Exists(file));
                Assert.AreEqual("2048", Parse(File.ReadAllText(file))["orbitSnowWidth"]);

                File.WriteAllText(file, "snowStrength = 0.25\n");
                Assert.IsFalse(AtmospheresSettings.LoadFrom(file), "read, not made");
                Assert.AreEqual(0.25f, AtmospheresSettings.SnowStrength);
                Assert.AreEqual("snowStrength = 0.25\n", File.ReadAllText(file), "the player's file is left as it is");
            }
            finally
            {
                AtmospheresSettings.Restore(saved);
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        }

        [TestMethod]
        public void SnapshotAndRestoreBringBackEverythingAndTheDefaultsAreTheDefaults()
        {
            ConfigNode before = AtmospheresSettings.Snapshot();
            try
            {
                AtmospheresSettings.ResetToDefaults();
                Assert.IsTrue(AtmospheresSettings.Enabled);
                Assert.AreEqual(2048, AtmospheresSettings.OrbitSnowWidth);
                Assert.AreEqual(AtmospheresSettings.GroundRefresh.Live, AtmospheresSettings.GroundRefreshMode);
                Assert.IsFalse(AtmospheresSettings.ForceCellOn);
                Assert.AreEqual(-1f, AtmospheresSettings.ForcePhase);

                AtmospheresSettings.StormSeed = 99;
                AtmospheresSettings.OrbitSnowWidth = 1024;
                AtmospheresSettings.ForceCellOn = true;
                AtmospheresSettings.ForceCellLatitude = 50.5;
                ConfigNode changed = AtmospheresSettings.Snapshot();
                AtmospheresSettings.ResetToDefaults();
                Assert.AreEqual(1, AtmospheresSettings.StormSeed);
                AtmospheresSettings.Restore(changed);
                Assert.AreEqual(99, AtmospheresSettings.StormSeed);
                Assert.AreEqual(1024, AtmospheresSettings.OrbitSnowWidth);
                Assert.IsTrue(AtmospheresSettings.ForceCellOn);
                Assert.AreEqual(50.5, AtmospheresSettings.ForceCellLatitude);
            }
            finally
            {
                AtmospheresSettings.Restore(before);
            }
        }

        [TestMethod]
        public void TheIntegerLimitIsTheSameClampAsTheFloatOne()
        {
            Assert.AreEqual(5, AtmospheresSettings.Limit(2, 5, 9));
            Assert.AreEqual(9, AtmospheresSettings.Limit(20, 5, 9));
            Assert.AreEqual(7, AtmospheresSettings.Limit(7, 5, 9));
            Assert.AreEqual(5f, AtmospheresSettings.Limit(2f, 5f, 9f));
        }
    }
}
