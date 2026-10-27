using System.Collections.Generic;
using System.Linq;
using FidelityFX.FSR3;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Proxy;
using ReDefinition.Modules;
using ReDefinition.Settings;
using ReDefinition.Vendor;

namespace ReDefinition.Tests
{
    // ReDefinition's own features as modules (OurModules): what a profile's MODULE node sets,
    // and the rows under General.
    [TestClass]
    public class OurModulesTests
    {
        // A GRAPHICS_PROFILE named test holding the given MODULE nodes.
        private static GraphicsProfile Profile(string modules)
        {
            ConfigNode node = ConfigNode.Parse("GRAPHICS_PROFILE\n{\n name = test\n" + modules + "}\n")
                .GetNode("GRAPHICS_PROFILE");
            return GraphicsProfile.FromConfigNode(node, new List<string>());
        }

        // An upscaler MODULE node with the given value lines.
        private static string Upscaler(string values)
        {
            return " MODULE\n {\n  name = upscaler\n" + values + " }\n";
        }

        // A profile's upscaler module switches the upscaler on and sets its mode by its name in any letter case, with
        // technique Auto taken.
        [TestMethod]
        public void AProfileSetsTheUpscalerAndItsModeByName()
        {
            OwnSettings settings = new OwnSettings();
            List<string> problems = new List<string>();

            ModuleProfiles.Apply(Profile(Upscaler("  technique = Auto\n  quality = performance\n")), settings, problems);

            Assert.IsTrue(settings.Enabled);
            Assert.AreEqual(Fsr3Upscaler.QualityMode.Performance, settings.Quality);
            Assert.AreEqual(0, problems.Count, string.Join("\n", problems));
        }

        // A mode given as a number is reported and not taken: the number of a mode is no name a player reads.
        [TestMethod]
        public void AModeIsANameNeverANumber()
        {
            OwnSettings settings = new OwnSettings();
            List<string> problems = new List<string>();

            ModuleProfiles.Apply(Profile(Upscaler("  quality = 2\n")), settings, problems);

            Assert.AreEqual(Fsr3Upscaler.QualityMode.NativeAA, settings.Quality);
            Assert.AreEqual(1, problems.Count);
            StringAssert.Contains(problems[0], "quality = 2");
        }

        // A profile sets frame generation but neither the sharpness, which is taste, nor the hotkeys: both are
        // reported and left as they are.
        [TestMethod]
        public void AProfileLeavesTasteAndTheHotkeysToThePlayer()
        {
            OwnSettings settings = new OwnSettings();
            List<string> problems = new List<string>();

            ModuleProfiles.Apply(Profile(Upscaler("  sharpness = 0.5\n")
                                                + " MODULE\n {\n  name = frameGeneration\n  enabled = True\n }\n"
                                                + " MODULE\n {\n  name = hotkeys\n  upscalerKey = F5\n }\n"),
                settings, problems);

            Assert.AreEqual(1.0f, settings.Sharpness);
            Assert.IsTrue(settings.FrameGeneration, "frame generation is the profile's");
            Assert.AreEqual(KeyCombination.NoneText, settings.UpscalerKey);
            Assert.AreEqual(2, problems.Count, string.Join("\n", problems));
        }

        // A value can differ by the card: the first alternative naming the game's card class is taken, else the
        // one without classes.
        [TestMethod]
        public void AValueDiffersByTheCard()
        {
            GraphicsProfile profile = Profile(Upscaler("  dlssModel = K (Rtx20 Rtx30), L (Rtx40 Rtx50), Default\n"));

            Assert.AreEqual("K", ModuleProfiles.Values(profile, null, GpuClass.Rtx30)[0].Value);
            Assert.AreEqual("L", ModuleProfiles.Values(profile, null, GpuClass.Rtx40)[0].Value);
            Assert.AreEqual("Default", ModuleProfiles.Values(profile, null, GpuClass.Radeon9000)[0].Value);
            Assert.AreEqual("Default", ModuleProfiles.Values(profile, null, GpuClass.Unknown)[0].Value);
        }

        // A value with alternatives for some cards and none for all gives nothing on another card, leaving the
        // setting as it is.
        [TestMethod]
        public void ACardNoAlternativeIsForGetsNoValue()
        {
            GraphicsProfile profile = Profile(Upscaler("  dlssModel = K (Rtx20 Rtx30)\n"));

            Assert.AreEqual(0, ModuleProfiles.Values(profile, null, GpuClass.Rtx40).Count);
            Assert.AreEqual(1, ModuleProfiles.Values(profile, null, GpuClass.Rtx20).Count);
        }

