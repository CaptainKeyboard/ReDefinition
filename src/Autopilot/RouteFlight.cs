using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace ReDefinition.Autopilot
{
    // A route flown by MechJeb: the next step is planned as a node, handed to MechJeb, waited for, and the route planned again from where
    // the burn left the vessel, until the last stop. Between burns the time is warped to the next change of orbit.
    internal sealed class RouteFlight
    {
        private const int MaxSteps = 24;
        // How much more delta-v than the plan's the vessel must carry: a burn is flown a little off, and a landing costs more than the orbit's speed.
        private const double BurnMargin = 1.05;
        private const double LandingMargin = 1.25;

        private readonly Vessel vessel;
        private readonly IList<Destination> route;
        private bool cancelled;
        private float nextNote;

        // What the flight is doing, and how it ended: Arrived, or the reason it stopped.
        internal string Status = "";
        internal bool Arrived;
        internal bool Finished;

        internal RouteFlight(Vessel vessel, IList<Destination> route)
        {
            this.vessel = vessel;
            this.route = route;
        }

        // Stops the flight and hands the vessel back: the executor is aborted, the warp is brought down.
        internal void Cancel()
        {
            cancelled = true;
            MechJebDriver.Abort(vessel);
        }

        // The flight as a coroutine: run it from a behaviour.
        internal IEnumerator Run()
        {
            if (!MechJebDriver.CanFly(vessel))
            {
                Stop(MechJebDriver.Unavailable ?? "The vessel has no MechJeb unit.");
                yield break;
            }
            for (int step = 0; step < MaxSteps && !cancelled; step++)
            {
                if (vessel == null || vessel != FlightGlobals.ActiveVessel)
                {
                    Stop("The vessel is not the one in focus.");
                    yield break;
                }
                NodePlanner.Result plan = LegPlanner.PlanNext(vessel, route);
                Debug.Log("[ReDefinition] Route: step " + step + ": " + plan.Why);
                if (!plan.Ok)
                {
                    Stop(plan.Why);
                    yield break;
                }
                if (plan.Arrived)
                {
                    Arrived = true;
                    Stop("Arrived.");
                    yield break;
                }
                if (plan.Kind == NodePlanner.StepKind.Ascent)
                {
                    Status = plan.Why;
                    if (!MechJebDriver.StartAscent(vessel, plan.Altitude, 0.0))
                    {
                        Stop("MechJeb would not start the ascent.");
                        yield break;
                    }
                    yield return Ascend();
                    continue;
                }
                if (plan.Kind == NodePlanner.StepKind.Landing)
                {
                    Status = plan.Why;
                    double circular = System.Math.Sqrt(vessel.mainBody.gravParameter / vessel.orbit.radius);
                    if (!Enough(circular * LandingMargin, "the landing"))
                    {
                        yield break;
                    }
                    if (!Engines.Running(vessel))
                    {
                        Engines.FireNextStageIfCold(vessel);
                        yield return new WaitForSeconds(1f);
                    }
                    if (!MechJebDriver.StartLanding(vessel))
                    {
                        Stop("MechJeb would not start the landing.");
                        yield break;
                    }
                    yield return Descend();
                    continue;
                }
                if (plan.UT <= 0.0)
                {
                    Status = plan.Why + " Warping to the next change of orbit.";
                    yield return WarpToNextPatch();
                    continue;
                }
                Status = plan.Why;
                if (!Enough(plan.DeltaV.Length * BurnMargin, "the burn"))
                {
                    KspOrbits.ClearNodes(vessel);
                    yield break;
                }
                if (!MechJebDriver.ExecuteNode(vessel, true))
                {
                    Stop("MechJeb would not take the node.");
                    yield break;
                }
                yield return Burn(plan);
            }
            if (!cancelled && !Finished) Stop("The route took more steps than it should.");
        }

        // Whether the vessel carries the delta-v asked for; if not, the flight stops, with the reason.
        private bool Enough(double needed, string what)
        {
            double have = Engines.DeltaV(vessel);
            if (have >= needed) return true;
            Stop("Not enough delta-v for " + what + ": " + needed.ToString("0") + " m/s needed, " + have.ToString("0") + " m/s on board.");
            return false;
        }

        // Waits while MechJeb's ascent flies: it stops itself in the orbit. On the pad the first stage is fired here, which MechJeb leaves to
        // the player's launch button; after that its own staging takes over.
        private IEnumerator Ascend()
        {
            float started = Time.realtimeSinceStartup;
            if (vessel.LandedOrSplashed || vessel.situation == Vessel.Situations.PRELAUNCH)
            {
                yield return new WaitForSeconds(2f);
                if (!Engines.Running(vessel)) Engines.FireNextStageIfCold(vessel);
            }
            while (!cancelled && MechJebDriver.AscentBusy(vessel) && Time.realtimeSinceStartup - started < 1200f)
            {
                Status = "Ascent: " + (vessel.altitude / 1000.0).ToString("0.0") + " km, " + vessel.verticalSpeed.ToString("0") + " m/s up.";
                Note();
                yield return new WaitForSeconds(0.5f);
            }
            MechJebDriver.StopAscent(vessel);
        }

        // Waits while MechJeb's landing flies, and ends it a moment after the vessel stands still on the ground.
        private IEnumerator Descend()
        {
            float started = Time.realtimeSinceStartup;
            float still = 0f;
            while (!cancelled && MechJebDriver.LandingBusy(vessel) && Time.realtimeSinceStartup - started < 1500f)
            {
                Status = "Landing: " + (vessel.heightFromTerrain >= 0f ? vessel.heightFromTerrain.ToString("0") : vessel.altitude.ToString("0")) + " m up, " + vessel.srfSpeed.ToString("0") + " m/s.";
                Note();
                still = vessel.LandedOrSplashed && vessel.srfSpeed < 0.5 ? still + 0.5f : 0f;
                if (still >= 3f) break;
                yield return new WaitForSeconds(0.5f);
            }
            MechJebDriver.StopLanding(vessel);
            // MechJeb ends the landing a moment before KSP calls the vessel landed: not planned again from the air.
            for (float waited = 0f; !cancelled && !vessel.LandedOrSplashed && waited < 10f && vessel.heightFromTerrain < 100f; waited += 0.5f)
                yield return new WaitForSeconds(0.5f);
        }

        // Waits while MechJeb flies the node, lighting the next stage where no engine runs for the burn.
        private IEnumerator Burn(NodePlanner.Result plan)
        {
            float nextStage = 0f;
            while (!cancelled && MechJebDriver.Busy(vessel))
            {
                string state = MechJebDriver.State(vessel);
                Status = "Burn: " + state + ", " + KSPUtil.PrintTimeCompact(plan.UT - Planetarium.GetUniversalTime(), false) + " to the node.";
                Note();
                if ((state == "LEAD" || state == "BURN") && Time.realtimeSinceStartup > nextStage)
                {
                    nextStage = Time.realtimeSinceStartup + 2f;
                    if (!Engines.Running(vessel) && !Engines.FireNextStageIfCold(vessel)) MechJebDriver.Abort(vessel);
                }
                yield return new WaitForSeconds(0.5f);
            }
        }

        // Warps to the end of the orbit the vessel is on, where the next one starts: the change of the body it is round.
        private IEnumerator WarpToNextPatch()
        {
            Orbit orbit = vessel.orbit;
            if (orbit.patchEndTransition == Orbit.PatchTransitionType.FINAL)
            {
                Stop("There is no change of orbit ahead to warp to.");
                yield break;
            }
            CelestialBody before = vessel.mainBody;
            double until = orbit.EndUT + 1.0;
            TimeWarp.fetch.WarpTo(until);
            float started = Time.realtimeSinceStartup;
            while (!cancelled && vessel.mainBody == before && Planetarium.GetUniversalTime() < until && Time.realtimeSinceStartup - started < 600f)
                yield return new WaitForSeconds(0.5f);
            TimeWarp.SetRate(0, true);
            yield return new WaitForSeconds(1f);
        }

        // The status into KSP.log, at most every 10 s, so that a flight can be followed afterwards.
        private void Note()
        {
            if (Time.realtimeSinceStartup < nextNote) return;
            nextNote = Time.realtimeSinceStartup + 10f;
            Debug.Log("[ReDefinition] Route: " + Status + " [" + Engines.Report(vessel) + "]");
        }

        private void Stop(string why)
        {
            Status = why;
            Finished = true;
        }
    }
}
