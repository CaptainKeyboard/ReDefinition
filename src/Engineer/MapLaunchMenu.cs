using KSP.UI.Screens.Mapview;
using KSP.UI.Screens.Mapview.MapContextMenuOptions;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Engineer
{
    // A right click on the icon of the vessel that stands on the ground, in the map view, opens a menu with the one thing to do there: launch to orbit.
    // The game opens no menu on the vessel the player flies. The click starts the navigation on a route from the ground into the lowest orbit round the
    // body, with no plan made in the planner. The climb is a maneuver of the vessel's own that the player flies as any other.
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class MapLaunchMenu : MonoBehaviour
    {
        // The vessel whose icon the menu listens to, and the menu that is open.
        private Vessel listening;
        private MapContextMenu open;

        internal static MapLaunchMenu Instance;

        private void Awake()
        {
            Instance = this;
        }

        private void Update()
        {
            Vessel vessel = FlightGlobals.ActiveVessel;
            if (vessel == listening || (vessel != null && vessel.orbitRenderer == null)) return;
            Listen(listening, false);
            if (open != null) open.Dismiss();
            listening = vessel;
            Listen(vessel, true);
        }

        private void OnDestroy()
        {
            Listen(listening, false);
            listening = null;
            if (Instance == this) Instance = null;
        }

        private void Listen(Vessel vessel, bool on)
        {
            if (vessel == null || vessel.orbitRenderer == null) return;
            if (on) vessel.orbitRenderer.onVesselIconClicked.Add(Clicked);
            else vessel.orbitRenderer.onVesselIconClicked.Remove(Clicked);
        }

        // Whether the vessel stands on the ground, ready for the climb.
        private static bool OnTheGround(Vessel vessel)
        {
            return vessel != null && (vessel.situation == Vessel.Situations.LANDED || vessel.situation == Vessel.Situations.PRELAUNCH);
        }

        private void Clicked(Vessel vessel)
        {
            if (!Input.GetMouseButtonUp(1)) return;
            Show(vessel);
        }

        // The menu of the vessel's icon, where the vessel stands on the ground and the map is open.
        internal void Show(Vessel vessel)
        {
            if (vessel != FlightGlobals.ActiveVessel || !MapView.MapIsEnabled || !OnTheGround(vessel) || RouteDriver.Instance == null) return;
            if (open != null) open.Dismiss();
            open = MapContextMenu.Create(vessel.GetDisplayName(), new IconPoint(vessel), () => open = null, new LaunchToOrbit(vessel));
        }

        internal void Close()
        {
            if (open != null) open.Dismiss();
        }

        // Where the menu stands: at the vessel's icon in the map, as the game's own menus stand at theirs.
        private sealed class IconPoint : IScreenCaster
        {
            private readonly Vessel vessel;

            internal IconPoint(Vessel vessel)
            {
                this.vessel = vessel;
            }

            public Vector3 GetScreenSpacePoint()
            {
                return PlanetariumCamera.Camera.WorldToScreenPoint(ScaledSpace.LocalToScaledSpace(vessel.GetWorldPos3D()));
            }
        }

        private sealed class LaunchToOrbit : MapContextMenuOption
        {
            private readonly Vessel vessel;

            internal LaunchToOrbit(Vessel vessel) : base("Launch to orbit")
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
