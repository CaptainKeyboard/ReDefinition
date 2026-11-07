using System.Collections.Generic;
using ReDefinition.Autopilot.Planning;
using ReDefinition.Ui;
using UnityEngine;

namespace ReDefinition.Engineer
{
    // What the Live button offers while the planner's route is empty: the goals the vessel can be sent to from where it is. A choice only sets the
    // planner's route and opens the planner; the next press of the button flies it. Shift adds the goal to the route instead of replacing it.
    internal static class LiveMenu
    {
        private static PopupDialog shown;

        internal static void Open(Vessel vessel)
        {
            Close();
            MapPlannerWindow window = MapPlannerWindow.Instance;
            if (vessel == null || vessel.mainBody == null || window == null) return;
            CelestialBody here = vessel.mainBody;
            var entries = new List<DialogGUIBase>();
            bool onGround = vessel.situation == Vessel.Situations.LANDED || vessel.situation == Vessel.Situations.PRELAUNCH || vessel.situation == Vessel.Situations.SPLASHED;
            bool inOrbit = vessel.situation == Vessel.Situations.ORBITING;
            if (onGround)
                entries.Add(Entry("Launch to Orbit", () => window.PlanGoal(vessel, here, false, NextStepPlanner.LowOrbit(here), MapPlannerWindow.Adding)));
            if (inOrbit && here.hasSolidSurface && here.pqsController != null)
                entries.Add(Entry("Land on " + here.displayName.LocalizeRemoveGender(), () => window.PlanGoal(vessel, here, true, 0.0, MapPlannerWindow.Adding)));
            // An orbit that is not round is rounded at its apoapsis or its periapsis: the planner's change of orbit to that altitude is one burn there.
            if (inOrbit && vessel.orbit != null && vessel.orbit.eccentricity < 1.0 && vessel.orbit.eccentricity > 0.01)
            {
                double apoapsis = vessel.orbit.ApA, periapsis = vessel.orbit.PeA;
                entries.Add(Entry("Circularize at Apoapsis", () => window.PlanGoal(vessel, here, false, apoapsis, MapPlannerWindow.Adding)));
                if (periapsis > 1000.0) entries.Add(Entry("Circularize at Periapsis", () => window.PlanGoal(vessel, here, false, periapsis, MapPlannerWindow.Adding)));
            }
            if (inOrbit && vessel.orbit != null && vessel.orbit.eccentricity < 1.0)
            {
                if (vessel.orbit.inclination > NodePlanner.PlaneTolerance && vessel.orbit.inclination < 180.0 - NodePlanner.PlaneTolerance)
                    entries.Add(Entry("Equatorial Orbit", () => TurnPlane(window, vessel, null, true)));
                Vessel plane = Meetable(vessel);
                if (plane != null) entries.Add(Entry("Match Plane with " + plane.vesselName, () => TurnPlane(window, vessel, plane, false)));
            }
            CelestialBody home = FlightGlobals.GetHomeBody();
            if (home != null && home != here && (inOrbit || onGround))
                entries.Add(Entry("Return to " + home.displayName.LocalizeRemoveGender(), () => window.PlanGoal(vessel, home, false, NextStepPlanner.LowOrbit(home), MapPlannerWindow.Adding)));
            foreach (CelestialBody body in onGround || inOrbit ? Reachable(here) : new CelestialBody[0])
            {
                CelestialBody goal = body;
                if (goal == home && home != here) continue;
                entries.Add(Entry(goal.displayName.LocalizeRemoveGender() + " Orbit", () => window.PlanGoal(vessel, goal, false, NextStepPlanner.LowOrbit(goal), MapPlannerWindow.Adding)));
            }
            Vessel meet = Meetable(vessel);
            if (meet != null && inOrbit)
            {
                entries.Add(Entry("Rendezvous with " + meet.vesselName, () => Meet(window, vessel, meet, false)));
                if (HasDockingPort(meet)) entries.Add(Entry("Dock with " + meet.vesselName, () => Meet(window, vessel, meet, true)));
            }
            entries.Add(Entry("Open Planner", window.OpenNow));
            entries.Add(new DialogGUIButton("Close", () => { }, 220f, 26f, true));
            var dialog = new MultiOptionDialog("ReDefinitionLiveMenu", "", "Autopilot", HighLogic.UISkin, 240f,
                new DialogGUIVerticalLayout(220f, -1f, 2f, new RectOffset(4, 4, 4, 4), TextAnchor.UpperLeft, entries.ToArray()));
            var anchor = new Vector2(0.2f, 0.72f);
            shown = PopupDialog.SpawnPopupDialog(anchor, anchor, dialog, false, HighLogic.UISkin, false);
            UnityMouseEvents.Shield(ScreenFit.Add(shown));
            UiHygiene.MouseOnly(shown.gameObject);
        }

