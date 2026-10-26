using System.Collections.Generic;
using System;
using ReDefinition.Core;
using ReDefinition.Settings;
using ReDefinition.Vendor;

namespace ReDefinition.Modules
{
    // A graphics profile's MODULE nodes (docs/player/graphics-profiles.md) onto
    // ReDefinition's own modules (OurModules); the registered mods' part is
    // ProfileApplier's.
    internal static class ModuleProfiles
    {
        // Each MODULE node's values onto the settings they name, in `settings`.
        // `active`: a profile is chosen already, so the settings only the first
        // one sets (ModuleSetting.OnlyActivates) stay as the player has them.
        public static void Apply(GraphicsProfile profile, OwnSettings settings, List<string> problems, bool active = false)
        {
            if (settings == null) return;
            foreach (KeyValuePair<ModuleSetting, string> pair in Values(profile, problems, null))
                if (!active || !pair.Key.OnlyActivates) pair.Key.Write(settings, pair.Value);
        }

        // Whether a profile can set the setting: the graphics settings of ReDefinition's modules, not
        // taste (the sharpness), the hotkeys, the interface or the raytracing's single settings.
        internal static bool CanSet(ModuleSetting setting)
        {
            return setting.Kind != SettingKind.Taste && setting.Row == SettingCategory.General && !setting.Detail;
        }

        // The values a profile's MODULE nodes set, each as its setting holds it --
        // read as any mod's block is: a module, a setting or a value that cannot be
        // taken is reported and left out. A profile sets what its own modules offer in
        // the graphics settings, but neither taste (the sharpness) nor the hotkeys and the
        // interface. A value may differ by the card (ProfileValue); `gpu` null takes the
        // card KSP runs on. `problems` may be null. Internal: the check outside the game
        // asks the same.
        internal static List<KeyValuePair<ModuleSetting, string>> Values(GraphicsProfile profile,
                                                                          List<string> problems, GpuClass? gpu = null)
        {
            GpuClass card = gpu ?? GpuClasses.Current;
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
                        : !CanSet(setting) ? "a profile sets no taste, hotkey, interface or single raytracing setting"
                        : null;
                    List<ProfileValue.Alternative> alternatives = null;
                    if (why == null)
                    {
                        alternatives = ProfileValue.Parse(text, out why);
                        if (alternatives != null)
                            foreach (ProfileValue.Alternative alternative in alternatives)
                            {
                                why = setting.Refusal(alternative.Value);
                                if (why != null) break;
                            }
                    }
                    if (why != null)
                    {
                        if (problems != null) problems.Add(where + ", " + key + " = " + text + ": " + why + " -- left out.");
                        continue;
                    }
                    string value = ProfileValue.Resolve(alternatives, card);
                    if (value != null) values.Add(new KeyValuePair<ModuleSetting, string>(setting, setting.Normalize(value)));
                }
            }
            return values;
        }

        // How many of the modules' settings in `settings` no longer match the
        // profile: each that differs counts as one, as a mod's setting does --
        // with `active` as for Apply, not the ones it leaves to the player.
        public static int Differences(GraphicsProfile profile, OwnSettings settings, bool active = false)
        {
            int count = 0;
            if (settings == null) return count;
            foreach (KeyValuePair<ModuleSetting, string> pair in Values(profile, null))
            {
                if (active && pair.Key.OnlyActivates) continue;
                if (!SettingValues.Same(pair.Key.Normalize(pair.Key.Read(settings)), pair.Value)) count++;
            }
            return count;
        }

        // Right away, without the window -- the main menu's first question: the
        // registered mods' part, then the modules'. Bundling must be on already.
        public static void ApplyNow(GraphicsProfile profile, IList<GraphicsProfile> all, List<string> problems)
        {
            bool active = BundledSettings.ProfileChosen;
            ProfileApplier.ApplyNow(profile, all, problems);
            ReDefinitionAddon addon = ReDefinitionAddon.Instance;
            if (addon == null) return;
            // Caught as the window catches it: a failure here must not take the
            // report of what came before with it.
            try
            {
                OwnSettings before = addon.Current();
                OwnSettings after = before.Clone();
                Apply(profile, after, problems, active);
                addon.Apply(before, after);
                // A profile is what the player has now: the proxy's ini follows it, whatever was set there.
                LowLatencySetting.Write(after.LowLatency);
            }
            catch (Exception e)
            {
                problems.Add("Profile '" + profile.Name + "', upscaler: could not be applied (" + CompatibilityLog.Reason(e)
                             + ").");
            }
        }
    }
}
