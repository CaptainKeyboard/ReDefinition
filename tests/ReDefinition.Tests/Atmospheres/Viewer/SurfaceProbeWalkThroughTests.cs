using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Atmospheres.Viewer;
using ReDefinition.Core;
using ReDefinition.Tests.Atmospheres.B09;
using UnityEngine;

namespace ReDefinition.Tests.Atmospheres.Viewer
{
    // The whole K-S2 scenario of SurfaceProbe against the solo rig's model of the game: the five frames in their order, each with its fog line, the rows file that lists exactly the
    // metrics the start emits, the series file that names the probe, and the faults the probe judges (the sun runs on, the cell is not alone, a camera that moved).
    [TestClass]
    public class SurfaceProbeWalkThroughTests
    {
        private const double Dt = 0.05;

        private sealed class Run
        {
            internal FakeSoloRig Rig;
            internal SurfaceProbe Probe;
            internal readonly List<string> Log = new List<string>();
            internal readonly List<string> Rows = new List<string>();
            internal int Quits;
            internal string Joined { get { return string.Join("; ", Probe.Failures.ToArray()); } }
        }

        private Action<string> savedLog, savedSink, savedSay;

        [TestInitialize]
        public void Capture()
        {
            savedLog = SeriesProbe.LogLine;
            savedSink = AtmoResult.Sink;
            savedSay = AtmospheresLog.SayLine;
            SeriesProbe.ForgetRunningProbes();
            AtmospheresSettings.ResetToDefaults();
            Tuning.ClearOverrides();
        }

        [TestCleanup]
        public void Release()
        {
            SeriesProbe.LogLine = savedLog;
            AtmoResult.Sink = savedSink;
            AtmospheresLog.SayLine = savedSay;
            AtmospheresSettings.ResetToDefaults();
            Tuning.ClearOverrides();
            SeriesProbe.ForgetRunningProbes();
        }

        private static Run Start(Action<FakeSoloRig> fault = null)
        {
            var r = new Run { Rig = new FakeSoloRig() };
            if (fault != null) fault(r.Rig);
            SeriesProbe.LogLine = m => r.Log.Add(m);
            AtmoResult.Sink = r.Rows.Add;
            r.Probe = new SurfaceProbe { Rig = r.Rig, CommandLineOverride = new[] { "KSP_x64.exe", "-atmoSeries", "K-S2", "-atmoStart", "surface", "-atmoProbe", "SurfaceProbe" } };
            // The fog the scene holds: a density only under the rule before the package (the old rule keeps the field's visibility), none with the weather effects or the mod off.
            int beginFrame = 1;
            r.Probe.FogReader = () =>
            {
                Tuning.BeginFrame(beginFrame++);   // an override takes effect at the next frame
                bool old = !Tuning.Flag("driver.forcedLevelOwnsFog", true);
                bool on = AtmospheresSettings.Enabled && AtmospheresSettings.WeatherEffects && old;
                return new FogReading { On = on, Mode = 3, Density = on ? 0.00012f : 0f, Colour = new Color(0.7f, 0.75f, 0.8f), Held = on ? 0.00012f : 0f, Asked = on ? 0.00012f : 0f, Level = 0f, VisibilityM = on ? 14000f : 20000f };
            };
            r.Probe.QuitGame = () => r.Quits++;
            r.Probe.Init();
            return r;
        }

        private static void Drive(Run r, double maxS = 1500.0)
        {
            double elapsed = 0.0;
            while (elapsed < maxS && !(r.Probe.Finished && r.Quits > 0))
            {
                r.Rig.Advance(Dt);
                r.Probe.Tick((float)Dt);
                elapsed += Dt;
            }
        }

        private static string Dump(Run r)
        {
            return "--- failures: " + r.Joined + "\n--- log tail:\n" + string.Join("\n", r.Log.Skip(Math.Max(0, r.Log.Count - 30)).ToArray());
        }

        private static readonly Regex RowPattern = new Regex("\"metric\":\"(?<m>[^\"]+)\",\"value\":(?<v>\"(?:[^\"\\\\]|\\\\.)*\"|[^,}]+)");

        private static Dictionary<string, string> Emitted(Run r)
        {
            var map = new Dictionary<string, string>();
            foreach (string line in r.Rows)
            {
                Match m = RowPattern.Match(line);
                if (m.Success) map[m.Groups["m"].Value] = m.Groups["v"].Value.Trim('"');
            }
            return map;
        }

