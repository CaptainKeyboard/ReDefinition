using System.Collections;
using System.IO;
using KSP.UI;
using KSP.UI.Screens.Mapview;
using KSP.UI.Screens.Mapview.MapContextMenuOptions;
using ReDefinition.Autopilot.Flight;
using ReDefinition.Core;
using ReDefinition.Engineer;
using UnityEngine;

namespace ReDefinition.Probe
{
    // -redefinitionProbeDrive: a rover on the pad is sent to a place on the ground by the plan and the Live button.
    // -redefinitionProbeMeet, with -redefinitionProbeTarget: a vessel in orbit meets another. The map's menu of it sets the plan, the Live button flies it.
    internal static partial class LightProbe
    {
        private static IEnumerator DriveRun(string folder)
        {
            for (float waited = 0f; waited < 60f && (MapPlannerWindow.Instance == null || FlightGlobals.ActiveVessel == null || !FlightGlobals.ActiveVessel.LandedOrSplashed); waited += 1f)
                yield return new WaitForSeconds(1f);
            yield return new WaitForSeconds(10f);
            Vessel vessel = FlightGlobals.ActiveVessel;
            MapPlannerWindow window = MapPlannerWindow.Instance;
            if (vessel == null || window == null || !vessel.LandedOrSplashed)
            {
                ProbeSteps.Failed("drive: the planner window is " + (window != null ? "there" : "missing") + "; the vessel is " + (vessel != null ? vessel.situation.ToString() : "missing") + ".");
                yield break;
            }
            if (UIMasterController.Instance != null) UIMasterController.Instance.ShowUI();
            Debug.Log(Log.Tag + " Probe: drive: " + vessel.vesselName + " can drive: " + MechJebDriver.CanDrive(vessel) + ".");
            CelestialBody body = vessel.mainBody;
            // 450 m due west on the ground.
            double latitude = vessel.latitude;
            double longitude = vessel.longitude - 450.0 / (body.Radius * System.Math.Cos(latitude * System.Math.PI / 180.0)) * 180.0 / System.Math.PI;
            window.PlanGoal(vessel, body, true, 0.0, false, true, latitude, longitude);
            yield return new WaitForSeconds(10f);
            ReportPlan(window, "drive 450 m west");
            ScreenCapture.CaptureScreenshot(Path.Combine(folder, "drive-plan.png"));
            yield return ProbeSteps.Frames(10);
            RouteDriver.Instance.ToggleAutopilot(vessel);
            float started = Time.realtimeSinceStartup;
            yield return new WaitForSeconds(5f);
            while (RouteDriver.Instance.Flying && !ProbeWatch.Lost && Time.realtimeSinceStartup - started < 600f)
            {
                yield return new WaitForSeconds(10f);
                Vector3d site = body.GetWorldSurfacePosition(latitude, longitude, body.TerrainAltitude(latitude, longitude));
                Debug.Log(Log.Tag + " Probe: drive: " + ((Vector3d)vessel.GetWorldPos3D() - site).magnitude.ToString("0") + " m to the place, speed " + vessel.srfSpeed.ToString("0.0") + " m/s.");
            }
            ScreenCapture.CaptureScreenshot(Path.Combine(folder, "drive-end.png"));
            yield return ProbeSteps.Frames(10);
            Vector3d end = body.GetWorldSurfacePosition(latitude, longitude, body.TerrainAltitude(latitude, longitude));
            Debug.Log(Log.Tag + " Probe: drive: the drive ended (" + RouteDriver.Instance.Status + "): " + ((Vector3d)FlightGlobals.ActiveVessel.GetWorldPos3D() - end).magnitude.ToString("0") + " m from the place.");
            Debug.Log(Log.Tag + " Probe: drive done.");
        }

        private static IEnumerator MeetRun(string folder)
        {
            for (float waited = 0f; waited < 60f && (MapPlannerWindow.Instance == null || FlightGlobals.ActiveVessel == null || FlightGlobals.ActiveVessel.situation != Vessel.Situations.ORBITING); waited += 1f)
                yield return new WaitForSeconds(1f);
            yield return ProbeSteps.Frames(60);
            Vessel vessel = FlightGlobals.ActiveVessel;
            MapPlannerWindow window = MapPlannerWindow.Instance;
            Vessel target = FlightGlobals.Vessels.Find(v => v.vesselName == "ProbeTarget");
            if (vessel == null || window == null || target == null || vessel.situation != Vessel.Situations.ORBITING)
            {
                ProbeSteps.Failed("meet: the planner is " + (window != null ? "there" : "missing") + "; the target is " + (target != null ? "there" : "missing") + ".");
                yield break;
            }
            if (UIMasterController.Instance != null) UIMasterController.Instance.ShowUI();
            MapView.EnterMapView();
            yield return new WaitForSeconds(3f);
            // The map's menu of the other vessel, made as the game makes it for a click on its icon: the entries the planner adds.
            Debug.Log(Log.Tag + " Probe: meet: before the menu the target is " + (LiveMenu.IsMeetable(vessel, target) ? "meetable" : "not meetable") + ", with a docking port: " + LiveMenu.HasDockingPort(target) + ".");
            var hit = new OrbitRendererBase.OrbitCastHit { driver = target.orbitDriver, or = target.orbitDriver.Renderer, mouseTA = target.orbit.trueAnomaly };
            MapContextMenu menu = MapContextMenu.Create(target.GetDisplayName(), hit, () => { }, new MapContextMenuOption[0]);
            yield return new WaitForSeconds(2f);
            ScreenCapture.CaptureScreenshot(Path.Combine(folder, "meet-menu.png"));
            yield return ProbeSteps.Frames(10);
            menu.Dismiss();
            MapView.ExitMapView();
            yield return ProbeSteps.Frames(30);
            Debug.Log(Log.Tag + " Probe: meet: the target is " + target.situation + ", " + (LiveMenu.IsMeetable(vessel, target) ? "meetable" : "not meetable") + ", with a docking port: " + LiveMenu.HasDockingPort(target) + ".");
            bool dock = LiveMenu.HasDockingPort(target);
            LiveMenu.Meet(window, vessel, target, dock);
            yield return new WaitForSeconds(10f);
            ReportPlan(window, dock ? "dock with the target" : "meet the target");
            ScreenCapture.CaptureScreenshot(Path.Combine(folder, "meet-plan.png"));
            yield return ProbeSteps.Frames(10);
            RouteDriver.Instance.ToggleAutopilot(vessel);
            float started = Time.realtimeSinceStartup;
            yield return new WaitForSeconds(5f);
            while (RouteDriver.Instance.Flying && !ProbeWatch.Lost && Time.realtimeSinceStartup - started < 1200f)
            {
                yield return new WaitForSeconds(15f);
                Vessel flown = FlightGlobals.ActiveVessel;
                Debug.Log(Log.Tag + " Probe: meet: " + (flown.GetWorldPos3D() - target.GetWorldPos3D()).magnitude.ToString("0") + " m to the target (" + RouteDriver.Instance.Status + ").");
            }
            ScreenCapture.CaptureScreenshot(Path.Combine(folder, "meet-end.png"));
            yield return ProbeSteps.Frames(10);
            Debug.Log(Log.Tag + " Probe: meet: the flight ended (" + RouteDriver.Instance.Status + "): " + (FlightGlobals.ActiveVessel.GetWorldPos3D() - target.GetWorldPos3D()).magnitude.ToString("0") + " m from the target.");
            Debug.Log(Log.Tag + " Probe: meet done.");
        }
    }
}
