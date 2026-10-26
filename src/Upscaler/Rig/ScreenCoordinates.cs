using System.Collections.Generic;
using System.Reflection;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Upscaler
{
    // The screen's pixels for every camera the rig draws into its texture in place of the
    // screen.
    //
    // A camera with a target texture measures its screen space in that texture's pixels:
    // Unity's WorldToScreenPoint, ScreenPointToRay and the rest take pixelRect, the target's
    // size times the camera's rect. Everything outside the drawing means the screen's pixels
    // by them -- the mouse position (Input.mousePosition), KSP's markers on its UI canvases
    // over the map and the flight (UIMasterController, OrbitRendererBase, PatchedConics,
    // ManeuverGizmo, RectTransformUtility.WorldToScreenPoint), the picking of parts under
    // the cursor (Mouse, EditorLogic: ScreenPointToRay(Input.mousePosition)) and what the
    // mods draw over the scene or pick in it (their DLLs call them: MechJeb, Kerbal
    // Engineer, Trajectories, Waypoint Manager, Distant Object, Kopernicus, Firefly, FreeIva,
    // EVE's cloud painter). At a render size below the screen's, a part was picked away
    // from the cursor, and the markers sat away from what they mark.
    //
    // So the five conversions between the screen and the world or the viewport take and
    // give the screen's pixels for those cameras, outside of their own drawing: there
    // (Camera.current is the camera) what is drawn into the texture means its pixels, as
    // Vectrosity's lines drawn in OnPostRender and the mods' GL lines. Unity's mouse
    // event pass sees the cameras with the screen as their target (UnityMouseEvents), and
    // nothing is scaled then. The cameras' pixelWidth, pixelHeight and pixelRect stay the
    // texture's: Scatterer, EVE, TUFX and KSP's highlighting size their render targets by
    // them. KSPCommunityFixes' faster projection for Vectrosity (OptimisedVectorLines)
    // reads pixelRect and keeps the texture's pixels both ways, as its 3D lines need.
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    internal sealed class ScreenCoordinates : MonoBehaviour
    {
        // Patches the five conversions once at start-up, then removes itself.
        private void Awake()
        {
            HarmonyHooks.Safely("screen-coordinates", "the cursor and the markers miss the scene at a lower render size", () =>
            {
                const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                System.Type camera = typeof(Camera);
                System.Type eye = typeof(Camera.MonoOrStereoscopicEye);
                HarmonyHooks.Install("ReDefinition.ScreenCoordinates", typeof(ScreenCoordinates), new List<HarmonyHook>
                {
                    new HarmonyHook(camera.GetMethod("WorldToScreenPoint", any, null, new[] { typeof(Vector3), eye }, null),
                                    null, nameof(ToScreen), null),
                    new HarmonyHook(camera.GetMethod("ViewportToScreenPoint", any, null, new[] { typeof(Vector3) }, null),
                                    null, nameof(ToScreen), null),
                    new HarmonyHook(camera.GetMethod("ScreenToWorldPoint", any, null, new[] { typeof(Vector3), eye }, null),
                                    nameof(FromScreen), null, null),
                    new HarmonyHook(camera.GetMethod("ScreenToViewportPoint", any, null, new[] { typeof(Vector3) }, null),
                                    nameof(FromScreen), null, null),
                    new HarmonyHook(camera.GetMethod("ScreenPointToRay", any, null, new[] { typeof(Vector2), eye }, null),
                                    nameof(RayFromScreen), null, null),
                });
            });
            Destroy(this);
        }

        // Render pixels per screen pixel for this call, or false where it is to stay as it is.
        private static bool Scale(Camera camera, out float x, out float y)
        {
            if (!CameraRedirect.RenderPerScreen(camera, out x, out y)) return false;
            Camera drawing = Camera.current;
            return drawing == null || !CameraRedirect.InScreensPlace(drawing);
        }

        // Postfix of WorldToScreenPoint and ViewportToScreenPoint: the texture's pixels Unity answers turned
        // into the screen's.
        private static void ToScreen(Camera __instance, ref Vector3 __result)
        {
            float x, y;
            if (!Scale(__instance, out x, out y)) return;
            __result.x /= x;
            __result.y /= y;
        }

        // Prefix of ScreenToWorldPoint and ScreenToViewportPoint: the screen position given turned into the
        // texture's pixels before Unity reads it.
        private static void FromScreen(Camera __instance, ref Vector3 position)
        {
            float x, y;
            if (!Scale(__instance, out x, out y)) return;
            position.x *= x;
            position.y *= y;
        }

        // Prefix of ScreenPointToRay: the screen position turned into the texture's pixels, so the ray goes
        // through the point under the cursor.
        private static void RayFromScreen(Camera __instance, ref Vector2 pos)
        {
            float x, y;
            if (!Scale(__instance, out x, out y)) return;
            pos.x *= x;
            pos.y *= y;
        }
    }
}
