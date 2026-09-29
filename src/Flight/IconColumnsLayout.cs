using UnityEngine;
using UnityEngine.UI;

namespace ReDefinition.Flight
{
    // A stage's part icons between KSP's one column and two columns, blended by
    // `Blend` from 0 (one column, as KSP's vertical layout lays them) to 1 (two
    // columns, filled left to right): each icon moves on a straight line between its
    // two places, and the list's width and height follow, so that the icons close up
    // as the stage's details slide in (StageInfoColumns). Every icon keeps `Cell`,
    // its size in KSP's column.
    public class IconColumnsLayout : LayoutGroup
    {
        public Vector2 Cell;
        public float Spacing;
        public float Blend;

        private float InnerWidth
        {
            get { return rectTransform.rect.width - padding.horizontal; }
        }

        public override void CalculateLayoutInputHorizontal()
        {
            base.CalculateLayoutInputHorizontal();
            float width = padding.horizontal + Cell.x * Mathf.Lerp(1f, 2f, Blend);
            SetLayoutInputForAxis(width, width, -1f, 0);
        }

        public override void CalculateLayoutInputVertical()
        {
            int count = rectChildren.Count;
            float height = padding.vertical + Mathf.Lerp(Rows(count), Rows((count + 1) / 2), Blend);
            SetLayoutInputForAxis(height, height, -1f, 1);
        }

        private float Rows(int rows)
        {
            return rows <= 0 ? 0f : rows * Cell.y + (rows - 1) * Spacing;
        }

        public override void SetLayoutHorizontal()
        {
            float inner = InnerWidth;
            float single = padding.left + (inner - Cell.x) / 2f;
            float pair = padding.left + (inner - 2f * Cell.x) / 2f;
            for (int i = 0; i < rectChildren.Count; i++)
                SetChildAlongAxis(rectChildren[i], 0, Mathf.Lerp(single, pair + (i % 2) * Cell.x, Blend), Cell.x);
        }

        public override void SetLayoutVertical()
        {
            for (int i = 0; i < rectChildren.Count; i++)
            {
                float single = padding.top + i * (Cell.y + Spacing);
                float pair = padding.top + (i / 2) * (Cell.y + Spacing);
                SetChildAlongAxis(rectChildren[i], 1, Mathf.Lerp(single, pair, Blend), Cell.y);
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
