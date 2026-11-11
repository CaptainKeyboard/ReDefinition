using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using ReDefinition.Core;

namespace ReDefinition.Atmospheres
{
    // The settings of ReDefinition Atmospheres, in settings.cfg in the PluginData folder of the mod's own folder. A missing or damaged file
    // leaves the defaults; a value outside its range is brought into it, and a value that is not a number is ignored. The mod writes
    // the file with the defaults when it starts and finds none (Load). The settings are the table SettingsTable.All (keys in
    // AtmospheresSettings.Keys.cs); the typed readers are in AtmospheresSettings.Values.cs and the members of deleted features, kept
    // until their last caller is gone, in AtmospheresSettings.Legacy.cs. Every setting starts at its default (ResetToDefaults).
    internal static partial class AtmospheresSettings
    {
        internal const string Mod = "ReDefinition/Atmospheres";
        internal const string FileName = "settings.cfg";

        private static readonly Dictionary<string, SettingDef> ByKey = BuildIndex();
        private static readonly Dictionary<string, double> Numbers = new Dictionary<string, double>(StringComparer.Ordinal);
        private static readonly Dictionary<string, string> Texts = new Dictionary<string, string>(StringComparer.Ordinal);
        private static bool loaded;

        static AtmospheresSettings()
        {
            ResetToDefaults();
        }

        private static Dictionary<string, SettingDef> BuildIndex()
        {
            var index = new Dictionary<string, SettingDef>(StringComparer.Ordinal);
            foreach (SettingDef def in SettingsTable.All) index.Add(def.Key, def);
            return index;
        }

        // Every setting, in the order the window lists them.
        internal static IReadOnlyList<SettingDef> Definitions
        {
            get { return SettingsTable.All; }
        }

        // The definition of a key; null for a key that is not a setting.
        internal static SettingDef Definition(string key)
        {
            SettingDef def;
            return ByKey.TryGetValue(key, out def) ? def : null;
        }

        // The value of a number, bool (0 or 1) or choice (the index) setting.
        internal static double Number(string key)
        {
            return Numbers[key];
        }

        // The value of a text setting.
        internal static string Text(string key)
        {
            return Texts[key];
        }

        // Sets a number, bool or choice setting; the value is brought into its range. Not a number: ignored, false.
        internal static bool SetNumber(string key, double value)
        {
            SettingDef def = Definition(key);
            if (def == null || def.Kind == SettingKind.Text || double.IsNaN(value) || double.IsInfinity(value)) return false;
            Numbers[key] = def.Limit(value);
            return true;
        }

        // Sets a text setting.
        internal static bool SetText(string key, string value)
        {
            SettingDef def = Definition(key);
            if (def == null || def.Kind != SettingKind.Text || value == null) return false;
            Texts[key] = value;
            return true;
        }

        // Every setting to its default.
        internal static void ResetToDefaults()
        {
            foreach (SettingDef def in SettingsTable.All)
            {
                if (def.Kind == SettingKind.Text) Texts[def.Key] = def.DefaultText;
                else Numbers[def.Key] = def.Default;
            }
            ResetLegacy();
        }

        // All settings as a node, to be put back with Restore (the tests keep the settings they change this way).
        internal static ConfigNode Snapshot()
        {
            var root = new ConfigNode();
            Fill(root);
            return root;
        }

        // The settings of a Snapshot: the defaults first, then every value of the node.
        internal static void Restore(ConfigNode snapshot)
        {
            ResetToDefaults();
            Apply(snapshot);
        }

        // Reads the file once. A file that is not there is made with the defaults, so that the player finds it and can edit it.
        internal static void Load()
        {
            if (loaded) return;
            loaded = true;
            LoadFrom(ModFolder.PluginFile(Mod, FileName));
        }

        // The settings of that file; where it does not exist it is written with the current settings. True if it was written.
        internal static bool LoadFrom(string path)
        {
            if (File.Exists(path))
            {
                Read(path);
                return false;
            }
            return Write(path);
        }

        internal static void Save()
        {
            Write(ModFolder.PluginFile(Mod, FileName));
        }

        // The settings from that file; they stay as they are where it is missing or damaged.
        internal static void Read(string path)
        {
            ModSettingsFile.Read(path, Apply, false);
        }

        // All settings into that file.
        internal static bool Write(string path)
        {
            return ModSettingsFile.Write(path, Fill);
        }

        private static bool Apply(ConfigNode root)
        {
            foreach (SettingDef def in SettingsTable.All)
            {
                string text;
                if (!ModSettingsFile.TryString(root, def.Key, out text)) continue;
                switch (def.Kind)
                {
                    case SettingKind.Text:
                        Texts[def.Key] = text;
                        break;
                    case SettingKind.Bool:
                        bool flag;
                        if (ModSettingsFile.TryBool(root, def.Key, out flag)) Numbers[def.Key] = flag ? 1.0 : 0.0;
                        break;
                    case SettingKind.Choice:
                        int index = def.ChoiceIndex(text);
                        if (index >= 0) Numbers[def.Key] = index;
                        break;
                    default:
                        double number;
                        if (TryNumber(text, out number)) Numbers[def.Key] = def.Limit(number);
                        break;
                }
            }
            return true;
        }

        private static void Fill(ConfigNode root)
        {
            foreach (SettingDef def in SettingsTable.All)
            {
                root.AddValue(def.Key, def.Kind == SettingKind.Text ? Texts[def.Key] : def.Format(Numbers[def.Key]));
            }
        }

        // A finite number written the invariant way.
        private static bool TryNumber(string text, out double value)
        {
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && !double.IsNaN(value) && !double.IsInfinity(value);
        }

        internal static float Limit(float value, float min, float max)
        {
            return Mathx.Clamp(value, min, max);
        }

        internal static int Limit(int value, int min, int max)
        {
            return Mathx.Clamp(value, min, max);
        }
    }
}
