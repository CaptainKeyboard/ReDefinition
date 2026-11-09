namespace ReDefinition.Autopilot.Planning
{
    // How a route is planned: for whom, from where, and with what simulated. The defaults are the plan the map shows the player.
    internal sealed class PlanOptions
    {
        // The player flies the climb and the descent: a deorbit is a node and the landing is guided.
        public bool ForPlayer;
        // The course of a transfer that is under way is kept, and only corrected.
        public bool KeepCourse;
        // The passes through the air are flown in the simulation and the plan goes on from the orbit each leaves the air in: a plan the vessel flies without
        // the player. Without, a pass ends the plan.
        public bool AirPasses;
        // The plan starts in this orbit at this UT, as if the vessel were there: the launch of an ascent is not flown by the plan.
        public Orbit From;
        public double FromUT;
        // The count of stops reached that the plan starts with, instead of the vessel's own.
        public int? ReachedAtStart;
        // A search that takes long runs in the background, and the plan answers that it is pending until it is done: whoever asks keeps asking. Without, the
        // plan waits for the answer.
        public bool Responsive;

        // The route as the player has it guided: the descent is his, and a deorbit is a node.
        internal static PlanOptions Guided
        {
            get { return new PlanOptions { ForPlayer = true, Responsive = true }; }
        }

        // The route flown by the autopilot.
        internal static PlanOptions Flown(bool keepCourse)
        {
            return new PlanOptions { KeepCourse = keepCourse, Responsive = true };
        }

        // The route the vessel flies on its own while the player is away.
        internal static PlanOptions Background
        {
            get { return new PlanOptions { AirPasses = true }; }
        }
    }
}
