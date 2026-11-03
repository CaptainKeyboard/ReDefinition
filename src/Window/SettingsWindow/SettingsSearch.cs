using System;
using System.Collections.Generic;
using ReDefinition.Settings;
using TMPro;
using UnityEngine;
using static ReDefinition.Window.SettingsWindow;

namespace ReDefinition.Window
{
    // The search over every setting of the settings window: the field above the tabs, the words typed into it,
    // and which rows, groups and pages they leave standing. Rows are noted as the window is built, and
    // everything noted is dropped when it is built again.
    internal static class SettingsSearch
    {
        // What is typed into the window's search field, trimmed; the Keys tab's rows and every other tab filter by it.
        internal static string Text = "";

        // Both a row's own name and its mod's are searched: "camera" finds KSP's
        // camera rows, "scatterer" the mod's. Each word of the search is looked for
        // on its own, in any order: "wobble camera" finds "Camera wobble external",
        // "scatterer shadows" Scatterer's terrain shadows.
        internal static bool Matches(string title, string owner)
        {
            if (Text.Length == 0) return true;
            foreach (string word in SearchWords())
            {
                bool found = (title != null && title.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0)
                             || (owner != null && owner.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0);
                if (!found) return false;
            }
            return true;
        }

        // Whether a row of that name and owner stands for what is typed, as the row's enabled condition asks it in
        // every frame: worked out again only when the search changes.
        internal static Func<bool> Filter(string title, string owner)
        {
            string madeFor = null;
            bool matches = true;
            return () =>
            {
                if (!string.Equals(madeFor, Text, StringComparison.Ordinal))
                {
                    madeFor = Text;
                    matches = Matches(title, owner);
                }
                return matches;
            };
        }

        // The search's words, split once per search.
        private static string searchWordsFor;
        private static string[] searchWords = new string[0];

        // The search's words, split anew only when the search changed.
        private static string[] SearchWords()
        {
            if (searchWordsFor != Text)
            {
                searchWordsFor = Text;
                searchWords = Text.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            }
            return searchWords;
        }

        // What the search matches a row by, per row, and every text of a page.
        private static readonly Dictionary<DialogGUIBase, string> searchTexts = new Dictionary<DialogGUIBase, string>();
        private static readonly Dictionary<SettingCategory, List<string>> pageTexts =
            new Dictionary<SettingCategory, List<string>>();
        // The rows a fold shows and hides itself, while searching too.
        internal static readonly HashSet<DialogGUIBase> FoldRows = new HashSet<DialogGUIBase>();
        // The category the rows being built belong to.
        internal static SettingCategory Building;

        // Whether something is typed into the search field.
        internal static bool Searching
        {
            get { return Text.Length > 0; }
        }

        // Notes the row's search text, for the row and for the page being built; the row itself is returned.
        internal static DialogGUIBase Searchable(DialogGUIBase row, string text)
        {
            if (row == null) return null;
            searchTexts[row] = text;
            NotePageText(Building, text);
            return row;
        }

        // Notes a text the search finds the page by.
        internal static void NotePageText(SettingCategory category, string text)
        {
            List<string> texts;
            if (!pageTexts.TryGetValue(category, out texts))
            {
                texts = new List<string>();
                pageTexts[category] = texts;
            }
            texts.Add(text);
        }

        // Whether the row's search text matches what is typed. Every row asks in every frame while something is
        // typed, so each answer is kept until the search changes.
        internal static bool RowMatches(DialogGUIBase row)
        {
            if (!string.Equals(rowMatchesFor, Text, StringComparison.Ordinal))
            {
                rowMatchesFor = Text;
                rowMatches.Clear();
            }
            bool matches;
            if (rowMatches.TryGetValue(row, out matches)) return matches;
            string text;
            matches = searchTexts.TryGetValue(row, out text) && Matches(text, null);
            rowMatches[row] = matches;
            return matches;
        }

        // The search the rows' answers were worked out for, and the answers.
        private static string rowMatchesFor;
        private static readonly Dictionary<DialogGUIBase, bool> rowMatches = new Dictionary<DialogGUIBase, bool>();

        // The search the page matches were last worked out for, and the pages that matched it.
        private static string pageMatchesFor;
        private static readonly HashSet<SettingCategory> pagesMatching = new HashSet<SettingCategory>();

        // Whether any row of the page matches the search, worked out once per search.
        internal static bool PageMatches(SettingCategory category)
        {
            if (pageMatchesFor != Text)
            {
                pageMatchesFor = Text;
                pagesMatching.Clear();
                foreach (KeyValuePair<SettingCategory, List<string>> page in pageTexts)
                {
                    foreach (string text in page.Value)
                    {
                        if (!Matches(text, null)) continue;
                        pagesMatching.Add(page.Key);
                        break;
                    }
                }
            }
            return pagesMatching.Contains(category);
        }

        // Rows the search has no text for stand only while nothing is searched;
        // the Keys tab's rows filter themselves (KeysTab.KspBindingRow and the others).
        internal static void MakeSearchable(SettingCategory category, IEnumerable<DialogGUIBase> rows)
        {
            foreach (DialogGUIBase row in rows)
            {
                if (FoldRows.Contains(row)) continue;
                bool known = searchTexts.ContainsKey(row);
                if (category == SettingCategory.Keys && !known) continue;
                DialogGUIBase shown = row;
                Func<bool> before = row.OptionEnabledCondition;
                row.OptionEnabledCondition = () => (!Searching || (known && RowMatches(shown)))
                                                   && (before == null || before());
            }
        }

        // The field above the categories, and x that empties it.
        internal static DialogGUIBase Row()
        {
            DialogGUITextInput input = null;
            input = new DialogGUITextInput("", "Search", false, 64, text =>
            {
                Text = (text ?? "").Trim();
                return text;
            }, TabWidth - 26f, 24f);
            DialogGUIButton clear = new DialogGUIButton("x", () =>
            {
                Text = "";
                TMP_InputField field = input != null && input.uiItem != null
                    ? input.uiItem.GetComponentInChildren<TMP_InputField>()
                    : null;
                if (field != null) field.text = "";
            }, 22f, 24f, false);
            clear.tooltipText = "Empties the search.";
            return new DialogGUIHorizontalLayout(TabWidth, 24f, 4f, new RectOffset(), TextAnchor.MiddleLeft, input, clear);
        }

        // Empties what the last build noted, the typed search among it, as the window is built anew.
        internal static void Reset()
        {
            Text = "";
            searchTexts.Clear();
            pageTexts.Clear();
            FoldRows.Clear();
            pageMatchesFor = null;
            rowMatchesFor = null;
            rowMatches.Clear();
        }
    }
}
