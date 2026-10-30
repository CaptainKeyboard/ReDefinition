using System.Collections;
using System.IO;
using KSP.UI;
using ReDefinition.Autopilot;
using ReDefinition.Autopilot.Flight;
using ReDefinition.Autopilot.Planning;
using ReDefinition.Core;
using ReDefinition.Engineer;
using ReDefinition.Orbits;
using UnityEngine;

namespace ReDefinition.Probe
{
    // -redefinitionProbeMapPlanner: the planner's panel in the map view, open, as a shot of the screen.
    internal static partial class LightProbe
    {
        internal const string MapPlannerArgument = "-redefinitionProbeMapPlanner";

        // Whether the vessel was left and loaded again, so that the next run of the probe only watches what the autopilot does with it.
        private static bool ResumeDone;

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
            MapPlannerWindow.OpenAtOnce = true;
            yield return ProbeSteps.Frames(30);
            MapView.EnterMapView();
            if (UIMasterController.Instance != null) UIMasterController.Instance.ShowUI();
            yield return ProbeSteps.Frames(180);
            ScreenCapture.CaptureScreenshot(Path.Combine(folder, "map-planner.png"));
            yield return ProbeSteps.Frames(5);
            if (MapPlannerWindow.Instance != null && CommandLine.Has("-redefinitionProbeMapPlannerClimb"))
            {
                string wantedBody = CommandLine.After("-redefinitionProbeMapPlannerTo") ?? "Kerbin";
                Debug.Log(Log.Tag + " Probe: map planner: destination " + wantedBody + " set: " + MapPlannerWindow.Instance.SetDestination(wantedBody, 100000.0));
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
            if (MapPlannerWindow.Instance != null && (FlightGlobals.ActiveVessel.situation == Vessel.Situations.ORBITING || FlightGlobals.ActiveVessel.situation == Vessel.Situations.ESCAPING))
            {
                string wanted = CommandLine.After("-redefinitionProbeMapPlannerTo") ?? "Minmus";
                bool landOn = CommandLine.Has("-redefinitionProbeMapPlannerLand");
                string from = CommandLine.After("-redefinitionProbeMapPlannerFrom");
                if (from != null)
                    Debug.Log(Log.Tag + " Probe: map planner: start " + from + " set: " + MapPlannerWindow.Instance.SetStart(from, FlightGlobals.ActiveVessel.orbit.PeA));
                Debug.Log(Log.Tag + " Probe: map planner: destination " + wanted + (landOn ? " (the surface)" : "") + " set: "
                          + (landOn ? MapPlannerWindow.Instance.SetSurfaceDestination(wanted, CommandLine.Has("-redefinitionProbeMapPlannerAir"), CommandLine.Has("-redefinitionProbeMapPlannerOrbitFirst"))
                                    : MapPlannerWindow.Instance.SetDestination(wanted, double.Parse(CommandLine.After("-redefinitionProbeMapPlannerAltitude") ?? "30000"), CommandLine.Has("-redefinitionProbeMapPlannerAir"))));
                if (CommandLine.Has("-redefinitionProbeTargetVessel"))
                {
                    // The route ends with a meeting: another vessel in an orbit round the same body is made the target.
                    Vessel active = FlightGlobals.ActiveVessel;
                    Vessel other = FlightGlobals.Vessels.Find(v => v != active && v.mainBody == active.mainBody && v.situation == Vessel.Situations.ORBITING);
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
                    MapPlannerWindow.Instance.PlanAllNow();
                    RoutePlan whole = MapPlannerWindow.Instance.WholePlan;
                    Debug.Log(Log.Tag + " Probe: whole plan: " + MapPlannerWindow.Instance.Status);
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
