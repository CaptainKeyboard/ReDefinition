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
        // How often the saved route is read, for the nodes and a resumed flight: the file holds all the plans.
        private const float RouteAge = 5f;

        internal static RouteDriver Instance;

        private AutopilotFlight flight;
        private Navigation follower = new Navigation();
        private Vessel followed;
        private float next;
        // When the vessel in focus was first seen: nothing is done to a vessel just loaded before it has settled.
        private float seen;
        private float routeRead = -1000f;
        private List<Destination> route;

        // The route the driver holds for the flight, as the planner has it: a probe that leaves the flight as the player does gives it here.
        internal void Remember(List<Destination> destinations)
        {
            quick = null;
            route = destinations;
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
            GameEvents.onGameSceneLoadRequested.Add(OnSceneLoadRequested);
            GameEvents.onGamePause.Add(OnPause);
            GameEvents.onSceneConfirmExit.Add(OnConfirmExit);
            // What was refused in an earlier flight has been told, or is not the news of this one.
            RouteMessages.Refused.Clear();
            // The player takes the maneuver of a climb or a descent away: the navigation that made it is over.
            GuideNode.OnDismissed = () => SetNavigation(FlightGlobals.ActiveVessel, false);
        }

        // Whether the engines are burning: the autopilot's flight runs the whole time and says nothing of that. Engines that are lit and give no thrust are not.
        private static bool Burning(Vessel vessel)
        {
            return vessel.ctrlState.mainThrottle > 0.01f || Engines.Burning(vessel);
        }

        // The pause menu's buttons for leaving the flight save the game right after this event and before the scene is asked for, so the route is kept now
        // for the save to hold it. A route kept only when the scene is asked for is in the game's memory, not in its file.
        // The exits that do not save (a vessel that burns, or flies, cannot be saved) leave the old game to be loaded: there is nothing to keep it in.
        private void OnConfirmExit(GameScenes scene)
        {
            discarding = FlightGlobals.ClearToSave() != ClearToSaveStatus.CLEAR;
            if (!discarding) OnPause();
        }

        // Whether the flight is left without being saved, so that no route is kept for it.
        private bool discarding;

        // The pause menu is where the flight is left from, and it saves the game before the scene is asked for: the route is kept for the background by then.
        private void OnPause()
        {
            Vessel vessel = FlightGlobals.ActiveVessel;
            RouteState state = RouteState.Of(vessel);
            if (vessel == null || state == null || !state.Autopilot || route == null || FlightGlobals.ready == false) return;
            if (!RouteHandOver.UsesBackground) return;
            // Under way, with the engines running or in the air, the plan is made when the scene is asked for; the same pause is not planned twice.
            if (Burning(vessel) || vessel.situation == Vessel.Situations.FLYING || BackgroundRoutes.Holds(vessel, route)) return;
            string problem = RouteHandOver.ToBackground(vessel, route, false);
            if (problem != null) ScreenMessages.PostScreenMessage("The route is not flown while you are away: " + problem, 12f, ScreenMessageStyle.UPPER_CENTER);
        }

        // The player leaves the flight with the autopilot on: the rest of the route is planned. The vessel flies it in the background
        // (BackgroundRoutes), or Parsek plays it as a recording and puts the vessel back at its end.
        private void OnSceneLoadRequested(GameScenes scene)
        {
            Vessel vessel = FlightGlobals.ActiveVessel;
            RouteState state = RouteState.Of(vessel);
            if ((scene != GameScenes.SPACECENTER && scene != GameScenes.TRACKSTATION) || vessel == null || state == null) return;
            if (discarding)
            {
                discarding = false;
                return;
            }
            if (!state.Autopilot || route == null)
            {
                BackgroundRoutes.Cancel(vessel);
                return;
            }
            // A burn that is under way ends here: the vessel is recorded, and put back, without thrust, and the route is planned again from its orbit.
            bool burning = Burning(vessel);
            if (flight != null) flight.Cancel();
            FlightInputHandler.state.mainThrottle = 0f;
            // The route was kept when the pause menu opened; the time stands still now, which a plan of a launch cannot be made in.
            bool background = RouteHandOver.UsesBackground;
            if (!burning && background && BackgroundRoutes.Holds(vessel, route))
            {
                Debug.Log(Log.Tag + " Route: the flight is left with the autopilot on; the route kept when the pause menu opened stays.");
                return;
            }
            string problem = background ? RouteHandOver.ToBackground(vessel, route, true) : RouteHandOver.ToParsek(vessel, route);
            if (problem != null && background) RouteMessages.Refused.Add(problem);
        }

        private void OnDestroy()
        {
            GameEvents.onGameSceneLoadRequested.Remove(OnSceneLoadRequested);
            GameEvents.onGamePause.Remove(OnPause);
            GameEvents.onSceneConfirmExit.Remove(OnConfirmExit);
            if (Instance == this) Instance = null;
            if (flight != null) flight.Cancel();
            GuideNode.Release(FlightGlobals.ActiveVessel);
            GuideNode.OnDismissed = null;
        }

        // The flight begins, and the vessel is marked as flown by the autopilot, which holds when it is loaded again.
        internal void StartFlight(Vessel vessel, List<Destination> destinations)
        {
            RouteState state = RouteState.Of(vessel);
            if (state == null || flight != null || destinations == null) return;
            GuideNode.Release(vessel);
            // The climb the navigation guides stays its route when the autopilot flies it and when it is over.
            if (!ReferenceEquals(destinations, quick)) quick = null;
            state.Autopilot = true;
            route = destinations;
            routeRead = Time.realtimeSinceStartup;
            Begin(vessel, state, destinations);
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
            List<Destination> destinations = state.Navigation ? ReadRoute(vessel) : RouteSource.For(vessel.vesselName);
            if (destinations != null)
            {
                if (!state.Navigation) SetNavigation(vessel, false);
                StartFlight(vessel, destinations);
                return;
            }
            if (GuideNode.First(vessel) != null)
            {
                state.Autopilot = true;
                Begin(vessel, state, null);
                return;
            }
            status = "There is nothing to fly: no route in the planner and no maneuver node.";
            ScreenMessages.PostScreenMessage(status, 6f, ScreenMessageStyle.UPPER_CENTER);
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
            quick = null;
            GuideNode.Reset();
            if (!on) GuideNode.Release(vessel);
            if (!on && !state.Autopilot) KspOrbits.ClearNodes(vessel);
        }

        private void Begin(Vessel vessel, RouteState state, List<Destination> destinations)
        {
            flight = new AutopilotFlight(vessel, destinations);
            StartCoroutine(Run(flight, state));
        }

        private IEnumerator Run(AutopilotFlight running, RouteState state)
        {
            yield return running.RunSafely();
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
                followed = vessel;
                seen = Time.realtimeSinceStartup;
                follower = new Navigation();
                route = null;
                quick = null;
                routeRead = -1000f;
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
                if (ReadRoute(vessel) == null && GuideNode.First(vessel) != null)
                {
                    Begin(vessel, state, null);
                    return;
                }
                if (ReadRoute(vessel) == null)
                {
                    status = "The autopilot has no route to fly.";
                    state.Autopilot = false;
                    return;
                }
                Debug.Log(Log.Tag + " Route: the flight goes on for " + vessel.vesselName + ".");
                Begin(vessel, state, route);
            }
            else if (!state.Autopilot && state.Navigation)
            {
                follower.Tick(vessel, ReadRoute(vessel));
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
                follower.Guide(vessel);
                GuideNode.Ensure(vessel);
            }
            else GuideNode.Release(vessel);
            // The warp the player turns up is cut back as the next burn, change of orbit or air comes near.
            if (state != null && (state.Autopilot || state.Navigation) && !ParsekBridge.IsGhost(vessel)) WarpGuard.Limit(vessel);
        }

        // The route the player asked for from the map, for a vessel without a plan of its own: it holds until the navigation is switched off.
        private List<Destination> quick;

        // Whether the route held is aimed at a place on the ground.
        internal bool Aimed
        {
            get { return quick != null && quick.Count > 0 && quick[quick.Count - 1].HasSite; }
        }

        // The vessel on the ground is guided into the lowest orbit round its body, with no plan made in the planner: the navigation is switched on for this route.
        internal void StartAscent(Vessel vessel)
        {
            RouteState state = RouteState.Of(vessel);
            if (state == null || vessel.mainBody == null || flight != null || state.Autopilot) return;
            SetNavigation(vessel, true);
            quick = new List<Destination>
            {
                new Destination { Body = vessel.mainBody, Surface = true, Altitude = 0.0 },
                new Destination { Body = vessel.mainBody, Surface = false, Altitude = NextStepPlanner.LowOrbit(vessel.mainBody) },
            };
            route = quick;
            routeRead = Time.realtimeSinceStartup;
            Debug.Log(Log.Tag + " Navigation: " + vessel.vesselName + " is guided into an orbit " + (NextStepPlanner.LowOrbit(vessel.mainBody) / 1000.0).ToString("0") + " km over " + vessel.mainBody.bodyName + ".");
        }

        // The vessel in orbit is guided down to a place on the ground the player chose: the navigation is switched on for a route from the orbit to that place,
        // with no plan made in the planner. The deorbit burn is a node aimed at the place, and the descent is guided as any other.
        internal void StartDescent(Vessel vessel, double latitude, double longitude)
        {
            RouteState state = RouteState.Of(vessel);
            if (state == null || vessel.mainBody == null || flight != null || state.Autopilot) return;
            SetNavigation(vessel, true);
            quick = new List<Destination>
            {
                new Destination { Body = vessel.mainBody, Surface = false, Altitude = 0.5 * (vessel.orbit.PeA + vessel.orbit.ApA) },
                new Destination { Body = vessel.mainBody, Surface = true, HasSite = true, SiteLatitude = latitude, SiteLongitude = longitude },
            };
            route = quick;
            routeRead = Time.realtimeSinceStartup;
            Debug.Log(Log.Tag + " Navigation: " + vessel.vesselName + " is guided down to " + latitude.ToString("0.00") + ", " + longitude.ToString("0.00") + " on " + vessel.mainBody.bodyName + ".");
        }

        // The vessel's saved route, read again every few seconds.
        private List<Destination> ReadRoute(Vessel vessel)
        {
            if (quick != null) return quick;
            if (route == null || Time.realtimeSinceStartup - routeRead > RouteAge)
            {
                route = RouteSource.For(vessel.vesselName);
                routeRead = Time.realtimeSinceStartup;
            }
            return route;
        }
    }
}
