using System.Collections;
using TMPro;
using ReDefinition.Autopilot;
using ReDefinition.Autopilot.Flight;
using ReDefinition.Core;
using ReDefinition.Engineer;
using ReDefinition.Ui;
using UnityEngine;
using UnityEngine.UI;

namespace ReDefinition.Hud
{
    // The button of the autopilot, at the top of the screen right of the time panel, small, in the panel's row, with a red lamp in a grey ring. The lamp is lit
    // while the autopilot flies, dark while it does not.
    // One press starts what there is to fly: the route of the planner or the climb and the descent the navigation guides, else the maneuver nodes made in
    // the map, flown by MechJeb. Where there is nothing to fly it offers the goals the vessel can be sent to (LiveMenu), which only set the planner's
    // route. Another press lets the vessel go. Where MechJeb cannot fly the button cannot be pressed.
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class LiveButton : MonoBehaviour
    {
        // The edge of the button, in the canvas's units, and the gap that its place right of the time panel is counted in.
        private const float Size = 38f;
        private const float Gap = 10f;

        private RectTransform root;
        private RectTransform panel;
        private GameObject made;
        private Image face;
        private Button button;
        private KSP.UI.TooltipTypes.TooltipController_Text tooltip;
        private Sprite lit, dark;
        private float asked;
        private bool possible;
        private string reason;

        private IEnumerator Start()
        {
            float until = Time.unscaledTime + 15f;
            while (Time.unscaledTime < until && (TimeWarpButton.Label() == null || RouteDriver.Instance == null)) yield return null;
            TextMeshProUGUI label = TimeWarpButton.Label();
            lit = IconSprites.Load("ReDefinition/Icons/Live_On");
            dark = IconSprites.Load("ReDefinition/Icons/Live_Off");
            Canvas canvas = label != null ? label.GetComponentInParent<Canvas>() : null;
            Canvas top = canvas != null ? canvas.rootCanvas : null;
            if (label == null || top == null || lit == null || dark == null)
            {
                Debug.Log(Log.Tag + " The autopilot button is not shown: the time panel, the canvas or the pictures were not found.");
                yield break;
            }
            // The row of the time panel: its background is as wide and as high as the panel.
            panel = label.transform.parent as RectTransform;
            root = (RectTransform)top.transform;
            made = new GameObject("ReDefinitionLiveButton", typeof(RectTransform), typeof(Image), typeof(Button));
            made.layer = panel != null ? panel.gameObject.layer : top.gameObject.layer;
            RectTransform rect = (RectTransform)made.transform;
            rect.SetParent(root, false);
            rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.sizeDelta = new Vector2(Size, Size);
            face = made.GetComponent<Image>();
            face.preserveAspect = true;
            button = made.GetComponent<Button>();
            button.transition = Selectable.Transition.None;
            button.targetGraphic = face;
            UiHygiene.MouseOnly(button);
            button.onClick.AddListener(Pressed);
            tooltip = UiHygiene.Tooltip(made, "Autopilot");
            if (tooltip != null) tooltip.continuousUpdate = true;
            Place();
            Debug.Log(Log.Tag + " The autopilot button stands beside the time panel.");
        }

        private void OnDestroy()
        {
            if (button != null) button.onClick.RemoveListener(Pressed);
            if (made != null) Destroy(made);
        }

        private void Pressed()
        {
            if (RouteDriver.Instance != null) RouteDriver.Instance.ToggleAutopilot(FlightGlobals.ActiveVessel);
        }

        // Beside the time panel, at the top of the screen: under the panel the staging stack stands, which grows and opens out over it.
        private void Place()
        {
            RectTransform rect = face != null ? (RectTransform)face.transform : null;
            if (rect == null || root == null) return;
            float right = 0f;
            if (panel != null)
            {
                Transform background = panel.Find("Background");
                RectTransform area = background != null ? background as RectTransform : panel;
                var corners = new Vector3[4];
                area.GetWorldCorners(corners);
                right = root.InverseTransformPoint(corners[2]).x - root.rect.xMin;
            }
            // Past the sunrise button, which slides out at the panel's right edge as it opens, and level with the panel's row, which is about as high.
            rect.anchoredPosition = new Vector2(right + 7.5f * Gap, 0f);
        }

        private void LateUpdate()
        {
            if (face == null) return;
            Vessel vessel = FlightGlobals.ActiveVessel;
            RouteDriver driver = RouteDriver.Instance;
            RouteState state = RouteState.Of(vessel);
            bool flying = driver != null && (driver.Flying || (state != null && state.Autopilot));
            // Whether MechJeb can fly is asked now and then, not in every frame: it is found by reflection over the vessel's modules.
            if (Time.unscaledTime > asked)
            {
                asked = Time.unscaledTime + 1f;
                possible = vessel != null && MechJebDriver.CanFly(vessel);
                reason = possible ? null : MechJebDriver.Unavailable;
            }
            bool able = flying || possible;
            face.sprite = flying ? lit : dark;
            face.color = able ? Color.white : new Color(1f, 1f, 1f, 0.45f);
            button.interactable = able;
            if (tooltip != null)
                tooltip.textString = flying ? "Autopilot On" : able ? "Autopilot Off" : "Autopilot (" + (reason ?? "No MechJeb") + ")";
            Place();
        }
    }
}
