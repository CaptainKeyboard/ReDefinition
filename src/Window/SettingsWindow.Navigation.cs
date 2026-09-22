using System.Collections.Generic;
using System;
using ReDefinition.Settings;
using TMPro;
using UnityEngine;

namespace ReDefinition.Window
{
    // How the settings window is found through: the categories as a tree -- the
    // graphics' and the controls' tabs indented under Graphics and Controls, and
    // shown only while one of theirs is open -- a search over every setting, a
    // mark on rows that differ from their default and on categories where
    // something waits for Apply, and a line that sets ReDefinition's own tabs
    // apart.
    internal static partial class SettingsWindow
    {
        private const float TabIndent = 16f;
        private const string MarkColor = "#8fc8ff";
        // U+2022, which the dialogs' font holds.
        private const string Mark = "•";

        // The graphics' tabs under Graphics, the controls' under Controls.
        private static SettingCategory? ParentOf(SettingCategory category)
        {
            switch (category)
            {
                case SettingCategory.Display:
                case SettingCategory.General:
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
        private static bool InDetail(SettingCategory category)
        {
            return category == SettingCategory.ShadowsAndReflections || category == SettingCategory.Planets
                   || category == SettingCategory.Effects;
        }

        // A child tab stands while its parent or one of its siblings is open.
        private static bool TabShown(SettingCategory category)
        {
            SettingCategory? parent = ParentOf(category);
            return parent == null || current == parent.Value || ParentOf(current) == parent;
        }

        // ------------------------------------------------------------ search

        // What the search matches a row by, per row, and every text of a page.
        private static readonly Dictionary<DialogGUIBase, string> searchTexts = new Dictionary<DialogGUIBase, string>();
        private static readonly Dictionary<SettingCategory, List<string>> pageTexts =
            new Dictionary<SettingCategory, List<string>>();
        // The rows a fold shows and hides itself, while searching too.
        private static readonly HashSet<DialogGUIBase> foldRows = new HashSet<DialogGUIBase>();
        // The category the rows being built belong to.
        private static SettingCategory building;

        private static bool Searching
        {
            get { return keySearch.Length > 0; }
        }

        private static DialogGUIBase Searchable(DialogGUIBase row, string text)
        {
            if (row == null) return null;
            searchTexts[row] = text;
            List<string> texts;
            if (!pageTexts.TryGetValue(building, out texts))
            {
                texts = new List<string>();
                pageTexts[building] = texts;
            }
            texts.Add(text);
            return row;
        }

        private static bool RowMatches(DialogGUIBase row)
        {
            string text;
            return searchTexts.TryGetValue(row, out text) && MatchesSearch(text, null);
        }

        private static string pageMatchesFor;
        private static readonly HashSet<SettingCategory> pagesMatching = new HashSet<SettingCategory>();

        private static bool PageMatches(SettingCategory category)
        {
            if (pageMatchesFor != keySearch)
            {
                pageMatchesFor = keySearch;
                pagesMatching.Clear();
                foreach (KeyValuePair<SettingCategory, List<string>> page in pageTexts)
                {
                    foreach (string text in page.Value)
                    {
                        if (!MatchesSearch(text, null)) continue;
                        pagesMatching.Add(page.Key);
                        break;
                    }
                }
            }
            return pagesMatching.Contains(category);
        }

        // Rows the search has no text for stand only while nothing is searched;
        // the Keys tab's rows filter themselves (KspBindingRow and the others).
        private static void MakeSearchable(SettingCategory category, IEnumerable<DialogGUIBase> rows)
        {
            foreach (DialogGUIBase row in rows)
            {
                if (foldRows.Contains(row)) continue;
                bool known = searchTexts.ContainsKey(row);
                if (category == SettingCategory.Keys && !known) continue;
                DialogGUIBase shown = row;
                Func<bool> before = row.OptionEnabledCondition;
                row.OptionEnabledCondition = () => (!Searching || (known && RowMatches(shown)))
                                                   && (before == null || before());
            }
        }

        // The field above the categories, and x that empties it.
        private static DialogGUIBase SearchRow()
        {
            DialogGUITextInput input = null;
            input = new DialogGUITextInput("", "Search", false, 64, text =>
            {
                keySearch = (text ?? "").Trim();
                return text;
            }, TabWidth - 26f, 24f);
            DialogGUIButton clear = new DialogGUIButton("x", () =>
            {
                keySearch = "";
                TMP_InputField field = input != null && input.uiItem != null
                    ? input.uiItem.GetComponentInChildren<TMP_InputField>()
                    : null;
                if (field != null) field.text = "";
            }, 22f, 24f, false);
            clear.tooltipText = "Empties the search.";
            return new DialogGUIHorizontalLayout(TabWidth, 24f, 4f, new RectOffset(), TextAnchor.MiddleLeft, input, clear);
        }

        // A page's name above its matches while searching.
        private static DialogGUIBase PageHeading(SettingCategory category)
        {
            SettingCategory? parent = ParentOf(category);
            string title = parent != null ? Title(parent.Value) + " / " + Title(category) : Title(category);
            DialogGUIBase heading = SectionHeading(title);
            heading.OptionEnabledCondition = () => Searching;
            return heading;
        }

        // ------------------------------------------------------------ marks

        // Each shown setting's default, as Reset fills it in; worked out as the
        // window is built.
        private static Dictionary<BundledSetting, string> rowDefaults = new Dictionary<BundledSetting, string>();

        private static void LoadRowDefaults()
        {
            try
            {
                rowDefaults = ProfileApplier.DefaultValues(new List<string>());
            }
            catch (Exception)
            {
                rowDefaults = new Dictionary<BundledSetting, string>();
            }
        }

        // The row's name, marked where its value differs from its default.
        private static string MarkedTitle(BundledSetting setting)
        {
            string value;
            string shipped;
            if (model.TryGetPending(setting.Key, out value) && rowDefaults.TryGetValue(setting, out shipped)
                && shipped != null && value != null && !SettingValues.Same(value, shipped))
                return "<color=" + MarkColor + ">" + Mark + "</color> " + setting.Title;
            return setting.Title;
        }

        // The keys of the bundled rows by the tab they stand in, for the tabs' marks.
        private static readonly Dictionary<SettingCategory, List<string>> keysIn =
            new Dictionary<SettingCategory, List<string>>();

        private static void NoteKey(SettingCategory category, string key)
        {
            List<string> keys;
            if (!keysIn.TryGetValue(category, out keys))
            {
                keys = new List<string>();
                keysIn[category] = keys;
            }
            keys.Add(key);
        }

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
                if (UpscalerPending()) pendingIn.Add(SettingCategory.General);
                if (model.Profile != BundledSettings.ProfileName || model.Resetting) pendingIn.Add(SettingCategory.Profiles);
                if (kspPending.Count > 0 || layoutPending != null) pendingIn.Add(SettingCategory.Keys);
                if (AxesDiffer()) pendingIn.Add(SettingCategory.Axes);
                if (model.Bundled != BundledSettings.Enabled || model.ButtonsPending()) pendingIn.Add(SettingCategory.Interface);
                foreach (SettingCategory child in new List<SettingCategory>(pendingIn))
                {
                    SettingCategory? parent = ParentOf(child);
                    if (parent != null) pendingIn.Add(parent.Value);
                }
            }
            return pendingIn.Contains(category);
        }

