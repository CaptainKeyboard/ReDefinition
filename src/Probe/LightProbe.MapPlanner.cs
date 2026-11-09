using System.Collections;
using System.IO;
using KSP.UI;
using KSP.UI.Screens;
using ReDefinition.Planner.Autopilot;
using ReDefinition.Planner.Autopilot.Background;
using ReDefinition.Planner.Autopilot.Flight;
using ReDefinition.Planner.Autopilot.Guidance;
using ReDefinition.Planner.Autopilot.Planning;
using ReDefinition.Core;
using ReDefinition.Planner.Engineer;
using ReDefinition.Planner.Orbits;
using UnityEngine;

namespace ReDefinition.Probe
{
    // -redefinitionProbeMapPlanner: the planner's panel in the map view, open, as a shot of the screen.
    internal static partial class LightProbe
    {
        internal const string MapPlannerArgument = "-redefinitionProbeMapPlanner";

        // Whether the vessel was left and loaded again, so that the next run of the probe only watches what the autopilot does with it.
        private static bool ResumeDone;

        // What the map holds of orbit lines and markers: the planner draws its own, and what the game makes for the vessel's patches is its own.
        private static string MapMarkers()
        {
            int maneuvers = 0;
            foreach (MapObject mapObject in Object.FindObjectsOfType<MapObject>())
                if (mapObject.type == MapObject.ObjectType.ManeuverNode) maneuvers++;
            return "maneuver icons " + maneuvers + ", nodes " + Object.FindObjectsOfType<KSP.UI.Screens.Mapview.MapNode>().Length + ", orbit renderers " + Object.FindObjectsOfType<OrbitRendererBase>().Length
                   + ", patched conic renderers " + Object.FindObjectsOfType<PatchedConicRenderer>().Length + ", maneuver gizmos " + Object.FindObjectsOfType<ManeuverGizmo>().Length;
        }

