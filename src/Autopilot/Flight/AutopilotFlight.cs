using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using ReDefinition.Autopilot.Guidance;
using ReDefinition.Autopilot.Planning;
using ReDefinition.Core;

namespace ReDefinition.Autopilot.Flight
{
    // A route flown by MechJeb: the next step is planned as a node, handed to MechJeb, waited for, and the route planned again from where
    // the burn left the vessel, until the last stop. Between burns the time is warped to the next change of orbit.
    internal sealed class AutopilotFlight
    {
        private const int MaxFailures = 3;
        // The turn, in rad/s, under which a vessel counts as at rest before it warps.
        private const double SteadyRate = 0.01;

        private readonly Vessel vessel;
        private readonly IList<Destination> route;
        private bool cancelled;
        private float nextNote;
        // The corrections burned on the way to the stop of the route that they are counted for, and whether the last burn was lit.
        private int corrections, correctionGoal = -1;
        private bool lastBurnLit;

        // The burns of the flight and how the vessel behaved between them, for the log and the probes.
        internal readonly FlightLedger Ledger = new FlightLedger();

        // What the flight is doing, and how it ended: Arrived, or the reason it stopped.
        internal string Status = "";
        internal bool Arrived;
        internal bool Finished;

        // A flight of a route: the next step is planned, and the route planned again from where the vessel is.
        internal AutopilotFlight(Vessel vessel, IList<Destination> route)
        {
            this.vessel = vessel;
            this.route = route;
        }

        private AutopilotFlight(Vessel vessel)
        {
            this.vessel = vessel;
            flyNodes = true;
        }

        // A flight of the maneuver nodes the vessel holds, which the player made in the map.
        internal static AutopilotFlight OfNodes(Vessel vessel)
        {
            return new AutopilotFlight(vessel);
        }

        private readonly bool flyNodes;

        // The route the flight flies, as RoutePlan.SignatureOf has it; empty for a flight of nodes.
        internal string RouteSignature
        {
            get { return route != null ? RoutePlan.SignatureOf(route) : ""; }
        }

        // Whether the flight is one of a route, not of the nodes the player made.
        internal bool OfRoute
        {
            get { return !flyNodes; }
        }

        // Stops the flight and hands the vessel back: the executor is aborted, the warp is brought down.
        internal void Cancel()
        {
            cancelled = true;
            WarpGuard.SelfWarpUntil = 0.0;
            MechJebDriver.Abort(vessel);
            MechJebDriver.RestoreSettings(vessel);
            MechJebDriver.StopRendezvous(vessel);
            MechJebDriver.StopDocking(vessel);
            // The ascent and the landing are let go here, not where the coroutine that waits for them next wakes: it may not wake.
            try
            {
                MechJebDriver.StopAscent(vessel);
                MechJebDriver.StopLanding(vessel);
                MechJebDriver.StopDriving(vessel);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning(Log.Tag + " Autopilot: MechJeb's ascent or landing could not be let go: " + e.Message);
            }
            ParsekBridge.StopRecording();
        }

        // The flight as a coroutine for a behaviour to run. An exception anywhere in it ends the flight with its reason, with MechJeb let go, instead of
        // leaving a flight that is no longer run but still marked as on.
        internal IEnumerator RunSafely()
        {
            return SafeRun.Guard(flyNodes ? RunNodes() : Run(), Fail);
        }

