using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Core;

namespace ReDefinition.Tests.Atmospheres.T09
{
    // The whole K-T2 scenario of T09Probe, from "flight reached" to the verdict, against a fake world (T09FakeWorld.cs): the scene, the vessel, the flight
    // camera, the weather field, the window, the clock and the own pass's counters are fakes, and time is the test's (a frame is dt seconds, the 16 minute
    // scenario takes a few hundred milliseconds). Every step runs in the order of the real start: the clock, the placement with its read-back retries,
    // the aim, the clock jump, the dwell of each view, the variant switches, the gain checks and the verdict. The fault tests inject each fault the
    // starts 35 to 40 hit, and the probe has to fail with a named reason, never with an unhandled exception ("exception in the scenario").
    // What it cannot cover: the real flight camera and vessel physics, the GPU's timings and the profiler's labels (tools/weather_metrics/kt2.py reads those
    // from the log; its own --selftest covers the parsing), the game's load, the order of the game's own frame callbacks and the real field's state.
    [TestClass]
    public partial class T09WalkThroughTests
    {
        private static readonly string[] AllOwn = { "off", "plain", "recon4", "entry", "edge", "heightoff", "cap2", "cache", "gain" };
        private static int frameBase = 100;

        private sealed class Run
        {
            internal FakeWorld World;
            internal FakeField Field;
            internal T09Probe Probe;
            internal readonly List<string> Log = new List<string>();
            // The game's frame at which each log line was written (same index as Log).
            internal readonly List<int> LogFrames = new List<int>();
            internal readonly List<string> Rows = new List<string>();
            internal int Quits;
            internal string Verdict;

            internal string Joined { get { return string.Join("; ", Probe.Failures.ToArray()); } }
        }

        private IWeatherField savedField;
        private Func<bool> savedReady;
        private OwnCloudModule savedModule;

        [TestInitialize]
        public void Capture()
        {
            savedField = WeatherHub.Field;
            savedReady = SeriesProbe.FlightReadyState;
            savedModule = OwnCloudModule.Current;
            SeriesProbe.ForgetRunningProbes();
            AtmospheresSettings.ResetToDefaults();
            Tuning.ClearOverrides();
        }

        [TestCleanup]
        public void Release()
        {
            WeatherHub.Field = savedField;
            SeriesProbe.FlightReadyState = savedReady;
            SeriesProbe.LogLine = message => AtmospheresLog.Say(message);
            AtmospheresLog.SayLine = line => UnityEngine.Debug.Log(line);
            AtmoResult.Sink = line => UnityEngine.Debug.Log(line);
            SetCurrent(savedModule);
            AtmospheresSettings.ResetToDefaults();
            Tuning.ClearOverrides();
            Tuning.Instance.BeginFrame(frameBase += 10);
            SeriesProbe.ForgetRunningProbes();
        }

        private static void SetCurrent(OwnCloudModule module)
        {
            typeof(OwnCloudModule).GetProperty("Current", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public).SetValue(null, module, null);
        }

        // Cover by the hour of the day (a Kerbin day has 6): the candidate hours 0, 1, 3 and 4 are covered; with the sun's elevation 0, 56, 0, -56 the first that
        // fits is hour 1 of the day, so the clock step has to pass hours that are covered but out of the sun band.
        private static float CoverByHour(double ut)
        {
            int n = (int)Math.Round(ut / 3600.0);
            int h = ((n % 6) + 6) % 6;
            return h == 2 || h == 5 ? 0.2f : 0.8f;
        }

        private Run Start(string[] variants, Action<Run> tweak = null, string width = "1707", string startId = "own1707", string series = "K-T2")
        {
            var r = new Run();
            r.World = new FakeWorld();
            r.World.Frames = frameBase;
            frameBase += 100000;
            r.Field = new FakeField { RealNow = () => r.World.RealS, Cover = CoverByHour };
            r.Field.ReadyAtS = 0;
            // The hub holds the NullField until the generator's gate (GeneratorModule.Tick: CloudGates.GeneratorInstalled) installs the field; Drive runs the gate.
            WeatherHub.Field = null;
            SeriesProbe.LogLine = m => { r.Log.Add(m); r.LogFrames.Add(r.World.Frames); };
            AtmospheresLog.SayLine = line =>
            {
                r.Log.Add(line.StartsWith(AtmospheresLog.Prefix, StringComparison.Ordinal) ? line.Substring(AtmospheresLog.Prefix.Length) : line);
                r.LogFrames.Add(r.World.Frames);
            };
            AtmoResult.Sink = r.Rows.Add;
            SeriesProbe.FlightReadyState = () => r.World.FlightReady;
            var module = new OwnCloudModule();
            module.Init();
            r.World.Module = module;
            var args = new List<string> { "KSP_x64.exe", "-atmoSeries", series, "-atmoStart", startId, "-screen-width", width, "-screen-height", "960", "-atmoProbe", "T09Probe" };
            if (variants != null)
            {
                args.Add("-atmoVariants");
                args.Add(string.Join(",", variants));
            }
            r.Probe = new T09Probe { Game = r.World, CommandLineOverride = args.ToArray() };
            r.Probe.QuitGame = () => r.Quits++;
            if (tweak != null) tweak(r);
            r.Probe.Init();
            return r;
        }

        // Frames until the verdict (and the quit three seconds later), dt seconds each, at most maxS real seconds.
        private static void Drive(Run r, double dt = 0.1, double maxS = 3600.0, Action<Run> perFrame = null)
        {
            while (r.World.RealS < maxS && !(r.Probe.Finished && r.Quits > 0))
            {
                // GeneratorModule.Tick: the field of the generator is the hub's only while the gate is open, the NullField otherwise.
                WeatherHub.Field = CloudGates.GeneratorInstalled(AtmospheresSettings.Number) ? r.Field : null;
                r.World.Advance(dt);
                r.Probe.Tick((float)dt);
                if (perFrame != null) perFrame(r);
                if (r.Probe.Finished && r.Verdict == null) r.Verdict = r.Log.LastOrDefault(l => l.StartsWith("Probe: PASSED") || l.StartsWith("Probe: FAILED"));
            }
        }

