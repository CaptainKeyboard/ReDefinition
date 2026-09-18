using System;
using System.Collections.Generic;

namespace ReDefinition.Settings
{
    // What the store keeps (docs/development/architecture.md): the values kept for
    // the mods, the values from before ReDefinition per save with their marks to be
    // put back, what waits for a mod's save -- and whether any of it changed since
    // bundled.cfg was written. Plain data: no mod is reached from here (BundledStore).
    internal sealed class BundledLedger
    {
        // A value from before ReDefinition, and whether it is to be put back.
        internal sealed class Backup
        {
            public string Value;
            public bool Restore;
            // The scene load in which it was last tried -- written, or refused, or
            // waiting for its mod's save: tried again in the next scene or at a
            // restore, not on every tick. Not saved.
            public int TriedAt = -1;
        }

        // The values kept for the mods, by key.
        private readonly Dictionary<string, string> stored = new Dictionary<string, string>();
        // Per save a per-save setting keeps values for -- "" for everything else
        // -- the values from before ReDefinition, by key.
        private readonly Dictionary<string, Dictionary<string, Backup>> backups =
            new Dictionary<string, Dictionary<string, Backup>>();
        private readonly HashSet<IBundledMod> unsaved = new HashSet<IBundledMod>();
        // What becomes true only once a mod has saved.
        private readonly Dictionary<IBundledMod, List<Action>> afterSave = new Dictionary<IBundledMod, List<Action>>();
        // Counts the changes to what is kept, for what is asked every frame or
        // tick.
        private int version;
        private int canRestoreVersion = -1;
        private bool canRestore;
        private int markedVersion = -1;
        private bool anyMarked;

        // bundled.cfg differs from what is kept here.
        public bool Dirty { get; set; }

        public bool Contains(string key)
        {
            return stored.ContainsKey(key);
        }

        public bool TryStored(string key, out string value)
        {
            return stored.TryGetValue(key, out value);
        }

        // A copy, in the order they were kept: what is walked over may change.
        public List<KeyValuePair<string, string>> StoredCopy()
        {
            return new List<KeyValuePair<string, string>>(stored);
        }

        public void Keep(string key, string value)
        {
            stored[key] = value;
            Changed();
        }

        public bool Forget(string key)
        {
            if (!stored.Remove(key)) return false;
            Changed();
            return true;
        }

        public void ForgetAll()
        {
            if (stored.Count == 0) return;
            stored.Clear();
            Changed();
        }

        // Whether Restore has anything to do: a value kept for a mod, or one from
        // before ReDefinition not marked to be put back. One that waits for a mod
        // not installed or a save not loaded again does not keep the button
        // enabled, and one put back is no longer kept.
        public bool CanRestore
        {
            get
            {
                if (canRestoreVersion != version)
                {
                    canRestoreVersion = version;
                    canRestore = stored.Count > 0 || AnyBackup(false);
                }
                return canRestore;
            }
        }

        public bool TryBackup(string context, string key, out Backup backup)
        {
            Dictionary<string, Backup> values;
            backup = null;
            return backups.TryGetValue(context, out values) && values.TryGetValue(key, out backup);
        }

        public void SetBackup(string context, string key, string value)
        {
            Dictionary<string, Backup> values;
            if (!backups.TryGetValue(context, out values)) backups[context] = values = new Dictionary<string, Backup>();
            values[key] = new Backup { Value = value };
            Changed();
        }

        public void RemoveBackup(string context, string key)
        {
            Dictionary<string, Backup> values;
            if (!backups.TryGetValue(context, out values) || !values.Remove(key)) return;
            if (values.Count == 0) backups.Remove(context);
            Changed();
        }

        // Every value from before kept for `key`, in every save.
        public void MarkRestore(string key, bool restore)
        {
            foreach (Dictionary<string, Backup> values in backups.Values)
            {
                Backup backup;
                if (!values.TryGetValue(key, out backup) || backup.Restore == restore) continue;
                backup.Restore = restore;
                Changed();
            }
        }

        // Every value from before, in every save, to be put back.
        public void MarkAll()
        {
            foreach (Dictionary<string, Backup> values in backups.Values)
            {
                foreach (Backup backup in values.Values)
                {
                    if (backup.Restore) continue;
                    backup.Restore = true;
                    Changed();
                }
            }
        }

        public bool AnyBackup(bool marked)
        {
            foreach (Dictionary<string, Backup> values in backups.Values)
            {
                foreach (Backup backup in values.Values)
                    if (backup.Restore == marked) return true;
            }
            return false;
        }

        public bool AnyMarked()
        {
            if (markedVersion != version)
            {
                markedVersion = version;
                anyMarked = AnyBackup(true);
            }
            return anyMarked;
        }

        // The keys marked to be put back in a save -- "" for the game as a whole.
        public void AddMarked(List<KeyValuePair<string, string>> due, string context)
        {
            Dictionary<string, Backup> values;
            if (!backups.TryGetValue(context, out values)) return;
            foreach (KeyValuePair<string, Backup> entry in values)
                if (entry.Value.Restore) due.Add(new KeyValuePair<string, string>(context, entry.Key));
        }

        // Every key marked to be put back, once for each save it is kept for.
        public List<string> MarkedKeys()
        {
            List<string> keys = new List<string>();
            foreach (Dictionary<string, Backup> values in backups.Values)
            {
                foreach (KeyValuePair<string, Backup> entry in values)
                    if (entry.Value.Restore) keys.Add(entry.Key);
            }
            return keys;
        }

        // The saves values from before are kept for, "" among them where any.
        public List<string> Contexts()
        {
            return new List<string>(backups.Keys);
        }

        public Dictionary<string, Backup> BackupsIn(string context)
        {
            Dictionary<string, Backup> values;
            return backups.TryGetValue(context, out values) ? values : null;
        }

        // A mod with changes not saved yet.
        public void Unsaved(IBundledMod mod)
        {
            unsaved.Add(mod);
        }

        public void AfterSave(IBundledMod mod, Action action)
        {
            List<Action> actions;
            if (!afterSave.TryGetValue(mod, out actions)) afterSave[mod] = actions = new List<Action>();
            actions.Add(action);
        }

        public List<IBundledMod> UnsavedMods()
        {
            return new List<IBundledMod>(unsaved);
        }

        public void Saved(IBundledMod mod)
        {
            unsaved.Remove(mod);
        }

        // What waited for a save that failed: asked for again at the next scene
        // change.
        public void DropAfterSave(IBundledMod mod)
        {
            afterSave.Remove(mod);
        }

        // What waited for the mod's save, taken; null where nothing did.
        public List<Action> TakeAfterSave(IBundledMod mod)
        {
            List<Action> actions;
            if (!afterSave.TryGetValue(mod, out actions)) return null;
            afterSave.Remove(mod);
            return actions;
        }

        private void Changed()
        {
            Dirty = true;
            version++;
        }
    }
}
