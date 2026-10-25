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
    // middle of the whole complex with its tanks, ramps and flag pole. It looks like the space centre's own button for
    // the pad or the runway (WorldButton, KscButtons) and launches there as KSP's
    // launch button does (EditorLogic.launchVessel with the site's name), while
    // KSP's own launch button can be pressed.
    [KSPAddon(KSPAddon.Startup.EditorAny, false)]
    public class EditorLaunchButton : MonoBehaviour
    {
        // The button in the world; the pad or the runway it stands over, the launch site's name KSP takes, and the space
        // centre button it looks like; its place on top of what the pad or runway draws, in their own space, and how far it
        // is brought towards the camera; and whether that place was measured from something drawn.
        private WorldButton marker;
        private Transform place;
        private string site;
        private string look;
        private Vector3 middle;
        private float reach;
        private bool measured;

        // Waits for the editor and KSP's interface, then looks for the pad or the runway in the scenery for up to 15 seconds.
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

        // The transform of that name somewhere under the scenery of that name.
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

        // The button goes with the editor.
        private void OnDestroy()
        {
            if (marker != null) marker.Destroy();
            marker = null;
        }

        // Shows the button over the pad or the runway while KSP's own launch button can be pressed, and hides it otherwise.
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
                Vector3 drawn = Over(place);
                middle = place.InverseTransformPoint(drawn);
                Transform pad = place.Find("ksp_pad_launchPad");
                reach = WorldButton.Reach(pad != null ? pad : place) * 0.6f;
                measured = drawn != place.position;
            }
            if (marker.Place(place.TransformPoint(middle), editor.editorCamera, reach, true))
                marker.Look(look, "Launch");
        }

        // On the top of what the place draws: over the launch pad above the model's
        // origin, which is where a vessel stands; over the runway on its middle.
        private static Vector3 Over(Transform place)
        {
            Transform pad = place.Find("ksp_pad_launchPad");
            Transform drawn = pad != null ? pad : place;
            Vector3 middle = WorldButton.Middle(drawn);
            float top = middle.y;
            foreach (Renderer part in drawn.GetComponentsInChildren<Renderer>(false)) top = Mathf.Max(top, part.bounds.max.y);
            return pad != null ? new Vector3(place.position.x, top, place.position.z) : new Vector3(middle.x, top, middle.z);
        }

        // Launches as KSP's launch button does, from the site the button stands over.
        private void Launch()
        {
            EditorLogic editor = EditorLogic.fetch;
            if (editor == null || editor.launchBtn == null || !editor.launchBtn.interactable) return;
            Debug.Log(Log.Tag + " Launch from the " + site + " by the button over it.");
            editor.launchVessel(site);
        }
    }
}
