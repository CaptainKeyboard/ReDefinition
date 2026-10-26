using System.Collections.Generic;
using System;

namespace ReDefinition.Engineer
{
    // The steps on the way from one destination to the next, each to a place of its
    // own. They go up from the surface into an orbit, across to the next body's orbit or
    // straight into its air (or from one orbit to another around the same body), and
    // down onto the surface. Where the player splits the way across, Transfer is the
    // burn that leaves and Arrive the burns after it.
    internal enum StepRole
    {
        // Up from a surface into an orbit; the way across, or the burn that leaves where it is split; the burns after it;
        // down onto the surface.
        Launch,
        Transfer,
        Arrive,
        Land,
    }

    // What a section of the vessel is for (PlannerState.Sections): it only sets what
    // the planner has it do by itself.
    internal enum SectionKind
    {
        // Capsule: the crew's way home, which leaves everything else for good; Lander: lands where the rest waits; Satellite:
        // stays at its destination.
        Capsule,
        Lander,
        Satellite,
    }

    // A section of the vessel as the route needs it.
    internal sealed class RouteSection
    {
        // 1 and up; 0 is the rest.
        public int Id;
        public SectionKind Kind;
        // Its name for the player: "Lander", "Satellite 2".
        public string Name = "";
        // The section it is part of, 0 for the rest.
        public int Parent;
        // By itself, it comes back to what carried it and docks: the whole vessel flies
        // on after it where it meets the rest (a docking port separates it).
        public bool Docks;
        // It has a docking port, and the rest one to dock with.
        public bool CanDock = true;
        // A satellite: the destination it stays at, -1 for none.
        public int DeployAt = -1;
    }

    // Who flies a step, as Flyer holds it.
    internal enum FlyerKind
    {
        // The whole vessel; one section, with the sections part of it; the vessel leaving a section behind for good.
        Ship,
        Alone,
        Without,
    }

    // Who flies a step: the whole vessel; one section, with the sections that are part
    // of it; or the vessel leaving a section where it is for good. Kept in a stop's By
    // as a number: 0 the whole vessel, 1000 and 2000 on a section's id.
    internal struct Flyer
    {
        // Who, and the section's id for Alone and Without.
        public FlyerKind Kind;
        public int Section;

        // The whole vessel flies it.
        public static readonly Flyer Ship = new Flyer();

        // One section flies, with the sections part of it.
        public static Flyer Alone(int section)
        {
            return new Flyer { Kind = FlyerKind.Alone, Section = section };
        }

        // The vessel flies on, leaving the section behind for good.
        public static Flyer Without(int section)
        {
            return new Flyer { Kind = FlyerKind.Without, Section = section };
        }

        // As kept in a stop's By: 0, or 1000 or 2000 on the section's id.
        public int Code
        {
            get { return Kind == FlyerKind.Ship ? 0 : (Kind == FlyerKind.Alone ? 1000 : 2000) + Section; }
        }

        // From the number kept in a stop's By.
        public static Flyer FromCode(int code)
        {
            if (code >= 2000) return Without(code - 2000);
            if (code >= 1000) return Alone(code - 1000);
            return Ship;
        }

        // Whether it is of that kind, and for a section alone or left, that section.
        public bool Is(FlyerKind kind, int section)
        {
            return Kind == kind && (kind == FlyerKind.Ship || Section == section);
        }
    }

