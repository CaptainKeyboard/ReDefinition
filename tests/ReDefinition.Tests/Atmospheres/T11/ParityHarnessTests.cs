using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ReDefinition.Tests.Atmospheres.T11
{
    // The parity harness (package T11): the check list is complete against the plan's parity table, every check names a function of
    // tools/weather_metrics/parity_checks.py, and every fixed number names its source. The logic lives in tools/weather_metrics/parity_run.py
    // (verify); these tests run it. The self-test of the reference renderer is a Python test (tools/tests/test_parity_run.py).
    // Harness: tests the parity harness (tools/weather_metrics), not the product.
    [TestClass, TestCategory("Harness")]
    public class ParityHarnessTests
    {
        private static string Root()
        {
            return RepoRoot.Root("src", "ReDefinition.csproj");
        }

        private static bool Python(string arguments, int timeoutMs, out string output)
        {
            string script = Path.Combine(Root(), "tools", "weather_metrics", "parity_run.py");
            ProcessStartInfo info = new ProcessStartInfo("python", "\"" + script + "\" " + arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Root()
            };
            try
            {
                using (Process p = Process.Start(info))
                {
                    var text = new System.Text.StringBuilder();
                    p.OutputDataReceived += (s, e) => { if (e.Data != null) text.AppendLine(e.Data); };
                    p.ErrorDataReceived += (s, e) => { if (e.Data != null) text.AppendLine(e.Data); };
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(); } catch (InvalidOperationException) { }
                        output = text + "TIMEOUT";
                        return false;
                    }
                    p.WaitForExit();
                    output = text.ToString();
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
        public void TheCheckListCoversEveryRowOfTheParityTable()
        {
            string output;
            bool ok = Python("verify", 120000, out output);
            Assert.IsTrue(ok, output);
            StringAssert.Contains(output, "COMPLETE");
        }

        [TestMethod]
        public void EveryFixedNumberNamesItsSourceAndNoThresholdIsProposed()
        {
            string json = File.ReadAllText(Path.Combine(Root(), "tests", "data", "parity", "checks.json"));
            MatchCollection numbers = Regex.Matches(json, "\"id\": \"N\\d\\d\"");
            Assert.IsTrue(numbers.Count >= 14, "the fixed reference numbers");
            Assert.AreEqual(numbers.Count, Regex.Matches(json, "\"id\": \"N\\d\\d\"[^}]*\"source\": \"[^\"]{20,}").Count, "every number carries a source");
            Assert.AreEqual(0, Regex.Matches(json, "\\[proposed\\](?! kept)").Count, "no threshold is left to be proposed");
        }
    }
}
