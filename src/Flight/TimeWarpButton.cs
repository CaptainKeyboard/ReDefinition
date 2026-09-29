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
    // Better Time Warp Continued's drop-down button as part of KSP's time panel.
    // The mod draws it with IMGUI at a place worked out from KSP's stock panel
    // (BetterTimeWarp.OnGUI: `GUI.Toggle(new Rect(scale * y, x, ...), windowOpen,
    // buttonContent, skin.button)`; its source read), which a UI theme's panel does
    // not keep, and in every scene its OnGUI runs in, the loading between scenes
    // too. Here a copy of the panel's MET button (its sprites, states and theme;
    // the panel read with UnityPy from sharedassets7), square, stands at the end of
    // the warp row: as high as the row, as wide as the warp-to-sunrise button below
    // it (UIWarpToNextMorning.button) and right above it, showing the mod's arrow
    // and opening the mod's window (BetterTimeWarp.UIOpen). It keeps one place on
    // the screen: at the end of the warp row while the panel is unfolded, at the
    // end of the MET row while it is folded, so that the sunrise extension, which
    // shows only while the panel is unfolded, slides out from under it; the
    // extension's top right corner is squared to join it. The mod's own IMGUI
    // button is not drawn while the copy stands (a transpiler routes its
    // GUI.Toggle through Toggle), nor where there is no time panel; elsewhere it
    // stays, right of the WARP label (Place).
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
            if (Replaced || HighLogic.LoadedScene == GameScenes.LOADING || HighLogic.LoadedScene == GameScenes.LOADINGBUFFER
                || Label() == null)
                return value;
            return GUI.Toggle(position, value, content, style);
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

    // The copy of the MET button at the end of the warp row (TimeWarpButton).
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
        // The button's place in the panel while the panel is unfolded.
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
            RectTransform sunriseButton = sunrise.button != null ? sunrise.button.transform as RectTransform : null;
            RectTransform container = sunrise.transform as RectTransform;
            RectTransform background = row != null ? row.Find("Background") as RectTransform : null;
            Transform met = row != null ? row.Find("METButton") : null;
            if (row == null || sunriseButton == null || container == null || background == null || met == null
                || met.GetComponent<Button>() == null || met.GetComponent<Image>() == null)
                throw new System.InvalidOperationException("no sunrise button, background or MET button in the time panel");

            // As wide as the part of the sunrise button its picture fills, which a
            // theme may draw narrower than the button (ZTheme: about 30 of 43 units,
            // right of the middle), and centred above it; as high as the warp row,
            // the upper half of the panel's background.
            Image sunriseFace = sunriseButton.GetComponent<Image>();
            Rect filled = Opaque(sunriseFace != null ? sunriseFace.sprite : null);
            Rect sunriseRect = sunriseButton.rect;
            Vector3 sunriseAt = row.InverseTransformPoint(sunriseButton.TransformPoint(new Vector3(
                sunriseRect.xMin + sunriseRect.width * filled.center.x, 0f, 0f)));
            Vector3[] corners = new Vector3[4];
            background.GetWorldCorners(corners);
            float bottom = row.InverseTransformPoint(corners[0]).y, top = row.InverseTransformPoint(corners[1]).y;
            float middle = (bottom + top) / 2f;
            Vector2 size = new Vector2(sunriseRect.width * filled.width, top - middle);

            // The MET button copied: its look and its states; its text, tooltip and
            // click taken away.
            bar = Instantiate(met.gameObject, row, false);
            bar.name = "ReDefinitionTimeWarpButton";
            for (int i = bar.transform.childCount - 1; i >= 0; i--) DestroyImmediate(bar.transform.GetChild(i).gameObject);
            foreach (TooltipController_Text tooltip in bar.GetComponents<TooltipController_Text>()) DestroyImmediate(tooltip);
            Button button = bar.GetComponent<Button>();
            button.onClick = new Button.ButtonClickedEvent();
            button.onClick.AddListener(Press);

            // Square: each state's sprite cut to its middle column, which holds the
            // face's top and bottom edges but not its rounded corners, stretched
            // over the button.
            Image face = bar.GetComponent<Image>();
            face.sprite = Middle(face.sprite);
            face.type = Image.Type.Simple;
            face.preserveAspect = false;
            SpriteState states = button.spriteState;
            states.highlightedSprite = Middle(states.highlightedSprite);
            states.pressedSprite = Middle(states.pressedSprite);
            states.selectedSprite = Middle(states.selectedSprite);
            states.disabledSprite = Middle(states.disabledSprite);
            button.spriteState = states;

            barRect = (RectTransform)bar.transform;
            barRect.anchorMin = barRect.anchorMax = barRect.pivot = new Vector2(0.5f, 0.5f);
            barRect.sizeDelta = size;
            // Over the sunrise extension's mask, so that the extension slides out
            // from under it.
            Transform mask = sunrise.transform.parent;
            barRect.SetSiblingIndex(mask != null && mask.parent == row ? mask.GetSiblingIndex() + 1 : 0);

            GameObject arrow = new GameObject("Arrow", typeof(RectTransform), typeof(Image));
            arrow.layer = bar.layer;
            RectTransform arrowRect = (RectTransform)arrow.transform;
            arrowRect.SetParent(barRect, false);
            arrowRect.anchorMin = arrowRect.anchorMax = arrowRect.pivot = new Vector2(0.5f, 0.5f);
            arrowRect.anchoredPosition = Vector2.zero;
            arrowRect.sizeDelta = new Vector2(ArrowSize, ArrowSize);
            icon = arrow.GetComponent<Image>();
            icon.preserveAspect = true;
            icon.raycastTarget = false;
            icon.enabled = false;

            // The sunrise extension's top right corner squared, in its bar's colour.
            Image sunrisePlate = sunrise.GetComponent<Image>();
            Color colour;
            if (!Colour(sunrisePlate != null ? sunrisePlate.sprite : null, new Vector2(0.25f, 0.5f), out colour))
                colour = new Color(0.2f, 0.2f, 0.22f, 1f);
            float corner = Square(container, sunrisePlate, (sunrisePlate != null ? sunrisePlate.color : Color.white) * colour);

            slide = row.GetComponent<UIHoverSlidePanel>();
            unfolded = new Vector3(sunriseAt.x, (top + middle) / 2f, 0f);
            Hold();
            TimeWarpButton.Replaced = true;
            Debug.Log(Log.Tag + " Better Time Warp's button, the MET button copied, at the end of the warp row: at "
                      + unfolded.ToString("0") + " unfolded, " + size.ToString("0.0") + " large; the sunrise button's picture over "
                      + filled.ToString("0.00") + " of it, the warp row from " + middle.ToString("0.0") + " to " + top.ToString("0.0")
                      + "; the sunrise extension's top right corner squared by " + corner.ToString("0.0") + ".");
        }

        // A sprite's middle column, two pixels wide, over its full height; null
        // stays null.
        private static Sprite Middle(Sprite sprite)
        {
            if (sprite == null || sprite.texture == null) return sprite;
            Rect area = sprite.textureRect;
            float x = Mathf.Floor(area.x + area.width / 2f) - 1f;
            return Sprite.Create(sprite.texture, new Rect(x, area.y, 2f, area.height), new Vector2(0.5f, 0.5f), sprite.pixelsPerUnit);
        }

        // A sprite's pixels, row by row from the bottom, or null where it cannot be
        // read. The texture is copied through a render texture, since a theme's
        // textures need not be readable; where the graphics API counts a render
        // texture's rows from the top (Direct3D, SystemInfo.graphicsUVStartsAtTop),
        // the copy stands upside down, and the sprite's rows are read from there
        // and turned.
        private static Color32[] Pixels(Sprite sprite, out int width, out int height)
        {
            width = height = 0;
            if (sprite == null || sprite.texture == null) return null;
            Texture2D texture = sprite.texture;
            Rect area = sprite.textureRect;
            width = Mathf.RoundToInt(area.width);
            height = Mathf.RoundToInt(area.height);
            if (width <= 0 || height <= 0) return null;
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
                if (!flipped) return pixels;
                Color32[] turned = new Color32[pixels.Length];
                for (int y = 0; y < height; y++)
                    System.Array.Copy(pixels, (height - 1 - y) * width, turned, y * width, width);
                return turned;
            }
            finally
            {
                RenderTexture.active = before;
                RenderTexture.ReleaseTemporary(copy);
                Destroy(read);
            }
        }

        // The colour of a sprite at a point given in fractions of it.
        private static bool Colour(Sprite sprite, Vector2 at, out Color colour)
        {
            colour = Color.white;
            int width, height;
            Color32[] pixels = Pixels(sprite, out width, out height);
            if (pixels == null) return false;
            int x = Mathf.Clamp(Mathf.FloorToInt(at.x * width), 0, width - 1);
            int y = Mathf.Clamp(Mathf.FloorToInt(at.y * height), 0, height - 1);
            colour = pixels[y * width + x];
            colour.a = 1f;
            return true;
        }

        // The sunrise extension's rounded top right corner filled square in the
        // bar's colour, so that it joins the bar above it: a patch in the
        // extension, under its button, as large as the corner's rounding, which is
        // how far the sprite's top row stops short of its right edge. Returns that
        // size in the extension's units, 0 where there is nothing to square.
        private static float Square(RectTransform container, Image plate, Color colour)
        {
            int width, height;
            Color32[] pixels = Pixels(plate != null ? plate.sprite : null, out width, out height);
            if (pixels == null) return 0f;
            int last = -1;
            for (int x = width - 1; x >= 0 && last < 0; x--)
                if (pixels[(height - 1) * width + x].a >= 128) last = x;
            int rounding = width - 1 - last;
            if (last < 0 || rounding <= 0) return 0f;
            float size = rounding * container.rect.width / width;
            GameObject patch = new GameObject("ReDefinitionSquareCorner", typeof(RectTransform), typeof(Image));
            patch.layer = container.gameObject.layer;
            RectTransform patchRect = (RectTransform)patch.transform;
            patchRect.SetParent(container, false);
            patchRect.SetSiblingIndex(0);
            patchRect.anchorMin = patchRect.anchorMax = patchRect.pivot = new Vector2(1f, 1f);
            patchRect.anchoredPosition = Vector2.zero;
            patchRect.sizeDelta = new Vector2(size, size);
            Image image = patch.GetComponent<Image>();
            image.color = colour;
            image.raycastTarget = false;
            return size;
        }

        // The part of a sprite that is not see-through, as fractions of it: all of
        // it where it cannot be read.
        private static Rect Opaque(Sprite sprite)
        {
            Rect all = new Rect(0f, 0f, 1f, 1f);
            int width, height;
            Color32[] pixels = Pixels(sprite, out width, out height);
            if (pixels == null) return all;
            int xMin = width, xMax = -1, yMin = height, yMax = -1;
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                if (pixels[y * width + x].a < 128) continue;
                xMin = Mathf.Min(xMin, x);
                xMax = Mathf.Max(xMax, x);
                yMin = Mathf.Min(yMin, y);
                yMax = Mathf.Max(yMax, y);
            }
            if (xMax < 0) return all;
            return Rect.MinMaxRect(xMin / (float)width, yMin / (float)height, (xMax + 1) / (float)width, (yMax + 1) / (float)height);
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
