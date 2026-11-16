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
        // The height of the point the ray hit above sea level, and how far the camera stands over it (NaN where there is no hit).
        internal double AltAsl, CameraOverM;
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
        // The ground-level pose, as tools/ProbeLite poses its low views: the camera alone (the flight camera's transform, the vessel is not moved), GroundCameraDistM behind the pad
        // along the heading, looking GroundElevationDeg up, GroundOverColliderM above the ground collider found under it. The first hold is at GroundCameraAglM over the PQS terrain;
        // the ray under the camera then gives the collider (the pad's apron and the lawn lie 3.5 to 5.3 m over the PQS terrain there: a height over the PQS terrain put the camera inside
        // the deck, 0.27 m over it, in the first run) and the camera is held again at its height + GroundOverColliderM. The ground 40 m from the pad is the pad's concrete (green share about 1
        // in tools/ProbeLite's pictures), 60 m from it lawn (+19): the camera stands 90 m from the pad. Headings 90 and 180 (270 looks along the road to the VAB, concrete).
        internal const double GroundCameraAglM = 5.2, GroundElevationDeg = 1.5, GroundCameraDistM = 90.0, GroundOverColliderM = 2.0, GroundHeadingA = 90.0, GroundHeadingB = 180.0;
        // The camera of a ground-level frame is within these metres above the PQS terrain; a far frame within FarAglToleranceM of 290. Its clearance over the collider under it is within
        // GroundClearMinM and GroundClearMaxM after the second hold: under the lawn or deck is a failed view.
        internal const double GroundAglMinM = 1.5, GroundAglMaxM = 15.0, GroundClearMinM = 1.2, GroundClearMaxM = 3.0, FarAglToleranceM = 15.0;
        internal const double AimToleranceDeg = 0.5, SunDriftDeg = 1.0, PosesApartDeg = 60.0, RayAheadM = 30.0, RayUpM = 20.0, RayLengthM = 450.0;
        internal const double PollPlaceS = 60.0, PollMapS = 20.0, PollPictureS = 15.0, SettleS = 3.0, PollWindowS = 10.0, PollPassS = 40.0, CoverSettleS = 20.0;
        internal const int SettleFrames = 60, AimStillMaxFrames = 80, MinPictureBytes = 1000;

        // The start ids this probe knows and what each says of the cover: on (the PQS mod is in the sphere and paints) or off (it is in none).
        internal const string StartOn = "ground_on", StartOff = "ground_off", StartNoCover = "ground_nocover";
        // The capture A/B start: the package off from load, the far_p2 pose taken three ways at K-S2's time: the camera alone with the vessel on the pad (tools/ProbeLite's way), then
        // with the viewer's placement (the vessel held at 300 m), the viewer's own picture and a screen read-back in the same frame.
        internal const string StartCaptureAb = "capture_ab";
        // A's ground has had minutes to build when it is read (the viewer opens after B); B is the first picture after the scene starts: tools/ProbeLite settles its first flight pose for at
        // least 30 s, and so does B. A settle that ends at maxS has not settled: the log says so.
        internal const double SettleMinS = 6.0, SettleMaxS = 30.0, FirstSettleMinS = 30.0, FirstSettleMaxS = 90.0, SettleLumaStep = 0.6, GrabWaitS = 10.0, CaptureDifferenceMax = 4.0;
        internal const double AimToleranceFarDeg = 2.0, UiLumaFloor = 5.0;
        // The green share (mean G - (R + B) / 2 over the lower-left box, x 0-33 %, bottom 15 %) the far_p2 picture has to reach. Measured on the pictures of 2026-11-16 in that box:
        // the ground of a flight reached through the space centre reads +15.5 (far_p2, tools/ProbeLite) and +17.2 (far_p1), the one straight from the main menu +3.7 (K-S2's far_p2) and
        // +5.1 (far_p1); the viewer's picture is about 14 % darker than a screen read, which scales the green side down to about +13. The middle of the two, rounded down: 9.
        // (tools/vanilla_ground.py's low_green is another box, the lowest quarter over the whole width: +11.1 and +2.4.)
        internal const double GroundGreenMin = 9.0;

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
            new Frame { Name = "g2_p1", Heading = GroundHeadingA, AglM = VesselAglFor(GroundCameraAglM, GroundElevationDeg, GroundCameraDistM), CameraDistM = GroundCameraDistM, ElevationDeg = GroundElevationDeg, WantedCameraAglM = GroundCameraAglM, Ground = true },
            new Frame { Name = "g2_p2", Heading = GroundHeadingB, AglM = VesselAglFor(GroundCameraAglM, GroundElevationDeg, GroundCameraDistM), CameraDistM = GroundCameraDistM, ElevationDeg = GroundElevationDeg, WantedCameraAglM = GroundCameraAglM, Ground = true }
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
        internal Func<double, GroundReading> GroundReader = ReadGameGround;
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

        // The ground under the point aheadM in front of the camera (horizontally; 0: straight under it): a ray straight down from RayUpM over that point. Colliders of vessel parts
        // and of the scaled space (layer 10) are not ground. The ground is the highest collider within LawnWindowM of the PQS terrain there (the lawn lies 2.7 to 3.2 m over it, the
        // pad's apron at it; the first collider from above can be the pad's tower or a building, as tools/ProbeLite found); where none is in the window, the first collider.
        internal const double LawnWindowM = 6.0;
        private const int ScaledSpaceLayer = 10;

        private static GroundReading ReadGameGround(double aheadM)
        {
            var none = new GroundReading { Object = "", Material = "", Shader = "", DistanceM = double.NaN, AltAsl = double.NaN, CameraOverM = double.NaN };
            try
            {
                Camera cam = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null;
                CelestialBody body = FlightGlobals.currentMainBody;
                if (cam == null || body == null) return none;
                Vector3 position = cam.transform.position;
                Vector3 up = (position - body.position).normalized;
                Vector3 forward = Vector3.ProjectOnPlane(cam.transform.forward, up).normalized;
                Vector3 origin = position + forward * (float)aheadM + up * (float)RayUpM;
                double camLat, camLon, camAlt, oLat, oLon, oAlt;
                body.GetLatLonAlt(position, out camLat, out camLon, out camAlt);
                body.GetLatLonAlt(origin, out oLat, out oLon, out oAlt);
                double terrain = body.TerrainAltitude(oLat, oLon, true);
                RaycastHit[] hits = Physics.RaycastAll(origin, -up, (float)RayLengthM);
                Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
                GroundReading first = none, lawn = none;
                foreach (RaycastHit hit in hits)
                {
                    if (hit.collider == null || hit.collider.gameObject.layer == ScaledSpaceLayer || hit.collider.GetComponentInParent<Part>() != null) continue;
                    Renderer renderer = hit.collider.GetComponent<Renderer>() ?? hit.collider.GetComponentInParent<Renderer>();
                    Material material = renderer != null ? renderer.sharedMaterial : null;
                    double lat, lon, alt;
                    body.GetLatLonAlt(hit.point, out lat, out lon, out alt);
                    var reading = new GroundReading
                    {
                        Hit = true, Object = hit.collider.gameObject.name, Material = material != null ? material.name : "none",
                        Shader = material != null && material.shader != null ? material.shader.name : "none", DistanceM = hit.distance, AltAsl = alt, CameraOverM = camAlt - alt
                    };
                    if (!first.Hit) first = reading;
                    if (alt >= terrain - 1.0 && alt <= terrain + LawnWindowM && (!lawn.Hit || alt > lawn.AltAsl)) lawn = reading;
                }
                return lawn.Hit ? lawn : first;
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

        // The active vessel and what is left of it, for the log of every frame: the pictures of K-S2 show the pad's clamps and no rocket (the first picture of 2026-11-15 and the one
        // of the first space-centre run), and nothing in the log says where the rocket went.
        internal Func<string> VesselDescriber = ReadVessel;

        private static string ReadVessel()
        {
            try
            {
                Vessel v = FlightGlobals.ActiveVessel;
                if (v == null) return "no active vessel";
                int engines = 0, pods = 0, clamps = 0;
                foreach (Part p in v.parts)
                {
                    string name = p.partInfo != null ? p.partInfo.name : p.name;
                    if (name.IndexOf("clamp", StringComparison.OrdinalIgnoreCase) >= 0) clamps++;
                    else if (name.IndexOf("pod", StringComparison.OrdinalIgnoreCase) >= 0) pods++;
                    if (p.FindModuleImplementing<ModuleEngines>() != null) engines++;
                }
                return v.vesselName + ": " + v.parts.Count + " parts (" + pods + " pods, " + engines + " engines, " + clamps + " clamps), " + v.situation + ", " + v.altitude.ToString("0.#", CultureInfo.InvariantCulture)
                       + " m, mass " + v.GetTotalMass().ToString("0.#", CultureInfo.InvariantCulture) + " t, packed " + v.packed + "; vessels in the scene " + FlightGlobals.Vessels.Count;
            }
            catch (Exception e)
            {
                return "the vessel could not be read (" + e.GetType().Name + ")";
            }
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
                    + "; UT " + F(Rig.GameUt) + ", sun elevation " + F(Rig.SunElevationDeg) + "; " + VesselDescriber());
            Emit(Prefix + frame + "_cover_quads", quads);
            Emit(Prefix + frame + "_cover_bodies", installed);
            Emit(Prefix + frame + "_camera_agl_m", aglCamera);
            Emit(Prefix + frame + "_ground_hit", ground.Hit);
            Emit(Prefix + frame + "_ground_is_parallax", ground.Hit && ground.Shader != null && ground.Shader.IndexOf("Parallax", StringComparison.OrdinalIgnoreCase) >= 0);
            if (!double.IsNaN(sunAngle)) Emit(Prefix + frame + "_sun_angle_deg", sunAngle);
        }

        // One frame: the pose, the wait for the hold and the aim, the readings, the picture.
        // A ground-level pose by the camera alone: held at the first guess, the collider under it read, held again 2 m over that collider, the ground settled with the interface away.
        private IEnumerable<ProbeWait> PlaceGround(Frame f)
        {
            Host.HideUi();
            Host.HoldCamera(f.Heading, f.ElevationDeg, f.CameraDistM, GroundCameraAglM);
            yield return ProbeWait.Frames(SettleFrames);
            yield return ProbeWait.Seconds(SettleS);
            GroundReading under = GroundReader(0.0);
            if (under.Hit && !double.IsNaN(under.AltAsl))
            {
                Host.HoldCameraAbsolute(f.Heading, f.ElevationDeg, f.CameraDistM, under.AltAsl + GroundOverColliderM);
                LogLine("ground: " + f.Name + ": the camera was " + F(under.CameraOverM) + " m over " + under.Object + " at the first hold; held again " + F(GroundOverColliderM) + " m over it (" + F(under.AltAsl + GroundOverColliderM) + " m above sea level)");
                yield return ProbeWait.Frames(SettleFrames);
            }
            else LogLine("ground: " + f.Name + ": no collider under the camera at the first hold; the first height stays");
            foreach (ProbeWait w in SettleScreen(f.Name, SettleMinS, SettleMaxS)) yield return w;
        }

        private IEnumerable<ProbeWait> TakeFrame(Frame f, Dictionary<string, string> pictures, double sun)
        {
            if (f.Ground)
            {
                foreach (ProbeWait w in PlaceGround(f)) yield return w;
            }
            else
            {
                Rig.PlacePose(new ViewerPose { Name = "cb_far", AglM = f.AglM, ElevationDeg = f.ElevationDeg, HeadingDeg = f.Heading, CameraDistM = f.CameraDistM });
                yield return ProbeWait.Frames(2);
                foreach (ProbeWait w in Poll(() => !Rig.PlacementPending && Rig.HoldStable, PollPlaceS)) yield return w;
                yield return ProbeWait.Frames(SettleFrames);
                foreach (ProbeWait w in StillAim()) yield return w;
                // The terrain under a pose that was just placed builds and the cover repaints within a budget of a few ms a frame.
                yield return ProbeWait.Seconds(SettleS);
            }
            ViewerPoseReading aim = Rig.Pose;
            GroundReading ground = GroundReader(RayAheadM);
            GroundReading under = f.Ground ? GroundReader(0.0) : ground;
            double angle = SunAngleReader();
            DescribeCover(f.Name, "ground_" + f.Name, ground, aim.CameraAglM, angle);
            foreach (ProbeWait w in Take()) yield return w;
            pictures[f.Name] = taken;
            // How green the ground of the picture the viewer wrote is (the number tools/vanilla_ground.py reads): the series shows by itself when the ground is the wrong one.
            ProbeScreen written = taken != null ? PictureReader(Rig.CapturePicturePath(taken)) : null;
            double green = written != null ? GroundRegion.GreenShare(written.Pixels, written.Width, written.Height) : double.NaN;
            Emit(Prefix + f.Name + "_green_share", green);
            if (f.Name == "far_p2") Emit(Prefix + "green_share", green);
            LogLine("ground: " + f.Name + ": green share " + F(green) + " (mean G - (R + B) / 2 over the lower left, x 0-33 %, bottom 15 %)");
            LogLine("ground: " + f.Name + ": picture " + (taken ?? "none"));
            ViewerPoseReading end = Rig.Pose;
            Verdict(f.Name + "_same_aim", f.Name + ": the camera did not move while the picture was taken", Circular(end.AzimuthDeg, aim.AzimuthDeg) <= AimToleranceDeg && Math.Abs(end.ElevationDeg - aim.ElevationDeg) <= AimToleranceDeg,
                    "within " + F(AimToleranceDeg) + " deg of its aim before the picture", "azimuth " + F(end.AzimuthDeg) + " (" + F(aim.AzimuthDeg) + "), elevation " + F(end.ElevationDeg) + " (" + F(aim.ElevationDeg) + ")");
            double lo = f.Ground ? GroundAglMinM : f.WantedCameraAglM - FarAglToleranceM, hi = f.Ground ? GroundAglMaxM : f.WantedCameraAglM + FarAglToleranceM;
            Verdict(f.Name + "_camera_height", f.Name + ": the camera stands at the height of the pose", aim.CameraAglM >= lo && aim.CameraAglM <= hi, F(lo) + " to " + F(hi) + " m above the ground", F(aim.CameraAglM) + " m");
            if (f.Ground)
            {
                // The collider under the camera (the lawn or the pad's apron within 6 m of the PQS terrain), as the ray read it with the camera held.
                double clear = under.Hit ? under.CameraOverM : double.NaN;
                Verdict(f.Name + "_clearance", f.Name + ": the camera stands over the ground collider under it", !double.IsNaN(clear) && clear >= GroundClearMinM && clear <= GroundClearMaxM,
                        F(GroundClearMinM) + " to " + F(GroundClearMaxM) + " m over the ground collider", under.Hit ? F(clear) + " m over " + under.Object : "no collider found");
            }
            double sunEnd = Rig.SunElevationDeg;
            Verdict(f.Name + "_sun_fixed", f.Name + ": the sun did not move", !double.IsNaN(sunEnd) && Math.Abs(sunEnd - sun) <= SunDriftDeg, F(sun) + " +-" + F(SunDriftDeg) + " deg", F(sunEnd) + " deg");
            if (f.Ground)
            {
                // The game's camera and interface are back before the next frame's pose.
                Host.ReleaseCamera();
                Host.ShowUi();
            }
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
                case StartCaptureAb:
                    Verdict("cover_state", "the cover is off from load in the " + StartId + " start", maxInstalled == 0 && maxQuads == 0, "its PQS mod in no sphere and no quad records", "bodies " + maxInstalled + ", quads " + maxQuads);
                    break;
                default:
                    Verdict("cover_state", "the start id is one of the three this probe knows", false, StartOn + ", " + StartOff + " or " + StartNoCover, StartId ?? "none");
                    break;
            }
        }

        // The way the flight was reached: where the command line asks for the space centre the run went through it, else straight from the main menu. The ground of the KSC is
        // judged only on the way a player's flight is reached.
        internal const string ViaSpaceCentreSwitch = "-redefinitionProbeViaSpaceCentre";
        internal Func<string> ScenePathReader = () => ScenePathWatcher.Current;

        private void JudgeScenePath()
        {
            bool asked = CommandLine.Has(Args(), ViaSpaceCentreSwitch, true);
            string path = ScenePathReader();
            LogLine("ground: scene path: " + path);
            Verdict("scene_path", "the flight was reached the way the start asks for", path == (asked ? ScenePathWatcher.SpaceCentre : ScenePathWatcher.Direct),
                    asked ? "space centre" : "direct", path);
        }

        // The setting the start carries on its command line took effect before the scene: read here, in the flight, as the game holds it.
        private void JudgeLaunchSettings()
        {
            bool enabled = AtmospheresSettings.Enabled, cover = AtmospheresSettings.GroundCover;
            bool wantEnabled = StartId != StartOff && StartId != StartCaptureAb, wantCover = StartId != StartNoCover;
            Verdict("launch_pin", "the setting of the command line is in force", enabled == wantEnabled && cover == wantCover && AtmospheresSettings.WeatherEffects,
                    "enabled " + (wantEnabled ? "on" : "off") + ", groundCover " + (wantCover ? "on" : "off") + ", weatherEffects on", "enabled " + (enabled ? "on" : "off") + ", groundCover " + (cover ? "on" : "off")
                    + ", weatherEffects " + (AtmospheresSettings.WeatherEffects ? "on" : "off"));
        }

        protected override IEnumerator<ProbeWait> Scenario()
        {
            JudgeLaunchSettings();
            yield return ProbeWait.Until(() => Rig.FlightReady, 300, "the flight scene");
            reached = true;
            JudgeScenePath();
            yield return ProbeWait.Seconds(3);
            // With the package on the own pass draws; the frames then hold every layer empty as K-S2's baseline does. Off, there is no pass and nothing to hold.
            if (AtmospheresSettings.Enabled)
                foreach (ProbeWait w in Poll(() => PassDraws, PollPassS)) yield return w;
            LogLine("ground: the own cloud pass " + (PassDraws ? "draws" : "does not draw") + " in this start");

            // The capture A/B start takes its camera-only picture first, with the vessel on the pad and no viewer session (the way tools/ProbeLite poses its camera).
            bool ab = StartId == StartCaptureAb;
            if (ab)
                foreach (ProbeWait w in CameraOnlyStep()) yield return w;

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
            if (ab)
            {
                foreach (ProbeWait w in ViewerAndScreenStep(sun)) yield return w;
                maxQuads = Math.Max(maxQuads, CoverQuadCount());
                maxInstalled = Math.Max(maxInstalled, CoverInstalledCount());
                JudgeCover();
                Rig.ToggleWindow();
                foreach (ProbeWait w in Poll(() => !Rig.WindowOpen && !Rig.SessionActive, PollWindowS)) yield return w;
                Emit(Prefix + "elapsed_s", Elapsed);
                yield break;
            }
            var pictures = new Dictionary<string, string>();
            foreach (Frame f in Frames)
                foreach (ProbeWait w in TakeFrame(f, pictures, sun)) yield return w;

            JudgeCover();
            double greenFar = Emitted("far_p2_green_share");
            Verdict("ground_green", "the ground of the far_p2 picture reads green (the ground a player's flight shows)", !double.IsNaN(greenFar) && greenFar >= GroundGreenMin,
                    "green share at least " + F(GroundGreenMin), F(greenFar));
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

        // --- The capture A/B start ---

        private IProbeHost host;
        private ProbeScreen grabbed;

        internal IProbeHost Host
        {
            get { return host ?? (host = GameProbeHost.Get()); }
            set { host = value; }
        }

        // What a test replaces: a picture file read as pixels, and a picture written to a file.
        internal Func<string, ProbeScreen> PictureReader = ReadPicture;
        internal Action<string, byte[]> PictureWriter = WritePicture;

        private static ProbeScreen ReadPicture(string path)
        {
            try
            {
                if (!System.IO.File.Exists(path)) return null;
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                try
                {
                    if (!tex.LoadImage(System.IO.File.ReadAllBytes(path))) return null;
                    return new ProbeScreen { Width = tex.width, Height = tex.height, Pixels = tex.GetPixels32() };
                }
                finally
                {
                    UnityEngine.Object.Destroy(tex);
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void WritePicture(string path, byte[] png)
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
            System.IO.File.WriteAllBytes(path, png);
        }

        // The screen as the game drew it at the end of this frame.
        private IEnumerable<ProbeWait> GrabNow(bool png)
        {
            ProbeScreen got = null;
            bool done = false;
            Host.Grab(() => true, s => { got = s; done = true; }, png);
            foreach (ProbeWait w in Poll(() => done, GrabWaitS)) yield return w;
            grabbed = done ? got : null;
        }

        // Screens once a second until the ground's luma stands still (at least minS, at most maxS): what tools/ProbeLite waits for; the last screen is in 'grabbed'.
        private IEnumerable<ProbeWait> SettleScreen(string label, double minS, double maxS)
        {
            double t0 = Rig.RealS, previous = double.NaN;
            int stable = 0;
            var lumas = new System.Text.StringBuilder();
            while (true)
            {
                foreach (ProbeWait w in GrabNow(false)) yield return w;
                ProbeScreen s = grabbed;
                if (s == null) yield break;
                double r, g, b;
                GroundRegion.Mean(s.Pixels, s.Width, s.Height, out r, out g, out b);
                double luma = 0.2126 * r + 0.7152 * g + 0.0722 * b;
                double elapsed = Rig.RealS - t0;
                if (lumas.Length > 0) lumas.Append(' ');
                lumas.Append(F(luma));
                stable = elapsed >= minS && !double.IsNaN(previous) && Math.Abs(luma - previous) < SettleLumaStep ? stable + 1 : 0;
                previous = luma;
                bool settled = stable >= 2;
                if (settled || elapsed >= maxS)
                {
                    // The picture that is kept: the last grab, encoded.
                    foreach (ProbeWait w in GrabNow(true)) yield return w;
                    LogLine("ground: " + label + " settle " + (settled ? "stable" : "LIMIT, not settled") + " after " + F(elapsed) + " s; ground luma per second: " + lumas);
                    yield break;
                }
                yield return ProbeWait.Seconds(1);
            }
        }

        private double bR = double.NaN, bG = double.NaN, bB = double.NaN;

        // The far_p2 pose by the camera alone, the vessel on the pad: the universal time is K-S2's, the clouds and the viewer are not touched.
        private IEnumerable<ProbeWait> CameraOnlyStep()
        {
            ForceUniversalTime(K1Ut);
            yield return ProbeWait.Seconds(1.5);
            double cameraAgl = FarAglM - FarCameraDistM * Math.Sin(FarElevationDeg * Math.PI / 180.0);
            // The interface is away as it is for the viewer's capture (the stage stack lies in the measured region).
            Host.HideUi();
            Host.HoldCamera(HeadingAway, FarElevationDeg, FarCameraDistM, cameraAgl);
            yield return ProbeWait.Frames(SettleFrames);
            foreach (ProbeWait w in SettleScreen("B", FirstSettleMinS, FirstSettleMaxS)) yield return w;
            ProbeScreen shot = grabbed;
            ViewerPoseReading pose = Rig.Pose;
            bool read = shot != null && shot.Pixels != null;
            double lr = double.NaN, lg = double.NaN, lb = double.NaN;
            if (read) GroundRegion.Mean(shot.Pixels, shot.Width, shot.Height, out lr, out lg, out lb);
            bool lit = read && 0.2126 * lr + 0.7152 * lg + 0.0722 * lb > UiLumaFloor;
            Verdict("B_screen_read", "B (camera alone, vessel on the pad): the screen was read and is not black", lit, "a screen picture with a lit ground region", read ? shot.Width + "x" + shot.Height + ", ground " + F(lr) + " " + F(lg) + " " + F(lb) : "none");
            Verdict("B_ui_hidden", "B: the game's interface is away", !Host.UiShowing, "no interface showing", Host.UiShowing ? "showing" : "hidden");
            Verdict("B_aim", "B: the camera looks along the far pose's heading and elevation", Circular(pose.AzimuthDeg, HeadingAway) <= AimToleranceFarDeg && Math.Abs(pose.ElevationDeg - FarElevationDeg) <= AimToleranceFarDeg,
                    "azimuth " + F(HeadingAway) + " and elevation " + F(FarElevationDeg) + " within " + F(AimToleranceFarDeg) + " deg", "azimuth " + F(pose.AzimuthDeg) + ", elevation " + F(pose.ElevationDeg));
            Verdict("B_camera_height", "B: the camera stands at the height of the far pose", pose.CameraAglM >= cameraAgl - FarAglToleranceM && pose.CameraAglM <= cameraAgl + FarAglToleranceM,
                    F(cameraAgl - FarAglToleranceM) + " to " + F(cameraAgl + FarAglToleranceM) + " m above the ground", F(pose.CameraAglM) + " m (vessel " + F(pose.VesselAglM) + " m above the ground)");
            if (read)
            {
                GroundRegion.Mean(shot.Pixels, shot.Width, shot.Height, out bR, out bG, out bB);
                Emit(Prefix + "B_ground_r", bR);
                Emit(Prefix + "B_ground_g", bG);
                Emit(Prefix + "B_ground_b", bB);
                WriteScreen("capture_ab_B_camera_only", shot);
            }
            Emit(Prefix + "B_sun_elevation_deg", Rig.SunElevationDeg);
            LogLine("ground: capture_ab: B camera alone, vessel " + F(pose.VesselAglM) + " m over the ground, camera " + F(pose.CameraAglM) + " m, azimuth " + F(pose.AzimuthDeg) + ", ground mean " + F(bR) + " " + F(bG) + " " + F(bB)
                    + ", sun " + F(Rig.SunElevationDeg) + " deg, UT " + F(Rig.GameUt) + "; cameras: " + Host.DescribeCameras());
            Host.ReleaseCamera();
            Host.ShowUi();
            yield return ProbeWait.Seconds(SettleS);
        }

        private void WriteScreen(string stem, ProbeScreen shot)
        {
            if (shot == null || shot.Png == null) return;
            try
            {
                PictureWriter(Rig.CapturePicturePath(stem), shot.Png);
            }
            catch (Exception e)
            {
                LogLine("ground: the screen picture " + stem + " could not be written (" + e.Message + ")");
            }
        }

        // The far_p2 pose with the viewer's placement: the viewer's own capture and the screen read back at the end of the very frame the viewer asked the game for its picture.
        private IEnumerable<ProbeWait> ViewerAndScreenStep(double sun)
        {
            Frame f = Frames[1];
            Rig.PlacePose(new ViewerPose { Name = "cb_far", AglM = f.AglM, ElevationDeg = f.ElevationDeg, HeadingDeg = f.Heading, CameraDistM = f.CameraDistM });
            yield return ProbeWait.Frames(2);
            foreach (ProbeWait w in Poll(() => !Rig.PlacementPending && Rig.HoldStable, PollPlaceS)) yield return w;
            yield return ProbeWait.Frames(SettleFrames);
            foreach (ProbeWait w in StillAim()) yield return w;
            Host.HideUi();
            foreach (ProbeWait w in SettleScreen("A", SettleMinS, SettleMaxS)) yield return w;
            ViewerPoseReading pose = Rig.Pose;
            Verdict("A_aim", "A: the camera looks along the far pose's heading and elevation", Circular(pose.AzimuthDeg, HeadingAway) <= AimToleranceFarDeg && Math.Abs(pose.ElevationDeg - FarElevationDeg) <= AimToleranceFarDeg,
                    "azimuth " + F(HeadingAway) + " and elevation " + F(FarElevationDeg) + " within " + F(AimToleranceFarDeg) + " deg", "azimuth " + F(pose.AzimuthDeg) + ", elevation " + F(pose.ElevationDeg));
            double lo = f.WantedCameraAglM - FarAglToleranceM, hi = f.WantedCameraAglM + FarAglToleranceM;
            Verdict("A_camera_height", "A (the viewer's placement): the camera stands at the height of the far pose", pose.CameraAglM >= lo && pose.CameraAglM <= hi, F(lo) + " to " + F(hi) + " m above the ground", F(pose.CameraAglM) + " m (vessel " + F(pose.VesselAglM) + " m above the ground)");

            // The grab is asked for before the viewer's capture: it fires at the end of the frame in which the viewer called ScreenCapture.CaptureScreenshot.
            string before = Rig.LastCapture;
            ProbeScreen screen = null;
            bool got = false;
            Host.Grab(() => Rig.CaptureFrame >= 0 && Rig.CaptureFrame == Rig.Frame && Rig.LastCapture != before, s => { screen = s; got = true; }, true);
            foreach (ProbeWait w in Take()) yield return w;
            foreach (ProbeWait w in Poll(() => got, GrabWaitS)) yield return w;
            Host.ShowUi();
            string name = taken;
            Verdict("A_viewer_picture", "A: the viewer's picture is written", name != null, "a picture file", name ?? "none");
            bool read = got && screen != null && screen.Pixels != null;
            Verdict("A_screen_read", "A: the screen was read in the frame of the viewer's capture", read, "a screen picture", read ? screen.Width + "x" + screen.Height : "none");
            Verdict("A_same_frame", "A: the screen read and the viewer's capture are one frame", read && screen.Frame == Rig.CaptureFrame, "the viewer's capture frame " + Rig.CaptureFrame, read ? "screen frame " + screen.Frame : "none");
            ProbeScreen viewer = name != null ? PictureReader(Rig.CapturePicturePath(name)) : null;
            double vr = double.NaN, vg = double.NaN, vb = double.NaN, sr = double.NaN, sg = double.NaN, sb = double.NaN;
            if (viewer != null) GroundRegion.Mean(viewer.Pixels, viewer.Width, viewer.Height, out vr, out vg, out vb);
            if (read) GroundRegion.Mean(screen.Pixels, screen.Width, screen.Height, out sr, out sg, out sb);
            Verdict("A_picture_sizes", "A: the viewer's picture and the screen read have one size", viewer != null && read && viewer.Width == screen.Width && viewer.Height == screen.Height,
                    "equal sizes", (viewer != null ? viewer.Width + "x" + viewer.Height : "no viewer picture") + " and " + (read ? screen.Width + "x" + screen.Height : "no screen picture"));
            double diff = GroundRegion.MaxDifference(vr, vg, vb, sr, sg, sb), against = GroundRegion.MaxDifference(sr, sg, sb, bR, bG, bB);
            Emit(Prefix + "A_viewer_ground_r", vr);
            Emit(Prefix + "A_viewer_ground_g", vg);
            Emit(Prefix + "A_viewer_ground_b", vb);
            Emit(Prefix + "A_screen_ground_r", sr);
            Emit(Prefix + "A_screen_ground_g", sg);
            Emit(Prefix + "A_screen_ground_b", sb);
            Emit(Prefix + "A_capture_diff_max", diff);
            Emit(Prefix + "A_vs_B_diff_max", against);
            if (read && name != null) WriteScreen(System.IO.Path.GetFileNameWithoutExtension(name) + "_screen", screen);
            LogLine("ground: capture_ab: A viewer placement, vessel " + F(pose.VesselAglM) + " m over the ground, camera " + F(pose.CameraAglM) + " m; ground mean of the viewer's picture " + F(vr) + " " + F(vg) + " " + F(vb)
                    + ", of the screen read " + F(sr) + " " + F(sg) + " " + F(sb) + " (largest channel difference " + F(diff) + (diff <= CaptureDifferenceMax ? ": the same picture" : ": DIFFERENT pictures")
                    + "); against B (camera alone) " + F(against) + " (" + (against <= CaptureDifferenceMax ? "the same ground" : "A and B show different ground") + "); sun " + F(Rig.SunElevationDeg) + " deg (set " + F(sun) + "), UT " + F(Rig.GameUt)
                    + "; cameras: " + Host.DescribeCameras());
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
