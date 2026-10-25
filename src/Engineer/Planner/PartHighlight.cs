using System.Collections.Generic;
using UnityEngine;

namespace ReDefinition.Engineer
{
    // Marks parts on the vessel in the editor, each in its own colour, with
    // KSP's own part highlighter: always on in that colour while marked, back to
    // KSP's default (on mouse-over) after. Part.SetHighlightType,
    // SetHighlightColor and SetHighlightDefault, decompiled. One marker at a time:
    // whichever marks parts last takes the other's marks away. While the pointer
    // is on a part of the vessel (Mouse.HoveredPart), nothing is marked, and KSP
    // highlights as it does without the mod. A marker whose marks are taken away is told (Lost).
    internal sealed class PartHighlight
    {
        // The marker whose marks show now; whether the pointer taking them away went to the log; this marker's parts and colours.
        private static PartHighlight current;
        private static bool pointerLogged;
        private readonly Dictionary<Part, Color> marked = new Dictionary<Part, Color>();

        // Told when the marks are taken away: by the pointer on a part, or by
        // another marker.
        internal System.Action Lost;

        // Marks these parts in their colours, in place of whatever was marked; none, or the pointer on a part, clears the marks.
        internal void Show(IDictionary<Part, Color> parts)
        {
            if (parts.Count == 0)
            {
                Clear();
                return;
            }
            if (PointerOnPart())
            {
                Lose();
                return;
            }
            if (current != null && current != this) current.Lose();
            current = this;
            if (Same(parts)) return;
            Clear();
            current = this;
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
            if (current != this) return;
            if (marked.Count > 0 && PointerOnPart())
            {
                if (!pointerLogged)
                {
                    pointerLogged = true;
                    Debug.Log(ReDefinition.Core.Log.Tag + " Pointer on a part: the marks are taken away, KSP highlights it.");
                }
                Lose();
                return;
            }
            foreach (KeyValuePair<Part, Color> part in marked)
                if (part.Key != null && !Holds(part.Key, part.Value)) Apply(part.Key, part.Value);
        }

        // The part's highlight always on, in the colour.
        private static void Apply(Part part, Color color)
        {
            part.SetHighlightType(Part.HighlightType.AlwaysOn);
            part.SetHighlightColor(color);
            part.SetHighlight(true, false);
        }

        // Whether the part still shows the mark in that colour.
        private static bool Holds(Part part, Color color)
        {
            return part.highlightType == Part.HighlightType.AlwaysOn && part.HighlightActive && part.highlightColor == color;
        }

        // The pointer on a part of the vessel, not on a window or list over it.
        private static bool PointerOnPart()
        {
            if (Mouse.HoveredPart == null) return false;
            UnityEngine.EventSystems.EventSystem events = UnityEngine.EventSystems.EventSystem.current;
            return events == null || !events.IsPointerOverGameObject();
        }

        // The marks go, and the marker is told.
        private void Lose()
        {
            bool had = marked.Count > 0;
            Clear();
            if (had && Lost != null) Lost();
        }

        // The marked parts' highlights back to KSP's default.
        internal void Clear()
        {
            foreach (Part part in marked.Keys)
                if (part != null) part.SetHighlightDefault();
            marked.Clear();
            if (current == this) current = null;
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