    // One step of the route: who flies it, from where to where, what it costs, and
    // what leaves, docks or stays on it.
    internal sealed class RouteStep
    {
        // The destination it leads to; the leg is Goal - 1.
        public int Goal;
        public StepRole Role;
        public Flyer Flyer;
        // Set by the player rather than worked out (RouteStop.By).
        public bool Chosen;
        public RouteStop From;
        public RouteStop To;
        public Leg Leg;
        // At its start, a section leaves what carried it, or the vessel leaves a
        // section behind: the section, and what it leaves (0 the rest, else a section).
        public bool Separates;
        public int Leaver;
        public int LeavesFrom;
        // At its end, the section flying it docks with what carried it, and is refilled
        // from the rest's tanks where the player wants it.
        public bool Docks;
        public bool WantsRefuel;
        public bool RefuelAfter;
        // What flies it: the rest (0) and sections, by id; the sections gone for good
        // as it starts; the sections deployed at its end.
        public readonly HashSet<int> Flying = new HashSet<int>();
        public readonly List<GoneSection> Gone = new List<GoneSection>();
        public readonly List<int> Deploys = new List<int>();
        // What makes the step impossible as set, for the player; null where none.
        public string Problem;

        // The leg it belongs to, counted from 0.
        public int Segment
        {
            get { return Goal - 1; }
        }
    }

    // A section, or the rest (0), gone for good as a step starts (StageBudget.GoneGroup).
    internal sealed class GoneSection
    {
        // The section, 0 for the rest; whether it stays as a vessel of its own; whether it waited elsewhere rather than flew
        // with what flies.
        public int Section;
        public bool Stays;
        public bool Waiting;
    }

    // The steps of a route (src/Engineer/README.md). Between two destinations
    // come the steps the way needs, each flown by the whole vessel, one of its
    // sections alone, or the vessel leaving a section behind. A section alone takes the
    // sections that are part of it along. Where the whole vessel flies the step after
    // it, the section docks first. Where the vessel leaves it behind then, it docks and is
    // left from there. A satellite stays at its destination. A capsule leaves
    // everything else for good. Who flies each step is worked out first, as a
    // section's climb goes to the rest's own orbit only where it docks after it. Who flies a step is the
    // player's choice (RouteStop.By), or else Default's. A section climbs to the rest's own
    // orbit where the rest waits around the body it starts from and the whole vessel flies
    // on after it. Free of Unity, tested.
    internal static class RouteSteps
    {
        // Finding the rest in its orbit and docking, over the climb to that orbit.
        internal const double Rendezvous = 50.0;

        // Where one section is and what became of it, as the steps are walked through.
        private sealed class SectionState
        {
            // The section as the route has it.
            internal RouteSection Section;
            // Flying on its own, rather than part of what carries it.
            internal bool Away;
            // Left for good, and where, in words for the player; where it is while away; the last step it flew.
            internal bool Gone;
            internal string GoneAt;
            internal RouteStop At;
            internal RouteStep LastStep;
        }

        // The rest and the sections as the steps are walked through, one destination after the other.
        private sealed class Walk
        {
            // Where the rest is; each section's state, by id and in the player's order; who flew the step before.
            internal RouteStop Rest;
            internal readonly Dictionary<int, SectionState> Sections = new Dictionary<int, SectionState>();
            internal readonly List<SectionState> Order = new List<SectionState>();
            internal Flyer Last = Flyer.Ship;
            // The capsule that has left everything else, 0 for none.
            internal int CapsuleOut;
            // The step being flown, and whether a section's climb on it is followed by
            // its docking; null while who flies is still being worked out, when a
            // section that docks by itself is taken to.
            internal int Index;
            internal System.Func<int, bool> Returns;

            // The section's state; null for the rest (0) or an id the vessel does not have.
            internal SectionState Of(int id)
            {
                SectionState state;
                return Sections.TryGetValue(id, out state) ? state : null;
            }

            // Where a section is: where it flew to, or wherever what carries it is.
            internal RouteStop Location(int id)
            {
                for (int depth = 0; depth < 32; depth++)
                {
                    SectionState state = Of(id);
                    if (state == null) return Rest;
                    if (state.Away) return state.At;
                    id = state.Section.Parent;
                }
                return Rest;
            }

            // The section, or the rest (0), and every section still part of it.
            internal HashSet<int> Carried(int top)
            {
                HashSet<int> set = new HashSet<int> { top };
                bool grew = true;
                while (grew)
                {
                    grew = false;
                    foreach (SectionState state in Order)
                        if (!state.Gone && !state.Away && !set.Contains(state.Section.Id) && set.Contains(state.Section.Parent))
                        {
                            set.Add(state.Section.Id);
                            grew = true;
                        }
                }
                return set;
            }

