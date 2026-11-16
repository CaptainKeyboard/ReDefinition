using System;
using System.Collections.Generic;
using System.Globalization;

namespace ReDefinition.Atmospheres.Viewer
{
    // What the cloud viewer reads and writes of the options: the settings (AtmospheresSettings) and the tuning overrides (Tuning). The game's store is
    // GameOptionStore; the tests pass one of their own.
    internal interface IOptionStore
    {
        double Setting(string key);
        bool SetSetting(string key, double value);
        bool TryOverride(string key, out float value);
        void SetOverride(string key, float value);
        void ClearOverride(string key);
        // The value a reader gets now: the override, the nodes' value or the compiled default.
        float Effective(string key, float compiledDefault);
    }

    // The game's options: the settings table and the tuning table. A setting changed here is not written to settings.cfg (the viewer puts it back on close).
    internal sealed class GameOptionStore : IOptionStore
    {
        public double Setting(string key)
        {
            return AtmospheresSettings.Number(key);
        }

        public bool SetSetting(string key, double value)
        {
            return AtmospheresSettings.SetNumber(key, value);
        }

        public bool TryOverride(string key, out float value)
        {
            return Tuning.Instance.TryGetOverride(key, out value);
        }

        public void SetOverride(string key, float value)
        {
            Tuning.SetOverride(key, value);
        }

        public void ClearOverride(string key)
        {
            Tuning.ClearOverride(key);
        }

        public float Effective(string key, float compiledDefault)
        {
            // The override and the nodes' value as they stand now, not the table a reader got at the frame's start: that one still holds a value whose override was removed in this
            // frame (a solo mode that took the cell off read the cell back from it and put it on again: K-S1 start, every uniform-field view was drawn as one far cell).
            float value;
            if (Tuning.Instance.TryGetOverride(key, out value)) return value;
            return Tuning.Instance.TryGetLoaded(key, out value) ? value : compiledDefault;
        }
    }

    // The settings A/B and the presets hold: the settings of the Clouds group and the forced precipitation (forceLevel, forceType) that a forced weather sets with its overrides.
    internal static class ViewerOptionKeys
    {
        internal static List<string> Settings(IEnumerable<string> cloudsGroup)
        {
            var keys = new List<string>(cloudsGroup);
            foreach (string key in new[] { AtmospheresSettings.Keys.ForceLevel, AtmospheresSettings.Keys.ForceType })
                if (!keys.Contains(key)) keys.Add(key);
            return keys;
        }
    }

    // One set of options: the values of a list of settings, the tuning overrides among a list of keys (a key without an entry has no override) and the name of the forced
    // weather the overrides belong to.
    internal sealed class OptionSet
    {
        internal const string SettingsNode = "SETTINGS", TuningNode = "TUNING", ViewerNode = "VIEWER", LabNode = "LAB", WeatherValue = "weather";

        internal readonly Dictionary<string, double> Settings = new Dictionary<string, double>(StringComparer.Ordinal);
        internal readonly Dictionary<string, float> Overrides = new Dictionary<string, float>(StringComparer.Ordinal);
        // The weather lab's levers and seed ("gen.<field>", "layer.<key>", "seed"); they are staged by ApplyTo and act at the next Rebuild Weather.
        internal readonly Dictionary<string, double> Extras = new Dictionary<string, double>(StringComparer.Ordinal);
        internal ViewerWeather Weather = ViewerWeather.Field;

        // The options as the store holds them now.
        internal static OptionSet Capture(IOptionStore store, IEnumerable<string> settingKeys, IEnumerable<string> tuningKeys, ViewerWeather weather = ViewerWeather.Field,
                                          WeatherLab lab = null)
        {
            var set = new OptionSet { Weather = weather };
            foreach (string key in settingKeys) set.Settings[key] = store.Setting(key);
            foreach (string key in tuningKeys)
            {
                float value;
                if (store.TryOverride(key, out value)) set.Overrides[key] = value;
            }
            if (lab != null) lab.Capture(set.Extras);
            return set;
        }

        // Puts the set into the store: its settings, and for every key of the list its override or none; the lab's levers are staged (a set without any puts them back to the baseline).
        internal void ApplyTo(IOptionStore store, IEnumerable<string> tuningKeys, WeatherLab lab = null)
        {
            foreach (KeyValuePair<string, double> pair in Settings) store.SetSetting(pair.Key, pair.Value);
            foreach (string key in tuningKeys)
            {
                float value;
                if (Overrides.TryGetValue(key, out value)) store.SetOverride(key, value);
                else store.ClearOverride(key);
            }
            if (lab != null) lab.Stage(Extras);
        }

