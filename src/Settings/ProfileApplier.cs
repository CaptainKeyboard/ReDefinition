using System.Collections.Generic;
using System.Globalization;
using System;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Settings
{
    // Carries a graphics profile out (docs/player/graphics-profiles.md): the
    // PROFILE blocks of the registrations onto the settings of the registered
    // mods, the same way the settings window commits a change, so that a profile
    // is nothing but many rows set at once, saved like every other. Its MODULE
    // nodes are ModuleProfiles'.
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

        // How many of the given values no longer match the profile -- with
        // ModuleProfiles.Differences, what makes it Custom. valueOf gives a
        // setting's value as the caller holds it.
        public static int Differences(Dictionary<BundledSetting, string> values,
                                      Func<BundledSetting, string> valueOf)
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
            return count;
        }

        // The registered mods' part right away, without the window
        // (ModuleProfiles.ApplyNow). Bundling must be on already.
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
        }

        // A mod the chosen profile never reached gets its values now: one
        // installed since the profile was applied, or one built differently --
        // Volumetric Clouds brings another build of EVE and of Scatterer, and its
        // TUFX profile. Only those mods are set, so what the player changed in the
        // others stays. Nothing happens with no profile chosen, with the bundling
        // off, or where the profile is no longer in the GameDatabase.
        public static bool CatchUpNewMods(List<string> problems)
        {
            List<string> fresh = BundledSettings.ModsWithoutTheProfile();
            if (fresh.Count == 0) return false;

            List<GraphicsProfile> all = Ordered(problems);
            GraphicsProfile profile = Find(all, BundledSettings.ProfileName);
            if (profile == null)
            {
                problems.Add("The profile '" + BundledSettings.ProfileName + "' is not in the GameDatabase -- the"
                             + " settings of " + string.Join(", ", fresh.ToArray()) + " are left as they are.");
                return false;
            }

            HashSet<string> ids = new HashSet<string>(fresh);
            List<string> touched = new List<string>();
            foreach (KeyValuePair<BundledSetting, string> pair in Values(profile, problems))
            {
                BundledSetting setting = pair.Key;
                if (setting.Owner == null || !ids.Contains(setting.Owner.Id)) continue;
                try
                {
                    if (SettingValues.Same(pair.Value, BundledSettings.Current(setting))
                        && BundledSettings.Holds(setting, pair.Value)) continue;
                    BundledSettings.Set(setting, pair.Value, false);
                    if (!touched.Contains(setting.Owner.ModName)) touched.Add(setting.Owner.ModName);
                }
                catch (Exception e)
                {
                    problems.Add("Profile '" + profile.Name + "', " + setting.Owner.ModName + ", " + setting.Title
                                 + ": could not be set (" + CompatibilityLog.Reason(e) + ").");
                }
            }

            BundledSettings.NoteProfileApplied();
            BundledSettings.SaveNow();
            Debug.Log(Log.Tag + " Profile '" + profile.Title + "' applied to "
                      + (touched.Count > 0 ? string.Join(", ", touched.ToArray()) : "no setting of "
                                             + string.Join(", ", fresh.ToArray()))
                      + ": installed or built differently since it was chosen.");
            return touched.Count > 0;
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
            Debug.LogWarning(Log.Tag + " " + what + ":\n  " + string.Join("\n  ", fresh.ToArray()));
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
