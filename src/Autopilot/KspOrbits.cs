using System.Collections.Generic;
using ReDefinition.Orbits;
using UnityEngine;

namespace ReDefinition.Autopilot
{
    // KSP's orbits and maneuver nodes seen by the orbit maths (src/Orbits). A state is taken in Unity's world axes, centred on the body, which do
    // not turn with it: the same frame the nodes' burn vectors are in. That frame is left-handed (Maneuvers.ToNodeFrame knows).
    internal static class KspOrbits
    {
        // The state of a body or vessel on `orbit` at `ut`, relative to the body the orbit is round.
        internal static State StateAt(Orbit orbit, double ut)
        {
            Vector3d r = orbit.getRelativePositionAtUT(ut).xzy;
            Vector3d v = orbit.getOrbitalVelocityAtUT(ut).xzy;
            return new State(new V3(r.x, r.y, r.z), new V3(v.x, v.y, v.z));
        }

        internal static V3 ToV3(Vector3d v)
        {
            return new V3(v.x, v.y, v.z);
        }

        internal static Vector3d ToVector(V3 v)
        {
            return new Vector3d(v.X, v.Y, v.Z);
        }

        // The orbit the vessel is on at `ut`, by the flight plan with the nodes it holds: the patch that covers the time, or the vessel's own.
        internal static Orbit OrbitAt(Vessel vessel, double ut)
        {
            PatchedConicSolver solver = vessel.patchedConicSolver;
            if (solver != null && solver.flightPlan != null)
                foreach (Orbit patch in solver.flightPlan)
                    if (patch != null && patch.StartUT <= ut && ut < patch.EndUT) return patch;
            return vessel.orbit;
        }

        // A maneuver node at `ut` for a change of velocity given in the world's axes, put on the orbit the vessel has there; null where the vessel
        // has no flight plan.
        internal static ManeuverNode AddNode(Vessel vessel, double ut, V3 deltaV)
        {
            PatchedConicSolver solver = vessel.patchedConicSolver;
            if (solver == null) return null;
            State at = StateAt(OrbitAt(vessel, ut), ut);
            V3 inNode = Maneuvers.ToNodeFrame(at, deltaV, true);
            ManeuverNode node = solver.AddManeuverNode(ut);
            node.DeltaV = new Vector3d(inNode.X, inNode.Y, inNode.Z);
            solver.UpdateFlightPlan();
            return node;
        }

        // Every maneuver node the vessel holds, taken off.
        internal static void ClearNodes(Vessel vessel)
        {
            PatchedConicSolver solver = vessel.patchedConicSolver;
            if (solver == null) return;
            var nodes = new List<ManeuverNode>(solver.maneuverNodes);
            foreach (ManeuverNode node in nodes) solver.RemoveManeuverNode(node);
            solver.UpdateFlightPlan();
        }

        // The first patch round `body` that lies ahead of the vessel on the orbit it flies now, nodes left out: the chain of conics from its own.
        internal static Orbit PatchAhead(Vessel vessel, CelestialBody body)
        {
            Orbit patch = vessel.orbit;
            for (int i = 0; i < 8 && patch != null; i++)
            {
                if (patch.referenceBody == body && i > 0) return patch;
                patch = patch.patchEndTransition != Orbit.PatchTransitionType.FINAL ? patch.nextPatch : null;
            }
            return null;
        }

        // The patch of the flight plan round `body`, the first one; null where the plan does not reach it.
        internal static Orbit PatchAround(Vessel vessel, CelestialBody body)
        {
            PatchedConicSolver solver = vessel.patchedConicSolver;
            if (solver == null || solver.flightPlan == null) return null;
            foreach (Orbit patch in solver.flightPlan)
                if (patch != null && patch.referenceBody == body) return patch;
            return null;
        }
    }
}
