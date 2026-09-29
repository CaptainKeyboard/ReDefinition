using System.Collections;
using KSP.UI.Screens.Flight;
using ReDefinition.Core;
using ReDefinition.Engineer;
using TMPro;
using UnityEngine;

namespace ReDefinition.Flight
{
    // KSP's vertical speed gauge made a digital display of the time until the
    // vessel hits the ground: the vertical speed stands at the navball
    // (NavballSpeeds). The gauge (GaugeVerticalSpeed under the top frame's
    // altimeter, 83 by 85: its picture with the dial's face, scale and bezel, a
    // needle, a knob, the label "VERT SPD" and the sink rate light; sharedassets7)
    // keeps its bezel and its light. Its picture is read as it stands, the theme's
    // where one replaces it, and its face painted over in the face's own colour up
    // to the bezel, so that scale and numbers go; needle, knob and label are
    // switched off. Two copies of the label stand on the face: "IMPACT", and the
    // time, or a dash while the vessel's path does not meet the ground or it is
    // not coming down (Impact.Seconds).
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class ImpactGauge : MonoBehaviour
    {
        private const float Interval = 0.25f;
        // How far out the face reaches, as a share of the bezel's outer radius:
        // ZTheme's bezel starts just beyond, KSP's a little further out (read
        // from both pictures).
        internal const float Face = 0.84f;

        private TextMeshProUGUI title;
        private TextMeshProUGUI value;
        private Color shown = Color.white, dim = Color.grey;
        private float next;

        private IEnumerator Start()
        {
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
            UnityEngine.UI.Image picture = gauge != null ? gauge.GetComponent<UnityEngine.UI.Image>() : null;
            Vector2 centre;
            if (label == null || picture == null || !Blank(picture, out centre))
            {
                Debug.Log(Log.Tag + " The time to impact is not shown: KSP's vertical speed gauge, its label or its picture was not found.");
                yield break;
            }
            foreach (string part in new[] { "GaugeVerticalSpeedPointerParent", "GaugeVerticalSpeedKnob" })
            {
                Transform child = gauge.transform.Find(part);
                if (child != null) child.gameObject.SetActive(false);
            }
            dim = label.color;
            // The dial's middle on the gauge: neither picture has it in its own.
            Rect rect = picture.rectTransform.rect;
            Vector2 middle = new Vector2(centre.x * rect.width, centre.y * rect.height);
            title = Copy(label, "ReDefinitionImpactTitle", middle + new Vector2(0f, 9f), label.fontSize * 1.15f, label.color);
            title.text = "IMPACT";
            value = Copy(label, "ReDefinitionImpactTime", middle + new Vector2(0f, -6f), label.fontSize * 2.1f, shown);
            label.gameObject.SetActive(false);
            Debug.Log(Log.Tag + " KSP's vertical speed gauge shows the time to impact.");
        }

        // The gauge's picture with its face painted over, and where the dial's
        // middle lies off the picture's, as a share of its size; false where the
        // picture cannot be read. The dial is the picture's opaque part: its
        // middle is that part's.
        private static bool Blank(UnityEngine.UI.Image image, out Vector2 centre)
        {
            centre = Vector2.zero;
            Sprite sprite = image.sprite;
            int width, height;
            Color32[] pixels = TimeWarpBar.Pixels(sprite, out width, out height);
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
            float reach = Face * Mathf.Min(right - left + 1, top - bottom + 1) / 2f;
            centre = new Vector2((cx - (width - 1) / 2f) / width, (cy - (height - 1) / 2f) / height);
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                float dx = x - cx, dy = y - cy;
                if (dx * dx + dy * dy > reach * reach) continue;
                Color32 was = pixels[y * width + x];
                pixels[y * width + x] = new Color32(face.r, face.g, face.b, was.a);
            }
            Texture2D texture = new Texture2D(width, height, TextureFormat.ARGB32, false);
            texture.SetPixels32(pixels);
            texture.Apply();
            image.sprite = Sprite.Create(texture, new Rect(0f, 0f, width, height), new Vector2(0.5f, 0.5f), sprite.pixelsPerUnit);
            return true;
        }