        [TestMethod]
        public void AHealthyStartTakesTheFiveFramesInOrderAndLogsTheFogOfEach()
        {
            Run r = Start();
            Drive(r);
            Assert.IsTrue(r.Probe.Passed, Dump(r));
            Assert.AreEqual("surface", r.Probe.StartId);
            Assert.IsTrue(r.Probe.Elapsed < 12 * 60, "the scenario fits its twelve minutes: " + r.Probe.Elapsed);
            Assert.IsTrue(r.Probe.Elapsed > 60, "the real scenario is not a handful of frames: " + r.Probe.Elapsed);
            int last = -1;
            foreach (string frame in SurfaceProbe.Frames)
            {
                int at = r.Log.FindIndex(l => l.StartsWith("surface: " + frame + ": fog "));
                Assert.IsTrue(at > last, frame + " is described after the frame before it\n" + Dump(r));
                last = at;
                Assert.IsTrue(r.Log.Any(l => l.StartsWith("surface: " + frame + ": picture ") && !l.EndsWith("none")), frame + " has a picture");
            }
            Dictionary<string, string> rows = Emitted(r);
            Assert.IsTrue(double.Parse(rows[SurfaceProbe.Prefix + "old_fog_fog_density"], CultureInfo.InvariantCulture) > 0.0001, "the old rule's frame holds the fog");
            Assert.AreEqual(0.0, double.Parse(rows[SurfaceProbe.Prefix + "baseline_fog_density"], CultureInfo.InvariantCulture), 0.0, "the rule now holds none");
            Assert.AreEqual(0.0, double.Parse(rows[SurfaceProbe.Prefix + "no_weather_fog_density"], CultureInfo.InvariantCulture), 0.0);
            Assert.AreEqual(0.0, double.Parse(rows[SurfaceProbe.Prefix + "no_mod_fog_density"], CultureInfo.InvariantCulture), 0.0);
            Assert.IsTrue(AtmospheresSettings.Enabled && AtmospheresSettings.WeatherEffects, "the switches are back on");
            Tuning.BeginFrame(100000);
            Assert.IsTrue(Tuning.Flag("driver.forcedLevelOwnsFog", true), "the fog rule's override is cleared");
        }

        [TestMethod]
        public void TheRowsFileListsExactlyTheMetricsTheProbeEmits()
        {
            Run r = Start();
            Drive(r);
            Assert.IsTrue(r.Probe.Passed, Dump(r));
            string root = RepoRoot.Root("tests", "series", "rows", "SURFACEPROBE.json");
            var file = (Dictionary<string, object>)MiniJson.Parse(File.ReadAllText(Path.Combine(root, "tests", "series", "rows", "SURFACEPROBE.json")));
            Assert.AreEqual("SURFACE", (string)file["package"]);
            var listed = new Dictionary<string, Dictionary<string, object>>();
            foreach (object o in (List<object>)file["rows"])
            {
                var row = (Dictionary<string, object>)o;
                Assert.AreEqual("K-S2", (string)row["series"], (string)row["metric"]);
                Assert.AreEqual("surface", (string)row["start"], (string)row["metric"]);
                Assert.IsFalse(listed.ContainsKey((string)row["metric"]), "row twice: " + (string)row["metric"]);
                listed[(string)row["metric"]] = row;
            }
            CollectionAssert.AreEquivalent(Emitted(r).Keys.ToList(), listed.Keys.ToList(), "rows file against the emitted metrics");
            foreach (KeyValuePair<string, Dictionary<string, object>> pair in listed)
            {
                if (pair.Key.EndsWith("_ok", StringComparison.Ordinal))
                {
                    Assert.AreEqual(1.0, Convert.ToDouble(pair.Value["equals"], CultureInfo.InvariantCulture), pair.Key + " must equal 1");
                    Assert.IsFalse(pair.Value.ContainsKey("required") && (bool)pair.Value["required"] == false, pair.Key + " is a check: required");
                }
                else Assert.IsTrue(pair.Value.ContainsKey("required") && (bool)pair.Value["required"] == false, pair.Key + " is information (required false)");
            }
        }

        [TestMethod]
        public void TheSeriesFileNamesTheProbeTheStartAndItsRows()
        {
            string root = RepoRoot.Root("tests", "series", "K-S2.json");
            var file = (Dictionary<string, object>)MiniJson.Parse(File.ReadAllText(Path.Combine(root, "tests", "series", "K-S2.json")));
            Assert.AreEqual("K-S2", (string)file["name"]);
            var start = (Dictionary<string, object>)((List<object>)file["starts"])[0];
            Assert.AreEqual("surface", (string)start["id"]);
            Assert.IsTrue(((List<object>)start["probeArgs"]).Cast<string>().Contains("SurfaceProbe"), "the start names its probe");
            var section = (Dictionary<string, object>)((List<object>)file["sections"])[0];
            Assert.AreEqual("SURFACEPROBE", (string)section["id"]);
        }

        [TestMethod]
        public void ASunThatRunsOnFailsTheFrameSameness()
        {
            Run r = Start(rig => rig.SunDrifts = true);
            Drive(r);
            Assert.IsTrue(r.Probe.Finished, Dump(r));
            Assert.IsFalse(r.Probe.Passed, Dump(r));
            Assert.IsFalse(r.Probe.Failures.Any(f => f.StartsWith("exception in the scenario")), "no unhandled exception: " + r.Joined);
            Assert.IsTrue(r.Probe.Failures.Any(f => f.StartsWith(SurfaceProbe.Prefix + "sun_fixed")), "the failure names sun_fixed: " + r.Joined);
            Assert.IsTrue(r.Log.Any(l => l.StartsWith("surface: ") && l.EndsWith(" - FAILED")), "a 'surface:' line says what was expected and what was read");
        }
    }
}
