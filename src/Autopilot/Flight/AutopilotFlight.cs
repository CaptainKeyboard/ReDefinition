using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using ReDefinition.Autopilot.Guidance;
using ReDefinition.Autopilot.Planning;

namespace ReDefinition.Autopilot.Flight
{
    // A route flown by MechJeb: the next step is planned as a node, handed to MechJeb, waited for, and the route planned again from where
    // the burn left the vessel, until the last stop. Between burns the time is warped to the next change of orbit.
    internal sealed class AutopilotFlight
    {
        private const int MaxFailures = 3;

        private readonly Vessel vessel;
        private readonly IList<Destination> route;
        private bool cancelled;
        private float nextNote;

        // What the flight is doing, and how it ended: Arrived, or the reason it stopped.
        internal string Status = "";
        internal bool Arrived;
        internal bool Finished;

        internal AutopilotFlight(Vessel vessel, IList<Destination> route)
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
            ParsekBridge.StartRecording();
            int failures = 0;
            // A correction of the course is made again only while it brings the periapsis nearer: a burn too small for the engines to give exactly
            // would otherwise be made over and over.
            double lastError = double.NaN;
            bool keepCourse = false;
            for (int step = 0; !cancelled; step++)
            {
                if (Lost)
                {
                    Stop("The vessel was lost.");
                    yield break;
                }
                if (vessel != FlightGlobals.ActiveVessel)
                {
                    Stop("The vessel is not the one in focus.");
                    yield break;
                }
                NodePlanner.Result plan;
                try
                {
                    plan = LegPlanner.PlanNext(new Frame(vessel), route, false, keepCourse);
                    keepCourse = false;
                }
                catch (System.Exception e)
                {
                    Debug.LogError("[ReDefinition] Route: the plan failed: " + e);
                    Stop("The plan failed: " + e.Message);
                    yield break;
                }
                Debug.Log("[ReDefinition] Route: step " + step + ": " + plan.Why);
                if (plan.Correction)
                {
                    if (!double.IsNaN(lastError) && plan.Error >= lastError)
                    {
                        // The last correction did not help: the course is kept as it is, and the plan goes on from there.
                        KspOrbits.ClearNodes(vessel);
                        keepCourse = true;
                        lastError = double.NaN;
                        continue;
                    }
                    lastError = plan.Error;
                }
                else
                {
                    lastError = double.NaN;
                }
                if (!plan.Ok)
                {
                    // A plan can fail for a moment, as with a vessel just loaded: tried again a few times before the flight stops.
                    if (++failures >= MaxFailures)
                    {
                        Stop(plan.Why);
                        yield break;
                    }
                    Status = plan.Why + " Trying again.";
                    yield return new WaitForSeconds(4f);
                    continue;
                }
                failures = 0;
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
                    if (!Engines.Running(vessel))
                    {
                        Engines.FireNextStageIfCold(vessel);
                        yield return new WaitForSeconds(1f);
                    }
                    double needed = LandingPrediction.DeltaVNeeded(vessel);
                    if (double.IsPositiveInfinity(needed))
                    {
                        Stop("The engines are too weak to brake against the gravity here.");
                        yield break;
                    }
                    if (!double.IsNaN(needed) && !Enough(needed, "the landing")) yield break;
                    if (!MechJebDriver.StartLanding(vessel))
                    {
                        Stop("MechJeb would not start the landing.");
                        yield break;
                    }
                    yield return Descend();
                    continue;
                }
                if (plan.Kind == NodePlanner.StepKind.Release)
                {
                    Status = plan.Why;
                    yield return Release(plan.Section);
                    if (vessel != FlightGlobals.ActiveVessel) yield break;
                    continue;
                }
                if (plan.Kind == NodePlanner.StepKind.Aerobrake)
                {
                    Status = plan.Why;
                    yield return Aerobrake();
                    if (!cancelled && !Lost && vessel.orbit.eccentricity >= 1.0)
                    {
                        Stop("The pass through the air did not slow the vessel into an orbit.");
                        yield break;
                    }
                    continue;
                }
                if (plan.UT <= 0.0)
                {
                    Status = plan.Why + " Warping to the next change of orbit.";
                    yield return WarpToNextPatch();
                    continue;
                }
                Status = plan.Why;
                if (!Enough(plan.DeltaV.Length, "the burn"))
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
        }

        // Whether the vessel is gone: destroyed, or not there.
        private bool Lost
        {
            get { return vessel == null || vessel.state == Vessel.State.DEAD; }
        }

        // Whether the vessel carries the delta-v asked for, as the game works it out stage by stage; if not, the flight stops, with the reason. Where
        // the game has no figure yet, the flight goes on.
        private bool Enough(double needed, string what)
        {
            if (vessel.VesselDeltaV == null) return true;
            double have = vessel.VesselDeltaV.TotalDeltaVVac;
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
            while (!cancelled && !Lost && MechJebDriver.AscentBusy(vessel) && Time.realtimeSinceStartup - started < 1200f)
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
            while (!cancelled && !Lost && MechJebDriver.LandingBusy(vessel) && Time.realtimeSinceStartup - started < 1500f)
            {
                Status = "Landing: " + (vessel.heightFromTerrain >= 0f ? vessel.heightFromTerrain.ToString("0") : vessel.altitude.ToString("0")) + " m up, " + vessel.srfSpeed.ToString("0") + " m/s.";
                Note();
                // The last metres: the three heights KSP has, told apart, for a vessel that sinks into the ground.
                if (vessel.radarAltitude < 50.0)
                    Debug.Log("[ReDefinition] Route: touchdown: altitude " + vessel.altitude.ToString("0.0") + ", terrain " + vessel.terrainAltitude.ToString("0.0")
                              + ", above it " + vessel.radarAltitude.ToString("0.0") + ", raycast " + vessel.heightFromTerrain.ToString("0.0") + ", " + vessel.srfSpeed.ToString("0.0")
                              + " m/s, " + vessel.situation + ".");
                still = vessel.LandedOrSplashed && vessel.srfSpeed < 0.5 ? still + 0.5f : 0f;
                if (still >= 3f) break;
                yield return new WaitForSeconds(0.5f);
            }
            MechJebDriver.StopLanding(vessel);
            // MechJeb ends the landing a moment before KSP calls the vessel landed: not planned again from the air.
            for (float waited = 0f; !cancelled && !vessel.LandedOrSplashed && waited < 10f && vessel.heightFromTerrain < 100f; waited += 0.5f)
                yield return new WaitForSeconds(0.5f);
        }

