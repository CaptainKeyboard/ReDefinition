using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using FidelityFX.FSR3;
using ReDefinition.Bridges;
using ReDefinition.Core;
using ReDefinition.Raytracing;
using ReDefinition.Settings;
using ReDefinition.Upscaler;
using UnityEngine;

namespace ReDefinition
{
    // What the player chose, kept across sessions.
    //
    // Not kept here: the state taken from other mods for the upscaler
    // (HostStack). It is captured fresh each time it is taken, so a restore
    // returns to what the player chose in TUFX or Scatterer in the meantime.
    internal class OwnSettings
    {
        public bool Enabled;
        public Fsr3Upscaler.QualityMode Quality = Fsr3Upscaler.QualityMode.NativeAA;
        public float Sharpness = 1.0f;
        public bool AutoExposure = true;
        public bool MipmapBias = true;
        public bool CompensateLodBias = true;
        public bool DisableMsaa = true;
        public bool ForceAnisotropic = true;
        public bool Jitter = true;
        public bool SkinnedMotionVectors = true;
        public bool TufxAfterUpscaling = true;
        public bool TransparencyMask;
        public UpscalerMasks.ReactiveSource ReactiveMask = UpscalerMasks.ReactiveSource.Off;
        public bool FrameGeneration;
        // How many frames frame generation shows per rendered frame, 2x to 6x, or Dynamic where
        // DLSS-G chooses (FrameGenerationChoice). Held while frame generation is off; never more
        // than the technique that runs allows.
        public string FrameGenerationMultiplier = FrameGenerationChoice.Default;
        // How much of the lighting is raytraced, and how often its light bounces.
        public RaytracingMode Raytracing = RaytracingMode.Off;
        public int RaytracingBounces = 2;
        // DLSS Ray Reconstruction denoises and upscales the raytraced frame where it runs;
        // off, the raytracing's own denoiser and DLSS Super Resolution do
        // (Upscaler/UpscalerRig.Reconstruction).
        public bool RayReconstruction = true;
        // The least raytracing mode it runs in: below it the raytracing's own denoiser and DLSS
        // Super Resolution take the frame. Ray Reconstruction takes about 9 ms of GPU time on an
        // RTX A4500 in every mode, against the own denoiser's 0.7 ms (Shadows), 2.7 (Light) and
        // 3.7 (Full) plus Super Resolution's 1.5. Shadows: every mode, as before.
        public RaytracingMode RayReconstructionFrom = RaytracingMode.Shadows;
        // The technique, the mode and Ray Reconstruction follow the graphics card and the
        // screen (AutoSettings): Backend, Quality, RayReconstruction and RayReconstructionFrom
        // hold what Auto resolved to, and are the player's own value once the flag is off.
        public bool BackendAuto = true;
        public bool QualityAuto = true;
        public bool RayReconstructionAuto = true;
        // The render height, at the display's height of 1440, the mode Auto aims for; a profile
        // sets it (Upscaler/AutoSettings.Mode).
        public int RenderTargetHeight = 1440;
        // Auto: the latency brake and the card's own technique; Off: neither (LowLatencySetting).
        public string LowLatency = LowLatencySetting.Auto;
        // How bright the moons' and planets' light is, in percent of the raytracing's
        // artistic scale (Raytracing/Ksp/RtReflectors).
        public float RaytracingMoonlight = 10f;
        // The moons' discs made to stand out in the night sky (Raytracing/Ksp/MoonGlow).
        public bool MoonGlow = true;
        // How strongly water mirrors the moons, in percent of their discs as the eye sees
        // them at night (NightAdaptation).
        public float RaytracingMoonReflection = 100f;
        // How strongly water mirrors the sun, in percent of SunReflectionAt100 times the
        // sun's own brightness and Scatterer's configured glint (RtKsp, SeaHighlights).
        public float SunReflection = 100f;
        // What 100 % of SunReflection stands for: three times the sun's own brightness and
        // Scatterer's glint, the player's choice as the standard (2026-10-13, then 300 %).
        public const float SunReflectionAt100 = 3f;
        // How much of KSP's night light stays under raytracing, in percent: the least
        // light a point gets where neither the sun nor a moon reaches it.
        public float RaytracingNightAmbient = 0f;
        // The colour of the raytraced light from the sky and the surroundings
        // (Raytracing/Ksp/RtKsp, RaytracingLightColour).
        public RaytracingLightColour RaytracingLightColour = RaytracingLightColour.Neutral;

