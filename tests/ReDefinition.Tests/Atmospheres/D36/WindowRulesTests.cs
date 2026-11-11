using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Core;
using ReDefinition.Settings;
using SettingKind = ReDefinition.Atmospheres.SettingKind;

namespace ReDefinition.Tests.Atmospheres.D36
{
    // The rules of the settings window without the game: the completeness of the key table's words, the greyed-out reasons, the choices a
    // game cannot run, the status lines and the model's changes.
    [TestClass]
    public class WindowRulesTests
    {
        private ConfigNode saved;
        private Func<long> vram;
        private Func<QualityTier> host;

        [TestInitialize]
        public void Save()
        {
            saved = AtmospheresSettings.Snapshot();
            vram = QualityProfile.VramSource;
            host = QualityProfile.HostTier;
            AtmospheresSettings.ResetToDefaults();
            QualityProfile.VramSource = () => 16384;
            QualityProfile.HostTier = () => QualityTier.Auto;
            ForeignChanges.Clear();
            ComposeEvidence.Clear();
            reports = new Action<string>[] { ForeignChanges.Report, SettingChanges.Report };
            ForeignChanges.Report = line => { };
            SettingChanges.Report = line => { };
        }

        private Action<string>[] reports;

        [TestCleanup]
        public void Restore()
        {
            ForeignChanges.Report = reports[0];
            SettingChanges.Report = reports[1];
            AtmospheresSettings.Restore(saved);
            QualityProfile.VramSource = vram;
            QualityProfile.HostTier = host;
            ForeignChanges.Clear();
            ComposeEvidence.Clear();
        }

        // The values of the table's defaults with some overridden, without touching the live settings.
        private static Func<string, double> Values(params object[] pairs)
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

        // The table's defaults with the master switch on, as a player who turned the weather on has them.
        private static Func<string, double> On(params object[] pairs)
        {
            var all = new List<object> { "weatherEffects", 1 };
            all.AddRange(pairs);
            return Values(all.ToArray());
        }

        private static SettingDef Def(string key)
        {
            return AtmospheresSettings.Definition(key);
        }

        private static string Reason(string key, Func<string, double> values, WindowEnvironment env)
        {
            RowState state = Dependencies.Evaluate(Def(key), values, env);
            return state.Enabled ? null : state.ReasonId;
        }

        // --- Completeness of the key table's words (the table is the source of the guide's table) ---

        [TestMethod]
        public void EveryKeyHasDefaultTooltipRangeCategoryReasonAndRow()
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (SettingDef def in AtmospheresSettings.Definitions)
            {
                Assert.IsTrue(keys.Add(def.Key), def.Key + " twice");
                Assert.IsTrue(def.Key.Length > 0 && char.IsLower(def.Key[0]), def.Key + " is not lowerCamelCase");
                CollectionAssert.Contains(AtmospheresSettings.Categories.All, def.Category, def.Key + " category");
                Assert.IsTrue(def.Tooltip.Length > 0, def.Key + " has no tooltip");
                foreach (string word in def.Tooltip.Split(' '))
                {
                    Assert.IsTrue(char.IsUpper(word[0]) || char.IsDigit(word[0]), def.Key + ": '" + word + "' is not in Title Case in '" + def.Tooltip + "'");
                }
                if (def.Kind == SettingKind.Text) Assert.IsNotNull(def.DefaultText, def.Key);
                else
                {
                    Assert.IsTrue(def.Min <= def.Default && def.Default <= def.Max, def.Key + " default outside its range");
                    Assert.AreEqual(def.Default, def.Limit(def.Default), 0.0, def.Key + " default is not whole or in range");
                }
                if (def.GreyReason.Length > 0) CollectionAssert.Contains(AtmospheresSettings.GreyReasons.All, def.GreyReason, def.Key + " grey reason");
                if (def.ProfileRow.Length > 0) CollectionAssert.Contains(AtmospheresSettings.ProfileRows.All, def.ProfileRow, def.Key + " profile row");
                if (def.Kind == SettingKind.Choice) Assert.IsTrue(def.Choices.Length >= 2, def.Key);
            }
            Assert.IsTrue(keys.Count >= 90, "keys: " + keys.Count);
        }

        [TestMethod]
        public void EveryKeyHasAPlainExplanationAndNoOtherKeyHasOne()
        {
            var keys = new HashSet<string>(AtmospheresSettings.Definitions.Select(d => d.Key), StringComparer.Ordinal);
            var helped = new HashSet<string>(SettingHelp.Keys, StringComparer.Ordinal);
            CollectionAssert.AreEquivalent(keys.ToList(), helped.ToList(), "the explanations and the key table differ");
            foreach (string key in keys)
            {
                string help = SettingHelp.For(key);
                Assert.IsTrue(help.Length >= 8 && help.Length <= 320, key + ": " + help.Length + " characters");
                Assert.IsTrue(help.EndsWith(".", StringComparison.Ordinal), key + " does not end in a full stop");
                Assert.IsFalse(help.Contains("TODO") || help.Contains("[VERIFY"), key);
            }
            string gust = SettingHelp.For("gustStrength").ToLowerInvariant();
            StringAssert.Contains(gust, "gusts are short bursts of wind");
            StringAssert.Contains(gust, "smooth wind");
        }

