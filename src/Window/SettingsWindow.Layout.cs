using System.Collections.Generic;
using System;
using KSP.Localization;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Window
{
    // KSP's keyboard layout, as its input screen offers it (SettingsLayoutConfig,
    // SettingsKeyboardLayoutOs, SettingsKeyboardLayoutInput, decompiled): the
    // system's layout as a line, the layout chosen among
    // GameSettings.KeyboardLayouts, and binding the keys to that layout's preset.
    // KSP's Apply keeps the chosen layout (CURRENT_LAYOUT_SETTINGS) and loads it
    // (LoadLayoutKeyBindings); rebinding sets every key to the preset's. Here both
    // wait for Apply like every other row.
    internal static partial class SettingsWindow
    {
        // The layout chosen in the window, and whether the keys are to be bound
        // to its preset; null and false while nothing waits.
        private static string layoutPending;
        private static bool rebindPending;

        private static List<string> LayoutNames()
        {
            List<string> names = new List<string>();
            try
            {
                if (GameSettings.KeyboardLayouts != null) names.AddRange(GameSettings.KeyboardLayouts.Keys);
            }
            catch (Exception)
            {
                // None to offer.
            }
            return names;
        }

        // The layout KSP holds: the one chosen, else the one it detects.
        private static string LayoutNow()
        {
            try
            {
                return !string.IsNullOrEmpty(GameSettings.CURRENT_LAYOUT_SETTINGS)
                    ? GameSettings.CURRENT_LAYOUT_SETTINGS
                    : GameSettings.DetectKeyboardLayout();
            }
            catch (Exception)
            {
                return "";
            }
        }

        private static string LayoutShown()
        {
            return layoutPending ?? LayoutNow();
        }

        // What KSP calls a layout: its KEYBOARD_LAYOUT node's name.
        private static string LayoutTitle(string layout)
        {
            try
            {
                ConfigNode node;
                if (GameSettings.KeyboardLayouts != null && GameSettings.KeyboardLayouts.TryGetValue(layout, out node))
                {
                    ConfigNode info = node.GetNode("KEYBOARD_LAYOUT");
                    string name = info != null ? info.GetValue("name") : null;
                    if (!string.IsNullOrEmpty(name)) return name;
                }
            }
            catch (Exception)
            {
                // The key it is stored under.
            }
            return string.IsNullOrEmpty(layout) ? "--" : layout;
        }

        private static DialogGUIBase[] LayoutRows()
        {
            List<string> names = LayoutNames();
            List<DialogGUIBase> rows = new List<DialogGUIBase> { SectionHeading(Localizer.Format("#autoLOC_6001210")) };

            string system = "";
            try
            {
                KeyboardLayout os = KeyboardLayout.GetKeyboardLayout();
                system = Localizer.Format("#autoLOC_6001209", os.Type, os.Locale.NativeName).Replace("\n", "   ");
            }
            catch (Exception)
            {
                system = "--";
            }
            rows.Add(new DialogGUIHorizontalLayout(0f, RowHeight, 0f, new RectOffset(), TextAnchor.MiddleLeft,
                new DialogGUILabel("System keyboard", NameWidth), new DialogGUILabel(system, true)));

            if (names.Count == 0)
            {
                rows.Add(new DialogGUILabel("<color=#9a9a9a>KSP has no keyboard layouts to choose from here.</color>", true));
                return rows.ToArray();
            }

            DialogGUIButton previous = new DialogGUIButton("<", () => StepLayout(-1), 24f, RowHeight + 4f, false);
            DialogGUIButton next = new DialogGUIButton(">", () => StepLayout(1), 24f, RowHeight + 4f, false);
            previous.tooltipText = next.tooltipText =
                "The layout KSP reads key names with. Change it only where the system keyboard does not match it.";
            rows.Add(new DialogGUIHorizontalLayout(0f, RowHeight + 4f, 4f, new RectOffset(), TextAnchor.MiddleLeft,
                new DialogGUILabel("Keyboard layout", NameWidth - 4f), previous,
                new DialogGUILabel(() => LayoutTitle(LayoutShown()), ControlWidth - 56f), next));

            DialogGUIButton rebind = new DialogGUIButton("Bind keys to this layout ...", ConfirmRebind, ControlWidth + 60f,
                RowHeight + 6f, false);
            rebind.tooltipText = "Sets every one of KSP's keys to the chosen layout's preset at Apply.";
            rows.Add(new DialogGUIHorizontalLayout(0f, RowHeight + 6f, 0f, new RectOffset(), TextAnchor.MiddleLeft,
                new DialogGUISpace(NameWidth), rebind,
                new DialogGUILabel(() => rebindPending ? "<color=#f18a24>  at Apply</color>" : "", true)));
            return rows.ToArray();
        }

        private static void StepLayout(int step)
        {
            List<string> names = LayoutNames();
            if (names.Count == 0) return;
            int at = names.IndexOf(LayoutShown());
            at = at < 0 ? 0 : (at + step + names.Count) % names.Count;
            layoutPending = names[at] == LayoutNow() && !rebindPending ? null : names[at];
        }

        // KSP asks before it rebinds (#autoLOC_6001211).
        private static void ConfirmRebind()
        {
            string layout = LayoutShown();
            MultiOptionDialog confirm = new MultiOptionDialog("ReDefinitionRebindLayout",
                Localizer.Format("#autoLOC_6001211", LayoutTitle(layout)), "Keyboard layout", HighLogic.UISkin, 380f,
                new DialogGUIHorizontalLayout(
                    new DialogGUIButton("Bind at Apply", () =>
                    {
                        layoutPending = layout;
                        rebindPending = true;
                    }, 140f, 30f, true),
                    new DialogGUIFlexibleSpace(),
                    new DialogGUIButton("Cancel", () => { }, 100f, 30f, true)));
            UnityMouseEvents.Shield(PopupDialog.SpawnPopupDialog(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                confirm, false, HighLogic.UISkin, true));
        }

        // Before KSP's keys from the Keys tab: a rebind sets them all to the
        // preset, and a key changed by hand in the same Apply comes after it.
        private static void ApplyLayout()
        {
            if (layoutPending == null) return;
            string layout = layoutPending;
            bool rebind = rebindPending;
            layoutPending = null;
            rebindPending = false;
            GameSettings.CURRENT_LAYOUT_SETTINGS = layout;
            GameSettings.LoadLayoutKeyBindings(layout, rebind);
            GameSettings.SaveSettings();
            Debug.Log(Log.Tag + " KSP's keyboard layout: " + layout + (rebind ? ", keys bound to its preset." : "."));
        }

        private static void ClearLayoutPending()
        {
            layoutPending = null;
            rebindPending = false;
        }
    }
}
