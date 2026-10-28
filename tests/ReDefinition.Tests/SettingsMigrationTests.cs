using System.Collections.Generic;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Migration;

namespace ReDefinition.Tests
{
    // The migration of a player's settings file (SettingsMigration, the steps, MigrationNotice).
    [TestClass]
    public class SettingsMigrationTests
    {
        private static Dictionary<string, string> File3(params string[] pairs)
        {
            var values = new Dictionary<string, string>();
            for (int i = 0; i + 1 < pairs.Length; i += 2) values[pairs[i]] = pairs[i + 1];
            return values;
        }

        // A converted setting maps each old value, leaves any other as it is, and says what it did.
        [TestMethod]
        public void AConvertedSettingMapsItsOldValuesAndLeavesTheRest()
        {
            var step = new ConvertedSetting("dlss", "dlssModel", new Dictionary<string, string> { { "High", "Default" }, { "Fast", "M" } }, "text");
            foreach (string[] pair in new[] { new[] { "High", "Default" }, new[] { "Fast", "M" }, new[] { "K", "K" } })
            {
                var values = File3("dlssModel", pair[0]);
                var changes = new List<SettingsChange>();
                step.Apply(values, changes);
                Assert.AreEqual(pair[1], values["dlssModel"]);
                Assert.AreEqual(pair[0] == pair[1] ? 0 : 1, changes.Count);
            }
            var none = new List<SettingsChange>();
            step.Apply(File3("other", "x"), none);
            Assert.AreEqual(0, none.Count, "a file without the key is untouched");
        }

        // A new standard goes to a file that still holds the old one, and not to one the player changed.
        [TestMethod]
        public void ANewStandardGoesOnlyToWhoKeptTheOldOne()
        {
            var step = new ChangedDefault("d", "a", "30", "45", "text");
            var kept = File3("a", "30");
            var chosen = File3("a", "50");
            step.Apply(kept, new List<SettingsChange>());
            step.Apply(chosen, new List<SettingsChange>());
            Assert.AreEqual("45", kept["a"]);
            Assert.AreEqual("50", chosen["a"]);
        }

        // A renamed setting moves its value, and does not replace a value the new key has already.
        [TestMethod]
        public void ARenamedSettingMovesItsValue()
        {
            var step = new RenamedSetting("r", "a", "b", "text");
            var moved = File3("a", "1");
            var changes = new List<SettingsChange>();
            step.Apply(moved, changes);
            Assert.IsFalse(moved.ContainsKey("a"));
            Assert.AreEqual("1", moved["b"]);
            Assert.AreEqual(1, changes.Count);
            var both = File3("a", "1", "b", "2");
            step.Apply(both, new List<SettingsChange>());
            Assert.IsFalse(both.ContainsKey("a"));
            Assert.AreEqual("2", both["b"]);
        }

        // A removed setting leaves the file.
        [TestMethod]
        public void ARemovedSettingLeavesTheFile()
        {
            var values = File3("gone", "1", "stays", "2");
            var changes = new List<SettingsChange>();
            new RemovedSetting("x", "gone", "text").Apply(values, changes);
            Assert.IsFalse(values.ContainsKey("gone"));
            Assert.AreEqual("2", values["stays"]);
            Assert.IsNull(changes[0].After);
            Assert.AreEqual("1", changes[0].Before);
        }

        // Each step runs once: the file lists the ids it has been through, and a second run changes nothing.
        [TestMethod]
        public void EachStepRunsOnceAndTheFileSaysSo()
        {
            var steps = new SettingsStep[]
            {
                new RenamedSetting("one", "a", "b", "t1"),
                new RenamedSetting("two", "b", "c", "t2"),
            };
            var values = File3("a", "7");
            var first = SettingsMigration.Run(values, steps, "0.2.0");
            Assert.AreEqual("7", values["c"], "a renamed twice ends under the last name");
            Assert.AreEqual(2, first.Count);
            Assert.AreEqual("0.2.0", values[SettingsMigration.VersionKey]);
            Assert.AreEqual("one,two", values[SettingsMigration.AppliedKey]);
            values.Remove("c");
            values["a"] = "9";
            Assert.AreEqual(0, SettingsMigration.Run(values, steps, "0.2.0").Count, "the steps are done");
            Assert.AreEqual("9", values["a"]);
        }

