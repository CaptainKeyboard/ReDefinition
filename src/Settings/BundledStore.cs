using System;
using System.Collections.Generic;

namespace ReDefinition.Settings
{
    // The other mods' settings as the player chose them in ReDefinition's window, and the
    // switch that bundles them here: the hand-over of the store
    // (docs/development/settings-store.md) -- what reaches the mods, and when.
    // What is kept is the ledger's (BundledLedger), bundled.cfg BundledFile's, and
    // what this needs of the game its host's (IStoreHost; the game's own in
    // BundledSettings).
    //
    // A value set here is saved as if it had been set in the mod's own window,
    // through the mod's own save routine, so that the mod's window shows what
    // ReDefinition's shows. How it lasts depends on the mod (IBundledMod.Saving):
    //   * InModFiles -- KSP, Scatterer, EVE, Parallax, Firefly: the mod keeps it.
    //     Kept here as well until the mod has saved it, or while the mod cannot
    //     take it yet, and handed over at the next chance.
    //   * PerSave -- TUFX, Distant Object: the mod keeps it per save. A choice
    //     here is for the game, not one save, so it is kept here and goes into
    //     every save as that save loads; a change made in the mod's own window
    //     to a choice kept here becomes that choice (TakeFromMod).
    //   * AtEveryStart -- Deferred, Waterfall: no save routine of their own;
    //     kept here, and set at every start and scene change.
    //
    // Before the first change of a setting through this window, the value its
    // mod had is kept, for good -- for a per-save setting, per save
    // (BundledSetting.Context). Restore, or a profile that no longer sets it
    // (Release), marks each kept value to be put back once, with the bundling
    // on or off: a value kept for the game as a whole at once, a per-save value
    // in its save the next time that save is loaded. Once it is in its mod, and
    // saved where the mod saves, it is done and no longer kept; a new choice
    // before then unmarks it. A per-save value put back is a change to its save
    // like one made in the mod's own window: TUFX keeps it in the game, which
    // KSP writes when it saves, so a quicksave made before brings back what it
    // holds; Distant Object writes the save's own file at once.
    internal sealed class BundledStore
    {
        private readonly IStoreHost host;
        private readonly BundledLedger ledger = new BundledLedger();
        private readonly BundledState state = new BundledState();
        private bool loaded;
        private int sceneLoads;
        // Mods a value could not reach because they could not take it then --
        // Trajectories before its first flight: asked again on the add-on's tick in
        // every scene until they can.
        private readonly HashSet<IBundledMod> waiting = new HashSet<IBundledMod>();

        public BundledStore(IStoreHost host)
        {
            this.host = host;
        }

        // What is kept, bundled.cfg read first.
        internal BundledLedger Ledger
        {
            get
            {
                Load();
                return ledger;
            }
        }

        // A scene has loaded: what was tried in the scene before is tried again
        // in this one.
        public void NoteSceneLoaded()
        {
            sceneLoads++;
        }

        // The graphics profile last chosen, by name; empty when none was
        // (ProfileApplier), and again once the bundling is switched off or the
        // settings are restored. Whether the values still match it is the
        // window's to tell -- a value changed after it makes it Custom. Saved by
        // the caller's SaveNow, in one write with the values it came with.
        public string ProfileName
        {
            get
            {
                Load();
                return state.ProfileName;
            }
            set
            {
                Load();
                state.ProfileName = value ?? "";
            }
        }

        public bool Enabled
        {
            get
            {
                Load();
                return state.Enabled;
            }
        }

        // Whether a mod's toolbar button is hidden: with the bundling on, and
        // unless the player kept that one button. What a mod's button is, and
        // whether there is one to hide, is the window's question (ToolbarTakeover).
        public bool HidesButton(string modId)
        {
            Load();
            return state.Enabled && !state.ButtonKept.Contains(modId);
        }

        public void SetHidesButton(string modId, bool hide)
        {
            Load();
            if (hide == !state.ButtonKept.Contains(modId)) return;
            if (hide) state.ButtonKept.Remove(modId);
            else state.ButtonKept.Add(modId);
            Save();
        }

