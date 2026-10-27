using System;
using System.Diagnostics;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ReDefinition.Tests
{
    // The comments in the code (tools/DocLock), checked: no comment names a file, switch or member that does not
    // exist, and no new or changed comment breaks the limits of docs/development/writing-code-comments.md. The tool
    // is built and run as `check`; its output names what to fix. It builds the tool first, so it is the slowest test:
    // the category Docs lets a quick run leave it out (--filter TestCategory!=Docs).
    [TestClass]
    public class DeveloperDocumentationTests
    {
        // The longest the check may take, build included, in milliseconds.
        private const int TimeoutMilliseconds = 10 * 60 * 1000;

        // The check passes; its output in the failure message where it does not, or where it does not end in time.
        [TestMethod]
        [TestCategory("Docs")]
        public void EveryCommentNamesOnlyWhatExists()
        {
            string root = RepoRoot.Root("tools", "DocLock", "DocLock.csproj");
            var info = new ProcessStartInfo("dotnet", "run --project tools/DocLock -c Release -- check")
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            // Both streams are read as they come: reading one to its end first stops the tool when the other fills up.
            var output = new StringBuilder();
            using (Process run = Process.Start(info))
            {
                DataReceivedEventHandler collect = (sender, line) =>
                {
                    if (line.Data == null) return;
                    lock (output) output.AppendLine(line.Data);
                };
                run.OutputDataReceived += collect;
                run.ErrorDataReceived += collect;
                run.BeginOutputReadLine();
                run.BeginErrorReadLine();
                if (!run.WaitForExit(TimeoutMilliseconds))
                {
                    // The tree: `dotnet run` has the tool as a child.
                    Process.Start(new ProcessStartInfo("taskkill", "/PID " + run.Id + " /T /F") { UseShellExecute = false, CreateNoWindow = true })
                           .WaitForExit();
                    Assert.Fail("DocLock did not finish in " + TimeoutMilliseconds / 1000 + " s. Its output so far:\n" + output);
                }
                // The last lines of both streams arrive after the process ends.
                run.WaitForExit();
                Assert.AreEqual(0, run.ExitCode, output.ToString());
            }
        }
    }
}
