using ReDefinition.Core;
using ReDefinition.Planner.Autopilot;
using ReDefinition.Planner.Autopilot.Driver;

namespace ReDefinition.Planner
{
    // The route driver and a vessel's route state as the host reads them. Every call reads the live state; nothing is kept here.
    internal sealed class RouteAdapter : RouteHook
    {
        public override string Status
        {
            get { return RouteDriver.Instance != null ? RouteDriver.Instance.Status ?? "" : ""; }
        }

        public override bool NavigationOn(Vessel vessel)
        {
            RouteState state = RouteState.Of(vessel);
            return state != null && state.Navigation;
        }

        public override bool AutopilotOn(Vessel vessel)
        {
            RouteState state = RouteState.Of(vessel);
            return state != null && state.Autopilot;
        }
    }
}
