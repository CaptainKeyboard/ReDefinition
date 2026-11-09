#if REDEFINITION_PLANNER_PROBES
using System.Collections;
using System.Globalization;
using System.IO;
using ReDefinition.Planner.Autopilot;
using ReDefinition.Planner.Autopilot.Flight;
using ReDefinition.Planner.Autopilot.Planning;
using ReDefinition.Core;
using ReDefinition.Planner.Orbits;
using UnityEngine;

namespace ReDefinition.Probe
{
    // -redefinitionProbeNodes, with the vessel in an orbit round Kerbin (-redefinitionProbeOrbit Kerbin): the node frame's axes against the
    // world's, then a transfer to each moon put in as a node, and what KSP's own plan makes of it.
    internal static partial class LightProbe
    {
        internal const string NodesArgument = "-redefinitionProbeNodes";

        // -redefinitionProbeFly <body>: the transfer to that body put in as a node and handed to MechJeb, watched until it is done, and the
        // plan KSP shows afterwards.
        private static IEnumerator FlyShots(string folder, string bodyName)
        {
            yield return ProbeSteps.Frames(60);
            Vessel vessel = FlightGlobals.ActiveVessel;
            var culture = CultureInfo.InvariantCulture;
            Debug.Log(Log.Tag + " Probe: fly: " + vessel.vesselName + ", " + vessel.parts.Count + " parts, " + vessel.GetTotalMass().ToString("0.00", culture)
                      + " t, MechJeb " + (MechJebDriver.Available ? "available" : "not available: " + MechJebDriver.Unavailable)
                      + ", can fly " + MechJebDriver.CanFly(vessel) + ".");
            CelestialBody target = FlightGlobals.Bodies.Find(b => b.bodyName == bodyName);
            NodePlanner.Result result = NodePlanner.TransferToBody(new Frame(vessel), target, 50000.0);
            Debug.Log(Log.Tag + " Probe: fly: plan " + (result.Ok ? "ok, " + result.DeltaV.Length.ToString("0.0", culture) + " m/s, in " + (result.UT - Planetarium.GetUniversalTime()).ToString("0", culture) + " s. " : "failed. ") + result.Why);
            if (!result.Ok || !MechJebDriver.ExecuteNode(vessel, true))
            {
                Debug.Log(Log.Tag + " Probe: fly done.");
                yield break;
            }
            float started = Time.realtimeSinceStartup;
            string last = "";
            float nextLine = 0f, nextStage = 0f;
            while (MechJebDriver.Busy(vessel) && Time.realtimeSinceStartup - started < 900f)
            {
                string state = MechJebDriver.State(vessel);
                if ((state == "LEAD" || state == "BURN") && Time.realtimeSinceStartup > nextStage)
                {
                    nextStage = Time.realtimeSinceStartup + 2f;
                    if (!Engines.Running(vessel)) Debug.Log(Log.Tag + " Probe: fly: no engine runs, the next stage is fired: " + Engines.FireNextStageIfCold(vessel) + ".");
                }
                if (state != last || Time.realtimeSinceStartup > nextLine)
                {
                    Debug.Log(Log.Tag + " Probe: fly: " + state + ", to the burn " + (result.UT - Planetarium.GetUniversalTime()).ToString("0", culture) + " s, warp " + TimeWarp.CurrentRateIndex
                              + ", nodes " + vessel.patchedConicSolver.maneuverNodes.Count + ", engine " + Engines.Running(vessel) + ", orbit " + vessel.orbit.PeA.ToString("0", culture) + " x " + vessel.orbit.ApA.ToString("0", culture) + " m.");
                    last = state;
                    nextLine = Time.realtimeSinceStartup + 10f;
                }
                yield return ProbeSteps.Frames(10);
            }
            Debug.Log(Log.Tag + " Probe: fly: finished after " + (Time.realtimeSinceStartup - started).ToString("0", culture) + " s, " + (MechJebDriver.Busy(vessel) ? "still busy" : "idle")
                      + ", orbit " + vessel.orbit.PeA.ToString("0", culture) + " x " + vessel.orbit.ApA.ToString("0", culture) + " m round " + vessel.mainBody.bodyName + ".");
            vessel.patchedConicSolver.UpdateFlightPlan();
            foreach (Orbit patch in vessel.patchedConicSolver.flightPlan)
                Debug.Log(Log.Tag + " Probe: fly:   patch round " + patch.referenceBody.bodyName + ", periapsis " + patch.PeA.ToString("0", culture) + " m.");
            Orbit now = vessel.orbit;
            while (now != null && now.nextPatch != null && now.patchEndTransition != Orbit.PatchTransitionType.FINAL)
            {
                now = now.nextPatch;
                Debug.Log(Log.Tag + " Probe: fly:   next patch round " + now.referenceBody.bodyName + ", periapsis " + now.PeA.ToString("0", culture) + " m.");
            }
            MapView.EnterMapView();
            yield return ProbeSteps.Frames(90);
            UnityEngine.ScreenCapture.CaptureScreenshot(Path.Combine(folder, "fly-" + bodyName + ".png"));
            yield return ProbeSteps.Frames(5);
            MapView.ExitMapView();
            Debug.Log(Log.Tag + " Probe: fly done.");
        }

