using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Dashboard;

namespace ReDefinition.Tests
{
    // The dashboard's menu page as the mod gives it: the saves from their folders and summaries, KSP's
    // loading progress, and its keys known in every scene.
    [TestClass]
    public class MenuTelemetryTests
    {
        private const string CareerMeta = "vesselCount = 3\nUT = 17293.15681928899\ngameMode = CAREER\ngameNull = False\nkspVersion = 1.12.5\n"
                                          + "funds = 143625.90404933196\nscience = 8\nreputationPercent = 9\nongoingContracts = 1\n"
                                          + "missionExpansionVersion = \nlastWriteTime = 639262856283463812\n";

        private static string Date(double ut)
        {
            return "UT " + Math.Floor(ut);
        }

        [TestMethod]
        public void ASavesSummaryGivesItsModeDateAndNumbers()
        {
            DateTime saved = new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);
            Dictionary<string, object> entry = MenuTelemetry.Entry("Career", saved, CareerMeta, "1.12.5", Date);

            Assert.AreEqual("Career", entry["name"]);
            Assert.AreEqual(1792843200000L, entry["played"]);
            Assert.AreEqual("CAREER", entry["mode"]);
            Assert.AreEqual("UT 17293", entry["date"]);
            Assert.AreEqual(143626.0, entry["funds"]);
            Assert.AreEqual(8.0, entry["science"]);
            Assert.AreEqual(9.0, entry["reputation"]);
            Assert.AreEqual(1.0, entry["contracts"]);
            Assert.AreEqual(3.0, entry["vessels"]);
            Assert.IsFalse(entry.ContainsKey("version"));

            Assert.AreEqual("1.12.5", MenuTelemetry.Entry("Career", saved, CareerMeta, "1.12.4", Date)["version"]);
        }

        [TestMethod]
        public void ASaveWithoutASummaryHasItsNameAndTimeOnly()
        {
            Dictionary<string, object> entry = MenuTelemetry.Entry("old", DateTime.UtcNow, null, "1.12.5", Date);
            CollectionAssert.AreEquivalent(new[] { "name", "played" }, new List<string>(entry.Keys));
        }

        [TestMethod]
        public void TheSavesAreTheFoldersWithAPersistentSaveLastPlayedFirst()
        {
            string root = Path.Combine(Path.GetTempPath(), "ReDefinitionSaves" + Guid.NewGuid().ToString("N"));
            try
            {
                Folder(root, "Career", new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc), CareerMeta);
                Folder(root, "Newest", new DateTime(2026, 10, 23, 0, 0, 0, DateTimeKind.Utc), null);
                Folder(root, "Oldest", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), CareerMeta);
                // KSP's own folders of scenarios and training hold no persistent.sfs.
                Directory.CreateDirectory(Path.Combine(root, "training"));

                MenuTelemetry menu = new MenuTelemetry();
                List<Dictionary<string, object>> saves = menu.Read(root, "1.12.5", Date);
                CollectionAssert.AreEqual(new[] { "Newest", "Career", "Oldest" }, saves.ConvertAll(s => (string)s["name"]));
                Assert.AreEqual("CAREER", saves[1]["mode"]);

                // A summary written anew is read anew; one unchanged is the same entry.
                Dictionary<string, object> before = saves[1];
                Assert.AreSame(before, menu.Read(root, "1.12.5", Date)[1]);
                string meta = Path.Combine(root, "Career", "persistent.loadmeta");
                File.WriteAllText(meta, CareerMeta.Replace("funds = 143625.90404933196", "funds = 5"));
                File.SetLastWriteTimeUtc(meta, new DateTime(2026, 10, 21, 0, 0, 0, DateTimeKind.Utc));
                Assert.AreEqual(5.0, menu.Read(root, "1.12.5", Date)[1]["funds"]);

                Assert.AreEqual(0, menu.Read(Path.Combine(root, "missing"), "1.12.5", Date).Count);
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        private static void Folder(string root, string name, DateTime saved, string meta)
        {
            string folder = Path.Combine(root, name);
            Directory.CreateDirectory(folder);
            string sfs = Path.Combine(folder, "persistent.sfs");
            File.WriteAllText(sfs, "GAME\n{\n}\n");
            File.SetLastWriteTimeUtc(sfs, saved);
            if (meta != null) File.WriteAllText(Path.Combine(folder, "persistent.loadmeta"), meta);
        }

        [TestMethod]
        public void LoadingCountsTheLoadersByTheirWeightAsKspsBarDoes()
        {
            float[] weights = { 1f, 3f, 4f };
            Assert.AreEqual(0.0, MenuTelemetry.LoadFraction(weights, new[] { false, false, false }, 0f), 1e-9);
            Assert.AreEqual(0.25, MenuTelemetry.LoadFraction(weights, new[] { true, false, false }, 1f / 3f), 1e-6);
            // Only the first loader not done counts its share.
            Assert.AreEqual(0.5, MenuTelemetry.LoadFraction(weights, new[] { true, true, false }, 0f), 1e-9);
            Assert.AreEqual(1.0, MenuTelemetry.LoadFraction(weights, new[] { true, true, true }, 0.5f), 1e-9);
            Assert.AreEqual(0.0, MenuTelemetry.LoadFraction(new float[0], new bool[0], 0f), 1e-9);
        }

        // A save is loaded through the main menu's end of its Load dialog.
        [TestMethod]
        public void KspsMainMenuHasTheEndOfItsLoadDialog()
        {
            Assert.IsNotNull(MenuTelemetry.LoadDialogFinished);
        }

        [TestMethod]
        public void TheMenuKeysAndCommandsAreKnownInEveryScene()
        {
            Telemetry telemetry = new Telemetry();
            foreach (string key in new[] { "g.scene", "g.versions", "g.loading", "g.saves", "g.load[Career]", "g.quit" })
                Assert.IsTrue(telemetry.IsKnown(key), key);
        }

        [TestMethod]
        public void JsonWritesTheSavesAsObjects()
        {
            var save = new Dictionary<string, object> { { "name", "Career" }, { "played", 5L }, { "funds", 1.5 } };
            Assert.AreEqual("[{\"name\":\"Career\",\"played\":5,\"funds\":1.5}]", Json.Value(new List<Dictionary<string, object>> { save }));
        }
    }
}
