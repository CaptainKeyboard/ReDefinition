using System.Collections.Generic;
using System;
using ReDefinition.Core;
using ReDefinition.Modules;
using ReDefinition.Settings;
using ReDefinition.Ui;
using UnityEngine;

namespace ReDefinition.Window
{
    // The Mods / Toolbar tab: Replace original settings, the bundling switch,
    // the toolbar buttons, Restore, and what the window cannot show.
    internal static partial class SettingsWindow
    {
        // The mods whose toolbar button ReDefinition can hide, with what it does
        // with it now. A mod without a button of its own, or without a window or a
        // dialog to open in its place, has nothing to choose here.
        private static IEnumerable<KeyValuePair<string, bool>> ButtonStates()
        {
            foreach (IBundledMod mod in BundledSettings.Installed())
                if (ToolbarTakeover.CanHide(mod))
                    yield return new KeyValuePair<string, bool>(mod.Id, BundledSettings.HidesButton(mod.Id));
        }

        // The installed mods whose toolbar button ReDefinition can hide.
        private static List<IBundledMod> ButtonMods()
        {
            List<IBundledMod> mods = new List<IBundledMod>();
            foreach (IBundledMod mod in BundledSettings.Installed())
                if (ToolbarTakeover.CanHide(mod)) mods.Add(mod);
            return mods;
        }

        // Whether KSP's own Settings buttons -- in the main menu and the pause
        // menus -- open this window instead of KSP's screens. One of
        // ReDefinition's own settings, applied with the others.
        private static DialogGUIBase ReplaceRow()
        {
            DialogGUIToggle toggle = new DialogGUIToggle(() => edit != null && edit.After.ReplaceKspSettings,
                () => KspSettingsSection.StateText(edit != null && edit.After.ReplaceKspSettings),
                b =>
                {
                    if (edit != null) edit.After.ReplaceKspSettings = b;
                }, ControlWidth);
            toggle.tooltipText = "On: every Settings button of KSP -- the main menu's and the pause menus' -- opens this"
                                 + " window, which holds all of KSP's settings, and ReDefinition adds no entry of its own"
                                 + " there.\nOff: KSP's buttons open KSP's own screens, and ReDefinition's entry stands"
                                 + " under them.\nTakes effect the next time a menu is built.";
            return new DialogGUIHorizontalLayout(0f, RowHeight, 0f, new RectOffset(), TextAnchor.MiddleLeft,
                new DialogGUILabel(() => Marked("Replace original settings",
                    edit != null && edit.Before.ReplaceKspSettings != edit.After.ReplaceKspSettings), NameWidth),
                toggle, new DialogGUISpace(10f),
                new DialogGUILabel("", ValueWidth),
                new DialogGUILabel("<color=#9a9a9a>ReDefinition</color>", SourceWidth));
        }

        // ReDefinition's own rows under Mods / Toolbar, under their headings: the
        // scaling (OurModules.Scaling).
        private static void AddOwnInterfaceRows(List<DialogGUIBase> rows)
        {
            DialogGUIBase[] own = KspSettingsSection.Rows(edit, OwnLayout(), SettingCategory.Interface);
            List<ModuleSetting> settings = OurModules.Rows(SettingCategory.Interface);
            string section = null;
            for (int i = 0; i < own.Length && i < settings.Count; i++)
            {
                if (settings[i].Section != null && settings[i].Section != section)
                {
                    section = settings[i].Section;
                    rows.Add(SectionHeading(section));
                }
                rows.Add(Searchable(own[i], settings[i].Title + " " + (section ?? "") + " ReDefinition"));
            }
        }

