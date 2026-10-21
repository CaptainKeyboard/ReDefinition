using System.Collections.Generic;
using ReDefinition.Window;
using UnityEngine;

namespace ReDefinition
{
    // How the diagnostics window draws an option by its kind (DebugOption): a switch or a
    // choice as segments, an action as a button, each with its persistence marker and its
    // star; a row that does not apply greyed out with its reason beneath.
    public partial class ReDefinitionAddon
    {
        private const float MarkerWidth = 56f;
        private const float SegmentMinimum = 44f;

        // The options as rows, a heading where their group changes.
        private void DrawOptions(List<DebugOption> options, bool groups = true)
        {
            string group = "";
            foreach (DebugOption option in options)
            {
                if (option.Group != group)
                {
                    group = option.Group;
                    if (groups && group.Length > 0) Heading(group);
                }
                DrawOptionRow(option);
            }
        }

        private void Heading(string text)
        {
            GUILayout.Space(8f);
            GUILayout.Label(text, styles.Heading);
            GUILayout.Space(2f);
        }

        private void DrawOptionRow(DebugOption option)
        {
            string reason;
            optionReasons.TryGetValue(option.Id, out reason);
            bool blocked = reason != null;
            bool outer = GUI.enabled;

            GUILayout.BeginHorizontal();
            GUILayout.Label(new GUIContent(option.CurrentLabel, option.Tooltip), blocked ? styles.Dim : styles.Row);
            GUILayout.FlexibleSpace();
            GUI.enabled = outer && !blocked;
            DrawControl(option);
            GUI.enabled = outer;
            DrawStar(option);
            DrawMarker(option);
            GUILayout.EndHorizontal();

            if (blocked) GUILayout.Label(reason, styles.Reason);
        }

        // Whether the setting is kept, with a tooltip saying so.
        private void DrawMarker(DebugOption option)
        {
            if (option.Kind == DebugKind.Action)
            {
                GUILayout.Label(GUIContent.none, GUILayout.Width(MarkerWidth));
                return;
            }
            string text;
            string tip;
            switch (option.Persistence)
            {
                case DebugPersistence.Saved:
                    text = "saved";
                    tip = "Saved";
                    break;
                case DebugPersistence.NeedsRestart:
                    text = "restart";
                    tip = "Needs A Restart";
                    break;
                default:
                    text = "session";
                    tip = "This Session Only";
                    break;
            }
            GUILayout.Label(new GUIContent(text, tip), styles.Marker, GUILayout.Width(MarkerWidth));
        }

        private void DrawControl(DebugOption option)
        {
            if (option.Kind == DebugKind.Action)
            {
                if (GUILayout.Button(option.ActionText, styles.Segment, GUILayout.MinWidth(SegmentMinimum + 24f))) option.Run();
                return;
            }
            int value = option.Get();
            int picked = Segments(option.Choices, value);
            if (picked != value) option.Set(picked);
        }

        private void DrawStar(DebugOption option)
        {
            bool pinned = pins.Has(option.Id);
            string glyph = styles.StarGlyph ? (pinned ? "★" : "☆") : (pinned ? "*" : "+");
            if (GUILayout.Toggle(pinned, new GUIContent(glyph, pinned ? "Unpin" : "Pin"), styles.Star) != pinned)
                pendingPin = option.Id;
        }

        // A row of segments, the selected one pressed; the index clicked, or the selected one.
        private int Segments(string[] labels, int selected)
        {
            int result = selected;
            GUILayout.BeginHorizontal(GUILayout.ExpandWidth(false));
            for (int i = 0; i < labels.Length; i++)
                if (GUILayout.Toggle(selected == i, labels[i], styles.Segment, GUILayout.MinWidth(SegmentMinimum)) && selected != i)
                    result = i;
            GUILayout.EndHorizontal();
            return result;
        }

        // The pinned options as compact toggles at the window's foot, in rows that break where
        // the next one would not fit.
        private void DrawPinned(List<DebugOption> pinned, float width)
        {
            float used = 0f;
            bool open = false;
            foreach (DebugOption option in pinned)
            {
                float chip = ChipWidth(option);
                if (!open || used + chip > width)
                {
                    if (open) GUILayout.EndHorizontal();
                    GUILayout.BeginHorizontal();
                    open = true;
                    used = 0f;
                }
                DrawChip(option);
                used += chip + 6f;
                GUILayout.Space(6f);
            }
            if (open) GUILayout.EndHorizontal();
        }

        private float ChipWidth(DebugOption option)
        {
            GUIContent label = new GUIContent(option.Label);
            float width = styles.Segment.CalcSize(label).x + 4f;
            if (option.Kind != DebugKind.Choice) return width;
            width = styles.Small.CalcSize(label).x + 6f;
            foreach (string choice in option.Choices)
                width += Mathf.Max(SegmentMinimum, styles.Segment.CalcSize(new GUIContent(choice)).x);
            return width;
        }

        private void DrawChip(DebugOption option)
        {
            string reason;
            optionReasons.TryGetValue(option.Id, out reason);
            bool outer = GUI.enabled;
            GUI.enabled = outer && reason == null;
            GUIContent label = new GUIContent(option.Label, reason ?? option.Tooltip);
            switch (option.Kind)
            {
                case DebugKind.Action:
                    if (GUILayout.Button(label, styles.Segment)) option.Run();
                    break;
                case DebugKind.Choice:
                    GUILayout.Label(label, styles.Small, GUILayout.ExpandWidth(false));
                    GUILayout.Space(4f);
                    int value = option.Get();
                    int picked = Segments(option.Choices, value);
                    if (picked != value) option.Set(picked);
                    break;
                default:
                    bool on = option.Get() != 0;
                    if (GUILayout.Toggle(on, label, styles.Segment) != on) option.Set(on ? 0 : 1);
                    break;
            }
            GUI.enabled = outer;
        }

        // The room a row's star and marker take, for a row that has neither: its control ends where
        // the others' do.
        private static void RowTail()
        {
            GUILayout.Label(GUIContent.none, GUILayout.Width(MarkerWidth + 28f));
        }

        // A fact that is not a setting: its name and what it says now.
        private void InfoRow(string name, string value)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(name, styles.Dim, GUILayout.Width(150f));
            GUILayout.Label(value, GUI.skin.label, GUILayout.ExpandWidth(true));
            GUILayout.EndHorizontal();
        }

        // A line in the warning colour.
        private void WarningLabel(string text)
        {
            Color previous = GUI.color;
            GUI.color = DiagnosticsStyles.Warning;
            GUILayout.Label(text);
            GUI.color = previous;
        }
    }
}
