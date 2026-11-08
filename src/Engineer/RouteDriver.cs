using System.Collections;
using System.Collections.Generic;
using ReDefinition.Autopilot;
using ReDefinition.Autopilot.Background;
using ReDefinition.Autopilot.Flight;
using ReDefinition.Autopilot.Guidance;
using ReDefinition.Autopilot.Planning;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Engineer
{
    // Runs what the planner does for the vessel in flight: MechJeb flying its route, or the nodes kept up for the player. The state is the
    // vessel's own (RouteState), so a vessel that was flown by the autopilot when it was left goes on when it is loaded again. A ghost of
    // Parsek's is left alone.
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    internal sealed class RouteDriver : MonoBehaviour
    {
        private const float Interval = 1f;
        private const float Settle = 4f;

        internal static RouteDriver Instance;

        private AutopilotFlight flight;
        private Navigation follower = new Navigation();
        private Vessel followed;
        private float next;
        // When the vessel in focus was first seen: nothing is done to a vessel just loaded before it has settled.
        private float seen;
        // The route the driver follows, and where it comes from.
        private readonly HeldRoute held = new HeldRoute();
        // What is done when the flight is left with the autopilot on.
        private SceneExit exit;

        // The route the driver holds for the flight, as the planner has it: a probe that leaves the flight as the player does gives it here.
        internal void Remember(List<Destination> destinations)
        {
            held.Clear();
            held.HoldPlanner(destinations);
        }
        private string status = "";

        // What the flight or the nodes did last, for the window.
        internal string Status
        {
            get { return flight != null ? flight.Status : follower.Status.Length > 0 ? follower.Status : status; }
        }

        internal bool Flying
        {
            get { return flight != null; }
        }

        // The throttle the descent guidance asks for, for the probes that fly as the player does.
        internal double GuidedThrottle
        {
            get { return follower.Throttle; }
        }

        private void Awake()
        {
            Instance = this;
            ParsekBridge.WarnOnce();
            exit = new SceneExit(held, () =>
            {
                if (flight != null) flight.Cancel();
            });
            exit.Subscribe();
            // What was refused in an earlier flight has been told, or is not the news of this one.
            RouteMessages.Refused.Clear();
            // The player takes the maneuver of a climb or a descent away: the navigation that made it is over.
            GuideNode.OnDismissed = () => SetNavigation(FlightGlobals.ActiveVessel, false);
        }

        private void OnDestroy()
        {
            if (exit != null) exit.Unsubscribe();
            DeorbitAim.Abandon();
            if (Instance == this) Instance = null;
            if (flight != null) flight.Cancel();
            GuideNode.Release();
            GuideNode.OnDismissed = null;
        }

        // The flight begins, and the vessel is marked as flown by the autopilot, which holds when it is loaded again.
        internal void StartFlight(Vessel vessel, List<Destination> destinations)
        {
            RouteState state = RouteState.Of(vessel);
            if (state == null || flight != null || destinations == null) return;
            GuideNode.Release();
            // The climb the navigation guides stays its route when the autopilot flies it and when it is over.
            held.HoldPlanner(destinations);
            state.Autopilot = true;
            Begin(vessel, state, new AutopilotFlight(vessel, destinations));
        }

        // The button of the autopilot starts what there is to fly, or lets the vessel go. First the route the navigation holds: the climb or the descent it
        // guides, or the planner's route. Else the planner's route, else the maneuver nodes the vessel holds. Nothing is done where MechJeb cannot fly.
        internal void ToggleAutopilot(Vessel vessel)
        {
            RouteState state = RouteState.Of(vessel);
            if (state == null || vessel == null || ParsekBridge.IsGhost(vessel)) return;
            if (flight != null || state.Autopilot)
            {
                StopFlight(vessel);
                return;
            }
            if (!MechJebDriver.CanFly(vessel))
            {
                status = MechJebDriver.Unavailable ?? "The vessel has no MechJeb unit to fly the route.";
                ScreenMessages.PostScreenMessage(status, 6f, ScreenMessageStyle.UPPER_CENTER);
                return;
            }
            List<Destination> destinations = state.Navigation ? held.Read(vessel) : RouteSource.For(vessel.vesselName);
            if (destinations != null)
            {
                StartFlight(vessel, destinations);
                return;
            }
            if (GuideNode.First(vessel) != null)
            {
                // A flight of nodes holds no route: one left from before would be handed over when the flight is left.
                held.Clear();
                state.Autopilot = true;
                Begin(vessel, state, AutopilotFlight.OfNodes(vessel));
                return;
            }
            // Nothing to fly: the button offers what the vessel can be sent to, and the choice sets the route.
            status = "There is nothing to fly yet: choose where to go.";
            LiveMenu.Open(vessel);
        }

        internal void StopFlight(Vessel vessel)
        {
            RouteState state = RouteState.Of(vessel);
            if (state != null) state.Autopilot = false;
            if (flight != null)
            {
                flight.Cancel();
                status = "The flight is stopped.";
                flight = null;
            }
        }

        // The route planned and kept up to date for the player to fly, or not; the nodes it made go when it is switched off.
        internal void SetNavigation(Vessel vessel, bool on)
        {
            RouteState state = RouteState.Of(vessel);
            if (state == null) return;
            state.Navigation = on;
            follower = new Navigation();
            held.Clear();
            DeorbitAim.Abandon();
            GuideNode.Reset();
            if (!on) GuideNode.Release();
            // The navigation shows the maneuvers of the route, so the ones there are taken away when it starts; those the player adds after are kept.
            if (!state.Autopilot) KspOrbits.ClearNodes(vessel);
        }

        private void Begin(Vessel vessel, RouteState state, AutopilotFlight started)
        {
            flight = started;
            MechJebDriver.SetSwitch(vessel, MechJebDriver.Switch.AutoStaging, state.AutoStaging);
            StartCoroutine(Run(flight, state, vessel));
        }

        private IEnumerator Run(AutopilotFlight running, RouteState state, Vessel flown)
        {
            yield return running.RunSafely();
            // The route is flown to its end: the planner is empty for the next one.
            if (running.Arrived && running.OfRoute)
            {
                string vesselName = flown.vesselName;
                // A docking ends the flown vessel in the one it joined, which is the one in focus then.
                Vessel arrived = flown != null && flown.state != Vessel.State.DEAD ? flown : FlightGlobals.ActiveVessel;
                if (MapPlannerWindow.DeploysOnArrival(vesselName)) Deployables.Extend(arrived);
                MapPlannerWindow.RouteDone(vesselName, running.RouteSignature);
            }
            if (flight == running)
            {
                status = running.Status;
                state.Autopilot = false;
                flight = null;
            }
        }

        private void Update()
        {
            if (Time.realtimeSinceStartup < next || !HighLogic.LoadedSceneIsFlight) return;
            next = Time.realtimeSinceStartup + Interval;
            Vessel vessel = FlightGlobals.ActiveVessel;
            if (vessel == null || !vessel.loaded) return;
            PassHeat.Watch(vessel);
            if (vessel != followed)
            {
                if (flight != null) flight.Cancel();
                flight = null;
                LiveMenu.Close();
                followed = vessel;
                seen = Time.realtimeSinceStartup;
                follower = new Navigation();
                held.Clear();
                RouteState focused = RouteState.Of(vessel);
                if (focused != null) MechJebDriver.SetSwitch(vessel, MechJebDriver.Switch.AutoStaging, focused.AutoStaging);
            }
            RouteState state = RouteState.Of(vessel);
            if (state == null) return;
            if (ParsekBridge.IsGhost(vessel))
            {
                if (flight != null) StopFlight(vessel);
                return;
            }
            if (Time.realtimeSinceStartup - seen < Settle) return;
            if (state.Autopilot && flight == null && !MechJebDriver.CanFly(vessel))
            {
                status = MechJebDriver.Unavailable ?? "The vessel has no MechJeb unit to fly the route.";
                state.Autopilot = false;
            }
            else if (state.Autopilot && flight == null)
            {
                List<Destination> saved = held.Read(vessel);
                if (saved == null && GuideNode.First(vessel) != null)
                {
                    Begin(vessel, state, AutopilotFlight.OfNodes(vessel));
                    return;
                }
                if (saved == null)
                {
                    status = "The autopilot has no route to fly.";
                    state.Autopilot = false;
                    return;
                }
                Debug.Log(Log.Tag + " Route: the flight goes on for " + vessel.vesselName + ".");
                Begin(vessel, state, new AutopilotFlight(vessel, saved));
            }
            else if (!state.Autopilot && state.Navigation)
            {
                follower.Tick(vessel, held.Read(vessel));
            }
        }

        // The guidance for a climb or a descent in every frame, for the flight view's marker and gauge.
        private void LateUpdate()
        {
            Vessel vessel = FlightGlobals.ActiveVessel;
            if (vessel == null || vessel != followed) return;
            RouteState state = RouteState.Of(vessel);
            if (state != null && !state.Autopilot && state.Navigation)
            {
                // A plan that is searched for in the background is asked for again in every frame, so that its search goes on as fast as it can.
                if (follower.Searching) follower.Tick(vessel, held.Read(vessel));
                follower.Guide(vessel);
                GuideNode.Ensure(vessel);
            }
            else GuideNode.Release();
            // The warp the player turns up is cut back as the next burn, change of orbit or air comes near.
            if (state != null && (state.Autopilot || state.Navigation) && !ParsekBridge.IsGhost(vessel)) WarpGuard.Limit(vessel);
        }

        // Whether the route held is aimed at a place on the ground.
        internal bool Aimed
        {
            get { return held.Aimed; }
        }

        // The vessel on the ground is guided into the lowest orbit round its body, with no plan made in the planner: the navigation is switched on for this route.
        internal void StartAscent(Vessel vessel)
        {
            RouteState state = RouteState.Of(vessel);
            if (state == null || vessel.mainBody == null) return;
            if (flight != null || state.Autopilot)
            {
                ScreenMessages.PostScreenMessage("The autopilot is flying: switch it off to launch to orbit.", 6f, ScreenMessageStyle.UPPER_CENTER);
                return;
            }
            SetNavigation(vessel, true);
            held.HoldQuick(new List<Destination>
            {
                new Destination { Body = vessel.mainBody, Surface = true, Altitude = 0.0 },
                new Destination { Body = vessel.mainBody, Surface = false, Altitude = NextStepPlanner.LowOrbit(vessel.mainBody) },
            });
            Debug.Log(Log.Tag + " Navigation: " + vessel.vesselName + " is guided into an orbit " + (NextStepPlanner.LowOrbit(vessel.mainBody) / 1000.0).ToString("0") + " km over " + vessel.mainBody.bodyName + ".");
        }

        // The vessel in orbit is guided down to a place on the ground the player chose: the navigation is switched on for a route from the orbit to that place,
        // with no plan made in the planner. The deorbit burn is a node aimed at the place, and the descent is guided as any other.
        internal void StartDescent(Vessel vessel, double latitude, double longitude)
        {
            RouteState state = RouteState.Of(vessel);
            if (state == null || vessel.mainBody == null || flight != null || state.Autopilot) return;
            SetNavigation(vessel, true);
            held.HoldQuick(new List<Destination>
            {
                new Destination { Body = vessel.mainBody, Surface = false, Altitude = 0.5 * (vessel.orbit.PeA + vessel.orbit.ApA) },
                new Destination { Body = vessel.mainBody, Surface = true, HasSite = true, SiteLatitude = latitude, SiteLongitude = longitude },
            });
            Debug.Log(Log.Tag + " Navigation: " + vessel.vesselName + " is guided down to " + latitude.ToString("0.00") + ", " + longitude.ToString("0.00") + " on " + vessel.mainBody.bodyName + ".");
        }
    }
}
