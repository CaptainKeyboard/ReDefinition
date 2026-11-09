namespace ReDefinition.Engineer
{
    // How many rows the planner's window is made with. Every row a route could ever need is a set of dialog elements, about fifteen thousand
    // for the full size, and making them takes seconds. So the window is made for the route it has, and again, larger, when the route
    // outgrows it. The editor's panel is always the full size.
    internal sealed class PlannerPool
    {
        // The stops the rows are made for, the steps of the fuel plan and the thrust check, and the stages each of those shows.
        internal readonly int Stops;
        internal readonly int Steps;
        internal readonly int Stages;

        private PlannerPool(int stops, int steps, int stages)
        {
            Stops = stops;
            Steps = steps;
            Stages = stages;
        }

        // From the smallest to the full size, which holds what the planner can work out at all.
        private static readonly PlannerPool[] Levels =
        {
            new PlannerPool(4, 8, 6),
            new PlannerPool(7, 18, 8),
            new PlannerPool(11, 30, 10),
        };

        internal static int Full
        {
            get { return Levels.Length - 1; }
        }

        internal static PlannerPool Of(int level)
        {
            return Levels[System.Math.Max(0, System.Math.Min(Full, level))];
        }

        // The smallest level whose rows hold a route of `stops` stops, `steps` steps and at most `stages` stages in a step; the full size where none does.
        internal static int Needed(int stops, int steps, int stages)
        {
            for (int level = 0; level < Levels.Length; level++)
                if (Levels[level].Stops >= stops && Levels[level].Steps >= steps && Levels[level].Stages >= stages) return level;
            return Full;
        }
    }
}
