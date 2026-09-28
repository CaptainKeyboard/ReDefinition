using System.Collections.Generic;

namespace ReDefinition.Engineer
{
    // Where a step stands between two destinations: the lander coming back to the
    // rest, the whole vessel carrying the lander to where it leaves, or the flight
    // to the destination itself.
    internal enum StepRole
    {
        Return,
        Approach,
        Main,
    }

    // One step of the route, as the vessel flies it: who flies, from where to where,
    // and what it costs.
    internal sealed class RouteStep
    {
        // The destination it leads to; the leg of the fuel plan is Goal - 1.
        public int Goal;
        public StepRole Role;
        public LegMode Mode;
        public RouteStop From;
        public RouteStop To;
        public Leg Leg;
        // The lander is refilled from the rest's tanks once it has docked.
        public bool RefuelAfter;
        // A flight of the ship to its destination while the lander was away: it came
        // back first, or stayed (RouteStop.LanderStays).
        public bool LanderAway;
        // What makes the step impossible as set, for the player; null where none.
        public string Problem;

        public int Segment
        {
            get { return Goal - 1; }
        }
    }

    // The steps between the destinations, from who flies to each (RouteStop.Vehicle)
    // (docs/development/planner.md). The whole vessel flies to a destination of the
    // ship; a lander away comes back to the rest first, and docks. The lander flies
    // to a destination of the lander from where the rest waits: the rest carries it
    // to an orbit of the destination's body first where it is not there yet, or into
    // the air above it for a landing with parachutes, and is left there. A lander
    // already on that body flies there by itself. Free of Unity, tested.
    internal static class RouteSteps
    {
        // Finding the rest in its orbit and docking, over the climb to that orbit.
        internal const double Rendezvous = 50.0;

        internal static List<RouteStep> Build(IList<PlannerBody> bodies, IList<RouteStop> goals, bool hasLander,
                                              bool planeChanges)
        {
            List<RouteStep> steps = new List<RouteStep>();
            if (goals.Count == 0) return steps;
            RouteStop ship = goals[0];
            RouteStop lander = null;
            bool landerLeft = false;
            string landerLeftAt = null;

            for (int g = 1; g < goals.Count; g++)
            {
                RouteStop goal = goals[g];
                Vehicle vehicle = hasLander ? goal.Vehicle : Vehicle.Ship;

                if (vehicle == Vehicle.Ship)
                {
                    bool away = lander != null && !landerLeft;
                    if (away)
                    {
                        if (goal.LanderStays)
                        {
                            landerLeft = true;
                            landerLeftAt = Letter(g - 1);
                        }
                        else
                        {
                            steps.Add(Return(bodies, g, lander, ship, goal.Refuel, planeChanges));
                        }
                        lander = null;
                    }
                    RouteStep main = Step(bodies, g, StepRole.Main, landerLeft ? LegMode.WithoutLander : LegMode.Whole, ship, goal,
                        planeChanges);
                    main.LanderAway = away;
                    steps.Add(main);
                    ship = goal;
                    continue;
                }

                if (landerLeft)
                {
                    RouteStep stuck = Step(bodies, g, StepRole.Main, LegMode.Lander, goal, goal, planeChanges);
                    stuck.Problem = "The lander stayed at " + landerLeftAt;
                    // Never flown: it costs nothing.
                    stuck.Leg = new Leg { From = g - 1, To = g };
                    steps.Add(stuck);
                    continue;
                }

                if (lander != null)
                {
                    if (lander.Body == goal.Body)
                    {
                        // A hop on the same body.
                        steps.Add(Step(bodies, g, StepRole.Main, LegMode.Lander, lander, goal, planeChanges));
                        lander = goal;
                        continue;
                    }
                    steps.Add(Return(bodies, g, lander, ship, goal.Refuel, planeChanges));
                    lander = null;
                }

                RouteStop approach = ApproachFor(bodies, goal, ship);
                if (approach != null)
                {
                    steps.Add(Step(bodies, g, StepRole.Approach, LegMode.Whole, ship, approach, planeChanges));
                    ship = approach;
                }
                steps.Add(Step(bodies, g, StepRole.Main, LegMode.Lander, ship, goal, planeChanges));
                lander = goal;
            }
            return steps;
        }

        // Where the rest leaves the lander for this destination; null where the rest
        // is there already, or waits where it is while the lander flies to an orbit.
        private static RouteStop ApproachFor(IList<PlannerBody> bodies, RouteStop goal, RouteStop ship)
        {
            if (goal.Kind != StopKind.Surface) return null;
            if (ship.Body == goal.Body && ship.Kind != StopKind.Entry) return null;
            PlannerBody body = bodies[goal.Body];
            if (goal.UseAtmosphere && body.HasAtmosphere && !goal.OrbitFirst)
                return new RouteStop { Body = goal.Body, Kind = StopKind.Entry };
            return new RouteStop
            {
                Body = goal.Body, Kind = StopKind.Orbit, Altitude = body.LowOrbitAltitude,
                UseAtmosphere = goal.UseAtmosphere && body.HasAtmosphere,
            };
        }

        // The lander back to the rest: up to its orbit, then finding it and docking.
        private static RouteStep Return(IList<PlannerBody> bodies, int goal, RouteStop lander, RouteStop ship, bool refuel,
                                        bool planeChanges)
        {
            RouteStep step = Step(bodies, goal, StepRole.Return, LegMode.Lander, lander, ship, planeChanges);
            step.RefuelAfter = refuel;
            if (ship.Kind == StopKind.Entry)
                step.Problem = "The rest went into the air at " + bodies[ship.Body].Name;
            else if (ship.Kind == StopKind.Orbit)
                step.Leg.Add("Rendezvous and docking", Rendezvous);
            return step;
        }

        private static RouteStep Step(IList<PlannerBody> bodies, int goal, StepRole role, LegMode mode, RouteStop from,
                                      RouteStop to, bool planeChanges)
        {
            return new RouteStep
            {
                Goal = goal, Role = role, Mode = mode, From = from, To = to,
                Leg = RoutePlanner.LegOf(bodies, from, to, goal - 1, planeChanges),
            };
        }

        private static string Letter(int index)
        {
            return ((char)('A' + index)).ToString();
        }
    }
}