        // Whether the main-menu notice has had an answer before.
        public bool AnyAsked
        {
            get
            {
                Load();
                return state.Asked.Count > 0;
            }
        }

        public bool CanRestore
        {
            get
            {
                Load();
                return ledger.CanRestore;
            }
        }

        // The value the window starts from: the one kept here where there is one --
        // a value waiting to be handed over or saved, or one set at every start or
        // load -- otherwise the mod's own, which for a mod that saves is the value
        // saved.
        public string Current(BundledSetting setting)
        {
            Load();
            string value;
            if (ledger.TryStored(setting.Key, out value)) return value;
            return SafeRead(setting);
        }

        // What a setting had before ReDefinition first changed it -- in the save
        // loaded now, for a per-save setting. Where there is no such value, what
        // the mod itself holds, the value kept here left out: in the main menu that is a
        // per-save mod's own answer, not the choice kept here for every save.
        public string BeforeReDefinition(BundledSetting setting)
        {
            Load();
            if (setting == null) return null;
            string context = Context(setting);
            BundledLedger.Backup backup;
            if (context != null && ledger.TryBackup(context, setting.Key, out backup)) return backup.Value;
            return SafeRead(setting);
        }

        // Whether the mod holding `value` is all a choice of it needs: a
        // per-save value is a choice for every save only when kept here, so a
        // profile sets it even where the save loaded now holds it already.
        public bool Holds(BundledSetting setting, string value)
        {
            Load();
            if (setting == null || !PerSave(setting)) return true;
            string ours;
            return ledger.TryStored(setting.Key, out ours) && SettingValues.Same(ours, value);
        }

        // The player's change, from the window or a profile. The mods that save
        // are saved once after all of a batch's changes, by the caller's SaveNow
        // -- as is bundled.cfg, so `save` is asked for.
        public void Set(BundledSetting setting, string value, bool save)
        {
            Load();
            if (!state.Enabled || setting == null || value == null) return;
            // What an installed mod requires, whoever sets it (Requirements).
            value = host.Adjust(setting, value);

            // A new choice, for every save: nothing is put back any more.
            ledger.MarkRestore(setting.Key, false);
            ledger.Keep(setting.Key, value);
            bool reached = WriteToMod(setting, value, true);
            if (reached) Due(setting);
            if (save) SaveNow();

            host.Info(setting.Owner.ModName + ", " + setting.Title + ": " + value
                      + (reached
                          ? " (" + When(setting.Window) + "; " + Kept(setting) + ")."
                          : " -- kept; it reaches the mod once the mod can take it."));
        }

        // A setting a newly chosen profile leaves alone while it still holds the
        // profile applied before (ProfileApplier.WithReleased): no value is kept
        // for it any more, and every value from before ReDefinition kept
        // for it is put back, as by Restore. Saved by the caller's SaveNow.
        public void Release(BundledSetting setting)
        {
            Load();
            if (!state.Enabled || setting == null) return;

            ledger.Forget(setting.Key);
            ledger.MarkRestore(setting.Key, true);
            ContinueRestore("released by a profile", null, true);
            host.Info(setting.Owner.ModName + ", " + setting.Title + ": left to its mod again, with what it had before ReDefinition.");
        }

        // The value *Reset to defaults* gives a setting
        // (docs/player/graphics-profiles.md): as Set for a mod that saves -- kept as the choice, per-save ones for
        // every save -- and for a mod without a save routine of its own only its
        // running value, with no value kept here, so its own config stands at
        // the next start. Backed up as any change is, so Restore can undo it.
        // Quiet: the caller logs the reset once. Saved by the caller's SaveNow.
        public void ResetTo(BundledSetting setting, string value)
        {
            Load();
            if (!state.Enabled || setting == null || value == null) return;
            value = host.Adjust(setting, value);
            ledger.MarkRestore(setting.Key, false);

            if (setting.Owner.Saving == SettingsSaving.AtEveryStart)
            {
                // Its running value is all there is: where that holds the default
                // already, nothing is written.
                ledger.Forget(setting.Key);
                string now = SafeApplicable(setting) ? SafeRead(setting) : null;
                if (now == null || !SettingValues.Same(now, value)) WriteToMod(setting, value, true);
                return;
            }

            // Written even where the mod shows the default already: what it shows
            // is its running value, which need not be what its files hold -- a
            // change in its own window that its own save leaves out. The
            // follow-ups run once a frame however many settings ask for them, and
            // each mod saves once after the batch.
            ledger.Keep(setting.Key, value);
            if (WriteToMod(setting, value, true)) Due(setting);
        }

