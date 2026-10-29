using System.Collections;
using System.IO;
using KSP.UI;
using ReDefinition.Core;
using ReDefinition.Engineer;
using UnityEngine;

namespace ReDefinition.Probe
{
    // -redefinitionProbeMapPlanner: the planner's panel in the map view, open, as a shot of the screen.
    internal static partial class LightProbe
    {
        internal const string MapPlannerArgument = "-redefinitionProbeMapPlanner";

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
                MapPlannerWindow.Instance.PlanNow();
                Debug.Log(Log.Tag + " Probe: map planner: " + MapPlannerWindow.Instance.Status);
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
