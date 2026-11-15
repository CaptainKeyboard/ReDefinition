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
using ReDefinition.Tests.Atmospheres.T09;

namespace ReDefinition.Tests.Atmospheres.Viewer
{
    // The viewer's window kept inside the screen (WindowPlacement), the camera's aim tuned to the pose (ViewerAim, AimTuner), the options store's immediate reads, and the second start
    // of K-V1 (ViewerWindowProbe, the owner's 2560x1440).
    [TestClass]
    public class ViewerWindowTests
    {
        private static ScreenBox Box(float x0, float y0, float x1, float y1)
        {
            return new ScreenBox { XMin = x0, YMin = y0, XMax = x1, YMax = y1 };
        }

        // --- Where the window may stand ---

        [TestMethod]
        public void AWindowHalfBeyondTheLeftEdgeIsMovedRightToTheMargin()
        {
            float dx, dy;
            // The K-V1 picture: 1707x960, the window's left part beyond the screen.
            WindowPlacement.Shift(Box(-100f, 40f, 890f, 900f), 1707f, 960f, out dx, out dy);
            Assert.AreEqual(WindowPlacement.MarginPx + 100f, dx, 1e-4);
            Assert.AreEqual(0f, dy, 1e-4);
        }

        [TestMethod]
        public void AWindowInsideTheScreenIsLeftWhereItIs()
        {
            float dx, dy;
            WindowPlacement.Shift(Box(300f, 40f, 1200f, 900f), 1707f, 960f, out dx, out dy);
            Assert.AreEqual(0f, dx);
            Assert.AreEqual(0f, dy);
        }

        [TestMethod]
        public void AWindowPastTheRightAndTopEdgesIsMovedBackIn()
        {
            float dx, dy;
            WindowPlacement.Shift(Box(1000f, 200f, 1800f, 1000f), 1707f, 960f, out dx, out dy);
            Assert.AreEqual(1707f - WindowPlacement.MarginPx - 1800f, dx, 1e-4);
            Assert.AreEqual(960f - WindowPlacement.MarginPx - 1000f, dy, 1e-4);
        }

        [TestMethod]
        public void AWindowLargerThanTheScreenKeepsItsTitleAndLeftEdgeInView()
        {
            float dx, dy;
            WindowPlacement.Shift(Box(-50f, -300f, 1900f, 1100f), 1600f, 900f, out dx, out dy);
            Assert.AreEqual(WindowPlacement.MarginPx + 50f, dx, 1e-4, "left edge on the margin");
            Assert.AreEqual(900f - WindowPlacement.MarginPx - 1100f, dy, 1e-4, "top edge (the title) on the margin");
        }

        [TestMethod]
        public void AnUnmeasurableBoxIsNeverMoved()
        {
            float dx, dy;
            WindowPlacement.Shift(default(ScreenBox), 1600f, 900f, out dx, out dy);
            Assert.AreEqual(0f, dx);
            Assert.AreEqual(0f, dy);
            Assert.IsFalse(WindowPlacement.Inside(default(ScreenBox), 1600f, 900f), "a window that was not measured is not inside");
            Assert.IsFalse(WindowPlacement.Inside(Box(float.NaN, 0f, 10f, 10f), 1600f, 900f));
        }

        [TestMethod]
        public void InsideIsTheWholeBoxWithAPixelOfSlack()
        {
            Assert.IsTrue(WindowPlacement.Inside(Box(0f, 0f, 1707f, 960f), 1707f, 960f));
            Assert.IsTrue(WindowPlacement.Inside(Box(-0.5f, 8f, 1000f, 960.5f), 1707f, 960f));
            Assert.IsFalse(WindowPlacement.Inside(Box(-40f, 8f, 900f, 900f), 1707f, 960f), "cut off at the left (the K-V1 picture)");
            Assert.IsFalse(WindowPlacement.Inside(Box(8f, 8f, 1710f, 900f), 1707f, 960f));
        }

