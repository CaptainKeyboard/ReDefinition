namespace ReDefinition.Engineer
{
    // What the planner's part "Guidance" asks of the window it stands in: whether the route is planned, which of the navigation and the autopilot
    // is on, the lines that say what the route is doing, and the two switches. The editor has none, and then the part is left out.
    internal interface IPlanControls
    {
        // Whether the route is planned and the plan reaches somewhere: what the navigation and the autopilot need.
        bool PlanReady { get; }
        // Whether the autopilot flies the route now, and whether the navigation alone keeps the plan up to date.
        bool Flying { get; }
        bool NavigationOn { get; }
        // What the route is doing or what the last planning came to, and what Parsek does with the flight; empty where there is nothing to say.
        string Status { get; }
        string ParsekLine { get; }
        // Why the autopilot cannot be used here (a mod missing); null where it can.
        string AutopilotProblem { get; }
        // Each switch turns the other off.
        void ToggleNavigation();
        void ToggleAutopilot();
    }
}
