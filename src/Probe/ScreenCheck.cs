using System;
using System.Collections;
using System.Globalization;
using System.Runtime.InteropServices;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Probe
{
    // -redefinitionProbeScreenCheck, with the scene probe's flight: whether the screen's
    // pixels hold for the cameras the upscaler draws into its texture
    // (Upscaler/ScreenCoordinates), then KSP quits.
    // Check 1 compares each part's screen point with its viewport point times the screen's size.
    // It also takes the ray back through it and the world point back from it.
    // Check 2 puts the cursor onto the middle of each part, up to twelve, and compares
    // the part KSP finds under it (Mouse.HoveredPart) with the part a ray
    // through the viewport point meets.
    // Check 3 compares, in the map view, KSP's map markers and the map camera's screen points
    // with their viewport points times the screen's size.
    // A last check reads Camera.current in Update, LateUpdate, OnGUI and at the end of the frame,
    // with the parts' screen points measured in OnGUI.
    // Each logs a line "Probe: screen check".
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    internal sealed class ScreenCheck : MonoBehaviour
    {
        // The command-line switch that runs the check.
        internal const string Argument = "-redefinitionProbeScreenCheck";

        // The window that has the keyboard focus: KSP's, for its client area's place on the desktop.
        [DllImport("user32.dll")] private static extern IntPtr GetActiveWindow();
        // A point in a window's client area turned into desktop coordinates.
        [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window, ref Point point);
        // Puts the cursor onto a desktop point.
        [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);

        // Windows' POINT, which ClientToScreen fills.
        [StructLayout(LayoutKind.Sequential)]
        private struct Point
        {
            // The point's coordinates in pixels.
            public int X, Y;
        }

        // Camera.current where scripts run outside the drawing, by where; and the parts'
        // screen points measured in OnGUI, where IMGUI markers are drawn.
        private readonly System.Collections.Generic.Dictionary<string, System.Collections.Generic.HashSet<string>> current =
            new System.Collections.Generic.Dictionary<string, System.Collections.Generic.HashSet<string>>();
        private float worstInGui = -1f;
        private bool measureInGui;

        // Notes which camera is Camera.current at this place in the frame.
        private void Note(string where)
        {
            System.Collections.Generic.HashSet<string> names;
            if (!current.TryGetValue(where, out names)) current[where] = names = new System.Collections.Generic.HashSet<string>();
            Camera camera = Camera.current;
            names.Add(camera == null ? "none" : camera.name + (Upscaler.CameraRedirect.InScreensPlace(camera) ? " (redirected)" : ""));
        }

        // Off without the switch; with it, the check and the end-of-frame notes started.
        private void Start()
        {
            if (!CommandLine.Has(Argument))
            {
                Destroy(this);
                return;
            }
            StartCoroutine(Run());
            StartCoroutine(EndOfFrames());
        }

        // Camera.current while the scripts' Update runs.
        private void Update()
        {
            Note("Update");
        }

        // Camera.current while the scripts' LateUpdate runs.
        private void LateUpdate()
        {
            Note("LateUpdate");
        }

        // Camera.current at the end of each frame.
        private IEnumerator EndOfFrames()
        {
            var end = new WaitForEndOfFrame();
            while (true)
            {
                yield return end;
                Note("end of frame");
            }
        }

        // Camera.current in OnGUI's repaint, and once measuring has begun the parts' screen points there against
        // their viewport points times the screen's size.
        private void OnGUI()
        {
            if (Event.current.type != EventType.Repaint) return;
            Note("OnGUI");
            Vessel vessel = FlightGlobals.ActiveVessel;
            Camera camera = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null;
            if (!measureInGui || vessel == null || camera == null) return;
            foreach (Part part in vessel.parts)
            {
                Vector3 screen = camera.WorldToScreenPoint(part.transform.position);
                Vector3 viewport = camera.WorldToViewportPoint(part.transform.position);
                if (screen.z <= 0f) continue;
                worstInGui = Mathf.Max(worstInGui, Vector2.Distance(new Vector2(viewport.x * Screen.width, viewport.y * Screen.height), screen));
            }
        }

        // One result line into the log.
        private static void Report(string line)
        {
            Debug.Log(Log.Tag + " Probe: screen check: " + line);
        }

        // A number with two decimals, for the log.
        private static string F(float value)
        {
            return value.ToString("0.00", CultureInfo.InvariantCulture);
        }

        // The check: 25 s into the flight the Camera.current notes, then the three tests in turn, then KSP quits.
        private IEnumerator Run()
        {
            yield return new WaitForSeconds(25f);
            measureInGui = true;
            yield return null;
            yield return null;
            var seen = new System.Text.StringBuilder();
            foreach (var pair in current) seen.Append(pair.Key).Append(": ").Append(string.Join(", ", pair.Value)).Append("; ");
            Report("screen " + Screen.width + "x" + Screen.height + "; Camera.current " + seen + "parts' screen points in OnGUI off by at most "
                   + F(worstInGui) + " px.");
            Vessel vessel = FlightGlobals.ActiveVessel;
            Camera camera = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null;
            if (vessel == null || camera == null)
            {
                Report("no vessel or flight camera.");
                Application.Quit();
                yield break;
            }
            Report(camera.name + " pixelRect " + camera.pixelRect + ", target "
                   + (camera.targetTexture != null ? camera.targetTexture.width + "x" + camera.targetTexture.height : "screen") + ".");

            // 1. The conversions.
            float worstScreen = 0f, worstRay = 0f, worstBack = 0f;
            Part farthest = null;
            Vector3 farthestPoint = Vector3.zero;
            foreach (Part part in vessel.parts)
            {
                Vector3 world = part.transform.position;
                Vector3 screen = camera.WorldToScreenPoint(world);
                Vector3 viewport = camera.WorldToViewportPoint(world);
                if (screen.z <= 0f) continue;
                Vector2 expected = new Vector2(viewport.x * Screen.width, viewport.y * Screen.height);
                worstScreen = Mathf.Max(worstScreen, Vector2.Distance(expected, screen));
                Ray ray = camera.ScreenPointToRay(screen);
                worstRay = Mathf.Max(worstRay, Vector3.Cross(ray.direction, world - ray.origin).magnitude);
                worstBack = Mathf.Max(worstBack, Vector3.Distance(camera.ScreenToWorldPoint(screen), world));
                bool inside = screen.x > 10f && screen.y > 10f && screen.x < Screen.width - 10f && screen.y < Screen.height - 10f;
                if (inside && (farthest == null || screen.x + screen.y > farthestPoint.x + farthestPoint.y))
                {
                    farthest = part;
                    farthestPoint = screen;
                }
            }
            Report("parts: screen point off by at most " + F(worstScreen) + " px, the ray by " + F(worstRay)
                   + " m, the world point back by " + F(worstBack) + " m.");

            // 2. The cursor on the middle of each part, against the part a ray through that
            // viewport point meets (ViewportPointToRay, which the screen's pixels leave alone).
            IntPtr window = GetActiveWindow();
            var origin = new Point();
            bool known = window != IntPtr.Zero && ClientToScreen(window, ref origin);
            int tried = 0, found = 0;
            string missed = "";
            foreach (Part part in vessel.parts)
            {
                if (!known || tried >= 12) break;
                Renderer renderer = part.GetComponentInChildren<Renderer>();
                if (renderer == null) continue;
                Vector3 viewport = camera.WorldToViewportPoint(renderer.bounds.center);
                if (viewport.z <= 0f || viewport.x < 0.1f || viewport.x > 0.9f || viewport.y < 0.15f || viewport.y > 0.85f) continue;
                RaycastHit hit;
                if (!Physics.Raycast(camera.ViewportPointToRay(viewport), out hit, float.MaxValue, Part.layerMask.value)) continue;
                Part truth = hit.collider != null ? hit.collider.GetComponentInParent<Part>() : null;
                if (truth == null) continue;
                SetCursorPos(origin.X + Mathf.RoundToInt(viewport.x * Screen.width),
                             origin.Y + Screen.height - 1 - Mathf.RoundToInt(viewport.y * Screen.height));
                for (int i = 0; i < 4; i++) yield return null;
                tried++;
                if (Mouse.HoveredPart == truth) found++;
                else missed += " " + truth.partInfo.title + " at " + F(viewport.x * Screen.width) + "," + F(viewport.y * Screen.height)
                               + " (Unity reads " + Input.mousePosition + ", KSP finds "
                               + (Mouse.HoveredPart != null ? Mouse.HoveredPart.partInfo.title : "none") + ");";
            }
            Report("cursor: KSP found the part under it " + found + " of " + tried + " times." + missed);

            // 3. The map's markers.
            MapView.EnterMapView();
            yield return new WaitForSeconds(4f);
            Camera map = PlanetariumCamera.Camera;
            Camera ui = KSP.UI.UIMasterController.Instance != null ? KSP.UI.UIMasterController.Instance.uiCamera : null;
            int nodes = 0;
            float worstNode = 0f;
            string worstName = "none";
            if (map != null && ui != null)
                foreach (KSP.UI.Screens.Mapview.MapNode node in FindObjectsOfType<KSP.UI.Screens.Mapview.MapNode>())
                {
                    if (node == null || !node.gameObject.activeInHierarchy) continue;
                    Vector3d scaled = HarmonyLib.Traverse.Create(node).Field("scaledSpacePos").GetValue<Vector3d>();
                    Vector3 viewport = map.WorldToViewportPoint(scaled);
                    if (viewport.z <= 0f || viewport.x < 0f || viewport.x > 1f || viewport.y < 0f || viewport.y > 1f) continue;
                    Vector2 expected = new Vector2(viewport.x * Screen.width, viewport.y * Screen.height);
                    Vector2 shown = RectTransformUtility.WorldToScreenPoint(ui, node.transform.position);
                    float off = Vector2.Distance(expected, shown);
                    nodes++;
                    if (off > worstNode)
                    {
                        worstNode = off;
                        worstName = node.name;
                    }
                }
            // KSP's markers go through the viewport (MapViewCanvasUtil); what the orbits'
            // hover and the mods' map lines take goes through the screen's pixels.
            float worstMap = -1f;
            if (map != null)
                foreach (KSP.UI.Screens.Mapview.MapNode node in FindObjectsOfType<KSP.UI.Screens.Mapview.MapNode>())
                {
                    if (node == null || !node.gameObject.activeInHierarchy) continue;
                    Vector3 point = HarmonyLib.Traverse.Create(node).Field("scaledSpacePos").GetValue<Vector3d>();
                    Vector3 viewport = map.WorldToViewportPoint(point);
                    Vector3 screen = map.WorldToScreenPoint(point);
                    if (screen.z <= 0f) continue;
                    worstMap = Mathf.Max(worstMap, Vector2.Distance(new Vector2(viewport.x * Screen.width, viewport.y * Screen.height), screen));
                }
            Report("map: " + nodes + " of KSP's markers in view, off by at most " + F(worstNode) + " px (" + worstName
                   + "); the map camera's screen points off by at most " + F(worstMap) + " px.");
            MapView.ExitMapView();
            yield return new WaitForSeconds(2f);
            Application.Quit();
        }
    }
}
