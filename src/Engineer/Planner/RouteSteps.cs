using System.Collections.Generic;
using System;

namespace ReDefinition.Engineer
{
    // The steps on the way from one destination to the next, each to a place of its
    // own: up from the surface into an orbit, across to the next body's orbit or
    // straight into its air (or from one orbit to another around the same body), and
    // down onto the surface. Where the player splits the way across, Transfer is the
    // burn that leaves and Arrive the burns after it.
    internal enum StepRole
    {
        Launch,
        Transfer,
        Arrive,
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
    // alone, the rest without the lander, or the capsule alone. The capsule is part
    // of the lander or of the rest, and leaves whatever carries it for good; a landing on parachutes on the last destination
    // is the capsule's where the vessel has one. Who flies a step is the player's choice
    // (RouteStop.By), or else: from a surface whoever stands on it; across to another
    // body the whole vessel where the lander waits with the rest, so that it docks;
    // a landing short of the route's end the lander; otherwise whoever flew the step
    // before. From that, the lander leaves the rest
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
            internal RouteStop Capsule;
            internal bool HasLander;
            internal bool CapsuleInLander;
        }

        // Where the capsule is while it has not left: with the lander away where it is
        // part of the lander, or with the rest.
        private static RouteStop Carrier(Walk walk)
        {
            return walk.CapsuleInLander && walk.Last == Vehicle.Lander && !walk.Together && !walk.LanderLeft
                ? walk.Lander
                : walk.Rest;
        }

