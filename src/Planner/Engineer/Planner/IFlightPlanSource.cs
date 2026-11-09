using ReDefinition.Planner.Autopilot.Planning;

namespace ReDefinition.Planner.Engineer
{
    // Where the planner in flight gets the autopilot's whole plan for the route from: the window that simulates it. The host asks this and
    // not the window itself, so that the planner does not have to know the window.
    internal interface IFlightPlanSource
    {
        // The last whole plan made for the route, or null.
        RoutePlan WholePlan { get; }

        // Lets go of the plan, which belongs to a vessel flown before.
        void ForgetPlan();
    }
}
