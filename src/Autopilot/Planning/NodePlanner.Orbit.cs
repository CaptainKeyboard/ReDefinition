using System;
using ReDefinition.Orbits;
using UnityEngine;

namespace ReDefinition.Autopilot.Planning
{
    // The burns that change the orbit round one body, as nodes: a new circular orbit, the capture, and the deorbit, the lift of the periapsis
    // and the rounding of an orbit.
    internal static partial class NodePlanner
    {
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
            double near = Near(altitude);
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

        // The height over the ground the deorbit burn puts the periapsis at: under the ground.
        internal static double DeorbitAltitude(CelestialBody body)
        {
            return -UntargetedPeriapsis * body.Radius;
        }

        // The burn that brings a vessel in a closed orbit down: retrograde at the apoapsis, as much as puts the periapsis below the ground. Without
        // air that is a tenth of the body's radius, as MechJeb's untargeted landing has it. The descent then meets the ground at a shallow angle.
        // Put in as a node.
        internal static Result Deorbit(Vessel vessel)
        {
            return Deorbit(vessel, vessel.mainBody.Radius * (1.0 - UntargetedPeriapsis), "Deorbit");
        }

        // The same burn to a periapsis at another radius, for a pass through the air that lowers the apoapsis, with the name the plan gives it.
        internal static Result Deorbit(Vessel vessel, double periapsisRadius, string name)
        {
            Orbit orbit = vessel.orbit;
            CelestialBody body = vessel.mainBody;
            if (orbit == null || orbit.eccentricity >= 1.0) return Result.Failed("The orbit is not closed.");
            double now = Planetarium.GetUniversalTime();
            double mu = body.gravParameter;
            State here = KspOrbits.StateAt(orbit, now);
            Burn burn;
            if (!Maneuvers.ChangeOpposite(here, mu, true, periapsisRadius, out burn)) return Result.Failed("No burn was found.");
            if (burn.Delay < LeadTime) burn = new Burn(burn.Delay + new Conic(here, mu).Period, burn.DeltaV);
            double ut = now + burn.Delay;
            KspOrbits.ClearNodes(vessel);
            if (KspOrbits.AddNode(vessel, ut, burn.DeltaV) == null) return Result.Failed("The vessel has no flight plan.");
            return new Result { Ok = true, UT = ut, DeltaV = burn.DeltaV, Why = name + ": " + burn.Magnitude.ToString("0") + " m/s retrograde at the apoapsis." };
        }

        // The burn that lifts a periapsis that lies too deep to the height of a pass through the air, for an aerobraking pass that slows the vessel.
        internal static Result LiftToAirPass(Vessel vessel, double passAltitude)
        {
            CelestialBody body = vessel.mainBody;
            if (vessel.orbit == null || vessel.orbit.eccentricity >= 1.0) return Result.Failed("The orbit is not closed.");
            State here = KspOrbits.StateAt(vessel.orbit, Planetarium.GetUniversalTime());
            Result result = LiftPeriapsis(vessel, here, body.gravParameter, body.Radius + passAltitude);
            if (result.Ok) result.Why = "Lift the periapsis to " + (passAltitude / 1000.0).ToString("0.#") + " km, into the air, for the aerobraking pass: " + result.DeltaV.Length.ToString("0") + " m/s.";
            return result;
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
    }
}
