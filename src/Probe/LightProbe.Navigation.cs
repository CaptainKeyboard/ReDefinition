using System.Collections;
using System.IO;
using KSP.UI;
using ReDefinition.Planner.Autopilot;
using ReDefinition.Planner.Autopilot.Flight;
using ReDefinition.Planner.Autopilot.Guidance;
using ReDefinition.Planner.Autopilot.Planning;
using ReDefinition.Core;
using ReDefinition.Planner.Engineer;
using ReDefinition.Planner.Orbits;
using UnityEngine;

namespace ReDefinition.Probe
{
    // The navigation and the autopilot flown as the player flies them, one run to a goal each: burns by MechJeb, a descent by the marker and
    // the throttle the gauge asks for, a climb from the pad. The stand-in pilot is made to follow only what the flight view shows.
    internal static partial class LightProbe
    {
        // The route set as the player sets it, the nodes kept up by the planner, and burns flown by MechJeb as if they were the player's: the
        // plan has to be made, and again after each burn.
        private static IEnumerator AutoNodesRun()
        {
            Vessel vessel = FlightGlobals.ActiveVessel;
            ProbeWatch.Arm(vessel);
            RouteDriver.Instance.SetNavigation(vessel, true);
            float started = Time.realtimeSinceStartup;
            string last = "";
            int burns = 0;
            while (Time.realtimeSinceStartup - started < 900f && burns < 2 && !ProbeWatch.Gone(vessel))
            {
                string now = RouteDriver.Instance.Status;
                Scenario.Watch(now);
                if (now != last)
                {
                    Debug.Log(Log.Tag + " Probe: auto nodes: " + now);
                    last = now;
                }
                PatchedConicSolver solver = vessel.patchedConicSolver;
                if (solver.maneuverNodes.Count > 0 && !MechJebDriver.Busy(vessel))
                {
                    burns++;
                    Debug.Log(Log.Tag + " Probe: auto nodes: the player flies the burn, " + solver.maneuverNodes[0].DeltaV.magnitude.ToString("0.0") + " m/s.");
                    if (!Engines.Running(vessel)) Engines.FireNextStageIfCold(vessel);
                    MechJebDriver.ExecuteNode(vessel, true);
                    while (MechJebDriver.Busy(vessel)) yield return new WaitForSeconds(0.5f);
                }
                yield return new WaitForSeconds(1f);
            }
            Debug.Log(Log.Tag + " Probe: auto nodes: ended, " + RouteDriver.Instance.Status);
        }

        // Time is warped to two minutes before the vessel meets the ground, as the orbit says: the descent is not played in real time.
        private static void WarpToImpact(Vessel vessel)
        {
            double now = Planetarium.GetUniversalTime();
            State state = KspOrbits.StateAt(vessel.orbit, now);
            double ground = vessel.mainBody.Radius + (vessel.mainBody.atmosphere ? vessel.mainBody.atmosphereDepth : 3000.0);
            double impact = -1.0;
            for (double t = 0.0; t < vessel.orbit.period; t += 5.0)
                if (Kepler.Propagate(state, t, vessel.mainBody.gravParameter).R.Length <= ground)
                {
                    impact = t;
                    break;
                }
            if (impact < 400.0) return;
            Debug.Log(Log.Tag + " Probe: auto land: the impact is in " + impact.ToString("0") + " s, warped to five minutes before.");
            TimeWarp.fetch.WarpTo(now + impact - 300.0);
        }

