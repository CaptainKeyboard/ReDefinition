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
        private string folder;

        [TestInitialize]
        public void MakeFolder()
        {
            folder = Path.Combine(Path.GetTempPath(), "ReDefinitionAmperePackageTests-" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
        }

        [TestCleanup]
        public void RemoveFolder()
        {
            Directory.Delete(folder, true);
        }

        private string MakeZip(string name, params string[] entries)
        {
            return MakeZip(name, CompressionLevel.Optimal, entries);
        }

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

        private static readonly string[] Release =
        {
            "version.dll", "dlssg_sm86.ini", "README.md", "LICENSE.md", "THIRD_PARTY_NOTICES.txt", "alternatives/dxgi.dll",
            "dlssg_sm86/backend/sm86_backend.dll", "dlssg_sm86/kernels/fg_kernels.dll", "dlssg_sm86/runtime/nvngx_dlssg.dll",
            "dlssg_sm86/xefg/xefg_kernels.dll", "dlssg_sm86/sr/nvngx_dlss.dll", "dlssg_sm86/sr/sr_kernels.dll", "docs/INSTALL.md",
        };

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

        [TestMethod]
        public void TheNewestReleaseZipIsFound()
        {
            string older = MakeZip("dlssg-release-0.3.5-x64.zip", "version.dll");
            string newer = MakeZip("dlssg-release-0.4.0-x64.zip", "version.dll");
            File.SetLastWriteTimeUtc(older, System.DateTime.UtcNow.AddDays(-1));
            MakeZip("something-else.zip", "version.dll");

            Assert.AreEqual(newer, AmperePackage.FindZip(folder));
        }

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