        private static bool AxesDiffer()
        {
            foreach (KspAxes.Axis axis in KspAxes.All())
            {
                AxisBinding copy;
                if (axisPending.TryGetValue(axis.Name, out copy) && !KspAxes.Same(axis.Get(), copy)) return true;
            }
            return false;
        }

        // ------------------------------------------------------------ the tab list

        // A category's button, indented under its parent, its name marked while
        // something in it waits for Apply. KSP's toggle button takes its label
        // once, so the text object is set anew where the mark comes or goes.
        private static DialogGUIBase TabEntry(SettingCategory category, Callback<bool> chosen)
        {
            SettingCategory shown = category;
            bool child = ParentOf(shown) != null;
            float width = child ? TabWidth - TabIndent : TabWidth;
            DialogGUIToggleButton tab = new DialogGUIToggleButton(() => current == shown, Title(shown), chosen, width, 28f);
            string lastLabel = null;
            tab.OnUpdate = () =>
            {
                string label = CategoryPending(shown)
                    ? Title(shown) + " <color=" + MarkColor + ">" + Mark + "</color>"
                    : Title(shown);
                if (label == lastLabel || tab.uiItem == null) return;
                TextMeshProUGUI text = tab.uiItem.GetComponentInChildren<TextMeshProUGUI>();
                if (text == null) return;
                text.text = label;
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
        private static DialogGUIBase TabSeparator()
        {
            return new DialogGUIVerticalLayout(TabWidth, 12f, 0f, new RectOffset(0, 0, 5, 5), TextAnchor.MiddleCenter,
                new DialogGUIImage(new Vector2(TabWidth - 10f, 1f), Vector2.zero, new Color(0.6f, 0.6f, 0.6f, 0.6f),
                    Texture2D.whiteTexture));
        }
    }
}
