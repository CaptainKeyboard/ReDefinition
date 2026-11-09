using ReDefinition.Planner;
using System;
using System.Collections.Generic;
using ReDefinition.Autopilot;
using ReDefinition.Autopilot.Background;
using ReDefinition.Autopilot.Flight;
using ReDefinition.Autopilot.Planning;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Engineer
{
    // Where a route goes when the player leaves the flight with the autopilot on: to the background route, where the vessel flies it on its own, or to Parsek,
    // which plays it as a recording. The planning and the hand-over are the same wherever the player leaves from.
    internal static class RouteHandOver
    {
        // What the stages of a vessel are told by the planner's model, for the burns of the route.
        private static readonly IVesselFigures Figures = new BackgroundFigures();

        // Whether the flight goes to the background route: the setting says so, and where the addon is not there yet it is the way to go.
        internal static bool UsesBackground
        {
            get
            {
                
                return PlannerSeams.BackgroundRoute();
            }
        }

        // Plans the route for the vessel flying it on its own and hands it over; null where it was kept, else why not. With `late` the scene that follows is
        // asked for already.
        internal static string ToBackground(Vessel vessel, IList<Destination> route, bool late)
        {
            try
            {
                string problem = BackgroundRoutes.Hand(vessel, BackgroundRoutes.PlanFor(vessel, route), route, Figures, late);
                Debug.Log(Log.Tag + " Route: " + vessel.vesselName + " handed over for the background" + (late ? " as the scene is asked for" : "") + ": " + (problem ?? "kept") + ".");
                return problem;
            }
            catch (Exception e)
            {
                Debug.LogWarning(Log.Tag + " Route: the plan for the background failed: " + e);
                return "the plan could not be made (" + e.Message + ").";
            }
        }

        // Plans the route as the map shows it and commits it to Parsek as a recording; null where it was, else why not. The count of stops reached goes up to
        // where the plan ends, where Parsek puts the vessel back.
        internal static string ToParsek(Vessel vessel, IList<Destination> route)
        {
            try
            {
                RoutePlan plan = RouteSimulation.Run(vessel, route);
                RouteState own = RouteState.Of(vessel);
                if (own != null) own.ProgressFor(route).Reached = Math.Max(own.ProgressFor(route).Reached, plan.Reached);
                string problem = ParsekRecording.CommitPlan(vessel, plan, true);
                Debug.Log(Log.Tag + " Route: " + vessel.vesselName + " handed over to Parsek, " + plan.Steps.Count + " step(s) planned, " + plan.DeltaV.ToString("0") + " m/s: " + (problem ?? "kept") + ".");
                return problem;
            }
            catch (Exception e)
            {
                Debug.LogWarning(Log.Tag + " Route: the plan for Parsek failed: " + e);
                return "the plan could not be made (" + e.Message + ").";
            }
        }
    }
}
