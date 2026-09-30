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

        [TestMethod]
        public void TheUpscalingRowsStandInTheirOrder()
        {
            CollectionAssert.AreEqual(new[] { "Upscaler", "Technique", "Mode", "DLSS model", "Sharpness", "Frame generation",
                                            "Raytracing", "Light bounces" },
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
    }
}
