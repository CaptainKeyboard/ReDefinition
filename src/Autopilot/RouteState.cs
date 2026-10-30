using System;

namespace ReDefinition.Autopilot
{
    // What the planner does for a vessel on its route: the navigation plans the route and keeps the plan up to date while the player flies,
    // and the autopilot flies it with MechJeb. It is set in the map window and kept in the save with the vessel, so that it holds when the
    // vessel is loaded again.
    public sealed class RouteState : VesselModule
    {
        // The route is flown by MechJeb; it is planned and kept up to date for the player to fly. The autopilot includes the navigation.
        public bool Autopilot;
        public bool Navigation;

        protected override void OnLoad(ConfigNode node)
        {
            bool.TryParse(node.GetValue("autopilot"), out Autopilot);
            bool.TryParse(node.GetValue("navigation"), out Navigation);
        }

        protected override void OnSave(ConfigNode node)
        {
            if (Autopilot) node.AddValue("autopilot", true);
            if (Navigation) node.AddValue("navigation", true);
        }

        // The vessel's state, or null where it has none yet.
        internal static RouteState Of(Vessel vessel)
        {
            return vessel != null ? vessel.FindVesselModuleImplementing<RouteState>() : null;
        }
    }
}