        // -redefinitionProbeFlyRoute <body>: the route from where the vessel is, the pad or an orbit, to an orbit round that body (50 km, or
        // -redefinitionProbeRouteAltitude) or, with -redefinitionProbeRouteSurface, its surface. AutopilotFlight flies it with MechJeb, and the
        // status is logged as it changes, and where the vessel ends.
        private static IEnumerator FlyRouteShots(string folder, string bodyName)
        {
            yield return ProbeSteps.Frames(60);
            Vessel vessel = FlightGlobals.ActiveVessel;
            var culture = CultureInfo.InvariantCulture;
            CelestialBody target = FlightGlobals.Bodies.Find(b => b.bodyName == bodyName);
            bool onGround = vessel.LandedOrSplashed || vessel.situation == Vessel.Situations.PRELAUNCH;
            double altitude = CommandLine.Int("-redefinitionProbeRouteAltitude", 50000);
            var route = new System.Collections.Generic.List<Destination>
            {
                new Destination { Body = vessel.mainBody, Surface = onGround, Altitude = onGround ? 0.0 : vessel.orbit.PeA },
                new Destination { Body = target, Surface = CommandLine.Has("-redefinitionProbeRouteSurface"), Altitude = altitude, Aerobrake = CommandLine.Has("-redefinitionProbeRouteAerobrake") },
            };
            if (CommandLine.Has("-redefinitionProbeTrace")) FlightTrace.Begin(Path.Combine(folder, "flight-trace.csv"));
            var flight = new AutopilotFlight(vessel, route);
            IEnumerator run = flight.RunSafely();
            float started = Time.realtimeSinceStartup;
            string last = "";
            float nextLine = 0f;
            ProbeWatch.Arm(vessel);
            bool airCheck = CommandLine.Has("-redefinitionProbeRouteAerobrake");
            bool verdict = true;
            float waitingSince = -1f;
            while (!flight.Finished && Time.realtimeSinceStartup - started < 1500f)
            {
                if (ProbeWatch.Gone(vessel))
                {
                    flight.Cancel();
                    break;
                }
                // With -redefinitionProbeRouteAerobrake the run ends on the question whether the vessel is warped to the periapsis: the status says it
                // waits for it with minutes to go, and some warp has to be seen within 90 s.
                if (airCheck && flight.Status.StartsWith("Aerobrake: waiting") && vessel.orbit.timeToPe > 180.0)
                {
                    if (waitingSince < 0f) waitingSince = Time.realtimeSinceStartup;
                    if (TimeWarp.CurrentRateIndex > 0)
                    {
                        Debug.Log(Log.Tag + " Probe: aerobrake check: OK, the vessel is warped (rate " + TimeWarp.CurrentRateIndex + ") with " + vessel.orbit.timeToPe.ToString("0", culture) + " s to the periapsis.");
                        flight.Cancel();
                        verdict = false;
                        break;
                    }
                    if (Time.realtimeSinceStartup - waitingSince > 90f)
                    {
                        Debug.LogError(Log.Tag + " Probe: FAILED: aerobrake: no time warp in 90 s of waiting for the periapsis, " + vessel.orbit.timeToPe.ToString("0", culture) + " s away.");
                        flight.Cancel();
                        verdict = false;
                        break;
                    }
                }
                if (!run.MoveNext()) break;
                if (flight.Status != last || Time.realtimeSinceStartup > nextLine)
                {
                    Debug.Log(Log.Tag + " Probe: route: " + flight.Status + " [" + vessel.mainBody.bodyName + ", " + vessel.orbit.PeA.ToString("0", culture) + " x " + vessel.orbit.ApA.ToString("0", culture) + " m, warp " + TimeWarp.CurrentRateIndex + "]");
                    last = flight.Status;
                    nextLine = Time.realtimeSinceStartup + 15f;
                }
                yield return run.Current;
            }
            Debug.Log(Log.Tag + " Probe: route: ended, arrived " + flight.Arrived + ", " + flight.Status + " In " + vessel.mainBody.bodyName + " at " + vessel.orbit.PeA.ToString("0", culture) + " x " + vessel.orbit.ApA.ToString("0", culture) + " m.");
            if (verdict && airCheck && !flight.Status.StartsWith("Aerobrake"))
                Debug.LogError(Log.Tag + " Probe: FAILED: aerobrake: the flight never waited for the periapsis in the air (" + flight.Status + ").");
            else if (verdict) RouteVerdict(flight);
            MapView.EnterMapView();
            yield return ProbeSteps.Frames(90);
            UnityEngine.ScreenCapture.CaptureScreenshot(Path.Combine(folder, "route-" + bodyName + ".png"));
            yield return ProbeSteps.Frames(5);
            MapView.ExitMapView();
            Debug.Log(Log.Tag + " Probe: route done.");
        }

