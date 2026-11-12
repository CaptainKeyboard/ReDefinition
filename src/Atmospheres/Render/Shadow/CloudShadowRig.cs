using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Atmospheres
{
    // How a camera takes the cloud shadow.
    internal enum CloudShadowRole
    {
        None = 0,
        // A camera that draws the scene's own geometry (terrain, vessels, kerbals): the sun's mask under deferred shading, the colour under
        // forward shading.
        Scene = 1,
        // The scaled-space camera: the planet's disc is shadowed in the colour.
        Disc = 2
    }

    // Puts the cloud shadow's buffers on the sun light and on the cameras for the length of one camera's render, the way EVE's
    // ScreenSpaceShadowsRenderer does (Atmosphere/RaymarchedClouds/LightVolume/ScreenSpaceShadowsManager.cs, MIT; credit in
    // CloudShadowRenderer): added in Camera.onPreCull, taken off in Camera.onPostRender, so another camera never runs them. The buffers are
    // recorded again when the camera's size changes (the upscaler changes the render size when it attaches, detaches or changes mode; the
    // size is read from the camera each frame and not kept, which is what EveCompatibility has to force EVE's own renderer to do).
    //
    // Order against the rest of the frame, stated for the upscaler rig and the other mask writers [VERIFY in K-T1]:
    //  - Deferred: the sun's mask is multiplied by this buffer at AfterScreenspaceMask, after Unity has built the mask from the shadow maps
    //    and before the lighting pass reads it. The multiply commutes with the other multipliers on the same event (EVE's shadows, Scatterer's
    //    caustics mask, the vessel shadow layer's mask), so the order among them does not matter; a writer that REPLACES the mask would.
    //  - The depth is the camera's depth texture, which exists at that point under deferred shading; it carries the upscaler's jitter and render
    //    size. The shadow is a function of the world position only (no history, no per-frame noise), so it needs no motion vectors and no
    //    reactive mask; it moves with the cloud, slowly, and a sub-pixel change of the reconstructed position changes it by a hair.
    //  - Forward: the camera's buffer at AfterForwardOpaque, once the opaque geometry and its depth exist; it darkens everything drawn, ambient
    //    light included (the same fall-back as EVE's).
    internal sealed class CloudShadowRig
    {
        private readonly CloudShadowRenderer renderer;
        private readonly Func<Camera, CloudShadowRole> classify;
        private readonly Func<Light> sun;
        private CommandBuffer maskBuffer, forwardBuffer, discBuffer;
        private int recordedWidth, recordedHeight, recordedMode;
        private Light attachedLight;
        private Camera attachedCamera;
        private CameraEvent attachedEvent;
        private CommandBuffer attachedCameraBuffer;
        private bool subscribed, discRecorded;

        // The frame the buffers use; the owner (T09's integration) rewrites it each frame before the cameras cull.
        internal CloudShadowFrame Frame { get; } = new CloudShadowFrame();
        internal CloudShadowFrame DiscFrame { get; } = new CloudShadowFrame();
        internal bool Enabled { get; set; }

        internal CloudShadowRig(CloudShadowRenderer renderer, Func<Camera, CloudShadowRole> classify, Func<Light> sun)
        {
            this.renderer = renderer;
            this.classify = classify;
            this.sun = sun;
        }

        // The roles of the game's cameras by name: the flight cameras take the scene's shadow, the scaled-space camera the disc's.
        internal static CloudShadowRole ClassifyByName(Camera camera)
        {
            if (camera == null) return CloudShadowRole.None;
            switch (camera.name)
            {
                case "Camera 00":
                case "Camera 01":
                case "FlightCamera": return CloudShadowRole.Scene;
                case "Camera ScaledSpace": return CloudShadowRole.Disc;
                default: return CloudShadowRole.None;
            }
        }

        internal void Start()
        {
            if (subscribed) return;
            maskBuffer = new CommandBuffer { name = "ReDefinition cloud shadow (sun mask)" };
            forwardBuffer = new CommandBuffer { name = "ReDefinition cloud shadow (forward)" };
            discBuffer = new CommandBuffer { name = "ReDefinition cloud shadow (disc)" };
            Camera.onPreCull += OnPreCull;
            Camera.onPostRender += OnPostRender;
            subscribed = true;
        }

        internal void Stop()
        {
            if (!subscribed) return;
            Camera.onPreCull -= OnPreCull;
            Camera.onPostRender -= OnPostRender;
            Detach();
            maskBuffer.Release();
            forwardBuffer.Release();
            discBuffer.Release();
            subscribed = false;
        }

        private void OnPreCull(Camera camera)
        {
            Detach();
            if (!Enabled || !renderer.Ready) return;
            CloudShadowRole role = classify(camera);
            if (role == CloudShadowRole.None) return;
            int w = camera.pixelWidth, h = camera.pixelHeight;
            if (role == CloudShadowRole.Disc)
            {
                renderer.Apply(DiscFrame);
                if (!discRecorded) { renderer.RecordDiscBuffer(discBuffer); discRecorded = true; }
                camera.AddCommandBuffer(CameraEvent.AfterForwardOpaque, discBuffer);
                attachedCamera = camera;
                attachedEvent = CameraEvent.AfterForwardOpaque;
                attachedCameraBuffer = discBuffer;
                return;
            }
            renderer.Apply(Frame);
            bool deferred = camera.actualRenderingPath == RenderingPath.DeferredShading;
            int mode = deferred ? 1 : 2;
            if (recordedWidth != w || recordedHeight != h || recordedMode != mode)
            {
                if (deferred) renderer.RecordSunBuffer(maskBuffer, w, h);
                else renderer.RecordForwardBuffer(forwardBuffer, w, h);
                recordedWidth = w;
                recordedHeight = h;
                recordedMode = mode;
            }
            if (deferred)
            {
                Light light = sun();
                if (light == null) return;
                light.AddCommandBuffer(LightEvent.AfterScreenspaceMask, maskBuffer);
                attachedLight = light;
            }
            else
            {
                camera.AddCommandBuffer(CameraEvent.AfterForwardOpaque, forwardBuffer);
                attachedCamera = camera;
                attachedEvent = CameraEvent.AfterForwardOpaque;
                attachedCameraBuffer = forwardBuffer;
            }
        }

        private void OnPostRender(Camera camera)
        {
            if (camera == attachedCamera || attachedLight != null) Detach();
        }

        private void Detach()
        {
            if (attachedLight != null) attachedLight.RemoveCommandBuffer(LightEvent.AfterScreenspaceMask, maskBuffer);
            attachedLight = null;
            if (attachedCamera != null && attachedCameraBuffer != null) attachedCamera.RemoveCommandBuffer(attachedEvent, attachedCameraBuffer);
            attachedCamera = null;
            attachedCameraBuffer = null;
        }
    }
}