            // The section a section is part of that flies on its own, or the rest (0).
            internal int Carrier(int id)
            {
                SectionState state = Of(id);
                for (int depth = 0; state != null && depth < 32; depth++)
                {
                    int parent = state.Section.Parent;
                    SectionState up = Of(parent);
                    if (up == null) return 0;
                    if (up.Away) return parent;
                    state = up;
                }
                return 0;
            }
        }

        // The route's steps; with sections, walked through twice: who flies each step first, then the steps again with each
        // section's climb going to the rest where it docks after it.
        internal static List<RouteStep> Build(IList<PlannerBody> bodies, IList<RouteStop> goals, IList<RouteSection> sections,
                                              bool planeChanges)
        {
            // Who flies each step first; then the steps again with those, each climb of a
            // section going to the rest where the step after it docks it.
            List<RouteStep> first = Walkthrough(bodies, goals, sections, planeChanges, null, null);
            if (sections == null || sections.Count == 0) return first;
            List<RouteStep> flown = first;
            return Walkthrough(bodies, goals, sections, planeChanges, first, index => Rejoins(flown, sections, index));
        }

        // Whether a section that flies step `index` docks after it. That is so when the next step of
        // the whole vessel, or the one that leaves it behind, comes before it flies again itself or
        // a capsule leaves. The steps of other sections in between wait with it.
        private static bool Rejoins(List<RouteStep> flown, IList<RouteSection> sections, int index)
        {
            if (index >= flown.Count || flown[index].Flyer.Kind != FlyerKind.Alone) return false;
            int id = flown[index].Flyer.Section;
            for (int next = index + 1; next < flown.Count; next++)
            {
                Flyer flyer = flown[next].Flyer;
                if (flyer.Kind != FlyerKind.Alone) return true;
                if (flyer.Section == id) return false;
                foreach (RouteSection section in sections)
                    if (section != null && section.Id == flyer.Section && section.Kind == SectionKind.Capsule) return false;
            }
            return false;
        }

