using System.Collections.Generic;

namespace ReDefinition.Settings
{
    // What a graphics profile sets for each registered mod: the PROFILE blocks of its
    // registration with the profile's name that fit the installed build, taken as the
    // defaults' blocks are (ModDefaults.Fitting; docs/modders/registering-a-mod.md). A
    // profile starts from the defaults (ModDefaults), and what installed mods require
    // counts over both (ProfileApplier.Values).
    internal static class ModProfiles
    {
        // By mod id, the values `profile` gives the installed registered mods among
        // `mods`. The check outside the game selects the same way.
        internal static Dictionary<string, Dictionary<string, string>> Select(IEnumerable<IBundledMod> mods, string profile)
        {
            Dictionary<string, Dictionary<string, string>> selected = new Dictionary<string, Dictionary<string, string>>();
            foreach (IBundledMod mod in mods)
            {
                RegisteredMod registered = mod as RegisteredMod;
                if (registered == null || !registered.IsInstalled) continue;
                List<ProfileRegistration> named = registered.Registration.Profiles.FindAll(block => block.Name == profile);
                if (named.Count == 0) continue;
                Dictionary<string, string> values = null;
                foreach (ProfileRegistration block in ModDefaults.Fitting(mod, named))
                {
                    if (values == null) values = new Dictionary<string, string>();
                    foreach (KeyValuePair<string, string> pair in block.Values) values[pair.Key] = pair.Value;
                }
                if (values != null) selected[mod.Id] = values;
            }
            return selected;
        }

        // By mod id, what every profile sets for the installed registered mods among
        // `mods`, whatever the setting's kind: their ALL_PROFILES blocks that fit the
        // installed build -- what makes a mod fit ReDefinition.
        internal static Dictionary<string, Dictionary<string, string>> SelectAll(IEnumerable<IBundledMod> mods)
        {
            Dictionary<string, Dictionary<string, string>> selected = new Dictionary<string, Dictionary<string, string>>();
            foreach (IBundledMod mod in mods)
            {
                RegisteredMod registered = mod as RegisteredMod;
                if (registered == null || !registered.IsInstalled) continue;
                Dictionary<string, string> values = null;
                foreach (AllProfilesRegistration block in ModDefaults.Fitting(mod, registered.Registration.AllProfiles))
                {
                    if (values == null) values = new Dictionary<string, string>();
                    foreach (KeyValuePair<string, string> pair in block.Values) values[pair.Key] = pair.Value;
                }
                if (values != null) selected[mod.Id] = values;
            }
            return selected;
        }

        // A block for a profile no GRAPHICS_PROFILE defines sets nothing: said,
        // once for each mod and name -- usually a typo, or a pack's profile that is
        // not installed.
        internal static List<string> Unknown(IEnumerable<IBundledMod> mods, ICollection<string> profiles)
        {
            List<string> problems = new List<string>();
            foreach (IBundledMod mod in mods)
            {
                RegisteredMod registered = mod as RegisteredMod;
                if (registered == null || !registered.Detected) continue;
                foreach (ProfileRegistration block in registered.Registration.Profiles)
                {
                    if (profiles.Contains(block.Name)) continue;
                    string problem = "Mod '" + mod.Id + "': values for profile '" + block.Name
                                     + "', which no " + GraphicsProfile.NodeName + " defines -- unused.";
                    if (!problems.Contains(problem)) problems.Add(problem);
                }
            }
            return problems;
        }
    }
}
