using System.Collections.Generic;
using System.Reflection;
using System;
using ReDefinition.Core;
using ReDefinition.Settings;
using UnityEngine;

namespace ReDefinition.Window
{
    // The Graphics tab: the profiles Low to Max, their status line, and Reset,
    // which fills every row with its default and chooses High over it.
    internal static partial class SettingsWindow
    {
        // The Profiles tab's status line, worked out again only when something
        // it depends on has changed: DialogGUILabel asks for it every frame.
        private static string status;
        private static int statusVersion;
        private static string statusProfile;
        private static string statusApplied;
        private static bool statusBundled;
        private static bool statusEnabled;
        private static bool statusUpscalerPending;
        // The values of ReDefinition's modules' quality settings the status line was worked
        // out with: a profile's differences count each of them, and no other module
        // setting.
        private static string[] statusModules;

        // The profiles as the GameDatabase holds them -- a visual pack's patches
        // included -- with what choosing each fills in for the mods installed
        // now. What one names that cannot be set here reaches the log once per
        // run (ProfileApplier.Report).
        private static void LoadProfiles()
        {
            model.ProfilesStale = false;
            List<string> problems = new List<string>();
            profiles = ProfileApplier.Ordered(problems);

            Dictionary<string, Dictionary<BundledSetting, string>> own =
                new Dictionary<string, Dictionary<BundledSetting, string>>();
            foreach (GraphicsProfile profile in profiles) own[profile.Name] = ProfileApplier.Values(profile, problems);

            // Against the profile applied now, and the values the mods hold now:
            // read again after every Apply and restore.
            Dictionary<string, Dictionary<BundledSetting, string>> values =
                new Dictionary<string, Dictionary<BundledSetting, string>>();
            Dictionary<string, HashSet<BundledSetting>> releasedBy = new Dictionary<string, HashSet<BundledSetting>>();
            Dictionary<BundledSetting, string> applied;
            own.TryGetValue(BundledSettings.ProfileName, out applied);
            foreach (GraphicsProfile profile in profiles)
            {
                HashSet<BundledSetting> released = new HashSet<BundledSetting>();
                values[profile.Name] = ProfileApplier.WithReleased(own[profile.Name], applied, released);
                releasedBy[profile.Name] = released;
            }
            model.SetProfiles(values, releasedBy);
            defaultsOf = null;
            ProfileApplier.Report("Graphics profiles", problems);
            status = null;
        }

        // Low to Max. Choosing one fills the rows of every tab with its values --
        // with the bundling off, ReDefinition's own and those set straight into
        // their mods; nothing is set until Apply or Accept, as with any other row.
        private static DialogGUIBase[] ProfileRows()
        {
            if (profiles.Count == 0) return new DialogGUIBase[0];

            List<DialogGUIBase> rows = new List<DialogGUIBase>
            {
                new DialogGUILabel(ProfileStatus, PageWidth - 60f),
            };
            foreach (GraphicsProfile profile in profiles)
            {
                GraphicsProfile shown = profile;
                DialogGUIToggleButton choose = new DialogGUIToggleButton(() => model.Profile == shown.Name, shown.Title,
                    // Only the player's click chooses: KSP sets the toggle from the
                    // model in every frame it shows (DialogGUIToggle.Update), which
                    // fires this too -- after Cancel or Reset with what the model
                    // already holds. A click on the chosen one fills its rows again.
                    on => { if (on ? model.Profile != shown.Name : model.Profile == shown.Name) ChooseProfile(shown); }, 110f, 30f);
                // A sentence and the hardware beside the button; everything it sets in its tooltip.
                choose.tooltipText = shown.Description + "\nAccept sets it; a row changed afterwards makes it"
                                     + " Custom.";
                string beside = (shown.Summary.Length > 0 ? shown.Summary : shown.Title)
                                + (shown.Hardware.Length > 0 ? "\n<color=#9a9a9a>" + shown.Hardware + "</color>" : "");
                rows.Add(new DialogGUIHorizontalLayout(0f, 36f, 8f, new RectOffset(), TextAnchor.MiddleLeft,
                    choose, new DialogGUILabel(beside, true)));
            }

            return rows.ToArray();
        }

        // Everything back to its mod's default -- whether a profile or the player
        // changed it, and whether the window shows it -- once the question it asks
        // has been answered.
        private static DialogGUIButton ResetButton()
        {
            DialogGUIButton reset = new DialogGUIButton("Reset", ConfirmReset, ResetWidth, 30f, false);
            reset.tooltipText = "Every setting back to its default, KSP's and the mods'.\n"
                                + "Then the profile High over them. Asks first, and says what it resets.";
            return reset;
        }

