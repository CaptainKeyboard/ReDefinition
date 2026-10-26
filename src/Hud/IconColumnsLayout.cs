using System.Collections.Generic;
using KSP.UI.Screens;
using UnityEngine;
using UnityEngine.UI;

namespace ReDefinition.Hud
{
    // A layout of a stage's part icons between KSP's one column and two columns, blended by
    // `Blend` from 0 (one column, as KSP's vertical layout lays them) to 1 (two
    // columns, filled left to right). Each icon moves on a straight line between its
    // two places. The list's width and height follow, so that the icons close up
    // as the stage's details slide in (StageInfoColumns). Every icon keeps `Cell`,
    // its size in KSP's column.
    //
    // KSP makes room for an icon moved in or out with a placeholder that grows or
    // shrinks in height (TweeningController.SpawnResizingLayoutElement, a
    // ResizingLayoutElement among the icons). A placeholder here takes the share of a
    // place its height is of an icon's, and the icons after it move on by as much.
    // They move along the column, and in two columns from one place to the next, so
    // that they glide into the gap rather than jump.
    public class IconColumnsLayout : LayoutGroup
    {
        // An icon's size in KSP's column; the gap between rows; and the blend, 0 for one column, 1 for two.
        public Vector2 Cell;
        public float Spacing;
        public float Blend;

        // Per child, the place it starts at, counted from the first (CountSlots).
        private readonly List<float> slots = new List<float>();

        // As wide as one column, or two, or between them by the blend.
        public override void CalculateLayoutInputHorizontal()
        {
            base.CalculateLayoutInputHorizontal();
            CountSlots();
            float width = padding.horizontal + Cell.x * Mathf.Lerp(1f, 2f, Blend);
            SetLayoutInputForAxis(width, width, -1f, 0);
        }

        // Where each child starts, in places counted from the first. Unity lays out the
        // horizontal axis before it asks for the vertical inputs, so both passes count.
        private void CountSlots()
        {
            slots.Clear();
            float at = 0f;
            for (int i = 0; i < rectChildren.Count; i++)
            {
                slots.Add(at);
                at += Share(rectChildren[i]);
            }
        }

        // How far the lowest child reaches.
        public override void CalculateLayoutInputVertical()
        {
            CountSlots();
            float lowest = 0f;
            for (int i = 0; i < rectChildren.Count; i++)
            {
                float at = slots[i];
                Vector2 single = Single(at), pair = Pair(at);
                float top = Mathf.Lerp(single.y, pair.y, Blend);
                lowest = Mathf.Max(lowest, top + Cell.y * Share(rectChildren[i]));
            }
            float height = padding.vertical + lowest;
            SetLayoutInputForAxis(height, height, -1f, 1);
        }

        // A part icon takes a whole place; anything else, KSP's placeholder, the share
        // its height is of an icon's.
        private float Share(RectTransform child)
        {
            if (child.GetComponent<StageIcon>() != null || Cell.y <= 0f) return 1f;
            return Mathf.Clamp01(LayoutUtility.GetPreferredHeight(child) / Cell.y);
        }

        // In one column: straight down. In two: from place to place, left then right,
        // a fraction of a place on the straight line to the next.
        private Vector2 Single(float slot)
        {
            return new Vector2(0f, slot * (Cell.y + Spacing));
        }

        // In two columns, a place on the straight line from one place to the next.
        private Vector2 Pair(float slot)
        {
            int whole = Mathf.FloorToInt(slot);
            float part = slot - whole;
            return Vector2.Lerp(Place(whole), Place(whole + 1), part);
        }

        // In two columns: left and right in turn, a row down after each pair.
        private Vector2 Place(int slot)
        {
            return new Vector2((slot % 2) * Cell.x, (slot / 2) * (Cell.y + Spacing));
        }

        // Each child's x: centred in one column, or in two, blended between.
        public override void SetLayoutHorizontal()
        {
            float inner = rectTransform.rect.width - padding.horizontal;
            float single = padding.left + (inner - Cell.x) / 2f;
            float pair = padding.left + (inner - 2f * Cell.x) / 2f;
            for (int i = 0; i < rectChildren.Count && i < slots.Count; i++)
                SetChildAlongAxis(rectChildren[i], 0, Mathf.Lerp(single, pair + Pair(slots[i]).x, Blend), Cell.x);
        }

        // Each child's y and height, blended between one column and two.
        public override void SetLayoutVertical()
        {
            for (int i = 0; i < rectChildren.Count && i < slots.Count; i++)
            {
                float y = Mathf.Lerp(Single(slots[i]).y, Pair(slots[i]).y, Blend);
                SetChildAlongAxis(rectChildren[i], 1, padding.top + y, Cell.y * Share(rectChildren[i]));
            }
        }

        // A new blend: the layout worked out again.
        internal void SetBlend(float blend)
        {
            blend = Mathf.Clamp01(blend);
            if (Mathf.Approximately(blend, Blend)) return;
            Blend = blend;
            SetDirty();
        }
    }
}
