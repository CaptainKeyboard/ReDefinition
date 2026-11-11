using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ReDefinition.Tests.Atmospheres.B15
{
    // The shipped sound files and their manifest (package B15): every file has a complete line with its licence, and
    // tools/audio/check_manifest.py fails a folder whose line lacks the licence line.
    [TestClass]
    public class SoundManifestTests
    {
        private static string Root()
        {
            return RepoRoot.Root("src", "ReDefinition.csproj");
        }

        private static string SoundsFolder()
        {
            return Path.Combine(Root(), "GameData", "ReDefinition", "Atmospheres", "Sounds");
        }

        private static bool RunChecker(string arguments, out string output)
        {
            string script = Path.Combine(Root(), "tools", "audio", "check_manifest.py");
            ProcessStartInfo info = new ProcessStartInfo("python", "\"" + script + "\" " + arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            try
            {
                using (Process p = Process.Start(info))
                {
                    output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                    p.WaitForExit();
                    return p.ExitCode == 0;
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                Assert.Inconclusive("python is not on the path");
                output = null;
                return false;
            }
        }

        [TestMethod]
        public void EveryShippedSoundFileHasACompleteManifestLineAndTheLimitsHold()
        {
            string output;
            Assert.IsTrue(RunChecker("--dir \"" + SoundsFolder() + "\"", out output), output);
        }

        [TestMethod]
        public void TheManifestListsExactlyTheFilesInTheFolderAndTheyAreSmall()
        {
            string folder = SoundsFolder();
            string[] files = Directory.GetFiles(folder, "*.ogg").Select(Path.GetFileName).OrderBy(n => n).ToArray();
            string manifest = File.ReadAllText(Path.Combine(folder, "SOUNDS.json"));
            string[] listed = Regex.Matches(manifest, "\"file\":\\s*\"([^\"]+)\"").Cast<Match>().Select(m => m.Groups[1].Value).OrderBy(n => n).ToArray();
            CollectionAssert.AreEqual(files, listed);
            Assert.IsTrue(files.Length >= 8 && files.Length <= 12, "step count " + files.Length);
            long download = files.Sum(n => new FileInfo(Path.Combine(folder, n)).Length);
            long decoded = Regex.Matches(manifest, "\"decodedBytes\":\\s*(\\d+)").Cast<Match>().Sum(m => long.Parse(m.Groups[1].Value));
            Console.WriteLine("METRIC sounds: " + files.Length + " files, " + download + " bytes download, " + decoded + " bytes decoded");
            Assert.IsTrue(download <= 4L * 1000 * 1000, "download " + download);
            Assert.IsTrue(decoded <= 12L * 1000 * 1000, "decoded " + decoded);
            Assert.IsTrue(Regex.IsMatch(manifest, "web\\.archive\\.org"), "no archived page");
            Assert.IsFalse(Regex.IsMatch(manifest, "CC-BY|CC BY|Pixabay|Sonniss"), "a licence that is not CC0");
        }

        [TestMethod]
        public void TheCheckerFailsAFolderWhoseLineHasNoLicenceLine()
        {
            string tmp = Path.Combine(Path.GetTempPath(), "b15-fixture-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            try
            {
                File.WriteAllBytes(Path.Combine(tmp, "a.ogg"), new byte[] { 1, 2, 3, 4 });
                File.WriteAllText(Path.Combine(tmp, "LICENSE-CC0.txt"), "x");
                File.WriteAllText(Path.Combine(tmp, "SOUNDS.json"),
                    "{\"files\":[{\"file\":\"a.ogg\",\"source\":\"s\",\"derivedFrom\":\"d\",\"author\":\"a\",\"sourceUrl\":\"u\","
                    + "\"archivedUrl\":\"http://web.archive.org/web/1/u\",\"licence\":\"CC0 1.0\",\"licenceLine\":\"\",\"dateRead\":\"2026-11-11\","
                    + "\"processing\":\"p\",\"decodedBytes\":8}]}");
                string output;
                Assert.IsFalse(RunChecker("--dir \"" + tmp + "\"", out output), "the checker accepted a line without a licence line");
                StringAssert.Contains(output, "licenceLine");
                File.Delete(Path.Combine(tmp, "a.ogg"));
                File.WriteAllBytes(Path.Combine(tmp, "b.ogg"), new byte[] { 1 });
                Assert.IsFalse(RunChecker("--dir \"" + tmp + "\"", out output), "the checker accepted a file without a line");
                StringAssert.Contains(output, "shipped without a line");
            }
            finally
            {
                Directory.Delete(tmp, true);
            }
        }
    }
}
