using System.Collections.Generic;
using ReDefinition.Modules;
using ReDefinition.Settings;
using UnityEngine;
using static ReDefinition.Window.SettingsWindow;

namespace ReDefinition.Window
{
    // The tab column of the settings window: the tree of categories, which tabs stand, a tab's button with the
    // mark for a change that waits for Apply, and the line between ReDefinition's own tabs and the game's.
    internal static class SettingsTabs
    {
        // How far a child tab is indented.
        private const float TabIndent = 16f;

        // The graphics' tabs under Graphics, the controls' under Controls.
        internal static SettingCategory? ParentOf(SettingCategory category)
        {
            switch (category)
            {
                case SettingCategory.Display:
                case SettingCategory.Detail:
                    return SettingCategory.Profiles;
                case SettingCategory.Axes:
                case SettingCategory.Devices:
                    return SettingCategory.Keys;
                default:
                    return null;
            }
        }

        // Shown as groups of Detail, not as tabs: the registration format's
        // ShadowsAndReflections, Planets and Effects.
        internal static bool InDetail(SettingCategory category)
        {
            return category == SettingCategory.ShadowsAndReflections || category == SettingCategory.Planets
                   || category == SettingCategory.Effects;
        }

        // Categories with no tab of their own: Detail's groups, and General, which
        // is Display's upscaling section.
        internal static bool NoTab(SettingCategory category)
        {
            return InDetail(category) || category == SettingCategory.General;
        }

        // A child tab stands while its parent or one of its siblings is open.
        private static bool TabShown(SettingCategory category)
        {
            SettingCategory? parent = ParentOf(category);
            return parent == null || current == parent.Value || ParentOf(current) == parent;
        }

        // The first label among an element's descendants.
        private static DialogGUILabel LabelOf(DialogGUIBase element)
        {
            foreach (DialogGUIBase child in element.children)
            {
                DialogGUILabel label = child as DialogGUILabel ?? LabelOf(child);
                if (label != null) return label;
            }
            return null;
        }

        // A page's name above its matches while searching.
        internal static DialogGUIBase PageHeading(SettingCategory category)
        {
            SettingCategory? parent = ParentOf(category);
            string title = parent != null ? Title(parent.Value) + " / " + Title(category) : Title(category);
            DialogGUIBase heading = SectionHeading(title);
            heading.OptionEnabledCondition = () => SettingsSearch.Searching;
            return heading;
        }

        // The keys of the bundled rows by the tab they stand in, for the tabs' marks.
        private static readonly Dictionary<SettingCategory, List<string>> keysIn =
            new Dictionary<SettingCategory, List<string>>();

        // Notes a bundled row's key under the tab it stands in.
        internal static void NoteKey(SettingCategory category, string key)
        {
            List<string> keys;
            if (!keysIn.TryGetValue(category, out keys))
            {
                keys = new List<string>();
                keysIn[category] = keys;
            }
            keys.Add(key);
        }

        // When the pending tabs were last worked out, and those that were: asked by every tab every frame, worked out
        // four times a second.
        private static float pendingAt = -10f;
        private static readonly HashSet<SettingCategory> pendingIn = new HashSet<SettingCategory>();

