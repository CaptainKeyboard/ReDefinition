using System.IO;
using System.Text;
using FidelityFX.FSR3;
using ReDefinition.Bridges;
using ReDefinition.Core;
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
        public UpscalerBackend Backend = UpscalerBackend.Fsr3;
        public DlssPreset DlssPreset = DlssPreset.Default;

        // The hotkeys, as KeyCombination writes them. The defaults are the keys
        // ReDefinition had before they could be set.
        public string UpscalerKey = "RightControl+RightShift+U";
        public string DiagnosticsKey = "RightControl+RightShift+K";
        public string CameraListKey = "RightControl+RightShift+N";
        public string SettingsWindowKey = KeyCombination.NoneText;

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
            settings.ForceAnisotropic = Bool(node, "forceAnisotropic", settings.ForceAnisotropic);
            settings.Jitter = Bool(node, "jitter", settings.Jitter);
            settings.SkinnedMotionVectors = Bool(node, "skinnedMotionVectors", settings.SkinnedMotionVectors);
            settings.TufxAfterUpscaling = Bool(node, "tufxAfterUpscaling", settings.TufxAfterUpscaling);
            settings.TransparencyMask = Bool(node, "transparencyMask", settings.TransparencyMask);
            settings.ReactiveMask = Enum(node, "reactiveMask", settings.ReactiveMask);
            settings.FrameGeneration = Bool(node, "frameGeneration", settings.FrameGeneration);
            settings.Backend = Enum(node, "technique", settings.Backend);
            settings.DlssPreset = Enum(node, "dlssPreset", settings.DlssPreset);
            settings.UpscalerKey = Binding(node, "upscalerKey", settings.UpscalerKey);
            settings.DiagnosticsKey = Binding(node, "diagnosticsKey", settings.DiagnosticsKey);
            settings.CameraListKey = Binding(node, "cameraListKey", settings.CameraListKey);
            settings.SettingsWindowKey = Binding(node, "settingsWindowKey", settings.SettingsWindowKey);
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
            node.AddValue("forceAnisotropic", ForceAnisotropic);
            node.AddValue("jitter", Jitter);
            node.AddValue("skinnedMotionVectors", SkinnedMotionVectors);
            node.AddValue("tufxAfterUpscaling", TufxAfterUpscaling);
            node.AddValue("transparencyMask", TransparencyMask);
            node.AddValue("reactiveMask", ReactiveMask);
            node.AddValue("frameGeneration", FrameGeneration);
            node.AddValue("technique", Backend);
            node.AddValue("dlssPreset", DlssPreset);
            node.AddValue("upscalerKey", UpscalerKey);
            node.AddValue("diagnosticsKey", DiagnosticsKey);
            node.AddValue("cameraListKey", CameraListKey);
            node.AddValue("settingsWindowKey", SettingsWindowKey);

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
              .Append(Sharpness.ToString("0.00")).Append('|').Append(AutoExposure).Append('|')
              .Append(MipmapBias).Append('|').Append(CompensateLodBias).Append('|')
              .Append(DisableMsaa).Append('|').Append(ForceAnisotropic).Append('|')
              .Append(Jitter).Append('|').Append(SkinnedMotionVectors).Append('|').Append(TufxAfterUpscaling).Append('|')
              .Append(TransparencyMask).Append('|').Append(ReactiveMask).Append('|')
              .Append(FrameGeneration).Append('|').Append(Backend).Append('|').Append(DlssPreset).Append('|')
              .Append(UpscalerKey).Append('|').Append(DiagnosticsKey).Append('|').Append(CameraListKey).Append('|')
              .Append(SettingsWindowKey);
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
