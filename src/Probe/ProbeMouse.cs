using System;
using System.Collections;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.UI;

namespace ReDefinition.Probe
{
    // The mouse as a player has it, for the probes: the cursor is moved over the game's window and its buttons are pressed through the system, so that
    // the game reads them as it reads a hand. Used to click through the map's menus and the buttons of the flight view.
    internal static class ProbeMouse
    {
        private const uint LeftDown = 0x0002, LeftUp = 0x0004, RightDown = 0x0008, RightUp = 0x0010;

        [StructLayout(LayoutKind.Sequential)]
        private struct Point
        {
            public int X, Y;
        }

        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll")]
        private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr window);

        [DllImport("user32.dll")]
        private static extern bool ClientToScreen(IntPtr window, ref Point point);

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);

        private static IntPtr window;

        // The game's window to the front, where the system lets it; the cursor is read by the game only then.
        internal static void Focus()
        {
            window = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
            // A key press lets a process that was started behind take the front.
            keybd_event(0x12, 0, 0, UIntPtr.Zero);
            SetForegroundWindow(window);
            keybd_event(0x12, 0, 2, UIntPtr.Zero);
        }

        // The cursor over a place of the game's screen, as the game counts it: from the lower left.
        internal static void Move(Vector2 screen)
        {
            if (window == IntPtr.Zero) Focus();
            var origin = new Point();
            ClientToScreen(window, ref origin);
            SetCursorPos(origin.X + (int)screen.x, origin.Y + (int)(Screen.height - screen.y));
        }

        // A press and a release of a button, a moment apart.
        internal static IEnumerator Click(bool right = false)
        {
            mouse_event(right ? RightDown : LeftDown, 0, 0, 0, UIntPtr.Zero);
            yield return new WaitForSecondsRealtime(0.12f);
            mouse_event(right ? RightUp : LeftUp, 0, 0, 0, UIntPtr.Zero);
            yield return new WaitForSecondsRealtime(0.15f);
        }

        // Where a control of the interface is on the game's screen.
        internal static Vector2 ScreenOf(RectTransform rect)
        {
            Canvas canvas = rect.GetComponentInParent<Canvas>();
            Camera camera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
            return RectTransformUtility.WorldToScreenPoint(camera, rect.TransformPoint(rect.rect.center));
        }

        // The button with this text on it, where it is on the screen now; null where there is none.
        internal static Button ButtonWith(string text)
        {
            foreach (Button button in Resources.FindObjectsOfTypeAll<Button>())
            {
                if (button == null || !button.gameObject.activeInHierarchy || !button.gameObject.scene.IsValid()) continue;
                TMPro.TextMeshProUGUI label = button.GetComponentInChildren<TMPro.TextMeshProUGUI>();
                if (label != null && label.text.Contains(text)) return button;
            }
            return null;
        }
    }
}
