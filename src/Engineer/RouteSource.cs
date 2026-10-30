using System.Collections.Generic;
using ReDefinition.Autopilot;

namespace ReDefinition.Engineer
{
    // The planner's route as the autopilot takes it: the destinations, from the stops of a plan.
    internal static class RouteSource
    {
        // The stops as destinations: a stop the autopilot flies through (an entry, a transfer) is left out. Null where there are fewer than two.
        internal static List<Destination> FromStops(IList<RouteStop> stops, PlannerBodies bodies)
        {
            if (stops == null || bodies == null) return null;
            var route = new List<Destination>();
            foreach (RouteStop stop in stops)
            {
                if (stop.Kind == StopKind.Entry || stop.Kind == StopKind.Transfer) continue;
                string id = stop.Body >= 0 && stop.Body < bodies.Bodies.Count ? bodies.Bodies[stop.Body].Id : null;
                CelestialBody body = id != null ? FlightGlobals.Bodies.Find(b => b.bodyName == id) : null;
                route.Add(Arrival(new Destination { Body = body, Surface = stop.Kind == StopKind.Surface, Altitude = stop.Altitude }, stop, bodies));
            }
            return route.Count >= 2 ? route : null;
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
            return FromStops(state.Stops, bodies);
        }
    }
}
