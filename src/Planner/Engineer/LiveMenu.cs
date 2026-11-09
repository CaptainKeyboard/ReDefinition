using ReDefinition.Planner;
using System.Collections.Generic;
using ReDefinition.Planner.Autopilot;
using ReDefinition.Planner.Autopilot.Planning;
using ReDefinition.Ui;
using UnityEngine;
using UnityEngine.UI;

namespace ReDefinition.Planner.Engineer
{
    // What the Live button offers while the planner's route is empty: the goals the vessel can be sent to from where it is. A choice only sets the
    // planner's route and opens the planner; the next press of the button flies it. Shift adds the goal to the route instead of replacing it.
    internal static class LiveMenu
    {
        private static PopupDialog shown;

        // Whether the menu is on the screen: a press of the button that finds it open puts it away.
        internal static bool IsOpen
        {
            get { return shown != null && shown.gameObject != null; }
        }

        // `withRoute`: the menu is asked for while there is a route, so that it offers to cancel it.
        // Only what makes sense where the vessel is stands in the first list: from the ground the launch, from an orbit the body's moons. The other bodies stand under "Navigate to ...".
        internal static void Open(Vessel vessel, bool withRoute = false)
        {
            Close();
            MapPlannerWindow window = MapPlannerWindow.Instance;
            if (vessel == null || vessel.mainBody == null || window == null) return;
            CelestialBody here = vessel.mainBody;
            var entries = new List<DialogGUIBase>();
            bool onGround = vessel.situation == Vessel.Situations.LANDED || vessel.situation == Vessel.Situations.PRELAUNCH || vessel.situation == Vessel.Situations.SPLASHED;
            bool inOrbit = vessel.situation == Vessel.Situations.ORBITING;
            if (withRoute) entries.Add(Entry("Cancel Route", () => CancelRoute(vessel)));
            if (onGround)
                entries.Add(Entry("Launch to Orbit", () => window.PlanGoal(vessel, here, false, NextStepPlanner.LowOrbit(here), MapPlannerWindow.Adding)));
            if (inOrbit && here.hasSolidSurface && here.pqsController != null)
                entries.Add(Entry("Land on " + here.bodyName, () => window.PlanGoal(vessel, here, true, 0.0, MapPlannerWindow.Adding)));
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
                entries.Add(Entry("Return to " + home.bodyName, () => window.PlanGoal(vessel, home, false, NextStepPlanner.LowOrbit(home), MapPlannerWindow.Adding)));
            // From an orbit the moons of the body are one choice away; every other body is under "Navigate to ...".
            var others = new List<CelestialBody>();
            foreach (CelestialBody body in onGround || inOrbit ? Reachable(here) : new CelestialBody[0])
            {
                CelestialBody goal = body;
                if (goal == home && home != here) continue;
                if (inOrbit && goal.referenceBody == here)
                    entries.Add(Entry(goal.bodyName + " Orbit", () => window.PlanGoal(vessel, goal, false, NextStepPlanner.LowOrbit(goal), MapPlannerWindow.Adding)));
                else others.Add(goal);
            }
            if (others.Count > 0) entries.Add(Entry("Navigate to ...", () => OpenNavigate(vessel, withRoute, others), false));
            Vessel meet = Meetable(vessel);
            if (meet != null && inOrbit)
            {
                entries.Add(Entry("Rendezvous with " + meet.vesselName, () => Meet(window, vessel, meet, false)));
                if (HasDockingPort(meet)) entries.Add(Entry("Dock with " + meet.vesselName, () => Meet(window, vessel, meet, true)));
            }
            entries.Add(Entry("Open Planner", window.OpenNow));
            if (PlannerSeams.StandsAlone()) entries.Add(Entry("Planner Settings", PlannerSettingsDialog.Open));
            entries.Add(Entry("Close", () => { }));
            Show("Autopilot / MechJeb", entries);
        }

