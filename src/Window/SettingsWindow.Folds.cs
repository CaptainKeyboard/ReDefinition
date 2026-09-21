using System.Collections.Generic;
using System;
using ReDefinition.Settings;

namespace ReDefinition.Window
{
    // Groups that open and fold, for the long tabs -- Gameplay, Axes, Devices --
    // as the Keys tab's sections do: a header button with the group's name and
    // how many rows it holds, and the rows shown only while it is open. The first
    // group of a tab is open at first, the others folded; kept as the player
    // left them for the run.
    internal static partial class SettingsWindow
    {
        private static readonly HashSet<string> openFolds = new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<string> knownFolds = new HashSet<string>(StringComparer.Ordinal);

        private static bool Folding(SettingCategory category)
        {
            return category == SettingCategory.Gameplay || category == SettingCategory.Axes
                   || category == SettingCategory.Devices;
        }

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

            string folded = "+  " + title + " (" + members.Count + ")";
            string open = "-  " + title + " (" + members.Count + ")";
            DialogGUIButton header = new DialogGUIButton(() => FoldOpen(fold) ? open : folded, () =>
            {
                if (!openFolds.Remove(fold)) openFolds.Add(fold);
            }, PageWidth - 60f, RowHeight + 6f, false);
            header.tooltipText = "Opens or folds " + title + ".";
            rows.Add(header);

            foreach (DialogGUIBase member in members)
            {
                Func<bool> before = member.OptionEnabledCondition;
                member.OptionEnabledCondition = () => FoldOpen(fold) && (before == null || before());
                rows.Add(member);
            }
        }
    }
}