        // Whether something in the category waits for Apply -- a child's counts
        // for its parent, which stands when the child is folded away.
        private static bool CategoryPending(SettingCategory category)
        {
            float now = Time.unscaledTime;
            if (now - pendingAt >= 0.25f)
            {
                pendingAt = now;
                pendingIn.Clear();
                foreach (KeyValuePair<SettingCategory, List<string>> pair in keysIn)
                {
                    foreach (string key in pair.Value)
                    {
                        if (!model.Changed(key)) continue;
                        pendingIn.Add(pair.Key);
                        break;
                    }
                }
                // ReDefinition's own: the rows of General stand in Display's
                // upscaling section, its hotkeys under Keys.
                if (OwnPending(SettingCategory.General, false)) pendingIn.Add(SettingCategory.Display);
                if (OwnPending(SettingCategory.General, true)) pendingIn.Add(SettingCategory.Detail);
                if (model.Profile != BundledSettings.ProfileName || model.Resetting) pendingIn.Add(SettingCategory.Profiles);
                if (KeysTab.Pending.Count > 0 || KeyboardLayoutRows.Pending != null || OwnPending(SettingCategory.Keys))
                    pendingIn.Add(SettingCategory.Keys);
                if (AxesTab.Differ()) pendingIn.Add(SettingCategory.Axes);
                if (model.Bundled != BundledSettings.Enabled || model.ButtonsPending()
                    || OwnPending(SettingCategory.Interface))
                    pendingIn.Add(SettingCategory.Interface);
                if (OwnPending(SettingCategory.Gameplay)) pendingIn.Add(SettingCategory.Gameplay);
                foreach (SettingCategory child in new List<SettingCategory>(pendingIn))
                {
                    SettingCategory? parent = ParentOf(child);
                    if (parent != null) pendingIn.Add(parent.Value);
                }
            }
            return pendingIn.Contains(category);
        }

        // Whether one of ReDefinition's own rows in a tab waits for Accept; under
        // Mods / Toolbar, Better Time Warp's button among the toolbar's too.
        private static bool OwnPending(SettingCategory category, bool detail = false)
        {
            if (edit == null || edit.Before == null || edit.After == null) return false;
            if (category == SettingCategory.Interface && edit.Before.TimeWarpToolbarButton != edit.After.TimeWarpToolbarButton)
                return true;
            foreach (ModuleSetting setting in OurModules.Rows(category, detail))
                if (setting.Read(edit.Before) != setting.Read(edit.After)) return true;
            return false;
        }

        // A category's button, indented under its parent, its name marked while
        // something in it waits for Apply. KSP's toggle button makes a label of
        // its text (DialogGUIToggleButton.Create: a DialogGUILabel in a layout
        // among its children), and that label writes its own text in every frame
        // (DialogGUILabel.Update): the mark goes into the label's text.
        internal static DialogGUIBase TabEntry(SettingCategory category, Callback<bool> chosen)
        {
            SettingCategory shown = category;
            bool child = ParentOf(shown) != null;
            float width = child ? TabWidth - TabIndent : TabWidth;
            // Both texts made once: the name does not change while the window stands (AnyNotShown).
            string plain = Title(shown);
            string withMark = plain + " <color=" + SettingMarks.MarkColor + ">" + SettingMarks.Mark + "</color>";
            DialogGUIToggleButton tab = new DialogGUIToggleButton(() => current == shown, plain, chosen, width, 28f);
            string lastLabel = null;
            tab.OnUpdate = () =>
            {
                string label = CategoryPending(shown) ? withMark : plain;
                if (label == lastLabel || tab.uiItem == null) return;
                DialogGUILabel text = LabelOf(tab);
                if (text == null) return;
                text.OptionText = label;
                lastLabel = label;
            };
            tab.tooltipText = child ? "Part of " + Title(ParentOf(shown).Value) + "." : "";

            DialogGUIBase entry = child
                ? (DialogGUIBase)new DialogGUIHorizontalLayout(TabWidth, 28f, 0f, new RectOffset(), TextAnchor.MiddleLeft,
                    new DialogGUISpace(TabIndent), tab)
                : tab;
            entry.OptionEnabledCondition = () => TabShown(shown);
            return entry;
        }

        // A thin line across the tab list.
        internal static DialogGUIBase TabSeparator()
        {
            return new DialogGUIVerticalLayout(TabWidth, 12f, 0f, new RectOffset(0, 0, 5, 5), TextAnchor.MiddleCenter,
                new DialogGUIImage(new Vector2(TabWidth - 10f, 1f), Vector2.zero, new Color(0.6f, 0.6f, 0.6f, 0.6f),
                    Texture2D.whiteTexture));
        }

        // Forgets the bundled rows' keys noted by the last build, as the window is built anew.
        internal static void Reset()
        {
            keysIn.Clear();
        }
    }
}
