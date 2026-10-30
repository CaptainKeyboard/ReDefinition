using System;
using ReDefinition.Orbits;
using UnityEngine;

namespace ReDefinition.Autopilot.Planning
{
    // The burns of a way from one body to another, as nodes: the transfer to a moon or a planet, the ejection from a body, and the
    // correction of the course, each checked against KSP's own patched conics.
    internal static partial class NodePlanner
    {
        // A transfer from the vessel's orbit to a moon or a planet that orbits the same body, put in as a node. It leaves within the next
        // synodic period, aims at a point beside the target and moves that point until KSP's plan shows a prograde encounter with the periapsis
        // asked for, within 3 % or a kilometre. The vessel must be on a closed orbit round the target's parent.
        internal static Result TransferToBody(Frame frame, CelestialBody target, double periapsisAltitude)
        {
            Orbit orbit = frame.Orbit;
            CelestialBody parent = frame.Body;
            if (orbit == null || orbit.eccentricity >= 1.0) return Result.Failed("The orbit is not closed.");
            if (target == null || target.referenceBody != parent) return Result.Failed("The target does not orbit " + parent.bodyName + ".");
            double now = frame.Now;
            double mu = parent.gravParameter;
            State ship = KspOrbits.StateAt(orbit, now);
            State goal = KspOrbits.StateAt(target.orbit, now);
            double semiMajor = 0.5 * (ship.R.Length + goal.R.Length);
            double hohmann = Math.PI * Math.Sqrt(semiMajor * semiMajor * semiMajor / mu);
            double synodic = 1.0 / Math.Abs(1.0 / orbit.period - 1.0 / target.orbit.period);
            double maxDelay = Math.Min(synodic * 1.2, 20.0 * parent.rotationPeriod);

            Transfer best;
            if (!Transfers.Cheapest(ship, goal, mu, maxDelay, 0.5 * hohmann, 1.5 * hohmann, false, out best, LeadTime))
                return Result.Failed("No transfer was found.");
            double aim = target.Radius + periapsisAltitude;
            double excess = best.Arrival.Length;
            double impact = aim * Math.Sqrt(1.0 + 2.0 * target.gravParameter / (aim * excess * excess));
            V3 sideways = V3.Cross(V3.Cross(ship.R, ship.V).Unit, best.Arrival.Unit).Unit;
            Result result = Result.Failed("No encounter with " + target.bodyName + " came out of the transfer.");
            foreach (double side in new[] { 1.0, -1.0 })
            {
                double reach = impact;
                for (int pass = 0; pass < 8; pass++)
                {
                    double leave;
                    V3 deltaV;
                    Orbit patch = Place(frame, target, ship, goal, mu, now, best, sideways * (reach * side), out leave, out deltaV);
                    if (patch == null || patch.inclination > 90.0)
                    {
                        result = Result.Failed("No prograde encounter with " + target.bodyName + ".");
                        break;
                    }
                    double error = aim - patch.PeR;
                    if (Math.Abs(error) <= Math.Max(1000.0, 0.03 * periapsisAltitude))
                        return new Result
                        {
                            Ok = true, UT = leave, DeltaV = deltaV,
                            Why = "Transfer to " + target.bodyName + ", periapsis " + (patch.PeR - target.Radius).ToString("0") + " m over it.",
                        };
                    double speedSquared = -target.gravParameter / patch.semiMajorAxis;
                    double rp = Math.Max(1.0, patch.PeR);
                    reach = Math.Max(1.0, reach + error * (rp + target.gravParameter / speedSquared) / reach);
                    result = Result.Failed("The periapsis at " + target.bodyName + " is " + (patch.PeR - target.Radius).ToString("0") + " m, not " + periapsisAltitude.ToString("0") + ".");
                }
            }
            KspOrbits.ClearNodes(frame);
            return result;
        }

