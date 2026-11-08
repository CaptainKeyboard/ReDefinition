using System.Collections;
using System.IO;
using ReDefinition.Autopilot.Flight;
using ReDefinition.Autopilot.Planning;
using ReDefinition.Core;
using ReDefinition.Engineer;
using UnityEngine;
using UnityEngine.UI;

namespace ReDefinition.Probe
{
    // The autopilot button's menu, pressed with the system's mouse. The left press opens it and the next puts it away. The right press opens it
    // where a route is set and offers to cancel the route. "Navigate to ..." opens the second list.
    // And the Auto Staging switch: off, MechJeb's staging controller is off, whoever else asked for it.
    internal static partial class LightProbe
    {
        private static IEnumerator MenuChecks(string folder, Vessel vessel, MapPlannerWindow window)
        {
            GameObject live = GameObject.Find("ReDefinitionLiveButton");
            if (live == null)
            {
                ProbeSteps.Failed("menu: the autopilot button is not on the screen.");
                yield break;
            }
            RectTransform rect = (RectTransform)live.transform;
            int copies = 0;
            foreach (Button each in Resources.FindObjectsOfTypeAll<Button>())
            {
                if (each == null || !each.gameObject.scene.IsValid() || each.name != "ReDefinitionLiveButton") continue;
                copies++;
                Rect where = ScreenRect((RectTransform)each.transform, new Vector3[4]);
                Debug.Log(Log.Tag + " Probe: menu: autopilot button " + copies + ": " + (each.gameObject.activeInHierarchy ? "active" : "inactive") + " at " + where.xMin.ToString("0") + ".." + where.xMax.ToString("0") + ", parent "
                          + (each.transform.parent != null ? each.transform.parent.name : "-") + ", behaviours " + each.GetComponents<MonoBehaviour>().Length + ".");
            }
            Debug.Log(Log.Tag + " Probe: menu: " + copies + " autopilot button(s) in the scene.");
            Debug.Log(Log.Tag + " Probe: menu: MechJeb can fly the vessel: " + MechJebDriver.CanFly(vessel) + " (" + (MechJebDriver.Unavailable ?? "no reason") + "), vessel " + vessel.vesselName
                      + ", " + vessel.situation + ".");
            window.ClearNow();
            yield return new WaitForSecondsRealtime(2f);
            ProbeMouse.Focus();
            ProbeMouse.Move(ProbeMouse.ScreenOf(rect));
            yield return new WaitForSecondsRealtime(3f);
            Debug.Log(Log.Tag + " Probe: menu: with the mouse over the button:");
            LogUnderButton(rect, 160f);
            ScreenCapture.CaptureScreenshot(Path.Combine(folder, "menu-tooltip.png"));
            yield return new WaitForSecondsRealtime(1f);

            // The left press with nothing to fly opens the menu; the next puts it away. The press is the button's own click: a mod's controls that
            // the game draws over the button's place in this vessel's top bar would take the system's mouse.
            Button live2 = live.GetComponent<Button>();
            live2.onClick.Invoke();
            yield return new WaitForSecondsRealtime(1.5f);
            bool opened = LiveMenu.IsOpen;
            ScreenCapture.CaptureScreenshot(Path.Combine(folder, "menu-left.png"));
            yield return new WaitForSecondsRealtime(1f);
            live2.onClick.Invoke();
            yield return new WaitForSecondsRealtime(1f);
            bool closed = !LiveMenu.IsOpen;
            Debug.Log(Log.Tag + " Probe: menu: left press opens: " + opened + ", the next press closes: " + closed + ".");
            if (!opened || !closed) ProbeSteps.Failed("menu: the left press does not open and close the menu.");

            // The right press opens it, "Navigate to ..." puts the second list up.
            yield return ProbeMouse.Click(true);
            yield return new WaitForSecondsRealtime(1.5f);
            Button navigate = ProbeMouse.ButtonWith("Navigate to ...");
            Debug.Log(Log.Tag + " Probe: menu: right press opens: " + LiveMenu.IsOpen + ", \"Navigate to ...\" is " + (navigate != null ? "there" : "missing") + ".");
            if (!LiveMenu.IsOpen || navigate == null) ProbeSteps.Failed("menu: the right press does not open the menu with \"Navigate to ...\".");
            else
            {
                navigate.onClick.Invoke();
                yield return new WaitForSecondsRealtime(1.5f);
                Debug.Log(Log.Tag + " Probe: menu: the second list is " + (ProbeMouse.ButtonWith("Back") != null ? "open" : "NOT open") + ".");
                if (ProbeMouse.ButtonWith("Back") == null) ProbeSteps.Failed("menu: \"Navigate to ...\" did not open the second list.");
                ScreenCapture.CaptureScreenshot(Path.Combine(folder, "menu-navigate.png"));
                yield return new WaitForSecondsRealtime(1f);
            }
            LiveMenu.Close();

            // With a route, the right press offers to cancel it.
            CelestialBody mun = FlightGlobals.Bodies.Find(b => b.bodyName == "Mun");
            window.PlanGoal(vessel, mun, false, NextStepPlanner.LowOrbit(mun), false);
            yield return new WaitForSecondsRealtime(10f);
            // The right press is what opened the menu above; here its call is made directly, as the mod's controls over the button's place take the mouse.
            LiveMenu.Open(vessel, true);
            yield return new WaitForSecondsRealtime(1.5f);
            int dialogs = 0;
            foreach (PopupDialog each in Object.FindObjectsOfType<PopupDialog>()) if (each != null && each.gameObject.activeInHierarchy) dialogs++;
            Debug.Log(Log.Tag + " Probe: menu: dialogs on the screen with the route's menu: " + dialogs + ".");
            Button cancel = ProbeMouse.ButtonWith("Cancel Route");
            ScreenCapture.CaptureScreenshot(Path.Combine(folder, "menu-route.png"));
            yield return new WaitForSecondsRealtime(1f);
            Debug.Log(Log.Tag + " Probe: menu: with a route, \"Cancel Route\" is " + (cancel != null ? "there" : "missing") + ".");
            if (cancel == null) ProbeSteps.Failed("menu: the right press with a route does not offer \"Cancel Route\".");
            else
            {
                cancel.onClick.Invoke();
                yield return new WaitForSecondsRealtime(3f);
                Debug.Log(Log.Tag + " Probe: menu: after \"Cancel Route\" the route " + (window.RouteIsEmpty ? "is empty" : "is NOT empty") + ".");
                if (!window.RouteIsEmpty) ProbeSteps.Failed("menu: \"Cancel Route\" did not empty the route.");
            }
            LiveMenu.Close();

            // The Auto Staging switch: with it off, the staging controller is off too.
            bool offEnabled = StagingEnabledAfter(vessel, false);
            bool onEnabled = StagingEnabledAfter(vessel, true);
            Debug.Log(Log.Tag + " Probe: menu: staging controller with Auto Staging off: " + offEnabled + ", on: " + onEnabled + ".");
            if (offEnabled || !onEnabled) ProbeSteps.Failed("menu: the Auto Staging switch does not switch MechJeb's staging controller (off: " + offEnabled + ", on: " + onEnabled + ").");

            // The planner's list of bodies offers the vessel's own place first.
            window.OpenNow();
            yield return new WaitForSecondsRealtime(2f);
            Button start = ProbeMouse.ButtonWith("Kerbin");
            if (start == null) ProbeSteps.Failed("menu: the start's body button is not in the planner.");
            else
            {
                start.onClick.Invoke();
                yield return new WaitForSecondsRealtime(1.5f);
                Button position = ProbeMouse.ButtonWith("Current Position");
                Debug.Log(Log.Tag + " Probe: menu: the list of bodies offers \"Current Position\": " + (position != null) + ".");
                if (position == null) ProbeSteps.Failed("menu: the list of bodies does not offer \"Current Position\".");
                ScreenCapture.CaptureScreenshot(Path.Combine(folder, "menu-body-list.png"));
                yield return new WaitForSecondsRealtime(1f);
                if (position != null)
                {
                    position.onClick.Invoke();
                    yield return new WaitForSecondsRealtime(2f);
                    ScreenCapture.CaptureScreenshot(Path.Combine(folder, "menu-body-picked.png"));
                    yield return new WaitForSecondsRealtime(1f);
                }
            }

            // The map view: the button stands where it covers nothing.
            MapView.EnterMapView();
            yield return new WaitForSecondsRealtime(4f);
            ScreenCapture.CaptureScreenshot(Path.Combine(folder, "menu-map.png"));
            yield return new WaitForSecondsRealtime(1f);
            MapView.ExitMapView();
            yield return new WaitForSecondsRealtime(2f);
        }

