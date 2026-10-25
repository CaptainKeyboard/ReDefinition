using System.Collections.Generic;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Settings;
using ReDefinition.Window;

namespace ReDefinition.Tests
{
    // The registrations ReDefinition ships (GameData/ReDefinition/Mods), read as the
    // game reads them: no problem in any, and the values the compatibility audit
    // checked against the mods' code.
    [TestClass]
    public class ShippedRegistrationTests
    {
        // Every registration file shipped reads without a single problem: one would show in the player's log.
        [TestMethod]
        public void EveryShippedRegistrationReadsWithoutAProblem()
        {
            string folder = Path.Combine(Repository(), "GameData", "ReDefinition", "Mods");
            foreach (string file in Directory.GetFiles(folder, "*.cfg"))
            {
                List<string> problems = new List<string>();
                Assert.IsNotNull(Read(file, problems), file);
                Assert.AreEqual(0, problems.Count, file + ": " + string.Join("\n", problems.ToArray()));
            }
        }

        // ModDefaults checks a setting's own default against the registration's
        // version: an empty DEFAULTS block's version reached none of them.
        [TestMethod]
        public void VersionsStandWhereTheSettingsDefaultsAreCheckedAgainstThem()
        {
            Assert.AreEqual("3.2.2.0", Mod("EVE").Version);
            Assert.AreEqual("1.1.1.0", Mod("TUFX").Version);
        }

        // Interface.disabled starts false: its window shows, inverted into the row.
        [TestMethod]
        public void PersistentRotationsWindowShowsByDefault()
        {
            SettingRegistration window = Mod("PersistentRotation").Setting("window");
            Assert.IsTrue(window.Invert);
            Assert.AreEqual("True", window.Default);
        }

        // When the audited settings take effect, as the audit found in the mods' code: Waterfall's queues live,
        // its distortion queue in the next scene, the flare saturation and Scatterer's two after a restart.
        [TestMethod]
        public void WhenTheAuditedSettingsTakeEffect()
        {
            ModRegistration waterfall = Mod("Waterfall");
            Assert.AreEqual(ApplyWindow.Live, waterfall.Setting("TransparentQueueBase").TakesEffect);
            Assert.AreEqual(ApplyWindow.Live, waterfall.Setting("QueueDepth").TakesEffect);
            Assert.AreEqual(ApplyWindow.Live, waterfall.Setting("SortedDepth").TakesEffect);
            Assert.AreEqual(ApplyWindow.NextScene, waterfall.Setting("DistortQueue").TakesEffect);
            Assert.AreEqual(ApplyWindow.Restart, Mod("DistantObject").Setting("flareSaturation").TakesEffect);
            ModRegistration scatterer = Mod("Scatterer");
            Assert.AreEqual(ApplyWindow.Restart, scatterer.Setting("integrateWithEVEClouds").TakesEffect);
            Assert.AreEqual(ApplyWindow.Restart, scatterer.Setting("overrideNearClipPlane").TakesEffect);
            Assert.IsNotNull(Mod("TUFX").Setting("ShowToolbarButton").LeftOut);
        }

        // KSP's settings screen's frame limits, 160 among them, and its ambient
        // boosts from -1.
        [TestMethod]
        public void KspsOwnRangesAreKspsScreens()
        {
            ModRegistration ksp = Mod("KSP");
            CollectionAssert.Contains(ksp.Setting("FRAMERATE_LIMIT").Choices, "160");
            foreach (string name in new[]
                     {
                         "AMBIENTLIGHT_BOOSTFACTOR", "AMBIENTLIGHT_BOOSTFACTOR_MAPONLY", "AMBIENTLIGHT_BOOSTFACTOR_EDITONLY",
                     })
                Assert.AreEqual(-1f, ksp.Setting(name).Min, name);
        }

        // Scatterer's window opens on either modifier with either key: a crossed
        // combination counts, and its own rows are no conflict with each other.
        [TestMethod]
        public void ACrossedCombinationCountsAmongTheConflicts()
        {
            Assert.AreEqual("RightAlt+F10", Conflicts.Crossed("LeftAlt+F10", "RightAlt+F11"));
            // A row without a modifier has its key written as its modifier: held, it is the
            // other row's modifier.
            Assert.AreEqual(new KeyCombination(UnityEngine.KeyCode.F10, UnityEngine.KeyCode.F11, UnityEngine.KeyCode.None).ToString(),
                            Conflicts.Crossed("F10", "F11"));
            Assert.AreEqual(KeyCombination.NoneText, Conflicts.Crossed("F10", "F10"));
            Assert.AreEqual(KeyCombination.NoneText, Conflicts.Crossed("None", "RightAlt+F11"));

            string[] keys = { "own1", "own2", "own1~crossed", "other" };
            string[] texts = { "LeftAlt+F10", "RightAlt+F11", "RightAlt+F10", "RightAlt+F10" };
            int[] situations = { KspKeyBindings.Everywhere, KspKeyBindings.Everywhere, KspKeyBindings.Everywhere,
                                 KspKeyBindings.Everywhere };
            int[] modes = { -1, -1, -1, -1 };
            string[] shipped = { null, null, null, null };
            CollectionAssert.AreEquivalent(new[] { "own1~crossed", "other" },
                Conflicts.Sharing(keys, texts, situations, modes, shipped, new[] { "mod", "mod", "mod", null }));
            // The same two rows alike: one window, no conflict.
            Assert.AreEqual(0, Conflicts.Sharing(new[] { "own1", "own2" }, new[] { "LeftAlt+F10", "LeftAlt+F10" },
                new[] { KspKeyBindings.Everywhere, KspKeyBindings.Everywhere }, new[] { -1, -1 },
                new string[] { null, null }, new[] { "mod", "mod" }).Count);
        }

        // The shipped registration of that file name in GameData/ReDefinition/Mods, which must read.
        private static ModRegistration Mod(string file)
        {
            List<string> problems = new List<string>();
            ModRegistration mod = Read(Path.Combine(Repository(), "GameData", "ReDefinition", "Mods", file + ".cfg"),
                problems);
            Assert.IsNotNull(mod, file);
            return mod;
        }

        // Reads a registration file as the game does, its problems added to `problems`.
        private static ModRegistration Read(string file, List<string> problems)
        {
            ConfigNode node = ConfigNode.Parse(File.ReadAllText(file)).GetNode(ModRegistration.NodeName);
            return ModRegistration.FromConfigNode(node, problems);
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
