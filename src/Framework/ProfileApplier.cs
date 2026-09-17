using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace ReDefinition.Framework
{
    // Carries a graphics profile out (docs/player/graphics-profiles.md): its MODULE
    // nodes onto this mod's own settings, the PROFILE blocks of the registrations
    // onto the settings of the registered mods -- the same way the settings window
    // commits a change, so that a profile is nothing but many rows set at once,
    // saved like every other.
    //
    // A mod that is not installed is skipped without a word: a profile covers
    // every mod it knows, a player has only some of them. So is a setting an
    // installed mod's registration or behaviour leaves out -- KSP's aerodynamic FX
    // next to Firefly. A key an installed mod does not offer at all, or a value its
    // control cannot take, is reported and left out.
    internal static class ProfileApplier
    {
        // Problems already in the log this run.
        private static readonly HashSet<string> reported = new HashSet<string>();

        // Every profile in the GameDatabase, Low before Max: by their order,
        // then by name.
        public static List<GraphicsProfile> Ordered(List<string> problems)
        {
            List<GraphicsProfile> profiles = ProfileLibrary.LoadAll(problems);
            profiles.Sort((a, b) => a.Order != b.Order
                ? a.Order.CompareTo(b.Order)
                : string.CompareOrdinal(a.Name, b.Name));
            return profiles;
        }

        public static GraphicsProfile Find(IList<GraphicsProfile> profiles, string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            foreach (GraphicsProfile profile in profiles)
                if (profile.Name == name) return profile;
            return null;
        }

        // The profile's values for the mods installed now, by setting -- each
        // looked up in its own mod's settings, not across all of them: every
        // quality setting at the default of the installed build, the values each
        // mod's registration gives this profile (ModProfiles.Select), what every
        // profile sets so that the mods fit ReDefinition, whatever the kind, over
        // them (ModProfiles.SelectAll), and what installed mods require over all of
        // it (Requirements).
        public static Dictionary<BundledSetting, string> Values(GraphicsProfile profile, List<string> problems)
        {
            Dictionary<BundledSetting, string> values = new Dictionary<BundledSetting, string>();
            foreach (KeyValuePair<string, Dictionary<string, string>> block in ModDefaults.Values(problems))
                Take(values, "Defaults", block.Key, block.Value, true, true, problems);
            List<IBundledMod> installed = BundledSettings.Installed();
            foreach (KeyValuePair<string, Dictionary<string, string>> block in ModProfiles.Select(installed, profile.Name))
                Take(values, "Profile '" + profile.Name + "'", block.Key, block.Value, false, true, problems);
            foreach (KeyValuePair<string, Dictionary<string, string>> block in ModProfiles.SelectAll(installed))
                Take(values, "Every profile", block.Key, block.Value, false, false, problems);

            foreach (BundledSetting setting in new List<BundledSetting>(values.Keys))
                values[setting] = Requirements.Adjust(setting, values[setting]);
            return values;
        }

        // What *Reset to defaults* fills in: every setting of every kind at the
        // default of the installed build, with what an installed mod requires
        // over it -- KSP's terrain shader quality, which has no default, left as
        // it is (docs/player/graphics-profiles.md).
        public static Dictionary<BundledSetting, string> DefaultValues(List<string> problems)
        {
            Dictionary<BundledSetting, string> values = new Dictionary<BundledSetting, string>();
            foreach (KeyValuePair<string, Dictionary<string, string>> block in ModDefaults.Values(problems))
                Take(values, "Defaults", block.Key, block.Value, true, false, problems);
            foreach (BundledSetting setting in new List<BundledSetting>(values.Keys))
                values[setting] = Requirements.Adjust(setting, values[setting]);
            return values;
        }

        // One mod's values into `values`: quality settings only -- a profile
        // never sets a mod's taste or its other switches. `inherited` for the
        // defaults, which hold every kind and are not reported for the ones a
        // profile leaves.
        private static void Take(Dictionary<BundledSetting, string> values, string from, string id,
                                 Dictionary<string, string> pairs, bool inherited, bool qualityOnly,
                                 List<string> problems)
        {
            // Both selections hold installed registered mods only.
            IBundledMod mod = Mod(id);
            if (mod == null || !mod.IsInstalled) return;

            foreach (KeyValuePair<string, string> pair in pairs)
            {
                BundledSetting setting = Setting(mod, pair.Key);
                if (setting == null)
                {
                    if (!inherited && !Dropped(mod, pair.Key))
                        problems.Add(from + ", " + mod.ModName + ": no setting '" + pair.Key + "' here -- left out.");
                    continue;
                }
                if (qualityOnly && setting.Kind != SettingKind.Quality)
                {
                    if (!inherited)
                        problems.Add(from + ", " + mod.ModName + ", " + setting.Title
                                     + ": a profile sets quality only, never a mod's taste or its other switches -- left out.");
                    continue;
                }
                string refusal = Refusal(setting, pair.Value);
                if (refusal != null)
                {
                    problems.Add(from + ", " + mod.ModName + ", " + setting.Title + ": '" + pair.Value + "' is " + refusal
                                 + " -- left out.");
                    continue;
                }
                values[setting] = pair.Value;
            }
        }

        // What choosing a profile fills in: its own values, and for every
        // setting the profile applied now set but this one leaves alone, what it
        // had before ReDefinition first changed it -- or a TUFX profile Low
        // strips would stay stripped under Medium, whose rows leave the pack's
        // own choice in place. Only where it still holds the applied profile's
        // value: one changed since, here or in the mod's own window, is the
        // player's and stays. Those settings also go into `released`: they are
        // released rather than set, since a per-save mod's value from before
        // differs from save to save. `applied` is null where no profile is
        // applied; settings no profile names stay as well.
        public static Dictionary<BundledSetting, string> WithReleased(Dictionary<BundledSetting, string> own,
                                                                       Dictionary<BundledSetting, string> applied,
                                                                       ICollection<BundledSetting> released)
        {
            Dictionary<BundledSetting, string> full = new Dictionary<BundledSetting, string>(own);
            if (applied == null) return full;
            foreach (KeyValuePair<BundledSetting, string> pair in applied)
            {
                if (full.ContainsKey(pair.Key) || !SettingValues.Same(BundledSettings.Current(pair.Key), pair.Value))
                    continue;
                string back = BundledSettings.BeforeReDefinition(pair.Key);
                if (back == null) continue;
                full[pair.Key] = back;
                released.Add(pair.Key);
            }
            return full;
        }

        // The full values of `profile` among `all`, as WithReleased has them
        // against the profile applied now.
        public static Dictionary<BundledSetting, string> FullValues(GraphicsProfile profile, IList<GraphicsProfile> all,
                                                                     List<string> problems,
                                                                     ICollection<BundledSetting> released)
        {
            GraphicsProfile applied = Find(all, BundledSettings.ProfileName);
            return WithReleased(Values(profile, problems),
                applied != null && applied != profile ? Values(applied, new List<string>()) : null, released);
        }

        // ReDefinition's modules' part (OurModules): each MODULE node's values onto the
        // settings they name, in `settings`.
        public static void ApplyModules(GraphicsProfile profile, UpscalerSettings settings, List<string> problems)
        {
            if (settings == null) return;
            foreach (KeyValuePair<ModuleSetting, string> pair in ModuleValues(profile, problems))
                pair.Key.Write(settings, pair.Value);
        }

        // The values a profile's MODULE nodes set, each as its setting holds it --
        // read as any mod's block is, without a case of their own: quality settings
        // only, as for the other mods; a module, a setting or a value that cannot be
        // taken is reported and left out. `problems` may be null. Internal: the
        // check outside the game asks the same.
        internal static List<KeyValuePair<ModuleSetting, string>> ModuleValues(GraphicsProfile profile,
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

        // How many of the given values and of ReDefinition's modules' settings no longer
        // match the profile -- what makes it Custom. valueOf gives a setting's value
        // as the caller holds it.
        public static int Differences(GraphicsProfile profile, Dictionary<BundledSetting, string> values,
                                      Func<BundledSetting, string> valueOf, UpscalerSettings upscaler)
        {
            int count = 0;
            // A value that cannot be read now is not known to differ: with every
            // quality setting in every profile, one mod not ready would make a
            // profile Custom the moment it is applied.
            foreach (KeyValuePair<BundledSetting, string> pair in values)
            {
                string now = valueOf(pair.Key);
                if (now != null && !SettingValues.Same(now, pair.Value)) count++;
            }

            // A module's setting that differs counts as one, as a mod's does.
            if (upscaler != null)
            {
                foreach (KeyValuePair<ModuleSetting, string> pair in ModuleValues(profile, null))
                    if (!SettingValues.Same(pair.Key.Normalize(pair.Key.Read(upscaler)), pair.Value)) count++;
            }
            return count;
        }

        // Right away, without the window -- the main menu's first question.
        // Bundling must be on already.
        public static void ApplyNow(GraphicsProfile profile, IList<GraphicsProfile> all, List<string> problems)
        {
            HashSet<BundledSetting> released = new HashSet<BundledSetting>();
            foreach (KeyValuePair<BundledSetting, string> pair in FullValues(profile, all, problems, released))
            {
                BundledSetting setting = pair.Key;
                try
                {
                    // As the window's Apply: what a setting holds already is not
                    // set again, and what is handed back is released.
                    if (released.Contains(setting)) BundledSettings.Release(setting);
                    else if (!SettingValues.Same(pair.Value, BundledSettings.Current(setting))
                             || !BundledSettings.Holds(setting, pair.Value))
                        BundledSettings.Set(setting, pair.Value, false);
                }
                catch (Exception e)
                {
                    problems.Add("Profile '" + profile.Name + "', " + setting.Owner.ModName + ", " + setting.Title
                                 + ": could not be set (" + CompatibilityLog.Reason(e) + ").");
                }
            }
            BundledSettings.ProfileName = profile.Name;
            BundledSettings.SaveNow();

            UpscalerAddon addon = UpscalerAddon.Instance;
            if (addon == null) return;
            // Caught as the window catches it: a failure here must not take the
            // report of what came before with it.
            try
            {
                UpscalerSettings before = addon.Current();
                UpscalerSettings after = before.Clone();
                ApplyModules(profile, after, problems);
                addon.Apply(before, after);
            }
            catch (Exception e)
            {
                problems.Add("Profile '" + profile.Name + "', upscaler: could not be applied (" + CompatibilityLog.Reason(e)
                             + ").");
            }
        }

        // Each problem once per run, however often the window opens -- and one
        // that appears only at a later opening, TUFX's profile list known by
        // then, still reaches the log.
        public static void Report(string what, List<string> problems)
        {
            List<string> fresh = new List<string>();
            foreach (string problem in problems)
                if (reported.Add(problem)) fresh.Add(problem);
            if (fresh.Count == 0) return;
            Debug.LogWarning(UpscalerProbe.Tag + " " + what + ":\n  " + string.Join("\n  ", fresh.ToArray()));
        }

        // Null where the setting's control can take the value; otherwise why
        // not. Internal: the check outside the game asks the same.
        internal static string Refusal(BundledSetting setting, string value)
        {
            switch (setting.Control)
            {
                case SettingControl.Toggle:
                    bool on;
                    return bool.TryParse(value, out on) ? null : "not True or False";

                case SettingControl.Slider:
                    double number;
                    if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number))
                        return "not a number";
                    // TryParse takes "NaN" and "Infinity", and NaN passes every
                    // comparison below.
                    if (double.IsNaN(number) || double.IsInfinity(number)) return "not a finite number";
                    if (number < setting.Min - 1e-6 || number > setting.Max + 1e-6)
                        return "outside " + setting.Min.ToString(CultureInfo.InvariantCulture) + " .. "
                               + setting.Max.ToString(CultureInfo.InvariantCulture);
                    // The slider would round it on its first frame, and the row
                    // turn Custom by itself.
                    if (setting.WholeNumbers && Math.Abs(number - Math.Round(number)) > 1e-6)
                        return "not a whole number";
                    return null;

                case SettingControl.Value:
                    if (setting.ValueType == null) return null;
                    try
                    {
                        SettingValues.Parse(value, setting.ValueType);
                        return null;
                    }
                    catch (Exception)
                    {
                        return "no value its " + setting.ValueType.Name + " takes";
                    }

                default:
                    string[] choices;
                    try
                    {
                        choices = setting.CurrentChoices();
                    }
                    catch (Exception)
                    {
                        choices = null;
                    }
                    // Known only later -- TUFX's profiles before TUFX has loaded
                    // them: taken as written, and checked again when it applies.
                    if (choices == null || choices.Length == 0) return null;
                    foreach (string choice in choices)
                        if (SettingValues.Same(choice, value)) return null;
                    return "not one of " + string.Join(", ", choices);
            }
        }

        private static IBundledMod Mod(string id)
        {
            foreach (IBundledMod mod in BundledSettings.Mods)
                if (mod.Id == id) return mod;
            return null;
        }

        // Through the index by key, not along the list: some 180 lookups a
        // profile, whenever the profiles are read. Take asks of installed mods
        // only, which the index holds.
        private static BundledSetting Setting(IBundledMod mod, string name)
        {
            BundledSetting setting = BundledSettings.Find(mod.Id + "." + name);
            return setting != null && setting.Owner == mod ? setting : null;
        }

        // Left out by its registration or behaviour, with the reason kept.
        private static bool Dropped(IBundledMod mod, string name)
        {
            foreach (string line in mod.DroppedMembers)
                if (line.StartsWith(name + RegisteredMod.DropSeparator, StringComparison.Ordinal)) return true;
            return false;
        }
    }
}
