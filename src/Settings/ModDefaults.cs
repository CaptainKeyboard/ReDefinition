using System.Collections.Generic;
using System;

namespace ReDefinition.Settings
{
    // The defaults of every bundled mod, per build (docs/reference/mod-defaults.md):
    // what the reset sets, and what a profile starts from
    // (docs/player/graphics-profiles.md). From each mod's registration
    // (docs/modders/registering-a-mod.md): a setting's `default`, then the DEFAULTS
    // blocks that fit the installed build (Fitting). The version a value used was
    // read from -- its block's, or the registration's -- is reported where it is not
    // the installed one, and the values are still used for the settings that build
    // has. A setting without a default is left as it is by the reset: KSP's terrain
    // shader quality, which KSP's own reset leaves alone and no code of KSP assigns.
    internal static class ModDefaults
    {
        // The defaults as last worked out, with the problems found then: the
        // profiles are read together, each from the defaults, so these are worked
        // out once a frame rather than once a profile.
        private static Dictionary<string, Dictionary<string, string>> frameValues;
        private static readonly List<string> frameProblems = new List<string>();
        private static int valuesFrame = -1;

        // By mod id, the defaults of the mods installed now. Not to be changed by
        // the caller: it is shared.
        public static Dictionary<string, Dictionary<string, string>> Values(List<string> problems)
        {
            int frame = UnityEngine.Time.frameCount;
            if (frameValues == null || frame != valuesFrame)
            {
                frameProblems.Clear();
                frameValues = Read(frameProblems);
                valuesFrame = frame;
            }
            problems.AddRange(frameProblems);
            return frameValues;
        }

        // The blocks that fit the build of the mod installed: those for every build
        // first, then those for its build, each in their order, a later value over an
        // earlier one -- so a block for the build counts over one for every build
        // wherever a patch adds it. The profiles' blocks are taken the same way.
        internal static List<T> Fitting<T>(IBundledMod mod, IEnumerable<T> blocks) where T : ValueBlock
        {
            List<T> fitting = new List<T>();
            List<T> forBuild = null;
            string build = null;
            foreach (T block in blocks)
            {
                if (block.ForEveryBuild)
                {
                    fitting.Add(block);
                    continue;
                }
                // Asked once, and only where a block needs it: a behaviour may look
                // for a type to tell the build.
                if (forBuild == null)
                {
                    forBuild = new List<T>();
                    build = mod.Build;
                }
                if (block.Build == build) forBuild.Add(block);
            }
            if (forBuild != null) fitting.AddRange(forBuild);
            return fitting;
        }

        private static Dictionary<string, Dictionary<string, string>> Read(List<string> problems)
        {
            List<IBundledMod> installed = BundledSettings.Installed();
            if (!BundledSettings.Built)
            {
                problems.Add("The game is still loading -- no defaults yet. Read them from the main menu on.");
                return new Dictionary<string, Dictionary<string, string>>();
            }
            return Select(installed, problems);
        }

        // By mod id, the defaults of the installed registered mods given. The check
        // outside the game selects the same way, from the mods it builds.
        internal static Dictionary<string, Dictionary<string, string>> Select(IEnumerable<IBundledMod> mods,
                                                                              List<string> problems)
        {
            Dictionary<string, Dictionary<string, string>> selected = new Dictionary<string, Dictionary<string, string>>();
            foreach (IBundledMod mod in mods)
            {
                RegisteredMod registered = mod as RegisteredMod;
                if (registered == null || !registered.IsInstalled) continue;
                ModRegistration registration = registered.Registration;

                // Each value with the version it was read from -- its block's, or the
                // registration's: a version is reported only where a setting this build
                // has took a value from it.
                Dictionary<string, string> values = new Dictionary<string, string>();
                Dictionary<string, string> readFrom = new Dictionary<string, string>();
                foreach (SettingRegistration setting in registration.Settings)
                {
                    if (setting.Default == null) continue;
                    values[setting.Name] = setting.Default;
                    readFrom[setting.Name] = registration.Version;
                }
                foreach (DefaultsRegistration block in Fitting(mod, registration.Defaults))
                {
                    foreach (KeyValuePair<string, string> pair in block.Values)
                    {
                        values[pair.Key] = pair.Value;
                        readFrom[pair.Key] = block.Version ?? registration.Version;
                    }
                }
                // Over the registration's: what the behaviour works out in the game.
                ModBehaviour behaviour = registered.MainBehaviour;
                if (behaviour != null)
                {
                    foreach (string name in new List<string>(values.Keys))
                    {
                        string own = null;
                        try
                        {
                            own = behaviour.Default(registered, name);
                        }
                        catch (Exception)
                        {
                        }
                        if (own != null) values[name] = own;
                    }
                }
                foreach (KeyValuePair<string, string> pair in readFrom)
                    if (registered.Has(pair.Key)) Version(mod, pair.Value, problems);
                selected[mod.Id] = values;
            }
            return selected;
        }

        private static void Version(IBundledMod mod, string version, List<string> problems)
        {
            if (string.IsNullOrEmpty(version) || !Known(mod.Version) || version == mod.Version) return;
            string problem = "Defaults for " + mod.ModName + " were read from version " + version + ", and " + mod.Version
                             + " is installed -- used as they are.";
            if (!problems.Contains(problem)) problems.Add(problem);
        }

        // An assembly without a version of its own reads 0.0.0.0.
        private static bool Known(string version)
        {
            return !string.IsNullOrEmpty(version) && version != "0.0.0.0";
        }
    }
}