        [TestMethod]
        public void EveryReasonOfTheTableHasATextAndEveryReasonTheRulesGiveToo()
        {
            foreach (string id in AtmospheresSettings.GreyReasons.All)
            {
                string text = SettingHelp.ReasonText(id);
                Assert.AreNotEqual(id, text, id + " has no text");
                Assert.IsTrue(text.EndsWith(".", StringComparison.Ordinal), id);
            }
            foreach (string id in SettingHelp.ReasonIds) Assert.IsTrue(SettingHelp.ReasonText(id).Length > 5, id);
            // Every reason a rule can give is one with a text.
            foreach (WindowEnvironment env in new[] { WindowEnvironment.Alone(), WindowEnvironment.Full() })
            {
                foreach (SettingDef def in AtmospheresSettings.Definitions)
                {
                    foreach (Func<string, double> values in new[] { Values(), On(), On("enabled", 0), On("genEnabled", 0, "volumePass", 0, "weatherAudio", 0,
                        "forecastEnabled", 0, "groundCover", 0, "seaIce", 0, "lightInMedium", 0, "windForces", 0, "precipDensity", 0, "solarDimming", 0, "alertsEnabled", 0) })
                    {
                        RowState state = Dependencies.Evaluate(def, values, env);
                        if (!state.Enabled) Assert.AreNotEqual(state.ReasonId, state.Reason, def.Key + ": " + state.ReasonId + " has no text");
                        for (int i = 0; i < (def.Kind == SettingKind.Choice ? def.Choices.Length : 0); i++)
                        {
                            string why = Dependencies.ChoiceUnavailable(def, i, env);
                            if (why != null) Assert.AreNotEqual(why, SettingHelp.ReasonText(why), def.Key + " choice " + i);
                        }
                    }
                }
            }
        }

        // --- The greyed-out cases: each dependency shows its reason ---

        [TestMethod]
        public void TheModOffGreysEverythingButItself()
        {
            Func<string, double> values = Values("enabled", 0, "weatherEffects", 1);
            Assert.AreEqual(SettingHelp.ModOff, Reason("genEnabled", values, WindowEnvironment.Full()));
            Assert.AreEqual(SettingHelp.ModOff, Reason("yearLengthFactor", values, WindowEnvironment.Full()));
            Assert.IsNull(Reason("enabled", values, WindowEnvironment.Full()));
        }

        [TestMethod]
        public void TheMasterSwitchOffGreysEveryGroupButGeneral()
        {
            Func<string, double> off = Values();
            foreach (SettingDef def in AtmospheresSettings.Definitions)
            {
                string reason = Reason(def.Key, off, WindowEnvironment.Full());
                if (def.Category == AtmospheresSettings.Categories.General) Assert.IsNull(reason, def.Key);
                else Assert.AreEqual(AtmospheresSettings.GreyReasons.NeedsMaster, reason, def.Key);
            }
            foreach (SettingDef def in AtmospheresSettings.Definitions)
            {
                if (def.Category == AtmospheresSettings.Categories.General) continue;
                Assert.AreNotEqual(AtmospheresSettings.GreyReasons.NeedsMaster, Reason(def.Key, On(), WindowEnvironment.Full()), def.Key + " with the master on");
            }
        }

        [TestMethod]
        public void WithoutEveTheEveRowsAreGreyedWithTheReason()
        {
            WindowEnvironment none = WindowEnvironment.Full();
            none.EveInstalled = false;
            foreach (string key in new[] { "useEveWeather", "eveLayerCap", "eveSound" })
            {
                Assert.AreEqual(AtmospheresSettings.GreyReasons.NeedsEve, Reason(key, On(), none), key);
                Assert.IsNull(Reason(key, On(), WindowEnvironment.Full()), key + " with EVE");
            }
            Assert.AreEqual("EVE is not installed.", SettingHelp.ReasonText(AtmospheresSettings.GreyReasons.NeedsEve));
        }

        [TestMethod]
        public void CloudSourceEveAndBothAreNotOfferedWithoutEveAndTheRowSaysWhatRuns()
        {
            WindowEnvironment none = WindowEnvironment.Full();
            none.EveInstalled = false;
            SettingDef def = Def("cloudSource");
            Assert.IsNull(Dependencies.ChoiceUnavailable(def, (int)CloudSourceKind.Ours, none));
            Assert.IsNull(Dependencies.ChoiceUnavailable(def, (int)CloudSourceKind.None, none));
            Assert.AreEqual(AtmospheresSettings.GreyReasons.NeedsEve, Dependencies.ChoiceUnavailable(def, (int)CloudSourceKind.Eve, none));
            Assert.AreEqual(AtmospheresSettings.GreyReasons.NeedsEve, Dependencies.ChoiceUnavailable(def, (int)CloudSourceKind.Both, none));
            Assert.IsNull(Dependencies.ChoiceUnavailable(def, (int)CloudSourceKind.Eve, WindowEnvironment.Full()));
            // The default is Eve: the row stays open and tells the truth.
            RowState state = Dependencies.Evaluate(def, On(), none);
            Assert.IsTrue(state.Enabled);
            StringAssert.Contains(state.Note, "own volume pass");
            Assert.AreEqual("", Dependencies.Evaluate(def, On(), WindowEnvironment.Full()).Note);
            Assert.AreEqual("", Dependencies.Evaluate(def, On("cloudSource", (int)CloudSourceKind.Ours), none).Note);
        }

