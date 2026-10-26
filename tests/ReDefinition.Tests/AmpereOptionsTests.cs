using System.IO;
using System.IO.Compression;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Bridges;
using ReDefinition.Ui;
using ReDefinition.Vendor;

namespace ReDefinition.Tests
{
    // The choices of dlssg_for_sm86's dlssg_sm86.ini as the settings window reads and writes
    // them (AmpereOptions): a file laid out like the package's, with its comments.
    [TestClass]
    public class AmpereOptionsTests
    {
        // The test's folder and the ini in it.
        private string folder;
        private string ini;

        // dlssg_sm86.ini as the package ships it, with comments and a value written in one.
        private const string Package =
            "; DLSSG SM86 - frame generation.\r\n"
            + "[General]\r\n"
            + "Enabled=1\r\n"
            + "\r\n"
            + "[FrameGeneration]\r\n"
            + "; Frame-generation multiplier ceiling: 3 = up to 4X.\r\n"
            + "MaxGeneratedFrames=3\r\n"
            + "\r\n"
            + "[DLSSG]\r\n"
            + "; Preset = Preset=C is mentioned here and is no setting.\r\n"
            + "Optimized=2\r\n"
            + "Preset=Auto\r\n"
            + "\r\n"
            + "[Logging]\r\n"
            + "Level=1\r\n"
            + "Directory=dlssg_sm86\\logs\r\n"
            + "\r\n"
            + "[SuperResolution]\r\n"
            + "Mode=Off\r\n";

