using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Bridges;
using ReDefinition.Settings;

namespace ReDefinition.Tests
{
    // The DLSS model setting (DlssModel): which preset each model asks NVIDIA for, its
    // default, its row, and what the bundled profiles choose.
    [TestClass]
    public class DlssModelTests
    {
        // Default leaves the choice to NGX; each other model is its preset, in every mode.
        [TestMethod]
        public void EachModelIsItsPreset()
        {
            Assert.AreEqual(DlssPreset.Default, DlssModels.Preset(DlssModel.Default));
            Assert.AreEqual(DlssPreset.K, DlssModels.Preset(DlssModel.K));
            Assert.AreEqual(DlssPreset.M, DlssModels.Preset(DlssModel.M));
            Assert.AreEqual(DlssPreset.L, DlssModels.Preset(DlssModel.L));
        }

        [TestMethod]
        public void NvidiasChoiceIsTheDefault()
        {
            Assert.AreEqual(DlssModel.Default, new OwnSettings().DlssModel);
        }

        [TestMethod]
        public void DefaultStandsFirstThenKMAndL()
        {
            ModuleSetting row = OurModules.Rows().Single(setting => setting.Title == "DLSS model");
            CollectionAssert.AreEqual(new[] { "Default", "K", "M", "L" }, row.Choices);
            Assert.AreEqual(SettingKind.Quality, row.Kind);
        }

        [TestMethod]
        public void OnlyDlssOffersIt()
        {
            ModuleSetting row = OurModules.Rows().Single(setting => setting.Title == "DLSS model");
            Assert.IsTrue(row.Interactable(new OwnSettings { Backend = UpscalerBackend.Dlss }));
            Assert.IsFalse(row.Interactable(new OwnSettings { Backend = UpscalerBackend.Fsr3 }));
        }

        [TestMethod]
        public void AProfileSetsItByName()
        {
            ConfigNode node = ConfigNode.Parse("GRAPHICS_PROFILE\n{\n name = test\n MODULE\n {\n  name = upscaler\n"
                                               + "  dlssModel = M\n }\n}\n").GetNode("GRAPHICS_PROFILE");
            OwnSettings settings = new OwnSettings();
            List<string> problems = new List<string>();

            ModuleProfiles.Apply(GraphicsProfile.FromConfigNode(node, new List<string>()), settings, problems);

            Assert.AreEqual(DlssModel.M, settings.DlssModel);
            Assert.AreEqual(0, problems.Count, string.Join("\n", problems));
        }

        // Ultra and Max take the sharpest model; the others keep NVIDIA's choice.
        [TestMethod]
        public void TheBundledProfilesChooseLForUltraAndMaxOnly()
        {
            string file = Path.Combine(Repository(), "GameData", "ReDefinition", "Profiles", "ReDefinition-Profiles.cfg");
            ConfigNode root = ConfigNode.Parse(File.ReadAllText(file));
            Dictionary<string, string> chosen = new Dictionary<string, string>();
            foreach (ConfigNode profile in root.GetNodes("GRAPHICS_PROFILE"))
                foreach (ConfigNode module in profile.GetNodes("MODULE"))
                    if (module.GetValue("name") == "upscaler")
                        chosen[profile.GetValue("name")] = module.GetValue("dlssModel");

            CollectionAssert.AreEquivalent(new[] { "low", "medium", "high", "ultra", "max" }, chosen.Keys.ToArray());
            foreach (string name in new[] { "low", "medium", "high" })
                Assert.AreEqual("Default", chosen[name], name);
            foreach (string name in new[] { "ultra", "max" })
                Assert.AreEqual("L", chosen[name], name);
        }

        private static string Repository()
        {
            DirectoryInfo directory = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "GameData", "ReDefinition")))
                directory = directory.Parent;
            Assert.IsNotNull(directory, "the repository's GameData folder was not found above the test's folder");
            return directory.FullName;
        }
    }
}