        private static readonly Regex RowPattern = new Regex("\"metric\":\"(?<m>[^\"]+)\",\"value\":(?<v>\"(?:[^\"\\\\]|\\\\.)*\"|[^,}]+)(,\"variant\":\"(?<var>[^\"]+)\")?");

        private static List<KeyValuePair<string, string>> Values(Run r, string metric, string variant = null)
        {
            var found = new List<KeyValuePair<string, string>>();
            foreach (string line in r.Rows)
            {
                Match m = RowPattern.Match(line);
                if (!m.Success || m.Groups["m"].Value != metric) continue;
                string v = m.Groups["var"].Success ? m.Groups["var"].Value : null;
                if (variant != null && v != variant) continue;
                found.Add(new KeyValuePair<string, string>(v, m.Groups["v"].Value.Trim('"')));
            }
            return found;
        }

        private static double Number(Run r, string metric, string variant = null)
        {
            var v = Values(r, metric, variant);
            Assert.IsTrue(v.Count > 0, "row " + metric + (variant != null ? " (" + variant + ")" : "") + " was emitted\n" + Dump(r));
            return double.Parse(v[v.Count - 1].Value, CultureInfo.InvariantCulture);
        }

        private static string Dump(Run r)
        {
            return "--- failures: " + r.Joined + "\n--- log tail:\n" + string.Join("\n", r.Log.Skip(Math.Max(0, r.Log.Count - 25)).ToArray());
        }

        private static void AssertNoUnhandled(Run r)
        {
            Assert.IsFalse(r.Probe.Failures.Any(f => f.StartsWith("exception in the scenario")), "no unhandled exception: " + r.Joined);
            Assert.IsFalse(r.Probe.Failures.Any(f => f.StartsWith("the scenario did not end")), "the scenario ended on its own: " + r.Joined);
        }

        // The probe failed with a reason that contains the text.
        private static void AssertNamedFailure(Run r, string text)
        {
            Assert.IsTrue(r.Probe.Finished, "the probe ended\n" + Dump(r));
            Assert.IsFalse(r.Probe.Passed, "the probe failed");
            AssertNoUnhandled(r);
            Assert.IsTrue(r.Probe.Failures.Any(f => f.Contains(text)), "the failure names '" + text + "': " + r.Joined);
            Assert.IsTrue(r.Log.Any(l => l.StartsWith("Probe: FAILED")), "the verdict line was written");
            Assert.AreEqual(1, r.Quits, "the game was told to quit once");
        }

        // --- The healthy starts ---------------------------------------------------------------------------------------------------------

