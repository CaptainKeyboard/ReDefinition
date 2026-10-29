using System;
using UnityEngine;

namespace ReDefinition.Autopilot
{
    // What the autonavigation tells the player where there is no node to put in: how to fly the climb to an orbit. The values come from the
    // vessel's state in every second, from a gravity turn that is flat when the air is passed. The descent is told by LandingGuide. It does not
    // need MechJeb.
    internal static class Guidance
    {
        // Where the pitch reaches the horizon, as a part of the air's depth; for a body without air, a fixed height in metres.
        private const double TurnEndInAir = 0.55;
        private const double TurnEndWithoutAir = 12000.0;
        // The height over which the climb goes straight up, and the speed it needs for the turn to begin.
        private const double VerticalHeight = 250.0;
        private const double TurnSpeed = 60.0;

        // The climb to a circular orbit at `aimAltitude`: the pitch and heading to fly now, and what the engines do.
        internal static string Climb(Vessel vessel, double aimAltitude)
        {
            CelestialBody body = vessel.mainBody;
            double height = vessel.altitude;
            double turnEnd = body.atmosphere ? TurnEndInAir * body.atmosphereDepth : TurnEndWithoutAir;
            double pitch = 90.0;
            if (height > VerticalHeight && vessel.srfSpeed > TurnSpeed)
                pitch = 90.0 * (1.0 - Math.Sqrt(Math.Min(1.0, (height - VerticalHeight) / (turnEnd - VerticalHeight))));
            double latitude = vessel.latitude * Math.PI / 180.0;
            double heading = 90.0;
            double inclination = 0.0;
            double ratio = Math.Cos(inclination * Math.PI / 180.0) / Math.Max(0.1, Math.Cos(latitude));
            if (ratio <= 1.0) heading = Math.Asin(ratio) * 180.0 / Math.PI;
            string engines = vessel.orbit.ApA < aimAltitude
                ? "full throttle until the apoapsis is " + (aimAltitude / 1000.0).ToString("0.#") + " km, now " + Math.Max(0.0, vessel.orbit.ApA / 1000.0).ToString("0.0") + " km"
                : "engines off, the apoapsis is high enough";
            return "Climb: pitch " + pitch.ToString("0") + ", heading " + heading.ToString("0") + ", " + engines + ".";
        }
    }
}
