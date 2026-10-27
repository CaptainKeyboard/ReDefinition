using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using KSP.UI;
using KSP.UI.TooltipTypes;
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

    // The copy of the MET button at the end of the warp row (TimeWarpButton).
    public abstract class TimeWarpBar : MonoBehaviour
    {
        // The arrow's size on the button, in KSP's UI units: about the MET
        // button's text height (16 points).
        private const float ArrowSize = 12f;
        // How far a button picture's corners are rounded beyond its see-through
        // margins, in its pixels (ZTheme's METButton.png: three).
        private const float Rounding = 4f;
        private static readonly FieldInfo ContentField = TypeLookup.Find(TimeWarpButton.ModType) != null
            ? TypeLookup.Find(TimeWarpButton.ModType).GetField("buttonContent", BindingFlags.Instance | BindingFlags.NonPublic)
            : null;
        // The arrow the mod shows, read every frame through a compiled getter; by the
        // FieldInfo where the runtime refuses the compiled read of a private field.
        private static System.Func<object, GUIContent> readContent;
        private static readonly System.Func<object, GUIContent> ReflectContent = target => ContentField.GetValue(target) as GUIContent;

        // The copy of the MET button, once built.
        private GameObject bar;
        // What Build adds to the sunrise extension to join it to the area, shown and
        // hidden with it.
        private readonly List<GameObject> joins = new List<GameObject>();
        private UIWarpToNextMorning sunrise;
        // The time panel and the mod were found: the copy follows the mod's own switches
        // from then on (Follow), checked once a second.
        private bool found;
        // Whether the copy is built, and whether building it failed; when Follow looks next.
        private bool built;
        private bool failed;
        private float followAt;
        // The copy's transform, and KSP's slide that folds the time panel away.
        private RectTransform barRect;
        private UIHoverSlidePanel slide;
        // The button's place in the panel while the panel is unfolded.
        private Vector3 unfolded;
        // The copy's arrow, the MET button's label, the mod's component, and the mod's arrow shown now.
        private Image icon;
        private TextMeshProUGUI label;
        private MonoBehaviour mod;
        private Texture shown;
        // The sprites made here, destroyed with the bar; the mod's icons by texture.
        private readonly List<Sprite> made = new List<Sprite>();
        private readonly Dictionary<Texture2D, Sprite> icons = new Dictionary<Texture2D, Sprite>();

        // Waits for the MET button's label, the sunrise extension and Better Time Warp, then follows the mod's switches.
        private IEnumerator Start()
        {
            System.Type type = TypeLookup.Find(TimeWarpButton.ModType);
            if (type == null || !TimeWarpButton.Routed || !FlightUiSwitch.On(s => s.TimeWarpPanelButton)) yield break;
            TimeWarpButton.Coming = TimeWarpButton.ModDrawsButton();
            float until = Time.unscaledTime + 15f;
            while (Time.unscaledTime < until)
            {
                label = TimeWarpButton.Label();
                sunrise = Find<UIWarpToNextMorning>();
                mod = FindObjectOfType(type) as MonoBehaviour;
                if (label != null && sunrise != null && mod != null) break;
                yield return new WaitForSecondsRealtime(0.5f);
            }
            TimeWarpButton.Coming = false;
            if (label == null || sunrise == null || mod == null) yield break;
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

        // Makes the copy where the mod draws its button (TimeWarpButton.ModDrawsButton) the
        // first time it does. It hides the copy, with what joins the sunrise extension to it,
        // where the player has switched the mod or its button off in its settings. So the
        // time panel shows no button that would open nothing the mod shows.
        private void Follow()
        {
            followAt = Time.unscaledTime + 1f;
            bool draws = TimeWarpButton.ModDrawsButton();
            if (!built)
            {
                if (draws && !failed && sunrise != null && label != null) TryBuild();
                return;
            }
            // Gone with the scene's UI.
            if (bar == null || bar.activeSelf == draws) return;
            bar.SetActive(draws);
            foreach (GameObject join in joins)
                if (join != null) join.SetActive(draws);
            TimeWarpButton.Replaced = draws;
            Debug.Log(Log.Tag + " Better Time Warp's button in the time panel " + (draws ? "shown again" : "hidden")
                      + ", as the mod's own settings say.");
        }

        // Builds the copy; where that fails, nothing of it stays and the mod's own button counts again.
        private void TryBuild()
        {
            try
            {
                Build(sunrise);
                built = true;
            }
            catch (System.Exception e)
            {
                failed = true;
                CompatibilityLog.Warn("timewarp-bar", "Better Time Warp's button stays where the mod puts it ("
                                                      + CompatibilityLog.Reason(e) + ").");
                if (bar != null) Destroy(bar);
                bar = null;
                foreach (GameObject join in joins)
                    if (join != null) Destroy(join);
                joins.Clear();
                TimeWarpButton.Replaced = false;
            }
        }

        // The first loaded component of that type in a scene; null for none.
        private static T Find<T>() where T : Component
        {
            foreach (T found in Resources.FindObjectsOfTypeAll<T>())
                if (found != null && found.gameObject.scene.IsValid()) return found;
            return null;
        }

        // The copy of the MET button at the end of the warp row, joined to the sunrise extension.
        // It keeps one place on the screen: at the end of the warp row while the panel is
        // unfolded, at the end of the MET row while it is folded. So the sunrise
        // extension, which shows only while the panel is unfolded, slides out from under it.
        // The extension's top right corner is squared to join it (Square).
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
            // The area keeps this height, folded too, less two screen pixels, and a
            // pixel as soft as the picture's lower edge (Edge) under it. The MET row
            // it then stands beside ends a row of the picture earlier (ZTheme's
            // FlightTimeQuadrant.png: the warp row 34 of its 68 rows, the MET row
            // 33), its last drawn row soft: on the player's screenshot, at ten
            // times, the full area went on two pixels below the MET row's last dark
            // one.
            Image backgroundFace = background.GetComponent<Image>();
            Rect backgroundFilled = Opaque(backgroundFace != null ? backgroundFace.sprite : null);
            Canvas canvas = row.GetComponentInParent<Canvas>();
            Canvas root = canvas != null ? canvas.rootCanvas : null;
            float pixel = root != null && row.lossyScale.y > 0f
                ? root.transform.lossyScale.y / (root.scaleFactor * row.lossyScale.y) : 1f;
            size.y -= 2f * pixel;

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
            float soft = Edge(backgroundFace != null ? backgroundFace.sprite : null, 0.97f);
            GameObject edge = new GameObject("Edge", typeof(RectTransform), typeof(Image));
            edge.layer = bar.layer;
            RectTransform edgeRect = (RectTransform)edge.transform;
            edgeRect.SetParent(barRect, false);
            edgeRect.anchorMin = new Vector2(0f, 0f);
            edgeRect.anchorMax = new Vector2(1f, 0f);
            edgeRect.pivot = new Vector2(0.5f, 1f);
            edgeRect.anchoredPosition = Vector2.zero;
            edgeRect.sizeDelta = new Vector2(0f, pixel);
            Image edgeImage = edge.GetComponent<Image>();
            Color edgeColour = bar.GetComponent<Image>().color;
            edgeColour.a *= soft;
            edgeImage.color = edgeColour;
            edgeImage.raycastTarget = false;

            // The button: the MET button copied, its look and its states, its
            // text and click taken away and its tooltip its own; square, as the
            // MET button's face is high, its rounded corners kept (each state's
            // sprite sliced at them), and that face centred in the area. It does
            // not take the keyboard's selection, as the MET button does: the
            // staging key would press it again (UiHygiene).
            GameObject copy = Instantiate(met.gameObject, barRect, false);
            copy.name = "ReDefinitionTimeWarpButton";
            for (int i = copy.transform.childCount - 1; i >= 0; i--) DestroyImmediate(copy.transform.GetChild(i).gameObject);
            foreach (TooltipController_Text tooltip in copy.GetComponents<TooltipController_Text>()) tooltip.textString = "Better Time Warp";
            Button button = copy.GetComponent<Button>();
            button.onClick = new Button.ButtonClickedEvent();
            button.onClick.AddListener(Press);
            UiHygiene.MouseOnly(button);
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
            // The button stands still in the area, at the height the MET button has
            // beside it once the panel is folded, which is as high and draws the same
            // picture: where the MET button's middle lies in the row, less how far
            // the row then stands from where it stands unfolded.
            if (slide != null)
            {
                RectTransform metRect = (RectTransform)met;
                float metMiddle = row.InverseTransformPoint(metRect.TransformPoint(metRect.rect.center)).y;
                float foldedAway = slide.positionNormal.y - slide.positionHovered.y;
                faceRect.localPosition = new Vector3(faceRect.localPosition.x, metMiddle - (top - foldedAway), 0f);
            }
            Hold();
            TimeWarpButton.Replaced = true;
            Debug.Log(Log.Tag + " Better Time Warp's button, the MET button copied, at the end of the warp row: "
                      + size.y.ToString("0.0") + " high, the button's middle " + (-faceRect.localPosition.y).ToString("0.0")
                      + " under its top, the sunrise bar's gap to the row line " + gap.ToString("0.0") + "; from x "
                      + left.ToString("0.0") + " to " + right.ToString("0.0") + ", y " + middle.ToString("0.0") + " to " + top.ToString("0.0")
                      + " unfolded (the background's right edge " + backgroundRight.ToString("0.0") + ", the sunrise bar's picture over "
                      + barFilled.ToString("0.00") + "); the button " + faceRect.sizeDelta.ToString("0.0") + " with its drawn square " + side.ToString("0.0")
                      + " wide, the MET picture over " + drawn.ToString("0.00") + "; the area in " + areaColour
                      + "; the sunrise extension's top right corner squared by " + corner.ToString("0.0")
                      + "; a soft pixel (" + pixel.ToString("0.00") + " units) at " + soft.ToString("0.00") + " opacity.");
        }

        // A sprite with a border for slicing, in pixels as the sprite's own rect
        // counts them (left, bottom, right, top); null stays null.
        private Sprite Sliced(Sprite sprite, Vector4 border)
        {
            if (sprite == null || sprite.texture == null) return sprite;
            float scale = sprite.rect.width > 0f ? sprite.textureRect.width / sprite.rect.width : 1f;
            Sprite sliced = Sprite.Create(sprite.texture, sprite.textureRect, new Vector2(0.5f, 0.5f), sprite.pixelsPerUnit, 0,
                SpriteMeshType.FullRect, border * scale);
            made.Add(sliced);
            return sliced;
        }

        // How opaque a sprite's soft lower edge is. It is the opacity of the lowest row that
        // is neither see-through nor full, in the column at `x` (a fraction of it), under the
        // part that is not see-through. It is half where there is none or it cannot be
        // read.
        private static float Edge(Sprite sprite, float x)
        {
            int width, height;
            Color32[] pixels = TexturePixels.OfSprite(sprite, out width, out height);
            if (pixels == null) return 0.5f;
            int column = Mathf.Clamp(Mathf.FloorToInt(x * width), 0, width - 1);
            for (int y = 0; y < height; y++)
            {
                byte alpha = pixels[y * width + column].a;
                if (alpha == 0) continue;
                return alpha < 250 ? alpha / 255f : 0.5f;
            }
            return 0.5f;
        }

        // The colour of a sprite at a point given in fractions of it.
        private static bool Colour(Sprite sprite, Vector2 at, out Color colour)
        {
            colour = Color.white;
            int width, height;
            Color32[] pixels = TexturePixels.OfSprite(sprite, out width, out height);
            if (pixels == null) return false;
            int x = Mathf.Clamp(Mathf.FloorToInt(at.x * width), 0, width - 1);
            int y = Mathf.Clamp(Mathf.FloorToInt(at.y * height), 0, height - 1);
            colour = pixels[y * width + x];
            colour.a = 1f;
            return true;
        }

        // Fills the sunrise extension's rounded top right corner square in the
        // bar's colour, so that it joins the bar above it. The fill is a patch in the
        // extension, under its button, as large as the corner's rounding, which is
        // how far the sprite's top row stops short of its right edge. It returns that
        // size in the extension's units, 0 where there is nothing to square.
        private float Square(RectTransform container, Image plate, Color colour)
        {
            int width, height;
            Color32[] pixels = TexturePixels.OfSprite(plate != null ? plate.sprite : null, out width, out height);
            if (pixels == null) return 0f;
            int last = -1;
            for (int x = width - 1; x >= 0 && last < 0; x--)
                if (pixels[(height - 1) * width + x].a >= 128) last = x;
            int rounding = width - 1 - last;
            if (last < 0 || rounding <= 0) return 0f;
            float size = rounding * container.rect.width / width;
            GameObject patch = new GameObject("ReDefinitionSquareCorner", typeof(RectTransform), typeof(Image));
            joins.Add(patch);
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
            Color32[] pixels = TexturePixels.OfSprite(sprite, out width, out height);
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
            Vector3 wanted = unfolded - (Vector3)away, now = barRect.localPosition;
            // A write marks the layout dirty, so only a change is written.
            if (now.x != wanted.x || now.y != wanted.y || now.z != wanted.z) barRect.localPosition = wanted;
        }

        // A strip in the extension's bar colour from the top of its bar picture up
        // by `gap`, over the picture's drawn width.
        private void Strip(RectTransform container, Rect filled, float gap, Color colour)
        {
            GameObject strip = new GameObject("ReDefinitionSunriseGap", typeof(RectTransform), typeof(Image));
            joins.Add(strip);
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

        // Every frame: the mod's switches followed once a second, the copy kept in its place, and its arrow the mod's.
        private void LateUpdate()
        {
            if (!found) return;
            if (Time.unscaledTime >= followAt) Follow();
            if (bar == null || !bar.activeSelf || label == null) return;
            Hold();
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
            PropertyInfo open = mod != null ? mod.GetType().GetProperty("UIOpen", BindingFlags.Instance | BindingFlags.Public) : null;
            if (open != null) open.SetValue(mod, !(bool)open.GetValue(mod, null), null);
        }
    }
}
