using System;
using ReDefinition.Orbits;
using UnityEngine;

namespace ReDefinition.Autopilot
{
    // The burns of a leg worked out from the vessel's orbit and put in the flight plan as nodes, each checked against KSP's own patched conics.
    internal static class NodePlanner
    {
        // Without air the deorbit burn goes on until the periapsis is this part of the body's radius under the ground (MechJeb's untargeted landing,
        // UntargetedDeorbit: a tenth).
        private const double UntargetedPeriapsis = 0.1;

        // Where an entry into air has its periapsis, as a part of the air's depth: the vessel meets the air at a shallow angle, which keeps it
        // from overheating. Without air the periapsis is below the ground.
        private const double EntryShare = 0.45;

        // The smallest correction worth a burn, in m/s: at the distance of a planet the periapsis moves by kilometres for less than that.
        private const double MinCorrection = 0.1;

        // The least time between now and a burn, in seconds: room to see the node and turn the vessel.
        internal const double LeadTime = 60.0;

        // What a step of a plan is: a burn as a node, a launch into an orbit, or a descent to the ground.
        internal enum StepKind
        {
            Burn,
            Ascent,
            Landing,
        }

        // What a plan came to: a burn at a time, and why, or the reason there is none. Ok with a time of zero is a plan that needs no burn.
        internal sealed class Result
        {
            public bool Ok;
            public StepKind Kind;
            // The altitude of the orbit an ascent ends in, in metres.
            public double Altitude;
            // True where the route is flown to its last stop, and there is nothing left to plan.
            public bool Arrived;
            public string Why;
            public double UT;
            public V3 DeltaV;

            public static Result Failed(string why)
            {
                return new Result { Why = why };
            }

            public static Result Ascent(double altitude, string why)
            {
                return new Result { Ok = true, Kind = StepKind.Ascent, Altitude = altitude, Why = why };
            }

            public static Result Landing(string why)
            {
                return new Result { Ok = true, Kind = StepKind.Landing, Why = why };
            }

            public static Result Done(string why)
            {
                return new Result { Ok = true, Arrived = true, Why = why };
            }
        }

        // A transfer from the vessel's orbit to a moon or a planet that orbits the same body, put in as a node. It leaves within the next
        // synodic period, aims at a point beside the target and moves that point until KSP's plan shows a prograde encounter with the periapsis
        // asked for, within 3 % or a kilometre. The vessel must be on a closed orbit round the target's parent.
        internal static Result TransferToBody(Vessel vessel, CelestialBody target, double periapsisAltitude)
        {
            Orbit orbit = vessel.orbit;
            CelestialBody parent = vessel.mainBody;
            if (orbit == null || orbit.eccentricity >= 1.0) return Result.Failed("The orbit is not closed.");
            if (target == null || target.referenceBody != parent) return Result.Failed("The target does not orbit " + parent.bodyName + ".");
            double now = Planetarium.GetUniversalTime();
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
                    Orbit patch = Place(vessel, target, ship, goal, mu, now, best, sideways * (reach * side), out leave, out deltaV);
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
            KspOrbits.ClearNodes(vessel);
            return result;
        }

