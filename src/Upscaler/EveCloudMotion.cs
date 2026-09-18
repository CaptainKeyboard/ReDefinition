using System.Collections.Generic;
using System.Reflection;
using System;
using Object = UnityEngine.Object;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Upscaler
{
    // EVE's raymarched clouds and the upscaler's jitter.
    //
    // EVE casts the clouds' rays through unity_CameraInvProjection, the
    // projection Unity binds for the camera, but reprojects their history and
    // makes their motion vectors with the projection without the jitter:
    // DeferredRaymarchedVolumetricCloudsRenderer.OnPreRender takes
    // GL.GetGPUProjectionMatrix(VRUtils.GetNonJitteredProjectionMatrixForCamera
    // (targetCamera), false) for currentVP and keeps it for the next frame's
    // previousVP (Atmosphere.dll 3.2.2, decompiled; the matrices' use
    // disassembled from Scatterer-EVE/RaymarchCloud, which Scatterer puts in
    // place of EVE's). NVIDIA: "Ensure that all input to DLSS is properly
    // jittered. This includes secondary passes that might otherwise have not been
    // jittered" (DLSS Programming Guide, 31 March 2026, troubleshooting).
    //
    // While EVE prepares the clouds for a camera the rig jitters, a Harmony
    // postfix hands it that camera's jittered projection, and the jitter it
    // handed is kept: EVE's motion vectors, half the change in normalized device
    // coordinates since its last frame, then carry half the change of the jitter
    // too, which CloudMotionVectors takes out again through
    // _ReDefinitionCloudJitterDelta. Only inside OnPreRender -- EVE's
    // screen-space shadows and wet surfaces ask the same helper and stay as they
    // are.
    internal static class EveCloudMotion
    {
        private const string RendererTypeName = "Atmosphere.DeferredRaymarchedVolumetricCloudsRenderer";
        private const string VrUtilsTypeName = "Utils.VRUtils";
        private const string ToScreenTypeName = "Atmosphere.DeferredRaymarchedRendererToScreen";
        private const string HarmonyId = "ReDefinition.EveCloudJitter";

        // The reconstruction whose motion vectors CloudMotionVectors knows: Scatterer
        // replaces EVE's own in EVE's shader dictionary ("replaced
        // ReconstructRaymarchedClouds in EVE shader dictionary" in KSP.log), and
        // only its output was read (disassembled): the change in viewport position
        // since the last frame, below -1 where there is none, as Scatterer's
        // TemporalAntialiasing reads it.
        internal const string KnownReconstruction = "Scatterer-EVE/ReconstructRaymarchedClouds";

        // The Debug switch: jitter and motion vectors for the clouds together. Not
        // saved; on at every start.
        internal static bool Enabled = true;

        private static readonly int JitterDeltaId = Shader.PropertyToID("_ReDefinitionCloudJitterDelta");
        private static readonly int FadeId = Shader.PropertyToID("cloudFade");

        private static bool resolved;
        private static FieldInfo targetCamera;
        private static FieldInfo reconstructionShader;
        private static FieldInfo toScreen;
        private static FieldInfo compositeMaterial;
        private static Material composite;
        private static float nextCompositeLookup;

        // The camera whose clouds EVE prepares right now, on the main thread.
        private static Camera preparing;
        private static readonly Dictionary<int, string> loggedOutcomes = new Dictionary<int, string>();

        public static bool Present
        {
            get
            {
                Resolve();
                return targetCamera != null;
            }
        }

        // From the addon's Awake, before any clouds render.
        public static void InstallHook()
        {
            Resolve();
            Type renderer = TypeLookup.Find(RendererTypeName);
            if (renderer == null) return;
            // EVE with its clouds but without the helper is another build: said,
            // like any other member missing.
            Type vrUtils = TypeLookup.Find(VrUtilsTypeName);

            MethodInfo onPreRender = renderer.GetMethod("OnPreRender", HostStack.Any, null, Type.EmptyTypes, null);
            MethodInfo helper = vrUtils != null
                ? vrUtils.GetMethod("GetNonJitteredProjectionMatrixForCamera", BindingFlags.Public | BindingFlags.Static,
                                    null, new[] { typeof(Camera) }, null)
                : null;
            if (onPreRender == null || helper == null || targetCamera == null)
            {
                CompatibilityLog.Warn("eve-cloud-jitter", "EVE is installed, but not the one this mod was written against:"
                                                          + " its volumetric clouds render without the upscaler's jitter.");
                return;
            }

            try
            {
                HarmonyLib.Harmony harmony = new HarmonyLib.Harmony(HarmonyId);
                harmony.Patch(onPreRender, prefix: Hook(nameof(BeforePreRender)), finalizer: Hook(nameof(AfterPreRender)));
                harmony.Patch(helper, postfix: Hook(nameof(AfterNonJitteredProjection)));
            }
            catch (Exception e)
            {
                new HarmonyLib.Harmony(HarmonyId).UnpatchAll(HarmonyId);
                CompatibilityLog.Warn("eve-cloud-jitter", "EVE's clouds could not be hooked for the jitter ("
                                                          + CompatibilityLog.Reason(e) + ").");
            }
        }

        private static HarmonyLib.HarmonyMethod Hook(string name)
        {
            return new HarmonyLib.HarmonyMethod(typeof(EveCloudMotion).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static));
        }

        // Runs inside EVE: nothing may escape.
        private static void BeforePreRender(object __instance)
        {
            try
            {
                preparing = targetCamera.GetValue(__instance) as Camera;
            }
            catch (Exception)
            {
                preparing = null;
            }
        }

        private static Exception AfterPreRender(Exception __exception)
        {
            preparing = null;
            return __exception;
        }

        // The parameter's name is EVE's own.
        private static void AfterNonJitteredProjection(Camera cam, ref Matrix4x4 __result)
        {
            if (preparing == null || cam != preparing) return;
            try
            {
                Vector2 jitter = Vector2.zero;
                if (Enabled && Jittered(cam, out jitter)) __result = cam.projectionMatrix;
                else jitter = Vector2.zero;

                // Per camera, as EVE keeps previousVP: the frame before is the one
                // EVE last prepared this camera's clouds in.
                Vector2 previous;
                if (!lastJitter.TryGetValue(cam.GetInstanceID(), out previous)) previous = jitter;
                lastJitter[cam.GetInstanceID()] = jitter;
                // What this camera's clouds are drawn with, in this camera's render:
                // CloudMotionVectors reads it at AfterForwardAlpha.
                Shader.SetGlobalVector(JitterDeltaId, 0.5f * (jitter - previous));
            }
            catch (Exception e)
            {
                CompatibilityLog.Warn("eve-cloud-jitter", "EVE's clouds could not be given the jitter ("
                                                          + CompatibilityLog.Reason(e) + ").");
            }
        }

        // The jitter EVE was last handed per camera, by instance id.
        private static readonly Dictionary<int, Vector2> lastJitter = new Dictionary<int, Vector2>();

        // Whether the rig jitters this camera for the frame EVE prepares, and by how
        // much in normalized device coordinates. CameraRedirect jitters in its own
        // OnPreRender, and Unity calls EVE's first where the rig's component was
        // added to the camera after EVE's -- in the space centre -- so the jitter is
        // applied from here where it is not yet (EnsureJitter). EVE is handed the
        // jitter the redirect applied, as long as the camera still has the
        // projection it applied it to. GL.GetGPUProjectionMatrix, which EVE puts the
        // projection through, leaves the first two rows as they are unless it
        // renders into a texture, which EVE does not ask for.
        private static bool Jittered(Camera cam, out Vector2 jitter)
        {
            jitter = Vector2.zero;
            if (!CameraRedirect.JitterActive || cam.stereoActiveEye != Camera.MonoOrStereoscopicEye.Mono) return false;
            CameraRedirect redirect = cam.GetComponent<CameraRedirect>();
            if (redirect == null) return false;
            if (!redirect.EnsureJitter())
            {
                Note(cam, "without the upscaler's jitter -- it was not applied to the camera");
                return false;
            }
            if (cam.projectionMatrix != redirect.AppliedProjection)
            {
                Note(cam, "without the upscaler's jitter -- the camera's projection is not the one the jitter was applied to");
                return false;
            }
            jitter = redirect.AppliedJitter;
            Note(cam, "with the upscaler's jitter");
            return true;
        }

        // Each change per camera, not every frame.
        private static void Note(Camera cam, string how)
        {
            string last;
            if (loggedOutcomes.TryGetValue(cam.GetInstanceID(), out last) && last == how) return;
            loggedOutcomes[cam.GetInstanceID()] = how;
            Debug.Log(UpscalerProbe.Tag + " EVE's clouds render " + how + " on '" + cam.name + "'.");
        }

        // The shader EVE reconstructs its clouds with, once it has looked it up --
        // read from the field behind the property, whose getter would look it up
        // itself, possibly before Scatterer has put its own in place. Null before.
        internal static Shader ReconstructionShader()
        {
            Resolve();
            return reconstructionShader != null ? reconstructionShader.GetValue(null) as Shader : null;
        }

        // EVE's fade on its cloud composite as the camera that rendered last left
        // it -- EVE sets it before each camera renders --, so the scene camera's
        // value of the frame before. Read without HasProperty, which asks the
        // shader for the property (Unity: "Checks if material's shader has a
        // property of a given name") while EVE only sets it on the material. The
        // material is looked up again once a second, in case EVE made another; 1
        // where there is none.
        internal static float Fade()
        {
            Resolve();
            if (composite == null || Time.unscaledTime >= nextCompositeLookup)
            {
                nextCompositeLookup = Time.unscaledTime + 1f;
                composite = null;
                if (toScreen == null || compositeMaterial == null) return 1f;
                Object instance = toScreen.GetValue(null) as Object;
                if (instance == null) return 1f;
                composite = compositeMaterial.GetValue(instance) as Material;
                if (composite == null) return 1f;
            }
            return composite.GetFloat(FadeId);
        }

        private static void Resolve()
        {
            if (resolved) return;
            resolved = true;

            Type renderer = TypeLookup.Find(RendererTypeName);
            if (renderer == null) return;
            targetCamera = renderer.GetField("targetCamera", HostStack.Any);
            reconstructionShader = renderer.GetField("reconstructCloudShader", BindingFlags.NonPublic | BindingFlags.Static);
            // The field behind EVE's static DeferredRaymarchedRendererToScreen
            // property, whose getter creates the object when there is none.
            toScreen = renderer.GetField("deferredRaymarchedRendererToScreen", BindingFlags.NonPublic | BindingFlags.Static);
            Type screen = TypeLookup.Find(ToScreenTypeName);
            compositeMaterial = screen != null
                ? screen.GetField("compositeColorMaterial", BindingFlags.Public | BindingFlags.Instance)
                : null;
        }
    }
}
