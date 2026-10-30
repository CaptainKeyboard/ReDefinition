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

        // The route set as the player sets it, the nodes kept up by the planner, and burns flown by MechJeb as if they were the player's: the
        // plan has to be made, and again after each burn.
        private static IEnumerator AutoNodesRun()
        {
            Vessel vessel = FlightGlobals.ActiveVessel;
            ProbeWatch.Arm(vessel);
            RouteDriver.Instance.SetAutonavigation(vessel, true);
            float started = Time.realtimeSinceStartup;
            string last = "";
            int burns = 0;
            while (Time.realtimeSinceStartup - started < 900f && burns < 2 && !ProbeWatch.Gone(vessel))
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

        // The autonavigation on a route to a surface, and the probe as the player: the deorbit node is flown, then the vessel is turned
        // retrograde and the throttle opened where the guidance says to brake, and shut near the ground. The guidance is logged as it changes.
        private static IEnumerator AutoLandRun(string folder)
        {
            Vessel vessel = FlightGlobals.ActiveVessel;
            ProbeWatch.Arm(vessel);
            if (CommandLine.Has("-redefinitionProbeTrace")) FlightTrace.Begin(System.IO.Path.Combine(folder, "flight-trace.csv"));
            RouteDriver.Instance.SetAutonavigation(vessel, true);
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
                    Debug.Log(Log.Tag + " Probe: auto land: " + now + " [" + vessel.situation + ", " + vessel.altitude.ToString("0") + " m, " + vessel.srfSpeed.ToString("0") + " m/s]");
                    last = now;
                    nextLine = Time.realtimeSinceStartup + 10f;
                }
                PatchedConicSolver solver = vessel.patchedConicSolver;
                if (!deorbited && solver.maneuverNodes.Count > 0 && !MechJebDriver.Busy(vessel))
                {
                    deorbited = true;
                    Debug.Log(Log.Tag + " Probe: auto land: the player flies the deorbit burn, " + solver.maneuverNodes[0].DeltaV.magnitude.ToString("0.0") + " m/s.");
                    if (!Engines.Running(vessel)) Engines.FireNextStageIfCold(vessel);
                    MechJebDriver.ExecuteNode(vessel, true);
                    while (MechJebDriver.Busy(vessel) && !ProbeWatch.Gone(vessel)) yield return new WaitForSeconds(0.5f);
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
            Debug.Log(Log.Tag + " Probe: auto land: ended " + (ProbeWatch.Lost ? "LOST (" + ProbeWatch.Why + ")" : vessel.situation + ", " + vessel.srfSpeed.ToString("0.0") + " m/s") + ", "
                      + RouteDriver.Instance.Status);
        }

        // The pilot as the player is: the nose is turned steadily to the marker, 40 degrees a second at most, and the throttle is the one the
        // gauge asks for, set again in every frame.
        private static void StandIn(Vessel vessel, double throttle)
        {
            if (GuidanceBoard.Current(vessel) && GuidanceBoard.HasDirection)
            {
                Quaternion want = Quaternion.FromToRotation(vessel.GetTransform().up, (Vector3)GuidanceBoard.Direction) * vessel.GetTransform().rotation;
                vessel.SetRotation(Quaternion.RotateTowards(vessel.GetTransform().rotation, want, 40f * Time.deltaTime));
                foreach (Part part in vessel.parts)
                    if (part.Rigidbody != null) part.Rigidbody.angularVelocity = Vector3.zero;
            }
            FlightInputHandler.state.mainThrottle = (float)throttle;
        }

        // The autonavigation from the pad to an orbit, and the probe as the player. The first stage is lit, the nose follows the marker, and the
        // throttle is the one the guidance asks for. A stage is fired where none is running. MechJeb burns the nodes that follow the climb.
        private static IEnumerator AutoClimbRun(string folder)
        {
            Vessel vessel = FlightGlobals.ActiveVessel;
            ProbeWatch.Arm(vessel);
            if (CommandLine.Has("-redefinitionProbeTrace")) FlightTrace.Begin(System.IO.Path.Combine(folder, "flight-trace.csv"));
            RouteDriver.Instance.SetAutonavigation(vessel, true);
            MapView.ExitMapView();
            if (UIMasterController.Instance != null) UIMasterController.Instance.ShowUI();
            float started = Time.realtimeSinceStartup;
            string last = "";
            float nextLine = 0f;
            float nextShot = 5f;
            int shots = 0;
            bool burned = false;
            while (Time.realtimeSinceStartup - started < 1500f && !ProbeWatch.Gone(vessel))
            {
                string now = RouteDriver.Instance.Status;
                if (now != last && Time.realtimeSinceStartup > nextLine)
                {
                    Debug.Log(Log.Tag + " Probe: auto climb: " + now + " [" + vessel.situation + ", " + vessel.altitude.ToString("0") + " m, " + vessel.srfSpeed.ToString("0") + " m/s, apoapsis "
                              + vessel.orbit.ApA.ToString("0") + " m]");
                    last = now;
                    nextLine = Time.realtimeSinceStartup + 5f;
                }
                PatchedConicSolver solver = vessel.patchedConicSolver;
                if (now.StartsWith("Climb"))
                {
                    if (GuidanceBoard.Current(vessel) && GuidanceBoard.HasThrottle && GuidanceBoard.Throttle > 0.05 && !Engines.Running(vessel)) Engines.FireNextStageIfCold(vessel);
                    StandIn(vessel, GuidanceBoard.Current(vessel) && GuidanceBoard.HasThrottle ? GuidanceBoard.Throttle : 0.0);
                    if (shots < 4 && Time.realtimeSinceStartup - started > nextShot)
                    {
                        nextShot += 25f;
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