        // The steps, their flyers the player's or the planner's; or, with `flown`, the
        // ones worked out before.
        private static List<RouteStep> Walkthrough(IList<PlannerBody> bodies, IList<RouteStop> goals, IList<RouteSection> sections,
                                                   bool planeChanges, List<RouteStep> flown, System.Func<int, bool> returns)
        {
            List<RouteStep> steps = new List<RouteStep>();
            if (goals.Count == 0) return steps;
            Walk walk = new Walk { Rest = goals[0], Returns = returns };
            if (sections != null)
                foreach (RouteSection section in sections)
                {
                    if (section == null || section.Id <= 0 || walk.Sections.ContainsKey(section.Id)) continue;
                    SectionState state = new SectionState { Section = section, At = goals[0] };
                    walk.Sections[section.Id] = state;
                    walk.Order.Add(state);
                }

            for (int g = 1; g < goals.Count; g++)
            {
                RouteStop from = goals[g - 1];
                RouteStop goal = goals[g];
                List<StepRole> roles = RolesBetween(bodies, from, goal);
                foreach (StepRole role in roles)
                {
                    int by = goal.By[(int)role];
                    Flyer chosen = Flyer.FromCode(by);
                    bool isChosen = by >= 0 && Available(walk, chosen);
                    Flyer flyer = flown != null && steps.Count < flown.Count ? flown[steps.Count].Flyer
                        : isChosen ? chosen : Default(bodies, walk, role, from, goal, g == goals.Count - 1);
                    walk.Index = steps.Count;
                    RouteStep step = Fly(bodies, walk, g, role, flyer, from, goal, planeChanges);
                    step.Chosen = isChosen;
                    step.WantsRefuel = goal.Refuel[(int)role];
                    steps.Add(step);
                }
                if (roles.Count > 0) DeployAt(bodies, walk, steps[steps.Count - 1], g);
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

        // Who the vessel has to fly a step: the whole vessel, or a section it has.
        private static bool Available(Walk walk, Flyer flyer)
        {
            return flyer.Kind == FlyerKind.Ship || walk.Of(flyer.Section) != null;
        }

        // Who flies a step the player left to the planner. The capsule flies once it has left.
        // The capsule flies a landing on parachutes on the last destination, where what carries it
        // is in the air already. A lander flies a landing short of the route's end. From a surface,
        // whoever stands on it flies. Across to another body, the whole vessel flies where a lander
        // that docks waits in an orbit of the body the rest is at. Otherwise whoever flew the step
        // before flies it.
        private static Flyer Default(IList<PlannerBody> bodies, Walk walk, StepRole role, RouteStop from, RouteStop goal, bool last)
        {
            if (walk.CapsuleOut != 0) return Flyer.Alone(walk.CapsuleOut);
            // Home on the parachutes: the capsule, where what carries it is in the air
            // already; from an orbit it would have to bring itself down.
            if (last && role == StepRole.Land && goal.UseAtmosphere && bodies[goal.Body].HasAtmosphere)
                foreach (SectionState state in walk.Order)
                    if (state.Section.Kind == SectionKind.Capsule && !state.Gone && walk.Location(state.Section.Id).Kind == StopKind.Entry)
                        return Flyer.Alone(state.Section.Id);
            // A landing short of the route's end: a lander's, the rest waiting in orbit,
            // as that is what a lander is for; the one that flew last, else the first.
            if (role == StepRole.Land && !last)
            {
                SectionState flew = walk.Last.Kind == FlyerKind.Alone ? walk.Of(walk.Last.Section) : null;
                if (flew != null && flew.Section.Kind == SectionKind.Lander && !flew.Gone) return walk.Last;
                foreach (SectionState state in walk.Order)
                    if (state.Section.Kind == SectionKind.Lander && !state.Gone) return Flyer.Alone(state.Section.Id);
            }
            if (role == StepRole.Launch)
            {
                // Whoever stands on that surface: the one that flew last, the whole
                // vessel, or another section there.
                if (OnSurface(walk.Last.Kind == FlyerKind.Alone ? walk.Location(walk.Last.Section) : walk.Rest, from)
                    && Usable(walk, walk.Last))
                    return walk.Last;
                if (OnSurface(walk.Rest, from)) return Flyer.Ship;
                foreach (SectionState state in walk.Order)
                    if (!state.Gone && state.Away && OnSurface(state.At, from)) return Flyer.Alone(state.Section.Id);
                return Usable(walk, walk.Last) ? walk.Last : Flyer.Ship;
            }
            // A section that docks by itself, in an orbit of the body the rest waits at:
            // it docks, and the whole vessel flies on.
            if (role == StepRole.Transfer && walk.Last.Kind == FlyerKind.Alone)
            {
                SectionState flew = walk.Of(walk.Last.Section);
                if (flew != null && flew.Section.Docks && flew.Away && !flew.Gone && walk.Carrier(flew.Section.Id) == 0
                    && Meets(flew.At, walk.Rest))
                    return Flyer.Ship;
            }
            return Usable(walk, walk.Last) ? walk.Last : Flyer.Ship;
        }

        // Still there to fly: the whole vessel, or a section not left for good.
        private static bool Usable(Walk walk, Flyer flyer)
        {
            if (flyer.Kind != FlyerKind.Alone) return true;
            SectionState state = walk.Of(flyer.Section);
            return state != null && !state.Gone;
        }

        // Whether `at` stands on the surface of the body the step starts from.
        private static bool OnSurface(RouteStop at, RouteStop from)
        {
            return at != null && at.Kind == StopKind.Surface && at.Body == from.Body;
        }

        // The step as `flyer` flies it: a section leaving or left behind as it starts, the sections away docking as the whole
        // vessel flies on, where it goes and its burns; and the walk moved on with it.
        private static RouteStep Fly(IList<PlannerBody> bodies, Walk walk, int g, StepRole role, Flyer flyer, RouteStop from,
                                     RouteStop goal, bool planeChanges)
        {
            RouteStep step = new RouteStep { Goal = g, Role = role, Flyer = flyer };

            // Once the capsule has left, only it flies.
            if (walk.CapsuleOut != 0 && !flyer.Is(FlyerKind.Alone, walk.CapsuleOut))
                return Nothing(step, walk, "The capsule has left the vessel", walk.Location(walk.CapsuleOut), g, flyer);

            RouteStop start;
            if (flyer.Kind == FlyerKind.Alone)
            {
                SectionState state = walk.Of(flyer.Section);
                if (state.Gone) return Nothing(step, walk, "The " + Lower(state.Section.Name) + " was left " + state.GoneAt,
                    Target(bodies, walk, role, from, goal, flyer), g, flyer);
                if (!state.Away)
                {
                    // It leaves whatever carries it.
                    int carrier = walk.Carrier(flyer.Section);
                    step.Separates = true;
                    step.Leaver = flyer.Section;
                    step.LeavesFrom = carrier;
                    state.At = walk.Location(carrier);
                    state.Away = true;
                }
                if (state.Section.Kind == SectionKind.Capsule && walk.CapsuleOut == 0) LeaveForCapsule(bodies, walk, step, state);
                start = state.At;
            }
            else
            {
                // Where the vessel leaves a section behind that flew the step before and is
                // back where the rest waits, it docks first, as a lander whose crew comes
                // over.
                if (flyer.Kind == FlyerKind.Without)
                {
                    SectionState left = walk.Of(flyer.Section);
                    if (left != null && walk.Last.Is(FlyerKind.Alone, flyer.Section) && Returns(walk, left)) Dock(bodies, walk, left);
                    LeaveBehind(bodies, walk, step, flyer.Section);
                }
                // Every section away comes back as the whole vessel flies on: it docks with
                // the rest, in the rest's orbit or on its way to it from another one around
                // the same body; anywhere else it is not where the rest waits.
                foreach (SectionState state in walk.Order)
                {
                    if (!state.Away || state.Gone || walk.Carrier(state.Section.Id) != 0) continue;
                    if (Returns(walk, state))
                    {
                        Dock(bodies, walk, state);
                        if (!state.Section.CanDock)
                            step.Problem = step.Problem ?? "The " + Lower(state.Section.Name) + " has no docking port";
                    }
                    else step.Problem = step.Problem ?? "The " + Lower(state.Section.Name) + " is not with the rest of the vessel "
                                   + At(bodies, walk.Rest);
                    state.Away = false;
                }
                start = walk.Rest;
            }

            // In the air, all that is left is the landing.
            if (start.Kind == StopKind.Entry && role != StepRole.Land)
                step.Problem = step.Problem ?? "The " + (flyer.Kind == FlyerKind.Alone ? Lower(walk.Of(flyer.Section).Section.Name) : "rest")
                               + " is already in " + Place(bodies, start);

            RouteStop to = Target(bodies, walk, role, from, goal, flyer);
            if (to.Kind == StopKind.Transfer) to.Origin = start;
            step.From = start;
            step.To = to;
            step.Leg = RoutePlanner.LegOf(bodies, start, to, g - 1, planeChanges);
            step.Flying.UnionWith(walk.Carried(flyer.Kind == FlyerKind.Alone ? flyer.Section : 0));

            if (flyer.Kind == FlyerKind.Alone)
            {
                SectionState state = walk.Of(flyer.Section);
                state.At = to;
                state.LastStep = step;
            }
            else
            {
                walk.Rest = to;
            }
            walk.Last = flyer;
            return step;
        }

        // A step that cannot be flown: no burns, and why.
        private static RouteStep Nothing(RouteStep step, Walk walk, string problem, RouteStop at, int g, Flyer flyer)
        {
            step.Problem = problem;
            step.From = step.To = at;
            step.Leg = new Leg { From = g - 1, To = g };
            walk.Last = flyer;
            return step;
        }

        // The vessel flies on without a section, which stays where it is for good, with
        // the sections part of it.
        private static void LeaveBehind(IList<PlannerBody> bodies, Walk walk, RouteStep step, int id)
        {
            SectionState state = walk.Of(id);
            if (state == null || state.Gone) return;
            bool attached = !state.Away;
            RouteStop at = walk.Location(id);
            if (attached)
            {
                step.Separates = true;
                step.Leaver = id;
                step.LeavesFrom = walk.Carrier(id);
            }
            foreach (int gone in walk.Carried(id))
            {
                SectionState part = walk.Of(gone);
                if (part == null || part.Gone) continue;
                part.Gone = true;
                part.GoneAt = At(bodies, at);
                step.Gone.Add(new GoneSection { Section = gone, Stays = true });
            }
            state.At = at;
        }

        // The capsule leaves everything else for good: what carried it is dropped, what
        // waits elsewhere stays there.
        private static void LeaveForCapsule(IList<PlannerBody> bodies, Walk walk, RouteStep step, SectionState capsule)
        {
            int carrier = step.Separates ? step.LeavesFrom : walk.Carrier(capsule.Section.Id);
            HashSet<int> with = walk.Carried(carrier);
            HashSet<int> keeps = walk.Carried(capsule.Section.Id);
            keeps.Add(capsule.Section.Id);
            walk.CapsuleOut = capsule.Section.Id;
            step.Gone.Add(new GoneSection { Section = 0, Waiting = !with.Contains(0) });
            foreach (SectionState state in walk.Order)
            {
                int id = state.Section.Id;
                if (keeps.Contains(id) || state.Gone) continue;
                state.Gone = true;
                state.GoneAt = At(bodies, walk.Location(id));
                step.Gone.Add(new GoneSection { Section = id, Waiting = !with.Contains(id) });
            }
        }

        // Back where the rest waits, to dock: in its orbit, or in another orbit of the
        // same body.
        private static bool Returns(Walk walk, SectionState state)
        {
            return state.LastStep != null && (SamePlace(state.At, walk.Rest) || Meets(state.At, walk.Rest));
        }

        // A section docks with the rest at the end of its last step: the burn to the
        // rest's orbit where it ended in another one, and the rendezvous.
        private static void Dock(IList<PlannerBody> bodies, Walk walk, SectionState state)
        {
            RouteStep last = state.LastStep;
            last.Docks = true;
            last.RefuelAfter = last.WantsRefuel;
            if (!SamePlace(state.At, walk.Rest))
            {
                double up, down;
                PlannerBody body = bodies[walk.Rest.Body];
                RoutePlanner.Hohmann(body.Mu, body.Radius + state.At.Altitude, body.Radius + walk.Rest.Altitude, out up, out down);
                last.Leg.Add("Transfer to the rest's orbit", up, walk.Rest.Body, body.Radius + state.At.Altitude);
                last.Leg.Add("Match the rest's orbit", down, walk.Rest.Body, body.Radius + walk.Rest.Altitude);
            }
            if (walk.Rest.Kind == StopKind.Orbit) last.Leg.Add("Rendezvous and docking", Rendezvous);
            state.Away = false;
        }

        // Satellites that stay at this destination stay there, whether carried there or
        // flown there themselves, with the sections part of them; a lander part of one
        // leaves it there to fly on its own.
        private static void DeployAt(IList<PlannerBody> bodies, Walk walk, RouteStep step, int g)
        {
            foreach (SectionState state in walk.Order)
            {
                if (state.Section.Kind != SectionKind.Satellite || state.Section.DeployAt != g || state.Gone) continue;
                int id = state.Section.Id;
                if (step.Flyer.Is(FlyerKind.Alone, id) || step.Flying.Contains(id))
                {
                    foreach (int part in walk.Carried(id))
                    {
                        SectionState deployed = walk.Of(part);
                        if (deployed == null) continue;
                        if (part != id && deployed.Section.Kind == SectionKind.Lander)
                        {
                            deployed.Away = true;
                            deployed.At = step.To;
                            continue;
                        }
                        deployed.Gone = true;
                        deployed.GoneAt = At(bodies, step.To);
                        step.Deploys.Add(part);
                    }
                }
                else
                {
                    step.Problem = step.Problem ?? "The " + Lower(state.Section.Name) + " does not reach " + Place(bodies, step.To);
                }
            }
        }

        // Where a step goes. It is the destination itself where the step reaches it. It is the
        // body's low orbit, or the rest's own orbit where a section climbs to it to dock
        // after. It is into the air for a landing on parachutes straight from the way across.
        // It is a point on the way across, where the player splits it.
        private static RouteStop Target(IList<PlannerBody> bodies, Walk walk, StepRole role, RouteStop from, RouteStop goal, Flyer flyer)
        {
            if (role == StepRole.Land) return goal;
            if (role == StepRole.Launch)
            {
                SectionState state = flyer.Kind == FlyerKind.Alone ? walk.Of(flyer.Section) : null;
                bool docks = state != null && (walk.Returns != null ? walk.Returns(walk.Index) : state.Section.Docks);
                if (state != null && docks && state.Away)
                {
                    RouteStop rest = walk.Location(state.Section.Parent);
                    if (rest.Kind == StopKind.Orbit && rest.Body == from.Body) return rest;
                }
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

        // The body's low orbit, as a stop.
        private static RouteStop LowOrbit(IList<PlannerBody> bodies, int body)
        {
            return new RouteStop { Body = body, Kind = StopKind.Orbit, Altitude = bodies[body].LowOrbitAltitude };
        }

        // Both in orbits of the same body: the one can reach the other.
        private static bool Meets(RouteStop a, RouteStop b)
        {
            return a != null && b != null && a.Body == b.Body && a.Kind == StopKind.Orbit && b.Kind == StopKind.Orbit;
        }

        // Whether two stops are one place: the same body and kind, and for orbits altitudes within a tenth or a kilometre.
        internal static bool SamePlace(RouteStop a, RouteStop b)
        {
            if (a == null || b == null || a.Body != b.Body || a.Kind != b.Kind) return false;
            return a.Kind != StopKind.Orbit || Math.Abs(a.Altitude - b.Altitude) <= Math.Max(1000.0, a.Altitude * 0.1);
        }

        // The section's name as it reads inside a sentence, its first letter small; "section" for none.
        private static string Lower(string name)
        {
            return string.IsNullOrEmpty(name) ? "section" : char.ToLowerInvariant(name[0]) + name.Substring(1);
        }

        // "on the Mun", "in Kerbin orbit", "in Kerbin's atmosphere".
        internal static string At(IList<PlannerBody> bodies, RouteStop stop)
        {
            string name = bodies[stop.Body].Name;
            if (stop.Kind != StopKind.Surface) return "in " + Place(bodies, stop);
            return "on " + (name.StartsWith("The ", StringComparison.Ordinal) ? "the " + name.Substring(4) : name);
        }

        // "Mun surface", "Kerbin orbit", "Kerbin's atmosphere": a body named with its
        // article, as KSP names the Mun, without it.
        internal static string Place(IList<PlannerBody> bodies, RouteStop stop)
        {
            string name = bodies[stop.Body].Name;
            if (name.StartsWith("The ", StringComparison.Ordinal)) name = name.Substring(4);
            switch (stop.Kind)
            {
                case StopKind.Surface: return name + " surface";
                case StopKind.Entry: return name + "'s atmosphere";
                case StopKind.Transfer: return name + " transfer orbit";
                default: return name + " orbit";
            }
        }
    }
}
