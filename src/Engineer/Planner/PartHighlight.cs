using System.Collections.Generic;
using UnityEngine;

namespace ReDefinition.Engineer
{
    // Marks parts on the vessel in the editor in one colour, with KSP's own part
    // highlighter: always on in that colour while marked, back to KSP's default
    // (on mouse-over) after. Part.SetHighlightType, SetHighlightColor and
    // SetHighlightDefault, decompiled.
    internal sealed class PartHighlight
    {
        private readonly Color color;
        private readonly List<Part> marked = new List<Part>();

        internal PartHighlight(Color color)
        {
            this.color = color;
        }

        internal void Show(ICollection<Part> parts)
        {
            if (Same(parts)) return;
            Clear();
            foreach (Part part in parts)
            {
                if (part == null) continue;
                part.SetHighlightType(Part.HighlightType.AlwaysOn);
                part.SetHighlightColor(color);
                part.SetHighlight(true, false);
                marked.Add(part);
            }
        }

        internal void Clear()
        {
            foreach (Part part in marked)
                if (part != null) part.SetHighlightDefault();
            marked.Clear();
        }

        // The same parts, each still marked: KSP's editor puts a part's
        // highlight back to its default when it is attached or re-rooted
        // (EditorLogic.highlightSelected, decompiled), and the mark is set again.
        private bool Same(ICollection<Part> parts)
        {
            if (parts.Count != marked.Count) return false;
            foreach (Part part in marked)
                if (part == null || !parts.Contains(part) || part.highlightType != Part.HighlightType.AlwaysOn) return false;
            return true;
        }
    }
}
