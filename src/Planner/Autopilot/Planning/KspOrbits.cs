using System.Collections.Generic;
using ReDefinition.Orbits;
using UnityEngine;

namespace ReDefinition.Autopilot.Planning
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

        internal static Orbit OrbitAt(Vessel vessel, double ut)
        {
            PatchedConicSolver solver = vessel.patchedConicSolver;
            Orbit found = null;
            if (solver != null && solver.flightPlan != null)
                foreach (Orbit patch in solver.flightPlan)
                    if (patch != null && patch.StartUT <= ut) found = patch;
            if (found != null) return found;
            // The game fills the flight plan only where there are nodes. Without any, the vessel's own orbit and the patches that follow it are the chain.
            Orbit chain = vessel.orbit;
            for (int i = 0; i < 150 && chain.patchEndTransition != Orbit.PatchTransitionType.FINAL && chain.nextPatch != null && chain.nextPatch.StartUT <= ut; i++)
                chain = chain.nextPatch;
            return chain;
        }

        // A maneuver node at `ut` for a change of velocity given in the world's axes, put on the orbit the vessel has there; null where the vessel
        // has no flight plan.
        internal static ManeuverNode AddNode(Frame frame, double ut, V3 deltaV)
        {
            PatchedConicSolver solver = frame.Solver;
            if (solver == null) return null;
            State at = StateAt(OrbitAt(frame.Vessel, ut), ut);
            V3 inNode = Maneuvers.ToNodeFrame(at, deltaV, true);
            ManeuverNode node = solver.AddManeuverNode(ut);
            node.DeltaV = new Vector3d(inNode.X, inNode.Y, inNode.Z);
            solver.UpdateFlightPlan();
            return node;
        }

        internal static void ClearNodes(Vessel vessel)
        {
            ClearNodes(new Frame(vessel));
        }

        // Every maneuver node the vessel holds, taken off; in a plan ahead only those from the state on, the nodes of the steps before it stay.
        internal static void ClearNodes(Frame frame)
        {
            PatchedConicSolver solver = frame.Solver;
            if (solver == null) return;
            var nodes = new List<ManeuverNode>(solver.maneuverNodes);
            foreach (ManeuverNode node in nodes)
                if (!Guidance.GuideNode.Is(node) && (!frame.Ahead || node.UT > frame.Now + Frame.AfterNode)) Remove(node);
            solver.UpdateFlightPlan();
        }

        // The maneuver nodes of a vessel that is not loaded, taken off with the saved plan the game loads them from. A route flown without the player has
        // spent them or leaves them behind; a node that is left reaches the map again when the vessel is loaded, as one the game cannot solve.
        internal static void DropNodes(Vessel vessel)
        {
            if (vessel.loaded) return;
            PatchedConicSolver solver = vessel.PatchedConicsAttached ? vessel.patchedConicSolver : null;
            if (solver != null && solver.maneuverNodes.Count > 0)
            {
                // The last first: the solver drops a node it cannot solve by itself, with a message to the player, when an earlier one is gone.
                for (int i = solver.maneuverNodes.Count - 1; i >= 0; i--) Remove(solver.maneuverNodes[i]);
                solver.UpdateFlightPlan();
            }
            if (vessel.flightPlanNode != null && vessel.flightPlanNode.CountNodes > 0) vessel.flightPlanNode.ClearData();
            if (vessel.protoVessel != null && vessel.protoVessel.flightPlan != null && vessel.protoVessel.flightPlan.CountNodes > 0) vessel.protoVessel.flightPlan.ClearData();
        }

        // A node taken off the vessel. The solver's own removal leaves the node's icon on the map and its place among the camera's targets.
        // The node's call takes them away, for a node that the map has made them for.
        private static void Remove(ManeuverNode node)
        {
            if (node.scaledSpaceTarget != null) node.RemoveSelf();
            else node.solver.RemoveManeuverNode(node);
        }

        // The first patch round `body` that lies ahead of the vessel on the orbit it flies now, nodes left out: the chain of conics from its own.
        internal static Orbit PatchAhead(Frame frame, CelestialBody body)
        {
            Orbit patch = frame.Orbit;
            for (int i = 0; i < 8 && patch != null; i++)
            {
                if (patch.referenceBody == body && i > 0) return patch;
                patch = patch.patchEndTransition != Orbit.PatchTransitionType.FINAL ? patch.nextPatch : null;
            }
            return null;
        }

        // The patch of the flight plan round `body`, the first one; null where the plan does not reach it.
        internal static Orbit PatchAround(Frame frame, CelestialBody body)
        {
            PatchedConicSolver solver = frame.Solver;
            if (solver == null || solver.flightPlan == null) return null;
            foreach (Orbit patch in solver.flightPlan)
                if (patch != null && patch.referenceBody == body && (!frame.Ahead || patch.EndUT > frame.Now)) return patch;
            return null;
        }
    }
}
