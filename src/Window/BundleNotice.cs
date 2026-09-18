using System.Collections.Generic;
using System.Collections;
using ReDefinition.Core;
using ReDefinition.Settings;
using UnityEngine;

namespace ReDefinition.Window
{
    // The main menu's word about ReDefinition. The first time: ReDefinition does
    // nothing until a graphics profile is chosen, so the question is High now or
    // later -- High bundles the graphics mods here and switches the upscaler on,
    // later leaves every mod as it is. A mod installed after that follows the
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

                MultiOptionDialog note = new MultiOptionDialog("ReDefinitionBundleNote",
                    "The graphics settings of " + list + " are now in ReDefinition's settings window as well"
                    + (withButton.Count > 0 ? ", and the toolbar " + Buttons(withButton.Count) + buttonList
                                              + (withButton.Count > 1 ? " are hidden" : " is hidden") : "")
                    + " -- as you chose for the other graphics mods. "
                    + "You can change this under \"Mods and toolbar\" in the window.",
                    "Graphics settings in one place", HighLogic.UISkin, 420f,
                    new DialogGUIButton("OK", () => { }, true));
                UnityMouseEvents.Shield(PopupDialog.SpawnPopupDialog(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                    note, false, HighLogic.UISkin));
                Debug.Log(Log.Tag + " Main menu note: " + list + " bundled too, as chosen before.");
                yield break;
            }

            string message =
                "ReDefinition brings FSR 3 upscaling and frame generation to KSP, and the graphics settings of " + list
                + " into one window, sorted by feature -- open it with ReDefinition's toolbar button.\n\n"
                + "It does nothing until a graphics profile is chosen. A profile sets these mods up to work together"
                + " with FSR and switches the upscaler on. High is made for an RTX 3080 or 4080 at 1440p; Low, Medium,"
                + " Ultra and Max are under \"Profiles\" in the window.\n\n"
                + "With a profile, their settings are changed from ReDefinition's window and saved in each mod, as its"
                + " own window would save them"
                + (withButton.Count > 0
                    ? "; the toolbar " + Buttons(withButton.Count) + buttonList
                      + (withButton.Count > 1 ? " are hidden" : " is hidden")
                    : "")
                + ". ReDefinition keeps what each had before, and \"Restore settings from before ReDefinition\" under"
                + " \"Mods and toolbar\" brings it back.";

            MultiOptionDialog dialog = new MultiOptionDialog("ReDefinitionBundleNotice", message,
                "ReDefinition", HighLogic.UISkin, 540f,
                new DialogGUIButton("Use High", () => Choose(fresh, "high"), true),
                new DialogGUIButton("Later", () => Choose(fresh, null), true));
            // Shielded: the main menu's entries behind it take Unity's mouse
            // events, which no dialog locks.
            UnityMouseEvents.Shield(PopupDialog.SpawnPopupDialog(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                dialog, false, HighLogic.UISkin));
            Debug.Log(Log.Tag + " Main menu notice about ReDefinition shown for " + list + ".");
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

            Debug.Log(Log.Tag + " Main menu notice answered: the profile '" + profileName + "'.");
            ProfileApplier.ApplyNow(profile, profiles, problems);
            Debug.Log(Log.Tag + " Graphics profile '" + profile.Title + "' applied from the main menu.");
            ProfileApplier.Report("Graphics profile from the main menu", problems);
        }
    }
}
