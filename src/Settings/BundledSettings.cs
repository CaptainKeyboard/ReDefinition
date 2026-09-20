using System.Collections.Generic;
using System;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Settings
{
    // The bundled mods and their settings as the rest of ReDefinition reaches them
    // (docs/development/architecture.md): the mods, built from their registrations,
    // and the store (BundledStore) with the game as its host -- bundled.cfg in
    // GameData/ReDefinition/PluginData, KSP's loaded save, what installed mods
    // require, and ReDefinition's log.
    internal static class BundledSettings
    {
        private const string FileName = "bundled.cfg";

        private static readonly BundledStore store = new BundledStore(new GameHost());

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

        public static string Current(BundledSetting setting)
        {
            return store.Current(setting);
        }

        internal static string BeforeReDefinition(BundledSetting setting)
        {
            return store.BeforeReDefinition(setting);
        }

        internal static bool Holds(BundledSetting setting, string value)
        {
            return store.Holds(setting, value);
        }

        public static void Set(BundledSetting setting, string value, bool save)
        {
            store.Set(setting, value, save);
        }

        public static void Release(BundledSetting setting)
        {
            store.Release(setting);
        }

        internal static void ResetTo(BundledSetting setting, string value)
        {
            store.ResetTo(setting, value);
        }

        internal static bool Correct(BundledSetting setting, string value)
        {
            return store.Correct(setting, value);
        }

        internal static bool TakeFromMod(BundledSetting setting, string value, bool save = true)
        {
            return store.TakeFromMod(setting, value, save);
        }

        internal static void SaveIfDirty()
        {
            store.SaveIfDirty();
        }

        public static void SaveNow()
        {
            store.SaveNow();
        }

        public static void ReapplyStored(string when, IBundledMod only = null, bool sceneChange = false)
        {
            store.ReapplyStored(when, only, sceneChange);
        }

        public static void ReapplyWaiting(string when)
        {
            store.ReapplyWaiting(when);
        }

        public static void RestoreBackup()
        {
            store.RestoreBackup();
        }

        public static void SetEnabled(bool on)
        {
            store.SetEnabled(on);
        }

        public static void NoteProfileApplied()
        {
            store.NoteProfileApplied();
        }

        public static List<string> ModsWithoutTheProfile()
        {
            return store.ModsWithoutTheProfile();
        }

        public static bool HidesButton(string modId)
        {
            return store.HidesButton(modId);
        }

        public static void SetHidesButton(string modId, bool hide)
        {
            store.SetHidesButton(modId, hide);
        }

        public static List<IBundledMod> NotYetAsked()
        {
            return store.NotYetAsked();
        }

        public static void MarkAsked(IEnumerable<IBundledMod> list)
        {
            store.MarkAsked(list);
        }

        internal static string SafeRead(BundledSetting setting)
        {
            return store.SafeRead(setting);
        }

        // The game as the store's host.
        private sealed class GameHost : IStoreHost
        {
            public void Read(BundledLedger ledger, BundledState state)
            {
                BundledFile.Read(Path, ledger, state, this);
            }

            public bool Write(BundledLedger ledger, BundledState state)
            {
                return BundledFile.Write(Path, ledger, state, this);
            }

            public BundledSetting Find(string key)
            {
                return BundledSettings.Find(key);
            }

            public IList<IBundledMod> Installed()
            {
                return BundledSettings.Installed();
            }

            public string LoadedSave()
            {
                return RegisteredMod.LoadedSave();
            }

            public string Adjust(BundledSetting setting, string value)
            {
                return Requirements.Adjust(setting, value);
            }

            public bool Allows(BundledSetting setting, string value)
            {
                return Requirements.Allows(setting, value);
            }

            public void Tell(BundledSetting setting, string value)
            {
                Requirements.Tell(setting, value);
            }

            public void Info(string message)
            {
                Debug.Log(Log.Tag + " " + message);
            }

            public void Warning(string message)
            {
                Debug.LogWarning(Log.Tag + " " + message);
            }

            public void Warn(string kind, string message)
            {
                CompatibilityLog.Warn(kind, message);
            }
        }
    }
}
