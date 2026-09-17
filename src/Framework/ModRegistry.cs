using System;
using System.Collections;
using System.Collections.Generic;

namespace ReDefinition.Framework
{
    // The registrations of the mods ReDefinition bundles, from the GameDatabase --
    // its own in GameData/ReDefinition/Mods and any another mod or pack ships -- and
    // the mods built from them.
    internal static class ModRegistry
    {
        // Whether the GameDatabase holds the registrations with ModuleManager's
        // patches applied. GameDatabase.IsReady alone is not that: ModuleManager
        // puts its PostPatchLoader right after the GameDatabase among the loading
        // screen's loaders and patches once the database has loaded, and its own
        // reload waits for the GameDatabase, the PostPatchLoader and PartLoader in
        // that order (ModuleManager 4.2.3, decompiled: ModuleManager.Awake and
        // DataBaseReloadWithMM). PartLoader ready is after both, with
        // ModuleManager or without it.
        public static bool Ready()
        {
            return GameDatabase.Instance != null && GameDatabase.Instance.IsReady()
                   && PartLoader.Instance != null && PartLoader.Instance.IsReady();
        }

        public static List<ModRegistration> LoadAll(List<string> problems)
        {
            if (!Ready())
            {
                problems.Add("The game is still loading -- no mod registrations yet.");
                return new List<ModRegistration>();
            }
            List<KeyValuePair<ConfigNode, string>> sources = new List<KeyValuePair<ConfigNode, string>>();
            foreach (UrlDir.UrlConfig config in GameDatabase.Instance.GetConfigs(ModRegistration.NodeName))
                sources.Add(new KeyValuePair<ConfigNode, string>(config.config, config.parent != null ? config.parent.url : ""));
            List<string> notes = new List<string>();
            List<ModRegistration> read = ReadSources(sources, problems, notes);
            foreach (string note in notes) UnityEngine.Debug.Log(UpscalerProbe.Tag + " " + note);
            return read;
        }

        // Whether a file's place in GameData, as KSP's GameDatabase names it
        // ("ReDefinition/Mods/KSP"), is ReDefinition's own folder.
        internal static bool IsOwn(string url)
        {
            return url == null || url.StartsWith("ReDefinition/", StringComparison.OrdinalIgnoreCase);
        }

        // The registrations in the nodes given, all counted as ReDefinition's own:
        // the check outside the game reads its files this way.
        internal static List<ModRegistration> Read(IEnumerable nodes, List<string> problems)
        {
            List<KeyValuePair<ConfigNode, string>> sources = new List<KeyValuePair<ConfigNode, string>>();
            foreach (object item in nodes) sources.Add(new KeyValuePair<ConfigNode, string>(item as ConfigNode, null));
            return ReadSources(sources, problems, null);
        }

        // Each node with the place of its file in GameData. A registration from
        // anywhere else -- the mod's own folder, a visual pack -- counts over
        // ReDefinition's of the same name, whatever order KSP read their folders in,
        // with a note saying which file counts. Of two alike, the later counts,
        // reported: a pack meant to change one uses ModuleManager's @.
        internal static List<ModRegistration> ReadSources(IEnumerable<KeyValuePair<ConfigNode, string>> sources,
                                                          List<string> problems, List<string> notes)
        {
            List<ModRegistration> registrations = new List<ModRegistration>();
            List<bool> own = new List<bool>();
            Dictionary<string, int> byName = new Dictionary<string, int>();
            foreach (KeyValuePair<ConfigNode, string> source in sources)
            {
                ModRegistration registration = ModRegistration.FromConfigNode(source.Key, problems);
                if (registration == null) continue;
                bool isOwn = IsOwn(source.Value);
                int index;
                if (byName.TryGetValue(registration.Name, out index))
                {
                    if (own[index] != isOwn)
                    {
                        if (notes != null)
                            notes.Add("Mod '" + registration.Name + "': the registration in "
                                      + (isOwn ? "another mod's folder" : source.Value) + " counts over ReDefinition's own.");
                        if (isOwn) continue;
                    }
                    else
                    {
                        problems.Add("Mod '" + registration.Name + "' registered twice -- the later one counts. A patch"
                                     + " meant to change it should edit it with @.");
                    }
                    registrations[index] = registration;
                    own[index] = isOwn;
                    continue;
                }
                byName[registration.Name] = registrations.Count;
                registrations.Add(registration);
                own.Add(isOwn);
            }
            return registrations;
        }

        // A mod for every registration, by title, then name; one whose detect type is
        // not loaded is there, not installed. One that cannot be built is reported and
        // left out, and the others are built.
        //
        // By title as the player reads it, a localization tag in their language: the
        // list of bundled mods, the main-menu notice, the Advanced buttons and rows of
        // the same order stand the same wherever a registration's file is -- the
        // GameDatabase holds them in the order of their folders and files.
        //
        // What one mod holds of another's settings is left out, once all are built,
        // while the other is loaded -- bundled or not: its own code holds them. Firefly
        // sets KSP's aerodynamic FX to its lowest as it loads and warns whenever it is
        // raised (AssetLoader, EventManager.OnGameSettingsApplied; its source), whether
        // ReDefinition bundles it or not.
        internal static List<IBundledMod> Build(IList<ModRegistration> registrations, List<string> problems)
        {
            List<RegisteredMod> built = new List<RegisteredMod>();
            foreach (ModRegistration registration in registrations)
            {
                try
                {
                    built.Add(new RegisteredMod(registration, problems));
                }
                catch (Exception e)
                {
                    problems.Add("Mod '" + registration.Name + "' could not be built (" + Reason(e) + ") -- not bundled.");
                }
            }
            built.Sort((a, b) =>
            {
                int byTitle = string.Compare(a.ModName, b.ModName, StringComparison.OrdinalIgnoreCase);
                return byTitle != 0 ? byTitle : string.CompareOrdinal(a.Id, b.Id);
            });

            HashSet<string> loaded = new HashSet<string>();
            foreach (RegisteredMod mod in built)
                if (mod.AssemblyLoaded) loaded.Add(mod.Id);
            foreach (RegisteredMod mod in built) mod.LeaveOutHeld(loaded.Contains);

            List<IBundledMod> mods = new List<IBundledMod>();
            foreach (RegisteredMod mod in built) mods.Add(mod);
            return mods;
        }

        // Without CompatibilityLog, which the check outside the game cannot call.
        private static string Reason(Exception e)
        {
            Exception inner = e;
            while (inner.InnerException != null) inner = inner.InnerException;
            return inner.GetType().Name + ": " + inner.Message;
        }
    }
}