        // A value a requirement puts right (Requirements.Enforce). Where a choice
        // for every save is kept, or the mod keeps the value for the game as a
        // whole, it is a new choice, as Set makes it. A per-save value with no
        // choice kept here is the loaded save's own: put right in that save only,
        // as a change in the mod's own window would be, and every other save
        // keeps its own. False where it cannot be written now. Saved by the
        // caller's SaveNow.
        public bool Correct(BundledSetting setting, string value)
        {
            Load();
            if (!state.Enabled || setting == null || value == null) return false;
            if (!PerSave(setting) || ledger.Contains(setting.Key))
            {
                Set(setting, value, false);
                return true;
            }
            if (!WriteToMod(setting, value, true)) return false;
            Due(setting);
            return true;
        }

        // A change made in the own window of a mod that keeps its values per save
        // (the hooks on TUFX and Distant Object), to a choice kept here: that
        // choice follows it, so both windows keep showing the same and the next
        // save that loads gets it. True where it did. bundled.cfg is written by
        // `save`, or by the caller's SaveIfDirty.
        public bool TakeFromMod(BundledSetting setting, string value, bool save)
        {
            Load();
            string ours;
            if (!state.Enabled || setting == null || value == null || !ledger.TryStored(setting.Key, out ours)
                || SettingValues.Same(ours, value)) return false;
            // A value an installed mod forbids does not become the choice for
            // every save: the choice kept goes straight back into the save --
            // not remembered as that save's own -- and the rule is said once per
            // run.
            if (!host.Allows(setting, value))
            {
                if (WriteToMod(setting, ours, false)) Due(setting);
                host.Tell(setting, value);
                return false;
            }
            ledger.Keep(setting.Key, value);
            if (save) Save();
            host.Info(setting.Owner.ModName + ", " + setting.Title + ": set to " + value
                      + " in its own window -- ReDefinition's window follows.");
            return true;
        }

        public void SaveIfDirty()
        {
            Load();
            if (ledger.Dirty) Save();
        }

        // bundled.cfg, and every mod with changes not yet saved, through its own
        // save routine.
        public void SaveNow()
        {
            Load();
            SaveMods(null);
            Save();
        }

        // After the mods' own applies -- at every scene change, on the main
        // menu's tick, at camera changes, and after a mod read its own file: what
        // is to be put back, then what is kept here, to the mods where it is not
        // there yet. `only` limits both, and the saving, to one mod -- for a mod
        // that has just read its own file, where saving the others from inside it
        // would run them at a moment their own timing was never meant for. What
        // failed -- a save, a refused value -- is tried again only at a scene
        // change (`sceneChange`).
        public void ReapplyStored(string when, IBundledMod only, bool sceneChange)
        {
            Load();
            ContinueRestore(when, only, sceneChange);

            int handed = 0;
            if (state.Enabled)
            {
                foreach (KeyValuePair<string, string> pair in ledger.StoredCopy())
                {
                    BundledSetting setting = host.Find(pair.Key);
                    if (setting == null || (only != null && setting.Owner != only)) continue;
                    if (!SafeApplicable(setting))
                    {
                        waiting.Add(setting.Owner);
                        continue;
                    }
                    string now = SafeRead(setting);
                    if (now == null) continue;

                    if (!SettingValues.Same(now, pair.Value))
                    {
                        if (!WriteToMod(setting, pair.Value, true)) continue;
                        handed++;
                        Due(setting);
                    }
                    // In the mod already, but still kept here: its save failed.
                    else if (sceneChange && setting.Owner.Saving == SettingsSaving.InModFiles)
                    {
                        Due(setting);
                    }
                }
            }
            SaveMods(only);
            if (ledger.Dirty) Save();

            if (handed > 0) host.Info("Bundled settings: " + handed + " handed to their mods (" + when + ").");
        }

