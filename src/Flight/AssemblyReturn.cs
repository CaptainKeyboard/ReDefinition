using System.Collections;
using System.IO;
using KSP.UI;
using KSP.UI.Screens;
using KSP.UI.TooltipTypes;
using ReDefinition.Core;
using ReDefinition.Window;
using UnityEngine;
using UnityEngine.UI;

namespace ReDefinition.Flight
{
    // A symbol over the space centre's assembly building while the vessel stands on
    // the ground near it (docs/player/navball.md): a click, after a question, goes
    // back to that building. Placed as KSP places its vessel labels: a point in
    // the world on KSP's main canvas (UIMasterController.WorldToMainCanvas with the
    // flight camera; VesselLabels, decompiled).
    //
    // Where KSP's pause menu offers a revert to the editor -- the game allows it
    // (Flight.CanLeaveToEditor), the state before the launch is still kept
    // (FlightDriver.CanRevertToPrelaunch) and the ship came from an editor
    // (ShipConstruction.ShipConfig) -- the symbol stands over the building it was
    // built in (ShipConstruction.ShipType) and reverts to it, in KSP's words
    // ("Revert to Vehicle Assembly", "Revert to Spaceplane Hangar"). Otherwise it
    // stands over the building for where the vessel stands -- the runway's
    // hangar, else the assembly building -- and recovers the vessel as KSP's
    // recover button does (OnVesselRecoveryRequested: saved, the space centre
    // loaded, the vessel recovered there; VesselRetrieval), then opens that
    // editor with the vessel (AssemblyAfterRecovery): the ship as it was launched
    // where that is the same ship, else its craft file by its name, else an empty
    // editor. It shows only where one of the two can be done: the vessel landed,
    // within 3 km of the building, in flight view, on the body the space centre is
    // on.
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class AssemblyReturn : MonoBehaviour
    {
        private const double Range = 3000.0;
        // Above the building's roof, in metres.
        private const float Lift = 20f;
        private const float Size = 34f;
        private static readonly Color Plate = new Color(0.13f, 0.13f, 0.16f, 0.9f);

        private sealed class Building
        {
            internal EditorFacility Facility;
            internal Transform Place;
            internal Vector3 Roof;
            internal bool Measured;
        }

        private Building vab, sph;
        private CelestialBody home;
        private RectTransform marker;
        private Image icon;
        private TooltipController_Text tooltip;
        private Sprite vabIcon, sphIcon;
        private EditorFacility shownFacility;
        private bool shownRevert;

        private IEnumerator Start()
        {
            float until = Time.unscaledTime + 15f;
            while (Time.unscaledTime < until && (UIMasterController.Instance == null || PSystemSetup.Instance == null
                                                 || FlightCamera.fetch == null))
                yield return null;
            if (UIMasterController.Instance == null || PSystemSetup.Instance == null) yield break;
            // By name: a facility's editor is the one it launches from, so the
            // launch pad counts as the VAB's and the runway as the SPH's
            // (SpaceCenterFacility.editorFacility); the buildings are
            // VehicleAssemblyBuilding and SpaceplaneHangar (the SpaceCenterFacility
            // enum KSP names its facilities by).
            System.Collections.Generic.List<string> names = new System.Collections.Generic.List<string>();
            foreach (PSystemSetup.SpaceCenterFacility facility in PSystemSetup.Instance.SpaceCenterFacilities)
            {
                if (facility == null || facility.facilityTransform == null) continue;
                names.Add(facility.name);
                EditorFacility editor;
                if (Named(facility, SpaceCenterFacility.VehicleAssemblyBuilding.ToString())) editor = EditorFacility.VAB;
                else if (Named(facility, SpaceCenterFacility.SpaceplaneHangar.ToString())) editor = EditorFacility.SPH;
                else continue;
                Building building = new Building { Facility = editor, Place = facility.facilityTransform };
                if (editor == EditorFacility.VAB) vab = building;
                else sph = building;
                if (facility.hostBody != null) home = facility.hostBody;
            }
            Debug.Log(Log.Tag + " The space centre's facilities: " + string.Join(", ", names.ToArray()) + ".");
            // The facility knows its body only where its PQS hangs under it.
            if (home == null) home = FlightGlobals.GetHomeBody();
            if (vab == null && sph == null)
            {
                Debug.Log(Log.Tag + " No assembly building found in the space centre: no way back to it from its symbol.");
                yield break;
            }
            vabIcon = Load("Assembly_VAB");
            sphIcon = Load("Assembly_SPH");
            Build();
            Debug.Log(Log.Tag + " The assembly buildings' symbols are ready: the VAB " + Where(vab) + ", the SPH " + Where(sph)
                      + ", on " + (home != null ? home.bodyName : "no body") + ".");
        }

        private static bool Named(PSystemSetup.SpaceCenterFacility facility, string name)
        {
            return facility.name == name || facility.facilityName == name
                   || (facility.facilityTransformName != null && facility.facilityTransformName.Contains(name));
        }

        private static string Where(Building building)
        {
            return building == null || building.Place == null ? "missing" : "at " + building.Place.name;
        }

        private static Sprite Load(string name)
        {
            Texture2D texture = GameDatabase.Instance != null ? GameDatabase.Instance.GetTexture("ReDefinition/Icons/" + name, false) : null;
            return texture != null ? Sprite.Create(texture, new Rect(0f, 0f, texture.width, texture.height), new Vector2(0.5f, 0.5f)) : null;
        }

        // A round plate with the building's symbol, a Button as KSP's are: behind
        // the rest of the HUD on the main canvas.
        private void Build()
        {
            Transform canvas = UIMasterController.Instance.mainCanvas.transform;
            GameObject plate = UiHygiene.Child("ReDefinitionAssemblyReturn", canvas, typeof(RectTransform), typeof(Image), typeof(Button));
            marker = (RectTransform)plate.transform;
            marker.SetAsFirstSibling();
            // KSP's canvas position is the screen's from its lower left, over the UI
            // scale (UIMasterController.WorldToMainCanvas: WorldToScreenPoint /
            // uiScale): anchored at the canvas's lower left, as KSP's labels are.
            marker.anchorMin = marker.anchorMax = Vector2.zero;
            marker.pivot = new Vector2(0.5f, 0.5f);
            marker.sizeDelta = new Vector2(Size, Size);
            Image back = plate.GetComponent<Image>();
            back.sprite = Disc();
            back.color = Plate;
            Button button = plate.GetComponent<Button>();
            button.targetGraphic = back;
            ColorBlock tint = button.colors;
            // Lighter under the pointer and pressed: the plate is dark.
            tint.highlightedColor = new Color(2f, 2f, 2.2f, 1f);
            tint.pressedColor = new Color(2.6f, 2.6f, 2.8f, 1f);
            tint.colorMultiplier = 1f;
            button.colors = tint;
            UiHygiene.MouseOnly(button);
            button.onClick.AddListener(Ask);
            tooltip = UiHygiene.Tooltip(plate, "");
            if (tooltip != null) tooltip.continuousUpdate = true;

            GameObject glyph = UiHygiene.Child("Symbol", marker, typeof(RectTransform), typeof(Image));
            RectTransform glyphRect = (RectTransform)glyph.transform;
            glyphRect.anchorMin = glyphRect.anchorMax = glyphRect.pivot = new Vector2(0.5f, 0.5f);
            glyphRect.sizeDelta = new Vector2(Size - 12f, Size - 12f);
            icon = glyph.GetComponent<Image>();
            icon.preserveAspect = true;
            icon.raycastTarget = false;
            plate.SetActive(false);
        }

        private static Sprite disc;

        private static Sprite Disc()
        {
            if (disc != null) return disc;
            const int size = 64;
            Texture2D texture = new Texture2D(size, size, TextureFormat.ARGB32, false);
            Color32[] pixels = new Color32[size * size];
            float c = (size - 1) / 2f, r = size / 2f - 0.5f;
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c));
                pixels[y * size + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(Mathf.Clamp01(r - d + 0.5f) * 255f));
            }
            texture.SetPixels32(pixels);
            texture.Apply();
            disc = Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f));
            return disc;
        }

        private void LateUpdate()
        {
            if (marker == null) return;
            Building building;
            bool revert;
            bool show = Wanted(out building, out revert);
            Camera camera = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null;
            Vector3 roof = Vector3.zero;
            if (show && camera != null)
            {
                roof = Roof(building);
                show = camera.WorldToViewportPoint(roof).z > 0f;
                if (!show) Note("the building's roof behind the camera");
            }
            else show = false;
            if (show) Note("shown over the " + building.Facility + (revert ? ", reverting" : ", recovering") + ", "
                           + ((Vector3d)building.Place.position - FlightGlobals.ActiveVessel.CoMD).magnitude.ToString("0") + " m off, "
                           + (Roof(building) - building.Place.position).magnitude.ToString("0") + " m over the building's origin");
            if (marker.gameObject.activeSelf != show) marker.gameObject.SetActive(show);
            if (!show) return;
            marker.anchoredPosition = UIMasterController.WorldToMainCanvas(roof, camera);
            if (building.Facility == shownFacility && revert == shownRevert && icon.sprite != null) return;
            shownFacility = building.Facility;
            shownRevert = revert;
            icon.sprite = building.Facility == EditorFacility.SPH ? sphIcon : vabIcon;
            if (tooltip != null) tooltip.textString = Title(building.Facility, revert);
        }

        // Where the symbol stands now, and whether it reverts; false where it is
        // not to be shown.
        private bool Wanted(out Building building, out bool revert)
        {
            building = null;
            revert = false;
            Vessel vessel = FlightGlobals.ActiveVessel;
            if (vessel == null) return Note("no vessel");
            if (MapView.MapIsEnabled) return Note("the map");
            if (vessel.mainBody != home) return Note("another body");
            if (!vessel.LandedOrSplashed && vessel.situation != Vessel.Situations.PRELAUNCH) return Note("not on the ground");
            revert = CanRevert();
            EditorFacility facility = revert ? ShipConstruction.ShipType
                : vessel.landedAt != null && vessel.landedAt.Contains("Runway") ? EditorFacility.SPH : EditorFacility.VAB;
            building = facility == EditorFacility.SPH ? sph : vab;
            if (building == null || building.Place == null) return Note("no " + facility);
            double distance = (vessel.CoMD - (Vector3d)building.Place.position).magnitude;
            if (distance > Range) return Note(distance.ToString("0") + " m from the " + facility);
            return revert || CanRecover(vessel) || Note("neither a revert nor a recovery possible");
        }

        // Why the symbol stands or not, into the log as it changes.
        private string noted;

        private bool Note(string why)
        {
            string key = why.Length > 12 && char.IsDigit(why[0]) ? "far" : why;
            if (key.StartsWith("shown")) key = "shown";
            if (key == noted) return false;
            noted = key;
            Debug.Log(Log.Tag + " Assembly building's symbol: " + why + ".");
            return false;
        }

        private static bool CanRevert()
        {
            return HighLogic.CurrentGame != null && HighLogic.CurrentGame.Parameters.Flight.CanLeaveToEditor
                   && FlightDriver.CanRevertToPrelaunch && ShipConstruction.ShipConfig != null
                   && (ShipConstruction.ShipType == EditorFacility.VAB || ShipConstruction.ShipType == EditorFacility.SPH);
        }

        private static bool CanRecover(Vessel vessel)
        {
            return HighLogic.CurrentGame != null && HighLogic.CurrentGame.Parameters.Flight.CanLeaveToSpaceCenter
                   && vessel.IsRecoverable;
        }

        private static string Title(EditorFacility facility, bool revert)
        {
            string building = facility == EditorFacility.SPH ? "Spaceplane Hangar" : "Vehicle Assembly";
            return revert ? "Revert to " + building : "Recover vessel and go to " + building;
        }

        // Over the roof: the top of what the building draws, found once it draws.
        private Vector3 Roof(Building building)
        {
            if (!building.Measured)
            {
                Renderer[] parts = building.Place.GetComponentsInChildren<Renderer>(false);
                if (parts.Length > 0)
                {
                    Bounds bounds = parts[0].bounds;
                    foreach (Renderer part in parts) bounds.Encapsulate(part.bounds);
                    Vector3 up = (building.Place.position - home.position).normalized;
                    Vector3 extent = bounds.extents;
                    float height = Mathf.Abs(extent.x * up.x) + Mathf.Abs(extent.y * up.y) + Mathf.Abs(extent.z * up.z);
                    building.Roof = building.Place.InverseTransformPoint(bounds.center + up * (height + Lift));
                    building.Measured = true;
                }
                else return building.Place.position;
            }
            return building.Place.TransformPoint(building.Roof);
        }

        // ---------------------------------------------------------------- click

        private void Ask()
        {
            Building building;
            bool revert;
            if (!Wanted(out building, out revert)) return;
            EditorFacility facility = building.Facility;
            string title = Title(facility, revert);
            string message = revert
                ? "The flight since the launch is lost."
                : "The vessel is recovered, then opened in the " + (facility == EditorFacility.SPH ? "Spaceplane Hangar." : "Vehicle Assembly Building.");
            MultiOptionDialog question = new MultiOptionDialog("ReDefinitionAssemblyReturn", message, title, HighLogic.UISkin, 340f,
                new DialogGUIHorizontalLayout(
                    new DialogGUIButton(revert ? "Revert" : "Recover", () => Go(facility, revert), 110f, 30f, true),
                    new DialogGUIFlexibleSpace(),
                    new DialogGUIButton(KSP.Localization.Localizer.Format("#autoLOC_174783"), () => { }, 110f, 30f, true)));
            UnityMouseEvents.Shield(PopupDialog.SpawnPopupDialog(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), question, false,
                HighLogic.UISkin));
        }

        private static void Go(EditorFacility facility, bool revert)
        {
            Vessel vessel = FlightGlobals.ActiveVessel;
            if (vessel == null) return;
            if (revert)
            {
                if (CanRevert()) FlightDriver.RevertToPrelaunch(facility);
                return;
            }
            // As KSP's own recover button asks (AltimeterSliderButtons).
            ClearToSaveStatus clear = FlightGlobals.ClearToSave();
            if (clear != ClearToSaveStatus.CLEAR)
            {
                ScreenMessages.PostScreenMessage(FlightGlobals.GetNotClearToSaveStatusReason(clear, "recover the vessel"), 5f,
                    ScreenMessageStyle.UPPER_CENTER);
                return;
            }
            if (!CanRecover(vessel)) return;
            AssemblyAfterRecovery.Plan(Craft(vessel, facility), facility);
            Debug.Log(Log.Tag + " Recovering " + vessel.vesselName + " for the " + facility + ".");
            GameEvents.OnVesselRecoveryRequested.Fire(vessel);
        }

        // The vessel as a craft file for the editor: the ship as it was launched,
        // where it is this ship, else its craft file by its name, in either
        // building; null for none.
        private static string Craft(Vessel vessel, EditorFacility facility)
        {
            try
            {
                ConfigNode launched = ShipConstruction.ShipConfig;
                if (launched != null && launched.GetValue("ship") == vessel.vesselName)
                {
                    string path = PluginData.Path("Recovered.craft");
                    string directory = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory)) Directory.CreateDirectory(directory);
                    launched.Save(path);
                    return path;
                }
                string name = KSPUtil.SanitizeFilename(vessel.vesselName) + ".craft";
                foreach (EditorFacility folder in new[] { facility, facility == EditorFacility.SPH ? EditorFacility.VAB : EditorFacility.SPH })
                {
                    string file = Path.Combine(ShipConstruction.GetCurrentGameShipsPathFor(folder), name);
                    if (File.Exists(file)) return file;
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning(Log.Tag + " The recovered vessel's craft could not be found: " + e.Message);
            }
            return null;
        }
    }

    // After a recovery from the assembly building's symbol: in the space centre,
    // once KSP has recovered the vessel and its report is closed
    // (MissionRecoveryDialog), the editor opens, with the vessel where its craft
    // was found.
    [KSPAddon(KSPAddon.Startup.SpaceCentre, false)]
    public class AssemblyAfterRecovery : MonoBehaviour
    {
        private static bool planned;
        private static string craft;
        private static EditorFacility facility;

        internal static void Plan(string path, EditorFacility where)
        {
            planned = true;
            craft = path;
            facility = where;
        }

        private IEnumerator Start()
        {
            if (!planned) yield break;
            planned = false;
            // KSP recovers the vessel a few frames into the scene (VesselRetrieval)
            // and shows its report: waited for, then until it is closed.
            float until = Time.unscaledTime + 5f;
            while (Time.unscaledTime < until && FindObjectOfType<MissionRecoveryDialog>() == null) yield return null;
            while (FindObjectOfType<MissionRecoveryDialog>() != null) yield return null;
            yield return null;
            Debug.Log(Log.Tag + " Opening the " + facility + (craft != null ? " with " + craft : " empty") + " after the recovery.");
            if (craft != null && File.Exists(craft)) EditorDriver.StartAndLoadVessel(craft, facility);
            else EditorDriver.StartEditor(facility);
        }
    }
}