        // `capsuleInLander`: the capsule is part of the lander rather than of the rest.
        internal static List<RouteStep> Build(IList<PlannerBody> bodies, IList<RouteStop> goals, bool hasLander,
                                              bool planeChanges, bool hasCapsule = false, bool capsuleInLander = true)
        {
            List<RouteStep> steps = new List<RouteStep>();
            if (goals.Count == 0) return steps;
            Walk walk = new Walk
            {
                Rest = goals[0], Lander = goals[0], HasLander = hasLander, CapsuleInLander = hasLander && capsuleInLander,
            };

            for (int g = 1; g < goals.Count; g++)
            {
                RouteStop from = goals[g - 1];
                RouteStop goal = goals[g];
                foreach (StepRole role in RolesBetween(bodies, from, goal))
                {
                    int by = goal.By[(int)role];
                    bool isChosen = by >= 0 && Available((Vehicle)by, hasLander, hasCapsule);
                    Vehicle vehicle = isChosen ? (Vehicle)by
                        : Default(bodies, walk, role, from, goal, g == goals.Count - 1, hasLander, hasCapsule);
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
        internal static List<StepRole> RolesBetween(IList<PlannerBody> bodies, RouteStop from, RouteStop goal)
        {
            List<StepRole> roles = new List<StepRole>();
            if (from.Kind == StopKind.Surface) roles.Add(StepRole.Launch);
            if (Crosses(from, goal))
            {
                roles.Add(StepRole.Transfer);
                if (goal.SplitBurns && Splits(bodies, from, goal)) roles.Add(StepRole.Arrive);
            }
            if (goal.Kind == StopKind.Surface) roles.Add(StepRole.Land);
            return roles;
        }

        // Across to another body, or from one orbit to another around this one; a
        // launch straight into the destination's orbit needs no more.
        private static bool Crosses(RouteStop from, RouteStop goal)
        {
            return from.Body != goal.Body || (from.Kind == StopKind.Orbit && goal.Kind == StopKind.Orbit);
        }

        // The way across has a burn that leaves and one that arrives, so that it can be
        // two steps: not straight into the air, not down into it by aerobraking.
        internal static bool Splits(IList<PlannerBody> bodies, RouteStop from, RouteStop goal)
        {
            if (!Crosses(from, goal)) return false;
            RouteStop start = from.Kind == StopKind.Surface ? LowOrbit(bodies, from.Body) : from;
            RouteStop end = Across(bodies, goal);
            if (end.Kind != StopKind.Orbit) return false;
            Leg leg = RoutePlanner.LegOf(bodies, start, end, 0, false);
            bool leaves = false, arrives = false;
            foreach (LegLine line in leg.Lines)
            {
                if (line.Name == RoutePlanner.LeavingBurn) leaves = true;
                else arrives = true;
            }
            return leaves && arrives;
        }

        // What the vessel has: a lander to fly alone or leave, a capsule.
        internal static bool Available(Vehicle vehicle, bool hasLander, bool hasCapsule)
        {
            switch (vehicle)
            {
                case Vehicle.Lander:
                case Vehicle.Rest:
                    return hasLander;
                case Vehicle.Capsule:
                    return hasCapsule;
                default:
                    return true;
            }
        }

        private static Vehicle Default(IList<PlannerBody> bodies, Walk walk, StepRole role, RouteStop from, RouteStop goal,
                                       bool last, bool hasLander, bool hasCapsule)
        {
            if (walk.Capsule != null) return Vehicle.Capsule;
            // Home on the parachutes: the capsule, where what carries it is in the air
            // already; from an orbit it would have to bring itself down.
            if (hasCapsule && last && role == StepRole.Land && goal.UseAtmosphere && bodies[goal.Body].HasAtmosphere
                && Carrier(walk).Kind == StopKind.Entry)
                return Vehicle.Capsule;
            if (!hasLander) return Vehicle.Ship;
            // A landing short of the route's end: the lander's, the rest waiting in
            // orbit, as that is what a lander is for.
            if (role == StepRole.Land && !last && !walk.LanderLeft) return Vehicle.Lander;
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

            if (vehicle == Vehicle.Capsule || walk.Capsule != null)
            {
                if (vehicle != Vehicle.Capsule)
                {
                    step.Problem = "The capsule has left the vessel";
                    step.Mode = vehicle == Vehicle.Lander ? LegMode.Lander : LegMode.Whole;
                    step.From = step.To = walk.Capsule;
                    step.Leg = new Leg { From = g - 1, To = g };
                    walk.Last = vehicle;
                    return step;
                }
                if (walk.Capsule == null && walk.LanderLeft && walk.CapsuleInLander)
                {
                    // The capsule is part of the lander, which stayed behind.
                    step.Problem = "The capsule stayed with the lander at " + walk.LanderLeftAt;
                    step.Mode = LegMode.Capsule;
                    step.From = step.To = walk.Rest;
                    step.Leg = new Leg { From = g - 1, To = g };
                    walk.Last = vehicle;
                    return step;
                }
                if (walk.Capsule == null)
                {
                    // It leaves whatever flew the step before.
                    step.Separates = true;
                    walk.Capsule = Carrier(walk);
                }
                step.Mode = LegMode.Capsule;
                RouteStop target = Target(bodies, walk, role, from, goal, vehicle);
                if (target.Kind == StopKind.Transfer) target.Origin = walk.Capsule;
                step.From = walk.Capsule;
                step.To = target;
                step.Leg = RoutePlanner.LegOf(bodies, walk.Capsule, target, g - 1, planeChanges);
                walk.Capsule = target;
                walk.Last = vehicle;
                return step;
            }

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
            if (to.Kind == StopKind.Transfer) to.Origin = start;
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
        // into the air for a landing on parachutes straight from the way across; on
        // the way across, where the player splits it.
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
            RouteStop across = Across(bodies, goal);
            if (role == StepRole.Transfer && goal.SplitBurns && Splits(bodies, from, goal))
                return new RouteStop { Body = across.Body, Kind = StopKind.Transfer, Final = across };
            return across;
        }

        // Where the way across ends: the destination's orbit, its air, or its low
        // orbit before a landing.
        private static RouteStop Across(IList<PlannerBody> bodies, RouteStop goal)
        {
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
                case StopKind.Transfer: return name + " transfer orbit";
                default: return name + " orbit";
            }
        }
    }
}
