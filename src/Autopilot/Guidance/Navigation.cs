using System.Collections.Generic;
using System.Text;
using UnityEngine;
using ReDefinition.Autopilot;
using ReDefinition.Autopilot.Flight;
using ReDefinition.Autopilot.Planning;

namespace ReDefinition.Autopilot.Guidance
{
    // Keeps the nodes of the route's next step on the vessel while the player flies the burns. The plan is made again when the orbit has
    // changed (a burn done, or flown a little off), when a node was missed, and when the route was edited. It does not plan during a burn
    // or in the air, nor more often than every thirty seconds, so that a plan that cannot be made is not tried over and over.
    internal sealed class Navigation
    {
        private const float MinGap = 30f;

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

        // The guidance for a climb or a descent, made in every frame and written on the board for the flight view; nothing for a step with a node.
        internal void Guide(Vessel vessel)
        {
            if (!guided || vessel == null) return;
            if (step == NodePlanner.StepKind.Ascent)
            {
                double asked, fill;
                string text;
                Status = ClimbGuide.Climb(vessel, aim, out asked, out fill, out text);
                GuidanceBoard.Write(vessel, Status, ClimbGuide.ClimbDirection(vessel, aim), asked, fill, text);
                return;
            }
            if (step == NodePlanner.StepKind.Release)
            {
                // The line the plan gave stands; it is told on the board until the section has gone.
                GuidanceBoard.Write(vessel, Status, null, null);
                return;
            }
            if (step == NodePlanner.StepKind.Aerobrake)
            {
                string part;
                double hottest = Heat.Hottest(vessel, out part);
                Status = "Aerobrake: point retrograde and let the air slow the vessel" + (hottest > 0.8 ? ". HOT: " + part + " at " + (hottest * 100.0).ToString("0") + " % of its limit." : ".");
                GuidanceBoard.Write(vessel, Status, vessel.srf_velocity.sqrMagnitude > 1.0 ? -vessel.srf_velocity.normalized : (Vector3d?)null, null);
                return;
            }
            Status = landing.Describe(vessel);
            GuidanceBoard.Write(vessel, Status, landing.HasDirection ? landing.Direction : (Vector3d?)null, landing.Throttle,
                landing.HasBar ? landing.BarFill : (double?)null, landing.BarText, landing.BarLevel);
        }

        // Called now and then for the vessel; plans where one of the triggers holds.
        internal void Tick(Vessel vessel, IList<Destination> route)
        {
            if (vessel == null || route == null || Time.realtimeSinceStartup - lastPlan < MinGap) return;
            if (vessel.situation == Vessel.Situations.FLYING || Engines.Burning(vessel) || MechJebDriver.Busy(vessel)) return;
            string now = Signature(vessel, route);
            PatchedConicSolver solver = vessel.patchedConicSolver;
            bool missed = solver != null && solver.maneuverNodes.Count > 0 && Missed(vessel, solver.maneuverNodes[0]);
            if (now == signature && !missed) return;
            lastPlan = Time.realtimeSinceStartup;
            signature = now;
            RouteState own = RouteState.Of(vessel);
            NodePlanner.Result plan = LegPlanner.PlanNext(new Frame(vessel), route, true, false, own != null ? own.ProgressFor(route) : null);
            step = plan.Kind;
            aim = plan.Altitude;
            guided = plan.Ok && (plan.Kind == NodePlanner.StepKind.Ascent || plan.Kind == NodePlanner.StepKind.Landing || plan.Kind == NodePlanner.StepKind.Aerobrake
                                 || plan.Kind == NodePlanner.StepKind.Release);
            Status = plan.Why;
            Debug.Log("[ReDefinition] Route: nodes: " + plan.Why);
            if (guided) Guide(vessel);
        }

        // Whether the node was missed: its burn, which is centred on the node, would be over by now with the engines the vessel has.
        private static bool Missed(Vessel vessel, ManeuverNode node)
        {
            double acceleration = Engines.Acceleration(vessel);
            double burn = acceleration > 0.0 ? node.GetBurnVector(vessel.orbit).magnitude / acceleration : 0.0;
            return node.UT + 0.5 * burn < Planetarium.GetUniversalTime();
        }

        // The orbit and the route in a text: the body, the apsides to fifty metres and the plane to a tenth of a degree, the body of the next
        // patch, and the stops. Equal texts mean nothing that matters to the plan has changed.
        private static string Signature(Vessel vessel, IList<Destination> route)
        {
            Orbit orbit = vessel.orbit;
            var text = new StringBuilder();
            text.Append(vessel.mainBody.bodyName).Append('|').Append(System.Math.Round(orbit.PeA / 50.0)).Append('|').Append(System.Math.Round(orbit.ApA / 50.0));
            text.Append('|').Append(System.Math.Round(orbit.inclination, 1)).Append('|').Append(vessel.situation);
            text.Append('|').Append(vessel.parts.Count);
            if (orbit.patchEndTransition != Orbit.PatchTransitionType.FINAL && orbit.nextPatch != null) text.Append('|').Append(orbit.nextPatch.referenceBody.bodyName);
            foreach (Destination stop in route)
                text.Append('|').Append(stop.Body != null ? stop.Body.bodyName : "-").Append(stop.Surface ? 'S' : 'O').Append(stop.Altitude).Append(stop.MeetWith);
            return text.ToString();
        }
    }
}
