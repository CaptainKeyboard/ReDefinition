using System;
using ReDefinition.Autopilot.Planning;

namespace ReDefinition.Engineer
{
    // The editor as the planner's window: the vessel is the ship on the editor's table, and KSP's editor events say when another is on it.
    internal sealed class EditorPlannerHost : IPlannerHost
    {
        private readonly EditorEngineer owner;
        private Action changed, fresh;

        internal EditorPlannerHost(EditorEngineer owner)
        {
            this.owner = owner;
        }

        public EditorReadout Readout
        {
            get { return owner.Readout; }
        }

        public string VesselName()
        {
            ShipConstruct ship = EditorLogic.fetch != null ? EditorLogic.fetch.ship : null;
            return ship != null && !string.IsNullOrEmpty(ship.shipName) ? ship.shipName : "Untitled";
        }

        // The editor has no flight to plan.
        public RoutePlan Plan
        {
            get { return null; }
        }

        public void WatchVessel(Action changed, Action fresh)
        {
            this.changed = changed;
            this.fresh = fresh;
            GameEvents.onEditorLoad.Add(OnEditorLoad);
            GameEvents.onEditorRestart.Add(OnEditorRestart);
        }

        public void StopWatching()
        {
            GameEvents.onEditorLoad.Remove(OnEditorLoad);
            GameEvents.onEditorRestart.Remove(OnEditorRestart);
        }

        // A craft merged into the vessel keeps the plan.
        private void OnEditorLoad(ShipConstruct ship, KSP.UI.Screens.CraftBrowserDialog.LoadType type)
        {
            if (type != KSP.UI.Screens.CraftBrowserDialog.LoadType.Merge && changed != null) changed();
        }

        // KSP started a new vessel.
        private void OnEditorRestart()
        {
            if (fresh != null) fresh();
        }
    }
}
