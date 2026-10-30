using System.Collections.Generic;
using ReDefinition.Autopilot.Flight;
using UnityEngine;

namespace ReDefinition.Autopilot.Planning
{
    // The next leg of a route from where the vessel is, planned as nodes. The stop the vessel has reached is found, and the leg to the one
    // after it is handed to the planner that knows how to fly it.
    internal static class LegPlanner
    {
        // Plans the next step of the leg to the first stop the vessel has not reached: a launch, a burn as a node, a descent, or the reason
        // where there is none yet. For the player, who flies the climb and the descent, a deorbit is a node and the landing is guided.
        internal static NodePlanner.Result PlanNext(Frame frame, IList<Destination> route, bool forPlayer = false, bool keepCourse = false)
        {
            string note;
            int goal;
            bool fromStop;
            NodePlanner.Result result = Plan(frame, route, forPlayer, keepCourse, out note, out goal, out fromStop);
            if (result.Goal < 0)
            {
                result.Goal = goal;
                result.FromStop = fromStop;
            }
            if (note != null && result.Ok && result.Why != null) result.Why = note + result.Why;
            return result;
        }

        private static NodePlanner.Result Plan(Frame frame, IList<Destination> route, bool forPlayer, bool keepCourse, out string note, out int goal, out bool fromStop)
        {
            note = null;
            goal = -1;
            fromStop = false;
            if (frame == null || route == null || route.Count < 2) return NodePlanner.Result.Failed("There is no route.");
            int last = -1;
            for (int i = 0; i < route.Count; i++)
                if (Passed(frame, route[i], i)) last = i;
            if (last >= 0 && !frame.Ahead)
            {
                SectionRelease pending = PendingRelease(frame.Vessel, route[last]);
                if (pending != null) return NodePlanner.Result.Release(pending, "Let the " + pending.Name.ToLowerInvariant() + " go: separate it.");
            }
            if (last + 1 >= route.Count) return NodePlanner.Result.Done("The vessel is at the last stop.");
            int next = last >= 0 ? last + 1 : HeadedFor(frame, route);
            if (next < 0) return NodePlanner.Result.Failed("The vessel is not at a stop of the route, nor on its way to one.");
            goal = next;
            fromStop = last >= 0;
            Destination stop = route[next];
            if (stop.Body == null) return NodePlanner.Result.Failed("The next stop has no body.");
            // Where the air would load the vessel beyond what it takes, the stop is reached by burning, as a stop that does not use the air.
            if (!frame.Ahead && (stop.Direct || stop.Aerobrake))
            {
                string refused = AirRefused(frame, stop);
                if (refused != null)
                {
                    note = "The air is not used: " + refused + " ";
                    Debug.Log("[ReDefinition] Route: " + note);
                    stop = new Destination { Body = stop.Body, Surface = stop.Surface, Altitude = stop.Altitude, MeetWith = stop.MeetWith, DockWith = stop.DockWith, Releases = stop.Releases };
                }
            }
            bool landing = stop.Surface && stop.Body == frame.Body && !frame.OnGround;
            double aim = stop.Body == frame.Body ? stop.Altitude : LowOrbit(frame.Body);
            bool climbing = frame.Situation == Vessel.Situations.SUB_ORBITAL && frame.Orbit.PeA < 0.0 && frame.Orbit.ApA < 0.98 * aim;
            if (!landing && (frame.OnGround || frame.Situation == Vessel.Situations.FLYING || climbing))
            {
                if (stop.Body == frame.Body && stop.Surface) return NodePlanner.Result.Failed("A hop on the surface is not planned yet.");
                return NodePlanner.Result.Ascent(aim, "Launch into an orbit " + (aim / 1000.0).ToString("0.#") + " km over " + frame.Body.bodyName + ".");
            }
            if (stop.Surface)
            {
                if (stop.Body == frame.Body)
                {
                    if (frame.Orbit.eccentricity >= 1.0)
                    {
                        if (stop.Direct) return NodePlanner.Result.Landing("Land on " + stop.Body.bodyName + " straight from the approach.");
                        return stop.Aerobrake ? NodePlanner.Result.Aerobrake("Aerobrake: the air of " + stop.Body.bodyName + " slows the vessel at the periapsis.") : NodePlanner.Capture(frame);
                    }
                    // MechJeb's landing starts from an orbit whose periapsis is under the ground (MechJebModuleLandingAutopilot, StartLanding) and
                    // deorbits otherwise.
                    if (forPlayer && frame.Orbit.PeA >= 0.0) return NodePlanner.Deorbit(frame);
                    return NodePlanner.Result.Landing("Land on " + stop.Body.bodyName + ".");
                }
                stop = new Destination { Body = stop.Body, Altitude = LowOrbit(stop.Body), Direct = stop.Direct, Aerobrake = stop.Aerobrake, PassAltitude = stop.PassAltitude };
            }
            if (stop.Body == frame.Body)
            {
                if (frame.Orbit.eccentricity < 1.0)
                {
                    // The last stop is met with a vessel: MechJeb's rendezvous brings the vessel there, from wherever it is in the orbit round the body.
                    Vessel meet = stop.MeetWith != System.Guid.Empty ? MeetingTarget(frame, stop) : null;
                    if (meet != null) return NodePlanner.Result.Rendezvous(meet.id, stop.DockWith, stop.Dock, (stop.Dock ? "Meet and dock with " : "Meet ") + meet.vesselName + ".");
                    if (!stop.Aerobrake || !(frame.Orbit.ApA > stop.Altitude + NodePlanner.Near(stop.Altitude))) return NodePlanner.ChangeOrbit(frame, stop.Altitude);
                    // The orbit is wider than the one asked for: the air is to bring it down, in a pass of the depth the simulation finds. A plan
                    // ahead does not simulate the pass: it ends there.
                    if (frame.Ahead) return NodePlanner.Result.Aerobrake("Aerobrake: the air of " + stop.Body.bodyName + " lowers the apoapsis, in a pass at the periapsis.");
                    string why;
                    double pass = AerobrakePass.FromOrbit(frame.Vessel, stop.Altitude, out why);
                    if (pass < 0.0) return NodePlanner.Result.Failed(why);
                    double tolerance = System.Math.Max(1000.0, 0.03 * pass);
                    if (frame.Orbit.PeA < pass - tolerance) return NodePlanner.LiftToAirPass(frame, pass);
                    if (frame.Orbit.PeA > pass + tolerance && frame.Orbit.PeA >= frame.Body.atmosphereDepth)
                        return NodePlanner.Deorbit(frame, frame.Body.Radius + pass, "Lower the periapsis into the air for the aerobraking pass");
                    return frame.Orbit.PeA < frame.Body.atmosphereDepth
                        ? NodePlanner.Result.Aerobrake("Aerobrake: the air of " + stop.Body.bodyName + " lowers the apoapsis at the periapsis, a pass at " + (pass / 1000.0).ToString("0.#") + " km.")
                        : NodePlanner.ChangeOrbit(frame, stop.Altitude);
                }
                return stop.Aerobrake ? NodePlanner.Result.Aerobrake("Aerobrake: the air of " + stop.Body.bodyName + " slows the vessel at the periapsis.") : NodePlanner.Capture(frame);
            }
            if (frame.Orbit.eccentricity >= 1.0 && frame.Orbit.patchEndTransition == Orbit.PatchTransitionType.ESCAPE)
                return new NodePlanner.Result { Ok = true, Why = "Leaving " + frame.Body.bodyName + "." };
            CelestialBody here = frame.Body;
            // The sun is its own reference body in KSP and has no orbit to leave on.
            if (here.orbit != null && here.referenceBody != null && here.referenceBody != here
                && (stop.Body == here.referenceBody || stop.Body.referenceBody == here.referenceBody))
                return NodePlanner.Eject(frame, stop.Body, Aim(stop));
            if (stop.Body.referenceBody != frame.Body)
                return NodePlanner.Result.Failed("A way from " + frame.Body.bodyName + " to " + stop.Body.bodyName + " is not planned as nodes yet.");
            Orbit encounter = KspOrbits.PatchAhead(frame, stop.Body);
            if (encounter == null) return NodePlanner.TransferToBody(frame, stop.Body, Aim(stop));
            double entry = Aim(stop);
            if (!frame.Ahead && (stop.Direct || stop.Aerobrake))
            {
                // The periapsis to aim at is the depth of a pass the vessel takes, found by simulating it, and none where the air would break it.
                string why;
                entry = AerobrakePass.FromApproach(frame.Vessel, encounter, stop.Direct, out why);
                if (entry < 0.0) return NodePlanner.Result.Failed(why);
            }
            return NodePlanner.CorrectCourse(frame, stop.Body, entry, keepCourse);
        }

