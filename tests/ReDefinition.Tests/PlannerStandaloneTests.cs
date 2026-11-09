using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Core;
using ReDefinition.Planner;

namespace ReDefinition.Tests
{
    // The planner on its own: how it joins ReDefinition, where it keeps its files and what it does with a damaged one.
    [TestClass]
    public class PlannerStandaloneTests
    {
        [TestMethod]
        public void ThePlannerIsJoinedOnlyWhereItNamesTheLevelReDefinitionFills()
        {
            var warnings = new List<string>();
            bool filled = false;
            Assert.IsTrue(PlannerLink.Connect(() => true, () => filled = true, warnings.Add));
            Assert.IsTrue(filled);
            Assert.AreEqual(0, warnings.Count);

            filled = false;
            Assert.IsFalse(PlannerLink.Connect(() => false, () => filled = true, warnings.Add));
            Assert.IsFalse(filled);
            Assert.AreEqual(1, warnings.Count);
        }

        [TestMethod]
        public void APlannerWithoutTheMemberOrWithATypeThatDoesNotLoadStandsAlone()
        {
            Exception[] old = { new MissingMethodException(), new MissingFieldException(), new TypeLoadException(), new TypeInitializationException("T", null) };
            foreach (Exception cause in old)
            {
                var warnings = new List<string>();
                bool filled = false;
                Assert.IsFalse(PlannerLink.Connect(() => { throw cause; }, () => filled = true, warnings.Add), cause.GetType().Name);
                Assert.IsFalse(filled, cause.GetType().Name);
                Assert.AreEqual(1, warnings.Count, cause.GetType().Name);
            }
        }

        [TestMethod]
        public void AnyOtherFailureOfTheCheckIsNotHidden()
        {
            Assert.ThrowsException<InvalidOperationException>(() => PlannerLink.Connect(() => { throw new InvalidOperationException(); }, () => { }, s => { }));
        }

        [TestMethod]
        public void AnOlderCommonThanTheOneBuiltAgainstIsNamedAtTheStart()
        {
            Assert.IsNull(PlannerLink.CommonProblem(new Version(0, 1, 4, 0), new Version(0, 1, 4, 0)));
            Assert.IsNull(PlannerLink.CommonProblem(new Version(0, 1, 4, 0), new Version(0, 1, 5, 0)));
            Assert.IsNull(PlannerLink.CommonProblem(null, new Version(0, 1, 3, 0)));
            StringAssert.Contains(PlannerLink.CommonProblem(new Version(0, 1, 5, 0), new Version(0, 1, 4, 0)), "0.1.4");
        }

        [TestMethod]
        public void EachModKeepsItsFilesInItsOwnPluginData()
        {
            string path = ModFolder.PluginFile("root", "ReDefinitionPlanner", "settings.cfg");
            Assert.AreEqual(Path.Combine("root", "GameData", "ReDefinitionPlanner", "PluginData", "settings.cfg"), path);
            Assert.AreNotEqual(path, ModFolder.PluginFile("root", "ReDefinition", "settings.cfg"));
        }

        [TestMethod]
        public void TheSwitchesComeBackAsTheyWereSaved()
        {
            string file = Path.Combine(Path.GetTempPath(), "planner-settings-" + Guid.NewGuid().ToString("N"), "settings.cfg");
            bool background = PlannerSettings.BackgroundRoute, buttons = PlannerSettings.WorldButtons;
            try
            {
                PlannerSettings.BackgroundRoute = false;
                PlannerSettings.WorldButtons = true;
                PlannerSettings.Write(file);
                PlannerSettings.BackgroundRoute = true;
                PlannerSettings.WorldButtons = false;
                PlannerSettings.Read(file);
                Assert.IsFalse(PlannerSettings.BackgroundRoute);
                Assert.IsTrue(PlannerSettings.WorldButtons);
            }
            finally
            {
                PlannerSettings.BackgroundRoute = background;
                PlannerSettings.WorldButtons = buttons;
                if (File.Exists(file)) Directory.Delete(Path.GetDirectoryName(file), true);
            }
        }

        [TestMethod]
        public void AMissingOrDamagedSettingsFileLeavesTheSwitchesAlone()
        {
            string folder = Path.Combine(Path.GetTempPath(), "planner-settings-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            bool background = PlannerSettings.BackgroundRoute, buttons = PlannerSettings.WorldButtons;
            try
            {
                PlannerSettings.BackgroundRoute = true;
                PlannerSettings.WorldButtons = false;
                PlannerSettings.Read(Path.Combine(folder, "missing.cfg"));
                Assert.IsTrue(PlannerSettings.BackgroundRoute);
                Assert.IsFalse(PlannerSettings.WorldButtons);

                string damaged = Path.Combine(folder, "damaged.cfg");
                File.WriteAllText(damaged, "backgroundRoute = maybe\nworldButtons = 7\n{{{");
                PlannerSettings.Read(damaged);
                Assert.IsTrue(PlannerSettings.BackgroundRoute);
                Assert.IsFalse(PlannerSettings.WorldButtons);
            }
            finally
            {
                PlannerSettings.BackgroundRoute = background;
                PlannerSettings.WorldButtons = buttons;
                Directory.Delete(folder, true);
            }
        }
    }
}