        // The screens and UI scales the viewer must stay on: a window of 660 canvas units wide and the list's height plus the dialog's other rows, shifted, ends inside the screen
        // whatever the scale and size; the list is shorter where the screen is, and scrolls.
        [TestMethod]
        public void TheWindowFitsAtEveryScreenAndUiScaleOfTheRange()
        {
            int[][] screens = { new[] { 1600, 900 }, new[] { 1707, 960 }, new[] { 1920, 1080 }, new[] { 2560, 1440 }, new[] { 3840, 2160 } };
            float[] scales = { 0.8f, 1f, 1.5f, 2f };
            foreach (int[] s in screens)
            {
                foreach (float scale in scales)
                {
                    float list = WindowPlacement.ListHeight(s[1], scale);
                    Assert.IsTrue(list >= WindowPlacement.MinListUnits && list <= WindowPlacement.MaxListUnits, s[0] + "x" + s[1] + " at " + scale + ": the list " + list);
                    float width = 660f * scale, height = (list + 80f) * scale;
                    float units = s[1] / scale;
                    // The height the window takes fits the screen wherever the screen is not shorter than the list's minimum plus the chrome (450 units at 1600x900 and scale 2).
                    if (units >= WindowPlacement.MinListUnits + 2f * WindowPlacement.ScreenFitMarginUnits + 80f)
                        Assert.IsTrue(height <= s[1] - 2f * WindowPlacement.MarginPx, s[0] + "x" + s[1] + " at " + scale + ": height " + height);
                    Assert.IsTrue(width <= s[0] - 2f * WindowPlacement.MarginPx, s[0] + "x" + s[1] + " at " + scale + ": width " + width);
                    // From any start the shift brings it inside.
                    foreach (float startX in new[] { -400f, 0f, s[0] * 0.75f, s[0] - 100f })
                    {
                        ScreenBox b = Box(startX, 30f, startX + width, 30f + height);
                        float dx, dy;
                        WindowPlacement.Shift(b, s[0], s[1], out dx, out dy);
                        b = Box(b.XMin + dx, b.YMin + dy, b.XMax + dx, b.YMax + dy);
                        Assert.IsTrue(WindowPlacement.Inside(b, s[0], s[1]), s[0] + "x" + s[1] + " at " + scale + " from x " + startX);
                    }
                }
            }
        }

        [TestMethod]
        public void TheListIsAsHighAsTheScreenAllowsAndNeverBelowItsMinimum()
        {
            Assert.AreEqual(WindowPlacement.MaxListUnits, WindowPlacement.ListHeight(2160f, 0.8f));
            Assert.AreEqual(WindowPlacement.MaxListUnits, WindowPlacement.ListHeight(1440f, 1.5f));
            Assert.AreEqual(470f, WindowPlacement.ListHeight(960f, 1.5f), 1e-3, "1707x960 at scale 1.5: 640 units less the margins and the chrome");
            Assert.AreEqual(280f, WindowPlacement.ListHeight(900f, 2f), 1e-3);
            Assert.AreEqual(WindowPlacement.MinListUnits, WindowPlacement.ListHeight(300f, 2f));
        }

        // --- The camera's aim ---

        [TestMethod]
        public void TheHeadingFirstGuessIsTheCompassHeadingAsTheGameReadItBack()
        {
            // K-V1 start 2026-11-15: camHdg 270 degrees read back as azimuth -90, camHdg 380 degrees as 20.
            Assert.AreEqual(-Math.PI / 2.0, ViewerAim.HeadingRad(270.0), 1e-5, "270 degrees is the same direction as -90");
            Assert.AreEqual(ViewerAim.HeadingRad(380.0), ViewerAim.HeadingRad(20.0), 1e-5);
            Assert.AreEqual(-18.0 * Math.PI / 180.0, ViewerAim.PitchRad(18.0), 1e-6);
            Assert.AreEqual(-170.0, ViewerAim.Wrap(190.0), 1e-9);
            Assert.AreEqual(170.0, ViewerAim.Wrap(-190.0), 1e-9);
            Assert.AreEqual(-180.0, ViewerAim.Wrap(180.0), 1e-9);
        }

        // A camera whose read aim follows (heading, pitch) with a lag of a few frames: azimuth = signH * heading + offset, elevation = signP * pitch.
        private sealed class Camera
        {
            internal double HeadingDeg, PitchDeg, Offset, SignH = 1.0, SignP = -1.0, PitchLimit = 80.0;
            private double az, el;