        internal ConfigNode ToNode()
        {
            var root = new ConfigNode();
            ConfigNode settings = root.AddNode(SettingsNode);
            foreach (KeyValuePair<string, double> pair in Settings) settings.AddValue(pair.Key, pair.Value.ToString("R", CultureInfo.InvariantCulture));
            ConfigNode tuning = root.AddNode(TuningNode);
            foreach (KeyValuePair<string, float> pair in Overrides) tuning.AddValue(pair.Key, pair.Value.ToString("R", CultureInfo.InvariantCulture));
            ConfigNode lab = root.AddNode(LabNode);
            foreach (KeyValuePair<string, double> pair in Extras) lab.AddValue(pair.Key, pair.Value.ToString("R", CultureInfo.InvariantCulture));
            root.AddNode(ViewerNode).AddValue(WeatherValue, Weather.ToString());
            return root;
        }

        // A set from a node of ToNode; lines that are not numbers are skipped.
        internal static OptionSet FromNode(ConfigNode root)
        {
            var set = new OptionSet();
            if (root == null) return set;
            ConfigNode settings = root.GetNode(SettingsNode);
            if (settings != null)
                foreach (ConfigNode.Value v in settings.values)
                {
                    double d;
                    if (double.TryParse(v.value, NumberStyles.Float, CultureInfo.InvariantCulture, out d) && !double.IsNaN(d) && !double.IsInfinity(d)) set.Settings[v.name] = d;
                }
            ConfigNode tuning = root.GetNode(TuningNode);
            if (tuning != null)
                foreach (ConfigNode.Value v in tuning.values)
                {
                    float f;
                    if (float.TryParse(v.value, NumberStyles.Float, CultureInfo.InvariantCulture, out f) && !float.IsNaN(f) && !float.IsInfinity(f)) set.Overrides[v.name] = f;
                }
            ConfigNode lab = root.GetNode(LabNode);
            if (lab != null)
                foreach (ConfigNode.Value v in lab.values)
                {
                    double d;
                    if (double.TryParse(v.value, NumberStyles.Float, CultureInfo.InvariantCulture, out d) && !double.IsNaN(d) && !double.IsInfinity(d)) set.Extras[v.name] = d;
                }
            ConfigNode viewer = root.GetNode(ViewerNode);
            ViewerWeather weather;
            if (viewer != null && Enum.TryParse(viewer.GetValue(WeatherValue), out weather) && Enum.IsDefined(typeof(ViewerWeather), weather)) set.Weather = weather;
            return set;
        }
    }

    // The A/B comparison: the set not shown is stored, the shown one is the store's. StoreA keeps the options as they are as A and the player edits on as B;
    // Flip puts the stored set into the store and keeps the one that was shown.
    internal sealed class OptionFlip
    {
        private OptionSet stored;

        // 'A' or 'B': the side the store holds now; '-' before StoreA.
        internal char Showing { get; private set; } = '-';

        internal bool HasStored
        {
            get { return stored != null; }
        }

        internal void StoreA(OptionSet current)
        {
            stored = current;
            Showing = 'B';
        }

        // Swaps the sides; false where nothing is stored.
        internal bool Flip(IOptionStore store, IEnumerable<string> settingKeys, IList<string> tuningKeys)
        {
            ViewerWeather weather = ViewerWeather.Field;
            return Flip(store, settingKeys, tuningKeys, ref weather);
        }

        // Swaps the sides; weather is the forced weather shown now and becomes the one of the side put back; lab holds the weather lab's staged levers, which flip with the rest.
        internal bool Flip(IOptionStore store, IEnumerable<string> settingKeys, IList<string> tuningKeys, ref ViewerWeather weather, WeatherLab lab = null)
        {
            if (stored == null) return false;
            OptionSet shown = OptionSet.Capture(store, settingKeys, tuningKeys, weather, lab);
            stored.ApplyTo(store, tuningKeys, lab);
            weather = stored.Weather;
            stored = shown;
            Showing = Showing == 'A' ? 'B' : 'A';
            return true;
        }

        internal void Clear()
        {
            stored = null;
            Showing = '-';
        }
    }

    // The named option presets of the viewer: one file each in the mod's settings folder (PluginData/CloudViewer/<name>.cfg).
    internal static class ViewerPresets
    {
        internal const string Folder = "CloudViewer";

        // A file name from what the player typed: letters, digits, '-' and '_' kept, anything else '_', at most 40 characters; empty where nothing is left.
        internal static string SafeName(string typed)
        {
            if (typed == null) return "";
            var chars = new List<char>();
            foreach (char c in typed.Trim())
            {
                if (chars.Count >= 40) break;
                chars.Add(char.IsLetterOrDigit(c) && c < 128 || c == '-' || c == '_' ? c : '_');
            }
            string name = new string(chars.ToArray()).Trim('_');
            return name;
        }

        // The name of a picture taken at a time (to the second): the stamp, with -2, -3 and so on where a picture of that name stands, so that two pictures in one
        // second do not write over each other.
        internal static string UniqueName(string stamp, Func<string, bool> exists)
        {
            string name = stamp;
            for (int n = 2; exists(name); n++) name = stamp + "-" + n.ToString(CultureInfo.InvariantCulture);
            return name;
        }
    }
}
