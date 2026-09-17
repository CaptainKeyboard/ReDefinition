using System.Collections.Generic;
using System.Linq;
using FidelityFX.FSR3;
using ReDefinition.Framework;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ReDefinition.Tests
{
    // ReDefinition's own features as modules (OurModules): what a profile's MODULE node sets,
    // and the rows under General.
    [TestClass]
    public class OurModulesTests
    {
        private static GraphicsProfile Profile(string modules)
        {
            ConfigNode node = ConfigNode.Parse("REDEFINITION_PROFILE\n{\n name = test\n" + modules + "}\n")
                .GetNode("REDEFINITION_PROFILE");
            return GraphicsProfile.FromConfigNode(node, new List<string>());
        }

        private static string Upscaler(string values)
        {
            return " MODULE\n {\n  name = upscaler\n" + values + " }\n";
        }

        [TestMethod]
        public void AProfileSetsTheUpscalerAndItsModeByName()
        {
            UpscalerSettings settings = new UpscalerSettings();
            List<string> problems = new List<string>();

            ProfileApplier.ApplyModules(Profile(Upscaler("  enabled = true\n  quality = performance\n")), settings, problems);

            Assert.IsTrue(settings.Enabled);
            Assert.AreEqual(Fsr3Upscaler.QualityMode.Performance, settings.Quality);
            Assert.AreEqual(0, problems.Count, string.Join("\n", problems));
        }

        [TestMethod]
        public void AModeIsANameNeverANumber()
        {
            UpscalerSettings settings = new UpscalerSettings();
            List<string> problems = new List<string>();

            ProfileApplier.ApplyModules(Profile(Upscaler("  quality = 2\n")), settings, problems);

            Assert.AreEqual(Fsr3Upscaler.QualityMode.NativeAA, settings.Quality);
            Assert.AreEqual(1, problems.Count);
            StringAssert.Contains(problems[0], "quality = 2");
        }

        [TestMethod]
        public void AProfileLeavesTasteAndFrameGenerationToThePlayer()
        {
            UpscalerSettings settings = new UpscalerSettings();
            List<string> problems = new List<string>();

            ProfileApplier.ApplyModules(Profile(Upscaler("  sharpness = 0.5\n")
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

            ProfileApplier.ModuleValues(Profile(Upscaler("  colour = blue\n") + " MODULE\n {\n  name = shaders\n  on = True\n }\n"),
                problems);

            Assert.AreEqual(2, problems.Count, string.Join("\n", problems));
            Assert.IsTrue(problems.Any(problem => problem.Contains("colour")));
            Assert.IsTrue(problems.Any(problem => problem.Contains("'shaders': no such module")));
        }

        [TestMethod]
        public void EveryModuleValueThatDiffersCountsAsASetting()
        {
            UpscalerSettings settings = new UpscalerSettings();
            GraphicsProfile profile = Profile(Upscaler("  enabled = True\n  quality = Balanced\n"));
            Dictionary<BundledSetting, string> none = new Dictionary<BundledSetting, string>();

            Assert.AreEqual(2, ProfileApplier.Differences(profile, none, setting => null, settings));
            ProfileApplier.ApplyModules(profile, settings, new List<string>());
            Assert.AreEqual(0, ProfileApplier.Differences(profile, none, setting => null, settings));
        }

        [TestMethod]
        public void TheRowsUnderGeneralStandInTheirOrder()
        {
            CollectionAssert.AreEqual(new[] { "Upscaler", "Technique", "Mode", "DLSS preset", "Sharpness", "Frame generation" },
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
    }
}
