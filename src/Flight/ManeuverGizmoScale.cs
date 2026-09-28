using System.Collections.Generic;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Flight
{
    // The maneuver node's gizmo at the size of the rest of the UI. KSP draws it at
    // a fixed number of screen pixels, whatever the UI scale (ManeuverGizmoBase:
    // screenSize divided by Screen.height in LateUpdate; decompiled; the value comes
    // from the gizmo's prefab), so that it is half as large on a 4K screen as on a
    // 1080p one. Here each gizmo's own screenSize, as it appears, is multiplied by
    // GameSettings.UI_SCALE. A gizmo KSP shrinks to minimise it (screenSize 0) is
    // left to shrink; a gizmo opened again is a new one. Its handles and their drag
    // distances follow its size, as KSP works them out from its transform. With
    // DMagic's Maneuver Node Evolved installed, which sizes the gizmo from the same
    // field by settings of its own (BetterManeuvering.ManeuverController), this
    // stays off.
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class ManeuverGizmoScale : MonoBehaviour
    {
        // Each gizmo's size as KSP made it.
        private readonly Dictionary<ManeuverGizmo, float> sizes = new Dictionary<ManeuverGizmo, float>();
        private readonly List<ManeuverGizmo> gone = new List<ManeuverGizmo>();
        private bool logged;

        private void Start()
        {
            foreach (AssemblyLoader.LoadedAssembly loaded in AssemblyLoader.loadedAssemblies)
            {
                if (loaded.assembly == null || loaded.assembly.GetType("BetterManeuvering.ManeuverController", false) == null) continue;
                Debug.Log(Log.Tag + " Maneuver Node Evolved sizes the maneuver gizmo; ReDefinition leaves it to it.");
                enabled = false;
                return;
            }
        }

        private void Update()
        {
            Vessel vessel = FlightGlobals.ActiveVessel;
            PatchedConicSolver solver = vessel != null ? vessel.patchedConicSolver : null;
            if (solver == null || solver.maneuverNodes == null) return;
            float scale = Mathf.Max(0.5f, GameSettings.UI_SCALE);
            List<ManeuverNode> nodes = solver.maneuverNodes;
            for (int i = 0; i < nodes.Count; i++)
            {
                ManeuverGizmo gizmo = nodes[i] != null ? nodes[i].attachedGizmo : null;
                if (gizmo == null || gizmo.screenSize <= 0f) continue;
                float own;
                if (!sizes.TryGetValue(gizmo, out own))
                {
                    own = gizmo.screenSize;
                    sizes[gizmo] = own;
                    if (!logged)
                    {
                        logged = true;
                        Debug.Log(Log.Tag + " Maneuver gizmo drawn at " + (own * scale).ToString("0") + " pixels instead of "
                                  + own.ToString("0") + ", for UI scale " + scale.ToString("0.00") + ".");
                    }
                }
                if (!Mathf.Approximately(gizmo.screenSize, own * scale)) gizmo.screenSize = own * scale;
            }
            // Gizmos closed and destroyed.
            gone.Clear();
            foreach (ManeuverGizmo gizmo in sizes.Keys)
                if (gizmo == null) gone.Add(gizmo);
            foreach (ManeuverGizmo gizmo in gone) sizes.Remove(gizmo);
        }
    }
}
