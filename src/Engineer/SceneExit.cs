using System;
using ReDefinition.Autopilot;
using ReDefinition.Autopilot.Background;
using ReDefinition.Autopilot.Flight;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Engineer
{
    // What is done with the route when the flight is left with the autopilot on. The rest of it is planned and handed to the background (BackgroundRoutes) or
    // to Parsek, which plays it as a recording and puts the vessel back at its end. It listens for the pause menu, the confirmed exit and the request for another scene.
    internal sealed class SceneExit
    {
        private readonly HeldRoute held;
        private readonly Action cancelFlight;

        internal SceneExit(HeldRoute held, Action cancelFlight)
        {
            this.held = held;
            this.cancelFlight = cancelFlight;
        }

        internal void Subscribe()
        {
            GameEvents.onGameSceneLoadRequested.Add(OnSceneLoadRequested);
            GameEvents.onGamePause.Add(OnPause);
            GameEvents.onSceneConfirmExit.Add(OnConfirmExit);
        }

        internal void Unsubscribe()
        {
            GameEvents.onGameSceneLoadRequested.Remove(OnSceneLoadRequested);
            GameEvents.onGamePause.Remove(OnPause);
            GameEvents.onSceneConfirmExit.Remove(OnConfirmExit);
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
            if (vessel == null || state == null || !state.Autopilot || held.Held == null || FlightGlobals.ready == false) return;
            if (!RouteHandOver.UsesBackground) return;
            // Under way, with the engines running or in the air, the plan is made when the scene is asked for; the same pause is not planned twice.
            if (Burning(vessel) || vessel.situation == Vessel.Situations.FLYING || BackgroundRoutes.Holds(vessel, held.Held)) return;
            string problem = RouteHandOver.ToBackground(vessel, held.Held, false);
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
            if (!state.Autopilot || held.Held == null)
            {
                BackgroundRoutes.Cancel(vessel);
                return;
            }
            // A burn that is under way ends here: the vessel is recorded, and put back, without thrust, and the route is planned again from its orbit.
            bool burning = Burning(vessel);
            cancelFlight();
            FlightInputHandler.state.mainThrottle = 0f;
            // The route was kept when the pause menu opened; the time stands still now, which a plan of a launch cannot be made in.
            bool background = RouteHandOver.UsesBackground;
            if (!burning && background && BackgroundRoutes.Holds(vessel, held.Held))
            {
                Debug.Log(Log.Tag + " Route: the flight is left with the autopilot on; the route kept when the pause menu opened stays.");
                return;
            }
            string problem = background ? RouteHandOver.ToBackground(vessel, held.Held, true) : RouteHandOver.ToParsek(vessel, held.Held);
            if (problem != null && background) RouteMessages.Refused.Add(problem);
        }
    }
}
