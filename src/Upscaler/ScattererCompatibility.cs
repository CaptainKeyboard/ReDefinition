using System;
using System.Reflection;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ReDefinition
{
    // Scatterer's raymarched godrays -- light shafts through EVE's clouds,
    // useRaymarchedCloudGodrays in its config -- size
    // themselves by the screen, not by their camera: RaymarchedGodraysRenderer
    // .Init sets screenWidth = Screen.width, renders at a quarter of it
    // (renderWidth = screenWidth / 4; renderHeight = screenHeight / 4, or / 2
    // with terrain godrays) and tells its shader so (godrayRenderingResolution);
    // afterwards only screenshots resize them (ResizeRenderTextures). A camera
    // the rig renders smaller gets godrays made for the screen.
    //
    // A renderer gets its camera's size -- pixelWidth, which for a redirected
    // camera is the rig texture's and for any other the screen's, as Scatterer
    // takes it -- the fraction Scatterer takes of it, and Scatterer's own
    // ResizeRenderTextures, but only when the size really differs: a resize
    // releases the godrays' history as Scatterer's own screenshot resize does.
    // Checked whenever the rig attaches or detaches, and -- since Scatterer
    // makes godrays whenever it loads a body's effects, often with the rig long
    // in place (SkyNode.Init) -- straight after each Init, through a Harmony
    // postfix.
    internal static class ScattererCompatibility
    {
        private const string GodraysTypeName = "Scatterer.RaymarchedGodraysRenderer";
        private const string HarmonyId = "ReDefinition.ScattererGodrays";

        private static bool resolved;
        private static Type godraysType;
        private static FieldInfo screenWidth;
        private static FieldInfo screenHeight;
        private static FieldInfo renderWidth;
        private static FieldInfo renderHeight;
        private static FieldInfo useTerrainGodrays;
        private static FieldInfo occlusionMaterial;
        private static FieldInfo downscaledDepth;
        private static MethodInfo resize;
        private static MethodInfo init;

        public static bool Present
        {
            get
            {
                Resolve();
                return godraysType != null && screenWidth != null && screenHeight != null && renderWidth != null
                       && renderHeight != null && useTerrainGodrays != null && occlusionMaterial != null
                       && downscaledDepth != null && resize != null;
            }
        }

        // From the addon's Awake, before any scene has godrays.
        public static void InstallHook()
        {
            if (!Present || init == null) return;

            try
            {
                MethodInfo postfix = typeof(ScattererCompatibility).GetMethod(nameof(AfterInit),
                    BindingFlags.NonPublic | BindingFlags.Static);
                new HarmonyLib.Harmony(HarmonyId).Patch(init, postfix: new HarmonyLib.HarmonyMethod(postfix));
            }
            catch (Exception e)
            {
                CompatibilityLog.Warn("scatterer-hook", "Scatterer's godrays could not be hooked ("
                                                        + CompatibilityLog.Reason(e) + ").");
            }
        }

        // Runs inside Scatterer: nothing may escape.
        private static void AfterInit(object __instance, bool __result)
        {
            try
            {
                if (!__result) return;
                UpscalerAddon addon = UpscalerAddon.Instance;
                UpscalerRig rig = addon != null ? addon.CurrentRig : null;
                if (rig == null) return;   // nothing redirected: Scatterer's own size is right

                if (Size(__instance as Component))
                    Debug.Log(UpscalerProbe.Tag + " Scatterer godrays made while the upscaler runs sized for its"
                              + " camera.");
            }
            catch (Exception e)
            {
                CompatibilityLog.Warn("scatterer-godrays", "Scatterer godrays could not be sized anew ("
                                                           + CompatibilityLog.Reason(e) + ").");
            }
        }

        public static void AfterRenderSizeChange(string reason)
        {
            if (!Present) return;

            int resized = 0;
            foreach (Object found in Object.FindObjectsOfType(godraysType))
            {
                // One at a time: one failing must not keep the others.
                try
                {
                    if (Size(found as Component)) resized++;
                }
                catch (Exception e)
                {
                    CompatibilityLog.Warn("scatterer-godrays", "Scatterer godrays could not be sized anew ("
                                                               + CompatibilityLog.Reason(e) + ").");
                }
            }

            if (resized > 0)
                Debug.Log(UpscalerProbe.Tag + " Scatterer godrays sized anew for " + resized
                          + " camera(s) (" + reason + ").");
        }

        // True when the renderer was resized; false when it was right already
        // or is not set up yet -- Init sizes that one, and the hook follows.
        private static bool Size(Component renderer)
        {
            if (renderer == null) return false;

            Material material = occlusionMaterial.GetValue(renderer) as Material;
            if (material == null || downscaledDepth.GetValue(renderer) == null) return false;

            Camera camera = renderer.GetComponent<Camera>();
            if (camera == null) return false;

            int width = camera.pixelWidth;
            int height = camera.pixelHeight;
            int partWidth = width / 4;
            int partHeight = (bool)useTerrainGodrays.GetValue(renderer) ? height / 2 : height / 4;

            if ((int)renderWidth.GetValue(renderer) == partWidth && (int)renderHeight.GetValue(renderer) == partHeight)
                return false;

            screenWidth.SetValue(renderer, width);
            screenHeight.SetValue(renderer, height);
            renderWidth.SetValue(renderer, partWidth);
            renderHeight.SetValue(renderer, partHeight);
            material.SetVector("godrayRenderingResolution", new Vector2(partWidth, partHeight));
            resize.Invoke(renderer, new object[] { false });
            return true;
        }

        private static void Resolve()
        {
            if (resolved) return;
            resolved = true;

            godraysType = TypeLookup.Find(GodraysTypeName);
            if (godraysType == null) return;

            screenWidth = godraysType.GetField("screenWidth", HostStack.Any);
            screenHeight = godraysType.GetField("screenHeight", HostStack.Any);
            renderWidth = godraysType.GetField("renderWidth", HostStack.Any);
            renderHeight = godraysType.GetField("renderHeight", HostStack.Any);
            useTerrainGodrays = godraysType.GetField("useTerrainGodrays", HostStack.Any);
            occlusionMaterial = godraysType.GetField("scatteringOcclusionMaterial", HostStack.Any);
            downscaledDepth = godraysType.GetField("downscaledDepth", HostStack.Any);
            resize = godraysType.GetMethod("ResizeRenderTextures", HostStack.Any, null, new[] { typeof(bool) }, null);

            // Init(Light, SkyNode, bool, bool, int, int), returning bool: the one
            // overload there is, found without naming Scatterer's SkyNode type.
            foreach (MethodInfo method in godraysType.GetMethods(HostStack.Any))
            {
                if (method.Name == "Init" && method.ReturnType == typeof(bool) && method.GetParameters().Length == 6)
                    init = method;
            }

            if (!Present || init == null)
                Debug.LogWarning(UpscalerProbe.Tag + " Scatterer is installed, but not the one this mod was"
                                 + " written against; its godrays may keep the screen's size in the smaller modes.");
        }
    }
}