        // A new circular orbit at `altitude` metres, from the vessel's: a burn at the apsis that raises or lowers the opposite one to it, and a
        // burn there that rounds the orbit, both put in as nodes. The vessel must be on a closed orbit that does not already pass the altitude.
        internal static Result ChangeOrbit(Vessel vessel, double altitude)
        {
            Orbit orbit = vessel.orbit;
            CelestialBody body = vessel.mainBody;
            if (orbit == null || orbit.eccentricity >= 1.0) return Result.Failed("The orbit is not closed.");
            double now = Planetarium.GetUniversalTime();
            double mu = body.gravParameter;
            double radius = body.Radius + altitude;
            State here = KspOrbits.StateAt(orbit, now);
            var conic = new Conic(here, mu);
            double near = Math.Max(2000.0, 0.1 * altitude);
            if (Math.Abs(orbit.PeA - altitude) <= near || Math.Abs(orbit.ApA - altitude) <= near) return Round(vessel, here, mu, Math.Abs(orbit.ApA - altitude) < Math.Abs(orbit.PeA - altitude));
            double safe = body.atmosphere ? body.atmosphereDepth + 1000.0 : 1000.0;
            if (orbit.PeA < safe && altitude > orbit.PeA) return LiftPeriapsis(vessel, here, mu, body.Radius + Math.Max(altitude, safe));
            bool raise = radius > conic.RadiusAt(Math.PI);
            Burn first;
            if (!Maneuvers.ChangeOpposite(here, mu, !raise, radius, out first)) return Result.Failed("No burn was found.");
            if (first.Delay < LeadTime) first = new Burn(first.Delay + conic.Period, first.DeltaV);
            double firstUT = now + first.Delay;
            State before = Kepler.Propagate(here, first.Delay, mu);
            var after = new State(before.R, before.V + first.DeltaV);
            Burn second;
            if (!Maneuvers.Circularize(after, mu, raise, out second)) return Result.Failed("No second burn was found.");
            KspOrbits.ClearNodes(vessel);
            if (KspOrbits.AddNode(vessel, firstUT, first.DeltaV) == null) return Result.Failed("The vessel has no flight plan.");
            KspOrbits.AddNode(vessel, firstUT + second.Delay, second.DeltaV);
            double total = first.Magnitude + second.Magnitude;
            return new Result
            {
                Ok = true, UT = firstUT, DeltaV = first.DeltaV,
                Why = "Orbit at " + (altitude / 1000.0).ToString("0.#") + " km: two burns, " + total.ToString("0") + " m/s.",
            };
        }

