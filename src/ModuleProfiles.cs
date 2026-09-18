using System.Collections.Generic;
using System;
using ReDefinition.Core;
using ReDefinition.Settings;

namespace ReDefinition
{
    // A graphics profile's MODULE nodes (docs/player/graphics-profiles.md) onto
    // ReDefinition's own modules (OurModules); the registered mods' part is
    // ProfileApplier's.
    internal static class ModuleProfiles
    {
        // Each MODULE node's values onto the settings they name, in `settings`.
        public static void Apply(GraphicsProfile profile, OwnSettings settings, List<string> problems)
        {
            if (settings == null) return;
            foreach (KeyValuePair<ModuleSetting, string> pair in Values(profile, problems))
                pair.Key.Write(settings, pair.Value);
        }

        // The values a profile's MODULE nodes set, each as its setting holds it --
        // read as any mod's block is, without a case of their own: quality settings
        // only, as for the other mods; a module, a setting or a value that cannot be
        // taken is reported and left out. `problems` may be null. Internal: the
        // check outside the game asks the same.
        internal static List<KeyValuePair<ModuleSetting, string>> Values(GraphicsProfile profile,
                                                                          List<string> problems)
        {
            List<KeyValuePair<ModuleSetting, string>> values = new List<KeyValuePair<ModuleSetting, string>>();
            foreach (KeyValuePair<string, ConfigNode> entry in profile.Modules)
            {
                string where = "Profile '" + profile.Name + "', module '" + entry.Key + "'";
                IGraphicsModule module = OurModules.Find(entry.Key);
                if (module == null)
                {
                    if (problems != null) problems.Add(where + ": no such module -- skipped.");
                    continue;
                }
                // A key given twice -- a pack's patch that adds a value rather than
                // editing it -- counts once, with its last value, and is said, as in a
                // mod's block: the last value is judged, and where it is refused the
                // earlier one does not stand in.
                List<string> keys = new List<string>();
                Dictionary<string, string> last = new Dictionary<string, string>();
                foreach (ConfigNode.Value value in entry.Value.values)
                {
                    if (value.name == "name") continue;
                    if (!last.ContainsKey(value.name)) keys.Add(value.name);
                    else if (problems != null) problems.Add(where + ": '" + value.name + "' twice -- the last one counts.");
                    last[value.name] = value.value;
                }
                foreach (string key in keys)
                {
                    string text = last[key];
                    ModuleSetting setting = module.Setting(key);
                    string why = setting == null ? "no setting of the module"
                        : setting.Kind != SettingKind.Quality ? "a profile sets quality only, never taste or other switches"
                        : setting.Refusal(text);
                    if (why != null)
                    {
                        if (problems != null) problems.Add(where + ", " + key + " = " + text + ": " + why + " -- left out.");
                        continue;
                    }
                    values.Add(new KeyValuePair<ModuleSetting, string>(setting, setting.Normalize(text)));
                }
            }
            return values;
        }

        // How many of the modules' settings in `settings` no longer match the
        // profile: each that differs counts as one, as a mod's setting does.
        public static int Differences(GraphicsProfile profile, OwnSettings settings)
        {
            int count = 0;
            if (settings == null) return count;
            foreach (KeyValuePair<ModuleSetting, string> pair in Values(profile, null))
                if (!SettingValues.Same(pair.Key.Normalize(pair.Key.Read(settings)), pair.Value)) count++;
            return count;
        }

        // Right away, without the window -- the main menu's first question: the
        // registered mods' part, then the modules'. Bundling must be on already.
        public static void ApplyNow(GraphicsProfile profile, IList<GraphicsProfile> all, List<string> problems)
        {
            ProfileApplier.ApplyNow(profile, all, problems);
            ReDefinitionAddon addon = ReDefinitionAddon.Instance;
            if (addon == null) return;
            // Caught as the window catches it: a failure here must not take the
            // report of what came before with it.
            try
            {
                OwnSettings before = addon.Current();
                OwnSettings after = before.Clone();
                Apply(profile, after, problems);
                addon.Apply(before, after);
            }
            catch (Exception e)
            {
                problems.Add("Profile '" + profile.Name + "', upscaler: could not be applied (" + CompatibilityLog.Reason(e)
                             + ").");
            }
        }
    }
}
