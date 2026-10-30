using System.Collections;
using System.IO;
using KSP.UI;
using ReDefinition.Autopilot;
using ReDefinition.Autopilot.Flight;
using ReDefinition.Autopilot.Guidance;
using ReDefinition.Autopilot.Planning;
using ReDefinition.Core;
using ReDefinition.Engineer;
using ReDefinition.Orbits;
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
            RouteDriver.Instance.SetNavigation(vessel, true);
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

        // The navigation from the pad to an orbit, and the probe as the player. The first stage is lit, the nose follows the marker, and the
        // throttle is the one the guidance asks for. A stage is fired where none is running. MechJeb burns the nodes that follow the climb.
        private static IEnumerator AutoClimbRun(string folder)
        {
            Vessel vessel = FlightGlobals.ActiveVessel;
            ProbeWatch.Arm(vessel);
            if (CommandLine.Has("-redefinitionProbeTrace")) FlightTrace.Begin(System.IO.Path.Combine(folder, "flight-trace.csv"));
            RouteDriver.Instance.SetNavigation(vessel, true);
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
    }
}
