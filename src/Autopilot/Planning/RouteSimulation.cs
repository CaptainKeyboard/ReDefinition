using System.Collections.Generic;
using ReDefinition.Orbits;
using UnityEngine;

namespace ReDefinition.Autopilot.Planning
{
    // One step of the plan ahead: what it is, when, and what it asks of the engines.
    internal sealed class PlannedStep
    {
        public NodePlanner.StepKind Kind;
        // The stop of the route the step leads to, by its index in the route.
        public int Goal;
        // The UT of the burn; zero for a step that is not a burn (a descent, a pass through the air, a release).
        public double UT;
        // The UT the step is made at: a burn's, and for a release the time of the state it is made in. Zero for a step that has no time.
        public double At;
        // The section a release lets go.
        public SectionRelease Section;
        // The altitude of the orbit an ascent ends in, in m.
        public double Altitude;
        // The vessel a meeting is made with, the docking port of it (its part's persistent id, zero for any), and whether the step ends with a docking.
        public System.Guid Target;
        public uint Port;
        public bool Dock;
        // Whether a pass through the air was flown in the simulation: the plan goes on from the orbit it leaves the air in.
        public bool Flown;
        // The burn in the axes of the maneuver node it is put in as.
        public V3 DeltaV;
        public string Why;
    }

    // A stretch of the plan on one orbit: the orbit, and from which UT to which it is flown.
    internal struct PlannedLeg
    {
        public Orbit Orbit;
        public double StartUT;
        public double EndUT;
        // Whether the orbit begins where a pass through the air ends: no burn made it.
        public bool Air;
    }

    // The route worked out from the vessel to its last stop, step after step: the plan the navigation guides along, the autopilot flies, the map
    // shows, and Parsek is given as a recording.
    internal sealed class RoutePlan
    {
        public readonly List<PlannedStep> Steps = new List<PlannedStep>();
        public readonly List<PlannedLeg> Legs = new List<PlannedLeg>();
        // The delta-v of all the burns in the plan, in m/s.
        public double DeltaV;
        // The last stop of the route the plan reaches, by its index; the count the vessel goes on with where it is taken out of the world.
        public int Reached = -1;
        // The stops reached when each state of the plan was met: its UT and the last stop reached by then.
        public readonly List<KeyValuePair<double, int>> ReachedAt = new List<KeyValuePair<double, int>>();
        // The route the plan was made for, as SignatureOf has it; a plan is not the one of a route whose signature differs.
        public string Signature;
        // The first stop the plan leads to, and whether the vessel was at the one before it.
        // Where it was not, the leg is under way and the burns already flown are not in the plan. -1 where the plan has no step.
        public int FirstGoal = -1;
        public bool FirstGoalWhole;
        // The UT the plan ends at: the end of its last burn, or of the last orbit it flies.
        public double EndUT;
        // The delta-v of the burns that lead to the stop at `goal`, in m/s.
        public double DeltaVOf(int goal)
        {
            double total = 0.0;
            foreach (PlannedStep step in Steps)
                if (step.Goal == goal && step.Kind == NodePlanner.StepKind.Burn) total += step.DeltaV.Length;
            return total;
        }

        // The stops of a route as a text: the body, whether it is landed on, the altitude and how the air is used, up to the first stop without a body.
        internal static string SignatureOf(IList<Destination> route)
        {
            var text = new System.Text.StringBuilder();
            if (route != null)
                foreach (Destination stop in route)
                {
                    if (stop.Body == null) break;
                    text.Append(stop.Body.bodyName).Append(stop.Surface ? "/s/" : "/o/").Append(stop.Altitude.ToString("0")).Append(stop.Direct ? "/d" : "").Append(stop.Aerobrake ? "/a" : "").Append(';');
                }
            return text.ToString();
        }

        // Why a pass through the air of a plan flown without the player cannot be made, where it cannot: the vessel is not taken through it.
        public string AirProblem;

        // Why the plan does not reach the last stop (the vessel lands, a pass through the air, no way found); null where it does.
        public string Ended;
        // Whether a step could not be planned, or the plan did not move on: the plan ends in a state the vessel does not reach.
        public bool Failed;
    }