        // The vessel the stop is met with, if it is there and round the body the vessel is round; null where it is not, or the meeting is done.
        private static Vessel MeetingTarget(Frame frame, Destination stop)
        {
            Vessel target = FlightGlobals.Vessels.Find(v => v.id == stop.MeetWith);
            if (target == null || target.mainBody != frame.Body) return null;
            // A plan ahead ends at the meeting; the vessel itself is judged by what it has done.
            return !frame.Ahead && Done(frame.Vessel, target, stop) ? null : target;
        }

        // Whether the stop's meeting is done: the vessel is docked with the port, where the stop docks, else it has met the target.
        private static bool Done(Vessel vessel, Vessel target, Destination stop)
        {
            if (stop.DockWith != 0u) return vessel.parts.Exists(p => p.persistentId == stop.DockWith);
            if (stop.Dock) return Docked(vessel);
            return Met(vessel, target);
        }

        // Whether one of the vessel's docking ports is docked: its state says so.
        internal static bool Docked(Vessel vessel)
        {
            return vessel.parts.Exists(p =>
            {
                ModuleDockingNode port = p.FindModuleImplementing<ModuleDockingNode>();
                return port != null && port.state != null && port.state.StartsWith("Docked");
            });
        }

        // Whether the vessel has met the target, as MechJeb's rendezvous counts it: nearer than the distance it was asked for, and nearly at rest to
        // it (MechJebModuleRendezvousAutopilot, Drive). The distance and the speed are those MechJeb's target controller gives.
        internal static bool Met(Vessel vessel, Vessel target)
        {
            double distance, speed;
            if (!MechJebDriver.TargetRange(vessel, out distance, out speed))
            {
                distance = (target.GetWorldPos3D() - vessel.GetWorldPos3D()).magnitude;
                speed = (target.obt_velocity - vessel.obt_velocity).magnitude;
            }
            return distance < MechJebDriver.RendezvousDistance(vessel) * 1.05 + 2.0 && speed < 1.0;
        }

