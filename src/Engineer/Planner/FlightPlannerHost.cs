using System;
using ReDefinition.Autopilot.Planning;
using UnityEngine;

namespace ReDefinition.Engineer
{
    // The flight scene as the planner's window: the vessel is the active one, read as it is now with its fuel, and KSP's events say when
    // it is another or has changed (a stage fired, a part went). The readout is made again every few seconds, which the view notices.
    internal sealed class FlightPlannerHost : IPlannerHost
    {
        // Seconds between two readings where nothing says the vessel changed.
        private const float ReadEvery = 2f;

        private Vessel vessel;
        private EditorReadout readout = new EditorReadout();
        private float nextRead;
        private bool dirty = true;
        private Action changed;
        // The plan the window held when this vessel became the active one; it belongs to the vessel before.
        private RoutePlan foreignPlan;

        public EditorReadout Readout
        {
            get { return readout; }
        }

        // The active vessel's name, which its plan is kept under: the craft's, unless the player renamed the vessel.
        public string VesselName()
        {
            return vessel != null && !string.IsNullOrEmpty(vessel.vesselName) ? vessel.vesselName : "Untitled";
        }

        // The map window's last whole plan, unless it was made for a vessel flown before this one.
        public RoutePlan Plan
        {
            get
            {
                RoutePlan plan = MapPlannerWindow.Instance != null ? MapPlannerWindow.Instance.WholePlan : null;
                return plan == foreignPlan ? null : plan;
            }
        }

        public void WatchVessel(Action changed, Action fresh)
        {
            this.changed = changed;
            GameEvents.onVesselChange.Add(OnVesselChange);
            GameEvents.onVesselWasModified.Add(OnVesselModified);
            GameEvents.onStageActivate.Add(OnStage);
        }

        public void StopWatching()
        {
            GameEvents.onVesselChange.Remove(OnVesselChange);
            GameEvents.onVesselWasModified.Remove(OnVesselModified);
            GameEvents.onStageActivate.Remove(OnStage);
        }

        // Every frame the window is up: the active vessel taken, and its readout made again where it is due.
        internal void Tick()
        {
            Vessel active = FlightGlobals.ActiveVessel;
            if (active != vessel)
            {
                vessel = active;
                dirty = true;
                // `WholePlan` is not cleared when the vessel changes; the one it holds now is not this vessel's.
                foreignPlan = MapPlannerWindow.Instance != null ? MapPlannerWindow.Instance.WholePlan : null;
                if (changed != null) changed();
            }
            if (!dirty && Time.unscaledTime < nextRead) return;
            readout = EditorReadout.Read(vessel);
            nextRead = Time.unscaledTime + ReadEvery;
            dirty = false;
        }

        private void OnVesselChange(Vessel v)
        {
            dirty = true;
        }

        private void OnVesselModified(Vessel v)
        {
            if (v == vessel) dirty = true;
        }

        private void OnStage(int stage)
        {
            dirty = true;
        }
    }
}