        [TestMethod]
        public void WindInjectionAeroNeedsModularFlightIntegrator()
        {
            WindowEnvironment none = WindowEnvironment.Full();
            none.ModularFlightIntegrator = false;
            SettingDef def = Def("windInjection");
            Assert.AreEqual(SettingHelp.NeedsModularFlightIntegrator, Dependencies.ChoiceUnavailable(def, (int)WindInjection.Aero, none));
            Assert.IsNull(Dependencies.ChoiceUnavailable(def, (int)WindInjection.Force, none));
            Assert.IsNull(Dependencies.ChoiceUnavailable(def, (int)WindInjection.Auto, none));
            Assert.IsNull(Dependencies.ChoiceUnavailable(def, (int)WindInjection.Aero, WindowEnvironment.Full()));
            RowState state = Dependencies.Evaluate(def, On("windInjection", (int)WindInjection.Aero), none);
            Assert.IsTrue(state.Enabled);
            StringAssert.Contains(state.Note, "ModularFlightIntegrator is not installed");
            // The row itself needs the wind forces.
            Assert.AreEqual(AtmospheresSettings.GreyReasons.NeedsWindForces, Reason("windInjection", On("windForces", 0), WindowEnvironment.Full()));
            Assert.AreEqual(AtmospheresSettings.GreyReasons.NeedsWindForces, Reason("windStrength", On("windForces", 0), WindowEnvironment.Full()));
            Assert.AreEqual(AtmospheresSettings.GreyReasons.NeedsWindForces, Reason("windOnLanded", On("windForces", 0), WindowEnvironment.Full()));
        }

        [TestMethod]
        public void TheGeneratorKeysNeedTheSimulation()
        {
            foreach (string key in new[] { "simGrid", "simLevels", "simSliceMs", "genSeed", "lightWeatherFrequency", "heavyShare" })
            {
                Assert.AreEqual(AtmospheresSettings.GreyReasons.NeedsSim, Reason(key, On("genEnabled", 0), WindowEnvironment.Full()), key);
                Assert.IsNull(Reason(key, On(), WindowEnvironment.Full()), key);
            }
            Assert.IsNull(Reason("genEnabled", On("genEnabled", 0), WindowEnvironment.Full()), "the switch itself stays");
        }

        [TestMethod]
        public void VehicleCoverNeedsTheShellShadersAfterGroundCover()
        {
            WindowEnvironment none = WindowEnvironment.Full();
            none.ShellShaders = false;
            Assert.AreEqual(SettingHelp.NeedsShellShaders, Reason("vehicleCover", On(), none));
            Assert.AreEqual(AtmospheresSettings.GreyReasons.NeedsGroundCover, Reason("vehicleCover", On("groundCover", 0), none), "the root is named first");
            Assert.IsNull(Reason("vehicleCover", On(), WindowEnvironment.Full()));
            Assert.IsNull(Reason("tracks", On(), none), "tracks do not use the shells");
            Assert.AreEqual(AtmospheresSettings.GreyReasons.NeedsGroundCover, Reason("coverStrength", On("groundCover", 0), none));
        }

        [TestMethod]
        public void AudioMufflingRseNeedsRocketSoundEnhancement()
        {
            WindowEnvironment none = WindowEnvironment.Full();
            none.RocketSoundEnhancement = false;
            SettingDef def = Def("audioMuffling");
            Assert.AreEqual(SettingHelp.NeedsRse, Dependencies.ChoiceUnavailable(def, (int)AudioMuffling.Rse, none));
            Assert.IsNull(Dependencies.ChoiceUnavailable(def, (int)AudioMuffling.Own, none));
            Assert.IsNull(Dependencies.ChoiceUnavailable(def, (int)AudioMuffling.Rse, WindowEnvironment.Full()));
            RowState state = Dependencies.Evaluate(def, On("audioMuffling", (int)AudioMuffling.Rse), none);
            Assert.IsTrue(state.Enabled);
            StringAssert.Contains(state.Note, "Rocket Sound Enhancement is not installed");
            Assert.AreEqual(AtmospheresSettings.GreyReasons.NeedsAudio, Reason("audioMuffling", On("weatherAudio", 0), none));
        }

        [TestMethod]
        public void TheCompositionChoicesNeedTheHostAndAutoSaysUnverified()
        {
            WindowEnvironment none = WindowEnvironment.Full();
            none.HostApi = false;
            Assert.AreEqual(SettingHelp.NeedsHostForCompose, Dependencies.ChoiceUnavailable(Def("weatherCompose"), (int)WeatherCompose.Host, none));
            Assert.IsNull(Dependencies.ChoiceUnavailable(Def("weatherCompose"), (int)WeatherCompose.Scene, none));
            Assert.AreEqual(SettingHelp.NeedsHostForCompose, Dependencies.ChoiceUnavailable(Def("volumeCompose"), (int)VolumeCompose.PostUpscale, none));
            Assert.IsNull(Dependencies.ChoiceUnavailable(Def("volumeCompose"), (int)VolumeCompose.Scene, none));
            Assert.IsNull(Dependencies.ChoiceUnavailable(Def("weatherCompose"), (int)WeatherCompose.Host, WindowEnvironment.Full()));

            // Auto with an upscaler and the host: unverified until a pair is recorded.
            RowState auto = Dependencies.Evaluate(Def("weatherCompose"), On(), WindowEnvironment.Full());
            StringAssert.Contains(auto.Note, "unverified");
            StringAssert.Contains(auto.Note, "NVIDIA/Dlss");
            ComposeEvidence.Record("NVIDIA", UpscalerKind.Dlss);
            Assert.IsFalse(Dependencies.Evaluate(Def("weatherCompose"), On(), WindowEnvironment.Full()).Note.Contains("unverified"));
            // Chosen Scene: nothing to verify.
            Assert.AreEqual("", Dependencies.Evaluate(Def("weatherCompose"), On("weatherCompose", (int)WeatherCompose.Scene), WindowEnvironment.Full()).Note);
        }

