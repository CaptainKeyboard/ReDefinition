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
        // How far a button picture's corners are rounded beyond its see-through
        // margins, in its pixels (ZTheme's METButton.png: three).
        private const float Rounding = 4f;
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

            // As wide as the sunrise extension shows beside the panel: from the
            // background's right edge to the right end of the extension's bar
            // picture (ZTheme's bottomBar_plain: see-through left of 60 of its 96
            // pixels, so 208 to 244); as high as the warp row, the upper half of the
            // background (0 to -34). The extension stands at one place in the panel,
            // UITimeWarpHoverController.fixedMaskStart: KSP moves its mask to that
            // place less the panel's offset and the extension within the mask by
            // the offset, every LateUpdate (decompiled); where it stands just now
            // depends on whether that has run yet.
            UITimeWarpHoverController hover = row.GetComponent<UITimeWarpHoverController>();
            if (hover == null) throw new System.InvalidOperationException("no hover controller on the time panel");
            Image sunrisePlate = sunrise.GetComponent<Image>();
            Rect barFilled = Opaque(sunrisePlate != null ? sunrisePlate.sprite : null);
            Rect containerRect = container.rect;
            Vector3[] corners = new Vector3[4];
            background.GetWorldCorners(corners);
            float bottom = row.InverseTransformPoint(corners[0]).y, top = row.InverseTransformPoint(corners[1]).y;
            float backgroundRight = row.InverseTransformPoint(corners[2]).x;
            float middle = (bottom + top) / 2f;
            float right = hover.fixedMaskStart.x + containerRect.xMin + containerRect.width * barFilled.xMax;
            float left = Mathf.Max(backgroundRight, hover.fixedMaskStart.x + containerRect.xMin + containerRect.width * barFilled.xMin);
            if (right - left < 1f) throw new System.InvalidOperationException("no room for the button beside the time panel");
            Vector2 size = new Vector2(right - left, top - middle);
            // Folded, the button stands beside the MET row, which the background's
            // picture draws a row lower than the warp row (ZTheme's
            // FlightTimeQuadrant.png: the warp row 34 of its 68 rows, the MET row
            // 33, the last see-through); the button's height follows the panel
            // from the one to the other, its top kept.
            Image backgroundFace = background.GetComponent<Image>();
            Rect backgroundFilled = Opaque(backgroundFace != null ? backgroundFace.sprite : null);
            unfoldedHeight = size.y;
            foldedHeight = middle - (bottom + (top - bottom) * backgroundFilled.yMin);

            // The area: a plain rectangle in the colour of the MET row's background
            // (ZTheme: #302E35), over the sunrise extension's mask, so that the
            // extension slides out from under it.
            bar = new GameObject("ReDefinitionTimeWarpArea", typeof(RectTransform), typeof(Image));
            bar.layer = row.gameObject.layer;
            barRect = (RectTransform)bar.transform;
            barRect.SetParent(row, false);
            barRect.anchorMin = barRect.anchorMax = new Vector2(0.5f, 0.5f);
            barRect.pivot = new Vector2(0.5f, 1f);
            barRect.sizeDelta = size;
            Transform mask = sunrise.transform.parent;
            barRect.SetSiblingIndex(mask != null && mask.parent == row ? mask.GetSiblingIndex() + 1 : 0);
            Color areaColour;
            if (!Colour(backgroundFace != null ? backgroundFace.sprite : null,
                    new Vector2(0.97f, backgroundFilled.yMin + (0.5f - backgroundFilled.yMin) / 2f), out areaColour))
                areaColour = new Color(0.19f, 0.18f, 0.21f, 1f);
            bar.GetComponent<Image>().color = (backgroundFace != null ? backgroundFace.color : Color.white) * areaColour;

            // The button: the MET button copied, its look and its states, its
            // text, tooltip and click taken away; square, as the MET button's face
            // is high, its rounded corners kept (each state's sprite sliced at
            // them), and that face centred in the area.
            GameObject copy = Instantiate(met.gameObject, barRect, false);
            copy.name = "ReDefinitionTimeWarpButton";
            for (int i = copy.transform.childCount - 1; i >= 0; i--) DestroyImmediate(copy.transform.GetChild(i).gameObject);
            foreach (TooltipController_Text tooltip in copy.GetComponents<TooltipController_Text>()) DestroyImmediate(tooltip);
            Button button = copy.GetComponent<Button>();
            button.onClick = new Button.ButtonClickedEvent();
            button.onClick.AddListener(Press);
            Image face = copy.GetComponent<Image>();
            RectTransform faceRect = (RectTransform)copy.transform;
            Vector2 metSize = ((RectTransform)met).rect.size;
            Rect drawn = Opaque(face.sprite);
            float marginLeft = metSize.x * drawn.xMin, marginRight = metSize.x * (1f - drawn.xMax);
            float marginBottom = metSize.y * drawn.yMin, marginTop = metSize.y * (1f - drawn.yMax);
            float side = metSize.y * drawn.height;
            Vector4 border = new Vector4(marginLeft, marginBottom, marginRight, marginTop) + Vector4.one * Rounding;
            face.sprite = Sliced(face.sprite, border);
            face.type = Image.Type.Sliced;
            face.preserveAspect = false;
            SpriteState states = button.spriteState;
            states.highlightedSprite = Sliced(states.highlightedSprite, border);
            states.pressedSprite = Sliced(states.pressedSprite, border);
            states.selectedSprite = Sliced(states.selectedSprite, border);
            states.disabledSprite = Sliced(states.disabledSprite, border);
            button.spriteState = states;
            faceRect.anchorMin = faceRect.anchorMax = faceRect.pivot = new Vector2(0.5f, 0.5f);
            faceRect.sizeDelta = new Vector2(side + marginLeft + marginRight, metSize.y);
            // The drawn square's middle on the area's middle.
            Vector2 drawnMiddle = new Vector2(marginLeft + side / 2f - faceRect.sizeDelta.x / 2f,
                marginBottom + side / 2f - metSize.y / 2f);
            faceRect.anchoredPosition = -drawnMiddle;

            GameObject arrow = new GameObject("Arrow", typeof(RectTransform), typeof(Image));
            arrow.layer = copy.layer;
            RectTransform arrowRect = (RectTransform)arrow.transform;
            arrowRect.SetParent(faceRect, false);
            arrowRect.anchorMin = arrowRect.anchorMax = arrowRect.pivot = new Vector2(0.5f, 0.5f);
            arrowRect.anchoredPosition = drawnMiddle;
            arrowRect.sizeDelta = new Vector2(ArrowSize, ArrowSize);
            icon = arrow.GetComponent<Image>();
            icon.preserveAspect = true;
            icon.raycastTarget = false;
            icon.enabled = false;

            // The sunrise extension's top right corner squared, in its bar's colour.
            Color colour;
            if (!Colour(sunrisePlate != null ? sunrisePlate.sprite : null, barFilled.center, out colour))
                colour = new Color(0.2f, 0.2f, 0.22f, 1f);
            Color barColour = (sunrisePlate != null ? sunrisePlate.color : Color.white) * colour;
            float corner = Square(container, sunrisePlate, barColour);
            // KSP stands the extension's bar a little below the row line (its top
            // at fixedMaskStart.y plus half its 33 units: -34.5 against -34), where
            // nothing is drawn beside the background: a strip in the bar's colour
            // closes it up to the line, over the bar's drawn width.
            float barTop = hover.fixedMaskStart.y + containerRect.yMin + containerRect.height * barFilled.yMax;
            float gap = middle - barTop;
            if (gap > 0.05f) Strip(container, barFilled, gap, barColour);

            slide = row.GetComponent<UIHoverSlidePanel>();
            unfolded = new Vector3((left + right) / 2f, top, 0f);
            travel = slide != null ? Mathf.Abs(slide.positionNormal.y - slide.positionHovered.y) : 0f;
            Hold();
            TimeWarpButton.Replaced = true;
            Debug.Log(Log.Tag + " Better Time Warp's button, the MET button copied, at the end of the warp row: "
                      + foldedHeight.ToString("0.0") + " high folded, the sunrise bar's gap to the row line " + gap.ToString("0.0") + "; from x "
                      + left.ToString("0.0") + " to " + right.ToString("0.0") + ", y " + middle.ToString("0.0") + " to " + top.ToString("0.0")
                      + " unfolded (the background's right edge " + backgroundRight.ToString("0.0") + ", the sunrise bar's picture over "
                      + barFilled.ToString("0.00") + "); the button " + faceRect.sizeDelta.ToString("0.0") + " with its drawn square " + side.ToString("0.0")
                      + " wide, the MET picture over " + drawn.ToString("0.00") + "; the area in " + areaColour
                      + "; the sunrise extension's top right corner squared by " + corner.ToString("0.0") + ".");
        }

        // A sprite with a border for slicing, in pixels as the sprite's own rect
        // counts them (left, bottom, right, top); null stays null.
        private static Sprite Sliced(Sprite sprite, Vector4 border)
        {
            if (sprite == null || sprite.texture == null) return sprite;
            float scale = sprite.rect.width > 0f ? sprite.textureRect.width / sprite.rect.width : 1f;
            return Sprite.Create(sprite.texture, sprite.textureRect, new Vector2(0.5f, 0.5f), sprite.pixelsPerUnit, 0,
                SpriteMeshType.FullRect, border * scale);
        }

        // A sprite's pixels, row by row from the bottom, or null where it cannot be
        // read. The texture is copied through a render texture, since a theme's
        // textures need not be readable. Where the graphics API counts a render
        // texture's rows from the top (Direct3D, SystemInfo.graphicsUVStartsAtTop),
        // the sprite's area lies mirrored in the copy, its rows still read from the
        // bottom: read from ZTheme's bottomBar_plain.png (the sprite is the whole
        // texture; its top row, which ends 5 pixels short of the right edge, came
        // last only unturned) and FlightButtonNextMorning.png (the sun's rows from
        // 0.04 to 1.00 of the sprite, as the file has them).
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
                return read.GetPixels32();
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
            float folded = travel > 0f ? Mathf.Clamp01(Mathf.Abs(away.y) / travel) : 0f;
            barRect.sizeDelta = new Vector2(barRect.sizeDelta.x, Mathf.Lerp(unfoldedHeight, foldedHeight, folded));
        }

        private float unfoldedHeight, foldedHeight, travel;

        // A strip in the extension's bar colour from the top of its bar picture up
        // by `gap`, over the picture's drawn width.
        private static void Strip(RectTransform container, Rect filled, float gap, Color colour)
        {
            GameObject strip = new GameObject("ReDefinitionSunriseGap", typeof(RectTransform), typeof(Image));
            strip.layer = container.gameObject.layer;
            RectTransform stripRect = (RectTransform)strip.transform;
            stripRect.SetParent(container, false);
            stripRect.SetSiblingIndex(0);
            stripRect.anchorMin = new Vector2(filled.xMin, filled.yMax);
            stripRect.anchorMax = new Vector2(filled.xMax, filled.yMax);
            stripRect.pivot = new Vector2(0.5f, 0f);
            stripRect.anchoredPosition = Vector2.zero;
            stripRect.sizeDelta = new Vector2(0f, gap);
            Image image = strip.GetComponent<Image>();
            image.color = colour;
            image.raycastTarget = false;
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
