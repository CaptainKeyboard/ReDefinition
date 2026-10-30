using System;
using System.Collections.Generic;
using ReDefinition.Autopilot;
using ReDefinition.Autopilot.Planning;
using ReDefinition.Core;

namespace ReDefinition.Engineer
{
    // The planner's route as the autopilot takes it: the destinations, from the stops of a plan.
    internal static class RouteSource
    {
        // The stops as destinations: a stop the autopilot flies through (an entry, a transfer) is left out. Null where there are fewer than two.
        internal static List<Destination> FromStops(IList<RouteStop> stops, PlannerBodies bodies, Func<int, List<SectionRelease>> releasesAt = null)
        {
            if (stops == null || bodies == null) return null;
            var route = new List<Destination>();
            for (int at = 0; at < stops.Count; at++)
            {
                RouteStop stop = stops[at];
                // The stops after the first one without a body are not picked yet.
                if (stop.Body < 0) break;
                if (stop.Kind == StopKind.Entry || stop.Kind == StopKind.Transfer) continue;
                string id = stop.Body >= 0 && stop.Body < bodies.Bodies.Count ? bodies.Bodies[stop.Body].Id : null;
                CelestialBody body = id != null ? FlightGlobals.Bodies.Find(b => b.bodyName == id) : null;
                Destination destination = Arrival(new Destination { Body = body, Surface = stop.Kind == StopKind.Surface, Altitude = stop.Altitude }, stop, bodies);
                if (releasesAt != null) destination.Releases = releasesAt(at);
                route.Add(destination);
            }
            if (route.Count == 0) return null;
            MeetTarget(route);
            return route.Count >= 2 ? route : null;
        }

        // The last orbit of the route is met with the vessel the player has as the target, in the orbit round that stop's body: the target's
        // vessel, and the docking port where the target is one. Nothing where there is no such target.
        private static void MeetTarget(List<Destination> route)
        {
            ITargetable target = FlightGlobals.fetch != null ? FlightGlobals.fetch.VesselTarget : null;
            Vessel vessel = target != null ? target.GetVessel() : null;
            Destination last = route[route.Count - 1];
            if (!ForeignAssumptions.MechJebRendezvous.Holds) return;
            if (vessel == null || last.Surface || last.Body == null || vessel.mainBody != last.Body || vessel == FlightGlobals.ActiveVessel) return;
            // Only a vessel in an orbit can be met.
            if (vessel.orbit == null || vessel.orbit.eccentricity >= 1.0 || vessel.LandedOrSplashed) return;
            last.MeetWith = vessel.id;
            ModuleDockingNode port = target as ModuleDockingNode;
            last.DockWith = port != null ? port.part.persistentId : 0u;
            // A port as the target means a docking; a probe's switch asks for one with a vessel as the target.
            last.Dock = port != null || CommandLine.Has("-redefinitionProbeDock");
        }

        // How the planner has the stop arrived at, as the planner's own steps read it (RouteSteps.Across). A landing that uses the air goes straight
        // into it, or into an orbit first by aerobraking; an orbit that uses the air is reached by aerobraking. Both dip into the air as far as
        // the planner's pass does.
        internal static Destination Arrival(Destination destination, RouteStop stop, PlannerBodies bodies)
        {
            if (stop.Body < 0 || stop.Body >= bodies.Bodies.Count) return destination;
            PlannerBody body = bodies.Bodies[stop.Body];
            if (!stop.UseAtmosphere || !body.HasAtmosphere) return destination;
            destination.Direct = stop.Kind == StopKind.Surface && !stop.OrbitFirst;
            destination.Aerobrake = !destination.Direct;
            destination.PassAltitude = body.AtmosphereDepth * RoutePlanner.AerobrakeDepth;
            return destination;
        }

        // The route the vessel's plan was saved with, from the file; null where it has none.
        internal static List<Destination> For(string vesselName)
        {
            PlannerBodies bodies = PlannerBodies.Read();
            PlannerState state = PlannerState.Load(vesselName, bodies);
            return FromStops(state.Stops, bodies, state.ReleasesAt);
        }
    }
}
