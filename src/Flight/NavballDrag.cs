using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System;
using KSP.UI;
using KSP.UI.Screens;
using KSP.UI.Screens.Flight;
using ReDefinition.Core;
using ReDefinition.Settings;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ReDefinition.Flight
{
    // The navball moved sideways by dragging its show/hide button
    // (docs/player/navball.md). A click on the button still shows and hides it;
    // a drag moves the whole navball frame, the SAS and RCS switches, the
    // autopilot modes, the throttle and G gauges and the delta-v gauge with it --
    // all of them are one group, NavballFrame, in the flight UI (sharedassets7,
    // read with UnityPy).
    //
    // KSP shows and hides the navball through a UIPanelTransitionToggle
    // (NavBallToggle.panel, decompiled): it sets its RectTransform's
    // anchoredPosition to one of two stored states, and animates from a position
    // it keeps privately. A move therefore shifts the panel, both states and that
    // kept position by the same amount, so that showing and hiding keep the new
    // place, in the map view too. The offset is saved in ReDefinition's settings
    // (OwnSettings.NavballOffset) and applied again in every flight scene; a
    // change in the settings window moves the navball the same way.
    //
    // KSP places the navball itself, where its own setting has it
    // (FlightUIModeController.SetNavBallHPos, GameSettings.UI_POS_NAVBALL): the
    // panel, its states and the transform set anew, at every change of the flight
    // UI's mode (the map, docking, maneuvers), a few frames after the flight
    // starts and when the crew portraits change; decompiled. After each, the
    // offset is laid on KSP's place again (a postfix, Placed).
    //
    // The SAS modes (VesselAutopilotUI, under the frame) stand on the side of the
    // navball that faces the screen's middle: on its left as KSP has them, on its
    // right, mirrored about the navball's middle, while the navball stands left of
    // the screen's middle. The buttons keep their order; only their curve turns, so
    // that it follows the navball's rim on either side: KSP places them one by one,
    // each row shifted a little along the rim (sharedassets7), and each row's shift
    // from the rows' mean is turned the other way.
    //
    // The navball keeps clear of the flight UI's mode buttons on its left
    // (FlightUIModeController.uiModeFrame) and of the crew portraits on its right
    // (UIFrameCrewPortaits), as they stand now, and of the screen's edges: where
    // either grows (another mode chosen, a portrait slot added), the navball moves
    // aside, and back towards where it was put once there is room. All three are
    // measured by what they draw now (Extent): their holders reach beyond it, and
    // the navball's frame holds parts shown only at times, such as the maneuver's
    // delta-v gauge right of the navball (DeltaVGauge, sharedassets7), which moves
    // the navball aside only while it shows.
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class NavballDrag : MonoBehaviour
    {
        private static readonly FieldInfo KeptPosition =
            typeof(UIPanelTransition).GetField("panelPosition", BindingFlags.Instance | BindingFlags.NonPublic);


        private static NavballDrag current;
        private static bool hooked;

        private UIPanelTransitionToggle panel;
        private RectTransform frame;
        // The offset the navball stands at now, in the canvas's units.
        private float applied;

        private IEnumerator Start()
        {
            while (NavBallToggle.Instance == null || NavBallToggle.Instance.panel == null) yield return null;
            // The panel sets its states up in its own Start.
            yield return null;

            try
            {
                NavBallToggle toggle = NavBallToggle.Instance;
                panel = toggle.panel;
                // The RectTransform the transition moves, set in the prefab: the
                // frame's own (UITransitionBase.panelTransform).
                frame = panel.panelTransform != null ? panel.panelTransform : panel.transform as RectTransform;
                if (frame == null || toggle.overrideButton == null) yield break;
                DrawOnTop();
                NavballDragHandle handle = toggle.overrideButton.gameObject.AddComponent<NavballDragHandle>();
                handle.Owner = this;
                current = this;
                if (!hooked)
                {
                    hooked = true;
                    HarmonyHooks.Safely("navball-placed", "The navball's offset is lost when KSP places the navball anew", () =>
                        HarmonyHooks.Install("ReDefinition.NavballDrag", typeof(NavballDrag), new List<HarmonyHook>
                        {
                            new HarmonyHook(typeof(FlightUIModeController).GetMethod("SetNavBallHPos", BindingFlags.Instance | BindingFlags.Public, null,
                                new[] { typeof(float) }, null),
                                null, nameof(Placed), null),
                        }));
                }
                Debug.Log(Log.Tag + " The navball moves sideways by dragging its show/hide button.");
            }
            catch (Exception e)
            {
                Debug.LogWarning(Log.Tag + " The navball cannot be moved: " + e);
                panel = null;
            }
        }

        // The navball over the rest of the flight UI, the stage stack above all,
        // which reaches beside it with its details open. The ball is a mesh
        // (NavBall, material queue 2995), drawn before the UI's canvas at the same
        // sorting order, so everything in the canvas covered it. Now the canvas
        // with the flight UI comes first, then the ball and the navball's other
        // meshes, then the navball's frame in a canvas of its own.
        private void DrawOnTop()
        {
            Canvas outer = frame.parent != null ? frame.parent.GetComponentInParent<Canvas>() : null;
            if (outer == null || frame.GetComponent<Canvas>() != null) return;
            int layer = outer.sortingLayerID, order = outer.sortingOrder;
            foreach (Renderer mesh in frame.GetComponentsInChildren<Renderer>(true))
            {
                mesh.sortingLayerID = layer;
                mesh.sortingOrder = order + 1;
            }
            Canvas own = frame.gameObject.AddComponent<Canvas>();
            own.overrideSorting = true;
            own.sortingLayerID = layer;
            own.sortingOrder = order + 2;
            frame.gameObject.AddComponent<GraphicRaycaster>();
            Debug.Log(Log.Tag + " Navball drawn over the flight UI: sorting order " + order + ", its meshes " + (order + 1)
                      + ", its frame " + (order + 2) + ".");
        }

        private void OnDestroy()
        {
            if (current == this) current = null;
        }

        // KSP set the navball where its own setting has it: the offset from there.
        private static void Placed()
        {
            NavballDrag drag = current;
            if (drag == null || drag.panel == null || drag.frame == null) return;
            drag.applied = 0f;
            if (KeptPosition != null && KeptPosition.FieldType == typeof(Vector2) && drag.panel.states != null
                && drag.panel.StateIndex >= 0 && drag.panel.StateIndex < drag.panel.states.Length)
                KeptPosition.SetValue(drag.panel, drag.panel.states[drag.panel.StateIndex].position);
            ReDefinitionAddon addon = ReDefinitionAddon.Instance;
            drag.requested = addon != null ? addon.NavballOffset : 0f;
            drag.Settle();
        }

        private void Update()
        {
            if (panel == null || frame == null) return;
            ReDefinitionAddon addon = ReDefinitionAddon.Instance;
            if (addon == null) return;
            // Worked out again when the setting changes, and when the room around or
            // how wide the navball draws changed, looked at a few times a second.
            bool asked = Mathf.Abs(addon.NavballOffset - requested) >= 0.01f;
            if (!asked && Time.unscaledTime < lookAgain) return;
            lookAgain = Time.unscaledTime + 0.2f;
            float left, right, min, max;
            Room(out left, out right);
            Drawn(out min, out max);
            if (!asked && Same(left, roomLeft) && Same(right, roomRight) && Same(max - min, drawnWidth)) return;
            requested = addon.NavballOffset;
            logged = false;
            Settle();
            Room(out roomLeft, out roomRight);
            Drawn(out min, out max);
            drawnWidth = max - min;
        }

        private float lookAgain;
        private float roomLeft = float.NaN, roomRight = float.NaN, drawnWidth = float.NaN;

        private static bool Same(float a, float b)
        {
            return Mathf.Abs(a - b) < 0.5f || float.IsNaN(a) && float.IsNaN(b);
        }

        // To the offset asked for, as far as the room allows; again where the SAS
        // modes changed sides on the way, since that changes the navball's width.
        private void Settle()
        {
            for (int pass = 0; pass < 2; pass++)
            {
                bool before = mirrored;
                float wanted = Clamp(requested);
                if (Mathf.Abs(wanted - applied) >= 0.5f) Shift(wanted - applied);
                else Sides();
                if (mirrored == before) return;
            }
        }

        private RectTransform Root()
        {
            Canvas canvas = frame.GetComponentInParent<Canvas>();
            return canvas != null ? canvas.rootCanvas.transform as RectTransform : null;
        }

        // The room between the mode buttons and the crew portraits, in the root
        // canvas's units: the screen's edges where either draws nothing beside the
        // navball. Only what stands at the navball's height counts; the stage
        // stack, which the navball is drawn over (DrawOnTop), does not.
        private void Room(out float left, out float right)
        {
            RectTransform root = Root();
            left = root != null ? root.rect.xMin : 0f;
            right = root != null ? root.rect.xMax : 0f;
            FlightUIModeController controller = FlightUIModeController.Instance;
            if (root == null || controller == null) return;
            Rect navball;
            float low = float.MinValue, high = float.MaxValue;
            if (Extent(frame, root, null, low, high, out navball))
            {
                low = navball.yMin;
                high = navball.yMax;
            }
            Rect found;
            if (controller.uiModeFrame != null)
            {
                Transform modes = controller.uiModeFrame.transform;
                Transform stack = modes.Find("EVACollapseGroup/StageManagerParent");
                if (stack == null && StageManager.Instance != null) stack = StageManager.Instance.transform;
                if (Extent(modes, root, stack, low, high, out found)) left = Mathf.Max(left, found.xMax);
            }
            if (controller.UIFrameCrewPortaits != null
                && Extent(controller.UIFrameCrewPortaits.transform, root, null, low, high, out found))
                right = Mathf.Min(right, found.xMin);
        }

        // What the navball draws now, sideways, in the root canvas's units.
        private void Drawn(out float min, out float max)
        {
            RectTransform root = Root();
            Rect found;
            if (root != null && Extent(frame, root, null, float.MinValue, float.MaxValue, out found))
            {
                min = found.xMin;
                max = found.xMax;
            }
            else min = max = float.NaN;
        }

        private static readonly Vector3[] Corners = new Vector3[4];

        // Where the pictures and texts under `under` stand that are drawn now, in
        // the root canvas's units: shown, not see-through, not a mask's shape, on
        // the screen, reaching into the height from `low` to `high`, and not under
        // `skip`. A text counts by its letters, a picture that keeps its aspect by
        // the part of its rectangle it fills: the throttle and G gauges are square
        // rectangles (219 wide) around narrow arcs (sprites 92 by 244;
        // sharedassets7), which reach far beyond the navball's rim. Empty holders
        // do not count.
        private static bool Extent(Transform under, RectTransform root, Transform skip, float low, float high, out Rect found)
        {
            float xMin = float.MaxValue, xMax = float.MinValue, yMin = float.MaxValue, yMax = float.MinValue;
            found = Rect.zero;
            if (under == null || !under.gameObject.activeInHierarchy) return false;
            Rect screen = root.rect;
            foreach (Graphic graphic in under.GetComponentsInChildren<Graphic>(false))
            {
                if (!graphic.isActiveAndEnabled || graphic.color.a < 0.02f || graphic.canvasRenderer.GetInheritedAlpha() < 0.02f)
                    continue;
                if (skip != null && graphic.transform.IsChildOf(skip)) continue;
                Mask mask = graphic.GetComponent<Mask>();
                if (mask != null && mask.enabled && !mask.showMaskGraphic) continue;
                Rect local;
                if (!Filled(graphic, out local)) continue;
                float gxMin = float.MaxValue, gxMax = float.MinValue, gyMin = float.MaxValue, gyMax = float.MinValue;
                Corners[0] = new Vector3(local.xMin, local.yMin, 0f);
                Corners[1] = new Vector3(local.xMin, local.yMax, 0f);
                Corners[2] = new Vector3(local.xMax, local.yMax, 0f);
                Corners[3] = new Vector3(local.xMax, local.yMin, 0f);
                foreach (Vector3 corner in Corners)
                {
                    Vector3 at = root.InverseTransformPoint(graphic.rectTransform.TransformPoint(corner));
                    gxMin = Mathf.Min(gxMin, at.x);
                    gxMax = Mathf.Max(gxMax, at.x);
                    gyMin = Mathf.Min(gyMin, at.y);
                    gyMax = Mathf.Max(gyMax, at.y);
                }
                if (gyMax < low || gyMin > high) continue;
                if (gxMax < screen.xMin || gxMin > screen.xMax || gyMax < screen.yMin || gyMin > screen.yMax) continue;
                xMin = Mathf.Min(xMin, gxMin);
                xMax = Mathf.Max(xMax, gxMax);
                yMin = Mathf.Min(yMin, gyMin);
                yMax = Mathf.Max(yMax, gyMax);
            }
            if (xMin > xMax) return false;
            found = Rect.MinMaxRect(xMin, yMin, xMax, yMax);
            return true;
        }

        // The part of a graphic's rectangle it draws in, in its own units, as
        // Unity's Image places a simple picture that keeps its aspect
        // (Image.PreserveSpriteAspectRatio: shrunk along one side, towards the
        // pivot); a text by its letters.
        private static bool Filled(Graphic graphic, out Rect local)
        {
            local = graphic.rectTransform.rect;
            TMP_Text text = graphic as TMP_Text;
            if (text != null)
            {
                if (string.IsNullOrEmpty(text.text)) return false;
                Bounds bounds = text.textBounds;
                if (bounds.size.x <= 0f) return false;
                local = Rect.MinMaxRect(bounds.min.x, bounds.min.y, bounds.max.x, bounds.max.y);
                return true;
            }
            Image image = graphic as Image;
            if (image == null || !image.preserveAspect || image.type != Image.Type.Simple || image.sprite == null) return true;
            Vector2 size = image.sprite.rect.size;
            if (size.x <= 0f || size.y <= 0f || local.width <= 0f || local.height <= 0f) return true;
            Vector2 pivot = graphic.rectTransform.pivot;
            float spriteRatio = size.x / size.y;
            if (spriteRatio > local.width / local.height)
            {
                float height = local.width / spriteRatio;
                local.y += (local.height - height) * pivot.y;
                local.height = height;
            }
            else
            {
                float width = local.height * spriteRatio;
                local.x += (local.width - width) * pivot.x;
                local.width = width;
            }
            return true;
        }

        private bool logged;

        // One unit of the frame's offset in the root canvas's units.
        private float Unit(Transform canvas)
        {
            if (canvas == null || frame.parent == null || canvas.lossyScale.x <= 0f) return 1f;
            float unit = frame.parent.lossyScale.x / canvas.lossyScale.x;
            return unit > 0f ? unit : 1f;
        }

        // The offset last asked for in the settings.
        private float requested = float.NaN;

        // By the drag, in screen pixels: into the canvas's units, kept on screen,
        // and saved.
        internal void Drag(float screenDelta)
        {
            if (panel == null || frame == null) return;
            Canvas canvas = frame.GetComponentInParent<Canvas>();
            Canvas root = canvas != null ? canvas.rootCanvas : null;
            float scale = root != null && root.scaleFactor > 0f ? root.scaleFactor : 1f;
            requested = Clamp(applied + screenDelta / scale / Unit(root != null ? root.transform : null));
            Settle();
            ReDefinitionAddon addon = ReDefinitionAddon.Instance;
            if (addon != null) addon.NavballOffset = requested = applied;
        }

        private void Shift(float dx)
        {
            Vector2 by = new Vector2(dx, 0f);
            frame.anchoredPosition += by;
            if (panel.states != null)
                foreach (UIPanelTransition.PanelPosition state in panel.states)
                    if (state != null) state.position += by;
            if (KeptPosition != null && KeptPosition.FieldType == typeof(Vector2))
                KeptPosition.SetValue(panel, (Vector2)KeptPosition.GetValue(panel) + by);
            applied += dx;
            Sides();
        }

        private RectTransform modes;
        private Vector2 modesAt;
        private readonly List<RectTransform> buttons = new List<RectTransform>();
        private readonly List<Vector2> buttonsAt = new List<Vector2>();
        private bool mirrored;

        // How far the row of button `index` stands from the rows' mean, as KSP placed
        // them: the rows are the buttons at one height.
        private float RowShift(int index)
        {
            float y = buttonsAt[index].y;
            float row = 0f, rows = 0f;
            int inRow = 0, rowCount = 0;
            Dictionary<int, float> sums = new Dictionary<int, float>();
            Dictionary<int, int> counts = new Dictionary<int, int>();
            for (int i = 0; i < buttonsAt.Count; i++)
            {
                int key = Mathf.RoundToInt(buttonsAt[i].y);
                float sum;
                int count;
                sums.TryGetValue(key, out sum);
                counts.TryGetValue(key, out count);
                sums[key] = sum + buttonsAt[i].x;
                counts[key] = count + 1;
            }
            foreach (KeyValuePair<int, float> pair in sums)
            {
                rows += pair.Value / counts[pair.Key];
                rowCount++;
            }
            int own = Mathf.RoundToInt(y);
            row = sums[own] / counts[own];
            inRow = counts[own];
            return inRow > 0 && rowCount > 0 ? row - rows / rowCount : 0f;
        }

        // The SAS modes on the side of the navball towards the screen's middle.
        private void Sides()
        {
            if (modes == null)
            {
                VesselAutopilotUI ui = frame.GetComponentInChildren<VesselAutopilotUI>(true);
                modes = ui != null ? ui.transform as RectTransform : null;
                if (modes == null) return;
                modesAt = modes.anchoredPosition;
                for (int i = 0; i < modes.childCount; i++)
                {
                    RectTransform button = modes.GetChild(i) as RectTransform;
                    if (button == null) continue;
                    buttons.Add(button);
                    buttonsAt.Add(button.anchoredPosition);
                }
            }
            Canvas canvas = frame.GetComponentInParent<Canvas>();
            Transform root = canvas != null ? canvas.rootCanvas.transform : null;
            if (root == null) return;
            Vector3[] corners = new Vector3[4];
            frame.GetWorldCorners(corners);
            float middle = root.InverseTransformPoint((corners[0] + corners[2]) / 2f).x;
            bool right = middle < -1f;
            if (right == mirrored) return;
            mirrored = right;
            modes.anchoredPosition = modesAt;
            // The buttons keep their order; only the curve turns: each row's shift
            // from the rows' mean, which bends them along the rim, the other way.
            for (int i = 0; i < buttons.Count; i++)
                if (buttons[i] != null)
                    buttons[i].anchoredPosition = right ? new Vector2(buttonsAt[i].x - 2f * RowShift(i), buttonsAt[i].y) : buttonsAt[i];
            if (!right) return;
            // The buttons' span mirrored about the frame's middle, in the frame's own
            // units, then in the modes' parent's.
            float left = float.MaxValue, edge = float.MinValue;
            foreach (RectTransform button in buttons)
            {
                if (button == null) continue;
                button.GetWorldCorners(corners);
                left = Mathf.Min(left, frame.InverseTransformPoint(corners[0]).x);
                edge = Mathf.Max(edge, frame.InverseTransformPoint(corners[2]).x);
            }
            if (left > edge) return;
            float centre = frame.rect.center.x;
            Vector3 shift = frame.TransformVector(new Vector3(2f * centre - edge - left, 0f, 0f));
            Transform parent = modes.parent != null ? modes.parent : frame;
            modes.anchoredPosition = modesAt + new Vector2(parent.InverseTransformVector(shift).x, 0f);
        }

        // At most as far as keeps what the navball draws within its room (Room),
        // each side on its own: the SAS modes stand out to one side of the frame
        // (AutopilotModes, sharedassets7), so the frame's own width is not enough.
        // Nothing drawn (the navball hidden): the offset as asked.
        private float Clamp(float offset)
        {
            RectTransform canvasRect = Root();
            float min, max;
            Drawn(out min, out max);
            if (canvasRect == null || float.IsNaN(min)) return offset;
            // The frame's offset in the canvas's units: the canvas and the frame's
            // parents are not scaled against each other in KSP's flight UI, but the
            // frame's parent may be.
            float unit = Unit(canvasRect);
            float left, right;
            Room(out left, out right);
            float lowest = applied + (left - min) / unit;
            float highest = applied + (right - max) / unit;
            if (!logged)
            {
                logged = true;
                Debug.Log(Log.Tag + " Navball drawn from " + min.ToString("0") + " to " + max.ToString("0") + " with room from "
                          + left.ToString("0") + " to " + right.ToString("0") + ": it moves from "
                          + lowest.ToString("0") + " to " + highest.ToString("0") + ".");
            }
            // No room either way: in the middle of what there is.
            if (lowest > highest) return (lowest + highest) / 2f;
            return Mathf.Clamp(offset, lowest, highest);
        }
    }

    // On KSP's show/hide button: a drag moves the navball, a click still shows
    // and hides it.
    internal sealed class NavballDragHandle : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler
    {
        internal NavballDrag Owner;

        // The press and the drag are on one object, so the input module would
        // still click the button when the drag ends.
        public void OnBeginDrag(PointerEventData data)
        {
            if (data.button != PointerEventData.InputButton.Left) return;
            data.eligibleForClick = false;
        }

        // The left button only: the input module drags with every button, and a
        // right drag from here turns the camera.
        public void OnDrag(PointerEventData data)
        {
            if (data.button != PointerEventData.InputButton.Left) return;
            if (Owner != null) Owner.Drag(data.delta.x);
        }

        public void OnEndDrag(PointerEventData data)
        {
        }
    }
}
