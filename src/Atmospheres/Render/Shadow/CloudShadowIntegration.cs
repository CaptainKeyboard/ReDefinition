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
    // The weather player's scene cloud-shadow (tests/ReDefinition.Tests/Atmospheres/T04/Probe) drives the forward path of this integration and
    // measures the picture with and without it; the deferred path (the sun's mask) and the disc are not run by it.
    //
    // Order against the rest of the frame, stated for the upscaler integration and the other mask writers [VERIFY in K-T1]:
    //  - Deferred: the sun's mask is multiplied by this buffer at AfterScreenspaceMask, after Unity has built the mask from the shadow maps
    //    and before the lighting pass reads it. Unity builds that mask only for a light with shadows (Light.shadows != None, as KSP's sun
    //    has); the mask holds the sun's term only, so the sky ambient is not touched and the ground under a dense cloud keeps the sky light.
    //    The multiply commutes with the other multipliers on the same event (EVE's shadows, Scatterer's caustics mask), so the order among
    //    them does not matter; a writer that REPLACES the mask would not.
    //  - The depth is the camera's depth texture, which exists at that point under deferred shading. The upscaler integration (Integration/CameraRedirect)
    //    redirects the camera into its own render-size texture, so camera.pixelWidth/Height are the render size (the buffers are recorded
    //    again when it changes), and jitters camera.projectionMatrix in OnPreRender, after this integration's OnPreCull; the depth and the
    //    reconstruction both use the jittered matrix of the frame [VERIFY: unity_CameraInvProjection is the jittered inverse]. The shadow
    //    is a function of the world position only (no history, no per-frame noise), so it needs no motion vectors and no reactive mask for
    //    DLSS; it moves with the cloud (tens of metres a second, well under a pixel a frame), and a sub-pixel change of the reconstructed
    //    position changes it by a hair; the penumbra is a few metres wide (h x 9.31 mrad / sin^2 e), not a pixel-sharp edge to flicker.
    //  - The vessel shadow layer (Upscaler/FrameGeneration/VesselShadowLayer) keeps a permanent buffer on the brightest shadow-casting
    //    directional light at the same event that COPIES the mask for the frame generation replay. This integration adds its buffers per camera in
    //    OnPreCull, so they run after the copy: the replayed vessel shadow sees the mask without the cloud shadow. Where a vessel lies in a
    //    cloud shadow its own shadow is then drawn on ground that is already dark [VERIFY in K-T1; the fix is a hook that lets the layer
    //    copy after this multiply].
    //  - Forward: the camera's buffer at AfterForwardOpaque, once the opaque geometry and its depth exist. Forward shading has no separate sun
    //    term to scale, so the colour is multiplied by 1 - share (1 - mask), where share is the sun's part of a lit surface's colour (the
    //    scene's sun light against its ambient, ForwardShare): the ambient part is kept, and a surface under a dense cloud stays lit by the
    //    sky. EVE's own forward fall-back multiplies the whole colour; ours does not.
    internal sealed class CloudShadowIntegration
    {
        private readonly CloudShadowRenderer renderer;
        private readonly Func<Camera, CloudShadowRole> classify;
        private readonly Func<Light> sun;
        private CommandBuffer maskBuffer, maskDisplayBuffer, forwardBuffer, discBuffer;
        private int recordedWidth, recordedHeight, recordedMode, recordedDivisor;
        private Light attachedLight;
        private Camera attachedCamera;
        private CameraEvent attachedEvent;
        private CommandBuffer attachedCameraBuffer;
        private bool subscribed, discRecorded;

        // The frame the buffers use; the owner (T09's integration) rewrites it each frame before the cameras cull.
        internal CloudShadowFrame Frame { get; } = new CloudShadowFrame();
        internal CloudShadowFrame DiscFrame { get; } = new CloudShadowFrame();
        internal bool Enabled { get; set; }

        // The cloud shadow on Scatterer's ocean (master plan 46.1): its delegate is ours while the shadow is on.
        private readonly ScattererOceanCloudShadow ocean;

        internal CloudShadowIntegration(CloudShadowRenderer renderer, Func<Camera, CloudShadowRole> classify, Func<Light> sun)
        {
            this.renderer = renderer;
            this.classify = classify;
            this.sun = sun;
            ocean = new ScattererOceanCloudShadow(renderer, () => Enabled);
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
            maskBuffer = new CommandBuffer { name = "ReDefinition cloud shadow (sun mask, build)" };
            maskDisplayBuffer = new CommandBuffer { name = "ReDefinition cloud shadow (sun mask, display)" };
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
            ocean.Uninstall();
            maskBuffer.Release();
            maskDisplayBuffer.Release();
            forwardBuffer.Release();
            discBuffer.Release();
            subscribed = false;
        }

        private void OnPreCull(Camera camera)
        {
            Detach();
            ocean.Update();
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
            // The shadow reads the camera's depth texture.
            camera.depthTextureMode |= DepthTextureMode.Depth;
            bool deferred = camera.actualRenderingPath == RenderingPath.DeferredShading;
            if (!deferred && AutoForwardShare) Frame.ForwardDirectShare = ForwardShare(camera);
            renderer.Apply(Frame);
            int mode = deferred ? 1 : 2;
            if (recordedWidth != w || recordedHeight != h || recordedMode != mode || recordedDivisor != Frame.Divisor)
            {
                // Each re-record is rare: the log names the path the shadow takes (PRUEFPROTOKOLL 287: the land under a closed deck kept its sunlight).
                Light l0 = sun();
                AtmospheresLog.Say("own clouds: the cloud shadow records for " + camera.name + " (" + w + " x " + h + "): " + (deferred ? "deferred, the sun's shadow mask" : "forward, the colour")
                                   + "; sun light " + (l0 != null ? l0.name + " shadows " + l0.shadows + " strength " + l0.shadowStrength.ToString("0.00") : "none")
                                   + "; shadow distance " + QualitySettings.shadowDistance.ToString("0") + " m, camera path " + camera.actualRenderingPath);
                if (deferred) renderer.RecordSunBuffer(maskBuffer, maskDisplayBuffer, w, h, Frame.Divisor);
                else renderer.RecordForwardBuffer(forwardBuffer, w, h, null, null, Frame.Divisor);
                recordedWidth = w;
                recordedHeight = h;
                recordedMode = mode;
                recordedDivisor = Frame.Divisor;
            }
            if (deferred)
            {
                Light light = sun();
                if (light == null) return;
                light.AddCommandBuffer(LightEvent.AfterScreenspaceMask, maskBuffer);
                light.AddCommandBuffer(LightEvent.AfterScreenspaceMask, maskDisplayBuffer);
                attachedLight = light;
            }
            else
            {
                camera.AddCommandBuffer(CameraEvent.AfterForwardOpaque, forwardBuffer);
                attachedCamera = camera;
                attachedEvent = CameraEvent.AfterForwardOpaque;
                attachedCameraBuffer = forwardBuffer;
            }
            // The shadow carries the scene's sun for this frame: published only where it is attached to the shadow camera (PRUEFPROTOKOLL 291).
            OwnCloudSceneLight.Publish();
        }

        // Whether the forward path reads the share of the colour that is the sun's from the scene's light and ambient each frame (the default)
        // or keeps Frame.ForwardDirectShare as the owner set it (the analytic checks set 1).
        internal bool AutoForwardShare { get; set; } = true;

        // The sun's share of a horizontal surface's colour at the camera, from the scene's sun light and ambient setting.
        private float ForwardShare(Camera camera)
        {
            Light light = sun();
            if (light == null) return 1f;
            Vector3 up = camera.transform.position - Frame.PlanetCenter;
            double cosZ = up.sqrMagnitude > 0f ? Vector3.Dot(up.normalized, Frame.TowardsSun) : 1.0;
            double sunLum = light.intensity * Luminance(light.color);
            double ambient;
            switch (RenderSettings.ambientMode)
            {
                case AmbientMode.Trilight: ambient = Luminance(RenderSettings.ambientEquatorColor); break;
                case AmbientMode.Skybox:
                    var probe = RenderSettings.ambientProbe;
                    var dirs = new[] { Vector3.up };
                    var colours = new Color[1];
                    probe.Evaluate(dirs, colours);
                    ambient = Luminance(colours[0]) * RenderSettings.ambientIntensity;
                    break;
                default: ambient = Luminance(RenderSettings.ambientLight); break;
            }
            return (float)CloudShadowMath.DirectShare(sunLum, cosZ, ambient);
        }

        private static double Luminance(Color c)
        {
            return 0.2126 * c.r + 0.7152 * c.g + 0.0722 * c.b;
        }

        private void OnPostRender(Camera camera)
        {
            if (camera == attachedCamera || attachedLight != null) Detach();
        }

        private void Detach()
        {
            if (attachedLight != null)
            {
                attachedLight.RemoveCommandBuffer(LightEvent.AfterScreenspaceMask, maskBuffer);
                attachedLight.RemoveCommandBuffer(LightEvent.AfterScreenspaceMask, maskDisplayBuffer);
            }
            attachedLight = null;
            if (attachedCamera != null && attachedCameraBuffer != null) attachedCamera.RemoveCommandBuffer(attachedEvent, attachedCameraBuffer);
            attachedCamera = null;
            attachedCameraBuffer = null;
        }
    }
}
