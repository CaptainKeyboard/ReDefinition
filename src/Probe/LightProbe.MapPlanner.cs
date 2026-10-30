using System.Collections;
using System.IO;
using KSP.UI;
using ReDefinition.Autopilot;
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
            if (MapPlannerWindow.Instance != null && FlightGlobals.ActiveVessel.situation == Vessel.Situations.ORBITING)
            {
                string wanted = CommandLine.After("-redefinitionProbeMapPlannerTo") ?? "Minmus";
                bool landOn = CommandLine.Has("-redefinitionProbeMapPlannerLand");
                string from = CommandLine.After("-redefinitionProbeMapPlannerFrom");
                if (from != null)
                    Debug.Log(Log.Tag + " Probe: map planner: start " + from + " set: " + MapPlannerWindow.Instance.SetStart(from, FlightGlobals.ActiveVessel.orbit.PeA));
                Debug.Log(Log.Tag + " Probe: map planner: destination " + wanted + (landOn ? " (the surface)" : "") + " set: "
                          + (landOn ? MapPlannerWindow.Instance.SetSurfaceDestination(wanted, CommandLine.Has("-redefinitionProbeMapPlannerAir"), CommandLine.Has("-redefinitionProbeMapPlannerOrbitFirst"))
                                    : MapPlannerWindow.Instance.SetDestination(wanted, double.Parse(CommandLine.After("-redefinitionProbeMapPlannerAltitude") ?? "30000"), CommandLine.Has("-redefinitionProbeMapPlannerAir"))));
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
                    while (MapPlannerWindow.Instance.Flying && Time.realtimeSinceStartup - started < 3600f)
                    {
                        if (ProbeWatch.Gone(FlightGlobals.ActiveVessel)) break;
                        string now = MapPlannerWindow.Instance.FlightStatus;
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
