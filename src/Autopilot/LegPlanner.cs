using System.Collections.Generic;
using UnityEngine;

namespace ReDefinition.Autopilot
{
    // Where a route goes: a body, and on its surface or in a circular orbit at an altitude. The planner's stops, without the planner.
    internal sealed class Destination
    {
        public CelestialBody Body;
        public bool Surface;
        public double Altitude;
    }

    // The next leg of a route from where the vessel is, planned as nodes. The stop the vessel has reached is found, and the leg to the one
    // after it is handed to the planner that knows how to fly it.
    internal static class LegPlanner
    {
        // Plans the leg to the first stop the vessel has not reached; the reason where it cannot yet.
        internal static NodePlanner.Result PlanNext(Vessel vessel, IList<Destination> route)
        {
            if (vessel == null || route == null || route.Count < 2) return NodePlanner.Result.Failed("There is no route.");
            if (vessel.situation != Vessel.Situations.ORBITING)
                return NodePlanner.Result.Failed("The vessel is not in an orbit: " + vessel.situation.ToString().ToLowerInvariant().Replace('_', ' ') + ".");
            int last = -1;
            for (int i = 0; i < route.Count; i++)
                if (Passed(vessel, route[i])) last = i;
            if (last < 0) return NodePlanner.Result.Failed("The vessel is not at a stop of the route.");
            if (last + 1 >= route.Count) return NodePlanner.Result.Failed("The vessel is at the last stop.");
            Destination next = route[last + 1];
            if (next.Body == null) return NodePlanner.Result.Failed("The next stop has no body.");
            if (next.Surface) return NodePlanner.Result.Failed("A landing is not planned as nodes yet.");
            if (next.Body == vessel.mainBody) return NodePlanner.ChangeOrbit(vessel, next.Altitude);
            if (next.Body.referenceBody == vessel.mainBody) return NodePlanner.TransferToBody(vessel, next.Body, next.Altitude);
            return NodePlanner.Result.Failed("A way from " + vessel.mainBody.bodyName + " to " + next.Body.bodyName + " is not planned as nodes yet.");
        }

        // Whether the vessel is at or past a stop: in an orbit of its body near the stop's altitude, or, for a surface stop, in orbit round the body
        // it was left from.
        private static bool Passed(Vessel vessel, Destination stop)
        {
            if (stop.Body != vessel.mainBody) return false;
            if (stop.Surface) return true;
            double tolerance = System.Math.Max(2000.0, 0.1 * stop.Altitude);
            return System.Math.Abs(vessel.orbit.PeA - stop.Altitude) <= tolerance && System.Math.Abs(vessel.orbit.ApA - stop.Altitude) <= tolerance;
        }
    }
}
