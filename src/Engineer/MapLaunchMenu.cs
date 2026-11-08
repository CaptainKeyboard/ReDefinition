using System;
using System.Collections.Generic;
using System.Reflection;
using FinePrint;
using KSP.UI;
using KSP.UI.Screens.Mapview;
using KSP.UI.Screens.Mapview.MapContextMenuOptions;
using ReDefinition.Autopilot.Flight;
using ReDefinition.Autopilot.Planning;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Engineer
{
    // The map's own menu of an object has an entry for the vessel that the player flies. That menu is the one with "Set as target" and "Focus", which a
    // click on the icon of a vessel or a body opens. The game casts no menu on the vessel flown, since it leaves out its own icon. So the cast takes it
    // in while it stands on the ground or flies in an orbit, and the menu has its one entry. On the ground it is "Launch to orbit": the planner's route is
    // set to a climb into the lowest orbit round the body. In an orbit it is "Land at marker": the player clicks on the body, and the route is set to a
    // landing at the place. The entries only set the plan; the Live button flies it. With Shift held the entry adds the stop to the route instead of
    // replacing it. The menu of another body has "Navigate to" entries with the same effect. A vessel on the pad stands under the icon of the
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
            try
            {
                TakeInCore(ref __result, ref orbitHit);
            }
            catch (Exception e)
            {
                Failed("the vessel's icon", e);
            }
        }

        private static void TakeInCore(ref OrbitDriver __result, ref OrbitRendererBase.OrbitCastHit orbitHit)
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
            try
            {
                AddCore(sc, ref options);
            }
            catch (Exception e)
            {
                Failed("the vessel's menu", e);
            }
        }

        private static void AddCore(IScreenCaster sc, ref MapContextMenuOption[] options)
        {
            if (sc is PatchedConics.PatchCastHit && !MapMarkerMenu.Making)
            {
                options = MapMarkerMenu.WithNodeEntries(options);
                return;
            }
            if (!(sc is OrbitRendererBase.OrbitCastHit) || MapMarkerMenu.Making) return;
            OrbitRendererBase.OrbitCastHit hit = (OrbitRendererBase.OrbitCastHit)sc;
            Vessel vessel = hit.driver != null ? hit.driver.vessel : null;
            if (OnTheGround(vessel))
                options = MechJebDriver.CanDrive(vessel)
                    ? new MapContextMenuOption[] { new LaunchToOrbit(vessel), new DriveToMarker(vessel) }
                    : new MapContextMenuOption[] { new LaunchToOrbit(vessel) };
            else if (InOrbit(vessel)) options = new MapContextMenuOption[] { new LandAtMarker(vessel) };
            else if (vessel == null) AddNavigate(hit, ref options);
            else AddMeet(vessel, ref options);
        }

        // The menu of another vessel in an orbit: the route set to a meeting with it, or to docking with it.
        private static void AddMeet(Vessel other, ref MapContextMenuOption[] options)
        {
            Vessel flown = FlightGlobals.ActiveVessel;
            if (MapPlannerWindow.Instance == null || !InOrbit(flown) || !LiveMenu.IsMeetable(flown, other)) return;
            bool dock = LiveMenu.HasDockingPort(other);
            int count = options != null ? options.Length : 0;
            var longer = new MapContextMenuOption[count + (dock ? 2 : 1)];
            if (count > 0) options.CopyTo(longer, 0);
            longer[count] = new MeetWith(flown, other, false);
            if (dock) longer[count + 1] = new MeetWith(flown, other, true);
            options = longer;
        }

        // The menu of a body other than the one the vessel is round: the two places the route can be set to.
        private static void AddNavigate(OrbitRendererBase.OrbitCastHit hit, ref MapContextMenuOption[] options)
        {
            CelestialBody body = hit.driver != null ? hit.driver.celestialBody : null;
            Vessel flown = FlightGlobals.ActiveVessel;
            if (body == null || body.Equals(null) || flown == null || flown.mainBody == null || MapPlannerWindow.Instance == null || RouteDriver.Instance == null) return;
            // The sun has no orbit to go to, and the body the vessel is round is reached by the other entries.
            if (body.referenceBody == body || body == flown.mainBody) return;
            int count = options != null ? options.Length : 0;
            int extra = body.hasSolidSurface && body.pqsController != null ? 2 : 1;
            var longer = new MapContextMenuOption[count + extra];
            if (count > 0) options.CopyTo(longer, 0);
            longer[count] = new NavigateTo(flown, body, false);
            if (extra == 2) longer[count + 1] = new NavigateTo(flown, body, true);
            options = longer;
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
            try
            {
                AddToSiteCore(ref rct, sc, ref options);
            }
            catch (Exception e)
            {
                Failed("the launch site's menu", e);
            }
        }

        // A hook that fails leaves the game's own menu as it is. It is told once.
        private static bool reported;

        private static void Failed(string what, Exception e)
        {
            if (reported) return;
            reported = true;
            Debug.LogWarning(Log.Tag + " The map's menu has no entry for " + what + ": " + e);
        }

        private static void AddToSiteCore(ref Rect rct, IScreenCaster sc, ref MapContextMenuOption[] options)
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

        private sealed class DriveToMarker : MapContextMenuOption
        {
            private readonly Vessel vessel;

            internal DriveToMarker(Vessel vessel) : base("Drive to marker")
            {
                this.vessel = vessel;
            }

            protected override void OnSelect()
            {
                if (vessel == null || LandingMarker.Instance == null) return;
                LandingMarker.Instance.Place(vessel, true);
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
                if (vessel == null || vessel.mainBody == null || MapPlannerWindow.Instance == null) return;
                MapPlannerWindow.Instance.PlanGoal(vessel, vessel.mainBody, false, NextStepPlanner.LowOrbit(vessel.mainBody), MapPlannerWindow.Adding);
            }
        }

        private sealed class MeetWith : MapContextMenuOption
        {
            private readonly Vessel flown;
            private readonly Vessel other;
            private readonly bool dock;

            internal MeetWith(Vessel flown, Vessel other, bool dock) : base(dock ? "Dock with" : "Rendezvous with")
            {
                this.flown = flown;
                this.other = other;
                this.dock = dock;
            }

            protected override void OnSelect()
            {
                if (flown == null || other == null || MapPlannerWindow.Instance == null) return;
                LiveMenu.Meet(MapPlannerWindow.Instance, flown, other, dock);
            }
        }

        private sealed class NavigateTo : MapContextMenuOption
        {
            private readonly Vessel vessel;
            private readonly CelestialBody body;
            private readonly bool surface;

            internal NavigateTo(Vessel vessel, CelestialBody body, bool surface) : base("Navigate to " + (surface ? "Surface" : "Orbit"))
            {
                this.vessel = vessel;
                this.body = body;
                this.surface = surface;
            }

            protected override void OnSelect()
            {
                if (vessel == null || body == null || MapPlannerWindow.Instance == null) return;
                MapPlannerWindow.Instance.PlanGoal(vessel, body, surface, NextStepPlanner.LowOrbit(body), MapPlannerWindow.Adding);
            }
        }
    }
}