        // A flight of the nodes flies the maneuver nodes the vessel holds, one after the other, as MechJeb executes a node: the player made them in the map.
        // The guide of a climb or a descent is no node to fly here. A node that was missed ends the flight, since MechJeb would burn it late and blind.
        private IEnumerator RunNodes()
        {
            if (!MechJebDriver.CanFly(vessel))
            {
                Stop(MechJebDriver.Unavailable ?? "The vessel has no MechJeb unit.");
                yield break;
            }
            ManeuverNode last = null;
            int again = 0;
            while (!cancelled)
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
                ManeuverNode node = GuideNode.First(vessel);
                if (node == null)
                {
                    Arrived = true;
                    Stop("All maneuver nodes are flown.");
                    yield break;
                }
                if (Navigation.Missed(vessel, node))
                {
                    Stop("The maneuver node was missed: its burn is over.");
                    yield break;
                }
                // A node that is still there after MechJeb was let fly it three times is one it cannot fly.
                again = node == last ? again + 1 : 0;
                last = node;
                if (again >= 3)
                {
                    Stop("MechJeb did not fly the maneuver node.");
                    yield break;
                }
                if (!Enough(node.DeltaV.magnitude, "the burn")) yield break;
                if (!MechJebDriver.ExecuteNode(vessel, true))
                {
                    Stop("MechJeb cannot fly the maneuver node.");
                    yield break;
                }
                yield return Burn(node.UT, NodePlanner.BurnPurpose.Other);
            }
        }

        private void Fail(System.Exception e)
        {
            Debug.LogError("[ReDefinition] Route: the flight failed: " + e);
            try
            {
                Cancel();
            }
            catch (System.Exception again)
            {
                Debug.LogWarning("[ReDefinition] Route: letting go of the executors failed as well: " + again);
            }
            Stop("The flight failed: " + e.Message);
        }

        private IEnumerator Run()
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
                // A search that runs in the background is asked again in the next frame, until it has an answer.
                while (true)
                {
                    try
                    {
                        RouteState own = RouteState.Of(vessel);
                        plan = NextStepPlanner.PlanNext(new Frame(vessel), route, PlanOptions.Flown(keepCourse), own != null ? own.ProgressFor(route) : null);
                    }
                    catch (System.Exception e)
                    {
                        Debug.LogError("[ReDefinition] Route: the plan failed: " + e);
                        Stop("The plan failed: " + e.Message);
                        yield break;
                    }
                    if (!plan.Pending) break;
                    Status = plan.Why;
                    yield return null;
                    // The flight may have been stopped, or the vessel lost or left, while the search ran.
                    if (cancelled) yield break;
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
                }
                keepCourse = false;
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
                    // A route is allowed this many corrections between the transfer and the capture: the miss that is left is the capture's to live with.
                    Destination goal = plan.Goal >= 0 && plan.Goal < route.Count ? route[plan.Goal] : null;
                    int allowed = CorrectionLimit(goal != null ? goal.Body : null);
                    // The limit is for each stop of the route: a transfer to the next one starts the count again.
                    if (plan.Goal != correctionGoal)
                    {
                        correctionGoal = plan.Goal;
                        corrections = 0;
                    }
                    if (corrections >= allowed)
                    {
                        Debug.Log("[ReDefinition] Route: the " + allowed + " correction" + (allowed == 1 ? "" : "s") + " allowed " + (allowed == 1 ? "is" : "are") + " burned; the course is kept.");
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
                if (plan.Kind == NodePlanner.StepKind.Drive)
                {
                    Destination place = plan.Goal >= 0 && plan.Goal < route.Count ? route[plan.Goal] : null;
                    if (place == null || !place.HasSite || ++drives > MostDrives)
                    {
                        Stop(place == null || !place.HasSite ? "The drive has no place to go to." : "The rover did not get to the place.");
                        yield break;
                    }
                    Status = plan.Why;
                    if (!MechJebDriver.StartDriving(vessel, place.SiteLatitude, place.SiteLongitude))
                    {
                        Stop("MechJeb would not start the rover.");
                        yield break;
                    }
                    yield return DriveTo(place);
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
                    Debug.Log("[ReDefinition] Route: the landing needs " + needed.ToString("0") + " m/s by the simulation; the vessel has "
                              + (vessel.VesselDeltaV != null ? vessel.VesselDeltaV.TotalDeltaVVac.ToString("0") : "?") + " m/s in vacuum.");
                    if (double.IsPositiveInfinity(needed))
                    {
                        Stop("The engines are too weak to brake against the gravity here.");
                        yield break;
                    }
                    if (!double.IsNaN(needed) && !Enough(needed, "the landing")) yield break;
                    // Where the player chose the place, MechJeb is aimed at it.
                    Destination goal = plan.Goal >= 0 && plan.Goal < route.Count ? route[plan.Goal] : null;
                    bool started = goal != null && goal.HasSite ? MechJebDriver.StartLanding(vessel, goal.SiteLatitude, goal.SiteLongitude) : MechJebDriver.StartLanding(vessel);
                    if (!started)
                    {
                        Stop("MechJeb would not start the landing.");
                        yield break;
                    }
                    yield return Descend();
                    continue;
                }
                if (plan.Kind == NodePlanner.StepKind.Rendezvous)
                {
                    Status = plan.Why;
                    Vessel target = FlightGlobals.Vessels.Find(v => v.id == plan.Target);
                    // The engines the rendezvous burns with must be lit, and a target that is not in an orbit cannot be met.
                    if (!Engines.Running(vessel))
                    {
                        Engines.FireNextStageIfCold(vessel);
                        yield return new WaitForSeconds(1f);
                    }
                    if (target == null || target.orbit == null || target.orbit.eccentricity >= 1.0 || target.LandedOrSplashed || !MechJebDriver.StartRendezvous(vessel, target))
                    {
                        Stop("MechJeb would not start the rendezvous.");
                        yield break;
                    }
                    yield return Meet(target);
                    MechJebDriver.StopRendezvous(vessel);
                    if ((plan.Port != 0u || plan.Dock) && !cancelled && !Lost && target != null && NextStepPlanner.Met(vessel, target))
                    {
                        // The target is loaded now, so its ports can be read: the free one that suits the vessel's own is the one to dock at.
                        uint port = plan.Port != 0u ? plan.Port : MechJebDriver.FreeTargetPort(vessel, target);
                        if (port == 0u || !MechJebDriver.StartDocking(vessel, target, port))
                        {
                            Stop("MechJeb would not start the docking: it needs a free port that suits the target's, and RCS.");
                            yield break;
                        }
                        yield return Dock(target);
                        // The two vessels are one after the docking, and the one that is left goes on; the other is gone.
                        if (vessel == null || vessel.state == Vessel.State.DEAD)
                        {
                            Arrived = true;
                            Stop("Docked.");
                            yield break;
                        }
                        MechJebDriver.StopDocking(vessel);
                        if (vessel != FlightGlobals.ActiveVessel) yield break;
                    }
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
                yield return Burn(plan.UT, plan.Purpose);
                if (plan.Purpose == NodePlanner.BurnPurpose.Correction && lastBurnLit) corrections++;
            }
        }