        // The flight against what the route may take. The switches name the most burns of the transfer and its corrections together
        // (-redefinitionProbeBudgetEncounter), the most burns in all (-redefinitionProbeBudgetTotal) and the most the vessel may turn while it coasts,
        // in thousandths of rad/s, 20 without (-redefinitionProbeBudgetSpinMilli). The probe says FAILED itself where one is not kept.
        private static void RouteVerdict(AutopilotFlight flight)
        {
            var culture = CultureInfo.InvariantCulture;
            int encounter = CommandLine.Int("-redefinitionProbeBudgetEncounter", int.MaxValue);
            int total = CommandLine.Int("-redefinitionProbeBudgetTotal", int.MaxValue);
            double spin = CommandLine.Int("-redefinitionProbeBudgetSpinMilli", 20) / 1000.0;
            foreach (string line in flight.Ledger.Lines) Debug.Log(Log.Tag + " Probe: route: " + line);
            Debug.Log(Log.Tag + " Probe: route: " + flight.Ledger.Summary());
            var failures = new System.Collections.Generic.List<string>();
            if (!flight.Arrived) failures.Add("the flight did not arrive (" + flight.Status + ")");
            if (flight.Ledger.UntilEncounter > encounter) failures.Add(flight.Ledger.UntilEncounter + " burns until the encounter, at most " + encounter);
            if (flight.Ledger.Total > total) failures.Add(flight.Ledger.Total + " burns in all, at most " + total);
            if (flight.Ledger.CoastRate > spin) failures.Add("the vessel turned at " + flight.Ledger.CoastRate.ToString("0.000", culture) + " rad/s while coasting, at most " + spin.ToString("0.000", culture));
            if (failures.Count > 0) Debug.LogError(Log.Tag + " Probe: FAILED: route: " + string.Join("; ", failures.ToArray()) + ".");
            else Debug.Log(Log.Tag + " Probe: route check: OK, " + flight.Ledger.Summary());
        }