        // Leaves the body the vessel is round, from a closed orbit, for another: a planet that orbits the same body as this one, or the body
        // this one orbits. The excess speed comes from a transfer between the two orbits, or from the speed that brings the periapsis at the
        // body being returned to down to the aim, retrograde. The burn is put in as a node at the cheapest place on the orbit before the time
        // the vessel must leave the sphere of influence.
        internal static Result Eject(Frame frame, CelestialBody target, double aimAltitude)
        {
            Orbit orbit = frame.Orbit;
            CelestialBody here = frame.Body;
            CelestialBody parent = here.referenceBody;
            if (orbit == null || orbit.eccentricity >= 1.0) return Result.Failed("The orbit is not closed.");
            if (parent == null || (target != parent && target.referenceBody != parent)) return Result.Failed(target.bodyName + " is not reached from " + here.bodyName + " by leaving it.");
            double now = frame.Now;
            double mu = here.gravParameter;
            State ship = KspOrbits.StateAt(orbit, now);
            double period = orbit.period;
            State orbiting = KspOrbits.StateAt(here.orbit, now);
            double leaveGuess = Ejection.TimeToLeave(new State(ship.R, ship.V * 1.5), mu, here.sphereOfInfluence);
            if (double.IsInfinity(leaveGuess)) return Result.Failed("The sphere of influence of " + here.bodyName + " is not left.");
            double earliest = leaveGuess + period + LeadTime;
            V3 excess;
            double depart;
            if (target == parent)
            {
                double apoapsis = orbiting.R.Length;
                double periapsis = target.Radius + aimAltitude;
                double slow = Math.Sqrt(target.gravParameter * (2.0 / apoapsis - 2.0 / (apoapsis + periapsis)));
                double speed = orbiting.V.Length - slow;
                if (speed <= 0.0) return Result.Failed("No burn brings the periapsis down to " + aimAltitude.ToString("0") + " m.");
                excess = orbiting.V.Unit * -speed;
                depart = earliest;
            }
            else
            {
                State goal = KspOrbits.StateAt(target.orbit, now);
                double semiMajor = 0.5 * (orbiting.R.Length + goal.R.Length);
                double hohmann = Math.PI * Math.Sqrt(semiMajor * semiMajor * semiMajor / parent.gravParameter);
                double synodic = 1.0 / Math.Abs(1.0 / here.orbit.period - 1.0 / target.orbit.period);
                Transfer best;
                if (!Transfers.Cheapest(orbiting, goal, parent.gravParameter, synodic * 1.1, 0.5 * hohmann, 1.5 * hohmann, false, out best, earliest))
                    return Result.Failed("No transfer to " + target.bodyName + " was found.");
                excess = best.Departure;
                depart = best.Delay;
            }
            Burn burn = Ejection.Cheapest(ship, mu, excess, depart - leaveGuess - period, period);
            if (burn.Delay < LeadTime) return Result.Failed("The burn would come too soon.");
            double ut = now + burn.Delay;
            KspOrbits.ClearNodes(frame);
            if (KspOrbits.AddNode(frame, ut, burn.DeltaV) == null) return Result.Failed("The vessel has no flight plan.");
            if (KspOrbits.PatchAround(frame, parent) == null)
            {
                KspOrbits.ClearNodes(frame);
                return Result.Failed("The burn does not leave " + here.bodyName + ".");
            }
            return new Result
            {
                Ok = true, UT = ut, DeltaV = burn.DeltaV,
                Why = "Leave " + here.bodyName + " for " + target.bodyName + ": " + burn.Magnitude.ToString("0") + " m/s, excess speed " + excess.Length.ToString("0") + " m/s.",
            };
        }

