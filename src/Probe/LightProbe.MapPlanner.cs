using System.Collections;
using System.IO;
using KSP.UI;
using ReDefinition.Autopilot;
using ReDefinition.Core;
using ReDefinition.Engineer;
using UnityEngine;

namespace ReDefinition.Probe
{
    // -redefinitionProbeMapPlanner: the planner's panel in the map view, open, as a shot of the screen.
    internal static partial class LightProbe
    {
        internal const string MapPlannerArgument = "-redefinitionProbeMapPlanner";

        // Whether the vessel was left and loaded again, so that the next run of the probe only watches what the autopilot does with it.
        private static bool ResumeDone;

        // The route set as the player sets it, the nodes kept up by the planner, and burns flown by MechJeb as if they were the player's: the
        // plan has to be made, and again after each burn.
        private static IEnumerator AutoNodesRun()
        {
            Vessel vessel = FlightGlobals.ActiveVessel;
            RouteDriver.Instance.SetAutonavigation(vessel, true);
            float started = Time.realtimeSinceStartup;
            string last = "";
            int burns = 0;
            while (Time.realtimeSinceStartup - started < 900f && burns < 2)
            {
                string now = RouteDriver.Instance.Status;
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

        // What the autopilot does with the vessel that was loaded again while it flew: the flight goes on, to the end.
        private static IEnumerator ResumedFlight()
        {
            float started = Time.realtimeSinceStartup;
            string last = "";
            while (Time.realtimeSinceStartup - started < 1200f)
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
            if (MapPlannerWindow.Instance != null && FlightGlobals.ActiveVessel.situation == Vessel.Situations.ORBITING)
            {
                string wanted = CommandLine.After("-redefinitionProbeMapPlannerTo") ?? "Minmus";
                Debug.Log(Log.Tag + " Probe: map planner: destination " + wanted + " set: " + MapPlannerWindow.Instance.SetDestination(wanted, 30000.0));
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
                    yield return AutoNodesRun();
                    Debug.Log(Log.Tag + " Probe: map planner done.");
                    yield break;
                }
                if (CommandLine.Has("-redefinitionProbeMapPlannerFly"))
                {
                    MapPlannerWindow.Instance.FlyNow();
                    float started = Time.realtimeSinceStartup;
                    string last = "";
                    bool shot = false;
                    while (MapPlannerWindow.Instance.Flying && Time.realtimeSinceStartup - started < 1500f)
                    {
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