        // Lets a section go: its decoupler fires, or its docking port undocks. Of the two vessels that are then there, the one with a MechJeb unit
        // goes on with the route, or the heavier where both or neither have one. It takes the vessel's name and the autopilot's state, and is put
        // in focus where it is not. The section's vessel is named for the section.
        private IEnumerator Release(SectionRelease section)
        {
            string name = vessel.vesselName;
            var before = new HashSet<System.Guid>();
            foreach (Vessel other in FlightGlobals.Vessels) before.Add(other.id);
            foreach (Part part in vessel.parts)
            {
                if (part.persistentId != section.SeparatorId) continue;
                foreach (ModuleDecouplerBase decoupler in part.FindModulesImplementing<ModuleDecouplerBase>())
                    if (!decoupler.isDecoupled) decoupler.Decouple();
                foreach (ModuleDockingNode port in part.FindModulesImplementing<ModuleDockingNode>())
                {
                    if (port.state.StartsWith("PreAttached")) port.Decouple();
                    else if (port.state.StartsWith("Docked")) port.Undock();
                }
                break;
            }
            yield return new WaitForSeconds(2f);
            Vessel apart = null;
            foreach (Vessel other in FlightGlobals.Vessels)
                if (!before.Contains(other.id) && other.loaded && Vector3d.Distance(other.GetWorldPos3D(), vessel.GetWorldPos3D()) < 500.0) apart = other;
            if (apart == null) yield break;
            // The one MechJeb can fly goes on with the route; where both or neither can, the heavier.
            bool apartFlies = MechJebDriver.CanFly(apart);
            bool vesselFlies = MechJebDriver.CanFly(vessel);
            Vessel carrier = apartFlies != vesselFlies ? (apartFlies ? apart : vessel) : apart.GetTotalMass() > vessel.GetTotalMass() ? apart : vessel;
            Vessel released = carrier == vessel ? apart : vessel;
            released.vesselName = name + " " + section.Name;
            carrier.vesselName = name;
            if (carrier == vessel) yield break;
            RouteState from = RouteState.Of(vessel);
            RouteState to = RouteState.Of(carrier);
            if (from != null && to != null)
            {
                to.Autopilot = from.Autopilot;
                to.Navigation = from.Navigation;
                from.Autopilot = false;
                from.Navigation = false;
            }
            FlightGlobals.SetActiveVessel(carrier);
        }

        // The pass through the air that slows the vessel: it is warped to a minute before the periapsis, held retrograde on the stock SAS, and waited
        // for until it is out of the air on a closed orbit.
        private IEnumerator Aerobrake()
        {
            vessel.ActionGroups.SetGroup(KSPActionGroup.SAS, true);
            float started = Time.realtimeSinceStartup;
            double toPeriapsis = vessel.orbit.timeToPe - NodePlanner.LeadTime;
            if (toPeriapsis > 30.0) TimeWarp.fetch.WarpTo(Planetarium.GetUniversalTime() + toPeriapsis);
            bool entered = false;
            double peak = 0.0;
            while (!cancelled && !Lost && Time.realtimeSinceStartup - started < 1800f)
            {
                bool inAir = vessel.situation == Vessel.Situations.FLYING;
                entered |= inAir;
                string hottest;
                double share = Heat.Hottest(vessel, out hottest);
                if (inAir) peak = System.Math.Max(peak, share);
                // Retrograde, the heat shield first; but where a part is at its limit the vessel climbs out, nose away from the ground, on full thrust.
                bool climbing = inAir && Heat.TemperatureThrottle(share) < 1.0;
                if (vessel.Autopilot != null && vessel.Autopilot.Enabled)
                    vessel.Autopilot.SetMode(climbing ? VesselAutopilot.AutopilotMode.RadialOut : VesselAutopilot.AutopilotMode.Retrograde);
                FlightInputHandler.state.mainThrottle = climbing ? 1f : 0f;
                if (climbing && !Engines.Running(vessel)) Engines.FireNextStageIfCold(vessel);
                Status = inAir ? "Aerobrake: in the air at " + vessel.altitude.ToString("0") + " m, " + vessel.srfSpeed.ToString("0") + " m/s, " + hottest + " at "
                                 + (share * 100.0).ToString("0") + " % of its limit." : "Aerobrake: waiting for the periapsis.";
                if (inAir) PassHeat.LogFlux(vessel);
                if (entered && !inAir) break;
                yield return new WaitForSeconds(1f);
            }
            FlightInputHandler.state.mainThrottle = 0f;
            vessel.ActionGroups.SetGroup(KSPActionGroup.SAS, false);
            if (entered) PassHeat.Learn(vessel, peak);
        }

        // Waits while MechJeb flies the node, lighting the next stage where no engine runs for the burn.
        private IEnumerator Burn(NodePlanner.Result plan)
        {
            float nextStage = 0f;
            while (!cancelled && !Lost && MechJebDriver.Busy(vessel))
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
            ParsekBridge.StopRecording();
        }
    }
}
