using System.Collections;
using System.IO;
using KSP.UI;
using KSP.UI.Screens;
using KSP.UI.Screens.Mapview;
using ReDefinition.Autopilot.Flight;
using ReDefinition.Autopilot.Planning;
using ReDefinition.Core;
using ReDefinition.Engineer;
using UnityEngine;

namespace ReDefinition.Probe
{
    // -redefinitionProbeGoals: the planner in the flight view, and the goals the menus set: a body's orbit and a shaped orbit.
    // Shots of the Live menu for an empty plan and of the map's menus. The shaped orbit is flown with the Live button.
    internal static partial class LightProbe
    {
        private static IEnumerator GoalsRun(string folder)
        {
            // The flight has just started: the vessel settles and the window is made before the probe looks for them.
            for (float waited = 0f; waited < 40f && (MapPlannerWindow.Instance == null || FlightGlobals.ActiveVessel == null || FlightGlobals.ActiveVessel.situation != Vessel.Situations.ORBITING); waited += 1f)
                yield return new WaitForSeconds(1f);
            yield return ProbeSteps.Frames(60);
            Vessel vessel = FlightGlobals.ActiveVessel;
            MapPlannerWindow window = MapPlannerWindow.Instance;
            if (vessel == null || window == null || vessel.situation != Vessel.Situations.ORBITING)
            {
                ProbeSteps.Failed("goals: the planner window is " + (window != null ? "there" : "missing") + "; the vessel is " + (vessel != null ? vessel.situation + " at " + vessel.altitude.ToString("0") + " m" : "missing") + ".");
                yield break;
            }
            // The probe's flight has its interface hidden; the shots are of the interface.
            if (UIMasterController.Instance != null) UIMasterController.Instance.ShowUI();
            // The planner in the flight view: the map is left and the panel is opened.
            MapView.ExitMapView();
            yield return ProbeSteps.Frames(60);
            window.OpenNow();
            yield return new WaitForSeconds(3f);
            Debug.Log(Log.Tag + " Probe: goals: the planner is open with the map " + (MapView.MapIsEnabled ? "on" : "off") + ".");
            ScreenCapture.CaptureScreenshot(Path.Combine(folder, "goals-planner-flight.png"));
            yield return ProbeSteps.Frames(10);

            // A goal from a menu: the orbit of the Mun replaces the route.
            CelestialBody mun = FlightGlobals.Bodies.Find(b => b.bodyName == "Mun");
            window.PlanGoal(vessel, mun, false, NextStepPlanner.LowOrbit(mun), false);
            yield return new WaitForSeconds(10f);
            ReportPlan(window, "Mun orbit");
            ScreenCapture.CaptureScreenshot(Path.Combine(folder, "goals-mun.png"));
            yield return ProbeSteps.Frames(10);

            // Navigate to Surface: no site, so no plane turn and no marker, only the burn that brings the orbit down.
            window.PlanGoal(vessel, mun, true, 0.0, false);
            yield return new WaitForSeconds(10f);
            ReportPlan(window, "Mun surface");
            yield return ProbeSteps.Frames(10);

            // A shaped orbit: an apoapsis and an inclination, planned by the autopilot's own planner.
            double apoapsis = vessel.orbit.ApA * 1.5;
            double inclination = vessel.orbit.inclination + 10.0;
            window.PlanGoal(vessel, vessel.mainBody, false, vessel.orbit.PeA, false, false, 0.0, 0.0, null, false,
                new MapPlannerWindow.Shape { Apoapsis = apoapsis, HasInclination = true, Inclination = inclination });
            yield return new WaitForSeconds(10f);
            ReportPlan(window, "shaped orbit " + (apoapsis / 1000.0).ToString("0") + " km, " + inclination.ToString("0.#") + " deg");
            ScreenCapture.CaptureScreenshot(Path.Combine(folder, "goals-shaped.png"));
            yield return ProbeSteps.Frames(10);

            // The Live menu for an empty plan, and the map's menu of the vessel.
            window.ClearNow();
            yield return new WaitForSeconds(2f);
            LiveMenu.Open(vessel);
            yield return new WaitForSeconds(2f);
            ScreenCapture.CaptureScreenshot(Path.Combine(folder, "goals-live-menu.png"));
            yield return ProbeSteps.Frames(10);
            LiveMenu.Close();
            MapView.EnterMapView();
            yield return new WaitForSeconds(3f);
            if (MapLaunchMenu.Instance != null)
            {
                MapLaunchMenu.Instance.Show(vessel);
                yield return new WaitForSeconds(2f);
                ScreenCapture.CaptureScreenshot(Path.Combine(folder, "goals-vessel-menu.png"));
                yield return ProbeSteps.Frames(10);
                MapLaunchMenu.Instance.Close();
            }
            yield return BodyMenuShot(folder, "Mun", "goals-body-menu.png");
            MapView.ExitMapView();
            yield return ProbeSteps.Frames(30);

            // The shaped orbit flown with the Live button.
            window.PlanGoal(vessel, vessel.mainBody, false, vessel.orbit.PeA, false, false, 0.0, 0.0, null, false,
                new MapPlannerWindow.Shape { Apoapsis = apoapsis, HasInclination = true, Inclination = inclination });
            yield return new WaitForSeconds(10f);
            RouteDriver.Instance.ToggleAutopilot(vessel);
            float started = Time.realtimeSinceStartup;
            yield return new WaitForSeconds(5f);
            while (RouteDriver.Instance.Flying && !ProbeWatch.Lost && Time.realtimeSinceStartup - started < 1500f) yield return new WaitForSeconds(5f);
            Orbit now = FlightGlobals.ActiveVessel.orbit;
            Debug.Log(Log.Tag + " Probe: goals: the flight ended (" + RouteDriver.Instance.Status + "): " + (now.PeA / 1000.0).ToString("0.0") + " x " + (now.ApA / 1000.0).ToString("0.0")
                      + " km, inclination " + now.inclination.ToString("0.0") + " degrees; asked " + (apoapsis / 1000.0).ToString("0.0") + " km and " + inclination.ToString("0.0") + " degrees.");
            yield return new WaitForSeconds(3f);
            Debug.Log(Log.Tag + " Probe: goals: the route afterwards " + (window.RouteIsEmpty ? "is empty" : "is not empty") + ".");
            // The orbit is no longer round and no longer in the equator: the icons of its apsides and nodes have something to offer.
            MapView.EnterMapView();
            yield return new WaitForSeconds(4f);
            if (FlightGlobals.ActiveVessel.mapObject != null) PlanetariumCamera.fetch.SetTarget(FlightGlobals.ActiveVessel.mapObject);
            yield return new WaitForSeconds(3f);
            yield return MarkerMenuShot(folder, FlightGlobals.ActiveVessel);
            Debug.Log(Log.Tag + " Probe: goals done.");
        }