    // Plans the whole route from the vessel's orbit by chaining the steps: each is planned from the state the one before it leaves, with its
    // nodes put in the flight plan, where KSP works out the orbits that follow. The nodes are taken off again; the plan is what is returned.
    internal static class RouteSimulation
    {
        // With `forPlayer` the deorbit of a landing is a burn of the plan, as the player flies it; without, the plan ends in the orbit the landing
        // starts from, which MechJeb's landing leaves by itself.
        // With `from` the plan starts in that orbit at `fromUT`, as if the vessel were there: the launch of an ascent is not flown by the plan.
        // With `airPasses` the passes through the air are flown in the simulation, and the plan goes on from the orbit each leaves the air in: a plan
        // the vessel flies without the player. Without, a pass ends the plan.
        internal static RoutePlan Run(Vessel vessel, IList<Destination> route, bool forPlayer = false, Orbit from = null, double fromUT = 0.0, int reachedAtStart = int.MinValue,
                                      bool airPasses = false)
        {
            var plan = new RoutePlan { Signature = RoutePlan.SignatureOf(route) };
            if (vessel == null || route == null || route.Count < 2 || vessel.patchedConicSolver == null)
            {
                plan.Failed = true;
                plan.Ended = "There is no route to plan.";
                return plan;
            }
            // The nodes the vessel holds are put back when the plan is done.
            var held = new List<KeyValuePair<double, Vector3d>>();
            foreach (ManeuverNode node in vessel.patchedConicSolver.maneuverNodes) held.Add(new KeyValuePair<double, Vector3d>(node.UT, node.DeltaV));
            KspOrbits.ClearNodes(vessel);
            // The vessel's own orbit, put back when the plan is done.
            Vector3d ownPosition = Vector3d.zero, ownVelocity = Vector3d.zero;
            double ownTime = Planetarium.GetUniversalTime();
            CelestialBody ownBody = vessel.orbit.referenceBody;
            bool swapped = false;
            try
            {
                if (from != null)
                {
                    ownPosition = vessel.orbit.getRelativePositionAtUT(ownTime);
                    ownVelocity = vessel.orbit.getOrbitalVelocityAtUT(ownTime);
                    swapped = true;
                    vessel.orbit.UpdateFromOrbitAtUT(from, fromUT, from.referenceBody);
                    vessel.patchedConicSolver.Update();
                }
                Frame frame = swapped ? new Frame(vessel, airPasses).At(fromUT) : new Frame(vessel, airPasses);
                int passes = 0;
                // From where the flight plan is collected into the plan's legs, and the UT a leg begins at where a pass through the air ended.
                double collectedFrom = ownTime;
                double airLegAt = -1.0;
                Orbit airOrbit = null;
                double airEntryUT = -1.0;
                // Counted from where the vessel is, on a copy: the plan does not move the vessel's own count.
                RouteState own = RouteState.Of(vessel);
                RouteProgress progress = new RouteProgress { Reached = reachedAtStart != int.MinValue ? reachedAtStart : own != null ? own.ProgressFor(route).Reached : -1, Key = plan.Signature };
                // The states the plan has been in: one met again is a plan that goes round in a circle.
                var seen = new HashSet<string>();
                // How far along the vessel is, as the plan's first step found it; the vessel's own count goes up to it.
                int atStart = -2;
                while (true)
                {
                    if (!seen.Add(StateKey(frame) + "|" + progress.Released.Count))
                    {
                        plan.Failed = true;
                        plan.Ended = "The plan does not move on.";
                        break;
                    }
                    NodePlanner.Result result = NextStepPlanner.PlanNext(frame, route, forPlayer, false, progress);
                    if (atStart == -2) atStart = progress.Reached;
                    if (airPasses)
                        Debug.Log("[ReDefinition] Route: air plan at UT " + frame.Now.ToString("0") + " on " + frame.Orbit.PeA.ToString("0") + " x " + frame.Orbit.ApA.ToString("0") + " m round "
                                  + frame.Body.bodyName + ": " + result.Kind + (result.UT > 0.0 ? " at UT " + result.UT.ToString("0") : "") + ", " + result.Why);
                    plan.ReachedAt.Add(new KeyValuePair<double, int>(frame.Now, progress.Reached));
                    if (plan.FirstGoal < 0 && result.Goal >= 0)
                    {
                        plan.FirstGoal = result.Goal;
                        plan.FirstGoalWhole = result.FromStop;
                    }
                    if (!result.Ok)
                    {
                        plan.Failed = true;
                        plan.Ended = result.Why;
                        break;
                    }
                    if (result.Arrived) break;
                    if (result.Kind == NodePlanner.StepKind.Release && frame.Ahead && result.Section != null && progress.Released.Add(result.Section.SeparatorId))
                    {
                        // The section goes at the stop, and the plan goes on from the same state with the vessel lighter.
                        plan.Steps.Add(new PlannedStep { Kind = result.Kind, Goal = result.Goal, Why = result.Why, Section = result.Section, At = frame.Now });
                        frame = frame.At(frame.Now);
                        continue;
                    }
                    if (result.Kind == NodePlanner.StepKind.Aerobrake && airPasses)
                    {
                        // The pass is flown in the simulation, and the plan goes on from the orbit the vessel leaves the air in.
                        Orbit exit;
                        double exitUT, entryUT;
                        string why;
                        Debug.Log("[ReDefinition] Route: pass " + (passes + 1) + " through the air at UT " + frame.Now.ToString("0") + " from " + frame.Orbit.PeA.ToString("0") + " x " + frame.Orbit.ApA.ToString("0")
                                  + " m round " + frame.Body.bodyName + ": " + result.Why);
                        if (++passes > MostPasses)
                        {
                            plan.AirProblem = "The air does not bring the vessel down in " + MostPasses + " passes.";
                        }
                        else if (!AerobrakePass.Fly(vessel, frame.Orbit, frame.Now, out exit, out exitUT, out entryUT, out why))
                        {
                            plan.AirProblem = why;
                        }
                        else
                        {
                            plan.Steps.Add(new PlannedStep { Kind = result.Kind, Goal = result.Goal, Why = result.Why, At = exitUT, Flown = true });
                            TakeLegs(vessel, plan, collectedFrom, frame.Now, airLegAt, airOrbit, airEntryUT);
                            KspOrbits.ClearNodes(vessel);
                            if (!swapped)
                            {
                                ownPosition = vessel.orbit.getRelativePositionAtUT(ownTime);
                                ownVelocity = vessel.orbit.getOrbitalVelocityAtUT(ownTime);
                                swapped = true;
                            }
                            vessel.orbit.UpdateFromOrbitAtUT(exit, exitUT, exit.referenceBody);
                            vessel.patchedConicSolver.Update();
                            collectedFrom = exitUT;
                            airLegAt = exitUT;
                            airEntryUT = entryUT;
                            airOrbit = new Orbit(exit.inclination, exit.eccentricity, exit.semiMajorAxis, exit.LAN, exit.argumentOfPeriapsis, exit.meanAnomalyAtEpoch, exit.epoch, exit.referenceBody);
                            frame = new Frame(vessel, airPasses).At(exitUT);
                            continue;
                        }
                        plan.Failed = true;
                        plan.Ended = plan.AirProblem;
                        break;
                    }
                    if (result.Kind != NodePlanner.StepKind.Burn)
                    {
                        plan.Steps.Add(new PlannedStep { Kind = result.Kind, Goal = result.Goal, Why = result.Why, Altitude = result.Altitude, Target = result.Target, Port = result.Port, Dock = result.Dock });
                        plan.Ended = result.Why;
                        break;
                    }
                    Frame next = Advance(frame, result);
                    if (next == null)
                    {
                        plan.Failed = true;
                        plan.Ended = "The plan ends: " + result.Why;
                        break;
                    }
                    // Every burn of the step, the nodes it put in: a change of orbit has two.
                    bool first = true;
                    foreach (ManeuverNode node in vessel.patchedConicSolver.maneuverNodes)
                    {
                        if (node.UT <= frame.Now + Frame.AfterNode) continue;
                        plan.Steps.Add(new PlannedStep
                        {
                            Kind = result.Kind, Goal = result.Goal, UT = node.UT, At = node.UT, DeltaV = new V3(node.DeltaV.x, node.DeltaV.y, node.DeltaV.z),
                            Why = first ? result.Why : "The next burn of: " + result.Why,
                        });
                        first = false;
                    }
                    frame = next;
                }
                plan.Reached = progress.Reached;
                if (from == null && own != null && atStart > own.ProgressFor(route).Reached) own.ProgressFor(route).Reached = atStart;
                Collect(vessel, frame, plan, collectedFrom, airLegAt, airOrbit, airEntryUT);
            }
            finally
            {
                KspOrbits.ClearNodes(vessel);
                if (swapped)
                {
                    vessel.orbit.UpdateFromStateVectors(ownPosition, ownVelocity, ownBody, ownTime);
                    vessel.patchedConicSolver.Update();
                }
                foreach (KeyValuePair<double, Vector3d> node in held)
                {
                    vessel.patchedConicSolver.AddManeuverNode(node.Key).DeltaV = node.Value;
                    vessel.patchedConicSolver.UpdateFlightPlan();
                }
            }
            return plan;
        }