        [TestMethod]
        public void TheAlertsHangOnTheForecastAndKerbalAlarmClock()
        {
            WindowEnvironment none = WindowEnvironment.Full();
            none.KerbalAlarmClock = false;
            Assert.AreEqual(AtmospheresSettings.GreyReasons.NeedsForecast, Reason("alertsEnabled", On("forecastEnabled", 0), none));
            Assert.AreEqual(AtmospheresSettings.GreyReasons.NeedsForecast, Reason("alertLeadHours", On("forecastEnabled", 0), none), "root first");
            Assert.AreEqual(AtmospheresSettings.GreyReasons.NeedsAlerts, Reason("alertLeadHours", On("alertsEnabled", 0), none));
            Assert.AreEqual(SettingHelp.NeedsKac, Reason("alertKac", On(), none));
            Assert.IsNull(Reason("alertKac", On(), WindowEnvironment.Full()));
            Assert.IsNull(Reason("alertWarpStop", On(), none), "only the Kerbal Alarm Clock row needs the mod");
            Assert.AreEqual(AtmospheresSettings.GreyReasons.NeedsForecast, Reason("launchMaxWindMs", On("forecastEnabled", 0), none));
        }

        [TestMethod]
        public void TheOtherSwitchesGreyTheirOwnRows()
        {
            WindowEnvironment env = WindowEnvironment.Full();
            Assert.AreEqual(AtmospheresSettings.GreyReasons.NeedsSeaIce, Reason("iceDrawDistanceKm", On("seaIce", (int)SeaIceMode.Off), env));
            Assert.AreEqual(AtmospheresSettings.GreyReasons.NeedsSeaIce, Reason("iceCollision", On("seaIce", (int)SeaIceMode.Off), env));
            Assert.IsNull(Reason("iceDrawDistanceKm", On(), env));
            Assert.AreEqual(AtmospheresSettings.GreyReasons.NeedsLightInMedium, Reason("lightCount", On("lightInMedium", 0), env));
            Assert.AreEqual(AtmospheresSettings.GreyReasons.NeedsLightInMedium, Reason("lightInMediumStrength", On("lightInMedium", 0), env));
            Assert.AreEqual(AtmospheresSettings.GreyReasons.NeedsAudio, Reason("audioWind", On("weatherAudio", 0), env));
            Assert.AreEqual(AtmospheresSettings.GreyReasons.NeedsAudio, Reason("weatherAudioVolume", On("weatherAudio", 0), env));
            Assert.AreEqual(AtmospheresSettings.GreyReasons.NeedsPrecip, Reason("precipSnow", On("precipDensity", 0), env));
            Assert.AreEqual(AtmospheresSettings.GreyReasons.NeedsSolarDimming, Reason("solarDimmingStrength", On(), env), "off by default");
            Assert.IsNull(Reason("solarDimmingStrength", On("solarDimming", 1), env));
            Assert.AreEqual(AtmospheresSettings.GreyReasons.NeedsVolume, Reason("volumeSteps", On("volumePass", 0), env));
            Assert.AreEqual(AtmospheresSettings.GreyReasons.NeedsVolume, Reason("volumeResolution", On("volumePass", 0), env));
        }

        [TestMethod]
        public void AuroraVolumetricNeedsEveAndWindSourceForeignNeedsAWindMod()
        {
            WindowEnvironment none = WindowEnvironment.Alone();
            Assert.AreEqual(AtmospheresSettings.GreyReasons.NeedsEve, Dependencies.ChoiceUnavailable(Def("aurora"), (int)AuroraMode.Volumetric, none));
            Assert.IsNull(Dependencies.ChoiceUnavailable(Def("aurora"), (int)AuroraMode.Shell2D, none));
            Assert.AreEqual(SettingHelp.NeedsForeignWind, Dependencies.ChoiceUnavailable(Def("windSource"), (int)WindSourceMode.Foreign, none));
            WindowEnvironment wind = WindowEnvironment.Alone();
            wind.ForeignWindOwner = "FAR";
            Assert.IsNull(Dependencies.ChoiceUnavailable(Def("windSource"), (int)WindSourceMode.Foreign, wind));
            Assert.IsNull(Dependencies.ChoiceUnavailable(Def("windSource"), (int)WindSourceMode.Auto, none));
        }

        [TestMethod]
        public void TheGreyedOutRowsOfEveryDependencyInTheTableAreCounted()
        {
            // Ten different dependencies are shown with a reason (the cases above): count the distinct reason ids the rules can give.
            var ids = new HashSet<string>(StringComparer.Ordinal);
            WindowEnvironment bare = WindowEnvironment.Alone();
            Func<string, double> allOff = On("genEnabled", 0, "volumePass", 0, "weatherAudio", 0, "forecastEnabled", 0, "groundCover", 0, "seaIce", 0,
                "lightInMedium", 0, "windForces", 0, "precipDensity", 0, "solarDimming", 0, "alertsEnabled", 0);
            foreach (SettingDef def in AtmospheresSettings.Definitions)
            {
                foreach (Func<string, double> values in new[] { Values(), On(), allOff, Values("enabled", 0) })
                {
                    RowState state = Dependencies.Evaluate(def, values, bare);
                    if (!state.Enabled) ids.Add(state.ReasonId);
                }
                for (int i = 0; i < (def.Kind == SettingKind.Choice ? def.Choices.Length : 0); i++)
                {
                    string why = Dependencies.ChoiceUnavailable(def, i, bare);
                    if (why != null) ids.Add(why);
                }
            }
            Assert.IsTrue(ids.Count >= 10, "reasons: " + string.Join(", ", ids));
        }

