using System;
using System.Collections.Generic;
using System.Globalization;
using ReDefinition.Autopilot.Flight;
using ReDefinition.Autopilot.Guidance;
using ReDefinition.Autopilot.Planning;
using ReDefinition.Core;
using ReDefinition.Landing;
using ReDefinition.Orbits;
using UnityEngine;

namespace ReDefinition.Autopilot.Background
{
    // What the player is told of the routes the vessels fly on their own, in the scenes where he is away from them.
    internal sealed class RouteMessages
    {
        // The routes that were not kept as the flight was left, with the reason: told in the scene that follows, where the player is.
        internal static readonly List<string> Refused = new List<string>();
        // Whether the player has been told, in this scene, what the vessels do while he is away.
        private bool told;

        // In the space centre and the tracking station: which vessels fly their route on by themselves and when the next step comes, and the routes that were
        // refused. Nothing else shows the player that a route is kept, or that game time has to pass for it to go on.
        // The game drops the messages posted while a scene is still loading, so they wait a moment.
        internal void Tell(List<RouteEntry> entries)
        {
            if (told || ScreenMessages.Instance == null || Time.timeSinceLevelLoad < 3f || (HighLogic.LoadedScene != GameScenes.SPACECENTER && HighLogic.LoadedScene != GameScenes.TRACKSTATION)) return;
            bool posted = true;
            foreach (string refused in Refused) posted &= ScreenMessages.PostScreenMessage("The route is not flown while you are away: " + refused, 12f, ScreenMessageStyle.UPPER_CENTER) != null;
            double now = Planetarium.GetUniversalTime();
            foreach (RouteEntry entry in entries)
                if (entry.Steps.Count > 0 && FlightGlobals.Vessels.Exists(v => v.id == entry.Vessel))
                    posted &= ScreenMessages.PostScreenMessage(entry.Name + " flies its route on by itself: the next step comes in " + KSPUtil.PrintTimeCompact(Math.Max(0.0, entry.Steps[0].UT - now), false)
                                                               + ". Warp time to go on.", 12f, ScreenMessageStyle.UPPER_CENTER) != null;
            if (!posted) return;
            told = true;
            Refused.Clear();
            Debug.Log(Log.Tag + " Route: in the " + HighLogic.LoadedScene + " " + entries.Count + " route(s) are kept for the vessels.");
        }
    }
}
