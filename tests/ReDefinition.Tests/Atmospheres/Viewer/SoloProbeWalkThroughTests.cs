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

namespace ReDefinition.Tests.Atmospheres.Viewer
{
    // The whole K-S1 scenario of SoloProbe against a model of the game (FakeSoloRig): the order of the steps, every row the start emits (and that tests/series/rows/SOLOPROBE.json lists
    // exactly these), and one fault test for each thing the probe judges (another layer drawn, the object not in view or filling the frame, the sun running on, rain forced, a mask the
    // pass does not take, a noisy baseline): the probe has to fail with the check that names it, never with an unhandled exception. What it cannot cover: the real pass, the real pictures
    // (PictureMaskTests do the arithmetic on known pictures), and whether the bands fit the real game.
    [TestClass]
    public class SoloProbeWalkThroughTests
    {
        private const double Dt = 0.05;

        private sealed class Run
        {
            internal FakeSoloRig Rig;
            internal SoloProbe Probe;
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
            SeriesProbe.ForgetRunningProbes();
        }

        private static Run Start(Action<FakeSoloRig> fault = null)
        {
            var r = new Run { Rig = new FakeSoloRig() };
            if (fault != null) fault(r.Rig);
            SeriesProbe.LogLine = m => r.Log.Add(m);
            AtmoResult.Sink = r.Rows.Add;
            r.Probe = new SoloProbe { Rig = r.Rig, CommandLineOverride = new[] { "KSP_x64.exe", "-atmoSeries", "K-S1", "-atmoStart", "solo", "-atmoProbe", "SoloProbe" } };
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

        private static void AssertFailsWith(Run r, string check)
        {
            Assert.IsTrue(r.Probe.Finished, "the probe ended\n" + Dump(r));
            Assert.IsFalse(r.Probe.Passed, "the probe failed\n" + Dump(r));
            Assert.IsFalse(r.Probe.Failures.Any(f => f.StartsWith("exception in the scenario")), "no unhandled exception: " + r.Joined);
            Assert.IsFalse(r.Probe.Failures.Any(f => f.StartsWith("the scenario did not end")), "the scenario ended on its own: " + r.Joined);
            Assert.IsTrue(r.Probe.Failures.Any(f => f.StartsWith(SoloProbe.Prefix + check + " ") || f == SoloProbe.Prefix + check), "the failure names " + check + ": " + r.Joined);
            Assert.IsTrue(r.Log.Any(l => l.StartsWith("Probe: FAILED")), "the verdict line was written");
            Assert.IsTrue(r.Log.Any(l => l.StartsWith("solo: ") && l.EndsWith(" - FAILED")), "a 'solo:' line says what was expected and what was read");
            Assert.AreEqual(1, r.Quits, "the game was told to quit once");
        }

        // --- The healthy start ---

        [TestMethod]
        public void AHealthySoloStartPassesEveryCheckForEveryView()
        {
            Run r = Start();
            Drive(r);
            Assert.IsTrue(r.Probe.Passed, Dump(r));
            Assert.AreEqual("solo", r.Probe.StartId);
            Assert.AreEqual("Probe: PASSED", r.Log[r.Log.Count - 1]);
            Assert.IsTrue(r.Probe.Elapsed < 15 * 60, "the scenario fits its fifteen minutes: " + r.Probe.Elapsed);
            Assert.IsTrue(r.Probe.Elapsed > 60, "the real scenario is not a handful of frames: " + r.Probe.Elapsed);
            Dictionary<string, string> rows = Emitted(r);
            foreach (SoloView v in SoloProbe.Views)
            {
                foreach (string check in new[] { "type_found_ok", "pose_ok", "background_clear_ok", "isolated_ok", "picture_ok", "share_ok" })
                    Assert.AreEqual("1", rows[SoloProbe.Prefix + v.Name + "_" + check], v.Name + " " + check);
                Assert.AreEqual(!double.IsNaN(v.SpanMin), rows.ContainsKey(SoloProbe.Prefix + v.Name + "_span_ok"), v.Name + " span is judged where the view states it");
            }
            Assert.IsTrue(r.Log.Any(l => l.StartsWith("solo: cb_near: baseline ")), "the log names the two pictures of a view");
        }

        // Every view states its bands, and they leave room above the noise floor and below the whole frame.
        [TestMethod]
        public void EveryViewStatesABandThatCanFailAtBothEnds()
        {
            foreach (SoloView v in SoloProbe.Views)
            {
                Assert.IsTrue(v.ShareMin > SoloProbe.NoiseFloorShare, v.Name + ": the lower bound is above the noise floor");
                Assert.IsTrue(v.ShareMax <= 1.0 && v.ShareMax > v.ShareMin, v.Name + ": the band is a band");
                Assert.IsTrue(v.Strength > 0f && v.Strength <= 1f, v.Name + ": strength");
                if (v.Cell) Assert.IsTrue(v.CellAheadKm >= CloudViewer.SoloCellMinKm && v.CellAheadKm <= CloudViewer.SoloCellMaxKm, v.Name + ": the cell stands where the control allows");
                Assert.IsFalse(string.IsNullOrEmpty(v.Reference), v.Name + ": the reference section is named");
            }
            Assert.AreEqual(SoloProbe.Views.Length, SoloProbe.Views.Select(v => v.Name).Distinct().Count(), "names are distinct");
        }

        [TestMethod]
        public void TheRowsFileListsExactlyTheMetricsTheProbeEmits()
        {
            Run r = Start();
            Drive(r);
            Assert.IsTrue(r.Probe.Passed, Dump(r));
            string root = RepoRoot.Root("tests", "series", "rows", "SOLOPROBE.json");
            var file = (Dictionary<string, object>)MiniJson.Parse(File.ReadAllText(Path.Combine(root, "tests", "series", "rows", "SOLOPROBE.json")));
            Assert.AreEqual("SOLOPROBE", (string)file["package"]);
            var listed = new Dictionary<string, Dictionary<string, object>>();
            foreach (object o in (List<object>)file["rows"])
            {
                var row = (Dictionary<string, object>)o;
                Assert.AreEqual("K-S1", (string)row["series"], (string)row["metric"]);
                Assert.AreEqual("solo", (string)row["start"], (string)row["metric"]);
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
            string root = RepoRoot.Root("tests", "series", "K-S1.json");
            var file = (Dictionary<string, object>)MiniJson.Parse(File.ReadAllText(Path.Combine(root, "tests", "series", "K-S1.json")));
            Assert.AreEqual("K-S1", (string)file["name"]);
            var start = (Dictionary<string, object>)((List<object>)file["starts"])[0];
            Assert.AreEqual("solo", (string)start["id"]);
            Assert.IsTrue(((List<object>)start["probeArgs"]).Cast<string>().Contains("SoloProbe"), "the start names its probe");
            var section = (Dictionary<string, object>)((List<object>)file["sections"])[0];
            Assert.AreEqual("SOLOPROBE", (string)section["package"]);
        }

        // --- One fault for each thing the probe judges ---

        [TestMethod]
        public void AnotherLayerThatStaysDrawnFailsTheIsolation()
        {
            Run r = Start(rig => rig.OtherLayerDrawn = true);
            Drive(r);
            AssertFailsWith(r, "sc_isolated");
            Assert.IsTrue(Emitted(r).ContainsKey(SoloProbe.Prefix + "nimbostratus_share_ok"), "the start goes on and returns the other rows");
        }

        [TestMethod]
        public void ACellThatIsNotInViewFailsItsShareAndItsReach()
        {
            Run r = Start(rig => rig.NothingInView = true);
            Drive(r);
            AssertFailsWith(r, "cb_near_share");
            Assert.IsTrue(r.Probe.Failures.Any(f => f.StartsWith(SoloProbe.Prefix + "cb_near_span")), "the reach fails too: " + r.Joined);
            Assert.IsFalse(r.Probe.Failures.Any(f => f.StartsWith(SoloProbe.Prefix + "sc_share")), "a uniform field is still in view: " + r.Joined);
        }

        [TestMethod]
        public void AFieldThatFillsTheWholeFrameFailsTheUpperBound()
        {
            Run r = Start(rig => rig.FillsFrame = true);
            Drive(r);
            AssertFailsWith(r, "humilis_share");
        }

        [TestMethod]
        public void ASunThatKeepsMovingFailsTheFixedSun()
        {
            Run r = Start(rig => rig.SunDrifts = true);
            Drive(r);
            AssertFailsWith(r, "sun_fixed");
        }

        [TestMethod]
        public void RainThatStaysForcedFailsTheIsolationAndTheClearBackground()
        {
            Run r = Start(rig => rig.RainStaysOn = true);
            Drive(r);
            AssertFailsWith(r, "cb_near_background_clear");
            Assert.IsTrue(r.Probe.Failures.Any(f => f.StartsWith(SoloProbe.Prefix + "cb_near_isolated")), "the isolation fails too: " + r.Joined);
        }

        [TestMethod]
        public void APassThatDrawsEveryLayerNotOnlyTheSoloOneFailsTheIsolation()
        {
            Run r = Start(rig => rig.MaskNotSet = true);
            Drive(r);
            AssertFailsWith(r, "cb_near_isolated");
        }

        [TestMethod]
        public void ACameraThatDoesNotLookWhereTheViewSaysFailsThePose()
        {
            Run r = Start(rig => rig.PoseLifted = true);
            Drive(r);
            AssertFailsWith(r, "sc_pose");
        }

        // K-S1 start 1: the views were placed at heading 90 and the camera looked 180 degrees round, into the setting sun.
        [TestMethod]
        public void ACameraThatLooksIntoTheSunFailsThePoseAndTheSunAngleOfEveryView()
        {
            Run r = Start(rig => rig.AimLooksAway = true);
            Drive(r);
            AssertFailsWith(r, "cb_near_sun_angle");
            Assert.IsTrue(r.Probe.Failures.Any(f => f.StartsWith(SoloProbe.Prefix + "sc_pose")), "the heading is judged too: " + r.Joined);
            Assert.IsTrue(r.Probe.Failures.Any(f => f.StartsWith(SoloProbe.Prefix + "nimbostratus_sun_angle")), "every view is judged: " + r.Joined);
        }

        [TestMethod]
        public void EveryViewIsPlacedWithTheSunBehindTheCameraAtTheMeasuredAzimuth()
        {
            Run r = Start(rig => rig.SunAzimuth = 250.0);
            Drive(r);
            Assert.IsTrue(r.Probe.Passed, Dump(r));
            Dictionary<string, string> rows = Emitted(r);
            Assert.AreEqual(250.0, double.Parse(rows[SoloProbe.Prefix + "sun_azimuth_deg"], CultureInfo.InvariantCulture), 1e-6);
            foreach (SoloView v in SoloProbe.Views)
            {
                Assert.IsFalse(v.Backlit, v.Name + " is a sunlit view");
                Assert.AreEqual(70.0, v.HeadingFor(250.0), 1e-9, v.Name + ": the sun's azimuth plus 180");
                double angle = double.Parse(rows[SoloProbe.Prefix + v.Name + "_sun_angle_deg"], CultureInfo.InvariantCulture);
                Assert.IsTrue(angle > SoloProbe.SunBehindMinDeg, v.Name + ": " + angle);
            }
        }

        [TestMethod]
        public void ASunWhoseAzimuthCannotBeReadStopsTheStartByName()
        {
            Run r = Start(rig => rig.SunAzimuth = double.NaN);
            Drive(r);
            AssertFailsWith(r, "sun_azimuth");
            Assert.IsFalse(Emitted(r).ContainsKey(SoloProbe.Prefix + "cb_near_pose_ok"), "no view is shot into an unknown sun");
        }

        [TestMethod]
        public void AViewsNoiseFloorLiftsTheLowerBoundOfItsShare()
        {
            Assert.AreEqual(0.03, SoloProbe.EffectiveShareMin(0.03, 0.005), 1e-12, "a floor near nothing leaves the band's bound");
            Assert.AreEqual(0.105, SoloProbe.EffectiveShareMin(0.03, 0.035), 1e-12, "the 0.035 of the start of 2026-11-15 would have lifted the cirrus bound over it");
            Assert.AreEqual(0.03, SoloProbe.EffectiveShareMin(0.03, double.NaN), 1e-12, "an unread floor leaves the band's bound");
            Run r = Start();
            Drive(r);
            Assert.IsTrue(r.Probe.Passed, Dump(r));
            foreach (SoloView v in SoloProbe.Views)
                Assert.IsTrue(Emitted(r).ContainsKey(SoloProbe.Prefix + v.Name + "_noise_floor_share"), v.Name + " has its own floor");
        }

        [TestMethod]
        public void TheSunAngleRuleSeparatesSunlitFromBacklitViews()
        {
            Assert.IsTrue(SoloProbe.SunAngleSuits(false, 75.0));
            Assert.IsFalse(SoloProbe.SunAngleSuits(false, 60.0), "within 60 degrees of the sun is not a sunlit view");
            Assert.IsFalse(SoloProbe.SunAngleSuits(false, 3.0));
            Assert.IsTrue(SoloProbe.SunAngleSuits(true, 3.0), "a named backlit view looks at the sun");
            Assert.IsFalse(SoloProbe.SunAngleSuits(true, 120.0));
            Assert.IsFalse(SoloProbe.SunAngleSuits(false, double.NaN), "an unread angle never passes");
            Assert.IsFalse(SoloProbe.SunAngleSuits(true, double.NaN));
            var backlit = new SoloView { HeadingFromSunDeg = 0.0 };
            Assert.IsTrue(backlit.Backlit);
            Assert.AreEqual(-170.0, backlit.HeadingFor(190.0), 1e-9, "wrapped to -180..180");
        }

        [TestMethod]
        public void TheMeasureLeavesOutTheGroundBelowTheHorizonOfTheMeasuredPose()
        {
            Run r = Start();
            Drive(r);
            Assert.IsTrue(r.Probe.Passed, Dump(r));
            // cb_far: the camera 5 degrees up over a horizon about 1.8 degrees down: the horizon row is 0.40 from the bottom, the last measure was given it plus the margin;
            // the last view is a deck from 90 m at 45 degrees: the horizon is far below the frame.
            Assert.AreEqual(0.0, r.Rig.LastSkyFromV, 1e-9, "no ground in the last view's frame");
            double row = PictureMask.HorizonRow(5.0, 60.0, 300.0, 600000.0);
            Assert.AreEqual(0.397, row, 0.005);
            Assert.IsTrue(PictureMask.HorizonRow(45.0, 60.0, 90.0, 600000.0) < 0.0, "a view 45 degrees up has no horizon");
        }

        [TestMethod]
        public void ABaselineThatDiffersFromItselfFailsTheNoiseFloor()
        {
            Run r = Start(rig => rig.NoisyBaseline = true);
            Drive(r);
            AssertFailsWith(r, "noise_floor");
        }

        [TestMethod]
        public void ATypeThatIsNotInTheStackFailsItsViewAndTheOthersRun()
        {
            Run r = Start(rig => rig.TypeMissing = true);
            Drive(r);
            AssertFailsWith(r, "mediocris_type_found");
            Assert.IsTrue(r.Probe.Failures.Any(f => f.StartsWith(SoloProbe.Prefix + "views_all_run")), "the count of views run fails: " + r.Joined);
            Assert.IsTrue(Emitted(r).ContainsKey(SoloProbe.Prefix + "congestus_share_ok"), "the other views run");
        }
    }
}
