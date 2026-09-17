using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ReDefinition.Tests
{
    // NVIDIA's DLLs as the download finds, checks and places them (NvidiaFiles),
    // without the network: an archive made here the way NVIDIA's is laid out.
    [TestClass]
    public class NvidiaFilesTests
    {
        private const uint RtxTuring = 0x4;     // TU104
        private const uint GtxTuring = 0x8;     // TU116
        private const uint Ada102 = 0x2;

        private string folder;

        [TestInitialize]
        public void MakeFolder()
        {
            folder = Path.Combine(Path.GetTempPath(), "ReDefinitionNvidiaFilesTests-" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
        }

        [TestCleanup]
        public void RemoveFolder()
        {
            Directory.Delete(folder, true);
        }

        private NvidiaFiles.Folders Here()
        {
            return new NvidiaFiles.Folders { Dlss = folder, FrameGeneration = folder };
        }

        [TestMethod]
        public void TheGpuDecidesWhatIsOffered()
        {
            Assert.AreEqual(0, NvidiaFiles.For(0, 0).Count, "not NVIDIA's, or unknown");
            Assert.AreEqual(0, NvidiaFiles.For(0x150, 0).Count, "before Turing");
            Assert.AreEqual(0, NvidiaFiles.For(0x160, GtxTuring).Count, "GTX 16: Turing without DLSS");
            Assert.AreEqual(0, NvidiaFiles.For(0xE0000040, 0).Count, "Tegra");

            CollectionAssert.AreEquivalent(new[] { "nvngx_dlss.dll", "nvngx_dlss.license.txt" },
                                           NvidiaFiles.For(0x160, RtxTuring).Select(e => e.FileName).ToArray());
            CollectionAssert.AreEquivalent(new[] { "nvngx_dlss.dll", "nvngx_dlss.license.txt" },
                                           NvidiaFiles.For(0x170, 0x4).Select(e => e.FileName).ToArray(), "Ampere");

            Assert.AreEqual(NvidiaFiles.All.Length, NvidiaFiles.For(0x190, Ada102).Count);
            Assert.AreEqual(NvidiaFiles.All.Length, NvidiaFiles.For(0x1B0, Ada102).Count, "Blackwell");
        }

        [TestMethod]
        public void TheReleaseIsAboutThirtySixMegabytesForAnAdaGpu()
        {
            long bytes = NvidiaFiles.DownloadSize(NvidiaFiles.For(0x190, Ada102));
            Assert.IsTrue(bytes > 35000000 && bytes < 37000000, bytes.ToString());
        }

        [TestMethod]
        public void OnlyWhatIsNeededCountsAsMissing()
        {
            List<NvidiaFiles.Entry> all = NvidiaFiles.For(0x190, Ada102);
            Assert.AreEqual(all.Count, NvidiaFiles.Missing(Here(), all).Count, "an empty folder lacks everything");

            // NGX's DLLs the player brought stay, whatever their version.
            File.WriteAllBytes(Path.Combine(folder, "nvngx_dlss.dll"), new byte[10]);
            File.WriteAllBytes(Path.Combine(folder, "nvngx_dlssg.dll"), new byte[10]);
            List<NvidiaFiles.Entry> missing = NvidiaFiles.Missing(Here(), all);
            Assert.IsFalse(missing.Any(e => e.FileName == "nvngx_dlss.dll" || e.FileName == "nvngx_dlssg.dll"));
            // Streamline is still missing, so the licence text for both comes with it.
            Assert.IsTrue(missing.Any(e => e.FileName == "nvngx_dlss.license.txt"));

            // Streamline of another version -- or of the right size but another content
            // -- does not run, so it counts as missing.
            NvidiaFiles.Entry interposer = all.First(e => e.FileName == "sl.interposer.dll");
            File.WriteAllBytes(Path.Combine(folder, "sl.interposer.dll"), new byte[10]);
            Assert.IsTrue(NvidiaFiles.Missing(Here(), all).Contains(interposer), "another size");
            File.WriteAllBytes(Path.Combine(folder, "sl.interposer.dll"), new byte[interposer.Size]);
            Assert.IsTrue(NvidiaFiles.Missing(Here(), all).Contains(interposer), "the right size, another content");
        }

        [TestMethod]
        public void EachUseIsLookedForInItsOwnFolder()
        {
            string streamline = Path.Combine(folder, "streamline");
            Directory.CreateDirectory(streamline);
            NvidiaFiles.Folders folders = new NvidiaFiles.Folders { Dlss = folder, FrameGeneration = streamline };
            List<NvidiaFiles.Entry> all = NvidiaFiles.For(0x190, Ada102);

            File.WriteAllBytes(Path.Combine(streamline, "nvngx_dlssg.dll"), new byte[10]);
            Assert.IsFalse(NvidiaFiles.Missing(folders, all).Any(e => e.FileName == "nvngx_dlssg.dll"));
            File.WriteAllBytes(Path.Combine(streamline, "nvngx_dlss.dll"), new byte[10]);
            Assert.IsTrue(NvidiaFiles.Missing(folders, all).Any(e => e.FileName == "nvngx_dlss.dll"),
                          "DLSS's folder is the other one");

            NvidiaFiles.Entry notice = all.First(e => e.FileName == "nvngx_dlss.license.txt");
            Assert.AreEqual(folder, NvidiaFiles.FolderOf(notice, folders, true));
            Assert.AreEqual(streamline, NvidiaFiles.FolderOf(notice, folders, false));

            // NGX's second folder, next to KSP_x64.exe, holds a DLSS DLL NGX finds.
            string game = Path.Combine(folder, "game");
            Directory.CreateDirectory(game);
            folders.DlssAlso = game;
            File.WriteAllBytes(Path.Combine(game, "nvngx_dlss.dll"), new byte[10]);
            Assert.IsFalse(NvidiaFiles.Missing(folders, all).Any(e => e.FileName == "nvngx_dlss.dll"),
                           "found where NGX looks second");
            // A file of any version counts where it is looked for; frame generation's
            // is not looked for there.
            File.Delete(Path.Combine(streamline, "nvngx_dlssg.dll"));
            File.WriteAllBytes(Path.Combine(game, "nvngx_dlssg.dll"), new byte[10]);
            Assert.IsTrue(NvidiaFiles.Missing(folders, all).Any(e => e.FileName == "nvngx_dlssg.dll"),
                          "only DLSS's DLL is looked for there");
        }

        [TestMethod]
        public void ALicenceTextAloneAsksForNoDownload()
        {
            // DLSS on an RTX 20: its DLL in place, its licence text not.
            File.WriteAllBytes(Path.Combine(folder, "nvngx_dlss.dll"), new byte[10]);
            Assert.AreEqual(0, NvidiaFiles.Missing(Here(), NvidiaFiles.For(0x160, RtxTuring)).Count);

            // Frame generation too: every DLL in place, those of an exact version with
            // the checksum asked for -- stand-ins here, with checksums of their own --
            // and no licence text.
            List<NvidiaFiles.Entry> standIns = new List<NvidiaFiles.Entry>();
            foreach (NvidiaFiles.Entry entry in NvidiaFiles.For(0x190, Ada102))
            {
                byte[] content = Content(entry.FileName);
                standIns.Add(new NvidiaFiles.Entry
                {
                    Name = entry.Name, FileName = entry.FileName, Size = content.Length,
                    Sha256 = NvidiaFiles.Sha256(content), Use = entry.Use, Notice = entry.Notice,
                    ExactVersion = entry.ExactVersion,
                });
                if (!entry.Notice) File.WriteAllBytes(Path.Combine(folder, entry.FileName), content);
            }
            Assert.AreEqual(0, NvidiaFiles.Missing(Here(), standIns).Count);

            // One of Streamline's DLLs gone: it comes with the licence texts of its use.
            File.Delete(Path.Combine(folder, "sl.pcl.dll"));
            CollectionAssert.AreEquivalent(
                new[] { "sl.pcl.dll", "nvngx_dlss.license.txt", "reflex.license.txt", "sl.license.txt",
                        "sl.3rd-party-licenses.md" },
                NvidiaFiles.Missing(Here(), standIns).Select(e => e.FileName).ToArray());
        }

        // An archive entry as NVIDIA's are: deflated, no data descriptor, no extra
        // field. .NET's ZipArchive writes it; the local header is made the way
        // NVIDIA's are around its data.
        private static NvidiaFiles.Entry Archived(string name, byte[] content, out byte[] range)
        {
            byte[] archive;
            using (MemoryStream stream = new MemoryStream())
            {
                using (ZipArchive zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
                {
                    ZipArchiveEntry entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                    using (Stream write = entry.Open()) write.Write(content, 0, content.Length);
                }
                archive = stream.ToArray();
            }

            int nameLength = archive[26] | archive[27] << 8;
            int extraLength = archive[28] | archive[29] << 8;
            int dataStart = 30 + nameLength + extraLength;
            int centralDirectory = FindCentralDirectory(archive);
            uint compressedSize = System.BitConverter.ToUInt32(archive, centralDirectory + 20);
            Assert.AreEqual(8, archive[8] | archive[9] << 8, "deflated");

            range = new byte[30 + nameLength + compressedSize];
            System.Array.Copy(archive, 0, range, 0, 30);
            range[6] = 0;
            range[7] = 0;
            System.BitConverter.GetBytes(compressedSize).CopyTo(range, 18);
            System.BitConverter.GetBytes((uint)content.Length).CopyTo(range, 22);
            range[28] = 0;
            range[29] = 0;
            System.Array.Copy(archive, 30, range, 30, nameLength);
            System.Array.Copy(archive, dataStart, range, 30 + nameLength, compressedSize);

            return new NvidiaFiles.Entry
            {
                Name = name,
                FileName = Path.GetFileName(name),
                Offset = 0,
                CompressedSize = compressedSize,
                Size = content.Length,
                Sha256 = NvidiaFiles.Sha256(content),
            };
        }

        private static int FindCentralDirectory(byte[] archive)
        {
            for (int i = archive.Length - 4; i >= 0; i--)
                if (archive[i] == 0x50 && archive[i + 1] == 0x4B && archive[i + 2] == 1 && archive[i + 3] == 2)
                    return i;
            Assert.Fail("no central directory");
            return -1;
        }

        private static byte[] Content(string what = "a file NVIDIA would ship")
        {
            StringBuilder text = new StringBuilder();
            for (int i = 0; i < 5000; i++) text.Append("line ").Append(i).Append(" of ").Append(what).Append('\n');
            return Encoding.UTF8.GetBytes(text.ToString());
        }

        private static byte[] Extracted(NvidiaFiles.Entry entry, byte[] range)
        {
            using (MemoryStream output = new MemoryStream())
            {
                NvidiaFiles.Extract(entry, new MemoryStream(range), output);
                return output.ToArray();
            }
        }

        [TestMethod]
        public void AnEntryComesOutAsItWent()
        {
            byte[] content = Content();
            byte[] range;
            NvidiaFiles.Entry entry = Archived("bin/x64/sl.interposer.dll", content, out range);
            Assert.AreEqual(entry.RangeLength, range.Length, "the range fetched is the entry");
            CollectionAssert.AreEqual(content, Extracted(entry, range));
        }

        [TestMethod]
        public void ADamagedOrForeignEntryIsRefused()
        {
            byte[] content = Content();
            byte[] range;
            NvidiaFiles.Entry entry = Archived("bin/x64/sl.interposer.dll", content, out range);

            byte[] shorter = new byte[range.Length - 1];
            System.Array.Copy(range, shorter, shorter.Length);
            Assert.ThrowsException<InvalidDataException>(() => Extracted(entry, shorter), "cut short");

            byte[] renamed = (byte[])range.Clone();
            renamed[30] ^= 0x20;
            Assert.ThrowsException<InvalidDataException>(() => Extracted(entry, renamed), "another name");

            NvidiaFiles.Entry otherChecksum = Archived("bin/x64/sl.interposer.dll", content, out range);
            otherChecksum.Sha256 = NvidiaFiles.Sha256(new byte[] { 1, 2, 3 });
            Assert.ThrowsException<InvalidDataException>(() => Extracted(otherChecksum, range), "checksum");

            byte[] corrupted = (byte[])range.Clone();
            corrupted[corrupted.Length / 2] ^= 0xFF;
            Assert.ThrowsException<InvalidDataException>(() => Extracted(entry, corrupted), "damaged data");
        }

        private NvidiaFiles.Placement Planned(NvidiaFiles.Entry entry, byte[] content)
        {
            string target = Path.Combine(folder, entry.FileName);
            string partial = NvidiaFiles.PartialPath(target);
            File.WriteAllBytes(partial, content);
            return new NvidiaFiles.Placement { Entry = entry, Target = target, Partial = partial };
        }

        [TestMethod]
        public void AFileAlreadyThereIsKeptNotReplaced()
        {
            byte[] content = Content();
            byte[] range;
            NvidiaFiles.Entry entry = Archived("bin/x64/nvngx_dlssg.dll", content, out range);
            string target = Path.Combine(folder, "nvngx_dlssg.dll");

            File.WriteAllBytes(target, new byte[] { 9, 9, 9 });
            CollectionAssert.AreEqual(new[] { "nvngx_dlssg.dll.old" },
                                      NvidiaFiles.PlaceAll(new[] { Planned(entry, content) }));
            CollectionAssert.AreEqual(content, File.ReadAllBytes(target));
            CollectionAssert.AreEqual(new byte[] { 9, 9, 9 }, File.ReadAllBytes(target + ".old"));

            // The same file again is left alone; another one goes to .old2.
            Assert.AreEqual(0, NvidiaFiles.PlaceAll(new[] { Planned(entry, content) }).Count);
            File.WriteAllBytes(target, new byte[] { 7 });
            CollectionAssert.AreEqual(new[] { "nvngx_dlssg.dll.old2" },
                                      NvidiaFiles.PlaceAll(new[] { Planned(entry, content) }));
            Assert.IsFalse(File.Exists(NvidiaFiles.PartialPath(target)));
        }

        [TestMethod]
        public void AFailurePartWayPutsEverythingBack()
        {
            byte[] first = Content("the first");
            byte[] second = Content("the second");
            byte[] range;
            NvidiaFiles.Entry firstEntry = Archived("bin/x64/sl.common.dll", first, out range);
            NvidiaFiles.Entry secondEntry = Archived("bin/x64/sl.dlss_g.dll", second, out range);

            string firstTarget = Path.Combine(folder, "sl.common.dll");
            File.WriteAllBytes(firstTarget, new byte[] { 1 });
            NvidiaFiles.Placement placedFirst = Planned(firstEntry, first);
            NvidiaFiles.Placement failing = Planned(secondEntry, second);
            // The second cannot be placed: its partial file is gone.
            File.Delete(failing.Partial);

            Assert.ThrowsException<IOException>(() => NvidiaFiles.PlaceAll(new[] { placedFirst, failing }));
            CollectionAssert.AreEqual(new byte[] { 1 }, File.ReadAllBytes(firstTarget), "the player's file is back");
            Assert.IsFalse(File.Exists(firstTarget + ".old"));
            Assert.IsFalse(File.Exists(placedFirst.Partial));
            Assert.IsFalse(File.Exists(Path.Combine(folder, "sl.dlss_g.dll")));
        }
    }
}
