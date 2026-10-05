using System.Collections.Generic;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Flight
{
    // The space centre's facility buttons (Buttons_VAB, Buttons_SPH, ... in
    // sharedassets5, the space centre scene's assets), so that the buttons over the
    // buildings and in the editors are KSP's own: each a texture of four sprites,
    // Buttons_<name>_0 to _3 (normal top left, highlighted top right, pressed
    // bottom left, disabled bottom right; their rects, read with UnityPy). Taken by
    // name from the loaded sprites wherever they are loaded -- the space centre, and
    // a flight or an editor while they still are -- and held: Unity keeps an asset
    // where something reaches it, static fields included
    // (Resources.UnloadUnusedAssets). A UI theme replaces their texture in place
    // (HUDReplacer: Texture2D.LoadImage on KSP's own texture), so the sprites held
    // show the theme.
    [KSPAddon(KSPAddon.Startup.FlightEditorAndKSC, false)]
    public class KscButtons : MonoBehaviour
    {
        internal sealed class Look
        {
            internal Sprite Normal, Highlighted, Pressed, Disabled;
        }

        internal static readonly string[] Wanted =
        {
            "Buttons_VAB", "Buttons_SPH", "Buttons_Launchpad", "Buttons_Runway", "Buttons_TrackingStation",
            "Buttons_MissionControl", "Buttons_AstronautComplex", "Buttons_RnD", "Buttons_Admin",
        };
        private static readonly Dictionary<string, Look> looks = new Dictionary<string, Look>();

        internal static Look Get(string name)
        {
            Look look;
            return looks.TryGetValue(name, out look) && look.Normal != null ? look : null;
        }

        // Looked for a second into the scene and every second after until all are
        // there, for ten seconds.
        private float next;
        private float until;

        private void Awake()
        {
            next = Time.unscaledTime + 1f;
            until = next + 10f;
        }

        private void Update()
        {
            if (next <= 0f || Time.unscaledTime < next) return;
            next = Time.unscaledTime + 1f;
            int found;
            try
            {
                found = Capture();
            }
            catch (System.Exception e)
            {
                CompatibilityLog.Warn("ksc-buttons", "The space centre's facility buttons could not be taken (" + CompatibilityLog.Reason(e) + ").");
                next = 0f;
                return;
            }
            if (found < Wanted.Length && Time.unscaledTime < until) return;
            next = 0f;
            Debug.Log(Log.Tag + " The space centre's facility buttons held for the buttons in the world: " + found + " of "
                      + Wanted.Length + (found < Wanted.Length ? " (" + Missing() + " not loaded in this scene)" : "") + ".");
        }

        private static int Capture()
        {
            if (Held() == Wanted.Length) return Wanted.Length;
            Dictionary<string, Sprite> byName = new Dictionary<string, Sprite>();
            foreach (Sprite sprite in Resources.FindObjectsOfTypeAll<Sprite>())
            {
                if (sprite == null || sprite.texture == null) continue;
                string name = sprite.name;
                if (name.StartsWith("Buttons_", System.StringComparison.Ordinal) && !byName.ContainsKey(name))
                    byName[name] = sprite;
            }
            foreach (string wanted in Wanted)
            {
                if (Get(wanted) != null) continue;
                Sprite normal;
                if (!byName.TryGetValue(wanted + "_0", out normal)) continue;
                Look look = new Look { Normal = normal };
                byName.TryGetValue(wanted + "_1", out look.Highlighted);
                byName.TryGetValue(wanted + "_2", out look.Pressed);
                byName.TryGetValue(wanted + "_3", out look.Disabled);
                if (look.Highlighted == null) look.Highlighted = look.Normal;
                if (look.Pressed == null) look.Pressed = look.Highlighted;
                if (look.Disabled == null) look.Disabled = look.Normal;
                looks[wanted] = look;
            }
            return Held();
        }

        private static int Held()
        {
            int held = 0;
            foreach (string wanted in Wanted)
                if (Get(wanted) != null) held++;
            return held;
        }

        private static string Missing()
        {
            List<string> missing = new List<string>();
            foreach (string wanted in Wanted)
                if (Get(wanted) == null) missing.Add(wanted);
            return string.Join(", ", missing.ToArray());
        }
    }
}
