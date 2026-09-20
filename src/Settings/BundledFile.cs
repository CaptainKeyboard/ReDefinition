using System;
using System.Collections.Generic;
using System.IO;

namespace ReDefinition.Settings
{
    // The switch, the mods the main-menu notice asked about, and the profile last
    // chosen -- what bundled.cfg keeps beside the ledger.
    internal sealed class BundledState
    {
        // Whether the other mods' settings are bundled here and their buttons
        // hidden. On until the player chooses otherwise.
        public bool Enabled = true;
        public readonly List<string> Asked = new List<string>();
        public string ProfileName = "";

        // The mods whose toolbar button stays where it is, by id. Everything else
        // with a button ReDefinition can open its window through is hidden while
        // the bundling is on, so a mod installed later follows the rule rather
        // than an old file.
        public readonly List<string> ButtonKept = new List<string>();
    }

    // What the store needs of the game (docs/development/architecture.md):
    // bundled.cfg, the mods, the save loaded, what installed mods require, and
    // ReDefinition's log -- the game's own in BundledSettings, a stand-in in the
    // tests.
    internal interface IStoreHost
    {
        // bundled.cfg, read into the ledger and the state once, and written whole;
        // false where the writing failed.
        void Read(BundledLedger ledger, BundledState state);
        bool Write(BundledLedger ledger, BundledState state);

        // An installed mod's setting by its key; null where there is none.
        BundledSetting Find(string key);
        IList<IBundledMod> Installed();

        // The save a per-save mod keeps its values for now; empty outside a save.
        string LoadedSave();

        // What installed mods require of a setting (Requirements).
        string Adjust(BundledSetting setting, string value);
        bool Allows(BundledSetting setting, string value);
        void Tell(BundledSetting setting, string value);

        // A line in the log, a warning, and a warning of a kind said a few times
        // at most (CompatibilityLog).
        void Info(string message);
        void Warning(string message);
        void Warn(string kind, string message);
    }

    // ReDefinition's file GameData/ReDefinition/PluginData/bundled.cfg: the state,
    // the values kept here, and the values from before ReDefinition with their marks. The part of
    // the store that touches KSP's ConfigNode and the disk.
    internal static class BundledFile
    {
        private const string RootName = "REDEFINITION_BUNDLED";
        private const string ValuesName = "VALUES";
        private const string BackupName = "BEFORE_REDEFINITION";
        private const string SaveName = "SAVE";
        private const string RestoreName = "restoring";

        // A file that cannot be read is set aside, not written over: what it held
        // stays there for a look, and the main-menu notice asks again.
        internal static void Read(string path, BundledLedger ledger, BundledState state, IStoreHost host)
        {
            if (!File.Exists(path)) return;

            ConfigNode node = null;
            try
            {
                ConfigNode root = ConfigNode.Load(path);
                node = root != null ? root.GetNode(RootName) : null;
            }
            catch (Exception)
            {
                node = null;
            }

            if (node == null)
            {
                SetAside(path, host);
                return;
            }

            try
            {
                bool on;
                if (bool.TryParse(node.GetValue("enabled"), out on)) state.Enabled = on;

                string list = node.GetValue("asked");
                if (!string.IsNullOrEmpty(list))
                {
                    foreach (string id in list.Split(','))
                        if (id.Trim().Length > 0 && !state.Asked.Contains(id.Trim())) state.Asked.Add(id.Trim());
                }

                state.ProfileName = node.GetValue("profile") ?? "";

                string kept = node.GetValue("buttonsKept");
                if (!string.IsNullOrEmpty(kept))
                {
                    foreach (string id in kept.Split(','))
                        if (id.Trim().Length > 0 && !state.ButtonKept.Contains(id.Trim()))
                            state.ButtonKept.Add(id.Trim());
                }

                // With the bundling off no value is kept to hand over.
                ConfigNode values = state.Enabled ? node.GetNode(ValuesName) : null;
                if (values != null)
                {
                    foreach (ConfigNode.Value value in values.values) ledger.Keep(value.name, value.value);
                }

                // Kept whatever the switch says: it is the way back.
                ConfigNode before = node.GetNode(BackupName);
                if (before != null)
                {
                    ReadBackups(ledger, "", before);
                    foreach (ConfigNode save in before.GetNodes(SaveName))
                    {
                        string name = save.GetValue("name");
                        if (!string.IsNullOrEmpty(name)) ReadBackups(ledger, name, save);
                    }
                }
            }
            catch (Exception e)
            {
                host.Warning("Bundled settings read only in part from " + path + ": " + e.Message);
            }
            ledger.Dirty = false;
        }

