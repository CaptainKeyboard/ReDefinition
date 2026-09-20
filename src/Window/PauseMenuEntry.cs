using System.Reflection;
using System;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Window
{
    // A ReDefinition button in the menu Escape opens, next to KSP's own.
    //
    // KSP has two of these menus, and both are patched: PauseMenu in flight and
    // KSCPauseMenu in the space centre (the two classes that build a dialog named
    // GamePaused; the editors and the tracking station have none). Each is a
    // PopupDialog whose contents come from its own draw(), answering a
    // DialogGUIBase[]. A Harmony postfix appends one button to that array, the way
    // the settings section appends its rows to the graphics part
    // (KspSettingsSection). Built from KSP's own dialog elements, so a UI theme
    // covers it.
    //
    // The toolbar is hidden while such a menu stands, so without this the window
    // can only be reached by a hotkey the player has bound.
    //
    // Harmony is a requirement (src/KspAssemblyInfo.cs).
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class PauseMenuEntry : MonoBehaviour
    {
        private const string HarmonyId = "ReDefinition.PauseMenuEntry";

        // KSP's own pause menu buttons are this wide and high (PauseMenu.draw).
        private const float ButtonWidth = 160f;
        private const float ButtonHeight = 30f;

        private void Awake()
        {
            try
            {
                Patch();
                Debug.Log(Log.Tag + " Entry added to the menu Escape opens.");
            }
            catch (Exception e)
            {
                Debug.LogWarning(Log.Tag + " Could not add the entry to the pause menu: " + e);
            }

            Destroy(gameObject);
        }

        private static void Patch()
        {
            MethodInfo postfix = typeof(PauseMenuEntry).GetMethod(nameof(DrawPostfix),
                BindingFlags.NonPublic | BindingFlags.Static);
            if (postfix == null)
                throw new MissingMethodException("PauseMenuEntry", nameof(DrawPostfix));

            HarmonyLib.Harmony harmony = new HarmonyLib.Harmony(HarmonyId);
            Patch(harmony, postfix, typeof(PauseMenu));
            Patch(harmony, postfix, typeof(KSCPauseMenu));
        }

        private static void Patch(HarmonyLib.Harmony harmony, MethodInfo postfix, Type menu)
        {
            MethodInfo original = menu.GetMethod("draw",
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (original == null)
                throw new MissingMethodException(menu.Name, "draw");

            harmony.Patch(original, postfix: new HarmonyLib.HarmonyMethod(postfix));
        }

        // Inside KSP's own dialog code: an exception escaping here would take the
        // pause menu with it, and with it the way out of a flight.
        private static void DrawPostfix(ref DialogGUIBase[] __result)
        {
            try
            {
                if (__result == null) return;

                DialogGUIButton open = new DialogGUIButton("ReDefinition", Open, ButtonWidth, ButtonHeight, false);
                open.tooltipText = "The settings of ReDefinition and of the graphics mods it bundles.";

                DialogGUIBase[] withEntry = new DialogGUIBase[__result.Length + 1];
                Array.Copy(__result, withEntry, __result.Length);
                withEntry[__result.Length] = open;
                __result = withEntry;
            }
            catch (Exception e)
            {
                Debug.LogWarning(Log.Tag + " The pause menu's ReDefinition entry could not be built: " + e);
            }
        }

        // The window over the pause menu: the menu stays, so Escape still ends
        // where it did. Closing the window leaves the game exactly as it was.
        private static void Open()
        {
            try
            {
                if (!SettingsWindow.Visible) SettingsWindow.Toggle();
            }
            catch (Exception e)
            {
                Debug.LogWarning(Log.Tag + " The settings window could not be opened from the pause menu: " + e);
            }
        }
    }
}
