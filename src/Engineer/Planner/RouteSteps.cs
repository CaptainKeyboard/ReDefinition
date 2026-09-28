using System.Collections.Generic;
using System;

namespace ReDefinition.Engineer
{
    // The steps on the way from one destination to the next, each to a place of its
    // own: up from the surface into an orbit, across to the next body's orbit or
    // straight into its air (or from one orbit to another around the same body), and
    // down onto the surface.
    internal enum StepRole
    {
        Launch,
        Transfer,
        Land,
    }

    // One step of the route: who flies it, from where to where, what it costs, and
    // whether the lander leaves the rest there or docks with it at its end.
    internal sealed class RouteStep
    {
        // The destination it leads to; the leg of the fuel plan is Goal - 1.
        public int Goal;
        public StepRole Role;
        public Vehicle Vehicle;
        // Set by the player rather than worked out (RouteStop.By).
        public bool Chosen;
        public LegMode Mode;
        public RouteStop From;
        public RouteStop To;
        public Leg Leg;
        // At its start, the lander leaves the rest (a step of the lander), or the rest
        // leaves the lander behind (a step of the rest).
        public bool Separates;
        // At its end, the lander docks with the rest, and is refilled from the rest's
        // tanks where the player wants it.
        public bool Docks;
        public bool WantsRefuel;
        public bool RefuelAfter;
        // It reaches its destination, which keeps a payload (RouteStop.LeavesPayload).
        public bool PayloadAfter;
        // What makes the step impossible as set, for the player; null where none.
        public string Problem;

        public int Segment
        {
            get { return Goal - 1; }
        }
    }

    // The steps of a route (docs/development/planner.md). Between two destinations
    // come the steps the way needs, each flown by the whole vessel (Ship), its lander
    // alone, or the rest without the lander. Who flies a step is the player's choice
    // (RouteStop.By), or else: from a surface whoever stands on it; across to another
    // body the whole vessel where the lander waits with the rest, so that it docks;
    // otherwise whoever flew the step before. From that, the lander leaves the rest
    // where a step of the lander follows one of the whole vessel; it docks where a
    // step of the whole vessel follows, and has to be where the rest waits for that;
    // a rest that is never met again is left where it waited. The lander climbs to
    // the rest's own orbit where the rest waits around the body it starts from. Free
    // of Unity, tested.
    internal static class RouteSteps
    {
        // Finding the rest in its orbit and docking, over the climb to that orbit.
        internal const double Rendezvous = 50.0;

        private sealed class Walk
        {
            internal RouteStop Rest;
            internal RouteStop Lander;
            internal bool Together = true;
            internal bool LanderLeft;
            internal string LanderLeftAt;
            internal Vehicle Last = Vehicle.Ship;
            internal RouteStep LastLanderStep;
        }

        internal static List<RouteStep> Build(IList<PlannerBody> bodies, IList<RouteStop> goals, bool hasLander,
                                              bool planeChanges)
        {
            List<RouteStep> steps = new List<RouteStep>();
            if (goals.Count == 0) return steps;
            Walk walk = new Walk { Rest = goals[0], Lander = goals[0] };

            for (int g = 1; g < goals.Count; g++)
            {
                RouteStop from = goals[g - 1];
                RouteStop goal = goals[g];
                foreach (StepRole role in RolesBetween(from, goal))
                {
                    int by = goal.By[(int)role];
                    bool isChosen = hasLander && by >= 0;
                    Vehicle vehicle = !hasLander ? Vehicle.Ship : isChosen ? (Vehicle)by : Default(walk, role, from);
                    RouteStep step = Fly(bodies, walk, g, role, vehicle, from, goal, planeChanges);
                    step.Chosen = isChosen;
                    step.WantsRefuel = goal.Refuel[(int)role];
                    steps.Add(step);
                }
                if (steps.Count > 0 && steps[steps.Count - 1].Goal == g) steps[steps.Count - 1].PayloadAfter = goal.LeavesPayload;
            }
            return steps;
        }