        // Every alternative of a value is checked whatever card the game runs on: a value the setting cannot take,
        // an unknown card class, and a value for every card that does not stand last are reported.
        [TestMethod]
        public void EveryAlternativeIsCheckedWhateverTheCard()
        {
            List<string> problems = new List<string>();

            ModuleProfiles.Values(Profile(Upscaler("  dlssModel = K (Rtx20), Q\n")), problems, GpuClass.Rtx20);
            ModuleProfiles.Values(Profile(Upscaler("  dlssModel = K (Rtx999), L\n")), problems, GpuClass.Rtx20);
            ModuleProfiles.Values(Profile(Upscaler("  dlssModel = K, L (Rtx20)\n")), problems, GpuClass.Rtx20);

            Assert.AreEqual(3, problems.Count, string.Join("\n", problems));
            StringAssert.Contains(problems[0], "dlssModel = K (Rtx20), Q");
            StringAssert.Contains(problems[1], "Rtx999");
            StringAssert.Contains(problems[2], "must stand last");
        }

        // A setting a module does not have and a module that does not exist are each reported.
        [TestMethod]
        public void AModuleOrASettingThatIsNotThereIsReported()
        {
            List<string> problems = new List<string>();

            ModuleProfiles.Values(Profile(Upscaler("  colour = blue\n") + " MODULE\n {\n  name = shaders\n  on = True\n }\n"),
                problems);

            Assert.AreEqual(2, problems.Count, string.Join("\n", problems));
            Assert.IsTrue(problems.Any(problem => problem.Contains("colour")));
            Assert.IsTrue(problems.Any(problem => problem.Contains("'shaders': no such module")));
        }

        // Each module value a profile sets that the settings do not hold yet counts as one difference, and
        // none once the profile is applied.
        [TestMethod]
        public void EveryModuleValueThatDiffersCountsAsASetting()
        {
            OwnSettings settings = new OwnSettings();
            GraphicsProfile profile = Profile(Upscaler("  enabled = True\n  quality = Balanced\n"));

            Assert.AreEqual(2, ModuleProfiles.Differences(profile, settings));
            ModuleProfiles.Apply(profile, settings, new List<string>());
            Assert.AreEqual(0, ModuleProfiles.Differences(profile, settings));
        }

        // The profile that makes ReDefinition active switches the upscaler on; once
        // one is chosen, the switch is the player's, and another profile neither
        // sets it nor counts it as a difference.
        [TestMethod]
        public void OnlyTheFirstProfileSwitchesTheUpscalerOn()
        {
            GraphicsProfile profile = Profile(Upscaler("  enabled = True\n  quality = Balanced\n"));
            OwnSettings switchedOff = new OwnSettings();

            ModuleProfiles.Apply(profile, switchedOff, new List<string>(), true);
            Assert.IsFalse(switchedOff.Enabled);
            Assert.AreEqual(Fsr3Upscaler.QualityMode.Balanced, switchedOff.Quality);
            Assert.AreEqual(0, ModuleProfiles.Differences(profile, switchedOff, true));

            OwnSettings first = new OwnSettings();
            ModuleProfiles.Apply(profile, first, new List<string>());
            Assert.IsTrue(first.Enabled);
        }

        // Reset: every setting back to its default, but the player's menu choices; the graphics
        // settings are the profile's, which the Reset chooses over them.
        [TestMethod]
        public void TheResetKeepsWhatIsNoGraphicsSetting()
        {
            OwnSettings now = new OwnSettings
            {
                Enabled = true,
                Backend = UpscalerBackend.Dlss,
                BackendAuto = false,
                Raytracing = Raytracing.RaytracingMode.Full,
                RaytracingBounces = 3,
                ReplaceKspSettings = true,
                FrameGeneration = true,
                Sharpness = 0.3f,
                Quality = Fsr3Upscaler.QualityMode.Performance,
                QualityAuto = false,
            };

            OwnSettings reset = OwnSettingsReset.From(now, true);
            Assert.IsTrue(reset.ReplaceKspSettings);
            Assert.IsFalse(reset.Enabled);
            Assert.IsTrue(reset.BackendAuto);
            Assert.AreEqual(Raytracing.RaytracingMode.Off, reset.Raytracing);
            Assert.IsFalse(reset.FrameGeneration);
            Assert.AreEqual(1.0f, reset.Sharpness);
            Assert.AreEqual(Fsr3Upscaler.QualityMode.NativeAA, reset.Quality);
            Assert.IsTrue(reset.QualityAuto);
        }

