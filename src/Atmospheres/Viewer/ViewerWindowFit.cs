using System;
using UnityEngine;

namespace ReDefinition.Atmospheres.Viewer
{
    // A rectangle in screen pixels (origin bottom left, y up).
    internal struct ScreenBox
    {
        internal float XMin, YMin, XMax, YMax;

        internal float Width { get { return XMax - XMin; } }
        internal float Height { get { return YMax - YMin; } }
        internal bool Valid { get { return !float.IsNaN(XMin + YMin + XMax + YMax) && XMax > XMin && YMax > YMin; } }
    }

    // Where the cloud viewer's window may stand: wholly inside the screen, whatever the resolution and KSP's UI scale. The pure part, so that it can be tested.
    internal static class WindowPlacement
    {
        // The room kept between the window and the screen's edge, in pixels (the title and the buttons stay reachable), and the height the dialog's frame, title and button row need
        // besides the list, in canvas units, and the list's smallest and largest height in canvas units.
        internal const float MarginPx = 8f, ChromeUnits = 110f, ScreenFitMarginUnits = 30f, MinListUnits = 160f, MaxListUnits = 520f;

        // The move in pixels (x right, y up) that brings the box inside the screen less the margin. A box that is larger than the room keeps its left edge and its top edge on the
        // margin: the title and the first rows stay in view, the rest is reached by the list's scroll.
        internal static void Shift(ScreenBox box, float screenW, float screenH, out float dx, out float dy)
        {
            dx = dy = 0f;
            if (!box.Valid || screenW <= 0f || screenH <= 0f) return;
            if (box.Width > screenW - 2f * MarginPx || box.XMin < MarginPx) dx = MarginPx - box.XMin;
            else if (box.XMax > screenW - MarginPx) dx = screenW - MarginPx - box.XMax;
            if (box.Height > screenH - 2f * MarginPx || box.YMax > screenH - MarginPx) dy = screenH - MarginPx - box.YMax;
            else if (box.YMin < MarginPx) dy = MarginPx - box.YMin;
        }

        // Whether the box lies wholly in the screen (a pixel of slack for the rounding of the corners).
        internal static bool Inside(ScreenBox box, float screenW, float screenH)
        {
            const float slack = 1f;
            return box.Valid && box.XMin >= -slack && box.YMin >= -slack && box.XMax <= screenW + slack && box.YMax <= screenH + slack;
        }

        // The list's height in canvas units for a screen height in pixels at a UI scale: as much of 520 as the dialog's other rows and the margin KSP keeps leave, never below 160
        // (the list scrolls where it is shorter than its content).
        internal static float ListHeight(float screenHeightPx, float uiScale)
        {
            float units = uiScale > 0.01f ? screenHeightPx / uiScale : screenHeightPx;
            float room = units - 2f * ScreenFitMarginUnits - ChromeUnits;
            return Math.Max(MinListUnits, Math.Min(MaxListUnits, room));
        }
    }

    // Keeps a dialog's window inside the screen each frame. KSP puts a popup at the anchor it is given; the anchor of the viewer's (0.75, 0.5) left its window half beyond the left edge at
    // 1707x960 and UI scale 1.5 (K-V1 start, 2026-11-15). ScreenFit scales a window down that is larger than the screen; this moves it. The window is measured, not computed: its real
    // corners on the screen, through the canvas's own camera, so that the anchor, the pivot, the canvas's scale and ScreenFit's scale all drop out.
    internal sealed class ViewerWindowFit : MonoBehaviour
    {
        private RectTransform window;
        private RectTransform canvasRect;
        private Camera canvasCamera;

        internal static ViewerWindowFit Add(PopupDialog dialog)
        {
            if (dialog == null || dialog.popupWindow == null) return null;
            Canvas canvas = dialog.GetComponentInParent<Canvas>();
            if (canvas == null) return null;
            Canvas root = canvas.rootCanvas != null ? canvas.rootCanvas : canvas;
            ViewerWindowFit fit = dialog.gameObject.AddComponent<ViewerWindowFit>();
            fit.window = dialog.popupWindow.transform as RectTransform;
            fit.canvasRect = root.transform as RectTransform;
            fit.canvasCamera = root.renderMode == RenderMode.ScreenSpaceOverlay ? null : root.worldCamera;
            fit.Fit();
            return fit;
        }

        private void LateUpdate()
        {
            Fit();
        }

        // The window's box on the screen, in pixels; false where it cannot be measured.
        internal static bool Measure(RectTransform window, Camera canvasCamera, out ScreenBox box)
        {
            box = default(ScreenBox);
            if (window == null) return false;
            var corners = new Vector3[4];
            window.GetWorldCorners(corners);
            box.XMin = box.YMin = float.MaxValue;
            box.XMax = box.YMax = float.MinValue;
            for (int i = 0; i < 4; i++)
            {
                Vector2 p = RectTransformUtility.WorldToScreenPoint(canvasCamera, corners[i]);
                box.XMin = Mathf.Min(box.XMin, p.x);
                box.YMin = Mathf.Min(box.YMin, p.y);
                box.XMax = Mathf.Max(box.XMax, p.x);
                box.YMax = Mathf.Max(box.YMax, p.y);
            }
            return box.Valid;
        }

        internal bool Box(out ScreenBox box)
        {
            return Measure(window, canvasCamera, out box);
        }

        private void Fit()
        {
            if (window == null || canvasRect == null) return;
            ScreenBox box;
            if (!Measure(window, canvasCamera, out box)) return;
            float dx, dy;
            WindowPlacement.Shift(box, Screen.width, Screen.height, out dx, out dy);
            if (Mathf.Abs(dx) < 0.5f && Mathf.Abs(dy) < 0.5f) return;
            // The move in the canvas's world: the same pixel step from the window's own corner, converted back.
            Vector2 from = new Vector2(box.XMin, box.YMin);
            Vector3 a, b;
            if (!RectTransformUtility.ScreenPointToWorldPointInRectangle(canvasRect, from, canvasCamera, out a)
                || !RectTransformUtility.ScreenPointToWorldPointInRectangle(canvasRect, from + new Vector2(dx, dy), canvasCamera, out b)) return;
            window.position += b - a;
        }
    }
}