        // Whether the settings window holds the flight while it is open.
        public bool PauseWhileOpen;

        // The diagnostic measurements -- audits, read-backs and per-frame checks of the
        // upscaler's inputs, with their log lines -- run only with this on (the diagnostics window, Capture & log).
        public bool DebugMode;

        // Whether KSP's own Settings buttons open ReDefinition's window instead
        // of KSP's screens (PauseMenuEntry, MainMenuEntry).
        public bool ReplaceKspSettings;
        public UpscalerBackend Backend = UpscalerBackend.Fsr3;
        public DlssModel DlssModel = DlssModel.Default;

        // The hotkeys, as KeyCombination writes them. Unbound until the player
        // sets one in the Keys tab: KSP's own bindings fire on their key whatever
        // modifiers are held, and KSP binds nearly every letter, digit and
        // function key, so a default would set off one of KSP's as well.
        public string UpscalerKey = KeyCombination.NoneText;
        public string DiagnosticsKey = KeyCombination.NoneText;
        public string CameraListKey = KeyCombination.NoneText;
        public string SettingsWindowKey = KeyCombination.NoneText;

        // How far the navball stands from the bottom centre, in the flight UI's
        // units: negative to the left (NavballDrag); and the screen, width by
        // height, it was put there on. On another screen it stands in the centre.
        public float NavballOffset;
        public string NavballScreen = "";
        // The options pinned to the strip at the diagnostics window's foot, their ids
        // separated by commas (DebugPins).
        public string DiagnosticsPins = "";
        // The diagnostics window's last page and where it stands.
        public ReDefinition.Window.DebugCategory DiagnosticsPage = ReDefinition.Window.DebugCategory.Overview;
        public float DiagnosticsX = 60f;
        public float DiagnosticsY = 60f;
        // Other mods' IMGUI windows at KSP's UI scale (Scaling/ModWindowScale).
        public bool ScaleModWindows = true;
        // Better Time Warp Continued's own toolbar button: hidden while off, since
        // its button stands in the time panel (Flight/TimeWarpButton).
        public bool TimeWarpToolbarButton;
        // The flight scene starts in KSP's maneuver mode (Flight/FlightStartMode).
        public bool FlightStartsInManeuverMode = true;
        // The altimeter switches between sea level and the ground itself (Flight/AutoAltimeter).
        public bool AutoAltimeter = true;
        // The flight panel switches to the profile of the situation (Flight/Panel/FlightPanel).
        public bool FlightPanelAuto = true;
        // The buttons standing over the space centre's buildings in flight and over
        // the launch site in the editors (Flight/WorldButton).
        public bool WorldButtons = true;
        // Fireworks on Kerbin's night side in the main menu (Menu/MenuFireworks).
        public bool MenuFireworks = true;
        // From the main menu to the space centre as one camera flight (Menu/KscArrival).
        public bool MenuKscZoom = true;
        // Parallax's grass on the space centre's lawns (Scenery/KscGrass).
        public bool KscGrass = true;
        // The round gauge in place of the vertical speed's (Flight/RoundGauge).
        public bool RoundGauge = true;
        // The vertical and horizontal speed at the navball (Flight/NavballSpeeds).
        public bool NavballSpeeds = true;
        // The navball's marker for a docking port as the target (Flight/NavballDockingMarker).
        public bool NavballDockingMarker = true;
        // The navball moved by dragging (Flight/NavballDrag).
        public bool NavballDrag = true;
        // The flight panel's profiles in the maneuver mode (Flight/Panel/FlightPanel).
        public bool FlightPanel = true;
        // The stage details in two columns (Flight/StageInfoColumns).
        public bool StageDetailColumns = true;
        // The stage details' TWR at the stage's start and end (Flight/StageInfoColumns).
        public bool StageTwrRange = true;
        // KSP's delta-v tools as part of the staging stack (Flight/DeltaVTools).
        public bool DeltaVTools = true;
        // Tooltips on the HUD's buttons (Flight/HudTooltips).
        public bool HudTooltips = true;
        // The editors' staging reset asks first (Flight/HudTooltips).
        public bool StagingResetAsks = true;
        // The crew slot buttons in the staging stack's look (Flight/CrewSlotButtons).
        public bool CrewSlotButtons = true;
        // Better Time Warp's button in the time panel (Flight/TimeWarpBar).
        public bool TimeWarpPanelButton = true;

