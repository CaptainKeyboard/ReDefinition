using System.Collections;
using System.Collections.Generic;
using KSP.UI.Screens.Flight;
using ReDefinition.Core;
using TMPro;
using UnityEngine;

namespace ReDefinition.Flight
{
    // The vessel's vertical and horizontal speed under the navball's SAS modes, on
    // its left: two fields as wide as the modes' block and a little more, one
    // under the other, each a dark rounded plate of our own with a label, V or H,
    // and the value in metres per second -- copies of the heading field's two
    // texts (HeadingFrame: Label_HDG and TextHeading; sharedassets7), so that they
    // have the navball's font and colours. The heading field's own picture is
    // not used: a theme draws its plate into the navball's frame. The values are
    // the surface speed's parts, as KSP's vessel gives them (Vessel.verticalSpeed,
    // Vessel.horizontalSrfSpeed). The fields hang where the heading field does,
    // so they move and hide with the navball.
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class NavballSpeeds : MonoBehaviour
    {
        private TextMeshProUGUI vertical;
        private TextMeshProUGUI horizontal;

        private IEnumerator Start()
        {
            while (NavBallToggle.Instance == null || NavBallToggle.Instance.panel == null) yield return null;
            // KSP lays the SAS modes out in their first frame.
            yield return null;
            yield return null;
            RectTransform frame = NavBallToggle.Instance.panel.panelTransform != null
                ? NavBallToggle.Instance.panel.panelTransform
                : NavBallToggle.Instance.panel.transform as RectTransform;
            Transform heading = null;
            if (frame != null)
                foreach (Transform child in frame.GetComponentsInChildren<Transform>(true))
                    if (child.name == "HeadingFrame") heading = child;
            VesselAutopilotUI modes = frame != null ? frame.GetComponentInChildren<VesselAutopilotUI>(true) : null;
            if (heading == null || modes == null)
            {
                Debug.Log(Log.Tag + " The navball's speeds are not shown: its heading field or SAS modes were not found.");
                yield break;
            }

            // Where the SAS buttons reach, in the frame's units.
            float left = float.MaxValue, right = float.MinValue, bottom = float.MaxValue;
            Vector3[] corners = new Vector3[4];
            for (int i = 0; i < modes.transform.childCount; i++)
            {
                RectTransform button = modes.transform.GetChild(i) as RectTransform;
                if (button == null) continue;
                button.GetWorldCorners(corners);
                Vector3 low = frame.InverseTransformPoint(corners[0]), high = frame.InverseTransformPoint(corners[2]);
                left = Mathf.Min(left, low.x);
                right = Mathf.Max(right, high.x);
                bottom = Mathf.Min(bottom, low.y);
            }
            if (left > right) yield break;
            float x = (left + right) / 2f;
            float width = right - left + 2f * Overhang;

            vertical = Field(heading, "V", frame, new Vector3(x, bottom - Gap - Height / 2f, 0f), width);
            horizontal = Field(heading, "H", frame, new Vector3(x, bottom - 2f * Gap - 1.5f * Height, 0f), width);
            // On the screen, in pixels from its lower left, to lay beside a
            // screenshot: the SAS buttons' block, and the two fields under it.
            Canvas canvas = frame.GetComponentInParent<Canvas>();
            Canvas root = canvas != null ? canvas.rootCanvas : null;
            Camera camera = root != null && root.renderMode != RenderMode.ScreenSpaceOverlay ? root.worldCamera : null;
            Vector2 blockLow = RectTransformUtility.WorldToScreenPoint(camera, frame.TransformPoint(new Vector3(left, bottom, 0f)));
            Vector2 blockHigh = RectTransformUtility.WorldToScreenPoint(camera, frame.TransformPoint(new Vector3(right, bottom, 0f)));
            Debug.Log(Log.Tag + " The navball's vertical and horizontal speeds shown under its SAS modes, " + width.ToString("0")
                      + " wide at " + x.ToString("0") + " across its frame, from " + (bottom - Gap).ToString("0") + " down to "
                      + (bottom - 2f * Gap - 2f * Height).ToString("0") + "; on the " + Screen.width + " by " + Screen.height
                      + " screen the SAS block's lower edge from x " + blockLow.x.ToString("0") + " to " + blockHigh.x.ToString("0")
                      + " at y " + blockLow.y.ToString("0") + ", V " + OnScreen(vertical, camera) + ", H " + OnScreen(horizontal, camera) + ".");
        }

        // A field's plate on the screen: x from-to, y from-to.
        private static string OnScreen(TextMeshProUGUI value, Camera camera)
        {
            RectTransform plate = value != null ? value.transform.parent as RectTransform : null;
            if (plate == null) return "missing";
            Vector3[] corners = new Vector3[4];
            plate.GetWorldCorners(corners);
            Vector2 low = RectTransformUtility.WorldToScreenPoint(camera, corners[0]);
            Vector2 high = RectTransformUtility.WorldToScreenPoint(camera, corners[2]);
            return "x " + low.x.ToString("0") + "-" + high.x.ToString("0") + " y " + low.y.ToString("0") + "-" + high.y.ToString("0");
        }

        // A field's height, the gap over it, and how far it reaches beyond the SAS
        // buttons on either side.
        private const float Height = 18f;
        private const float Gap = 4f;
        private const float Overhang = 8f;
        private static readonly Color Plate = new Color(0.13f, 0.13f, 0.16f, 0.85f);

        // A field centred at `at` in the frame's units: the plate, the label at its
        // left and the value filling the rest, right-aligned.
        private static TextMeshProUGUI Field(Transform heading, string label, RectTransform frame, Vector3 at, float width)
        {
            GameObject field = new GameObject("ReDefinitionSpeed" + label, typeof(RectTransform), typeof(UnityEngine.UI.Image));
            field.layer = heading.gameObject.layer;
            field.transform.SetParent(heading.parent, false);
            RectTransform rect = (RectTransform)field.transform;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(width, Height);
            rect.position = frame.TransformPoint(at);
            ReDefinition.Window.UiHygiene.Tooltip(field, label == "V" ? "Vertical Speed" : "Horizontal Speed");
            UnityEngine.UI.Image plate = field.GetComponent<UnityEngine.UI.Image>();
            plate.sprite = Rounded();
            plate.type = UnityEngine.UI.Image.Type.Sliced;
            plate.color = Plate;
            // As the heading field's plate: a click on it does not reach the scene
            // behind.
            plate.raycastTarget = true;

            TextMeshProUGUI value = null, name = null;
            foreach (TextMeshProUGUI text in heading.GetComponentsInChildren<TextMeshProUGUI>(true))
            {
                bool isLabel = text.name == "Label_HDG";
                if (!isLabel && text.name != "TextHeading") continue;
                TextMeshProUGUI copy = Instantiate(text.gameObject, field.transform, false).GetComponent<TextMeshProUGUI>();
                RectTransform place = copy.rectTransform;
                place.anchorMin = new Vector2(0f, 0f);
                place.anchorMax = new Vector2(1f, 1f);
                place.pivot = new Vector2(0.5f, 0.5f);
                // The label in the first 16 units, the value in the rest.
                place.offsetMin = new Vector2(isLabel ? 4f : 18f, 0f);
                place.offsetMax = new Vector2(isLabel ? -(width - 18f) : -5f, 0f);
                copy.margin = Vector4.zero;
                copy.enableWordWrapping = false;
                copy.raycastTarget = false;
                copy.alignment = isLabel ? TextAlignmentOptions.Center : TextAlignmentOptions.Right;
                copy.enableAutoSizing = true;
                copy.fontSizeMax = isLabel ? 11f : 13f;
                copy.fontSizeMin = 7f;
                copy.text = isLabel ? label : "";
                if (isLabel) name = copy;
                else value = copy;
            }
            // The heading's label is written for the box its picture draws under it
            // (dark on ZTheme's light box): on the plate here it takes the value's
            // colour, dimmed.
            if (name != null && value != null)
            {
                Color colour = value.color;
                name.color = new Color(colour.r, colour.g, colour.b, colour.a * 0.6f);
                name.transform.SetAsLastSibling();
            }
            return value;
        }

        // A white plate with corners rounded by 5 texels, to be sliced.
        private static Sprite rounded;

        private static Sprite Rounded()
        {
            if (rounded != null) return rounded;
            const int size = 16;
            const float corner = 5f;
            Texture2D texture = new Texture2D(size, size, TextureFormat.ARGB32, false);
            Color32[] pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                // How far into a corner's square the texel lies, from that corner's
                // centre of curvature.
                float dx = Mathf.Max(corner - (x + 0.5f), (x + 0.5f) - (size - corner), 0f);
                float dy = Mathf.Max(corner - (y + 0.5f), (y + 0.5f) - (size - corner), 0f);
                float alpha = Mathf.Clamp01(corner - Mathf.Sqrt(dx * dx + dy * dy) + 0.5f);
                pixels[y * size + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(alpha * 255f));
            }
            texture.SetPixels32(pixels);
            texture.Apply();
            rounded = Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f, 0,
                SpriteMeshType.FullRect, new Vector4(corner + 1f, corner + 1f, corner + 1f, corner + 1f));
            return rounded;
        }

        // The speeds as last written, rounded as shown: the text is made anew only when
        // one of them changes, not in every frame.
        private double verticalShown = double.NaN;
        private double horizontalShown = double.NaN;

        private void LateUpdate()
        {
            Vessel vessel = FlightGlobals.ActiveVessel;
            if (vessel == null) return;
            if (vertical != null) Show(vertical, vessel.verticalSpeed, ref verticalShown);
            if (horizontal != null) Show(horizontal, vessel.horizontalSrfSpeed, ref horizontalShown);
        }

        private static void Show(TextMeshProUGUI field, double metresPerSecond, ref double before)
        {
            double shown = Shown(metresPerSecond);
            if (shown == before) return;
            before = shown;
            field.text = Speed(metresPerSecond);
        }

        // "12.3m/s" below 100, "123m/s" above, as KSP's speed display writes it.
        internal static string Speed(double metresPerSecond)
        {
            double size = System.Math.Abs(metresPerSecond);
            // No "-0.0" for a speed that rounds to nothing.
            return Shown(metresPerSecond).ToString(size < 99.95 ? "0.0" : "0", System.Globalization.CultureInfo.InvariantCulture) + "m/s";
        }

        // The speed rounded as it is written, which alone decides the text.
        private static double Shown(double metresPerSecond)
        {
            double shown = System.Math.Abs(metresPerSecond) < 99.95 ? System.Math.Round(metresPerSecond, 1) : System.Math.Round(metresPerSecond);
            return shown == 0.0 ? 0.0 : shown;
        }
    }
}
