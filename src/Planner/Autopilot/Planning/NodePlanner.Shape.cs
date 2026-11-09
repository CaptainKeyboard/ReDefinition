using System;
using ReDefinition.Planner.Orbits;
using UnityEngine;

namespace ReDefinition.Planner.Autopilot.Planning
{
    // The burns that give an orbit a shape the player asked for: another apoapsis, another periapsis, another inclination, or the plane of another
    // vessel. One burn is planned each time; the flight plans again from the orbit it left, so that the steps follow each other.
    internal static partial class NodePlanner
    {
        // How far the plane may lie off the asked one and still count as it, in degrees.
        internal const double PlaneTolerance = 0.5;

        // The apoapsis the stop asks for, in metres over the ground: its own, or the altitude where the orbit is to be round.
        internal static double ApoapsisOf(Destination stop)
        {
            return stop.Apoapsis > stop.Altitude ? stop.Apoapsis : stop.Altitude;
        }

        // Whether the stop asks for more than a round orbit at its altitude.
        internal static bool Shaped(Destination stop)
        {
            return stop.Apoapsis > stop.Altitude || stop.HasInclination || stop.MatchPlaneWith != Guid.Empty;
        }

        // Whether both apsides are where the stop asks for them.
        internal static bool ApsidesMatch(Orbit orbit, Destination stop)
        {
            double apoapsis = ApoapsisOf(stop);
            return Math.Abs(orbit.PeA - stop.Altitude) <= Near(stop.Altitude) && Math.Abs(orbit.ApA - apoapsis) <= Near(apoapsis);
        }

        // The plane the stop asks for is the orbit's: the inclination is the asked one, and the plane is the target's where the stop gives a vessel.
        internal static bool PlaneMatches(Orbit orbit, Destination stop)
        {
            if (stop.HasInclination && Math.Abs(orbit.inclination - stop.Inclination) > PlaneTolerance) return false;
            if (stop.MatchPlaneWith != Guid.Empty)
            {
                Vessel target = FlightGlobals.Vessels.Find(v => v.id == stop.MatchPlaneWith);
                if (target != null && target.orbit != null && target.orbit.referenceBody == orbit.referenceBody && target.orbit.eccentricity < 1.0)
                    return Vector3d.Angle(orbit.GetOrbitNormal(), target.orbit.GetOrbitNormal()) <= PlaneTolerance;
            }
            return true;
        }

        // The next burn towards the orbit the stop asks for: the plane first, then the apsides. A stop that asks for nothing but its altitude is the
        // change to a round orbit.
        internal static Result ShapeOrbit(Frame frame, Destination stop)
        {
            if (!Shaped(stop)) return ChangeOrbit(frame, stop.Altitude);
            Orbit orbit = frame.Orbit;
            if (orbit == null || orbit.eccentricity >= 1.0) return Result.Failed("The orbit is not closed.");
            if (!PlaneMatches(orbit, stop)) return TiltOrbit(frame, stop);
            if (!ApsidesMatch(orbit, stop)) return ShapeApsides(frame, stop);
            return Result.Failed("The orbit already is the one asked for.");
        }

