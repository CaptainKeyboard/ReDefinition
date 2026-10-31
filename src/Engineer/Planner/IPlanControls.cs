namespace ReDefinition.Engineer
{
    // What the planner's first part, "Plan", asks of the window it stands in: whether the whole route is planned, what the navigation and the
    // autopilot are doing, the lines that say it, and the three actions. The editor has none, and then the part is left out.
    internal interface IPlanControls
    {
        // Whether the route was planned as a whole and the plan reaches somewhere: what the navigation and the autopilot need.
        bool PlanReady { get; }
        // Whether the autopilot flies the route now, and whether the navigation keeps the plan up to date (the autopilot includes it).
        bool Flying { get; }
        bool NavigationOn { get; }
        // What the route is doing or what the last press came to, and what Parsek does with the flight; empty where there is nothing to say.
        string Status { get; }
        string ParsekLine { get; }
        // Why the autopilot or the navigation cannot be used here (a mod missing); null where they can.
        string AutopilotProblem { get; }
        string NavigationNote { get; }
        void PlanRoute();
        void ToggleNavigation();
        void ToggleAutopilot();
    }
}
