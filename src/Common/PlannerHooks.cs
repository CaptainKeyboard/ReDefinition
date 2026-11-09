namespace ReDefinition.Core
{
    // Where the planner offers its state to the host. Null means the planner is not installed.
    // The game keeps one copy of Common, so these are shared by every mod that uses it. The planner assigns them at the start of the game.
    public static class PlannerHooks
    {
        public static GuidanceHook Guidance;
        public static RouteHook Routes;
        public static EditorPlanHook EditorPlan;
        public static DryMarksHook DryMarks;
        // What the planner takes for granted about MechJeb's and Parsek's code, for the host's report of such assumptions.
        internal static ForeignAssumption[] ExtraAssumptions;
    }
}