        [TestMethod]
        public void AHealthyOwnStartRunsEveryStepInOrderAndPasses()
        {
            Run r = Start(null);
            Drive(r);
            Assert.IsTrue(r.Probe.Passed, Dump(r));
            Assert.AreEqual(1, r.Quits);
            Assert.AreEqual("own1707", r.Probe.StartId);

            // The order of the log: begin, the variants one after another, the verdict.
            int at = r.Log.FindIndex(l => l.StartsWith("Probe: series K-T2 start own1707 begins with variants off,plain,recon4,entry,edge,heightoff,cap2,cache,gain"));
            Assert.IsTrue(at >= 0, "the begin line");
            int last = at;
            foreach (string v in AllOwn)
            {
                int i = r.Log.FindIndex(last + 1, l => l == "Probe: variant " + v + " on");
                Assert.IsTrue(i > last, "variant " + v + " on, after the one before\n" + Dump(r));
                last = i;
            }
            Assert.AreEqual("Probe: PASSED", r.Log[r.Log.Count - 1]);

            // The clock: the first hour that is covered and in the sun band, five hours on from the base.
            double ut = Number(r, "T09.ut");
            Assert.AreEqual(0.0, ut % 3600.0, 1e-6, "an hour boundary");
            Assert.AreEqual(1000800.0 + 5 * 3600.0, ut, 1e-6);
            Assert.IsTrue(Number(r, "T09.cover") >= 0.5);
            double sun = Number(r, "T09.sun_elev_deg");
            Assert.IsTrue(sun >= 15 && sun <= 60, "sun " + sun);
            Assert.AreEqual(ut - (1000000.0 + 123.0 + 55 + 12), Number(r, "T09.ut_jump_s"), 60.0, "the jump is the distance from the start");

            // The window and the display.
            Assert.AreEqual("1707x960 (asked 1707)", Values(r, "T09.screen")[0].Value);
            Assert.AreEqual(0.0, Number(r, "T09.vsync_count"));
            Assert.AreEqual(-1.0, Number(r, "T09.target_frame_rate"));

            // Views: 4 variants x 5 views + 4 inside-only variants, each with its begin and end line and its rows.
            int begins = r.Log.Count(l => l.StartsWith("T09 view begin: ")), ends = r.Log.Count(l => l.StartsWith("T09 view end: "));
            Assert.AreEqual(24, begins);
            Assert.AreEqual(24, ends);
            foreach (string v in new[] { "off", "plain", "recon4", "entry" })
            {
                foreach (T09Probe.View view in T09Probe.Views)
                {
                    Assert.IsTrue(r.Log.Contains("T09 view begin: " + view.Name + " " + v + " 1707x960"), view.Name + " " + v + " begins");
                    Assert.IsTrue(r.Log.Contains("T09 view end: " + view.Name + " " + v + " 1707x960"), view.Name + " " + v + " ends");
                    string p = "T09.view." + view.Name + ".";
                    double fps = Number(r, p + "fps", v);
                    Assert.IsTrue(fps > 5 && fps < 20, "fps " + fps);
                    Assert.IsTrue(Number(r, p + "frames", v) > 250, "frames in 30 s");
                    Assert.AreEqual("1707x960", Values(r, p + "render_size", v)[0].Value);
                    Assert.IsTrue(Values(r, p + "altitude_m", v).Count > 0);
                    double altitude = Number(r, p + "altitude_m", v);
                    Assert.AreEqual(70.0 + view.AglM, altitude, 1.0, view.Name + " altitude");
                    double headingError = Math.Abs(((Number(r, p + "azimuth_deg", v) - view.HeadingDeg + 540.0) % 360.0) - 180.0);
                    Assert.IsTrue(headingError < 15.0, view.Name + " heading error " + headingError);
                    if (v == "off") Assert.AreEqual(0.0, Number(r, p + "frames_drawn", v));
                    else
                    {
                        Assert.IsTrue(Number(r, p + "frames_drawn", v) >= 60, "drawn");
                        Assert.AreEqual(v == "recon4" ? "reconstruct" : "plain", Values(r, p + "mode", v)[0].Value);
                        Assert.IsTrue(Values(r, p + "cover_at_view_height", v).Count > 0, "cover at view height");
                    }
                }
            }
            foreach (string v in new[] { "edge", "heightoff", "cap2", "cache" })
            {
                Assert.IsTrue(r.Log.Contains("T09 view begin: inside " + v + " 1707x960"), v);
                Assert.IsFalse(r.Log.Any(l => l.StartsWith("T09 view begin: ground " + v)), v + " is an inside-only variant");
            }
            Assert.AreEqual(1.0, Number(r, "T09.view.inside.light_cache_on", "cache"));
            Assert.AreEqual(0.0, Number(r, "T09.view.inside.light_cache_on", "plain"));
            Assert.AreEqual("reconstruct", Values(r, "T09.view.inside.mode", "edge")[0].Value);

            // The module's totals after every own variant, and the gains.
            foreach (string v in new[] { "plain", "recon4", "entry", "edge", "heightoff", "cap2", "cache", "gain" })
            {
                Assert.IsTrue(Number(r, "T09.height_build_worst_ms", v) > 0 && Number(r, "T09.height_build_worst_ms", v) <= 1500, v);
                Assert.IsTrue(Number(r, "T09.height_build_main_ms", v) <= 2, v);
                Assert.IsTrue(Number(r, "T09.height_upload_worst_ms", v) <= 3, v);
                Assert.IsTrue(Number(r, "T09.map_fetch_worst_ms", v) <= 8, v);
                Assert.IsTrue(Number(r, "T09.map_fetch_main_ms", v) <= 2, v);
                Assert.IsTrue(Number(r, "T09.buffer_attaches", v) <= 6, v);
            }
            foreach (string v in new[] { "plain", "recon4", "entry", "edge", "heightoff", "cap2", "cache" })
            {
                Assert.IsTrue(Number(r, "T09.view.frames_drawn", v) >= 60, v);
                Assert.IsTrue(Number(r, "T09.view.frames_ratio", v) >= 0.95, v);
            }
            Assert.AreEqual(2.0, Number(r, "T09.gain.sun_ratio", "gain"), 0.1);
            Assert.AreEqual(2.0, Number(r, "T09.gain.moon_ratio", "gain"), 0.1);
            Assert.AreEqual(1.0, Number(r, "T09.moon.provider_answers", "gain"));

            // The time it takes: about the 16 minutes the plan names, inside the 19 minute limit of the probe.
            Assert.IsTrue(r.Probe.Elapsed > 12 * 60 && r.Probe.Elapsed < 18.5 * 60, "scenario time " + r.Probe.Elapsed.ToString("0") + " s");
            Assert.IsFalse(double.IsNaN(r.World.HoldStartedAtS), "the vessel was held while it was placed");
            Assert.IsFalse(r.World.Held, "the hold was released at the end");
        }

        [TestMethod]
        public void EveryProbeOwnedRowOfTheSeriesFileIsEmittedByAHealthyStart()
        {
            Run r = Start(null);
            Drive(r);
            Assert.IsTrue(r.Probe.Passed, Dump(r));
            string root = RepoRoot.Root("tests", "series", "rows", "T09.json");
            string rows = File.ReadAllText(Path.Combine(root, "tests", "series", "rows", "T09.json"));
            foreach (string metric in new[] { "T09.ut", "T09.cover", "T09.sun_elev_deg", "T09.view.frames_drawn", "T09.view.frames_ratio", "T09.height_build_worst_ms",
                                              "T09.height_build_main_ms", "T09.map_fetch_main_ms", "T09.height_upload_worst_ms", "T09.map_fetch_worst_ms", "T09.buffer_attaches", "T09.gain.sun_ratio", "T09.gain.moon_ratio", "T09.moon.provider_answers" })
            {
                Assert.IsTrue(rows.Contains("\"metric\": \"" + metric + "\""), metric + " is a row of the series file");
                Assert.IsTrue(Values(r, metric).Count > 0, metric + " is emitted by the probe");
            }
        }

        [TestMethod]
        public void AHealthyTvcStartDwellsInTheFiveViewsAndPasses()
        {
            Run r = Start(new[] { "tvc" });
            Drive(r);
            Assert.IsTrue(r.Probe.Passed, Dump(r));
            Assert.AreEqual(1.0, Number(r, "T09.tvc.scatterer_loaded"));
            Assert.AreEqual(1.0, Number(r, "T09.tvc.eve_available"));
            Assert.AreEqual(5.0, Number(r, "T09.tvc.layers_bound"));
            Assert.AreEqual(5, r.Log.Count(l => l.StartsWith("T09 view begin: ") && l.Contains(" tvc ")));
            Assert.IsTrue(r.Probe.Elapsed > 3.5 * 60 && r.Probe.Elapsed < 6.5 * 60, "tvc start time " + r.Probe.Elapsed.ToString("0"));
        }

