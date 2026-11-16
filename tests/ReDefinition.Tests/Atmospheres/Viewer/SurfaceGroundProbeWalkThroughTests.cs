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
    // The probe of the K-S2 starts that load the game with the cover off (SurfaceGroundProbe) against the solo rig's model of the game, and the command-line settings those starts
    // carry (AtmospheresSettings.ApplyLaunchPairs): the four frames in their order, the cover's state each start has to show, the rows file that lists exactly what each start
    // emits, the series file that names the three starts, and the faults the probe judges.
    [TestClass]
    public class SurfaceGroundProbeWalkThroughTests
    {
        private const double Dt = 0.05;

        private static readonly string[] Starts = { SurfaceGroundProbe.StartOn, SurfaceGroundProbe.StartOff, SurfaceGroundProbe.StartNoCover };
        private static readonly string[] AllStarts = { SurfaceGroundProbe.StartOn, SurfaceGroundProbe.StartOff, SurfaceGroundProbe.StartNoCover, SurfaceGroundProbe.StartCaptureAb };

        // The game's host without the game: a screen read at the end of the frame a condition holds in (Pump, called at the end of the test's frame), and a camera that is held or not.
        private sealed class FakeHost : IProbeHost
        {
            private sealed class Pending
            {
                internal Func<bool> When;
                internal Action<ProbeScreen> Done;
                internal bool Png;
            }

            private readonly List<Pending> pending = new List<Pending>();
            internal Color32 HeldColour = new Color32(90, 102, 81, 255), PlacedColour = new Color32(90, 102, 81, 255);
            internal bool NeverPumps;
            internal readonly List<bool> UiShownAtRead = new List<bool>();
            public bool CameraHeld { get; private set; }
            public void Grab(System.Func<bool> when, System.Action<ProbeScreen> done, bool png) { pending.Add(new Pending { When = when, Done = done, Png = png }); }
            public bool UiShowing { get; private set; } = true;
            public bool HideUi() { bool was = UiShowing; UiShowing = false; return was; }
            public void ShowUi() { UiShowing = true; }
            internal FakeSoloRig Rig;

            // The camera stands where the pose says: the fake rig's pose is the camera's (its height is the vessel's less distance times the sine of the elevation).
            public void HoldCamera(double headingDeg, double elevationDeg, double distanceM, double cameraAglM)
            {
                CameraHeld = true;
                Rig.PlacePose(new ViewerPose { Name = "camera only", AglM = cameraAglM + distanceM * Math.Sin(elevationDeg * Math.PI / 180.0), ElevationDeg = elevationDeg, HeadingDeg = headingDeg, CameraDistM = distanceM });
            }
            public void HoldCameraAbsolute(double headingDeg, double elevationDeg, double distanceM, double altitudeAslM)
            {
                CameraHeld = true;
                Rig.PlacePose(new ViewerPose { Name = "camera only", AglM = altitudeAslM + distanceM * Math.Sin(elevationDeg * Math.PI / 180.0), ElevationDeg = elevationDeg, HeadingDeg = headingDeg, CameraDistM = distanceM });
            }
            public void ReleaseCamera() { CameraHeld = false; }
            public string DescribeCameras() { return "Camera 00 depth -1 mask 0xffffff screen"; }

            internal static ProbeScreen Shot(Color32 colour, int frame)
            {
                var px = new Color32[64 * 36];
                for (int i = 0; i < px.Length; i++) px[i] = colour;
                return new ProbeScreen { Width = 64, Height = 36, Frame = frame, Pixels = px, Png = new byte[] { 1, 2, 3 } };
            }

            internal void Pump(int frame)
            {
                if (NeverPumps) return;
                for (int i = pending.Count - 1; i >= 0; i--)
                {
                    if (!pending[i].When()) continue;
                    Pending p = pending[i];
                    pending.RemoveAt(i);
                    ProbeScreen shot = Shot(CameraHeld ? HeldColour : PlacedColour, frame);
                    if (!p.Png) shot.Png = null;
                    UiShownAtRead.Add(UiShowing);
                    p.Done(shot);
                }
            }
        }

        private sealed class Run
        {
            internal FakeHost Host = new FakeHost();
            internal string ScenePath = ScenePathWatcher.SpaceCentre;
            internal bool Unpacked = true;
            internal Color32 ViewerFileColour = new Color32(90, 102, 81, 255);
            internal readonly List<string> Written = new List<string>();
            internal FakeSoloRig Rig;
            internal SurfaceGroundProbe Probe;
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
            AtmospheresLog.SayLine = m => { };
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

        // The -atmoSet of each start as the series file has it.
        private static string PairsOf(string start)
        {
            return start == SurfaceGroundProbe.StartOff ? "enabled=0,weatherEffects=1,cloudSource=Ours,ownClouds=1"
                 : start == SurfaceGroundProbe.StartCaptureAb ? "enabled=0,weatherEffects=1,cloudSource=Ours,ownClouds=1"
                 : start == SurfaceGroundProbe.StartNoCover ? "groundCover=0,weatherEffects=1,cloudSource=Ours,ownClouds=1"
                 : "weatherEffects=1,cloudSource=Ours,ownClouds=1";
        }

        // The cover as the start leaves it: quads and bodies where it is on, none where it is off from load.
        private static Run Start(string start, Func<string, bool> coverOn = null, bool applyPins = true, Action<FakeSoloRig> fault = null)
        {
            var r = new Run { Rig = new FakeSoloRig() };
            if (fault != null) fault(r.Rig);
            SeriesProbe.LogLine = m => r.Log.Add(m);
            AtmoResult.Sink = r.Rows.Add;
            // The runner adds the space-centre switch for a series that says viaSpaceCentre (tools/atmo_series.py plan_args).
            string[] args = { "KSP_x64.exe", "-atmoSeries", "K-S2", "-atmoStart", start, "-atmoProbe", "SurfaceGroundProbe", "-atmoSet", PairsOf(start), "-redefinitionProbeViaSpaceCentre" };
            AtmospheresSettings.ResetToDefaults();
            if (applyPins) AtmospheresSettings.ApplyLaunchPins(args);
            bool on = coverOn != null ? coverOn(start) : start == SurfaceGroundProbe.StartOn;
            r.Probe = new SurfaceGroundProbe
            {
                Rig = r.Rig, CommandLineOverride = args,
                CoverQuadCount = () => on ? 900 : 0,
                CoverInstalledCount = () => on ? 3 : 0,
                // The collider is at 3 m above sea level (the fake's terrain is at 0); the camera stands over it as the rig's pose says.
                GroundReader = ahead => new GroundReading { Hit = true, Object = "ksc_terrain", Material = "ksc_exterior_terrain_grass_02", Shader = "Custom/ParallaxKSCTerrain", DistanceM = SurfaceGroundProbe.RayUpM + 2.0,
                                                           AltAsl = 3.0, CameraOverM = r.Rig.Pose.CameraAglM - 3.0 }
            };
            r.Probe.ScenePathReader = () => r.ScenePath;
            r.Probe.VesselDescriber = () => "Kerbal X: 12 parts";
            r.Probe.VesselUnpacked = () => r.Unpacked;
            r.Host.Rig = r.Rig;
            r.Probe.Host = r.Host;
            r.Probe.PictureReader = path => FakeHost.Shot(r.ViewerFileColour, 0);
            r.Probe.PictureWriter = (path, png) => r.Written.Add(path);
            r.Probe.SunAngleReader = () =>
            {
                double d = Math.Abs(r.Rig.Pose.AzimuthDeg - 90.0) % 360.0;
                return d > 180.0 ? 360.0 - d : d;
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
                r.Host.Pump(r.Rig.Frame);
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
        public void EachStartTakesTheFourFramesInOrderAndPassesWithItsCoverState()
        {
            foreach (string start in Starts)
            {
                Run r = Start(start);
                Drive(r);
                Assert.IsTrue(r.Probe.Passed, start + "\n" + Dump(r));
                Assert.AreEqual(start, r.Probe.StartId);
                Assert.IsTrue(r.Probe.Elapsed < 15 * 60, start + ": the scenario fits its fifteen minutes: " + r.Probe.Elapsed);
                Assert.IsTrue(r.Probe.Elapsed > 60, start + ": the real scenario is not a handful of frames: " + r.Probe.Elapsed);
                int last = -1;
                foreach (SurfaceGroundProbe.Frame f in SurfaceGroundProbe.Frames)
                {
                    int at = r.Log.FindIndex(l => l.StartsWith("ground: ground_" + f.Name + ": cover quads "));
                    Assert.IsTrue(at > last, start + ": " + f.Name + " is described after the frame before it\n" + Dump(r));
                    last = at;
                    Assert.IsTrue(r.Log.Any(l => l.StartsWith("ground: " + f.Name + ": picture ") && !l.EndsWith("none")), start + ": " + f.Name + " has a picture");
                }
                Dictionary<string, string> rows = Emitted(r);
                Assert.AreEqual(start == SurfaceGroundProbe.StartOn ? 900.0 : 0.0, double.Parse(rows["GRD.g2_p2_cover_quads"], CultureInfo.InvariantCulture), 0.0, start + ": the cover's records");
                Assert.AreEqual("1", rows["GRD.g2_p1_ground_is_parallax"], start + ": the shader the ray found is a Parallax one");
                double ground = double.Parse(rows["GRD.g2_p1_camera_agl_m"], CultureInfo.InvariantCulture);
                Assert.AreEqual(3.0 + SurfaceGroundProbe.GroundOverColliderM, ground, 0.01, start + ": the ground-level camera is held 2 m over the collider the ray found (3 m in the fake), not at the first guess");
                Assert.IsTrue(r.Log.Any(l => l.StartsWith("ground: g2_p1: the camera was ") && l.Contains("held again")), start + ": the second hold is logged");
                Assert.IsTrue(r.Host.UiShowing && !r.Host.CameraHeld, start + ": the camera and the interface are given back");
                Assert.AreEqual(291.3, double.Parse(rows["GRD.far_p1_camera_agl_m"], CultureInfo.InvariantCulture), 0.2, start + ": the far camera stands where K-S2's does");
                Assert.IsTrue(double.Parse(rows["GRD.far_p1_sun_angle_deg"], CultureInfo.InvariantCulture) < 30.0, "heading 90 looks at the sun");
                Assert.IsTrue(double.Parse(rows["GRD.far_p2_sun_angle_deg"], CultureInfo.InvariantCulture) > 150.0, "heading 270 looks away from it");
            }
        }

        [TestMethod]
        public void TheSettingsOfTheCommandLineAreInForceBeforeTheFirstFrame()
        {
            Run off = Start(SurfaceGroundProbe.StartOff);
            Assert.IsFalse(AtmospheresSettings.Enabled, "enabled=0");
            Assert.IsTrue(AtmospheresSettings.GroundCover && AtmospheresSettings.WeatherEffects && AtmospheresSettings.OwnClouds, "the others as given");
            Run nocover = Start(SurfaceGroundProbe.StartNoCover);
            Assert.IsTrue(AtmospheresSettings.Enabled && !AtmospheresSettings.GroundCover, "groundCover=0");
            Assert.AreEqual(CloudSourceKind.Ours, AtmospheresSettings.CloudSource);
            Assert.IsNotNull(off);
            Assert.IsNotNull(nocover);
        }

        [TestMethod]
        public void TheRowsFileListsExactlyWhatEachStartEmits()
        {
            string root = RepoRoot.Root("tests", "series", "rows", "SURFACEGROUND.json");
            var file = (Dictionary<string, object>)MiniJson.Parse(File.ReadAllText(Path.Combine(root, "tests", "series", "rows", "SURFACEGROUND.json")));
            Assert.AreEqual("SURFACE", (string)file["package"]);
            var listed = new Dictionary<string, Dictionary<string, Dictionary<string, object>>>();
            foreach (object o in (List<object>)file["rows"])
            {
                var row = (Dictionary<string, object>)o;
                Assert.AreEqual("K-S2", (string)row["series"], (string)row["metric"]);
                Assert.AreEqual("SURFACEGROUND", (string)row["section"], (string)row["metric"]);
                string start = (string)row["start"];
                Assert.IsTrue(AllStarts.Contains(start), "a start the probe knows: " + start);
                if (!listed.ContainsKey(start)) listed[start] = new Dictionary<string, Dictionary<string, object>>();
                Assert.IsFalse(listed[start].ContainsKey((string)row["metric"]), "row twice: " + start + " " + (string)row["metric"]);
                listed[start][(string)row["metric"]] = row;
            }
            foreach (string start in AllStarts)
            {
                Run r = Start(start);
                Drive(r);
                Assert.IsTrue(r.Probe.Passed, start + "\n" + Dump(r));
                CollectionAssert.AreEquivalent(Emitted(r).Keys.ToList(), listed[start].Keys.ToList(), start + ": rows file against the emitted metrics");
                foreach (KeyValuePair<string, Dictionary<string, object>> pair in listed[start])
                {
                    if (pair.Key.EndsWith("_ok", StringComparison.Ordinal))
                    {
                        Assert.AreEqual(1.0, Convert.ToDouble(pair.Value["equals"], CultureInfo.InvariantCulture), pair.Key + " must equal 1");
                        Assert.IsFalse(pair.Value.ContainsKey("required") && (bool)pair.Value["required"] == false, pair.Key + " is a check: required");
                    }
                    else Assert.IsTrue(pair.Value.ContainsKey("required") && (bool)pair.Value["required"] == false, pair.Key + " is information (required false)");
                }
            }
        }

        [TestMethod]
        public void TheSeriesFileNamesTheThreeStartsTheirProbeAndTheirSetting()
        {
            string root = RepoRoot.Root("tests", "series", "K-S2.json");
            var file = (Dictionary<string, object>)MiniJson.Parse(File.ReadAllText(Path.Combine(root, "tests", "series", "K-S2.json")));
            var starts = ((List<object>)file["starts"]).Cast<Dictionary<string, object>>().ToDictionary(s => (string)s["id"]);
            Assert.AreEqual((long)5, Convert.ToInt64(file["plannedStarts"], CultureInfo.InvariantCulture), "the surface start, the three ground starts and the capture A/B start");
            foreach (string id in AllStarts)
            {
                Assert.IsTrue(starts.ContainsKey(id), id);
                string[] args = ((List<object>)starts[id]["probeArgs"]).Cast<string>().ToArray();
                Assert.IsTrue(args.Contains("SurfaceGroundProbe"), id + " names its probe");
                string pairs = args[Array.IndexOf(args, "-atmoSet") + 1];
                Assert.AreEqual(PairsOf(id), pairs, id + ": the setting of the start");
                Assert.IsFalse(starts[id].ContainsKey("productSwitches"), id + ": the series' switches are the surface start's");
            }
            Assert.IsTrue(((List<object>)file["sections"]).Cast<Dictionary<string, object>>().Any(s => (string)s["id"] == "SURFACEGROUND"));
            Assert.IsFalse(file.ContainsKey("productSwitches"), "the switches moved to the surface start");
            Assert.AreEqual(true, file["judgesGround"], "K-S2 judges the ground");
            Assert.AreEqual(true, file["viaSpaceCentre"], "and reaches the flight through the space centre, as every start of it does");
            foreach (Dictionary<string, object> s in starts.Values) Assert.IsFalse(s.ContainsKey("viaSpaceCentre") && (bool)s["viaSpaceCentre"] == false, "no start of K-S2 takes the straight path: " + (string)s["id"]);
        }

        [TestMethod]
        public void AnOffStartWhoseCoverIsStillInTheGroundFails()
        {
            foreach (string start in new[] { SurfaceGroundProbe.StartOff, SurfaceGroundProbe.StartNoCover })
            {
                Run r = Start(start, coverOn: s => true);
                Drive(r);
                Assert.IsTrue(r.Probe.Finished, Dump(r));
                Assert.IsFalse(r.Probe.Passed, start + "\n" + Dump(r));
                Assert.IsTrue(r.Probe.Failures.Any(f => f.StartsWith(SurfaceGroundProbe.Prefix + "cover_state")), start + ": the failure names cover_state: " + r.Joined);
                Assert.IsFalse(r.Probe.Failures.Any(f => f.StartsWith("exception in the scenario")), r.Joined);
            }
        }

        [TestMethod]
        public void AControlWithoutCoverRecordsFails()
        {
            Run r = Start(SurfaceGroundProbe.StartOn, coverOn: s => false);
            Drive(r);
            Assert.IsFalse(r.Probe.Passed, Dump(r));
            Assert.IsTrue(r.Probe.Failures.Any(f => f.StartsWith(SurfaceGroundProbe.Prefix + "cover_state")), r.Joined);
        }

        [TestMethod]
        public void ASettingThatDidNotTakeEffectFailsTheLaunchPinCheck()
        {
            Run r = Start(SurfaceGroundProbe.StartOff, applyPins: false);
            Drive(r);
            Assert.IsFalse(r.Probe.Passed, Dump(r));
            Assert.IsTrue(r.Probe.Failures.Any(f => f.StartsWith(SurfaceGroundProbe.Prefix + "launch_pin")), r.Joined);
        }

        [TestMethod]
        public void ACameraUnderTheLawnFailsTheClearanceOfTheGroundFrames()
        {
            Run r = Start(SurfaceGroundProbe.StartOn);
            r.Probe.GroundReader = ahead => new GroundReading { Hit = true, Object = "ksc_terrain", Material = "m", Shader = "s", DistanceM = SurfaceGroundProbe.RayUpM - 1.0, AltAsl = 3.0, CameraOverM = 0.3 };
            Drive(r);
            Assert.IsFalse(r.Probe.Passed, Dump(r));
            Assert.IsTrue(r.Probe.Failures.Any(f => f.StartsWith(SurfaceGroundProbe.Prefix + "g2_p1_clearance")), r.Joined);
            Assert.IsFalse(r.Probe.Failures.Any(f => f.StartsWith(SurfaceGroundProbe.Prefix + "far_p1_")), "the far frames have no clearance check: " + r.Joined);
        }

        [TestMethod]
        public void TheCaptureABStartTakesBothPicturesInOneFrameAndReportsTheGroundOfEach()
        {
            Run r = Start(SurfaceGroundProbe.StartCaptureAb);
            r.Host.HeldColour = new Color32(60, 70, 60, 255);
            Drive(r);
            Assert.IsTrue(r.Probe.Passed, Dump(r));
            Dictionary<string, string> rows = Emitted(r);
            Func<string, double> v = k => double.Parse(rows["GRD." + k], CultureInfo.InvariantCulture);
            Assert.AreEqual(0.0, v("A_capture_diff_max"), 0.5, "the viewer's file and the screen read are one picture");
            Assert.AreEqual(90.0, v("A_screen_ground_r"), 0.01);
            Assert.AreEqual(60.0, v("B_ground_r"), 0.01, "B was read with the camera held");
            Assert.AreEqual(32.0, v("A_vs_B_diff_max"), 0.5, "the difference between the placements is reported");
            Assert.IsFalse(r.Host.CameraHeld, "the camera is let go before the viewer places the vessel");
            Assert.IsTrue(r.Host.UiShownAtRead.Count >= 4 && !r.Host.UiShownAtRead.Any(x => x), "the interface is away at every screen read: " + string.Join(",", r.Host.UiShownAtRead.Select(x => x.ToString()).ToArray()));
            Assert.IsTrue(r.Host.UiShowing, "and back at the end");
            string bSettle = r.Log.FirstOrDefault(l => l.StartsWith("ground: B settle stable after "));
            Assert.IsNotNull(bSettle, "B settles on a stable ground, not at the limit\n" + Dump(r));
            Assert.IsTrue(double.Parse(Regex.Match(bSettle, "after (?<s>[0-9.]+) s").Groups["s"].Value, CultureInfo.InvariantCulture) >= SurfaceGroundProbe.FirstSettleMinS, "B waits as long as ProbeLite's first pose: " + bSettle);
            Assert.IsTrue(r.Written.Any(p => p.EndsWith("capture_ab_B_camera_only.png")) && r.Written.Any(p => p.EndsWith("_screen.png")), string.Join(";", r.Written.ToArray()));
            int b = r.Log.FindIndex(l => l.StartsWith("ground: capture_ab: B camera alone")), a = r.Log.FindIndex(l => l.StartsWith("ground: capture_ab: A viewer placement"));
            Assert.IsTrue(b >= 0 && a > b, "B first, then A\n" + Dump(r));
            Assert.IsTrue(r.Log.Any(l => l.Contains("cameras: Camera 00 depth")), "the cameras are described");
        }

        [TestMethod]
        public void ADifferentViewerFileIsReportedNotFailed()
        {
            Run r = Start(SurfaceGroundProbe.StartCaptureAb);
            r.ViewerFileColour = new Color32(60, 70, 120, 255);
            Drive(r);
            Assert.IsTrue(r.Probe.Passed, Dump(r));
            Assert.AreEqual(39.0, double.Parse(Emitted(r)["GRD.A_capture_diff_max"], CultureInfo.InvariantCulture), 0.5);
            Assert.IsTrue(r.Log.Any(l => l.Contains("DIFFERENT pictures")), Dump(r));
        }

        [TestMethod]
        public void AScreenReadThatNeverComesInTheCaptureFrameFailsTheChecks()
        {
            Run r = Start(SurfaceGroundProbe.StartCaptureAb);
            r.Host.NeverPumps = true;
            Drive(r);
            Assert.IsFalse(r.Probe.Passed, Dump(r));
            Assert.IsTrue(r.Probe.Failures.Any(f => f.StartsWith(SurfaceGroundProbe.Prefix + "B_screen_read")), r.Joined);
            Assert.IsTrue(r.Probe.Failures.Any(f => f.StartsWith(SurfaceGroundProbe.Prefix + "A_screen_read")), r.Joined);
            Assert.IsFalse(r.Probe.Failures.Any(f => f.StartsWith("exception in the scenario")), r.Joined);
        }

        [TestMethod]
        public void TheGroundRegionIsTheLowerLeftOfAnyPictureSize()
        {
            var px = new Color32[100 * 100];
            for (int y = 0; y < 100; y++)
                for (int x = 0; x < 100; x++) px[y * 100 + x] = x < 27 && y >= 1 && y < 18 ? new Color32(10, 20, 30, 255) : new Color32(200, 200, 200, 255);
            double r, g, b;
            GroundRegion.Mean(px, 100, 100, out r, out g, out b);
            Assert.AreEqual(10.0, r, 0.0);
            Assert.AreEqual(20.0, g, 0.0);
            Assert.AreEqual(30.0, b, 0.0);
            GroundRegion.Mean(null, 100, 100, out r, out g, out b);
            Assert.IsTrue(double.IsNaN(r));
            Assert.AreEqual(5.0, GroundRegion.MaxDifference(1, 2, 3, 2, 7, 3), 0.0);
        }

        [TestMethod]
        public void AFlightReachedStraightFromTheMainMenuFailsTheScenePathWhereTheSpaceCentreWasAskedFor()
        {
            Run r = Start(SurfaceGroundProbe.StartOn);
            r.ScenePath = ScenePathWatcher.Direct;
            Drive(r);
            Assert.IsFalse(r.Probe.Passed, Dump(r));
            Assert.IsTrue(r.Probe.Failures.Any(f => f.StartsWith(SurfaceGroundProbe.Prefix + "scene_path")), r.Joined);
            Assert.IsTrue(r.Log.Any(l => l == "ground: scene path: direct"), "the path is logged");
        }

        [TestMethod]
        public void AVesselThatStaysPackedFailsTheCheckBeforeTheViewerPlacesIt()
        {
            Run r = Start(SurfaceGroundProbe.StartOn);
            r.Unpacked = false;
            Drive(r);
            Assert.IsFalse(r.Probe.Passed, Dump(r));
            Assert.IsTrue(r.Probe.Failures.Any(f => f.StartsWith(SurfaceGroundProbe.Prefix + "vessel_unpacked")), r.Joined);
            Assert.IsTrue(r.Log.Any(l => l.StartsWith("ground: the vessel before the viewer places it: ")), "the vessel is logged before the placement");
        }

        [TestMethod]
        public void AGreyGroundFailsTheGreenShareAndTheNumberIsEmitted()
        {
            Run r = Start(SurfaceGroundProbe.StartOn);
            r.ViewerFileColour = new Color32(77, 86, 90, 255);
            Drive(r);
            Assert.IsFalse(r.Probe.Passed, Dump(r));
            Assert.IsTrue(r.Probe.Failures.Any(f => f.StartsWith(SurfaceGroundProbe.Prefix + "ground_green")), r.Joined);
            Assert.AreEqual(2.5, double.Parse(Emitted(r)["GRD.green_share"], CultureInfo.InvariantCulture), 0.01, "G - (R + B) / 2 of the grey-blue ground");
        }

        [TestMethod]
        public void TheGreenShareIsTheLowerLeftBoxAsVanillaGroundMeasuresIt()
        {
            var px = new Color32[100 * 100];
            for (int y = 0; y < 100; y++)
                for (int x = 0; x < 100; x++) px[y * 100 + x] = x < 33 && y < 15 ? new Color32(88, 99, 88, 255) : new Color32(255, 0, 255, 255);
            Assert.AreEqual(11.0, GroundRegion.GreenShare(px, 100, 100), 1e-9);
            Assert.IsTrue(double.IsNaN(GroundRegion.GreenShare(null, 100, 100)));
            Assert.AreEqual(ScenePathWatcher.Unknown, ScenePathWatcher.Current, "no flight was asked for in the test host");
        }

        [TestMethod]
        public void AnUnknownStartIdFails()
        {
            Run r = Start("surface_x", coverOn: s => false);
            Drive(r);
            Assert.IsFalse(r.Probe.Passed, Dump(r));
            Assert.IsTrue(r.Probe.Failures.Any(f => f.StartsWith(SurfaceGroundProbe.Prefix + "cover_state")), r.Joined);
        }

        [TestMethod]
        public void ASunThatRunsOnFailsTheFrameSameness()
        {
            Run r = Start(SurfaceGroundProbe.StartOn, fault: rig => rig.SunDrifts = true);
            Drive(r);
            Assert.IsTrue(r.Probe.Finished, Dump(r));
            Assert.IsFalse(r.Probe.Passed, Dump(r));
            Assert.IsTrue(r.Probe.Failures.Any(f => f.Contains("_sun_fixed")), r.Joined);
        }

        [TestMethod]
        public void TheGroundLevelPoseStandsTheVesselAtTheHeightThatPutsTheCameraTwoMetresUp()
        {
            Assert.AreEqual(2.0, SurfaceGroundProbe.VesselAglFor(2.0, 0.0, 40.0), 1e-9, "no elevation: the vessel and the camera stand level");
            double vessel = SurfaceGroundProbe.VesselAglFor(SurfaceGroundProbe.GroundCameraAglM, SurfaceGroundProbe.GroundElevationDeg, SurfaceGroundProbe.GroundCameraDistAM);
            double camera = vessel - SurfaceGroundProbe.GroundCameraDistAM * Math.Sin(SurfaceGroundProbe.GroundElevationDeg * Math.PI / 180.0);
            Assert.AreEqual(SurfaceGroundProbe.GroundCameraAglM, camera, 1e-9);
            Assert.IsTrue(vessel > 7.5 && vessel < 7.6, "about 7.56 m for a camera 90 m behind: " + vessel);
        }

        [TestMethod]
        public void TheLaunchPairsAreReadAndAppliedBySettingKind()
        {
            var pairs = AtmospheresSettings.LaunchPairs(new[] { "KSP.exe", "-atmoSet", "enabled=0, groundCover=false,cloudSource=Eve,volumeSteps=22,nonsense=1,broken,=3,x=" });
            Assert.AreEqual(5, pairs.Count, string.Join(";", pairs.Select(p => p.Key + "=" + p.Value).ToArray()));
            IList<string> applied = AtmospheresSettings.ApplyLaunchPairs(pairs);
            CollectionAssert.AreEqual(new[] { "enabled=0", "groundCover=false", "cloudSource=Eve", "volumeSteps=22" }, applied.ToList(), "the unknown key is left out");
            Assert.IsFalse(AtmospheresSettings.Enabled);
            Assert.IsFalse(AtmospheresSettings.GroundCover);
            Assert.AreEqual(CloudSourceKind.Eve, AtmospheresSettings.CloudSource);
            Assert.AreEqual(22.0, AtmospheresSettings.Number(AtmospheresSettings.Keys.VolumeSteps), 0.0);
            AtmospheresSettings.ResetToDefaults();
            Assert.AreEqual(0, AtmospheresSettings.LaunchPairs(new[] { "KSP.exe" }).Count, "no switch, no pairs");
            Assert.AreEqual(0, AtmospheresSettings.ApplyLaunchPairs(new[] { new KeyValuePair<string, string>("groundCover", "maybe") }).Count, "a word that is no switch value is left out");
            Assert.IsTrue(AtmospheresSettings.GroundCover);
            Assert.AreEqual(0, AtmospheresSettings.ApplyLaunchPairs(new[] { new KeyValuePair<string, string>("cloudSource", "nothing") }).Count, "a choice that does not exist is left out");
        }
    }
}