        // One burn towards the periapsis and the apoapsis asked for: the apoapsis is set at the periapsis and the periapsis at the apoapsis. The
        // apoapsis goes first where it can lie above the periapsis there is; else the periapsis is lowered first.
        private static Result ShapeApsides(Frame frame, Destination stop)
        {
            Orbit orbit = frame.Orbit;
            CelestialBody body = frame.Body;
            double safe = body.atmosphere ? body.atmosphereDepth + 1000.0 : 1000.0;
            if (stop.Altitude < safe) return Result.Failed("A periapsis of " + (stop.Altitude / 1000.0).ToString("0.#") + " km is inside the ground or the air of " + body.bodyName + ".");
            double mu = body.gravParameter;
            double apoapsis = ApoapsisOf(stop);
            State here = KspOrbits.StateAt(orbit, frame.Now);
            var conic = new Conic(here, mu);
            double periapsisRadius = body.Radius + stop.Altitude, apoapsisRadius = body.Radius + apoapsis;
            bool apoapsisOff = Math.Abs(orbit.ApA - apoapsis) > Near(apoapsis);
            bool periapsisOff = Math.Abs(orbit.PeA - stop.Altitude) > Near(stop.Altitude);
            bool atApoapsis;
            // A periapsis in the air or the ground is lifted first, at the apoapsis: a burn at that periapsis would be made in the air.
            if (orbit.PeA < safe) atApoapsis = true;
            else if (apoapsisOff && apoapsisRadius >= conic.RadiusAt(0.0)) atApoapsis = false;
            else atApoapsis = periapsisOff;
            Burn burn;
            double radius = atApoapsis ? periapsisRadius : apoapsisRadius;
            if (!Maneuvers.ChangeOpposite(here, mu, atApoapsis, radius, out burn)) return Result.Failed("No burn was found.");
            if (burn.Delay < LeadTime) burn = new Burn(burn.Delay + conic.Period, burn.DeltaV);
            double ut = frame.Now + burn.Delay;
            KspOrbits.ClearNodes(frame);
            if (KspOrbits.AddNode(frame, ut, burn.DeltaV) == null) return Result.Failed("The vessel has no flight plan.");
            return new Result
            {
                Ok = true, UT = ut, DeltaV = burn.DeltaV, Purpose = BurnPurpose.Orbit,
                Why = (atApoapsis ? "Set the periapsis to " + (stop.Altitude / 1000.0).ToString("0.#") + " km at the apoapsis: " : "Set the apoapsis to " + (apoapsis / 1000.0).ToString("0.#") + " km at the periapsis: ")
                      + burn.Magnitude.ToString("0") + " m/s.",
            };
        }

        // The burn that turns the plane to the asked inclination or to the plane of the target, at the node that comes first.
        private static Result TiltOrbit(Frame frame, Destination stop)
        {
            Orbit orbit = frame.Orbit;
            CelestialBody body = frame.Body;
            double mu = body.gravParameter;
            State here = KspOrbits.StateAt(orbit, frame.Now);
            V3 wanted;
            string what;
            if (!WantedPlane(frame, stop, here, out wanted, out what)) return Result.Failed(what);
            State later = Kepler.Propagate(here, LeadTime, mu);
            Burn burn;
            if (!Maneuvers.MatchPlane(later, mu, wanted, out burn)) return Result.Failed("The plane cannot be turned from this orbit.");
            burn = new Burn(burn.Delay + LeadTime, burn.DeltaV);
            double ut = frame.Now + burn.Delay;
            KspOrbits.ClearNodes(frame);
            if (KspOrbits.AddNode(frame, ut, burn.DeltaV) == null) return Result.Failed("The vessel has no flight plan.");
            return new Result { Ok = true, UT = ut, DeltaV = burn.DeltaV, Purpose = BurnPurpose.Orbit, Why = "Turn the plane to " + what + ": " + burn.Magnitude.ToString("0") + " m/s." };
        }

        // The normal of the plane asked for, in the axes of the vessel's state; or why there is none.
        private static bool WantedPlane(Frame frame, Destination stop, State here, out V3 wanted, out string what)
        {
            wanted = new V3();
            what = null;
            CelestialBody body = frame.Body;
            if (stop.MatchPlaneWith != Guid.Empty)
            {
                Vessel target = FlightGlobals.Vessels.Find(v => v.id == stop.MatchPlaneWith);
                if (target == null || target.orbit == null || target.orbit.referenceBody != body || target.orbit.eccentricity >= 1.0)
                {
                    what = "The vessel whose plane is asked for is not in an orbit round " + body.bodyName + ".";
                    return false;
                }
                State there = KspOrbits.StateAt(target.orbit, frame.Now);
                wanted = V3.Cross(there.R, there.V).Unit;
                what = "the plane of " + target.vesselName;
                return true;
            }
            // The inclination is the angle of the orbit's normal to the body's axis, or its supplement: which of the two follows from the inclination
            // the game gives for the orbit now, so that the left-handed world of Unity does not matter.
            Vector3 axis = body.angularVelocity.normalized;
            V3 pole = new V3(axis.x, axis.y, axis.z);
            V3 normal = V3.Cross(here.R, here.V).Unit;
            wanted = Maneuvers.NormalForInclination(normal, pole, stop.Inclination, frame.Orbit.inclination);
            what = stop.Inclination.ToString("0.#") + " degrees";
            return true;
        }
    }
}