        private static void ConfirmReset()
        {
            List<string> names = new List<string>();
            List<string> atEveryStart = new List<string>();
            foreach (IBundledMod mod in BundledSettings.Installed())
            {
                // KSP's own reset is KSP's (KspReset), said apart below.
                if (mod.Id == "ksp") continue;
                names.Add(mod.ModName);
                if (mod.Saving == SettingsSaving.AtEveryStart) atEveryStart.Add(mod.ModName);
            }

            List<string> ownDefaults = KspOwnDefaults();
            string message = "All of KSP's settings go back to what KSP ships, as KSP's own Reset sets them: graphics,"
                             + " gameplay, system, audio, input, and the key bindings of the keyboard layout KSP detects."
                             + (ownDefaults.Count > 0
                                 ? " " + string.Join(", ", ownDefaults.ToArray()) + (ownDefaults.Count == 1 ? " takes" : " take")
                                   + " ReDefinition's default instead, or what an installed mod requires."
                                 : "")
                             + " The screen resolution and full screen stay as they are.\n\n";
            if (names.Count > 0)
            {
                message += "Every setting of " + string.Join(", ", names.ToArray()) + " goes back to its default, the"
                           + " ones this window does not show included: what the mods' releases ship, and with"
                           + " Volumetric Clouds installed what its author ships. What an installed mod requires still"
                           + " counts."
                           + (atEveryStart.Count > 0
                               ? " " + string.Join(" and ", atEveryStart.ToArray()) + ", which ReDefinition sets at every"
                                 + " start, take what their own files hold from the next start on."
                               : "")
                           + (model.Bundled ? " Their settings stay in this window." : " Their settings are bundled in this window.")
                           + "\n\n";
            }
            bool active = BundledSettings.ProfileChosen;
            message += "ReDefinition's own settings go back to theirs, with frame generation off. The upscaling"
                       + " technique, the raytracing and \"Replace original settings\" stay as they are"
                       + (active ? ", and so does whether the upscaler is on" : "") + ".\n\n"
                       + "Then the graphics profile High is chosen over the defaults, as its button chooses it: the"
                       + " quality settings as High has them, what the mods need of KSP's settings, and the upscaler"
                       + " at AA only" + (active ? "" : ", switched on") + ".\n\n"
                       + "The rows are only filled in: Accept sets them, Cancel puts them back as they were."
                       + " \"Restore settings from before ReDefinition\" under Mods and toolbar brings back what the mods"
                       + " had before ReDefinition first changed them.\n\nThe key bindings go back to their defaults"
                       + " too: ReDefinition's own and the mods'.";

            ConfirmDialog.OverWindow("ReDefinitionReset", "Reset", message, 460f, "Reset", ChooseDefaults);
        }

        // The titles of KSP's settings the reset sets to something other than what
        // KSP ships: those whose default ReDefinition's registration recommends
        // (Mods/KSP.cfg; the UI scale by the screen's height) and those an
        // installed mod requires otherwise. Only the settings that are a field of
        // GameSettings of the same name: SetDefaultValues sets those, and the others
        // -- Track IR's switches, what a behaviour works out -- would read as they
        // are now rather than as KSP ships them.
        private static List<string> KspOwnDefaults()
        {
            List<string> titles = new List<string>();
            List<KeyValuePair<BundledSetting, string>> ksp = new List<KeyValuePair<BundledSetting, string>>();
            string prefix = KspId + ".";
            foreach (KeyValuePair<BundledSetting, string> pair in ProfileApplier.DefaultValues(new List<string>()))
            {
                string key = pair.Key.Key;
                if (pair.Key.Owner == null || pair.Key.Owner.Id != KspId || !key.StartsWith(prefix, StringComparison.Ordinal))
                    continue;
                if (typeof(GameSettings).GetField(key.Substring(prefix.Length), BindingFlags.Public | BindingFlags.Static) != null)
                    ksp.Add(pair);
            }
            string[] shipped = new string[ksp.Count];
            Exception e = KspShipped.Read(() =>
            {
                for (int i = 0; i < ksp.Count; i++) shipped[i] = ksp[i].Key.Read();
            });
            if (e != null)
            {
                Debug.LogWarning(Log.Tag + " Reset: what KSP ships could not be read (" + e.Message + ").");
                return titles;
            }
            for (int i = 0; i < ksp.Count; i++)
                if (!SettingValues.Same(shipped[i], ksp[i].Value)) titles.Add(ksp[i].Key.Title);
            return titles;
        }

