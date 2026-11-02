using System;
using ReDefinition.Orbits;
using UnityEngine;

namespace ReDefinition.Autopilot.Planning
{
    // The burn that turns the orbit's plane so that it passes over a place on the ground. The place lies off the plane by an angle; the plane is turned about
    // the node a quarter of an orbit before the place, by that angle, which is the least that gets it there. The body turns while the vessel goes round, so
    // the place is taken where it will be when the vessel is over it, as MechJeb's landing does (PlaneChange, GPL-3.0).
    internal static class PlaneChange
    {
        // The place's direction from the body's centre, in the world's axes, as it will be `after` seconds from now.
        internal static V3 TargetDirection(CelestialBody body, double latitude, double longitude, double after)
        {
            Vector3 now = (Vector3)(body.GetWorldSurfacePosition(latitude, longitude, 0.0) - body.position);
            float degrees = (float)(360.0 * after / body.rotationPeriod);
            Vector3 turned = Quaternion.AngleAxis(degrees, (Vector3)body.angularVelocity) * now;
            return new V3(turned.x, turned.y, turned.z).Unit;
        }

        // The burn from the state, with its delay, that brings the place under the orbit's plane. False where the place is within `tolerance` metres
        // of the track. False too where the orbit is open. `across` is how far off the track the place lies, in metres. `arrival` is when the vessel
        // is over the place, in seconds from the state. NaN reckons it from the burn.
        internal static bool Plan(CelestialBody body, State here, double latitude, double longitude, double tolerance, double arrival, out Burn burn, out double across)
        {
            burn = new Burn(0.0, new V3(0.0, 0.0, 0.0));
            across = 0.0;
            double mu = body.gravParameter;
            var conic = new Conic(here, mu);
            if (!conic.Closed) return false;
            V3 normal = V3.Cross(here.R, here.V).Unit;
            double wait = double.IsNaN(arrival) ? 0.25 * conic.Period : arrival;
            for (int pass = 0; pass < (double.IsNaN(arrival) ? 3 : 1); pass++)
            {
                V3 target = TargetDirection(body, latitude, longitude, wait);
                double sine = Math.Max(-1.0, Math.Min(1.0, V3.Dot(target, normal)));
                across = body.Radius * Math.Asin(sine);
                if (Math.Abs(across) <= tolerance) return false;
                // The node is where the orbit is a quarter of a turn before the place's own point on the track.
                V3 along = (target - normal * sine).Unit;
                V3 node = V3.Cross(along, normal).Unit;
                double angle = Math.Asin(sine);
                V3 up = normal.Rotated(node, angle), down = normal.Rotated(node, -angle);
                V3 wanted = Math.Abs(V3.Dot(target, up)) <= Math.Abs(V3.Dot(target, down)) ? up : down;
                // The node is looked for after the lead time every burn needs, so that there is time to turn the vessel and light the engines.
                State later = Kepler.Propagate(here, NodePlanner.LeadTime, mu);
                if (!Maneuvers.MatchPlane(later, mu, wanted, out burn)) return false;
                burn = new Burn(burn.Delay + NodePlanner.LeadTime, burn.DeltaV);
                // The place is a quarter of an orbit behind the node it was worked out from, three quarters behind the opposite one.
                State there = Kepler.Propagate(here, burn.Delay, mu);
                wait = burn.Delay + (V3.Dot(there.R.Unit, node) > 0.0 ? 0.25 : 0.75) * conic.Period;
            }
            return true;
        }
    }
}
