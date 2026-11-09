using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Core;
using ReDefinition.Dashboard;

namespace ReDefinition.Tests
{
    // The dashboard on its own: where it keeps its files, what it does with a damaged one, how it makes its key, and how it takes the
    // host's settings where ReDefinition is present.
    [TestClass]
    public class DashboardStandaloneTests
    {
        [TestMethod]
        public void TheDashboardKeepsItsFileInItsOwnPluginData()
        {
            string path = ModFolder.PluginFile("root", "ReDefinition/Dashboard", "settings.cfg");
            Assert.AreEqual(Path.Combine("root", "GameData", "ReDefinition/Dashboard", "PluginData", "settings.cfg"), path);
            Assert.AreNotEqual(path, ModFolder.PluginFile("root", "ReDefinition/Planner", "settings.cfg"));
        }

        [TestMethod]
        public void TheSettingsComeBackAsTheyWereSaved()
        {
            string file = Path.Combine(Path.GetTempPath(), "dashboard-settings-" + Guid.NewGuid().ToString("N"), "settings.cfg");
            bool network = DashboardSettings.Network;
            string key = DashboardSettings.Key;
            try
            {
                DashboardSettings.Network = false;
                DashboardSettings.Key = "abcdefgh";
                DashboardSettings.Write(file);
                DashboardSettings.Network = true;
                DashboardSettings.Key = "";
                DashboardSettings.Read(file);
                Assert.IsFalse(DashboardSettings.Network);
                Assert.AreEqual("abcdefgh", DashboardSettings.Key);
            }
            finally
            {
                DashboardSettings.Network = network;
                DashboardSettings.Key = key;
                if (File.Exists(file)) Directory.Delete(Path.GetDirectoryName(file), true);
            }
        }

        [TestMethod]
        public void AMissingOrDamagedSettingsFileLeavesTheSettingsAlone()
        {
            string folder = Path.Combine(Path.GetTempPath(), "dashboard-settings-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            bool network = DashboardSettings.Network;
            string key = DashboardSettings.Key;
            try
            {
                DashboardSettings.Network = false;
                DashboardSettings.Key = "kept";
                DashboardSettings.Read(Path.Combine(folder, "missing.cfg"));
                Assert.IsFalse(DashboardSettings.Network);
                Assert.AreEqual("kept", DashboardSettings.Key);

                string damaged = Path.Combine(folder, "damaged.cfg");
                File.WriteAllText(damaged, "network = maybe\n{{{");
                DashboardSettings.Read(damaged);
                Assert.IsFalse(DashboardSettings.Network);
                Assert.AreEqual("kept", DashboardSettings.Key);
            }
            finally
            {
                DashboardSettings.Network = network;
                DashboardSettings.Key = key;
                Directory.Delete(folder, true);
            }
        }

        [TestMethod]
        public void ANewKeyHasEightLettersAndDigitsThatDoNotReadAlike()
        {
            for (int i = 0; i < 20; i++)
            {
                string key = DashboardSettings.NewKey();
                Assert.AreEqual(8, key.Length);
                Assert.IsTrue(key.All(c => "abcdefghijkmnpqrstuvwxyz23456789".IndexOf(c) >= 0), key);
            }
        }

        [TestMethod]
        public void WithTheHostTheKeyAndTheSwitchAreTheHostsAndAMissingKeyIsMadeThroughIt()
        {
            Func<bool> alone = HostSeams.StandsAlone, network = HostSeams.DashboardNetwork;
            Func<string> key = HostSeams.DashboardKey;
            Action<string> set = HostSeams.SetDashboardKey;
            try
            {
                string held = null;
                HostSeams.StandsAlone = () => false;
                HostSeams.DashboardNetwork = () => false;
                HostSeams.DashboardKey = () => held;
                HostSeams.SetDashboardKey = value => held = value;

                // The host's settings are not loaded yet.
                Assert.IsFalse(DashboardSettings.Ready);

                held = "";
                Assert.IsTrue(DashboardSettings.Ready);
                Assert.IsFalse(DashboardSettings.NetworkOn);
                string made = DashboardSettings.EnsureKey();
                Assert.AreEqual(8, made.Length);
                Assert.AreEqual(made, held);
                Assert.AreEqual(made, DashboardSettings.EnsureKey());
            }
            finally
            {
                HostSeams.StandsAlone = alone;
                HostSeams.DashboardNetwork = network;
                HostSeams.DashboardKey = key;
                HostSeams.SetDashboardKey = set;
            }
        }

        [TestMethod]
        public void AnOlderCommonThanTheOneTheDashboardWasBuiltAgainstIsNamed()
        {
            Assert.IsNull(CommonVersionCheck.Problem("ReDefinition Dashboard", new Version(0, 1, 14, 0), new Version(0, 1, 14, 0)));
            Assert.IsNull(CommonVersionCheck.Problem("ReDefinition Dashboard", new Version(0, 1, 14, 0), new Version(0, 1, 15, 0)));
            StringAssert.Contains(CommonVersionCheck.Problem("ReDefinition Dashboard", new Version(0, 1, 15, 0), new Version(0, 1, 14, 0)),
                "ReDefinition Dashboard was made against 0.1.15");
        }
    }
}
