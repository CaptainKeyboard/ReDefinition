using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Bridges;

namespace ReDefinition.Tests
{
    // Intel's DLLs as the download finds, checks and places them (IntelFiles),
    // without the network: the table's figures, what counts as missing, the ini's
    // folders, and an archive entry laid out as Intel's release has it (streamed:
    // a data descriptor, no sizes in the local header).
    [TestClass]
    public class IntelFilesTests
    {
        // The test's folder, and IntelFiles' version reader as it was before a test replaced it.
        private string folder;
        private Func<string, Version> readVersion;

        // A fresh folder of the test's own, and the version reader kept for the clean-up.
        [TestInitialize]
        public void MakeFolder()
        {
            folder = Path.Combine(Path.GetTempPath(), "ReDefinitionIntelFilesTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            readVersion = IntelFiles.ReadVersion;
        }

        // Puts the version reader back and deletes the test's folder.
        [TestCleanup]
        public void RemoveFolder()
        {
            IntelFiles.ReadVersion = readVersion;
            Directory.Delete(folder, true);
        }

        // Folders for both uses pointing at the test's folder.
        private IntelFiles.Folders Here()
        {
            return new IntelFiles.Folders { SuperResolution = folder, FrameGeneration = folder };
        }

        // The file names of the items.
        private static string[] Names(IEnumerable<IntelFiles.Item> items)
        {
            return items.Select(i => i.Entry.FileName).ToArray();
        }

        // Puts a ten-byte stand-in of the file into the test's folder.
        private void Put(string name)
        {
            File.WriteAllBytes(Path.Combine(folder, name), new byte[10]);
        }

        // The table holds the XeSS SDK release's four files at their places in its archive, each with a SHA-256
        // in upper case and its use.
        [TestMethod]
        public void TheTableHoldsTheFilesOfTheXessSdkRelease()
        {
            CollectionAssert.AreEquivalent(
                new[] { "libxess.dll", "xess.license.txt", "libxess_fg.dll", "libxell.dll" }, Names(IntelFiles.All));
            Assert.AreEqual("https://github.com/intel/xess/releases/download/v3.0.2/XeSS_SDK_3.0.2.zip", IntelFiles.ArchiveUrl);

            // Read from the release: libxess.dll 2.0.2.68, libxess_fg.dll 1.3.1.78, libxell.dll 1.3.2.10.
            IntelFiles.Item xess = IntelFiles.All.First(i => i.Entry.FileName == "libxess.dll");
            Assert.AreEqual("bin/libxess.dll", xess.Entry.Name);
            Assert.AreEqual(405372, xess.Entry.Offset);
            Assert.AreEqual(59737353, xess.Entry.CompressedSize);
            Assert.AreEqual(77795704, xess.Entry.Size);
            // Local header (30), the name (15), the data.
            Assert.AreEqual(405372 + 30 + 15 + 59737353 - 1, xess.Entry.RangeEnd);
            Assert.AreEqual(59737398, xess.Entry.RangeLength);

            foreach (IntelFiles.Item item in IntelFiles.All)
            {
                Assert.AreEqual(64, item.Entry.Sha256.Length, item.Entry.FileName);
                Assert.AreEqual(item.Entry.Sha256.ToUpperInvariant(), item.Entry.Sha256, item.Entry.FileName);
                Assert.AreEqual("Intel's release", item.Entry.Release);
                Assert.AreEqual(item.Use == IntelFiles.Use.Notice, item.Entry.Notice);
                Assert.IsTrue(item.Entry.Size > 0 && item.Entry.CompressedSize > 0);
            }
        }

        // The whole download is about 73 MB, super resolution about 60 and frame generation about 13, the licence
        // text counted with both.
        [TestMethod]
        public void TheReleaseIsAboutSeventyThreeMegabytes()
        {
            long bytes = IntelFiles.DownloadSize(IntelFiles.All);
            Assert.IsTrue(bytes > 72500000 && bytes < 73500000, bytes.ToString());
            long superResolution = IntelFiles.DownloadSize(IntelFiles.Only(IntelFiles.All, IntelFiles.Use.SuperResolution));
            long frameGeneration = IntelFiles.DownloadSize(IntelFiles.Only(IntelFiles.All, IntelFiles.Use.FrameGeneration));
            Assert.IsTrue(superResolution > 59700000 && superResolution < 59800000, superResolution.ToString());
            Assert.IsTrue(frameGeneration > 13200000 && frameGeneration < 13400000, frameGeneration.ToString());
            long notice = IntelFiles.All.First(i => i.Use == IntelFiles.Use.Notice).Entry.RangeLength;
            Assert.AreEqual(bytes + notice, superResolution + frameGeneration);
        }

        // An empty folder lacks every file; with all DLLs in place nothing is missing, the licence text alone
        // being no reason to download.
        [TestMethod]
        public void OnlyWhatIsNeededCountsAsMissing()
        {
            Assert.AreEqual(IntelFiles.All.Length, IntelFiles.Missing(Here(), IntelFiles.All).Count,
                            "an empty folder lacks everything");

            IntelFiles.ReadVersion = path => null;
            Put("libxess.dll");
            List<IntelFiles.Item> missing = IntelFiles.Missing(Here(), IntelFiles.All);
            CollectionAssert.AreEquivalent(new[] { "libxess_fg.dll", "libxell.dll", "xess.license.txt" }, Names(missing));

            Put("libxess_fg.dll");
            Put("libxell.dll");
            Assert.AreEqual(0, IntelFiles.Missing(Here(), IntelFiles.All).Count,
                            "every DLL in place, and no download for the licence text alone");
        }

        // A libxess_fg.dll older than 1.2.2, the oldest the proxy takes, counts as missing, with the licence
        // text; a version that cannot be read is let through.
        [TestMethod]
        public void AFrameGenerationDllOlderThanTheProxyRunsCountsAsMissing()
        {
            Put("libxess.dll");
            Put("libxess_fg.dll");
            Put("libxell.dll");
            string fg = Path.Combine(folder, "libxess_fg.dll");

            // 1.2.2 is the oldest the proxy takes (XeSS SDK 2.1.1).
            foreach (KeyValuePair<Version, bool> known in new Dictionary<Version, bool>
                     {
                         { new Version(1, 1, 0, 100), true },
                         { new Version(1, 2, 1, 9), true },
                         { new Version(1, 2, 2, 0), false },
                         { new Version(1, 2, 2), false },
                         { new Version(1, 3, 1, 78), false },
                         { new Version(2, 0, 0, 0), false },
                     })
            {
                Version version = known.Key;
                IntelFiles.ReadVersion = path => Path.GetFileName(path) == "libxess_fg.dll" ? version : null;
                List<IntelFiles.Item> missing = IntelFiles.Missing(Here(), IntelFiles.All);
                Assert.AreEqual(known.Value, missing.Any(i => i.Entry.FileName == "libxess_fg.dll"), version.ToString());
                // The old one comes with the licence text, and nothing else.
                Assert.AreEqual(known.Value ? 2 : 0, missing.Count, version.ToString());
            }

            IntelFiles.ReadVersion = path => null;
            Assert.AreEqual(0, IntelFiles.Missing(Here(), IntelFiles.All).Count, "a version that cannot be read is let through");
            Assert.IsTrue(File.Exists(fg));
        }

        // Each use's files are looked for in that use's folder, and the licence text goes with super resolution
        // where that is downloaded too, else with frame generation.
        [TestMethod]
        public void EachUseIsLookedForInItsOwnFolder()
        {
            string frameGeneration = Path.Combine(folder, "fg");
            Directory.CreateDirectory(frameGeneration);
            IntelFiles.Folders folders = new IntelFiles.Folders { SuperResolution = folder, FrameGeneration = frameGeneration };
            IntelFiles.ReadVersion = path => null;

            File.WriteAllBytes(Path.Combine(frameGeneration, "libxess.dll"), new byte[10]);
            Assert.IsTrue(Names(IntelFiles.Missing(folders, IntelFiles.All)).Contains("libxess.dll"),
                          "libxess.dll belongs in the other folder");
            File.WriteAllBytes(Path.Combine(folder, "libxell.dll"), new byte[10]);
            Assert.IsTrue(Names(IntelFiles.Missing(folders, IntelFiles.All)).Contains("libxell.dll"));

            IntelFiles.Item notice = IntelFiles.All.First(i => i.Use == IntelFiles.Use.Notice);
            Assert.AreEqual(folder, IntelFiles.FolderOf(notice, folders, true));
            Assert.AreEqual(frameGeneration, IntelFiles.FolderOf(notice, folders, false));
        }

        // The ini's folders are read as the proxy reads them, from [Proxy], the key's case and quotes not counting;
        // an empty folder is next to KSP_x64.exe, a relative one KSP's.
        [TestMethod]
        public void TheInisFoldersCountAsTheProxyReadsThem()
        {
            string[] ini =
            {
                "; Where Intel's libxess.dll lies",
                "[Other]",
                "xessDirectory=wrong",
                "[Proxy]",
                "enabled=1",
                "xessDirectory = D:\\Intel\\XeSS ",
                "; xessFgDirectory=commented",
                "xessFgDirectory=\"fg\"",
                "emptyValue=",
            };
            Assert.AreEqual("D:\\Intel\\XeSS", IntelFiles.IniValue(ini, "xessDirectory"));
            Assert.AreEqual("fg", IntelFiles.IniValue(ini, "XESSFGDIRECTORY"), "the key's case, and the quotes, do not count");
            Assert.AreEqual("", IntelFiles.IniValue(ini, "emptyValue"));
            Assert.AreEqual("", IntelFiles.IniValue(ini, "missing"));
            Assert.AreEqual("", IntelFiles.IniValue(Path.Combine(folder, "none.ini"), "xessDirectory"), "no file");

            // Empty = next to KSP_x64.exe; a relative folder is KSP's.
            IntelFiles.Folders folders = IntelFiles.FoldersOf("C:\\KSP", "", "fg");
            Assert.AreEqual("C:\\KSP", folders.SuperResolution);
            Assert.AreEqual(Path.Combine("C:\\KSP", "fg"), folders.FrameGeneration);
            Assert.AreEqual("D:\\Intel\\XeSS", IntelFiles.FoldersOf("C:\\KSP", "D:\\Intel\\XeSS", null).SuperResolution);
        }

        // The files in place are named in the window with their versions where those can be read; none in place
        // is nothing to name.
        [TestMethod]
        public void TheFilesInPlaceAreNamedWithTheirVersions()
        {
            IntelFiles.ReadVersion = path => Path.GetFileName(path) == "libxell.dll" ? new Version(1, 3, 2, 10) : null;
            Assert.IsNull(IntelFiles.InstalledText(Here()));
            Put("libxess.dll");
            Put("libxell.dll");
            Assert.AreEqual("libxess.dll, libxell.dll 1.3.2.10", IntelFiles.InstalledText(Here()));
        }

        // The uses of the files chosen decide what an acceptance covers: all of them both, the frame generation
        // files alone frame generation alone.
        [TestMethod]
        public void TheUsesDecideWhatAnAcceptanceCovers()
        {
            bool superResolution;
            bool frameGeneration;
            IntelFiles.Uses(IntelFiles.All, out superResolution, out frameGeneration);
            Assert.IsTrue(superResolution && frameGeneration);

            List<IntelFiles.Item> only = IntelFiles.Only(IntelFiles.All, IntelFiles.Use.FrameGeneration);
            CollectionAssert.AreEquivalent(new[] { "libxess_fg.dll", "libxell.dll", "xess.license.txt" }, Names(only));
            IntelFiles.Uses(only, out superResolution, out frameGeneration);
            Assert.IsFalse(superResolution);
            Assert.IsTrue(frameGeneration);
        }

        // The entry extracted from the bytes of its range.
        private static byte[] Extracted(NvidiaFiles.Entry entry, byte[] range)
        {
            using (MemoryStream output = new MemoryStream())
            {
                NvidiaFiles.Extract(entry, new MemoryStream(range), output);
                return output.ToArray();
            }
        }

        // A streamed entry, flagged with a data descriptor and without sizes in its local header, is fetched as
        // its range and extracts to what went in.
        [TestMethod]
        public void AStreamedEntryComesOutAsItWent()
        {
            byte[] content = NvidiaFilesTests.Content("a file Intel would ship");
            byte[] range;
            NvidiaFiles.Entry entry = NvidiaFilesTests.Archived("bin/libxell.dll", content, out range, true);
            entry.Release = "Intel's release";

            Assert.AreEqual(8, range[6] | range[7] << 8, "flags: a data descriptor");
            Assert.AreEqual(0u, BitConverter.ToUInt32(range, 18), "no compressed size in the local header");
            Assert.AreEqual(0u, BitConverter.ToUInt32(range, 22), "no size in the local header");
            Assert.AreEqual(entry.RangeLength, range.Length, "the range fetched is the entry");
            CollectionAssert.AreEqual(content, Extracted(entry, range));
        }

        // A streamed entry is refused where its local header names other sizes than the central directory, where
        // its flags are neither 0 nor 8, or where its data or checksum do not match; the refusal names Intel's release.
        [TestMethod]
        public void AStreamedEntryOfAnotherSizeOrFlagIsRefused()
        {
            byte[] content = NvidiaFilesTests.Content("a file Intel would ship");
            byte[] range;
            NvidiaFiles.Entry entry = NvidiaFilesTests.Archived("bin/libxell.dll", content, out range, true);
            entry.Release = "Intel's release";

            // A local header that names sizes names the central directory's.
            byte[] sized = (byte[])range.Clone();
            BitConverter.GetBytes((uint)entry.CompressedSize + 1).CopyTo(sized, 18);
            InvalidDataException wrongSize = Assert.ThrowsException<InvalidDataException>(() => Extracted(entry, sized));
            StringAssert.Contains(wrongSize.Message, "Intel's release");

            // A flag that is neither 0 nor 8 (here: the encryption bit) is not Intel's.
            byte[] encrypted = (byte[])range.Clone();
            encrypted[6] = 9;
            Assert.ThrowsException<InvalidDataException>(() => Extracted(entry, encrypted));

            // Data that is not what the checksum says.
            byte[] corrupted = (byte[])range.Clone();
            corrupted[corrupted.Length / 2] ^= 0xFF;
            Assert.ThrowsException<InvalidDataException>(() => Extracted(entry, corrupted));

            NvidiaFiles.Entry otherChecksum = NvidiaFilesTests.Archived("bin/libxell.dll", content, out range, true);
            otherChecksum.Release = "Intel's release";
            otherChecksum.Sha256 = NvidiaFiles.Sha256(new byte[] { 1, 2, 3 });
            InvalidDataException checksum = Assert.ThrowsException<InvalidDataException>(() => Extracted(otherChecksum, range));
            StringAssert.Contains(checksum.Message, "Intel's release");
        }

        // An NVIDIA entry's refusal still names NVIDIA's release: the release named is the entry's own.
        [TestMethod]
        public void NvidiasMessagesStillNameNvidia()
        {
            byte[] range;
            NvidiaFiles.Entry entry = NvidiaFilesTests.Archived("bin/x64/sl.common.dll",
                                                                NvidiaFilesTests.Content(), out range);
            entry.Sha256 = NvidiaFiles.Sha256(new byte[] { 1 });
            InvalidDataException e = Assert.ThrowsException<InvalidDataException>(() => Extracted(entry, range));
            StringAssert.Contains(e.Message, "NVIDIA's release");
        }
    }
}
