using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Bridges;

namespace ReDefinition.Tests
{
    // dlssg_for_sm86's release zip as the install finds and takes it apart
    // (AmperePackage): a zip made here the way the project's release is laid out.
    [TestClass]
    public class AmperePackageTests
    {
        // The folder the test's zips and game folders stand in.
        private string folder;

        // A fresh folder of the test's own in the temporary folder.
        [TestInitialize]
        public void MakeFolder()
        {
            folder = Path.Combine(Path.GetTempPath(), "ReDefinitionAmperePackageTests-" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
        }

        // Deletes the test's folder and everything in it.
        [TestCleanup]
        public void RemoveFolder()
        {
            Directory.Delete(folder, true);
        }

        // A zip of that name with the entries given, compressed as usual.
        private string MakeZip(string name, params string[] entries)
        {
            return MakeZip(name, CompressionLevel.Optimal, entries);
        }

        // A zip of that name in the test's folder holding each entry as the text "content of <entry>", compressed
        // as given.
        private string MakeZip(string name, CompressionLevel level, params string[] entries)
        {
            string path = Path.Combine(folder, name);
            using (ZipArchive zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                foreach (string entry in entries)
                {
                    using (StreamWriter writer = new StreamWriter(zip.CreateEntry(entry, level).Open(), Encoding.UTF8))
                        writer.Write("content of " + entry);
                }
            }
            return path;
        }

        // The files of the project's release zip, as it lays them out.
        private static readonly string[] Release =
        {
            "version.dll", "dlssg_sm86.ini", "README.md", "LICENSE.md", "THIRD_PARTY_NOTICES.txt", "alternatives/dxgi.dll",
            "dlssg_sm86/backend/sm86_backend.dll", "dlssg_sm86/kernels/fg_kernels.dll", "dlssg_sm86/runtime/nvngx_dlssg.dll",
            "dlssg_sm86/xefg/xefg_kernels.dll", "dlssg_sm86/sr/nvngx_dlss.dll", "dlssg_sm86/sr/sr_kernels.dll", "docs/INSTALL.md",
        };

        // From the release the package takes the proxy version.dll, its ini, the files of its folder and the
        // licences moved into it; not the readme, the docs, the alternative dxgi.dll nor super resolution.
        [TestMethod]
        public void TheProxyItsIniAndFolderAreTakenWithoutSuperResolution()
        {
            string zip = MakeZip("dlssg-release-0.4.0-x64.zip", Release);
            using (FileStream stream = File.OpenRead(zip))
            {
                string[] targets = AmperePackage.Read(stream).Select(item => item.Target).OrderBy(t => t).ToArray();
                CollectionAssert.AreEqual(new[]
                {
                    "dlssg_sm86.ini", "dlssg_sm86/LICENSE.md", "dlssg_sm86/THIRD_PARTY_NOTICES.txt",
                    "dlssg_sm86/backend/sm86_backend.dll", "dlssg_sm86/kernels/fg_kernels.dll",
                    "dlssg_sm86/runtime/nvngx_dlssg.dll", "dlssg_sm86/xefg/xefg_kernels.dll", "version.dll",
                }.OrderBy(t => t).ToArray(), targets);
            }
        }

        // The super resolution part takes only its two files, installed on their own and not as the package.
        [TestMethod]
        public void TheSuperResolutionFilesAreTakenOnTheirOwn()
        {
            string zip = MakeZip("dlssg-release-0.4.0-x64.zip", Release);
            using (FileStream stream = File.OpenRead(zip))
            {
                string[] targets = AmperePackage.Read(stream, AmperePackage.Part.SuperResolution)
                    .Select(item => item.Target).OrderBy(t => t).ToArray();
                CollectionAssert.AreEqual(new[] { "dlssg_sm86/sr/nvngx_dlss.dll", "dlssg_sm86/sr/sr_kernels.dll" }, targets);
            }

            string game = Path.Combine(folder, "KSP");
            Directory.CreateDirectory(game);
            Assert.IsFalse(AmperePackage.SuperResolutionInstalled(game));
            Assert.AreEqual(2, AmperePackage.Install(zip, game, AmperePackage.Part.SuperResolution));
            Assert.IsTrue(AmperePackage.SuperResolutionInstalled(game));
            // Not the package itself: that is another install.
            Assert.IsFalse(AmperePackage.Installed(game));
            Assert.IsFalse(File.Exists(Path.Combine(game, "version.dll")));
            Assert.AreEqual("content of dlssg_sm86/sr/sr_kernels.dll",
                File.ReadAllText(Path.Combine(game, "dlssg_sm86", "sr", "sr_kernels.dll")));
        }

        // A zip without the super resolution kernels is refused for that part, naming the file, and nothing is
        // placed.
        [TestMethod]
        public void AZipWithoutTheSuperResolutionKernelsIsRefusedForThem()
        {
            string zip = MakeZip("dlssg-release-0.4.0-x64.zip", "version.dll", "dlssg_sm86/sr/nvngx_dlss.dll");
            string game = Path.Combine(folder, "KSP");
            Directory.CreateDirectory(game);

            InvalidDataException refused = Assert.ThrowsException<InvalidDataException>(
                () => AmperePackage.Install(zip, game, AmperePackage.Part.SuperResolution));
            StringAssert.Contains(refused.Message, "dlssg_sm86/sr/sr_kernels.dll");
            Assert.AreEqual(0, Directory.GetFileSystemEntries(game).Length);
        }

        // The install places the package's eight files as the zip holds them, the licences in the package's
        // folder, and leaves neither the super resolution files, the readme nor a file of its own behind.
        [TestMethod]
        public void TheInstallPlacesEveryFileAsTheZipHoldsIt()
        {
            string zip = MakeZip("dlssg-release-0.4.0-x64.zip", Release);
            string game = Path.Combine(folder, "KSP");
            Directory.CreateDirectory(game);

            Assert.AreEqual(8, AmperePackage.Install(zip, game));
            Assert.IsTrue(AmperePackage.Installed(game));
            Assert.AreEqual("content of dlssg_sm86/runtime/nvngx_dlssg.dll",
                File.ReadAllText(Path.Combine(game, "dlssg_sm86", "runtime", "nvngx_dlssg.dll")));
            Assert.AreEqual("content of LICENSE.md", File.ReadAllText(Path.Combine(game, "dlssg_sm86", "LICENSE.md")));
            Assert.IsFalse(Directory.Exists(Path.Combine(game, "dlssg_sm86", "sr")));
            Assert.IsFalse(File.Exists(Path.Combine(game, "README.md")));
            Assert.AreEqual(0, Directory.GetFiles(game, "*.redefinition-install", SearchOption.AllDirectories).Length);
        }

        // A file the install replaces is kept next to it as .old.
        [TestMethod]
        public void AFileAlreadyThereIsKeptAsOld()
        {
            string zip = MakeZip("dlssg-release-0.4.0-x64.zip", Release);
            string game = Path.Combine(folder, "KSP");
            Directory.CreateDirectory(game);
            File.WriteAllText(Path.Combine(game, "version.dll"), "earlier");

            AmperePackage.Install(zip, game);

            Assert.AreEqual("earlier", File.ReadAllText(Path.Combine(game, "version.dll.old")));
            Assert.AreEqual("content of version.dll", File.ReadAllText(Path.Combine(game, "version.dll")));
        }

        // A zip without the frame generation runtime is refused naming it, and nothing is placed.
        [TestMethod]
        public void AZipWithoutTheRuntimeIsRefusedAndNothingIsPlaced()
        {
            string zip = MakeZip("dlssg-release-0.5.0-x64.zip", "version.dll", "dlssg_sm86.ini", "dlssg_sm86/backend/b.dll",
                "dlssg_sm86/kernels/k.dll");
            string game = Path.Combine(folder, "KSP");
            Directory.CreateDirectory(game);

            InvalidDataException refused = Assert.ThrowsException<InvalidDataException>(() => AmperePackage.Install(zip, game));
            StringAssert.Contains(refused.Message, "dlssg_sm86/runtime/nvngx_dlssg.dll");
            Assert.AreEqual(0, Directory.GetFileSystemEntries(game).Length);
        }

        // A file whose checksum no longer matches its content is refused, and no file is placed.
        [TestMethod]
        public void ADamagedFileIsRefusedAndNothingIsPlaced()
        {
            // Stored uncompressed, one byte of version.dll's content changed: its checksum
            // no longer matches.
            string zip = MakeZip("dlssg-release-0.4.0-x64.zip", CompressionLevel.NoCompression, Release);
            byte[] bytes = File.ReadAllBytes(zip);
            byte[] content = Encoding.UTF8.GetBytes("content of version.dll");
            int at = IndexOf(bytes, content);
            Assert.IsTrue(at >= 0);
            bytes[at] ^= 0x20;
            File.WriteAllBytes(zip, bytes);
            string game = Path.Combine(folder, "KSP");
            Directory.CreateDirectory(game);

            Assert.ThrowsException<InvalidDataException>(() => AmperePackage.Install(zip, game));
            Assert.AreEqual(0, Directory.GetFiles(game, "*", SearchOption.AllDirectories).Length);
        }

        // Of the release zips in a folder the newest is found, and a zip of another name is not taken.
        [TestMethod]
        public void TheNewestReleaseZipIsFound()
        {
            string older = MakeZip("dlssg-release-0.3.5-x64.zip", "version.dll");
            string newer = MakeZip("dlssg-release-0.4.0-x64.zip", "version.dll");
            File.SetLastWriteTimeUtc(older, System.DateTime.UtcNow.AddDays(-1));
            MakeZip("something-else.zip", "version.dll");

            Assert.AreEqual(newer, AmperePackage.FindZip(folder));
        }

        // Where the needle first stands in the haystack, or -1.
        private static int IndexOf(byte[] haystack, byte[] needle)
        {
            for (int i = 0; i + needle.Length <= haystack.Length; i++)
            {
                int k = 0;
                while (k < needle.Length && haystack[i + k] == needle[k]) k++;
                if (k == needle.Length) return i;
            }
            return -1;
        }
    }
}
