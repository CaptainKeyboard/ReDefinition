using System;
using System.IO;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // What the K-T2 probe asks of the running game: the scene, the vessel, the flight camera, the window, the clock and EVE. T09Probe reads the game
    // only through this interface so that tests/ReDefinition.Tests/Atmospheres/T09 can drive the whole scenario with a fake world (the engine
    // cannot run in a test). KspT09World is the one the game uses; each member is the call the probe made before the seam existed.
    internal interface IT09World
    {
        // Null while the game is in the flight scene; else the scene's name (the probe ends when it is left).
        string SceneNotFlight();
        bool FlightCameraPresent();
        // The active vessel is loaded, unpacked and has a main body.
        bool VesselUnpacked();

        bool BodyHasAtmosphere { get; }
        int BodyIndex { get; }
        double BodyRadius { get; }
        double VesselLatitude { get; }
        double VesselLongitude { get; }
        double TerrainAltitude(double lat, double lon);

        double VesselAltitude { get; }
        // Situation and packed state, for the placement lines.
        string VesselState { get; }
        // The vessel at rest in the air over the site, upright; the hold keeps its velocity at the body's rotation until StopHold.
        void PlaceVessel(double lat, double lon, double altitude);
        void StartHold();
        void StopHold();
        // The sun's elevation over the vessel's horizon in degrees.
        double SunElevationDeg();

        // Where the vessel and the flight camera are now, all in one read (the camera the pass draws into and the picture is read from): used at the
        // end of every dwell and around every capture, because the placement check alone says only where the vessel stood a few seconds earlier.
        T09Pose ReadPose();
        // How often the hold moved the vessel back since the last placement and the largest distance it had drifted (metres).
        int HoldCorrections { get; }
        double HoldWorstDriftM { get; }
        // The vessel stands at the placed point and the hold is anchored there; and the hold's state in words (for the log).
        bool HoldReached { get; }
        string HoldStatus { get; }
        // The flight camera's mode as the game names it (AUTO and the mode it resolves to), and forcing the mode whose heading and pitch are relative to the
        // local horizon (above the atmosphere AUTO resolves to ORBITAL, whose axes are the inertial frame's).
        string CameraModeText { get; }
        void UseFreeCamera();

        // The flight camera: compass heading and elevation of its view (0 north, 90 east).
        void CameraAim(out double azimuthDeg, out double elevationDeg);
        float CameraHeading { get; set; }
        float CameraPitch { get; set; }
        void SetCameraDistance(double distance);
        // The pixel size of the flight camera; false where there is none.
        bool CameraPixelSize(out int width, out int height);

        int ScreenWidth { get; }
        int ScreenHeight { get; }
        int VSyncCount { get; set; }
        int TargetFrameRate { get; set; }
        int FrameCount { get; }
        double UniversalTime { get; set; }

        // TVC (EVE's layers).
        bool ScattererLoaded { get; }
        bool EveAvailable { get; }
        int VolumetricLayerCount { get; }
        // EVE's renderer is the effective cloud source and a layer is bound to the maps.
        bool EveLayerBound { get; }
        string EveStatusText { get; }
        bool EveRendererReady { get; }
        int EveLayersBound { get; }
        int EveFailureLines { get; }

        // Whether a moon or planet lights the clouds from the camera's place.
        bool MoonProviderAnswers();

        // The pictures (T09Pictures.cs). Null where the folder can take them; else the reason it cannot (not writable, nearly full).
        string PicturesFolderProblem();
        // Starts the read of the flight camera's picture at the end of this frame; false where no read can start.
        bool CaptureBegin();
        bool CaptureBusy { get; }
        // The picture read (linear colours, row 0 at the bottom), or null where the read failed. Taken once.
        T09Image CaptureTake();
        // Switches the aid view of the own pass on (the layer shader's optical-depth picture over the scene); false where the pass has none.
        bool AidOn();
        void AidOff();
        // K-T2d: the aid picture of a given mode (4: the direct light against the ambient, the light taps), the vertical field of view of the flight camera (degrees),
        // the raw copy of the next read (T09Image.Raw: the values as the target held them), and the log lines about the light and the target (key, text).
        bool AidOn(int mode);
        double CameraFovDeg { get; }
        // Which layers the pass draws (bit i = layer i; 0 = all): a mask that names no layer gives the baseline frame with the pass still running.
        void SetLayerMask(int mask);
        bool KeepRaw { get; set; }
        System.Collections.Generic.IList<System.Collections.Generic.KeyValuePair<string, string>> EnvironmentLines();
        // Writes the picture as a PNG named name, scaled down by the whole factor; returns the size of the file, throws where it cannot write.
        long SavePicture(string name, T09Image image, int factor);
    }

    // The pose of the vessel and the flight camera at one moment. Heights are metres over sea level and over the terrain below the point (the ocean counts
    // as terrain at sea level); the aim is the camera's forward direction against the local north and up at the camera.
    internal struct T09Pose
    {
        internal double VesselAsl, VesselAgl, CameraAsl, CameraAgl, CameraToVesselM, AzimuthDeg, ElevationDeg;
        // How far the vessel is from the point the hold keeps it at (metres); 0 where no hold has an anchor yet.
        internal double DriftM;
    }

    // A picture the probe read from the flight camera.
    internal sealed class T09Image
    {
        internal int Width, Height;
        internal Color[] Pixels;
        // The values as the camera target held them (no gamma step), where the read asked for them (IT09World.KeepRaw); else null.
        internal Color[] Raw;
    }

    // The game's own world: the calls T09Probe made directly before the seam.
    internal sealed class KspT09World : IT09World
    {
        private ReconHost host;
        private bool holding;

        public string SceneNotFlight()
        {
            return HighLogic.LoadedScene == GameScenes.FLIGHT ? null : HighLogic.LoadedScene.ToString();
        }

        public bool FlightCameraPresent()
        {
            return FlightCamera.fetch != null;
        }

        public bool VesselUnpacked()
        {
            Vessel v = FlightGlobals.ActiveVessel;
            return v.loaded && !v.packed && v.mainBody != null;
        }

        public bool BodyHasAtmosphere { get { return FlightGlobals.ActiveVessel.mainBody.atmosphere; } }
        public int BodyIndex { get { return FlightGlobals.ActiveVessel.mainBody.flightGlobalsIndex; } }
        public double BodyRadius { get { return FlightGlobals.ActiveVessel.mainBody.Radius; } }
        public double VesselLatitude { get { return FlightGlobals.ActiveVessel.latitude; } }
        public double VesselLongitude { get { return FlightGlobals.ActiveVessel.longitude; } }

        public double TerrainAltitude(double lat, double lon)
        {
            return FlightGlobals.ActiveVessel.mainBody.TerrainAltitude(lat, lon, true);
        }

        public double VesselAltitude { get { return FlightGlobals.ActiveVessel.altitude; } }

        public string VesselState
        {
            get
            {
                Vessel v = FlightGlobals.ActiveVessel;
                return "situation " + v.situation + ", packed " + v.packed;
            }
        }

        public void PlaceVessel(double lat, double lon, double altitude)
        {
            Vessel vessel = FlightGlobals.ActiveVessel;
            CelestialBody body = vessel.mainBody;
            Vector3d up = body.GetSurfaceNVector(lat, lon);
            Vector3d place = body.GetWorldSurfacePosition(lat, lon, altitude);
            vessel.SetPosition(place);
            vessel.SetRotation(Quaternion.FromToRotation(Vector3.up, (Vector3)up));
            FloatingOrigin.SetOffset(place);
            Krakensbane.ResetVelocityFrame(true);
            vessel.SetWorldVelocity(body.getRFrmVel(vessel.GetWorldPos3D()));
            CheatOptions.IgnoreMaxTemperature = true;
            CheatOptions.NoCrashDamage = true;
            // The hold starts again for this view: it waits until the vessel's centre of mass really stands at the placed point, then anchors there (T09Hold).
            // No engine may push the vessel out of the pose: start 42's vessel climbed from 4.6 km to 23 km in 25 s.
            Hold.Begin(lat, lon, altitude);
            try
            {
                FlightInputHandler.state.mainThrottle = 0f;
                foreach (ModuleEngines engine in vessel.FindPartModulesImplementing<ModuleEngines>())
                {
                    if (engine.EngineIgnited) engine.Shutdown();
                }
            }
            catch (Exception) { /* a vessel without engines or an input handler that is not there yet: nothing to shut down */ }
        }

        public void StartHold()
        {
            if (holding) return;
            holding = true;
            host = ReconHost.Get();
            host.PerFrame.Add(HoldVessel);
        }

        public void StopHold()
        {
            if (host != null) host.PerFrame.Remove(HoldVessel);
            holding = false;
        }

        // The hold (T09Hold) on the active vessel: reads its centre of mass, sets its position, keeps the ground's velocity.
        private sealed class KspHoldVessel : IHoldVessel
        {
            public void Read(out double lat, out double lon, out double alt)
            {
                Vessel v = FlightGlobals.ActiveVessel;
                v.mainBody.GetLatLonAlt(v.GetWorldPos3D(), out lat, out lon, out alt);
            }

            public void Set(double lat, double lon, double alt)
            {
                Vessel v = FlightGlobals.ActiveVessel;
                v.SetPosition(v.mainBody.GetWorldSurfacePosition(lat, lon, alt));
            }

            public double Distance(double lat1, double lon1, double alt1, double lat2, double lon2, double alt2)
            {
                CelestialBody body = FlightGlobals.ActiveVessel.mainBody;
                return (body.GetWorldSurfacePosition(lat1, lon1, alt1) - body.GetWorldSurfacePosition(lat2, lon2, alt2)).magnitude;
            }

            public void KeepVelocity()
            {
                Vessel v = FlightGlobals.ActiveVessel;
                try { FlightInputHandler.state.mainThrottle = 0f; } catch (Exception) { /* no input handler */ }
                v.SetWorldVelocity(v.mainBody.getRFrmVel(v.GetWorldPos3D()));
            }
        }

        private T09Hold hold;
        private T09Hold Hold { get { return hold ?? (hold = new T09Hold(new KspHoldVessel(), m => AtmospheresLog.Say(m))); } }

        public int HoldCorrections { get { return Hold.Corrections; } }
        public double HoldWorstDriftM { get { return Hold.WorstDriftM; } }
        public bool HoldReached { get { return Hold.Reached; } }
        public string HoldStatus { get { return Hold.Status; } }

        // Every frame. Setting the velocity alone did not hold the vessel in start 42 (it left the pose within seconds: east at the body's rotation speed at 265 m, then
        // upward at up to 1.6 km/s at 4.6 km, shedding its stages on the way); the position is what the pictures and the timing depend on, so the hold owns it.
        private void HoldVessel()
        {
            if (!SeriesProbe.FlightReadyState()) return;
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null || v.mainBody == null) return;
            Hold.Tick();
        }

        public T09Pose ReadPose()
        {
            Vessel v = FlightGlobals.ActiveVessel;
            CelestialBody body = v.mainBody;
            Camera cam = FlightCamera.fetch.mainCamera;
            var pose = new T09Pose();
            double lat, lon, alt;
            Vector3d vesselPos = v.GetWorldPos3D();
            body.GetLatLonAlt(vesselPos, out lat, out lon, out alt);
            pose.VesselAsl = alt;
            pose.VesselAgl = alt - body.TerrainAltitude(lat, lon, true);
            Vector3d camPos = cam.transform.position;
            body.GetLatLonAlt(camPos, out lat, out lon, out alt);
            pose.CameraAsl = alt;
            pose.CameraAgl = alt - body.TerrainAltitude(lat, lon, true);
            pose.CameraToVesselM = (camPos - vesselPos).magnitude;
            CameraAim(out pose.AzimuthDeg, out pose.ElevationDeg);
            pose.DriftM = Hold.DriftM();
            return pose;
        }

        public string CameraModeText
        {
            get
            {
                FlightCamera fc = FlightCamera.fetch;
                Vessel v = FlightGlobals.ActiveVessel;
                return fc.mode == FlightCamera.Modes.AUTO && v != null ? "AUTO (" + FlightCamera.GetAutoModeForVessel(v) + ")" : fc.mode.ToString();
            }
        }

        public void UseFreeCamera()
        {
            FlightCamera.fetch.mode = FlightCamera.Modes.FREE;
        }

        public double SunElevationDeg()
        {
            Vessel v = FlightGlobals.ActiveVessel;
            CelestialBody sun = GameGlobals.Sun;
            if (sun == null || v == null || v.mainBody == null) return 90.0;
            Vector3d toSun = (sun.position - v.GetWorldPos3D()).normalized;
            Vector3d up = (v.GetWorldPos3D() - v.mainBody.position).normalized;
            return Math.Asin(Math.Max(-1.0, Math.Min(1.0, Vector3d.Dot(toSun, up)))) * 180.0 / Math.PI;
        }

        public void CameraAim(out double azimuthDeg, out double elevationDeg)
        {
            Camera cam = FlightCamera.fetch.mainCamera;
            CelestialBody body = FlightGlobals.currentMainBody;
            Vector3d forward = cam.transform.forward;
            Vector3d up = ((Vector3d)cam.transform.position - body.position).normalized;
            Vector3d axis = body.RotationAxis.normalized;
            Vector3d north = (axis - up * Vector3d.Dot(axis, up)).normalized;
            Vector3d east = Vector3d.Cross(up, north);
            azimuthDeg = Math.Atan2(Vector3d.Dot(forward, east), Vector3d.Dot(forward, north)) * 180.0 / Math.PI;
            elevationDeg = Math.Asin(Math.Max(-1.0, Math.Min(1.0, Vector3d.Dot(forward, up)))) * 180.0 / Math.PI;
        }

        public float CameraHeading
        {
            get { return FlightCamera.fetch.camHdg; }
            set { FlightCamera.fetch.camHdg = value; }
        }

        public float CameraPitch
        {
            get { return FlightCamera.fetch.camPitch; }
            set { FlightCamera.fetch.camPitch = value; }
        }

        public void SetCameraDistance(double distance)
        {
            FlightCamera fc = FlightCamera.fetch;
            fc.maxDistance = Mathf.Max(fc.maxDistance, (float)distance + 100f);
            fc.minDistance = Mathf.Min(fc.minDistance, (float)distance);
            fc.SetDistanceImmediate((float)distance);
        }

        public bool CameraPixelSize(out int width, out int height)
        {
            Camera cam = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null;
            width = height = 0;
            if (cam == null) return false;
            width = cam.pixelWidth;
            height = cam.pixelHeight;
            return true;
        }

        public int ScreenWidth { get { return Screen.width; } }
        public int ScreenHeight { get { return Screen.height; } }

        public int VSyncCount
        {
            get { return QualitySettings.vSyncCount; }
            set { QualitySettings.vSyncCount = value; }
        }

        public int TargetFrameRate
        {
            get { return Application.targetFrameRate; }
            set { Application.targetFrameRate = value; }
        }

        public int FrameCount { get { return Time.frameCount; } }

        public double UniversalTime
        {
            get { return Planetarium.GetUniversalTime(); }
            set { Planetarium.SetUniversalTime(value); }
        }

        public bool ScattererLoaded { get { return EveRenderCompat.ScattererLoaded; } }
        public bool EveAvailable { get { return EveLayerAccess.Available; } }
        public int VolumetricLayerCount { get { return EveLayerAccess.VolumetricLayerCount(); } }

        public bool EveLayerBound
        {
            get
            {
                EveCloudModule painter = EveCloudModule.Current;
                return EveRenderCompat.EffectiveSource(CloudSourceKind.Eve) == CloudSourceKind.Eve && painter != null && painter.FirstBoundLayer() != null;
            }
        }

        public string EveStatusText { get { return EveRenderCompat.Status + ": " + EveRenderCompat.Reason; } }
        public bool EveRendererReady { get { return EveRenderCompat.Status == EveRenderStatus.Ready; } }
        public int EveLayersBound { get { return EveCloudStats.LayersBound; } }
        public int EveFailureLines { get { return EveRenderCompat.FailureLines; } }

        public bool MoonProviderAnswers()
        {
            Vector3 towards;
            Color colour;
            Vector3d eye = FlightCamera.fetch.mainCamera.transform.position;
            return CloudBridgeLights.Moon != null && CloudBridgeLights.Moon(FlightGlobals.ActiveVessel.mainBody, eye, out towards, out colour);
        }

        private const long LeastFreeBytes = 300L * 1024 * 1024;

        public string PicturesFolderProblem()
        {
            try
            {
                string folder = SeriesPictures.Folder;
                Directory.CreateDirectory(folder);
                string probe = Path.Combine(folder, "t09-write-test.tmp");
                File.WriteAllText(probe, "x");
                File.Delete(probe);
                long free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))).AvailableFreeSpace;
                if (free < LeastFreeBytes) return "only " + (free / (1024 * 1024)) + " MB are free on the drive of " + folder;
                return null;
            }
            catch (Exception e)
            {
                return SeriesPictures.Folder + " cannot be written (" + e.GetType().Name + " " + e.Message + ")";
            }
        }

        public bool CaptureBegin()
        {
            ReconHost host = ReconHost.Get();
            if (host.Reading) return false;
            host.KeepRaw = KeepRaw;
            host.Read();
            return true;
        }

        public bool CaptureBusy { get { return ReconHost.Get().Reading; } }

        public T09Image CaptureTake()
        {
            ReconFrame f = ReconHost.Get().Frame;
            return f == null ? null : new T09Image { Width = f.Width, Height = f.Height, Pixels = f.Pixels, Raw = f.Raw };
        }

        public bool KeepRaw { get; set; }

        public void SetLayerMask(int mask)
        {
            OwnCloudModule m = OwnCloudModule.Current;
            if (m != null) m.Frame.LayerMask = mask;
        }

        public double CameraFovDeg { get { return FlightCamera.fetch.mainCamera.fieldOfView; } }

        public System.Collections.Generic.IList<System.Collections.Generic.KeyValuePair<string, string>> EnvironmentLines()
        {
            return T09Environment.Read();
        }

        public bool AidOn(int mode)
        {
            OwnCloudModule m = OwnCloudModule.Current;
            if (m == null || m.Renderer == null || !m.Renderer.Ready || m.Stats.Mode == "off") return false;
            m.Frame.AidMode = mode;
            return true;
        }

        // The layer shader's aid picture 1 (optical depth as a heat map over the scene); the module fills the frame's other fields every frame but not this one.
        public bool AidOn()
        {
            OwnCloudModule m = OwnCloudModule.Current;
            if (m == null || m.Renderer == null || !m.Renderer.Ready || m.Stats.Mode == "off") return false;
            m.Frame.AidMode = 1;
            return true;
        }

        public void AidOff()
        {
            OwnCloudModule m = OwnCloudModule.Current;
            if (m != null) m.Frame.AidMode = 0;
        }

        public long SavePicture(string name, T09Image image, int factor)
        {
            return SeriesPictures.SaveImage(name, image.Pixels, image.Width, image.Height, factor);
        }
    }
}