        // --- Wind status lines: each cell of the mode table ---

        [TestMethod]
        public void TheWindStatusSaysWhoOwnsTheBaseWindInEveryCellOfTheModeTable()
        {
            Assert.AreEqual("Wind: FAR owns the base wind, ours adds the storm delta.", WindStatus.Text(WindSourceMode.Auto, "FAR"));
            Assert.AreEqual("Wind: ours, base wind and storms.", WindStatus.Text(WindSourceMode.Auto, ""));
            Assert.AreEqual("Wind: ours replaces KWP's where it can; see the log.", WindStatus.Text(WindSourceMode.Ours, "KWP"));
            Assert.AreEqual("Wind: ours, base wind and storms.", WindStatus.Text(WindSourceMode.Ours, null));
            Assert.AreEqual("Wind: FAR's only, ours draws the visuals.", WindStatus.Text(WindSourceMode.Foreign, "FAR"));
            Assert.AreEqual("Wind: no other wind mod found, so no wind forces; visuals only.", WindStatus.Text(WindSourceMode.Foreign, ""));
            Assert.AreEqual("Wind: off, ours draws the visuals.", WindStatus.Text(WindSourceMode.Off, "FAR"));
            Assert.AreEqual("Wind: off, ours draws the visuals.", WindStatus.Text(WindSourceMode.Off, ""));
            // All cells are distinct where they should be and every mode has a line.
            foreach (WindSourceMode mode in Enum.GetValues(typeof(WindSourceMode)))
            {
                foreach (string owner in new[] { "", "FAR" }) StringAssert.StartsWith(WindStatus.Text(mode, owner), "Wind: ");
            }
        }

        [TestMethod]
        public void TheInjectionLineFollowsTheModeAndModularFlightIntegrator()
        {
            Assert.AreEqual("Pushing: through the air speed the drag sees.", WindStatus.InjectionText(WindInjection.Auto, true, true));
            Assert.AreEqual("Pushing: as a force on the parts.", WindStatus.InjectionText(WindInjection.Auto, false, true));
            Assert.AreEqual("Pushing: as a force on the parts.", WindStatus.InjectionText(WindInjection.Force, true, true));
            Assert.AreEqual("Pushing: through the air speed the drag sees.", WindStatus.InjectionText(WindInjection.Aero, true, true));
            StringAssert.Contains(WindStatus.InjectionText(WindInjection.Aero, false, true), "ModularFlightIntegrator is not installed");
            Assert.AreEqual("", WindStatus.InjectionText(WindInjection.Aero, true, false));
        }

        [TestMethod]
        public void EveryStatusLineFitsOnOneLineOfThePage()
        {
            // The page holds 510 pixels; KSP's font takes about 7 pixels a character, so 72 characters.
            const int Most = 72;
            foreach (WindSourceMode mode in Enum.GetValues(typeof(WindSourceMode)))
            {
                foreach (string owner in new[] { "", "FAR", "Kerbal Wind", "AtmosphereAutopilot" })
                    Assert.IsTrue(WindStatus.Text(mode, owner).Length <= Most + (owner.Length > 12 ? 20 : 0), mode + "/" + owner + ": " + WindStatus.Text(mode, owner));
            }
            foreach (WindInjection injection in Enum.GetValues(typeof(WindInjection)))
            {
                foreach (bool mfi in new[] { false, true })
                    Assert.IsTrue(WindStatus.InjectionText(injection, mfi, true).Length <= Most, injection + "/" + mfi);
            }
            var model = Model(WindowEnvironment.Full(), new List<QualityTier>(), new List<string>());
            QualityProfile.VramSource = () => 8192;
            Assert.IsTrue(model.TierLine().Length <= Most, model.TierLine());
            QualityProfile.VramSource = () => 16384;
            QualityProfile.HostTier = () => QualityTier.Ultra;
            Assert.IsTrue(model.TierLine().Length <= Most, model.TierLine());
        }

        [TestMethod]
        public void EveryRowNameFitsTheNameColumn()
        {
            int longest = 0;
            string which = "";
            foreach (SettingDef def in AtmospheresSettings.Definitions)
            {
                if (def.Tooltip.Length > longest)
                {
                    longest = def.Tooltip.Length;
                    which = def.Key;
                }
            }
            Assert.IsTrue(longest <= SettingsWindow.NameChars, which + ": " + longest + " characters against " + SettingsWindow.NameChars);
            // A whole number with a range too wide for a slider is typed; the seed is the only one.
            var typed = AtmospheresSettings.Definitions.Where(SettingsWindow.TypedNumber).Select(d => d.Key).ToArray();
            CollectionAssert.AreEqual(new[] { "genSeed" }, typed);
        }

        [TestMethod]
        public void TheRowOfABodyShowsTheNumbersSetInTheWindow()
        {
            var written = new List<string>();
            var row = new BodyRow { Name = "Duna", Status = BodyStatusKind.Full };
            var model = new WindowModel(WindowEnvironment.Full(), () => { }, t => { }, () => new List<BodyRow> { row });
            model.UseBodiesFile(() => new Dictionary<string, BodyPlayerOptions>(), options => written.Add("bodies"));
            model.SetBody(row, 50.0, 9.0);
            Assert.AreEqual(BodyPlayerOptions.MaxYearFactor, row.YearFactor, "brought into its range, as the file will have it");
            Assert.AreEqual(BodyPlayerOptions.MaxFrequency, row.Frequency);
            model.SetBody(row, 0.5, 0.0);
            Assert.AreEqual(0.5, row.YearFactor);
            Assert.AreEqual(0.0, row.Frequency);
            Assert.AreEqual(0, written.Count);
        }