        // How many corrections a transfer to the body may take: one to a moon, which is near, and two to a planet, where a small miss of the ejection grows
        // over the long way.
        internal static int CorrectionLimit(CelestialBody target)
        {
            return target != null && target.referenceBody == Planetarium.fetch.Sun ? 2 : 1;
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

        // Waits while MechJeb's ascent flies: it stops itself in the orbit. On the pad it waits for the player's launch, the first stage, which
        // MechJeb leaves to the launch button; the autopilot stays on meanwhile, and after the launch MechJeb's own staging takes over.
        private IEnumerator Ascend()
        {
            while (!cancelled && !Lost && (vessel.LandedOrSplashed || vessel.situation == Vessel.Situations.PRELAUNCH) && !Engines.Running(vessel))
            {
                Status = "Ascent: waiting for the launch. Stage to start the engines.";
                yield return new WaitForSeconds(0.5f);
            }
            float started = Time.realtimeSinceStartup;
            while (!cancelled && !Lost && MechJebDriver.AscentBusy(vessel) && Time.realtimeSinceStartup - started < 1200f)
            {
                Status = "Ascent: " + (vessel.altitude / 1000.0).ToString("0.0") + " km, " + vessel.verticalSpeed.ToString("0") + " m/s up.";
                Note();
                yield return new WaitForSeconds(0.5f);
            }
            MechJebDriver.StopAscent(vessel);
        }

        // Waits while MechJeb's landing flies, and ends it a moment after the vessel stands still on the ground.
        // The most drives a route is given: a rover that stops short is sent again, and then the route stops.
        private const int MostDrives = 3;
        private int drives;

        // MechJeb's rover drives until it has reached the waypoint (it sets the index back) or the time the distance takes at a walking pace is up.
        private IEnumerator DriveTo(Destination place)
        {
            float started = Time.realtimeSinceStartup;
            float still = 0f;
            while (!cancelled && !Lost && MechJebDriver.DrivingBusy(vessel) && Time.realtimeSinceStartup - started < 3600f)
            {
                // A rover that stands still for half a minute is held by something in its way: the drive ends, and the step asks again or gives up.
                still = vessel.srfSpeed < 0.3 && Time.realtimeSinceStartup - started > 15f ? still + 1f : 0f;
                if (still >= 30f) break;
                Vector3d spot = place.Body.GetWorldSurfacePosition(place.SiteLatitude, place.SiteLongitude, place.Body.TerrainAltitude(place.SiteLatitude, place.SiteLongitude));
                Status = "Driving: " + ((spot - vessel.GetWorldPos3D()).magnitude / 1000.0).ToString("0.0") + " km to go, " + vessel.srfSpeed.ToString("0") + " m/s.";
                Note();
                yield return new WaitForSeconds(1f);
            }
            MechJebDriver.StopDriving(vessel);
        }

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
                              + " m/s, " + vessel.situation + ", ground below " + GroundBelow() + ", PQS " + (vessel.mainBody.pqsController != null && vessel.mainBody.pqsController.isActive ? "active" : "off") + ".");
                still = vessel.LandedOrSplashed && vessel.srfSpeed < 0.5 ? still + 0.5f : 0f;
                if (still >= 3f) break;
                yield return new WaitForSeconds(0.5f);
            }
            MechJebDriver.StopLanding(vessel);
            // MechJeb ends the landing a moment before KSP calls the vessel landed: not planned again from the air.
            for (float waited = 0f; !cancelled && !vessel.LandedOrSplashed && waited < 10f && vessel.heightFromTerrain < 100f; waited += 0.5f)
                yield return new WaitForSeconds(0.5f);
        }

        // MechJeb's rendezvous runs until it ends by itself: the vessel is near the target at rest to it, or it found no way. The distance and the
        // relative speed are told on the way.
        // It is given as many orbits as MechJeb's own limit on the orbits of a rendezvous, and two more.
        private IEnumerator Meet(Vessel target)
        {
            double until = Planetarium.GetUniversalTime() + (MechJebDriver.RendezvousOrbits(vessel) + 2.0) * vessel.orbit.period;
            // MechJeb's rendezvous stays on once it has met the target and holds it there: the meeting is over when the vessel has been near the target
            // and at rest to it for ten seconds.
            double metSince = -1.0;
            while (!cancelled && !Lost && target != null && MechJebDriver.RendezvousBusy(vessel) && Planetarium.GetUniversalTime() < until)
            {
                double now = Planetarium.GetUniversalTime();
                if (!NextStepPlanner.Met(vessel, target)) metSince = -1.0;
                else if (metSince < 0.0) metSince = now;
                else if (now - metSince >= 10.0) break;
                Status = "Rendezvous: " + ((target.GetWorldPos3D() - vessel.GetWorldPos3D()).magnitude / 1000.0).ToString("0.0") + " km, "
                         + (target.obt_velocity - vessel.obt_velocity).magnitude.ToString("0.0") + " m/s to " + target.vesselName + ".";
                Note();
                yield return new WaitForSeconds(1f);
            }
        }

        // MechJeb's docking runs until the ports have coupled, or it gives up; the vessel that is in focus afterwards is the one that goes on.
        // It is given twice the time the distance takes at MechJeb's docking speed, and at least five minutes: it first lines the vessel up with the port.
        private IEnumerator Dock(Vessel target)
        {
            double speed = System.Math.Max(MechJebDriver.DockingSpeed(vessel), 0.1);
            double until = Planetarium.GetUniversalTime() + System.Math.Max(300.0, 2.0 * (target.GetWorldPos3D() - vessel.GetWorldPos3D()).magnitude / speed);
            while (!cancelled && !Lost && vessel != null && MechJebDriver.DockingBusy(vessel) && Planetarium.GetUniversalTime() < until)
            {
                Status = "Docking.";
                Note();
                yield return new WaitForSeconds(1f);
            }
        }

        // Lets a section go: its decoupler fires, or its docking port undocks. Of the two vessels that are then there, the one with a MechJeb unit
        // goes on with the route, or the heavier where both or neither have one. It takes the vessel's name and the autopilot's state, and is put
        // in focus where it is not. The section's vessel is named for the section.
        private IEnumerator Release(SectionRelease section)
        {
            string name = vessel.vesselName;
            var before = new HashSet<System.Guid>();
            foreach (Vessel other in FlightGlobals.Vessels) before.Add(other.id);
            Part separator = section.FindOn(vessel);
            if (separator != null)
            {
                foreach (ModuleDecouplerBase decoupler in separator.FindModulesImplementing<ModuleDecouplerBase>())
                    if (!decoupler.isDecoupled) decoupler.Decouple();
                foreach (ModuleDockingNode port in separator.FindModulesImplementing<ModuleDockingNode>())
                {
                    if (port.state.StartsWith("PreAttached")) port.Decouple();
                    else if (port.state.StartsWith("Docked")) port.Undock();
                }
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
            if (toPeriapsis > 30.0)
            {
                WarpGuard.SelfWarpUntil = Planetarium.GetUniversalTime() + toPeriapsis;
                TimeWarp.fetch.WarpTo(WarpGuard.SelfWarpUntil);
            }
            bool entered = false;
            bool guarding = false;
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
                if (climbing != guarding)
                {
                    guarding = climbing;
                    Debug.Log("[ReDefinition] Route: the heat guard " + (climbing ? "acts" : "lets go") + ": " + hottest + " at " + (share * 100.0).ToString("0") + " % of its limit, "
                              + vessel.altitude.ToString("0") + " m up, " + vessel.srfSpeed.ToString("0") + " m/s; thrust " + (climbing ? "full, nose away from the ground." : "off."));
                }
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
        private IEnumerator Burn(double nodeUT, NodePlanner.BurnPurpose purpose)
        {
            float nextStage = 0f;
            float began = Time.realtimeSinceStartup;
            ManeuverNode first = vessel.patchedConicSolver != null && vessel.patchedConicSolver.maneuverNodes.Count > 0 ? vessel.patchedConicSolver.maneuverNodes[0] : null;
            var watch = new BurnWatch(vessel, first, purpose, Ledger.Total);
            while (!cancelled && !Lost && MechJebDriver.Busy(vessel))
            {
                // A burn that goes on for an hour of the player's time is not one: MechJeb is let go, and the route is planned again from where the vessel is.
                if (Time.realtimeSinceStartup - began > 3600f)
                {
                    Debug.LogWarning("[ReDefinition] Route: the burn went on for an hour; MechJeb is let go.");
                    MechJebDriver.Abort(vessel);
                    yield break;
                }
                string state = MechJebDriver.State(vessel);
                Status = "Burn: " + state + ", " + KSPUtil.PrintTimeCompact(nodeUT - Planetarium.GetUniversalTime(), false) + " to the node.";
                Note();
                watch.Sample();
                if ((state == "LEAD" || state == "BURN") && Time.realtimeSinceStartup > nextStage)
                {
                    nextStage = Time.realtimeSinceStartup + 2f;
                    if (!Engines.Running(vessel) && !Engines.FireNextStageIfCold(vessel)) MechJebDriver.Abort(vessel);
                }
                yield return new WaitForSeconds(0.5f);
            }
            Debug.Log("[ReDefinition] Route: " + watch.Finish(Ledger));
            lastBurnLit = watch.Burned;
        }

        // The vessel brought to rest after a burn, before it coasts or warps: MechJeb lets the attitude go when the burn is done, and a vessel that still turns
        // keeps turning through the warp where PersistentRotation holds its spin. The stability assist is switched on, and the turn waited for.
        private IEnumerator Steady()
        {
            // A vessel that cannot hold its attitude has nothing to wait for.
            if (vessel.angularVelocity.magnitude <= SteadyRate || vessel.Autopilot == null || !vessel.Autopilot.Enabled) yield break;
            bool wasOn = vessel.ActionGroups[KSPActionGroup.SAS];
            vessel.ActionGroups.SetGroup(KSPActionGroup.SAS, true);
            if (vessel.Autopilot != null && vessel.Autopilot.Enabled) vessel.Autopilot.SetMode(VesselAutopilot.AutopilotMode.StabilityAssist);
            float from = Time.realtimeSinceStartup;
            double rate = vessel.angularVelocity.magnitude;
            while (!cancelled && !Lost && vessel.angularVelocity.magnitude > SteadyRate && Time.realtimeSinceStartup - from < 60f)
                yield return new WaitForSeconds(0.5f);
            Debug.Log("[ReDefinition] Route: the vessel turned at " + rate.ToString("0.000") + " rad/s after the burn; the stability assist " + (wasOn ? "was on" : "was switched on")
                      + " and it turned at " + vessel.angularVelocity.magnitude.ToString("0.000") + " rad/s after " + (Time.realtimeSinceStartup - from).ToString("0") + " s.");
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
            yield return Steady();
            WarpGuard.SelfWarpUntil = until;
            TimeWarp.fetch.WarpTo(until);
            float started = Time.realtimeSinceStartup;
            double coastFrom = Planetarium.GetUniversalTime(), fastest = 0.0, rateAtStart = vessel.angularVelocity.magnitude;
            while (!cancelled && vessel.mainBody == before && Planetarium.GetUniversalTime() < until && Time.realtimeSinceStartup - started < 600f)
            {
                fastest = System.Math.Max(fastest, vessel.angularVelocity.magnitude);
                yield return new WaitForSeconds(0.5f);
            }
            TimeWarp.SetRate(0, true);
            Ledger.Coasted(Planetarium.GetUniversalTime() - coastFrom, fastest);
            Debug.Log("[ReDefinition] Route: coast of " + (Planetarium.GetUniversalTime() - coastFrom).ToString("0") + " s: the vessel turned at " + rateAtStart.ToString("0.000")
                      + " rad/s at the start, at most " + fastest.ToString("0.000") + " rad/s, and at " + vessel.angularVelocity.magnitude.ToString("0.000") + " rad/s at the end.");
            yield return new WaitForSeconds(1f);
        }

        // The status into KSP.log, at most every 10 s, so that a flight can be followed afterwards.
        // The ground under the vessel on the terrain layer, as a ray finds it: its distance, or none. The physics' own view of the ground.
        private string GroundBelow()
        {
            if (RouteTuning.NoGroundRay) return "not asked";
            RaycastHit hit;
            Vector3 up = FlightGlobals.getUpAxis(vessel.CoM);
            return Physics.Raycast(vessel.CoM, -up, out hit, 500f, 1 << 15) ? hit.distance.ToString("0.0") + " m" : "none";
        }

        private void Note()
        {
            if (Time.realtimeSinceStartup < nextNote) return;
            nextNote = Time.realtimeSinceStartup + 10f;
            Debug.Log("[ReDefinition] Route: " + Status + " [" + Engines.Report(vessel) + "]" + (Status.StartsWith("Landing") ? " MechJeb: " + MechJebDriver.LandingStatus(vessel)
                      + ", throttle " + vessel.ctrlState.mainThrottle.ToString("0.00") + ", warp " + TimeWarp.CurrentRate + "x" : ""));
        }

        private void Stop(string why)
        {
            if (!Finished) Debug.Log("[ReDefinition] Route: flight summary: " + Ledger.Summary() + " Ended: " + why);
            WarpGuard.SelfWarpUntil = 0.0;
            MechJebDriver.RestoreSettings(vessel);
            Status = why;
            Finished = true;
            ParsekBridge.StopRecording();
        }
    }
}
