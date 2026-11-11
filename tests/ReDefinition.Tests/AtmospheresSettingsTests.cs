using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests
{
    // The settings of the atmospheres: one table gives every setting its key, default, range, tooltip, category, greyed-out reason and
    // profile row; every value is brought into its range or ignored, the file lists the keys of the table, and the file is made when it
    // is missing. (The comparison with the player guide returns when the documentation package rewrites the guide for the new keys.)
    [TestClass]
    public class AtmospheresSettingsTests
    {
        private static readonly Regex FileLine = new Regex(@"^[ \t]*(?<key>\w+)[ \t]*=[ \t]*(?<value>.*?)[ \t\r]*$", RegexOptions.Multiline);

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
        [DataRow("coverStrength", "-1", "0")]
        [DataRow("coverStrength", "5", "1")]
        [DataRow("coverStrength", "NaN", "1")]
        [DataRow("coverStrength", "abc", "1")]
        [DataRow("heavyShare", "-2", "0")]
        [DataRow("heavyShare", "9", "2")]
        [DataRow("heavyShare", "NaN", "1")]
        [DataRow("lightWeatherFrequency", "9", "2")]
        [DataRow("genSeed", "12", "12")]
        [DataRow("genSeed", "-7", "-7")]
        [DataRow("genSeed", "abc", "1")]
        [DataRow("genSeed", "2147483647", "2147483647")]
        [DataRow("blizzardVisibilityM", "1", "20")]
        [DataRow("blizzardVisibilityM", "9999", "500")]
        [DataRow("blizzardVisibilityM", "NaN", "60")]
        [DataRow("veilStrength", "-1", "0")]
        [DataRow("veilStrength", "9", "0.95")]
        [DataRow("veilStrength", "abc", "0.85")]
        [DataRow("skyDimming", "-1", "0")]
        [DataRow("skyDimming", "9", "1")]
        [DataRow("skyDimming", "NaN", "0.6")]
        [DataRow("volumeSteps", "1", "16")]
        [DataRow("volumeSteps", "999", "48")]
        [DataRow("volumeSteps", "NaN", "28")]
        [DataRow("volumeResolution", "0", "0.25")]
        [DataRow("volumeResolution", "3", "1")]
        [DataRow("orbitMapWidth", "1", "1024")]
        [DataRow("orbitMapWidth", "99999", "4096")]
        [DataRow("orbitMapWidth", "NaN", "2048")]
        [DataRow("forceLevel", "-5", "-1")]
        [DataRow("forceLevel", "9", "1")]
        [DataRow("forceLevel", "NaN", "-1")]
        [DataRow("forceLevel", "abc", "-1")]
        [DataRow("forcePhase", "-5", "-1")]
        [DataRow("forcePhase", "1.25", "0.25")]
        [DataRow("forcePhase", "NaN", "-1")]
        [DataRow("forcePhase", "abc", "-1")]
        [DataRow("gustStrength", "0", "0")]
        [DataRow("gustStrength", "7", "2")]
        [DataRow("launchMaxWindMs", "-3", "0")]
        [DataRow("launchMinVisibilityM", "99999", "20000")]
        [DataRow("enabled", "maybe", "True")]
        [DataRow("weatherEffects", "0", "False")]
        [DataRow("weatherEffects", "True", "True")]
        [DataRow("audioDistantStorm", "True", "True")]
        [DataRow("cloudSource", "nonsense", "Eve")]
        [DataRow("cloudSource", "Both", "Both")]
        [DataRow("cloudSource", "none", "None")]
        [DataRow("weatherCompose", "Host", "Host")]
        [DataRow("windSource", "Foreign", "Foreign")]
        [DataRow("vehicleCover", "Always", "Always")]
        [DataRow("seaIce", "Physical", "Physical")]
        [DataRow("alertMinLevel", "Blizzard", "Blizzard")]
        [DataRow("alertMinLevel", "None", "Heavy")]
        [DataRow("qualityTier", "Max", "Max")]
        [DataRow("alertSites", "Home;Pad", "Home;Pad")]
        [DataRow("forceBody", "Duna", "Duna")]
        public void EveryValueIsBroughtIntoItsRangeOrIgnored(string key, string input, string expected)
        {
            Dictionary<string, string> written = ReadBack(key + " = " + input);
            Assert.AreEqual(expected, written[key], key + " = " + input);
        }

        [TestMethod]
        public void EveryNumberSettingIsBroughtIntoItsRangeFromBothSides()
        {
            // forcePhase wraps a number into 0 to 1 instead of clamping it; it has its own rows above.
            foreach (SettingDef def in AtmospheresSettings.Definitions.Where(d => (d.Kind == SettingKind.Float || d.Kind == SettingKind.Int) && d.Key != "forcePhase"))
            {
                string low = def.Format(def.Min);
                string high = def.Format(def.Max);
                Assert.AreEqual(low, ReadBack(def.Key + " = -1e30")[def.Key], def.Key + " below");
                Assert.AreEqual(high, ReadBack(def.Key + " = 1e30")[def.Key], def.Key + " above");
                Assert.AreEqual(def.Format(def.Default), ReadBack(def.Key + " = oops")[def.Key], def.Key + " not a number");
            }
        }

        [TestMethod]
        public void TheKeysClassAndTheTableAgreeKeyForKey()
        {
            var constants = typeof(AtmospheresSettings.Keys).GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                .Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()).ToList();
            var keys = AtmospheresSettings.Definitions.Select(d => d.Key).ToList();
            CollectionAssert.AreEquivalent(constants, keys);
            Assert.AreEqual(keys.Count, new HashSet<string>(keys).Count, "no key twice");
            Assert.IsTrue(keys.Count >= 90, "keys: " + keys.Count);
            foreach (string key in keys) Assert.IsTrue(Regex.IsMatch(key, "^[a-z][a-zA-Z0-9]*$"), key + " is lowerCamelCase");
        }

        [TestMethod]
        public void EveryKeyTheBriefsNameIsPreDeclared()
        {
            string[] named =
            {
                "weatherEffects", "seasonMode", "cloudSource", "weatherCompose", "qualityTier", "lightWeatherFrequency", "heavyShare", "typeByTemperature", "useEveWeather",
                "eveLayerCap", "windSource", "windInjection", "windForces", "windStrength", "windOnLanded", "gustStrength", "solarDimming", "solarDimmingStrength",
                "visibilityExport", "genEnabled", "simGrid", "simLevels", "simSliceMs", "genSeed", "volumeResolution", "volumeSteps", "volumeCompose", "precipDensity",
                "stormCullDistance", "fogBrightness", "lightInMedium", "lightCount", "lightInMediumStrength", "aurora", "auroraAmbientTint", "cameraFrost",
                "weatherAudio", "weatherAudioVolume", "audioWind", "audioPrecip", "audioFootsteps", "audioFootstepsSurface", "audioDistantStorm", "audioQuietSnow",
                "audioMuffling", "eveSound", "audioBedSource", "audioProbeSolo", "groundCover", "vehicleCover", "tracks", "coverDrift", "seaIce", "iceDrawDistanceKm",
                "iceCollision", "forecastEnabled", "alertsEnabled", "alertLeadHours", "alertMinLevel", "alertWindMs", "alertWarpStop", "alertKac", "alertMessageApp",
                "alertSites", "forecastHorizonHours", "forecastUncertainty", "launchMaxLevel", "launchMaxWindMs", "launchMinVisibilityM", "launchLeadHours",
                "orbitBodies", "qualityGovernor", "forceLevel", "forceType", "forceBody", "forcePhase", "atmoDebug"
            };
            foreach (string key in named) Assert.IsNotNull(AtmospheresSettings.Definition(key), key);
        }

        [TestMethod]
        public void TheKeysTheContractRemovesAreNotSettings()
        {
            foreach (string key in new[] { "iceWinterLine", "freezeLineDeg", "lagYears", "winterSnowLine", "groundRefreshMode", "refreshSeconds", "storms",
                "stormFrequency", "stormSeed", "stormVisibility", "stormVeil", "stormSnowfall", "stormSkyDarkening", "stormEveSnow", "stormEveRainToSnow", "stormCellRadiusKm",
                "stormVolume", "stormVolumeSteps", "stormClouds", "stormOwnFlakes", "stormMaxCells", "orbitSnow", "orbitSnowWidth", "forceStorm", "forceStormCell", "snowStrength" })
            {
                Assert.IsNull(AtmospheresSettings.Definition(key), key);
            }
            Assert.IsFalse(ReadBack("stormFrequency = 1.5").ContainsKey("stormFrequency"), "an old key is neither read nor written");
        }

        [TestMethod]
        public void EveryKeyHasADefaultInRangeATooltipInTitleCaseACategoryAndKnownIds()
        {
            var words = new Regex(@"^[A-Z0-9]");
            var problems = new List<string>();
            foreach (SettingDef def in AtmospheresSettings.Definitions)
            {
                if (def.Kind != SettingKind.Text && (def.Default < def.Min || def.Default > def.Max)) problems.Add(def.Key + ": default outside the range");
                if (string.IsNullOrWhiteSpace(def.Tooltip)) problems.Add(def.Key + ": no tooltip");
                else
                {
                    foreach (string word in def.Tooltip.Split(' '))
                    {
                        if (word.Length > 0 && !words.IsMatch(word)) problems.Add(def.Key + ": tooltip word not in Title Case: " + word);
                    }
                    if (def.Tooltip.EndsWith(".")) problems.Add(def.Key + ": tooltip ends with a full stop");
                }
                if (Array.IndexOf(AtmospheresSettings.Categories.All, def.Category) < 0) problems.Add(def.Key + ": category " + def.Category);
                if (def.GreyReason.Length > 0 && Array.IndexOf(AtmospheresSettings.GreyReasons.All, def.GreyReason) < 0) problems.Add(def.Key + ": grey reason " + def.GreyReason);
                if (def.ProfileRow.Length > 0 && Array.IndexOf(AtmospheresSettings.ProfileRows.All, def.ProfileRow) < 0) problems.Add(def.Key + ": profile row " + def.ProfileRow);
                if (def.Kind == SettingKind.Choice && (def.Choices == null || def.Choices.Length < 2)) problems.Add(def.Key + ": choices");
                if (def.Kind != SettingKind.Choice && def.Choices != null) problems.Add(def.Key + ": choices on a non-choice");
            }
            Assert.AreEqual(0, problems.Count, string.Join("\n", problems));
            foreach (string category in AtmospheresSettings.Categories.All)
            {
                Assert.IsTrue(AtmospheresSettings.Definitions.Any(d => d.Category == category), "an empty category: " + category);
            }
        }

        // The aliases of Settings/AtmospheresSettings.Legacy.cs are removed by the packages named in CONTRACT.md; the package that removes
        // some lowers this number. It is 0 at F50.
        private const int ObsoleteAliasesLeft = 19;

        [TestMethod]
        public void TheObsoleteAliasesLeftAreCounted()
        {
            const System.Reflection.BindingFlags all = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly;
            int count = typeof(AtmospheresSettings).GetMembers(all)
                .Count(m => m.GetCustomAttributes(typeof(ObsoleteAttribute), false).Length > 0);
            Assert.AreEqual(ObsoleteAliasesLeft, count, "lower ObsoleteAliasesLeft when a package removes aliases (CONTRACT.md lists who)");
        }

        [TestMethod]
        public void TheGustTooltipIsTheName()
        {
            Assert.AreEqual("Gust Strength", AtmospheresSettings.Definition("gustStrength").Tooltip);
            Assert.AreEqual(0.0, AtmospheresSettings.Definition("gustStrength").Min, "0 is off");
        }

        [TestMethod]
        public void TheDefaultsAreTheOnesTheContractNames()
        {
            ConfigNode saved = AtmospheresSettings.Snapshot();
            try
            {
                AtmospheresSettings.ResetToDefaults();
                Assert.AreEqual(1.0f, AtmospheresSettings.VolumeResolution, "full resolution until the half-resolution look is proven");
                Assert.IsFalse(AtmospheresSettings.AudioDistantStorm, "distant storm sound is off");
                Assert.AreEqual(LevelChoice.Heavy, AtmospheresSettings.LaunchMaxLevel);
                Assert.AreEqual(10f, AtmospheresSettings.LaunchMaxWindMs);
                Assert.AreEqual(1000f, AtmospheresSettings.LaunchMinVisibilityM);
                Assert.AreEqual(2f, AtmospheresSettings.LaunchLeadHours);
                Assert.IsFalse(AtmospheresSettings.QualityGovernor, "the governor is off");
                Assert.AreEqual(AuroraMode.Auto, AtmospheresSettings.Aurora);
                Assert.AreEqual(ReDefinition.Core.QualityTier.Auto, AtmospheresSettings.QualityTier);
                Assert.AreEqual(VehicleCoverMode.Landed, AtmospheresSettings.VehicleCover);
                Assert.IsFalse(AtmospheresSettings.IceCollision);
                Assert.AreEqual(-1f, AtmospheresSettings.ForceLevel);
                Assert.AreEqual(-1f, AtmospheresSettings.ForcePhase);
                Assert.AreEqual(ReDefinition.Core.PrecipBand.Heavy, AtmospheresSettings.BandOf(LevelChoice.Heavy));
                Assert.AreEqual(ReDefinition.Core.PrecipBand.Flurries, AtmospheresSettings.BandOf(LevelChoice.Flurries));
                Assert.IsTrue(AtmospheresSettings.PrecipTypeDrawn(ReDefinition.Core.PrecipType.FreezingRain));
                Assert.IsFalse(AtmospheresSettings.PrecipTypeDrawn(ReDefinition.Core.PrecipType.None));
            }
            finally
            {
                AtmospheresSettings.Restore(saved);
            }
        }

        [TestMethod]
        public void ChoicesHaveTheNamesOfTheirEnumInOrder()
        {
            var pairs = new Dictionary<string, Type>
            {
                { "seasonMode", typeof(SeasonMode) }, { "cloudSource", typeof(CloudSourceKind) }, { "weatherCompose", typeof(WeatherCompose) }, { "windSource", typeof(WindSourceMode) },
                { "windInjection", typeof(WindInjection) }, { "volumeCompose", typeof(VolumeCompose) }, { "aurora", typeof(AuroraMode) },
                { "audioMuffling", typeof(AudioMuffling) }, { "eveSound", typeof(EveSoundPolicy) }, { "audioBedSource", typeof(AudioBedSource) },
                { "vehicleCover", typeof(VehicleCoverMode) }, { "seaIce", typeof(SeaIceMode) }, { "orbitBodies", typeof(OrbitBodiesMode) },
                { "simGrid", typeof(SimGridChoice) }, { "alertMinLevel", typeof(LevelChoice) }, { "launchMaxLevel", typeof(LevelChoice) },
                { "forceType", typeof(ForceType) }, { "atmoDebug", typeof(AtmoDebugMode) }, { "qualityTier", typeof(ReDefinition.Core.QualityTier) }
            };
            foreach (KeyValuePair<string, Type> pair in pairs)
            {
                SettingDef def = AtmospheresSettings.Definition(pair.Key);
                CollectionAssert.AreEqual(Enum.GetNames(pair.Value), def.Choices, pair.Key);
                Array values = Enum.GetValues(pair.Value);
                for (int i = 0; i < values.Length; i++) Assert.AreEqual(i, Convert.ToInt32(values.GetValue(i)), pair.Key + " is numbered from 0 in order");
            }
            Assert.AreEqual(CloudSourceKind.Eve, AtmospheresSettings.CloudSource);
        }

        [TestMethod]
        public void EverySettingComesBackAsItWasWritten()
        {
            ConfigNode saved = AtmospheresSettings.Snapshot();
            string folder = Path.Combine(Path.GetTempPath(), "atmospheres-roundtrip-" + Guid.NewGuid().ToString("N"));
            try
            {
                AtmospheresSettings.ResetToDefaults();
                // Move every setting off its default, to the top of its range or the last choice.
                foreach (SettingDef def in AtmospheresSettings.Definitions)
                {
                    if (def.Kind == SettingKind.Text) AtmospheresSettings.SetText(def.Key, "a;b");
                    else AtmospheresSettings.SetNumber(def.Key, Moved(def));
                }
                string file = Path.Combine(folder, "settings.cfg");
                Assert.IsTrue(AtmospheresSettings.Write(file));
                ConfigNode written = AtmospheresSettings.Snapshot();
                AtmospheresSettings.ResetToDefaults();
                AtmospheresSettings.Read(file);
                foreach (SettingDef def in AtmospheresSettings.Definitions)
                {
                    if (def.Kind == SettingKind.Text) Assert.AreEqual("a;b", AtmospheresSettings.Text(def.Key), def.Key);
                    else Assert.AreEqual(def.Limit(Moved(def)), AtmospheresSettings.Number(def.Key), def.Key);
                }
                Assert.AreEqual(written.values.Count, AtmospheresSettings.Definitions.Count);
            }
            finally
            {
                AtmospheresSettings.Restore(saved);
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        }

        // A value other than the default: the top of the range, or the bottom where the default is the top.
        private static double Moved(SettingDef def)
        {
            return def.Default == def.Max ? def.Min : def.Max;
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
                Assert.AreEqual("2048", Parse(File.ReadAllText(file))["orbitMapWidth"]);

                File.WriteAllText(file, "coverStrength = 0.25\n");
                Assert.IsFalse(AtmospheresSettings.LoadFrom(file), "read, not made");
                Assert.AreEqual(0.25f, AtmospheresSettings.CoverStrength);
                Assert.AreEqual("coverStrength = 0.25\n", File.ReadAllText(file), "the player's file is left as it is");
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
                Assert.AreEqual(2048, AtmospheresSettings.OrbitMapWidth);
                Assert.AreEqual(-1f, AtmospheresSettings.ForcePhase);

                AtmospheresSettings.SetNumber("genSeed", 99);
                AtmospheresSettings.SetNumber("orbitMapWidth", 1024);
                AtmospheresSettings.SetText("forceBody", "Eve");
                ConfigNode changed = AtmospheresSettings.Snapshot();
                AtmospheresSettings.ResetToDefaults();
                Assert.AreEqual(1, AtmospheresSettings.GenSeed);
                Assert.AreEqual("", AtmospheresSettings.ForceBody);
                AtmospheresSettings.Restore(changed);
                Assert.AreEqual(99, AtmospheresSettings.GenSeed);
                Assert.AreEqual(1024, AtmospheresSettings.OrbitMapWidth);
                Assert.AreEqual("Eve", AtmospheresSettings.ForceBody);
            }
            finally
            {
                AtmospheresSettings.Restore(before);
            }
        }

        [TestMethod]
        public void ASetterBringsTheValueIntoItsRangeAndRefusesWhatIsNotASetting()
        {
            ConfigNode saved = AtmospheresSettings.Snapshot();
            try
            {
                Assert.IsTrue(AtmospheresSettings.SetNumber("lightCount", 99));
                Assert.AreEqual(8, AtmospheresSettings.LightCount);
                Assert.IsTrue(AtmospheresSettings.SetNumber("cameraFrost", 0));
                Assert.IsFalse(AtmospheresSettings.CameraFrost);
                Assert.IsFalse(AtmospheresSettings.SetNumber("lightCount", double.NaN));
                Assert.IsFalse(AtmospheresSettings.SetNumber("noSuchKey", 1));
                Assert.IsFalse(AtmospheresSettings.SetNumber("forceBody", 1), "a text setting is not a number");
                Assert.IsFalse(AtmospheresSettings.SetText("lightCount", "x"));
                Assert.IsFalse(AtmospheresSettings.SetText("forceBody", null));
            }
            finally
            {
                AtmospheresSettings.Restore(saved);
            }
        }

        [TestMethod]
        public void TheAliasesOfRenamedSettingsForwardToTheNewOnes()
        {
            ConfigNode saved = AtmospheresSettings.Snapshot();
#pragma warning disable 618
            try
            {
                AtmospheresSettings.StormSeed = 41;
                Assert.AreEqual(41, AtmospheresSettings.GenSeed);
                AtmospheresSettings.StormFrequency = 1.5f;
                Assert.AreEqual(1.5f, AtmospheresSettings.HeavyShare);
                AtmospheresSettings.StormsEnabled = false;
                Assert.IsFalse(AtmospheresSettings.WeatherEffects);
                AtmospheresSettings.ForceStorm = 0.35f;
                Assert.AreEqual(0.35f, AtmospheresSettings.ForceLevel);
                AtmospheresSettings.ResetToDefaults();
                Assert.AreEqual(1, AtmospheresSettings.StormSeed);
                Assert.AreEqual(AtmospheresSettings.StormCellRadiusDefaultKm, AtmospheresSettings.StormCellRadiusKm, "a deleted setting's field starts at its default");
            }
            finally
            {
                AtmospheresSettings.Restore(saved);
            }
#pragma warning restore 618
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