        // --- The words of a value ---

        [TestMethod]
        public void TheSeasonModeIsAChoiceOfFourNamedInWords()
        {
            SettingDef def = Def("seasonMode");
            Assert.AreEqual(SettingKind.Choice, def.Kind);
            Assert.AreEqual(AtmospheresSettings.Categories.General, def.Category);
            var names = def.Choices.Select(ValueFormat.ChoiceName).ToArray();
            CollectionAssert.AreEqual(new[] { "Global Summer", "Global Winter", "Global Seasons", "Real Seasons" }, names);
            Assert.AreEqual("Real Seasons", ValueFormat.Of(def, def.Default));
            Assert.AreEqual(SettingHelp.For("seasonMode").Contains("Real Seasons"), true);
        }

        [TestMethod]
        public void ValuesAreWrittenAsOnOffNamesAndNumbersWithUnits()
        {
            Assert.AreEqual("On", ValueFormat.Of(Def("enabled"), 1));
            Assert.AreEqual("Off", ValueFormat.Of(Def("weatherEffects"), 0));
            Assert.AreEqual("Orbit And Ground", ValueFormat.Of(Def("seaIce"), (double)(int)SeaIceMode.OrbitAndGround));
            Assert.AreEqual("EVE", ValueFormat.ChoiceName("Eve"));
            Assert.AreEqual("RSE", ValueFormat.ChoiceName("Rse"));
            Assert.AreEqual("Shell 2D", ValueFormat.ChoiceName("Shell2D"));
            Assert.AreEqual("0.5 ms", ValueFormat.Of(Def("simSliceMs"), 0.5));
            Assert.AreEqual("15 m/s", ValueFormat.Of(Def("alertWindMs"), 15));
            Assert.AreEqual("2048 px", ValueFormat.Of(Def("orbitMapWidth"), 2048));
            Assert.AreEqual("24 h", ValueFormat.Of(Def("forecastHorizonHours"), 24));
            Assert.AreEqual("1 to 50 km", ValueFormat.Range(Def("iceDrawDistanceKm")));
            Assert.AreEqual("0", ValueFormat.Number(-0.0));
            Assert.AreEqual("empty", ValueFormat.DefaultOf(Def("alertSites")));
        }

        // --- The model: changes, tier, Reset ---

        private static WindowModel Model(WindowEnvironment env, List<QualityTier> marked, List<string> written)
        {
            var model = new WindowModel(env, () => written.Add("settings"), marked.Add, () => new List<BodyRow>());
            model.UseBodiesFile(() => new Dictionary<string, BodyPlayerOptions>(), options => written.Add("bodies:" + options.Count));
            return model;
        }

        [TestMethod]
        public void AGreyedRowTakesNoChangeAndAnOfferedChoiceDoes()
        {
            var marked = new List<QualityTier>();
            var written = new List<string>();
            WindowModel model = Model(WindowEnvironment.Alone(), marked, written);
            // The master switch is off: a Volume row is greyed out.
            Assert.IsFalse(model.Set(Def("volumeSteps"), 32));
            Assert.AreEqual(28, AtmospheresSettings.VolumeSteps);
            Assert.IsTrue(model.Set(Def("weatherEffects"), 1));
            Assert.IsTrue(model.Set(Def("volumeSteps"), 32));
            Assert.AreEqual(32, AtmospheresSettings.VolumeSteps);
            // A choice the game cannot run is refused.
            Assert.IsFalse(model.Set(Def("windInjection"), (double)(int)WindInjection.Aero));
            Assert.IsTrue(model.Set(Def("windInjection"), (double)(int)WindInjection.Force));
            Assert.IsFalse(model.Set(Def("cloudSource"), (double)(int)CloudSourceKind.Both));
            Assert.IsTrue(model.Set(Def("cloudSource"), (double)(int)CloudSourceKind.Ours));
            Assert.IsTrue(model.Dirty);
            model.Flush();
            CollectionAssert.AreEqual(new[] { "settings" }, written);
            model.Flush();
            Assert.AreEqual(1, written.Count, "nothing changed since");
        }

        [TestMethod]
        public void ChangingTheTierWritesItsRowAndTheMarker()
        {
            var marked = new List<QualityTier>();
            var written = new List<string>();
            WindowModel model = Model(WindowEnvironment.Full(), marked, written);
            Assert.IsTrue(model.Set(Def("qualityTier"), (double)(int)QualityTier.Low));
            Assert.AreEqual(16, AtmospheresSettings.VolumeSteps);
            Assert.AreEqual(2, AtmospheresSettings.LightCount);
            CollectionAssert.AreEqual(new[] { QualityTier.Low }, marked);
            Assert.IsTrue(model.Set(Def("qualityTier"), (double)(int)QualityTier.Ultra));
            Assert.AreEqual(36, AtmospheresSettings.VolumeSteps);
            Assert.AreEqual(QualityTier.Ultra, marked.Last());
            StringAssert.Contains(model.Tooltip(Def("qualityTier")), "In force: Ultra.");
        }

