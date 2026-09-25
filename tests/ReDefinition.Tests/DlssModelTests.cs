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
        [TestMethod]
        public void HighIsPresetLAndFastIsPresetM()
        {
            Assert.AreEqual(DlssPreset.L, DlssModels.Preset(DlssModel.High));
            Assert.AreEqual(DlssPreset.M, DlssModels.Preset(DlssModel.Fast));
        }

        [TestMethod]
        public void HighIsTheDefault()
        {
            Assert.AreEqual(DlssModel.High, new OwnSettings().DlssModel);
        }

        [TestMethod]
        public void FastStandsLeftOfHigh()
        {
            ModuleSetting row = OurModules.Rows().Single(setting => setting.Title == "DLSS model");
            CollectionAssert.AreEqual(new[] { "Fast", "High" }, row.Choices);
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
                                               + "  dlssModel = Fast\n }\n}\n").GetNode("GRAPHICS_PROFILE");
            OwnSettings settings = new OwnSettings();
            List<string> problems = new List<string>();

            ModuleProfiles.Apply(GraphicsProfile.FromConfigNode(node, new List<string>()), settings, problems);

            Assert.AreEqual(DlssModel.Fast, settings.DlssModel);
            Assert.AreEqual(0, problems.Count, string.Join("\n", problems));
        }

        // Low takes the faster model; the others keep the default.
        [TestMethod]
        public void TheBundledProfilesChooseFastForLowOnly()
        {
            string file = Path.Combine(Repository(), "GameData", "ReDefinition", "Profiles", "ReDefinition-Profiles.cfg");
            ConfigNode root = ConfigNode.Parse(File.ReadAllText(file));
            Dictionary<string, string> chosen = new Dictionary<string, string>();
            foreach (ConfigNode profile in root.GetNodes("GRAPHICS_PROFILE"))
                foreach (ConfigNode module in profile.GetNodes("MODULE"))
                    if (module.GetValue("name") == "upscaler")
                        chosen[profile.GetValue("name")] = module.GetValue("dlssModel");

            CollectionAssert.AreEquivalent(new[] { "low", "medium", "high", "ultra", "max" }, chosen.Keys.ToArray());
            Assert.AreEqual("Fast", chosen["low"]);
            foreach (string name in new[] { "medium", "high", "ultra", "max" })
                Assert.AreEqual("High", chosen[name], name);
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
