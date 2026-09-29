using System.Collections;
using System.Reflection;
using System;
using KSP.UI;
using KSP.UI.Screens.Flight;
using ReDefinition.Core;
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
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class NavballDrag : MonoBehaviour
    {
        private static readonly FieldInfo KeptPosition =
            typeof(UIPanelTransition).GetField("panelPosition", BindingFlags.Instance | BindingFlags.NonPublic);


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
                Debug.Log(Log.Tag + " The navball moves sideways by dragging its show/hide button.");
            }
            catch (Exception e)
            {
                Debug.LogWarning(Log.Tag + " The navball cannot be moved: " + e);
                panel = null;
            }
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

        // The offset last asked for in the settings.
        private float requested = float.NaN;

        // By the drag, in screen pixels: into the canvas's units, kept on screen,
        // and saved.
        internal void Drag(float screenDelta)
        {
            if (panel == null || frame == null) return;
            Canvas canvas = frame.GetComponentInParent<Canvas>();
            float scale = canvas != null && canvas.scaleFactor > 0f ? canvas.scaleFactor : 1f;
            float target = Clamp(applied + screenDelta / scale);
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
            float unit = frame.parent != null && canvasRect.lossyScale.x > 0f ? frame.parent.lossyScale.x / canvasRect.lossyScale.x : 1f;
            if (unit <= 0f) unit = 1f;
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