        // Every drawn control of the game whose place on the screen meets the button's: its name, its parents and the canvas, to see what the button covers.
        private static void LogUnderButton(RectTransform button, float extra = 0f)
        {
            var corners = new Vector3[4];
            Rect mine = ScreenRect(button, corners);
            mine.width += extra;
            int found = 0;
            foreach (UnityEngine.UI.Graphic graphic in Resources.FindObjectsOfTypeAll<UnityEngine.UI.Graphic>())
            {
                if (graphic == null || !graphic.gameObject.scene.IsValid() || !graphic.gameObject.activeInHierarchy || graphic.transform.IsChildOf(button)) continue;
                Rect other = ScreenRect(graphic.rectTransform, corners);
                if (!other.Overlaps(mine) || other.width > Screen.width * 0.3f || other.height > 200f) continue;
                string path = graphic.name;
                for (Transform up = graphic.transform.parent; up != null && path.Length < 160; up = up.parent) path = up.name + "/" + path;
                Debug.Log(Log.Tag + " Probe: menu: under the button: " + graphic.GetType().Name + " " + path + " " + other.xMin.ToString("0") + ".." + other.xMax.ToString("0")
                          + " raycast " + graphic.raycastTarget);
                if (++found >= 25) break;
            }
            Debug.Log(Log.Tag + " Probe: menu: the button is at " + mine.xMin.ToString("0") + ".." + mine.xMax.ToString("0") + ", " + found + " control(s) meet it.");
            // Whatever else is named for the action groups, folded away or not.
            int others = 0;
            foreach (RectTransform each in Resources.FindObjectsOfTypeAll<RectTransform>())
            {
                if (each == null || !each.gameObject.scene.IsValid() || !each.name.Contains("ActionGroup") || each.name.StartsWith("ButtonActionGroup") || others++ > 40) continue;
                string path = each.name;
                for (Transform up = each.parent; up != null && path.Length < 140; up = up.parent) path = up.name + "/" + path;
                Rect span = ScreenRect(each, corners);
                Debug.Log(Log.Tag + " Probe: menu: named for action groups: " + path + (each.gameObject.activeInHierarchy ? "" : " (inactive)") + " " + span.xMin.ToString("0") + ".." + span.xMax.ToString("0"));
            }
            // The group the game's action group buttons stand in, with what is folded away in it.
            foreach (UnityEngine.UI.Selectable each in Resources.FindObjectsOfTypeAll<UnityEngine.UI.Selectable>())
            {
                if (each == null || !each.gameObject.scene.IsValid() || each.name != "ButtonActionGroupLights" || each.transform.parent == null) continue;
                foreach (RectTransform child in each.transform.parent.GetComponentsInChildren<RectTransform>(true))
                {
                    Rect span = ScreenRect(child, corners);
                    Debug.Log(Log.Tag + " Probe: menu: group member " + child.name + (child.gameObject.activeInHierarchy ? "" : " (inactive)") + " " + span.xMin.ToString("0") + ".." + span.xMax.ToString("0"));
                }
                break;
            }
        }

        private static Rect ScreenRect(RectTransform rect, Vector3[] corners)
        {
            Canvas canvas = rect.GetComponentInParent<Canvas>();
            Camera camera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
            rect.GetWorldCorners(corners);
            Vector2 low = RectTransformUtility.WorldToScreenPoint(camera, corners[0]);
            Vector2 high = RectTransformUtility.WorldToScreenPoint(camera, corners[2]);
            return new Rect(low.x, low.y, high.x - low.x, high.y - low.y);
        }

        // Whether MechJeb's staging controller is enabled after the switch is set.
        private static bool StagingEnabledAfter(Vessel vessel, bool on)
        {
            MechJebDriver.SetSwitch(vessel, MechJebDriver.Switch.AutoStaging, on);
            object staging = MechJebDriver.Module(vessel, "Staging");
            System.Reflection.PropertyInfo enabled = staging != null ? staging.GetType().GetProperty("Enabled", TypeLookup.Any) : null;
            return enabled != null && (bool)enabled.GetValue(staging, null);
        }
    }
}
