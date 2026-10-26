using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Scaling
{
    // The scale for what KSP draws at a fixed number of pixels, whatever its UI
    // scale (src/Scaling/README.md), and the mods that scale the same
    // things, whose scaling OtherScalers turns off.
    internal static class UiScaling
    {
        // 4kSP: the map's icons and captions (FourkSP.FourkSP), the R&D screen
        // (4kSP-RnD) and other mods' IMGUI windows (4kSP-ModWindows); DMagic's
        // Maneuver Node Evolved: the maneuver gizmo.
        internal const string FourkSpMap = "FourkSP.FourkSP";
        internal const string FourkSpRnD = "_4kSP_RnD.RDScalerHarmonyBootstrap";
        internal const string FourkSpWindows = "_4kSP_ModWindows.ModWindowScalerHarmonyBootstrap";
        internal const string ManeuverNodeEvolved = "BetterManeuvering.ManeuverController";

        // KSP's UI scale.
        internal static float World
        {
            get { return Mathf.Clamp(GameSettings.UI_SCALE, 0.25f, 8f); }
        }

        // Other mods' IMGUI windows at KSP's UI scale (OwnSettings.ScaleModWindows).
        internal static bool ModWindows
        {
            get
            {
                ReDefinitionAddon addon = ReDefinitionAddon.Instance;
                return addon == null || addon.Live.ScaleModWindows;
            }
        }

        // Once per scene and piece: what it scales, at which scale.
        internal static void Note(string what)
        {
            Debug.Log(Log.Tag + " UI scaling: " + what + " at " + World.ToString("0.00") + ".");
        }
    }
}
