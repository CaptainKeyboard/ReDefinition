using System.Collections;
using System.Globalization;
using System.IO;
using ReDefinition.Autopilot;
using ReDefinition.Core;
using ReDefinition.Orbits;
using UnityEngine;

namespace ReDefinition.Probe
{
    // -redefinitionProbeNodes, with the vessel in an orbit round Kerbin (-redefinitionProbeOrbit Kerbin): the node frame's axes against the
    // world's, then a transfer to each moon put in as a node, and what KSP's own plan makes of it.
    internal static partial class LightProbe
    {
        internal const string NodesArgument = "-redefinitionProbeNodes";

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

            foreach (string name in new[] { "Mun", "Minmus" })
            {
                CelestialBody target = FlightGlobals.Bodies.Find(b => b.bodyName == name);
                if (target == null) continue;
                NodePlanner.Result result = NodePlanner.TransferToBody(vessel, target, 50000.0);
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
