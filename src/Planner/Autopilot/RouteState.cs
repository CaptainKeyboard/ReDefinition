using System;
using System.Collections.Generic;
using ReDefinition.Planner.Autopilot.Planning;

namespace ReDefinition.Planner.Autopilot
{
    // What the planner does for a vessel on its route: the navigation plans the route and keeps the plan up to date while the player flies,
    // and the autopilot flies it with MechJeb. It is set in the map window and kept in the save with the vessel, so that it holds when the
    // vessel is loaded again.
    public sealed class RouteState : VesselModule
    {
        // The route is flown by MechJeb; it is planned and kept up to date for the player to fly. The autopilot includes the navigation.
        public bool Autopilot;
        public bool Navigation;
        // MechJeb stages by itself; on unless the player switched it off, and applied to MechJeb when the vessel comes into focus.
        public bool AutoStaging = true;
        // How far along the route the vessel has come, kept with it.
        internal RouteProgress Progress = new RouteProgress();

        // The progress for this route: a count made for another route starts again.
        internal RouteProgress ProgressFor(IList<Destination> route)
        {
            string key = RoutePlan.SignatureOf(route);
            if (Progress.Key != key) Progress = new RouteProgress { Key = key };
            return Progress;
        }

        // The stops reached on this route as far as it is counted, -1 where the count is of another route. Asking changes nothing: a route looked at in the
        // planner's window is not the one the vessel flies, and must not take its count away.
        internal int ReachedFor(IList<Destination> route)
        {
            return Progress.Key == RoutePlan.SignatureOf(route) ? Progress.Reached : -1;
        }

        protected override void OnLoad(ConfigNode node)
        {
            bool.TryParse(node.GetValue("autopilot"), out Autopilot);
            bool.TryParse(node.GetValue("navigation"), out Navigation);
            string staging = node.GetValue("autoStaging");
            AutoStaging = staging == null || !bool.TryParse(staging, out AutoStaging) || AutoStaging;
            int reached;
            Progress = new RouteProgress { Key = node.GetValue("reachedFor") ?? "", Reached = int.TryParse(node.GetValue("reached"), out reached) ? reached : -1 };
        }

        protected override void OnSave(ConfigNode node)
        {
            if (Autopilot) node.AddValue("autopilot", true);
            if (Navigation) node.AddValue("navigation", true);
            if (!AutoStaging) node.AddValue("autoStaging", false);
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
