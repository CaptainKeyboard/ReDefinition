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
        private string path;
        private FakeHost host;

        [TestInitialize]
        public void Folder()
        {
            path = Path.Combine(Path.GetTempPath(), "ReDefinitionTests", Guid.NewGuid().ToString("N"), "bundled.cfg");
            host = new FakeHost();
        }

        [TestCleanup]
        public void RemoveFolder()
        {
            string directory = Path.GetDirectoryName(path);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }

        private void FileHolds(string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, text);
        }

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
