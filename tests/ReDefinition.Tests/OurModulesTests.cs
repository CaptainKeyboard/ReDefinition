using System.Collections.Generic;
using System.Linq;
using FidelityFX.FSR3;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Settings;

namespace ReDefinition.Tests
{
    // ReDefinition's own features as modules (OurModules): what a profile's MODULE node sets,
    // and the rows under General.
    [TestClass]
    public class OurModulesTests
    {
        private static GraphicsProfile Profile(string modules)
        {
            ConfigNode node = ConfigNode.Parse("GRAPHICS_PROFILE\n{\n name = test\n" + modules + "}\n")
                .GetNode("GRAPHICS_PROFILE");
            return GraphicsProfile.FromConfigNode(node, new List<string>());
        }

        private static string Upscaler(string values)
        {
            return " MODULE\n {\n  name = upscaler\n" + values + " }\n";
        }

        [TestMethod]
        public void AProfileSetsTheUpscalerAndItsModeByName()
        {
            OwnSettings settings = new OwnSettings();
            List<string> problems = new List<string>();

            ModuleProfiles.Apply(Profile(Upscaler("  enabled = true\n  quality = performance\n")), settings, problems);

            Assert.IsTrue(settings.Enabled);
            Assert.AreEqual(Fsr3Upscaler.QualityMode.Performance, settings.Quality);
            Assert.AreEqual(0, problems.Count, string.Join("\n", problems));
        }

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

        [TestMethod]
        public void AProfileLeavesTasteAndFrameGenerationToThePlayer()
        {
            OwnSettings settings = new OwnSettings();
            List<string> problems = new List<string>();

            ModuleProfiles.Apply(Profile(Upscaler("  sharpness = 0.5\n")
                                                + " MODULE\n {\n  name = frameGeneration\n  enabled = True\n }\n"),
                settings, problems);

            Assert.AreEqual(1.0f, settings.Sharpness);
            Assert.IsFalse(settings.FrameGeneration);
            Assert.AreEqual(2, problems.Count, string.Join("\n", problems));
        }

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

        // Reset: every setting back to its default and frame generation off, but
        // the player's hardware and menu choices, and once a profile is chosen
        // the upscaler's switch.
        [TestMethod]
        public void TheResetKeepsWhatIsNoQuality()
        {
            OwnSettings now = new OwnSettings
            {
                Enabled = true,
                Backend = Bridges.UpscalerBackend.Dlss,
                Raytracing = Raytracing.RaytracingMode.Full,
                RaytracingBounces = 3,
                ReplaceKspSettings = true,
                FrameGeneration = true,
                Sharpness = 0.3f,
                Quality = Fsr3Upscaler.QualityMode.Performance,
            };

            OwnSettings reset = OwnSettingsReset.From(now, true);
            Assert.AreEqual(Bridges.UpscalerBackend.Dlss, reset.Backend);
            Assert.AreEqual(Raytracing.RaytracingMode.Full, reset.Raytracing);
            Assert.AreEqual(3, reset.RaytracingBounces);
            Assert.IsTrue(reset.ReplaceKspSettings);
            Assert.IsTrue(reset.Enabled);
            Assert.IsFalse(reset.FrameGeneration);
            Assert.AreEqual(1.0f, reset.Sharpness);
            Assert.AreEqual(Fsr3Upscaler.QualityMode.NativeAA, reset.Quality);

            Assert.IsFalse(OwnSettingsReset.From(now, false).Enabled, "without a profile, High switches it on");
        }

        [TestMethod]
        public void TheUpscalingRowsStandInTheirOrder()
        {
            CollectionAssert.AreEqual(new[] { "Upscaler", "Technique", "Mode", "DLSS model", "Sharpness", "Frame generation",
                                            "Raytracing", "Light bounces", "Ray Reconstruction", "Ray Reconstruction from", "Moonlight", "Moon reflection", "Moon glow", "Sun reflection", "Night light", "Light colour" },
                OurModules.Rows().Select(setting => setting.Title).ToArray());
        }

        [TestMethod]
        public void TheModesRunFromTheSmallestRenderSizeToAAOnly()
        {
            string[] modes = OurModules.Upscaler.Setting("quality").Choices;

            Assert.AreEqual(6, modes.Length);
            Assert.AreEqual("UltraPerformance", modes[0]);
            Assert.AreEqual("NativeAA", modes[modes.Length - 1]);
        }
        // The settings window builds a switch, a slider or a list for every own row
        // of a tab but Keys, whose rows are bindings it builds itself: a binding in
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
    }
}