        // The General tab's own rows stand in their order, by title.
        [TestMethod]
        public void TheUpscalingRowsStandInTheirOrder()
        {
            CollectionAssert.AreEqual(new[] { "Upscaler", "Mode", "Sharpness", "One antialiasing per camera", "Frame generation", "Multiplier",
                                            "Low latency", "Raytracing", "Ray Reconstruction", "Moonlight", "Moon reflection", "Moon glow", "Sun reflection", "Night light" },
                OurModules.Rows().Select(setting => setting.Title).ToArray());
        }

        // The mode's list runs Auto first, then from Ultra Performance, the smallest render size, to AA only.
        [TestMethod]
        public void TheModesRunFromTheSmallestRenderSizeToAAOnly()
        {
            string[] modes = OurModules.Upscaler.Setting("quality").Choices;

            Assert.AreEqual(7, modes.Length);
            Assert.AreEqual("Auto", modes[0]);
            Assert.AreEqual("UltraPerformance", modes[1]);
            Assert.AreEqual("NativeAA", modes[modes.Length - 1]);
        }
        // The settings window builds a switch, a slider or a list for every own row
        // of a tab but Keys, whose rows are bindings it builds itself. A binding in
        // another tab, or a list without its choices, would stop the window from
        // opening.
        [TestMethod]
        public void EveryRowOutsideKeysHasAControlTheWindowBuilds()
        {
            foreach (SettingCategory tab in System.Enum.GetValues(typeof(SettingCategory)))
            {
                foreach (ModuleSetting row in OurModules.Rows(tab))
                {
                    if (tab == SettingCategory.Keys)
                    {
                        Assert.AreEqual(SettingControl.Binding, row.Control, row.Key);
                        continue;
                    }
                    Assert.IsTrue(row.Control == SettingControl.Toggle || row.Control == SettingControl.Slider
                                  || row.Control == SettingControl.Choice, tab + "/" + row.Key + ": " + row.Control);
                    if (row.Control == SettingControl.Choice)
                        Assert.IsTrue(row.Choices != null && row.Choices.Length > 0, row.Key);
                    Assert.IsNotNull(row.Read, row.Key);
                    Assert.IsNotNull(row.Write, row.Key);
                }
            }
        }

        // Every switch of the flight UI is on as it ships, and each turns off its own
        // feature alone, which the settings file's change check sees.
        [TestMethod]
        public void TheFlightUiSwitchesStartOnAndTurnOffAlone()
        {
            IList<ModuleSetting> rows = OurModules.FlightUi.Settings;
            foreach (ModuleSetting row in rows)
                Assert.AreEqual(bool.TrueString, row.Read(new OwnSettings()), row.Key + " ships on");
            foreach (ModuleSetting row in rows)
            {
                OwnSettings settings = new OwnSettings();
                string before = settings.Snapshot();
                row.Write(settings, bool.FalseString);
                Assert.AreNotEqual(before, settings.Snapshot(), row.Key + " is saved");
                foreach (ModuleSetting other in rows)
                    Assert.AreEqual(other == row ? bool.FalseString : bool.TrueString, other.Read(settings), row.Key + " leaves " + other.Key);
            }
        }