        // Leaves the body the vessel is round, from a closed orbit, for another: a planet that orbits the same body as this one, or the body
        // this one orbits. The excess speed comes from a transfer between the two orbits, or from the speed that brings the periapsis at the
        // body being returned to down to the aim, retrograde. The burn is put in as a node at the cheapest place on the orbit before the time
        // the vessel must leave the sphere of influence.
        internal static Result Eject(Vessel vessel, CelestialBody target, double aimAltitude)
        {
            Orbit orbit = vessel.orbit;
            CelestialBody here = vessel.mainBody;
            CelestialBody parent = here.referenceBody;
            if (orbit == null || orbit.eccentricity >= 1.0) return Result.Failed("The orbit is not closed.");
            if (parent == null || (target != parent && target.referenceBody != parent)) return Result.Failed(target.bodyName + " is not reached from " + here.bodyName + " by leaving it.");
            double now = Planetarium.GetUniversalTime();
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
            KspOrbits.ClearNodes(vessel);
            if (KspOrbits.AddNode(vessel, ut, burn.DeltaV) == null) return Result.Failed("The vessel has no flight plan.");
            if (KspOrbits.PatchAround(vessel, parent) == null)
            {
                KspOrbits.ClearNodes(vessel);
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
        internal static Result CorrectCourse(Vessel vessel, CelestialBody target, double aimAltitude)
        {
            KspOrbits.ClearNodes(vessel);
            Orbit patch = KspOrbits.PatchAhead(vessel, target);
            if (patch == null) return Result.Failed("The plan has no encounter with " + target.bodyName + ".");
            double aim = target.Radius + aimAltitude;
            double tolerance = Math.Max(1000.0, 0.03 * aimAltitude);
            string over = (patch.PeR - target.Radius).ToString("0") + " m over " + target.bodyName + ".";
            if (Math.Abs(patch.PeR - aim) <= tolerance) return new Result { Ok = true, Why = "On course, periapsis " + over };
            double at = Planetarium.GetUniversalTime() + 2.0 * LeadTime;
            if (at >= vessel.orbit.EndUT) return Result.Failed("The encounter is too close for a correction.");
            PatchedConicSolver solver = vessel.patchedConicSolver;
            ManeuverNode node = solver.AddManeuverNode(at);
            Vector3d burn = Vector3d.zero;
            double periapsis = PeriapsisWith(vessel, node, target, burn);
            for (int pass = 0; pass < 8 && !double.IsNaN(periapsis) && Math.Abs(periapsis - aim) > 0.5 * tolerance; pass++)
            {
                const double probe = 0.2;
                var slope = new Vector3d(0.0, 0.0, 0.0);
                for (int axis = 0; axis < 3; axis++)
                {
                    Vector3d nudged = burn;
                    nudged[axis] += probe;
                    double other = PeriapsisWith(vessel, node, target, nudged);
                    if (!double.IsNaN(other)) slope[axis] = (other - periapsis) / probe;
                }
                if (slope.sqrMagnitude < 1e-12) break;
                Vector3d step = slope * ((aim - periapsis) / slope.sqrMagnitude);
                if (step.magnitude > 20.0) step = step.normalized * 20.0;
                burn += step;
                periapsis = PeriapsisWith(vessel, node, target, burn);
            }
            if (double.IsNaN(periapsis) || Math.Abs(periapsis - aim) > tolerance)
            {
                KspOrbits.ClearNodes(vessel);
                return Result.Failed("No correction was found that brings the periapsis to " + aimAltitude.ToString("0") + " m.");
            }
            if (burn.magnitude < MinCorrection)
            {
                KspOrbits.ClearNodes(vessel);
                return new Result { Ok = true, Why = "On course, a correction of " + burn.magnitude.ToString("0.00") + " m/s is not worth a burn. Periapsis " + over };
            }
            return new Result
            {
                Ok = true, UT = at, DeltaV = KspOrbits.ToV3(burn),
                Why = "Correction of " + burn.magnitude.ToString("0.0") + " m/s, periapsis " + (periapsis - target.Radius).ToString("0") + " m over " + target.bodyName + ".",
            };
        }

        // The periapsis at the target that KSP's plan shows with the node given that burn (in the node's axes); NaN where the plan has no encounter.
        private static double PeriapsisWith(Vessel vessel, ManeuverNode node, CelestialBody target, Vector3d burn)
        {
            node.DeltaV = burn;
            vessel.patchedConicSolver.UpdateFlightPlan();
            Orbit patch = KspOrbits.PatchAround(vessel, target);
            return patch != null ? patch.PeR : double.NaN;
        }

        // The burn that rounds a vessel on an open orbit round the body into a circle at its periapsis, put in as a node there: the capture, with
        // the periapsis chosen when the transfer was made.
        internal static Result Capture(Vessel vessel)
        {
            Orbit orbit = vessel.orbit;
            CelestialBody body = vessel.mainBody;
            if (orbit == null || orbit.eccentricity < 1.0) return Result.Failed("The orbit is not open.");
            double toPeriapsis = orbit.timeToPe;
            if (toPeriapsis < LeadTime) return Result.Failed("The periapsis is past or too close for a burn.");
            double ut = Planetarium.GetUniversalTime() + toPeriapsis;
            State at = KspOrbits.StateAt(orbit, ut);
            double speed = at.V.Length;
            double circular = Math.Sqrt(body.gravParameter / at.R.Length);
            V3 deltaV = at.V.Unit * (circular - speed);
            KspOrbits.ClearNodes(vessel);
            if (KspOrbits.AddNode(vessel, ut, deltaV) == null) return Result.Failed("The vessel has no flight plan.");
            return new Result
            {
                Ok = true, UT = ut, DeltaV = deltaV,
                Why = "Capture at " + body.bodyName + ": " + deltaV.Length.ToString("0") + " m/s at the periapsis, " + ((at.R.Length - body.Radius) / 1000.0).ToString("0.#") + " km up.",
            };
        }

        // The height over the ground the deorbit burn puts the periapsis at: in the air, a share of its depth; without air, under the ground.
        internal static double DeorbitAltitude(CelestialBody body)
        {
            return body.atmosphere ? EntryShare * body.atmosphereDepth : -UntargetedPeriapsis * body.Radius;
        }

        // The burn that brings a vessel in a closed orbit down: retrograde at the apoapsis, as much as puts the periapsis below the ground. Without
        // air that is a tenth of the body's radius, as MechJeb's untargeted landing has it. The descent then meets the ground at a shallow angle.
        // Put in as a node.
        internal static Result Deorbit(Vessel vessel)
        {
            Orbit orbit = vessel.orbit;
            CelestialBody body = vessel.mainBody;
            if (orbit == null || orbit.eccentricity >= 1.0) return Result.Failed("The orbit is not closed.");
            double now = Planetarium.GetUniversalTime();
            double mu = body.gravParameter;
            State here = KspOrbits.StateAt(orbit, now);
            Burn burn;
            double lowest = body.atmosphere ? body.Radius + EntryShare * body.atmosphereDepth : body.Radius * (1.0 - UntargetedPeriapsis);
            if (!Maneuvers.ChangeOpposite(here, mu, true, lowest, out burn)) return Result.Failed("No burn was found.");
            if (burn.Delay < LeadTime) burn = new Burn(burn.Delay + new Conic(here, mu).Period, burn.DeltaV);
            double ut = now + burn.Delay;
            KspOrbits.ClearNodes(vessel);
            if (KspOrbits.AddNode(vessel, ut, burn.DeltaV) == null) return Result.Failed("The vessel has no flight plan.");
            return new Result { Ok = true, UT = ut, DeltaV = burn.DeltaV, Why = "Deorbit: " + burn.Magnitude.ToString("0") + " m/s retrograde at the apoapsis." };
        }

        // The burn that lifts a periapsis that lies in the ground or the air, soon: a burn along the velocity a little ahead, as big as brings the
        // periapsis to `radius`. Waiting for the apoapsis could mean passing the periapsis first.
        private static Result LiftPeriapsis(Vessel vessel, State here, double mu, double radius)
        {
            double delay = 2.0 * LeadTime;
            State there = Kepler.Propagate(here, delay, mu);
            double low = 0.0, high = 4000.0;
            for (int i = 0; i < 60; i++)
            {
                double mid = 0.5 * (low + high);
                var after = new State(there.R, there.V + there.V.Unit * mid);
                var conic = new Conic(after, mu);
                double periapsis = conic.Closed ? conic.RadiusAt(0.0) : double.PositiveInfinity;
                if (periapsis < radius) low = mid;
                else high = mid;
            }
            V3 deltaV = there.V.Unit * (0.5 * (low + high));
            double ut = Planetarium.GetUniversalTime() + delay;
            KspOrbits.ClearNodes(vessel);
            if (KspOrbits.AddNode(vessel, ut, deltaV) == null) return Result.Failed("The vessel has no flight plan.");
            return new Result { Ok = true, UT = ut, DeltaV = deltaV, Why = "Lift the periapsis out of the ground: " + deltaV.Length.ToString("0") + " m/s." };
        }

        // The second burn of a change of orbit, or the whole of one that is one apsis from done: the orbit rounded at the apsis at the altitude.
        private static Result Round(Vessel vessel, State here, double mu, bool atApoapsis)
        {
            Burn burn;
            if (!Maneuvers.Circularize(here, mu, atApoapsis, out burn)) return Result.Failed("No burn was found.");
            if (burn.Delay < LeadTime) burn = new Burn(burn.Delay + new Conic(here, mu).Period, burn.DeltaV);
            double ut = Planetarium.GetUniversalTime() + burn.Delay;
            KspOrbits.ClearNodes(vessel);
            if (KspOrbits.AddNode(vessel, ut, burn.DeltaV) == null) return Result.Failed("The vessel has no flight plan.");
            return new Result { Ok = true, UT = ut, DeltaV = burn.DeltaV, Why = "Round the orbit at the " + (atApoapsis ? "apoapsis" : "periapsis") + ": " + burn.Magnitude.ToString("0") + " m/s." };
        }

        // One try: a transfer that aims at the point `offset` from the target, put in as the vessel's only node. The patch of the plan round
        // the target, or null where the plan shows no encounter.
        private static Orbit Place(Vessel vessel, CelestialBody target, State ship, State goal, double mu, double now, Transfer found, V3 offset,
                                   out double leave, out V3 deltaV)
        {
            leave = now + found.Delay;
            deltaV = new V3(0.0, 0.0, 0.0);
            State leaving = Kepler.Propagate(ship, found.Delay, mu);
            State targetThen = Kepler.Propagate(goal, found.Delay + found.FlightTime, mu);
            V3 v1, v2;
            if (!Lambert.Solve(leaving.R, targetThen.R + offset, found.FlightTime, mu, true, V3.Cross(ship.R, ship.V), out v1, out v2)) return null;
            deltaV = v1 - leaving.V;
            KspOrbits.ClearNodes(vessel);
            if (KspOrbits.AddNode(vessel, leave, deltaV) == null) return null;
            return KspOrbits.PatchAround(vessel, target);
        }
    }
}