        // The tab's rows of the bundling: the switch with the mods it bundles, the toolbar block, Restore, and what is not
        // shown here.
        private static DialogGUIBase[] InterfaceRows()
        {
            List<DialogGUIBase> notShown = NotShownRows();
            List<IBundledMod> installed = BundledSettings.Installed();
            if (installed.Count == 0) return notShown.ToArray();

            List<string> names = new List<string>();
            foreach (IBundledMod mod in installed) names.Add(mod.ModName);
            string list = string.Join(", ", names.ToArray());

            DialogGUIToggle bundle = new DialogGUIToggle(() => model.Bundled,
                () => KspSettingsSection.StateText(model.Bundled), model.SetBundled, ControlWidth);
            bundle.tooltipText = "On: the settings of " + list + " are changed here, sorted by feature, and saved in\n"
                                 + "those mods as their own windows save them; their own toolbar buttons are hidden\n"
                                 + "where the Advanced buttons here open their windows.\n"
                                 + "Off: each keeps its own button and window, and ReDefinition leaves their settings to them.\n"
                                 + "What was saved in the mods stays. What only ReDefinition kept is dropped: the choices\n"
                                 + "for every save of TUFX and Distant Object, and Deferred's and Waterfall's settings.\n"
                                 + "The graphics profile stays chosen, and with it the upscaler and frame generation.";

            DialogGUIButton restore = new DialogGUIButton("Restore settings from before ReDefinition", ConfirmRestore,
                300f, 30f, false);
            restore.tooltipText = "Every setting ReDefinition has changed goes back to what its mod had before, and is"
                                  + " saved there.\nAccept or cancel the changes made here first.";
            restore.OptionInteractableCondition = () => BundledSettings.CanRestore && !Unapplied();

            List<DialogGUIBase> rows = new List<DialogGUIBase>
            {
                new DialogGUIHorizontalLayout(0f, RowHeight, 0f, new RectOffset(), TextAnchor.MiddleLeft,
                    new DialogGUILabel("Bundle other mods here", NameWidth), bundle),
                new DialogGUILabel("<color=#9a9a9a>Bundled: " + list + "</color>", true),
            };
            rows.AddRange(ButtonRows());
            rows.Add(restore);
            rows.AddRange(notShown);
            return rows.ToArray();
        }

        // Whether the per-mod switches are folded out. Kept for the run, as the
        // Keys tab keeps its sections.
        private static bool buttonsOpen;

