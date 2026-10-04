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
        // How much of the lighting is raytraced, and how often its light bounces.
        public RaytracingMode Raytracing = RaytracingMode.Off;
        public int RaytracingBounces = 2;
        // How bright the moons' and planets' light is, in percent of the raytracing's
        // artistic scale (Raytracing/Ksp/RtReflectors).
        public float RaytracingMoonlight = 10f;
        // How strongly water mirrors the moons, in percent of their discs as the eye sees
        // them at night (NightAdaptation).
        public float RaytracingMoonReflection = 100f;
        // How strongly Scatterer's ocean mirrors the sun, in percent of its configuration's
        // strength (SeaHighlights).
        public float SunReflection = 100f;
        // How much of KSP's night light stays under raytracing, in percent: the least
        // light a point gets where neither the sun nor a moon reaches it.
        public float RaytracingNightAmbient = 0f;

        // Whether the settings window holds the flight while it is open.
        public bool PauseWhileOpen;

        // Whether KSP's own Settings buttons open ReDefinition's window instead
        // of KSP's screens (PauseMenuEntry, MainMenuEntry).
        public bool ReplaceKspSettings;
        public UpscalerBackend Backend = UpscalerBackend.Fsr3;
        public DlssModel DlssModel = DlssModel.High;

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

        private const string RootName = "ReDefinition";

        public static string Path
        {
            get { return PluginData.Path("settings.cfg"); }
        }

        public static OwnSettings Load()
        {
            OwnSettings settings = new OwnSettings();
            if (!File.Exists(Path)) return settings;

            ConfigNode root = ConfigNode.Load(Path);
            ConfigNode node = root == null ? null : root.GetNode(RootName);
            if (node == null) return settings;

            settings.Enabled = Bool(node, "enabled", settings.Enabled);
            settings.Quality = Enum(node, "quality", settings.Quality);
            settings.Sharpness = Float(node, "sharpness", settings.Sharpness);
            settings.AutoExposure = Bool(node, "autoExposure", settings.AutoExposure);
            settings.MipmapBias = Bool(node, "mipmapBias", settings.MipmapBias);
            settings.CompensateLodBias = Bool(node, "lodBias", settings.CompensateLodBias);
            settings.DisableMsaa = Bool(node, "disableMsaa", settings.DisableMsaa);
            settings.PauseWhileOpen = Bool(node, "pauseWhileOpen", settings.PauseWhileOpen);
            settings.ReplaceKspSettings = Bool(node, "replaceKspSettings", settings.ReplaceKspSettings);
            settings.ForceAnisotropic = Bool(node, "forceAnisotropic", settings.ForceAnisotropic);
            settings.Jitter = Bool(node, "jitter", settings.Jitter);
            settings.SkinnedMotionVectors = Bool(node, "skinnedMotionVectors", settings.SkinnedMotionVectors);
            settings.TufxAfterUpscaling = Bool(node, "tufxAfterUpscaling", settings.TufxAfterUpscaling);
            settings.TransparencyMask = Bool(node, "transparencyMask", settings.TransparencyMask);
            settings.ReactiveMask = Enum(node, "reactiveMask", settings.ReactiveMask);
            settings.FrameGeneration = Bool(node, "frameGeneration", settings.FrameGeneration);
            settings.Raytracing = Enum(node, "raytracing", settings.Raytracing);
            settings.RaytracingBounces = (int)Float(node, "raytracingBounces", settings.RaytracingBounces);
            settings.RaytracingMoonlight = Float(node, "raytracingMoonlight", settings.RaytracingMoonlight);
            settings.RaytracingMoonReflection = Float(node, "raytracingMoonReflection", settings.RaytracingMoonReflection);
            settings.SunReflection = Float(node, "sunReflection", settings.SunReflection);
            settings.RaytracingNightAmbient = Float(node, "raytracingNightAmbient", settings.RaytracingNightAmbient);
            settings.Backend = Enum(node, "technique", settings.Backend);
            settings.DlssModel = Enum(node, "dlssModel", settings.DlssModel);
            settings.UpscalerKey = Binding(node, "upscalerKey", settings.UpscalerKey);
            settings.DiagnosticsKey = Binding(node, "diagnosticsKey", settings.DiagnosticsKey);
            settings.CameraListKey = Binding(node, "cameraListKey", settings.CameraListKey);
            settings.SettingsWindowKey = Binding(node, "settingsWindowKey", settings.SettingsWindowKey);
            settings.NavballOffset = Float(node, "navballOffset", settings.NavballOffset);
            settings.NavballScreen = node.GetValue("navballScreen") ?? "";
            settings.ScaleModWindows = Bool(node, "scaleModWindows", settings.ScaleModWindows);
            settings.TimeWarpToolbarButton = Bool(node, "timeWarpToolbarButton", settings.TimeWarpToolbarButton);
            settings.FlightStartsInManeuverMode = Bool(node, "flightStartsInManeuverMode", settings.FlightStartsInManeuverMode);
            settings.AutoAltimeter = Bool(node, "autoAltimeter", settings.AutoAltimeter);
            settings.WorldButtons = Bool(node, "worldButtons", settings.WorldButtons);
            settings.FlightPanelAuto = Bool(node, "flightPanelAuto", settings.FlightPanelAuto);
            settings.MenuFireworks = Bool(node, "menuFireworks", settings.MenuFireworks);
            return settings;
        }

        public void Save()
        {
            ConfigNode node = new ConfigNode(RootName);
            node.AddValue("enabled", Enabled);
            node.AddValue("quality", Quality);
            node.AddValue("sharpness", Sharpness.ToString("0.00",
                System.Globalization.CultureInfo.InvariantCulture));
            node.AddValue("autoExposure", AutoExposure);
            node.AddValue("mipmapBias", MipmapBias);
            node.AddValue("lodBias", CompensateLodBias);
            node.AddValue("disableMsaa", DisableMsaa);
            node.AddValue("pauseWhileOpen", PauseWhileOpen);
            node.AddValue("replaceKspSettings", ReplaceKspSettings);
            node.AddValue("forceAnisotropic", ForceAnisotropic);
            node.AddValue("jitter", Jitter);
            node.AddValue("skinnedMotionVectors", SkinnedMotionVectors);
            node.AddValue("tufxAfterUpscaling", TufxAfterUpscaling);
            node.AddValue("transparencyMask", TransparencyMask);
            node.AddValue("reactiveMask", ReactiveMask);
            node.AddValue("frameGeneration", FrameGeneration);
            node.AddValue("technique", Backend);
            node.AddValue("dlssModel", DlssModel);
            node.AddValue("upscalerKey", UpscalerKey);
            node.AddValue("diagnosticsKey", DiagnosticsKey);
            node.AddValue("cameraListKey", CameraListKey);
            node.AddValue("settingsWindowKey", SettingsWindowKey);
            node.AddValue("navballOffset", NavballOffset.ToString("0", System.Globalization.CultureInfo.InvariantCulture));
            node.AddValue("navballScreen", NavballScreen);
            node.AddValue("scaleModWindows", ScaleModWindows);
            node.AddValue("timeWarpToolbarButton", TimeWarpToolbarButton);
            node.AddValue("flightStartsInManeuverMode", FlightStartsInManeuverMode);
            node.AddValue("autoAltimeter", AutoAltimeter);
            node.AddValue("worldButtons", WorldButtons);
            node.AddValue("flightPanelAuto", FlightPanelAuto);
            node.AddValue("menuFireworks", MenuFireworks);
            node.AddValue("raytracing", Raytracing);
            node.AddValue("raytracingBounces", RaytracingBounces);
            node.AddValue("raytracingMoonlight", RaytracingMoonlight.ToString("0", CultureInfo.InvariantCulture));
            node.AddValue("raytracingMoonReflection", RaytracingMoonReflection.ToString("0", CultureInfo.InvariantCulture));
            node.AddValue("sunReflection", SunReflection.ToString("0", CultureInfo.InvariantCulture));
            node.AddValue("raytracingNightAmbient", RaytracingNightAmbient.ToString("0", CultureInfo.InvariantCulture));

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
            sb.Append(Enabled).Append('|').Append(Quality).Append('|')
              .Append(Sharpness.ToString("0.00", CultureInfo.InvariantCulture)).Append('|').Append(AutoExposure).Append('|')
              .Append(MipmapBias).Append('|').Append(CompensateLodBias).Append('|')
              .Append(DisableMsaa).Append('|').Append(ForceAnisotropic).Append('|')
              .Append(PauseWhileOpen).Append('|').Append(ReplaceKspSettings).Append('|')
              .Append(Jitter).Append('|').Append(SkinnedMotionVectors).Append('|').Append(TufxAfterUpscaling).Append('|')
              .Append(TransparencyMask).Append('|').Append(ReactiveMask).Append('|')
              .Append(FrameGeneration).Append('|').Append(Backend).Append('|').Append(DlssModel).Append('|')
              .Append(UpscalerKey).Append('|').Append(DiagnosticsKey).Append('|').Append(CameraListKey).Append('|')
              .Append(SettingsWindowKey).Append('|')
              .Append(NavballOffset.ToString("0", CultureInfo.InvariantCulture)).Append('|')
              .Append(NavballScreen).Append('|').Append(ScaleModWindows)
              .Append('|').Append(TimeWarpToolbarButton).Append('|').Append(FlightStartsInManeuverMode)
              .Append('|').Append(AutoAltimeter).Append('|').Append(FlightPanelAuto).Append('|').Append(WorldButtons)
              .Append('|').Append(MenuFireworks)
              .Append('|').Append(Raytracing).Append('|').Append(RaytracingBounces)
              .Append('|').Append(RaytracingMoonlight.ToString("0", CultureInfo.InvariantCulture))
              .Append('|').Append(RaytracingMoonReflection.ToString("0", CultureInfo.InvariantCulture))
              .Append('|').Append(SunReflection.ToString("0", CultureInfo.InvariantCulture))
              .Append('|').Append(RaytracingNightAmbient.ToString("0", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        // For KSP's settings dialog, which edits a copy and commits it only on
        // Apply or Accept -- Cancel has to leave the live values untouched.
        public OwnSettings Clone()
        {
            return (OwnSettings)MemberwiseClone();
        }

        private static bool Bool(ConfigNode node, string name, bool fallback)
        {
            bool value;
            string text = node.GetValue(name);
            return bool.TryParse(text, out value) ? value : fallback;
        }

        private static float Float(ConfigNode node, string name, float fallback)
        {
            float value;
            string text = node.GetValue(name);
            return float.TryParse(text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out value) ? value : fallback;
        }

        // A binding the file holds; what is no binding leaves the default.
        private static string Binding(ConfigNode node, string name, string fallback)
        {
            string text = node.GetValue(name);
            return text != null && KeyCombination.IsText(text) ? KeyCombination.Parse(text).ToString() : fallback;
        }

        private static T Enum<T>(ConfigNode node, string name, T fallback)
        {
            string text = node.GetValue(name);
            if (string.IsNullOrEmpty(text)) return fallback;
            try { return (T)System.Enum.Parse(typeof(T), text); }
            catch { return fallback; }
        }
    }
}
