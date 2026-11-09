namespace ReDefinition.Core
{
    // What the host reads of the planner's route: what it is doing, and the two switches of a vessel.
    // An abstract class with defaults, for the same reason as GuidanceHook: an added member must not break an older adapter.
    public abstract class RouteHook
    {
        // What the route is doing now; empty for nothing.
        public virtual string Status { get { return ""; } }

        // Whether the vessel's navigation is on.
        public virtual bool NavigationOn(Vessel vessel) { return false; }

        // Whether the vessel's autopilot is on.
        public virtual bool AutopilotOn(Vessel vessel) { return false; }
    }
}