        // The navigation on a route to a surface, and the probe as the player: the deorbit node is flown, then the vessel is turned
        // retrograde and the throttle opened where the guidance says to brake, and shut near the ground. The guidance is logged as it changes.
        private static IEnumerator AutoLandRun(string folder)
        {
            Vessel vessel = FlightGlobals.ActiveVessel;
            ProbeWatch.Arm(vessel);
            if (CommandLine.Has("-redefinitionProbeTrace")) FlightTrace.Begin(System.IO.Path.Combine(folder, "flight-trace.csv"));
            // -redefinitionProbeLandMarker <latitude>,<longitude>: the landing is aimed at a marker, as the map's menu aims it.
            string marker = CommandLine.After("-redefinitionProbeLandMarker");
            double markerLatitude = 0.0, markerLongitude = 0.0;
            bool aimed = marker != null && marker.Split(',').Length == 2
                         && double.TryParse(marker.Split(',')[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out markerLatitude)
                         && double.TryParse(marker.Split(',')[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out markerLongitude);
            if (aimed) LandingMarker.Instance.Set(vessel, markerLatitude, markerLongitude);
            else RouteDriver.Instance.SetNavigation(vessel, true);
            float started = Time.realtimeSinceStartup;
            string last = "";
            float nextLine = 0f;
            bool deorbited = false;
            bool shownHud = false;
            int shots = 0;
            float nextShot = 0f;
            float lastChange = started;
            while (Time.realtimeSinceStartup - started < 1500f && !ProbeWatch.Gone(vessel) && !vessel.LandedOrSplashed)
            {
                string now = RouteDriver.Instance.Status;
                if (now != last) lastChange = Time.realtimeSinceStartup;
                if (Time.realtimeSinceStartup - lastChange > 240f)
                {
                    Debug.Log(Log.Tag + " " + ProbeWatch.LostLine + ": the status did not change for four minutes (" + now + ").");
                    yield break;
                }
                if (now != last && (!(now.StartsWith("Descent") || now.StartsWith("Final")) || Time.realtimeSinceStartup > nextLine))
                {
                    Debug.Log(Log.Tag + " Probe: auto land: " + now + " [" + vessel.situation + ", " + vessel.altitude.ToString("0") + " m, " + vessel.srfSpeed.ToString("0") + " m/s, guide node "
                              + GuideNode.Holds(vessel) + ", gauge " + (GuidanceBoard.HasBar ? GuidanceBoard.NumberText + ", need " + GuidanceBoard.Need.ToString("0.00") : "-") + "]");
                    last = now;
                    nextLine = Time.realtimeSinceStartup + (now.StartsWith("Final") ? 0.5f : 10f);
                }
                PatchedConicSolver solver = vessel.patchedConicSolver;
                if (!deorbited && solver.maneuverNodes.Count > 0 && !MechJebDriver.Busy(vessel))
                {
                    deorbited = true;
                    Debug.Log(Log.Tag + " Probe: auto land: the player flies the deorbit burn, " + solver.maneuverNodes[0].DeltaV.magnitude.ToString("0.0") + " m/s.");
                    if (!Engines.Running(vessel)) Engines.FireNextStageIfCold(vessel);
                    MechJebDriver.ExecuteNode(vessel, true);
                    // A plan that turns the plane first has two burns: the second is planned again once the first is flown.
                    int planned = solver.maneuverNodes.Count;
                    for (int burn = 0; burn < 2; burn++)
                    {
                        float nextBurnLine = 0f;
                        while (MechJebDriver.Busy(vessel) && !ProbeWatch.Gone(vessel))
                        {
                            if (Time.realtimeSinceStartup > nextBurnLine)
                            {
                                nextBurnLine = Time.realtimeSinceStartup + 20f;
                                Debug.Log(Log.Tag + " Probe: auto land: the burn: " + MechJebDriver.State(vessel) + ", " + vessel.altitude.ToString("0") + " m.");
                            }
                            // A burn this long outlasts a stage: the next one is lit, as a player stages.
                            if (!Engines.Running(vessel) && MechJebDriver.State(vessel) == "BURN") Engines.FireNextStageIfCold(vessel);
                            yield return new WaitForSeconds(0.5f);
                        }
                        if (burn == 1 || planned < 2) break;
                        float replanned = Time.realtimeSinceStartup + 60f;
                        while (solver.maneuverNodes.Count == 0 && Time.realtimeSinceStartup < replanned && !ProbeWatch.Gone(vessel)) yield return new WaitForSeconds(1f);
                        if (solver.maneuverNodes.Count == 0) break;
                        Debug.Log(Log.Tag + " Probe: auto land: the player flies the next burn, " + solver.maneuverNodes[0].DeltaV.magnitude.ToString("0.0") + " m/s.");
                        if (!Engines.Running(vessel)) Engines.FireNextStageIfCold(vessel);
                        MechJebDriver.ExecuteNode(vessel, true);
                    }
                    WarpToImpact(vessel);
                }
                if (deorbited && now.StartsWith("Descent: no engine")) Engines.FireNextStageIfCold(vessel);
                if (deorbited && now.StartsWith("Parachutes"))
                    foreach (Part part in vessel.parts)
                        foreach (ModuleParachute chute in part.FindModulesImplementing<ModuleParachute>())
                            if (chute.deploymentState == ModuleParachute.deploymentStates.STOWED) chute.Deploy();
                if (deorbited && (now.StartsWith("Descent") || now.StartsWith("Final") || now.StartsWith("Down") || now.StartsWith("Parachutes")))
                {
                    if (!shownHud)
                    {
                        shownHud = true;
                        MapView.ExitMapView();
                        if (UIMasterController.Instance != null) UIMasterController.Instance.ShowUI();
                    }
                    StandIn(vessel, RouteDriver.Instance.GuidedThrottle);
                    if (shots < 3 && Time.realtimeSinceStartup > nextShot && (shots == 0 || RouteDriver.Instance.GuidedThrottle > 0.3))
                    {
                        nextShot = Time.realtimeSinceStartup + 8f;
                        UnityEngine.ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(folder, "land-hud-" + shots + ".png"));
                        Debug.Log(Log.Tag + " Probe: auto land: shot " + shots + ": " + now);
                        shots++;
                    }
                }
                yield return deorbited ? null : new WaitForSeconds(0.2f);
            }
            FlightInputHandler.state.mainThrottle = 0f;
            if (aimed)
            {
                double a = markerLatitude * System.Math.PI / 180.0, b2 = vessel.latitude * System.Math.PI / 180.0;
                double dLon = (vessel.longitude - markerLongitude) * System.Math.PI / 180.0;
                double h = System.Math.Sin((b2 - a) / 2.0) * System.Math.Sin((b2 - a) / 2.0) + System.Math.Cos(a) * System.Math.Cos(b2) * System.Math.Sin(dLon / 2.0) * System.Math.Sin(dLon / 2.0);
                Debug.Log(Log.Tag + " Probe: marker: landed at " + vessel.latitude.ToString("0.000") + ", " + vessel.longitude.ToString("0.000") + ", "
                          + (2.0 * vessel.mainBody.Radius * System.Math.Asin(System.Math.Min(1.0, System.Math.Sqrt(h)))).ToString("0") + " m from the marker.");
            }
            Debug.Log(Log.Tag + " Probe: auto land: ended " + (ProbeWatch.Lost ? "LOST (" + ProbeWatch.Why + ")" : vessel.situation + ", " + vessel.srfSpeed.ToString("0.0") + " m/s") + ", "
                      + RouteDriver.Instance.Status);
        }

        // The pilot as the player is: the nose is turned steadily to the marker, 40 degrees a second at most, and the throttle is the one the
        // gauge asks for, set again in every frame.
        private static void StandIn(Vessel vessel, double throttle, bool turn = true)
        {
            if (turn && GuidanceBoard.Current(vessel) && GuidanceBoard.HasDirection)
            {
                Quaternion want = Quaternion.FromToRotation(vessel.GetTransform().up, (Vector3)GuidanceBoard.Direction) * vessel.GetTransform().rotation;
                vessel.SetRotation(Quaternion.RotateTowards(vessel.GetTransform().rotation, want, 40f * Time.deltaTime));
                foreach (Part part in vessel.parts)
                    if (part.Rigidbody != null) part.Rigidbody.angularVelocity = Vector3.zero;
            }
            FlightInputHandler.state.mainThrottle = (float)throttle;
        }

        // What the SAS does with the guidance: its mode, whether it may be chosen, how far the nose is from the board's direction, and the burn gauge.
        private static string SasState(Vessel vessel)
        {
            VesselAutopilot autopilot = vessel.Autopilot;
            float angle = GuidanceBoard.Current(vessel) && GuidanceBoard.HasDirection ? Vector3.Angle(vessel.GetTransform().up, (Vector3)GuidanceBoard.Direction) : -1f;
            KSP.UI.Screens.Flight.NavBallBurnVector vector = Object.FindObjectOfType<KSP.UI.Screens.Flight.NavBallBurnVector>();
            string gauge = vector != null && vector.deltaVGauge != null ? vector.deltaVGauge.gameObject.activeInHierarchy + " at " + vector.deltaVGauge.currentValue.ToString("0.00") + " of " + vector.deltaVGauge.MaxValue.ToString("0.00") + ", angle "
                                                                                                                                         + vector.deltaVGauge.currentAngle.ToString("0") + ", readout " + vector.deltaVGauge.showReadout + " "
                                                                                                                                         + (vector.deltaVGauge.readoutField != null ? vector.deltaVGauge.readoutField.name + " at " + vector.deltaVGauge.readoutField.position + " parent " + vector.deltaVGauge.readoutField.parent.name : "no field")
                                                                                                                                         + ", min " + vector.deltaVGauge.MinValue + " angles " + vector.deltaVGauge.angleAtMinValue + " " + vector.deltaVGauge.angleAtMaxValue
                                                                                                                                         + " fill " + vector.deltaVGauge.FillatMinValue + " " + vector.deltaVGauge.FillatMaxValue + " amount " + vector.deltaVGauge.tgtImage.fillAmount.ToString("0.00")
                                                                                                                                         + " enabled " + vector.deltaVGauge.enabled + " standoff " + vector.deltaVGauge.readoutStandoff + " gauge at " + vector.deltaVGauge.transform.position
                                                                                                                                         + " scale " + GameSettings.UI_SCALE + " " + GameSettings.UI_SCALE_NAVBALL + " " + GameSettings.UIELEMENTSCALINGENABLED
                                                                                                                                         + ", text at " + (vector.readoutText != null ? vector.readoutText.transform.position + " in " + vector.readoutText.transform.parent.name : "none") : "none";
            PatchedConicSolver solver = vessel.patchedConicSolver;
            string node = solver != null && solver.maneuverNodes.Count > 0
                ? "nodes " + solver.maneuverNodes.Count + " (guide " + GuideNode.Holds(vessel) + ", burn " + solver.maneuverNodes[0].GetBurnVector(vessel.orbit).magnitude.ToString("0") + " m/s, "
                  + Vector3.Angle((Vector3)solver.maneuverNodes[0].GetBurnVector(vessel.orbit), (Vector3)GuidanceBoard.Direction).ToString("0.0") + " degrees from the board)"
                : "no node";
            // Each node: when, how big, and the orbit after it, as the map draws it.
            if (solver != null)
                foreach (ManeuverNode each in solver.maneuverNodes)
                    node += " [node in " + (each.UT - Planetarium.GetUniversalTime()).ToString("0") + " s, " + each.DeltaV.magnitude.ToString("0") + " m/s, then "
                            + (each.nextPatch != null ? each.nextPatch.PeA.ToString("0") + " x " + each.nextPatch.ApA.ToString("0") + " m" : "no patch") + "]";
            return node + ", mode " + autopilot.Mode + ", on " + autopilot.Enabled + ", maneuver mode offered " + autopilot.CanSetMode(VesselAutopilot.AutopilotMode.Maneuver) + ", nose "
                   + angle.ToString("0.0") + " degrees from the board, altitude " + vessel.altitude.ToString("0") + " m, speed " + vessel.srfSpeed.ToString("0") + " m/s, bar "
                   + (GuidanceBoard.HasBar ? GuidanceBoard.Arc.ToString("0.00") + " (" + GuidanceBoard.Level + ")" : "none") + ", number "
                   + (GuidanceBoard.HasBar ? GuidanceBoard.NumberText + ", need " + GuidanceBoard.Need.ToString("0.00") : "none") + ", burn gauge " + gauge + ".";
        }

        // The navigation from the pad to an orbit, and the probe as the player. The first stage is lit, the nose follows the marker, and the
        // throttle is the one the guidance asks for. A stage is fired where none is running. MechJeb burns the nodes that follow the climb.
        private static IEnumerator AutoClimbRun(string folder)
        {
            Vessel vessel = FlightGlobals.ActiveVessel;
            ProbeWatch.Arm(vessel);
            if (CommandLine.Has("-redefinitionProbeTrace")) FlightTrace.Begin(System.IO.Path.Combine(folder, "flight-trace.csv"));
            // -redefinitionProbeQuickAscent: the climb is asked for as the menu of the map asks for it, with no plan made in the planner.
            if (CommandLine.Has("-redefinitionProbeQuickAscent")) RouteDriver.Instance.StartAscent(vessel);
            else RouteDriver.Instance.SetNavigation(vessel, true);
            MapView.ExitMapView();
            if (UIMasterController.Instance != null) UIMasterController.Instance.ShowUI();
            // -redefinitionProbeSasFollow: the SAS is on and steers; the probe only gives the throttle, and says how the nose follows the board.
            bool sasFollow = CommandLine.Has("-redefinitionProbeSasFollow");
            // -redefinitionProbeNoSolver: the vessel has no solver for nodes, as before the tracking station's level allows them; it is taken off every second.
            bool noSolver = CommandLine.Has("-redefinitionProbeNoSolver");
            if (sasFollow) vessel.ActionGroups.SetGroup(KSPActionGroup.SAS, true);
            float nextSample = 0f;
            int samples = 0;
            float started = Time.realtimeSinceStartup;
            string last = "";
            float nextLine = 0f;
            float nextShot = 5f;
            int shots = 0;
            bool burned = false;
            // -redefinitionProbeGaugeShots: five shots of the thrust gauges along the climb, with the throttle held wrong where the bar is to be seen off the mark.
            bool gaugeShots = CommandLine.Has("-redefinitionProbeGaugeShots");
            bool shotUnder = false, shotBefore = false, shotMid = false, shotAfter = false, shotApoapsis = false;
            float holdUntil = -1f, holdThrottle = 0f, captureAt = -1f;
            string captureName = null;
            float doneSeen = -1f;
            while (Time.realtimeSinceStartup - started < 1500f && !ProbeWatch.Gone(vessel))
            {
                if (noSolver && vessel.PatchedConicsAttached) vessel.DetachPatchedConicsSolver();
                string now = RouteDriver.Instance.Status;
                if (gaugeShots)
                {
                    float clock = Time.realtimeSinceStartup;
                    // The burn left to the apoapsis aimed for (80 km), as the guidance works it out.
                    double idealNow, burnLeft;
                    bool climbDone;
                    ClimbGuide.Climb(vessel, 80000.0, out idealNow, out burnLeft, out climbDone);
                    double full = Engines.Acceleration(vessel) * 10.0;
                    if (now.Contains("engines off") && doneSeen < 0f) doneSeen = clock;
                    // 1: half way up, the throttle too low: the bar under the mark.
                    if (!shotUnder && vessel.orbit.ApA > 40000.0 && GuidanceBoard.Current(vessel) && !now.Contains("engines off"))
                    {
                        shotUnder = true;
                        holdUntil = clock + 3f;
                        holdThrottle = (float)(0.45 * GuidanceBoard.Throttle);
                        captureAt = clock + 2f;
                        captureName = "gauge-1-halfway.png";
                    }
                    // 2: just before the throttle starts to fall: the bar on the mark, the number counting down.
                    else if (shotUnder && !shotBefore && holdUntil < clock && burnLeft > 0.0 && burnLeft <= 1.25 * full && burnLeft > 1.0 * full)
                    {
                        shotBefore = true;
                        captureAt = clock;
                        captureName = "gauge-2-before-slowdown.png";
                    }
                    // 3: in the middle of the fall, the throttle kept up: the bar over the mark, less is wanted.
                    else if (shotBefore && !shotMid && burnLeft > 0.0 && burnLeft <= 0.5 * full)
                    {
                        shotMid = true;
                        holdUntil = clock + 2.5f;
                        holdThrottle = 1f;
                        captureAt = clock + 1.5f;
                        captureName = "gauge-3-mid-slowdown.png";
                    }
                    // 4: after the fall, the apoapsis reached: the mark and the bar slide off, and the game's display takes over.
                    else if (shotMid && !shotAfter && doneSeen >= 0f && clock > doneSeen + 3.5f)
                    {
                        shotAfter = true;
                        captureAt = clock;
                        captureName = "gauge-4-after-slowdown.png";
                    }
                    // 5: near the vessel's apoapsis: the game's burn display of the circularizing.
                    else if (shotAfter && !shotApoapsis && vessel.orbit.timeToAp < 25.0 && vessel.altitude > 70000.0)
                    {
                        shotApoapsis = true;
                        captureAt = clock;
                        captureName = "gauge-5-apoapsis.png";
                    }
                    if (captureName != null && clock >= captureAt)
                    {
                        UnityEngine.ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(folder, captureName));
                        Debug.Log(Log.Tag + " Probe: gauge shot " + captureName + ".");
                        captureName = null;
                    }
                }
                if (now != last && Time.realtimeSinceStartup > nextLine)
                {
                    Debug.Log(Log.Tag + " Probe: auto climb: " + now + " [" + vessel.situation + ", " + vessel.altitude.ToString("0") + " m, " + vessel.srfSpeed.ToString("0") + " m/s, apoapsis "
                              + vessel.orbit.ApA.ToString("0") + " m, air " + vessel.atmDensity.ToString("0.000") + ", gauge "
                              + (GuidanceBoard.HasBar ? "ideal " + GuidanceBoard.Ideal.ToString("0.00") + " need " + GuidanceBoard.Need.ToString("0.00") + " " + GuidanceBoard.NumberText : "none") + "]");
                    last = now;
                    nextLine = Time.realtimeSinceStartup + 5f;
                }
                PatchedConicSolver solver = vessel.patchedConicSolver;
                if (now.StartsWith("Climb"))
                {
                    if (GuidanceBoard.Current(vessel) && GuidanceBoard.HasThrottle && GuidanceBoard.Throttle > 0.02 && !Engines.Running(vessel)) Engines.FireNextStageIfCold(vessel);
                    double standing = GuidanceBoard.Current(vessel) && GuidanceBoard.HasThrottle ? GuidanceBoard.Throttle : 0.0;
                    if (gaugeShots && Time.realtimeSinceStartup < holdUntil) standing = holdThrottle;
                    StandIn(vessel, standing, !sasFollow);
                    if (sasFollow && Time.realtimeSinceStartup > nextSample)
                    {
                        nextSample = Time.realtimeSinceStartup + (samples >= 9 && samples <= 13 ? 2f : 4f);
                        Debug.Log(Log.Tag + " Probe: sas: " + SasState(vessel));
                        if (samples == 10) UnityEngine.ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(folder, "guide-node.png"));
                        if (CommandLine.Has("-redefinitionProbeGuideChecks"))
                        {
                            // The save must not hold the guide node: the game's file is read for a maneuver in the vessel's plan.
                            if (samples == 4)
                            {
                                GamePersistence.SaveGame("redefinition-guide-save", HighLogic.SaveFolder, SaveMode.OVERWRITE);
                                string saved = System.IO.File.ReadAllText(System.IO.Path.Combine(KSPUtil.ApplicationRootPath, "saves", HighLogic.SaveFolder, "redefinition-guide-save.sfs"));
                                int at = saved.IndexOf("FLIGHTPLAN");
                                int maneuvers = 0;
                                for (int index = saved.IndexOf("MANEUVER"); index >= 0; index = saved.IndexOf("MANEUVER", index + 1)) maneuvers++;
                                Debug.Log(Log.Tag + " Probe: guide checks: the save was made with the guide node " + GuideNode.Holds(vessel) + ", and holds " + maneuvers + " MANEUVER block(s).");
                            }
                            // The planner plans while the guide stands: the guide is made again afterwards, and the navigation goes on.
                            if (samples == 6 && MapPlannerWindow.Instance != null)
                            {
                                MapPlannerWindow.Instance.PlanNow();
                                Debug.Log(Log.Tag + " Probe: guide checks: the planner planned; " + MapPlannerWindow.Instance.Status);
                            }
                            if (samples == 8) Debug.Log(Log.Tag + " Probe: guide checks: two samples after the planner: guide " + GuideNode.Holds(vessel) + ", navigation " + (RouteState.Of(vessel) != null && RouteState.Of(vessel).Navigation) + ".");
                        }
                        // The X button of the game takes the node away with this call; the navigation is to end with it.
                        if (samples == 12 && vessel.patchedConicSolver != null && vessel.patchedConicSolver.maneuverNodes.Count > 0)
                        {
                            Debug.Log(Log.Tag + " Probe: sas: the player takes the maneuver away.");
                            vessel.patchedConicSolver.maneuverNodes[0].RemoveSelf();
                            yield return new WaitForSecondsRealtime(1.5f);
                            RouteState taken = RouteState.Of(vessel);
                            bool clean = vessel.patchedConicSolver.maneuverNodes.Count == 0;
                            Debug.Log(Log.Tag + " Probe: sas: " + (clean ? "" : "FAILED: ") + "1.5 s after the maneuver was taken away: navigation " + (taken != null && taken.Navigation) + ", nodes left "
                                      + vessel.patchedConicSolver.maneuverNodes.Count + ".");
                        }
                        if (samples == 15)
                        {
                            RouteState left = RouteState.Of(vessel);
                            Debug.Log(Log.Tag + " Probe: sas: after the maneuver was taken away: navigation " + (left != null && left.Navigation) + ", nodes " + vessel.patchedConicSolver.maneuverNodes.Count
                                      + ", dismissed " + GuideNode.Dismissed + ".");
                        }
                        if (++samples >= 20)
                        {
                            FlightInputHandler.state.mainThrottle = 0f;
                            Debug.Log(Log.Tag + " Probe: sas follow: done.");
                            yield break;
                        }
                    }
                    if (shots < 4 && Time.realtimeSinceStartup - started > nextShot)
                    {
                        nextShot += sasFollow ? 12f : 25f;
                        UnityEngine.ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(folder, "climb-hud-" + shots + ".png"));
                        Debug.Log(Log.Tag + " Probe: auto climb: shot " + shots + ": " + now);
                        shots++;
                    }
                }
                else if (solver.maneuverNodes.Count > 0 && !MechJebDriver.Busy(vessel) && vessel.situation != Vessel.Situations.FLYING)
                {
                    FlightInputHandler.state.mainThrottle = 0f;
                    burned = true;
                    Debug.Log(Log.Tag + " Probe: auto climb: the player flies the burn, " + solver.maneuverNodes[0].DeltaV.magnitude.ToString("0.0") + " m/s.");
                    if (!Engines.Running(vessel)) Engines.FireNextStageIfCold(vessel);
                    MechJebDriver.ExecuteNode(vessel, true);
                    float nextState = 0f;
                    float nextStage = 0f;
                    while (MechJebDriver.Busy(vessel) && !ProbeWatch.Gone(vessel))
                    {
                        string state = MechJebDriver.State(vessel);
                        if ((state == "LEAD" || state == "BURN") && Time.realtimeSinceStartup > nextStage)
                        {
                            nextStage = Time.realtimeSinceStartup + 2f;
                            if (!Engines.Running(vessel) && !Engines.FireNextStageIfCold(vessel)) MechJebDriver.Abort(vessel);
                        }
                        if (Time.realtimeSinceStartup > nextState)
                        {
                            nextState = Time.realtimeSinceStartup + 15f;
                            Debug.Log(Log.Tag + " Probe: auto climb: MechJeb " + MechJebDriver.State(vessel) + ", node in " + (solver.maneuverNodes.Count > 0
                                      ? (solver.maneuverNodes[0].UT - Planetarium.GetUniversalTime()).ToString("0") : "-") + " s, warp " + TimeWarp.CurrentRateIndex + ", throttle "
                                      + vessel.ctrlState.mainThrottle.ToString("0.00") + ", " + Engines.Report(vessel));
                        }
                        // MechJeb flies the burn from here: the shot at the apoapsis is taken while it waits.
                        if (gaugeShots && shotAfter && !shotApoapsis && vessel.orbit.timeToAp < 25.0)
                        {
                            shotApoapsis = true;
                            UnityEngine.ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(folder, "gauge-5-apoapsis.png"));
                            Debug.Log(Log.Tag + " Probe: gauge shot gauge-5-apoapsis.png.");
                        }
                        yield return new WaitForSeconds(0.5f);
                    }
                }
                else if (burned && vessel.orbit.PeA > 70000.0) break;
                yield return null;
            }
            FlightInputHandler.state.mainThrottle = 0f;
            Debug.Log(Log.Tag + " Probe: auto climb: ended " + (ProbeWatch.Lost ? "LOST (" + ProbeWatch.Why + ")" : vessel.situation.ToString()) + ", orbit " + vessel.orbit.PeA.ToString("0")
                      + " x " + vessel.orbit.ApA.ToString("0") + " m, " + RouteDriver.Instance.Status);
        }
    }
}
