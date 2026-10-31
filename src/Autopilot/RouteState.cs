using System;
using System.Collections.Generic;
using ReDefinition.Autopilot.Planning;

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
        // How far along the route the vessel has come, kept with it.
        internal RouteProgress Progress = new RouteProgress();

        // The progress for this route: a count made for another route starts again.
        internal RouteProgress ProgressFor(IList<Destination> route)
        {
            string key = RoutePlan.SignatureOf(route);
            if (Progress.Key != key) Progress = new RouteProgress { Key = key };
            return Progress;
        }

        protected override void OnLoad(ConfigNode node)
        {
            bool.TryParse(node.GetValue("autopilot"), out Autopilot);
            bool.TryParse(node.GetValue("navigation"), out Navigation);
            int reached;
            Progress = new RouteProgress { Key = node.GetValue("reachedFor") ?? "", Reached = int.TryParse(node.GetValue("reached"), out reached) ? reached : -1 };
        }

        protected override void OnSave(ConfigNode node)
        {
            if (Autopilot) node.AddValue("autopilot", true);
            if (Navigation) node.AddValue("navigation", true);
            if (Progress.Reached >= 0)
            {
                node.AddValue("reached", Progress.Reached);
                node.AddValue("reachedFor", Progress.Key);
            }
        }

        // The vessel's state, or null where it has none yet.
        internal static RouteState Of(Vessel vessel)
        {
            return vessel != null ? vessel.FindVesselModuleImplementing<RouteState>() : null;
        }
    }
}