        private const string RootName = "ReDefinition";

        public static string Path
        {
            get { return PluginData.Path("settings.cfg"); }
        }

        // Every setting once: its key in the file, how it is read from and written to a
        // settings object, its text, and whether the add-on reads it live from its settings
        // object (CopyLive, ChangeLive) rather than keeping it in a field of its own.
        private abstract class Field
        {
            public string Key;
            public bool Live;
            public abstract string Text(OwnSettings settings);
            // From the file's text; a text that is no such value leaves the default.
            public abstract void Read(OwnSettings settings, string text);
            public abstract void Copy(OwnSettings to, OwnSettings from);
            public abstract bool Same(OwnSettings a, OwnSettings b);
        }

        private delegate bool Parser<T>(string text, out T value);

        private sealed class Field<T> : Field
        {
            public Func<OwnSettings, T> Get;
            public Action<OwnSettings, T> Set;
            public Func<T, string> Format;
            public Parser<T> Parse;

            public override string Text(OwnSettings settings) { return Format(Get(settings)); }

            public override void Read(OwnSettings settings, string text)
            {
                T value;
                if (Parse(text, out value)) Set(settings, value);
            }

            public override void Copy(OwnSettings to, OwnSettings from) { Set(to, Get(from)); }

            public override bool Same(OwnSettings a, OwnSettings b)
            {
                return EqualityComparer<T>.Default.Equals(Get(a), Get(b));
            }
        }

        private static Field<bool> Bool(string key, Func<OwnSettings, bool> get, Action<OwnSettings, bool> set, bool live = false)
        {
            return new Field<bool> { Key = key, Get = get, Set = set, Live = live, Format = v => v.ToString(), Parse = bool.TryParse };
        }

        private static Field<float> Float(string key, string format, Func<OwnSettings, float> get, Action<OwnSettings, float> set,
                                          bool live = false)
        {
            return new Field<float>
            {
                Key = key, Get = get, Set = set, Live = live,
                Format = v => v.ToString(format, CultureInfo.InvariantCulture),
                Parse = ParseFloat,
            };
        }

        // A finite number: NaN and the infinities parse, but no setting is one, and a
        // window position of NaN is a window nobody sees.
        internal static bool ParseFloat(string text, out float value)
        {
            return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                && !float.IsNaN(value) && !float.IsInfinity(value);
        }

        // A name or number of a value the enum has: Enum.Parse takes any number, 99 included.
        internal static bool ParseEnum<T>(string text, out T value) where T : struct
        {
            value = default(T);
            // Not a list: Enum.Parse adds up "Overview, Capture" to one value of its own.
            if (string.IsNullOrEmpty(text) || text.IndexOf(',') >= 0) return false;
            try
            {
                object parsed = System.Enum.Parse(typeof(T), text);
                if (!System.Enum.IsDefined(typeof(T), parsed)) return false;
                value = (T)parsed;
                return true;
            }
            catch { return false; }
        }

        private static Field<T> Enum<T>(string key, Func<OwnSettings, T> get, Action<OwnSettings, T> set, bool live = false)
            where T : struct
        {
            return new Field<T>
            {
                Key = key, Get = get, Set = set, Live = live, Format = v => v.ToString(),
                Parse = ParseEnum,
            };
        }