        // A copy of the gauge's label, centred on `at` from the gauge's middle.
        private static TextMeshProUGUI Copy(TextMeshProUGUI label, string name, Vector2 at, float size, Color colour)
        {
            GameObject copy = Instantiate(label.gameObject, label.transform.parent, false);
            copy.name = name;
            copy.SetActive(true);
            RectTransform rect = (RectTransform)copy.transform;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(60f, size * 1.4f);
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

        private void Update()
        {
            if (value == null || Time.unscaledTime < next) return;
            next = Time.unscaledTime + Interval;
            Vessel vessel = FlightGlobals.ActiveVessel;
            // Only while it comes down: climbing, or flying level on wings, the
            // ballistic path would warn of a fall that is not happening.
            double seconds = vessel != null && vessel.verticalSpeed < -1.0 ? Impact.Seconds(vessel) : double.NaN;
            bool known = !double.IsNaN(seconds);
            value.text = known ? EngineerFormat.Time(seconds) : "-";
            value.color = known ? shown : dim;
        }
    }

    // When a vessel's path meets the ground: its orbit, followed from now to its
    // next periapsis, against the height of the ground under each point of it at
    // the time the vessel gets there (CelestialBody.TerrainAltitude, the sea's
    // surface over water, below the datum where there is no sea; the body turning
    // beneath: the longitude moves west by its turn since now). Drag is not counted: in air the vessel falls later than
    // this says. First in steps along the way, then by halving the step where the
    // height over the ground turns negative. NaN where it does not meet the ground:
    // on the ground already, or a periapsis above the highest ground there is.
    internal static class Impact
    {
        private const int Steps = 200;
        private const int Halvings = 16;

        // One search per vessel and quarter second, for the gauge and the panel
        // alike.
        private static Vessel cachedFor;
        private static float cachedAt = -1f;
        private static double cached = double.NaN;

        internal static double Seconds(Vessel vessel)
        {
            if (vessel == cachedFor && Time.unscaledTime - cachedAt < 0.2f) return cached;
            cachedFor = vessel;
            cachedAt = Time.unscaledTime;
            cached = Search(vessel);
            return cached;
        }

        private static double Search(Vessel vessel)
        {
            if (vessel == null || vessel.LandedOrSplashed || vessel.orbit == null || vessel.mainBody == null) return double.NaN;
            Orbit orbit = vessel.orbit;
            CelestialBody body = vessel.mainBody;
            double highest = body.pqsController != null ? body.pqsController.radiusMax - body.Radius : 0.0;
            if (orbit.PeA > highest || double.IsNaN(orbit.timeToPe) || orbit.timeToPe <= 0.0) return double.NaN;
            double now = Planetarium.GetUniversalTime();
            double end = orbit.timeToPe;
            double before = 0.0;
            if (Above(orbit, body, now, 0.0) <= 0.0) return double.NaN;
            for (int i = 1; i <= Steps; i++)
            {
                double at = end * i / Steps;
                if (Above(orbit, body, now, at) > 0.0)
                {
                    before = at;
                    continue;
                }
                double low = before, high = at;
                for (int h = 0; h < Halvings; h++)
                {
                    double middle = (low + high) / 2.0;
                    if (Above(orbit, body, now, middle) > 0.0) low = middle;
                    else high = middle;
                }
                return high;
            }
            return double.NaN;
        }

        // How high the vessel stands over the ground `after` seconds from `now`.
        private static double Above(Orbit orbit, CelestialBody body, double now, double after)
        {
            Vector3d relative = orbit.getRelativePositionAtUT(now + after).xzy;
            Vector3d world = body.position + relative;
            double latitude = body.GetLatitude(world);
            double longitude = body.GetLongitude(world);
            if (body.rotates && body.rotationPeriod > 0.0) longitude -= 360.0 * after / body.rotationPeriod;
            longitude = ((longitude % 360.0) + 540.0) % 360.0 - 180.0;
            // Below the datum only where the sea covers it: craters on airless bodies
            // lie lower than it.
            return relative.magnitude - body.Radius - body.TerrainAltitude(latitude, longitude, !body.ocean);
        }
    }
}