        // The mods a value could not reach before, each asked again on its own --
        // outside the main menu the add-on's tick hands over to these only.
        public void ReapplyWaiting(string when)
        {
            Load();
            if (waiting.Count == 0) return;
            List<IBundledMod> mods = new List<IBundledMod>(waiting);
            waiting.Clear();
            foreach (IBundledMod mod in mods) ReapplyStored(when, mod, false);
        }

        // Every setting back to what its mod had before ReDefinition first
        // changed it, saved as the change was: at once where it can be, and where
        // not -- a save not loaded now, a mod that cannot take it yet or is not
        // installed -- at the next chance. No value stays kept here: a value still
        // waiting for its mod, or one no different from a save's own, would be
        // handed over later. With the bundling on or off.
        public void RestoreBackup()
        {
            Load();
            ledger.ForgetAll();
            ledger.MarkAll();
            state.ProfileName = "";
            ContinueRestore("restore", null, true);
            SaveNow();

            // Per-save values of an installed mod wait for their saves, as said;
            // the rest waits for a mod to be installed or ready.
            int pending = 0;
            foreach (string key in ledger.MarkedKeys())
            {
                BundledSetting setting = host.Find(key);
                if (setting == null || !PerSave(setting)) pending++;
            }
            host.Info("Bundled settings: put back what the mods had before ReDefinition; per-save values follow in each"
                      + " save the next time it loads"
                      + (pending > 0
                          ? "; " + pending + " wait -- for their mods to be installed or ready, or to take or save them in"
                            + " the next scene."
                          : "."));
        }

        // Off: each mod's own window decides again, and what is kept here only
        // -- values set at every start, choices for every save, values still
        // waiting -- is dropped. What the mods saved stays theirs; RestoreBackup
        // is the way back to before.
        public void SetEnabled(bool on)
        {
            Load();
            if (state.Enabled == on) return;

            state.Enabled = on;
            if (on)
            {
                ReapplyStored("bundling switched on", null, false);
            }
            else
            {
                ledger.ForgetAll();
                state.ProfileName = "";
            }
            Save();
            host.Info("Other mods' settings " + (on
                ? "bundled here; their toolbar buttons are hidden."
                : "left to their own windows; their toolbar buttons are back. What was saved in them stays."));
        }

        // Installed mods the main-menu notice has not asked about.
        public List<IBundledMod> NotYetAsked()
        {
            Load();
            List<IBundledMod> list = new List<IBundledMod>();
            foreach (IBundledMod mod in host.Installed())
                if (!state.Asked.Contains(mod.Id)) list.Add(mod);
            return list;
        }

        public void MarkAsked(IEnumerable<IBundledMod> list)
        {
            Load();
            foreach (IBundledMod mod in list)
                if (!state.Asked.Contains(mod.Id)) state.Asked.Add(mod.Id);
            Save();
        }

        public string SafeRead(BundledSetting setting)
        {
            try
            {
                return setting.Read();
            }
            catch (Exception e)
            {
                host.Warn("bundled-read-" + setting.Key, setting.Owner.ModName + ", " + setting.Title
                                                         + ": could not be read (" + Reason(e) + ").");
                return null;
            }
        }

        public static string When(ApplyWindow window)
        {
            switch (window)
            {
                case ApplyWindow.Live: return "at once";
                case ApplyWindow.NextScene: return "from the next scene on";
                case ApplyWindow.Restart: return "after a restart";
                default: return "when the mod next reads it";
            }
        }

        // Where a value set here is kept, as the window says it -- per setting:
        // TUFX's main-menu profile is its configuration's, one for the game.
        public static string Kept(BundledSetting setting)
        {
            IBundledMod mod = setting.Owner;
            if (PerSave(setting)) return "saved in " + mod.ModName + " and set into every save as it loads";
            return mod.Saving == SettingsSaving.AtEveryStart ? "set by ReDefinition at every start" : "saved in " + mod.ModName;
        }

