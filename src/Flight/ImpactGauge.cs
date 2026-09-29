using System.Collections;
using KSP.UI.Screens.Flight;
using ReDefinition.Core;
using ReDefinition.Engineer;
using TMPro;
using UnityEngine;

namespace ReDefinition.Flight
{
    // The time until the vessel hits the ground, as digits on KSP's vertical speed
    // gauge, whose needle still shows the vertical speed. The gauge (GaugeVerticalSpeed
    // under the top frame's altimeter: a RotationalGauge from -135 to 135 degrees,
    // so its needle never crosses the bottom quarter, and the label "VERT SPD" in
    // KSP's 8-point font; sharedassets7) gets two copies of that label in that
    // bottom quarter: "IMPACT" and the time. They show only while the vessel comes
    // down and its path meets the ground (Impact.Seconds); otherwise the gauge is as
    // KSP has it.
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class ImpactGauge : MonoBehaviour
    {
        private const float Interval = 0.25f;

        private TextMeshProUGUI title;
        private TextMeshProUGUI value;
        private float next;

        private IEnumerator Start()
        {
            float until = Time.unscaledTime + 15f;
            VerticalSpeedGauge gauge = null;
            while (Time.unscaledTime < until && (gauge = FindObjectOfType<VerticalSpeedGauge>()) == null) yield return null;
            TextMeshProUGUI label = null;
            if (gauge != null)
                foreach (TextMeshProUGUI text in gauge.GetComponentsInChildren<TextMeshProUGUI>(true))
                    if (text.name == "Label_Vert") label = text;
            if (label == null)
            {
                Debug.Log(Log.Tag + " The time to impact is not shown: KSP's vertical speed gauge or its label was not found.");
                yield break;
            }
            title = Copy(label, "ReDefinitionImpactTitle", -21f, label.fontSize, label.color);
            title.text = "IMPACT";
            value = Copy(label, "ReDefinitionImpactTime", -31f, label.fontSize * 1.5f, Color.white);
            Show(false);
            Debug.Log(Log.Tag + " The time to impact shows on KSP's vertical speed gauge.");
        }

        // A copy of the gauge's label, centred across the gauge at `y` below its middle.
        private static TextMeshProUGUI Copy(TextMeshProUGUI label, string name, float y, float size, Color colour)
        {
            GameObject copy = Instantiate(label.gameObject, label.transform.parent, false);
            copy.name = name;
            RectTransform rect = (RectTransform)copy.transform;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(50f, size * 1.4f);
            rect.anchoredPosition = new Vector2(0f, y);
            TextMeshProUGUI text = copy.GetComponent<TextMeshProUGUI>();
            text.fontSize = size;
            text.color = colour;
            text.enableWordWrapping = false;
            text.alignment = TextAlignmentOptions.Center;
            text.raycastTarget = false;
            return text;
        }

        private void Show(bool shown)
        {
            if (title != null && title.gameObject.activeSelf != shown) title.gameObject.SetActive(shown);
            if (value != null && value.gameObject.activeSelf != shown) value.gameObject.SetActive(shown);
        }

        private void Update()
        {
            if (value == null || Time.unscaledTime < next) return;
            next = Time.unscaledTime + Interval;
            Vessel vessel = FlightGlobals.ActiveVessel;
            // Only while it comes down: climbing, or flying level on wings, the
            // ballistic path would warn of a fall that is not happening.
            double seconds = vessel != null && vessel.verticalSpeed < -1.0 ? Impact.Seconds(vessel) : double.NaN;
            if (double.IsNaN(seconds))
            {
                Show(false);
                return;
            }
            value.text = EngineerFormat.Time(seconds);
            Show(true);
        }
    }

    // When a vessel's path meets the ground: its orbit, followed from now to its
    // next periapsis, against the height of the ground under each point of it at
    // the time the vessel gets there (CelestialBody.TerrainAltitude, the sea's
    // surface over water, below the datum where there is no sea; the body turning beneath: the longitude moves west by
    // its turn since now). Drag is not counted: in air the vessel falls later than
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
