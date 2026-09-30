using System.Collections;
using ReDefinition.Core;
using ReDefinition.Flight;
using UnityEngine;

namespace ReDefinition.Engineer
{
    // A launch button over the launch pad seen through the VAB's door, and over
    // the runway seen from the SPH (docs/player/planner.md): the editors' scenery
    // holds the space centre's pad and runway outside (VABscenery/Background/
    // LaunchPadMedium, the pad's model; SPHscenery/SpaceCenter/Runway; level10 and
    // level11, read with UnityPy). Over the pad it stands on the launch pad itself
    // (ksp_pad_launchPad, where a vessel stands at the model's origin), not on the
    // middle of the whole complex with its tanks, ramps and flag pole. Over the
    // runway it stands where a plane is put for its launch: the runway's model
    // (instanced by its facility's level, sharedassets9) has a SpawnPoint at each
    // end, and the one nearest the SPH's camera is the end seen through the door.
    // It looks like the space centre's own button for the pad or the runway (WorldButton, KscButtons) and launches there as KSP's
    // launch button does (EditorLogic.launchVessel with the site's name), while
    // KSP's own launch button can be pressed.
    [KSPAddon(KSPAddon.Startup.EditorAny, false)]
    public class EditorLaunchButton : MonoBehaviour
    {
        private WorldButton marker;
        private Transform place;
        private string site;
        private string look;
        private Vector3 middle;
        private float reach;
        private bool measured;

        private IEnumerator Start()
        {
            float until = Time.unscaledTime + 15f;
            while (Time.unscaledTime < until && (EditorLogic.fetch == null || KSP.UI.UIMasterController.Instance == null))
                yield return null;
            if (EditorLogic.fetch == null || KSP.UI.UIMasterController.Instance == null) yield break;
            bool vab = EditorDriver.editorFacility != EditorFacility.SPH;
            string scenery = vab ? "VABscenery" : "SPHscenery";
            while (Time.unscaledTime < until && place == null)
            {
                place = Find(scenery, vab ? "LaunchPadMedium" : "Runway") ?? (vab ? Find(scenery, "LaunchPad") : null);
                if (place == null) yield return new WaitForSecondsRealtime(0.5f);
            }
            if (place == null)
            {
                Debug.Log(Log.Tag + " The editor's scenery has no " + (vab ? "launch pad" : "runway") + ": no launch button over it.");
                yield break;
            }
            site = vab ? "LaunchPad" : "Runway";
            look = vab ? "Buttons_Launchpad" : "Buttons_Runway";
            marker = new WorldButton("ReDefinitionEditorLaunch", Launch, 40f);
            marker.Tooltip = "Launch";
            Debug.Log(Log.Tag + " Launch button over the " + (vab ? "launch pad" : "runway") + " outside the "
                      + (vab ? "VAB" : "SPH") + " (" + place.name + ").");
        }

        private static Transform Find(string scenery, string name)
        {
            foreach (Transform found in FindObjectsOfType<Transform>())
            {
                if (found.name != name) continue;
                for (Transform above = found.parent; above != null; above = above.parent)
                    if (above.name == scenery) return found;
            }
            return null;
        }

        private void OnDestroy()
        {
            if (marker != null) marker.Destroy();
            marker = null;
        }

        private void LateUpdate()
        {
            if (marker == null || place == null) return;
            EditorLogic editor = EditorLogic.fetch;
            bool can = editor != null && editor.ship != null && editor.ship.parts.Count > 0 && editor.launchBtn != null
                       && editor.launchBtn.gameObject.activeInHierarchy && editor.launchBtn.interactable;
            if (!can)
            {
                marker.Hide();
                return;
            }
            if (!measured)
            {
                Transform pad = place.Find("ksp_pad_launchPad");
                Transform spawn = pad == null ? Spawn(place, editor.editorCamera) : null;
                if (pad == null && spawn == null)
                {
                    marker.Hide();
                    return;
                }
                Vector3 drawn = pad != null ? OnPad(place, pad) : spawn.position;
                middle = place.InverseTransformPoint(drawn);
                reach = pad != null ? WorldButton.Reach(pad) * 0.6f : 5f;
                measured = true;
                Debug.Log(Log.Tag + " Launch button placed on the " + (pad != null ? pad.name : spawn.parent.name + "/" + spawn.name) + ".");
            }
            if (marker.Place(place.TransformPoint(middle), editor.editorCamera, reach, true))
                marker.Look(look, null);
        }

        // On the pad's top, above the model's origin, which is where a vessel stands.
        private static Vector3 OnPad(Transform place, Transform pad)
        {
            float top = pad.position.y;
            foreach (Renderer part in pad.GetComponentsInChildren<Renderer>(false)) top = Mathf.Max(top, part.bounds.max.y);
            return new Vector3(place.position.x, top, place.position.z);
        }

        // The runway's spawn point nearest the camera; null while its model is not
        // there.
        private static Transform Spawn(Transform place, Camera camera)
        {
            Transform nearest = null;
            float best = float.MaxValue;
            foreach (Transform found in place.GetComponentsInChildren<Transform>(false))
            {
                if (found.name != "SpawnPoint") continue;
                float distance = camera != null ? (found.position - camera.transform.position).sqrMagnitude : 0f;
                if (distance >= best) continue;
                best = distance;
                nearest = found;
            }
            return nearest;
        }

        private void Launch()
        {
            EditorLogic editor = EditorLogic.fetch;
            if (editor == null || editor.launchBtn == null || !editor.launchBtn.interactable) return;
            Debug.Log(Log.Tag + " Launch from the " + site + " by the button over it.");
            editor.launchVessel(site);
        }
    }
}
