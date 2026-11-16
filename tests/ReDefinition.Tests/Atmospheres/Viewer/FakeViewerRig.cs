using System;
using System.Collections.Generic;
using System.IO;
using ReDefinition.Atmospheres;
using ReDefinition.Atmospheres.Viewer;

namespace ReDefinition.Tests.Atmospheres.Viewer
{
    // The viewer probe's game without the game (IViewerRig): the viewer's own pure parts do the work where they exist (ViewerClock, OptionFlip and OptionSet,
    // WeatherPresets, CaptureSchedule, ViewerSidecar, AltitudeScale), and the rest is a small model of what the game does with them: the vessel's placement, the cloud pass
    // reading the tuning table every frame (exposure, noise clock, drift), the map mirror slewing to the forced cover, the files. A fault is a flag that makes one of the
    // viewer's effects not happen, so that the probe has to fail with the named check.
    internal class FakeViewerRig : IViewerRig
    {
        // The store of the model: the settings and the overrides the game's table would hold.
        internal sealed class Store : IOptionStore
        {
            internal readonly Dictionary<string, double> Settings = new Dictionary<string, double>
            {
                { AtmospheresSettings.Keys.CloudSoftness, 0.5 }, { AtmospheresSettings.Keys.ForceLevel, -1.0 }, { AtmospheresSettings.Keys.ForceType, 0.0 }
            };
            internal readonly Dictionary<string, float> Overrides = new Dictionary<string, float>();

            public double Setting(string key) { double v; return Settings.TryGetValue(key, out v) ? v : 0.0; }
            public bool SetSetting(string key, double value) { Settings[key] = value; return true; }
            public bool TryOverride(string key, out float value) { return Overrides.TryGetValue(key, out value); }
            public void SetOverride(string key, float value) { Overrides[key] = value; }
            public void ClearOverride(string key) { Overrides.Remove(key); }
            public float Effective(string key, float compiledDefault) { float v; return Overrides.TryGetValue(key, out v) ? v : compiledDefault; }
        }

        // The faults.
        internal bool WindowNeverOpens, SpeedIgnored, FlipDoesNothing, SliderPlacesWrong, FreezeIgnored, WeatherNotForced, CombineStaysOn, WindowStaysVisible, NothingWritten,
            ReadNotSeen, SidecarWrongWeather, NoSecondName, TimeOffsetIgnored, CloseKeepsOptions, SunStandsStill;
        // More faults: an altitude change that puts the pose's aim back, a camera left behind by an altitude change, a clock the viewer leaves wrong at the close while its own natural
        // time stays right, rain at the pad (the deck's rain floor keeps a Fair cover up) and a field that is overcast at the pad before anything is forced.
        internal bool SliderResetsAim, CameraDoesNotFollow, ClockLeftWrongAtClose, RainAtPad, FieldOvercast;
        // A close that leaves the game's clock where the time offset and the freeze put it.
        internal bool CloseKeepsTimeOffset;
        // A game that runs at 0.4 of the real time (a low frame rate), a camera that looks the other way round from the aim asked for (the viewer's first guess of the camera's
        // heading before it was tuned), a window that stands half beyond the left edge of the screen and one that cannot be measured.
        internal bool ClockDiverges, SlowGame, AimLooksAway, WindowCutOffLeft, WindowNotMeasurable;
        // The screen the window stands on.
        internal int ScreenW = 1707, ScreenH = 960;
        internal float UiScaleValue = 1.5f;

        internal readonly Store Options = new Store();
        internal static readonly ViewerLayer[] KerbinLayers =
        {
            new ViewerLayer { Name = "Kerbin-weather-rain", Group = CloudCoverShift.RainGroup, InnerAltitudeM = 300f, ThicknessM = 3000f },
            new ViewerLayer { Name = "Kerbin-cumulus", Group = "", InnerAltitudeM = 600f, ThicknessM = 7000f },
            new ViewerLayer { Name = "Kerbin-cumulonimbus", Group = "", InnerAltitudeM = 500f, ThicknessM = 12000f },
            new ViewerLayer { Name = "Kerbin-cirrus", Group = "", InnerAltitudeM = 9000f, ThicknessM = 2000f }
        };