        // -redefinitionProbeNodesFly: a node is made on the orbit, as the player makes one in the map, and the autopilot button is pressed: MechJeb is to fly it.
        private static IEnumerator NodesFlyRun(string folder)
        {
            Vessel vessel = FlightGlobals.ActiveVessel;
            MapView.ExitMapView();
            if (UIMasterController.Instance != null) UIMasterController.Instance.ShowUI();
            yield return new WaitForSeconds(6f);
            ManeuverNode node = vessel.patchedConicSolver.AddManeuverNode(Planetarium.GetUniversalTime() + 240.0);
            node.DeltaV = new Vector3d(0.0, 0.0, 60.0);
            vessel.patchedConicSolver.UpdateFlightPlan();
            yield return new WaitForSeconds(2f);
            UnityEngine.ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(folder, "live-off.png"));
            yield return new WaitForSeconds(1f);
            RouteDriver.Instance.ToggleAutopilot(vessel);
            yield return new WaitForSeconds(3f);
            UnityEngine.ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(folder, "live-on.png"));
            Debug.Log(Log.Tag + " Probe: nodes fly: pressed: flying " + RouteDriver.Instance.Flying + ", " + RouteDriver.Instance.Status + ".");
            float started = Time.realtimeSinceStartup;
            float nextLine = 0f;
            while (RouteDriver.Instance.Flying && Time.realtimeSinceStartup - started < 900f)
            {
                if (Time.realtimeSinceStartup > nextLine)
                {
                    nextLine = Time.realtimeSinceStartup + 10f;
                    Debug.Log(Log.Tag + " Probe: nodes fly: " + RouteDriver.Instance.Status + " nodes " + vessel.patchedConicSolver.maneuverNodes.Count);
                }
                yield return new WaitForSeconds(1f);
            }
            Debug.Log(Log.Tag + " Probe: nodes fly: ended: " + RouteDriver.Instance.Status + " nodes " + vessel.patchedConicSolver.maneuverNodes.Count + ", orbit " + vessel.orbit.PeA.ToString("0")
                      + " x " + vessel.orbit.ApA.ToString("0") + ".");
        }

        // What the autopilot does with the vessel that was loaded again while it flew: the flight goes on, to the end.
        private static IEnumerator ResumedFlight()
        {
            float started = Time.realtimeSinceStartup;
            string last = "";
            ProbeWatch.Arm(FlightGlobals.ActiveVessel);
            while (Time.realtimeSinceStartup - started < 1200f && !ProbeWatch.Gone(FlightGlobals.ActiveVessel))
            {
                string now = RouteDriver.Instance != null ? RouteDriver.Instance.Status : "";
                if (now != last)
                {
                    Debug.Log(Log.Tag + " Probe: resumed: " + now + " (flying " + (RouteDriver.Instance != null && RouteDriver.Instance.Flying) + ")");
                    last = now;
                }
                if (RouteDriver.Instance != null && !RouteDriver.Instance.Flying && Time.realtimeSinceStartup - started > 30f) break;
                yield return new WaitForSeconds(1f);
            }
            Debug.Log(Log.Tag + " Probe: resumed: ended in " + FlightGlobals.ActiveVessel.mainBody.bodyName + " at " + FlightGlobals.ActiveVessel.orbit.PeA.ToString("0") + " x "
                      + FlightGlobals.ActiveVessel.orbit.ApA.ToString("0") + " m.");
        }

        // Whether the vessel is marked as flown by the autopilot: after a section is let go the flight goes on in the carrier, which takes a moment to start.
        private static bool FlownByAutopilot(Vessel vessel)
        {
            RouteState state = RouteState.Of(vessel);
            return state != null && state.Autopilot;
        }

        private static IEnumerator MapPlannerShots(string folder)
        {
            // The flag -redefinitionProbeMapPlannerClosed leaves the panel shut and shoots the map as the player first sees it.
            bool closed = CommandLine.Has("-redefinitionProbeMapPlannerClosed");
            MapPlannerWindow.OpenAtOnce = !closed;
            yield return ProbeSteps.Frames(30);
            MapView.EnterMapView();
            if (!closed && MapPlannerWindow.Instance != null) MapPlannerWindow.Instance.OpenNow();
            if (UIMasterController.Instance != null) UIMasterController.Instance.ShowUI();
            yield return ProbeSteps.Frames(180);
            ScreenCapture.CaptureScreenshot(Path.Combine(folder, closed ? "map-planner-closed.png" : "map-planner.png"));
            yield return ProbeSteps.Frames(5);
            // The flag -redefinitionProbeMapPlannerHold keeps the panel open this many seconds first, for the timing of the planner.
            // The flag -redefinitionProbeMapPlannerMoreStops adds that many stops to the route, which makes the panel again for the longer route.
            string moreStops = CommandLine.After("-redefinitionProbeMapPlannerMoreStops");
            if (moreStops != null && !closed && MapPlannerWindow.Instance != null)
            {
                yield return new WaitForSeconds(3f);
                MapPlannerWindow.Instance.AddStops(int.Parse(moreStops));
                yield return new WaitForSeconds(8f);
                ScreenCapture.CaptureScreenshot(Path.Combine(folder, "map-planner-more.png"));
                yield return ProbeSteps.Frames(5);
            }
            string firstHold = CommandLine.After("-redefinitionProbeMapPlannerHold");
            if (firstHold != null && !closed) yield return new WaitForSeconds(float.Parse(firstHold, System.Globalization.CultureInfo.InvariantCulture));
            if (closed)
            {
                yield return new WaitForSeconds(12f);
                Debug.Log(Log.Tag + " Probe: map planner: shot with the panel shut.");
                yield break;
            }
            if (CommandLine.Has("-redefinitionProbeNavigationClears"))
            {
                yield return NavigationClearsRun();
                yield break;
            }
            if (CommandLine.Has("-redefinitionProbeButtonState"))
            {
                yield return ButtonStateRun(folder);
                yield break;
            }
            if (CommandLine.Has("-redefinitionProbeUiTour"))
            {
                yield return UiTourRun(folder);
                yield break;
            }
            if (CommandLine.Has("-redefinitionProbeGoals"))
            {
                yield return GoalsRun(folder);
                yield break;
            }
            if (CommandLine.Has("-redefinitionProbeNodesFly"))
            {
                yield return NodesFlyRun(folder);
                yield break;
            }
            if (MapPlannerWindow.Instance != null && CommandLine.Has("-redefinitionProbeMapPlannerClimb"))
            {
                string wantedBody = CommandLine.After("-redefinitionProbeMapPlannerTo") ?? "Kerbin";
                Debug.Log(Log.Tag + " Probe: map planner: destination " + wantedBody + " set: " + MapPlannerWindow.Instance.SetDestination(wantedBody, 100000.0));
                // -redefinitionProbeBodyMenu <body>: the game's own menu of a body in the map, made as the game makes it for a click on its icon, as a shot.
                string menuBody = CommandLine.After("-redefinitionProbeBodyMenu");
                if (menuBody != null)
                {
                    yield return new WaitForSeconds(3f);
                    CelestialBody menuTarget = FlightGlobals.Bodies.Find(b => b.bodyName == menuBody);
                    if (menuTarget != null && menuTarget.MapObject != null) PlanetariumCamera.fetch.SetTarget(menuTarget.MapObject);
                    yield return new WaitForSeconds(3f);
                    OrbitTargeter targeter = FlightGlobals.ActiveVessel.orbitTargeter;
                    System.Reflection.FieldInfo castField = typeof(OrbitTargeter).GetField("orbitCastHit", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                    System.Reflection.MethodInfo setMode = typeof(OrbitTargeter).GetMethod("SetMenuMode", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                    if (targeter != null && menuTarget != null && castField != null && setMode != null)
                    {
                        object cast = castField.GetValue(targeter);
                        cast.GetType().GetField("driver").SetValue(cast, menuTarget.orbitDriver);
                        cast.GetType().GetField("or").SetValue(cast, menuTarget.orbitDriver.Renderer);
                        cast.GetType().GetField("mouseTA").SetValue(cast, menuTarget.orbit.trueAnomaly);
                        castField.SetValue(targeter, cast);
                        setMode.Invoke(targeter, new object[] { System.Enum.Parse(setMode.GetParameters()[0].ParameterType, "TARGET"), menuTarget.orbitDriver });
                        yield return new WaitForSeconds(2f);
                        UnityEngine.ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(folder, "body-menu.png"));
                        yield return new WaitForSeconds(1f);
                    }
                    Debug.Log(Log.Tag + " Probe: body menu done: " + (targeter != null && menuTarget != null));
                }
                // -redefinitionProbeLaunchMenu: the map's menu of the vessel on the ground, as a shot.
                if (CommandLine.Has("-redefinitionProbeLaunchMenu") && MapLaunchMenu.Instance != null)
                {
                    yield return new WaitForSeconds(3f);
                    MapLaunchMenu.Instance.Show(FlightGlobals.ActiveVessel);
                    yield return new WaitForSeconds(2f);
                    UnityEngine.ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(folder, "launch-menu.png"));
                    yield return new WaitForSeconds(1f);
                    MapLaunchMenu.Instance.Close();
                }
                yield return AutoClimbRun(folder);
                if (CommandLine.Has("-redefinitionProbeMapPlannerThenLand") && !ProbeWatch.Lost && FlightGlobals.ActiveVessel.situation == Vessel.Situations.ORBITING)
                {
                    yield return ProbeSteps.Frames(120);
                    Debug.Log(Log.Tag + " Probe: map planner: destination " + wantedBody + " (the surface) set: " + MapPlannerWindow.Instance.SetSurfaceDestination(wantedBody));
                    yield return AutoLandRun(folder);
                }
                Debug.Log(Log.Tag + " Probe: map planner done.");
                yield break;
            }
            Debug.Log(Log.Tag + " Probe: map planner: the window " + (MapPlannerWindow.Instance != null ? "is there" : "is missing") + ", the vessel is " + FlightGlobals.ActiveVessel.situation + ".");
            // The flag -redefinitionProbePadRoute: the route starts on the pad and comes back to it, the planner plans it, and the navigation is turned on.
            if (CommandLine.Has("-redefinitionProbePadRoute") && MapPlannerWindow.Instance != null)
            {
                yield return new WaitForSeconds(2f);
                Debug.Log(Log.Tag + " Probe: pad route: last stop set " + MapPlannerWindow.Instance.SetLastStop("Kerbin"));
                yield return new WaitForSeconds(8f);
                RoutePlan pad = MapPlannerWindow.Instance.WholePlan;
                Debug.Log(Log.Tag + " Probe: pad route: plan " + (pad == null ? "none" : pad.Steps.Count + " step(s), " + pad.DeltaV.ToString("0") + " m/s, reached " + pad.Reached + ", " + (pad.Ended ?? "no end")));
                if (pad != null) foreach (PlannedStep step in pad.Steps) Debug.Log(Log.Tag + " Probe: pad route: step " + step.Kind + " goal " + step.Goal + ": " + step.Why);
                ScreenCapture.CaptureScreenshot(Path.Combine(folder, "map-planner-pad.png"));
                yield return ProbeSteps.Frames(5);
                // The flag -redefinitionProbeFlightBudget flies straight up on full thrust and shows the planner's figures high in the air.
                if (CommandLine.Has("-redefinitionProbeFlightBudget"))
                {
                    FlightInputHandler.state.mainThrottle = 1f;
                    StageManager.ActivateNextStage();
                    float lifted = Time.realtimeSinceStartup;
                    while (FlightGlobals.ActiveVessel.altitude < 14000.0 && Time.realtimeSinceStartup - lifted < 120f) yield return new WaitForSeconds(1f);
                    FlightInputHandler.state.mainThrottle = 0f;
                    MapView.EnterMapView();
                    MapPlannerWindow.Instance.OpenNow();
                    yield return new WaitForSeconds(6f);
                    Debug.Log(Log.Tag + " Probe: pad route: in flight at " + FlightGlobals.ActiveVessel.altitude.ToString("0") + " m, " + FlightGlobals.ActiveVessel.obt_speed.ToString("0") + " m/s.");
                    ScreenCapture.CaptureScreenshot(Path.Combine(folder, "map-planner-flight.png"));
                    yield return ProbeSteps.Frames(5);
                    Debug.Log(Log.Tag + " Probe: map planner done.");
                    yield break;
                }
                MapPlannerWindow.Instance.ToggleNavigation();
                MapView.ExitMapView();
                yield return new WaitForSeconds(6f);
                Debug.Log(Log.Tag + " Probe: pad route: guidance " + (GuidanceBoard.Current(FlightGlobals.ActiveVessel) ? GuidanceBoard.Line + " | bar " + GuidanceBoard.HasBar + " " + GuidanceBoard.Arc.ToString("0.00") + " " + GuidanceBoard.NumberText : "none"));
                ScreenCapture.CaptureScreenshot(Path.Combine(folder, "pad-navigation.png"));
                yield return ProbeSteps.Frames(5);
                Debug.Log(Log.Tag + " Probe: map planner done.");
                yield break;
            }
            // The flag -redefinitionProbePadClimb makes the route start on the pad, where the vessel is, and go up from there.
            bool onThePad = CommandLine.Has("-redefinitionProbePadClimb") && FlightGlobals.ActiveVessel.situation == Vessel.Situations.PRELAUNCH;
            if (MapPlannerWindow.Instance != null && (onThePad || FlightGlobals.ActiveVessel.situation == Vessel.Situations.ORBITING || FlightGlobals.ActiveVessel.situation == Vessel.Situations.ESCAPING))
            {
                string wanted = CommandLine.After("-redefinitionProbeMapPlannerTo") ?? "Minmus";
                bool landOn = CommandLine.Has("-redefinitionProbeMapPlannerLand");
                // The flag -redefinitionProbeMapPlannerOrbitStop makes the route an orbit round the destination first and the landing after it.
                bool orbitStop = landOn && CommandLine.Has("-redefinitionProbeMapPlannerOrbitStop");
                string from = CommandLine.After("-redefinitionProbeMapPlannerFrom");
                if (from != null)
                    Debug.Log(Log.Tag + " Probe: map planner: start " + from + " set: " + MapPlannerWindow.Instance.SetStart(from, FlightGlobals.ActiveVessel.orbit.PeA));
                Debug.Log(Log.Tag + " Probe: map planner: destination " + wanted + (landOn ? " (the surface)" : "") + " set: "
                          + (orbitStop ? MapPlannerWindow.Instance.SetOrbitThenSurface(wanted, double.Parse(CommandLine.After("-redefinitionProbeMapPlannerAltitude") ?? "60000"), CommandLine.Has("-redefinitionProbeMapPlannerAir"))
                           : landOn ? MapPlannerWindow.Instance.SetSurfaceDestination(wanted, CommandLine.Has("-redefinitionProbeMapPlannerAir"), CommandLine.Has("-redefinitionProbeMapPlannerOrbitFirst"))
                                    : MapPlannerWindow.Instance.SetDestination(wanted, double.Parse(CommandLine.After("-redefinitionProbeMapPlannerAltitude") ?? "30000"), CommandLine.Has("-redefinitionProbeMapPlannerAir"))));
                // The flag -redefinitionProbeThirdStop names a body whose surface the route ends on, as a round trip does.
                string third = CommandLine.After("-redefinitionProbeThirdStop");
                if (third != null) Debug.Log(Log.Tag + " Probe: map planner: third stop " + third + " set: " + MapPlannerWindow.Instance.SetLastStop(third));
                if (CommandLine.Has("-redefinitionProbeTargetVessel"))
                {
                    // The route ends with a meeting: another vessel in an orbit round the same body is made the target.
                    Vessel active = FlightGlobals.ActiveVessel;
                    // The orbiter whose plane is the nearest to the vessel's: a meeting with a polar one is not what the probe is after.
                    Vessel other = null;
                    foreach (Vessel candidate in FlightGlobals.Vessels)
                        if (candidate != active && candidate.mainBody == active.mainBody && candidate.situation == Vessel.Situations.ORBITING
                            && (other == null || System.Math.Abs(candidate.orbit.inclination - active.orbit.inclination) < System.Math.Abs(other.orbit.inclination - active.orbit.inclination)))
                            other = candidate;
                    ModuleDockingNode port = null;
                    if (other != null && CommandLine.Has("-redefinitionProbeTargetPort"))
                        foreach (Part part in other.parts)
                        {
                            ModuleDockingNode candidate = part.FindModuleImplementing<ModuleDockingNode>();
                            if (candidate != null && candidate.state == "Ready") port = candidate;
                        }
                    if (port != null) FlightGlobals.fetch.SetVesselTarget(port);
                    else if (other != null) FlightGlobals.fetch.SetVesselTarget(other);
                    Debug.Log(Log.Tag + " Probe: map planner: target " + (other != null ? other.vesselName + " (pid " + other.persistentId + "), " + other.orbit.PeA.ToString("0") + " x " + other.orbit.ApA.ToString("0") + " m" : "none"));
                }
                if (CommandLine.Has("-redefinitionProbePlanAll"))
                {
                    // The flag -redefinitionProbePlanAuto leaves the planning to the planner, which makes the plan by itself, and waits for it.
                    if (CommandLine.Has("-redefinitionProbePlanAuto")) yield return new WaitForSeconds(8f);
                    else MapPlannerWindow.Instance.PlanAllNow();
                    RoutePlan whole = MapPlannerWindow.Instance.WholePlan;
                    Debug.Log(Log.Tag + " Probe: whole plan: " + MapPlannerWindow.Instance.Status);
                    // The window with its flight plan section and the route drawn on the map, as the player sees them.
                    yield return ProbeSteps.Frames(120);
                    ScreenCapture.CaptureScreenshot(Path.Combine(folder, "map-planner-whole-plan.png"));
                    yield return ProbeSteps.Frames(10);
                    // And the same from the Sun, where the transfer between the planets shows.
                    MapObject sun = PlanetariumCamera.fetch.targets.Find(t => t.celestialBody != null && t.celestialBody == Planetarium.fetch.Sun);
                    if (sun != null)
                    {
                        PlanetariumCamera.fetch.SetTarget(sun);
                        PlanetariumCamera.fetch.SetDistance(5e7f);
                        yield return ProbeSteps.Frames(120);
                        ScreenCapture.CaptureScreenshot(Path.Combine(folder, "map-planner-whole-plan-sun.png"));
                        yield return ProbeSteps.Frames(10);
                    }
                    if (whole != null)
                    {
                        foreach (PlannedStep step in whole.Steps)
                            Debug.Log(Log.Tag + " Probe: whole plan: step " + step.Kind + " at UT " + step.UT.ToString("0") + ", " + step.DeltaV.Length.ToString("0.0") + " m/s: " + step.Why);
                        foreach (PlannedLeg leg in whole.Legs)
                            Debug.Log(Log.Tag + " Probe: whole plan: leg round " + leg.Orbit.referenceBody.bodyName + " from UT " + leg.StartUT.ToString("0") + " to " + leg.EndUT.ToString("0")
                                      + ", " + leg.Orbit.PeA.ToString("0") + " x " + leg.Orbit.ApA.ToString("0") + " m.");
                        if (CommandLine.Has("-redefinitionProbeParsekCommit"))
                            Debug.Log(Log.Tag + " Probe: whole plan: Parsek: " + (ParsekRecording.CommitPlan(FlightGlobals.ActiveVessel, whole) ?? "committed"));
                    }
                    // -redefinitionProbeMarkers: the planner makes the whole plan again and again, with the map open; the map's markers and orbit lines are counted each time.
                    if (CommandLine.Has("-redefinitionProbeMarkers"))
                    {
                        // The vessel holds a node of its own, as it does while the player or the navigation has one in the map.
                        Vessel holder = FlightGlobals.ActiveVessel;
                        if (holder.patchedConicSolver.maneuverNodes.Count == 0)
                        {
                            holder.patchedConicSolver.AddManeuverNode(Planetarium.GetUniversalTime() + 900.0).DeltaV = new Vector3d(0.0, 0.0, 50.0);
                            holder.patchedConicSolver.UpdateFlightPlan();
                        }
                        for (int round = 0; round < 8; round++)
                        {
                            Debug.Log(Log.Tag + " Probe: markers: round " + round + ": " + MapMarkers());
                            MapPlannerWindow.Instance.PlanAllNow();
                            yield return ProbeSteps.Frames(90);
                        }
                        Debug.Log(Log.Tag + " Probe: markers: final: " + MapMarkers());
                        ScreenCapture.CaptureScreenshot(Path.Combine(folder, "map-planner-markers.png"));
                        yield return ProbeSteps.Frames(10);
                        yield break;
                    }
                    if (CommandLine.Has("-redefinitionProbeBackgroundLeave") && whole != null)
                    {
                        // The vessel stays in the world with its route kept for the background; the other scene warps past the plan's end.
                        Vessel leaving = FlightGlobals.ActiveVessel;
                        RouteState.Of(leaving).Autopilot = true;
                        double extra;
                        if (double.TryParse(CommandLine.After("-redefinitionProbeExtraClearance") ?? "", System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out extra)) RouteTuning.ExtraClearance = extra;
                        RouteTuning.TrustHeight = !CommandLine.Has("-redefinitionProbeCheckGround");
                        RouteTuning.Measure = !CommandLine.Has("-redefinitionProbeNoSurvey");
                        RouteTuning.ScanSteepest = CommandLine.Has("-redefinitionProbeTouchdownScan");
                        string at = CommandLine.After("-redefinitionProbeTouchdownAt");
                        double siteLatitude, siteLongitude, siteRadius;
                        string[] where = at != null ? at.Split(',') : new string[0];
                        if (where.Length == 2 && double.TryParse(where[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out siteLatitude)
                            && double.TryParse(where[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out siteLongitude))
                        {
                            RouteTuning.FixedSite = true;
                            RouteTuning.SiteLatitude = siteLatitude;
                            RouteTuning.SiteLongitude = siteLongitude;
                        }
                        if (double.TryParse(CommandLine.After("-redefinitionProbeTouchdownRadius") ?? "", System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out siteRadius))
                            RouteTuning.SiteRadius = siteRadius;
                        ParsekLeave.PartCount = leaving.parts.Count;
                        // The flag -redefinitionProbeViaPause leaves as the player does: the pause menu opens, and the scene is asked for with the time standing still.
                        string problem;
                        if (CommandLine.Has("-redefinitionProbeViaPause") && RouteDriver.Instance != null)
                        {
                            RouteDriver.Instance.Remember(MapPlannerWindow.Instance.Route());
                            FlightDriver.SetPause(true);
                            yield return new WaitForSecondsRealtime(1f);
                            problem = BackgroundRoutes.Holds(leaving, MapPlannerWindow.Instance.Route()) ? null : "the route was not kept when the pause menu opened";
                        }
                        else
                        {
                            whole = BackgroundRoutes.PlanFor(leaving, MapPlannerWindow.Instance.Route());
                            problem = BackgroundRoutes.Hand(leaving, whole, MapPlannerWindow.Instance.Route(), new BackgroundFigures());
                        }
                        Debug.Log(Log.Tag + " Probe: whole plan: background route: " + (problem ?? "kept"));
                        if (problem == null)
                        {
                            // The pause menu saves the game as the scene is left; the probe does the same.
                            GamePersistence.SaveGame("persistent", HighLogic.SaveFolder, SaveMode.OVERWRITE);
                            ParsekLeave.Name = leaving.vesselName;
                            ParsekLeave.EndUT = System.Math.Max(whole.EndUT, BackgroundRoutes.LastStep(leaving));
                            ParsekLeave.Active = true;
                            yield return ProbeSteps.Frames(30);
                            ParsekLeave.Scene = CommandLine.After("-redefinitionProbeParsekLeaveTo") == "KSC" ? GameScenes.SPACECENTER : GameScenes.TRACKSTATION;
                            HighLogic.LoadScene(ParsekLeave.Scene);
                            yield break;
                        }
                    }
                    if (CommandLine.Has("-redefinitionProbeParsekLeave") && whole != null)
                    {
                        Vessel leaving = FlightGlobals.ActiveVessel;
                        RouteState.Of(leaving).Autopilot = true;
                        string problem = ParsekRecording.CommitPlan(leaving, whole, true);
                        Debug.Log(Log.Tag + " Probe: whole plan: Parsek, vessel taken out: " + (problem ?? "done"));
                        if (problem == null)
                        {
                            ParsekLeave.RemovedPid = leaving.persistentId;
                            ParsekLeave.Name = leaving.vesselName;
                            ParsekLeave.EndUT = whole.EndUT;
                            ParsekLeave.Active = true;
                            yield return ProbeSteps.Frames(30);
                            ParsekLeave.Scene = CommandLine.After("-redefinitionProbeParsekLeaveTo") == "KSC" ? GameScenes.SPACECENTER : GameScenes.TRACKSTATION;
                            HighLogic.LoadScene(ParsekLeave.Scene);
                            yield break;
                        }
                    }
                    // The flag -redefinitionProbeMapPlannerHold keeps the window open this many seconds, for the timing of the planner.
                    string hold = CommandLine.After("-redefinitionProbeMapPlannerHold");
                    if (hold != null) yield return new WaitForSeconds(float.Parse(hold, System.Globalization.CultureInfo.InvariantCulture));
                    Debug.Log(Log.Tag + " Probe: map planner done.");
                    yield break;
                }
                if (!ResumeDone && !CommandLine.Has("-redefinitionProbeMapPlannerAuto"))
                {
                    MapPlannerWindow.Instance.PlanNow();
                    Debug.Log(Log.Tag + " Probe: map planner: " + MapPlannerWindow.Instance.Status);
                }
                if (ResumeDone)
                {
                    yield return ResumedFlight();
                    Debug.Log(Log.Tag + " Probe: map planner done.");
                    yield break;
                }
                if (CommandLine.Has("-redefinitionProbeMapPlannerAuto"))
                {
                    yield return CommandLine.Has("-redefinitionProbeMapPlannerLand") ? AutoLandRun(folder) : AutoNodesRun();
                    Debug.Log(Log.Tag + " Probe: map planner done.");
                    yield break;
                }
                if (CommandLine.Has("-redefinitionProbeMapPlannerFly"))
                {
                    MapPlannerWindow.Instance.FlyNow();
                    float started = Time.realtimeSinceStartup;
                    string last = "";
                    bool shot = false;
                    ProbeWatch.Arm(FlightGlobals.ActiveVessel);
                    while ((MapPlannerWindow.Instance.Flying || FlownByAutopilot(FlightGlobals.ActiveVessel)) && Time.realtimeSinceStartup - started < 3600f)
                    {
                        if (ProbeWatch.Gone(FlightGlobals.ActiveVessel)) break;
                        string now = MapPlannerWindow.Instance.FlightStatus;
                        Scenario.Watch(now);
                        // The map view stays open in this probe; a landing is flown from the flight view, as a player does.
                        if (now.StartsWith("Landing") && MapView.MapIsEnabled && !CommandLine.Has("-redefinitionProbeKeepMap"))
                        {
                            MapView.ExitMapView();
                            Debug.Log(Log.Tag + " Probe: map planner: the map view is left for the landing.");
                        }
                        if (now != last)
                        {
                            Debug.Log(Log.Tag + " Probe: map planner: flight: " + now);
                            last = now;
                        }
                        if (CommandLine.Has("-redefinitionProbeMapPlannerResume") && now.StartsWith("Burn") && Time.realtimeSinceStartup - started > 5f)
                        {
                            ResumeDone = true;
                            Game game = HighLogic.CurrentGame.Updated();
                            int index = game.flightState.protoVessels.FindIndex(v => v.vesselID == FlightGlobals.ActiveVessel.id);
                            Debug.Log(Log.Tag + " Probe: map planner: the flight is left and the vessel loaded again (" + index + ").");
                            FlightDriver.StartAndFocusVessel(game, index);
                            yield break;
                        }
                        // -redefinitionProbeLeaveAtBurn: the flight is left, as the player would, while MechJeb burns; the route driver does the rest.
                        if (CommandLine.Has("-redefinitionProbeLeaveAtBurn") && now.StartsWith("Burn: BURN") && Time.realtimeSinceStartup - started > 5f)
                        {
                            Debug.Log(Log.Tag + " Probe: the flight is left at a burn: " + now);
                            ParsekLeave.Name = FlightGlobals.ActiveVessel.vesselName;
                            ParsekLeave.RemovedPid = FlightGlobals.ActiveVessel.persistentId;
                            ParsekLeave.Scene = GameScenes.TRACKSTATION;
                            ParsekLeave.Active = true;
                            HighLogic.LoadScene(GameScenes.TRACKSTATION);
                            yield break;
                        }
                        // -redefinitionProbeLeaveWhenCoasting: the flight is left once the transfer burn is flown and the autopilot waits for the next change of orbit.
                        // With -redefinitionProbeLeaveWithNode it is left as the burn waits, with the maneuver node on the vessel.
                        bool leaveWithNode = CommandLine.Has("-redefinitionProbeLeaveWithNode");
                        if (CommandLine.Has("-redefinitionProbeLeaveWhenCoasting") && (leaveWithNode ? now.StartsWith("Burn") : now.EndsWith("Warping to the next change of orbit.")) && Time.realtimeSinceStartup - started > 5f)
                        {
                            Debug.Log(Log.Tag + " Probe: the flight is left while it coasts: " + now);
                            Vessel coasting = FlightGlobals.ActiveVessel;
                            Debug.Log(Log.Tag + " Probe: leave: the vessel holds " + coasting.patchedConicSolver.maneuverNodes.Count + " maneuver node(s) as it is left.");
                            // As the player leaves: the pause menu opens, the route is kept for the background, the game is saved, and the scene is asked for.
                            // -redefinitionProbeLateHand: no pause menu, so the route is handed over only when the scene is asked for.
                            if (!CommandLine.Has("-redefinitionProbeLateHand")) FlightDriver.SetPause(true);
                            yield return new WaitForSecondsRealtime(1f);
                            for (int waits = 0; !CommandLine.Has("-redefinitionProbeLateHand") && waits < 20 && BackgroundRoutes.LastStep(coasting) <= 0.0; waits++) yield return new WaitForSecondsRealtime(0.5f);
                            for (int waits = 0; waits < 20 && BackgroundRoutes.LastStep(coasting) <= 0.0; waits++) yield return new WaitForSecondsRealtime(0.5f);
                            Debug.Log(Log.Tag + " Probe: the route is kept up to UT " + BackgroundRoutes.LastStep(coasting).ToString("0") + " when the game is saved.");
                            if (!CommandLine.Has("-redefinitionProbeLateHand")) GamePersistence.SaveGame("persistent", HighLogic.SaveFolder, SaveMode.OVERWRITE);
                            ParsekLeave.Name = coasting.vesselName;
                            ParsekLeave.RemovedPid = coasting.persistentId;
                            ParsekLeave.EndUT = BackgroundRoutes.LastStep(coasting);
                            ParsekLeave.Scene = GameScenes.TRACKSTATION;
                            ParsekLeave.Active = true;
                            HighLogic.LoadScene(GameScenes.TRACKSTATION);
                            yield break;
                        }
                        if (!shot && now.StartsWith("Burn"))
                        {
                            shot = true;
                            ScreenCapture.CaptureScreenshot(Path.Combine(folder, "map-planner-burn.png"));
                        }
                        yield return new WaitForSeconds(0.5f);
                    }
                    Debug.Log(Log.Tag + " Probe: map planner: flight over: " + MapPlannerWindow.Instance.Status + " In " + FlightGlobals.ActiveVessel.mainBody.bodyName
                              + " at " + FlightGlobals.ActiveVessel.orbit.PeA.ToString("0") + " x " + FlightGlobals.ActiveVessel.orbit.ApA.ToString("0") + " m.");
                }
                yield return ProbeSteps.Frames(120);
                foreach (Orbit patch in FlightGlobals.ActiveVessel.patchedConicSolver.flightPlan)
                    Debug.Log(Log.Tag + " Probe: map planner:   patch round " + patch.referenceBody.bodyName + ", periapsis " + patch.PeA.ToString("0") + " m.");
                ScreenCapture.CaptureScreenshot(Path.Combine(folder, "map-planner-planned.png"));
                yield return ProbeSteps.Frames(5);
            }
            Debug.Log(Log.Tag + " Probe: map planner done.");
        }
    }
}
