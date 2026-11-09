using UnityEngine;

namespace ReDefinition.Core
{
    // What the host reads of the planner's guidance in a frame: where to point, how much throttle to give, and the bar of the gauges.
    // An abstract class, not an interface: the game keeps one copy of Common, and a member added later must not break an adapter built against an older one.
    // Every member has a default that says "nothing to show". The planner derives from it and forwards to its live state.
    public abstract class GuidanceHook
    {
        // Whether the guidance wrote for this vessel a moment ago.
        public virtual bool Current(Vessel vessel) { return false; }

        // The line that says what to do.
        public virtual string Line { get { return ""; } }

        // The unit vector in the world to point the nose along, where HasDirection.
        public virtual Vector3d Direction { get { return Vector3d.zero; } }
        public virtual bool HasDirection { get { return false; } }

        // The throttle to give, 0 to 1, where HasThrottle.
        public virtual double Throttle { get { return 0.0; } }
        public virtual bool HasThrottle { get { return false; } }

        // Whether the gauges have a bar to show, and the bar: 0 to 1 with the mark at a half, its text, and how far off it is (0 on target, 2 at the full colour).
        public virtual bool HasBar { get { return false; } }
        public virtual double Arc { get { return 0.0; } }
        public virtual string NumberText { get { return ""; } }
        public virtual int Level { get { return 0; } }

        // Where the mark stands on the gauges, from a half to nothing, and whether the guidance has done what it was for.
        public virtual double MarkAt { get { return 0.5; } }
        public virtual bool Done { get { return false; } }

        // How far the throttle may be off the ideal for the bar to count as on target, and for it to be at its end, as shares of the range.
        public virtual double OnTarget { get { return 0.06; } }
        public virtual double FullColour { get { return 0.6; } }

        // Whether the vessel holds the guide node and no other, so that the game's maneuver display is the guide's.
        public virtual bool HoldsNode(Vessel vessel) { return false; }

        // Whether the node is one the guidance made itself.
        public virtual bool IsGuideNode(ManeuverNode node) { return false; }
    }
}
