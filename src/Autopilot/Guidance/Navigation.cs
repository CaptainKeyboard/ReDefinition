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

        // How long the mark takes to slide off the gauges at the apoapsis aimed for, in s, and when the climb was done.
        private const float SlideSeconds = 2f;
        private float doneAt = -1f;
        private bool climbDone;
        private float nextTrace;
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
                double asked, burn;
                bool done;
                Status = ClimbGuide.Climb(vessel, aim, out asked, out burn, out done);
                ClimbGuide.Measure(vessel, burn);
                // Once the apoapsis is reached the climb stays done: the burn left wavers about its least size, and the mark would go up and down.
                if (done) climbDone = true;
                done = climbDone;
                if (done) asked = 0.0;
                if (!done) doneAt = -1f;
                else if (doneAt < 0f) doneAt = Time.realtimeSinceStartup;
                // At the apoapsis aimed for the mark and the bar slide off the gauges. Then the guidance ends, and the plan is made again at once, with the burn
                // that circularizes as a node of its own, which the game's burn display shows.
                if (done && Time.realtimeSinceStartup - doneAt > SlideSeconds)
                {
                    GuideNode.Handover();
                    GuidanceBoard.Forget();
                    guided = false;
                    signature = "";
                    lastPlan = -1000f;
                    return;
                }
                // The number is the need as a speed: the most the engines change it in the time looked ahead.
                GuidanceBoard.Write(vessel, Status, ClimbGuide.ClimbDirection(vessel, aim), asked, 0.5, "", 0, null, ClimbGuide.RampBurn(Engines.Acceleration(vessel)), false, null, ClimbGuide.Remaining(vessel, aim), aim, asked, done);
                Trace(vessel, burn);
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
                landing.HasBar ? landing.BarFill : (double?)null, landing.BarText, landing.BarLevel, null,
                ClimbGuide.RampBurn(Engines.Acceleration(vessel)), true, null, vessel.srfSpeed, null, landing.HasBar ? landing.Throttle : (double?)null);
        }

        // The climb's guidance twice a second in the log, to read after a flight how the bar moved: height, apoapsis, burn left, ideal and given throttle, need.
        private void Trace(Vessel vessel, double burn)
        {
            if (Time.realtimeSinceStartup < nextTrace) return;
            nextTrace = Time.realtimeSinceStartup + 0.5f;
            Debug.Log("[ReDefinition] Climb trace: " + vessel.altitude.ToString("0") + " m, apoapsis " + vessel.orbit.ApA.ToString("0") + " m, air " + vessel.atmDensity.ToString("0.000")
                      + ", burn left " + burn.ToString("0") + " m/s, ideal " + GuidanceBoard.Ideal.ToString("0.00") + ", given " + FlightInputHandler.state.mainThrottle.ToString("0.00")
                      + ", need " + GuidanceBoard.Need.ToString("0.00") + ", speed " + vessel.obt_velocity.magnitude.ToString("0.0") + ", full-thrust acceleration "
                      + Engines.Acceleration(vessel).ToString("0.0") + ".");
        }

        // Called now and then for the vessel; plans where one of the triggers holds.
        internal void Tick(Vessel vessel, IList<Destination> route)
        {
            if (vessel == null || route == null || Time.realtimeSinceStartup - lastPlan < MinGap) return;
            if (vessel.situation == Vessel.Situations.FLYING || Engines.Burning(vessel) || MechJebDriver.Busy(vessel)) return;
            string now = Signature(vessel, route);
            PatchedConicSolver solver = vessel.patchedConicSolver;
            ManeuverNode first = GuideNode.First(vessel);
            bool missed = solver != null && first != null && Missed(vessel, first);
            if (now == signature && !missed) return;
            // The nodes of the plan are made on the orbit the vessel has, which the guide's burn would change.
            GuideNode.Release(vessel);
            lastPlan = Time.realtimeSinceStartup;
            signature = now;
            RouteState own = RouteState.Of(vessel);
            NodePlanner.Result plan = NextStepPlanner.PlanNext(new Frame(vessel), route, PlanOptions.Guided, own != null ? own.ProgressFor(route) : null);
            step = plan.Kind;
            aim = plan.Altitude;
            climbDone = false;
            doneAt = -1f;
            guided = plan.Ok && (plan.Kind == NodePlanner.StepKind.Ascent || plan.Kind == NodePlanner.StepKind.Landing || plan.Kind == NodePlanner.StepKind.Aerobrake
                                 || plan.Kind == NodePlanner.StepKind.Release);
            Status = plan.Why;
            Debug.Log("[ReDefinition] Route: nodes: " + plan.Why);
            // A guided step has no node of its own: the one the climb left behind, the circularizing, goes.
            if (guided) GuideNode.DropKept();
            if (guided) Guide(vessel);
        }

        // Whether the node was missed: its burn, which is centred on the node, would be over by now with the engines the vessel has.
        internal static bool Missed(Vessel vessel, ManeuverNode node)
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