        // Brings a transfer back to its aim with one small burn, early on the way. The burn is tried against KSP's own plan until the periapsis
        // at the target is within 3 % or a kilometre of the one asked for. No node where the vessel is on course already.
        internal static Result CorrectCourse(Frame frame, CelestialBody target, double aimAltitude, bool keepCourse = false)
        {
            KspOrbits.ClearNodes(frame);
            Orbit patch = KspOrbits.PatchAhead(frame, target);
            if (patch == null) return Result.Failed("The plan has no encounter with " + target.bodyName + ".");
            double aim = target.Radius + aimAltitude;
            double tolerance = Math.Max(1000.0, 0.03 * aimAltitude);
            string over = (patch.PeR - target.Radius).ToString("0") + " m over " + target.bodyName + ".";
            if (Math.Abs(patch.PeR - aim) <= tolerance) return new Result { Ok = true, Why = "On course, periapsis " + over };
            if (keepCourse) return new Result { Ok = true, Why = "The course is kept, periapsis " + over };
            double at = frame.Now + 2.0 * LeadTime;
            if (at >= frame.Orbit.EndUT) return Result.Failed("The encounter is too close for a correction.");
            PatchedConicSolver solver = frame.Solver;
            ManeuverNode node = solver.AddManeuverNode(at);
            Vector3d burn = Vector3d.zero;
            double periapsis = PeriapsisWith(frame, node, target, burn);
            for (int pass = 0; pass < 8 && !double.IsNaN(periapsis) && Math.Abs(periapsis - aim) > 0.5 * tolerance; pass++)
            {
                const double probe = 0.2;
                var slope = new Vector3d(0.0, 0.0, 0.0);
                for (int axis = 0; axis < 3; axis++)
                {
                    Vector3d nudged = burn;
                    nudged[axis] += probe;
                    double other = PeriapsisWith(frame, node, target, nudged);
                    if (!double.IsNaN(other)) slope[axis] = (other - periapsis) / probe;
                }
                if (slope.sqrMagnitude < 1e-12) break;
                Vector3d step = slope * ((aim - periapsis) / slope.sqrMagnitude);
                if (step.magnitude > 20.0) step = step.normalized * 20.0;
                burn += step;
                periapsis = PeriapsisWith(frame, node, target, burn);
            }
            if (double.IsNaN(periapsis) || Math.Abs(periapsis - aim) > tolerance)
            {
                KspOrbits.ClearNodes(frame);
                return Result.Failed("No correction was found that brings the periapsis to " + aimAltitude.ToString("0") + " m.");
            }
            return new Result
            {
                Ok = true, UT = at, DeltaV = KspOrbits.ToV3(burn), Correction = true, Error = Math.Abs(patch.PeR - aim),
                Why = "Correction of " + burn.magnitude.ToString("0.0") + " m/s, periapsis " + (periapsis - target.Radius).ToString("0") + " m over " + target.bodyName + ".",
            };
        }

        // The periapsis at the target that KSP's plan shows with the node given that burn (in the node's axes); NaN where the plan has no encounter.
        private static double PeriapsisWith(Frame frame, ManeuverNode node, CelestialBody target, Vector3d burn)
        {
            node.DeltaV = burn;
            frame.Solver.UpdateFlightPlan();
            Orbit patch = KspOrbits.PatchAround(frame, target);
            return patch != null ? patch.PeR : double.NaN;
        }

        // One try: a transfer that aims at the point `offset` from the target, put in as the vessel's only node. The patch of the plan round
        // the target, or null where the plan shows no encounter.
        private static Orbit Place(Frame frame, CelestialBody target, State ship, State goal, double mu, double now, Transfer found, V3 offset,
                                   out double leave, out V3 deltaV)
        {
            leave = now + found.Delay;
            deltaV = new V3(0.0, 0.0, 0.0);
            State leaving = Kepler.Propagate(ship, found.Delay, mu);
            State targetThen = Kepler.Propagate(goal, found.Delay + found.FlightTime, mu);
            V3 v1, v2;
            if (!Lambert.Solve(leaving.R, targetThen.R + offset, found.FlightTime, mu, true, V3.Cross(ship.R, ship.V), out v1, out v2)) return null;
            deltaV = v1 - leaving.V;
            KspOrbits.ClearNodes(frame);
            if (KspOrbits.AddNode(frame, leave, deltaV) == null) return null;
            return KspOrbits.PatchAround(frame, target);
        }
    }
}
