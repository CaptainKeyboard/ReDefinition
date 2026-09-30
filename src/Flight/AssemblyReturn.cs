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
    // A button on the space centre's assembly building while the vessel stands on
    // the ground near it (docs/player/flight-panel.md): a click, after a question,
    // goes back to that building. It stands on the middle of what the building
    // draws, and looks like the space centre's own button for it (WorldButton,
    // KscButtons).
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

        private sealed class Building
        {
            internal EditorFacility Facility;
            internal Transform Place;
            internal Vector3 Middle;
            internal Collider[] Solid;
            internal bool Measured;
        }

        private Building vab, sph;
        private CelestialBody home;
        private WorldButton marker;
        private Sprite vabIcon, sphIcon;

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
            marker = new WorldButton("ReDefinitionAssemblyReturn", Ask, 40f);
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

        // The button goes with the flight scene: KSP's main canvas stays.
        private void OnDestroy()
        {
            if (marker != null) marker.Destroy();
            marker = null;
        }

        private void LateUpdate()
        {
            if (marker == null) return;
            Building building;
            bool revert;
            if (!Wanted(out building, out revert))
            {
                marker.Hide();
                return;
            }
            Camera camera = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null;
            Vector3 middle = Middle(building);
            // Before the building's front, not inside it.
            if (!marker.Place(middle, camera, Front(building, middle, camera)))
            {
                Note("the building behind the camera or too far to see");
                return;
            }
            Note("shown on the " + building.Facility + (revert ? ", reverting" : ", recovering") + ", "
                 + ((Vector3d)building.Place.position - FlightGlobals.ActiveVessel.CoMD).magnitude.ToString("0") + " m off");
            marker.Look(building.Facility == EditorFacility.SPH ? "Buttons_SPH" : "Buttons_VAB",
                building.Facility == EditorFacility.SPH ? sphIcon : vabIcon);
            marker.Tooltip = Title(building.Facility, revert);
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

        // The middle of the building's hall, found once it is there: over the
        // middle of the largest drawn part that is also solid (a collider on it:
        // the SPH's model_sph_exterior_main, not the flat ground around it or the
        // light flare 90 m up its tower, sharedassets9), half as high as its roof
        // stands there -- a ray down onto the building's colliders; the tower
        // beside the SPH's hall would make the hall's box twice as high.
        private Vector3 Middle(Building building)
        {
            if (!building.Measured)
            {
                Vector3 ground = building.Place.position;
                Vector3 up = (ground - home.position).normalized;
                Renderer hall = null;
                float most = -1f;
                foreach (Renderer part in building.Place.GetComponentsInChildren<Renderer>(false))
                {
                    if (part.GetComponent<Collider>() == null) continue;
                    Vector3 size = part.bounds.size;
                    float volume = size.x * size.y * size.z;
                    if (volume <= most) continue;
                    most = volume;
                    hall = part;
                }
                if (hall == null) return ground;
                building.Solid = building.Place.GetComponentsInChildren<Collider>(false);
                Bounds bounds = hall.bounds;
                Vector3 extent = bounds.extents;
                float top = Vector3.Dot(bounds.center - ground, up)
                            + Mathf.Abs(extent.x * up.x) + Mathf.Abs(extent.y * up.y) + Mathf.Abs(extent.z * up.z);
                Vector3 across = bounds.center - up * Vector3.Dot(bounds.center - ground, up);
                float roof = 0f;
                Ray down = new Ray(across + up * (top + 10f), -up);
                foreach (Collider solid in building.Solid)
                {
                    RaycastHit hit;
                    if (solid != null && !solid.isTrigger && solid.Raycast(down, out hit, top + 20f))
                        roof = Mathf.Max(roof, Vector3.Dot(hit.point - ground, up));
                }
                if (roof <= 0f) roof = top;
                building.Middle = building.Place.InverseTransformPoint(across + up * (roof / 2f));
                building.Measured = true;
                Debug.Log(Log.Tag + " The " + building.Facility + "'s roof stands " + roof.ToString("0") + " m high over "
                          + hall.name + "; its button half as high.");
            }
            return building.Place.TransformPoint(building.Middle);
        }

        // How far to bring the button from the middle towards the camera to stand
        // before the building's front: to where a ray from the camera meets the
        // building, and a little more.
        private static float Front(Building building, Vector3 middle, Camera camera)
        {
            if (camera == null || building.Solid == null) return 0f;
            Vector3 eye = camera.transform.position;
            Ray look = new Ray(eye, (middle - eye).normalized);
            float distance = (middle - eye).magnitude;
            float nearest = distance;
            foreach (Collider solid in building.Solid)
            {
                RaycastHit hit;
                if (solid != null && !solid.isTrigger && solid.Raycast(look, out hit, distance))
                    nearest = Mathf.Min(nearest, hit.distance);
            }
            return distance - nearest + 2f;
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
