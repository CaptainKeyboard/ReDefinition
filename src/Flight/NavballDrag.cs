using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System;
using KSP.UI;
using KSP.UI.Screens.Flight;
using ReDefinition.Core;
using ReDefinition.Settings;
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
    // the screen's middle.
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
            float wanted = drag.Clamp(addon != null ? addon.NavballOffset : 0f);
            if (Mathf.Abs(wanted) >= 0.5f) drag.Shift(wanted);
            else drag.Sides();
        }

        private void Update()
        {
            if (panel == null || frame == null) return;
            ReDefinitionAddon addon = ReDefinitionAddon.Instance;
            if (addon == null) return;
            // Worked out only when the setting changes, not every frame.
            if (Mathf.Abs(addon.NavballOffset - requested) < 0.01f) return;
            requested = addon.NavballOffset;
            float wanted = Clamp(requested);
            if (Mathf.Abs(wanted - applied) >= 0.5f) Shift(wanted - applied);
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
            float target = Clamp(applied + screenDelta / scale / Unit(root != null ? root.transform : null));
            Shift(target - applied);
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
        private bool mirrored;

        // The SAS modes on the side of the navball towards the screen's middle.
        private void Sides()
        {
            if (modes == null)
            {
                VesselAutopilotUI ui = frame.GetComponentInChildren<VesselAutopilotUI>(true);
                modes = ui != null ? ui.transform as RectTransform : null;
                if (modes == null) return;
                modesAt = modes.anchoredPosition;
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
            if (!right) return;
            // Mirrored about the frame's middle, in the frame's own units, then in
            // the modes' parent's.
            modes.GetWorldCorners(corners);
            float left = frame.InverseTransformPoint(corners[0]).x;
            float edge = frame.InverseTransformPoint(corners[2]).x;
            float centre = frame.rect.center.x;
            Vector3 shift = frame.TransformVector(new Vector3(2f * centre - edge - left, 0f, 0f));
            Transform parent = modes.parent != null ? modes.parent : frame;
            modes.anchoredPosition = modesAt + new Vector2(parent.InverseTransformVector(shift).x, 0f);
        }

        // At most as far as keeps everything of the navball on the screen, each
        // side on its own: the SAS modes stand out to the left of the frame
        // (AutopilotModes, sharedassets7), so the frame's own width is not enough.
        // Measured where its pictures and texts stand now (every Graphic under the
        // frame, the hidden ones too, since they show later), in the canvas's own
        // units against the canvas's edges. Empty holders, which can reach far
        // beyond what is drawn, do not count.
        private float Clamp(float offset)
        {
            Canvas canvas = frame.GetComponentInParent<Canvas>();
            RectTransform canvasRect = canvas != null ? canvas.rootCanvas.transform as RectTransform : null;
            if (canvasRect == null) return offset;
            float min = float.MaxValue, max = float.MinValue;
            Vector3[] corners = new Vector3[4];
            foreach (Graphic graphic in frame.GetComponentsInChildren<Graphic>(true))
            {
                graphic.rectTransform.GetWorldCorners(corners);
                foreach (Vector3 corner in corners)
                {
                    float x = canvasRect.InverseTransformPoint(corner).x;
                    min = Mathf.Min(min, x);
                    max = Mathf.Max(max, x);
                }
            }
            if (min > max) return offset;
            // The frame's offset in the canvas's units: the canvas and the frame's
            // parents are not scaled against each other in KSP's flight UI, but the
            // frame's parent may be.
            float unit = Unit(canvasRect);
            float lowest = applied + (canvasRect.rect.xMin - min) / unit;
            float highest = applied + (canvasRect.rect.xMax - max) / unit;
            if (!logged)
            {
                logged = true;
                Debug.Log(Log.Tag + " Navball drawn from " + min.ToString("0") + " to " + max.ToString("0") + " in a canvas from "
                          + canvasRect.rect.xMin.ToString("0") + " to " + canvasRect.rect.xMax.ToString("0") + ": it moves from "
                          + lowest.ToString("0") + " to " + highest.ToString("0") + ".");
            }
            if (lowest > highest) return applied;
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
