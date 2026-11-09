using System.Collections.Generic;
using ReDefinition.Planner.Autopilot.Planning;
using UnityEngine;

namespace ReDefinition.Planner.Engineer
{
    // Where the route that the driver follows comes from.
    internal enum RouteOrigin
    {
        // None is held: the route is read from the plan the vessel has saved, every few seconds.
        Saved,
        // The planner's route, as the planner's window handed it over.
        Planner,
        // The route the map's menu made ("Launch to orbit", "Land at marker"), with no plan in the planner. It holds until the navigation is switched off.
        Quick,
    }

    // The route the driver follows for the vessel in focus, and where it comes from. The driver asks it for the route; it never has to tell a route that was
    // made for the moment from one that was planned by whether a list is null or is the same list as another.
    internal sealed class HeldRoute
    {
        // How often the saved route is read, in s: the file holds all the plans.
        private const float Age = 5f;

        private List<Destination> destinations;
        private float readAt = -1000f;

        internal RouteOrigin Origin { get; private set; }

        // The route as it is held, or null where none is: what the pause menu and the leaving of the flight hand to the background.
        internal List<Destination> Held
        {
            get { return destinations; }
        }

        // Nothing is held any more.
        internal void Clear()
        {
            Origin = RouteOrigin.Saved;
            destinations = null;
            readAt = -1000f;
        }

        // The planner's route is held. Where it is the quick route itself, as the autopilot starts on it, it stays the quick route.
        internal void HoldPlanner(List<Destination> route)
        {
            if (Origin == RouteOrigin.Quick && ReferenceEquals(route, destinations)) return;
            Origin = RouteOrigin.Planner;
            destinations = route;
            readAt = Time.realtimeSinceStartup;
        }

        // The route the menu made is held until the navigation is switched off.
        internal void HoldQuick(List<Destination> route)
        {
            Origin = RouteOrigin.Quick;
            destinations = route;
            readAt = Time.realtimeSinceStartup;
        }

        // Whether the route held is aimed at a place on the ground.
        internal bool Aimed
        {
            get { return Origin == RouteOrigin.Quick && destinations != null && destinations.Count > 0 && destinations[destinations.Count - 1].HasSite; }
        }

        // The route to follow: the quick route, or else the saved plan of the vessel, read again when it is old.
        internal List<Destination> Read(Vessel vessel)
        {
            if (Origin == RouteOrigin.Quick) return destinations;
            if (destinations == null || Time.realtimeSinceStartup - readAt > Age)
            {
                destinations = RouteSource.For(vessel.vesselName);
                readAt = Time.realtimeSinceStartup;
            }
            return destinations;
        }
    }
}
