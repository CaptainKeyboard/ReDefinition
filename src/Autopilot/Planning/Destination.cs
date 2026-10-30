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
        // The sections that stay at this stop and are let go there: a satellite, by the part that separates it.
        public List<SectionRelease> Releases = new List<SectionRelease>();
    }

    // A section of the vessel that stays where the route has it: its name as the planner shows it, and the decoupler or docking port, by its
    // persistent id, whose separating lets it go.
    internal sealed class SectionRelease
    {
        public string Name;
        public uint SeparatorId;
        public bool Docking;
    }
}
