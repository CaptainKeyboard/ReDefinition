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
                Apply(part.Key, part.Value);
                marked[part.Key] = part.Value;
            }
        }

        // Every frame: in the game the mark went when the pointer passed over a
        // marked part, so whatever turns a part's highlight off or recolours
        // it, the mark is put back at once.
        internal void Refresh()
        {
            foreach (KeyValuePair<Part, Color> part in marked)
                if (part.Key != null && !Holds(part.Key, part.Value)) Apply(part.Key, part.Value);
        }

        private static void Apply(Part part, Color color)
        {
            part.SetHighlightType(Part.HighlightType.AlwaysOn);
            part.SetHighlightColor(color);
            part.SetHighlight(true, false);
        }

        private static bool Holds(Part part, Color color)
        {
            return part.highlightType == Part.HighlightType.AlwaysOn && part.HighlightActive && part.highlightColor == color;
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
                if (!Holds(part.Key, color)) return false;
            }
            return true;
        }
    }
}
