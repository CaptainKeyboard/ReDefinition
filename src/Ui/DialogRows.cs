using System;

namespace ReDefinition.Ui
{
    // The rows and headers the settings window and the main menu's notice build their dialogs from, written once.
    internal static class DialogRows
    {
        // A row of controls from the left, centred on its height, with nothing between them.
        public static DialogGUIHorizontalLayout Row(float height, params DialogGUIBase[] children)
        {
            return Row(height, 0f, children);
        }

        // The same with a gap of that many pixels between the controls.
        public static DialogGUIHorizontalLayout Row(float height, float spacing, params DialogGUIBase[] children)
        {
            return new DialogGUIHorizontalLayout(0f, height, spacing, new UnityEngine.RectOffset(),
                UnityEngine.TextAnchor.MiddleLeft, children);
        }

        // A button that opens and folds a group: "+  title" while it is folded, "-  title" while it is open.
        // `tooltip` is null for none.
        public static DialogGUIButton FoldHeader(string title, Func<bool> open, Callback toggle, float width, float height,
                                                 string tooltip)
        {
            string folded = "+  " + title;
            string opened = "-  " + title;
            DialogGUIButton header = new DialogGUIButton(() => open() ? opened : folded, toggle, width, height, false);
            if (tooltip != null) header.tooltipText = tooltip;
            return header;
        }
    }
}