        // A fresh file takes the steps as done: they are for files from before.
        [TestMethod]
        public void AFreshFileHasNoStepToRun()
        {
            var steps = new SettingsStep[] { new RemovedSetting("x", "a", "t") };
            var values = new Dictionary<string, string>();
            SettingsMigration.Fresh(values, steps, "0.2.0");
            values["a"] = "1";
            Assert.AreEqual(0, SettingsMigration.Run(values, steps, "0.2.0").Count);
            Assert.AreEqual("1", values["a"]);
        }

        // A file without a version comes from the first release; releases are compared as numbers.
        [TestMethod]
        public void AFileWithoutAVersionComesFromTheFirstRelease()
        {
            Assert.AreEqual(SettingsMigration.FirstRelease, SettingsMigration.From(new Dictionary<string, string>()));
            Assert.AreEqual("0.2.0", SettingsMigration.From(File3(SettingsMigration.VersionKey, "0.2.0")));
            Assert.IsTrue(SettingsMigration.Compare("0.10.0", "0.9.0") > 0);
            Assert.IsTrue(SettingsMigration.Compare("0.1.3", "0.2.0") < 0);
            Assert.AreEqual(0, SettingsMigration.Compare("1.0.0", "1.0.0"));
        }

        // The file is kept as it was, under the release it came from, and a newer file is replaced by that backup.
        [TestMethod]
        public void TheFileIsKeptAndPutBackForAnOlderRelease()
        {
            string folder = Path.Combine(Path.GetTempPath(), "redefinition-migration-" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                string path = Path.Combine(folder, "settings.cfg");
                File.WriteAllText(path, "old");
                Assert.IsNull(SettingsMigration.Backup(path, "0.2.0"));
                File.WriteAllText(path, "new");
                Assert.IsFalse(SettingsMigration.RestoreOlder(path, "0.9.0"), "no backup from that release");
                Assert.AreEqual("new", File.ReadAllText(path));
                Assert.IsTrue(SettingsMigration.RestoreOlder(path, "0.2.0"));
                Assert.AreEqual("old", File.ReadAllText(path));
                Assert.AreEqual("new", File.ReadAllText(path + ".bak-newer"));
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }

        // The notice is written, read back with the old values, and takes them back into the file's values.
        [TestMethod]
        public void TheNoticeKeepsTheOldValuesToTakeBack()
        {
            string folder = Path.Combine(Path.GetTempPath(), "redefinition-notice-" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                string path = Path.Combine(folder, "migration-notice.txt");
                var changes = new[]
                {
                    new SettingsChange("dlssModel", "High", "Default", "The model row changed."),
                    new SettingsChange("gone", "5", null, "Removed."),
                    new SettingsChange("fresh", null, "1", "New under a new name."),
                };
                MigrationNotice.Write(path, "0.1.3", changes);
                MigrationNotice.Pending pending = MigrationNotice.Read(path);
                Assert.AreEqual("0.1.3", pending.From);
                Assert.AreEqual(3, pending.Changes.Count);
                Assert.IsNull(pending.Changes[1].After);
                var values = File3("dlssModel", "Default", "fresh", "1");
                MigrationNotice.Restore(pending, values);
                Assert.AreEqual("High", values["dlssModel"]);
                Assert.AreEqual("5", values["gone"]);
                Assert.IsFalse(values.ContainsKey("fresh"));
                MigrationNotice.Clear(path);
                Assert.IsNull(MigrationNotice.Read(path));
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }

        // The step ids of the table never repeat, or a file would skip a step for the id of another.
        [TestMethod]
        public void NoTwoStepsShareAnId()
        {
            var seen = new HashSet<string>();
            foreach (SettingsStep step in Migrations.All)
                Assert.IsTrue(seen.Add(step.Id), "the id " + step.Id + " is used twice");
        }
    }
}
