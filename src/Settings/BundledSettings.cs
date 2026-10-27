using System.Collections.Generic;
using System;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Settings
{
    // The bundled mods and their settings as the rest of ReDefinition reaches them
    // (docs/development/architecture.md): the mods, built from their registrations,
    // and the store (BundledStore) with the game as its host.
    // The game supplies bundled.cfg in GameData/ReDefinition/PluginData, KSP's loaded save, what installed mods
    // require, and ReDefinition's log.
    internal static class BundledSettings
    {
        // ReDefinition's file in its PluginData.
        private const string FileName = "bundled.cfg";

        // The store, with the game as its host.
        private static readonly BundledStore store = new BundledStore(new GameHost());

        // The mods once built (Mods), and their installed settings by key once asked for (Find).
        private static List<IBundledMod> mods;
        private static Dictionary<string, BundledSetting> byKey;

        // Whether the mods are there, all of them, without building them: what
        // only asks must not start the building.
        private static bool built;
        private static bool building;
        private static readonly IList<IBundledMod> none = new List<IBundledMod>().AsReadOnly();
        private static readonly List<string> registrationProblems = new List<string>();

        // A scene has loaded: what was tried in the scene before is tried again
        // in this one.
        internal static void NoteSceneLoaded()
        {
            store.NoteSceneLoaded();
        }

        // The graphics profile last chosen, by name (BundledStore.ProfileName).
        internal static string ProfileName
        {
            get { return store.ProfileName; }
            set { store.ProfileName = value; }
        }

        // Whether a graphics profile is chosen: only then is ReDefinition active --
        // the upscaler, frame generation, what is taken from other mods for the upscaler.
        internal static bool ProfileChosen
        {
            get { return !string.IsNullOrEmpty(store.ProfileName); }
        }

        // bundled.cfg's full path.
        public static string Path
        {
            get { return PluginData.Path(FileName); }
        }

        // Whether the other mods' settings are bundled here and their buttons
        // hidden. On until the player chooses otherwise -- in the main-menu
        // notice or under "Mods and toolbar" in the window.
        public static bool Enabled
        {
            get { return store.Enabled; }
        }

        // Whether the main-menu notice has had an answer before.
        public static bool AnyAsked
        {
            get { return store.AnyAsked; }
        }

        // Whether Restore has anything to do (BundledLedger.CanRestore).
        public static bool CanRestore
        {
            get { return store.CanRestore; }
        }

        // Whether the mods have been built from their registrations.
        internal static bool Built
        {
            get { return built; }
        }

        // What was wrong in the registrations and in building the mods from them --
        // in the log as well.
        internal static IList<string> RegistrationProblems
        {
            get { return registrationProblems; }
        }

        // The mods, built once from their registrations (docs/modders/registering-a-mod.md)
        // as the GameDatabase holds them with ModuleManager's patches, in its order.
        // Until then none, and nothing is kept of that (ModRegistry.Ready); asked
        // again while they are being built, none either.
        public static IList<IBundledMod> Mods
        {
            get
            {
                if (mods != null) return mods;
                if (building || !ModRegistry.Ready()) return none;
                building = true;
                try
                {
                    List<string> problems = new List<string>();
                    List<IBundledMod> list;
                    try
                    {
                        list = ModRegistry.Build(ModRegistry.LoadAll(problems), problems);
                    }
                    catch (Exception e)
                    {
                        list = new List<IBundledMod>();
                        problems.Add("The mod registrations could not be read (" + CompatibilityLog.Reason(e) + ").");
                    }
                    mods = list;
                    built = true;
                    registrationProblems.AddRange(problems);
                    if (problems.Count > 0)
                        Debug.LogWarning(Log.Tag + " Mod registrations:\n  " + string.Join("\n  ", problems.ToArray()));
                    foreach (IBundledMod mod in mods) Report(mod);
                }
                finally
                {
                    building = false;
                }
                return mods;
            }
        }

        // Its hooks where it is bundled; what it leaves out logged whether or not
        // it is: where a missing member takes the whole mod with it, the name of
        // that member is what says why.
        private static void Report(IBundledMod mod)
        {
            if (mod.IsInstalled) InstallHooks(mod);
            if (mod.DroppedMembers.Count > 0)
                Debug.Log(Log.Tag + " " + mod.ModName
                          + (mod.IsInstalled ? ": not bundled here -- " : ": not bundled at all -- ")
                          + string.Join("; ", new List<string>(mod.DroppedMembers).ToArray()) + ".");
        }

        // The mod's hooks on its own window and save; a failure is a warning, the mod stays bundled.
        private static void InstallHooks(IBundledMod mod)
        {
            try
            {
                mod.InstallHooks();
            }
            catch (Exception e)
            {
                CompatibilityLog.Warn("bundled-hooks-" + mod.Id, mod.ModName + ": its hooks could not be installed, so its"
                                      + " own window or a save it loads can differ from ReDefinition's window until the"
                                      + " next scene change (" + CompatibilityLog.Reason(e) + ").");
            }
        }

        // The mods that are installed, in the registrations' order.
        public static List<IBundledMod> Installed()
        {
            List<IBundledMod> list = new List<IBundledMod>();
            foreach (IBundledMod mod in Mods)
                if (mod.IsInstalled) list.Add(mod);
            return list;
        }

        // An installed mod's setting by its key.
        internal static BundledSetting Find(string key)
        {
            if (byKey == null)
            {
                IList<IBundledMod> all = Mods;
                if (!built) return null;
                Dictionary<string, BundledSetting> index = new Dictionary<string, BundledSetting>();
                foreach (IBundledMod mod in all)
                {
                    if (!mod.IsInstalled) continue;
                    foreach (BundledSetting setting in mod.Settings) index[setting.Key] = setting;
                }
                byKey = index;
            }
            BundledSetting found;
            return key != null && byKey.TryGetValue(key, out found) ? found : null;
        }

        // The value the window starts from (BundledStore.Current).
        public static string Current(BundledSetting setting)
        {
            return store.Current(setting);
        }

        // What a setting had before ReDefinition first changed it (BundledStore.BeforeReDefinition).
        internal static string BeforeReDefinition(BundledSetting setting)
        {
            return store.BeforeReDefinition(setting);
        }

        // Whether the mod holding the value is all a choice of it needs (BundledStore.Holds).
        internal static bool Holds(BundledSetting setting, string value)
        {
            return store.Holds(setting, value);
        }

        // The player's change, from the window or a profile (BundledStore.Set).
        public static void Set(BundledSetting setting, string value, bool save)
        {
            store.Set(setting, value, save);
        }

        // The values from before of a batch of Set calls, written to bundled.cfg once (BundledStore.Prepare);
        // EndPrepare closes the batch.
        internal static void Prepare(IEnumerable<KeyValuePair<BundledSetting, string>> coming)
        {
            store.Prepare(coming);
        }

        // The batch is over (BundledStore.EndPrepare).
        internal static void EndPrepare()
        {
            store.EndPrepare();
        }

        // A setting the new profile leaves alone goes back to what it had (BundledStore.Release).
        public static void Release(BundledSetting setting)
        {
            store.Release(setting);
        }

        // The value Reset to defaults gives a setting (BundledStore.ResetTo).
        internal static void ResetTo(BundledSetting setting, string value)
        {
            store.ResetTo(setting, value);
        }

        // The values settings had before something outside the store changed them (BundledStore.KeepBefore).
        internal static bool KeepBefore(IEnumerable<KeyValuePair<BundledSetting, string>> coming)
        {
            return store.KeepBefore(coming);
        }

        // A value a requirement puts right (BundledStore.Correct).
        internal static bool Correct(BundledSetting setting, string value)
        {
            return store.Correct(setting, value);
        }

        // A change made in a per-save mod's own window, followed here (BundledStore.TakeFromMod).
        internal static bool TakeFromMod(BundledSetting setting, string value, bool save = true)
        {
            return store.TakeFromMod(setting, value, save);
        }

        // bundled.cfg written where something changed since.
        internal static void SaveIfDirty()
        {
            store.SaveIfDirty();
        }

        // bundled.cfg and every mod with changes not yet saved (BundledStore.SaveNow).
        public static void SaveNow()
        {
            store.SaveNow();
        }

        // As SaveNow, with the mods the window wrote directly saved too.
        internal static void SaveNow(IEnumerable<IBundledMod> also)
        {
            store.SaveNow(also);
        }

        // What is kept here, to the mods where it is not there yet (BundledStore.ReapplyStored).
        public static void ReapplyStored(string when, IBundledMod only = null, bool sceneChange = false)
        {
            store.ReapplyStored(when, only, sceneChange);
        }

        // The mods a value could not reach before, asked again (BundledStore.ReapplyWaiting).
        public static void ReapplyWaiting(string when)
        {
            store.ReapplyWaiting(when);
        }

        // Every setting back to what it had before ReDefinition (BundledStore.RestoreBackup).
        public static void RestoreBackup()
        {
            store.RestoreBackup();
        }

        // The bundling switched on or off (BundledStore.SetEnabled).
        public static void SetEnabled(bool on)
        {
            store.SetEnabled(on);
        }

        // The mods the chosen profile was applied to noted, with their builds (BundledStore.NoteProfileApplied).
        public static void NoteProfileApplied()
        {
            store.NoteProfileApplied();
        }

        // The mods that have not had the chosen profile (BundledStore.ModsWithoutTheProfile).
        public static List<string> ModsWithoutTheProfile()
        {
            return store.ModsWithoutTheProfile();
        }

        // Whether a mod's toolbar button is hidden (BundledStore.HidesButton).
        public static bool HidesButton(string modId)
        {
            return store.HidesButton(modId);
        }

        // A mod's toolbar button hidden, or kept by the player.
        public static void SetHidesButton(string modId, bool hide)
        {
            store.SetHidesButton(modId, hide);
        }

        // Installed mods the main-menu notice has not asked about.
        public static List<IBundledMod> NotYetAsked()
        {
            return store.NotYetAsked();
        }

        // The mods as asked about by the main-menu notice.
        public static void MarkAsked(IEnumerable<IBundledMod> list)
        {
            store.MarkAsked(list);
        }

        // The mod's value now; null, with a warning, where reading it threw.
        internal static string SafeRead(BundledSetting setting)
        {
            return store.SafeRead(setting);
        }

        // The game as the store's host.
        private sealed class GameHost : IStoreHost
        {
            // Fills the ledger and the state from bundled.cfg in ReDefinition's PluginData (BundledFile.Read).
            public void Read(BundledLedger ledger, BundledState state)
            {
                BundledFile.Read(Path, ledger, state, this);
            }

            // The ledger and the state into bundled.cfg; false where that failed.
            public bool Write(BundledLedger ledger, BundledState state)
            {
                return BundledFile.Write(Path, ledger, state, this);
            }

            // An installed mod's setting by key (BundledSettings.Find).
            public BundledSetting Find(string key)
            {
                return BundledSettings.Find(key);
            }

            // The installed mods (BundledSettings.Installed).
            public IList<IBundledMod> Installed()
            {
                return BundledSettings.Installed();
            }

            // The save loaded now, as RegisteredMod tells it.
            public string LoadedSave()
            {
                return RegisteredMod.LoadedSave();
            }

            // The value as installed mods' requirements allow it (Requirements.Adjust).
            public string Adjust(BundledSetting setting, string value)
            {
                return Requirements.Adjust(setting, value);
            }

            // Whether installed mods' requirements allow the value (Requirements.Allows).
            public bool Allows(BundledSetting setting, string value)
            {
                return Requirements.Allows(setting, value);
            }

            // On screen, why installed mods' requirements refuse the value (Requirements.Tell).
            public void Tell(BundledSetting setting, string value)
            {
                Requirements.Tell(setting, value);
            }

            // A line in ReDefinition's log.
            public void Info(string message)
            {
                Debug.Log(Log.Tag + " " + message);
            }

            // A warning in ReDefinition's log.
            public void Warning(string message)
            {
                Debug.LogWarning(Log.Tag + " " + message);
            }

            // A warning of a kind said a few times at most (CompatibilityLog.Warn).
            public void Warn(string kind, string message)
            {
                CompatibilityLog.Warn(kind, message);
            }
        }
    }
}
