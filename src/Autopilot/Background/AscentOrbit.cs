using System;
using UnityEngine;

namespace ReDefinition.Autopilot.Background
{
    // The orbit a launch ends in, without the launch being flown: a circle at the altitude asked for.
    // Its plane holds the launch site and the way east from it; MechJeb's ascent asks for an inclination of zero, which a site off the equator
    // reaches at its latitude. The vessel is `downrange` along it from the site when it gets there: half the orbit's speed on average, for the burn time.
    internal static class AscentOrbit
    {
        // The orbit at the UT the ascent ends. `site` is where the launch site is at `now`, from the body's centre in the world's axes.
        // The launch is at `launchUT`, the burn takes `burnTime` s, and the orbit is at `altitude` m.
        internal static Orbit Make(CelestialBody body, Vector3d site, double now, double launchUT, double altitude, double burnTime, out double endUT)
        {
            endUT = launchUT + burnTime;
            double radius = body.Radius + altitude;
            double speed = Math.Sqrt(body.gravParameter / radius);
            double downrange = 0.5 * speed * burnTime / radius;
            // The site turns with the body from now to the launch.
            Vector3d at = body.rotates ? (Vector3d)(Quaternion.AngleAxis((float)(360.0 * body.rotPeriodRecip * (launchUT - now)), (Vector3)body.angularVelocity.normalized) * (Vector3)site) : site;
            Vector3d up = at.normalized;
            // The way east at the site is the way the ground moves there; at a pole there is none, and any is as good as another.
            Vector3d east = Vector3d.Cross(body.angularVelocity, up);
            if (east.sqrMagnitude < 1e-12) east = Vector3d.Cross(Vector3d.up, up);
            east = east.normalized;
            Vector3d position = (up * Math.Cos(downrange) + east * Math.Sin(downrange)) * radius;
            Vector3d velocity = (east * Math.Cos(downrange) - up * Math.Sin(downrange)) * speed;
            var orbit = new Orbit();
            orbit.UpdateFromStateVectors(position.xzy, velocity.xzy, body, endUT);
            return orbit;
        }
    }
}
