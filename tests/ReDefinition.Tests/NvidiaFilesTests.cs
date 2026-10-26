using System.Collections.Generic;
using System.IO.Compression;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Proxy;
using ReDefinition.Vendor;

namespace ReDefinition.Tests
{
    // NVIDIA's DLLs as the download finds, checks and places them (NvidiaFiles),
    // without the network: an archive made here the way NVIDIA's is laid out.
    [TestClass]
    public class NvidiaFilesTests
    {
        // NVAPI's implementation ids: a Turing chip with DLSS, one without (a GTX 16), and an Ada chip.
        private const uint RtxTuring = 0x4;     // TU104, a Turing chip with DLSS
        private const uint GtxTuring = 0x8;     // TU116, a GTX 16: Turing without DLSS
        private const uint Ada102 = 0x2;

        // The folder the test's files are placed in.
        private string folder;

        // A fresh folder of the test's own in the temporary folder.
        [TestInitialize]
        public void MakeFolder()
        {
            folder = Path.Combine(Path.GetTempPath(), "ReDefinitionNvidiaFilesTests-" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
        }

        // Deletes the test's folder and everything in it.
        [TestCleanup]
        public void RemoveFolder()
        {
            Directory.Delete(folder, true);
        }

        // Folders for both uses pointing at the test's folder.
        private NvidiaFiles.Folders Here()
        {
            return new NvidiaFiles.Folders { Dlss = folder, FrameGeneration = folder };
        }

        // The GPU decides what is offered: nothing before Turing, on a GTX 16 or a Tegra; DLSS and Ray
        // Reconstruction from Turing on; frame generation's files from Ada on, or on Ampere with the package.
        [TestMethod]
        public void TheGpuDecidesWhatIsOffered()
        {
            Assert.AreEqual(0, NvidiaFiles.For(0, 0).Count, "not NVIDIA's, or unknown");
            Assert.AreEqual(0, NvidiaFiles.For(0x150, 0).Count, "before Turing");
            Assert.AreEqual(0, NvidiaFiles.For(0x160, GtxTuring).Count, "GTX 16: Turing without DLSS");
            Assert.AreEqual(0, NvidiaFiles.For(0xE0000040, 0).Count, "Tegra");

            // DLSS and Ray Reconstruction from Turing on; frame generation's files from Ada on.
            string[] beforeAda =
            {
                "nvngx_dlss.dll", "nvngx_dlss.license.txt", "sl.interposer.dll", "sl.common.dll", "sl.pcl.dll",
                "sl.dlss_d.dll", "nvngx_dlssd.dll", "sl.license.txt", "sl.3rd-party-licenses.md",
            };
            CollectionAssert.AreEquivalent(beforeAda, NvidiaFiles.For(0x160, RtxTuring).Select(e => e.FileName).ToArray());
            CollectionAssert.AreEquivalent(beforeAda, NvidiaFiles.For(0x170, 0x4).Select(e => e.FileName).ToArray(), "Ampere");
            Assert.AreEqual(NvidiaFiles.All.Length, NvidiaFiles.For(0x170, 0x4, true).Count,
                "Ampere with dlssg_for_sm86: Streamline's frame generation plugins too");
            Assert.AreEqual(0, NvidiaFiles.For(0x160, GtxTuring, true).Count, "no DLSS, no package's frame generation");

            Assert.AreEqual(NvidiaFiles.All.Length, NvidiaFiles.For(0x190, Ada102).Count);
            Assert.AreEqual(NvidiaFiles.All.Length, NvidiaFiles.For(0x1B0, Ada102).Count, "Blackwell");
        }

        // The download for an Ada GPU, with DLSS, frame generation and Ray Reconstruction, is about 57 MB.
        [TestMethod]
        public void TheReleaseIsAboutFiftySevenMegabytesForAnAdaGpu()
        {
            // DLSS, frame generation and Ray Reconstruction (nvngx_dlssd.dll, 21.6 MB).
            long bytes = NvidiaFiles.DownloadSize(NvidiaFiles.For(0x190, Ada102));
            Assert.IsTrue(bytes > 56000000 && bytes < 58500000, bytes.ToString());
        }

        // An empty folder lacks everything; NGX's DLLs the player brought count whatever their version, while
        // Streamline of another size or content counts as missing.
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

        // Each use's files are looked for in that use's folder, and DLSS's also where NGX looks second, next to
        // KSP_x64.exe; the licence text goes with DLSS where that is fetched too.
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

        // A licence text missing alone asks for no download; a DLL missing brings the licence texts of its use.
        [TestMethod]
        public void ALicenceTextAloneAsksForNoDownload()
        {
            // DLSS on an RTX 20: its DLL in place, its licence text not.
            File.WriteAllBytes(Path.Combine(folder, "nvngx_dlss.dll"), new byte[10]);
            Assert.AreEqual(0, NvidiaFiles.Missing(Here(), NvidiaFiles.For(0x160, RtxTuring)
                                                               .Where(e => e.Use != NvidiaFiles.Use.Streamline).ToList()).Count);

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
        internal static NvidiaFiles.Entry Archived(string name, byte[] content, out byte[] range, bool dataDescriptor = false)
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
            // Intel's are streamed: flags 8, and no checksum or sizes in the local header,
            // which a data descriptor after the data -- not in the range -- carries.
            if (dataDescriptor)
            {
                range[6] = 8;
                for (int i = 14; i < 26; i++) range[i] = 0;
            }
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

        // Where the archive's central directory entry starts, found by its signature from the end.
        private static int FindCentralDirectory(byte[] archive)
        {
            for (int i = archive.Length - 4; i >= 0; i--)
                if (archive[i] == 0x50 && archive[i + 1] == 0x4B && archive[i + 2] == 1 && archive[i + 3] == 2)
                    return i;
            Assert.Fail("no central directory");
            return -1;
        }

        // A file's content of 5000 lines, large enough to be deflated, telling what it stands for.
        internal static byte[] Content(string what = "a file NVIDIA would ship")
        {
            StringBuilder text = new StringBuilder();
            for (int i = 0; i < 5000; i++) text.Append("line ").Append(i).Append(" of ").Append(what).Append('\n');
            return Encoding.UTF8.GetBytes(text.ToString());
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

        // An entry fetched as its range extracts to what went in.
        [TestMethod]
        public void AnEntryComesOutAsItWent()
        {
            byte[] content = Content();
            byte[] range;
            NvidiaFiles.Entry entry = Archived("bin/x64/sl.interposer.dll", content, out range);
            Assert.AreEqual(entry.RangeLength, range.Length, "the range fetched is the entry");
            CollectionAssert.AreEqual(content, Extracted(entry, range));
        }

        // An entry cut short, under another name, with another checksum or with damaged data is refused.
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

        // A placement of the entry in the test's folder, its content already written to the partial file.
        private NvidiaFiles.Placement Planned(NvidiaFiles.Entry entry, byte[] content)
        {
            string target = Path.Combine(folder, entry.FileName);
            string partial = NvidiaFiles.PartialPath(target);
            File.WriteAllBytes(partial, content);
            return new NvidiaFiles.Placement { Entry = entry, Target = target, Partial = partial };
        }

        // A file already in place is kept as .old, then .old2, and replaced; the same file again is left alone.
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

        // Where a file cannot be placed part way, every file placed before goes back to what the player had, and
        // neither .old nor partial files are left.
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