        // The steps the way from one destination to the next needs.
        internal static List<StepRole> RolesBetween(RouteStop from, RouteStop goal)
        {
            List<StepRole> roles = new List<StepRole>();
            bool sameBody = from.Body == goal.Body;
            if (from.Kind == StopKind.Surface) roles.Add(StepRole.Launch);
            // Across to another body, or from one orbit to another around this one;
            // a launch straight into the destination's orbit needs no more.
            if (!sameBody || (from.Kind == StopKind.Orbit && goal.Kind == StopKind.Orbit)) roles.Add(StepRole.Transfer);
            if (goal.Kind == StopKind.Surface) roles.Add(StepRole.Land);
            return roles;
        }

        private static Vehicle Default(Walk walk, StepRole role, RouteStop from)
        {
            if (role == StepRole.Launch)
            {
                // Whoever stands on that surface.
                if (walk.Together) return Vehicle.Ship;
                return !walk.LanderLeft && walk.Lander.Kind == StopKind.Surface && walk.Lander.Body == from.Body
                    ? Vehicle.Lander
                    : walk.Last;
            }
            // The lander in an orbit of the body the rest waits at: it docks.
            if (role == StepRole.Transfer && !walk.Together && !walk.LanderLeft && Meets(walk.Lander, walk.Rest))
                return Vehicle.Ship;
            return walk.Last;
        }

        private static RouteStep Fly(IList<PlannerBody> bodies, Walk walk, int g, StepRole role, Vehicle vehicle,
                                     RouteStop from, RouteStop goal, bool planeChanges)
        {
            RouteStep step = new RouteStep { Goal = g, Role = role, Vehicle = vehicle };
            RouteStop start;

            if (vehicle == Vehicle.Ship && !walk.LanderLeft)
            {
                if (!walk.Together)
                {
                    // The lander docks with the rest before the whole vessel flies on: in
                    // the rest's orbit, or on its way to it from another one around the
                    // same body.
                    if ((SamePlace(walk.Lander, walk.Rest) || Meets(walk.Lander, walk.Rest)) && walk.LastLanderStep != null)
                    {
                        walk.LastLanderStep.Docks = true;
                        walk.LastLanderStep.RefuelAfter = walk.LastLanderStep.WantsRefuel;
                        if (!SamePlace(walk.Lander, walk.Rest))
                        {
                            double up, down;
                            PlannerBody body = bodies[walk.Rest.Body];
                            RoutePlanner.Hohmann(body.Mu, body.Radius + walk.Lander.Altitude, body.Radius + walk.Rest.Altitude, out up, out down);
                            walk.LastLanderStep.Leg.Add("To the rest's orbit", up + down);
                        }
                        if (walk.Rest.Kind == StopKind.Orbit)
                            walk.LastLanderStep.Leg.Add("Rendezvous and docking", Rendezvous);
                    }
                    else
                    {
                        step.Problem = "The lander is not where the rest waits, at " + Place(bodies, walk.Rest);
                    }
                    walk.Together = true;
                }
                step.Mode = LegMode.Whole;
                start = walk.Rest;
            }
            else if (vehicle == Vehicle.Lander)
            {
                if (walk.LanderLeft)
                {
                    step.Problem = "The lander stayed at " + walk.LanderLeftAt;
                    step.Mode = LegMode.Lander;
                    step.From = step.To = Target(bodies, walk, role, from, goal, vehicle);
                    step.Leg = new Leg { From = g - 1, To = g };
                    walk.Last = vehicle;
                    return step;
                }
                if (walk.Together)
                {
                    step.Separates = true;
                    walk.Together = false;
                    walk.Lander = walk.Rest;
                }
                step.Mode = LegMode.Lander;
                start = walk.Lander;
            }
            else
            {
                // The rest without the lander, which stays where it is for good.
                if (!walk.LanderLeft)
                {
                    step.Separates = walk.Together;
                    walk.LanderLeft = true;
                    walk.LanderLeftAt = Place(bodies, walk.Together ? walk.Rest : walk.Lander);
                    walk.Together = false;
                }
                step.Mode = LegMode.WithoutLander;
                start = walk.Rest;
            }

            // In the air, all that is left is the landing.
            if (start.Kind == StopKind.Entry && vehicle != Vehicle.Lander && role != StepRole.Land)
                step.Problem = step.Problem ?? "The rest went into the air at " + bodies[start.Body].Name;

            RouteStop to = Target(bodies, walk, role, from, goal, vehicle);
            step.From = start;
            step.To = to;
            step.Leg = RoutePlanner.LegOf(bodies, start, to, g - 1, planeChanges);

            if (vehicle == Vehicle.Lander)
            {
                walk.Lander = to;
                walk.LastLanderStep = step;
            }
            else
            {
                walk.Rest = to;
                if (walk.Together) walk.Lander = to;
            }
            walk.Last = vehicle;
            return step;
        }

