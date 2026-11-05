using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using KSP.UI;
using ReDefinition.Core;
using ReDefinition.Ui;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ReDefinition.Hud
{
    // Better Time Warp's button in the flight's time panel.
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class TimeWarpBarFlight : TimeWarpBar
    {
    }

    // Better Time Warp's button in the space centre's time panel.
    [KSPAddon(KSPAddon.Startup.SpaceCentre, false)]
    public class TimeWarpBarSpaceCentre : TimeWarpBar
    {
    }

    // The WARP label of the time panel as Better Time Warp's button: the label is clickable and an arrow
    // stands right of it, down while the mod's window is closed and up while it is open (TimeWarpButton). A click
    // opens and closes the mod's window, which opens under the panel.
    public abstract class TimeWarpBar : MonoBehaviour
    {
        // The arrow's size on the label, in KSP's UI units: about the label's text height.
        private const float ArrowSize = 10f;
        // Between the text and the arrow, and around the pair, in KSP's UI units.
        private const float Gap = 3f;
        // Between the panel's lower edge and the mod's window, in screen pixels.
        private const float WindowGap = 6f;
        private static readonly FieldInfo ContentField = TypeLookup.Find(TimeWarpButton.ModType) != null
            ? TypeLookup.Find(TimeWarpButton.ModType).GetField("buttonContent", BindingFlags.Instance | BindingFlags.NonPublic)
            : null;
        // The arrow the mod shows, read every frame through a compiled getter; by the
        // FieldInfo where the runtime refuses the compiled read of a private field.
        private static System.Func<object, GUIContent> readContent;
        private static readonly System.Func<object, GUIContent> ReflectContent = target => ContentField.GetValue(target) as GUIContent;
        // The mod's three window rectangles, which it sets once when a scene loads.
        private static FieldInfo[] windowFields;

        // The click area over the label and the arrow, once built.
        private GameObject bar;
        private RectTransform barRect, arrowRect;
        private UIHoverSlidePanel slide;
        private RectTransform background;
        // The label and the mod were found: the area follows the mod's own switches
        // from then on (Follow), checked once a second.
        private bool found;
        private bool built;
        private bool failed;
        private float followAt;
        // The label's text bounds the area was laid out for.
        private Bounds laidOut;
        private Image icon;
        private TextMeshProUGUI label;
        private MonoBehaviour mod;
        private PropertyInfo open;
        private bool wasOpen;
        private Texture shown;
        // The sprites made here, destroyed with the bar; the mod's icons by texture.
        private readonly List<Sprite> made = new List<Sprite>();
        private readonly Dictionary<Texture2D, Sprite> icons = new Dictionary<Texture2D, Sprite>();

        // Waits for the WARP label and Better Time Warp, then follows the mod's switches.
        private IEnumerator Start()
        {
            System.Type type = TypeLookup.Find(TimeWarpButton.ModType);
            if (type == null || !TimeWarpButton.Routed || !FlightUiSwitch.On(s => s.TimeWarpPanelButton)) yield break;
            TimeWarpButton.Coming = TimeWarpButton.ModDrawsButton();
            float until = Time.unscaledTime + 15f;
            while (Time.unscaledTime < until)
            {
                label = TimeWarpButton.Label();
                mod = FindObjectOfType(type) as MonoBehaviour;
                if (label != null && mod != null) break;
                yield return new WaitForSecondsRealtime(0.5f);
            }
            TimeWarpButton.Coming = false;
            if (label == null || mod == null) yield break;
            open = mod.GetType().GetProperty("UIOpen", BindingFlags.Instance | BindingFlags.Public);
            if (readContent == null && ContentField != null)
            {
                try
                {
                    readContent = CompiledGetter.Of<GUIContent>(ContentField);
                }
                catch (System.Exception)
                {
                    readContent = null;
                }
                if (readContent == null) readContent = ReflectContent;
            }
            found = true;
            Follow();
        }

        // Makes the click area where the mod draws its button (TimeWarpButton.ModDrawsButton) the
        // first time it does. It hides it where the player has switched the mod or its button off
        // in its settings, so the label does not open a window the mod does not show.
        private void Follow()
        {
            followAt = Time.unscaledTime + 1f;
            bool draws = TimeWarpButton.ModDrawsButton();
            if (!built)
            {
                if (draws && !failed && label != null) TryBuild();
                return;
            }
            // Gone with the scene's UI.
            if (bar == null || bar.activeSelf == draws) return;
            bar.SetActive(draws);
            TimeWarpButton.Replaced = draws;
            Debug.Log(Log.Tag + " Better Time Warp's button on the WARP label " + (draws ? "shown again" : "hidden")
                      + ", as the mod's own settings say.");
        }

        // Builds the area; where that fails, nothing of it stays and the mod's own button counts again.
        private void TryBuild()
        {
            try
            {
                Build();
                built = true;
            }
            catch (System.Exception e)
            {
                failed = true;
                CompatibilityLog.Warn("timewarp-bar", "Better Time Warp's button stays where the mod puts it ("
                                                      + CompatibilityLog.Reason(e) + ").");
                if (bar != null) Destroy(bar);
                bar = null;
                TimeWarpButton.Replaced = false;
            }
        }

        // The click area over the label: see-through, lit a little under the pointer, with the arrow on its right.
        private void Build()
        {
            RectTransform row = label.transform.parent as RectTransform;
            background = row != null ? row.Find("Background") as RectTransform : null;
            if (row == null || background == null) throw new System.InvalidOperationException("no background in the time panel");
            slide = row.GetComponent<UIHoverSlidePanel>();

            bar = new GameObject("ReDefinitionTimeWarpLabelButton", typeof(RectTransform), typeof(Image), typeof(Button));
            bar.layer = label.gameObject.layer;
            barRect = (RectTransform)bar.transform;
            barRect.SetParent(label.rectTransform, false);
            barRect.anchorMin = barRect.anchorMax = barRect.pivot = new Vector2(0.5f, 0.5f);
            Image area = bar.GetComponent<Image>();
            area.color = Color.white;
            Button button = bar.GetComponent<Button>();
            button.targetGraphic = area;
            button.transition = Selectable.Transition.ColorTint;
            ColorBlock colours = button.colors;
            colours.normalColor = new Color(1f, 1f, 1f, 0f);
            colours.highlightedColor = new Color(1f, 1f, 1f, 0.12f);
            colours.pressedColor = new Color(1f, 1f, 1f, 0.25f);
            colours.selectedColor = colours.normalColor;
            colours.disabledColor = colours.normalColor;
            colours.colorMultiplier = 1f;
            button.colors = colours;
            button.onClick.AddListener(Press);
            UiHygiene.MouseOnly(button);
            UiHygiene.Tooltip(bar, "Better Time Warp");

            GameObject arrow = new GameObject("Arrow", typeof(RectTransform), typeof(Image));
            arrow.layer = bar.layer;
            arrowRect = (RectTransform)arrow.transform;
            arrowRect.SetParent(barRect, false);
            arrowRect.anchorMin = arrowRect.anchorMax = arrowRect.pivot = new Vector2(0.5f, 0.5f);
            arrowRect.sizeDelta = new Vector2(ArrowSize, ArrowSize);
            icon = arrow.GetComponent<Image>();
            icon.preserveAspect = true;
            icon.raycastTarget = false;
            icon.enabled = false;

            Layout();
            TimeWarpButton.Replaced = true;
            Debug.Log(Log.Tag + " Better Time Warp's button on the WARP label: the label clickable, the mod's arrow right of it.");
        }

        // The area over the label's text and the arrow right of it, in the label's own space. It is laid out
        // again when the text's size changes (the bounds are zero while the label is hidden).
        private void Layout()
        {
            if (!label.gameObject.activeInHierarchy) return;
            Bounds bounds = label.textBounds;
            if (bounds.size.x <= 0f || bounds.size.y <= 0f || bounds == laidOut) return;
            laidOut = bounds;
            float width = bounds.size.x + 3f * Gap + ArrowSize;
            float height = Mathf.Max(bounds.size.y, ArrowSize) + 2f * Gap;
            barRect.sizeDelta = new Vector2(width, height);
            barRect.localPosition = new Vector3(bounds.min.x - Gap + width / 2f, bounds.center.y, 0f);
            arrowRect.anchoredPosition = new Vector2(width / 2f - Gap - ArrowSize / 2f, 0f);
        }

        // Every frame: the mod's switches followed once a second, the area kept over the label, its arrow the mod's,
        // and the window put under the panel when it opens.
        private void LateUpdate()
        {
            if (!found) return;
            if (Time.unscaledTime >= followAt) Follow();
            if (bar == null || !bar.activeSelf || label == null) return;
            Layout();
            GUIContent content = Content();
            Texture2D texture = content != null ? content.image as Texture2D : null;
            if (icon != null && texture != null && texture != shown)
            {
                shown = texture;
                Sprite sprite;
                if (!icons.TryGetValue(texture, out sprite) || sprite == null)
                {
                    sprite = Sprite.Create(texture, new Rect(0f, 0f, texture.width, texture.height), new Vector2(0.5f, 0.5f));
                    icons[texture] = sprite;
                    made.Add(sprite);
                }
                icon.sprite = sprite;
                icon.enabled = true;
            }
            bool isOpen = open != null && mod != null && (bool)open.GetValue(mod, null);
            if (isOpen && !wasOpen) PutWindowUnderPanel();
            wasOpen = isOpen;
        }

        // The mod's window under the time panel's lower edge, at its left, kept on the screen. The panel is measured
        // as it stands unfolded, since it folds away when the pointer leaves it. The window can be dragged afterwards,
        // as the mod lets it.
        private void PutWindowUnderPanel()
        {
            Camera camera = UIMasterController.Instance != null ? UIMasterController.Instance.uiCamera : null;
            if (camera == null || background == null || mod == null) return;
            try
            {
                if (windowFields == null)
                {
                    System.Type type = mod.GetType();
                    windowFields = new[]
                    {
                        type.GetField("quikWindowRect", BindingFlags.Instance | BindingFlags.NonPublic),
                        type.GetField("advWindowRect", BindingFlags.Instance | BindingFlags.NonPublic),
                        type.GetField("physSettingsRect", BindingFlags.Instance | BindingFlags.NonPublic)
                    };
                }
                foreach (FieldInfo field in windowFields)
                    if (field == null || field.FieldType != typeof(Rect)) return;
                Vector3[] corners = new Vector3[4];
                background.GetWorldCorners(corners);
                Vector3 shift = Vector3.zero;
                if (slide != null && slide.panel != null)
                {
                    Vector2 away = slide.panel.anchoredPosition - slide.positionHovered;
                    shift = slide.panel.parent.TransformVector(new Vector3(-away.x, -away.y, 0f));
                }
                Vector2 bottomLeft = RectTransformUtility.WorldToScreenPoint(camera, corners[0] + shift);
                float x = bottomLeft.x, y = Screen.height - bottomLeft.y + WindowGap;
                Rect quick = Placed((Rect)windowFields[0].GetValue(mod), x, y);
                Rect advanced = Placed((Rect)windowFields[1].GetValue(mod), x, y);
                Rect physics = Placed((Rect)windowFields[2].GetValue(mod), x, advanced.yMax + WindowGap);
                windowFields[0].SetValue(mod, quick);
                windowFields[1].SetValue(mod, advanced);
                windowFields[2].SetValue(mod, physics);
            }
            catch (System.Exception e)
            {
                CompatibilityLog.Warn("timewarp-window", "Better Time Warp's window stays where the mod puts it ("
                                                         + CompatibilityLog.Reason(e) + ").");
            }
        }

        // A rectangle of the same size at the given place, moved back onto the screen.
        private static Rect Placed(Rect size, float x, float y)
        {
            x = Mathf.Clamp(x, 0f, Mathf.Max(0f, Screen.width - size.width));
            y = Mathf.Clamp(y, 0f, Mathf.Max(0f, Screen.height - size.height));
            return new Rect(x, y, size.width, size.height);
        }

        // The mod's button content, read through the compiled getter, or by reflection where the runtime refuses it.
        private GUIContent Content()
        {
            if (readContent == null || mod == null) return null;
            try
            {
                return readContent(mod);
            }
            catch (System.MemberAccessException)
            {
                readContent = ReflectContent;
                return readContent(mod);
            }
        }

        // KSP destroys the scene's UI before its addons, so the area is gone
        // already: the mod's own button counts again whatever is left of it.
        private void OnDestroy()
        {
            TimeWarpButton.Replaced = false;
            TimeWarpButton.Coming = false;
            foreach (Sprite sprite in made)
                if (sprite != null) Destroy(sprite);
            made.Clear();
            icons.Clear();
        }

        // As a click on the mod's own button: its window opens or closes.
        private void Press()
        {
            if (open != null && mod != null) open.SetValue(mod, !(bool)open.GetValue(mod, null), null);
        }
    }
}
