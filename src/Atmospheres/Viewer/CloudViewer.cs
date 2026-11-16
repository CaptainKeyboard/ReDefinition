using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres.Viewer
{
    // The cloud viewer's session in the flight scene: the scene control (poses, altitude, time of day, frozen time, forced weather, cloud speed, the baseline
    // without layers), the options (settings and tuning overrides, A/B, presets) and the capture. Everything it changes is noted at Open and put back at Close:
    // the game time (the natural time, ViewerClock), the vessel's place (the hold of the K-T2 probe, KspT09World, places and holds it), the camera, the cheat
    // switches the placement sets, the settings it touched and the tuning overrides. The window (CloudViewerWindow) draws this and decides nothing.
    internal sealed partial class CloudViewer
    {
        internal static readonly CloudViewer Instance = new CloudViewer();

        // The settings the short option list shows (in this order); A/B and the presets hold every setting of the Clouds group.
        internal static readonly string[] ShortSettings =
        {
            AtmospheresSettings.Keys.CloudSoftness, AtmospheresSettings.Keys.CloudSamples, AtmospheresSettings.Keys.CloudResolution, AtmospheresSettings.Keys.CloudReconMode,
            AtmospheresSettings.Keys.LayerStepQuality, AtmospheresSettings.Keys.CloudLightQuality, AtmospheresSettings.Keys.CloudShadows, AtmospheresSettings.Keys.CloudShadowQuality,
            AtmospheresSettings.Keys.CloudNoiseTier, AtmospheresSettings.Keys.LayerCap, AtmospheresSettings.Keys.CloudFarHaze
        };

        private const int AimDelayFrames = 20, RestoreHoldFrames = 90, CaptureHideFrames = 2;
        private readonly CaptureSchedule capture = new CaptureSchedule(CaptureHideFrames);
        private const double AltitudeSettleS = 0.4;

        private readonly IOptionStore store = new GameOptionStore();
        private readonly KspT09World world = new KspT09World();
        private readonly OptionFlip flip = new OptionFlip();
        private readonly AltitudePlan heights = new AltitudePlan();
        private readonly Dictionary<string, double> settingsAtOpen = new Dictionary<string, double>(StringComparer.Ordinal);
        private readonly Dictionary<string, float> overridesAtOpen = new Dictionary<string, float>(StringComparer.Ordinal);
        private readonly HashSet<string> touched = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<string> settingKeys = new List<string>();
        private readonly ViewerKeyList keyList = new ViewerKeyList();
        private List<CloudKey> keys { get { return keyList.Keys; } }
        private List<string> keyNames { get { return keyList.Names; } }
        private ViewerClock clock;
        private bool ignoreTemperature, noCrashDamage, placed, frozenClouds;
        private double startLat, startLon, startAlt;
        private KspT09World.RootPose startRoot;
        private float cameraHeading, cameraPitch, cameraDistance;
        private FlightCamera.Modes cameraMode;
        private int aimIn = -1, restoreIn = -1;
        private double aimElevation = double.NaN, aimHeading;
        private bool aimKeep;
        private float keepHeading, keepPitch;
        private string pendingCapture;
        private readonly CloudSpeedMemory speedMemory = new CloudSpeedMemory();

        private CloudViewer()
        {
        }

        // When the vessel may be moved (PlacementGuard), and the reason the last placement was refused (null where it was not); the window shows it.
        private readonly PlacementGuard guard = new PlacementGuard();
        internal string PlacementNote { get; private set; }
        private float lastRefusalLog = -100f;

        private static bool VesselSettled(Vessel v)
        {
            return v != null && v.loaded && !v.packed;
        }

        // False, with the reason said in the window, the log and a screen message, where the vessel is still loading: nothing of it is moved then.
        private bool MayPlace()
        {
            Vessel v = GameGlobals.ActiveVessel;
            guard.Observe(Time.unscaledTime, VesselSettled(v));
            string why = guard.Refusal(Time.unscaledTime, v != null);
            // A vessel that is moving fast, in time warp or a kerbal on EVA is not teleported either: the placement zeroes its velocity, and a vessel warped while held would be packed
            // as landed and snapped to the ground (the game's own packed-vessel correction).
            if (why == null && v != null)
                why = PlacementGuard.StateRefusal(TimeWarp.CurrentRate > 1.0f, v.isEVA, v.srfSpeed);
            PlacementNote = why;
            if (why == null) return true;
            if (Time.unscaledTime - lastRefusalLog > 5f)
            {
                lastRefusalLog = Time.unscaledTime;
                AtmospheresLog.Say("cloud viewer: the vessel is not placed: " + why.ToLowerInvariant() + " (loaded " + (v != null && v.loaded) + ", packed " + (v != null && v.packed) + ")");
                ScreenMessages.PostScreenMessage("Cloud viewer: " + why + ".", 4f, ScreenMessageStyle.UPPER_CENTER);
            }
            return false;
        }

        internal bool Active { get; private set; }
        internal bool KeepOptions { get; set; }
        internal ViewerWeather Weather { get; private set; }
        internal string PoseName { get; private set; } = "";
        internal bool Baseline { get; private set; }
        internal double SiteLat { get; private set; }
        internal double SiteLon { get; private set; }
        internal double TargetAglM { get { return heights.Target; } }
        // The altitude slider's position: it follows the height of the last pose, slider move or typed height.
        internal float AltitudeSlider { get { return heights.SliderValue; } }
        internal string WatchedKey { get; private set; }
        internal string LastCapture { get; private set; } = "";
        internal IOptionStore Store { get { return store; } }
        internal OptionFlip Flip { get { return flip; } }
        internal IList<CloudKey> Keys { get { return keys; } }
        internal ViewerClock Clock { get { return clock; } }
        // Raised when the window must be built anew (the key list changed, the options were replaced).
        internal Action Changed;

        // Whether a session can start: the flight scene with a loaded active vessel.
        internal static string CannotOpen()
        {
            if (!HighLogic.LoadedSceneIsFlight) return "the cloud viewer runs in the flight scene";
            Vessel v = GameGlobals.ActiveVessel;
            if (v == null || v.mainBody == null || !v.loaded) return "no active vessel";
            if (FlightCamera.fetch == null) return "no flight camera";
            return null;
        }

        internal bool Open()
        {
            if (Active) return true;
            string why = CannotOpen();
            if (why != null)
            {
                ScreenMessages.PostScreenMessage("Cloud viewer: " + why + ".", 4f, ScreenMessageStyle.UPPER_CENTER);
                return false;
            }
            if (restoreIn >= 0) FinishRestore();
            Vessel v = GameGlobals.ActiveVessel;
            clock = new ViewerClock(Planetarium.GetUniversalTime());
            v.mainBody.GetLatLonAlt(v.GetWorldPos3D(), out startLat, out startLon, out startAlt);
            startRoot = KspT09World.ReadRoot(v);
            SiteLat = startLat;
            SiteLon = startLon;
            ignoreTemperature = CheatOptions.IgnoreMaxTemperature;
            noCrashDamage = CheatOptions.NoCrashDamage;
            FlightCamera fc = FlightCamera.fetch;
            cameraHeading = fc.camHdg;
            cameraPitch = fc.camPitch;
            cameraDistance = fc.Distance;
            cameraMode = fc.mode;
            placed = false;
            Weather = ViewerWeather.Field;
            PoseName = "start";
            Baseline = false;
            frozenClouds = false;
            speedMemory.Reset(CloudSpeed);
            heights.Clear();
            settingKeys.Clear();
            settingsAtOpen.Clear();
            var cloudSettings = new List<string>();
            foreach (SettingDef def in AtmospheresSettings.Definitions)
                if (def.Category == AtmospheresSettings.Categories.Clouds && def.Kind != SettingKind.Text) cloudSettings.Add(def.Key);
            // The Clouds group and the forced precipitation: what A/B and the presets hold, and what is put back at Close.
            settingKeys.AddRange(ViewerOptionKeys.Settings(cloudSettings));
            foreach (string key in settingKeys) settingsAtOpen[key] = store.Setting(key);
            overridesAtOpen.Clear();
            touched.Clear();
            flip.Clear();
            RefreshKeys();
            Active = true;
            AtmospheresLog.Say("cloud viewer: opened over " + v.mainBody.name + " at " + startLat.ToString("0.0000", CultureInfo.InvariantCulture) + ", "
                               + startLon.ToString("0.0000", CultureInfo.InvariantCulture) + ", " + startAlt.ToString("0", CultureInfo.InvariantCulture) + " m; UT "
                               + clock.Natural.ToString("0", CultureInfo.InvariantCulture) + "; " + keys.Count + " tuning keys, " + settingKeys.Count + " settings");
            return true;
        }

        // The keys of the list: the registry's and the per-layer keys of the stack that stands. Overrides set before the viewer opened are noted for the restore.
        internal bool RefreshKeys()
        {
            bool rebuilt = keyList.Sync(new List<string>(LayerNames()));
            foreach (CloudKey k in keys)
            {
                float value;
                if (!touched.Contains(k.Key) && !overridesAtOpen.ContainsKey(k.Key) && store.TryOverride(k.Key, out value)) overridesAtOpen[k.Key] = value;
            }
            return rebuilt;
        }

        // The list is built for the layers that stand now: the viewer may have opened before the own stack stood (the per-layer keys, and with them the weather overrides, would
        // be missing from A/B, the presets and the sidecars). Where it changed the window is built again, unless the caller does it (notify false).
        private void EnsureKeys(bool notify)
        {
            if (RefreshKeys() && notify && Changed != null) Changed();
        }

        private static int StackLayerCount()
        {
            OwnCloudModule m = OwnCloudModule.Current;
            LayerStackSource stack = m == null ? null : m.Stack as LayerStackSource;
            return stack == null ? 0 : stack.LayerCount;
        }

        private static IEnumerable<string> LayerNames()
        {
            foreach (ViewerLayer l in Layers()) yield return l.Name;
        }

        // The layers of the stack of the own cloud pass (empty where it does not draw).
        internal static List<ViewerLayer> Layers()
        {
            var list = new List<ViewerLayer>();
            OwnCloudModule m = OwnCloudModule.Current;
            LayerStackSource stack = m == null ? null : m.Stack as LayerStackSource;
            if (stack == null) return list;
            for (int l = 0; l < stack.LayerCount; l++)
            {
                CloudLayerDef def = stack.LayerAt(l);
                list.Add(new ViewerLayer { Name = def.Name, Group = def.Group, InnerAltitudeM = def.InnerAltitudeM, ThicknessM = def.ThicknessM });
            }
            return list;
        }

        // Ends the session and puts everything back (the vessel is held at its old place for a moment before it is let go).
        internal void Close(bool sceneEnding = false)
        {
            if (!Active) return;
            Active = false;
            tuner = null;
            ShowHud();
            try
            {
                if (clock != null && HighLogic.LoadedSceneIsFlight) Planetarium.SetUniversalTime(clock.Natural);
                if (!KeepOptions)
                {
                    foreach (KeyValuePair<string, double> pair in settingsAtOpen) store.SetSetting(pair.Key, pair.Value);
                    foreach (string key in touched)
                    {
                        float value;
                        if (overridesAtOpen.TryGetValue(key, out value)) store.SetOverride(key, value);
                        else store.ClearOverride(key);
                    }
                }
                else
                {
                    // The forced weather is the viewer's and goes with it in any case.
                    store.SetSetting(AtmospheresSettings.Keys.ForceLevel, settingsAtOpen[AtmospheresSettings.Keys.ForceLevel]);
                    store.SetSetting(AtmospheresSettings.Keys.ForceType, settingsAtOpen[AtmospheresSettings.Keys.ForceType]);
                    foreach (KeyValuePair<string, float> pair in WeatherPresets.Overrides(ViewerWeather.Field, Layers())) Restore(pair.Key);
                    Restore(CloudMotion.TuneScale);
                    Restore(CloudLookTuning.TuneCoverCombine);
                    RestoreSolo();
                }
                Tuning.Instance.Watch(null);
                WatchedKey = null;
                world.SetLayerMask(0);
                CloudBridgeLights.RequestHistoryReset("cloud viewer closed");
                // The vessel back at its start: held there for a moment, or set there at once where the scene is about to be saved and left.
                // The root of the placed vessel goes back exactly where it was (position and rotation), at rest, its parts still held kinematic; a few frames later they are let go
                // (FinishRestore: StopHold). The hold is not started again: it drives the centre of mass, not the root.
                bool restored = placed && world.PlacedVessel != null && world.RestoreVessel(startRoot);
                if (placed && !restored) AtmospheresLog.Say("cloud viewer: the vessel could not be put back exactly (" + (world.PlacedVessel == null ? "none was placed" : startRoot.Valid ? "it is gone" : "its start was not read") + "); it is let go where it stands");
                if (restored && !sceneEnding) restoreIn = RestoreHoldFrames;
                else FinishRestore();
                AtmospheresLog.Say("cloud viewer: closed; UT back to " + (clock != null ? clock.Natural.ToString("0", CultureInfo.InvariantCulture) : "-") + ", "
                                   + (KeepOptions ? "options kept" : "options put back") + (placed ? ", vessel back to its start" : ""));
            }
            catch (Exception e)
            {
                AtmospheresLog.Failed("cloud viewer", "the viewer could not put everything back", e);
                FinishRestore();
            }
        }

        // The scene ends: the session closes at once and a vessel still held at its start is let go.
        internal void EndScene()
        {
            Close(true);
            if (restoreIn >= 0) FinishRestore();
        }

        private void Restore(string key)
        {
            float value;
            if (overridesAtOpen.TryGetValue(key, out value)) store.SetOverride(key, value);
            else store.ClearOverride(key);
        }

        private void FinishRestore()
        {
            restoreIn = -1;
            try
            {
                world.StopHold();
                if (placed && FlightCamera.fetch != null)
                {
                    FlightCamera fc = FlightCamera.fetch;
                    fc.mode = cameraMode;
                    fc.camHdg = cameraHeading;
                    fc.camPitch = cameraPitch;
                    fc.SetDistanceImmediate(cameraDistance);
                }
            }
            catch (Exception e)
            {
                AtmospheresLog.Failed("cloud viewer", "the camera could not be put back", e);
            }
            CheatOptions.IgnoreMaxTemperature = ignoreTemperature;
            CheatOptions.NoCrashDamage = noCrashDamage;
            placed = false;
        }

        // Every frame (CloudViewerAddon). Allocates nothing but where a step runs (a placement, a capture).
        internal void Tick()
        {
            if (restoreIn >= 0 && --restoreIn < 0) FinishRestore();
            // The vessel's settling is watched whether the viewer is open or not, so that the first placement after opening it is not refused for a vessel that has long stood.
            guard.Observe(Time.unscaledTime, VesselSettled(GameGlobals.ActiveVessel));
            if (PlacementNote == PlacementGuard.LoadingMessage && guard.Refusal(Time.unscaledTime, GameGlobals.ActiveVessel != null) == null) PlacementNote = null;
            if (!Active) return;
            // The vessel that was placed is not the active one any more (a switch, an EVA, a staging): the hold has stopped acting on it; the viewer closes and puts it back.
            if (placed && world.PlacedVesselLost)
            {
                AtmospheresLog.Say("cloud viewer: the active vessel is not the one that was placed; the viewer closes and puts the placed vessel back");
                Close(false);
                return;
            }
            if (!HighLogic.LoadedSceneIsFlight || GameGlobals.ActiveVessel == null)
            {
                Close(true);
                return;
            }
            if (StackLayerCount() != keyList.LayerCount) EnsureKeys(true);
            double observed = Planetarium.GetUniversalTime();
            double next = clock.Step(observed);
            if (next != observed) Planetarium.SetUniversalTime(next);
            double agl;
            if (heights.TryTake(Time.unscaledTime, AltitudeSettleS, out agl)) PlaceAt(agl, true);
            if (aimIn >= 0 && --aimIn < 0) Aim();
            TuneAim();
            if (capture.Step()) TakeCapture();
            bool busy = capture.Busy;
            if (captureWasBusy && !busy)
            {
                CaptureReleasedFrame = Time.frameCount;
                ShowHud();
            }
            captureWasBusy = busy;
        }

        private bool captureWasBusy;

        // The game's frame in which the last picture was asked for and the first frame the window was let back in (the probe reads the order of the two).
        internal int CaptureFrame { get; private set; } = -1;
        internal int CaptureReleasedFrame { get; private set; } = -1;

        // A placement is under way: an altitude waits for the slider to stand still, or the camera is yet to be aimed.
        internal bool PlacementPending
        {
            get { return heights.Pending || aimIn >= 0 || (tuner != null && !tuner.Done); }
        }

        internal KspT09World World
        {
            get { return world; }
        }

        // --- Scene ---

        internal void UseCurrentSpot()
        {
            Vessel v = GameGlobals.ActiveVessel;
            if (v == null) return;
            double alt;
            double lat, lon;
            v.mainBody.GetLatLonAlt(v.GetWorldPos3D(), out lat, out lon, out alt);
            SiteLat = lat;
            SiteLon = lon;
            AtmospheresLog.Say("cloud viewer: site " + lat.ToString("0.0000", CultureInfo.InvariantCulture) + ", " + lon.ToString("0.0000", CultureInfo.InvariantCulture));
        }

        internal void PlacePose(ViewerPose pose)
        {
            if (!MayPlace()) return;
            PoseName = pose.Name;
            aimHeading = pose.HeadingDeg;
            aimElevation = pose.ElevationDeg;
            aimKeep = false;
            cameraDistanceWanted = pose.CameraDistM;
            PlaceAt(pose.AglM, false);
        }

        private double cameraDistanceWanted = 150.0;

        // The altitude control: the vessel over the same site at that height above the ground, the camera's heading and pitch kept. The slider moves many times:
        // the placement follows once it has stood still for AltitudeSettleS.
        internal void RequestAltitude(double aglM)
        {
            heights.Request(aglM, Time.unscaledTime);
        }

        private void PlaceAt(double aglM, bool keepAim)
        {
            Vessel v = GameGlobals.ActiveVessel;
            if (v == null || !MayPlace()) return;
            tuner = null;
            double terrain = 0.0;
            try { terrain = world.TerrainAltitude(SiteLat, SiteLon); } catch (Exception) { terrain = 0.0; }
            double altitude = Math.Max(0.0, terrain) + aglM;
            if (keepAim && FlightCamera.fetch != null)
            {
                keepHeading = FlightCamera.fetch.camHdg;
                keepPitch = FlightCamera.fetch.camPitch;
                aimKeep = true;
            }
            world.PlaceVessel(SiteLat, SiteLon, altitude);
            world.StartHold();
            placed = true;
            heights.Place(aglM);
            aimIn = AimDelayFrames;
            CloudBridgeLights.RequestHistoryReset("cloud viewer placement");
            AtmospheresLog.Say("cloud viewer: vessel placed " + aglM.ToString("0", CultureInfo.InvariantCulture) + " m above the ground (" + altitude.ToString("0", CultureInfo.InvariantCulture)
                               + " m) for the " + PoseName + " pose");
        }

        private void Aim()
        {
            FlightCamera fc = FlightCamera.fetch;
            if (fc == null) return;
            fc.mode = FlightCamera.Modes.FREE;
            if (aimKeep)
            {
                tuner = null;
                fc.camHdg = keepHeading;
                fc.camPitch = keepPitch;
                return;
            }
            world.SetCameraDistance(cameraDistanceWanted);
            double el = aimElevation;
            if (double.IsNaN(el)) el = -T09Probe.HorizonDipDeg(world.BodyRadius, double.IsNaN(TargetAglM) ? 0.0 : TargetAglM);
            // A first guess (ViewerAim); TuneAim then turns the camera by what its measured aim lacks, until the view looks where the pose says.
            fc.camHdg = ViewerAim.HeadingRad(aimHeading);
            fc.camPitch = ViewerAim.PitchRad(el);
            tuner = new AimTuner(aimHeading, el);
            AimNote = null;
        }

        // The tuner of the pose's aim, and why it stopped short of the aim (null where it got there).
        private AimTuner tuner;
        internal string AimNote { get; private set; }

        // Every frame while a pose's aim is being tuned: the camera's real aim is read and the heading and pitch turned by the error once it stands still.
        private void TuneAim()
        {
            if (tuner == null || tuner.Done)
            {
                if (tuner != null && tuner.Note != null && AimNote == null)
                {
                    AimNote = tuner.Note;
                    AtmospheresLog.Say("cloud viewer: the camera's aim for the " + PoseName + " pose: " + tuner.Note);
                }
                return;
            }
            FlightCamera fc = FlightCamera.fetch;
            if (fc == null || fc.mainCamera == null) return;
            double az, el, turnHeading, turnPitch;
            world.CameraAim(out az, out el);
            if (!tuner.Frame(az, el, out turnHeading, out turnPitch)) return;
            fc.camHdg += (float)(turnHeading * Math.PI / 180.0);
            fc.camPitch += (float)(turnPitch * Math.PI / 180.0);
        }

        // Turns the camera by hand, as a player drags it: the compass heading and elevation of the view (no tuning: the camera is left where the turn put it).
        internal void TurnCamera(double headingDeg, double elevationDeg)
        {
            FlightCamera fc = FlightCamera.fetch;
            if (fc == null) return;
            tuner = null;
            fc.camHdg = ViewerAim.HeadingRad(headingDeg);
            fc.camPitch = ViewerAim.PitchRad(elevationDeg);
        }

        // The time of day: an offset from the natural time, in seconds.
        internal void SetTimeOffset(double seconds)
        {
            if (clock == null) return;
            clock.OffsetS = seconds;
        }

        internal void FreezeTime(bool on)
        {
            // The window's toggle reports its value again when the window is built anew: an unchanged state is not a new freeze.
            if (clock == null || clock.Frozen == on) return;
            clock.Freeze(on);
            AtmospheresLog.Say("cloud viewer: time " + (on ? "frozen at UT " + clock.FrozenUt.ToString("0", CultureInfo.InvariantCulture) : "running"));
        }

        internal bool CloudsFrozen { get { return frozenClouds; } }

        // Holds the clouds still (cloud.motionScale 0) or lets them move at the speed they had.
        internal void FreezeClouds(bool on)
        {
            if (on == frozenClouds) return;
            if (on)
            {
                speedMemory.Seen(CloudSpeed);
                SetKey(CloudMotion.TuneScale, 0f);
            }
            else SetKey(CloudMotion.TuneScale, speedMemory.Last);
            frozenClouds = on;
        }

        internal float CloudSpeed
        {
            get { return CloudMotion.Clamp(store.Effective(CloudMotion.TuneScale, CloudMotion.DefaultScale)); }
        }

        internal void SetCloudSpeed(float scale)
        {
            frozenClouds = false;
            float speed = CloudMotion.Clamp(scale);
            speedMemory.Seen(speed);
            SetKey(CloudMotion.TuneScale, speed);
        }

        internal void SetWeather(ViewerWeather weather)
        {
            Weather = weather;
            EnsureKeys(true);
            foreach (KeyValuePair<string, float> pair in WeatherPresets.Overrides(weather, Layers()))
            {
                if (float.IsNaN(pair.Value)) ResetKey(pair.Key);
                else SetKey(pair.Key, pair.Value);
            }
            float combine = WeatherPresets.CombineOverride(weather);
            if (float.IsNaN(combine)) ResetKey(CloudLookTuning.TuneCoverCombine);
            else SetKey(CloudLookTuning.TuneCoverCombine, combine);
            double level;
            ForceType type;
            WeatherPresets.Precipitation(weather, out level, out type);
            store.SetSetting(AtmospheresSettings.Keys.ForceLevel, level >= 0.0 ? level : settingsAtOpen[AtmospheresSettings.Keys.ForceLevel]);
            store.SetSetting(AtmospheresSettings.Keys.ForceType, level >= 0.0 ? (double)(int)type : settingsAtOpen[AtmospheresSettings.Keys.ForceType]);
            CloudBridgeLights.RequestHistoryReset("cloud viewer weather");
            AtmospheresLog.Say("cloud viewer: weather " + WeatherPresets.Title(weather));
        }

        internal void SetBaseline(bool on)
        {
            Baseline = on;
            world.SetLayerMask(on ? T09Probe.NoLayersMask : 0);
        }

        // --- Options ---

        internal void SetKey(string key, float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) return;
            Note(key);
            store.SetOverride(key, value);
            CloudKey entry = CloudKeyRegistry.Find(key);
            if (entry != null && entry.Liveness == KeyLiveness.FrameHistory) CloudBridgeLights.RequestHistoryReset("cloud viewer " + key);
        }

        // The key back to its default: its override removed (a value of the tuning files stands again).
        internal void ResetKey(string key)
        {
            Note(key);
            store.ClearOverride(key);
            CloudKey entry = CloudKeyRegistry.Find(key);
            if (entry != null && entry.Liveness == KeyLiveness.FrameHistory) CloudBridgeLights.RequestHistoryReset("cloud viewer " + key);
        }

        private void Note(string key)
        {
            if (touched.Contains(key)) return;
            float value;
            if (!overridesAtOpen.ContainsKey(key) && store.TryOverride(key, out value)) overridesAtOpen[key] = value;
            touched.Add(key);
        }

        internal void SetSetting(string key, double value)
        {
            store.SetSetting(key, value);
        }

        // Watches a key's reads (Tuning.Instance.Watch): the window shows how many frames ago it was last read.
        internal void WatchKey(string key)
        {
            WatchedKey = key;
            Tuning.Instance.Watch(key);
        }

        internal string WatchText()
        {
            if (WatchedKey == null) return "Read check: pick a key's Check.";
            int last = Tuning.Instance.LastWatchedRead;
            if (last == int.MinValue) return "Read check: " + WatchedKey + " not read since it was picked";
            int ago = Tuning.Instance.Frame - last;
            return "Read check: " + WatchedKey + (ago <= 1 ? " read this frame" : " last read " + ago + " frames ago");
        }

        internal void StoreA()
        {
            EnsureKeys(true);
            flip.StoreA(OptionSet.Capture(store, settingKeys, keyNames, Weather));
            AtmospheresLog.Say("cloud viewer: options stored as A");
        }

        internal void FlipAB()
        {
            EnsureKeys(true);
            foreach (string k in keyNames) Note(k);
            ViewerWeather weather = Weather;
            if (!flip.Flip(store, settingKeys, keyNames, ref weather)) return;
            Weather = weather;
            CloudBridgeLights.RequestHistoryReset("cloud viewer A/B");
            AtmospheresLog.Say("cloud viewer: showing " + flip.Showing);
            if (Changed != null) Changed();
        }

        internal static string PresetPath(string name)
        {
            return ModFolder.PluginFile(AtmospheresSettings.Mod, Path.Combine(ViewerPresets.Folder, name + ".cfg"));
        }

        internal bool SavePreset(string typed)
        {
            string name = ViewerPresets.SafeName(typed);
            if (name.Length == 0) return false;
            EnsureKeys(true);
            OptionSet set = OptionSet.Capture(store, settingKeys, keyNames, Weather);
            bool ok = ModSettingsFile.Write(PresetPath(name), root =>
            {
                ConfigNode node = set.ToNode();
                foreach (ConfigNode child in node.nodes) root.AddNode(child);
            });
            AtmospheresLog.Say("cloud viewer: preset " + name + (ok ? " saved" : " could not be saved"));
            return ok;
        }

        internal bool LoadPreset(string name)
        {
            string path = PresetPath(ViewerPresets.SafeName(name));
            OptionSet set = ModSettingsFile.Read<OptionSet>(path, OptionSet.FromNode, null);
            if (set == null) return false;
            EnsureKeys(true);
            foreach (string k in keyNames) Note(k);
            set.ApplyTo(store, keyNames);
            Weather = set.Weather;
            CloudBridgeLights.RequestHistoryReset("cloud viewer preset");
            AtmospheresLog.Say("cloud viewer: preset " + name + " loaded (" + set.Settings.Count + " settings, " + set.Overrides.Count + " overrides)");
            if (Changed != null) Changed();
            return true;
        }

        internal static List<string> PresetNames()
        {
            var names = new List<string>();
            try
            {
                string folder = Path.GetDirectoryName(PresetPath("x"));
                if (!Directory.Exists(folder)) return names;
                foreach (string file in Directory.GetFiles(folder, "*.cfg")) names.Add(Path.GetFileNameWithoutExtension(file));
                names.Sort(StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception) { /* no folder: no presets */ }
            return names;
        }

        // --- Capture ---

        // The window is hidden for CaptureHideFrames frames and through the frame the picture is asked for in, so that the picture shows the scene alone; the picture and
        // its sidecar are then written.
        internal void RequestCapture()
        {
            if (!capture.Request()) return;
            // The game's own interface (clock, navball, readouts) is part of the screen and changes from one picture to the next: it is hidden as well, and shown again once the
            // picture is asked for (an interface the player had hidden himself stays hidden).
            KSP.UI.UIMasterController ui = KSP.UI.UIMasterController.Instance;
            if (ui != null && ui.IsUIShowing)
            {
                ui.HideUI();
                hudHidden = true;
            }
            pendingCapture = ViewerPresets.UniqueName(DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture), name => File.Exists(CapturePath(name)));
        }

        internal static string CapturePath(string name)
        {
            return ModFolder.PluginFile(AtmospheresSettings.Mod, Path.Combine(ViewerPresets.Folder, Path.Combine("Captures", name + ".png")));
        }

        // The window is to be hidden.
        internal bool Capturing { get { return capture.Busy; } }

        private bool hudHidden;

        private void ShowHud()
        {
            if (!hudHidden) return;
            hudHidden = false;
            if (KSP.UI.UIMasterController.Instance != null) KSP.UI.UIMasterController.Instance.ShowUI();
        }

        private void TakeCapture()
        {
            try
            {
                CaptureFrame = Time.frameCount;
                string png = CapturePath(pendingCapture);
                Directory.CreateDirectory(Path.GetDirectoryName(png));
                ScreenCapture.CaptureScreenshot(png);
                File.WriteAllText(Path.ChangeExtension(png, ".txt"), ViewerSidecar.Format(Record(Path.GetFileName(png)), AtmospheresSettings.Definitions, AtmospheresSettings.Number,
                                                                                           AtmospheresSettings.Text, keys, store), new UTF8Encoding(false));
                LastCapture = Path.GetFileName(png);
                AtmospheresLog.Say("cloud viewer: picture " + png + " with its sidecar");
            }
            catch (Exception e)
            {
                AtmospheresLog.Failed("cloud viewer", "the picture could not be written", e);
            }
        }

        internal CaptureRecord Record(string picture)
        {
            Vessel v = GameGlobals.ActiveVessel;
            var r = new CaptureRecord
            {
                Picture = picture, Taken = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), Body = v != null ? v.mainBody.name : "", Pose = PoseName,
                Weather = WeatherPresets.Title(Weather), Ut = Planetarium.GetUniversalTime(), TimeFrozen = clock != null && clock.Frozen, Baseline = Baseline, CloudSpeed = CloudSpeed,
                Side = flip.Showing, GeneratorSeed = AtmospheresSettings.GenSeed
            };
            try
            {
                T09Pose pose = world.ReadPose();
                r.AltitudeAslM = pose.CameraAsl;
                r.AltitudeAglM = pose.CameraAgl;
                r.CameraAzimuthDeg = pose.AzimuthDeg;
                r.CameraElevationDeg = pose.ElevationDeg;
                r.SunElevationDeg = world.SunElevationDeg();
                double lat, lon, alt;
                v.mainBody.GetLatLonAlt(FlightCamera.fetch.mainCamera.transform.position, out lat, out lon, out alt);
                r.LatDeg = lat;
                r.LonDeg = lon;
            }
            catch (Exception) { /* the pose stays 0 where the camera is not there */ }
            return r;
        }

        // --- Status ---

        internal string PoseText()
        {
            try
            {
                T09Pose p = world.ReadPose();
                return "Camera " + Km(p.CameraAgl) + " above ground, " + Km(p.CameraAsl) + " above sea, aim " + p.AzimuthDeg.ToString("0") + " / " + p.ElevationDeg.ToString("0") + " deg";
            }
            catch (Exception)
            {
                return "Camera: no pose";
            }
        }

        internal string HoldText()
        {
            if (PlacementNote != null) return PlacementNote;
            return placed ? "Vessel hold: " + world.HoldStatus : "Vessel where it was (no pose placed)";
        }

        internal string TimeText()
        {
            if (clock == null) return " ";
            double sun = 0.0;
            try { sun = world.SunElevationDeg(); } catch (Exception) { /* no sun */ }
            return "UT " + clock.Shown.ToString("0") + (clock.Frozen ? " (frozen)" : "") + ", offset " + (clock.OffsetS / 3600.0).ToString("+0.00;-0.00") + " h, sun "
                   + sun.ToString("0.0") + " deg";
        }

        private float nextWind;
        private string windText = "Wind: -";

        // The wind at each layer's middle height at the camera, and the clouds' speed: what the pass's drift is made of (OwnCloudModule.UpdateMotion). Read once a second.
        internal string WindText()
        {
            if (Time.unscaledTime < nextWind) return windText;
            nextWind = Time.unscaledTime + 1f;
            try
            {
                OwnCloudModule m = OwnCloudModule.Current;
                LayerStackSource stack = m == null ? null : m.Stack as LayerStackSource;
                Vessel v = GameGlobals.ActiveVessel;
                if (stack == null || v == null || FlightCamera.fetch == null)
                {
                    windText = "Wind: no own cloud layers (" + (m != null ? m.Stats.Why : "pass not running") + ")";
                    return windText;
                }
                CelestialBody b = v.mainBody;
                Vector3d at = FlightCamera.fetch.mainCamera.transform.position;
                double lat = b.GetLatitude(at), lon = b.GetLongitude(at), ut = Planetarium.GetUniversalTime();
                float scale = CloudSpeed;
                var text = new StringBuilder("Wind at the layers ");
                for (int l = 0; l < stack.LayerCount; l++)
                {
                    WeatherSample s;
                    if (l > 0) text.Append(" / ");
                    if (!WeatherHub.Sample(b.flightGlobalsIndex, lat, lon, CloudBridgeStack.MidHeightM(stack.LayerAt(l)), ut, out s)) { text.Append('-'); continue; }
                    double speed = Math.Sqrt(s.WindWorld.x * s.WindWorld.x + s.WindWorld.y * s.WindWorld.y);
                    text.Append((speed * scale).ToString("0"));
                }
                text.Append(" m/s (clouds x").Append(scale.ToString("0.##")).Append(frozenClouds ? ", frozen)" : ")");
                windText = text.ToString();
            }
            catch (Exception)
            {
                windText = "Wind: not readable";
            }
            return windText;
        }

        private static string Km(double m)
        {
            return Math.Abs(m) >= 10000.0 ? (m / 1000.0).ToString("0.0") + " km" : m.ToString("0") + " m";
        }
    }
}
