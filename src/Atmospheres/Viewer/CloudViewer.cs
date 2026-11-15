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
    internal sealed class CloudViewer
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
        private readonly Dictionary<string, double> settingsAtOpen = new Dictionary<string, double>(StringComparer.Ordinal);
        private readonly Dictionary<string, float> overridesAtOpen = new Dictionary<string, float>(StringComparer.Ordinal);
        private readonly HashSet<string> touched = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<string> settingKeys = new List<string>();
        private List<CloudKey> keys = new List<CloudKey>();
        private readonly List<string> keyNames = new List<string>();
        private ViewerClock clock;
        private bool ignoreTemperature, noCrashDamage, placed, frozenClouds;
        private double startLat, startLon, startAlt;
        private float cameraHeading, cameraPitch, cameraDistance;
        private FlightCamera.Modes cameraMode;
        private int aimIn = -1, restoreIn = -1;
        private double aimElevation = double.NaN, aimHeading;
        private bool aimKeep;
        private float keepHeading, keepPitch;
        private double pendingAgl = double.NaN;
        private float pendingSince;
        private string pendingCapture;
        private float speedBeforeFreeze = 1f;

        private CloudViewer()
        {
        }

        internal bool Active { get; private set; }
        internal bool KeepOptions { get; set; }
        internal ViewerWeather Weather { get; private set; }
        internal string PoseName { get; private set; } = "";
        internal bool Baseline { get; private set; }
        internal double SiteLat { get; private set; }
        internal double SiteLon { get; private set; }
        internal double TargetAglM { get; private set; } = double.NaN;
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
            TargetAglM = double.NaN;
            settingKeys.Clear();
            settingsAtOpen.Clear();
            foreach (SettingDef def in AtmospheresSettings.Definitions)
                if (def.Category == AtmospheresSettings.Categories.Clouds && def.Kind != SettingKind.Text) settingKeys.Add(def.Key);
            foreach (string key in settingKeys) settingsAtOpen[key] = store.Setting(key);
            settingsAtOpen[AtmospheresSettings.Keys.ForceLevel] = store.Setting(AtmospheresSettings.Keys.ForceLevel);
            settingsAtOpen[AtmospheresSettings.Keys.ForceType] = store.Setting(AtmospheresSettings.Keys.ForceType);
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
        internal void RefreshKeys()
        {
            keys = CloudKeyRegistry.Expand(LayerNames());
            keyNames.Clear();
            foreach (CloudKey k in keys)
            {
                keyNames.Add(k.Key);
                float value;
                if (!touched.Contains(k.Key) && !overridesAtOpen.ContainsKey(k.Key) && store.TryOverride(k.Key, out value)) overridesAtOpen[k.Key] = value;
            }
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
                }
                Tuning.Instance.Watch(null);
                WatchedKey = null;
                world.SetLayerMask(0);
                CloudBridgeLights.RequestHistoryReset("cloud viewer closed");
                // The vessel back at its start: held there for a moment, or set there at once where the scene is about to be saved and left.
                if (placed && GameGlobals.ActiveVessel != null) world.PlaceVessel(startLat, startLon, startAlt);
                if (placed && !sceneEnding && GameGlobals.ActiveVessel != null)
                {
                    world.StartHold();
                    restoreIn = RestoreHoldFrames;
                }
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
            if (!Active) return;
            if (!HighLogic.LoadedSceneIsFlight || GameGlobals.ActiveVessel == null)
            {
                Close(true);
                return;
            }
            double observed = Planetarium.GetUniversalTime();
            double next = clock.Step(observed);
            if (next != observed) Planetarium.SetUniversalTime(next);
            if (!double.IsNaN(pendingAgl) && Time.unscaledTime - pendingSince >= AltitudeSettleS)
            {
                double agl = pendingAgl;
                pendingAgl = double.NaN;
                PlaceAt(agl, true);
            }
            if (aimIn >= 0 && --aimIn < 0) Aim();
            if (capture.Step()) TakeCapture();
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
            pendingAgl = aglM;
            pendingSince = Time.unscaledTime;
            TargetAglM = aglM;
        }

        private void PlaceAt(double aglM, bool keepAim)
        {
            Vessel v = GameGlobals.ActiveVessel;
            if (v == null) return;
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
            TargetAglM = aglM;
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
                fc.camHdg = keepHeading;
                fc.camPitch = keepPitch;
                return;
            }
            world.SetCameraDistance(cameraDistanceWanted);
            double el = aimElevation;
            if (double.IsNaN(el)) el = -T09Probe.HorizonDipDeg(world.BodyRadius, double.IsNaN(TargetAglM) ? 0.0 : TargetAglM);
            // The convention of the K-T2 ground views: the camera stands behind the vessel, heading + 180 degrees, pitch minus the elevation.
            fc.camHdg = (float)((aimHeading + 180.0) * Math.PI / 180.0);
            fc.camPitch = (float)(-el * Math.PI / 180.0);
        }

        // The time of day: an offset from the natural time, in seconds.
        internal void SetTimeOffset(double seconds)
        {
            if (clock == null) return;
            clock.OffsetS = seconds;
        }

        internal void FreezeTime(bool on)
        {
            if (clock == null) return;
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
                speedBeforeFreeze = CloudSpeed;
                SetKey(CloudMotion.TuneScale, 0f);
            }
            else SetKey(CloudMotion.TuneScale, speedBeforeFreeze);
            frozenClouds = on;
        }

        internal float CloudSpeed
        {
            get { return CloudMotion.Clamp(store.Effective(CloudMotion.TuneScale, CloudMotion.DefaultScale)); }
        }

        internal void SetCloudSpeed(float scale)
        {
            frozenClouds = false;
            SetKey(CloudMotion.TuneScale, CloudMotion.Clamp(scale));
        }

        internal void SetWeather(ViewerWeather weather)
        {
            Weather = weather;
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
            flip.StoreA(OptionSet.Capture(store, settingKeys, keyNames));
            AtmospheresLog.Say("cloud viewer: options stored as A");
        }

        internal void FlipAB()
        {
            foreach (string k in keyNames) Note(k);
            if (!flip.Flip(store, settingKeys, keyNames)) return;
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
            OptionSet set = OptionSet.Capture(store, settingKeys, keyNames);
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
            foreach (string k in keyNames) Note(k);
            set.ApplyTo(store, keyNames);
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
            pendingCapture = ViewerPresets.UniqueName(DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture), name => File.Exists(CapturePath(name)));
        }

        internal static string CapturePath(string name)
        {
            return ModFolder.PluginFile(AtmospheresSettings.Mod, Path.Combine(ViewerPresets.Folder, Path.Combine("Captures", name + ".png")));
        }

        // The window is to be hidden.
        internal bool Capturing { get { return capture.Busy; } }

        private void TakeCapture()
        {
            try
            {
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
