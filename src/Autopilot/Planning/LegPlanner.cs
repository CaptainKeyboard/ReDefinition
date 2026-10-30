using System.Collections.Generic;
using UnityEngine;

namespace ReDefinition.Autopilot.Planning
{
    // The next leg of a route from where the vessel is, planned as nodes. The stop the vessel has reached is found, and the leg to the one
    // after it is handed to the planner that knows how to fly it.
    internal static class LegPlanner
    {
        // Plans the next step of the leg to the first stop the vessel has not reached: a launch, a burn as a node, a descent, or the reason
        // where there is none yet. For the player, who flies the climb and the descent, a deorbit is a node and the landing is guided.
        internal static NodePlanner.Result PlanNext(Vessel vessel, IList<Destination> route, bool forPlayer = false, bool keepCourse = false)
        {
            string note;
            NodePlanner.Result result = Plan(vessel, route, forPlayer, keepCourse, out note);
            if (note != null && result.Ok && result.Why != null) result.Why = note + result.Why;
            return result;
        }

        private static NodePlanner.Result Plan(Vessel vessel, IList<Destination> route, bool forPlayer, bool keepCourse, out string note)
        {
            note = null;
            if (vessel == null || route == null || route.Count < 2) return NodePlanner.Result.Failed("There is no route.");
            int last = -1;
            for (int i = 0; i < route.Count; i++)
                if (Passed(vessel, route[i], i)) last = i;
            if (last >= 0)
            {
                SectionRelease pending = PendingRelease(vessel, route[last]);
                if (pending != null) return NodePlanner.Result.Release(pending, "Let the " + pending.Name.ToLowerInvariant() + " go: separate it.");
            }
            if (last + 1 >= route.Count) return NodePlanner.Result.Done("The vessel is at the last stop.");
            int next = last >= 0 ? last + 1 : HeadedFor(vessel, route);
            if (next < 0) return NodePlanner.Result.Failed("The vessel is not at a stop of the route, nor on its way to one.");
            Destination stop = route[next];
            if (stop.Body == null) return NodePlanner.Result.Failed("The next stop has no body.");
            // Where the air would load the vessel beyond what it takes, the stop is reached by burning, as a stop that does not use the air.
            if (stop.Direct || stop.Aerobrake)
            {
                string refused = AirRefused(vessel, stop);
                if (refused != null)
                {
                    note = "The air is not used: " + refused + " ";
                    Debug.Log("[ReDefinition] Route: " + note);
                    stop = new Destination { Body = stop.Body, Surface = stop.Surface, Altitude = stop.Altitude };
                }
            }
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
                    if (vessel.orbit.eccentricity >= 1.0)
                    {
                        if (stop.Direct) return NodePlanner.Result.Landing("Land on " + stop.Body.bodyName + " straight from the approach.");
                        return stop.Aerobrake ? NodePlanner.Result.Aerobrake("Aerobrake: the air of " + stop.Body.bodyName + " slows the vessel at the periapsis.") : NodePlanner.Capture(vessel);
                    }
                    // MechJeb's landing starts from an orbit whose periapsis is under the ground (MechJebModuleLandingAutopilot, StartLanding) and
                    // deorbits otherwise.
                    if (forPlayer && vessel.orbit.PeA >= 0.0) return NodePlanner.Deorbit(vessel);
                    return NodePlanner.Result.Landing("Land on " + stop.Body.bodyName + ".");
                }
                stop = new Destination { Body = stop.Body, Altitude = LowOrbit(stop.Body), Direct = stop.Direct, Aerobrake = stop.Aerobrake, PassAltitude = stop.PassAltitude };
            }
            if (stop.Body == vessel.mainBody)
            {
                if (vessel.orbit.eccentricity < 1.0)
                {
                    if (!stop.Aerobrake || !(vessel.orbit.ApA > stop.Altitude + NodePlanner.Near(stop.Altitude))) return NodePlanner.ChangeOrbit(vessel, stop.Altitude);
                    // The orbit is wider than the one asked for: the air is to bring it down, in a pass of the depth the simulation finds.
                    string why;
                    double pass = AerobrakePass.FromOrbit(vessel, stop.Altitude, out why);
                    if (pass < 0.0) return NodePlanner.Result.Failed(why);
                    double tolerance = System.Math.Max(1000.0, 0.03 * pass);
                    if (vessel.orbit.PeA < pass - tolerance) return NodePlanner.LiftToAirPass(vessel, pass);
                    if (vessel.orbit.PeA > pass + tolerance && vessel.orbit.PeA >= vessel.mainBody.atmosphereDepth)
                        return NodePlanner.Deorbit(vessel, vessel.mainBody.Radius + pass, "Lower the periapsis into the air for the aerobraking pass");
                    return vessel.orbit.PeA < vessel.mainBody.atmosphereDepth
                        ? NodePlanner.Result.Aerobrake("Aerobrake: the air of " + stop.Body.bodyName + " lowers the apoapsis at the periapsis, a pass at " + (pass / 1000.0).ToString("0.#") + " km.")
                        : NodePlanner.ChangeOrbit(vessel, stop.Altitude);
                }
                return stop.Aerobrake ? NodePlanner.Result.Aerobrake("Aerobrake: the air of " + stop.Body.bodyName + " slows the vessel at the periapsis.") : NodePlanner.Capture(vessel);
            }
            if (vessel.orbit.eccentricity >= 1.0 && vessel.orbit.patchEndTransition == Orbit.PatchTransitionType.ESCAPE)
                return new NodePlanner.Result { Ok = true, Why = "Leaving " + vessel.mainBody.bodyName + "." };
            CelestialBody here = vessel.mainBody;
            // The sun is its own reference body in KSP and has no orbit to leave on.
            if (here.orbit != null && here.referenceBody != null && here.referenceBody != here
                && (stop.Body == here.referenceBody || stop.Body.referenceBody == here.referenceBody))
                return NodePlanner.Eject(vessel, stop.Body, Aim(stop));
            if (stop.Body.referenceBody != vessel.mainBody)
                return NodePlanner.Result.Failed("A way from " + vessel.mainBody.bodyName + " to " + stop.Body.bodyName + " is not planned as nodes yet.");
            Orbit encounter = KspOrbits.PatchAhead(vessel, stop.Body);
            if (encounter == null) return NodePlanner.TransferToBody(vessel, stop.Body, Aim(stop));
            double entry = Aim(stop);
            if (stop.Direct || stop.Aerobrake)
            {
                // The periapsis to aim at is the depth of a pass the vessel takes, found by simulating it, and none where the air would break it.
                string why;
                entry = AerobrakePass.FromApproach(vessel, encounter, stop.Direct, out why);
                if (entry < 0.0) return NodePlanner.Result.Failed(why);
            }
            return NodePlanner.CorrectCourse(vessel, stop.Body, entry, keepCourse);
        }

        // The first section of the stop that has not been let go: its separating part is still on the vessel and has not separated.
        private static SectionRelease PendingRelease(Vessel vessel, Destination stop)
        {
            if (stop.Releases == null) return null;
            foreach (SectionRelease release in stop.Releases)
                foreach (Part part in vessel.parts)
                    if (part.persistentId == release.SeparatorId && !Separated(part)) return release;
            return null;
        }

        // Whether the part has separated: a decoupler that has fired, or a docking port that has undocked.
        private static bool Separated(Part part)
        {
            foreach (ModuleDecouplerBase decoupler in part.FindModulesImplementing<ModuleDecouplerBase>())
                if (!decoupler.isDecoupled) return false;
            foreach (ModuleDockingNode port in part.FindModulesImplementing<ModuleDockingNode>())
                if (port.state != "Ready" && port.state != "Disengage") return false;
            return true;
        }

        // Why the air cannot be used to arrive at the stop, or null where it can. Null as well where there is not yet an approach to judge it by.
        // The judge is the pass the vessel would take on the approach, or from the orbit it is in: it must not load the vessel beyond its limits.
        private static string AirRefused(Vessel vessel, Destination stop)
        {
            if (!PassHeat.ModelHolds) return PassHeat.ModelProblem + ".";
            string why = null;
            Orbit approach = null;
            if (stop.Body == vessel.mainBody) approach = vessel.orbit;
            else if (stop.Body.referenceBody == vessel.mainBody) approach = KspOrbits.PatchAhead(vessel, stop.Body);
            if (approach == null) return null;
            if (approach.eccentricity >= 1.0) AerobrakePass.FromApproach(vessel, approach, stop.Direct, out why);
            else if (stop.Aerobrake && vessel.orbit.ApA > LowOrbitOf(stop) + NodePlanner.Near(LowOrbitOf(stop))) AerobrakePass.FromOrbit(vessel, LowOrbitOf(stop), out why);
            return why;
        }

        // The altitude of the orbit the stop is reached in: its own, or the body's low orbit for a landing.
        private static double LowOrbitOf(Destination stop)
        {
            return stop.Surface ? LowOrbit(stop.Body) : stop.Altitude;
        }

        // The height over the body the way across aims its periapsis at: the pass through the air where the arrival uses it, else the orbit.
        private static double Aim(Destination stop)
        {
            return stop.PassAltitude > 0.0 ? stop.PassAltitude : stop.Altitude;
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
