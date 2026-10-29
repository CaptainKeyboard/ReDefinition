using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace ReDefinition.Autopilot
{
    // Keeps the nodes of the route's next step on the vessel while the player flies the burns. The plan is made again when the orbit has
    // changed (a burn done, or flown a little off), when a node was missed, and when the route was edited. It does not plan during a burn
    // or in the air, nor more often than every thirty seconds, so that a plan that cannot be made is not tried over and over.
    internal sealed class NodeFollower
    {
        private const float MinGap = 30f;
        private const double MissedAfter = 60.0;

        private string signature = "";
        private float lastPlan = -1000f;

        // What the last plan came to, for the window; where the step has no node, the guidance for it, made again in every call.
        internal string Status = "";

        // The step the last plan was, and for a climb the altitude it ends at.
        private readonly LandingGuide landing = new LandingGuide();
        private NodePlanner.StepKind step = NodePlanner.StepKind.Burn;
        private bool guided;
        private double aim;

        // The throttle the guidance asks for in the descent, 0 to 1.
        internal double Throttle
        {
            get { return landing.Throttle; }
        }

        // The guidance for a climb or a descent, in every second; nothing for a step with a node.
        internal void Guide(Vessel vessel)
        {
            if (!guided || vessel == null) return;
            Status = step == NodePlanner.StepKind.Ascent ? Guidance.Climb(vessel, aim) : landing.Describe(vessel);
        }

        // Called now and then for the vessel; plans where one of the triggers holds.
        internal void Tick(Vessel vessel, IList<Destination> route)
        {
            if (vessel == null || route == null || Time.realtimeSinceStartup - lastPlan < MinGap) return;
            if (vessel.situation == Vessel.Situations.FLYING || Engines.Burning(vessel) || MechJebDriver.Busy(vessel)) return;
            string now = Signature(vessel, route);
            PatchedConicSolver solver = vessel.patchedConicSolver;
            bool missed = solver != null && solver.maneuverNodes.Count > 0 && solver.maneuverNodes[0].UT < Planetarium.GetUniversalTime() - MissedAfter;
            if (now == signature && !missed) return;
            lastPlan = Time.realtimeSinceStartup;
            signature = now;
            NodePlanner.Result plan = LegPlanner.PlanNext(vessel, route, true);
            step = plan.Kind;
            aim = plan.Altitude;
            guided = plan.Ok && (plan.Kind == NodePlanner.StepKind.Ascent || plan.Kind == NodePlanner.StepKind.Landing);
            Status = plan.Why;
            Debug.Log("[ReDefinition] Route: nodes: " + plan.Why);
            if (guided) Guide(vessel);
        }

        // The orbit and the route in a text: the body, the apsides to fifty metres and the plane to a tenth of a degree, the body of the next
        // patch, and the stops. Equal texts mean nothing that matters to the plan has changed.
        private static string Signature(Vessel vessel, IList<Destination> route)
        {
            Orbit orbit = vessel.orbit;
            var text = new StringBuilder();
            text.Append(vessel.mainBody.bodyName).Append('|').Append(System.Math.Round(orbit.PeA / 50.0)).Append('|').Append(System.Math.Round(orbit.ApA / 50.0));
            text.Append('|').Append(System.Math.Round(orbit.inclination, 1)).Append('|').Append(vessel.situation);
            if (orbit.patchEndTransition != Orbit.PatchTransitionType.FINAL && orbit.nextPatch != null) text.Append('|').Append(orbit.nextPatch.referenceBody.bodyName);
            foreach (Destination stop in route)
                text.Append('|').Append(stop.Body != null ? stop.Body.bodyName : "-").Append(stop.Surface ? 'S' : 'O').Append(stop.Altitude);
            return text.ToString();
        }
    }
}
