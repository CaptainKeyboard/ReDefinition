using System;
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

        private static void HoldVessel()
        {
            if (!SeriesProbe.FlightReadyState()) return;
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null || v.mainBody == null) return;
            v.SetWorldVelocity(v.mainBody.getRFrmVel(v.GetWorldPos3D()));
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
    }
}
