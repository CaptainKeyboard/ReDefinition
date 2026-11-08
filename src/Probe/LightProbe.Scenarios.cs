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
            if (CommandLine.Has("-redefinitionProbeStuck")) HoldBrakes(vessel);
            RouteDriver.Instance.ToggleAutopilot(vessel);
            float started = Time.realtimeSinceStartup;
            yield return new WaitForSeconds(5f);
            while (RouteDriver.Instance.Flying && !ProbeWatch.Lost && Time.realtimeSinceStartup - started < 600f)
            {
                // -redefinitionProbeStuck: the brakes are held on, so that the rover cannot get to the place and its drive has to be given up and tried again.
                if (CommandLine.Has("-redefinitionProbeStuck")) HoldBrakes(vessel);
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

        // Every wheel's brake at its full and its motor off, the vessel's brake group on.
        private static void HoldBrakes(Vessel vessel)
        {
            vessel.ActionGroups.SetGroup(KSPActionGroup.Brakes, true);
            foreach (Part part in vessel.parts)
            {
                foreach (ModuleWheels.ModuleWheelBrakes brake in part.FindModulesImplementing<ModuleWheels.ModuleWheelBrakes>())
                    brake.brakeInput = 1f;
                foreach (ModuleWheels.ModuleWheelMotor motor in part.FindModulesImplementing<ModuleWheels.ModuleWheelMotor>())
                    motor.motorEnabled = false;
            }
        }

        // -redefinitionProbeClick: the icons of an eccentric orbit are clicked with the system's mouse, and the menus the planner gives them are shot.
        private static IEnumerator ClickRun(string folder)
        {
            for (float waited = 0f; waited < 60f && (MapPlannerWindow.Instance == null || FlightGlobals.ActiveVessel == null || FlightGlobals.ActiveVessel.situation != Vessel.Situations.ORBITING); waited += 1f)
                yield return new WaitForSeconds(1f);
            yield return ProbeSteps.Frames(60);
            Vessel vessel = FlightGlobals.ActiveVessel;
            if (vessel == null || MapPlannerWindow.Instance == null || vessel.situation != Vessel.Situations.ORBITING)
            {
                ProbeSteps.Failed("click: no vessel in an orbit or no planner.");
                yield break;
            }
            if (UIMasterController.Instance != null) UIMasterController.Instance.ShowUI();
            // With a target the map draws the nodes where the orbits cross.
            Vessel other = FlightGlobals.Vessels.Find(v => v.vesselName == "ProbeTarget");
            if (other != null) FlightGlobals.fetch.SetVesselTarget(other);
            MapView.EnterMapView();
            yield return new WaitForSeconds(4f);
            if (vessel.mapObject != null) PlanetariumCamera.fetch.SetTarget(vessel.mapObject);
            yield return new WaitForSeconds(3f);
            yield return MarkerMenuShot(folder, vessel);
            if (other != null)
            {
                // The plane of the target as the stop's goal: the planner's budget shows the turn.
                MapView.ExitMapView();
                yield return new WaitForSeconds(3f);
                MapPlannerWindow.Instance.PlanGoal(vessel, vessel.mainBody, false, System.Math.Max(1000.0, 0.5 * (vessel.orbit.PeA + vessel.orbit.ApA)), false,
                    shape: new MapPlannerWindow.Shape { MatchPlane = other });
                yield return new WaitForSeconds(8f);
                ReportPlan(MapPlannerWindow.Instance, "match the plane of " + other.vesselName + " (target inclination " + other.orbit.inclination.ToString("0.0") + ")");
                ScreenCapture.CaptureScreenshot(Path.Combine(folder, "click-plane-budget.png"));
                yield return ProbeSteps.Frames(10);
                yield return StackButtonClicks(vessel);
                // The plan is flown: the plane is turned to the target's, then the vessel meets it.
                RouteDriver.Instance.ToggleAutopilot(vessel);
                float flownFrom = Time.realtimeSinceStartup;
                yield return new WaitForSeconds(5f);
                while (RouteDriver.Instance.Flying && !ProbeWatch.Lost && Time.realtimeSinceStartup - flownFrom < 1000f)
                {
                    yield return new WaitForSeconds(15f);
                    Debug.Log(Log.Tag + " Probe: click: flying, inclination " + vessel.orbit.inclination.ToString("0.00") + ", angle to the target's plane "
                              + Vector3d.Angle(vessel.orbit.GetOrbitNormal(), other.orbit.GetOrbitNormal()).ToString("0.00") + ", " + RouteDriver.Instance.Status + ".");
                }
                Debug.Log(Log.Tag + " Probe: click: the flight ended (" + RouteDriver.Instance.Status + "; flying: " + RouteDriver.Instance.Flying + ", " + ((Vector3d)vessel.GetWorldPos3D() - (Vector3d)other.GetWorldPos3D()).magnitude.ToString("0") + " m from the target), the angle to the target's plane "
                          + Vector3d.Angle(vessel.orbit.GetOrbitNormal(), other.orbit.GetOrbitNormal()).ToString("0.00") + " degrees.");
            }
            Debug.Log(Log.Tag + " Probe: click done.");
        }

        // The Auto Staging button of the staging stack, pressed twice with the system's mouse: MechJeb's switch is read before and after each press.
        private static IEnumerator StackButtonClicks(Vessel vessel)
        {
            Transform found = null;
            foreach (UnityEngine.UI.Button each in Resources.FindObjectsOfTypeAll<UnityEngine.UI.Button>())
                if (each != null && each.name == "ReDefinitionAutoStaging" && each.gameObject.activeInHierarchy) found = each.transform;
            if (found == null)
            {
                ProbeSteps.Failed("click: the Auto Staging button is not in the stack.");
                yield break;
            }
            bool before;
            MechJebDriver.GetSwitch(vessel, MechJebDriver.Switch.AutoStaging, out before);
            ProbeMouse.Focus();
            for (int press = 1; press <= 2; press++)
            {
                ProbeMouse.Move(ProbeMouse.ScreenOf((RectTransform)found));
                yield return new WaitForSecondsRealtime(0.5f);
                yield return ProbeMouse.Click();
                yield return new WaitForSecondsRealtime(0.6f);
                bool after;
                MechJebDriver.GetSwitch(vessel, MechJebDriver.Switch.AutoStaging, out after);
                Debug.Log(Log.Tag + " Probe: click: Auto Staging button, press " + press + ": " + before + " -> " + after + (after != before ? " (switched)." : " (NOT switched)."));
                if (after == before) ProbeSteps.Failed("click: the Auto Staging button did not switch MechJeb.");
                before = after;
            }
        }

        private static int portShots;

        // Where the vessel's ports and the target's stand, as the coupling needs them: apart by less than half a metre, facing each other.
        private static IEnumerator PortsShot(string folder, Vessel flown, Vessel target)
        {
            foreach (Part own in flown.parts)
            {
                ModuleDockingNode port = own.FindModuleImplementing<ModuleDockingNode>();
                if (port == null) continue;
                foreach (Part there in target.parts)
                {
                    ModuleDockingNode other = there.FindModuleImplementing<ModuleDockingNode>();
                    if (other == null) continue;
                    Debug.Log(Log.Tag + " Probe: meet: ports " + port.state + " and " + other.state + " are " + (port.nodeTransform.position - other.nodeTransform.position).magnitude.ToString("0.00")
                              + " m apart, facing at " + Vector3.Angle(port.nodeTransform.forward, other.nodeTransform.forward).ToString("0") + " degrees, acquire range " + port.acquireRange + ", angle " + port.acquireMinFwdDot + ".");
                }
            }
            if (portShots++ < 2)
            {
                ScreenCapture.CaptureScreenshot(Path.Combine(folder, "meet-ports" + portShots + ".png"));
                yield return ProbeSteps.Frames(10);
            }
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
            foreach (Part part in vessel.parts)
            {
                ModuleDockingNode port = part.FindModuleImplementing<ModuleDockingNode>();
                if (port != null) Debug.Log(Log.Tag + " Probe: meet: own port " + part.partInfo.name + " is " + port.state + ", type " + port.nodeType + ", gendered " + port.gendered + "/" + port.genderFemale + ".");
                foreach (ModuleRCS rcs in part.FindModulesImplementing<ModuleRCS>())
                    Debug.Log(Log.Tag + " Probe: meet: RCS " + part.partInfo.name + " enabled " + rcs.rcsEnabled + ", flameout " + rcs.flameout + ", usable " + rcs.isEnabled + ".");
            }
            foreach (Part part in target.parts)
            {
                ModuleDockingNode port = part.FindModuleImplementing<ModuleDockingNode>();
                if (port != null) Debug.Log(Log.Tag + " Probe: meet: target port is " + port.state + ", type " + port.nodeType + ", gendered " + port.gendered + "/" + port.genderFemale + ".");
            }
            double mono = 0.0;
            foreach (Part part in vessel.parts)
                foreach (PartResource resource in part.Resources)
                    if (resource.resourceName == "MonoPropellant") mono += resource.amount;
            Debug.Log(Log.Tag + " Probe: meet: the vessel carries " + mono.ToString("0") + " units of monopropellant, mass " + vessel.GetTotalMass().ToString("0.0") + " t.");
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
                Debug.Log(Log.Tag + " Probe: meet: " + (flown.GetWorldPos3D() - target.GetWorldPos3D()).magnitude.ToString("0") + " m to the target (" + RouteDriver.Instance.Status + "), RCS "
                          + (flown.ActionGroups[KSPActionGroup.RCS] ? "on" : "off") + ", " + flown.parts.Count + " parts, target " + target.parts.Count + "; docking: " + MechJebDriver.DockingState(flown) + ".");
                if ((flown.GetWorldPos3D() - target.GetWorldPos3D()).magnitude < 8.0) yield return PortsShot(folder, flown, target);
            }
            ScreenCapture.CaptureScreenshot(Path.Combine(folder, "meet-end.png"));
            yield return ProbeSteps.Frames(10);
            Debug.Log(Log.Tag + " Probe: meet: the flight ended (" + RouteDriver.Instance.Status + "): " + (FlightGlobals.ActiveVessel.GetWorldPos3D() - target.GetWorldPos3D()).magnitude.ToString("0") + " m from the target.");
            Debug.Log(Log.Tag + " Probe: meet done.");
        }
    }
}