        [TestMethod]
        public void AGameInTheWarpOrPausedKeepsTheClockStepAndThePlacementWorking()
        {
            Run warp = Start(new[] { "plain" }, r => r.World.TimeScale = 4.0);
            Drive(warp);
            Assert.IsTrue(warp.Probe.Passed, Dump(warp));
            Assert.AreEqual(0.0, Number(warp, "T09.ut") % 3600.0, 1e-6);

            Run paused = Start(new[] { "plain" }, r => r.World.TimeScale = 0.0);
            Drive(paused);
            Assert.IsTrue(paused.Probe.Passed, Dump(paused));
        }

        [TestMethod]
        public void ABodyWithoutAnAtmosphereSkipsTheScenarioAndPasses()
        {
            Run r = Start(null, x => x.World.Atmosphere = false);
            Drive(r);
            Assert.IsTrue(r.Probe.Passed, Dump(r));
            Assert.AreEqual(1.0, Number(r, "T09.skipped_no_atmosphere"));
            Assert.AreEqual(0, r.Log.Count(l => l.StartsWith("T09 view begin")));
        }

        // --- Faults of the first contact ------------------------------------------------------------------------------------------------

        [TestMethod]
        public void TheGameInTheMenuForFiveMinutesIsAWaitAndTheFlightAfterItRuns()
        {
            Run r = Start(new[] { "plain" }, x => x.World.FlightReadyAtS = 290.0);
            Drive(r);
            Assert.IsTrue(r.Probe.Passed, Dump(r));
            Assert.IsTrue(r.Log.FindIndex(l => l.StartsWith("T09 view begin")) > 0);
        }

        [TestMethod]
        public void AFlightThatNeverComesIsANamedTimeout()
        {
            Run r = Start(new[] { "plain" }, x => x.World.FlightReadyAtS = double.PositiveInfinity);
            Drive(r);
            AssertNamedFailure(r, "timeout after 330 s waiting for the flight scene with an unpacked vessel");
            Assert.AreEqual(0, r.Rows.Count, "nothing was measured");
        }

        [TestMethod]
        public void LeavingTheFlightOrLosingTheVesselMidScenarioIsNamed()
        {
            Run left = Start(new[] { "plain" }, x => x.World.LeaveFlightAtS = 200.0);
            Drive(left);
            AssertNamedFailure(left, "the flight scene was left during the scenario (scene SPACECENTER)");

            Run gone = Start(new[] { "plain" }, x => x.World.VesselGoneAtS = 200.0);
            Drive(gone);
            AssertNamedFailure(gone, "the active vessel or the flight camera is gone during the scenario");
        }

        [TestMethod]
        public void NoFlightCameraAtTheStartIsAWaitThatEndsInANamedTimeout()
        {
            Run r = Start(new[] { "plain" }, x => x.World.CameraPresent = false);
            Drive(r);
            AssertNamedFailure(r, "waiting for the flight scene with an unpacked vessel");
        }

        // --- Faults of the weather field and the clock ----------------------------------------------------------------------------------

        [TestMethod]
        public void AFieldThatIsReadyWithoutAMapForHalfAMinuteIsWaitedFor()
        {
            // Start 40: the field reported Ready, every sample answered false, the clock step gave up after 48 false answers.
            Run r = Start(new[] { "plain" }, x => x.Field.ReadyAtS = 55.0 + 40.0);
            Drive(r);
            Assert.IsTrue(r.Probe.Passed, Dump(r));
            Assert.IsTrue(r.Field.FalseAnswers >= 48, "the first pass was all false");
            Assert.IsTrue(r.Log.Any(l => l.Contains("T09 clock: the field answered after")), "the wait is logged");
            Assert.AreEqual(1000800.0 + 5 * 3600.0, Number(r, "T09.ut"), 1e-6);
        }

        [TestMethod]
        public void AFieldThatNeverAnswersEndsAfterTheLimitWithTheFieldNamed()
        {
            Run r = Start(new[] { "plain" }, x => x.Field.ReadyAtS = double.PositiveInfinity);
            Drive(r);
            AssertNamedFailure(r, "the weather field did not answer at the site for 48 of 48 candidate times within 120 s (field FakeField, readiness Ready");
            double waited = r.World.RealS - 55.0;
            Assert.IsTrue(waited > 120.0 + 12.0 && waited < 120.0 + 30.0, "waited " + waited.ToString("0") + " s after the flight");
            Assert.IsTrue(r.Log.Any(l => l.Contains("T09 clock: no candidate (48 not sampled")));
            Assert.AreEqual(0, r.Log.Count(l => l.StartsWith("T09 view begin")));
        }

        [TestMethod]
        public void AFieldThatAnswersForSomeTimesOnlyIsScannedAgainAndStillFindsTheHour()
        {
            // Hours 0 and 1 of the day (the candidates that fit) are unanswered for the first 30 s after the flight, the others answer at once.
            Run r = Start(new[] { "plain" }, x =>
            {
                x.Field.ReadyAtS = 0;
                x.Field.AnswersAt = ut => x.World.RealS > 55.0 + 12.0 + 30.0 || (((int)Math.Round(ut / 3600.0)) % 6) != 1;
            });
            Drive(r);
            Assert.IsTrue(r.Probe.Passed, Dump(r));
            // The field is the hub's from the frame after the probe's switches (the generator's gate), so the pass that sees it answer depends on that frame: the
            // hour chosen is the first of the candidates the field answered for, which is hour 1 of a day (the only one that fits) in every case.
            double chosen = Number(r, "T09.ut");
            Assert.AreEqual(1, (int)Math.Round(chosen / 3600.0) % 6, "hour 1 of the day, ut " + chosen);
            Assert.AreEqual(0.0, chosen % 3600.0, 1e-6);
            Assert.IsTrue(r.Log.Any(l => l.Contains("T09 clock: the field answered after")));
        }

        [TestMethod]
        public void AFieldThatAnswersForHalfTheTimesAndNeverHasTheRightHourFailsNamedAfterTheLimit()
        {
            Run r = Start(new[] { "plain" }, x => x.Field.AnswersAt = ut => (((int)Math.Round(ut / 3600.0)) % 6) != 1);
            Drive(r);
            AssertNamedFailure(r, "the weather field did not answer at the site for 8 of 48 candidate times within 120 s");
        }

