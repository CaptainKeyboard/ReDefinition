using UnityEngine;

namespace ReDefinition.Engineer
{
    // What KSP's dialog does with a row it hides: DialogGUIBase.Update calls Update on every child, whether the row is shown or not, and only
    // afterwards switches the row off by its OptionEnabledCondition. The planner keeps its rows in pools of fixed size, most of them hidden, so
    // that walk reached about fifteen thousand elements a frame. A gated row looks at its own condition first and leaves what is under it alone
    // while it is off.
    internal static class Gate
    {
        // Whether the item is shown, switching it on or off as it needs; true where it has no condition or no object yet.
        internal static bool Shown(DialogGUIBase item)
        {
            System.Func<bool> condition = item.OptionEnabledCondition;
            if (condition == null || item.uiItem == null) return true;
            bool on = condition();
            if (item.uiItem.activeSelf != on) item.uiItem.SetActive(on);
            return on;
        }
    }

    // A horizontal row of the planner whose children are updated only while it is shown.
    internal sealed class GatedRow : DialogGUIHorizontalLayout
    {
        internal GatedRow(float minWidth, float minHeight, float spacing, RectOffset padding, TextAnchor anchor, params DialogGUIBase[] list)
            : base(minWidth, minHeight, spacing, padding, anchor, list)
        {
        }

        internal GatedRow(bool stretchWidth, bool stretchHeight, float spacing, RectOffset padding, TextAnchor anchor, params DialogGUIBase[] list)
            : base(stretchWidth, stretchHeight, spacing, padding, anchor, list)
        {
        }

        public override void Update()
        {
            if (Gate.Shown(this)) base.Update();
        }
    }

    // A vertical column of the planner whose children are updated only while it is shown.
    internal sealed class GatedColumn : DialogGUIVerticalLayout
    {
        internal GatedColumn(float minWidth, float minHeight, float spacing, RectOffset padding, TextAnchor anchor, params DialogGUIBase[] list)
            : base(minWidth, minHeight, spacing, padding, anchor, list)
        {
        }

        public override void Update()
        {
            if (Gate.Shown(this)) base.Update();
        }
    }
}
