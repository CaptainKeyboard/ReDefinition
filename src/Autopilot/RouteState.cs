using System;

namespace ReDefinition.Autopilot
{
    // What the planner does for a vessel on its route: the autonavigation plans the route and keeps the plan up to date while the player flies,
    // and the autopilot flies it with MechJeb. It is set in the map window and kept in the save with the vessel, so that it holds when the
    // vessel is loaded again.
    public sealed class RouteState : VesselModule
    {
        // The route is flown by MechJeb; it is planned and kept up to date for the player to fly. The autopilot includes the autonavigation.
        public bool Autopilot;
        public bool Autonavigation;

        protected override void OnLoad(ConfigNode node)
        {
            bool.TryParse(node.GetValue("autopilot"), out Autopilot);
            bool.TryParse(node.GetValue("autonavigation"), out Autonavigation);
        }

        protected override void OnSave(ConfigNode node)
        {
            if (Autopilot) node.AddValue("autopilot", true);
            if (Autonavigation) node.AddValue("autonavigation", true);
        }

        // The vessel's state, or null where it has none yet.
        internal static RouteState Of(Vessel vessel)
        {
            return vessel != null ? vessel.FindVesselModuleImplementing<RouteState>() : null;
        }
    }
}