        // Where a step goes: the destination itself where the step reaches it; the
        // body's low orbit, or the rest's own orbit where the lander climbs to it;
        // into the air for a landing on parachutes straight from the way across.
        private static RouteStop Target(IList<PlannerBody> bodies, Walk walk, StepRole role, RouteStop from, RouteStop goal,
                                        Vehicle vehicle)
        {
            if (role == StepRole.Land) return goal;
            if (role == StepRole.Launch)
            {
                // The lander climbs to the rest, where it waits around this body.
                if (vehicle == Vehicle.Lander && !walk.Together && walk.Rest.Kind == StopKind.Orbit && walk.Rest.Body == from.Body)
                    return walk.Rest;
                if (from.Body == goal.Body && goal.Kind == StopKind.Orbit) return goal;
                return LowOrbit(bodies, from.Body);
            }
            if (goal.Kind == StopKind.Orbit) return goal;
            PlannerBody body = bodies[goal.Body];
            if (goal.UseAtmosphere && body.HasAtmosphere && !goal.OrbitFirst)
                return new RouteStop { Body = goal.Body, Kind = StopKind.Entry };
            RouteStop orbit = LowOrbit(bodies, goal.Body);
            orbit.UseAtmosphere = goal.UseAtmosphere && body.HasAtmosphere;
            return orbit;
        }

        private static RouteStop LowOrbit(IList<PlannerBody> bodies, int body)
        {
            return new RouteStop { Body = body, Kind = StopKind.Orbit, Altitude = bodies[body].LowOrbitAltitude };
        }

        // Both in orbits of the same body: the lander can reach the rest.
        private static bool Meets(RouteStop lander, RouteStop rest)
        {
            return lander != null && rest != null && lander.Body == rest.Body && lander.Kind == StopKind.Orbit
                   && rest.Kind == StopKind.Orbit;
        }

        internal static bool SamePlace(RouteStop a, RouteStop b)
        {
            if (a == null || b == null || a.Body != b.Body || a.Kind != b.Kind) return false;
            return a.Kind != StopKind.Orbit || Math.Abs(a.Altitude - b.Altitude) <= Math.Max(1000.0, a.Altitude * 0.1);
        }

        // "on Mun surface", "in Kerbin orbit", "in Kerbin's air".
        internal static string At(IList<PlannerBody> bodies, RouteStop stop)
        {
            return (stop.Kind == StopKind.Surface ? "on " : "in ") + Place(bodies, stop);
        }

        internal static string Place(IList<PlannerBody> bodies, RouteStop stop)
        {
            string name = bodies[stop.Body].Name;
            switch (stop.Kind)
            {
                case StopKind.Surface: return name + " surface";
                case StopKind.Entry: return name + "'s air";
                default: return name + " orbit";
            }
        }
    }
}