        [TestMethod]
        public void TheTierLineSaysWhereAnAutoTierComesFrom()
        {
            var model = Model(WindowEnvironment.Full(), new List<QualityTier>(), new List<string>());
            StringAssert.Contains(model.TierLine(), "no main mod profile, so Medium");
            QualityProfile.HostTier = () => QualityTier.High;
            StringAssert.Contains(model.TierLine(), "from the main mod's profile");
            QualityProfile.VramSource = () => 8192;
            StringAssert.Contains(model.TierLine(), "under 10 GB");
            AtmospheresSettings.SetNumber("qualityTier", (double)(int)QualityTier.Max);
            Assert.AreEqual("In force: Max.", model.TierLine());
        }

        [TestMethod]
        public void TheTooltipExplainsAndNamesTheReasonWhenGreyedOut()
        {
            var model = Model(WindowEnvironment.Alone(), new List<QualityTier>(), new List<string>());
            string tip = model.Tooltip(Def("gustStrength"));
            StringAssert.Contains(tip, "Gusts are short bursts");
            StringAssert.Contains(tip, "Default: 1. Range: 0 to 2.");
            StringAssert.Contains(tip, "Greyed out: Weather Effects is off.");
            model.Set(Def("weatherEffects"), 1);
            Assert.IsFalse(model.Tooltip(Def("gustStrength")).Contains("Greyed out"));
            foreach (string line in model.Tooltip(Def("seasonMode")).Split('\n')) Assert.IsTrue(line.Length <= 83, line);
            Assert.IsTrue(model.Tooltip(Def("audioDistantStorm")).Contains("Off by default"));
            Assert.AreEqual(0.0, Def("audioDistantStorm").Default);
        }

        [TestMethod]
        public void ResetThroughTheModelWritesDefaultsTheTierAndTheBodies()
        {
            var marked = new List<QualityTier>();
            var written = new List<string>();
            QualityProfile.HostTier = () => QualityTier.Medium;
            WindowModel model = Model(WindowEnvironment.Full(), marked, written);
            model.Set(Def("weatherEffects"), 1);
            model.Set(Def("heavyShare"), 1.9);
            model.Set(Def("seasonMode"), (double)(int)SeasonMode.GlobalWinter);
            int restored = 0;
            ForeignChanges.Record("eve", () => restored++);
            Assert.AreEqual(QualityTier.Medium, model.Reset());
            Assert.AreEqual(0.0, AtmospheresSettings.Number("weatherEffects"));
            Assert.AreEqual(1.0f, AtmospheresSettings.HeavyShare);
            Assert.AreEqual(SeasonMode.RealSeasons, AtmospheresSettings.SeasonMode);
            Assert.AreEqual(20, AtmospheresSettings.VolumeSteps, "the Medium row, not the table's 28");
            Assert.AreEqual(1, restored);
            CollectionAssert.AreEqual(new[] { QualityTier.Medium }, marked);
            CollectionAssert.Contains(written, "bodies:0");
            CollectionAssert.Contains(written, "settings");
        }

        [TestMethod]
        public void TheChangesAreAnnouncedToTheParts()
        {
            var heard = new List<string>();
            Action<string> listener = heard.Add;
            SettingChanges.Changed += listener;
            try
            {
                var model = Model(WindowEnvironment.Full(), new List<QualityTier>(), new List<string>());
                model.Set(Def("weatherEffects"), 1);
                model.Set(Def("coverStrength"), 0.5);
                model.Reset();
            }
            finally
            {
                SettingChanges.Changed -= listener;
            }
            CollectionAssert.AreEqual(new[] { "weatherEffects", "coverStrength", SettingChanges.All }, heard);
        }

        [TestMethod]
        public void APartThatThrowsOnAChangeDoesNotKeepTheOthersFromHearing()
        {
            int heard = 0;
            Action<string> bad = key => { throw new InvalidOperationException("no"); };
            Action<string> good = key => heard++;
            SettingChanges.Changed += bad;
            SettingChanges.Changed += good;
            try
            {
                SettingChanges.Raise("x");
            }
            finally
            {
                SettingChanges.Changed -= bad;
                SettingChanges.Changed -= good;
            }
            Assert.AreEqual(1, heard);
        }

        [TestMethod]
        public void TheGroupsFollowTheKeyTableAndHoldAllRows()
        {
            var model = Model(WindowEnvironment.Alone(), new List<QualityTier>(), new List<string>());
            List<string> categories = model.Categories();
            CollectionAssert.AreEqual(AtmospheresSettings.Categories.All, categories);
            int rows = 0;
            foreach (string category in categories)
            {
                List<SettingDef> list = model.Rows(category);
                Assert.IsTrue(list.Count > 0, category);
                rows += list.Count;
                Assert.IsTrue(SettingHelp.CategoryTitle(category).Length > 0);
            }
            Assert.AreEqual(AtmospheresSettings.Definitions.Count, rows);
            Assert.AreEqual("seasonMode", model.Rows(AtmospheresSettings.Categories.General)[3].Key);
        }

        // --- Bodies ---

        private static BodyClimate Climate(string name, int index, BodyStatus status)
        {
            return new BodyClimate(name, index, 600000, 9203545, 21600, status, 1);
        }

