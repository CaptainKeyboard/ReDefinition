using System;
using System.Collections.Generic;
using System.IO;
using ReDefinition.Atmospheres;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Tests.Atmospheres.T09
{
    // A weather field for the walk-through: it answers false at the site until RealNow() reaches ReadyAtS (the real field reports Ready from the start and has
    // no body state yet), and then answers with a cover that depends on the universal time. Readiness is Ready throughout, as the hub's NullField and the
    // real field report it in that window.
    internal sealed class FakeField : IWeatherField
    {
        internal Func<double> RealNow = () => 0.0;
        internal double ReadyAtS;
        // False for the times the field does not answer for (a field that answers for some times only).
        internal Func<double, bool> AnswersAt = ut => true;
        internal Func<double, float> Cover = ut => 0.8f;
        internal int SampleCalls, FalseAnswers;

        public bool Sample(int bodyIndex, double latDeg, double lonDeg, double altM, double ut, out WeatherSample sample)
        {
            SampleCalls++;
            sample = WeatherSample.Clear(bodyIndex);
            if (RealNow() < ReadyAtS || !AnswersAt(ut))
            {
                FalseAnswers++;
                return false;
            }
            sample.CloudCover01 = Cover(ut);
            return true;
        }

        public bool GetMap(int bodyIndex, WeatherMapKind kind, double ut, WeatherMap target) { return false; }
        public bool FillWindow(int bodyIndex, double centreLatDeg, double centreLonDeg, double radiusKm, double ut, WeatherWindow target) { return false; }
        public void Advance(double budgetMs) { }
        public long Version { get { return 1; } }
        public long DefinitionId { get { return 1; } }
        public bool ThreadSafe { get { return true; } }
        public double StepSeconds { get { return 60; } }
        public FieldReadiness Readiness { get { return FieldReadiness.Ready; } }
    }

    // The game as the K-T2 probe sees it, with every number the walk-through needs to turn: the scene, the vessel, the flight camera, the window, the
    // clock and the own cloud module's counters. Advance(dt) is one frame. Any member of the flight (vessel, camera) throws a NullReferenceException before
    // the flight is ready, as FlightGlobals.ActiveVessel does in the main menu.
    internal sealed class FakeWorld : IT09World, IHoldVessel
    {
        // The game's placement as it really is (start 43): SetPosition shows in the read of the centre of mass only after a physics step. With RealisticHold the vessel's
        // true altitude changes PlaceDelayFrames frames after the placement, the read lags the true altitude by ReadLagFrames, and the real hold (T09Hold) runs every frame
        // on that read. Without it (the default) a placement is seen at once and the hold is a flag, as the older tests expect.
        internal bool RealisticHold;
        internal int PlaceDelayFrames = 3, ReadLagFrames = 5;
        private T09Hold hold;
        private double trueAlt = 70.0, pendingAlt;
        private int pendingApplyFrame = -1;
        private readonly List<double> altHistory = new List<double>();
        internal T09Hold TheHold { get { return hold; } }
        internal double TrueAltitude { get { return trueAlt; } set { trueAlt = value; } }

        public void Read(out double lat, out double lon, out double alt)
        {
            lat = SiteLat;
            lon = SiteLon;
            alt = altHistory.Count == 0 ? trueAlt : altHistory[Math.Max(0, altHistory.Count - 1 - ReadLagFrames)];
        }

        public void Set(double lat, double lon, double alt) { trueAlt = PlaceResult(Math.Max(0, PlaceCalls - 1), alt); }
        public double Distance(double lat1, double lon1, double alt1, double lat2, double lon2, double alt2) { return Math.Abs(alt1 - alt2); }
        public void KeepVelocity() { }

        private void StepRealisticHold()
        {
            if (pendingApplyFrame >= 0 && Frames >= pendingApplyFrame)
            {
                trueAlt = pendingAlt;
                pendingApplyFrame = -1;
            }
            altHistory.Add(trueAlt);
            if (altHistory.Count > ReadLagFrames + 2) altHistory.RemoveAt(0);
            if (Held && hold != null) hold.Tick();
        }

        // Real seconds since the game started, frames, universal time, the game's time scale.
        internal double RealS;
        internal int Frames;
        internal double Ut = 1000000.0 + 123.0;
        internal double TimeScale = 1.0;

        // The scene: flight is ready from FlightReadyAtS on; LeaveFlightAtS (when set) sends the game back to the space centre.
        internal double FlightReadyAtS = 55.0;
        internal double LeaveFlightAtS = double.PositiveInfinity;
        internal double VesselGoneAtS = double.PositiveInfinity;
        internal bool CameraPresent = true;
        internal bool Atmosphere = true;

        internal bool FlightReady { get { return RealS >= FlightReadyAtS && RealS < LeaveFlightAtS && RealS < VesselGoneAtS; } }

        // The window and the render size of the camera.
        internal int Width = 1707, Height = 960;
        internal int RenderWidth = 1707, RenderHeight = 960;
        internal int Vsync = 1, FrameRateCap = 60;
        // A game that sets the cap or the sync again behind the probe's back.
        internal bool CapSticks, VsyncSticks;

        // The vessel: the altitude it has, what each placement turns into (call index, wanted altitude) and how many real seconds the placement takes.
        internal double Altitude = 70.0;
        internal Func<int, double, double> PlaceResult = (call, wanted) => wanted;
        internal Func<int, double> PlaceLagS = call => 0.0;
        internal int PlaceCalls;
        private double placeAtS = double.NegativeInfinity, placeTarget;
        internal bool Held;
        internal double HoldStartedAtS = double.NaN;
        internal double SiteLat = -0.0972, SiteLon = -74.5577, Terrain = 70.0;
        internal double Radius = 600000.0;
        internal int Body = 1;
        internal string Situation = "FLYING";

        // The flight camera: heading and pitch in radians as FlightCamera keeps them (the game clamps the pitch to +-PitchLimitDeg); a stuck camera ignores the probe.
        // The aim the camera really has follows its commands with a lag (first order, LagS seconds), through the mapping of its mode: FREE is the convention of the
        // ground views (azimuth = heading - 180, elevation = minus the pitch); AUTO above the atmosphere is ORBITAL, whose axes are rotated against the local horizon by
        // OrbitalMixDeg and which turns against the horizon by RotationDriftDegPerS (the rotating frame). HeadingSign and PitchSign invert an axis.
        private float hdg, pitch;
        private double actualAz, actualEl, aimAtRealS = double.NaN;
        internal bool CameraStuck;
        internal double StuckAzimuth = 10.0, StuckElevation = 0.0;
        internal double CameraDistance;
        internal double LagS = 0.2, PitchLimitDeg = 85.0, OrbitalMixDeg, RotationDriftDegPerS, HeadingSign = 1.0, PitchSign = 1.0;
        // Whether the game honours the probe's request for the FREE mode; the aim must converge in both cases.
        internal bool FreeModeWorks = true;
        internal bool FreeMode;
        // The pose the vessel really has: its height drifts from the placement on (the hold fails) by the function of the seconds since the placement; the
        // horizontal distance from the anchor in metres per second; the camera's distance from the vessel as a multiple of the one asked for (a vessel that is a dot).
        internal Func<double, double> AltitudeDrift = s => 0.0;
        internal double HorizontalDriftMPerS;
        internal Func<double, double> CameraDistanceFactor = s => 1.0;
        // The pose moves when a read starts or ends (the placement is reordered behind the probe's back).
        internal double AltitudeJumpOnCaptureBegin, AltitudeJumpOnCaptureTake;
        // The hold's corrections as a real hold reports them.
        internal int Corrections;
        internal double WorstDrift;

        // The sun's elevation over the vessel: 65 degrees times the sine of the day's phase (a Kerbin day is 6 hours).
        internal Func<double, double> Sun = ut => 65.0 * Math.Sin(2.0 * Math.PI * ut / 21600.0);

        // TVC.
        internal bool Scatterer = true, Eve = true, EveBound = true, EveReady = true;
        internal int EveLayers = 5, EveLayerCount = 6, EveFailures;
        internal bool MoonAnswers = true;

        // The own pass: which frames it draws in, and whether the module exists at all.
        internal Func<int, bool> PassDrawsInFrame = f => true;
        internal bool ModuleExists = true;
        internal OwnCloudModule Module;
        // A pass whose frame counter is stuck (a pass that never warmed up) and one that reports a render size of its own.
        internal bool PassFrozen, GainIgnored;
        internal int PassRenderWidth = -1, PassRenderHeight = -1;
        internal int Wired;

        private void RequireFlight()
        {
            if (!FlightReady) throw new NullReferenceException("FlightGlobals.fetch is null (not in the flight scene)");
        }

        // One frame of the game: time, the clock, the scene state the game itself keeps and the counters of the cloud module.
        internal void Advance(double dt)
        {
            RealS += dt;
            Frames++;
            Ut += dt * TimeScale;
            if (CapSticks) FrameRateCap = 60;
            if (VsyncSticks) Vsync = 1;
            if (Frames > 0 && Module != null && FlightReady) UpdateModule();
            if (RealisticHold && FlightReady) StepRealisticHold();
            if (FlightReady) StepCamera(dt);
            Tuning.Instance.BeginFrame(Frames);
        }

        // The aim the commands lead to (degrees), with the mode's frame, and the lag towards it.
        private void StepCamera(double dt)
        {
            double h = hdg * 180.0 / Math.PI, p = pitch * 180.0 / Math.PI;
            double az = Wrap(h - 180.0) * HeadingSign, el = -p * PitchSign;
            if (!FreeMode)
            {
                // The inertial frame's axes against the local horizon: a rotation of the view direction about the east axis (at the equator the inertial pole is the
                // local north), then the turn of the frame against the horizon as it goes round the body.
                double t = (OrbitalMixDeg + RotationDriftDegPerS * Math.Max(0.0, RealS - placedCallS)) * Math.PI / 180.0;
                double a = az * Math.PI / 180.0, e = el * Math.PI / 180.0;
                double north = Math.Cos(e) * Math.Cos(a), east = Math.Cos(e) * Math.Sin(a), up = Math.Sin(e);
                double north2 = north * Math.Cos(t) + up * Math.Sin(t), up2 = -north * Math.Sin(t) + up * Math.Cos(t);
                az = Math.Atan2(east, north2) * 180.0 / Math.PI;
                el = Math.Asin(Math.Max(-1.0, Math.Min(1.0, up2))) * 180.0 / Math.PI;
            }
            if (double.IsNaN(aimAtRealS))
            {
                actualAz = az;
                actualEl = el;
            }
            else
            {
                double k = 1.0 - Math.Exp(-dt / Math.Max(1e-6, LagS));
                actualAz = Wrap(actualAz + k * Wrap(az - actualAz));
                actualEl += k * (el - actualEl);
            }
            aimAtRealS = RealS;
        }

        // The mode's pose as the probe reads it: the height and the horizontal distance the hold failed to keep, the camera's distance from the vessel.
        private double SincePlacementS { get { return double.IsInfinity(placeAtS) ? 0.0 : Math.Max(0.0, RealS - placeAtS); } }
        private double placedCallS, jump;

        private void UpdateModule()
        {
            // The real gates: the settings half is CloudGates.OwnPassSwitches, and the pass hides itself while the hub holds the NullField (OwnCloudModule.Run).
            bool own = CloudGates.OwnPassSwitches(AtmospheresSettings.Number, false) && !(WeatherHub.Field is NullField);
            OwnCloudModule.Statistics s = Module.Stats;
            if (!own)
            {
                s.Mode = "off";
                s.Why = "ownClouds is off";
                return;
            }
            bool recon = AtmospheresSettings.Number(AtmospheresSettings.Keys.CloudResolution) >= 4.0;
            s.Mode = recon ? "reconstruct" : "plain";
            s.Why = "drawing";
            s.LightCacheOn = AtmospheresSettings.Number(AtmospheresSettings.Keys.CloudLightCache) > 0.5;
            s.RenderWidth = PassRenderWidth > 0 ? PassRenderWidth : RenderWidth;
            s.RenderHeight = PassRenderHeight > 0 ? PassRenderHeight : RenderHeight;
            if (!PassFrozen && PassDrawsInFrame(Frames)) s.FramesDrawn++;
            if (s.BufferAttaches == 0) s.BufferAttaches = 1;
            s.WorstHeightMs = 6.5;
            s.WorstUploadMs = 1.4;
            s.WorstMapMs = 1.2;
            s.WorstHeightMainMs = 0.9;
            s.WorstMapMainMs = 0.8;
            s.FirstHeightMs = 30.0;
            s.FirstMapMs = 12.0;
            // The frame the module fills: the sun's colour scaled by cloud.sunGain, the first extra light by cloud.moonGain.
            float sunGain = GainIgnored ? 1f : Tuning.Get(OwnCloudModule.TuneSunGain, 1f);
            Module.Frame.SunColour = new Color(0.8f * sunGain, 0.7f * sunGain, 0.6f * sunGain, 1f);
            float moonGain = Tuning.Get(OwnCloudModule.TuneMoonGain, 1f);
            Module.Frame.Light.Lights[0].Colour = MoonAnswers ? new Color(0.02f * moonGain, 0.02f * moonGain, 0.03f * moonGain, 1f) : Color.black;
        }

        // --- IT09World --------------------------------------------------------------------------------------------------------------

        public string SceneNotFlight() { return RealS < FlightReadyAtS ? "MAINMENU" : RealS >= LeaveFlightAtS ? "SPACECENTER" : null; }
        public bool FlightCameraPresent() { RequireFlight(); return CameraPresent; }
        public bool VesselUnpacked() { RequireFlight(); return true; }
        public bool BodyHasAtmosphere { get { RequireFlight(); return Atmosphere; } }
        public int BodyIndex { get { RequireFlight(); return Body; } }
        public double BodyRadius { get { RequireFlight(); return Radius; } }
        public double VesselLatitude { get { RequireFlight(); return SiteLat; } }
        public double VesselLongitude { get { RequireFlight(); return SiteLon; } }
        public double TerrainAltitude(double lat, double lon) { RequireFlight(); return Terrain; }

        public double VesselAltitude
        {
            get
            {
                RequireFlight();
                if (RealisticHold)
                {
                    double read;
                    double lat, lon;
                    Read(out lat, out lon, out read);
                    return read + jump;
                }
                if (RealS >= placeAtS) Altitude = placeTarget;
                return Altitude + jump + (RealS >= placeAtS && !double.IsInfinity(placeAtS) ? AltitudeDrift(SincePlacementS) : 0.0);
            }
        }

        public T09Pose ReadPose()
        {
            RequireFlight();
            double asl = VesselAltitude;
            double az, el;
            CameraAim(out az, out el);
            double distance = CameraDistance * CameraDistanceFactor(SincePlacementS);
            double vertical = asl - (placeTarget + 0.0);
            double horizontal = HorizontalDriftMPerS * SincePlacementS;
            return new T09Pose
            {
                VesselAsl = asl,
                VesselAgl = asl - Terrain,
                CameraAsl = asl + distance * Math.Sin(-el * Math.PI / 180.0),
                CameraAgl = asl + distance * Math.Sin(-el * Math.PI / 180.0) - Terrain,
                CameraToVesselM = distance,
                AzimuthDeg = az,
                ElevationDeg = el,
                DriftM = RealisticHold ? (hold != null ? hold.DriftM() : 0.0) : (Held ? Math.Sqrt(vertical * vertical + horizontal * horizontal) : 0.0)
            };
        }

        public int HoldCorrections { get { return RealisticHold && hold != null ? hold.Corrections : Corrections; } }
        public double HoldWorstDriftM { get { return RealisticHold && hold != null ? hold.WorstDriftM : WorstDrift; } }
        public bool HoldReached { get { return !RealisticHold || (hold != null && hold.Reached); } }
        public string HoldStatus { get { return hold != null ? hold.Status : "no hold"; } }
        public string CameraModeText { get { return FreeMode ? "FREE" : "AUTO (ORBITAL)"; } }
        public void UseFreeCamera() { RequireFlight(); if (FreeModeWorks) FreeMode = true; }

        public string VesselState { get { RequireFlight(); return "situation " + Situation + ", packed False"; } }

        public void PlaceVessel(double lat, double lon, double altitude)
        {
            RequireFlight();
            int call = PlaceCalls++;
            placeTarget = PlaceResult(call, altitude);
            placeAtS = RealS + PlaceLagS(call);
            placedCallS = RealS;
            jump = 0.0;
            if (RealisticHold)
            {
                pendingAlt = placeTarget;
                pendingApplyFrame = Frames + PlaceDelayFrames;
                if (hold == null) hold = new T09Hold(this, m => AtmospheresLog.Say(m));
                hold.Begin(lat, lon, altitude);
            }
        }

        public void StartHold() { RequireFlight(); if (!Held) { Held = true; HoldStartedAtS = RealS; } }
        public void StopHold() { Held = false; }
        public double SunElevationDeg() { RequireFlight(); return Sun(Ut); }

        private static double Wrap(double d)
        {
            while (d > 180.0) d -= 360.0;
            while (d < -180.0) d += 360.0;
            return d;
        }

        public void CameraAim(out double azimuthDeg, out double elevationDeg)
        {
            RequireFlight();
            if (CameraStuck)
            {
                azimuthDeg = StuckAzimuth;
                elevationDeg = StuckElevation;
                return;
            }
            // FlightCamera: camHdg = heading + 180 degrees, camPitch = minus the elevation; the camera lags behind its commands (StepCamera).
            if (double.IsNaN(aimAtRealS)) StepCamera(0.0);
            azimuthDeg = actualAz;
            elevationDeg = actualEl;
        }

        public float CameraHeading { get { RequireFlight(); return hdg; } set { RequireFlight(); if (!CameraStuck) hdg = value; } }

        public float CameraPitch
        {
            get { RequireFlight(); return pitch; }
            set
            {
                RequireFlight();
                if (CameraStuck) return;
                double limit = PitchLimitDeg * Math.PI / 180.0;
                pitch = (float)Math.Max(-limit, Math.Min(limit, value));
            }
        }
        public void SetCameraDistance(double distance) { RequireFlight(); CameraDistance = distance; }

        public bool CameraPixelSize(out int width, out int height)
        {
            RequireFlight();
            width = RenderWidth;
            height = RenderHeight;
            return CameraPresent;
        }

        public int ScreenWidth { get { return Width; } }
        public int ScreenHeight { get { return Height; } }
        public int VSyncCount { get { return Vsync; } set { if (!VsyncSticks) Vsync = value; } }
        public int TargetFrameRate { get { return FrameRateCap; } set { if (!CapSticks) FrameRateCap = value; } }
        public int FrameCount { get { return Frames; } }
        public double UniversalTime { get { return Ut; } set { Ut = value; } }

        public bool ScattererLoaded { get { return Scatterer; } }
        public bool EveAvailable { get { return Eve; } }
        public int VolumetricLayerCount { get { return EveLayerCount; } }
        public bool EveLayerBound { get { return EveBound; } }
        public string EveStatusText { get { return (EveReady ? "Ready" : "NoScatterer") + ": fake"; } }
        public bool EveRendererReady { get { return EveReady; } }
        public int EveLayersBound { get { return EveLayers; } }
        public int EveFailureLines { get { return EveFailures; } }
        public bool MoonProviderAnswers() { RequireFlight(); return MoonAnswers; }

        // --- The pictures -----------------------------------------------------------------------------------------------------------

        // What the probe did, in order: "aidon", "aidoff", "capbegin", "take", "save:<name>:<factor>" (each with the frame in Stamps).
        internal readonly List<string> Events = new List<string>();
        internal readonly List<int> Stamps = new List<int>();
        internal bool AidAvailable = true, AidIsOn;
        // A pass whose aid mode does nothing: the aid picture equals the natural one.
        internal bool AidIgnored;
        internal string FolderProblem;
        // The n-th read (0-based) of the start: "throw" (CaptureBegin throws), "none" (no picture), "busy" (never ready), "busythrows", "takethrows", "nobegin" or null.
        internal Func<int, string> ReadFault = n => null;
        // The n-th save (0-based): "full" (IOException), "denied" (UnauthorizedAccessException) or null.
        internal Func<int, string> SaveFault = n => null;
        internal readonly List<string> Saved = new List<string>();
        internal readonly List<int> SavedFactor = new List<int>();
        internal int ReadCalls, SaveCalls;
        private int readReadyFrame = -1;
        private string readFaultNow;

        private void Stamp(string e) { Events.Add(e); Stamps.Add(Frames); }

        public string PicturesFolderProblem() { return FolderProblem; }

        public bool CaptureBegin()
        {
            RequireFlight();
            readFaultNow = ReadFault(ReadCalls++);
            if (readFaultNow == "throw") throw new InvalidOperationException("ReadPixels failed");
            if (readFaultNow == "nobegin") return false;
            Stamp("capbegin");
            jump += AltitudeJumpOnCaptureBegin;
            readReadyFrame = Frames + 1;
            return true;
        }

        public bool CaptureBusy
        {
            get
            {
                if (readFaultNow == "busythrows") throw new InvalidOperationException("the host is gone");
                return readFaultNow == "busy" || Frames < readReadyFrame;
            }
        }

        public T09Image CaptureTake()
        {
            Stamp("take");
            jump += AltitudeJumpOnCaptureTake;
            if (readFaultNow == "takethrows") throw new InvalidOperationException("the frame was released");
            if (readFaultNow == "none") return null;
            if (KeepRaw) return RawImage();
            var image = new T09Image { Width = 16, Height = 9, Pixels = new Color[16 * 9] };
            for (int i = 0; i < image.Pixels.Length; i++)
            {
                float v = (i % 16) / 16f;
                image.Pixels[i] = AidIsOn && !AidIgnored ? new Color(v, 0.2f, 1f - v, 1f) : new Color(v, v, v, 1f);
            }
            return image;
        }

        // --- K-T2d ------------------------------------------------------------------------------------------------------------------

        public bool KeepRaw { get; set; }
        // A probe that misreads the field of view puts the horizon far off the picture (EmptyHorizon): its regions come out empty while the picture itself stays right.
        public double CameraFovDeg { get { return EmptyHorizon ? 2.0 : FovDeg; } }
        internal double FovDeg = 60.0;
        internal int LayerMaskNow;
        internal int AidModeNow;
        // The picture of the sky: the sun-lit sea below the horizon, the blue sky above it; the own pass, when it draws all layers, replaces the sky by a deck of
        // DeckRadiance times the sun gain (clipped at ClipAt) where DeckCovers says so (by the picture's column). A model of the real thing with the numbers the test chooses.
        internal float SkyLuma = 0.5f, SeaLuma = 0.7f, DeckRadiance = 0.075f, ClipAt = 1f;
        internal Func<int, int, bool> DeckCovers = (x, y) => true;
        // The state the picture shows (a test can tamper with it): the deck is drawn when the pass is on, draws every layer and the aid is off.
        internal bool NoRawPixels, EmptyHorizon;
        internal int RawWidth = 64, RawHeight = 36;
        internal readonly List<string> EnvironmentRequests = new List<string>();
        internal bool EnvironmentThrows;

        public void SetLayerMask(int mask)
        {
            LayerMaskNow = mask;
            if (Module != null) Module.Frame.LayerMask = mask;
        }

        public bool AidOn(int mode)
        {
            RequireFlight();
            if (!AidAvailable) return false;
            AidIsOn = true;
            AidModeNow = mode;
            Stamp("aidon" + mode);
            return true;
        }

        public IList<KeyValuePair<string, string>> EnvironmentLines()
        {
            RequireFlight();
            EnvironmentRequests.Add(Frames + "@" + RealS.ToString("0.0"));
            if (EnvironmentThrows) throw new InvalidOperationException("no light");
            return new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("sunLight", "Sun: intensity 1.2"),
                new KeyValuePair<string, string>("colourSpace", "active Gamma")
            };
        }

        private T09Image RawImage()
        {
            int w = RawWidth, h = RawHeight;
            double elevation, azimuth;
            CameraAim(out azimuth, out elevation);
            // Independent of T09Readout: the horizon's angle below the horizontal, then its row by the pinhole model.
            double cameraHeight = VesselAltitude;
            double dip = Math.Acos(Radius / (Radius + Math.Max(0.0, cameraHeight)));
            double angleFromAxis = -dip - elevation * Math.PI / 180.0;
            double horizonV = 0.5 + Math.Tan(angleFromAxis) / (2.0 * Math.Tan(FovDeg * Math.PI / 360.0));
            float gain = Tuning.Get(OwnCloudModule.TuneSunGain, 1f);
            bool deck = Module != null && Module.Stats.Mode != "off" && LayerMaskNow == 0 && !AidIsOn;
            bool aid = AidIsOn && !AidIgnored && AidModeNow == 4;
            var raw = new Color[w * h];
            for (int y = 0; y < h; y++)
            {
                double v = (y + 0.5) / h;
                for (int x = 0; x < w; x++)
                {
                    float l = v > horizonV ? SkyLuma : SeaLuma;
                    Color c = new Color(l, l, l, 1f);
                    bool covered = DeckCovers(x, y);
                    if (deck && covered) c = new Color(Math.Min(ClipAt, DeckRadiance * gain), Math.Min(ClipAt, DeckRadiance * gain), Math.Min(ClipAt, DeckRadiance * gain), 1f);
                    if (aid && covered && Module != null && Module.Stats.Mode != "off" && LayerMaskNow == 0) c = new Color(0.3f, 0.5f, 0.1f, 1f);
                    raw[y * w + x] = c;
                }
            }
            return new T09Image { Width = w, Height = h, Pixels = NoRawPixels ? new Color[w * h] : (Color[])raw.Clone(), Raw = NoRawPixels ? null : raw };
        }

        public bool AidOn()
        {
            RequireFlight();
            if (!AidAvailable) return false;
            AidIsOn = true;
            Stamp("aidon");
            return true;
        }

        public void AidOff()
        {
            if (AidIsOn) Stamp("aidoff");
            AidIsOn = false;
        }

        public long SavePicture(string name, T09Image image, int factor)
        {
            string fault = SaveFault(SaveCalls++);
            Stamp("save:" + name + ":" + factor);
            if (fault == "full") throw new IOException("There is not enough space on the disk");
            if (fault == "denied") throw new UnauthorizedAccessException("access to the path is denied");
            Saved.Add(name);
            SavedFactor.Add(factor);
            return 40000 / factor;
        }
    }
}
