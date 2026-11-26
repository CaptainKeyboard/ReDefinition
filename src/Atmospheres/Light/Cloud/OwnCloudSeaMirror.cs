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
        private static readonly int IdCube = Shader.PropertyToID("_SmCube"), IdForward = Shader.PropertyToID("_SmForward"), IdRight = Shader.PropertyToID("_SmRight"),
            IdUp = Shader.PropertyToID("_SmUp"), IdZenith = Shader.PropertyToID("_SmZenith"), IdP = Shader.PropertyToID("_SmP"),
            IdSsrOutput = Shader.PropertyToID("ssrOutput"), IdCopy = Shader.PropertyToID("ReDefinitionSeaMirrorSsr");

        private Material material;
        private RenderTexture cube;
        private Vector3 planetCentre;
        private Type deferredSsr, oceanBuffer;
        private float rescanAt = -1f;
        private bool failed, said, orderSaid;

        // Whether the hooks draw this frame: the own sky probe's cube is there and the contracts hold.
        internal bool Active { get { return cube != null && material != null && !failed; } }

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
            Transform t = cam.transform;
            float ty = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad), tx = ty * cam.aspect;
            Vector3 zenith = (t.position - planetCentre).normalized;
            cb.SetGlobalTexture(IdCube, cube);
            cb.SetGlobalVector(IdForward, t.forward);
            cb.SetGlobalVector(IdRight, t.right * tx);
            cb.SetGlobalVector(IdUp, t.up * ty);
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
            bool last = list.Length > 0 && ReferenceEquals(list[list.Length - 1], ours);
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
