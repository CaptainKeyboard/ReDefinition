using TMPro;
using UnityEngine;

namespace ReDefinition.Planner
{
    // The time warp row's label in the flight interface, which the autopilot button sits beside.
    internal static class WarpLabel
    {
        private static TextMeshProUGUI label;
        private static float lookedAt = -10f;

        // The label, looked for again a second after it was not found.
        internal static TextMeshProUGUI Find()
        {
            if (label != null) return label;
            if (Time.unscaledTime - lookedAt < 1f) return null;
            lookedAt = Time.unscaledTime;
            foreach (TextMeshProUGUI text in Resources.FindObjectsOfTypeAll<TextMeshProUGUI>())
                if (text != null && text.name == "Label_Warp" && text.gameObject.scene.IsValid()) label = text;
            return label;
        }
    }
}
