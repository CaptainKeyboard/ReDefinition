using System.Collections.Generic;
using UnityEngine;

namespace ReDefinition.Engineer
{
    // Marks parts on the vessel in the editor, each in its own colour, with
    // KSP's own part highlighter: always on in that colour while marked, back to
    // KSP's default (on mouse-over) after. Part.SetHighlightType,
    // SetHighlightColor and SetHighlightDefault, decompiled.
    internal sealed class PartHighlight
    {
        private readonly Dictionary<Part, Color> marked = new Dictionary<Part, Color>();

        internal void Show(IDictionary<Part, Color> parts)
        {
            if (Same(parts)) return;
            Clear();
            foreach (KeyValuePair<Part, Color> part in parts)
            {
                if (part.Key == null) continue;
                part.Key.SetHighlightType(Part.HighlightType.AlwaysOn);
                part.Key.SetHighlightColor(part.Value);
                part.Key.SetHighlight(true, false);
                marked[part.Key] = part.Value;
            }
        }

        internal void Clear()
        {
            foreach (Part part in marked.Keys)
                if (part != null) part.SetHighlightDefault();
            marked.Clear();
        }

        // The same parts in the same colours, each still marked: KSP's editor
        // puts a part's highlight back to its default when it is attached or
        // re-rooted (EditorLogic.highlightSelected, decompiled), and the mark is
        // set again.
        private bool Same(IDictionary<Part, Color> parts)
        {
            if (parts.Count != marked.Count) return false;
            foreach (KeyValuePair<Part, Color> part in marked)
            {
                Color color;
                if (part.Key == null || !parts.TryGetValue(part.Key, out color) || color != part.Value) return false;
                if (part.Key.highlightType != Part.HighlightType.AlwaysOn) return false;
            }
            return true;
        }
    }
}