        // What the planner made of the route: its steps, their delta-v, and how it ended.
        private static void ReportPlan(MapPlannerWindow window, string what)
        {
            RoutePlan plan = window.WholePlan;
            Debug.Log(Log.Tag + " Probe: goals: " + what + ": " + (plan == null ? "no plan (" + window.Status + ")" : plan.Steps.Count + " step(s), " + plan.DeltaV.ToString("0") + " m/s, "
                      + (plan.Failed ? "FAILED: " : "") + (plan.Ended ?? "no end")));
            if (plan != null) foreach (PlannedStep step in plan.Steps) Debug.Log(Log.Tag + " Probe: goals:   step " + step.Kind + " goal " + step.Goal + ": " + step.Why);
        }

        // A click on the apoapsis icon and on the ascending node icon, as the map's menu makes it for them, as shots.
        private static IEnumerator MarkerMenuShot(string folder, Vessel vessel)
        {
            MapMarkerMenu menu = Object.FindObjectOfType<MapMarkerMenu>();
            System.Reflection.MethodInfo clicked = typeof(MapMarkerMenu).GetMethod("Clicked", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (menu == null || clicked == null)
            {
                Debug.Log(Log.Tag + " Probe: goals: the marker menu was not found.");
                yield break;
            }
            string[] names = { "Apoapsis", "AscendingNode" };
            for (int i = 0; i < names.Length; i++)
            {
                object marker = System.Enum.Parse(clicked.GetParameters()[0].ParameterType, names[i]);
                clicked.Invoke(menu, new object[] { marker, Mouse.Buttons.Left });
                yield return new WaitForSeconds(2f);
                ScreenCapture.CaptureScreenshot(Path.Combine(folder, "goals-marker-" + names[i] + ".png"));
                yield return new WaitForSeconds(1f);
                object open = typeof(MapMarkerMenu).GetField("open", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(menu);
                Debug.Log(Log.Tag + " Probe: goals: the " + names[i] + " menu is " + (open != null ? "open" : "not open") + ".");
                if (open != null) ((MapContextMenu)open).Dismiss();
            }
        }

        // The game's own menu of a body in the map, as it makes it for a click on the icon, as a shot.
        private static IEnumerator BodyMenuShot(string folder, string bodyName, string file)
        {
            CelestialBody target = FlightGlobals.Bodies.Find(b => b.bodyName == bodyName);
            if (target != null && target.MapObject != null) PlanetariumCamera.fetch.SetTarget(target.MapObject);
            yield return new WaitForSeconds(3f);
            OrbitTargeter targeter = FlightGlobals.ActiveVessel.orbitTargeter;
            System.Reflection.FieldInfo castField = typeof(OrbitTargeter).GetField("orbitCastHit", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            System.Reflection.MethodInfo setMode = typeof(OrbitTargeter).GetMethod("SetMenuMode", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (targeter == null || target == null || castField == null || setMode == null)
            {
                Debug.Log(Log.Tag + " Probe: goals: the body menu could not be made.");
                yield break;
            }
            object cast = castField.GetValue(targeter);
            cast.GetType().GetField("driver").SetValue(cast, target.orbitDriver);
            cast.GetType().GetField("or").SetValue(cast, target.orbitDriver.Renderer);
            cast.GetType().GetField("mouseTA").SetValue(cast, target.orbit.trueAnomaly);
            castField.SetValue(targeter, cast);
            setMode.Invoke(targeter, new object[] { System.Enum.Parse(setMode.GetParameters()[0].ParameterType, "TARGET"), target.orbitDriver });
            yield return new WaitForSeconds(2f);
            ScreenCapture.CaptureScreenshot(Path.Combine(folder, file));
            yield return new WaitForSeconds(1f);
        }
    }
}