        // A value the mod keeps per save: every setting of a per-save mod but one
        // it keeps for the game as a whole, TUFX's main-menu profile.
        internal static bool PerSave(BundledSetting setting)
        {
            return setting.Owner.Saving == SettingsSaving.PerSave && setting.Context != null;
        }

        // What is marked to be put back, where its setting's save is loaded now
        // and its mod can take it; done once the mod holds it and has saved it. A
        // per-save value its save already holds is saved once more; a whole-game
        // value already in its mod is written and saved again only at a restore
        // or scene change (`force`), since the mod's file may still hold ReDefinition's value.
        // Each is tried once per scene load (TriedAt).
        private void ContinueRestore(string when, IBundledMod only, bool force)
        {
            if (!ledger.AnyMarked()) return;

            // Only where a value can be put back now: the game's as a whole and
            // the loaded save's.
            List<KeyValuePair<string, string>> due = new List<KeyValuePair<string, string>>();
            ledger.AddMarked(due, "");
            string loadedSave = host.LoadedSave();
            if (loadedSave.Length > 0) ledger.AddMarked(due, loadedSave);

            int restored = 0;
            foreach (KeyValuePair<string, string> item in due)
            {
                string context = item.Key, key = item.Value;
                BundledSetting setting = host.Find(key);
                if (setting == null || (only != null && setting.Owner != only)) continue;
                if (!SafeApplicable(setting))
                {
                    waiting.Add(setting.Owner);
                    continue;
                }
                if (Context(setting) != context) continue;
                BundledLedger.Backup backup;
                if (!ledger.TryBackup(context, key, out backup) || !backup.Restore) continue;
                if (!force && backup.TriedAt == sceneLoads) continue;
                string now = SafeRead(setting);
                if (now == null) continue;

                IBundledMod mod = setting.Owner;
                bool perSave = PerSave(setting);
                if (SettingValues.Same(now, backup.Value) && !perSave && !force) continue;

                backup.TriedAt = sceneLoads;
                if (!SettingValues.Same(now, backup.Value) || !perSave)
                {
                    if (!WriteToMod(setting, backup.Value, false)) continue;
                    restored++;
                }
                // Only while it is still the same kept value, still marked: a Set
                // later in the batch unmarks it, and it stays the value from
                // before.
                BundledLedger.Backup kept = backup;
                Settle(mod, () =>
                {
                    BundledLedger.Backup current;
                    if (ledger.TryBackup(context, key, out current) && current == kept && current.Restore)
                        ledger.RemoveBackup(context, key);
                });
            }

            if (restored > 0)
                host.Info("Bundled settings: " + restored + " put back to what their mods had before ReDefinition (" + when + ").");
        }

        // Done once the mod has saved -- at once for a mod set at every start.
        private void Settle(IBundledMod mod, Action done)
        {
            if (mod.Saving == SettingsSaving.AtEveryStart)
            {
                done();
                return;
            }
            ledger.Unsaved(mod);
            ledger.AfterSave(mod, done);
        }

        // The mod saves at the end of the batch; a value of a mod that keeps it
        // in its files leaves the ledger only once that save has worked.
        private void Due(BundledSetting setting)
        {
            IBundledMod mod = setting.Owner;
            if (mod.Saving == SettingsSaving.AtEveryStart) return;
            ledger.Unsaved(mod);
            if (mod.Saving != SettingsSaving.InModFiles) return;

            string key = setting.Key, value;
            if (!ledger.TryStored(key, out value)) return;
            ledger.AfterSave(mod, () =>
            {
                string now;
                if (!ledger.TryStored(key, out now) || now != value) return;
                ledger.Forget(key);
            });
        }