        private readonly List<string> keyNames = new List<string>();
        private readonly List<string> settingKeys = ViewerOptionKeys.Settings(new[] { AtmospheresSettings.Keys.CloudSoftness });
        private readonly OptionFlip flip = new OptionFlip();
        private readonly CaptureSchedule schedule = new CaptureSchedule(2);
        private readonly Dictionary<string, long> files = new Dictionary<string, long>();
        private readonly Dictionary<string, string> texts = new Dictionary<string, string>();
        protected readonly Dictionary<string, double> openSettings = new Dictionary<string, double>();
        private readonly HashSet<string> touched = new HashSet<string>();
        private readonly double[] drift = new double[KerbinLayers.Length * 3];
        private readonly float[] cover = new float[KerbinLayers.Length];
        private ViewerClock clock;
        private bool open, session, captureWasBusy;
        private double real, gameUt = 1000800.0, flow, physics = 500.0;
        private int frame = 100, captureFrame = -1, releasedFrame = -1, captures, placedFrame;
        private float exposure = 1f;
        private string watched;
        private int watchedRead = int.MinValue;
        private ViewerWeather weather = ViewerWeather.Field;
        private string pendingName, lastCapture = "", poseName = "";
        // The vessel.
        private double agl, wantAgl, camDist = 100.0, aimAz, aimEl;
        private int aimIn = -1;
        private bool keepAim;
        private double startAsl = 70.0;

        internal FakeViewerRig()
        {
            foreach (ViewerLayer l in KerbinLayers) cover[Array.IndexOf(KerbinLayers, l)] = 0.4f;
        }

        // One frame of the game: the clock, the viewer's tick, then the pass reads the table.
        internal void Advance(double dt)
        {
            real += dt;
            frame++;
            // The game's own time: at a low frame rate the physics steps are limited per frame and the game's clock runs slower than the real time (0.4 of it at 10 fps).
            double game = dt * (SlowGame ? 0.4 : 1.0);
            gameUt += game * (ClockDiverges ? 0.5 : 1.0);
            physics += game;
            if (session)
            {
                if (!(FreezeIgnored && clock.Frozen)) gameUt = clock.Step(gameUt);
                double waiting;
                if (plan.TryTake(real, 0.4, out waiting)) Place(waiting, true);
                if (aimIn >= 0 && --aimIn < 0)
                {
                    if (!keepAim)
                    {
                        // aim kept where it was; the pose's aim was set at the placement
                    }
                }
                if (schedule.Step()) TakeCapture();
                bool busy = schedule.Busy;
                if (captureWasBusy && !busy) releasedFrame = frame;
                captureWasBusy = busy;
            }
            // The pass: the table's values this frame.
            exposure = Options.Effective("cloud.exposure", 1f);
            float scale = SpeedIgnored ? 1f : CloudMotion.Clamp(Options.Effective(CloudMotion.TuneScale, 1f));
            flow += dt * scale;
            for (int l = 0; l < drift.Length; l += 3)
            {
                drift[l] += 1.5 * dt * scale;
                drift[l + 1] += 0.5 * dt * scale;
            }
            if (watched != null && !ReadNotSeen && (watched == "cloud.exposure" || watched == CloudMotion.TuneScale || ReadsAnyKey)) watchedRead = frame;
            // The map mirror slews to the forced cover (or the field's 0.4), the combination scales a forced cover down to the field's.
            for (int l = 0; l < cover.Length; l++)
            {
                float target = Options.Effective(CloudLookTuning.TuneMapOverride + KerbinLayers[l].Name, -1f);
                if (target < 0f) target = FieldOvercast ? 0.9f : 0.4f;
                else if (Options.Effective(CloudLookTuning.TuneCoverCombine, 1f) > 0f) target = Math.Min(target, 0.4f);
                // The deck's rain floor applies unless the preset switched the deck rule off.
                if (RainAtPad && l == 0 && Options.Effective(CloudLookTuning.TuneDeckRule + KerbinLayers[l].Name, -1f) != 0f) target = Math.Max(target, 0.6f);
                target = CoverTarget(l, target);
                cover[l] += (target - cover[l]) * (float)Math.Min(1.0, dt / 2.0);
            }
        }

        private readonly AltitudePlan plan = new AltitudePlan();

        // What the altitude slider shows (the viewer's AltitudeSlider).
        internal float AltitudeSlider { get { return plan.SliderValue; } }

        private void Place(double aglM, bool keep)
        {
            plan.Place(aglM);
            wantAgl = aglM;
            agl = SliderPlacesWrong && keep ? aglM + 300.0 : aglM;
            keepAim = keep;
            if (keep && SliderResetsAim)
            {
                aimAz = poseAz;
                aimEl = poseEl;
            }
            aimIn = 20;
            placedFrame = frame;
        }

