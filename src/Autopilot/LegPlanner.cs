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
        // Plans the next step of the leg to the first stop the vessel has not reached: the burn for it as a node, the reason where it cannot yet.
        internal static NodePlanner.Result PlanNext(Vessel vessel, IList<Destination> route)
        {
            if (vessel == null || route == null || route.Count < 2) return NodePlanner.Result.Failed("There is no route.");
            if (vessel.situation != Vessel.Situations.ORBITING && vessel.situation != Vessel.Situations.ESCAPING && vessel.situation != Vessel.Situations.SUB_ORBITAL)
                return NodePlanner.Result.Failed("The vessel is not in an orbit: " + vessel.situation.ToString().ToLowerInvariant().Replace('_', ' ') + ".");
            int last = -1;
            for (int i = 0; i < route.Count; i++)
                if (Passed(vessel, route[i])) last = i;
            if (last + 1 >= route.Count) return NodePlanner.Result.Done("The vessel is at the last stop.");
            int next = last >= 0 ? last + 1 : HeadedFor(vessel, route);
            if (next < 0) return NodePlanner.Result.Failed("The vessel is not at a stop of the route, nor on its way to one.");
            Destination stop = route[next];
            if (stop.Body == null) return NodePlanner.Result.Failed("The next stop has no body.");
            if (stop.Surface) return NodePlanner.Result.Failed("A landing is not planned as nodes yet.");
            if (stop.Body == vessel.mainBody)
                return vessel.orbit.eccentricity >= 1.0 ? NodePlanner.Capture(vessel) : NodePlanner.ChangeOrbit(vessel, stop.Altitude);
            if (stop.Body.referenceBody != vessel.mainBody)
                return NodePlanner.Result.Failed("A way from " + vessel.mainBody.bodyName + " to " + stop.Body.bodyName + " is not planned as nodes yet.");
            return KspOrbits.PatchAhead(vessel, stop.Body) != null
                ? NodePlanner.CorrectCourse(vessel, stop.Body, stop.Altitude)
                : NodePlanner.TransferToBody(vessel, stop.Body, stop.Altitude);
        }

        // The stop the vessel is on its way to, where it is at none: the first after the start whose body it is round, or whose body the plan
        // shows an encounter with; -1 where there is none.
        private static int HeadedFor(Vessel vessel, IList<Destination> route)
        {
            for (int i = 1; i < route.Count; i++)
                if (route[i].Body != null && (route[i].Body == vessel.mainBody || KspOrbits.PatchAhead(vessel, route[i].Body) != null)) return i;
            return -1;
        }

        // Whether the vessel is at or past a stop: in a closed orbit of its body near the stop's altitude, or, for a surface stop, round the
        // body it was left from.
        private static bool Passed(Vessel vessel, Destination stop)
        {
            if (stop.Body != vessel.mainBody) return false;
            if (stop.Surface) return true;
            if (vessel.orbit.eccentricity >= 1.0) return false;
            double tolerance = System.Math.Max(2000.0, 0.1 * stop.Altitude);
            return System.Math.Abs(vessel.orbit.PeA - stop.Altitude) <= tolerance && System.Math.Abs(vessel.orbit.ApA - stop.Altitude) <= tolerance;
        }
    }
}
