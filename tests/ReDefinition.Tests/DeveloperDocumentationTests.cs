using System.Diagnostics;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ReDefinition.Tests
{
    // The developer documentation, which lives in the code (tools/DocLock), checked: every element documented, none
    // dirty, every element in the lock, and no comment naming a file, switch or member that does not exist. An
    // element is dirty when its code changed since its documentation was written or confirmed. The tool is built
    // and run as `check`; its output names what to fix.
    [TestClass]
    public class DeveloperDocumentationTests
    {
        // The check passes; its output in the failure message where it does not.
        [TestMethod]
        public void EveryElementIsDocumentedAndNoneIsDirty()
        {
            string root = Root();
            Assert.IsNotNull(root, "the repository's root, with tools/DocLock, not found above the test's folder");
            var info = new ProcessStartInfo("dotnet", "run --project tools/DocLock -c Release -- check")
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using (Process run = Process.Start(info))
            {
                string output = run.StandardOutput.ReadToEnd() + run.StandardError.ReadToEnd();
                run.WaitForExit();
                Assert.AreEqual(0, run.ExitCode, output);
            }
        }

        // The first folder above the working folder that holds tools/DocLock.
        private static string Root()
        {
            for (DirectoryInfo d = new DirectoryInfo(Directory.GetCurrentDirectory()); d != null; d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, "tools", "DocLock", "DocLock.csproj"))) return d.FullName;
            return null;
        }
    }
}
