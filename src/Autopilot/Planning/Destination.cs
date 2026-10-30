using System.Collections.Generic;

namespace ReDefinition.Autopilot.Planning
{
    // Where a route goes: a body, and on its surface or in a circular orbit at an altitude. The planner's stops, without the planner.
    internal sealed class Destination
    {
        public CelestialBody Body;
        public bool Surface;
        public double Altitude;
        // Arriving through the air: straight into it for a landing, or into it to brake and then out into the orbit. PassAltitude is how high
        // over the ground the pass has its periapsis, in metres, where the arrival uses the air.
        public bool Direct;
        public bool Aerobrake;
        public double PassAltitude;
    }
}
