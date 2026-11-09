using System;
using ReDefinition.Planner.Autopilot.Planning;
using UnityEngine;

namespace ReDefinition.Planner.Engineer
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
        private readonly IFlightPlanSource plans;
        // The plan the window held when this vessel became the active one; it belongs to the vessel before.
        private RoutePlan foreignPlan;

        internal FlightPlannerHost(IFlightPlanSource plans)
        {
            this.plans = plans;
        }

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
                RoutePlan plan = plans.WholePlan;
                return plan == foreignPlan ? null : plan;
            }
        }

        // Where the vessel is: its orbit round its body, with the plane it is in, or the place where it stands on the surface.
        public RouteStop Here(PlannerBodies bodies)
        {
            Vessel active = FlightGlobals.ActiveVessel;
            if (active == null || active.mainBody == null || bodies == null) return null;
            int body = bodies.Bodies.FindIndex(each => each.Id == active.mainBody.bodyName);
            if (body < 0) return null;
            // The place is where the vessel is now, at its height above the body, not an orbit it might have: on an escape course or an orbit
            // that dips into the air, the orbit is not what the vessel is in.
            double air = active.mainBody.atmosphere ? active.mainBody.atmosphereDepth : 0.0;
            bool inSpace = !active.LandedOrSplashed && active.altitude > air;
            var here = new RouteStop { Body = body, Kind = inSpace ? StopKind.Orbit : StopKind.Surface };
            if (inSpace)
            {
                here.Altitude = Math.Max(1000.0, active.altitude);
                if (active.orbit != null && active.orbit.eccentricity < 1.0)
                {
                    here.HasInclination = true;
                    here.Inclination = active.orbit.inclination;
                }
            }
            else
            {
                here.HasSite = true;
                here.SiteLatitude = active.latitude;
                here.SiteLongitude = active.longitude;
            }
            return here;
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
                foreignPlan = plans.WholePlan;
                plans.ForgetPlan();
                if (changed != null) changed();
            }
            if (!dirty && Time.unscaledTime < nextRead) return;
            long read = PlannerTiming.Start();
            readout = EditorReadout.Read(vessel);
            PlannerTiming.Stop("host.Read", read);
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
