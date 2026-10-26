using System.Collections.Generic;
using System;
using ReDefinition.Core;
using ReDefinition.Modules;
using ReDefinition.Settings;
using UnityEngine;

namespace ReDefinition.Window
{
    // Apply and Accept as one ordered pipeline (Apply), Cancel, and what waits for
    // either: the window's rows against what the game and the mods hold.
    internal static partial class SettingsWindow
    {
        // Applies what waits, saves ReDefinition's settings and restarts KSP, the game saved first where one is loaded.
        private static void ApplyAndRestart()
        {
            if (Unapplied()) Apply();
            ReDefinitionAddon addon = ReDefinitionAddon.Instance;
            if (addon != null) addon.SaveSettingsNow();
            GameRestart.SaveAndRestart();
        }

        // Cancel: every row back to what the game and the mods hold now, nothing
        // set, and the window stays open.
        private static void Discard()
        {
            KeyCapture.Stop();
            AxisCapture.Stop();
            kspPending.Clear();
            axisPending.Clear();
            ClearLayoutPending();
            KeysChanged();
            if (edit != null && edit.Before != null) edit.After = edit.Before.Clone();
            try
            {
                ReadBundled();
                LoadProfiles();
            }
            catch (Exception e)
            {
                Debug.LogWarning(Log.Tag + " The window's rows could not be read again: " + e);
            }
            status = null;
        }

        // Whether anything in the window still waits for Accept -- asked by the
        // buttons in every frame, so worked out once a frame.
        private static int unappliedFrame = -1;
        private static bool unapplied;

        // Worked out once a frame: KSP's keys, axes or layout pending, or a setting of a mod or of ReDefinition changed.
        private static bool Unapplied()
        {
            int frame = Time.frameCount;
            if (frame == unappliedFrame) return unapplied;
            unappliedFrame = frame;
            // KSP's key bindings and axes wait in their tabs as well, and so does
            // every setting of ReDefinition's own, the upscaler's and the rest.
            unapplied = kspPending.Count > 0 || AxesDiffer() || layoutPending != null
                        || model.Unapplied(OwnSettingsPending(), BundledSettings.Enabled, BundledSettings.ProfileName);
            return unapplied;
        }

        // Whether any of ReDefinition's own settings waits for Accept, the ones no
        // row shows included -- a reset sets them too. Compared as a whole, as the
        // add-on saves them: the copy the window started from once per copy, the
        // one it edits at most ten times a second, or at once where it is a new
        // copy -- after Apply, Cancel or Reset.
        private static OwnSettings ownBefore;
        private static OwnSettings ownAfter;
        private static string ownBeforeSnapshot;
        private static float ownCheckedAt = -10f;
        private static bool ownPending;

        // Whether the copy the window edits differs from the one it started from, snapshot against snapshot.
        private static bool OwnSettingsPending()
        {
            if (edit == null || edit.Before == null || edit.After == null) return false;
            float now = Time.unscaledTime;
            if (edit.Before == ownBefore && edit.After == ownAfter && now - ownCheckedAt < 0.1f) return ownPending;
            if (edit.Before != ownBefore)
            {
                ownBefore = edit.Before;
                ownBeforeSnapshot = edit.Before.Snapshot();
            }
            ownAfter = edit.After;
            ownCheckedAt = now;
            ownPending = ownBeforeSnapshot != edit.After.Snapshot();
            return ownPending;
        }

        // Every setting of every installed mod.
        private static IEnumerable<BundledSetting> InstalledSettings()
        {
            foreach (IBundledMod mod in BundledSettings.Installed())
            {
                foreach (BundledSetting setting in mod.Settings) yield return setting;
            }
        }

        // The settings that go through the bundling at Apply: all but those set
        // directly.
        private static IEnumerable<BundledSetting> BundledNow()
        {
            foreach (BundledSetting setting in InstalledSettings())
                if (!SetDirectly(setting, true)) yield return setting;
        }

        // Whether a row is set straight into its mod, as the mod's own screen
        // would: one that is never bundled, and one of a mod whose registration
        // says `direct` while the bundling is off -- as the window holds it, or
        // as it was applied.
        private static bool SetDirectly(BundledSetting setting, bool applied)
        {
            if (!setting.Bundled) return true;
            RegisteredMod mod = setting.Owner as RegisteredMod;
            if (mod == null || !mod.Registration.Direct) return false;
            return applied ? !BundledSettings.Enabled : !model.Bundled;
        }