            internal void Step()
            {
                double wantAz = SignH * HeadingDeg + Offset, wantEl = Math.Max(-PitchLimit, Math.Min(PitchLimit, SignP * PitchDeg));
                az += ViewerAim.Wrap(wantAz - az) * 0.4;
                el += (wantEl - el) * 0.4;
                if (Math.Abs(ViewerAim.Wrap(wantAz - az)) < 0.001) az = wantAz;
                if (Math.Abs(wantEl - el) < 0.001) el = wantEl;
            }

            internal double Az { get { return ViewerAim.Wrap(az); } }
            internal double El { get { return el; } }
        }

        private static AimTuner Tune(Camera cam, double targetAz, double targetEl, out int frames)
        {
            var tuner = new AimTuner(targetAz, targetEl);
            cam.HeadingDeg = ViewerAim.Wrap(targetAz);
            cam.PitchDeg = -targetEl;
            frames = 0;
            while (!tuner.Done && frames < 2000)
            {
                cam.Step();
                double th, tp;
                if (tuner.Frame(cam.Az, cam.El, out th, out tp))
                {
                    cam.HeadingDeg += th;
                    cam.PitchDeg += tp;
                }
                frames++;
            }
            return tuner;
        }

        [TestMethod]
        public void ACameraThatLooksWhereTheGuessSaysNeedsNoStep()
        {
            var cam = new Camera();
            int frames;
            AimTuner t = Tune(cam, 90.0, 18.0, out frames);
            Assert.IsTrue(t.Done && t.Note == null, t.Note);
            Assert.AreEqual(0, t.Steps);
            Assert.AreEqual(90.0, cam.Az, 0.5);
        }

        [TestMethod]
        public void ACameraTurnedHalfWayRoundByTheGamesFrameIsTunedToThePosesHeading()
        {
            foreach (double offset in new[] { 180.0, -90.0, 37.0 })
            {
                var cam = new Camera { Offset = offset };
                int frames;
                AimTuner t = Tune(cam, 90.0, 18.0, out frames);
                Assert.IsTrue(t.Done && t.Note == null, "offset " + offset + ": " + t.Note);
                Assert.AreEqual(90.0, cam.Az, AimTuner.ToleranceDeg, "offset " + offset);
                Assert.AreEqual(18.0, cam.El, AimTuner.ToleranceDeg, "offset " + offset);
                Assert.IsTrue(t.Steps <= 3, "offset " + offset + ": steps " + t.Steps);
            }
        }

        [TestMethod]
        public void ACameraWhoseAzimuthRunsAgainstItsHeadingIsTunedByLearningTheSign()
        {
            var cam = new Camera { SignH = -1.0, Offset = 20.0 };
            int frames;
            AimTuner t = Tune(cam, 250.0, 12.0, out frames);
            Assert.IsTrue(t.Done && t.Note == null, t.Note);
            Assert.AreEqual(ViewerAim.Wrap(250.0), cam.Az, AimTuner.ToleranceDeg);
        }

        [TestMethod]
        public void ACameraThatCannotTakeTheAimIsNamedNotLoopedOn()
        {
            // The pitch limit holds the elevation at 20 degrees: the aim of 45 cannot be reached.
            var cam = new Camera { PitchLimit = 20.0 };
            int frames;
            AimTuner t = Tune(cam, 90.0, 45.0, out frames);
            Assert.IsTrue(t.Done);
            Assert.IsNotNull(t.Note, "the stop is named");
            Assert.IsTrue(t.Steps <= AimTuner.MaxSteps);
            Assert.IsTrue(frames < 2000, "it stops: " + frames);
        }

