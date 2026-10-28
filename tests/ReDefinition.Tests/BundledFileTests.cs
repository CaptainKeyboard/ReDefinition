using System.IO;
using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Settings;

namespace ReDefinition.Tests
{
    // bundled.cfg (BundledFile), written and read with KSP's ConfigNode in a
    // folder of the test's own.
    [TestClass]
    public class BundledFileTests
    {
        // The file the test writes and reads, and the host that takes its warnings.
        private string path;
        private FakeHost host;

        // A bundled.cfg in a fresh folder of its own and a host that collects warnings, for each test.
        [TestInitialize]
        public void Folder()
        {
            path = Path.Combine(Path.GetTempPath(), "ReDefinitionTests", Guid.NewGuid().ToString("N"), "bundled.cfg");
            host = new FakeHost();
        }

        // Deletes the test's folder with the file.
        [TestCleanup]
        public void RemoveFolder()
        {
            string directory = Path.GetDirectoryName(path);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }

        // Writes the text as the file, making its folder.
        private void FileHolds(string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, text);
        }

        // A kept value of a setting that is gone leaves with the step that removes it. The file is kept as it was, under the release
        // it came from, and the player's notice names the change. The file written afterwards lists the step as done.
        [TestMethod]
        public void AKeptValueOfARemovedSettingLeavesWithItsStep()
        {
            string notice = Path.Combine(Path.GetDirectoryName(path), "migration-notice.txt");
            Func<string> before = BundledFile.NoticePath;
            BundledFile.NoticePath = () => notice;
            try
            {
                FileHolds("REDEFINITION_BUNDLED\n{\n\tenabled = True\n\tVALUES\n\t{\n\t\ttufx.old = 1\n\t\ttufx.keep = 2\n\t}\n}\n");
                BundledLedger ledger = new BundledLedger();
                BundledState state = new BundledState();
                var steps = new ReDefinition.Migration.SettingsStep[]
                {
                    new ReDefinition.Migration.RemovedSetting("remove-old", "tufx.old", "TUFX's old switch is gone."),
                };
                BundledFile.Read(path, ledger, state, host, steps);

                Assert.AreEqual(0, host.Warnings.Count, string.Join("\n", host.Warnings));
                Assert.IsFalse(ledger.StoredCopy().Exists(pair => pair.Key == "tufx.old"));
                Assert.AreEqual("2", ledger.StoredCopy().Find(pair => pair.Key == "tufx.keep").Value);
                Assert.IsTrue(File.Exists(path + ".bak-0.1.3"), "the file as it was is kept");
                Assert.AreEqual("remove-old", state.Stamp["migrations"]);
                ReDefinition.Migration.MigrationNotice.Pending pending = ReDefinition.Migration.MigrationNotice.Read(notice);
                Assert.AreEqual("tufx.old", pending.Changes[0].Key);
                BundledFile.Write(path, ledger, state, host);
                BundledLedger again = new BundledLedger();
                BundledState againState = new BundledState();
                BundledFile.Read(path, again, againState, host, steps);
                Assert.AreEqual("remove-old", againState.Stamp["migrations"]);
                Assert.AreEqual(1, ReDefinition.Migration.MigrationNotice.Read(notice).Changes.Count, "the second read adds no change");
            }
            finally
            {
                BundledFile.NoticePath = before;
            }
        }

        // Everything the file keeps -- kept values, the profile, the mods asked, the buttons kept, the backups per
        // save and their restore marks -- reads back as written, without warnings and not dirty.
        [TestMethod]
        public void WhatIsKeptComesBackAsItWasWritten()
        {
            BundledLedger ledger = new BundledLedger();
            BundledState state = new BundledState { ProfileName = "high" };
            state.Asked.Add("scatterer");
            state.ButtonKept.Add("tufx");
            ledger.Keep("waterfall.EnableLights", "False");
            ledger.SetBackup("", "ksp.SYNC_VBL", "1");
            ledger.MarkRestore("ksp.SYNC_VBL", true);
            ledger.SetBackup("career", "tufx.profileFlight", "Default-Flight");

            BundledFile.Write(path, ledger, state, host);
            BundledLedger read = new BundledLedger();
            BundledState readState = new BundledState();
            BundledFile.Read(path, read, readState, host);

            Assert.AreEqual(0, host.Warnings.Count, string.Join("\n", host.Warnings));
            string value;
            Assert.IsTrue(read.TryStored("waterfall.EnableLights", out value));
            Assert.AreEqual("False", value);
            Assert.AreEqual("high", readState.ProfileName);
            CollectionAssert.AreEqual(new[] { "scatterer" }, readState.Asked);
            CollectionAssert.AreEqual(new[] { "tufx" }, readState.ButtonKept,
                                      "a button the player kept stays kept over a restart");
            BundledLedger.Backup backup;
            Assert.IsTrue(read.TryBackup("", "ksp.SYNC_VBL", out backup));
            Assert.AreEqual("1", backup.Value);
            Assert.IsTrue(backup.Restore);
            Assert.IsTrue(read.TryBackup("career", "tufx.profileFlight", out backup));
            Assert.IsFalse(backup.Restore);
            Assert.IsFalse(read.Dirty);
        }

        // With the bundling switched off no value kept for a mod is read back, but the backups are, so the way
        // back stays.
        [TestMethod]
        public void WithTheBundlingOffNoValueOfOursIsReadBack()
        {
            BundledLedger ledger = new BundledLedger();
            BundledState state = new BundledState { Enabled = false };
            ledger.Keep("waterfall.EnableLights", "False");
            ledger.SetBackup("", "ksp.SYNC_VBL", "1");

            BundledFile.Write(path, ledger, state, host);
            BundledLedger read = new BundledLedger();
            BundledState readState = new BundledState();
            BundledFile.Read(path, read, readState, host);

            Assert.IsFalse(readState.Enabled);
            Assert.IsFalse(read.Contains("waterfall.EnableLights"));
            BundledLedger.Backup backup;
            Assert.IsTrue(read.TryBackup("", "ksp.SYNC_VBL", out backup), "the way back is kept whatever the switch says");
        }

        // A file that is no config node is moved aside as .unreadable, and the bundling starts on as by default.
        [TestMethod]
        public void AFileThatCannotBeReadIsSetAside()
        {
            FileHolds("this is no config node at all");
            BundledLedger ledger = new BundledLedger();
            BundledState state = new BundledState();

            BundledFile.Read(path, ledger, state, host);

            Assert.IsFalse(File.Exists(path));
            Assert.IsTrue(File.Exists(path + ".unreadable"));
            Assert.IsTrue(state.Enabled);
        }
    }
}
