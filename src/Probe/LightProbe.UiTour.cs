#if REDEFINITION_PLANNER_PROBES
using System;
using System.Collections;
using System.IO;
using FinePrint;
using KSP.UI;
using KSP.UI.Screens;
using KSP.UI.Screens.Mapview;
using KSP.UI.Screens.Mapview.MapContextMenuOptions;
using ReDefinition.Planner.Autopilot;
using ReDefinition.Planner.Autopilot.Guidance;
using ReDefinition.Core;
using ReDefinition.Planner.Engineer;
using UnityEngine;
using UnityEngine.UI;

namespace ReDefinition.Probe
{
    // -redefinitionProbeUiTour: what the player does with the map's menu and the autopilot button, done with the mouse. On the ground the cursor goes over the
    // vessel's icon, the left button opens the menu, "Launch to orbit" is clicked, and then the autopilot button of the flight view. In an orbit it is
    // "Land at marker", a click on the planet to set the marker, and the button again. Each step says in the log what the game made of it.
    internal static partial class LightProbe
    {
        // Where the vessel's icon is on the screen: the icon's own place, as the game has it for the cursor.
        private static Vector2 IconOf(Vessel vessel)
        {
            object node = typeof(OrbitRendererBase).GetField("objectNode", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(vessel.orbitDriver.Renderer);
            var icon = node as Selectable;
            RectTransform rect = icon != null && icon.targetGraphic != null ? icon.targetGraphic.rectTransform : null;
            Debug.Log(Log.Tag + " Probe: tour: the icon: node " + (node != null ? node.GetType().Name : "none") + ", selectable " + (icon != null) + ", rect " + (rect != null ? rect.name + " at " + rect.position : "none")
                      + ", canvas " + (rect != null && rect.GetComponentInParent<Canvas>() != null ? rect.GetComponentInParent<Canvas>().renderMode.ToString() : "none"));
            foreach (MapNode each in MapNode.AllMapNodes)
            {
                var s = each as Selectable;
                if (s != null && s.targetGraphic != null && s.targetGraphic.rectTransform.gameObject.activeInHierarchy)
                    Debug.Log(Log.Tag + " Probe: tour: a node at " + RectTransformUtility.WorldToScreenPoint(UIMasterController.Instance.uiCamera, s.targetGraphic.rectTransform.position) + " " + each.name);
            }
            if (rect != null) return RectTransformUtility.WorldToScreenPoint(UIMasterController.Instance.uiCamera, rect.position);
            Vector3 point = PlanetariumCamera.Camera.WorldToScreenPoint(ScaledSpace.LocalToScaledSpace(vessel.GetWorldPos3D()));
            return new Vector2(point.x, point.y);
        }

        private static void Step(string text)
        {
            Debug.Log(Log.Tag + " Probe: tour: " + text);
        }

        // -redefinitionProbeButtonState: how the autopilot button stands in the flight view, as a shot and in the log.
        private static IEnumerator ButtonStateRun(string folder)
        {
            yield return new WaitForSecondsRealtime(8f);
            GameObject live = GameObject.Find("ReDefinitionLiveButton");
            Button button = live != null ? live.GetComponent<Button>() : null;
            Image face = live != null ? live.GetComponent<Image>() : null;
            var tip = live != null ? live.GetComponent<KSP.UI.TooltipTypes.TooltipController_Text>() : null;
            Step("the autopilot button: " + (live != null) + ", can be pressed " + (button != null && button.interactable) + ", lamp " + (face != null && face.sprite != null ? face.sprite.name : "none")
                 + ", opacity " + (face != null ? face.color.a.ToString("0.00") : "-") + ", tooltip \"" + (tip != null ? tip.textString : "none") + "\".");
            if (button != null)
            {
                button.onClick.Invoke();
                yield return new WaitForSecondsRealtime(1f);
                Step("pressed: flying " + RouteDriver.Instance.Flying + ", " + RouteDriver.Instance.Status);
            }
            ScreenCapture.CaptureScreenshot(Path.Combine(folder, "button-state.png"));
            yield return new WaitForSecondsRealtime(2f);
            Step("ended.");
        }

        // -redefinitionProbeNavigationClears: a node of the player stands, the navigation starts, and the node is gone.
        private static IEnumerator NavigationClearsRun()
        {
            Vessel vessel = FlightGlobals.ActiveVessel;
            yield return new WaitForSecondsRealtime(8f);
            PatchedConicSolver solver = vessel.patchedConicSolver;
            ManeuverNode old = solver.AddManeuverNode(Planetarium.GetUniversalTime() + 600.0);
            old.DeltaV = new Vector3d(0.0, 0.0, 50.0);
            solver.UpdateFlightPlan();
            yield return new WaitForSecondsRealtime(1f);
            Step("navigation clears: a node of the player stands: " + solver.maneuverNodes.Count);
            RouteDriver.Instance.StartDescent(vessel, 0.0, -74.0);
            yield return new WaitForSecondsRealtime(1f);
            bool gone = !solver.maneuverNodes.Contains(old);
            Step((gone ? "" : "FAILED: ") + "navigation clears: the navigation started, the old node " + (gone ? "is gone" : "is still there") + ", nodes: " + solver.maneuverNodes.Count + ".");
            yield return new WaitForSecondsRealtime(2f);
            Step("ended.");
        }

        private static IEnumerator UiTourRun(string folder)
        {
            Vessel vessel = FlightGlobals.ActiveVessel;
            bool ground = vessel.situation == Vessel.Situations.LANDED || vessel.situation == Vessel.Situations.PRELAUNCH;
            ProbeMouse.Focus();
            MapView.EnterMapView();
            if (UIMasterController.Instance != null) UIMasterController.Instance.ShowUI();
            yield return new WaitForSecondsRealtime(5f);
            // Near the vessel the icons of the buildings round it come apart from its own. -redefinitionProbeTourDefaultCamera leaves the camera as the game
            // puts it on opening the map, which is what the player has.
            bool defaultCamera = CommandLine.Has("-redefinitionProbeTourDefaultCamera");
            if (!defaultCamera)
            {
                if (vessel.mapObject != null) PlanetariumCamera.fetch.SetTarget(vessel.mapObject);
                PlanetariumCamera.fetch.SetDistance(ground ? 40f : 60f);
            }
            if (!ground)
            {
                // The icon of a vessel in a circular orbit sits on the periapsis' own at first; a few minutes on it has left it.
                TimeWarp.fetch.WarpTo(Planetarium.GetUniversalTime() + 240.0);
                yield return new WaitForSecondsRealtime(30f);
                if (TimeWarp.CurrentRate > 1f) TimeWarp.SetRate(0, true);
            }
            yield return new WaitForSecondsRealtime(3f);
            Vector2 icon = IconOf(vessel);
            // -redefinitionProbeTourSite: the click is on the icon of the launch site that covers the vessel's, as the player's is.
            bool site = CommandLine.Has("-redefinitionProbeTourSite");
            if (site)
            {
                Waypoint ksc = WaypointManager.Instance().Waypoints.Find(w => w.id == "ksc");
                Selectable node = ksc != null ? ksc.node : null;
                if (node != null && node.targetGraphic != null)
                    icon = RectTransformUtility.WorldToScreenPoint(UIMasterController.Instance.uiCamera, node.targetGraphic.rectTransform.position);
                // The two icons lie on one place; the game gives the click to the one that is on top, which in the player's map is the site's.
                if (vessel.mapObject != null && vessel.mapObject.uiNode != null)
                    foreach (Graphic each in vessel.mapObject.uiNode.GetComponentsInChildren<Graphic>(true)) each.raycastTarget = false;
                Step("the space center's icon is at " + icon.x.ToString("0") + ", " + icon.y.ToString("0") + ", covers the vessel: " + MapLaunchMenu.Covers(ksc, vessel) + ".");
            }
            Step("the vessel's icon is at " + icon.x.ToString("0") + ", " + icon.y.ToString("0") + " of " + Screen.width + " x " + Screen.height + ", " + (ground ? "on the ground" : "in an orbit") + ".");
            ProbeMouse.Move(icon);
            yield return new WaitForSecondsRealtime(1.5f);
            if (UnityEngine.EventSystems.EventSystem.current != null)
            {
                var pointer = new UnityEngine.EventSystems.PointerEventData(UnityEngine.EventSystems.EventSystem.current) { position = Input.mousePosition };
                var hits = new System.Collections.Generic.List<UnityEngine.EventSystems.RaycastResult>();
                UnityEngine.EventSystems.EventSystem.current.RaycastAll(pointer, hits);
                foreach (var each in hits) Step("under the cursor: " + each.gameObject.name + " (" + (each.gameObject.transform.parent != null ? each.gameObject.transform.parent.name : "-") + "), depth " + each.depth);
            }
            Step("before the click: the cursor is at " + Input.mousePosition + ", the icon is hovered " + vessel.orbitDriver.Renderer.mouseOver + ", the targeter is " + (vessel.orbitTargeter != null)
                 + ", the map is " + MapView.MapIsEnabled + ", the situation " + vessel.situation + ", the route driver " + (RouteDriver.Instance != null) + ".");
            if (site)
            {
                Waypoint ksc = WaypointManager.Instance().Waypoints.Find(w => w.id == "ksc");
                var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
                Step("the space center before the click: hover " + (ksc.node != null && ksc.node.Hover) + ", within zoom " + typeof(Waypoint).GetField("withinZoom", flags).GetValue(ksc)
                     + ", occluded " + ksc.isOccluded + ", navigatable " + ksc.isNavigatable + ", visible " + ksc.visible + ", node visible " + (ksc.node != null && ksc.node.gameObject.activeInHierarchy)
                     + ", menu " + (typeof(Waypoint).GetField("menu", flags).GetValue(ksc) != null) + ".");
            }
            // The left button is the game's way to the menu of an icon. The site's menu is made as the game makes it where the game's own click is
            // refused for the view the probe has (the site is taken as hidden by the planet).
            MapContextMenu siteMenu = null;
            if (site)
            {
                Waypoint ksc = WaypointManager.Instance().Waypoints.Find(w => w.id == "ksc");
                siteMenu = MapContextMenu.Create(ksc.FullName, new Rect(0.5f, 0.5f, 300f, 64f), new WaypointCastHit(ksc), new Callback(() => { }), new MapContextMenuOption[0]);
            }
            else yield return ProbeMouse.Click();
            if (site)
            {
                Waypoint ksc = WaypointManager.Instance().Waypoints.Find(w => w.id == "ksc");
                var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
                Step("the space center after the click: hover " + (ksc.node != null && ksc.node.Hover) + ", menu " + (typeof(Waypoint).GetField("menu", flags).GetValue(ksc) != null) + ".");
            }
            yield return new WaitForSecondsRealtime(1.5f);
            ScreenCapture.CaptureScreenshot(Path.Combine(folder, "tour-menu.png"));
            string entry = ground ? (site ? "to Orbit" : "Launch to orbit") : "Land at marker";
            Button option = ProbeMouse.ButtonWith(entry);
            Step("the menu entry \"" + entry + "\" after the click: " + (option != null));
            if (option == null)
            {
                Step("FAILED: the click on the icon did not open the menu.");
                yield break;
            }
            ProbeMouse.Move(ProbeMouse.ScreenOf((RectTransform)option.transform));
            yield return new WaitForSecondsRealtime(0.6f);
            yield return ProbeMouse.Click();
            yield return new WaitForSecondsRealtime(1.5f);
            RouteState state = RouteState.Of(vessel);
            if (ground)
            {
                Step("after the entry: navigation " + (state != null && state.Navigation) + ", " + RouteDriver.Instance.Status);
            }
            else
            {
                // The click on the planet: the centre of its disc is on its surface.
                Vector3 below = vessel.mainBody.position + (vessel.GetWorldPos3D() - vessel.mainBody.position).normalized * vessel.mainBody.Radius;
                Vector3 planet = PlanetariumCamera.Camera.WorldToScreenPoint(ScaledSpace.LocalToScaledSpace(below));
                Step("the surface under the vessel is at " + planet.x.ToString("0") + ", " + planet.y.ToString("0") + "; the marker is to be placed there.");
                yield return new WaitForSecondsRealtime(0.8f);
                ProbeMouse.Move(new Vector2(planet.x, planet.y));
                yield return new WaitForSecondsRealtime(0.6f);
                yield return ProbeMouse.Click();
                yield return new WaitForSecondsRealtime(3f);
                int markers = WaypointManager.Instance() != null ? WaypointManager.Instance().Waypoints.FindAll(w => w.isCustom && w.name == "Landing marker").Count : -1;
                Waypoint first = WaypointManager.Instance() != null ? WaypointManager.Instance().Waypoints.Find(w => w.isCustom && w.name == "Landing marker") : null;
                Step("after the click on the planet: " + markers + " landing marker(s)" + (first != null ? " at " + first.latitude.ToString("0.00") + ", " + first.longitude.ToString("0.00") : "")
                     + ", navigation " + (state != null && state.Navigation) + ", " + RouteDriver.Instance.Status);
                yield return new WaitForSecondsRealtime(3f);
                ScreenCapture.CaptureScreenshot(Path.Combine(folder, "tour-marker.png"));
                yield return new WaitForSecondsRealtime(1f);
            }
            MapView.ExitMapView();
            yield return new WaitForSecondsRealtime(4f);
            ScreenCapture.CaptureScreenshot(Path.Combine(folder, "tour-flight.png"));
            yield return new WaitForSecondsRealtime(1f);
            Step("in the flight view: guide node " + GuideNode.Holds(vessel) + ", nodes " + (vessel.patchedConicSolver != null ? vessel.patchedConicSolver.maneuverNodes.Count : -1)
                 + ", guidance " + GuidanceBoard.Current(vessel));
            // The autopilot button.
            GameObject live = GameObject.Find("ReDefinitionLiveButton");
            if (live == null)
            {
                Step("FAILED: the autopilot button is not there.");
                yield break;
            }
            ProbeMouse.Move(ProbeMouse.ScreenOf((RectTransform)live.transform));
            yield return new WaitForSecondsRealtime(0.8f);
            yield return ProbeMouse.Click();
            yield return new WaitForSecondsRealtime(3f);
            Step("after the autopilot button: flying " + RouteDriver.Instance.Flying + ", " + RouteDriver.Instance.Status);
            ScreenCapture.CaptureScreenshot(Path.Combine(folder, "tour-autopilot.png"));
            float started = Time.realtimeSinceStartup;
            float next = 0f;
            bool staged = false;
            while (RouteDriver.Instance.Flying && Time.realtimeSinceStartup - started < 1500f && !ProbeWatch.Gone(vessel))
            {
                // The first stage is the player's to light: MechJeb's ascent waits for it.
                if (!staged && RouteDriver.Instance.Status.StartsWith("Ascent: waiting"))
                {
                    staged = true;
                    StageManager.ActivateNextStage();
                    Step("the first stage is lit.");
                }
                if (Time.realtimeSinceStartup > next)
                {
                    next = Time.realtimeSinceStartup + 20f;
                    Step("flying: " + RouteDriver.Instance.Status + " [" + vessel.situation + ", " + vessel.altitude.ToString("0") + " m, " + vessel.srfSpeed.ToString("0") + " m/s, orbit " + vessel.orbit.PeA.ToString("0")
                         + " x " + vessel.orbit.ApA.ToString("0") + "]");
                }
                yield return new WaitForSecondsRealtime(1f);
            }
            Step("ended: flying " + RouteDriver.Instance.Flying + ", " + RouteDriver.Instance.Status + " [" + vessel.situation + ", orbit " + vessel.orbit.PeA.ToString("0") + " x " + vessel.orbit.ApA.ToString("0")
                 + ", at " + vessel.latitude.ToString("0.000") + ", " + vessel.longitude.ToString("0.000") + "].");
        }
    }
}
#endif
