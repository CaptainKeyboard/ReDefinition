using UnityEngine;

namespace ReDefinition.Window
{
    // A dialog of ReDefinition's kept within the screen at KSP's UI scale. A
    // PopupDialog is built on the canvas PopupDialogController names, DialogCanvas
    // (level0, read from the scene), which is UIMasterController's dialogCanvas:
    // SetScale sets its scale factor to UI_SCALE with every other canvas of KSP's
    // (decompiled). A window laid out for 100 % grows with it, and at more than
    // KSP's own 200 % -- ReDefinition's row goes to 400 % -- the settings window and
    // its questions reach past the screen's edges, their buttons with them.
    //
    // Where the window would not fit within the margin KSP keeps dialogs from the
    // edge (PopupDialog.SpawnPopupDialog's ClampToScreen, 30), it is drawn smaller
    // about the point KSP placed it at, as large as fits: at the UI scale as far as
    // the screen allows. Measured in the canvas's own units, window against canvas, so that
    // the canvas's scale factor drops out. Looked at again each frame -- only
    // sizes compared -- since both can change while it is open: the UI scale
    // applied from the settings window itself, a tab of another height.
    // Dragging is untouched: DragPanel moves the dialog by the pointer's travel in
    // the world (UIMasterController.DragTooltip, decompiled), which this scale
    // does not enter.
    internal sealed class ScreenFit : MonoBehaviour
    {
        private const float Margin = 30f;

        private RectTransform canvas;
        private RectTransform window;
        private Vector2 canvasSize;
        private Vector2 windowSize;

        // The dialog as given, kept within the screen from now on.
        internal static PopupDialog Add(PopupDialog dialog)
        {
            if (dialog == null || dialog.popupWindow == null) return dialog;
            Canvas parent = dialog.GetComponentInParent<Canvas>();
            ScreenFit fit = dialog.gameObject.AddComponent<ScreenFit>();
            fit.canvas = parent != null ? parent.transform as RectTransform : null;
            fit.window = dialog.popupWindow.transform as RectTransform;
            fit.Fit();
            return dialog;
        }

        private void LateUpdate()
        {
            Fit();
        }

        private void Fit()
        {
            if (canvas == null || window == null) return;
            Vector2 room = canvas.rect.size;
            Vector2 size = window.rect.size;
            if (room == canvasSize && size == windowSize) return;
            canvasSize = room;
            windowSize = size;
            float scale = 1f;
            if (size.x > 0f) scale = Mathf.Min(scale, (room.x - 2f * Margin) / size.x);
            if (size.y > 0f) scale = Mathf.Min(scale, (room.y - 2f * Margin) / size.y);
            // A screen narrower than the margins leaves the window as it is.
            if (scale <= 0f) scale = 1f;
            if (Mathf.Abs(transform.localScale.x - scale) > 0.001f) transform.localScale = new Vector3(scale, scale, 1f);
        }
    }
}