        // The state of a frame as a text, to the metre: the body, the apsides and the plane.
        private static string StateKey(Frame frame)
        {
            Orbit orbit = frame.Orbit;
            return orbit.referenceBody.bodyName + "|" + System.Math.Round(orbit.PeA) + "|" + System.Math.Round(orbit.ApA) + "|" + System.Math.Round(orbit.inclination, 1);
        }

        // The state after a step: where its last node stands, or, for a step with no burn, where the orbit goes on into the next patch. Null where
        // there is nothing after it.
        private static Frame Advance(Frame frame, NodePlanner.Result result)
        {
            if (result.UT > 0.0)
            {
                double last = result.UT;
                foreach (ManeuverNode node in frame.Solver.maneuverNodes)
                    if (node.UT > last) last = node.UT;
                return last > frame.Now ? frame.At(last) : null;
            }
            Orbit orbit = frame.Orbit;
            if (orbit.patchEndTransition == Orbit.PatchTransitionType.FINAL || orbit.nextPatch == null) return null;
            return orbit.EndUT > frame.Now ? frame.At(orbit.EndUT) : null;
        }

        // The most passes through the air a plan flown without the player makes before it is refused.
        private const int MostPasses = 12;

        // The patches of the flight plan from `from` to `until` as legs, and the delta-v of its nodes taken into the plan.
        // With `later` only the patches that begin after `from`: the orbit the vessel is on at `from` is taken in by the caller.
        private static void CollectPatches(Vessel vessel, double from, double until, RoutePlan plan, bool later)
        {
            foreach (ManeuverNode node in vessel.patchedConicSolver.maneuverNodes) plan.DeltaV += node.DeltaV.magnitude;
            foreach (Orbit patch in vessel.patchedConicSolver.flightPlan)
            {
                if (patch == null || patch.StartUT > until + Frame.AfterNode || (later ? patch.StartUT <= from + Frame.AfterNode : patch.EndUT <= from)) continue;
                var copy = new Orbit(patch.inclination, patch.eccentricity, patch.semiMajorAxis, patch.LAN, patch.argumentOfPeriapsis,
                    patch.meanAnomalyAtEpoch, patch.epoch, patch.referenceBody);
                plan.Legs.Add(new PlannedLeg { Orbit = copy, StartUT = System.Math.Max(patch.StartUT, from), EndUT = patch.EndUT });
            }
        }