        internal static bool Write(string path, BundledLedger ledger, BundledState state, IStoreHost host)
        {
            try
            {
                ConfigNode node = new ConfigNode(RootName);
                node.AddValue("enabled", state.Enabled);
                node.AddValue("asked", string.Join(",", state.Asked.ToArray()));
                node.AddValue("profile", state.ProfileName);
                node.AddValue("buttonsKept", string.Join(",", state.ButtonKept.ToArray()));
                ConfigNode values = node.AddNode(ValuesName);
                foreach (KeyValuePair<string, string> pair in ledger.StoredCopy())
                    values.AddValue(pair.Key, pair.Value);

                ConfigNode before = node.AddNode(BackupName);
                Dictionary<string, BundledLedger.Backup> global = ledger.BackupsIn("");
                if (global != null) WriteBackups(before, global);
                foreach (string context in ledger.Contexts())
                {
                    if (context.Length == 0) continue;
                    ConfigNode save = before.AddNode(SaveName);
                    save.AddValue("name", context);
                    WriteBackups(save, ledger.BackupsIn(context));
                }

                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    Directory.CreateDirectory(directory);

                ConfigNode root = new ConfigNode();
                root.AddNode(node);
                if (!root.Save(path)) throw new IOException("KSP's ConfigNode.Save did not write it");
                return true;
            }
            catch (Exception e)
            {
                host.Warn("bundled-file", "Bundled settings could not be saved to " + path + ": " + e.Message
                                          + " -- tried again whenever a value is to be handed over or saved, and no"
                                          + " mod that saves gets a value whose value from before is not saved.");
                return false;
            }
        }

        private static void ReadBackups(BundledLedger ledger, string context, ConfigNode node)
        {
            foreach (ConfigNode.Value value in node.values)
                if (value.name != "name" && value.name != RestoreName) ledger.SetBackup(context, value.name, value.value);

            string keys = node.GetValue(RestoreName);
            if (string.IsNullOrEmpty(keys)) return;
            foreach (string key in keys.Split(','))
            {
                BundledLedger.Backup backup;
                if (ledger.TryBackup(context, key.Trim(), out backup)) backup.Restore = true;
            }
        }

        private static void WriteBackups(ConfigNode node, Dictionary<string, BundledLedger.Backup> values)
        {
            List<string> restore = new List<string>();
            foreach (KeyValuePair<string, BundledLedger.Backup> pair in values)
            {
                node.AddValue(pair.Key, pair.Value.Value);
                if (pair.Value.Restore) restore.Add(pair.Key);
            }
            if (restore.Count > 0) node.AddValue(RestoreName, string.Join(",", restore.ToArray()));
        }

        private static void SetAside(string path, IStoreHost host)
        {
            string aside = path + ".unreadable";
            try
            {
                if (File.Exists(aside)) File.Delete(aside);
                File.Move(path, aside);
                host.Warning(path + " could not be read; it is kept as " + aside + ", and the bundled settings start afresh.");
            }
            catch (Exception e)
            {
                host.Warning(path + " could not be read, nor set aside (" + e.Message + "); the bundled settings start afresh.");
            }
        }
    }
}
