using UnityEngine;

namespace ReDefinition.Diagnostics
{
    // The layout options the diagnostics window asks for in every event, made once.
    // IMGUI asks in the layout pass and again in the repaint, and GUILayout.Width and its kin make a new option each time.
    // The options that depend on a constant of one page stand beside that constant.
    internal static class DiagnosticsLayout
    {
        // A control that takes the width left, and one that takes only the width it needs.
        public static readonly GUILayoutOption Fill = GUILayout.ExpandWidth(true);
        public static readonly GUILayoutOption Shrink = GUILayout.ExpandWidth(false);
        // A control that takes the height left.
        public static readonly GUILayoutOption FillHeight = GUILayout.ExpandHeight(true);

        // The name column of a table row, and the columns of the live strip: frame rate, frame times, filter.
        public static readonly GUILayoutOption NameColumn = GUILayout.Width(150f);
        public static readonly GUILayoutOption FpsColumn = GUILayout.Width(200f);
        public static readonly GUILayoutOption FrameTimeColumn = GUILayout.Width(268f);
        public static readonly GUILayoutOption FilterColumn = GUILayout.Width(220f);

        // The search field's label, the clear button beside it, the sharpness page's step buttons and its value, the
        // window's Close button.
        public static readonly GUILayoutOption SearchLabel = GUILayout.Width(44f);
        public static readonly GUILayoutOption ClearButton = GUILayout.Width(24f);
        public static readonly GUILayoutOption StepButton = GUILayout.Width(30f);
        public static readonly GUILayoutOption ValueBox = GUILayout.Width(50f);
        public static readonly GUILayoutOption CloseButton = GUILayout.Width(80f);
    }
}