        private void TakeCapture()
        {
            captureFrame = frame;
            if (NothingWritten) return;
            string png = CapturePicturePath(pendingName);
            files[png] = 4000;
            var record = new CaptureRecord
            {
                Picture = pendingName + ".png", Taken = "t", Body = "Kerbin", Pose = poseName, Weather = WeatherPresets.Title(SidecarWrongWeather ? ViewerWeather.Overcast : weather), Ut = gameUt,
                TimeFrozen = clock.Frozen, CloudSpeed = CloudMotion.Clamp(Options.Effective(CloudMotion.TuneScale, 1f)), Side = flip.Showing, AltitudeAglM = CameraAgl()
            };
            texts[Path.ChangeExtension(png, ".txt")] = ViewerSidecar.Format(record, AtmospheresSettings.Definitions, AtmospheresSettings.Number, AtmospheresSettings.Text,
                                                                          CloudKeyRegistry.Expand(LayerNames()), Options);
            lastCapture = pendingName + ".png";
            OnPicture(lastCapture);
        }

        // Hooks of the solo model: the cover a layer's map slews to at the vessel, and a picture just written.
        protected virtual float CoverTarget(int layer, float target) { return target; }
        protected virtual void OnPicture(string name) { }

        private static IEnumerable<string> LayerNames()
        {
            foreach (ViewerLayer l in KerbinLayers) yield return l.Name;
        }

        private double CameraAgl()
        {
            return agl - camDist * Math.Sin(aimEl * Math.PI / 180.0);
        }

        // --- The game ---
        public bool FlightReady { get { return true; } }
        public bool PassDrawing { get { return !PassForcedOff; } }
        // The workbench section: the own clouds switched off stop the pass; with ReadsAnyKey the pass reads every watched key (the model knows two keys otherwise).
        internal bool PassForcedOff, ReadsAnyKey;
        public int PassFramesDrawn { get { return frame; } }
        public IList<ViewerLayer> Layers { get { return KerbinLayers; } }
        public int Frame { get { return frame; } }
        public double RealS { get { return real; } }
        public double PhysicsTimeS { get { return physics; } }
        public double GameUt { get { return gameUt; } }
        public virtual double SunElevationDeg { get { return SunStandsStill ? 30.0 : 80.0 * Math.Sin(2.0 * Math.PI * gameUt / 21600.0); } }
        // The sun stands in the west (afternoon); the angle to the camera's direction is the great-circle angle between the two directions.
        internal double SunAzimuth = 270.0;
        public double SunAzimuthDeg { get { return SunAzimuth; } }

        public double CameraSunAngleDeg
        {
            get
            {
                ViewerPoseReading p = Pose;
                double e1 = p.ElevationDeg * Math.PI / 180.0, e2 = SunElevationDeg * Math.PI / 180.0, da = (p.AzimuthDeg - SunAzimuth) * Math.PI / 180.0;
                double c = Math.Sin(e1) * Math.Sin(e2) + Math.Cos(e1) * Math.Cos(e2) * Math.Cos(da);
                return Math.Acos(Math.Max(-1.0, Math.Min(1.0, c))) * 180.0 / Math.PI;
            }
        }

        public bool HoldStable { get { return frame - placedFrame >= 30; } }
        public double FlowTimeS { get { return flow; } }
        public IList<double> DriftM { get { return (double[])drift.Clone(); } }
        public float PassExposure { get { return exposure; } }
        public float PassMapOverride(string layerName) { return WeatherNotForced ? -1f : Options.Effective(CloudLookTuning.TuneMapOverride + layerName, -1f); }
        public float LayerCover(int layer) { return cover[layer]; }
        public double PrecipitationLevel { get { return RainAtPad && !RainUnreadable ? 0.5 : 0.0; } }
        // The rain floor is there but the field reads no rain.
        internal bool RainUnreadable;
        public float TuningValue(string key, float compiledDefault) { return Options.Effective(key, compiledDefault); }
        public bool HasOverride(string key) { return Options.Overrides.ContainsKey(key); }
        public double Setting(string key) { return Options.Setting(key); }
        public int TuningFrame { get { return frame; } }
        public int WatchedReadFrame { get { return watchedRead; } }

