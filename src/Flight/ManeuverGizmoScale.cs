using System.Collections.Generic;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Flight
{
    // The maneuver node's gizmo at the size of the rest of the UI. KSP draws it
    // 128 screen pixels across, whatever the UI scale (ManeuverGizmoBase: its
    // screenSize, set once to 128, divided by Screen.height in LateUpdate;
    // decompiled), so that it is half as large on a 4K screen as on a 1080p one.
    // Here each open gizmo's screenSize is 128 times GameSettings.UI_SCALE. A
    // gizmo KSP shrinks to minimise it (screenSize 0) is left to shrink. Its
    // handles and their drag distances follow its size, as KSP works them out
    // from its transform. With DMagic's Maneuver Node Evolved installed, which sizes
    // the gizmo from the same field by settings of its own
    // (BetterManeuvering.ManeuverController), this stays off.
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class ManeuverGizmoScale : MonoBehaviour
    {
        private const float KspSize = 128f;
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
            float size = KspSize * Mathf.Max(0.5f, GameSettings.UI_SCALE);
            List<ManeuverNode> nodes = solver.maneuverNodes;
            for (int i = 0; i < nodes.Count; i++)
            {
                ManeuverGizmo gizmo = nodes[i] != null ? nodes[i].attachedGizmo : null;
                if (gizmo == null || gizmo.screenSize <= 0f || Mathf.Approximately(gizmo.screenSize, size)) continue;
                gizmo.screenSize = size;
                if (logged) continue;
                logged = true;
                Debug.Log(Log.Tag + " Maneuver gizmo drawn at " + size.ToString("0") + " pixels, for UI scale "
                          + GameSettings.UI_SCALE.ToString("0.00") + ".");
            }
        }
    }
}
