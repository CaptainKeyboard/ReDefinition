using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Bridges;
using ReDefinition.Modules;
using ReDefinition.Settings;
using ReDefinition.Vendor;

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

        // A fresh install leaves the model to NVIDIA.
        [TestMethod]
        public void NvidiasChoiceIsTheDefault()
        {
            Assert.AreEqual(DlssModel.Default, new OwnSettings().DlssModel);
        }

        // The row offers Default first, then K, M and L, and counts as quality, which profiles set.
        [TestMethod]
        public void DefaultStandsFirstThenKMAndL()
        {
            ModuleSetting row = OurModules.Upscaler.Setting("dlssModel");
            CollectionAssert.AreEqual(new[] { "Default", "K", "M", "L" }, row.Choices);
            Assert.AreEqual(SettingKind.Quality, row.Kind);
        }

        // The model's row can be changed only while DLSS is the technique.
        [TestMethod]
        public void OnlyDlssOffersIt()
        {
            ModuleSetting row = OurModules.Upscaler.Setting("dlssModel");
            Assert.IsTrue(row.Interactable(new OwnSettings { Backend = UpscalerBackend.Dlss }));
            Assert.IsFalse(row.Interactable(new OwnSettings { Backend = UpscalerBackend.Fsr3 }));
        }

        // A profile's upscaler module sets the model by its name.
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

        // Ultra and Max take the sharpest model, the others keep NVIDIA's choice; the RTX 20 and 30,
        // whose tensor cores take the larger models dearly, take K in every profile.
        [TestMethod]
        public void TheBundledProfilesChooseLForUltraAndMaxOnTheNewerCards()
        {
            foreach (string name in new[] { "low", "medium", "high" })
            {
                Assert.AreEqual("Default", Chosen(name, GpuClass.Rtx40, "upscaler", "dlssModel"), name);
                Assert.AreEqual("K", Chosen(name, GpuClass.Rtx30, "upscaler", "dlssModel"), name);
            }
            foreach (string name in new[] { "ultra", "max" })
            {
                Assert.AreEqual("L", Chosen(name, GpuClass.Rtx50, "upscaler", "dlssModel"), name);
                Assert.AreEqual("K", Chosen(name, GpuClass.Rtx20, "upscaler", "dlssModel"), name);
            }
        }

        // The table of defaults (.local/plans/settings-defaults.md) for what the bundled
        // profiles set of ReDefinition's own modules.
        [TestMethod]
        public void TheBundledProfilesSetTheUpscalerFrameGenerationLatencyAndRaytracing()
        {
            string[] names = { "low", "medium", "high", "ultra", "max" };
            string[] heights = { "720", "960", "960", "1200", "1440" };
            string[] raytracing = { "Off", "Off", "Shadows", "Light", "Full" };
            for (int i = 0; i < names.Length; i++)
            {
                string name = names[i];
                Assert.AreEqual("Auto", Chosen(name, GpuClass.Rtx40, "upscaler", "technique"), name);
                Assert.AreEqual("Auto", Chosen(name, GpuClass.Rtx40, "upscaler", "quality"), name);
                Assert.AreEqual(heights[i], Chosen(name, GpuClass.Rtx40, "upscaler", "renderTargetHeight"), name);
                Assert.AreEqual("Auto", Chosen(name, GpuClass.Rtx40, "latency", "mode"), name);
                Assert.AreEqual(raytracing[i], Chosen(name, GpuClass.Rtx40, "raytracing", "mode"), name);
                Assert.AreEqual("Auto", Chosen(name, GpuClass.Rtx40, "raytracing", "rayReconstruction"), name);

                // Frame generation Off on Low, the technique Auto elsewhere; Dynamic on an RTX 50 and, with the
                // package, on an RTX 20 and 30; 2x elsewhere.
                Assert.AreEqual(i == 0 ? "Off" : "Auto", Chosen(name, GpuClass.Rtx40, "frameGeneration", "technique"), name);
                Assert.AreEqual(i == 0 ? null : "Dynamic", Chosen(name, GpuClass.Rtx50, "frameGeneration", "mode"), name);
                Assert.AreEqual(i == 0 ? null : "Dynamic", Chosen(name, GpuClass.Rtx30, "frameGeneration", "mode"), name);
                Assert.AreEqual(i == 0 ? null : "2x", Chosen(name, GpuClass.Rtx40, "frameGeneration", "mode"), name);
                Assert.AreEqual(i == 0 ? null : "2x", Chosen(name, GpuClass.Radeon9000, "frameGeneration", "mode"), name);
            }
        }

        // The value the bundled profile of that name sets for a module's setting on the card class given, or null
        // where it sets none; the profile must read without problems.
        private static string Chosen(string profileName, GpuClass gpu, string module, string key)
        {
            string file = Path.Combine(Repository(), "GameData", "ReDefinition", "Profiles", "ReDefinition-Profiles.cfg");
            ConfigNode root = ConfigNode.Parse(File.ReadAllText(file));
            foreach (ConfigNode node in root.GetNodes("GRAPHICS_PROFILE"))
            {
                if (node.GetValue("name") != profileName) continue;
                List<string> problems = new List<string>();
                GraphicsProfile profile = GraphicsProfile.FromConfigNode(node, problems);
                foreach (KeyValuePair<ModuleSetting, string> pair in ModuleProfiles.Values(profile, problems, gpu))
                    if (pair.Key == OurModules.Find(module).Setting(key)) return pair.Value;
                Assert.AreEqual(0, problems.Count, string.Join("\n", problems));
                return null;
            }
            Assert.Fail("no profile " + profileName);
            return null;
        }

        // The repository's root, found as the first folder above the test's that holds GameData/ReDefinition.
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