        public virtual ViewerPoseReading Pose
        {
            get
            {
                return new ViewerPoseReading
                {
                    VesselAslM = startAsl + agl, VesselAglM = agl, CameraAglM = CameraAgl(), CameraToVesselM = CameraDoesNotFollow ? camDist + Math.Abs(agl - poseAgl) : camDist,
                    AzimuthDeg = AimLooksAway ? ViewerAim.Wrap(aimAz + 180.0) : aimAz, ElevationDeg = aimEl
                };
            }
        }

        // --- The viewer ---
        public bool AddonRunning { get { return true; } }
        public bool WindowOpen { get { return open; } }
        public bool WindowVisible { get { return open && (WindowStaysVisible || !schedule.Busy); } }
        public int ScreenWidth { get { return ScreenW; } }
        public int ScreenHeight { get { return ScreenH; } }
        public float UiScale { get { return UiScaleValue; } }
        public string AimNote { get { return null; } }

        // The window as the placement leaves it: inside the screen, its height as the list's height at this UI scale allows; a fault puts it half beyond the left edge.
        public bool WindowBox(out ScreenBox box)
        {
            box = default(ScreenBox);
            if (!open || WindowNotMeasurable) return false;
            float width = 660f * UiScaleValue, height = (WindowPlacement.ListHeight(ScreenH, UiScaleValue) + 80f) * UiScaleValue;
            float x = WindowCutOffLeft ? -0.4f * width : Math.Max(8f, ScreenW * 0.5f);
            if (x + width > ScreenW - 8f && !WindowCutOffLeft) x = ScreenW - 8f - width;
            box = new ScreenBox { XMin = x, XMax = x + width, YMin = 8f, YMax = 8f + height };
            return true;
        }
        public bool SessionActive { get { return session; } }
        public bool PlacementPending { get { return plan.Pending || aimIn >= 0; } }
        public int KeyCount { get { return KeysExpected; } }
        public int KeysExpected { get { return CloudKeyRegistry.All.Count + KerbinLayers.Length * CloudKeyRegistry.PerLayer.Count; } }
        public double ClockNatural { get { return clock != null ? clock.Natural : double.NaN; } }
        public double ClockOffsetS { get { return clock != null ? clock.OffsetS : double.NaN; } }
        public bool ClockFrozen { get { return clock != null && clock.Frozen; } }
        public double ClockFrozenUt { get { return clock != null ? clock.FrozenUt : double.NaN; } }
        public float CloudSpeed { get { return CloudMotion.Clamp(Options.Effective(CloudMotion.TuneScale, 1f)); } }
        public bool CloudsFrozen { get; private set; }
        public char Showing { get { return flip.Showing; } }
        public bool Capturing { get { return schedule.Busy; } }
        public string LastCapture { get { return lastCapture; } }
        public int CaptureFrame { get { return captureFrame; } }
        public int CaptureReleasedFrame { get { return releasedFrame; } }

        public void ToggleWindow()
        {
            if (WindowNeverOpens && !open) return;
            if (!open)
            {
                open = session = true;
                clock = new ViewerClock(gameUt);
                plan.Clear();
                speedMemory.Reset(CloudSpeed);
                openSettings.Clear();
                foreach (string k in new[] { AtmospheresSettings.Keys.CloudSoftness, AtmospheresSettings.Keys.ForceLevel, AtmospheresSettings.Keys.ForceType }) openSettings[k] = Options.Setting(k);
                touched.Clear();
                flip.Clear();
                keyNames.Clear();
                foreach (CloudKey k in CloudKeyRegistry.Expand(LayerNames())) keyNames.Add(k.Key);
                weather = ViewerWeather.Field;
                CloudsFrozen = false;
                return;
            }
            open = session = false;
            if (!CloseKeepsTimeOffset) gameUt = clock.Natural + (ClockLeftWrongAtClose ? 600.0 : 0.0);
            if (!CloseKeepsOptions)
            {
                foreach (KeyValuePair<string, double> pair in openSettings) Options.SetSetting(pair.Key, pair.Value);
                foreach (string key in touched) Options.ClearOverride(key);
            }
            agl = 0.0;
            aimEl = aimAz = 0.0;
        }

        protected void Note(string key)
        {
            touched.Add(key);
        }

        public void PlacePose(ViewerPose pose)
        {
            poseName = pose.Name;
            aimAz = pose.HeadingDeg;
            aimEl = double.IsNaN(pose.ElevationDeg) ? 0.0 : pose.ElevationDeg;
            camDist = pose.CameraDistM;
            poseAz = aimAz;
            poseEl = aimEl;
            Place(pose.AglM, false);
            poseAgl = agl;
        }

