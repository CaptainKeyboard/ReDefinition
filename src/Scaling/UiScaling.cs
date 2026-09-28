using System.Collections.Generic;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Scaling
{
    // The scale for what KSP draws at a fixed number of pixels, whatever its UI
    // scale (docs/development/ui-scaling.md), and the mods that already scale the
    // same things, before which each of these steps aside.
    internal static class UiScaling
    {
        // 4kSP: the map's icons and captions (FourkSP.FourkSP), the R&D screen
        // (4kSP-RnD) and other mods' IMGUI windows (4kSP-ModWindows); DMagic's
        // Maneuver Node Evolved: the maneuver gizmo.
        internal const string FourkSpMap = "FourkSP.FourkSP";
        internal const string FourkSpRnD = "_4kSP_RnD.RDScalerHarmonyBootstrap";
        internal const string FourkSpWindows = "_4kSP_ModWindows.ModWindowScalerHarmonyBootstrap";
        internal const string ManeuverNodeEvolved = "BetterManeuvering.ManeuverController";

        private static readonly Dictionary<string, bool> loaded = new Dictionary<string, bool>();

        // KSP's UI scale times the player's share of it (OwnSettings.WorldUiScale).
        internal static float World
        {
            get
            {
                ReDefinitionAddon addon = ReDefinitionAddon.Instance;
                float share = addon != null ? addon.Live.WorldUiScale / 100f : 1f;
                return Mathf.Clamp(GameSettings.UI_SCALE * share, 0.25f, 5f);
            }
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

        // A mod is installed that defines this type.
        internal static bool Installed(string type)
        {
            bool found;
            if (loaded.TryGetValue(type, out found)) return found;
            found = false;
            foreach (AssemblyLoader.LoadedAssembly assembly in AssemblyLoader.loadedAssemblies)
                if (assembly.assembly != null && assembly.assembly.GetType(type, false) != null)
                {
                    found = true;
                    break;
                }
            loaded[type] = found;
            return found;
        }

        // Once per scene and piece: what it scales, or which mod it leaves it to.
        internal static void Note(string what, string otherMod)
        {
            Debug.Log(Log.Tag + " UI scaling: " + what
                      + (otherMod != null ? " left to " + otherMod + "." : " at " + World.ToString("0.00") + "."));
        }
    }
}
