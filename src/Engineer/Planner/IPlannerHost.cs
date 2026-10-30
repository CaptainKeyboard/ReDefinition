using System;
using ReDefinition.Autopilot.Planning;

namespace ReDefinition.Engineer
{
    // What the planner's view needs of the window it sits in: what the vessel holds now, the name its plan is kept under, and the
    // moments another vessel takes its place. The editor's window is one host (EditorPlannerHost); the map view's is the other.
    internal interface IPlannerHost
    {
        // The vessel as the planner works from it: its stages, its fuel, its charge.
        EditorReadout Readout { get; }

        // The name the vessel's plan is kept under; "Untitled" where it has none yet.
        string VesselName();

        // The route worked out from the vessel to its last stop (the Plan button in flight); null where there is none, as in the editor.
        RoutePlan Plan { get; }

        // Calls `changed` where another vessel, or the same one anew, is in the window, and `fresh` where a new vessel was begun.
        void WatchVessel(Action changed, Action fresh);

        // Lets go of what WatchVessel took.
        void StopWatching();
    }
}