        // The legs of the flight plan from `from` to `until`. Where a pass through the air ended at `airAt`, the first is the orbit it left the air in; the
        // solver's patch for the vessel's orbit does not say the start of that. The game destroys an unloaded vessel that is in the air while the flight scene
        // is on, so the vessel is put out of the air as it goes in. From the top of the air it is on the orbit it leaves in, as it will be seconds later.
        private static void TakeLegs(Vessel vessel, RoutePlan plan, double from, double until, double airAt, Orbit airOrbit, double entryUT)
        {
            bool air = airAt >= 0.0 && airOrbit != null;
            double lastStart = plan.Legs.Count > 0 ? plan.Legs[plan.Legs.Count - 1].StartUT : double.NegativeInfinity;
            if (air && airAt - entryUT > 2.0 && entryUT > lastStart + Frame.AfterNode)
            {
                Orbit early = new Orbit(airOrbit.inclination, airOrbit.eccentricity, airOrbit.semiMajorAxis, airOrbit.LAN, airOrbit.argumentOfPeriapsis,
                                        airOrbit.meanAnomalyAtEpoch, airOrbit.epoch - (airAt - entryUT), airOrbit.referenceBody);
                plan.Legs.Add(new PlannedLeg { Orbit = early, StartUT = entryUT, EndUT = airAt, Air = true });
            }
            if (air) plan.Legs.Add(new PlannedLeg { Orbit = airOrbit, StartUT = airAt, EndUT = airAt, Air = true });
            CollectPatches(vessel, from, until, plan, air);
        }

        // The orbits of the flight plan, the delta-v of its nodes, and where the plan ends, taken into the plan. The plan ends on the orbit after
        // its last burn. KSP cuts a patch short after one revolution where the next node lies further off, so each orbit is flown until the next begins.
        private static void Collect(Vessel vessel, Frame end, RoutePlan plan, double from, double airAt, Orbit airOrbit, double entryUT)
        {
            plan.EndUT = end.Now;
            TakeLegs(vessel, plan, from, plan.EndUT, airAt, airOrbit, entryUT);
            for (int i = 0; i < plan.Legs.Count; i++)
            {
                PlannedLeg leg = plan.Legs[i];
                leg.EndUT = i + 1 < plan.Legs.Count ? plan.Legs[i + 1].StartUT : System.Math.Max(plan.EndUT, leg.StartUT) + 2.0 * Frame.AfterNode;
                plan.Legs[i] = leg;
            }
        }
    }
}
