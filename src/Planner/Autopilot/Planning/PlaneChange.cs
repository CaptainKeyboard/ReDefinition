using System;
using ReDefinition.Planner.Orbits;
using UnityEngine;

namespace ReDefinition.Planner.Autopilot.Planning
{
    // What a place on the ground asks of the orbit's plane.
    internal enum PlaneTurn
    {
        // The place is under the track already, near enough.
        None,
        // The plane is to be turned: the burn is given.
        Needed,
        // The plane cannot be turned to it: the orbit is open, or there is no node.
        Impossible,
    }

    // The burn that turns the orbit's plane so that it passes over a place on the ground. The place lies off the plane by an angle; the plane is turned about
    // the node a quarter of an orbit before the place, by that angle, which is the least that gets it there. The body turns while the vessel goes round, so
    // the place is taken where it will be when the vessel is over it, as MechJeb's landing does (PlaneChange, GPL-3.0).
    internal static class PlaneChange
    {
        // The place's direction from the body's centre, in the world's axes, as it will be `after` seconds from now.
        internal static V3 TargetDirection(CelestialBody body, double latitude, double longitude, double after)
        {
            Vector3d position = body.GetWorldSurfacePosition(latitude, longitude, 0.0) - body.position;
            double degrees = body.rotates && body.rotationPeriod > 0.0 ? 360.0 * after / body.rotationPeriod : 0.0;
            Vector3 turned = Quaternion.AngleAxis((float)degrees, (Vector3)body.angularVelocity) * (Vector3)position;
            return new V3(turned.x, turned.y, turned.z).Unit;
        }

        internal static PlaneTurn Plan(CelestialBody body, State here, double latitude, double longitude, double tolerance, double arrival, out Burn burn)
        {
            return Plan(here, body.gravParameter, body.Radius, after => TargetDirection(body, latitude, longitude, after), tolerance, arrival, out burn);
        }

        // The burn from the state, with its delay, that brings the place under the orbit's plane. Where the place is at a time, in seconds from the state,
        // is what `placeAt` says. How far off the track still counts as under it is the tolerance, in metres. The arrival is when the vessel is over the
        // place, in seconds from the state; NaN reckons it from the burn.
        internal static PlaneTurn Plan(State here, double mu, double radius, Func<double, V3> placeAt, double tolerance, double arrival, out Burn burn)
        {
            burn = new Burn(0.0, new V3(0.0, 0.0, 0.0));
            var conic = new Conic(here, mu);
            if (!conic.Closed) return PlaneTurn.Impossible;
            V3 normal = V3.Cross(here.R, here.V).Unit;
            bool reckoned = double.IsNaN(arrival);
            double wait = reckoned ? 0.25 * conic.Period : arrival;
            for (int pass = 0; pass < (reckoned ? 3 : 1); pass++)
            {
                V3 place = placeAt(wait);
                double sine = Math.Max(-1.0, Math.Min(1.0, V3.Dot(place, normal)));
                if (Math.Abs(radius * Math.Asin(sine)) <= tolerance) return PlaneTurn.None;
                // The node is where the orbit is a quarter of a turn before the place's own point on the track. A place straight over the orbit's pole has no
                // such point, and every node serves: the one where the vessel is.
                V3 off = place - normal * sine;
                V3 along = off.Length > 1e-9 ? off.Unit : here.R.Unit;
                V3 node = V3.Cross(along, normal).Unit;
                double angle = Math.Asin(sine);
                V3 up = normal.Rotated(node, angle), down = normal.Rotated(node, -angle);
                V3 wanted = Math.Abs(V3.Dot(place, up)) <= Math.Abs(V3.Dot(place, down)) ? up : down;
                // The node is looked for after the lead time every burn needs, so that there is time to turn the vessel and light the engines.
                State later = Kepler.Propagate(here, NodePlanner.LeadTime, mu);
                if (!Maneuvers.MatchPlane(later, mu, wanted, out burn)) return PlaneTurn.Impossible;
                burn = new Burn(burn.Delay + NodePlanner.LeadTime, burn.DeltaV);
                // The vessel is over the place when it comes to the place's point on the track, after the burn.
                double over = conic.TimeToTrueAnomaly(conic.TrueAnomalyOf(along));
                if (double.IsNaN(over)) return PlaneTurn.Impossible;
                if (over <= burn.Delay) over += conic.Period;
                bool settled = Math.Abs(over - wait) < 1.0;
                wait = over;
                if (settled) break;
            }
            return PlaneTurn.Needed;
        }
    }
}
