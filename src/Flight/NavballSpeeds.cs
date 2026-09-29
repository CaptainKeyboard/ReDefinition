using System.Collections;
using System.Collections.Generic;
using KSP.UI.Screens.Flight;
using ReDefinition.Core;
using TMPro;
using UnityEngine;

namespace ReDefinition.Flight
{
    // The vessel's vertical and horizontal speed beside the navball, on its right,
    // where the SAS modes stand on its left: two copies of the navball's heading
    // field (HeadingFrame: its picture, a label and a value; sharedassets7), one
    // per row of the SAS modes' two upper rows, mirrored about the navball's middle.
    // Labelled V and H, with the value in metres per second: the surface speed's
    // parts, as KSP's vessel gives them (Vessel.verticalSpeed,
    // Vessel.horizontalSrfSpeed). The copies hang where the heading field does,
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

            // The SAS buttons' rows, from the top, and where they reach sideways.
            List<float> rows = new List<float>();
            float left = float.MaxValue, right = float.MinValue;
            Vector3[] corners = new Vector3[4];
            for (int i = 0; i < modes.transform.childCount; i++)
            {
                RectTransform button = modes.transform.GetChild(i) as RectTransform;
                if (button == null) continue;
                button.GetWorldCorners(corners);
                Vector3 low = frame.InverseTransformPoint(corners[0]), high = frame.InverseTransformPoint(corners[2]);
                left = Mathf.Min(left, low.x);
                right = Mathf.Max(right, high.x);
                float middle = (low.y + high.y) / 2f;
                if (!rows.Exists(row => Mathf.Abs(row - middle) < 4f)) rows.Add(middle);
            }
            if (rows.Count < 2 || left > right) yield break;
            rows.Sort((a, b) => b.CompareTo(a));
            float x = 2f * frame.rect.center.x - (left + right) / 2f;

            vertical = Field(heading, "V", frame, new Vector3(x, rows[0], 0f));
            horizontal = Field(heading, "H", frame, new Vector3(x, rows[1], 0f));
            Debug.Log(Log.Tag + " The navball's vertical and horizontal speeds shown on its right, at " + x.ToString("0")
                      + " across its frame, rows " + rows[0].ToString("0") + " and " + rows[1].ToString("0") + ".");
        }

        // A copy of the heading field, labelled, centred at `at` in the frame's units.
        private static TextMeshProUGUI Field(Transform heading, string label, RectTransform frame, Vector3 at)
        {
            GameObject copy = Instantiate(heading.gameObject, heading.parent, false);
            copy.name = "ReDefinitionSpeed" + label;
            RectTransform rect = (RectTransform)copy.transform;
            Vector2 size = ((RectTransform)heading).rect.size;
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = size;
            rect.position = frame.TransformPoint(at);
            TextMeshProUGUI value = null;
            foreach (TextMeshProUGUI text in copy.GetComponentsInChildren<TextMeshProUGUI>(true))
            {
                if (text.name == "Label_HDG") text.text = label;
                else if (text.name == "TextHeading") value = text;
            }
            if (value != null)
            {
                // Wider values shrink to the field, as KSP's own never needs to.
                value.enableAutoSizing = true;
                value.fontSizeMax = value.fontSize;
                value.fontSizeMin = value.fontSize * 0.6f;
                value.text = "";
            }
            return value;
        }

        private void LateUpdate()
        {
            Vessel vessel = FlightGlobals.ActiveVessel;
            if (vessel == null) return;
            if (vertical != null) vertical.text = Speed(vessel.verticalSpeed);
            if (horizontal != null) horizontal.text = Speed(vessel.horizontalSrfSpeed);
        }

        // "12.3m/s" below 100, "123m/s" above, as KSP's speed display writes it.
        internal static string Speed(double metresPerSecond)
        {
            double size = System.Math.Abs(metresPerSecond);
            double shown = size < 99.95 ? System.Math.Round(metresPerSecond, 1) : System.Math.Round(metresPerSecond);
            // No "-0.0" for a speed that rounds to nothing.
            return (shown == 0.0 ? 0.0 : shown).ToString(size < 99.95 ? "0.0" : "0", System.Globalization.CultureInfo.InvariantCulture) + "m/s";
        }
    }
}