        [TestMethod]
        public void AClearSkyAllDayFailsNamedWithoutWaiting()
        {
            Run r = Start(new[] { "plain" }, x => x.Field.Cover = ut => 0.1f);
            Drive(r);
            AssertNamedFailure(r, "no universal time in 48 hours from the base has a cover of at least 0.5");
            Assert.IsTrue(r.World.RealS < 55.0 + 12.0 + 20.0, "no waiting: " + r.World.RealS.ToString("0") + " s");
            Assert.IsTrue(r.Log.Any(l => l.Contains("T09 clock: no candidate (0 not sampled, 0 covered but not in the sun band")));
        }

        [TestMethod]
        public void CoverThatNeverMeetsTheSunBandFailsNamedWithTheCount()
        {
            Run r = Start(new[] { "plain" }, x => x.World.Sun = ut => 5.0);
            Drive(r);
            AssertNamedFailure(r, "(32 had the cover)");
        }

        // --- Faults of the window and the display ---------------------------------------------------------------------------------------

        [TestMethod]
        public void AWindowOfAnotherSizeFailsNamedBeforeAnyViewRuns()
        {
            Run r = Start(new[] { "plain" }, x => { x.World.Width = 2560; x.World.Height = 1440; });
            Drive(r);
            AssertNamedFailure(r, "the window is 2560x1440 and the start asked for a width of 1707");
            Assert.AreEqual("2560x1440 (asked 1707)", Values(r, "T09.screen")[0].Value);
            Assert.AreEqual(0, r.Log.Count(l => l.StartsWith("T09 view begin")));
        }

        [TestMethod]
        public void AFrameCapTheGameSetsAgainFailsNamedInTheStartAndInEveryView()
        {
            Run r = Start(new[] { "plain" }, x => x.World.CapSticks = true);
            Drive(r);
            AssertNamedFailure(r, "no frame cap is set (targetFrameRate 60)");
            Assert.IsTrue(r.Probe.Failures.Any(f => f.StartsWith("no frame cap in the ground view (plain)")), r.Joined);
            Assert.AreEqual(60.0, Number(r, "T09.target_frame_rate"));
        }

        [TestMethod]
        public void VerticalSyncTheGameSetsAgainFailsNamed()
        {
            Run r = Start(new[] { "plain" }, x => x.World.VsyncSticks = true);
            Drive(r);
            AssertNamedFailure(r, "vertical sync is off (vSyncCount 1)");
        }

        [TestMethod]
        public void ARenderSizeThatIsNotTheWindowSizeFailsNamedInEveryView()
        {
            Run r = Start(new[] { "plain" }, x => { x.World.PassRenderWidth = 1138; x.World.PassRenderHeight = 640; });
            Drive(r);
            AssertNamedFailure(r, "the scene renders at the window size in the ground view (plain) (render 1138x640, window 1707x960");
            Assert.AreEqual("1138x640", Values(r, "T09.view.ground.render_size", "plain")[0].Value);
        }

        // --- Faults of the placement and the aim ----------------------------------------------------------------------------------------

        [TestMethod]
        public void AVesselThatArrivesLateIsPlacedAgainAndTheViewIsMeasured()
        {
            Run r = Start(new[] { "plain" }, x => x.World.PlaceLagS = call => call < 2 ? 15.0 : 0.0);
            Drive(r);
            Assert.IsTrue(r.Probe.Passed, Dump(r));
            Assert.IsTrue(r.Log.Any(l => l.StartsWith("T09 view placed: ground attempt 1 of 4") && l.EndsWith("(not yet)")), "attempt 1 read the old height");
            Assert.IsTrue(r.Log.Any(l => l.StartsWith("T09 view placed: ground attempt 3 of 4") && l.EndsWith("(ok)")), "attempt 3 held");
            Assert.IsTrue(r.Log.Any(l => l.StartsWith("T09 view begin: ground plain")));
        }

        [TestMethod]
        public void AVesselThatIsNeverAtTheTargetFailsNamedAndTheViewIsLeftOut()
        {
            Run r = Start(new[] { "plain" }, x => x.World.PlaceResult = (call, wanted) => x.World.Altitude);
            Drive(r);
            AssertNamedFailure(r, "T09.pose_placed_ground_plain (altitude 70 m against 270");
            Assert.AreEqual(0, r.Log.Count(l => l.StartsWith("T09 view begin")), "no view was measured at the wrong height");
            Assert.AreEqual(4, r.Log.Count(l => l.StartsWith("T09 view placed: ground attempt")));
        }

        [TestMethod]
        public void AVesselPlacedAtTheWrongHeightFailsNamedWithBothValues()
        {
            Run r = Start(new[] { "plain" }, x => x.World.PlaceResult = (call, wanted) => wanted + 500.0);
            Drive(r);
            AssertNamedFailure(r, "against 270");
            Assert.IsTrue(r.Probe.Failures.Any(f => f.StartsWith("T09.pose_placed_orbit_edge_plain") || f.StartsWith("T09.pose_placed_ground_plain")), r.Joined);
        }

        [TestMethod]
        public void ACameraThatDoesNotFollowFailsNamedWithTheAimItHas()
        {
            Run r = Start(new[] { "plain" }, x => x.World.CameraStuck = true);
            Drive(r);
            AssertNamedFailure(r, "T09.pose_placed_ground_plain");
            Assert.IsTrue(r.Probe.Failures.Any(f => f.Contains("camera elevation 0.0 against 18.0")), r.Joined);
        }

        // --- Faults of the pass and its frames ------------------------------------------------------------------------------------------

        [TestMethod]
        public void AViewWithFewerThanSixtyFramesFailsNamed()
        {
            // One frame a second: 30 frames in a view of 30 s, the steady stretch of 120 frames takes 121 s.
            Run r = Start(new[] { "plain" });
            Drive(r, 1.0);
            AssertNamedFailure(r, "the own pass drew in the ground view (plain) (30 of 30 frames");
        }

