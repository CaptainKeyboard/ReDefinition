using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using KSP.UI;
using KSP.UI.Screens.Flight;
using KSP.UI.TooltipTypes;
using ReDefinition.Core;
using ReDefinition.Scaling;
using ReDefinition.Settings;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ReDefinition.Flight
{
    // Better Time Warp Continued's drop-down button as part of KSP's time panel, at
    // the end of the warp row. The mod draws it with IMGUI at a place worked out from
    // KSP's stock panel (BetterTimeWarp.OnGUI: `GUI.Toggle(new Rect(scale * y, x,
    // ...), windowOpen, buttonContent, skin.button)`; its source read), which a UI
    // theme's panel does not keep. Here the warp-to-sunrise extension of the time
    // panel's lower row (UIWarpToNextMorning's WarpToSunriseContainer, its bar and its
    // button; the panel read with UnityPy from sharedassets7) is copied, square, in
    // the extension's size and theme, showing the mod's arrow and opening the mod's
    // window (BetterTimeWarp.UIOpen). It keeps one place on the screen: at the end
    // of the warp row (Label_Warp) while the panel is unfolded, at the end of the
    // MET row while it is folded, so that the sunrise extension, which shows only
    // while the panel is unfolded, slides out from under it. The mod's own IMGUI button is not drawn
    // while the copy stands (a transpiler routes its GUI.Toggle through Toggle);
    // where the panel is not found, it stays, right of the WARP label (Place).
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class TimeWarpButton : MonoBehaviour
    {
        internal const string ModType = "BetterTimeWarp.BetterTimeWarp";
        // Between the label and the button, in KSP's UI units.
        private const float Gap = 4f;

        // The copy in the time panel stands: the mod's own button is not drawn.
        internal static bool Replaced;
        private static TextMeshProUGUI label;
        private static float lookedAt = -10f;
        private static int routed;

        private void Awake()
        {
            System.Type mod = UiScaling.TypeOf(ModType);
            if (mod == null) return;
            HarmonyHooks.Safely("timewarp-button", "Better Time Warp's button stays where the mod puts it", () =>
            {
                MethodInfo onGui = mod.GetMethod("OnGUI", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                HarmonyHooks.Install("ReDefinition.TimeWarpButton", typeof(TimeWarpButton),
                    new List<HarmonyHook> { new HarmonyHook(onGui, null, null, null, -1, -1, nameof(Route)) });
                Debug.Log(Log.Tag + " Better Time Warp's button: " + routed + " of 2 calls routed.");
            });
        }

        // The mod's GUI.Toggle through Toggle, and the Rect built right before it
        // through Place.
        private static IEnumerable<CodeInstruction> Route(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> code = new List<CodeInstruction>(instructions);
            ConstructorInfo rect = typeof(Rect).GetConstructor(new[] { typeof(float), typeof(float), typeof(float), typeof(float) });
            MethodInfo place = typeof(TimeWarpButton).GetMethod(nameof(Place), BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo toggle = typeof(TimeWarpButton).GetMethod(nameof(Toggle), BindingFlags.Static | BindingFlags.NonPublic);
            for (int i = 0; i < code.Count; i++)
            {
                MethodInfo called = code[i].operand as MethodInfo;
                if (code[i].opcode != OpCodes.Call || called == null || called.DeclaringType != typeof(GUI) || called.Name != "Toggle"
                    || called.GetParameters().Length != 4) continue;
                code[i] = new CodeInstruction(OpCodes.Call, toggle) { labels = code[i].labels, blocks = code[i].blocks };
                routed++;
                for (int j = i - 1; j >= 0; j--)
                {
                    if (code[j].opcode != OpCodes.Newobj || !Equals(code[j].operand, rect)) continue;
                    code[j] = new CodeInstruction(OpCodes.Call, place) { labels = code[j].labels, blocks = code[j].blocks };
                    routed++;
                    break;
                }
                break;
            }
            return code;
        }

        private static bool Toggle(Rect position, bool value, GUIContent content, GUIStyle style)
        {
            return Replaced ? value : GUI.Toggle(position, value, content, style);
        }

        // Right of the label's text, centred on it, in screen pixels from the top left
        // as IMGUI counts them; the mod's own place where it is not found.
        private static Rect Place(float x, float y, float width, float height)
        {
            TextMeshProUGUI text = Label();
            Camera camera = UIMasterController.Instance != null ? UIMasterController.Instance.uiCamera : null;
            if (text == null || camera == null || !text.gameObject.activeInHierarchy) return new Rect(x, y, width, height);
            Bounds bounds = text.textBounds;
            Vector2 end = RectTransformUtility.WorldToScreenPoint(camera,
                text.rectTransform.TransformPoint(new Vector3(bounds.max.x, bounds.center.y, 0f)));
            return new Rect(end.x + Gap * GameSettings.UI_SCALE, Screen.height - end.y - height / 2f, width, height);
        }

        // The warp row's label, looked for again a second after it was not found.
        internal static TextMeshProUGUI Label()
        {
            if (label != null) return label;
            if (Time.unscaledTime - lookedAt < 1f) return null;
            lookedAt = Time.unscaledTime;
            foreach (TextMeshProUGUI text in Resources.FindObjectsOfTypeAll<TextMeshProUGUI>())
                if (text != null && text.name == "Label_Warp" && text.gameObject.scene.IsValid()) label = text;
            return label;
        }
    }

    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class TimeWarpBarFlight : TimeWarpBar
    {
    }

    [KSPAddon(KSPAddon.Startup.SpaceCentre, false)]
    public class TimeWarpBarSpaceCentre : TimeWarpBar
    {
    }

    // The copy of the warp-to-sunrise extension in the warp row (TimeWarpButton).
    public abstract class TimeWarpBar : MonoBehaviour
    {
        // The arrow's size on the button, in KSP's UI units: about the MET
        // button's text height (16 points).
        private const float ArrowSize = 12f;
        private static readonly FieldInfo ContentField = UiScaling.TypeOf(TimeWarpButton.ModType) != null
            ? UiScaling.TypeOf(TimeWarpButton.ModType).GetField("buttonContent", BindingFlags.Instance | BindingFlags.NonPublic)
            : null;

        private GameObject bar;
        private RectTransform barRect;
        private UIHoverSlidePanel slide;
        // The bar's place in the panel while the panel is unfolded.
        private Vector3 unfolded;
        private Image icon;
        private TextMeshProUGUI label;
        private MonoBehaviour mod;
        private Texture shown;

        private IEnumerator Start()
        {
            System.Type type = UiScaling.TypeOf(TimeWarpButton.ModType);
            if (type == null) yield break;
            float until = Time.unscaledTime + 15f;
            UIWarpToNextMorning sunrise = null;
            while (Time.unscaledTime < until)
            {
                label = TimeWarpButton.Label();
                sunrise = Find<UIWarpToNextMorning>();
                mod = FindObjectOfType(type) as MonoBehaviour;
                if (label != null && sunrise != null && mod != null) break;
                yield return new WaitForSecondsRealtime(0.5f);
            }
            if (label == null || sunrise == null || mod == null) yield break;
            try
            {
                Build(sunrise);
            }
            catch (System.Exception e)
            {
                CompatibilityLog.Warn("timewarp-bar", "Better Time Warp's button stays where the mod puts it ("
                                                      + CompatibilityLog.Reason(e) + ").");
                if (bar != null) Destroy(bar);
                bar = null;
                TimeWarpButton.Replaced = false;
            }
        }

        private static T Find<T>() where T : Component
        {
            foreach (T found in Resources.FindObjectsOfTypeAll<T>())
                if (found != null && found.gameObject.scene.IsValid()) return found;
            return null;
        }

        private void Build(UIWarpToNextMorning sunrise)
        {
            RectTransform row = label.transform.parent as RectTransform;
            RectTransform source = sunrise.transform as RectTransform;
            if (row == null || source == null || sunrise.button == null) return;
            bar = Instantiate(sunrise.gameObject, row, false);
            bar.name = "ReDefinitionTimeWarpBar";
            DestroyImmediate(bar.GetComponent<UIWarpToNextMorning>());
            // Square: the bar's picture, rounded only on its right, cut to its left
            // half and stretched over the bar.
            Image plate = bar.GetComponent<Image>();
            if (plate != null && plate.sprite != null)
            {
                Sprite whole = plate.sprite;
                Rect area = whole.textureRect;
                plate.sprite = Sprite.Create(whole.texture, new Rect(area.x, area.y, area.width / 2f, area.height),
                    new Vector2(0.5f, 0.5f), whole.pixelsPerUnit);
                plate.type = Image.Type.Simple;
                plate.preserveAspect = false;
            }
            // The sunrise's tooltip goes with it.
            foreach (TooltipController_Text tooltip in bar.GetComponentsInChildren<TooltipController_Text>(true))
                DestroyImmediate(tooltip);
            Button button = bar.GetComponentInChildren<Button>(true);
            if (button == null) throw new System.InvalidOperationException("no button in the sunrise extension");
            button.onClick = new Button.ButtonClickedEvent();
            button.onClick.AddListener(Press);

            // The MET button's face and its hover and press states (its sprites
            // hold no picture, the sunrise button's hold the sun), with the mod's
            // arrow on it, as small as the MET button's text; over the part of the
            // button the sunrise button's picture fills, which a theme may draw
            // narrower than the button (ZTheme: about 30 of 43 units, right of the
            // middle).
            Transform met = row.Find("METButton");
            Button metButton = met != null ? met.GetComponent<Button>() : null;
            Image face = button.GetComponent<Image>();
            Image metFace = met != null ? met.GetComponent<Image>() : null;
            if (metButton == null || face == null || metFace == null)
                throw new System.InvalidOperationException("no MET button in the time panel");
            Rect filled = Opaque(face.sprite);
            RectTransform faceRect = (RectTransform)button.transform;
            Vector2 full = faceRect.sizeDelta;
            faceRect.sizeDelta = new Vector2(full.x * filled.width, full.y * filled.height);
            faceRect.anchoredPosition += new Vector2(full.x * (filled.center.x - 0.5f), full.y * (filled.center.y - 0.5f));
            face.sprite = metFace.sprite;
            face.color = metFace.color;
            face.type = metFace.type;
            button.transition = metButton.transition;
            button.spriteState = metButton.spriteState;
            button.colors = metButton.colors;
            GameObject arrow = new GameObject("Arrow", typeof(RectTransform), typeof(Image));
            arrow.layer = button.gameObject.layer;
            RectTransform arrowRect = (RectTransform)arrow.transform;
            arrowRect.SetParent(button.transform, false);
            arrowRect.anchorMin = arrowRect.anchorMax = arrowRect.pivot = new Vector2(0.5f, 0.5f);
            arrowRect.anchoredPosition = Vector2.zero;
            arrowRect.sizeDelta = new Vector2(ArrowSize, ArrowSize);
            icon = arrow.GetComponent<Image>();
            icon.preserveAspect = true;
            icon.raycastTarget = false;
            icon.enabled = false;
            // Over the sunrise extension, which slides out from under it, and behind
            // the panel's background, which covers its left end as the extension's.
            Transform mask = source.parent;
            bar.transform.SetSiblingIndex(mask != null && mask.parent == row ? mask.GetSiblingIndex() + 1 : 0);

            // Where the sunrise extension stands, raised by the distance from the
            // lower row to the warp row (from the MET button to the warp gauge).
            // KSP keeps the extension at one place in the panel while the panel
            // slides (UITimeWarpHoverController.LateUpdate: its mask moved against
            // the panel, the extension by as much back).
            barRect = (RectTransform)bar.transform;
            barRect.anchorMin = barRect.anchorMax = new Vector2(0.5f, 0.5f);
            slide = row.GetComponent<UIHoverSlidePanel>();
            Transform gauge = row.Find("TimeWarp");
            if (gauge == null) throw new System.InvalidOperationException("no warp gauge in the time panel");
            Vector3 at = row.InverseTransformPoint(source.position);
            unfolded = new Vector3(at.x, at.y + gauge.localPosition.y - met.localPosition.y, 0f);
            Hold();
            TimeWarpButton.Replaced = true;
            Debug.Log(Log.Tag + " Better Time Warp's button in the time panel's warp row at " + unfolded.ToString("0")
                      + ", the sunrise extension at " + at.ToString("0") + "; its face over " + filled.ToString("0.00")
                      + " of the sunrise button's.");
        }

        // The part of a sprite that is not see-through, as fractions of it: all of
        // it where it cannot be read. The texture is copied through a render
        // texture, since a theme's textures need not be readable; where the
        // graphics API counts a render texture's rows from the top (Direct3D,
        // SystemInfo.graphicsUVStartsAtTop), the copy stands upside down, and the
        // sprite's rows are read from there.
        private static Rect Opaque(Sprite sprite)
        {
            Rect all = new Rect(0f, 0f, 1f, 1f);
            if (sprite == null || sprite.texture == null) return all;
            Texture2D texture = sprite.texture;
            Rect area = sprite.textureRect;
            int width = Mathf.RoundToInt(area.width), height = Mathf.RoundToInt(area.height);
            if (width <= 0 || height <= 0) return all;
            RenderTexture copy = RenderTexture.GetTemporary(texture.width, texture.height, 0, RenderTextureFormat.ARGB32);
            RenderTexture before = RenderTexture.active;
            Texture2D read = new Texture2D(width, height, TextureFormat.ARGB32, false);
            try
            {
                Graphics.Blit(texture, copy);
                RenderTexture.active = copy;
                bool flipped = SystemInfo.graphicsUVStartsAtTop;
                float from = flipped ? texture.height - area.y - height : area.y;
                read.ReadPixels(new Rect(area.x, from, width, height), 0, 0);
                read.Apply();
                Color32[] pixels = read.GetPixels32();
                int xMin = width, xMax = -1, yMin = height, yMax = -1;
                for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int row = flipped ? height - 1 - y : y;
                    if (pixels[row * width + x].a < 128) continue;
                    xMin = Mathf.Min(xMin, x);
                    xMax = Mathf.Max(xMax, x);
                    yMin = Mathf.Min(yMin, y);
                    yMax = Mathf.Max(yMax, y);
                }
                if (xMax < 0) return all;
                return Rect.MinMaxRect(xMin / (float)width, yMin / (float)height, (xMax + 1) / (float)width, (yMax + 1) / (float)height);
            }
            finally
            {
                RenderTexture.active = before;
                RenderTexture.ReleaseTemporary(copy);
                Destroy(read);
            }
        }

        // At one place on the screen: moved against the panel by as far as the
        // panel stands from where it stands unfolded (UIHoverSlidePanel moves the
        // panel's anchoredPosition between positionNormal and positionHovered).
        private void Hold()
        {
            if (barRect == null) return;
            Vector2 away = slide != null && slide.panel != null ? slide.panel.anchoredPosition - slide.positionHovered : Vector2.zero;
            barRect.localPosition = unfolded - (Vector3)away;
        }

        private void LateUpdate()
        {
            if (bar == null || label == null) return;
            Hold();
            GUIContent content = ContentField != null && mod != null ? ContentField.GetValue(mod) as GUIContent : null;
            Texture2D texture = content != null ? content.image as Texture2D : null;
            if (icon != null && texture != null && texture != shown)
            {
                shown = texture;
                icon.sprite = Sprite.Create(texture, new Rect(0f, 0f, texture.width, texture.height), new Vector2(0.5f, 0.5f));
                icon.enabled = true;
            }
        }

        private void OnDestroy()
        {
            if (bar != null) TimeWarpButton.Replaced = false;
        }

        // As a click on the mod's own button: its window opens or closes.
        private void Press()
        {
            PropertyInfo open = mod != null ? mod.GetType().GetProperty("UIOpen", BindingFlags.Instance | BindingFlags.Public) : null;
            if (open != null) open.SetValue(mod, !(bool)open.GetValue(mod, null), null);
        }
    }
}