        // The first section of the stop that has not been let go: its separating part is still on the vessel and has not separated.
        private static SectionRelease PendingRelease(Vessel vessel, Destination stop)
        {
            if (stop.Releases == null) return null;
            foreach (SectionRelease release in stop.Releases)
            {
                Part part = release.FindOn(vessel);
                if (part != null && !Separated(part)) return release;
            }
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
        private static string AirRefused(Frame frame, Destination stop)
        {
            if (!PassHeat.ModelHolds) return PassHeat.ModelProblem + ".";
            string why = null;
            Orbit approach = null;
            if (stop.Body == frame.Body) approach = frame.Orbit;
            else if (stop.Body.referenceBody == frame.Body) approach = KspOrbits.PatchAhead(frame, stop.Body);
            if (approach == null) return null;
            if (approach.eccentricity >= 1.0) AerobrakePass.FromApproach(frame.Vessel, approach, stop.Direct, out why);
            else if (stop.Aerobrake && frame.Orbit.ApA > LowOrbitOf(stop) + NodePlanner.Near(LowOrbitOf(stop))) AerobrakePass.FromOrbit(frame.Vessel, LowOrbitOf(stop), out why);
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


        // The stop the vessel is on its way to, where it is at none: the first after the start whose body it is round, or whose body the orbit
        // shows an encounter with. Where it is leaving a body, the first stop at another; round a sun, the first at one of its planets. -1 for none.
        private static int HeadedFor(Frame frame, IList<Destination> route)
        {
            bool leaving = frame.Orbit.eccentricity >= 1.0 && frame.Orbit.patchEndTransition == Orbit.PatchTransitionType.ESCAPE;
            for (int i = 1; i < route.Count; i++)
            {
                CelestialBody body = route[i].Body;
                if (body == null) continue;
                if (body == frame.Body || KspOrbits.PatchAhead(frame, body) != null) return i;
                if (leaving || body.referenceBody == frame.Body) return i;
            }
            return -1;
        }

        // Whether the vessel is at or past a stop. The first stop, a surface, is left behind wherever the vessel is round its body. A later
        // surface is reached where the vessel stands on it, an orbit where the vessel is on a closed orbit of its body near the altitude.
        private static bool Passed(Frame frame, Destination stop, int index)
        {
            if (stop.Body != frame.Body) return false;
            if (stop.Surface) return index == 0 || frame.OnGround;
            if (frame.OnGround || frame.Orbit.eccentricity >= 1.0) return false;
            // A stop that is met with a vessel is reached when they have met, or when the target is gone because they docked.
            if (!frame.Ahead && stop.MeetWith != System.Guid.Empty)
            {
                Vessel target = FlightGlobals.Vessels.Find(v => v.id == stop.MeetWith);
                if (target != null && target.mainBody == frame.Body) return Done(frame.Vessel, target, stop);
                // With the target gone, a docking is done where the port is now a part of the vessel; else the orbit is judged as for any stop.
                if (stop.DockWith != 0u && frame.Vessel.parts.Exists(p => p.persistentId == stop.DockWith)) return true;
                if (stop.Dock && Docked(frame.Vessel)) return true;
            }
            double tolerance = System.Math.Max(2000.0, 0.1 * stop.Altitude);
            return System.Math.Abs(frame.Orbit.PeA - stop.Altitude) <= tolerance && System.Math.Abs(frame.Orbit.ApA - stop.Altitude) <= tolerance;
        }
    }
}