        private double poseAz, poseEl, poseAgl;

        public void SetCameraAim(double headingDeg, double elevationDeg)
        {
            aimAz = headingDeg;
            aimEl = elevationDeg;
        }

        public void RequestAltitude(double aglM)
        {
            plan.Request(aglM, real);
        }

        public void SetTimeOffset(double seconds)
        {
            if (TimeOffsetIgnored) return;
            clock.OffsetS = seconds;
        }

        public void FreezeTime(bool on)
        {
            clock.Freeze(on);
        }

        public void SetWeather(ViewerWeather w)
        {
            weather = w;
            if (WeatherNotForced) return;
            foreach (KeyValuePair<string, float> pair in WeatherPresets.Overrides(w, KerbinLayers))
            {
                Note(pair.Key);
                if (float.IsNaN(pair.Value)) Options.ClearOverride(pair.Key);
                else Options.SetOverride(pair.Key, pair.Value);
            }
            float combine = WeatherPresets.CombineOverride(w);
            Note(CloudLookTuning.TuneCoverCombine);
            if (float.IsNaN(combine) || CombineStaysOn) Options.ClearOverride(CloudLookTuning.TuneCoverCombine);
            else Options.SetOverride(CloudLookTuning.TuneCoverCombine, combine);
            double level;
            ForceType type;
            WeatherPresets.Precipitation(w, out level, out type);
            Options.SetSetting(AtmospheresSettings.Keys.ForceLevel, level >= 0.0 ? level : openSettings[AtmospheresSettings.Keys.ForceLevel]);
            Options.SetSetting(AtmospheresSettings.Keys.ForceType, level >= 0.0 ? (double)(int)type : openSettings[AtmospheresSettings.Keys.ForceType]);
        }

        public void SetCloudSpeed(float scale)
        {
            CloudsFrozen = false;
            Note(CloudMotion.TuneScale);
            float speed = CloudMotion.Clamp(scale);
            speedMemory.Seen(speed);
            Options.SetOverride(CloudMotion.TuneScale, speed);
        }

        private readonly CloudSpeedMemory speedMemory = new CloudSpeedMemory();

        public void FreezeClouds(bool on)
        {
            if (on == CloudsFrozen) return;
            Note(CloudMotion.TuneScale);
            if (on)
            {
                speedMemory.Seen(CloudSpeed);
                Options.SetOverride(CloudMotion.TuneScale, 0f);
            }
            else Options.SetOverride(CloudMotion.TuneScale, speedMemory.Last);
            CloudsFrozen = on;
        }

        public void StoreA()
        {
            flip.StoreA(OptionSet.Capture(Options, settingKeys, keyNames, weather));
        }

        // The forced weather the viewer shows now (its label).
        internal ViewerWeather CurrentWeather { get { return weather; } }

        public void FlipAB()
        {
            if (FlipDoesNothing) return;
            foreach (string k in keyNames) Note(k);
            flip.Flip(Options, settingKeys, keyNames, ref weather);
        }

        public void SetKey(string key, float value)
        {
            Note(key);
            Options.SetOverride(key, value);
        }

        public void SetViewerSetting(string key, double value)
        {
            Options.SetSetting(key, value);
        }

        public void WatchKey(string key)
        {
            watched = key;
            watchedRead = int.MinValue;
        }

        public void RequestCapture()
        {
            if (!schedule.Request()) return;
            captures++;
            // The name is the clock's second: both pictures of a quick pair would get the same stamp, the viewer numbers them.
            string stamp = "20261115-120000";
            pendingName = NoSecondName ? stamp : ViewerPresets.UniqueName(stamp, n => files.ContainsKey(CapturePicturePath(n)));
        }

        // --- Files ---
        public string CapturePicturePath(string fileName)
        {
            return Path.Combine("C:\\fake\\CloudViewer\\Captures", Path.GetFileNameWithoutExtension(fileName) + ".png");
        }

        public string ReadText(string path)
        {
            string text;
            return texts.TryGetValue(path, out text) ? text : null;
        }

        public long FileSize(string path)
        {
            long size;
            return files.TryGetValue(path, out size) ? size : -1;
        }

        public string Screenshot(string name)
        {
            string path = Path.Combine("C:\\fake\\series-pictures", name);
            files[path] = 9000;
            return path;
        }
    }
}
