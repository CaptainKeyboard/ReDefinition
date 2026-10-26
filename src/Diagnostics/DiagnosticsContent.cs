using System.Collections.Generic;
using UnityEngine;

namespace ReDefinition.Diagnostics
{
    // The GUIContent, widths and layout options the diagnostics window draws with in every
    // event, made once. IMGUI asks for them in the layout pass and again in the repaint, a
    // row with its label, marker, star and segments each time. The window is open for
    // as long as the player watches it.
    internal sealed class DiagnosticsContent
    {
        // A segment's text as wide as it needs and not under the minimum.
        internal sealed class Segment
        {
            // The segment's width, and the layout option of that width.
            public float Width;
            public GUILayoutOption Option;
        }

        // What one option's row and chip draw with.
        internal sealed class Row
        {
            // The label and the chip's text with their tooltips, and the chip's width once measured (-1 before).
            public readonly GUIContent Label = new GUIContent();
            public readonly GUIContent Chip = new GUIContent();
            public float ChipWidth = -1f;
        }

        // The keys that end the typing in the search field.
        internal static bool EndsTyping(KeyCode key)
        {
            return key == KeyCode.Escape || key == KeyCode.Return || key == KeyCode.KeypadEnter;
        }

        // The persistence markers, the search's clear button, the reset button, the "Changed" chip, the pin star and the
        // tooltip box, each made once and given its text where it changes.
        public readonly GUIContent Saved = new GUIContent("saved", "Saved");
        public readonly GUIContent Restart = new GUIContent("restart", "Needs A Restart");
        public readonly GUIContent Session = new GUIContent("session", "This Session Only");
        public readonly GUIContent ClearSearch = new GUIContent("x", "Clear Search");
        public readonly GUIContent ResetSwitches = new GUIContent("Reset debug switches", "Put Them Back To Their Defaults");
        public readonly GUIContent Changed = new GUIContent("", "Differ From Their Default");
        public readonly GUIContent Star = new GUIContent("", "");
        public readonly GUIContent Tip = new GUIContent();

        // The room of a persistence marker, and of a row's star and marker together.
        public readonly GUILayoutOption MarkerOption = GUILayout.Width(MarkerWidth);
        public readonly GUILayoutOption TailOption = GUILayout.Width(MarkerWidth + 28f);

        // The width of a persistence marker.
        public const float MarkerWidth = 56f;

        // The "Changed" chip's count shown; the segments by label, the rows by option id, and the pages' entries with the
        // count each shows.
        private int changedCount = -1;
        private readonly Dictionary<string, Segment> segments = new Dictionary<string, Segment>();
        private readonly Dictionary<string, Row> rows = new Dictionary<string, Row>();
        private readonly Dictionary<DebugCategory, GUIContent> pages = new Dictionary<DebugCategory, GUIContent>();
        private readonly Dictionary<DebugCategory, int> pageCounts = new Dictionary<DebugCategory, int>();

        // "Changed (3)": the text is made when the count changes.
        public GUIContent ChangedChip(int count)
        {
            if (count != changedCount)
            {
                changedCount = count;
                Changed.text = "Changed (" + count + ")";
            }
            return Changed;
        }

        // The star: the glyph and the tooltip of a pinned or an unpinned option.
        public GUIContent StarFor(string glyph, bool pinned)
        {
            Star.text = glyph;
            Star.tooltip = pinned ? "Unpin" : "Pin";
            return Star;
        }

        // The marker of a persistence: saved, session or restart.
        public GUIContent MarkerFor(DebugPersistence persistence)
        {
            switch (persistence)
            {
                case DebugPersistence.Saved: return Saved;
                case DebugPersistence.NeedsRestart: return Restart;
                default: return Session;
            }
        }

        // The page's entry in the list: its title, with the number of its options that pass
        // when a filter is on (count above 0). The text is made when that number changes.
        public GUIContent PageEntry(DebugCategory page, int count)
        {
            GUIContent content;
            int last;
            if (!pages.TryGetValue(page, out content))
            {
                content = new GUIContent("", "");
                pages[page] = content;
                last = int.MinValue;
            }
            else last = pageCounts[page];
            int shown = count > 0 ? count : -1;
            if (shown != last)
            {
                pageCounts[page] = shown;
                content.text = DebugOptionSet.Title(page) + (shown > 0 ? "  (" + shown + ")" : "");
            }
            return content;
        }

        // A segment as wide as its label in the style, not under the minimum, measured once per label.
        public Segment SegmentOf(GUIStyle style, string label, float minimum)
        {
            Segment segment;
            if (segments.TryGetValue(label, out segment)) return segment;
            segment = new Segment { Width = Mathf.Max(minimum, style.CalcSize(new GUIContent(label)).x) };
            segment.Option = GUILayout.Width(segment.Width);
            segments[label] = segment;
            return segment;
        }

        // The option's row contents, made on first use.
        public Row RowOf(string id)
        {
            Row row;
            if (!rows.TryGetValue(id, out row))
            {
                row = new Row();
                rows[id] = row;
            }
            return row;
        }

        // The option's label as it reads now (a count-down changes it) and its tooltip.
        public GUIContent Label(DebugOption option)
        {
            Row row = RowOf(option.Id);
            string text = option.CurrentLabel;
            if (row.Label.text != text) row.Label.text = text;
            row.Label.tooltip = option.Tooltip;
            return row.Label;
        }

        // The option's chip: its label, and under the pointer why it is greyed out or what it is.
        public GUIContent Chip(DebugOption option, string reason)
        {
            Row row = RowOf(option.Id);
            if (row.Chip.text != option.Label) row.Chip.text = option.Label;
            row.Chip.tooltip = reason ?? option.Tooltip;
            return row.Chip;
        }
    }
}
