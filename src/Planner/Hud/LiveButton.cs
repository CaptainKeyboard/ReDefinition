using ReDefinition.Planner;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using ReDefinition.Planner.Autopilot;
using ReDefinition.Planner.Autopilot.Flight;
using ReDefinition.Core;
using ReDefinition.Planner.Engineer;
using ReDefinition.Ui;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ReDefinition.Planner.Hud
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
        private float nextLook;
        private float? beside;
        private float? clusterRight;
        private float nextActivity;
        private string activity = "";
        private bool possible;
        private string reason;

        private IEnumerator Start()
        {
            float until = Time.unscaledTime + 15f;
            while (Time.unscaledTime < until && (WarpLabel.Find() == null || RouteDriver.Instance == null)) yield return null;
            TextMeshProUGUI label = WarpLabel.Find();
            lit = IconSprites.Load("ReDefinition/Planner/Icons/Live_On");
            dark = IconSprites.Load("ReDefinition/Planner/Icons/Live_Off");
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
            made.AddComponent<RightClick>().Pressed = RightPressed;
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

        // A press while the menu is open puts the menu away and does nothing else.
        private void Pressed()
        {
            if (LiveMenu.IsOpen)
            {
                LiveMenu.Close();
                return;
            }
            if (RouteDriver.Instance != null) RouteDriver.Instance.ToggleAutopilot(FlightGlobals.ActiveVessel);
        }

        // The right button opens the menu, also where there is a route, and puts it away again.
        private void RightPressed()
        {
            if (LiveMenu.IsOpen)
            {
                LiveMenu.Close();
                return;
            }
            LiveMenu.Open(FlightGlobals.ActiveVessel, true);
        }

        private float busyUntil;

        // Whether the mouse is on the button.
        private bool PointerOver(RectTransform rect)
        {
            Canvas canvas = rect.GetComponentInParent<Canvas>();
            Camera camera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
            return RectTransformUtility.RectangleContainsScreenPoint(rect, Input.mousePosition, camera);
        }

        // The right button's press on the button, which the Button component does not pass on.
        private sealed class RightClick : MonoBehaviour, IPointerClickHandler
        {
            internal System.Action Pressed;

            public void OnPointerClick(PointerEventData eventData)
            {
                if (eventData.button == PointerEventData.InputButton.Right && Pressed != null) Pressed();
            }
        }

        // Beside the time panel, at the top of the screen: under the panel the staging stack stands, which grows and opens out over it.
        private void Place()
        {
            RectTransform rect = face != null ? (RectTransform)face.transform : null;
            if (rect == null || root == null) return;
            // The menu and the tooltip open under the button and would count as a control in its place; the place is kept while they are up.
            if (LiveMenu.IsOpen || PointerOver(rect)) busyUntil = Time.unscaledTime + 1.5f;
            if (Time.unscaledTime > nextLook && Time.unscaledTime > busyUntil)
            {
                nextLook = Time.unscaledTime + 1f;
                // In the map view the top frame with the gauge is drawn away: the button stands past the time panel there, not where the gauge was.
                bool inMap = MapView.MapIsEnabled;
                beside = inMap ? null : BesideGauge();
                List<KeyValuePair<string, Vector2>> items = TopStripItems();
                float? gauge = beside;
                if (beside.HasValue) beside = FreeEdge(beside.Value);
                clusterRight = beside.HasValue ? null : RightOfTopLeftStrip(TopStripGraphics());
                if (clusterRight.HasValue) clusterRight = FreeEdge(clusterRight.Value + root.rect.xMin) - root.rect.xMin;
                LogStrip(gauge, beside ?? clusterRight, items);
            }
            // The gap below the top edge is the gap beside the neighbour on the left.
            float down = -0.6f * Gap;
            if (beside.HasValue)
            {
                rect.anchoredPosition = new Vector2(beside.Value - root.rect.xMin + 0.6f * Gap, down);
                return;
            }
            if (clusterRight.HasValue)
            {
                rect.anchoredPosition = new Vector2(clusterRight.Value + 0.6f * Gap, down);
                return;
            }
            float right = 0f;
            if (panel != null)
            {
                Transform background = panel.Find("Background");
                RectTransform area = background != null ? background as RectTransform : panel;
                var corners = new Vector3[4];
                area.GetWorldCorners(corners);
                right = root.InverseTransformPoint(corners[2]).x - root.rect.xMin;
            }
            // Without the gauge's buttons: past the sunrise button, which slides out at the panel's right edge as it opens.
            rect.anchoredPosition = new Vector2(right + 7.5f * Gap, down);
        }

        // Where the map view has no gauge: the right edge, in the canvas's units, of whatever stands in the top left strip, the time panel and the
        // icons that join it there. The button stands past it, so that it covers nothing. Null where nothing is found.
        private float? RightOfTopLeftStrip(List<Vector2> spans)
        {
            float? best = null;
            foreach (Vector2 span in spans)
            {
                if (span.x > root.rect.xMin + 0.4f * root.rect.width) continue;
                float right = span.y - root.rect.xMin;
                if (!best.HasValue || right > best.Value) best = right;
            }
            return best;
        }

        // The left and right edges, in the canvas's units, of the small things that stand in the top strip of the screen. Backgrounds that span
        // the screen are left out, and so is the button.
        // The controls of the game that stand in the top strip of the screen, wherever their canvas is: their left and right edges are found on the
        // screen and put in the canvas's units. Controls as wide as the screen and the button itself are left out.
        private List<KeyValuePair<string, Vector2>> TopStripItems()
        {
            var items = new List<KeyValuePair<string, Vector2>>();
            if (root == null) return items;
            var corners = new Vector3[4];
            float top = root.rect.yMax;
            Canvas rootCanvas = root.GetComponent<Canvas>();
            Camera rootCamera = rootCanvas != null && rootCanvas.renderMode != RenderMode.ScreenSpaceOverlay ? rootCanvas.worldCamera : null;
            foreach (Selectable selectable in Resources.FindObjectsOfTypeAll<Selectable>())
            {
                if (selectable == null || !selectable.gameObject.scene.IsValid() || !selectable.gameObject.activeInHierarchy) continue;
                if (made != null && selectable.transform.IsChildOf(made.transform)) continue;
                var rect = selectable.transform as RectTransform;
                if (rect == null) continue;
                Canvas canvas = rect.GetComponentInParent<Canvas>();
                Camera camera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
                rect.GetWorldCorners(corners);
                Vector2 lowScreen = RectTransformUtility.WorldToScreenPoint(camera, corners[0]);
                Vector2 highScreen = RectTransformUtility.WorldToScreenPoint(camera, corners[2]);
                Vector2 low, high;
                if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(root, lowScreen, rootCamera, out low)) continue;
                if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(root, highScreen, rootCamera, out high)) continue;
                if (high.x - low.x < 4f || high.x - low.x > 0.3f * root.rect.width || high.y - low.y < 4f || high.y - low.y > 90f) continue;
                if (low.y < top - 130f || high.y > top + 1f) continue;
                items.Add(new KeyValuePair<string, Vector2>(selectable.name, new Vector2(low.x, high.x)));
            }
            return items;
        }

        // The left and right edges of everything drawn in the top strip under the canvas, the time panel's icons too, which are no controls.
        private List<Vector2> TopStripGraphics()
        {
            var spans = new List<Vector2>();
            if (root == null) return spans;
            var corners = new Vector3[4];
            float top = root.rect.yMax;
            foreach (Graphic graphic in root.GetComponentsInChildren<Graphic>(false))
            {
                if (graphic == null || (made != null && graphic.transform.IsChildOf(made.transform)) || graphic.color.a < 0.05f) continue;
                graphic.rectTransform.GetWorldCorners(corners);
                Vector3 low = root.InverseTransformPoint(corners[0]);
                Vector3 high = root.InverseTransformPoint(corners[2]);
                float width = high.x - low.x, height = high.y - low.y;
                if (width < 4f || height < 4f || width > 0.3f * root.rect.width || height > 90f) continue;
                if (low.y < top - 130f || high.y > top + 1f) continue;
                spans.Add(new Vector2(low.x, high.x));
            }
            return spans;
        }

        private string lastStrip = "";

        // What the button was placed by, in the log where it changes: the gauge's edge and the edge after the chaining.
        // The controls of the middle of the top strip follow with their edges, to show what stands beside the button.
        private void LogStrip(float? gauge, float? placed, List<KeyValuePair<string, Vector2>> items)
        {
            if (root == null) return;
            var text = new System.Text.StringBuilder();
            foreach (KeyValuePair<string, Vector2> item in items)
                if (item.Value.x > root.rect.xMin + 0.3f * root.rect.width && item.Value.x < root.rect.xMin + 0.7f * root.rect.width)
                    text.Append(' ').Append(item.Key).Append(' ').Append(item.Value.x.ToString("0")).Append("..").Append(item.Value.y.ToString("0")).Append(';');
            string now = (gauge.HasValue ? gauge.Value.ToString("0") : "-") + "/" + (placed.HasValue ? placed.Value.ToString("0") : "-") + text;
            if (now == lastStrip) return;
            lastStrip = now;
            Debug.Log(Log.Tag + " The autopilot button is placed by the gauge's edge " + (gauge.HasValue ? gauge.Value.ToString("0") : "(none)") + ", chained to "
                      + (placed.HasValue ? placed.Value.ToString("0") : "(none)") + "; the top strip holds:" + text);
        }

        // The edge moved right until the button's place is free: the game's action group tiles can have a column or a widget more at their side, and
        // mods put controls there. Free means that no control takes a press at a few points of the place, which the event system is asked.
        private float FreeEdge(float edge)
        {
            EventSystem events = EventSystem.current;
            Canvas rootCanvas = root != null ? root.GetComponent<Canvas>() : null;
            if (events == null || rootCanvas == null) return edge;
            Camera camera = rootCanvas.renderMode != RenderMode.ScreenSpaceOverlay ? rootCanvas.worldCamera : null;
            var hits = new List<RaycastResult>();
            for (int step = 0; step < 40; step++)
            {
                bool taken = false;
                for (int column = 0; column < 4 && !taken; column++)
                    for (int row = 0; row < 3 && !taken; row++)
                    {
                        float x = edge + 0.6f * Gap + Size * (column + 0.5f) / 4f;
                        float y = root.rect.yMax - 0.6f * Gap - Size * (row + 0.5f) / 3f;
                        var data = new PointerEventData(events) { position = RectTransformUtility.WorldToScreenPoint(camera, root.TransformPoint(new Vector3(x, y, 0f))) };
                        hits.Clear();
                        events.RaycastAll(data, hits);
                        foreach (RaycastResult hit in hits)
                            if (hit.gameObject != null && (made == null || !hit.gameObject.transform.IsChildOf(made.transform))) taken = true;
                    }
                if (!taken) return edge;
                edge += 0.5f * Gap;
            }
            return edge;
        }

        // The right edge, in the canvas's units, of the game's buttons for the lights, the gear and the brakes beside the altimeter and the stage
        // gauge, which the game names ButtonActionGroup... Null where they are not found.
        private float? BesideGauge()
        {
            if (root == null) return null;
            var corners = new Vector3[4];
            float? best = null;
            foreach (Selectable selectable in root.GetComponentsInChildren<Selectable>(false))
            {
                if (selectable == null || !selectable.name.StartsWith("ButtonActionGroup")) continue;
                ((RectTransform)selectable.transform).GetWorldCorners(corners);
                float right = root.InverseTransformPoint(corners[2]).x;
                if (!best.HasValue || right > best.Value) best = right;
            }
            return best;
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
            // What the autopilot does now is asked twice a second: it is read from MechJeb by reflection.
            if (Time.unscaledTime > nextActivity)
            {
                nextActivity = Time.unscaledTime + 0.5f;
                activity = AutopilotActivity.Describe(vessel) ?? "";
            }
            if (tooltip != null)
            {
                string title = flying ? "Autopilot On" : able ? "Autopilot Off" : "Autopilot (" + (reason ?? "No MechJeb") + ")";
                tooltip.textString = title + (activity.Length > 0 ? "\n" + activity : "") + "\nRight-Click: Menu";
            }
            Place();
        }
    }
}
