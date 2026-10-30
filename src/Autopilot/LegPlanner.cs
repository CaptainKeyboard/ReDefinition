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
        // How far over the periapsis a deorbit burn aims a periapsis in the air may lie before the burn is made again, in metres.
        private const double DeorbitMargin = 1000.0;

        // Plans the next step of the leg to the first stop the vessel has not reached: a launch, a burn as a node, a descent, or the reason
        // where there is none yet. For the player, who flies the climb and the descent, a deorbit is a node and the landing is guided.
        internal static NodePlanner.Result PlanNext(Vessel vessel, IList<Destination> route, bool forPlayer = false)
        {
            if (vessel == null || route == null || route.Count < 2) return NodePlanner.Result.Failed("There is no route.");
            int last = -1;
            for (int i = 0; i < route.Count; i++)
                if (Passed(vessel, route[i], i)) last = i;
            if (last + 1 >= route.Count) return NodePlanner.Result.Done("The vessel is at the last stop.");
            int next = last >= 0 ? last + 1 : HeadedFor(vessel, route);
            if (next < 0) return NodePlanner.Result.Failed("The vessel is not at a stop of the route, nor on its way to one.");
            Destination stop = route[next];
            if (stop.Body == null) return NodePlanner.Result.Failed("The next stop has no body.");
            bool landing = stop.Surface && stop.Body == vessel.mainBody && !OnTheGround(vessel);
            double aim = stop.Body == vessel.mainBody ? stop.Altitude : LowOrbit(vessel.mainBody);
            bool climbing = vessel.situation == Vessel.Situations.SUB_ORBITAL && vessel.orbit.PeA < 0.0 && vessel.orbit.ApA < 0.98 * aim;
            if (!landing && (OnTheGround(vessel) || vessel.situation == Vessel.Situations.FLYING || climbing))
            {
                if (stop.Body == vessel.mainBody && stop.Surface) return NodePlanner.Result.Failed("A hop on the surface is not planned yet.");
                return NodePlanner.Result.Ascent(aim, "Launch into an orbit " + (aim / 1000.0).ToString("0.#") + " km over " + vessel.mainBody.bodyName + ".");
            }
            if (stop.Surface)
            {
                if (stop.Body == vessel.mainBody)
                {
                    if (vessel.orbit.eccentricity >= 1.0) return NodePlanner.Capture(vessel);
                    bool high = vessel.mainBody.atmosphere ? vessel.orbit.PeA > NodePlanner.DeorbitAltitude(vessel.mainBody) + DeorbitMargin : vessel.orbit.PeA > 0.0;
                    if (forPlayer && high) return NodePlanner.Deorbit(vessel);
                    return NodePlanner.Result.Landing("Land on " + stop.Body.bodyName + ".");
                }
                stop = new Destination { Body = stop.Body, Altitude = LowOrbit(stop.Body) };
            }
            if (stop.Body == vessel.mainBody)
                return vessel.orbit.eccentricity >= 1.0 ? NodePlanner.Capture(vessel) : NodePlanner.ChangeOrbit(vessel, stop.Altitude);
            if (vessel.orbit.eccentricity >= 1.0 && vessel.orbit.patchEndTransition == Orbit.PatchTransitionType.ESCAPE)
                return new NodePlanner.Result { Ok = true, Why = "Leaving " + vessel.mainBody.bodyName + "." };
            CelestialBody here = vessel.mainBody;
            // The sun is its own reference body in KSP and has no orbit to leave on.
            if (here.orbit != null && here.referenceBody != null && here.referenceBody != here
                && (stop.Body == here.referenceBody || stop.Body.referenceBody == here.referenceBody))
                return NodePlanner.Eject(vessel, stop.Body, stop.Altitude);
            if (stop.Body.referenceBody != vessel.mainBody)
                return NodePlanner.Result.Failed("A way from " + vessel.mainBody.bodyName + " to " + stop.Body.bodyName + " is not planned as nodes yet.");
            return KspOrbits.PatchAhead(vessel, stop.Body) != null
                ? NodePlanner.CorrectCourse(vessel, stop.Body, stop.Altitude)
                : NodePlanner.TransferToBody(vessel, stop.Body, stop.Altitude);
        }

        // In metres: a low orbit's height over the atmosphere, and over the highest ground of a body without air.
        private const double LowOrbitMargin = 10000.0;
        private const double TerrainMargin = 5000.0;

        // The altitude of a body's low orbit, which the planner shows for a stop and the autopilot flies where the route gives none. It lies above
        // the atmosphere, or above the highest ground where the body has terrain; a twentieth of the radius otherwise, and the same margin.
        // It is inside the body's sphere of influence, in whole kilometres.
        internal static double LowOrbit(CelestialBody body)
        {
            double altitude;
            if (body.atmosphere)
            {
                altitude = body.atmosphereDepth + LowOrbitMargin;
            }
            else
            {
                double highest = body.pqsController != null ? body.pqsController.radiusMax - body.Radius : 0.0;
                altitude = highest > 0.0 ? highest + TerrainMargin : body.Radius * 0.05 + TerrainMargin;
            }
            if (body.sphereOfInfluence > 0.0 && body.Radius + altitude > body.sphereOfInfluence)
                altitude = System.Math.Max(1000.0, (body.sphereOfInfluence - body.Radius) / 2.0);
            return System.Math.Ceiling(altitude / 1000.0) * 1000.0;
        }

        private static bool OnTheGround(Vessel vessel)
        {
            return vessel.LandedOrSplashed || vessel.situation == Vessel.Situations.PRELAUNCH;
        }

        // The stop the vessel is on its way to, where it is at none: the first after the start whose body it is round, or whose body the orbit
        // shows an encounter with. Where it is leaving a body, the first stop at another; round a sun, the first at one of its planets. -1 for none.
        private static int HeadedFor(Vessel vessel, IList<Destination> route)
        {
            bool leaving = vessel.orbit.eccentricity >= 1.0 && vessel.orbit.patchEndTransition == Orbit.PatchTransitionType.ESCAPE;
            for (int i = 1; i < route.Count; i++)
            {
                CelestialBody body = route[i].Body;
                if (body == null) continue;
                if (body == vessel.mainBody || KspOrbits.PatchAhead(vessel, body) != null) return i;
                if (leaving || body.referenceBody == vessel.mainBody) return i;
            }
            return -1;
        }

        // Whether the vessel is at or past a stop. The first stop, a surface, is left behind wherever the vessel is round its body. A later
        // surface is reached where the vessel stands on it, an orbit where the vessel is on a closed orbit of its body near the altitude.
        private static bool Passed(Vessel vessel, Destination stop, int index)
        {
            if (stop.Body != vessel.mainBody) return false;
            if (stop.Surface) return index == 0 || OnTheGround(vessel);
            if (OnTheGround(vessel) || vessel.orbit.eccentricity >= 1.0) return false;
            double tolerance = System.Math.Max(2000.0, 0.1 * stop.Altitude);
            return System.Math.Abs(vessel.orbit.PeA - stop.Altitude) <= tolerance && System.Math.Abs(vessel.orbit.ApA - stop.Altitude) <= tolerance;
        }
    }
}
