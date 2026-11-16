using System;
using System.Collections.Generic;

namespace ReDefinition.Atmospheres.Viewer
{
    // One entry of the viewer's preset list: a file the player saved, or a built-in set that code provides (Today and the slots the cloud packages fill). A built-in entry is never
    // written to a file and never replaces a default: loading it sets the viewer's overrides and settings like any preset, and closing the viewer puts them back.
    internal sealed class PresetEntry
    {
        internal PresetEntry(string name, string note, bool builtIn, Func<OptionSet> make)
        {
            Name = name;
            Note = note ?? "";
            BuiltIn = builtIn;
            Make = make;
        }

        internal string Name { get; }
        internal string Note { get; }
        internal bool BuiltIn { get; }
        // The set; null for a file that cannot be read.
        internal Func<OptionSet> Make { get; }
    }

    // The list of the presets and the rules of their names. Today is the shipped defaults (every setting of the Clouds group at its default, no tuning override); the slots that
    // the cloud packages register (CloudKeyRegistrations.Preset) follow it; the player's saved presets follow those.
    internal static class ViewerPresetBook
    {
        internal const string TodayName = "Today";

        // A name the player may not save under: a built-in one (ignoring case).
        internal static bool IsReserved(string name)
        {
            string safe = ViewerPresets.SafeName(name);
            if (string.Equals(safe, ViewerPresets.SafeName(TodayName), StringComparison.OrdinalIgnoreCase)) return true;
            foreach (NamedKeyPreset p in CloudKeyRegistry.Presets)
                if (string.Equals(safe, ViewerPresets.SafeName(p.Name), StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // The set of Today: each setting key at its default and no tuning override.
        internal static OptionSet Today(IEnumerable<string> settingKeys, Func<string, double?> defaultOf)
        {
            var set = new OptionSet();
            foreach (string key in settingKeys)
            {
                double? d = defaultOf(key);
                if (d.HasValue) set.Settings[key] = d.Value;
            }
            return set;
        }

        // The set of a registered slot.
        internal static OptionSet FromSlot(NamedKeyPreset slot)
        {
            var set = new OptionSet();
            foreach (KeyValuePair<string, double> pair in slot.Settings) set.Settings[pair.Key] = pair.Value;
            foreach (KeyValuePair<string, float> pair in slot.Keys) set.Overrides[pair.Key] = pair.Value;
            return set;
        }

        // The built-in entries, in order: Today, then the slots.
        internal static List<PresetEntry> BuiltIn(IEnumerable<string> settingKeys, Func<string, double?> defaultOf)
        {
            var list = new List<PresetEntry>();
            // Which cloud system draws (ownClouds, cloudSource) is not part of a look: Today leaves them as they are, so that loading it never switches the pass off.
            var keys = new List<string>();
            foreach (string k in settingKeys)
                if (k != AtmospheresSettings.Keys.OwnClouds && k != AtmospheresSettings.Keys.CloudSource) keys.Add(k);
            list.Add(new PresetEntry(TodayName, "the shipped defaults: the cloud look and quality settings at their defaults, no tuning override; the pass and the cloud source stay as they are", true,
                () => Today(keys, defaultOf)));
            foreach (NamedKeyPreset slot in CloudKeyRegistry.Presets)
            {
                NamedKeyPreset s = slot;
                list.Add(new PresetEntry(s.Name, s.Note, true, () => FromSlot(s)));
            }
            return list;
        }

        // The names in the order the list shows them: the built-in ones, then the saved ones that are not built-in names.
        internal static List<PresetEntry> Entries(List<PresetEntry> builtIn, IEnumerable<string> savedNames, Func<string, Func<OptionSet>> reader)
        {
            var list = new List<PresetEntry>(builtIn);
            foreach (string name in savedNames)
            {
                bool taken = false;
                foreach (PresetEntry e in builtIn) if (string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)) { taken = true; break; }
                if (!taken) list.Add(new PresetEntry(name, "saved by the player", false, reader(name)));
            }
            return list;
        }

        // The entry after this one in the list (wrapping), for the window's cycling buttons; null for an empty list.
        internal static PresetEntry Next(IList<PresetEntry> entries, string current)
        {
            if (entries == null || entries.Count == 0) return null;
            for (int i = 0; i < entries.Count; i++)
                if (string.Equals(entries[i].Name, current, StringComparison.OrdinalIgnoreCase)) return entries[(i + 1) % entries.Count];
            return entries[0];
        }
    }
}