        // The toolbar block: one switch for every button ReDefinition can hide, and
        // a header that folds the mods out one by one, as the Keys tab's sections
        // do. Empty where no installed mod has such a button. The choices wait for
        // Apply, as everything else in this window does.
        private static List<DialogGUIBase> ButtonRows()
        {
            List<DialogGUIBase> rows = new List<DialogGUIBase>();
            List<IBundledMod> mods = ButtonMods();
            // Better Time Warp's button is hidden for ReDefinition's own reason (its
            // window opens from the time warp row), bundled or not
            // (OwnSettings.TimeWarpToolbarButton).
            bool timeWarp = Hud.TimeWarpButton.Installed && edit != null;
            if (mods.Count == 0 && !timeWarp) return rows;
            Func<bool> allHidden = () => (mods.Count == 0 || AllButtonsHidden(mods)) && (!timeWarp || TimeWarpHidden);

            DialogGUIToggle all = new DialogGUIToggle(allHidden, () => KspSettingsSection.StateText(allHidden()), hide =>
            {
                // Set from what the switches below say in every frame
                // (DialogGUIToggle.Update), which fires this too: one of them
                // switched off must not switch the others off.
                if (hide == allHidden()) return;
                foreach (IBundledMod mod in mods) model.SetButtonHidden(mod.Id, hide);
                if (timeWarp) TimeWarpHidden = hide;
            }, ControlWidth);
            all.tooltipText = "On: every mod below is hidden from the toolbar while its settings are bundled here.\n"
                              + "Off: every one of them keeps its button.\n"
                              + "It shows On while all of them are hidden.";
            all.OptionInteractableCondition = () => model.Bundled || mods.Count == 0;
            rows.Add(new DialogGUIHorizontalLayout(0f, RowHeight, 0f, new RectOffset(), TextAnchor.MiddleLeft,
                new DialogGUILabel("Hide all from toolbar", NameWidth), all));

            int count = mods.Count + (timeWarp ? 1 : 0);
            string folded = "+  Per mod (" + count + ")";
            string open = "-  Per mod (" + count + ")";
            DialogGUIButton header = new DialogGUIButton(() => buttonsOpen ? open : folded,
                () => { buttonsOpen = !buttonsOpen; }, PageWidth - 60f, RowHeight + 6f, false);
            header.tooltipText = "Opens or folds the switch for each mod's own toolbar button.";
            rows.Add(header);

            foreach (IBundledMod mod in mods)
            {
                IBundledMod shown = mod;
                DialogGUIToggle toggle = new DialogGUIToggle(() => model.ButtonHidden(shown.Id),
                    () => KspSettingsSection.StateText(model.ButtonHidden(shown.Id)),
                    hide => model.SetButtonHidden(shown.Id, hide), ControlWidth);
                toggle.tooltipText = "On: " + mod.ModName + "'s toolbar button is hidden, and the Advanced button here"
                                     + " opens its window.\n"
                                     + "Off: it keeps its button, and its settings stay in this window.";
                toggle.OptionInteractableCondition = () => model.Bundled;
                DialogGUIHorizontalLayout row = new DialogGUIHorizontalLayout(0f, RowHeight, 0f, new RectOffset(),
                    TextAnchor.MiddleLeft, new DialogGUILabel("    " + mod.ModName, NameWidth), toggle);
                row.OptionEnabledCondition = () => buttonsOpen;
                rows.Add(row);
            }
            if (timeWarp)
            {
                DialogGUIToggle toggle = new DialogGUIToggle(() => TimeWarpHidden,
                    () => KspSettingsSection.StateText(TimeWarpHidden), hide => TimeWarpHidden = hide, ControlWidth);
                toggle.tooltipText = "On: Better Time Warp's toolbar button is hidden; the arrow at the end of the time warp row"
                                     + " opens its window.\nOff: it keeps its button.";
                DialogGUIHorizontalLayout row = new DialogGUIHorizontalLayout(0f, RowHeight, 0f, new RectOffset(),
                    TextAnchor.MiddleLeft, new DialogGUILabel("    Better Time Warp", NameWidth), toggle);
                row.OptionEnabledCondition = () => buttonsOpen;
                rows.Add(row);
            }
            return rows;
        }

        // Whether Better Time Warp's toolbar button is to be hidden, over the copy the window edits.
        private static bool TimeWarpHidden
        {
            get { return edit != null && edit.After != null && !edit.After.TimeWarpToolbarButton; }
            set { if (edit != null && edit.After != null) edit.After.TimeWarpToolbarButton = !value; }
        }

        // Whether every one of those mods' buttons is to be hidden; false for none.
        private static bool AllButtonsHidden(List<IBundledMod> mods)
        {
            foreach (IBundledMod mod in mods)
                if (!model.ButtonHidden(mod.Id)) return false;
            return mods.Count > 0;
        }

        // What an installed mod has that this window cannot show: a mod this build of
        // which is not bundled at all, rows its registration places that the build
        // lacks, and settings the build saves that no registration names. Nothing
        // where a mod's version has changed without any of that. Each stays in the
        // mod's own window, which a button here opens where ReDefinition can.
        private static List<DialogGUIBase> NotShownRows()
        {
            List<DialogGUIBase> rows = new List<DialogGUIBase>();
            foreach (IBundledMod mod in BundledSettings.Mods)
            {
                RegisteredMod registered = mod as RegisteredMod;
                if (registered == null || !registered.AssemblyLoaded) continue;

                string text = NotShownText(registered);
                if (text == null) continue;
                if (rows.Count == 0)
                    rows.Add(new DialogGUILabel("<color=#ffdd55>Not shown in this window</color>", true));

                if (registered.IsInstalled && registered.OwnWindow != null)
                {
                    IBundledMod shown = mod;
                    DialogGUIButton open = new DialogGUIButton(mod.ModName + "'s window", () => OpenOwnWindow(shown),
                        160f, 24f, false);
                    rows.Add(new DialogGUIHorizontalLayout(0f, 24f, 8f, new RectOffset(), TextAnchor.MiddleLeft,
                        new DialogGUILabel(text, PageWidth - 220f), open));
                }
                else
                {
                    rows.Add(new DialogGUILabel(text, true));
                }
            }
            return rows;
        }

