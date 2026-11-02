using System.Collections.Generic;
using System.Reflection;
using FinePrint;
using KSP.UI;
using KSP.UI.Screens.Mapview;
using KSP.UI.Screens.Mapview.MapContextMenuOptions;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Engineer
{
    // The map's own menu of an object has an entry for the vessel that the player flies. That menu is the one with "Set as target" and "Focus", which a
    // click on the icon of a vessel or a body opens. The game casts no menu on the vessel flown, since it leaves out its own icon. So the cast takes it
    // in while it stands on the ground or flies in an orbit, and the menu has its one entry. On the ground it is "Launch to orbit": the navigation is
    // started on a route into the lowest orbit round the body, with no plan made in the planner. In an orbit it is "Land at marker": the player
    // clicks on the body, and a burn is aimed at the place. Both are maneuvers that the player flies. A vessel on the pad stands under the icon of the
    // launch site, which takes the click. So the menu of every launch site has the entry for the vessel whose icon it covers: the space center,
    // the runways, and every other site the game lists.
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class MapLaunchMenu : MonoBehaviour
    {
        private const string HarmonyId = "ReDefinition.MapLaunchMenu";
        private static bool hooked;
        private MapContextMenu open;

        internal static MapLaunchMenu Instance;

        private void Awake()
        {
            Instance = this;
        }

        private void Start()
        {
            if (hooked) return;
            hooked = true;
            HarmonyHooks.Safely("map-launch-menu", "The map's menu has no entry to launch to orbit", () =>
                HarmonyHooks.Install(HarmonyId, typeof(MapLaunchMenu), new List<HarmonyHook>
                {
                    new HarmonyHook(typeof(OrbitTargeter).GetMethod("TargetCastNodes", BindingFlags.Instance | BindingFlags.NonPublic), null, nameof(TakeIn), null),
                    new HarmonyHook(typeof(MapContextMenu).GetConstructor(new[] { typeof(string), typeof(IScreenCaster), typeof(Callback), typeof(MapContextMenuOption[]) }),
                        nameof(Add), null, null),
                    new HarmonyHook(typeof(MapContextMenu).GetConstructor(new[] { typeof(string), typeof(Rect), typeof(IScreenCaster), typeof(Callback), typeof(MapContextMenuOption[]) }),
                        nameof(AddToSite), null, null),
                }));
        }

        private void OnDestroy()
        {
            Close();
            if (Instance == this) Instance = null;
        }

        // Whether the vessel is the one flown, and stands on the ground, ready for the climb.
        private static bool OnTheGround(Vessel vessel)
        {
            return vessel != null && vessel == FlightGlobals.ActiveVessel && RouteDriver.Instance != null
                   && (vessel.situation == Vessel.Situations.LANDED || vessel.situation == Vessel.Situations.PRELAUNCH);
        }

        // Whether the vessel is the one flown, in an orbit that it can leave to land.
        private static bool InOrbit(Vessel vessel)
        {
            return vessel != null && vessel == FlightGlobals.ActiveVessel && RouteDriver.Instance != null && LandingMarker.Instance != null
                   && vessel.situation == Vessel.Situations.ORBITING && vessel.orbit != null && vessel.orbit.eccentricity < 1.0
                   && vessel.mainBody != null && vessel.mainBody.hasSolidSurface && vessel.mainBody.pqsController != null;
        }

        private static bool Ready(Vessel vessel)
        {
            return OnTheGround(vessel) || InOrbit(vessel);
        }

        // After the game has looked for the icon under the cursor: where it found none, the vessel's own icon is taken in, if the cursor is over it.
        private static void TakeIn(ref OrbitDriver __result, ref OrbitRendererBase.OrbitCastHit orbitHit)
        {
            if (__result != null) return;
            Vessel vessel = FlightGlobals.ActiveVessel;
            if (!Ready(vessel) || vessel.orbitDriver == null || vessel.orbitDriver.Renderer == null || !vessel.orbitDriver.Renderer.mouseOver) return;
            if (!MapViewFiltering.CheckAgainstFilter(vessel)) return;
            orbitHit.driver = vessel.orbitDriver;
            orbitHit.or = vessel.orbitDriver.Renderer;
            orbitHit.mouseTA = vessel.orbit.trueAnomaly;
            __result = vessel.orbitDriver;
        }

        // The menu of the vessel that is flown, on the ground: its one entry is the launch. What the game has for another vessel (target, focus) is of
        // no use for the vessel itself.
        private static void Add(IScreenCaster sc, ref MapContextMenuOption[] options)
        {
            if (!(sc is OrbitRendererBase.OrbitCastHit)) return;
            OrbitRendererBase.OrbitCastHit hit = (OrbitRendererBase.OrbitCastHit)sc;
            Vessel vessel = hit.driver != null ? hit.driver.vessel : null;
            if (OnTheGround(vessel)) options = new MapContextMenuOption[] { new LaunchToOrbit(vessel) };
            else if (InOrbit(vessel)) options = new MapContextMenuOption[] { new LandAtMarker(vessel) };
        }

        // The ids the game gives the waypoints of its launch sites: the space center, the runways, and every other site, those of mods too.
        private static bool IsSite(Waypoint waypoint)
        {
            return waypoint != null && !waypoint.isCustom && (waypoint.id == "ksc" || waypoint.id == "runway" || waypoint.id == "launchsite");
        }

        // The icon's place on the game's screen and its width there.
        private static bool Placed(MapNode node, out Vector2 centre, out float width)
        {
            centre = Vector2.zero;
            width = 0f;
            RectTransform rect = node != null && node.targetGraphic != null ? node.targetGraphic.rectTransform : null;
            if (rect == null || UIMasterController.Instance == null) return false;
            Camera camera = UIMasterController.Instance.uiCamera;
            var corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            centre = RectTransformUtility.WorldToScreenPoint(camera, rect.position);
            width = Vector2.Distance(RectTransformUtility.WorldToScreenPoint(camera, corners[0]), RectTransformUtility.WorldToScreenPoint(camera, corners[2]));
            return true;
        }

        // Whether the icon of the launch site covers the icon of the vessel: the two are nearer than their sizes reach.
        internal static bool Covers(Waypoint site, Vessel vessel)
        {
            if (!IsSite(site) || !OnTheGround(vessel) || vessel.mapObject == null || vessel.mainBody == null || site.celestialName != vessel.mainBody.GetName()) return false;
            Vector2 a, b;
            float widthA, widthB;
            return Placed(site.node, out a, out widthA) && Placed(vessel.mapObject.uiNode, out b, out widthB) && Vector2.Distance(a, b) < 0.5f * (widthA + widthB);
        }

        // The menu of a launch site over the vessel on the pad: the entry that launches it. The menu is one entry taller for it.
        private static void AddToSite(ref Rect rct, IScreenCaster sc, ref MapContextMenuOption[] options)
        {
            if (!(sc is WaypointCastHit)) return;
            Vessel vessel = FlightGlobals.ActiveVessel;
            if (!Covers(((WaypointCastHit)sc).waypoint, vessel)) return;
            int count = options != null ? options.Length : 0;
            var longer = new MapContextMenuOption[count + 1];
            longer[0] = new LaunchToOrbit(vessel, "Launch " + vessel.GetDisplayName() + " to Orbit");
            if (count > 0) options.CopyTo(longer, 1);
            options = longer;
            rct.height += 36f;
        }

        // The menu as the game makes it.
        internal void Show(Vessel vessel)
        {
            if (!MapView.MapIsEnabled || !Ready(vessel) || vessel.orbitDriver == null) return;
            Close();
            var hit = new OrbitRendererBase.OrbitCastHit { driver = vessel.orbitDriver, or = vessel.orbitDriver.Renderer, mouseTA = vessel.orbit.trueAnomaly };
            open = MapContextMenu.Create(vessel.GetDisplayName(), hit, () => open = null, new MapContextMenuOption[0]);
        }

        internal void Close()
        {
            if (open != null) open.Dismiss();
            open = null;
        }

        private sealed class LandAtMarker : MapContextMenuOption
        {
            private readonly Vessel vessel;

            internal LandAtMarker(Vessel vessel) : base("Land at marker")
            {
                this.vessel = vessel;
            }

            protected override void OnSelect()
            {
                if (vessel == null || LandingMarker.Instance == null) return;
                LandingMarker.Instance.Place(vessel);
            }
        }

        private sealed class LaunchToOrbit : MapContextMenuOption
        {
            private readonly Vessel vessel;

            internal LaunchToOrbit(Vessel vessel, string label = "Launch to orbit") : base(label)
            {
                this.vessel = vessel;
            }

            protected override void OnSelect()
            {
                if (vessel == null || RouteDriver.Instance == null) return;
                RouteDriver.Instance.StartAscent(vessel);
            }
        }
    }
}