        private static IEnumerator NodeShots(string folder)
        {
            yield return ProbeSteps.Frames(60);
            Vessel vessel = FlightGlobals.ActiveVessel;
            var culture = CultureInfo.InvariantCulture;
            Orbit orbit = vessel.orbit;
            Debug.Log(Log.Tag + " Probe: nodes: " + vessel.vesselName + " round " + vessel.mainBody.bodyName + ", " + vessel.situation + ", periapsis "
                      + orbit.PeA.ToString("0", culture) + " m, apoapsis " + orbit.ApA.ToString("0", culture) + " m, period " + orbit.period.ToString("0", culture) + " s.");
            KspOrbits.ClearNodes(vessel);
            double ut = Planetarium.GetUniversalTime() + 200.0;
            State at = KspOrbits.StateAt(orbit, ut);
            string[] axes = { "radial", "normal", "prograde" };
            V3[] expected = { at.R.Unit, -V3.Cross(at.R, at.V).Unit, at.V.Unit };
            for (int i = 0; i < 3; i++)
            {
                ManeuverNode node = vessel.patchedConicSolver.AddManeuverNode(ut);
                node.DeltaV = new Vector3d(i == 0 ? 1.0 : 0.0, i == 1 ? 1.0 : 0.0, i == 2 ? 1.0 : 0.0);
                vessel.patchedConicSolver.UpdateFlightPlan();
                V3 burn = KspOrbits.ToV3(node.GetBurnVector(orbit)).Unit;
                Debug.Log(Log.Tag + " Probe: nodes: the node's " + axes[i] + " axis, dotted with the world's as taken: " + V3.Dot(burn, expected[i]).ToString("0.0000", culture) + ".");
                vessel.patchedConicSolver.RemoveManeuverNode(node);
            }
            vessel.patchedConicSolver.UpdateFlightPlan();

            foreach (string name in new[] { "Mun", "Minmus", "Ike" })
            {
                CelestialBody target = FlightGlobals.Bodies.Find(b => b.bodyName == name);
                if (target == null) continue;
                NodePlanner.Result result = NodePlanner.TransferToBody(new Frame(vessel), target, 50000.0);
                string text = result.Ok
                    ? "ok, leaves in " + (result.UT - Planetarium.GetUniversalTime()).ToString("0", culture) + " s, " + result.DeltaV.Length.ToString("0.0", culture) + " m/s. " + result.Why
                    : "no: " + result.Why;
                Debug.Log(Log.Tag + " Probe: nodes: transfer to " + name + ": " + text);
                if (result.Ok)
                {
                    foreach (Orbit patch in vessel.patchedConicSolver.flightPlan)
                        Debug.Log(Log.Tag + " Probe: nodes:   patch round " + patch.referenceBody.bodyName + ", periapsis " + patch.PeA.ToString("0", culture)
                                  + " m, eccentricity " + patch.eccentricity.ToString("0.000", culture) + ", from " + (patch.StartUT - Planetarium.GetUniversalTime()).ToString("0", culture)
                                  + " s to " + (patch.EndUT - Planetarium.GetUniversalTime()).ToString("0", culture) + " s.");
                    MapView.EnterMapView();
                    yield return ProbeSteps.Frames(90);
                    UnityEngine.ScreenCapture.CaptureScreenshot(Path.Combine(folder, "nodes-" + name + ".png"));
                    yield return ProbeSteps.Frames(5);
                    MapView.ExitMapView();
                    yield return ProbeSteps.Frames(30);
                }
                KspOrbits.ClearNodes(vessel);
            }
            Debug.Log(Log.Tag + " Probe: nodes done.");
        }
    }
}
#endif