        [TestMethod]
        public void TheBodyListShowsActiveBodiesFirstWithStatusReadinessAndTheirOptions()
        {
            var options = new Dictionary<string, BodyPlayerOptions>(StringComparer.OrdinalIgnoreCase) { { "Duna", new BodyPlayerOptions(2.0, 0.5) } };
            var climates = new[]
            {
                Climate("Moho", 1, BodyStatus.Off("NoAtmosphere")),
                Climate("Kerbin", 2, BodyStatus.Full),
                Climate("Duna", 3, BodyStatus.Full),
                Climate("Gilly", 4, BodyStatus.CoverOnly),
                Climate("Eve", 5, BodyStatus.Broken("bad node"))
            };
            List<BodyRow> rows = BodyRows.Build(climates, options, index => index == 3 ? FieldReadiness.CatchingUp : FieldReadiness.Ready);
            CollectionAssert.AreEqual(new[] { "Kerbin", "Duna", "Gilly", "Eve", "Moho" }, rows.Select(r => r.Name).ToArray());
            Assert.IsTrue(rows.Take(4).All(r => r.Active));
            Assert.IsFalse(rows.Last().Active);
            Assert.AreEqual("Full", rows[0].StatusText);
            Assert.AreEqual("Cover Only", rows[2].StatusText);
            Assert.AreEqual("Broken (bad node)", rows[3].StatusText);
            Assert.AreEqual("Off (NoAtmosphere)", rows[4].StatusText);
            Assert.AreEqual("Simulation ready", rows[0].ReadinessText);
            Assert.AreEqual("Simulation catching up", rows[1].ReadinessText);
            Assert.AreEqual("Cover only, no simulation", rows[2].ReadinessText);
            Assert.AreEqual("No simulation", rows[4].ReadinessText);
            Assert.AreEqual(2.0, rows[1].YearFactor);
            Assert.AreEqual(0.5, rows[1].Frequency);
            Assert.AreEqual(1.0, rows[0].YearFactor);
            foreach (FieldReadiness readiness in Enum.GetValues(typeof(FieldReadiness)))
            {
                var row = new BodyRow { Status = BodyStatusKind.Full, Readiness = readiness };
                Assert.IsTrue(row.ReadinessText.StartsWith("Simulation", StringComparison.Ordinal));
            }
        }

        [TestMethod]
        public void BodyOptionsAreClampedDroppedAtOneAndWrittenToTheFile()
        {
            var options = BodyRows.With(null, "Duna", 100.0, -3.0);
            Assert.AreEqual(BodyPlayerOptions.MaxYearFactor, options["Duna"].YearFactor);
            Assert.AreEqual(BodyPlayerOptions.MinFrequency, options["Duna"].Frequency);
            options = BodyRows.With(options, "Eve", 0.5, 2.0);
            Assert.AreEqual(2, options.Count);
            options = BodyRows.With(options, "Duna", 1.0, 1.0);
            CollectionAssert.AreEqual(new[] { "Eve" }, options.Keys.ToArray(), "a body at 1 and 1 has no entry");
            options = BodyRows.With(options, " ", 3.0, 3.0);
            Assert.AreEqual(1, options.Count, "a blank name is ignored");

            string folder = Path.Combine(Path.GetTempPath(), "atmo-d36-" + Guid.NewGuid().ToString("N"));
            try
            {
                string path = Path.Combine(folder, "bodies.cfg");
                Assert.IsTrue(BodyRows.Write(path, options, false));
                Dictionary<string, BodyPlayerOptions> back = ModSettingsFile.Read(path, root => BodiesFile.Parse(root, null), null);
                Assert.AreEqual(1, back.Count);
                Assert.AreEqual(0.5, back["Eve"].YearFactor);
                Assert.AreEqual(2.0, back["Eve"].Frequency);
            }
            finally
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        }

        [TestMethod]
        public void SettingABodyKeepsItInMemoryUntilFlushAndResetWritesNone()
        {
            var written = new List<string>();
            var model = new WindowModel(WindowEnvironment.Full(), () => written.Add("settings"), t => { }, () => new List<BodyRow>
            {
                new BodyRow { Name = "Duna", Status = BodyStatusKind.Full }
            });
            Dictionary<string, BodyPlayerOptions> last = null;
            model.UseBodiesFile(() => new Dictionary<string, BodyPlayerOptions>(), options => { last = new Dictionary<string, BodyPlayerOptions>(options); written.Add("bodies"); });
            model.SetBody("Duna", 2.0, 3.0);
            Assert.AreEqual(0, written.Count, "a slider moves many times: nothing is written yet");
            Assert.AreEqual(2.0, model.BodyList()[0].YearFactor);
            Assert.AreEqual(3.0, model.BodyList()[0].Frequency);
            model.Flush();
            CollectionAssert.AreEqual(new[] { "bodies" }, written);
            Assert.AreEqual(2.0, last["Duna"].YearFactor);
            model.Reset();
            Assert.AreEqual(0, last.Count, "Reset clears the player's body numbers");
            Assert.AreEqual(1.0, model.BodyList()[0].YearFactor);
        }

        // --- The registration of the Keys tab ---

        [TestMethod]
        public void TheRegistrationHoldsTheForecastKeyWithoutADefault()
        {
            string path = Path.Combine(RepoRoot.Root("src", "ReDefinition.csproj"), "src", "Settings", "Atmospheres", "Atmospheres.cfg");
            var problems = new List<string>();
            ConfigNode node = ConfigNode.Parse(File.ReadAllText(path)).GetNode(ModRegistration.NodeName);
            ModRegistration registration = ModRegistration.FromConfigNode(node, problems);
            Assert.IsNotNull(registration);
            CollectionAssert.AreEqual(new string[0], problems.ToArray(), string.Join("; ", problems));
            Assert.AreEqual("atmospheres", registration.Name);
            Assert.AreEqual(1, registration.Settings.Count, "only the forecast window has a key");
            SettingRegistration key = registration.Settings[0];
            Assert.IsTrue(key.IsBinding);
            Assert.AreEqual("forecastWindow", key.Name);
            Assert.AreEqual("None", key.Default);
            Assert.IsNull(key.Member, "ReDefinition keeps the binding");
        }
    }
}