        private void SaveMods(IBundledMod only)
        {
            foreach (IBundledMod mod in ledger.UnsavedMods())
            {
                if (only != null && mod != only) continue;
                ledger.Saved(mod);
                try
                {
                    mod.Save();
                }
                catch (Exception e)
                {
                    // What waited for it is asked for again at the next scene change.
                    ledger.DropAfterSave(mod);
                    host.Warn("bundled-save-" + mod.Id, mod.ModName + " could not save the settings set in ReDefinition's"
                                                        + " window; they hold for this run, and saving is tried again at the"
                                                        + " next scene change (" + Reason(e) + ").");
                    continue;
                }

                List<Action> actions = ledger.TakeAfterSave(mod);
                if (actions == null) continue;
                foreach (Action action in actions) action();
            }
        }

        // The save a per-save setting keeps this value for now; "" for a value
        // kept for the game as a whole; null where that cannot be told.
        private string Context(BundledSetting setting)
        {
            if (!PerSave(setting)) return "";
            try
            {
                return setting.Context() ?? "";
            }
            catch (Exception e)
            {
                host.Warn("bundled-context-" + setting.Key, setting.Owner.ModName + ", " + setting.Title
                                                            + ": which save it belongs to could not be told (" + Reason(e) + ").");
                return null;
            }
        }

        private bool SafeApplicable(BundledSetting setting)
        {
            if (setting.Applicable == null) return true;
            try
            {
                return setting.Applicable();
            }
            catch (Exception e)
            {
                host.Warn("bundled-applicable-" + setting.Key, setting.Owner.ModName + ", " + setting.Title
                                                               + ": could not tell whether it applies now (" + Reason(e) + ").");
                return false;
            }
        }

        // Whether the row offers the value. A choice kept here that the mod no
        // longer has -- a TUFX profile from a pack since removed -- waits rather
        // than going into the mod; a value from before ReDefinition goes back as
        // it was. As a profile's value is judged.
        private static bool Offers(BundledSetting setting, string value)
        {
            return setting.Control != SettingControl.Choice || ProfileApplier.Refusal(setting, value) == null;
        }

        // False when the mod cannot take it now, or its objects are not there.
        // `remember`, for a choice: keep the value it had, the first time ever --
        // per save for a per-save setting. Without it the write puts back a value
        // from before, which goes back as it was, offered or not.
        private bool WriteToMod(BundledSetting setting, string value, bool remember)
        {
            if (!SafeApplicable(setting))
            {
                waiting.Add(setting.Owner);
                return false;
            }
            if (remember && !Offers(setting, value)) return false;
            string context = Context(setting);
            string before = context != null ? SafeRead(setting) : null;
            if (before == null) return false;

            // The value from before goes into bundled.cfg before the mod has the new
            // one: a mod that saves on its own could otherwise keep the new value
            // with nothing on disk to put back. Where bundled.cfg cannot be written,
            // the mod keeps its value. A mod that reads its value at every start
            // keeps the new one in no file of its own: its backup goes into
            // bundled.cfg with the next save.
            BundledLedger.Backup kept;
            bool newBackup = remember && !ledger.TryBackup(context, setting.Key, out kept)
                             && !SettingValues.Same(before, value);
            if (newBackup)
            {
                ledger.SetBackup(context, setting.Key, before);
                if (setting.Owner.Saving != SettingsSaving.AtEveryStart && !Save())
                {
                    ledger.RemoveBackup(context, setting.Key);
                    return false;
                }
            }

            try
            {
                setting.Write(value);
                return true;
            }
            catch (Exception e)
            {
                if (newBackup) ledger.RemoveBackup(context, setting.Key);
                host.Warn("bundled-write-" + setting.Key, setting.Owner.ModName + ", " + setting.Title + ": could not be set ("
                                                          + Reason(e) + ").");
                return false;
            }
        }

        private void Load()
        {
            if (loaded) return;
            loaded = true;
            host.Read(ledger, state);
        }

        // Clean only once written: a failed write stays due for the next save.
        private bool Save()
        {
            if (!host.Write(ledger, state)) return false;
            ledger.Dirty = false;
            return true;
        }

        // What went wrong inside a reflected call, not the wrapper around it.
        private static string Reason(Exception e)
        {
            return (e.InnerException ?? e).Message;
        }
    }
}