        // Fills every setting with its mod's default (SettingsEdit.ChooseDefaults),
        // and ReDefinition's own settings with theirs, then chooses High over
        // them, so that choosing High afterwards changes nothing. With no defaults
        // for the other mods -- none installed, or the game still loading -- the
        // rows stay as they are and High is chosen all the same.
        private static void ChooseDefaults()
        {
            List<string> problems = new List<string>();
            Dictionary<BundledSetting, string> values = ProfileApplier.DefaultValues(problems);
            ProfileApplier.Report("Reset", problems);
            if (values.Count > 0)
            {
                // A setting never bundled is set straight into its mod, which may
                // not be there in this scene: reset only where its row has a value.
                model.ChooseDefaults(values, setting => !setting.Bundled);
            }
            else
            {
                if (BundledSettings.Installed().Count > 0)
                    ScreenMessages.PostScreenMessage("ReDefinition has no defaults for the other mods yet; the log says why.",
                        5f);
                model.Profile = "";
            }
            if (edit != null) edit.After = OwnSettingsReset.From(edit.After, BundledSettings.ProfileChosen);
            // ReDefinition's own bindings went back with the settings above, the
            // mods' go with their other settings, and KSP's are filled in here.
            ResetKeyBindings();
            ResetAxes();
            ClearLayoutPending();
            GraphicsProfile high = ProfileApplier.Find(profiles, DefaultProfile);
            if (high != null) ChooseProfile(high);
            else Debug.LogWarning(Log.Tag + " Reset: the profile '" + DefaultProfile + "' is not installed; no profile chosen.");
            status = null;
        }

        // The profile the reset chooses over the defaults: the one that is the
        // defaults (ReDefinition-Profiles.cfg).
        private const string DefaultProfile = "high";

        private static void ChooseProfile(GraphicsProfile profile)
        {
            // Built from values that have changed since, the lists would hand back
            // what the player changed meanwhile.
            if (model.ProfilesStale)
            {
                try
                {
                    LoadProfiles();
                }
                catch (Exception e)
                {
                    Debug.LogWarning(Log.Tag + " The graphics profiles could not be read again: " + e);
                }
            }
            // With the bundling off only the rows that need none; the others take
            // the profile's values once it is switched on (SettingsEdit.SetBundled).
            if (!model.ChooseProfile(profile.Name, setting => model.Bundled || SetDirectly(setting, false))) return;

            List<string> problems = new List<string>();
            if (edit != null) ModuleProfiles.Apply(profile, edit.After, problems, BundledSettings.ProfileChosen);
            ProfileApplier.Report("Graphics profile '" + profile.Title + "'", problems);
        }

        // What the rows hold now against the profile they were filled from, and
        // whether any of it still waits for Apply (SettingsEdit.Status).
        private static string ProfileStatus()
        {
            string applied = BundledSettings.ProfileName;
            bool enabled = BundledSettings.Enabled;
            OwnSettings after = edit != null ? edit.After : null;
            bool upscalerPending = OwnSettingsPending();
            bool modulesSame = SameModules(after);

            if (status != null && statusVersion == model.Version && statusProfile == model.Profile
                && statusApplied == applied && statusBundled == model.Bundled && statusEnabled == enabled
                && statusUpscalerPending == upscalerPending && modulesSame)
                return status;

            statusVersion = model.Version;
            statusProfile = model.Profile;
            statusApplied = applied;
            statusBundled = model.Bundled;
            statusEnabled = enabled;
            statusUpscalerPending = upscalerPending;
            GraphicsProfile profile = ProfileApplier.Find(profiles, model.Profile);
            status = model.Status(applied, enabled, upscalerPending, profile != null ? profile.Title : null,
                values => ProfileApplier.Differences(values, PendingValue)
                          + ModuleProfiles.Differences(profile, after, BundledSettings.ProfileChosen));
            return status;
        }

        // Whether every setting of ReDefinition's modules a profile sets still holds the value the
        // status line was worked out with; the values now are kept for the next
        // frame.
        private static bool SameModules(OwnSettings after)
        {
            int count = 0;
            for (int m = 0; m < OurModules.All.Count; m++)
            {
                IList<ModuleSetting> settings = OurModules.All[m].Settings;
                for (int s = 0; s < settings.Count; s++)
                    if (ModuleProfiles.CanSet(settings[s])) count++;
            }
            bool same = statusModules != null && statusModules.Length == count;
            if (!same) statusModules = new string[count];

            int i = 0;
            for (int m = 0; m < OurModules.All.Count; m++)
            {
                IList<ModuleSetting> settings = OurModules.All[m].Settings;
                for (int s = 0; s < settings.Count; s++)
                {
                    if (!ModuleProfiles.CanSet(settings[s])) continue;
                    string value = after != null ? settings[s].Read(after) : null;
                    if (!string.Equals(statusModules[i], value, StringComparison.Ordinal))
                    {
                        statusModules[i] = value;
                        same = false;
                    }
                    i++;
                }
            }
            return same;
        }

        private static string PendingValue(BundledSetting setting)
        {
            string value;
            return model.TryGetPending(setting.Key, out value) ? value : null;
        }
    }
}