        [TestMethod]
        public void APassThatDrawsInOneFrameOfFourFailsTheRatioNamed()
        {
            Run r = Start(new[] { "plain" }, x => x.World.PassDrawsInFrame = f => f % 4 == 0);
            Drive(r);
            AssertNamedFailure(r, "the own pass drew in the ground view (plain)");
            Assert.IsTrue(Number(r, "T09.view.frames_ratio", "plain") < 0.5);
        }

        [TestMethod]
        public void APassThatNeverWarmsUpIsANamedTimeoutAndNothingIsMeasured()
        {
            Run frozen = Start(new[] { "plain" }, x => x.World.PassFrozen = true);
            Drive(frozen);
            AssertNamedFailure(frozen, "timeout after 150 s waiting for the own pass to draw a steady stretch (plain)");
            Assert.AreEqual(0, frozen.Log.Count(l => l.StartsWith("T09 view begin")));

            Run missing = Start(new[] { "plain" });
            SetCurrent(null);
            Drive(missing);
            AssertNamedFailure(missing, "timeout after 150 s waiting for the own pass to draw a steady stretch (plain)");
        }

        [TestMethod]
        public void AGainThatDoesNotReachThePassFailsNamed()
        {
            Run r = Start(new[] { "gain" }, x => x.World.GainIgnored = true);
            Drive(r);
            AssertNamedFailure(r, "cloud.sunGain 2 doubles the sun colour of the pass");
            Assert.AreEqual(1.0, Number(r, "T09.gain.sun_ratio", "gain"), 0.01);
        }

        [TestMethod]
        public void WithoutAMoonTheGainStepNotesItAndPasses()
        {
            Run r = Start(new[] { "gain" }, x => x.World.MoonAnswers = false);
            Drive(r);
            Assert.IsTrue(r.Probe.Passed, Dump(r));
            Assert.AreEqual(0.0, Number(r, "T09.moon.provider_answers", "gain"));
            Assert.IsTrue(Values(r, "T09.gain.moon_note").Count > 0);
            Assert.AreEqual(0, Values(r, "T09.gain.moon_ratio").Count);
        }

        // --- The pictures ---------------------------------------------------------------------------------------------------------------

        private static readonly string[] OwnWithAid = { "plain", "recon4", "entry" };
        private static readonly string[] InsideOnlyVariants = { "edge", "heightoff", "cap2", "cache" };

        // The [begin, end] frames of every measured window, from the probe's own lines.
        private static List<int[]> Windows(Run r)
        {
            var windows = new List<int[]>();
            for (int i = 0; i < r.Log.Count; i++)
            {
                if (!r.Log[i].StartsWith("T09 view begin: ")) continue;
                int end = r.Log.FindIndex(i + 1, l => l.StartsWith("T09 view end: "));
                Assert.IsTrue(end > i, "a view end follows the begin at " + i);
                windows.Add(new[] { r.LogFrames[i], r.LogFrames[end] });
            }
            return windows;
        }

        private static string Full(string view, string variant, string kind, bool half = false)
        {
            return "t09-own1707-" + view + "-" + variant + "-1707x960-" + kind + (half ? "-half" : "");
        }

