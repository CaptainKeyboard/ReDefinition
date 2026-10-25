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
    // mark on rows and categories where a change waits for Apply, and a line that
    // sets ReDefinition's own tabs apart.
    internal static partial class SettingsWindow
    {
        // How far a child tab is indented, and the colour of the mark for a change waiting for Apply.
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

        // Categories with no tab of their own: Detail's groups, and General, which
        // is Display's upscaling section.
        private static bool NoTab(SettingCategory category)
        {
            return InDetail(category) || category == SettingCategory.General;
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

        // Whether something is typed into the search field.
        private static bool Searching
        {
            get { return keySearch.Length > 0; }
        }

        // Notes the row's search text, for the row and for the page being built; the row itself is returned.
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

        // Whether the row's search text matches what is typed.
        private static bool RowMatches(DialogGUIBase row)
        {
            string text;
            return searchTexts.TryGetValue(row, out text) && MatchesSearch(text, null);
        }

        // The search the page matches were last worked out for, and the pages that matched it.
        private static string pageMatchesFor;
        private static readonly HashSet<SettingCategory> pagesMatching = new HashSet<SettingCategory>();

        // Whether any row of the page matches the search, worked out once per search.
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

        // The row's name, marked while a change to it waits for Apply -- made by
        // hand, by a profile or by Reset.
        private static string MarkedTitle(BundledSetting setting)
        {
            return MarkedName(setting.Title, model.Changed(setting.Key), BundledDefault(setting) != null);
        }

        // A row's name with its marks, kept per name until either mark changes: KSP asks
        // for the label's text in every frame it shows.
        private sealed class MarkedText
        {
            // Whether the text was made changed, differing from the profile, and at all; and the text made.
            public bool Changed, Differs, Made;
            public string Text;
        }

        // The marked names made so far, by the row's name.
        private static readonly Dictionary<string, MarkedText> markedNames = new Dictionary<string, MarkedText>();

        // The name with the change mark and the profile mark where they apply, made anew only when one of them changes.
        internal static string MarkedName(string title, bool changed, bool differs)
        {
            MarkedText kept;
            if (!markedNames.TryGetValue(title, out kept))
            {
                kept = new MarkedText();
                markedNames[title] = kept;
            }
            if (!kept.Made || kept.Changed != changed || kept.Differs != differs)
            {
                kept.Text = ProfileMarked(Marked(title, changed), differs);
                kept.Changed = changed;
                kept.Differs = differs;
                kept.Made = true;
            }
            return kept.Text;
        }

        // A row that holds another value than the chosen profile gives it: a mark of its
        // own before the name, and its tooltip ends in "Default: X".
        private const string ProfileMarkColor = "#e0b040";

        // The name with the profile's mark before it where the row differs from the profile.
        internal static string ProfileMarked(string title, bool differs)
        {
            return differs ? "<color=" + ProfileMarkColor + ">" + Mark + "</color> " + title : title;
        }

        // The tooltip as the row was built with it, and "Default: X" below it where
        // `defaultOf` gives X: kept in the element, which KSP hands to its tooltip each
        // frame, and in the tooltip the window attached itself (DialogTooltips).
        internal static void DefaultTooltip(DialogGUIBase control, Func<string> defaultOf)
        {
            string plain = control.tooltipText ?? "";
            string shownDefault = null;
            Callback before = control.OnUpdate;
            control.OnUpdate = () =>
            {
                if (before != null) before();
                string now = defaultOf();
                if (now == shownDefault) return;
                shownDefault = now;
                control.tooltipText = now == null ? plain : plain + (plain.Length > 0 ? "\n" : "") + "Default: " + now;
                KSP.UI.TooltipTypes.TooltipController_Text tooltip =
                    control.uiItem != null ? control.uiItem.GetComponent<KSP.UI.TooltipTypes.TooltipController_Text>() : null;
                if (tooltip != null) tooltip.textString = control.tooltipText;
            };
        }

        // The chosen profile's values, by the bundled settings' keys and by ReDefinition's
        // own rows, for the profile last asked about.
        private static string defaultsOf;
        private static readonly Dictionary<string, string> bundledDefaults = new Dictionary<string, string>();
        private static readonly Dictionary<ModuleSetting, string> ownDefaults = new Dictionary<ModuleSetting, string>();

        // Reads the profile's values into the two tables where it is not the one read last; false where there is no such
        // profile or none is chosen.
        private static bool DefaultsFor(string profileName)
        {
            if (string.IsNullOrEmpty(profileName)) return false;
            if (defaultsOf == profileName) return true;
            GraphicsProfile profile = ProfileApplier.Find(profiles, profileName);
            if (profile == null) return false;
            bundledDefaults.Clear();
            foreach (KeyValuePair<BundledSetting, string> pair in ProfileApplier.Values(profile, new List<string>()))
                bundledDefaults[pair.Key.Key] = pair.Value;
            ownDefaults.Clear();
            foreach (KeyValuePair<ModuleSetting, string> pair in ModuleProfiles.Values(profile, null))
                ownDefaults[pair.Key] = pair.Value;
            defaultsOf = profileName;
            return true;
        }

        // The profile's value of a bundled row as the row shows values, where the row holds
        // another one; null where it holds the profile's, the profile has none, or none is
        // chosen.
        private static string BundledDefault(BundledSetting setting)
        {
            string profileValue, now;
            if (!DefaultsFor(model.Profile) || !bundledDefaults.TryGetValue(setting.Key, out profileValue)
                || !model.TryGetPending(setting.Key, out now) || SettingValues.Same(now, profileValue))
                return null;
            return profileValue;
        }

        // The profile's value of one of ReDefinition's own rows as the row shows it, where the row holds another one;
        // null otherwise.
        private static string OwnDefault(ModuleSetting setting, OwnSettings settings)
        {
            string profileValue;
            // The upscaler's switch a profile only turns on, as ModuleProfiles.Differences
            // leaves it to the player.
            if (settings == null || setting.OnlyActivates || !DefaultsFor(model.Profile)
                || !ownDefaults.TryGetValue(setting, out profileValue)
                || SettingValues.Same(setting.Normalize(setting.Read(settings)), profileValue))
                return null;
            // As the row shows its value (KspSettingsSection.Shown).
            if (setting.Label != null) return setting.Label(profileValue);
            bool on;
            return setting.Control == SettingControl.Toggle && bool.TryParse(profileValue, out on)
                ? KspSettingsSection.StateText(on) : profileValue;
        }

        // The name with the change mark before it while a change waits.
        private static string Marked(string title, bool changed)
        {
            return changed ? "<color=" + MarkColor + ">" + Mark + "</color> " + title : title;
        }

        // The keys of the bundled rows by the tab they stand in, for the tabs' marks.
        private static readonly Dictionary<SettingCategory, List<string>> keysIn =
            new Dictionary<SettingCategory, List<string>>();

        // Notes a bundled row's key under the tab it stands in.
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
                if (kspPending.Count > 0 || layoutPending != null || OwnPending(SettingCategory.Keys))
                    pendingIn.Add(SettingCategory.Keys);
                if (AxesDiffer()) pendingIn.Add(SettingCategory.Axes);
                if (model.Bundled != BundledSettings.Enabled || model.ButtonsPending() || OwnPending(SettingCategory.Interface))
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

        // Whether an axis row holds another binding than KSP's axis has now.
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
        // something in it waits for Apply. KSP's toggle button makes a label of
        // its text (DialogGUIToggleButton.Create: a DialogGUILabel in a layout
        // among its children), and that label writes its own text in every frame
        // (DialogGUILabel.Update): the mark goes into the label's text.
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
        private static DialogGUIBase TabSeparator()
        {
            return new DialogGUIVerticalLayout(TabWidth, 12f, 0f, new RectOffset(0, 0, 5, 5), TextAnchor.MiddleCenter,
                new DialogGUIImage(new Vector2(TabWidth - 10f, 1f), Vector2.zero, new Color(0.6f, 0.6f, 0.6f, 0.6f),
                    Texture2D.whiteTexture));
        }
    }
}
