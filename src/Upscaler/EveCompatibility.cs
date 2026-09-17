using System;
using System.Collections;
using System.Reflection;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ReDefinition
{
    // Two parts of EVE Redux size themselves once by their camera and never
    // again, and the rig changes that size whenever it attaches, detaches or
    // changes the mode.
    //
    // * Raymarched volumetric clouds: DeferredRaymarchedVolumetricCloudsRenderer
    //   .Initialize reads targetCamera.activeTexture.width once and changes it
    //   afterwards only for screenshots (HandleScreenshotMode). EVE's own
    //   ReinitAll, which its quality settings call, drops every renderer, and
    //   EVE adds new ones as the clouds next render
    //   (DeferredRaymarchedRendererNotifier.OnWillRenderObject).
    // * Screen-space cloud shadows -- the 2D layers' shadows on the terrain with
    //   Deferred: ScreenSpaceShadowsRenderer.Init reads camera.pixelWidth once
    //   (SetRenderingResolution) and renders the shadows at half that size into
    //   the light's screen-space mask (UpdateCommandBuffer). With the camera
    //   redirected into a smaller texture they stay made for the old size, and
    //   the terrain shows vertical stripes in the cloud shadows. The renderers
    //   come from EVE's own
    //   ScreenSpaceShadowsManager (cameraToShadowsRenderer); each whose size is
    //   not its camera's any more -- at the end of the frame a redirected
    //   camera's pixelWidth is the rig texture's -- gets it anew by EVE's own
    //   SetRenderingResolution, and the manager rebuilds the command buffers
    //   (UpdateRenderers). The IVA camera's renderer is made as the view
    //   changes to IVA (RegisterInternalCamera, on OnCameraChange), before the
    //   rig has redirected that camera, so the shadows are checked at every
    //   follow-up, not only when the render size changed.
    //
    // All at the end of the frame, after every camera has rendered: ReinitAll's
    // Cleanup clears a renderer's initialised flag, and one still rendering in
    // that frame would set itself up again just before it is destroyed. The
    // clouds are rebuilt only when the render size changed: a rebuild throws
    // their history away, and EVE makes the renderers of cameras new to the rig
    // as they first render, by the camera as it then is. Whether EVE's particle
    // clouds, wet surfaces and droplets size themselves once the same way is not
    // verified.
    internal static class EveCompatibility
    {
        private const string CloudsRendererTypeName = "Atmosphere.DeferredRaymarchedVolumetricCloudsRenderer";
        private const string ShadowsRendererTypeName = "Atmosphere.ScreenSpaceShadowsRenderer";
        private const string ShadowsManagerTypeName = "Atmosphere.ScreenSpaceShadowsManager";

        private static bool resolved;
        private static MethodInfo reinitAll;
        private static FieldInfo shadowsWidth;
        private static FieldInfo shadowsHeight;
        private static MethodInfo shadowsSetResolution;
        private static FieldInfo shadowsManagerInstance;
        private static FieldInfo shadowsRenderers;
        private static MethodInfo shadowsManagerUpdate;

        public static bool Present
        {
            get
            {
                Resolve();
                return reinitAll != null || ShadowsUsable;
            }
        }

        private static bool ShadowsUsable
        {
            get
            {
                return shadowsWidth != null && shadowsHeight != null && shadowsSetResolution != null
                       && shadowsManagerInstance != null && shadowsRenderers != null && shadowsManagerUpdate != null;
            }
        }

        // rebuildClouds: whether the render size changed, which alone needs the
        // clouds rebuilt; the shadows are checked every time.
        public static void AfterRenderSizeChange(bool rebuildClouds, string reason)
        {
            Resolve();
            if (rebuildClouds) RebuildClouds(reason);
            ResizeShadows(reason);
        }

        private static void RebuildClouds(string reason)
        {
            if (reinitAll == null) return;

            try
            {
                reinitAll.Invoke(null, null);
                Debug.Log(UpscalerProbe.Tag + " EVE volumetric clouds rebuilt for the render size (" + reason + ").");
            }
            catch (Exception e)
            {
                CompatibilityLog.Warn("eve-clouds", "EVE volumetric clouds could not be rebuilt ("
                                                    + CompatibilityLog.Reason(e) + ").");
            }
        }

        private static void ResizeShadows(string reason)
        {
            if (!ShadowsUsable) return;

            // No manager, no screen-space shadows: EVE makes the renderers only
            // through it.
            Object manager = shadowsManagerInstance.GetValue(null) as Object;
            if (manager == null) return;
            IDictionary renderers = shadowsRenderers.GetValue(manager) as IDictionary;
            if (renderers == null) return;

            // One at a time: one renderer failing must not keep the others, or
            // the rebuild below, from happening.
            int resized = 0;
            foreach (DictionaryEntry entry in renderers)
            {
                try
                {
                    Camera camera = entry.Key as Camera;
                    Component renderer = entry.Value as Component;
                    if (camera == null || renderer == null) continue;

                    // Through Convert, whatever integer type a build declares.
                    if (Convert.ToInt32(shadowsWidth.GetValue(renderer)) == camera.pixelWidth
                        && Convert.ToInt32(shadowsHeight.GetValue(renderer)) == camera.pixelHeight) continue;

                    shadowsSetResolution.Invoke(renderer, null);
                    resized++;
                }
                catch (Exception e)
                {
                    CompatibilityLog.Warn("eve-shadow-renderer", "An EVE screen-space shadow renderer could not"
                                                                 + " be sized anew (" + CompatibilityLog.Reason(e) + ").");
                }
            }

            if (resized == 0) return;

            try
            {
                shadowsManagerUpdate.Invoke(manager, null);
                Debug.Log(UpscalerProbe.Tag + " EVE screen-space cloud shadows sized anew for " + resized
                          + " camera(s) (" + reason + ").");
            }
            catch (Exception e)
            {
                CompatibilityLog.Warn("eve-shadow-rebuild", "EVE's cloud shadows could not rebuild their command"
                                                            + " buffers (" + CompatibilityLog.Reason(e)
                                                            + "); stripes over the terrain may follow.");
            }
        }

        private static void Resolve()
        {
            if (resolved) return;
            resolved = true;

            Type clouds = TypeLookup.Find(CloudsRendererTypeName);
            if (clouds != null)
                reinitAll = clouds.GetMethod("ReinitAll", HostStack.Any, null, Type.EmptyTypes, null);

            Type renderer = TypeLookup.Find(ShadowsRendererTypeName);
            if (renderer != null)
            {
                shadowsWidth = renderer.GetField("screenWidth", HostStack.Any);
                shadowsHeight = renderer.GetField("screenHeight", HostStack.Any);
                shadowsSetResolution = renderer.GetMethod("SetRenderingResolution", HostStack.Any, null, Type.EmptyTypes, null);
            }

            Type manager = TypeLookup.Find(ShadowsManagerTypeName);
            if (manager != null)
            {
                shadowsManagerInstance = manager.GetField("instance", HostStack.Any);
                shadowsRenderers = manager.GetField("cameraToShadowsRenderer", HostStack.Any);
                shadowsManagerUpdate = manager.GetMethod("UpdateRenderers", HostStack.Any, null, Type.EmptyTypes, null);
            }

            if ((clouds != null && reinitAll == null) || ((renderer != null || manager != null) && !ShadowsUsable))
                Debug.LogWarning(UpscalerProbe.Tag + " EVE is installed, but not the one this mod was written"
                                 + " against; its clouds or cloud shadows may show stripes in the smaller modes.");
        }
    }
}