        // A key combination the file holds; what is no binding leaves the default.
        private static Field<string> Binding(string key, Func<OwnSettings, string> get, Action<OwnSettings, string> set)
        {
            return new Field<string>
            {
                Key = key, Get = get, Set = set, Live = true, Format = v => v,
                Parse = (string text, out string v) =>
                {
                    v = text != null && KeyCombination.IsText(text) ? KeyCombination.Parse(text).ToString() : null;
                    return v != null;
                },
            };
        }

        private static readonly Field[] Fields =
        {
            Bool("enabled", x => x.Enabled, (x, v) => x.Enabled = v),
            Enum("quality", x => x.Quality, (x, v) => x.Quality = v),
            Float("sharpness", "0.00", x => x.Sharpness, (x, v) => x.Sharpness = v),
            Bool("autoExposure", x => x.AutoExposure, (x, v) => x.AutoExposure = v),
            Bool("mipmapBias", x => x.MipmapBias, (x, v) => x.MipmapBias = v),
            Bool("lodBias", x => x.CompensateLodBias, (x, v) => x.CompensateLodBias = v),
            Bool("disableMsaa", x => x.DisableMsaa, (x, v) => x.DisableMsaa = v),
            Bool("pauseWhileOpen", x => x.PauseWhileOpen, (x, v) => x.PauseWhileOpen = v),
            Bool("debug", x => x.DebugMode, (x, v) => x.DebugMode = v),
            Bool("replaceKspSettings", x => x.ReplaceKspSettings, (x, v) => x.ReplaceKspSettings = v),
            Bool("forceAnisotropic", x => x.ForceAnisotropic, (x, v) => x.ForceAnisotropic = v),
            Bool("jitter", x => x.Jitter, (x, v) => x.Jitter = v),
            Bool("skinnedMotionVectors", x => x.SkinnedMotionVectors, (x, v) => x.SkinnedMotionVectors = v),
            Bool("tufxAfterUpscaling", x => x.TufxAfterUpscaling, (x, v) => x.TufxAfterUpscaling = v),
            Bool("transparencyMask", x => x.TransparencyMask, (x, v) => x.TransparencyMask = v),
            Enum("reactiveMask", x => x.ReactiveMask, (x, v) => x.ReactiveMask = v),
            Bool("frameGeneration", x => x.FrameGeneration, (x, v) => x.FrameGeneration = v),
            new Field<string>
            {
                Key = "frameGenerationMultiplier", Get = x => x.FrameGenerationMultiplier,
                Set = (x, v) => x.FrameGenerationMultiplier = v, Format = v => v,
                Parse = (string text, out string v) =>
                {
                    v = FrameGenerationChoice.Valid(text) ? text : null;
                    return v != null;
                },
            },
            Enum("technique", x => x.Backend, (x, v) => x.Backend = v),
            Enum("dlssModel", x => x.DlssModel, (x, v) => x.DlssModel = v),
            Binding("upscalerKey", x => x.UpscalerKey, (x, v) => x.UpscalerKey = v),
            Binding("diagnosticsKey", x => x.DiagnosticsKey, (x, v) => x.DiagnosticsKey = v),
            Binding("cameraListKey", x => x.CameraListKey, (x, v) => x.CameraListKey = v),
            Binding("settingsWindowKey", x => x.SettingsWindowKey, (x, v) => x.SettingsWindowKey = v),
            // The navball's place is the add-on's own (ReDefinitionAddon.NavballOffset).
            Float("navballOffset", "0", x => x.NavballOffset, (x, v) => x.NavballOffset = v),
            new Field<string>
            {
                Key = "navballScreen", Get = x => x.NavballScreen, Set = (x, v) => x.NavballScreen = v, Format = v => v,
                Parse = (string text, out string v) => { v = text ?? ""; return true; },
            },
            new Field<string>
            {
                Key = "diagnosticsPins", Get = x => x.DiagnosticsPins, Set = (x, v) => x.DiagnosticsPins = v, Live = true,
                Format = v => v, Parse = (string text, out string v) => { v = text ?? ""; return true; },
            },
            Enum("diagnosticsPage", x => x.DiagnosticsPage, (x, v) => x.DiagnosticsPage = v, true),
            Float("diagnosticsX", "0", x => x.DiagnosticsX, (x, v) => x.DiagnosticsX = v, true),
            Float("diagnosticsY", "0", x => x.DiagnosticsY, (x, v) => x.DiagnosticsY = v, true),
            Bool("scaleModWindows", x => x.ScaleModWindows, (x, v) => x.ScaleModWindows = v, true),
            Bool("timeWarpToolbarButton", x => x.TimeWarpToolbarButton, (x, v) => x.TimeWarpToolbarButton = v, true),
            Bool("flightStartsInManeuverMode", x => x.FlightStartsInManeuverMode, (x, v) => x.FlightStartsInManeuverMode = v, true),
            Bool("autoAltimeter", x => x.AutoAltimeter, (x, v) => x.AutoAltimeter = v, true),
            Bool("worldButtons", x => x.WorldButtons, (x, v) => x.WorldButtons = v, true),
            Bool("flightPanelAuto", x => x.FlightPanelAuto, (x, v) => x.FlightPanelAuto = v, true),
            Bool("menuFireworks", x => x.MenuFireworks, (x, v) => x.MenuFireworks = v, true),
            Bool("menuKscZoom", x => x.MenuKscZoom, (x, v) => x.MenuKscZoom = v, true),
            Bool("kscGrass", x => x.KscGrass, (x, v) => x.KscGrass = v, true),
            Bool("roundGauge", x => x.RoundGauge, (x, v) => x.RoundGauge = v, true),
            Bool("navballSpeeds", x => x.NavballSpeeds, (x, v) => x.NavballSpeeds = v, true),
            Bool("navballDockingMarker", x => x.NavballDockingMarker, (x, v) => x.NavballDockingMarker = v, true),
            Bool("navballDrag", x => x.NavballDrag, (x, v) => x.NavballDrag = v, true),
            Bool("flightPanel", x => x.FlightPanel, (x, v) => x.FlightPanel = v, true),
            Bool("stageDetailColumns", x => x.StageDetailColumns, (x, v) => x.StageDetailColumns = v, true),
            Bool("stageTwrRange", x => x.StageTwrRange, (x, v) => x.StageTwrRange = v, true),
            Bool("deltaVTools", x => x.DeltaVTools, (x, v) => x.DeltaVTools = v, true),
            Bool("hudTooltips", x => x.HudTooltips, (x, v) => x.HudTooltips = v, true),
            Bool("stagingResetAsks", x => x.StagingResetAsks, (x, v) => x.StagingResetAsks = v, true),
            Bool("crewSlotButtons", x => x.CrewSlotButtons, (x, v) => x.CrewSlotButtons = v, true),
            Bool("timeWarpPanelButton", x => x.TimeWarpPanelButton, (x, v) => x.TimeWarpPanelButton = v, true),
            Bool("techniqueAuto", x => x.BackendAuto, (x, v) => x.BackendAuto = v, true),
            Bool("qualityAuto", x => x.QualityAuto, (x, v) => x.QualityAuto = v, true),
            Bool("rayReconstructionAuto", x => x.RayReconstructionAuto, (x, v) => x.RayReconstructionAuto = v, true),
            new Field<int>
            {
                Key = "renderTargetHeight", Get = x => x.RenderTargetHeight, Set = (x, v) => x.RenderTargetHeight = v,
                Live = true, Format = v => v.ToString(CultureInfo.InvariantCulture),
                Parse = (string text, out int v) => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out v)
                                                    && v >= 240 && v <= 4320,
            },
            new Field<string>
            {
                Key = "lowLatency", Get = x => x.LowLatency, Set = (x, v) => x.LowLatency = v, Live = true, Format = v => v,
                Parse = (string text, out string v) =>
                {
                    v = LowLatencySetting.Valid(text) ? text : null;
                    return v != null;
                },
            },
            Enum("raytracing", x => x.Raytracing, (x, v) => x.Raytracing = v),
            new Field<int>
            {
                Key = "raytracingBounces", Get = x => x.RaytracingBounces, Set = (x, v) => x.RaytracingBounces = v,
                Format = v => v.ToString(CultureInfo.InvariantCulture),
                Parse = (string text, out int v) =>
                {
                    float f;
                    bool ok = float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out f);
                    v = (int)f;
                    return ok;
                },
            },
            Bool("rayReconstruction", x => x.RayReconstruction, (x, v) => x.RayReconstruction = v, true),
            Enum("rayReconstructionFrom", x => x.RayReconstructionFrom, (x, v) => x.RayReconstructionFrom = v, true),
            Float("raytracingMoonlight", "0", x => x.RaytracingMoonlight, (x, v) => x.RaytracingMoonlight = v, true),
            Bool("moonGlow", x => x.MoonGlow, (x, v) => x.MoonGlow = v, true),
            Float("raytracingMoonReflection", "0", x => x.RaytracingMoonReflection, (x, v) => x.RaytracingMoonReflection = v, true),
            Float("sunReflection", "0", x => x.SunReflection, (x, v) => x.SunReflection = v, true),
            Float("raytracingNightAmbient", "0", x => x.RaytracingNightAmbient, (x, v) => x.RaytracingNightAmbient = v, true),
            Enum("raytracingLightColour", x => x.RaytracingLightColour, (x, v) => x.RaytracingLightColour = v, true),
        };

        public static OwnSettings Load()
        {
            OwnSettings settings = new OwnSettings();
            if (!File.Exists(Path)) return settings;

            ConfigNode root = ConfigNode.Load(Path);
            ConfigNode node = root == null ? null : root.GetNode(RootName);
            if (node == null) return settings;

            foreach (Field field in Fields)
                field.Read(settings, node.GetValue(field.Key));
            return settings;
        }

        public void Save()
        {
            ConfigNode node = new ConfigNode(RootName);
            foreach (Field field in Fields)
                node.AddValue(field.Key, field.Text(this));

            string directory = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            ConfigNode root = new ConfigNode();
            root.AddNode(node);
            root.Save(Path);
        }

        // Change detection without a dirty flag at every call site: Update
        // compares this string a couple of times a second and writes only when it
        // differs.
        public string Snapshot()
        {
            StringBuilder sb = new StringBuilder();
            foreach (Field field in Fields)
                sb.Append(field.Text(this)).Append('|');
            return sb.ToString();
        }

        // The settings the add-on reads live from its settings object -- the hotkeys,
        // the flight interface's, the raytraced moonlight and reflections -- from another.
        public void CopyLive(OwnSettings from)
        {
            foreach (Field field in Fields)
                if (field.Live) field.Copy(this, from);
        }

        // Of those, only what a view changed between `before` and `after`: KSP's settings
        // dialog holds a copy from when it opened, and its Apply must not bring back a value
        // another view set meanwhile.
        public void ChangeLive(OwnSettings before, OwnSettings after)
        {
            foreach (Field field in Fields)
                if (field.Live && !field.Same(before, after)) field.Copy(this, after);
        }

        // A setting by its key in the file, from its text there; false for a key there
        // is none of, or a text that is no such value (the probe's ini shots, mod.<key>).
        public bool SetValue(string key, string text)
        {
            foreach (Field field in Fields)
            {
                if (field.Key != key) continue;
                string before = field.Text(this);
                field.Read(this, text);
                bool took = field.Text(this) != before || string.Equals(before, text, StringComparison.OrdinalIgnoreCase);
                // A value set by name is the player's, not Auto's.
                if (took && key == "technique") BackendAuto = false;
                if (took && key == "quality") QualityAuto = false;
                if (took && key == "rayReconstruction")
                {
                    RayReconstructionAuto = false;
                    // On, set by name, runs in every mode (as the row's On does).
                    if (RayReconstruction) RayReconstructionFrom = RaytracingMode.Shadows;
                }
                return took;
            }
            return false;
        }

        public string ValueOf(string key)
        {
            foreach (Field field in Fields)
            {
                if (field.Key == key) return field.Text(this);
            }
            return null;
        }

        // For KSP's settings dialog, which edits a copy and commits it only on
        // Apply or Accept -- Cancel has to leave the live values untouched.
        public OwnSettings Clone()
        {
            return (OwnSettings)MemberwiseClone();
        }

    }
}