        [TestMethod]
        public void AHealthyOwnStartTakesOneNaturalAndOneAidPicturePerViewAfterItsWindow()
        {
            Run r = Start(null);
            Drive(r);
            Assert.IsTrue(r.Probe.Passed, Dump(r));

            // 4 variants x 5 views natural, 3 own variants x 5 views aid, 4 inside-only variants natural and aid.
            Assert.AreEqual(20 + 15 + 8, r.World.Saved.Count, string.Join("\n", r.World.Saved.ToArray()));
            Assert.AreEqual(r.World.Saved.Count, r.World.Saved.Distinct().Count(), "names are unique");
            Assert.AreEqual(43.0, Number(r, "T09.pictures.saved"));
            Assert.AreEqual(0.0, Number(r, "T09.pictures.warnings"));
            Assert.IsTrue(Number(r, "T09.pictures.bytes") > 0);
            Assert.IsFalse(r.World.AidIsOn, "the aid view is off at the end");

            // Names and sizes: full size for the key views of off and plain, half size for the rest.
            Assert.IsTrue(r.World.Saved.Contains(Full("ground", "off", "natural")));
            Assert.IsTrue(r.World.Saved.Contains(Full("inside", "plain", "natural")));
            Assert.IsTrue(r.World.Saved.Contains(Full("above", "plain", "aid-opticaldepth")));
            Assert.IsTrue(r.World.Saved.Contains(Full("orbit_edge", "off", "natural", true)));
            Assert.IsTrue(r.World.Saved.Contains(Full("sky3", "plain", "natural", true)));
            Assert.IsTrue(r.World.Saved.Contains(Full("ground", "recon4", "aid-opticaldepth", true)));
            Assert.IsTrue(r.World.Saved.Contains(Full("inside", "cache", "aid-opticaldepth", true)));
            Assert.IsFalse(r.World.Saved.Any(n => n.Contains("-off-") && n.Contains("aid")), "the baseline has no aid view");
            for (int i = 0; i < r.World.Saved.Count; i++)
            {
                bool half = r.World.Saved[i].EndsWith("-half", StringComparison.Ordinal);
                Assert.AreEqual(half ? 2 : 1, r.World.SavedFactor[i], r.World.Saved[i]);
            }

            // The order inside one capture: the natural read and save, then the aid on, the read, the aid off, the save.
            int first = r.World.Events.IndexOf("save:" + Full("ground", "plain", "natural") + ":1");
            Assert.IsTrue(first > 1);
            Assert.AreEqual("capbegin,take", string.Join(",", r.World.Events.Skip(first - 2).Take(2).ToArray()));
            Assert.AreEqual("aidon,capbegin,take,aidoff,save:" + Full("ground", "plain", "aid-opticaldepth") + ":1", string.Join(",", r.World.Events.Skip(first + 1).Take(5).ToArray()));

            // Outside the measured windows: no picture event lies inside a window.
            List<int[]> windows = Windows(r);
            Assert.AreEqual(24, windows.Count);
            for (int i = 0; i < r.World.Stamps.Count; i++)
            {
                int s = r.World.Stamps[i];
                Assert.IsFalse(windows.Any(w => s > w[0] && s < w[1]), "event " + r.World.Events[i] + " at frame " + s + " is inside a measured window");
            }
            // The log: begin, end, capture begin, capture end, for every view of every variant, each pair naming the same view and variant.
            var kinds = r.Log.Where(l => l.StartsWith("T09 view begin: ") || l.StartsWith("T09 view end: ") || l.StartsWith("T09 capture begin: ") || l.StartsWith("T09 capture end: ")).ToList();
            Assert.AreEqual(24 * 4, kinds.Count);
            for (int i = 0; i < kinds.Count; i += 4)
            {
                string tag = kinds[i].Substring("T09 view begin: ".Length);
                Assert.AreEqual("T09 view end: " + tag, kinds[i + 1]);
                Assert.AreEqual("T09 capture begin: " + tag, kinds[i + 2]);
                Assert.AreEqual("T09 capture end: " + tag, kinds[i + 3]);
            }
            // The capture began after the window's end line, and the picture rows carry the cost.
            int endLine = r.Log.IndexOf("T09 view end: ground plain 1707x960"), capLine = r.Log.IndexOf("T09 capture begin: ground plain 1707x960");
            Assert.IsTrue(endLine >= 0 && capLine > endLine);
            Assert.IsTrue(r.LogFrames[capLine] >= r.LogFrames[endLine]);
            Assert.AreEqual(2.0, Number(r, "T09.view.ground.pictures", "plain"));
            Assert.AreEqual(1.0, Number(r, "T09.view.ground.pictures", "off"));
            Assert.IsTrue(Number(r, "T09.view.ground.capture_s", "plain") > 0.0);
            Assert.IsTrue(Number(r, "T09.view.ground.capture_bytes", "plain") > 0.0);
            Assert.IsTrue(r.Probe.Elapsed < 18.5 * 60, "scenario time with the pictures " + r.Probe.Elapsed.ToString("0") + " s");
            foreach (string v in OwnWithAid) Assert.IsTrue(r.Log.Any(l => l.StartsWith("Probe: picture " + Full("sky3", v, "aid-opticaldepth", true))), v);
            foreach (string v in InsideOnlyVariants) Assert.IsFalse(r.Log.Any(l => l.Contains("-ground-" + v + "-")), v + " has the inside view only");
        }

        [TestMethod]
        public void ATvcStartTakesTheNaturalPictureOfEachViewAndNoAid()
        {
            Run r = Start(new[] { "tvc" });
            Drive(r);
            Assert.IsTrue(r.Probe.Passed, Dump(r));
            Assert.AreEqual(5, r.World.Saved.Count);
            Assert.IsFalse(r.World.Events.Contains("aidon"));
            Assert.IsTrue(r.World.Saved.Contains(Full("ground", "tvc", "natural")));
            Assert.IsTrue(r.World.Saved.Contains(Full("orbit_edge", "tvc", "natural", true)));
            Assert.AreEqual(0.0, Number(r, "T09.pictures.warnings"));
        }

        [TestMethod]
        public void TheBaselineAloneTakesNaturalPicturesOnly()
        {
            Run r = Start(new[] { "off" });
            Drive(r);
            Assert.IsTrue(r.Probe.Passed, Dump(r));
            Assert.AreEqual(5, r.World.Saved.Count);
            Assert.AreEqual(0, r.World.Events.Count(e => e == "aidon"));
        }

        private static void AssertPicturesFailedButSeriesWentOn(Run r, string warning, int expectedViews)
        {
            Assert.IsTrue(r.Probe.Passed, "a fault of the pictures does not fail the series: " + Dump(r));
            AssertNoUnhandled(r);
            Assert.AreEqual(expectedViews, r.Log.Count(l => l.StartsWith("T09 view end: ")), "every view was still measured");
            Assert.IsTrue(r.Log.Any(l => l.StartsWith("Probe: warning " + warning + ": ")), "the warning " + warning + " is named\n" + Dump(r));
            Assert.IsTrue(Values(r, "T09.warning." + warning).Count > 0, "the warning is a row");
            Assert.IsFalse(r.World.AidIsOn, "the aid view is off");
        }

        [TestMethod]
        public void AReadThatThrowsOrFailsIsANamedWarningAndTheSeriesGoesOn()
        {
            foreach (string fault in new[] { "throw", "none", "busy", "busythrows", "takethrows", "nobegin" })
            {
                Run r = Start(new[] { "plain" }, x => x.World.ReadFault = n => fault);
                Drive(r);
                AssertPicturesFailedButSeriesWentOn(r, "capture_failed", 5);
                Assert.AreEqual(0, r.World.Saved.Count, fault);
                Assert.IsTrue(r.Log.Any(l => l.StartsWith("Probe: warning pictures_stopped: ")), fault + ": four in a row stop the pictures");
                Assert.AreEqual(4, r.Log.Count(l => l.StartsWith("Probe: warning capture_failed: ")), fault);
            }
        }

        [TestMethod]
        public void AReadThatFailsOnceIsSkippedAndTheNextPictureIsTaken()
        {
            Run r = Start(new[] { "plain" }, x => x.World.ReadFault = n => n == 3 ? "throw" : null);
            Drive(r);
            AssertPicturesFailedButSeriesWentOn(r, "capture_failed", 5);
            Assert.AreEqual(9, r.World.Saved.Count, "ten pictures, one failed");
            Assert.IsFalse(r.Log.Any(l => l.StartsWith("Probe: warning pictures_stopped")));
        }