        // The rows set directly whose value the player changed, or the reset
        // filled in, written into their mods; the mods written, for the one save
        // each gets at the end of Apply. A row whose mod cannot take a value in
        // this scene keeps what the mod has.
        private static HashSet<IBundledMod> ApplyDirect()
        {
            HashSet<IBundledMod> written = new HashSet<IBundledMod>();
            foreach (BundledSetting setting in InstalledSettings())
            {
                // Only what the window changed: a value KSP's own screen set since
                // the window read it stays.
                if (!SetDirectly(setting, true) || !model.Changed(setting.Key)) continue;
                string value;
                if (!model.TryGetPending(setting.Key, out value) || value == null) continue;
                if (!Applicable(setting))
                {
                    CompatibilityLog.Warn("direct-apply-" + setting.Key, setting.Owner.ModName + ", " + setting.Title
                                          + ": not set -- " + setting.Owner.ModName + " cannot take it in this scene.");
                    continue;
                }
                string now = BundledSettings.SafeRead(setting);
                if (now != null && SettingValues.Same(now, value)) continue;
                try
                {
                    setting.Write(value);
                    if (!setting.KeptHere) written.Add(setting.Owner);
                }
                catch (Exception e)
                {
                    CompatibilityLog.Warn("direct-apply-" + setting.Key, setting.Owner.ModName + ", " + setting.Title
                                          + ": could not be set (" + CompatibilityLog.Reason(e) + ").");
                }
            }
            return written;
        }

        // Whether the mod can take the setting in this scene; false where asking throws.
        private static bool Applicable(BundledSetting setting)
        {
            try
            {
                return setting.Applicable == null || setting.Applicable();
            }
            catch (Exception)
            {
                return false;
            }
        }

        // KSP's registration, which saves KSP's settings as its own screens do.
        private static IBundledMod KspMod()
        {
            foreach (IBundledMod mod in BundledSettings.Installed())
                if (mod.Id == KspId) return mod;
            return null;
        }

        // The id of KSP's own registration among the bundled mods.
        private const string KspId = "ksp";

        // KSP's own reset, where the window resets (KspReset), so that what KSP's
        // reset covers and no row shows -- its key layout, its terrain presets --
        // is reset as well. What KSP's rows held before it stays their value from
        // before ReDefinition where the reset changed it: the store would read
        // KSP's default there otherwise. Then the rows are compared with what KSP
        // holds after it.
        private static void ResetKsp()
        {
            List<BundledSetting> ksp = new List<BundledSetting>();
            List<KeyValuePair<BundledSetting, string>> before = new List<KeyValuePair<BundledSetting, string>>();
            foreach (BundledSetting setting in InstalledSettings())
            {
                if (setting.Owner.Id != KspId) continue;
                ksp.Add(setting);
                if (!SetDirectly(setting, true))
                    before.Add(new KeyValuePair<BundledSetting, string>(setting, BundledSettings.SafeRead(setting)));
            }
            KspReset.Run();
            if (!BundledSettings.KeepBefore(before))
                Debug.LogWarning(Log.Tag + " " + BundledSettings.Path + " could not be written: Restore cannot bring back"
                                 + " KSP's settings from before the reset.");
            foreach (BundledSetting setting in ksp) model.Reread(setting.Key, BundledSettings.SafeRead(setting));
        }

        // Apply and Accept, as in KSP's dialog, in this order:
        //   1. the bundling switch and the toolbar buttons -- on hands the stored
        //      values over, off takes them back;
        //   2. KSP's own reset, where the window resets;
        //   3. the rows set directly;
        //   4. KSP's keyboard layout, key bindings and axes -- a rebind to the
        //      layout's preset before the keys changed by hand;
        //   5. the store's steps and the profile;
        //   6. every mod written saved once, KSP's settings among them;
        //   7. ReDefinition's own settings through the add-on's setters, which
        //      switch nothing on without the profile in place;
        // then everything read back, as the section in KSP's settings dialog does.
        // Each step is caught on its own: one that fails does not take the
        // others with it.
        private static void Apply()
        {
            KeyCapture.Stop();
            NoteWaiting();
            bool resetting = model.Resetting;
            bool newProfile = model.Profile != BundledSettings.ProfileName;
            HashSet<IBundledMod> written = new HashSet<IBundledMod>();

            Step("The bundling switch and the toolbar buttons from the window could not be applied", ApplySwitches);
            if (resetting) Step("KSP's settings could not be reset", ResetKsp);
            Step("Settings set directly from the window applied only in part", () => written.UnionWith(ApplyDirect()));
            bool kspWritten = resetting;
            Step("KSP's keyboard layout from the window could not be applied", () => { kspWritten |= ApplyLayout(); });
            Step("KSP's key bindings from the window applied only in part", () => { kspWritten |= ApplyKeyBindings(); });
            Step("KSP's axes from the window applied only in part", () => { kspWritten |= ApplyAxes(); });
            if (kspWritten)
            {
                IBundledMod ksp = KspMod();
                if (ksp != null) written.Add(ksp);
                else Step("KSP's settings could not be saved", GameSettings.SaveSettings);
            }
            Step("Other mods' settings from the window applied only in part", () => ApplyBundled(resetting));
            Step("The settings set from the window could not be saved", () => BundledSettings.SaveNow(written));
            ApplyOwn();
            // A profile is what the player has now, whatever was set in the proxy's ini.
            if (newProfile || resetting)
                Step("The low latency mode could not be written into the proxy's ini",
                     () => LowLatencySetting.Write(edit.After.LowLatency));

            // The new starting point, read back -- a failure there stays a warning,
            // not an exception the button handler sees.
            Step("The other mods' values could not be read back into the window", () =>
            {
                ReadBundled();
                LoadProfiles();
            });
        }

