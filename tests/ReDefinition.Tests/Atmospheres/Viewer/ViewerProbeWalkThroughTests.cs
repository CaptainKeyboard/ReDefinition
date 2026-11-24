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
    // The whole K-V1 scenario of ViewerProbe, from "flight and pass ready" to the verdict, against a model of the game (FakeViewerRig): the order of the steps, every row the start
    // emits (and that tests/series/rows/VPROBE.json lists exactly these), and one fault test for each effect of the viewer the probe judges: the probe has to fail with the check
    // that names it, never with an unhandled exception. What it cannot cover: the real window and its controls, the vessel's physics and hold, the pass's frame order, the files
    // the game writes (the model's are in memory), and whether the thresholds fit the real game; the start review (.local/work/viewer-start-review.md) lists what the start
    // alone can show.
    // Harness: walks a probe (not shipped) through a fake rig, not the product.
    [TestClass, TestCategory("Harness")]
    public class ViewerProbeWalkThroughTests
    {
        private const double Dt = 0.05;

        private sealed class Run
        {
            internal FakeViewerRig Rig;
            internal ViewerProbe Probe;
            internal readonly List<string> Log = new List<string>();
            internal readonly List<string> Rows = new List<string>();
            internal int Quits;
            internal string Joined { get { return string.Join("; ", Probe.Failures.ToArray()); } }
        }

        private Action<string> savedLog;
        private Action<string> savedSink;
        private Action<string> savedSay;

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

        private static Run Start(Action<FakeViewerRig> fault = null, Action<FakeWorkbenchRig> workbenchFault = null)
        {
            var r = new Run { Rig = new FakeViewerRig() };
            if (fault != null) fault(r.Rig);
            var workbench = new FakeWorkbenchRig(r.Rig);
            if (workbenchFault != null) workbenchFault(workbench);
            SeriesProbe.LogLine = m => r.Log.Add(m);
            AtmoResult.Sink = r.Rows.Add;
            r.Probe = new ViewerProbe { Rig = r.Rig, Workbench = workbench, CommandLineOverride = new[] { "KSP_x64.exe", "-atmoSeries", "K-V1", "-atmoStart", "viewer", "-atmoProbe", "ViewerProbe" } };
            r.Probe.VesselUnpackedReader = () => true;
            r.Probe.VesselCountReader = () => new VesselCount { Valid = true, Parts = 64, Vessels = 1, Name = "Kerbal X" };
            r.Probe.QuitGame = () => r.Quits++;
            r.Probe.Init();
            return r;
        }

        private static void Drive(Run r, double maxS = 900.0)
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
            Assert.IsTrue(r.Probe.Failures.Any(f => f.StartsWith(ViewerProbe.Prefix + check + " ") || f == ViewerProbe.Prefix + check), "the failure names " + check + ": " + r.Joined);
            Assert.IsTrue(r.Log.Any(l => l.StartsWith("Probe: FAILED")), "the verdict line was written");
            Assert.IsTrue(r.Log.Any(l => l.StartsWith("viewer: ") && l.EndsWith(" - FAILED")), "a 'viewer:' line says what was expected and what was read");
            Assert.AreEqual(1, r.Quits, "the game was told to quit once");
        }

        // --- The healthy start ---

        [TestMethod]
        public void AHealthyViewerStartPassesEveryCheckInOrder()
        {
            Run r = Start();
            Drive(r);
            Assert.IsTrue(r.Probe.Passed, Dump(r));
            Assert.AreEqual(1, r.Quits);
            Assert.AreEqual("viewer", r.Probe.StartId);
            Assert.AreEqual("Probe: PASSED", r.Log[r.Log.Count - 1]);
            Assert.IsTrue(r.Probe.Elapsed < 10 * 60, "the scenario fits its ten minutes: " + r.Probe.Elapsed);
            Assert.IsTrue(r.Probe.Elapsed > 30, "the real scenario is not a handful of frames: " + r.Probe.Elapsed);

            // The order of the checks of the log is the order of the plan: window, pose and altitude, time, weather, A/B, speed, capture, close.
            string[] order =
            {
                "closed_at_start", "addon_running", "window_opens", "keys_listed", "window_closes", "window_reopens", "pose_height", "window_screenshot", "slider_height", "typed_height",
                "time_offset", "sun_moves", "time_freeze", "time_resumes", "weather_overcast_forced", "weather_fair_cover", "weather_cumulus_cover", "weather_rainlowstratiform_precipitation",
                "weather_field_forced", "ab_b_pass", "ab_flip_a_pass", "ab_flip_b_pass", "speed_x1_flows", "speed_x4_ratio", "speed_frozen", "speed_resumes", "capture_hidden", "capture_files",
                "capture_sidecar", "capture_second", "close_ends_session", "restore_options", "restore_time", "restore_vessel", "pass_kept_drawing"
            };
            int last = -1;
            foreach (string name in order)
            {
                int at = r.Rows.FindIndex(last + 1, l => l.Contains("\"metric\":\"" + ViewerProbe.Prefix + name + "_ok\""));
                Assert.IsTrue(at > last, name + " is emitted after the one before it\n" + Dump(r));
                last = at;
            }
            Dictionary<string, string> rows = Emitted(r);
            foreach (KeyValuePair<string, string> pair in rows.Where(p => p.Key.EndsWith("_ok", StringComparison.Ordinal)))
                Assert.AreEqual("1", pair.Value, pair.Key + " is ok in the healthy start");
            // Every check logs what was expected and what was read.
            Assert.IsTrue(r.Log.Count(l => l.StartsWith("viewer: ") && l.EndsWith(" - ok")) >= 40);
            Assert.IsFalse(r.Log.Any(l => l.EndsWith(" - FAILED")));
        }

        // The rows file lists exactly what the probe writes: no check without a row, no row the start does not fill.
        [TestMethod]
        public void TheRowsFileListsExactlyTheMetricsTheProbeEmits()
        {
            Run r = Start();
            Drive(r);
            Assert.IsTrue(r.Probe.Passed, Dump(r));
            string root = RepoRoot.Root("tests", "series", "rows", "VPROBE.json");
            var file = (Dictionary<string, object>)MiniJson.Parse(File.ReadAllText(Path.Combine(root, "tests", "series", "rows", "VPROBE.json")));
            Assert.AreEqual("VPROBE", (string)file["package"]);
            var listed = new Dictionary<string, Dictionary<string, object>>();
            foreach (object o in (List<object>)file["rows"])
            {
                var row = (Dictionary<string, object>)o;
                Assert.AreEqual("K-V1", (string)row["series"], (string)row["metric"]);
                // The rows of the second start ("wide", ViewerWindowProbe) are judged by ViewerWindowTests.
                if ((string)row["start"] == "wide") continue;
                Assert.AreEqual("viewer", (string)row["start"], (string)row["metric"]);
                Assert.IsFalse(listed.ContainsKey((string)row["metric"]), "row twice: " + (string)row["metric"]);
                listed[(string)row["metric"]] = row;
            }
            Dictionary<string, string> emitted = Emitted(r);
            CollectionAssert.AreEquivalent(emitted.Keys.ToList(), listed.Keys.ToList(), "rows file against the emitted metrics");
            foreach (KeyValuePair<string, Dictionary<string, object>> pair in listed)
            {
                if (pair.Key.EndsWith("_ok", StringComparison.Ordinal))
                {
                    Assert.AreEqual(1.0, Convert.ToDouble(pair.Value["equals"], CultureInfo.InvariantCulture), pair.Key + " must equal 1");
                    // The drift row is written only where the layers have wind to measure, the Overcast and Fair cover rows only where the field's own weather lets them tell; every
                    // other check is required.
                    bool optional = pair.Value.ContainsKey("required") && (bool)pair.Value["required"] == false;
                    bool mayBeMissing = pair.Key == "VIEWER.speed_drift_ratio_ok" || pair.Key == "VIEWER.weather_overcast_cover_ok" || pair.Key == "VIEWER.weather_fair_cover_ok"
                                       || pair.Key == "VIEWER.weather_rainlowstratiform_cover_ok";
                    Assert.AreEqual(mayBeMissing, optional, pair.Key + (optional ? " is optional" : " is required: it is a check"));
                }
                else Assert.IsTrue(pair.Value.ContainsKey("required") && (bool)pair.Value["required"] == false, pair.Key + " is information (required false)");
            }
        }

        // --- One fault for each effect the probe judges ---

        [TestMethod]
        public void AWindowThatDoesNotOpenFailsTheStartAtOnce()
        {
            Run r = Start(rig => rig.WindowNeverOpens = true);
            Drive(r);
            AssertFailsWith(r, "window_opens");
            Assert.IsTrue(r.Probe.Failures.Any(f => f.Contains("session did not start")));
            Assert.IsFalse(Emitted(r).ContainsKey(ViewerProbe.Prefix + "slider_height_ok"), "nothing further is checked without a session");
        }

        [TestMethod]
        public void AnAltitudeSliderThatPlacesTheVesselWrongFails()
        {
            Run r = Start(rig => rig.SliderPlacesWrong = true);
            Drive(r);
            AssertFailsWith(r, "slider_height");
            Assert.IsTrue(Emitted(r).ContainsKey(ViewerProbe.Prefix + "typed_height_ok"), "the start goes on and returns the other rows");
        }

        [TestMethod]
        public void ACloudSpeedThatIsNotAppliedToTheNoiseClockFails()
        {
            Run r = Start(rig => rig.SpeedIgnored = true);
            Drive(r);
            AssertFailsWith(r, "speed_x4_ratio");
            Assert.IsTrue(r.Probe.Failures.Any(f => f.StartsWith(ViewerProbe.Prefix + "speed_frozen")), "a frozen cloud that still flows fails too: " + r.Joined);
        }

        [TestMethod]
        public void AFlipThatChangesNothingFailsTheABRows()
        {
            Run r = Start(rig => rig.FlipDoesNothing = true);
            Drive(r);
            AssertFailsWith(r, "ab_flip_a_pass");
        }

        [TestMethod]
        public void AReadCheckThatNeverSeesThePassFailsTheABRows()
        {
            Run r = Start(rig => rig.ReadNotSeen = true);
            Drive(r);
            AssertFailsWith(r, "ab_b_read_check");
        }

        [TestMethod]
        public void AFrozenClockThatKeepsRunningFails()
        {
            Run r = Start(rig => rig.FreezeIgnored = true);
            Drive(r);
            AssertFailsWith(r, "time_freeze");
        }

        [TestMethod]
        public void ATimeOffsetThatMovesNothingFails()
        {
            Run r = Start(rig => rig.TimeOffsetIgnored = true);
            Drive(r);
            AssertFailsWith(r, "time_offset");
        }

        [TestMethod]
        public void ASunThatDoesNotFollowTheClockFails()
        {
            Run r = Start(rig => rig.SunStandsStill = true);
            Drive(r);
            AssertFailsWith(r, "sun_moves");
        }

        [TestMethod]
        public void AForcedWeatherThePassDoesNotReadFails()
        {
            Run r = Start(rig => rig.WeatherNotForced = true);
            Drive(r);
            AssertFailsWith(r, "weather_overcast_forced");
        }

        // The defect the cloud viewer had: the combination of the layer maps to the field's cover scaled a forced Overcast down.
        [TestMethod]
        public void AForcedWeatherThatTheCombinationScalesDownFailsOnTheCover()
        {
            Run r = Start(rig => rig.CombineStaysOn = true);
            Drive(r);
            AssertFailsWith(r, "weather_overcast_forced");
            Assert.IsTrue(r.Probe.Failures.Any(f => f.StartsWith(ViewerProbe.Prefix + "weather_overcast_cover")), "the cover stays under the forced value: " + r.Joined);
        }

        [TestMethod]
        public void AWindowThatStaysInThePictureFails()
        {
            Run r = Start(rig => rig.WindowStaysVisible = true);
            Drive(r);
            AssertFailsWith(r, "capture_hidden");
        }

        [TestMethod]
        public void APictureThatIsNotWrittenFails()
        {
            Run r = Start(rig => rig.NothingWritten = true);
            Drive(r);
            AssertFailsWith(r, "capture_files");
        }

        [TestMethod]
        public void ASidecarThatNamesTheWrongWeatherFails()
        {
            Run r = Start(rig => rig.SidecarWrongWeather = true);
            Drive(r);
            AssertFailsWith(r, "capture_sidecar");
        }

        [TestMethod]
        public void ASecondPictureThatWritesOverTheFirstFails()
        {
            Run r = Start(rig => rig.NoSecondName = true);
            Drive(r);
            AssertFailsWith(r, "capture_second");
        }

        [TestMethod]
        public void AViewerThatKeepsItsOptionsOnCloseFailsTheRestoreRow()
        {
            Run r = Start(rig => rig.CloseKeepsOptions = true);
            Drive(r);
            AssertFailsWith(r, "restore_options");
        }

        // --- The checks the plausibility review made able to fail ---

        // The old bound (the camera within its own distance of the vessel's height) held whatever the distance was.
        [TestMethod]
        public void ACameraLeftBehindByAnAltitudeChangeFailsCameraFollows()
        {
            Run r = Start(rig => rig.CameraDoesNotFollow = true);
            Drive(r);
            AssertFailsWith(r, "camera_follows");
        }

        [TestMethod]
        public void AnAltitudeChangeThatPutsThePosesAimBackFailsKeepAim()
        {
            Run r = Start(rig => rig.SliderResetsAim = true);
            Drive(r);
            AssertFailsWith(r, "slider_keeps_aim");
        }

        [TestMethod]
        public void AClockTheViewerLeavesWrongAtTheCloseFailsRestoreTimeAgainstTheStartsUt()
        {
            Run r = Start(rig => rig.ClockLeftWrongAtClose = true);
            Drive(r);
            AssertFailsWith(r, "restore_time");
            Assert.IsTrue(r.Log.Any(l => l.StartsWith("viewer: the game's clock is back") && l.Contains("UT before the viewer opened")), "the line names the outside reference");
        }

        [TestMethod]
        public void ACloseThatLeavesTheTimeOffsetAndTheFreezeInPlaceFailsRestoreTime()
        {
            Run r = Start(rig => rig.CloseKeepsTimeOffset = true);
            Drive(r);
            AssertFailsWith(r, "restore_time");
        }

        // The defect of the K-V1 start of 2026-11-15: the game ran at 0.38 of the real time (a low frame rate), the viewer's clock was right and the real-time reference failed it.
        [TestMethod]
        public void AGameThatRunsSlowerThanTheRealTimeStillPassesEveryTimeCheck()
        {
            Run r = Start(rig => rig.SlowGame = true);
            Drive(r);
            Assert.IsTrue(r.Probe.Passed, Dump(r));
            Assert.IsTrue(r.Log.Any(l => l.StartsWith("viewer: the game's clock is back") && l.EndsWith(" - ok") && l.Contains("physics time since")), "restore_time judged by the physics time\n" + Dump(r));
        }

        [TestMethod]
        public void ASlowGameDoesNotHideAClockTheViewerLeavesWrong()
        {
            Run r = Start(rig => { rig.SlowGame = true; rig.ClockLeftWrongAtClose = true; });
            Drive(r);
            AssertFailsWith(r, "restore_time");
        }

        // The reference of the time rows is the physics time; where the game's clock does not follow it the probe says so instead of judging against a wrong reference.
        [TestMethod]
        public void AGameClockThatDoesNotFollowThePhysicsTimeFailsTheReferenceCheck()
        {
            Run r = Start(rig => rig.ClockDiverges = true);
            Drive(r);
            AssertFailsWith(r, "natural_tracks_physics");
        }

        [TestMethod]
        public void ACameraThatLooksTheOtherWayFromThePosesHeadingFailsThePoseAzimuth()
        {
            Run r = Start(rig => rig.AimLooksAway = true);
            Drive(r);
            AssertFailsWith(r, "pose_aim_azimuth");
            Assert.IsTrue(r.Log.Any(l => l.StartsWith("viewer: the camera's heading: expected 90") && l.Contains("read -90")), Dump(r));
        }

        [TestMethod]
        public void AWindowCutOffAtTheScreensEdgeFailsWindowInsideScreen()
        {
            Run r = Start(rig => rig.WindowCutOffLeft = true);
            Drive(r);
            AssertFailsWith(r, "window_inside_screen");
        }

        [TestMethod]
        public void AWindowThatCannotBeMeasuredFailsWindowInsideScreenInsteadOfPassing()
        {
            Run r = Start(rig => rig.WindowNotMeasurable = true);
            Drive(r);
            AssertFailsWith(r, "window_inside_screen");
            Assert.IsTrue(r.Log.Any(l => l.Contains("could not be measured")), Dump(r));
        }

        [TestMethod]
        public void ALowStratiformLayerAlreadyClosedAtThePadMakesTheOvercastCheckUnclearNotPassedOrFailed()
        {
            Run r = Start(rig => rig.FieldOvercast = true);
            Drive(r);
            Assert.IsTrue(r.Probe.Passed, Dump(r));
            Dictionary<string, string> rows = Emitted(r);
            Assert.IsFalse(rows.ContainsKey(ViewerProbe.Prefix + "weather_overcast_cover_ok"), "no verdict row where the check cannot tell");
            Assert.AreEqual(1.0, double.Parse(rows[ViewerProbe.Prefix + "weather_overcast_unclear"], System.Globalization.CultureInfo.InvariantCulture));
            Assert.AreEqual(0.0, double.Parse(rows[ViewerProbe.Prefix + "weather_fair_unclear"], System.Globalization.CultureInfo.InvariantCulture));
            Assert.IsTrue(r.Log.Any(l => l.Contains("weather_overcast_cover") && l.Contains("UNCLEAR")) || r.Log.Any(l => l.Contains("Overcast") && l.Contains(" - UNCLEAR (")), "the line says UNCLEAR\n" + Dump(r));
        }

        [TestMethod]
        public void AFairCoverThatRainKeepsUpIsUnclearWhereItRainsAndFailedWhereItDoesNot()
        {
            Run raining = Start(rig => rig.RainAtPad = true);
            Drive(raining);
            Assert.IsTrue(raining.Probe.Passed, Dump(raining));
            Dictionary<string, string> rows = Emitted(raining);
            Assert.IsFalse(rows.ContainsKey(ViewerProbe.Prefix + "weather_fair_cover_ok"));
            Assert.AreEqual(1.0, double.Parse(rows[ViewerProbe.Prefix + "weather_fair_unclear"], System.Globalization.CultureInfo.InvariantCulture));
            Assert.AreEqual(0.0, double.Parse(rows[ViewerProbe.Prefix + "weather_overcast_unclear"], System.Globalization.CultureInfo.InvariantCulture));
            Assert.IsTrue(raining.Log.Any(l => l.Contains("UNCLEAR (it rains at the pad")), Dump(raining));
            Assert.IsTrue(raining.Log.Any(l => l.StartsWith("Probe: warning weather_fair_cover")), "an unclear check is a warning in the report: " + Dump(raining));

            Run dry = Start(rig => { rig.RainAtPad = true; rig.RainUnreadable = true; });
            Drive(dry);
            AssertFailsWith(dry, "weather_fair_cover");
        }

        // --- The workbench section (VIEW3): one fault for each effect it judges ---

        [TestMethod]
        public void AKeyThePassDoesNotReadFailsTheWorkbenchGroupCheck()
        {
            Run r = Start(null, wb => wb.KeysNotRead = true);
            Drive(r);
            AssertFailsWith(r, "wb_keys_read");
            Assert.IsTrue(Emitted(r).ContainsKey(ViewerProbe.Prefix + "wb_no_save_written_ok"), "the section goes on and returns the other rows");
        }

        [TestMethod]
        public void AKeyReadOnceASecondIsWaitedForAndOneThatIsNeverReadStillFails()
        {
            Run once = Start(null, wb => wb.SecondKeySample = true);
            Drive(once);
            Assert.IsTrue(once.Probe.Passed, Dump(once));
            Assert.IsTrue(once.Log.Any(l => l.Contains("cloud.afterScatterer (once a second)")), "the log names the key as read once a second\n" + Dump(once));
            Run never = Start(null, wb => { wb.SecondKeySample = true; wb.SecondKeyNeverRead = true; });
            Drive(never);
            AssertFailsWith(never, "wb_keys_read");
        }

        [TestMethod]
        public void ASlowPhysicsClockStillGivesTheRatioAndAWindowThatStaysShortIsNamed()
        {
            // 0.27 physics seconds per real second: the 3 s window holds 0.8 s, as in the start of 2026-11-16 (0.82 s at 104 ms frames).
            Run slow = Start(rig => rig.GameSpeed = 0.27);
            Drive(slow);
            Assert.IsTrue(slow.Probe.Passed, Dump(slow));
            Assert.AreEqual("1", Emitted(slow)[ViewerProbe.Prefix + "natural_tracks_physics_ok"]);
            double ratio = double.Parse(Emitted(slow)[ViewerProbe.Prefix + "natural_per_physics"], System.Globalization.CultureInfo.InvariantCulture);
            Assert.AreEqual(1.0, ratio, 0.02, "the clock follows the physics time");
            Run diverges = Start(rig => { rig.GameSpeed = 0.27; rig.ClockDiverges = true; });
            Drive(diverges);
            AssertFailsWith(diverges, "natural_tracks_physics");
        }

        [TestMethod]
        public void AReadoutThatWaitsForTheFirstCellListIsWaitedFor()
        {
            Run late = Start(null, wb => wb.ReadoutDelayFrames = 90);
            Drive(late);
            Assert.IsTrue(late.Probe.Passed, Dump(late));
            Run never = Start(null, wb => wb.NoReadout = true);
            Drive(never, 1500.0);
            AssertFailsWith(never, "wb_readout");
        }

        [TestMethod]
        public void TheIdleFrameTimeIsMeasuredWithTheViewerClosedAndNamesTheMedian()
        {
            Run r = Start();
            Drive(r);
            Dictionary<string, string> rows = Emitted(r);
            Assert.AreEqual("1", rows[ViewerProbe.Prefix + "wb_idle_frame_time_ok"]);
            double median = double.Parse(rows[ViewerProbe.Prefix + "wb_idle_frame_ms_median"], System.Globalization.CultureInfo.InvariantCulture);
            Assert.AreEqual(Dt * 1000.0, median, 1.0, "the model's frame is Dt long");
            int idle = r.Rows.FindIndex(l => l.Contains("wb_idle_frame_time_ok")), opens = r.Rows.FindIndex(l => l.Contains("\"VIEWER.window_opens_ok\""));
            Assert.IsTrue(idle >= 0 && idle < opens, "measured before the window opens");
        }

        [TestMethod]
        public void ThePercentileTakesTheNearestRankAndNaNForNone()
        {
            Assert.IsTrue(double.IsNaN(ViewerProbe.Percentile(new double[0], 0.5)));
            Assert.AreEqual(3.0, ViewerProbe.Percentile(new[] { 5.0, 1.0, 3.0, 2.0, 4.0 }, 0.5));
            Assert.AreEqual(5.0, ViewerProbe.Percentile(new[] { 5.0, 1.0, 3.0, 2.0, 4.0 }, 0.95));
            Assert.AreEqual(1.0, ViewerProbe.Percentile(new[] { 5.0, 1.0 }, 0.0));
        }

        [TestMethod]
        public void TheRegistryMarksTheOnceASecondKeyAndTheSampleChoiceFollowsTheLiveness()
        {
            Assert.AreEqual(KeyLiveness.Second, CloudKeyRegistry.Find(OwnCloudModule.TuneAfterScatterer).Liveness);
            Assert.IsTrue(GameWorkbenchRig.SampleRank(KeyLiveness.Frame) < GameWorkbenchRig.SampleRank(KeyLiveness.Second));
            Assert.IsTrue(GameWorkbenchRig.SampleRank(KeyLiveness.Second) < GameWorkbenchRig.SampleRank(KeyLiveness.Change));
            Assert.IsTrue(GameWorkbenchRig.SampleRank(KeyLiveness.Change) < GameWorkbenchRig.SampleRank(KeyLiveness.Volume));
            Assert.AreEqual(GameWorkbenchRig.SampleRank(KeyLiveness.Frame), GameWorkbenchRig.SampleRank(KeyLiveness.FrameHistory));
        }

        [TestMethod]
        public void AModeThatDoesNotActLiveFailsTheWorkbench()
        {
            Run r = Start(null, wb => wb.OwnCloudsIgnored = true);
            Drive(r);
            AssertFailsWith(r, "wb_mode_live");
        }

        [TestMethod]
        public void ARebuildThatNeverFinishesFailsAndOneThatFreezesTheMainThreadFails()
        {
            Run never = Start(null, wb => wb.RebuildNeverDone = true);
            Drive(never, 1500.0);
            AssertFailsWith(never, "wb_rebuild_done");
            Run slow = Start(null, wb => wb.RebuildOverBudget = true);
            Drive(slow);
            AssertFailsWith(slow, "wb_rebuild_budget");
        }

        [TestMethod]
        public void ASeedThatDoesNotChangeTheWeatherAndAWeatherThatIsNotAFunctionOfTheSeedBothFail()
        {
            Run ignored = Start(null, wb => wb.SeedIgnored = true);
            Drive(ignored);
            AssertFailsWith(ignored, "wb_seed_changes_weather");
            Run impure = Start(null, wb => wb.SeedNotPure = true);
            Drive(impure);
            AssertFailsWith(impure, "wb_seed_same_weather");
        }

        [TestMethod]
        public void AReadoutThatIsEmptyAPresetThatLosesAValueAndATodayThatKeepsOverridesFail()
        {
            Run noReadout = Start(null, wb => wb.NoReadout = true);
            Drive(noReadout);
            AssertFailsWith(noReadout, "wb_readout");
            Run lossy = Start(null, wb => wb.PresetLossy = true);
            Drive(lossy);
            AssertFailsWith(lossy, "wb_preset_round_trip");
            Run today = Start(null, wb => wb.TodayKeepsOverrides = true);
            Drive(today);
            AssertFailsWith(today, "wb_today_clears");
        }

        [TestMethod]
        public void AFlipThatDoesNothingFailsTheCompareAndAWriteToTheSaveFailsTheNoSaveCheck()
        {
            Run flip = Start(null, wb => wb.FlipDoesNothing = true);
            Drive(flip);
            AssertFailsWith(flip, "wb_ab_compare");
            Run write = Start(null, wb => wb.WritesSave = true);
            Drive(write);
            AssertFailsWith(write, "wb_no_save_written");
        }

        // --- The sidecar's reader ---

        [TestMethod]
        public void TheSidecarReaderTakesTheSceneLinesAndTheFirstOfAKey()
        {
            string text = ViewerSidecar.Format(new CaptureRecord { Picture = "p.png", Body = "Kerbin", Pose = "ground", Weather = "Fair", Ut = 12.5, Side = 'A', CloudSpeed = 4f, AltitudeAglM = 321.0 },
                                               new SettingDef[0], k => 0.0, k => "", new CloudKey[0], new FakeViewerRig.Store());
            Dictionary<string, string> map = ViewerProbe.ParseSidecar(text);
            Assert.AreEqual("p.png", map["picture"]);
            Assert.AreEqual("Fair", map["weather"]);
            Assert.AreEqual("4", map["cloudSpeed"]);
            Assert.AreEqual("A", map["abSide"]);
            Assert.AreEqual("321.0", map["altitudeAgl"]);
            Assert.AreEqual("12.5", map["ut"]);
            Assert.AreEqual(0, ViewerProbe.ParseSidecar(null).Count);
        }
    }
}
