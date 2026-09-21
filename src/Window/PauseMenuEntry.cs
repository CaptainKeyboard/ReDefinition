using System.Reflection;
using System;
using KSP.Localization;
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

        // The labels of KSP's own Settings button: in the space centre's menu
        // (KSCPauseMenu.draw) and in flight's (PauseMenu.draw).
        private static readonly string[] SettingsLabels = { "#autoLOC_417154", "#autoLOC_360624" };

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

                string[] settings = Array.ConvertAll(SettingsLabels, label => Localizer.Format(label));
                if (Replacing())
                {
                    TakeOverSettings(__result, settings);
                    return;
                }
                for (int i = 0; i < __result.Length; i++)
                {
                    if (IsSettings(__result[i], settings))
                    {
                        __result = Inserted(__result, i + 1, Entry(__result[i]));
                        return;
                    }

                    DialogGUIBase column;
                    int at;
                    if (Holding(__result[i], settings, out column, out at))
                    {
                        column.children.Insert(at + 1, Entry(column.children[at]));
                        return;
                    }
                }

                __result = Inserted(__result, BeforeLastSpace(__result), Entry(null));
            }
            catch (Exception e)
            {
                Debug.LogWarning(Log.Tag + " The pause menu's ReDefinition entry could not be built: " + e);
            }
        }

        // The entry goes directly under KSP's own Settings. In the space centre's
        // menu that button is an entry of the menu's array, one column
        // (KSCPauseMenu.draw); in flight's it is in the right of two columns,
        // a vertical layout inside one entry (PauseMenu.draw), and the entry goes
        // into that column. The buttons after Settings move down one place.
        // Without a Settings button the entry goes below the menu's buttons,
        // above the line with the game's version: both menus end with a space
        // and that line.
        private static DialogGUIButton Entry(DialogGUIBase besides)
        {
            // A sized DialogGUIButton keeps its size in `size`; `width` and
            // `height` stay -1 (DialogGUIButton's constructors, decompiled).
            float width = besides != null && besides.size.x > 0f ? besides.size.x : ButtonWidth;
            float height = besides != null && besides.size.y > 0f ? besides.size.y : ButtonHeight;
            DialogGUIButton open = new DialogGUIButton("ReDefinition", Open, width, height, false);
            open.tooltipText = "All of KSP's settings, ReDefinition's and those of the graphics mods it bundles.";
            return open;
        }

        // With *Replace original settings* on: KSP's own Settings button opens
        // ReDefinition's window, its callback swapped, and no entry is added.
        private static bool Replacing()
        {
            ReDefinitionAddon addon = ReDefinitionAddon.Instance;
            return addon != null && addon.ReplaceKspSettings;
        }

        private static void TakeOverSettings(DialogGUIBase[] entries, string[] settings)
        {
            foreach (DialogGUIBase entry in entries)
            {
                if (IsSettings(entry, settings))
                {
                    ((DialogGUIButton)entry).onOptionSelected = Open;
                    return;
                }
                DialogGUIBase column;
                int at;
                if (Holding(entry, settings, out column, out at))
                {
                    ((DialogGUIButton)column.children[at]).onOptionSelected = Open;
                    return;
                }
            }
        }

        private static bool IsSettings(DialogGUIBase entry, string[] settings)
        {
            DialogGUIButton button = entry as DialogGUIButton;
            return button != null && Array.IndexOf(settings, button.OptionText) >= 0;
        }

        // The layout among the entry's descendants that holds Settings, and where.
        private static bool Holding(DialogGUIBase entry, string[] settings, out DialogGUIBase column, out int at)
        {
            column = null;
            at = -1;
            if (entry == null || entry.children == null) return false;

            for (int i = 0; i < entry.children.Count; i++)
            {
                if (IsSettings(entry.children[i], settings))
                {
                    column = entry;
                    at = i;
                    return true;
                }
                if (Holding(entry.children[i], settings, out column, out at)) return true;
            }
            return false;
        }

        private static DialogGUIBase[] Inserted(DialogGUIBase[] entries, int at, DialogGUIBase entry)
        {
            DialogGUIBase[] withEntry = new DialogGUIBase[entries.Length + 1];
            Array.Copy(entries, 0, withEntry, 0, at);
            withEntry[at] = entry;
            Array.Copy(entries, at, withEntry, at + 1, entries.Length - at);
            return withEntry;
        }

        private static int BeforeLastSpace(DialogGUIBase[] entries)
        {
            for (int i = entries.Length - 1; i >= 0; i--)
                if (entries[i] is DialogGUISpace) return i;
            return entries.Length;
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