        // The route set to a round orbit at the altitude, from the orbit now.
        internal static void Circularize(MapPlannerWindow window, Vessel vessel, double altitude)
        {
            window.PlanGoal(vessel, vessel.mainBody, false, altitude, MapPlannerWindow.Adding);
        }

        // The route set to the orbit as it is (periapsis and apoapsis) turned to the equator, or to the plane of the vessel.
        internal static void TurnPlane(MapPlannerWindow window, Vessel vessel, Vessel plane, bool equatorial)
        {
            Orbit orbit = vessel.orbit;
            var shape = new MapPlannerWindow.Shape
            {
                Apoapsis = orbit.eccentricity > 0.01 ? orbit.ApA : 0.0, HasInclination = equatorial, Inclination = 0.0, MatchPlane = equatorial ? null : plane,
            };
            window.PlanGoal(vessel, vessel.mainBody, false, System.Math.Max(1000.0, orbit.PeA), MapPlannerWindow.Adding, false, 0.0, 0.0, null, false, shape);
        }

        // The vessel the player targets, if the flown one can be sent to meet it: another vessel in an orbit round a body.
        internal static Vessel Meetable(Vessel flown)
        {
            ITargetable target = FlightGlobals.fetch != null ? FlightGlobals.fetch.VesselTarget : null;
            Vessel meet = target != null ? target.GetVessel() : null;
            return IsMeetable(flown, meet) ? meet : null;
        }

        internal static bool IsMeetable(Vessel flown, Vessel meet)
        {
            return meet != null && meet != flown && flown != null && meet.orbit != null && meet.orbit.eccentricity < 1.0 && !meet.LandedOrSplashed
                   && meet.mainBody != null && meet.situation == Vessel.Situations.ORBITING;
        }

        // Whether the vessel has a docking port, loaded or not.
        internal static bool HasDockingPort(Vessel vessel)
        {
            if (vessel == null) return false;
            if (vessel.loaded) return vessel.FindPartModulesImplementing<ModuleDockingNode>().Count > 0;
            if (vessel.protoVessel == null) return false;
            foreach (ProtoPartSnapshot part in vessel.protoVessel.protoPartSnapshots)
                foreach (ProtoPartModuleSnapshot module in part.modules)
                    if (module.moduleName == "ModuleDockingNode") return true;
            return false;
        }

        // The route set to a meeting with the vessel: an orbit round its body at its altitude, met with it.
        internal static void Meet(MapPlannerWindow window, Vessel flown, Vessel meet, bool dock)
        {
            double altitude = System.Math.Max(1000.0, 0.5 * (meet.orbit.PeA + meet.orbit.ApA));
            window.PlanGoal(flown, meet.mainBody, false, altitude, MapPlannerWindow.Adding, false, 0.0, 0.0, meet, dock);
        }

        internal static void Close()
        {
            if (shown != null) shown.Dismiss();
            shown = null;
        }

        private static DialogGUIButton Entry(string label, Callback choose)
        {
            return new DialogGUIButton(label, choose, 220f, 26f, true);
        }

        // The bodies the route can go to from the one the vessel is round: its moons, the body it circles and the bodies that circle that one too.
        private static IEnumerable<CelestialBody> Reachable(CelestialBody here)
        {
            if (here.orbitingBodies != null)
                foreach (CelestialBody moon in here.orbitingBodies) yield return moon;
            CelestialBody parent = here.referenceBody;
            if (parent == null || parent == here) yield break;
            yield return parent;
            if (parent.orbitingBodies != null)
                foreach (CelestialBody sibling in parent.orbitingBodies)
                    if (sibling != here) yield return sibling;
        }
    }
}
