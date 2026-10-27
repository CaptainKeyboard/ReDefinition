using System.Collections.Generic;
using System;
using ReDefinition.Settings;
using ReDefinition.Ui;

namespace ReDefinition.Window
{
    // Groups that open and fold, for the long tabs -- Gameplay, Axes, Devices --
    // as the Keys tab's sections do: a header button with the group's name, and
    // the rows shown only while it is open. The first
    // group of a tab is open at first, the others folded; kept as the player
    // left them for the run.
    internal static partial class SettingsWindow
    {
        // The folds open now, and those built once already, by tab and title: kept for the run.
        private static readonly HashSet<string> openFolds = new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<string> knownFolds = new HashSet<string>(StringComparer.Ordinal);

        // Whether the tab's groups fold: the long ones.
        private static bool Folding(SettingCategory category)
        {
            return category == SettingCategory.Gameplay || category == SettingCategory.Axes
                   || category == SettingCategory.Devices;
        }

        // Asked for every header in every frame while searching: made once, where the
        // method group would be a new delegate each time.
        private static readonly Predicate<DialogGUIBase> RowMatchesCall = RowMatches;

        // Whether the fold is open.
        private static bool FoldOpen(string fold)
        {
            return openFolds.Contains(fold);
        }

        // A group's rows under its header: each row shown only while the group is open.
        private static void AddFold(List<DialogGUIBase> rows, SettingCategory category, string title,
                                    List<DialogGUIBase> members, bool first)
        {
            if (members.Count == 0) return;
            string fold = category + "/" + title;
            if (knownFolds.Add(fold) && first) openFolds.Add(fold);

            List<DialogGUIBase> group = new List<DialogGUIBase>(members);
            // While searching, a group stands open where one of its rows matches,
            // with only those rows; its button then folds nothing.
            DialogGUIButton header = DialogRows.FoldHeader(title, () => FoldOpen(fold) || Searching, () =>
            {
                if (!openFolds.Remove(fold)) openFolds.Add(fold);
            }, PageWidth - 60f, RowHeight + 6f, "Opens or folds " + title + ".");
            header.OptionEnabledCondition = () => !Searching || group.Exists(RowMatchesCall);
            header.OptionInteractableCondition = () => !Searching;
            rows.Add(header);
            foldRows.Add(header);

            foreach (DialogGUIBase member in members)
            {
                DialogGUIBase shown = member;
                Func<bool> before = member.OptionEnabledCondition;
                member.OptionEnabledCondition = () => (Searching ? RowMatches(shown) : FoldOpen(fold))
                                                      && (before == null || before());
                rows.Add(member);
                foldRows.Add(member);
            }
        }
    }
}
