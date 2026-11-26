using System;
using ReDefinition.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Atmospheres
{
    [AtmospheresAssumptions]
    internal static class AssumptionsSeaMirror
    {
        // Scatterer's ocean G-buffer and its switch for the screen-space reflections, read with ssrOutput (OceanCommandBuffer.RecreateCommandBuffers, 0.908).
        public static readonly ForeignAssumption ScattererOcean =
            new ForeignAssumption("atmo-scatterer-ocean-ssr", "Scatterer", "the sea mirrors the own clouds' image")
                .Type("Scatterer.OceanCommandBuffer").Type("Scatterer.ShaderProperties")
                .Literal("Scatterer.OceanCommandBuffer", "oceanGbufferDepth", "oceanGbufferNormalsAndSigma", "useOceanSSR")
                .Literal("Scatterer.ShaderProperties", "_VarianceMax")
                .Method("Scatterer.Scatterer", "get_Instance")
                .Field("Scatterer.Scatterer", "mainSettings", "Scatterer.MainSettingsReadWrite")
                .Field("Scatterer.MainSettingsReadWrite", "oceanScreenSpaceReflections", "System.Boolean")
                .TestedVersions("0.908.0.0")
                .OnChange(WhenChanged.Fallback, "the sea shows the own clouds' mean colour, not their image");

        // Deferred's screen-space reflections: their buffer added in OnPreRender, taken off in OnPostRender, their result the global ssrOutput (Deferred 1.3.5).
        public static readonly ForeignAssumption DeferredSsr =
            new ForeignAssumption("atmo-deferred-ssr-sea-mirror", "Deferred", "the sea mirrors the own clouds' image")
                .Type("Deferred.ScreenSpaceReflections")
                .Method("Deferred.ScreenSpaceReflections", "OnPreRender")
                .Method("Deferred.ScreenSpaceReflections", "OnPostRender")
                .Literal("Deferred.ScreenSpaceReflections", "ssrOutput")
                .Tested("17e177397045")
                .OnChange(WhenChanged.Fallback, "the sea shows the own clouds' mean colour, not their image");
    }

    // The sea mirrors the own clouds' image (master plan 46.1 part 4 stage D; the owner 11-26 06:0x: "Klar soll das Meer die Wolkendecke spiegeln", 09:4x: "Ich seh
    // da btw keine Spiegelung der Wolken"). Scatterer 0.908's ocean reads the reflection probe only as its ambient sky colour (mip 6 along the surface's up,
    // PRUEFPROTOKOLL 321); its mirror image is Deferred's screen-space reflections, the global ssrOutput, weighed by its alpha (where a ray met nothing, 0: the
    // raytraced hand-off writes it so, RtCamera.Ocean). The own clouds write no depth, so their rays miss. On each camera that draws Scatterer's ocean with
    // Deferred's reflections, a command buffer at BeforeImageEffectsOpaque, after Deferred's (re-added in this hook's OnPreRender, after Deferred's own
    // OnPreRender adds its buffer), copies ssrOutput and fills what it missed with the own sky probe's cube along each ocean pixel's reflected ray, and sets the
    // copy as ssrOutput for the ocean's shading (AfterImageEffectsOpaque). Rests on AssumptionsSeaMirror (the ocean's G-buffer and _VarianceMax; Deferred's
    // buffers in OnPreRender / OnPostRender, ssrOutput).
    internal sealed class OwnCloudSeaMirror
    {
        internal const string ShaderName = "ReDefinition/AtmospheresSeaMirror";
        private const float RescanSeconds = 1f;
        private static readonly int IdCube = Shader.PropertyToID("_SmCube"), IdScreenToWorld = Shader.PropertyToID("_SmScreenToWorld"),
            IdZenith = Shader.PropertyToID("_SmZenith"), IdP = Shader.PropertyToID("_SmP"),
            IdSsrOutput = Shader.PropertyToID("ssrOutput"), IdCopy = Shader.PropertyToID("ReDefinitionSeaMirrorSsr");

        private Material material;
        private RenderTexture cube;
        private Vector3 planetCentre;
        private Type deferredSsr, oceanBuffer;
        private float rescanAt = -1f;
        private bool failed, said, orderSaid, oceanSsr;

        // Whether the hooks draw this frame: the own sky probe's cube is there, the contracts hold and Scatterer's ocean reads the screen-space reflections
        // (its setting oceanScreenSpaceReflections; off, it sets useOceanSSR 0 and the pass would be work without effect: run 324 A drew it).
        internal bool Active { get { return cube != null && material != null && !failed && oceanSsr; } }

        // From the own clouds' frame, after the sky probe's update: cube null (the probe off) stops the mirror.
        internal void Update(RenderTexture skyCube, Vector3 planet)
        {
            cube = skyCube;
            planetCentre = planet;
            if (cube == null || failed) return;
            if (!AssumptionsSeaMirror.ScattererOcean.Holds || !AssumptionsSeaMirror.DeferredSsr.Holds)
            {
                cube = null;
                return;
            }
            try
            {
                if (material == null)
                {
                    string reason;
                    Shader shader = AtmosphereShaders.Get(ShaderName, out reason);
                    if (shader == null)
                    {
                        Fail("the shader " + ShaderName + " is not available (" + reason + ")");
                        return;
                    }
                    material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                }
                if (Time.realtimeSinceStartup < rescanAt) return;
                rescanAt = Time.realtimeSinceStartup + RescanSeconds;
                if (deferredSsr == null) deferredSsr = TypeLookup.Find("Deferred.ScreenSpaceReflections");
                if (oceanBuffer == null) oceanBuffer = TypeLookup.Find("Scatterer.OceanCommandBuffer");
                if (deferredSsr == null || oceanBuffer == null) return;
                oceanSsr = ScattererOceanSsr();
                if (!oceanSsr) return;
                foreach (UnityEngine.Object o in UnityEngine.Object.FindObjectsOfType(oceanBuffer))
                {
                    var c = o as Component;
                    if (c == null) continue;
                    var ssr = c.GetComponent(deferredSsr) as Behaviour;
                    Camera cam = c.GetComponent<Camera>();
                    if (ssr == null || cam == null) continue;
                    Attach(cam.gameObject, ssr);
                }
            }
            catch (Exception e)
            {
                Fail(e.Message);
            }
        }

        // Scatterer's own setting oceanScreenSpaceReflections (Scatterer.Instance.mainSettings), read once a second.
        private static bool ScattererOceanSsr()
        {
            Type scatterer = TypeLookup.Find("Scatterer.Scatterer");
            object instance = scatterer?.GetProperty("Instance", TypeLookup.Any)?.GetValue(null, null);
            object settings = instance == null ? null : scatterer.GetField("mainSettings", TypeLookup.Any)?.GetValue(instance);
            object on = settings?.GetType().GetField("oceanScreenSpaceReflections", TypeLookup.Any)?.GetValue(settings);
            return on is bool && (bool)on;
        }

        // The hook after Deferred's component on the camera's object (its OnPreRender then runs after Deferred's, which adds Deferred's buffer).
        private void Attach(GameObject go, Behaviour ssr)
        {
            Component[] all = go.GetComponents<Component>();
            int ssrAt = Array.IndexOf(all, ssr), hookAt = -1;
            OwnCloudSeaMirrorHook hook = null;
            for (int i = 0; i < all.Length; i++)
                if (all[i] is OwnCloudSeaMirrorHook)
                {
                    hook = (OwnCloudSeaMirrorHook)all[i];
                    hookAt = i;
                }
            if (hook != null && hookAt > ssrAt)
            {
                hook.Owner = this;
                return;
            }
            if (hook != null) UnityEngine.Object.Destroy(hook);
            hook = go.AddComponent<OwnCloudSeaMirrorHook>();
            hook.Owner = this;
        }

        // Records the camera's buffer for this frame (in the hook's OnPreRender).
        internal void Record(CommandBuffer cb, Camera cam)
        {
            cb.Clear();
            // Screen to world directions from the camera's own projection (jitter and any projection change included), as Scatterer's ocean unprojects
            // its G-buffer (the code check 324: the field of view alone misses a changed projection).
            Matrix4x4 view = cam.worldToCameraMatrix;
            view.SetColumn(3, new Vector4(0f, 0f, 0f, 1f));
            Vector3 zenith = (cam.transform.position - planetCentre).normalized;
            cb.SetGlobalTexture(IdCube, cube);
            cb.SetGlobalMatrix(IdScreenToWorld, (cam.projectionMatrix * view).inverse);
            cb.SetGlobalVector(IdZenith, zenith);
            // The ocean G-buffer's depth is cleared to black on a reversed depth buffer, else to white (OceanCommandBuffer.RecreateCommandBuffers).
            cb.SetGlobalVector(IdP, new Vector4(SystemInfo.usesReversedZBuffer ? 0f : 1f, Mathf.Max(0, cube.mipmapCount - 1), 0f, 0f));
            // Deferred's own size (half width, ScreenSpaceReflections.useHalfResolutionTracing); the ocean samples it with its screen's coordinates.
            cb.GetTemporaryRT(IdCopy, Mathf.Max(1, cam.pixelWidth / 2), cam.pixelHeight, 0, FilterMode.Bilinear, RenderTextureFormat.ARGBHalf);
            cb.Blit(BuiltinRenderTextureType.None, IdCopy, material, 0);
            cb.SetGlobalTexture(IdSsrOutput, IdCopy);
        }

        // Once: the order of the buffers at the event, ours to be the last after Deferred's (logged, PRUEFPROTOKOLL 322).
        internal void CheckOrder(Camera cam, CommandBuffer ours)
        {
            if (!said)
            {
                said = true;
                AtmospheresLog.Say("own clouds: the sea mirrors the own clouds through Deferred's reflections on " + cam.name);
            }
            if (orderSaid) return;
            orderSaid = true;
            CommandBuffer[] list = cam.GetCommandBuffers(CameraEvent.BeforeImageEffectsOpaque);
            var names = new string[list.Length];
            for (int i = 0; i < list.Length; i++) names[i] = list[i].name;
            // By name: GetCommandBuffers hands back new wrappers (run 6: ours last by name, not the same object).
            bool last = list.Length > 0 && list[list.Length - 1].name == ours.name;
            AtmospheresLog.Say("own clouds: the sea mirror's buffer order at BeforeImageEffectsOpaque on " + cam.name + ": " + string.Join(", ", names)
                               + (last ? " (ours last)" : " (ours NOT last)"));
        }

        internal void Dispose()
        {
            cube = null;
            if (material != null) UnityEngine.Object.Destroy(material);
            material = null;
        }

        private void Fail(string why)
        {
            failed = true;
            AtmospheresLog.WarnOnce("own clouds: the sea mirroring the own clouds' image stops: " + why);
        }
    }

    // On a camera that draws Scatterer's ocean with Deferred's reflections: adds the sea mirror's buffer each frame after Deferred's and takes it off after
    // the frame (as Deferred does with its own).
    internal sealed class OwnCloudSeaMirrorHook : MonoBehaviour
    {
        internal OwnCloudSeaMirror Owner;
        private Camera cam;
        private CommandBuffer cb;
        private bool attached;

        private void OnPreRender()
        {
            if (cam == null) cam = GetComponent<Camera>();
            if (cam == null || Owner == null || !Owner.Active)
            {
                Detach();
                return;
            }
            if (cb == null) cb = new CommandBuffer { name = "ReDefinition sea mirror" };
            Owner.Record(cb, cam);
            cam.RemoveCommandBuffer(CameraEvent.BeforeImageEffectsOpaque, cb);
            cam.AddCommandBuffer(CameraEvent.BeforeImageEffectsOpaque, cb);
            attached = true;
            Owner.CheckOrder(cam, cb);
        }

        private void OnPostRender()
        {
            Detach();
        }

        private void Detach()
        {
            if (!attached || cam == null) return;
            cam.RemoveCommandBuffer(CameraEvent.BeforeImageEffectsOpaque, cb);
            attached = false;
        }

        private void OnDestroy()
        {
            Detach();
            if (cb != null) cb.Release();
            cb = null;
        }
    }
}
