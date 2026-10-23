using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using System;
using Object = UnityEngine.Object;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Upscaler
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
    // takes it -- the fraction Scatterer takes of it, as Scatterer's Init left it
    // in screenWidth over renderWidth, and Scatterer's own ResizeRenderTextures,
    // but only when the size really differs: a resize releases the godrays'
    // history as Scatterer's own screenshot resize does.
    // Checked whenever the rig attaches or detaches, and -- since Scatterer
    // makes godrays whenever it loads a body's effects, often with the rig long
    // in place (SkyNode.Init) -- straight after each Init, through a Harmony
    // postfix.
    //
    // And their history leaves copies of a vessel's outline in the sky while the
    // camera turns. RaymarchScatteringOcclusion reads last frame's occlusion
    // (historyGodrayOcclusionBuffer) and its depth (historyGodrayDepthBuffer) at
    // the reprojected position and keeps the occlusion where the depth matches --
    // sky against sky, geometry against geometry (disassembled from
    // scatterershaders-directx). The depth texture is made with point filtering,
    // the occlusion texture with bilinear (Init: CreateVRFlipFlopRT ... ARGBHalf,
    // FilterMode.Bilinear), at a quarter of the width. Beside a vessel's edge the
    // depth read is the sky's and passes, while the occlusion read mixes in the
    // vessel's texel; the sky keeps about two thirds of its history a frame at
    // slow turns, so each frame's edge stays behind as one more fading outline.
    // Measured with the scene probe, the upscaler, frame generation and
    // raytracing off: the outlines go with useRaymarchedCloudGodrays off, stay
    // with the terrain godrays off, and go with the history read with point
    // filtering. The same texture is the next frame's history and this frame's
    // godrays, which the sky upsamples, so it is set per frame after Scatterer's
    // OnPreCull has chosen the pair: the history it reads to point, the one it
    // writes to bilinear, as Scatterer made it.
    internal static class ScattererCompatibility
    {
        private const string GodraysTypeName = "Scatterer.RaymarchedGodraysRenderer";
        private const string HarmonyId = "ReDefinition.ScattererGodrays";
        // Its own id: a failure takes back only this hook, not the one above.
        private const string HarmonyHistoryId = "ReDefinition.ScattererGodrayHistory";

        private static bool resolved;
        private static Type godraysType;
        private static FieldInfo screenWidth;
        private static FieldInfo screenHeight;
        private static FieldInfo renderWidth;
        private static FieldInfo renderHeight;
        private static FieldInfo occlusionMaterial;
        private static FieldInfo downscaledDepth;
        private static MethodInfo resize;
        private static MethodInfo init;
        private static MethodInfo preCull;
        private static FieldInfo godraysTextures;
        private static FieldInfo useFlipBuffer;
        private static FieldInfo targetCamera;
        private static bool historyWarned;

        public static bool Present
        {
            get
            {
                Resolve();
                return godraysType != null && screenWidth != null && screenHeight != null && renderWidth != null
                       && renderHeight != null && occlusionMaterial != null && downscaledDepth != null && resize != null;
            }
        }

        // From the addon's Awake, before any scene has godrays.
        public static void InstallHook()
        {
            Resolve();
            if (godraysType == null) return;
            // Its code checked first, the sizing's (ForeignAssumptions.ScattererGodrays) and
            // the history's (ScattererGodrayHistory): where it does not hold, that hook
            // does not go in, and the report says why.
            if (ForeignAssumptions.ScattererGodrays.Holds && Present && init != null)
            {
                try
                {
                    HarmonyHooks.Install(HarmonyId, typeof(ScattererCompatibility),
                        new List<HarmonyHook> { new HarmonyHook(init, null, nameof(AfterInit), null) });
                }
                catch (Exception e)
                {
                    CompatibilityLog.Warn("scatterer-hook", "Scatterer's godrays could not be hooked ("
                                                            + CompatibilityLog.Reason(e) + ").");
                }
            }

            if (!ForeignAssumptions.ScattererGodrayHistory.Holds
                || preCull == null || godraysTextures == null || useFlipBuffer == null || targetCamera == null)
            {
                CompatibilityLog.Warn("scatterer-godray-history", "Scatterer's godrays are not the ones this mod was"
                                      + " written against; their history may leave outlines of a vessel in the sky.");
                return;
            }
            CompileHistoryReads();
            try
            {
                HarmonyHooks.Install(HarmonyHistoryId, typeof(ScattererCompatibility),
                    new List<HarmonyHook> { new HarmonyHook(preCull, null, nameof(AfterPreCull), null) });
            }
            catch (Exception e)
            {
                CompatibilityLog.Warn("scatterer-godray-history", "Scatterer's godray history could not be hooked ("
                                                                  + CompatibilityLog.Reason(e) + ").");
            }
        }

        // Read every frame per renderer through compiled calls: godraysRT is a
        // FlipFlop<FlipFlop<RenderTexture>> struct, which FieldInfo.GetValue and its
        // indexer's PropertyInfo box at each read, and so is useFlipBuffer's bool.
        // Null where they could not be made, or once a call was refused: then by
        // reflection, as before.
        private static Func<object, bool, bool, RenderTexture> pickTexture;
        private static Func<object, Camera> cameraOf;
        private static Func<object, bool> flipBufferOf;
        // Scatterer's OnPreCull renders nothing while its sky node is gone or in
        // scaled space; nothing to set then. Null where not found: set anyway.
        private static Func<object, Object> skyNodeOf;
        private static Func<object, bool> inScaledSpaceOf;

        // Runs inside Scatterer: nothing may escape.
        private static void AfterPreCull(object __instance)
        {
            if (pickTexture != null)
            {
                try
                {
                    if (skyNodeOf != null && inScaledSpaceOf != null)
                    {
                        Object sky = skyNodeOf(__instance);
                        if (sky == null || inScaledSpaceOf(sky)) return;
                    }
                    Camera eyeCamera = cameraOf(__instance);
                    if (eyeCamera == null) return;
                    bool right = eyeCamera.stereoActiveEye == Camera.MonoOrStereoscopicEye.Right;
                    bool writes = flipBufferOf(__instance);
                    Filter(pickTexture(__instance, right, !writes), FilterMode.Point);
                    Filter(pickTexture(__instance, right, writes), FilterMode.Bilinear);
                    return;
                }
                catch (MemberAccessException)
                {
                    // A runtime that refuses compiled calls at Scatterer's private
                    // fields: by reflection from the next frame on.
                    pickTexture = null;
                    return;
                }
                catch (Exception e)
                {
                    WarnHistory(e);
                    return;
                }
            }
            try
            {
                Camera camera = targetCamera.GetValue(__instance) as Camera;
                object perEye = godraysTextures.GetValue(__instance);
                if (camera == null || perEye == null) return;
                object pair = Pick(perEye, camera.stereoActiveEye == Camera.MonoOrStereoscopicEye.Right);
                bool written = (bool)useFlipBuffer.GetValue(__instance);
                Filter(Pick(pair, !written) as RenderTexture, FilterMode.Point);
                Filter(Pick(pair, written) as RenderTexture, FilterMode.Bilinear);
            }
            catch (Exception e)
            {
                WarnHistory(e);
            }
        }

        private static void WarnHistory(Exception e)
        {
            if (historyWarned) return;
            historyWarned = true;
            CompatibilityLog.Warn("scatterer-godray-history", "Scatterer's godray history could not be set to"
                                  + " point filtering (" + CompatibilityLog.Reason(e) + ").");
        }

        private static void CompileHistoryReads()
        {
            try
            {
                PropertyInfo outer = godraysTextures.FieldType.GetProperty("Item", TypeLookup.Any, null, null,
                                                                           new[] { typeof(bool) }, null);
                PropertyInfo inner = outer != null
                    ? outer.PropertyType.GetProperty("Item", TypeLookup.Any, null, null, new[] { typeof(bool) }, null)
                    : null;
                if (inner == null || inner.PropertyType != typeof(RenderTexture)) return;
                ParameterExpression target = Expression.Parameter(typeof(object), "target");
                ParameterExpression eye = Expression.Parameter(typeof(bool), "eye");
                ParameterExpression buffer = Expression.Parameter(typeof(bool), "buffer");
                Expression pair = Expression.Property(
                    Expression.Field(Expression.Convert(target, godraysType), godraysTextures), outer, eye);
                Func<object, bool, bool, RenderTexture> pick = Expression.Lambda<Func<object, bool, bool, RenderTexture>>(
                    Expression.Property(pair, inner, buffer), target, eye, buffer).Compile();
                cameraOf = CompiledGetter.Of<Camera>(targetCamera);
                flipBufferOf = CompiledGetter.Of<bool>(useFlipBuffer);
                FieldInfo skyNode = godraysType.GetField("parentSkyNode", TypeLookup.Any);
                skyNodeOf = CompiledGetter.Of<Object>(skyNode);
                inScaledSpaceOf = skyNode != null
                    ? CompiledGetter.Of<bool>(skyNode.FieldType.GetField("inScaledSpace", TypeLookup.Any))
                    : null;
                if (cameraOf != null && flipBufferOf != null) pickTexture = pick;
            }
            catch (Exception e)
            {
                // By reflection, as before.
                Debug.Log(Log.Tag + " Scatterer's godray history is read by reflection (" + CompatibilityLog.Reason(e) + ").");
            }
        }

        // Scatterer's FlipFlop<T>: its indexer by bool, per closed type.
        private static readonly Dictionary<Type, PropertyInfo> indexers = new Dictionary<Type, PropertyInfo>();
        // The indexer's argument, made once: read every frame per renderer.
        private static readonly object[] FlipTrue = { true };
        private static readonly object[] FlipFalse = { false };

        private static object Pick(object flipFlop, bool flip)
        {
            if (flipFlop == null) return null;
            Type type = flipFlop.GetType();
            PropertyInfo item;
            if (!indexers.TryGetValue(type, out item))
            {
                item = type.GetProperty("Item", TypeLookup.Any, null, null, new[] { typeof(bool) }, null);
                indexers[type] = item;
            }
            return item != null ? item.GetValue(flipFlop, flip ? FlipTrue : FlipFalse) : null;
        }

        private static void Filter(RenderTexture texture, FilterMode mode)
        {
            if (texture != null && texture.filterMode != mode) texture.filterMode = mode;
        }

        // Runs inside Scatterer: nothing may escape.
        private static void AfterInit(object __instance, bool __result)
        {
            try
            {
                if (!__result) return;
                // Init has just set the fractions anew -- with or without terrain
                // godrays: read again.
                Component component = __instance as Component;
                if (component != null) divisors.Remove(component.GetInstanceID());
                UpscalerRig rig = UpscalerRig.Current;
                if (rig == null) return;   // nothing redirected: Scatterer's own size is right

                if (Size(__instance as Component))
                    Debug.Log(Log.Tag + " Scatterer godrays made while the upscaler runs sized for its"
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
            if (!Present || !ForeignAssumptions.ScattererGodrays.Holds) return;

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
                Debug.Log(Log.Tag + " Scatterer godrays sized anew for " + resized
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

            Vector2Int divisor = Divisor(renderer);
            if (divisor.x <= 0 || divisor.y <= 0) return false;
            int width = camera.pixelWidth;
            int height = camera.pixelHeight;
            int partWidth = width / divisor.x;
            int partHeight = height / divisor.y;

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

        // The fraction of its screen Scatterer renders a renderer's godrays at, as its
        // Init set them -- a quarter of the width, a half or a quarter of the height --,
        // read once per renderer before anything here changes them: afterwards both
        // sizes are the ones set here, in the same ratio. Zero where Init has not run.
        private static readonly Dictionary<int, Vector2Int> divisors = new Dictionary<int, Vector2Int>();

        private static Vector2Int Divisor(Component renderer)
        {
            Vector2Int divisor;
            if (divisors.TryGetValue(renderer.GetInstanceID(), out divisor)) return divisor;
            int fullWidth = (int)screenWidth.GetValue(renderer), fullHeight = (int)screenHeight.GetValue(renderer);
            int partWidth = (int)renderWidth.GetValue(renderer), partHeight = (int)renderHeight.GetValue(renderer);
            if (fullWidth <= 0 || fullHeight <= 0 || partWidth <= 0 || partHeight <= 0) return Vector2Int.zero;
            divisor = new Vector2Int(Mathf.RoundToInt((float)fullWidth / partWidth), Mathf.RoundToInt((float)fullHeight / partHeight));
            divisors[renderer.GetInstanceID()] = divisor;
            return divisor;
        }

        private static void Resolve()
        {
            if (resolved) return;
            resolved = true;

            godraysType = TypeLookup.Find(GodraysTypeName);
            if (godraysType == null) return;

            screenWidth = godraysType.GetField("screenWidth", TypeLookup.Any);
            screenHeight = godraysType.GetField("screenHeight", TypeLookup.Any);
            renderWidth = godraysType.GetField("renderWidth", TypeLookup.Any);
            renderHeight = godraysType.GetField("renderHeight", TypeLookup.Any);
            occlusionMaterial = godraysType.GetField("scatteringOcclusionMaterial", TypeLookup.Any);
            downscaledDepth = godraysType.GetField("downscaledDepth", TypeLookup.Any);
            resize = godraysType.GetMethod("ResizeRenderTextures", TypeLookup.Any, null, new[] { typeof(bool) }, null);
            preCull = godraysType.GetMethod("OnPreCull", TypeLookup.Any, null, Type.EmptyTypes, null);
            godraysTextures = godraysType.GetField("godraysRT", TypeLookup.Any);
            useFlipBuffer = godraysType.GetField("useFlipBuffer", TypeLookup.Any);
            targetCamera = godraysType.GetField("targetCamera", TypeLookup.Any);

            // Init(Light, SkyNode, bool, bool, int, int), returning bool: the one
            // overload there is, found without naming Scatterer's SkyNode type.
            foreach (MethodInfo method in godraysType.GetMethods(TypeLookup.Any))
            {
                if (method.Name == "Init" && method.ReturnType == typeof(bool) && method.GetParameters().Length == 6)
                    init = method;
            }

            if (!Present || init == null)
                Debug.LogWarning(Log.Tag + " Scatterer is installed, but not the one this mod was"
                                 + " written against; its godrays may keep the screen's size in the smaller modes.");
        }
    }
}
