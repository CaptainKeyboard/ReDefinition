using System.IO;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Bridges;

namespace ReDefinition.Tests
{
    // The choices of dlssg_for_sm86's dlssg_sm86.ini as the settings window reads and writes
    // them (AmpereOptions): a file laid out like the package's, with its comments.
    [TestClass]
    public class AmpereOptionsTests
    {
        private string folder;
        private string ini;

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

        [TestInitialize]
        public void MakeFolder()
        {
            folder = Path.Combine(Path.GetTempPath(), "ReDefinitionAmpereOptionsTests-" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            ini = Path.Combine(folder, "dlssg_sm86.ini");
            File.WriteAllText(ini, Package, new UTF8Encoding(false));
        }

        [TestCleanup]
        public void RemoveFolder()
        {
            Directory.Delete(folder, true);
        }

        [TestMethod]
        public void AValueIsReadFromItsSection()
        {
            Assert.AreEqual("Auto", AmpereOptions.Read(ini, AmpereOptions.Preset));
            Assert.AreEqual("3", AmpereOptions.Read(ini, AmpereOptions.MaxGeneratedFrames));
            Assert.AreEqual("Off", AmpereOptions.Read(ini, AmpereOptions.SuperResolution));
        }

        [TestMethod]
        public void AKeyTheFileLacksReadsAsItsDefault()
        {
            Assert.AreEqual("0", AmpereOptions.Read(ini, AmpereOptions.FrameTiming));
            Assert.AreEqual(0, AmpereOptions.IndexOf(AmpereOptions.FrameTiming, AmpereOptions.Read(ini, AmpereOptions.FrameTiming)));
            Assert.IsNull(AmpereOptions.Read(Path.Combine(folder, "none.ini"), AmpereOptions.Preset));
        }

        [TestMethod]
        public void AValueIsReplacedWhereItStandsAndTheRestOfTheFileIsKept()
        {
            Assert.IsTrue(AmpereOptions.Write(ini, AmpereOptions.Preset, "C"));

            Assert.AreEqual("C", AmpereOptions.Read(ini, AmpereOptions.Preset));
            string text = File.ReadAllText(ini);
            Assert.AreEqual(Package.Replace("Preset=Auto", "Preset=C"), text);
        }

        [TestMethod]
        public void AKeyIsAddedAtTheEndOfItsSection()
        {
            Assert.IsTrue(AmpereOptions.Write(ini, AmpereOptions.FrameTiming, "1"));

            Assert.AreEqual("1", AmpereOptions.Read(ini, AmpereOptions.FrameTiming));
            Assert.AreEqual(Package.Replace("Directory=dlssg_sm86\\logs\r\n", "Directory=dlssg_sm86\\logs\r\nFrameTiming=1\r\n"),
                File.ReadAllText(ini));
        }

        [TestMethod]
        public void ASectionTheFileLacksIsAddedAtTheEnd()
        {
            File.WriteAllText(ini, "[General]\nEnabled=1", new UTF8Encoding(false));

            Assert.IsTrue(AmpereOptions.Write(ini, AmpereOptions.Preset, "B"));

            Assert.AreEqual("[General]\nEnabled=1\n\n[DLSSG]\nPreset=B\n", File.ReadAllText(ini).Replace("\r\n", "\n"));
            Assert.AreEqual("B", AmpereOptions.Read(ini, AmpereOptions.Preset));
        }

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

        [TestMethod]
        public void AWriteThatChangesNothingLeavesTheFileAlone()
        {
            File.SetLastWriteTimeUtc(ini, new System.DateTime(2020, 1, 1, 0, 0, 0, System.DateTimeKind.Utc));
            Assert.IsTrue(AmpereOptions.Write(ini, AmpereOptions.Preset, "Auto"));
            Assert.AreEqual(new System.DateTime(2020, 1, 1, 0, 0, 0, System.DateTimeKind.Utc), File.GetLastWriteTimeUtc(ini));
        }

        [TestMethod]
        public void AFileThatCannotBeReadIsNotWritten()
        {
            Assert.IsFalse(AmpereOptions.Write(Path.Combine(folder, "none.ini"), AmpereOptions.Preset, "C"));
            Assert.IsFalse(File.Exists(Path.Combine(folder, "none.ini")));
        }

        [TestMethod]
        public void TheLabelsAndValuesPairUpAndTheDefaultIsAmongThem()
        {
            foreach (AmpereOptions.Option option in AmpereOptions.All)
            {
                Assert.AreEqual(option.Values.Length, option.Labels.Length, option.Key);
                CollectionAssert.Contains(option.Values, option.Default, option.Key);
                foreach (string line in option.Tooltip.Split('\n'))
                    Assert.IsTrue(line.Length <= 90, option.Key + ": " + line);
            }
        }
    }
}