        [TestMethod]
        public void ATunerNeverTurnsFromAReadingThatStillMoves()
        {
            var tuner = new AimTuner(90.0, 18.0);
            double th, tp;
            Assert.IsFalse(tuner.Frame(10.0, 5.0, out th, out tp));
            Assert.IsFalse(tuner.Frame(10.0, 5.0, out th, out tp));
            // Still: the two readings two frames apart agree.
            Assert.IsFalse(tuner.Frame(10.0, 5.0, out th, out tp), "the first frame of a gap only waits");
            Assert.IsTrue(tuner.Frame(10.0, 5.0, out th, out tp));
            Assert.AreEqual(80.0, th, 1e-9);
            Assert.AreEqual(-13.0, tp, 1e-9, "the pitch is the elevation's opposite");
        }

        // --- The store reads what was just written ---

        // The defect of the K-S1 start: the solo mode removed the cell's override and read the cell back from the table of the frame's start, which still held it; every uniform-field
        // view was drawn as one far cell.
        [TestMethod]
        public void AnOverrideRemovedInTheFrameIsGoneForTheViewersOwnReadsAtOnce()
        {
            Tuning.ClearOverrides();
            Tuning.Instance.BeginFrame(int.MaxValue - 10);
            var store = new GameOptionStore();
            try
            {
                store.SetOverride(CloudLookTuning.TuneCellKm, 12f);
                Tuning.Instance.BeginFrame(int.MaxValue - 9);
                Assert.AreEqual(12f, Tuning.Peek(CloudLookTuning.TuneCellKm, 0f), "the table of the frame holds it");
                Assert.AreEqual(12f, store.Effective(CloudLookTuning.TuneCellKm, 0f));
                store.ClearOverride(CloudLookTuning.TuneCellKm);
                // The reader of the pass still sees the old table in this frame; the viewer's own read must not.
                Assert.AreEqual(12f, Tuning.Peek(CloudLookTuning.TuneCellKm, 0f), "the table is swapped at the next frame");
                Assert.AreEqual(0f, store.Effective(CloudLookTuning.TuneCellKm, 0f), "the viewer reads the override as removed");
                store.SetOverride(CloudLookTuning.TuneCellKm, 5f);
                Assert.AreEqual(5f, store.Effective(CloudLookTuning.TuneCellKm, 0f), "and a value set in this frame as set");
            }
            finally
            {
                Tuning.ClearOverrides();
                Tuning.Instance.BeginFrame(int.MaxValue - 8);
            }
        }

        [TestMethod]
        public void ASoloSelectionReadBackAfterTheCellWasTakenOffIsAUniformField()
        {
            Tuning.ClearOverrides();
            Tuning.Instance.BeginFrame(int.MaxValue - 20);
            var store = new GameOptionStore();
            IList<CloudLayerDef> defs = new[] { CloudLookFixtures.Rain(), CloudLookFixtures.Cumulus(), CloudLookFixtures.Cumulonimbus(), CloudLookFixtures.Cirrus() };
            try
            {
                // A cell first (the Cb view), the table swapped in, then a uniform field of the cumulus layer in one frame: select, then read the selection back, as SetSoloStrength does.
                Apply(store, defs, new SoloSelection { Layer = 2, Type = 1, Strength = 1f, Cell = true, CellKm = 12.0, CellLatDeg = 1.0, CellLonDeg = 2.0 });
                Tuning.Instance.BeginFrame(int.MaxValue - 19);
                Apply(store, defs, new SoloSelection { Layer = 1, Type = 0, Strength = 0.8f, Cell = false });
                SoloSelection read = CloudSoloPlan.Read(defs, (k, d) => store.Effective(k, d));
                Assert.IsTrue(read.On);
                Assert.AreEqual(1, read.Layer);
                Assert.IsFalse(read.Cell, "a uniform field is not turned back into a cell by the table of the frame's start");
            }
            finally
            {
                Tuning.ClearOverrides();
                Tuning.Instance.BeginFrame(int.MaxValue - 18);
            }
        }

        private static void Apply(IOptionStore store, IList<CloudLayerDef> defs, SoloSelection s)
        {
            foreach (KeyValuePair<string, float> pair in CloudSoloPlan.Overrides(defs, s))
            {
                if (float.IsNaN(pair.Value)) store.ClearOverride(pair.Key);
                else store.SetOverride(pair.Key, pair.Value);
            }
        }

        // --- The second start of K-V1 ---