        // A fresh folder with the package's ini in it, for each test.
        [TestInitialize]
        public void MakeFolder()
        {
            folder = Path.Combine(Path.GetTempPath(), "ReDefinitionAmpereOptionsTests-" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            ini = Path.Combine(folder, "dlssg_sm86.ini");
            File.WriteAllText(ini, Package, new UTF8Encoding(false));
        }

        // Deletes the test's folder and everything in it.
        [TestCleanup]
        public void RemoveFolder()
        {
            Directory.Delete(folder, true);
        }

        // A value is read from its own section, the comments' look-alikes not counted.
        [TestMethod]
        public void AValueIsReadFromItsSection()
        {
            Assert.AreEqual("Auto", AmpereOptions.Read(ini, AmpereOptions.Preset));
            Assert.AreEqual("3", AmpereOptions.Read(ini, AmpereOptions.MaxGeneratedFrames));
            Assert.AreEqual("Off", AmpereOptions.Read(ini, AmpereOptions.SuperResolution));
        }

        // A key the file does not have reads as its default; a missing file reads as nothing.
        [TestMethod]
        public void AKeyTheFileLacksReadsAsItsDefault()
        {
            Assert.AreEqual("0", AmpereOptions.Read(ini, AmpereOptions.FrameTiming));
            Assert.AreEqual(0, AmpereOptions.IndexOf(AmpereOptions.FrameTiming, AmpereOptions.Read(ini, AmpereOptions.FrameTiming)));
            Assert.IsNull(AmpereOptions.Read(Path.Combine(folder, "none.ini"), AmpereOptions.Preset));
        }

        // A value is replaced where it stands, every other byte of the file kept.
        [TestMethod]
        public void AValueIsReplacedWhereItStandsAndTheRestOfTheFileIsKept()
        {
            Assert.IsTrue(AmpereOptions.Write(ini, AmpereOptions.Preset, "C"));

            Assert.AreEqual("C", AmpereOptions.Read(ini, AmpereOptions.Preset));
            string text = File.ReadAllText(ini);
            Assert.AreEqual(Package.Replace("Preset=Auto", "Preset=C"), text);
        }

        // A key the file does not have is added at the end of its section.
        [TestMethod]
        public void AKeyIsAddedAtTheEndOfItsSection()
        {
            Assert.IsTrue(AmpereOptions.Write(ini, AmpereOptions.FrameTiming, "1"));

            Assert.AreEqual("1", AmpereOptions.Read(ini, AmpereOptions.FrameTiming));
            Assert.AreEqual(Package.Replace("Directory=dlssg_sm86\\logs\r\n", "Directory=dlssg_sm86\\logs\r\nFrameTiming=1\r\n"),
                File.ReadAllText(ini));
        }

        // A section the file does not have is added at its end with the key.
        [TestMethod]
        public void ASectionTheFileLacksIsAddedAtTheEnd()
        {
            File.WriteAllText(ini, "[General]\nEnabled=1", new UTF8Encoding(false));

            Assert.IsTrue(AmpereOptions.Write(ini, AmpereOptions.Preset, "B"));

            Assert.AreEqual("[General]\nEnabled=1\n\n[DLSSG]\nPreset=B\n", File.ReadAllText(ini).Replace("\r\n", "\n"));
            Assert.AreEqual("B", AmpereOptions.Read(ini, AmpereOptions.Preset));
        }

        // A write keeps the file's UTF-8 with its byte order mark, written once, and the text beside the value.
        [TestMethod]
        public void TheEncodingAndTheBomAreKept()
        {
            string text = "﻿[DLSSG]\r\n; 插帧\r\nPreset=Auto\r\n";
            File.WriteAllText(ini, text, new UTF8Encoding(false));
            AmpereOptions.Write(ini, AmpereOptions.Preset, "C");
            byte[] bytes = File.ReadAllBytes(ini);
            Assert.AreEqual(0xEF, bytes[0]);
            Assert.AreEqual(0xBB, bytes[1]);
            Assert.AreEqual(0xBF, bytes[2]);
            Assert.AreNotEqual(0xEF, bytes[3]);
            Assert.AreEqual("[DLSSG]\r\n; 插帧\r\nPreset=C\r\n", File.ReadAllText(ini, Encoding.UTF8));
        }

        // A write of the value already there leaves the file as it is, its time included.
        [TestMethod]
        public void AWriteThatChangesNothingLeavesTheFileAlone()
        {
            File.SetLastWriteTimeUtc(ini, new System.DateTime(2020, 1, 1, 0, 0, 0, System.DateTimeKind.Utc));
            Assert.IsTrue(AmpereOptions.Write(ini, AmpereOptions.Preset, "Auto"));
            Assert.AreEqual(new System.DateTime(2020, 1, 1, 0, 0, 0, System.DateTimeKind.Utc), File.GetLastWriteTimeUtc(ini));
        }

        // A file that does not exist is not written, and is not made.
        [TestMethod]
        public void AFileThatCannotBeReadIsNotWritten()
        {
            Assert.IsFalse(AmpereOptions.Write(Path.Combine(folder, "none.ini"), AmpereOptions.Preset, "C"));
            Assert.IsFalse(File.Exists(Path.Combine(folder, "none.ini")));
        }

        // A game folder with the release zip's files as the package ships them (Package as its
        // ini), installed over whatever the folder holds.
        private string Reinstall(string game, out System.Collections.Generic.Dictionary<AmpereOptions.Option, string> before)
        {
            string zip = Path.Combine(folder, "dlssg-release-0.4.0-x64.zip");
            if (!File.Exists(zip))
            {
                using (ZipArchive archive = ZipFile.Open(zip, ZipArchiveMode.Create))
                {
                    foreach (string entry in new[] { "version.dll", "dlssg_sm86/backend/b.dll", "dlssg_sm86/kernels/k.dll",
                                                     "dlssg_sm86/runtime/nvngx_dlssg.dll" })
                    {
                        using (StreamWriter writer = new StreamWriter(archive.CreateEntry(entry).Open()))
                            writer.Write(entry);
                    }
                    using (StreamWriter writer = new StreamWriter(archive.CreateEntry("dlssg_sm86.ini").Open(), new UTF8Encoding(false)))
                        writer.Write(Package);
                }
            }
            string path = Path.Combine(game, AmperePackage.Ini);
            before = AmpereOptions.Snapshot(path);
            AmperePackage.Install(zip, game);
            AmpereOptions.AfterInstall(path, before);
            return path;
        }

        // A first install, with no ini before, raises the factory's multiplier ceiling and preset to ReDefinition's.
        [TestMethod]
        public void AFirstInstallRaisesTheFactoryCeilingAndPreset()
        {
            string game = Path.Combine(folder, "KSP");
            Directory.CreateDirectory(game);
            System.Collections.Generic.Dictionary<AmpereOptions.Option, string> before;
            string path = Reinstall(game, out before);

            Assert.IsNull(before);
            Assert.AreEqual(AmpereOptions.InstalledCeiling, AmpereOptions.Read(path, AmpereOptions.MaxGeneratedFrames));
            Assert.AreEqual(AmpereOptions.InstalledPreset, AmpereOptions.Read(path, AmpereOptions.Preset));
        }

        // A reinstall keeps every choice the player made, and the old ini is kept next to it.
        [TestMethod]
        public void AReinstallKeepsThePlayersChoices()
        {
            string game = Path.Combine(folder, "KSP");
            Directory.CreateDirectory(game);
            string path = Reinstall(game, out _);
            Assert.IsTrue(AmpereOptions.Write(path, AmpereOptions.Preset, "A"));
            Assert.IsTrue(AmpereOptions.Write(path, AmpereOptions.MaxGeneratedFrames, "2"));
            Assert.IsTrue(AmpereOptions.Write(path, AmpereOptions.SuperResolution, "On"));
            Assert.IsTrue(AmpereOptions.Write(path, AmpereOptions.FrameTiming, "1"));

            System.Collections.Generic.Dictionary<AmpereOptions.Option, string> before;
            Reinstall(game, out before);

            Assert.IsNotNull(before);
            Assert.AreEqual("A", AmpereOptions.Read(path, AmpereOptions.Preset));
            Assert.AreEqual("2", AmpereOptions.Read(path, AmpereOptions.MaxGeneratedFrames));
            Assert.AreEqual("On", AmpereOptions.Read(path, AmpereOptions.SuperResolution));
            Assert.AreEqual("1", AmpereOptions.Read(path, AmpereOptions.FrameTiming));
            StringAssert.Contains(File.ReadAllText(path + ".old"), "Preset=A");
        }

        // A choice the player made that equals the factory value is kept too, not raised over.
        [TestMethod]
        public void AReinstallKeepsAChoiceThatEqualsTheFactoryValue()
        {
            // Auto and 4X (the factory's) chosen on purpose: a reinstall neither raises nor
            // sets C over them.
            string game = Path.Combine(folder, "KSP");
            Directory.CreateDirectory(game);
            string path = Reinstall(game, out _);
            Assert.IsTrue(AmpereOptions.Write(path, AmpereOptions.Preset, "Auto"));
            Assert.IsTrue(AmpereOptions.Write(path, AmpereOptions.MaxGeneratedFrames, "3"));

            Reinstall(game, out _);

            Assert.AreEqual("Auto", AmpereOptions.Read(path, AmpereOptions.Preset));
            Assert.AreEqual("3", AmpereOptions.Read(path, AmpereOptions.MaxGeneratedFrames));
        }

        // A key the old ini did not have takes the factory value of the new one, not the raised one.
        [TestMethod]
        public void AKeyTheOldIniLackedStaysTheFactorys()
        {
            string game = Path.Combine(folder, "KSP");
            Directory.CreateDirectory(game);
            File.WriteAllText(Path.Combine(game, AmperePackage.Ini), "[DLSSG]\r\nPreset=B\r\n");
            System.Collections.Generic.Dictionary<AmpereOptions.Option, string> before;
            string path = Reinstall(game, out before);

            Assert.AreEqual(1, before.Count);
            Assert.AreEqual("B", AmpereOptions.Read(path, AmpereOptions.Preset));
            Assert.AreEqual("3", AmpereOptions.Read(path, AmpereOptions.MaxGeneratedFrames));
        }

        // Each option's labels and values pair up, its default is one of its values, and its tooltip's lines
        // fit KSP's tooltip once wrapped.
        [TestMethod]
        public void TheLabelsAndValuesPairUpAndTheDefaultIsAmongThem()
        {
            foreach (AmpereOptions.Option option in AmpereOptions.All)
            {
                Assert.AreEqual(option.Values.Length, option.Labels.Length, option.Key);
                CollectionAssert.Contains(option.Values, option.Default, option.Key);
                // The window breaks the lines (TooltipText.Wrap): none is longer than KSP's tooltip takes.
                foreach (string line in TooltipText.Wrap(option.Tooltip).Split('\n'))
                    Assert.IsTrue(line.Length <= TooltipText.MaxLine, option.Key + ": " + line);
            }
        }
    }
}