        // The second list: the bodies that are not one choice away, each as the orbit round it. "Back" brings the first list again.
        private static void OpenNavigate(Vessel vessel, bool withRoute, List<CelestialBody> bodies)
        {
            Close();
            MapPlannerWindow window = MapPlannerWindow.Instance;
            if (vessel == null || window == null) return;
            var entries = new List<DialogGUIBase>();
            foreach (CelestialBody body in bodies)
            {
                CelestialBody goal = body;
                entries.Add(Entry(goal.bodyName + " Orbit", () => window.PlanGoal(vessel, goal, false, NextStepPlanner.LowOrbit(goal), MapPlannerWindow.Adding)));
            }
            entries.Add(Entry("Back", () => Open(vessel, withRoute), false));
            entries.Add(Entry("Close", () => { }));
            Show("Navigate to", entries);
        }

        // The list on the screen under the button, put on the screen like the other dialogs.
        private static void Show(string title, List<DialogGUIBase> entries)
        {
            var dialog = new MultiOptionDialog("ReDefinitionLiveMenu", "", title, HighLogic.UISkin, 190f,
                new DialogGUIVerticalLayout(170f, -1f, 2f, new RectOffset(4, 4, 4, 4), TextAnchor.UpperLeft, entries.ToArray()));
            var anchor = new Vector2(0.2f, 0.72f);
            shown = PopupDialog.SpawnPopupDialog(anchor, anchor, dialog, false, HighLogic.UISkin, false);
            PlaceUnderButton();
            PlannerSeams.ShieldDialog(ScreenFit.Add(shown));
            UiHygiene.MouseOnly(shown.gameObject);
        }

        // The flight stopped and the route emptied: what the menu's "Cancel Route" does.
        internal static void CancelRoute(Vessel vessel)
        {
            RouteDriver driver = RouteDriver.Instance;
            if (driver != null && vessel != null)
            {
                driver.StopFlight(vessel);
                RouteState state = RouteState.Of(vessel);
                if (state != null && state.Navigation) driver.SetNavigation(vessel, false);
            }
            if (MapPlannerWindow.Instance != null) MapPlannerWindow.Instance.ClearNow();
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

        // The menu hangs from the lower left corner of the Live button, which is where the player pressed.
        private static void PlaceUnderButton()
        {
            GameObject button = GameObject.Find("ReDefinitionLiveButton");
            RectTransform rect = shown != null ? shown.RTrf : null;
            if (button == null || rect == null) return;
            var corners = new Vector3[4];
            ((RectTransform)button.transform).GetWorldCorners(corners);
            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate(rect);
            Vector3 size = new Vector3(rect.rect.width * rect.lossyScale.x, rect.rect.height * rect.lossyScale.y, 0f);
            // Under the button's tooltip, which stands there while the pointer is on the button: two lines of it.
            float tooltip = 50f * rect.lossyScale.y;
            rect.position = new Vector3(corners[0].x + size.x * rect.pivot.x, corners[0].y - tooltip - size.y * (1f - rect.pivot.y), rect.position.z);
        }

        internal static void Close()
        {
            if (shown != null) shown.Dismiss();
            shown = null;
        }

        // `dismiss` false where the choice puts another list up itself.
        private static DialogGUIButton Entry(string label, Callback choose, bool dismiss = true)
        {
            return new DialogGUIButton(label, choose, 170f, 26f, dismiss);
        }

        // The bodies the route can go to from the one the vessel is round: its moons, the body it circles and the bodies that circle that one too.
        private static IEnumerable<CelestialBody> Reachable(CelestialBody here)
        {
            if (here.orbitingBodies != null)
                foreach (CelestialBody moon in here.orbitingBodies) yield return moon;
            CelestialBody parent = here.referenceBody;
            if (parent == null || parent == here) yield break;
            // The sun has no orbit to go to.
            if (parent.referenceBody != parent) yield return parent;
            if (parent.orbitingBodies != null)
                foreach (CelestialBody sibling in parent.orbitingBodies)
                    if (sibling != here) yield return sibling;
        }
    }
}
