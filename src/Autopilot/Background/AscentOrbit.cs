using System;
using UnityEngine;

namespace ReDefinition.Autopilot.Background
{
    // The orbit a launch ends in, without the launch being flown: a circle at the altitude asked for.
    // Its plane holds the launch site and the way east from it; MechJeb's ascent asks for an inclination of zero, which a site off the equator
    // reaches at its latitude. The vessel is `downrange` along it from the site when it gets there: half the orbit's speed on average, for the burn time.
    internal static class AscentOrbit
    {
        // The orbit at the UT the ascent ends, from a launch now. `burnTime` is the time of the engines' burn in s, `altitude` that of the orbit in m.
        internal static Orbit Make(Vessel vessel, double altitude, double burnTime, out double endUT)
        {
            CelestialBody body = vessel.mainBody;
            double now = Planetarium.GetUniversalTime();
            endUT = now + burnTime;
            double radius = body.Radius + altitude;
            double speed = Math.Sqrt(body.gravParameter / radius);
            double downrange = 0.5 * speed * burnTime / radius;
            Vector3d site = (vessel.GetWorldPos3D() - body.position).normalized;
            // The way east at the site is the way the ground moves there; at a pole there is none, and any is as good as another.
            Vector3d east = Vector3d.Cross(body.angularVelocity, site);
            if (east.sqrMagnitude < 1e-12) east = Vector3d.Cross(Vector3d.up, site);
            east = east.normalized;
            Vector3d position = (site * Math.Cos(downrange) + east * Math.Sin(downrange)) * radius;
            Vector3d velocity = (east * Math.Cos(downrange) - site * Math.Sin(downrange)) * speed;
            var orbit = new Orbit();
            orbit.UpdateFromStateVectors(position.xzy, velocity.xzy, body, endUT);
            return orbit;
        }
    }
}