        // Runs one step of Apply; an exception is a warning in the log, and Apply goes on.
        private static void Step(string failure, Action step)
        {
            try
            {
                step();
            }
            catch (Exception e)
            {
                Debug.LogWarning(Log.Tag + " " + failure + ": " + e);
            }
        }

        // Step 1: the toolbar buttons' choices, and the bundling switch; the toolbar refreshed where either changed.
        private static void ApplySwitches()
        {
            bool buttonsChanged = model.ButtonsPending();
            foreach (KeyValuePair<string, bool> pair in model.Buttons)
                BundledSettings.SetHidesButton(pair.Key, pair.Value);

            if (model.Bundled != BundledSettings.Enabled)
            {
                BundledSettings.SetEnabled(model.Bundled);
                ToolbarTakeover.Refresh();
            }
            else if (buttonsChanged)
            {
                ToolbarTakeover.Refresh();
            }
        }

        // The model's steps with the bundling on, and the profile the rows came
        // from, with it on or off: the upscaler's part of a profile needs no
        // bundling.
        private static void ApplyBundled(bool resetting)
        {
            if (BundledSettings.Enabled)
            {
                foreach (SettingsEdit.Step step in model.Steps(BundledNow(), BundledSettings.Holds))
                {
                    BundledSetting setting = step.Setting;
                    // One setting that cannot be handed over must not take the
                    // other mods' changes with it -- nor the saving of the ones
                    // already made.
                    try
                    {
                        if (step.Kind == SettingsEdit.StepKind.Reset) BundledSettings.ResetTo(setting, step.Value);
                        else if (step.Kind == SettingsEdit.StepKind.Release) BundledSettings.Release(setting);
                        else BundledSettings.Set(setting, step.Value, false);
                    }
                    catch (Exception e)
                    {
                        CompatibilityLog.Warn("bundled-apply-" + setting.Key, setting.Owner.ModName + ", "
                                              + setting.Title + ": could not be applied from the window ("
                                              + CompatibilityLog.Reason(e) + ").");
                    }
                }
                if (resetting) Debug.Log(Log.Tag + " Bundled settings: every setting set to its mod's default (Reset).");
            }

            // Saved with the values it came with, at the end of Apply.
            if (model.Profile != BundledSettings.ProfileName) BundledSettings.ProfileName = model.Profile;
        }

        // Step 7: ReDefinition's own settings through the add-on, and the copies taken afresh from what came of them.
        private static void ApplyOwn()
        {
            ReDefinitionAddon addon = ReDefinitionAddon.Instance;
            if (addon == null || edit == null) return;
            try
            {
                addon.Apply(edit.Before, edit.After);
            }
            catch (Exception e)
            {
                Debug.LogWarning(Log.Tag + " The upscaler's settings from the window applied only in part: " + e);
            }
            finally
            {
                // A throw in here would replace the one just caught.
                try
                {
                    // Both or neither: a half-written pair would take the
                    // player's earlier changes for new ones at the next
                    // Apply.
                    OwnSettings now = addon.Current();
                    OwnSettings copy = now.Clone();
                    edit.Before = now;
                    edit.After = copy;
                }
                catch (Exception e)
                {
                    Debug.LogWarning(Log.Tag + " The upscaler's settings could not be read back into the"
                                     + " window: " + e);
                }
            }
        }
    }
}
