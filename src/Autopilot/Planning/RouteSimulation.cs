using System.Collections.Generic;
using ReDefinition.Orbits;
using UnityEngine;

namespace ReDefinition.Autopilot.Planning
{
    // One step of the plan ahead: what it is, when, and what it asks of the engines.
    internal sealed class PlannedStep
    {
        public NodePlanner.StepKind Kind;
        // The UT of the burn; zero for a step that is not a burn (a descent, a pass through the air, a release).
        public double UT;
        public V3 DeltaV;
        public string Why;
    }

    // A stretch of the plan on one orbit: the orbit, and from which UT to which it is flown.
    internal struct PlannedLeg
    {
        public Orbit Orbit;
        public double StartUT;
        public double EndUT;
    }

    // The route worked out from the vessel to its last stop, step after step: the plan the navigation guides along, the autopilot flies, the map
    // shows, and Parsek is given as a recording.
    internal sealed class RoutePlan
    {
        public readonly List<PlannedStep> Steps = new List<PlannedStep>();
        public readonly List<PlannedLeg> Legs = new List<PlannedLeg>();
        // The delta-v of all the burns in the plan, in m/s.
        public double DeltaV;
        // The UT the plan ends at: the end of its last burn, or of the last orbit it flies.
        public double EndUT;
        // Why the plan does not reach the last stop (the vessel lands, a pass through the air, no way found); null where it does.
        public string Ended;
    }

    // Plans the whole route from the vessel's orbit by chaining the steps: each is planned from the state the one before it leaves, with its
    // nodes put in the flight plan, where KSP works out the orbits that follow. The nodes are taken off again; the plan is what is returned.
    internal static class RouteSimulation
    {
        // With `forPlayer` the deorbit of a landing is a burn of the plan, as the player flies it; without, the plan ends in the orbit the landing
        // starts from, which MechJeb's landing leaves by itself.
        internal static RoutePlan Run(Vessel vessel, IList<Destination> route, bool forPlayer = false)
        {
            var plan = new RoutePlan();
            if (vessel == null || route == null || route.Count < 2 || vessel.patchedConicSolver == null)
            {
                plan.Ended = "There is no route to plan.";
                return plan;
            }
            KspOrbits.ClearNodes(vessel);
            try
            {
                Frame frame = new Frame(vessel);
                string previous = null;
                while (true)
                {
                    NodePlanner.Result result = LegPlanner.PlanNext(frame, route, forPlayer);
                    if (!result.Ok)
                    {
                        plan.Ended = result.Why;
                        break;
                    }
                    if (result.Arrived) break;
                    if (result.Kind != NodePlanner.StepKind.Burn)
                    {
                        plan.Steps.Add(new PlannedStep { Kind = result.Kind, Why = result.Why });
                        plan.Ended = result.Why;
                        break;
                    }
                    // The same step twice in a row is a plan that does not move on.
                    if (result.Why == previous)
                    {
                        plan.Ended = "The plan does not move on: " + result.Why;
                        break;
                    }
                    previous = result.Why;
                    Frame next = Advance(frame, result);
                    if (next == null)
                    {
                        plan.Ended = "The plan ends: " + result.Why;
                        break;
                    }
                    if (result.UT > 0.0) plan.Steps.Add(new PlannedStep { Kind = result.Kind, UT = result.UT, DeltaV = result.DeltaV, Why = result.Why });
                    frame = next;
                }
                Collect(vessel, frame, plan);
            }
            finally
            {
                KspOrbits.ClearNodes(vessel);
            }
            return plan;
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

        // The orbits of the flight plan, the delta-v of its nodes, and where the plan ends, taken into the plan. The plan ends on the orbit after
        // its last burn. KSP cuts a patch short after one revolution where the next node lies further off, so each orbit is flown until the next begins.
        private static void Collect(Vessel vessel, Frame end, RoutePlan plan)
        {
            double now = Planetarium.GetUniversalTime();
            plan.EndUT = end.Now;
            foreach (ManeuverNode node in vessel.patchedConicSolver.maneuverNodes) plan.DeltaV += node.DeltaV.magnitude;
            foreach (Orbit patch in vessel.patchedConicSolver.flightPlan)
            {
                if (patch == null || patch.EndUT <= now || patch.StartUT > plan.EndUT + Frame.AfterNode) continue;
                var copy = new Orbit(patch.inclination, patch.eccentricity, patch.semiMajorAxis, patch.LAN, patch.argumentOfPeriapsis,
                    patch.meanAnomalyAtEpoch, patch.epoch, patch.referenceBody);
                plan.Legs.Add(new PlannedLeg { Orbit = copy, StartUT = System.Math.Max(patch.StartUT, now), EndUT = patch.EndUT });
            }
            for (int i = 0; i < plan.Legs.Count; i++)
            {
                PlannedLeg leg = plan.Legs[i];
                leg.EndUT = i + 1 < plan.Legs.Count ? plan.Legs[i + 1].StartUT : System.Math.Max(plan.EndUT, leg.StartUT) + 2.0 * Frame.AfterNode;
                plan.Legs[i] = leg;
            }
        }
    }
}
