using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Planner.Autopilot.Flight
{
    // The solar panels, antennas and radiators the vessel folded for the flight, put out where the route ended. A part that is broken or
    // already out stays as it is.
    internal static class Deployables
    {
        // Puts out every deployable part that is retracted; says how many in the log.
        internal static int Extend(Vessel vessel)
        {
            // Not in the air, where a panel put out would be torn off.
            if (vessel == null || !vessel.loaded || vessel.atmDensity > 0.01) return 0;
            int count = 0;
            foreach (ModuleDeployablePart part in vessel.FindPartModulesImplementing<ModuleDeployablePart>())
            {
                if (part.deployState != ModuleDeployablePart.DeployState.RETRACTED || part.part.ShieldedFromAirstream) continue;
                part.Extend();
                count++;
            }
            Debug.Log(Log.Tag + " Route: " + count + " solar panel(s), antenna(s) or radiator(s) put out on arrival.");
            return count;
        }
    }
}
