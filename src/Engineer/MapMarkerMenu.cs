using System;
using System.Collections.Generic;
using System.Reflection;
using KSP.UI;
using KSP.UI.Screens.Mapview;
using KSP.UI.Screens.Mapview.MapContextMenuOptions;
using ReDefinition.Autopilot.Planning;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Engineer
{
    // A click on an icon of the flown vessel's orbit (apoapsis, periapsis, ascending or descending node) opens a menu of what the planner can do there.
    // The orbit can be rounded at an apsis, or its plane turned to the equator or to the target's. The choice only sets the planner's route.
    // The game gives these icons a click event (MapNode.OnClick) and no menu. Its handler pins the caption and is left alone.
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class MapMarkerMenu : MonoBehaviour
    {
        private enum Marker
        {
            Apoapsis,
            Periapsis,
            AscendingNode,
            DescendingNode,
        }

        private static readonly string[] Fields = { "apNode", "peNode", "ascNode", "descNode" };
        private static FieldInfo[] fieldInfos;
        private static FieldInfo[] patchFields;
        private readonly Dictionary<MapNode, Callback<MapNode, Mouse.Buttons>> hooked = new Dictionary<MapNode, Callback<MapNode, Mouse.Buttons>>();
        private float nextLook;
        private bool reported;
        private MapContextMenu open;

        // While the menu is made, the hook on the map's menus leaves its options alone.
        internal static bool Making;

        // The probe has no mouse: where the click is taken to be, on the screen.
        internal static Vector2? ClickAt;

        private void Update()
        {
            if (Time.unscaledTime < nextLook) return;
            nextLook = Time.unscaledTime + 1f;
            if (!MapView.MapIsEnabled) return;
            try
            {
                Look();
            }
            catch (Exception e)
            {
                if (!reported) Debug.LogWarning(Log.Tag + " The orbit's icons have no menu: " + e.Message);
                reported = true;
            }
        }

        // The vessel's orbit renderer, and its four icons that have not been given the click yet.
        private void Look()
        {
            Vessel vessel = FlightGlobals.ActiveVessel;
            OrbitRendererBase renderer = vessel != null && vessel.orbitDriver != null ? vessel.orbitDriver.Renderer : null;
            if (renderer == null) return;
            // The game makes its icons anew each time the map opens; the old ones are gone with their handlers.
            var gone = new List<MapNode>();
            foreach (MapNode node in hooked.Keys)
                if (node == null) gone.Add(node);
            foreach (MapNode node in gone) hooked.Remove(node);
            if (fieldInfos == null)
            {
                fieldInfos = new FieldInfo[Fields.Length];
                for (int i = 0; i < Fields.Length; i++)
                    fieldInfos[i] = typeof(OrbitRendererBase).GetField(Fields[i], BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            }
            for (int i = 0; i < fieldInfos.Length; i++)
                Hook(fieldInfos[i] != null ? fieldInfos[i].GetValue(renderer) as MapNode : null, (Marker)i);
            // The icons that show on the flown vessel's orbit belong to the map's patch renderings, not to the vessel's own orbit renderer.
            PatchedConicRenderer patches = vessel.patchedConicRenderer;
            if (patches == null || patches.patchRenders == null || patches.patchRenders.Count == 0) return;
            if (patchFields == null)
                patchFields = new[] { typeof(PatchRendering).GetField("mnAp", BindingFlags.Instance | BindingFlags.NonPublic), typeof(PatchRendering).GetField("mnPe", BindingFlags.Instance | BindingFlags.NonPublic) };
            PatchRendering first = patches.patchRenders[0];
            Hook(patchFields[0] != null ? patchFields[0].GetValue(first) as MapNode : null, Marker.Apoapsis);
            Hook(patchFields[1] != null ? patchFields[1].GetValue(first) as MapNode : null, Marker.Periapsis);
        }

        private void Hook(MapNode node, Marker marker)
        {
            if (node == null || hooked.ContainsKey(node)) return;
            Callback<MapNode, Mouse.Buttons> handler = (clicked, buttons) => Clicked(marker, buttons);
            node.OnClick += handler;
            hooked[node] = handler;
        }

        // What the planner offers at an icon of the flown vessel's orbit.
        private static List<MapContextMenuOption> Options(Marker marker, Vessel vessel, MapPlannerWindow window)
        {
            var options = new List<MapContextMenuOption>();
            Orbit orbit = vessel.orbit;
            bool round = orbit.eccentricity <= 0.01;
            if (marker == Marker.Apoapsis && !round) options.Add(new Entry("Circularize Here", () => LiveMenu.Circularize(window, vessel, orbit.ApA)));
            if (marker == Marker.Periapsis && !round && orbit.PeA > 1000.0) options.Add(new Entry("Circularize Here", () => LiveMenu.Circularize(window, vessel, orbit.PeA)));
            if (marker == Marker.AscendingNode || marker == Marker.DescendingNode)
            {
                if (orbit.inclination > NodePlanner.PlaneTolerance && orbit.inclination < 180.0 - NodePlanner.PlaneTolerance)
                    options.Add(new Entry("Equatorial Orbit", () => LiveMenu.TurnPlane(window, vessel, null, true)));
                Vessel target = LiveMenu.Meetable(vessel);
                if (target != null) options.Add(new Entry("Match Plane with " + target.vesselName, () => LiveMenu.TurnPlane(window, vessel, target, false)));
            }
            return options;
        }

        // The game gives a click on the ascending or descending node no event of its own: it opens its menu of the orbit there. Where the cursor is
        // on the node's icon, the entries for the plane are put in that menu.
        internal static MapContextMenuOption[] WithNodeEntries(MapContextMenuOption[] options)
        {
            Vessel vessel = FlightGlobals.ActiveVessel;
            MapPlannerWindow window = MapPlannerWindow.Instance;
            if (vessel == null || vessel.orbit == null || window == null || vessel.situation != Vessel.Situations.ORBITING || vessel.orbit.eccentricity >= 1.0) return options;
            foreach (MapNode node in Resources.FindObjectsOfTypeAll<MapNode>())
            {
                if (node == null || !node.gameObject.scene.IsValid() || !node.gameObject.activeInHierarchy || node.targetGraphic == null) continue;
                Marker marker;
                if (node.name == "AN Map Node") marker = Marker.AscendingNode;
                else if (node.name == "DN Map Node") marker = Marker.DescendingNode;
                else continue;
                if (!RectTransformUtility.RectangleContainsScreenPoint(node.targetGraphic.rectTransform, Input.mousePosition, UIMasterController.Instance.uiCamera)) continue;
                List<MapContextMenuOption> more = Options(marker, vessel, window);
                if (more.Count == 0) return options;
                var all = new List<MapContextMenuOption>(options ?? new MapContextMenuOption[0]);
                all.AddRange(more);
                return all.ToArray();
            }
            return options;
        }

        private void Clicked(Marker marker, Mouse.Buttons buttons)
        {
            if ((buttons & Mouse.Buttons.Left) == 0) return;
            Vessel vessel = FlightGlobals.ActiveVessel;
            MapPlannerWindow window = MapPlannerWindow.Instance;
            if (vessel == null || vessel.orbit == null || window == null || vessel.situation != Vessel.Situations.ORBITING || vessel.orbit.eccentricity >= 1.0) return;
            List<MapContextMenuOption> options = Options(marker, vessel, window);
            if (options.Count == 0) return;
            if (open != null) open.Dismiss();
            Making = true;
            try
            {
                MapContextMenu made = null;
                // The game calls this when the menu is gone, which for the one before comes after the next is made.
                made = MapContextMenu.Create(MarkerTitle(marker), new At(), () => { if (open == made) open = null; }, options.ToArray());
                open = made;
            }
            finally
            {
                Making = false;
            }
        }

        private static string MarkerTitle(Marker marker)
        {
            switch (marker)
            {
                case Marker.Apoapsis: return "Apoapsis";
                case Marker.Periapsis: return "Periapsis";
                case Marker.AscendingNode: return "Ascending Node";
                default: return "Descending Node";
            }
        }

        private void OnDestroy()
        {
            if (open != null) open.Dismiss();
            foreach (KeyValuePair<MapNode, Callback<MapNode, Mouse.Buttons>> pair in hooked)
                if (pair.Key != null) pair.Key.OnClick -= pair.Value;
            hooked.Clear();
        }

        // Where the menu hangs: at the mouse as it clicked the icon, on the screen, where it stays while the map's camera moves. The game shows a menu
        // only for a point in front of the camera: z above zero.
        private sealed class At : IScreenCaster
        {
            private readonly Vector3 point = ClickAt.HasValue ? new Vector3(ClickAt.Value.x, ClickAt.Value.y, 1f) : new Vector3(Input.mousePosition.x, Input.mousePosition.y, 1f);

            public Vector3 GetScreenSpacePoint()
            {
                return point;
            }
        }

        private sealed class Entry : MapContextMenuOption
        {
            private readonly Callback choose;

            internal Entry(string label, Callback choose) : base(label)
            {
                this.choose = choose;
            }

            protected override void OnSelect()
            {
                choose();
            }
        }
    }
}
