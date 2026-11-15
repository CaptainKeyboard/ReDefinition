using System;
using System.Collections.Generic;
using System.Globalization;
using ReDefinition.Atmospheres.Viewer;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // What the ground under the camera is, read in one frame: whether a ray found it, the object, its material and shader.
    internal struct GroundReading
    {
        internal bool Hit;
        internal string Object, Material, Shader;
        internal double DistanceM;
    }

    // The probe of the starts of K-S2 that load the game with the cover switched off before anything is built (rows tests/series/rows/SURFACEGROUND.json). K-S2's own start
    // switches the cover off at run time, and then the cover's PQS mod stays in the sphere with its records (the repaint gives the quads their original colours and tells Parallax
    // again), so its frames cannot say whether the grey ground of the KSC is ours. These starts carry one setting from the command line (-atmoSet, applied when the settings are
    // read, before the first scene), and the same frames:
    //   ground_on       the control: Atmospheres on, weather effects on, own clouds, the cover on (the picture K-S2's baseline takes);
    //   ground_off      enabled=0 at load: the whole package is off from the first frame (the cover's PQS mod is never put into a sphere);
    //   ground_nocover  groundCover=0 at load: only the cover feature is off, the rest runs.
    // The frames: the cb_far pose of K-S1 and K-S2 (300 m over the pad, 5 degrees up, a camera 100 m behind the vessel) at heading 90 and 270, the same universal time and sun as
    // K-S2, and two ground-level poses over the pad (the camera 2 m above the ground, 1.5 degrees up, the vessel 40 m ahead of it) at the same two headings. The probe judges that
    // the setting took effect (the cover's PQS mod is in no sphere where it is off and paints quads where it is on), that the frames are one sun and one pose each, and that every
    // picture is written; the pictures are compared offline (tools/surface_frames.py). Each frame logs 'ground: <frame>: ...' with the cover's state and what a ray down from the
    // ground the camera looks at hits (its object, material and shader: the name of a Parallax shader there says Parallax draws it).
    [AtmospheresModule(994)]
    internal sealed class SurfaceGroundProbe : SeriesProbe
    {
        internal const string Series = "K-S2";
        internal const string Prefix = "GRD.";
        // K-S2's pose and time (SurfaceProbe).
        internal const double FarAglM = 300.0, FarElevationDeg = 5.0, FarCameraDistM = 100.0, HeadingToward = 90.0, HeadingAway = 270.0, K1Ut = 1470283.6;
        // The ground-level pose: the camera is GroundCameraAglM above the ground, looking GroundElevationDeg up, GroundCameraDistM behind the vessel, which then stands
        // GroundCameraAglM + GroundCameraDistM * sin(elevation) above the ground (the camera sits below the vessel by that much).
        internal const double GroundCameraAglM = 2.0, GroundElevationDeg = 1.5, GroundCameraDistM = 40.0;
        // The camera of a ground-level frame is within these metres above the ground (the game may lift it a little over a bump); a far frame within FarAglToleranceM of 290.
        internal const double GroundAglMinM = 0.3, GroundAglMaxM = 6.0, FarAglToleranceM = 15.0;
        internal const double AimToleranceDeg = 0.5, SunDriftDeg = 1.0, PosesApartDeg = 60.0, RayAheadM = 30.0, RayUpM = 20.0, RayLengthM = 200.0;
        internal const double PollPlaceS = 60.0, PollMapS = 20.0, PollPictureS = 15.0, SettleS = 3.0, PollWindowS = 10.0, PollPassS = 40.0, CoverSettleS = 20.0;
        internal const int SettleFrames = 60, AimStillMaxFrames = 80, MinPictureBytes = 1000;

        // The start ids this probe knows and what each says of the cover: on (the PQS mod is in the sphere and paints) or off (it is in none).
        internal const string StartOn = "ground_on", StartOff = "ground_off", StartNoCover = "ground_nocover";

        internal struct Frame
        {
            internal string Name;
            internal double Heading, AglM, CameraDistM, ElevationDeg, WantedCameraAglM;
            internal bool Ground;
        }

        // The vessel's height for a camera that stands cameraAglM above the ground looking elevationDeg up from distanceM away.
        internal static double VesselAglFor(double cameraAglM, double elevationDeg, double distanceM)
        {
            return cameraAglM + distanceM * Math.Sin(elevationDeg * Math.PI / 180.0);
        }

        internal static readonly Frame[] Frames =
        {
            new Frame { Name = "far_p1", Heading = HeadingToward, AglM = FarAglM, CameraDistM = FarCameraDistM, ElevationDeg = FarElevationDeg, WantedCameraAglM = FarAglM - FarCameraDistM * 0.0871557 },
            new Frame { Name = "far_p2", Heading = HeadingAway, AglM = FarAglM, CameraDistM = FarCameraDistM, ElevationDeg = FarElevationDeg, WantedCameraAglM = FarAglM - FarCameraDistM * 0.0871557 },
            new Frame { Name = "g2_p1", Heading = HeadingToward, AglM = VesselAglFor(GroundCameraAglM, GroundElevationDeg, GroundCameraDistM), CameraDistM = GroundCameraDistM, ElevationDeg = GroundElevationDeg, WantedCameraAglM = GroundCameraAglM, Ground = true },
            new Frame { Name = "g2_p2", Heading = HeadingAway, AglM = VesselAglFor(GroundCameraAglM, GroundElevationDeg, GroundCameraDistM), CameraDistM = GroundCameraDistM, ElevationDeg = GroundElevationDeg, WantedCameraAglM = GroundCameraAglM, Ground = true }
        };

        private IViewerRig rig;
        private bool reached;
        private string taken;
        private bool polled;
        private int maxQuads, maxInstalled;

        internal IViewerRig Rig
        {
            get { return rig ?? (rig = new GameSoloRig()); }
            set { rig = value; }
        }

        // What a test replaces: the cover's quad records, the bodies the cover's PQS mod stands in, the ground under the camera, the angle between the camera and the sun.
        internal Func<int> CoverQuadCount = () => CoverQuads.Count;
        internal Func<int> CoverInstalledCount = ReadInstalled;
        internal Func<GroundReading> GroundReader = ReadGameGround;
        internal Func<double> SunAngleReader = ReadSunAngle;

        protected override string SeriesName
        {
            get { return Series; }
        }

        protected override double MaxSeconds
        {
            get { return 15 * 60; }
        }

        protected override string LostGround()
        {
            if (!reached) return null;
            return Rig.FlightReady ? null : "the flight scene was lost during the scenario";
        }

        // The own pass draws: the package is on and the pass has its buffers and a stack (with the master switch off the pass does not run).
        private bool PassDraws
        {
            get { return AtmospheresSettings.Enabled && Rig.PassDrawing; }
        }

        private static int ReadInstalled()
        {
            SnowRefresher refresher = UnityEngine.Object.FindObjectOfType<SnowRefresher>();
            return refresher != null ? refresher.InstalledCount : 0;
        }

        // The ground the camera looks at: a ray straight down from RayUpM over the point RayAheadM ahead of the camera (horizontally), the first hit that is not a vessel part.
        private static GroundReading ReadGameGround()
        {
            var none = new GroundReading { Object = "", Material = "", Shader = "", DistanceM = double.NaN };
            try
            {
                Camera cam = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null;
                CelestialBody body = FlightGlobals.currentMainBody;
                if (cam == null || body == null) return none;
                Vector3 position = cam.transform.position;
                Vector3 up = (position - body.position).normalized;
                Vector3 forward = Vector3.ProjectOnPlane(cam.transform.forward, up).normalized;
                Vector3 origin = position + forward * (float)RayAheadM + up * (float)RayUpM;
                RaycastHit[] hits = Physics.RaycastAll(origin, -up, (float)RayLengthM);
                Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
                foreach (RaycastHit hit in hits)
                {
                    if (hit.collider == null || hit.collider.GetComponentInParent<Part>() != null) continue;
                    Renderer renderer = hit.collider.GetComponent<Renderer>() ?? hit.collider.GetComponentInParent<Renderer>();
                    Material material = renderer != null ? renderer.sharedMaterial : null;
                    return new GroundReading
                    {
                        Hit = true, Object = hit.collider.gameObject.name, Material = material != null ? material.name : "none",
                        Shader = material != null && material.shader != null ? material.shader.name : "none", DistanceM = hit.distance
                    };
                }
            }
            catch (Exception)
            {
                // A ray that cannot be cast is no reading.
            }
            return none;
        }

        // The angle between the flight camera's forward and the direction to the sun, in degrees (NaN where either cannot be read).
        private static double ReadSunAngle()
        {
            if (HighLogic.LoadedScene != GameScenes.FLIGHT || FlightGlobals.fetch == null) return double.NaN;
            Camera cam = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null;
            Vessel v = FlightGlobals.ActiveVessel;
            CelestialBody sun = GameGlobals.Sun;
            if (cam == null || v == null || sun == null) return double.NaN;
            Vector3d toSun = (sun.position - v.GetWorldPos3D()).normalized;
            Vector3d forward = cam.transform.forward;
            return Math.Acos(Math.Max(-1.0, Math.Min(1.0, Vector3d.Dot(toSun, forward)))) * 180.0 / Math.PI;
        }

        private static string F(double v)
        {
            return v.ToString("0.####", CultureInfo.InvariantCulture);
        }

        private static double Circular(double a, double b)
        {
            double d = Math.Abs(a - b) % 360.0;
            return d > 180.0 ? 360.0 - d : d;
        }

        private bool Verdict(string name, string what, bool ok, string expected, string read)
        {
            LogLine("ground: " + what + ": expected " + expected + ", read " + read + (ok ? " - ok" : " - FAILED"));
            Emit(Prefix + name + "_ok", ok);
            Check(Prefix + name, ok, ok ? null : "expected " + expected + ", read " + read);
            return ok;
        }

        private IEnumerable<ProbeWait> Poll(Func<bool> condition, double seconds)
        {
            double end = Rig.RealS + seconds;
            while (!condition() && Rig.RealS < end) yield return ProbeWait.Frames(1);
            polled = condition();
        }

        private IEnumerable<ProbeWait> StillAim()
        {
            ViewerPoseReading last = Rig.Pose;
            for (int i = 0; i < AimStillMaxFrames; i++)
            {
                yield return ProbeWait.Frames(2);
                ViewerPoseReading now = Rig.Pose;
                bool still = Circular(last.AzimuthDeg, now.AzimuthDeg) <= SoloProbe.AimStillDeg && Math.Abs(last.ElevationDeg - now.ElevationDeg) <= SoloProbe.AimStillDeg;
                last = now;
                if (still) yield break;
            }
        }

        // One picture by the viewer's capture; taken holds its file name (null where none was written).
        private IEnumerable<ProbeWait> Take()
        {
            taken = null;
            string before = Rig.LastCapture;
            Rig.RequestCapture();
            yield return ProbeWait.Frames(1);
            int guard = 0;
            while (Rig.Capturing && guard++ < 120) yield return ProbeWait.Frames(1);
            foreach (ProbeWait w in Poll(() => Rig.LastCapture != before && Rig.LastCapture.Length > 0 && Rig.FileSize(Rig.CapturePicturePath(Rig.LastCapture)) > MinPictureBytes, PollPictureS)) yield return w;
            if (polled) taken = Rig.LastCapture;
            yield return ProbeWait.Frames(5);
        }

        // What the cover is in this frame: the records of its quads, the bodies its PQS mod stands in, the switches; the largest of each over the frames is judged at the end.
        private void DescribeCover(string frame, string key, GroundReading ground, double aglCamera, double sunAngle)
        {
            int quads = CoverQuadCount(), installed = CoverInstalledCount();
            if (quads > maxQuads) maxQuads = quads;
            if (installed > maxInstalled) maxInstalled = installed;
            LogLine("ground: " + key + ": cover quads " + quads + ", cover PQS mod in " + installed + " bodies, enabled " + (AtmospheresSettings.Enabled ? "on" : "off") + ", groundCover "
                    + (AtmospheresSettings.GroundCover ? "on" : "off") + ", weatherEffects " + (AtmospheresSettings.WeatherEffects ? "on" : "off") + "; camera " + F(aglCamera) + " m above the ground, "
                    + F(sunAngle) + " deg from the sun; ground " + (ground.Hit ? ground.Object + ", material " + ground.Material + ", shader " + ground.Shader + ", " + F(ground.DistanceM) + " m below the ray" : "no hit")
                    + "; UT " + F(Rig.GameUt) + ", sun elevation " + F(Rig.SunElevationDeg));
            Emit(Prefix + frame + "_cover_quads", quads);
            Emit(Prefix + frame + "_cover_bodies", installed);
            Emit(Prefix + frame + "_camera_agl_m", aglCamera);
            Emit(Prefix + frame + "_ground_hit", ground.Hit);
            Emit(Prefix + frame + "_ground_is_parallax", ground.Hit && ground.Shader != null && ground.Shader.IndexOf("Parallax", StringComparison.OrdinalIgnoreCase) >= 0);
            if (!double.IsNaN(sunAngle)) Emit(Prefix + frame + "_sun_angle_deg", sunAngle);
        }

        // One frame: the pose, the wait for the hold and the aim, the readings, the picture.
        private IEnumerable<ProbeWait> TakeFrame(Frame f, Dictionary<string, string> pictures, double sun)
        {
            Rig.PlacePose(new ViewerPose { Name = f.Ground ? "ground" : "cb_far", AglM = f.AglM, ElevationDeg = f.ElevationDeg, HeadingDeg = f.Heading, CameraDistM = f.CameraDistM });
            yield return ProbeWait.Frames(2);
            foreach (ProbeWait w in Poll(() => !Rig.PlacementPending && Rig.HoldStable, PollPlaceS)) yield return w;
            yield return ProbeWait.Frames(SettleFrames);
            foreach (ProbeWait w in StillAim()) yield return w;
            // The terrain under a pose that was just placed builds and the cover repaints within a budget of a few ms a frame.
            yield return ProbeWait.Seconds(SettleS);
            ViewerPoseReading aim = Rig.Pose;
            GroundReading ground = GroundReader();
            double angle = SunAngleReader();
            DescribeCover(f.Name, "ground_" + f.Name, ground, aim.CameraAglM, angle);
            foreach (ProbeWait w in Take()) yield return w;
            pictures[f.Name] = taken;
            LogLine("ground: " + f.Name + ": picture " + (taken ?? "none"));
            ViewerPoseReading end = Rig.Pose;
            Verdict(f.Name + "_same_aim", f.Name + ": the camera did not move while the picture was taken", Circular(end.AzimuthDeg, aim.AzimuthDeg) <= AimToleranceDeg && Math.Abs(end.ElevationDeg - aim.ElevationDeg) <= AimToleranceDeg,
                    "within " + F(AimToleranceDeg) + " deg of its aim before the picture", "azimuth " + F(end.AzimuthDeg) + " (" + F(aim.AzimuthDeg) + "), elevation " + F(end.ElevationDeg) + " (" + F(aim.ElevationDeg) + ")");
            double lo = f.Ground ? GroundAglMinM : f.WantedCameraAglM - FarAglToleranceM, hi = f.Ground ? GroundAglMaxM : f.WantedCameraAglM + FarAglToleranceM;
            Verdict(f.Name + "_camera_height", f.Name + ": the camera stands at the height of the pose", aim.CameraAglM >= lo && aim.CameraAglM <= hi, F(lo) + " to " + F(hi) + " m above the ground", F(aim.CameraAglM) + " m");
            double sunEnd = Rig.SunElevationDeg;
            Verdict(f.Name + "_sun_fixed", f.Name + ": the sun did not move", !double.IsNaN(sunEnd) && Math.Abs(sunEnd - sun) <= SunDriftDeg, F(sun) + " +-" + F(SunDriftDeg) + " deg", F(sunEnd) + " deg");
        }

        // The cover as the start asks for it: where it is off at load its PQS mod is in no sphere and no quad has a record; where it is on it is in the sphere of Kerbin and has painted.
        private void JudgeCover()
        {
            switch (StartId)
            {
                case StartOn:
                    Verdict("cover_state", "the cover is on in the control start", maxInstalled > 0 && maxQuads > 0, "its PQS mod in a sphere and quad records over 0", "bodies " + maxInstalled + ", quads " + maxQuads);
                    break;
                case StartOff:
                case StartNoCover:
                    Verdict("cover_state", "the cover is off from load in the " + StartId + " start", maxInstalled == 0 && maxQuads == 0, "its PQS mod in no sphere and no quad records", "bodies " + maxInstalled + ", quads " + maxQuads);
                    break;
                default:
                    Verdict("cover_state", "the start id is one of the three this probe knows", false, StartOn + ", " + StartOff + " or " + StartNoCover, StartId ?? "none");
                    break;
            }
        }

        // The setting the start carries on its command line took effect before the scene: read here, in the flight, as the game holds it.
        private void JudgeLaunchSettings()
        {
            bool enabled = AtmospheresSettings.Enabled, cover = AtmospheresSettings.GroundCover;
            bool wantEnabled = StartId != StartOff, wantCover = StartId != StartNoCover;
            Verdict("launch_pin", "the setting of the command line is in force", enabled == wantEnabled && cover == wantCover && AtmospheresSettings.WeatherEffects,
                    "enabled " + (wantEnabled ? "on" : "off") + ", groundCover " + (wantCover ? "on" : "off") + ", weatherEffects on", "enabled " + (enabled ? "on" : "off") + ", groundCover " + (cover ? "on" : "off")
                    + ", weatherEffects " + (AtmospheresSettings.WeatherEffects ? "on" : "off"));
        }

        protected override IEnumerator<ProbeWait> Scenario()
        {
            JudgeLaunchSettings();
            yield return ProbeWait.Until(() => Rig.FlightReady, 300, "the flight scene");
            reached = true;
            yield return ProbeWait.Seconds(3);
            // With the package on the own pass draws; the frames then hold every layer empty as K-S2's baseline does. Off, there is no pass and nothing to hold.
            if (AtmospheresSettings.Enabled)
                foreach (ProbeWait w in Poll(() => PassDraws, PollPassS)) yield return w;
            LogLine("ground: the own cloud pass " + (PassDraws ? "draws" : "does not draw") + " in this start");

            Rig.ToggleWindow();
            foreach (ProbeWait w in Poll(() => Rig.WindowOpen && Rig.SessionActive, PollWindowS)) yield return w;
            if (!Verdict("window_opens", "the viewer opens", polled, "window and session within " + F(PollWindowS) + " s", "window " + Rig.WindowOpen + ", session " + Rig.SessionActive)) yield break;
            if (PassDraws) Rig.FreezeClouds(true);
            Rig.PlacePose(new ViewerPose { Name = "cb_far", AglM = FarAglM, ElevationDeg = FarElevationDeg, HeadingDeg = HeadingToward, CameraDistM = FarCameraDistM });
            yield return ProbeWait.Frames(2);
            foreach (ProbeWait w in Poll(() => !Rig.PlacementPending && Rig.HoldStable, PollPlaceS)) yield return w;

            // K-S2's universal time where the sun stands in its band there, else the first offset that does (K-S1's search).
            double sun = double.NaN;
            double natural = Rig.ClockNatural;
            if (!double.IsNaN(natural))
            {
                Rig.SetTimeOffset(K1Ut - natural);
                yield return ProbeWait.Seconds(1.5);
                double e = Rig.SunElevationDeg;
                if (!double.IsNaN(e) && Math.Abs(e - SoloProbe.SunTargetDeg) <= SoloProbe.SunBandDeg) sun = e;
            }
            double previous = double.NaN;
            for (int k = 0; k <= SoloProbe.SunSteps && double.IsNaN(sun); k++)
            {
                Rig.SetTimeOffset(k * SoloProbe.SunStepS);
                yield return ProbeWait.Seconds(1.5);
                double e = Rig.SunElevationDeg;
                if (!double.IsNaN(e) && !double.IsNaN(previous) && Math.Abs(e - SoloProbe.SunTargetDeg) <= SoloProbe.SunBandDeg && e < previous) sun = e;
                previous = e;
            }
            if (!Verdict("sun_found", "a time puts the sun at the stated elevation", !double.IsNaN(sun), F(SoloProbe.SunTargetDeg) + " deg", double.IsNaN(sun) ? "none" : F(sun) + " deg")) yield break;
            Rig.FreezeTime(true);
            yield return ProbeWait.Seconds(1);
            Emit(Prefix + "sun_elevation_deg", sun);
            Emit(Prefix + "ut_s", Rig.GameUt);

            // Where the pass draws, the cell's type as K-S2 holds it: every layer empty.
            ISoloRig solo = Rig as ISoloRig;
            if (solo != null && PassDraws)
            {
                SoloEntry? found = null;
                foreach (SoloEntry e in solo.SoloEntries)
                    if (string.Equals(e.TypeName, SurfaceProbe.TypeName, StringComparison.OrdinalIgnoreCase)) found = e;
                if (found.HasValue)
                {
                    solo.SetSoloCellAhead(SurfaceProbe.CellAheadKm);
                    solo.SetSolo(found.Value.Layer, found.Value.Type);
                    solo.SetSoloStrength(0f);
                    int bit = 1 << found.Value.Layer;
                    string layer = found.Value.LayerName;
                    foreach (ProbeWait w in Poll(() => PassDraws && Math.Abs(Rig.PassMapOverride(layer)) <= SoloProbe.MapReadTolerance && solo.PassLayerMask == bit, PollMapS)) yield return w;
                    Verdict("empty_frame", "the frames have every layer empty", polled, "the solo layer's forced map 0 and the pass's layer mask the layer's bit", "map " + F(Rig.PassMapOverride(layer)) + ", mask " + solo.PassLayerMask);
                }
                else LogLine("ground: the type " + SurfaceProbe.TypeName + " is not in the stack; the frames keep the field's clouds");
            }

            // The ground the cover repaints builds up over a few seconds after the first pose: the first frame waits for it.
            yield return ProbeWait.Seconds(CoverSettleS);
            var pictures = new Dictionary<string, string>();
            foreach (Frame f in Frames)
                foreach (ProbeWait w in TakeFrame(f, pictures, sun)) yield return w;

            JudgeCover();
            int written = 0;
            foreach (KeyValuePair<string, string> pair in pictures) if (pair.Value != null) written++;
            Verdict("pictures", "every frame's picture is written", written == Frames.Length, Frames.Length + " pictures", written + " pictures");
            double a1 = Emitted("far_p1_sun_angle_deg"), a2 = Emitted("far_p2_sun_angle_deg");
            Verdict("poses_apart", "the two headings look at the sun from different angles", !double.IsNaN(a1) && !double.IsNaN(a2) && Math.Abs(a1 - a2) >= PosesApartDeg,
                    "at least " + F(PosesApartDeg) + " deg apart", "far_p1 " + F(a1) + ", far_p2 " + F(a2));
            Rig.ToggleWindow();
            foreach (ProbeWait w in Poll(() => !Rig.WindowOpen && !Rig.SessionActive, PollWindowS)) yield return w;
            Emit(Prefix + "elapsed_s", Elapsed);
        }

        private double Emitted(string name)
        {
            double v;
            return emitted.TryGetValue(name, out v) ? v : double.NaN;
        }

        private readonly Dictionary<string, double> emitted = new Dictionary<string, double>();

        private new void Emit(string metric, double value)
        {
            if (metric.StartsWith(Prefix, StringComparison.Ordinal)) emitted[metric.Substring(Prefix.Length)] = value;
            base.Emit(metric, value);
        }
    }
}
