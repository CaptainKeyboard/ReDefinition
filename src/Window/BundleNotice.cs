using System.Collections.Generic;
using System.Collections;
using ReDefinition.Core;
using ReDefinition.Settings;
using UnityEngine;

namespace ReDefinition.Window
{
    // The main menu's word about ReDefinition. The first time: ReDefinition does
    // nothing until a graphics profile is chosen, so the question is whether it
    // takes control now or later. Taking control applies High, which bundles the
    // graphics mods here and switches the upscaler on, makes KSP's Settings
    // buttons open ReDefinition's window, sets KSP's UI scale to the screen where
    // it is still KSP's 100 %, and loads the main menu again, which then leads to
    // the window. Later leaves every mod as it is. A mod installed after that follows the
    // answer given: bundled, it is named in a short note; kept apart, nothing is
    // said. The bundling can be changed under "Mods and toolbar" in the window.
    [KSPAddon(KSPAddon.Startup.MainMenu, false)]
    public class BundleNotice : MonoBehaviour
    {
        private IEnumerator Start()
        {
            // After the menu has settled and the other mods' own messages.
            yield return new WaitForSecondsRealtime(2f);

            List<IBundledMod> fresh = BundledSettings.NotYetAsked();
            if (fresh.Count == 0) yield break;

            List<string> names = new List<string>();
            List<string> withButton = new List<string>();
            foreach (IBundledMod mod in fresh)
            {
                names.Add(mod.ModName);
                // Not every mod has a button to hide: Waterfall's only one opens its
                // effect editor, an authoring tool, and stays -- so it is not named.
                if (ToolbarTakeover.WouldHide(mod)) withButton.Add(mod.ModName);
            }
            string list = string.Join(", ", names.ToArray());
            string buttonList = string.Join(", ", withButton.ToArray());

            if (BundledSettings.AnyAsked)
            {
                BundledSettings.MarkAsked(fresh);
                if (!BundledSettings.Enabled)
                {
                    Debug.Log(Log.Tag + " " + list + " keep their own buttons, as chosen before.");
                    yield break;
                }

                List<DialogGUIBase> noteRows = ToolbarRows();
                noteRows.Add(new DialogGUIButton("OK", () => { }, true));
                MultiOptionDialog note = new MultiOptionDialog("ReDefinitionBundleNote",
                    "The graphics settings of " + list + " are now in ReDefinition's settings window as well"
                    + (withButton.Count > 0 ? ", and the toolbar " + Buttons(withButton.Count) + buttonList
                                              + (withButton.Count > 1 ? " are hidden" : " is hidden") : "")
                    + " -- as you chose for the other graphics mods. "
                    + "You can change this under \"Mods / Toolbar\" in the window.",
                    "Graphics settings in one place", HighLogic.UISkin, 420f, noteRows.ToArray());
                UnityMouseEvents.Shield(PopupDialog.SpawnPopupDialog(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                    note, false, HighLogic.UISkin));
                Debug.Log(Log.Tag + " Main menu note: " + list + " bundled too, as chosen before.");
                yield break;
            }

            string message =
                "ReDefinition brings FSR and DLSS upscaling and frame generation to KSP, and the graphics settings of " + list
                + " into one window, sorted by feature -- open it with ReDefinition's toolbar button.\n\n"
                + "It does nothing until a graphics profile is chosen. A profile sets these mods up to work together"
                + " with the upscaler and switches it on. Taking control chooses High, every mod as its authors ship"
                + " it, and makes KSP's Settings buttons open ReDefinition's window; Low, Medium, Ultra and Max are"
                + " under \"Graphics\" in the window.\n\n"
                + "With a profile, their settings are changed from ReDefinition's window and saved in each mod, as its"
                + " own window would save them"
                + (withButton.Count > 0
                    ? "; the toolbar " + Buttons(withButton.Count) + buttonList
                      + (withButton.Count > 1 ? " are hidden" : " is hidden")
                    : "")
                + ". ReDefinition keeps what each had before, and \"Restore settings from before ReDefinition\" under"
                + " \"Mods and toolbar\" brings it back.";

            List<DialogGUIBase> rows = ToolbarRows();
            rows.Add(new DialogGUIHorizontalLayout(0f, 30f, 8f, new RectOffset(), TextAnchor.MiddleCenter,
                new DialogGUIButton("Let ReDefinition take control", () => Choose(fresh, "high"), true),
                new DialogGUIButton("Later", () => Choose(fresh, null), true)));
            MultiOptionDialog dialog = new MultiOptionDialog("ReDefinitionBundleNotice", message,
                "ReDefinition", HighLogic.UISkin, 540f, rows.ToArray());
            // Shielded: the main menu's entries behind it take Unity's mouse
            // events, which no dialog locks.
            UnityMouseEvents.Shield(PopupDialog.SpawnPopupDialog(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                dialog, false, HighLogic.UISkin));
            Debug.Log(Log.Tag + " Main menu notice about ReDefinition shown for " + list + ".");
        }

        // Whether the per-mod switches are folded out, for as long as the dialog
        // stands.
        private static bool perModOpen;

