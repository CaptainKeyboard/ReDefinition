using System;
using ReDefinition.Orbits;
using UnityEngine;

namespace ReDefinition.Autopilot
{
    // The burns of a leg worked out from the vessel's orbit and put in the flight plan as nodes, each checked against KSP's own patched conics.
    internal static class NodePlanner
    {
        // What a plan came to: a burn at a time, and why, or the reason there is none.
        internal sealed class Result
        {
            public bool Ok;
            public string Why;
            public double UT;
            public V3 DeltaV;

            public static Result Failed(string why)
            {
                return new Result { Why = why };
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
            if (!Transfers.Cheapest(ship, goal, mu, maxDelay, 0.5 * hohmann, 1.5 * hohmann, false, out best))
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
