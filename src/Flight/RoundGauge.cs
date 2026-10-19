using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using KSP.UI.Screens.Flight;
using ReDefinition.Core;
using ReDefinition.Engineer;
using ReDefinition.Flight.Panel;
using ReDefinition.Flight.Readouts;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ReDefinition.Flight
{
    // What the round gauge shows (docs/player/navball.md). Auto takes the one for
    // the situation the flight panel works out (FlightProfiles.Held).
    internal enum GaugeMode
    {
        Auto,
        Stage,
        Burn,
        Impact,
        Closing,
        Attack,
    }

    // KSP's vertical speed gauge made a digital gauge: the vertical speed stands
    // at the navball (NavballSpeeds). The gauge (GaugeVerticalSpeed under the top
    // frame's altimeter, 83 by 85: its picture with the dial's face, scale and
    // bezel, a needle, a knob, the label "VERT SPD" and the sink rate light;
    // sharedassets7) keeps its bezel and its light. Its picture is read as it
    // stands, the theme's where one replaces it, and its face painted over in the
    // face's own colour up to the bezel; needle, knob and label are switched off.
    // On the face: a lit arc of three quarters of a circle, open at the bottom,
    // filled from its left end as far as the value goes, with a dot at its tip,
    // over a dim track; the mode's name, the value, and "AUTO" while the gauge
    // follows the situation.
    //
    // As FAA AC 23.1311-1C has an electronic dial (9.4, 17.6, 22.7): the digits
    // and the arc give the same value, the arc on a scale fixed for the mode, so
    // that the margin to a limit shows at a glance; the limits as marks on the
    // scale, amber for caution and red for warning, and a limit that moves with
    // the flight (the moment to brake, a safe closing speed) as a tick on it; the
    // arc white while the value is fine, amber or red inside those bands. Where
    // the values span a wide range, the scale is logarithmic, as a vertical speed
    // indicator's is: fine near zero, compressed high up. The arc moves to a new
    // value within a fraction of a second.
    //
    // A click on the gauge goes to the next mode, a right-click to the one
    // before; the face lights up under the mouse. The mode is kept
    // (PluginData/gauge.cfg). The gauge is a Button, as the altimeter's mode
    // switch beside it is (AltitudeModeButton: a clear Image with a Button;
    // sharedassets7): the frame both stand in (SlideFrameParent) has an
    // XSelectable, which takes a pointer's press from any child that has no
    // handler for it, and Unity sends a click only to the object that took the
    // press (StandaloneInputModule: pointerPress == the click's handler;
    // decompiled). A Button takes the press itself. Its colour tint lights the
    // track, as it lights any button.
    //
    // The figures are worked out from plain numbers in GaugeReadings, which is
    // tested; here they are read from the game. The gauge watches itself in
    // flight (Watch): every ten seconds and at each change of figure it writes
    // what it shows into the log, and once per stage a warning where the stage's
    // arc leaves the digits.
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public partial class RoundGauge : MonoBehaviour
    {
        private const float Interval = 0.1f;
        // How far out the face reaches, as a share of the bezel's outer radius:
        // ZTheme's bezel starts just beyond, KSP's a little further out (read
        // from both pictures).
        internal const float Face = 0.84f;
        // The arc: three quarters of a circle, 3 wide, 1.5 inside the face's edge.
        private const float Sweep = 0.75f;
        private const float ArcWidth = 3f;
        private const float ArcInset = 1.5f;

        private static readonly Color Fine = new Color(0.95f, 0.95f, 0.95f);
        private static readonly Color Close = new Color(1f, 0.72f, 0.2f);
        private static readonly Color Past = new Color(1f, 0.32f, 0.28f);
        // The track at its brightest, pressed; the Button's tint shows a third of
        // it at rest and two thirds under the mouse.
        private static readonly Color Track = new Color(1f, 1f, 1f, 0.36f);
        private static readonly Color CautionBand = new Color(1f, 0.72f, 0.2f, 0.45f);
        private static readonly Color WarningBand = new Color(1f, 0.32f, 0.28f, 0.5f);

        private TextMeshProUGUI title;
        private TextMeshProUGUI value;
        private TextMeshProUGUI auto;
        private Image track;
        private Image caution;
        private Image warning;
        private Image arc;
        private RectTransform mark;
        private Image markImage;
        private RectTransform tip;
        private Image tipImage;
        private float radius;
        private Vector2 middle;
        private Color dim = Color.grey;
        private float next;
        private float shownFill, wantedFill;
        private Color wantedColour = Fine;

        private static GaugeMode mode = Load();

        private IEnumerator Start()
        {
            // KSP's vertical speed gauge stays where the navball does not show the
            // vertical speed: it would stand nowhere else.
            if (!FlightUiSwitch.On(s => s.RoundGauge && s.NavballSpeeds)) yield break;
            float until = Time.unscaledTime + 15f;
            VerticalSpeedGauge gauge = null;
            while (Time.unscaledTime < until && (gauge = FindObjectOfType<VerticalSpeedGauge>()) == null) yield return null;
            // A frame more: a theme swaps the gauge's picture as the scene's UI is
            // ready.
            yield return null;
            TextMeshProUGUI label = null;
            if (gauge != null)
                foreach (TextMeshProUGUI text in gauge.GetComponentsInChildren<TextMeshProUGUI>(true))
                    if (text.name == "Label_Vert") label = text;
            Image picture = gauge != null ? gauge.GetComponent<Image>() : null;
            // The picture's pixels, read back without waiting for the GPU, where they are
            // not at hand from a flight before (Blank).
            Color32[] pictureRead = null;
            int readWidth = 0, readHeight = 0;
            if (picture != null && !BlankKnown(picture.sprite))
                yield return Shared.TexturePixels.OfSpriteAsync(picture.sprite, (p, w, h) =>
                {
                    pictureRead = p;
                    readWidth = w;
                    readHeight = h;
                });
            Vector2 centre;
            float reach;
            if (label == null || picture == null || !Blank(picture, pictureRead, readWidth, readHeight, out centre, out reach))
            {
                Debug.Log(Log.Tag + " The round gauge keeps KSP's vertical speed: its label or its picture was not found.");
                yield break;
            }
            foreach (string part in new[] { "GaugeVerticalSpeedPointerParent", "GaugeVerticalSpeedKnob" })
            {
                Transform child = gauge.transform.Find(part);
                if (child != null) child.gameObject.SetActive(false);
            }
            // The dial's middle and the face's radius on the gauge: neither picture
            // has the dial in its own middle.
            Rect rect = picture.rectTransform.rect;
            middle = new Vector2(centre.x * rect.width, centre.y * rect.height);
            radius = reach * rect.width - ArcInset;

            track = Ring(picture.transform, "ReDefinitionGaugeTrack", middle);
            track.fillAmount = Sweep;
            track.color = Track;
            caution = Ring(picture.transform, "ReDefinitionGaugeCaution", middle);
            caution.color = CautionBand;
            warning = Ring(picture.transform, "ReDefinitionGaugeWarning", middle);
            warning.color = WarningBand;
            Band(caution, null);
            Band(warning, null);
            arc = Ring(picture.transform, "ReDefinitionGaugeArc", middle);
            arc.fillAmount = 0f;
            markImage = Tick(picture.transform);
            mark = markImage.rectTransform;
            tipImage = Dot(picture.transform);
            tip = tipImage.rectTransform;

            dim = label.color;
            title = Copy(label, "ReDefinitionGaugeTitle", middle + new Vector2(0f, 11f), 52f, label.fontSize * 1.15f, label.color);
            value = Copy(label, "ReDefinitionGaugeValue", middle + new Vector2(0f, -2f), 52f, label.fontSize * 2.1f, Fine);
            auto = Copy(label, "ReDefinitionGaugeAuto", middle + new Vector2(0f, -19f), 32f, label.fontSize * 0.8f, label.color);
            auto.text = "AUTO";
            label.gameObject.SetActive(false);

            // The button over the face.
            GameObject click = new GameObject("ReDefinitionGaugeButton", typeof(RectTransform), typeof(Image), typeof(Button),
                typeof(GaugeRightClick));
            click.layer = picture.gameObject.layer;
            click.transform.SetParent(picture.transform, false);
            RectTransform clickRect = (RectTransform)click.transform;
            clickRect.anchorMin = clickRect.anchorMax = clickRect.pivot = new Vector2(0.5f, 0.5f);
            clickRect.sizeDelta = new Vector2(2f * (radius + ArcInset), 2f * (radius + ArcInset));
            clickRect.anchoredPosition = middle;
            click.GetComponent<Image>().color = Color.clear;
            Button button = click.GetComponent<Button>();
            button.targetGraphic = track;
            button.transition = Selectable.Transition.ColorTint;
            ColorBlock tint = button.colors;
            tint.normalColor = tint.selectedColor = new Color(1f, 1f, 1f, 1f / 3f);
            tint.highlightedColor = new Color(1f, 1f, 1f, 2f / 3f);
            tint.pressedColor = Color.white;
            tint.colorMultiplier = 1f;
            tint.fadeDuration = 0.1f;
            button.colors = tint;
            ReDefinition.Window.UiHygiene.MouseOnly(button);
            button.onClick.AddListener(() => Step(1));
            // What the gauge shows, by name, as KSP's HUD names its parts.
            tooltip = ReDefinition.Window.UiHygiene.Tooltip(click, Name(mode == GaugeMode.Auto ? GaugeMode.Stage : mode));
            if (tooltip != null) tooltip.continuousUpdate = true;
            click.GetComponent<GaugeRightClick>().Pressed = () => Step(-1);
            Sprite read = picture.sprite;
            Debug.Log(Log.Tag + " The round gauge is digital, showing " + mode + ": the dial's middle at " + middle.ToString("0.0")
                      + " of the gauge's " + rect.size.ToString("0.0") + " (pivot " + picture.rectTransform.pivot.ToString("0.00")
                      + "), the arc " + radius.ToString("0.0") + " out; the picture " + (read != null ? read.rect.size.ToString("0") : "?")
                      + ", the label under " + label.transform.parent.name + ".");
        }

        // The painted picture, kept for the next flight scene: the gauge's own
        // picture is the same there.
        private static Sprite blankFrom;
        private static Sprite blankSprite;
        private static Vector2 blankCentre;
        private static float blankReach;

        // The gauge's picture with its face painted over, where the dial's middle
        // lies off the picture's, as a share of its size, and how far the face
        // reaches, as a share of its width; false where the picture cannot be
        // read. The dial is the picture's opaque part: its middle is that part's.
        private static bool BlankKnown(Sprite sprite)
        {
            return blankSprite != null && sprite != null && (sprite == blankFrom || sprite == blankSprite);
        }

        private static bool Blank(Image image, Color32[] pixels, int width, int height, out Vector2 centre, out float reach)
        {
            centre = Vector2.zero;
            reach = 0f;
            Sprite sprite = image.sprite;
            if (BlankKnown(sprite))
            {
                centre = blankCentre;
                reach = blankReach;
                image.sprite = blankSprite;
                return true;
            }
            if (pixels == null) return false;
            int left = width, right = -1, bottom = height, top = -1;
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                if (pixels[y * width + x].a < 128) continue;
                if (x < left) left = x;
                if (x > right) right = x;
                if (y < bottom) bottom = y;
                if (y > top) top = y;
            }
            if (right < left || top < bottom) return false;
            // The face's colour a little east of the middle, clear of KSP's knob
            // and of the scale's numbers.
            Color32 face = pixels[(height / 2) * width + Mathf.Clamp(Mathf.RoundToInt(width * 0.675f), 0, width - 1)];
            float cx = (left + right) / 2f, cy = (bottom + top) / 2f;
            float paint = Face * Mathf.Min(right - left + 1, top - bottom + 1) / 2f;
            centre = new Vector2((cx - (width - 1) / 2f) / width, (cy - (height - 1) / 2f) / height);
            reach = paint / width;
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                float dx = x - cx, dy = y - cy;
                if (dx * dx + dy * dy > paint * paint) continue;
                Color32 was = pixels[y * width + x];
                pixels[y * width + x] = new Color32(face.r, face.g, face.b, was.a);
            }
            Texture2D texture = new Texture2D(width, height, TextureFormat.ARGB32, false);
            texture.SetPixels32(pixels);
            texture.Apply();
            if (blankSprite != null)
            {
                Destroy(blankSprite.texture);
                Destroy(blankSprite);
            }
            blankFrom = sprite;
            blankSprite = Sprite.Create(texture, new Rect(0f, 0f, width, height), new Vector2(0.5f, 0.5f), sprite.pixelsPerUnit);
            blankCentre = centre;
            blankReach = reach;
            image.sprite = blankSprite;
            return true;
        }

        // A ring on the face, filled clockwise from its lower left (Image's radial
        // fill starts at the bottom; the ring is turned by an eighth).
        private Image Ring(Transform parent, string name, Vector2 middle)
        {
            GameObject ring = ReDefinition.Window.UiHygiene.Child(name, parent, typeof(RectTransform), typeof(Image));
            RectTransform rect = (RectTransform)ring.transform;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(2f * radius, 2f * radius);
            rect.anchoredPosition = middle;
            rect.localRotation = Quaternion.Euler(0f, 0f, -45f);
            Image image = ring.GetComponent<Image>();
            image.sprite = Annulus(1f - ArcWidth / radius);
            image.type = Image.Type.Filled;
            image.fillMethod = Image.FillMethod.Radial360;
            image.fillOrigin = (int)Image.Origin360.Bottom;
            image.fillClockwise = true;
            image.raycastTarget = false;
            return image;
        }

        // A band of the scale on the track, from one share of it to another; none
        // for null.
        private static void Band(Image band, double[] range)
        {
            if (range == null || !(range[1] > range[0]))
            {
                band.fillAmount = 0f;
                return;
            }
            float from = Mathf.Clamp01((float)range[0]), to = Mathf.Clamp01((float)range[1]);
            band.fillAmount = (to - from) * Sweep;
            band.rectTransform.localRotation = Quaternion.Euler(0f, 0f, -45f - 270f * from);
        }

        // A limit's tick across the arc.
        private Image Tick(Transform parent)
        {
            GameObject tick = ReDefinition.Window.UiHygiene.Child("ReDefinitionGaugeMark", parent, typeof(RectTransform), typeof(Image));
            RectTransform rect = (RectTransform)tick.transform;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(1.5f, ArcWidth + 3f);
            Image image = tick.GetComponent<Image>();
            image.color = Color.clear;
            image.raycastTarget = false;
            return image;
        }

        // Where a share of the scale lies on the arc's middle line, and at which
        // angle, clockwise from the top.
        private Vector2 On(float share, out float degrees)
        {
            degrees = -135f + 270f * share;
            float angle = degrees * Mathf.Deg2Rad;
            return middle + new Vector2(Mathf.Sin(angle), Mathf.Cos(angle)) * (radius - ArcWidth / 2f);
        }

        private Image Dot(Transform parent)
        {
            GameObject dot = ReDefinition.Window.UiHygiene.Child("ReDefinitionGaugeTip", parent, typeof(RectTransform), typeof(Image));
            RectTransform rect = (RectTransform)dot.transform;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(ArcWidth + 2f, ArcWidth + 2f);
            Image image = dot.GetComponent<Image>();
            image.sprite = Annulus(0f);
            image.raycastTarget = false;
            return image;
        }

        private static readonly Dictionary<float, Sprite> annuli = new Dictionary<float, Sprite>();

        // A white ring, its inner edge at `inner` of its radius (0 for a disc),
        // its edges smoothed over a texel; made once per `inner`.
        private static Sprite Annulus(float inner)
        {
            Sprite made;
            if (annuli.TryGetValue(inner, out made) && made != null) return made;
            made = MakeAnnulus(inner);
            annuli[inner] = made;
            return made;
        }

        private static Sprite MakeAnnulus(float inner)
        {
            const int size = 128;
            Texture2D texture = new Texture2D(size, size, TextureFormat.ARGB32, false);
            Color32[] pixels = new Color32[size * size];
            float c = (size - 1) / 2f, outer = size / 2f - 0.5f, hole = inner * outer;
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c));
                float alpha = Mathf.Clamp01(outer - d + 0.5f) * (inner > 0f ? Mathf.Clamp01(d - hole + 0.5f) : 1f);
                pixels[y * size + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(alpha * 255f));
            }
            texture.SetPixels32(pixels);
            texture.Apply();
            texture.wrapMode = TextureWrapMode.Clamp;
            return Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f));
        }

        // A copy of the gauge's label, `width` wide, centred on `at` from the
        // gauge's middle.
        private static TextMeshProUGUI Copy(TextMeshProUGUI label, string name, Vector2 at, float width, float size, Color colour)
        {
            GameObject copy = Instantiate(label.gameObject, label.transform.parent, false);
            copy.name = name;
            copy.SetActive(true);
            RectTransform rect = (RectTransform)copy.transform;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(width, size * 1.4f);
            rect.anchoredPosition = at;
            TextMeshProUGUI text = copy.GetComponent<TextMeshProUGUI>();
            text.fontSize = size;
            text.enableAutoSizing = true;
            text.fontSizeMax = size;
            text.fontSizeMin = size * 0.5f;
            text.color = colour;
            text.enableWordWrapping = false;
            text.alignment = TextAlignmentOptions.Center;
            text.raycastTarget = false;
            return text;
        }

        // ------------------------------------------------------------ modes

        private void Step(int by)
        {
            int count = Enum.GetValues(typeof(GaugeMode)).Length;
            mode = (GaugeMode)((((int)mode + by) % count + count) % count);
            Save();
            next = 0f;
            Debug.Log(Log.Tag + " The round gauge shows " + mode + ", picked by a click.");
        }

        private void Update()
        {
            if (value == null) return;
            // The arc and its tip move to the value each frame.
            shownFill = Mathf.MoveTowards(shownFill, wantedFill, Time.unscaledDeltaTime * 2f);
            arc.fillAmount = shownFill * Sweep;
            arc.color = wantedColour;
            float degrees;
            tip.anchoredPosition = On(shownFill, out degrees);
            tipImage.color = shownFill > 0.001f ? Color.white : Color.clear;

            if (Time.unscaledTime < next) return;
            next = Time.unscaledTime + Interval;
            Vessel vessel = FlightGlobals.ActiveVessel;
            ReadoutFrame frame = vessel != null ? new ReadoutFrame(vessel) : null;
            if (frame == null || !frame.Ready) return;
            GaugeMode shown = mode == GaugeMode.Auto ? ForSituation(frame) : mode;
            GaugeReading reading;
            try
            {
                reading = Read(shown, frame) ?? new GaugeReading();
            }
            catch (Exception)
            {
                reading = new GaugeReading();
            }
            Watch(shown, reading, frame.Vessel);
            Band(caution, reading.Caution);
            Band(warning, reading.Warning);
            if (double.IsNaN(reading.Mark))
                markImage.color = Color.clear;
            else
            {
                float at;
                mark.anchoredPosition = On(Mathf.Clamp01((float)reading.Mark), out at);
                mark.localRotation = Quaternion.Euler(0f, 0f, -at);
                markImage.color = Past;
            }
            title.text = Title(shown);
            if (tooltip != null) tooltip.textString = Name(shown);
            auto.gameObject.SetActive(mode == GaugeMode.Auto);
            bool known = reading.Text != null;
            value.text = known ? reading.Text : "-";
            value.color = known ? Fine : dim;
            wantedFill = known ? Mathf.Clamp01((float)reading.Fill) : 0f;
            wantedColour = reading.Level >= 2 ? Past : reading.Level == 1 ? Close : Fine;
        }

        private KSP.UI.TooltipTypes.TooltipController_Text tooltip;

        // The figure's name for the tooltip.
        private static string Name(GaugeMode mode)
        {
            switch (mode)
            {
                case GaugeMode.Stage: return "Stage Burn Time";
                case GaugeMode.Burn: return "Maneuver Burn";
                case GaugeMode.Impact: return "Time to Impact";
                case GaugeMode.Closing: return "Closing Speed";
                case GaugeMode.Attack: return "Angle of Attack";
                default: return "";
            }
        }

        private static string Title(GaugeMode mode)
        {
            switch (mode)
            {
                case GaugeMode.Stage: return "STAGE";
                case GaugeMode.Burn: return "BURN";
                case GaugeMode.Impact: return "IMPACT";
                case GaugeMode.Closing: return "CLOSING";
                case GaugeMode.Attack: return "AOA";
                default: return "";
            }
        }

        // ------------------------------------------------------------ kept

        private static string Path
        {
            get { return PluginData.Path("gauge.cfg"); }
        }

        private static GaugeMode Load()
        {
            try
            {
                ConfigNode node = File.Exists(Path) ? ConfigNode.Load(Path) : null;
                string text = node != null ? node.GetValue("mode") : null;
                if (text != null) return (GaugeMode)Enum.Parse(typeof(GaugeMode), text, true);
            }
            catch (Exception)
            {
            }
            return GaugeMode.Auto;
        }

        private static void Save()
        {
            string directory = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory)) Directory.CreateDirectory(directory);
            ConfigNode node = new ConfigNode();
            node.AddValue("mode", mode.ToString());
            node.Save(Path);
        }
    }

    // The right mouse button on the round gauge's Button, which itself answers
    // the left one only.
    public class GaugeRightClick : MonoBehaviour, IPointerClickHandler
    {
        internal Action Pressed;

        public void OnPointerClick(PointerEventData data)
        {
            if (data.button == PointerEventData.InputButton.Right && Pressed != null) Pressed();
        }
    }
}