        // Whether any loaded mod has something this window cannot show: the Mods / Toolbar tab's name marks it.
        private static bool AnyNotShown()
        {
            foreach (IBundledMod mod in BundledSettings.Mods)
            {
                RegisteredMod registered = mod as RegisteredMod;
                if (registered != null && registered.AssemblyLoaded && NotShownText(registered) != null) return true;
            }
            return false;
        }

        // What the window cannot show of the mod, in words; null where it shows everything.
        private static string NotShownText(RegisteredMod mod)
        {
            if (!mod.IsInstalled)
            {
                return mod.ModName + ": this build is not bundled, so none of its settings are here -- its own window"
                       + " and toolbar button have them all.";
            }
            List<string> parts = new List<string>();
            if (mod.RowsNotShown.Count > 0)
                parts.Add("this build has no " + Joined(mod.RowsNotShown) + " as ReDefinition knows "
                          + (mod.RowsNotShown.Count == 1 ? "it" : "them"));
            if (mod.SettingsNotKnown.Count > 0)
                parts.Add("it saves " + Joined(mod.SettingsNotKnown) + ", which ReDefinition does not know");
            if (parts.Count == 0) return null;
            return mod.ModName + ": " + string.Join("; ", parts.ToArray()) + ". Its own window has "
                   + (mod.RowsNotShown.Count + mod.SettingsNotKnown.Count == 1 ? "it." : "them.");
        }

        // The names in single quotes, separated by commas.
        private static string Joined(IList<string> names)
        {
            List<string> quoted = new List<string>();
            foreach (string name in names) quoted.Add("'" + name + "'");
            return string.Join(", ", quoted.ToArray());
        }

        // Asks before Restore, over the window.
        private static void ConfirmRestore()
        {
            ConfirmDialog.OverWindow("ReDefinitionRestore", "Restore settings from before ReDefinition",
                "Every setting ReDefinition has changed goes back to what its mod had before, and is saved there. "
                + "The graphics profile is cleared, so the upscaler and frame generation go off until one is chosen. "
                + "TUFX and Distant Object get theirs back in every save as it loads.", 420f, "Restore", Restore);
        }

        // Puts every mod's settings back to what they were before ReDefinition, the profile cleared, and reads the rows
        // again; refused while a change in the window waits.
        private static void Restore()
        {
            // The window stays usable behind the question: a row changed
            // meanwhile would be lost with the rows read again.
            if (Visible && Unapplied())
            {
                ScreenMessages.PostScreenMessage("Accept or cancel the changes in ReDefinition's window first, then restore.",
                    5f);
                return;
            }
            try
            {
                BundledSettings.RestoreBackup();
            }
            catch (Exception e)
            {
                Debug.LogWarning(Log.Tag + " The settings from before ReDefinition were restored only in part: " + e);
            }
            // Without the profile the upscaler goes off now, before anything reads it
            // back.
            ReDefinitionAddon addon = ReDefinitionAddon.Instance;
            if (addon != null) addon.EnforceProfile();
            // The rows show what the mods hold now.
            if (!Visible) return;
            ReadBundled();
            LoadProfiles();
            if (addon != null && edit != null)
            {
                OwnSettings now = addon.Current();
                edit.Before = now;
                edit.After = now.Clone();
            }
        }
    }
}
