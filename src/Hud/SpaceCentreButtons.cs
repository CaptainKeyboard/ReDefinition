using System.Collections;
using System.Collections.Generic;
using KSP.UI;
using ReDefinition.Core;
using ReDefinition.Ui;
using UnityEngine;

namespace ReDefinition.Hud
{
    // Buttons over the space centre's other buildings while the vessel stands on its
    // launch site and has not moved yet (Vessel.Situations.PRELAUNCH), beside the
    // assembly building's (AssemblyReturn), each where that one stands: the middle
    // of the building's hall, before its front (FacilitySpot). A click, after a
    // question, leaves the flight as KSP's pause menu does -- the game saved, the
    // vessel left where it stands (PauseMenu.saveAndExit, decompiled) -- for the
    // tracking station, or for the space centre with that building opened, as a
    // click on it there opens it (SpaceCenterBuilding.OnLeftClick, which shows a
    // damaged building's menu instead). Only where the game allows it
    // (GameParameters.Flight.CanLeaveToTrackingStation and CanLeaveToSpaceCenter,
    // SpaceCenter.CanGoInTrackingStation) and the game can be saved now
    // (FlightGlobals.ClearToSave). In flight view only: not on the map, nor from
    // inside the vessel (WorldButton.FromInside).
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class SpaceCentreButtons : MonoBehaviour
    {
        // How near a building the vessel has to stand, in metres.
        private const double Range = 3000.0;

        // One of the space centre's other buildings and its button.
        private sealed class Facility
        {
            // As in Wanted: its name, what the button says, the space centre's button it looks like, and its short name;
            // whether it is the tracking station; where it stands; its button.
            internal string Name;
            internal string Title;
            internal string Look;
            internal string Short;
            internal bool TrackingStation;
            internal FacilitySpot Spot;
            internal WorldButton Button;
        }

        // PSystemSetup's names for them, what the button says, the space centre's
        // button for it (KscButtons), and its short name where that is not loaded.
        private static readonly string[][] Wanted =
        {
            new[] { "TrackingStation", "Go to the Tracking Station", "Buttons_TrackingStation", "Tracking" },
            new[] { "MissionControl", "Go to Mission Control", "Buttons_MissionControl", "Mission" },
            new[] { "AstronautComplex", "Go to the Astronaut Complex", "Buttons_AstronautComplex", "Crew" },
            new[] { "RnD", "Go to Research and Development", "Buttons_RnD", "R&D" },
            new[] { "Administration", "Go to the Administration", "Buttons_Admin", "Admin" },
        };

        // The buildings the space centre has, with their buttons; the body the space centre is on.
        private readonly List<Facility> facilities = new List<Facility>();
        private CelestialBody home;

        // Waits for KSP's interface and the space centre's facilities, then makes a button for each building found.
        private IEnumerator Start()
        {
            float until = Time.unscaledTime + 15f;
            while (Time.unscaledTime < until && (UIMasterController.Instance == null || PSystemSetup.Instance == null
                                                 || FlightCamera.fetch == null))
                yield return null;
            if (UIMasterController.Instance == null || PSystemSetup.Instance == null) yield break;
            foreach (string[] wanted in Wanted)
            {
                CelestialBody body;
                FacilitySpot spot = FacilitySpot.Find(wanted[0], out body);
                if (spot == null) continue;
                if (home == null) home = body;
                Facility facility = new Facility
                {
                    Name = wanted[0], Title = wanted[1], Look = wanted[2], Short = wanted[3],
                    TrackingStation = wanted[0] == "TrackingStation", Spot = spot,
                };
                facility.Button = new WorldButton("ReDefinitionFacility_" + wanted[0], () => Ask(facility), 40f);
                facility.Button.Tooltip = facility.Title;
                facilities.Add(facility);
            }
            if (home == null) home = FlightGlobals.GetHomeBody();
            Debug.Log(Log.Tag + " Buttons over the space centre's buildings before a launch: " + facilities.Count + ".");
        }

        // The buttons go with the flight scene.
        private void OnDestroy()
        {
            foreach (Facility facility in facilities) facility.Button.Destroy();
            facilities.Clear();
        }

        // Each building's button before its front while the vessel waits on its launch site near it, and hidden otherwise.
        private void LateUpdate()
        {
            if (facilities.Count == 0) return;
            Vessel vessel = FlightGlobals.ActiveVessel;
            bool before = vessel != null && vessel.situation == Vessel.Situations.PRELAUNCH && !MapView.MapIsEnabled && !WorldButton.FromInside()
                          && vessel.mainBody == home && HighLogic.CurrentGame != null;
            Camera camera = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null;
            foreach (Facility facility in facilities)
            {
                if (!before || facility.Spot.Place == null || !Allowed(facility)
                    || (vessel.CoMD - (Vector3d)facility.Spot.Place.position).magnitude > Range)
                {
                    facility.Button.Hide();
                    continue;
                }
                // Looked at as it shows, as the assembly building's is: the space
                // centre's buttons may be taken after this flight started.
                if (facility.Button.Place(facility.Spot.Middle(home), camera, 0f, false, facility.Spot.Solid))
                    facility.Button.Look(facility.Look, facility.Short);
            }
        }

        // Whether the game lets the flight be left for that building.
        private static bool Allowed(Facility facility)
        {
            GameParameters parameters = HighLogic.CurrentGame.Parameters;
            return facility.TrackingStation
                ? parameters.Flight.CanLeaveToTrackingStation && parameters.SpaceCenter.CanGoInTrackingStation
                : parameters.Flight.CanLeaveToSpaceCenter;
        }

        // ---------------------------------------------------------------- click

        // Asks first: the flight is left, the game saved.
        private void Ask(Facility facility)
        {
            if (HighLogic.CurrentGame == null || !Allowed(facility)) return;
            ConfirmDialog.Show("ReDefinitionFacility", facility.Title, "The game is saved, and the vessel stays where it stands.",
                340f, "Go", () => Go(facility), 110f);
        }

        // As the pause menu leaves the flight where the game can be saved
        // (PauseMenu.saveAndExit).
        private static void Go(Facility facility)
        {
            if (HighLogic.CurrentGame == null || !Allowed(facility)) return;
            if (!ConfirmDialog.ClearToSave("leave the flight")) return;
            GameScenes scene = facility.TrackingStation ? GameScenes.TRACKSTATION : GameScenes.SPACECENTER;
            if (!facility.TrackingStation) SpaceCentreEntry.Plan(facility.Name);
            Debug.Log(Log.Tag + " Leaving the flight for the " + facility.Name + ".");
            GameEvents.onSceneConfirmExit.Fire(HighLogic.CurrentGame.startScene);
            // Internal in KSP: called as the pause menu calls it before it saves.
            System.Reflection.MethodInfo clearIds = typeof(FlightGlobals).GetMethod("ClearpersistentIdDictionaries",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            if (clearIds != null) clearIds.Invoke(null, null);
            GamePersistence.SaveGame(HighLogic.CurrentGame.Updated(), "persistent", HighLogic.SaveFolder, SaveMode.OVERWRITE);
            HighLogic.LoadScene(scene);
        }
    }

    // After a button over a building of the space centre: in the space centre, the
    // building opened as a click on it there opens it.
    [KSPAddon(KSPAddon.Startup.SpaceCentre, false)]
    public class SpaceCentreEntry : MonoBehaviour
    {
        // The facility to open once the space centre stands; null for none.
        private static string planned;

        // Plans the facility to open in the space centre.
        internal static void Plan(string facility)
        {
            planned = facility;
        }

        // Waits for the planned building and the scene's interface, then opens the building as a click on it would.
        private IEnumerator Start()
        {
            string facility = planned;
            planned = null;
            if (facility == null) yield break;
            // The buildings find their facilities in their Start (SpaceCenterBuilding);
            // a moment for the scene's interface to stand.
            float until = Time.unscaledTime + 10f;
            SpaceCenterBuilding building = null;
            while (Time.unscaledTime < until)
            {
                building = Find(facility);
                if (building != null && UIMasterController.Instance != null && Time.timeSinceLevelLoad > 1f) break;
                yield return null;
            }
            if (building == null)
            {
                Debug.LogWarning(Log.Tag + " The space centre's " + facility + " was not found to open.");
                yield break;
            }
            Debug.Log(Log.Tag + " Opening the space centre's " + facility + ".");
            building.OnLeftClick();
        }

        // The space centre's building for the facility; null where there is none.
        private static SpaceCenterBuilding Find(string facility)
        {
            foreach (SpaceCenterBuilding building in FindObjectsOfType<SpaceCenterBuilding>())
            {
                string name = building.facilityName;
                if (name != null && (name == facility || name.EndsWith("/" + facility))) return building;
            }
            return null;
        }
    }
}
