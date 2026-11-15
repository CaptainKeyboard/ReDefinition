using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Core;
using ReDefinition.Tests.Atmospheres.B09;

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

        private sealed class Run
        {
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
            string[] args = { "KSP_x64.exe", "-atmoSeries", "K-S2", "-atmoStart", start, "-atmoProbe", "SurfaceGroundProbe", "-atmoSet", PairsOf(start) };
            AtmospheresSettings.ResetToDefaults();
            if (applyPins) AtmospheresSettings.ApplyLaunchPins(args);
            bool on = coverOn != null ? coverOn(start) : start == SurfaceGroundProbe.StartOn;
            r.Probe = new SurfaceGroundProbe
            {
                Rig = r.Rig, CommandLineOverride = args,
                CoverQuadCount = () => on ? 900 : 0,
                CoverInstalledCount = () => on ? 3 : 0,
                GroundReader = () => new GroundReading { Hit = true, Object = "ksc_terrain", Material = "ksc_exterior_terrain_grass_02", Shader = "Custom/ParallaxKSCTerrain", DistanceM = SurfaceGroundProbe.RayUpM + 2.0 }
            };
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
                Assert.AreEqual(SurfaceGroundProbe.GroundCameraAglM, ground, 0.01, start + ": the ground-level camera stands about 2 m over the lawn");
                Assert.AreEqual(5.2, SurfaceGroundProbe.GroundCameraAglM, 0.0, "over the PQS terrain: the lawn lies 2.7 to 3.2 m above it");
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
                Assert.IsTrue(Starts.Contains(start), "a start the probe knows: " + start);
                if (!listed.ContainsKey(start)) listed[start] = new Dictionary<string, Dictionary<string, object>>();
                Assert.IsFalse(listed[start].ContainsKey((string)row["metric"]), "row twice: " + start + " " + (string)row["metric"]);
                listed[start][(string)row["metric"]] = row;
            }
            foreach (string start in Starts)
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
            Assert.AreEqual((long)4, Convert.ToInt64(file["plannedStarts"], CultureInfo.InvariantCulture), "the surface start and the three ground starts");
            foreach (string id in Starts)
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
            r.Probe.GroundReader = () => new GroundReading { Hit = true, Object = "ksc_terrain", Material = "m", Shader = "s", DistanceM = SurfaceGroundProbe.RayUpM - 1.0 };
            Drive(r);
            Assert.IsFalse(r.Probe.Passed, Dump(r));
            Assert.IsTrue(r.Probe.Failures.Any(f => f.StartsWith(SurfaceGroundProbe.Prefix + "g2_p1_clearance")), r.Joined);
            Assert.IsFalse(r.Probe.Failures.Any(f => f.StartsWith(SurfaceGroundProbe.Prefix + "far_p1_")), "the far frames have no clearance check: " + r.Joined);
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
            double vessel = SurfaceGroundProbe.VesselAglFor(SurfaceGroundProbe.GroundCameraAglM, SurfaceGroundProbe.GroundElevationDeg, SurfaceGroundProbe.GroundCameraDistM);
            double camera = vessel - SurfaceGroundProbe.GroundCameraDistM * Math.Sin(SurfaceGroundProbe.GroundElevationDeg * Math.PI / 180.0);
            Assert.AreEqual(SurfaceGroundProbe.GroundCameraAglM, camera, 1e-9);
            Assert.IsTrue(vessel > 6.1 && vessel < 6.4, "about 6.25 m: " + vessel);
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
