using System.Collections;
using System.Collections.Generic;
using KSP.UI;
using KSP.UI.Screens;
using ReDefinition.Core;
using ReDefinition.Window;
using UnityEngine;

namespace ReDefinition.Flight
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
    // (FlightGlobals.ClearToSave).
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class SpaceCentreButtons : MonoBehaviour
    {
        private const double Range = 3000.0;

        private sealed class Facility
        {
            internal string Name;
            internal string Title;
            internal string Look;
            internal Sprite Icon;
            internal bool TrackingStation;
            internal FacilitySpot Spot;
            internal WorldButton Button;
        }

        // PSystemSetup's names for them, what the button says, the space centre's
        // button for it (KscButtons), and ReDefinition's symbol where that has not
        // been seen yet.
        private static readonly string[][] Wanted =
        {
            new[] { "TrackingStation", "Go to the Tracking Station", "Buttons_TrackingStation", "Facility_TrackingStation" },
            new[] { "MissionControl", "Go to Mission Control", "Buttons_MissionControl", "Facility_MissionControl" },
            new[] { "AstronautComplex", "Go to the Astronaut Complex", "Buttons_AstronautComplex", "Facility_AstronautComplex" },
            new[] { "RnD", "Go to Research and Development", "Buttons_RnD", "Facility_RnD" },
            new[] { "Administration", "Go to the Administration", "Buttons_Admin", "Facility_Administration" },
        };

        private readonly List<Facility> facilities = new List<Facility>();
        private CelestialBody home;

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
                    Name = wanted[0], Title = wanted[1], Look = wanted[2], Icon = Load(wanted[3]),
                    TrackingStation = wanted[0] == "TrackingStation", Spot = spot,
                };
                facility.Button = new WorldButton("ReDefinitionFacility_" + wanted[0], () => Ask(facility), 40f);
                facility.Button.Tooltip = facility.Title;
                facilities.Add(facility);
            }
            if (home == null) home = FlightGlobals.GetHomeBody();
            Debug.Log(Log.Tag + " Buttons over the space centre's buildings before a launch: " + facilities.Count + ".");
        }

        private static Sprite Load(string name)
        {
            Texture2D texture = GameDatabase.Instance != null ? GameDatabase.Instance.GetTexture("ReDefinition/Icons/" + name, false) : null;
            return texture != null ? Sprite.Create(texture, new Rect(0f, 0f, texture.width, texture.height), new Vector2(0.5f, 0.5f)) : null;
        }

        private void OnDestroy()
        {
            foreach (Facility facility in facilities) facility.Button.Destroy();
            facilities.Clear();
        }

        private void LateUpdate()
        {
            if (facilities.Count == 0) return;
            Vessel vessel = FlightGlobals.ActiveVessel;
            bool before = vessel != null && vessel.situation == Vessel.Situations.PRELAUNCH && !MapView.MapIsEnabled
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
                    facility.Button.Look(facility.Look, facility.Icon);
            }
        }

        private static bool Allowed(Facility facility)
        {
            GameParameters parameters = HighLogic.CurrentGame.Parameters;
            return facility.TrackingStation
                ? parameters.Flight.CanLeaveToTrackingStation && parameters.SpaceCenter.CanGoInTrackingStation
                : parameters.Flight.CanLeaveToSpaceCenter;
        }

        // ---------------------------------------------------------------- click

        private void Ask(Facility facility)
        {
            if (HighLogic.CurrentGame == null || !Allowed(facility)) return;
            MultiOptionDialog question = new MultiOptionDialog("ReDefinitionFacility", "The game is saved, and the vessel stays "
                                                                                       + "where it stands.",
                facility.Title, HighLogic.UISkin, 340f,
                new DialogGUIHorizontalLayout(
                    new DialogGUIButton("Go", () => Go(facility), 110f, 30f, true),
                    new DialogGUIFlexibleSpace(),
                    new DialogGUIButton(KSP.Localization.Localizer.Format("#autoLOC_174783"), () => { }, 110f, 30f, true)));
            UnityMouseEvents.Shield(PopupDialog.SpawnPopupDialog(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), question, false,
                HighLogic.UISkin));
        }

        // As the pause menu leaves the flight where the game can be saved
        // (PauseMenu.saveAndExit).
        private static void Go(Facility facility)
        {
            if (HighLogic.CurrentGame == null || !Allowed(facility)) return;
            ClearToSaveStatus clear = FlightGlobals.ClearToSave();
            if (clear != ClearToSaveStatus.CLEAR)
            {
                ScreenMessages.PostScreenMessage(FlightGlobals.GetNotClearToSaveStatusReason(clear, "leave the flight"), 5f,
                    ScreenMessageStyle.UPPER_CENTER);
                return;
            }
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
        private static string planned;

        internal static void Plan(string facility)
        {
            planned = facility;
        }

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
