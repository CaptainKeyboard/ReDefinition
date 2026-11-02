using FinePrint;
using ReDefinition.Autopilot;
using ReDefinition.Core;
using UnityEngine;
using UnityEngine.EventSystems;

namespace ReDefinition.Engineer
{
    // The place on a body that the vessel in orbit is to land on. The player picks the menu entry in the map, then clicks on the body, and a marker stands
    // there. The marker is KSP's own custom waypoint, with its icon, its menu to delete it and its place in the save. The navigation is then switched on
    // for a route down to the marker, and the deorbit burn is a maneuver node aimed at it.
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class LandingMarker : MonoBehaviour
    {
        // How long after the entry was picked a click is not taken for the place: the click that picked the entry is not.
        private const float Wait = 0.35f;

        internal static LandingMarker Instance;

        private const string Name = "Landing marker";

        private Vessel placing;
        private float since;
        private float nextCheck;
        private Waypoint marker;

        private void Awake()
        {
            Instance = this;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        // The player is to click on the body the vessel is round.
        internal void Place(Vessel vessel)
        {
            if (vessel == null || vessel.mainBody == null) return;
            placing = vessel;
            since = Time.unscaledTime;
            ScreenMessages.PostScreenMessage("Click on " + vessel.mainBody.bodyName + " to place the landing marker. Escape cancels.", 8f, ScreenMessageStyle.UPPER_CENTER);
        }

        // The markers of this kind are taken away, those of earlier flights too: the game keeps a waypoint in the save.
        private void Remove()
        {
            marker = null;
            if (ScenarioCustomWaypoints.Instance == null || WaypointManager.Instance() == null) return;
            foreach (Waypoint each in WaypointManager.Instance().Waypoints.FindAll(w => w.isCustom && w.name == Name))
                ScenarioCustomWaypoints.RemoveWaypoint(each);
        }

        private void Update()
        {
            // The marker goes when the vessel is no longer guided to it. One that the save holds from an earlier flight goes the same way: the route is
            // not saved, so nothing guides the vessel to it.
            if (placing == null && Time.unscaledTime > nextCheck && (marker != null || WaypointManager.Instance() != null))
            {
                nextCheck = Time.unscaledTime + 2f;
                bool guided = RouteDriver.Instance != null && RouteDriver.Instance.Aimed && FlightGlobals.ActiveVessel != null && !FlightGlobals.ActiveVessel.LandedOrSplashed;
                if (!guided) Remove();
            }
            if (placing == null) return;
            if (!MapView.MapIsEnabled || placing != FlightGlobals.ActiveVessel || Input.GetKeyDown(KeyCode.Escape))
            {
                placing = null;
                return;
            }
            if (Time.unscaledTime - since < Wait || !Input.GetMouseButtonDown(0)) return;
            if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject()) return;
            double latitude, longitude;
            if (!Pick(placing.mainBody, out latitude, out longitude)) return;
            Vessel vessel = placing;
            placing = null;
            Set(vessel, latitude, longitude);
        }

        // Where the cursor is over the body in the map, in degrees. The body's picture in the map has a collider of its own, which the ray meets.
        private static bool Pick(CelestialBody body, out double latitude, out double longitude)
        {
            latitude = longitude = 0.0;
            if (PlanetariumCamera.Camera == null || body == null || body.scaledBody == null) return false;
            Ray ray = PlanetariumCamera.Camera.ScreenPointToRay(Input.mousePosition);
            RaycastHit hit;
            if (!Physics.Raycast(ray.origin, ray.direction, out hit, float.MaxValue, LayerMask.GetMask("Scaled Scenery"))) return false;
            if (!hit.collider.transform.IsChildOf(body.scaledBody.transform)) return false;
            double altitude;
            body.GetLatLonAlt(ScaledSpace.ScaledToLocalSpace(hit.point), out latitude, out longitude, out altitude);
            return true;
        }

        // The marker stands at the place, and the vessel is guided down to it.
        internal void Set(Vessel vessel, double latitude, double longitude)
        {
            if (vessel == null || vessel.mainBody == null || RouteDriver.Instance == null) return;
            if (RouteDriver.Instance.Flying || RouteState.Of(vessel) == null || RouteState.Of(vessel).Autopilot)
            {
                ScreenMessages.PostScreenMessage("The autopilot is flying: switch it off to land on a marker.", 6f, ScreenMessageStyle.UPPER_CENTER);
                return;
            }
            Remove();
            if (ScenarioCustomWaypoints.Instance != null)
            {
                var made = new Waypoint { name = Name, celestialName = vessel.mainBody.bodyName, latitude = latitude, longitude = longitude };
                ScenarioCustomWaypoints.AddWaypoint(made);
                marker = made;
            }
            else Debug.Log(Log.Tag + " Navigation: the game has no custom waypoints here; the landing marker is not drawn.");
            RouteDriver.Instance.StartDescent(vessel, latitude, longitude);
        }
    }
}
