using System.Collections;
using System.Collections.Generic;
using ReDefinition.Autopilot;
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

        private RouteFlight flight;
        private NodeFollower follower = new NodeFollower();
        private Vessel followed;
        private float next;
        // When the vessel in focus was first seen: nothing is done to a vessel just loaded before it has settled.
        private float seen;
        private float routeRead = -1000f;
        private List<Destination> route;
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

        private void Awake()
        {
            Instance = this;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            if (flight != null) flight.Cancel();
        }

        // The flight begins, and the vessel is marked as flown by the autopilot, which holds when it is loaded again.
        internal void StartFlight(Vessel vessel, List<Destination> destinations)
        {
            RouteState state = RouteState.Of(vessel);
            if (state == null || flight != null || destinations == null) return;
            state.Autopilot = true;
            route = destinations;
            routeRead = Time.realtimeSinceStartup;
            Begin(vessel, state, destinations);
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
        internal void SetAutonavigation(Vessel vessel, bool on)
        {
            RouteState state = RouteState.Of(vessel);
            if (state == null) return;
            state.Autonavigation = on;
            follower = new NodeFollower();
            if (!on && !state.Autopilot) KspOrbits.ClearNodes(vessel);
        }

        private void Begin(Vessel vessel, RouteState state, List<Destination> destinations)
        {
            flight = new RouteFlight(vessel, destinations);
            StartCoroutine(Run(flight, state));
        }

        private IEnumerator Run(RouteFlight running, RouteState state)
        {
            yield return running.Run();
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
            if (vessel != followed)
            {
                if (flight != null) flight.Cancel();
                flight = null;
                followed = vessel;
                seen = Time.realtimeSinceStartup;
                follower = new NodeFollower();
                route = null;
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
            if (state.Autopilot && flight == null && MechJebDriver.CanFly(vessel))
            {
                if (ReadRoute(vessel) == null)
                {
                    status = "The autopilot has no route to fly.";
                    state.Autopilot = false;
                    return;
                }
                Debug.Log(Log.Tag + " Route: the flight goes on for " + vessel.vesselName + ".");
                Begin(vessel, state, route);
            }
            else if (!state.Autopilot && state.Autonavigation)
            {
                follower.Tick(vessel, ReadRoute(vessel));
            }
        }

        // The vessel's saved route, read again every few seconds.
        private List<Destination> ReadRoute(Vessel vessel)
        {
            if (route == null || Time.realtimeSinceStartup - routeRead > RouteAge)
            {
                route = RouteSource.For(vessel.vesselName);
                routeRead = Time.realtimeSinceStartup;
            }
            return route;
        }
    }
}
