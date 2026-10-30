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
        // A vessel the last orbit of the route is met with: the player's target when the route is made. With Dock the target is a docking port and the
        // vessel docks with it. Empty where the stop is not a meeting.
        public System.Guid MeetWith;
        public uint DockWith;
        // The vessel docks with the target after the meeting, at a free port of the target's that suits one of its own: the port is chosen
        // when the target is loaded, which it is only near. DockWith names the port where the player chose one.
        public bool Dock;
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
        // Where the separating part sat among the vessel's parts and what it was called when the route was planned. A vessel that is put back
        // by Parsek has new persistent ids on all its parts, and is found by these.
        public int Index = -1;
        public string Title;

        // The part that separates on the vessel: the one with the persistent id, else the one at the planned place with the planned title.
        internal Part FindOn(Vessel vessel)
        {
            foreach (Part part in vessel.parts)
                if (part.persistentId == SeparatorId) return part;
            if (Index < 0 || Index >= vessel.parts.Count || Title == null) return null;
            Part at = vessel.parts[Index];
            return at.partInfo != null && at.partInfo.title == Title ? at : null;
        }
    }
}
