using System.Collections.Generic;
using ReDefinition.Core;
using UnityEngine;
using UnityEngine.UI;

namespace ReDefinition.Flight
{
    // The space centre's facility buttons (the column of buttons for the VAB, the
    // SPH, the launch pad, the runway and the rest), so that the buttons over the
    // buildings and in the editors look like them: KSP's own sprites, as their
    // Buttons show them and swap to (normal, highlighted, pressed, disabled),
    // held. They belong to the space centre's scene (Buttons_VAB_0 to _3 and so
    // on, sharedassets5) and are there only once it has been shown, which a game
    // loaded always is first; held here, Unity keeps them after it (it unloads
    // an asset only where nothing reaches it, static fields included:
    // Resources.UnloadUnusedAssets). A UI theme that replaces their textures
    // (HUDReplacer) shows here as well.
    [KSPAddon(KSPAddon.Startup.SpaceCentre, false)]
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

        // Taken a second and a half into the scene, where a theme has swapped the
        // pictures as the scene's UI stands; looked at again every second until all
        // four are there, for ten seconds.
        private float next;
        private float until;

        private void Awake()
        {
            next = Time.unscaledTime + 1.5f;
            until = next + 10f;
        }

        private void Update()
        {
            if (next <= 0f || Time.unscaledTime < next) return;
            next = Time.unscaledTime + 1f;
            int found = 0;
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
            Debug.Log(Log.Tag + " The space centre's facility buttons taken for the buttons in the world: " + found + " of "
                      + Wanted.Length + ".");
        }

        private static int Capture()
        {
            Seen.Clear();
            foreach (Button button in Resources.FindObjectsOfTypeAll<Button>())
            {
                Image image = button != null ? button.targetGraphic as Image : null;
                if (image == null || image.sprite == null || !button.gameObject.scene.IsValid()) continue;
                string name = Base(image.sprite.name);
                if (name == null || Seen.Contains(name)) continue;
                Look look = new Look
                {
                    Normal = image.sprite,
                    Highlighted = button.spriteState.highlightedSprite,
                    Pressed = button.spriteState.pressedSprite,
                    Disabled = button.spriteState.disabledSprite,
                };
                if (look.Normal == null) continue;
                if (look.Highlighted == null) look.Highlighted = look.Normal;
                if (look.Pressed == null) look.Pressed = look.Highlighted;
                if (look.Disabled == null) look.Disabled = look.Normal;
                looks[name] = look;
                Seen.Add(name);
            }
            return Seen.Count;
        }

        private static readonly HashSet<string> Seen = new HashSet<string>();

        // "Buttons_VAB" for "Buttons_VAB_0"; null for another sprite.
        private static string Base(string sprite)
        {
            foreach (string wanted in Wanted)
                if (sprite.StartsWith(wanted + "_", System.StringComparison.Ordinal)) return wanted;
            return null;
        }
    }
}