        // The toolbar choice in the main menu's panels: one switch for every
        // button, and a header that folds the mods out, as the settings window
        // has it. These panels have no Apply, so a switch here is the choice
        // itself, kept in bundled.cfg and acted on at once.
        private static List<DialogGUIBase> ToolbarRows()
        {
            List<DialogGUIBase> rows = new List<DialogGUIBase>();
            List<IBundledMod> mods = new List<IBundledMod>();
            foreach (IBundledMod mod in BundledSettings.Installed())
                if (ToolbarTakeover.CanHide(mod)) mods.Add(mod);
            if (mods.Count == 0) return rows;

            DialogGUIToggle all = new DialogGUIToggle(() => AllHidden(mods), "Hide all from toolbar", hide =>
            {
                foreach (IBundledMod mod in mods) BundledSettings.SetHidesButton(mod.Id, hide);
                ToolbarTakeover.Refresh();
            }, 300f);
            all.tooltipText = TooltipText.Wrap("On: these mods' own toolbar buttons are hidden while their settings are bundled in"
                              + " ReDefinition's window, which opens each of them with its Advanced button.");
            rows.Add(all);

            string folded = "+  Per mod (" + mods.Count + ")";
            string open = "-  Per mod (" + mods.Count + ")";
            rows.Add(new DialogGUIButton(() => perModOpen ? open : folded,
                () => { perModOpen = !perModOpen; }, 300f, 24f, false));

            foreach (IBundledMod mod in mods)
            {
                IBundledMod shown = mod;
                DialogGUIToggle toggle = new DialogGUIToggle(() => BundledSettings.HidesButton(shown.Id),
                    "    " + mod.ModName, hide =>
                    {
                        BundledSettings.SetHidesButton(shown.Id, hide);
                        ToolbarTakeover.Refresh();
                    }, 300f);
                toggle.OptionEnabledCondition = () => perModOpen;
                rows.Add(toggle);
            }
            return rows;
        }

        private static bool AllHidden(List<IBundledMod> mods)
        {
            foreach (IBundledMod mod in mods)
                if (!BundledSettings.HidesButton(mod.Id)) return false;
            return mods.Count > 0;
        }

        // One button or several: the sentence around the list follows the
        // list.
        private static string Buttons(int count)
        {
            return count > 1 ? "buttons of " : "button of ";
        }

        // A profile bundles the mods here and makes ReDefinition active; later --
        // or a profile that is not there -- leaves every mod its own button and
        // window until a profile is chosen in the window.
        private static void Choose(List<IBundledMod> mods, string profileName)
        {
            BundledSettings.MarkAsked(mods);
            List<string> problems = new List<string>();
            List<GraphicsProfile> profiles = null;
            GraphicsProfile profile = null;
            if (profileName != null)
            {
                profiles = ProfileApplier.Ordered(problems);
                profile = ProfileApplier.Find(profiles, profileName);
                if (profile == null)
                    problems.Add("The profile '" + profileName + "' is not in the GameDatabase -- nothing was set.");
            }

            BundledSettings.SetEnabled(profile != null);
            ToolbarTakeover.Refresh();
            if (profile == null)
            {
                Debug.Log(Log.Tag + " Main menu notice answered: "
                          + (profileName == null ? "later" : "the profile '" + profileName + "', which is not there")
                          + " -- nothing set.");
                ProfileApplier.Report("Graphics profile from the main menu", problems);
                return;
            }

            Debug.Log(Log.Tag + " Main menu notice answered: take control, with the profile '" + profileName + "'.");
            ModuleProfiles.ApplyNow(profile, profiles, problems);
            Debug.Log(Log.Tag + " Graphics profile '" + profile.Title + "' applied from the main menu.");
            ProfileApplier.Report("Graphics profile from the main menu", problems);

            UiScaleForTheScreen();
            ReDefinitionAddon addon = ReDefinitionAddon.Instance;
            if (addon == null) return;
            addon.TakeKspSettings();
            // The menu is built anew, so that its Settings entry leads to the window.
            HighLogic.LoadScene(GameScenes.MAINMENU);
        }

        // KSP's UI scale to its default for this screen (KspBehaviour.Default), where
        // it still holds KSP's own 100 %: a scale the player chose stays.
        private static void UiScaleForTheScreen()
        {
            BundledSetting setting = BundledSettings.Find("ksp.UI_SCALE");
            if (setting == null) return;
            try
            {
                string now = setting.Read();
                string wanted = null;
                Dictionary<string, string> ksp;
                if (ModDefaults.Values(new List<string>()).TryGetValue("ksp", out ksp)) ksp.TryGetValue("UI_SCALE", out wanted);
                if (now == null || wanted == null || !SettingValues.Same(now, "1") || SettingValues.Same(now, wanted)) return;
                setting.Write(wanted);
                setting.Owner.Save();
                Debug.Log(Log.Tag + " KSP's UI scale set to " + wanted + " for the screen's height.");
            }
            catch (System.Exception e)
            {
                Debug.LogWarning(Log.Tag + " KSP's UI scale could not be set for the screen: " + e.Message);
            }
        }
    }
}
