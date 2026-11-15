using System;
using System.Collections.Generic;
using System.IO;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres.Viewer
{
    // Where the cloud viewer's camera and vessel stand (T09Pose, the numbers the probe judges).
    internal struct ViewerPoseReading
    {
        internal double VesselAslM, VesselAglM, CameraAglM, CameraToVesselM, AzimuthDeg, ElevationDeg;
    }

    // What the viewer probe (ViewerProbe, series K-V1) asks of the running game: the viewer's window and session, its controls, and the places where the controls' effects can
    // be read back (the vessel's pose, the game's clock, the cloud pass's noise clock and exposure, the tuning table's reads, the picture files). ViewerProbe reads the game only
    // through this interface so that the tests walk the whole scenario against a fake rig; GameViewerRig is the one the game uses, each member one call of the viewer or of the pass.
    internal interface IViewerRig
    {
        // --- The game and the own cloud pass ---
        bool FlightReady { get; }
        // The own pass has its buffers on the camera and a layer stack.
        bool PassDrawing { get; }
        int PassFramesDrawn { get; }
        IList<ViewerLayer> Layers { get; }
        int Frame { get; }
        // Real seconds (Time.realtimeSinceStartup).
        double RealS { get; }
        double GameUt { get; }
        double SunElevationDeg { get; }
        ViewerPoseReading Pose { get; }
        // The vessel stands where the hold keeps it.
        bool HoldStable { get; }
        // The pass's noise clock (the time the layers' shape noise has run, in seconds) and each layer's drift distance (x, y, z a layer, metres).
        double FlowTimeS { get; }
        IList<double> DriftM { get; }
        // The exposure the pass used in its last frame (cloud.exposure as FillFrame read it).
        float PassExposure { get; }
        // cloud.mapOverride.<layer> as the pass reads it (-1: the field's map), and the cover the pass's CPU mirror holds at the vessel (NaN where there is none).
        float PassMapOverride(string layerName);
        float LayerCover(int layer);
        // The precipitation level (0 to 1) of the weather field at the vessel; NaN where it cannot be read.
        double PrecipitationLevel { get; }
        // The tuning table's value of a key (no read for the read check), and whether the key has an override.
        float TuningValue(string key, float compiledDefault);
        bool HasOverride(string key);
        double Setting(string key);
        int TuningFrame { get; }
        int WatchedReadFrame { get; }

        // --- The viewer ---
        bool AddonRunning { get; }
        bool WindowOpen { get; }
        bool WindowVisible { get; }
        bool SessionActive { get; }
        bool PlacementPending { get; }
        int KeyCount { get; }
        int KeysExpected { get; }
        double ClockNatural { get; }
        double ClockOffsetS { get; }
        bool ClockFrozen { get; }
        double ClockFrozenUt { get; }
        float CloudSpeed { get; }
        bool CloudsFrozen { get; }
        char Showing { get; }
        bool Capturing { get; }
        string LastCapture { get; }
        int CaptureFrame { get; }
        int CaptureReleasedFrame { get; }

        void ToggleWindow();
        void PlacePose(ViewerPose pose);
        void RequestAltitude(double aglM);
        // Turns the camera by hand to this compass heading and elevation (the way a player drags it), so that a later placement that keeps the aim can be told from one that resets it.
        void SetCameraAim(double headingDeg, double elevationDeg);
        void SetTimeOffset(double seconds);
        void FreezeTime(bool on);
        void SetWeather(ViewerWeather weather);
        void SetCloudSpeed(float scale);
        void FreezeClouds(bool on);
        void StoreA();
        void FlipAB();
        void SetKey(string key, float value);
        void SetViewerSetting(string key, double value);
        void WatchKey(string key);
        void RequestCapture();

        // --- Files ---
        // The path of a picture of the viewer by its file name, its sidecar text (null where there is none) and the size of a file (-1 where there is none).
        string CapturePicturePath(string fileName);
        string ReadText(string path);
        long FileSize(string path);
        // A screenshot of the screen as it is, into the series' pictures folder under that name; returns the path.
        string Screenshot(string name);
    }

    // The game's rig: the viewer's singletons and the own pass.
    internal class GameViewerRig : IViewerRig
    {
        protected readonly CloudViewer viewer = CloudViewer.Instance;
        private readonly CloudViewerWindow window = CloudViewerWindow.Instance;

        private static LayerStackSource Stack()
        {
            OwnCloudModule m = OwnCloudModule.Current;
            return m == null ? null : m.Stack as LayerStackSource;
        }

        public bool FlightReady
        {
            get { return HighLogic.LoadedScene == GameScenes.FLIGHT && GameGlobals.Present && FlightGlobals.ready && GameGlobals.ActiveVessel != null && FlightCamera.fetch != null; }
        }

        public bool PassDrawing
        {
            get
            {
                OwnCloudModule m = OwnCloudModule.Current;
                LayerStackSource stack = Stack();
                return m != null && OwnCloudModule.Drawing && stack != null && stack.LayerCount > 0;
            }
        }

        public int PassFramesDrawn
        {
            get { return OwnCloudModule.Current != null ? OwnCloudModule.Current.Stats.FramesDrawn : 0; }
        }

        public IList<ViewerLayer> Layers
        {
            get { return CloudViewer.Layers(); }
        }

        public int Frame { get { return Time.frameCount; } }
        public double RealS { get { return Time.realtimeSinceStartup; } }
        public double GameUt { get { return Planetarium.GetUniversalTime(); } }
        public double SunElevationDeg { get { return viewer.World.SunElevationDeg(); } }
        public bool HoldStable { get { return viewer.World.HoldStable; } }

        public ViewerPoseReading Pose
        {
            get
            {
                T09Pose p = viewer.World.ReadPose();
                return new ViewerPoseReading
                {
                    VesselAslM = p.VesselAsl, VesselAglM = p.VesselAgl, CameraAglM = p.CameraAgl, CameraToVesselM = p.CameraToVesselM, AzimuthDeg = p.AzimuthDeg, ElevationDeg = p.ElevationDeg
                };
            }
        }

        public double FlowTimeS
        {
            get
            {
                OwnCloudModule m = OwnCloudModule.Current;
                return m != null && m.Renderer != null ? m.Renderer.Noise.FlowTimeSeconds : double.NaN;
            }
        }

        public IList<double> DriftM
        {
            get
            {
                var list = new List<double>();
                OwnCloudModule m = OwnCloudModule.Current;
                LayerStackSource stack = Stack();
                if (m == null || m.Renderer == null || stack == null) return list;
                var drift = new double[3];
                for (int l = 0; l < stack.LayerCount; l++)
                {
                    double rise;
                    Vector3 wind;
                    m.Renderer.Noise.StateOf(l, drift, out rise, out wind);
                    list.Add(drift[0]);
                    list.Add(drift[1]);
                    list.Add(drift[2]);
                }
                return list;
            }
        }

        public float PassExposure
        {
            get { return OwnCloudModule.Current != null ? OwnCloudModule.Current.Frame.OutputExposure : float.NaN; }
        }

        public float PassMapOverride(string layerName)
        {
            return CloudLookTuning.MapOverrideOf(layerName);
        }

        public float LayerCover(int layer)
        {
            LayerStackSource stack = Stack();
            Vessel v = GameGlobals.ActiveVessel;
            if (stack == null || v == null || layer < 0 || layer >= stack.LayerCount) return float.NaN;
            float cover, type;
            stack.SampleMaps(layer, v.latitude, v.longitude, out cover, out type);
            return cover;
        }

        public double PrecipitationLevel
        {
            get
            {
                try
                {
                    Vessel v = GameGlobals.ActiveVessel;
                    WeatherSample s;
                    if (v == null || v.mainBody == null || !WeatherHub.Sample(v.mainBody.flightGlobalsIndex, v.latitude, v.longitude, 0.0, Planetarium.GetUniversalTime(), out s)) return double.NaN;
                    return s.Level01;
                }
                catch (Exception)
                {
                    return double.NaN;
                }
            }
        }

        public float TuningValue(string key, float compiledDefault)
        {
            return Tuning.Peek(key, compiledDefault);
        }

        public bool HasOverride(string key)
        {
            float value;
            return Tuning.Instance.TryGetOverride(key, out value);
        }

        public double Setting(string key)
        {
            return AtmospheresSettings.Number(key);
        }

        public int TuningFrame { get { return Tuning.Instance.Frame; } }
        public int WatchedReadFrame { get { return Tuning.Instance.LastWatchedRead; } }

        public bool AddonRunning { get { return CloudViewerAddon.Running; } }
        public bool WindowOpen { get { return window.IsOpen; } }
        public bool WindowVisible { get { return window.Visible; } }
        public bool SessionActive { get { return viewer.Active; } }
        public bool PlacementPending { get { return viewer.PlacementPending; } }
        public int KeyCount { get { return viewer.Keys.Count; } }
        public int KeysExpected { get { return CloudKeyRegistry.All.Count + CloudViewer.Layers().Count * CloudKeyRegistry.PerLayer.Count; } }
        public double ClockNatural { get { return viewer.Clock != null ? viewer.Clock.Natural : double.NaN; } }
        public double ClockOffsetS { get { return viewer.Clock != null ? viewer.Clock.OffsetS : double.NaN; } }
        public bool ClockFrozen { get { return viewer.Clock != null && viewer.Clock.Frozen; } }
        public double ClockFrozenUt { get { return viewer.Clock != null ? viewer.Clock.FrozenUt : double.NaN; } }
        public float CloudSpeed { get { return viewer.CloudSpeed; } }
        public bool CloudsFrozen { get { return viewer.CloudsFrozen; } }
        public char Showing { get { return viewer.Flip.Showing; } }
        public bool Capturing { get { return viewer.Capturing; } }
        public string LastCapture { get { return viewer.LastCapture; } }
        public int CaptureFrame { get { return viewer.CaptureFrame; } }
        public int CaptureReleasedFrame { get { return viewer.CaptureReleasedFrame; } }

        public void ToggleWindow() { window.Toggle(); }
        public void PlacePose(ViewerPose pose) { viewer.PlacePose(pose); }
        public void RequestAltitude(double aglM) { viewer.RequestAltitude(aglM); }

        // The convention of CloudViewer.Aim (the K-T2 ground views): the camera stands behind the vessel, heading + 180 degrees, pitch minus the elevation.
        public void SetCameraAim(double headingDeg, double elevationDeg)
        {
            FlightCamera fc = FlightCamera.fetch;
            if (fc == null) return;
            fc.camHdg = (float)((headingDeg + 180.0) * Math.PI / 180.0);
            fc.camPitch = (float)(-elevationDeg * Math.PI / 180.0);
        }

        public void SetTimeOffset(double seconds) { viewer.SetTimeOffset(seconds); }
        public void FreezeTime(bool on) { viewer.FreezeTime(on); }
        public void SetWeather(ViewerWeather weather) { viewer.SetWeather(weather); }
        public void SetCloudSpeed(float scale) { viewer.SetCloudSpeed(scale); }
        public void FreezeClouds(bool on) { viewer.FreezeClouds(on); }
        public void StoreA() { viewer.StoreA(); }
        public void FlipAB() { viewer.FlipAB(); }
        public void SetKey(string key, float value) { viewer.SetKey(key, value); }
        public void SetViewerSetting(string key, double value) { viewer.SetSetting(key, value); }
        public void WatchKey(string key) { viewer.WatchKey(key); }
        public void RequestCapture() { viewer.RequestCapture(); }

        public string CapturePicturePath(string fileName)
        {
            return CloudViewer.CapturePath(Path.GetFileNameWithoutExtension(fileName));
        }

        public string ReadText(string path)
        {
            try { return File.Exists(path) ? File.ReadAllText(path) : null; }
            catch (Exception) { return null; }
        }

        public long FileSize(string path)
        {
            try { return File.Exists(path) ? new FileInfo(path).Length : -1; }
            catch (Exception) { return -1; }
        }

        public string Screenshot(string name)
        {
            string folder = SeriesPictures.Folder;
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, name);
            ScreenCapture.CaptureScreenshot(path);
            return path;
        }
    }
}