        [TestMethod]
        public void ADiskThatIsFullIsANamedWarningAndStopsThePicturesAfterFourInARow()
        {
            Run r = Start(new[] { "plain", "recon4" }, x => x.World.SaveFault = n => "full");
            Drive(r);
            AssertPicturesFailedButSeriesWentOn(r, "picture_not_saved", 10);
            Assert.IsTrue(r.Log.Any(l => l.StartsWith("Probe: warning picture_not_saved: ") && l.Contains("not enough space")));
            Assert.AreEqual(4, r.World.SaveCalls, "the pictures stopped after four saves in a row");
            Assert.AreEqual(0, r.World.Saved.Count);
            Assert.IsTrue(r.Log.Any(l => l.StartsWith("Probe: warning pictures_stopped: ")));

            Run denied = Start(new[] { "plain" }, x => x.World.SaveFault = n => n < 2 ? "denied" : null);
            Drive(denied);
            AssertPicturesFailedButSeriesWentOn(denied, "picture_not_saved", 5);
            Assert.AreEqual(8, denied.World.Saved.Count, "two failed, the others were saved");
            Assert.IsFalse(denied.Log.Any(l => l.StartsWith("Probe: warning pictures_stopped")));
        }

        [TestMethod]
        public void AFolderThatCannotBeWrittenIsKnownBeforeTheFirstViewAndNoPictureIsTried()
        {
            Run r = Start(new[] { "plain" }, x => x.World.FolderProblem = "C:\\ksp\\series-pictures cannot be written");
            Drive(r);
            AssertPicturesFailedButSeriesWentOn(r, "pictures_folder", 5);
            Assert.AreEqual(0, r.World.ReadCalls);
            Assert.AreEqual(0, r.World.Events.Count);
            Assert.AreEqual(0, r.Log.Count(l => l.StartsWith("T09 capture begin")));
        }

        [TestMethod]
        public void AScenarioThatRunsLateStopsThePicturesBeforeTheLimitAndStillPasses()
        {
            // The flight comes 95 s late: the pictures of the last views would push the scenario over its 19 minute limit.
            // The fake's frames are 0.1 s, so its camera needs a hundred times the real frames to settle after every placement; a quick camera keeps the scenario's
            // length the one of the real game.
            Run r = Start(null, x => { x.World.FlightReadyAtS = 150.0; x.World.LagS = 0.03; });
            Drive(r);
            Assert.IsTrue(r.Probe.Passed, Dump(r));
            AssertNoUnhandled(r);
            Assert.IsTrue(r.Log.Any(l => l.StartsWith("Probe: warning pictures_time: ")), Dump(r));
            Assert.IsTrue(r.World.Saved.Count > 0 && r.World.Saved.Count < 43, "some pictures were taken, not all: " + r.World.Saved.Count);
            Assert.AreEqual(24, r.Log.Count(l => l.StartsWith("T09 view end: ")), "every view was measured");
        }

        [TestMethod]
        public void AModuleWithoutAnAidViewGivesTheNaturalPictureAndAWarning()
        {
            Run r = Start(null, x => x.World.AidAvailable = false);
            Drive(r);
            AssertPicturesFailedButSeriesWentOn(r, "no_aid_view", 24);
            Assert.AreEqual(20 + 4, r.World.Saved.Count, "natural pictures only");
            Assert.AreEqual(15 + 4, r.Log.Count(l => l.StartsWith("Probe: warning no_aid_view: ")));
            Assert.IsFalse(r.World.Saved.Any(n => n.Contains("aid")));
        }

        [TestMethod]
        public void AnAidModeThatDoesNothingIsNamedAndTheAidPictureIsStillKept()
        {
            Run r = Start(new[] { "plain" }, x => x.World.AidIgnored = true);
            Drive(r);
            AssertPicturesFailedButSeriesWentOn(r, "aid_equals_natural", 5);
            Assert.AreEqual(10, r.World.Saved.Count);
            Assert.AreEqual(5, r.Log.Count(l => l.StartsWith("Probe: warning aid_equals_natural: ")));
        }

        [TestMethod]
        public void TheAidViewIsOffAfterAnAidReadFailsAndEveryOnHasItsOff()
        {
            Run r = Start(new[] { "plain", "entry" }, x => x.World.ReadFault = n => n % 2 == 1 ? "takethrows" : null);
            Drive(r);
            Assert.IsTrue(r.Probe.Passed, Dump(r));
            Assert.IsFalse(r.World.AidIsOn);
            int on = r.World.Events.Count(e => e == "aidon"), off = r.World.Events.Count(e => e == "aidoff");
            Assert.AreEqual(on, off, "every aid switch-on has its switch-off");
        }

        // --- Faults of the TVC start ----------------------------------------------------------------------------------------------------

        [TestMethod]
        public void ATvcStartWithoutScattererOrEveFailsNamedAtOnce()
        {
            Run noScatterer = Start(new[] { "tvc" }, x => x.World.Scatterer = false);
            Drive(noScatterer);
            AssertNamedFailure(noScatterer, "the TVC variant needs EVE and Scatterer (Scatterer loaded False, EVE usable True)");

            Run noEve = Start(new[] { "tvc" }, x => x.World.Eve = false);
            Drive(noEve);
            AssertNamedFailure(noEve, "EVE usable False");
        }

        [TestMethod]
        public void ATvcLayerThatIsNeverBoundIsANamedTimeout()
        {
            Run r = Start(new[] { "tvc" }, x => x.World.EveBound = false);
            Drive(r);
            AssertNamedFailure(r, "timeout after 60 s waiting for EVE's layer to be bound to the maps of the field (TVC)");
        }

        [TestMethod]
        public void ATvcRendererThatFailsMidViewFailsTheViewCheckNamed()
        {
            Run r = Start(new[] { "tvc" }, x => x.World.EveFailures = 3);
            Drive(r);
            AssertNamedFailure(r, "TVC kept drawing in the ground view");
        }
    }
}