        private sealed class Run
        {
            internal FakeViewerRig Rig;
            internal ViewerWindowProbe Probe;
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

        private static Run Start(int w, int h, float scale, Action<FakeViewerRig> fault = null)
        {
            var r = new Run { Rig = new FakeViewerRig { ScreenW = w, ScreenH = h, UiScaleValue = scale } };
            if (fault != null) fault(r.Rig);
            SeriesProbe.LogLine = m => r.Log.Add(m);
            AtmoResult.Sink = r.Rows.Add;
            r.Probe = new ViewerWindowProbe { Rig = r.Rig, CommandLineOverride = new[] { "KSP_x64.exe", "-atmoSeries", "K-V1", "-atmoStart", "wide", "-atmoProbe", "ViewerWindowProbe" } };
            r.Probe.QuitGame = () => r.Quits++;
            r.Probe.Init();
            return r;
        }

        private static void Drive(Run r, double maxS = 300.0)
        {
            double elapsed = 0.0;
            while (elapsed < maxS && !(r.Probe.Finished && r.Quits > 0))
            {
                r.Rig.Advance(0.05);
                r.Probe.Tick(0.05f);
                elapsed += 0.05;
            }
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
        public void TheWindowProbePassesOnTheOwnersScreenAndOnTheOtherSettings()
        {
            int[][] settings =
            {
                new[] { 1707, 960, 15 }, new[] { 2560, 1440, 15 }, new[] { 1600, 900, 20 }, new[] { 1600, 900, 8 }, new[] { 3840, 2160, 8 }, new[] { 3840, 2160, 20 }
            };
            foreach (int[] s in settings)
            {
                Run r = Start(s[0], s[1], s[2] / 10f);
                Drive(r);
                Assert.IsTrue(r.Probe.Passed, s[0] + "x" + s[1] + " at " + s[2] / 10f + ": " + r.Joined + "\n" + string.Join("\n", r.Log.ToArray()));
                Assert.AreEqual("wide", r.Probe.StartId);
                Dictionary<string, string> rows = Emitted(r);
                Assert.AreEqual("1", rows["VIEWERWIN.opened_inside_screen_ok"]);
                Assert.AreEqual("1", rows["VIEWERWIN.placed_inside_screen_ok"]);
                Assert.AreEqual((double)s[0], double.Parse(rows["VIEWERWIN.screen_width_px"], CultureInfo.InvariantCulture));
                Assert.AreEqual(1, r.Quits);
            }
        }

        [TestMethod]
        public void TheWindowProbeFailsForAWindowCutOffAtTheLeftEdge()
        {
            Run r = Start(2560, 1440, 1.5f, rig => rig.WindowCutOffLeft = true);
            Drive(r);
            Assert.IsTrue(r.Probe.Finished && !r.Probe.Passed, r.Joined);
            Assert.IsTrue(r.Probe.Failures.Any(f => f.StartsWith("VIEWERWIN.opened_inside_screen")), r.Joined);
            Assert.IsTrue(r.Probe.Failures.Any(f => f.StartsWith("VIEWERWIN.placed_inside_screen")), r.Joined);
            Assert.IsTrue(r.Log.Any(l => l.StartsWith("viewerwin: ") && l.Contains("lies wholly inside the screen") && l.EndsWith(" - FAILED")));
        }

        [TestMethod]
        public void TheWindowProbeFailsForAWindowItCannotMeasure()
        {
            Run r = Start(2560, 1440, 1.5f, rig => rig.WindowNotMeasurable = true);
            Drive(r);
            Assert.IsTrue(r.Probe.Finished && !r.Probe.Passed, r.Joined);
            Assert.IsTrue(r.Probe.Failures.Any(f => f.StartsWith("VIEWERWIN.opened_inside_screen")), r.Joined);
        }

        [TestMethod]
        public void TheWindowProbeEndsByNameWhereTheWindowDoesNotOpen()
        {
            Run r = Start(2560, 1440, 1.5f, rig => rig.WindowNeverOpens = true);
            Drive(r);
            Assert.IsTrue(r.Probe.Finished && !r.Probe.Passed);
            Assert.IsTrue(r.Probe.Failures.Any(f => f.StartsWith("VIEWERWIN.window_opens")), r.Joined);
            Assert.IsFalse(Emitted(r).ContainsKey("VIEWERWIN.opened_inside_screen_ok"), "nothing further is judged without a window");
        }

        [TestMethod]
        public void TheRowsFileListsExactlyTheMetricsTheWindowProbeEmits()
        {
            Run r = Start(2560, 1440, 1.5f);
            Drive(r);
            Assert.IsTrue(r.Probe.Passed, r.Joined);
            string root = RepoRoot.Root("tests", "series", "rows", "VPROBE.json");
            var file = (Dictionary<string, object>)MiniJson.Parse(File.ReadAllText(Path.Combine(root, "tests", "series", "rows", "VPROBE.json")));
            var listed = new Dictionary<string, Dictionary<string, object>>();
            foreach (object o in (List<object>)file["rows"])
            {
                var row = (Dictionary<string, object>)o;
                if ((string)row["start"] != "wide") continue;
                Assert.IsFalse(listed.ContainsKey((string)row["metric"]), "row twice: " + (string)row["metric"]);
                listed[(string)row["metric"]] = row;
            }
            CollectionAssert.AreEquivalent(Emitted(r).Keys.ToList(), listed.Keys.ToList(), "rows file against the emitted metrics");
            foreach (KeyValuePair<string, Dictionary<string, object>> pair in listed)
            {
                if (pair.Key.EndsWith("_ok", StringComparison.Ordinal)) Assert.AreEqual(1.0, Convert.ToDouble(pair.Value["equals"], CultureInfo.InvariantCulture), pair.Key);
                else Assert.IsTrue(pair.Value.ContainsKey("required") && (bool)pair.Value["required"] == false, pair.Key + " is information");
            }
        }

        [TestMethod]
        public void TheSeriesFileHasTheTwoWindowStartsAtTheOwnersValuesWithTheUiScalePinned()
        {
            string root = RepoRoot.Root("tests", "series", "K-V1.json");
            var file = (Dictionary<string, object>)MiniJson.Parse(File.ReadAllText(Path.Combine(root, "tests", "series", "K-V1.json")));
            var starts = ((List<object>)file["starts"]).Cast<Dictionary<string, object>>().ToDictionary(s => (string)s["id"]);
            Assert.AreEqual(2, starts.Count);
            Assert.AreEqual(2, Convert.ToInt32(file["plannedStarts"], CultureInfo.InvariantCulture));
            foreach (var pair in new Dictionary<string, int[]> { { "viewer", new[] { 1707, 960 } }, { "wide", new[] { 2560, 1440 } } })
            {
                var set = (Dictionary<string, object>)((Dictionary<string, object>)((List<object>)starts[pair.Key]["settings"])[0])["set"];
                Assert.AreEqual(pair.Value[0], Convert.ToInt32(set["SCREEN_RESOLUTION_WIDTH"], CultureInfo.InvariantCulture), pair.Key);
                Assert.AreEqual(pair.Value[1], Convert.ToInt32(set["SCREEN_RESOLUTION_HEIGHT"], CultureInfo.InvariantCulture), pair.Key);
                Assert.AreEqual(1.5, Convert.ToDouble(set["UI_SCALE"], CultureInfo.InvariantCulture), 1e-9, pair.Key + ": the UI scale is pinned");
                var args = ((List<object>)starts[pair.Key]["probeArgs"]).Cast<string>().ToList();
                Assert.AreEqual(pair.Value[0].ToString(CultureInfo.InvariantCulture), args[args.IndexOf("-screen-width") + 1], pair.Key + ": the command line matches the pin");
                Assert.AreEqual(pair.Value[1].ToString(CultureInfo.InvariantCulture), args[args.IndexOf("-screen-height") + 1], pair.Key);
            }
            Assert.IsTrue(((List<object>)starts["wide"]["probeArgs"]).Cast<string>().Contains("ViewerWindowProbe"));
            Assert.IsTrue(((List<object>)starts["viewer"]["probeArgs"]).Cast<string>().Contains("ViewerProbe"));
        }
    }
}