        // The switches and percentage sliders of ReDefinition's own modules, by module and key, with the key
        // each is saved under in settings.cfg.
        private static readonly string[][] OwnToggles =
        {
            new[] { "upscaler", "oneAntialiasing", "oneAntialiasing" },
            new[] { "raytracing", "moonGlow", "moonGlow" },
            new[] { "flightUi", "flightPanelAuto", "flightPanelAuto" },
            new[] { "flightUi", "autoAltimeter", "autoAltimeter" },
            new[] { "flightUi", "flightStartsInManeuverMode", "flightStartsInManeuverMode" },
            new[] { "flightUi", "worldButtons", "worldButtons" },
            new[] { "flightUi", "roundGauge", "roundGauge" },
            new[] { "flightUi", "navballSpeeds", "navballSpeeds" },
            new[] { "flightUi", "navballDockingMarker", "navballDockingMarker" },
            new[] { "flightUi", "navballDrag", "navballDrag" },
            new[] { "flightUi", "flightPanel", "flightPanel" },
            new[] { "flightUi", "stageDetailColumns", "stageDetailColumns" },
            new[] { "flightUi", "stageTwrRange", "stageTwrRange" },
            new[] { "flightUi", "deltaVTools", "deltaVTools" },
            new[] { "flightUi", "hudTooltips", "hudTooltips" },
            new[] { "flightUi", "stagingResetAsks", "stagingResetAsks" },
            new[] { "flightUi", "crewSlotButtons", "crewSlotButtons" },
            new[] { "flightUi", "timeWarpPanelButton", "timeWarpPanelButton" },
            new[] { "uiScaling", "scaleModWindows", "scaleModWindows" },
            new[] { "mainMenu", "menuFireworks", "menuFireworks" },
            new[] { "mainMenu", "menuKscZoom", "menuKscZoom" },
            new[] { "spaceCentre", "kscGrass", "kscGrass" },
            new[] { "spaceCentre", "kscLawnTerrain", "kscLawnTerrain" },
            new[] { "reEntry", "fireflyIdle", "fireflyIdle" },
            new[] { "dashboard", "dashboardNetwork", "dashboardNetwork" },
        };

        private static readonly string[][] OwnPercentages =
        {
            new[] { "raytracing", "moonlight", "raytracingMoonlight" },
            new[] { "raytracing", "moonReflection", "raytracingMoonReflection" },
            new[] { "raytracing", "sunReflection", "sunReflection" },
            new[] { "raytracing", "nightAmbient", "raytracingNightAmbient" },
        };

        // Each switch of the own modules reads and writes the setting of its file key and no other, in the text the
        // file holds; a text that is no True or False is refused with an exception.
        [TestMethod]
        public void EachSwitchOfTheOwnModulesMapsToItsFileKey()
        {
            foreach (string[] toggle in OwnToggles)
            {
                ModuleSetting row = OurModules.Find(toggle[0]).Setting(toggle[1]);
                Assert.IsNotNull(row, toggle[0] + "." + toggle[1]);
                Assert.AreEqual(SettingControl.Toggle, row.Control, toggle[1]);
                foreach (bool on in new[] { false, true })
                {
                    OwnSettings settings = new OwnSettings();
                    OwnSettings others = new OwnSettings();
                    row.Write(settings, on ? "True" : "False");
                    Assert.AreEqual(on ? "True" : "False", settings.ValueOf(toggle[2]), toggle[1] + " in the file");
                    Assert.AreEqual(on ? "True" : "False", row.Read(settings), toggle[1] + " read back");
                    foreach (string[] other in OwnToggles)
                        if (other[2] != toggle[2])
                            Assert.AreEqual(others.ValueOf(other[2]), settings.ValueOf(other[2]), toggle[1] + " leaves " + other[1]);
                }
                OwnSettings refused = new OwnSettings();
                try
                {
                    row.Write(refused, "yes");
                    Assert.Fail(toggle[1] + " took 'yes'");
                }
                catch (System.FormatException)
                {
                }
            }
        }

        // The percentage sliders likewise: the value as a whole number, in the file's key.
        [TestMethod]
        public void EachPercentageSliderOfTheOwnModulesMapsToItsFileKey()
        {
            foreach (string[] slider in OwnPercentages)
            {
                ModuleSetting row = OurModules.Find(slider[0]).Setting(slider[1]);
                Assert.IsNotNull(row, slider[0] + "." + slider[1]);
                Assert.AreEqual(SettingControl.Slider, row.Control, slider[1]);
                OwnSettings settings = new OwnSettings();
                row.Write(settings, "42");
                Assert.AreEqual("42", settings.ValueOf(slider[2]), slider[1] + " in the file");
                Assert.AreEqual("42", row.Read(settings), slider[1] + " read back");
                row.Write(settings, "7.6");
                Assert.AreEqual("8", row.Read(settings), slider[1] + " shows whole percentages");
                Assert.AreEqual(row.Read(settings), settings.ValueOf(slider[2]), slider[1] + " as the file writes it");
                foreach (string[] other in OwnPercentages)
                    if (other[2] != slider[2])
                        Assert.AreEqual(new OwnSettings().ValueOf(other[2]), settings.ValueOf(other[2]), slider[1] + " leaves " + other[1]);
            }
        }
    }
}
