using System;
using ReDefinition.Api.Internal;
using UnityEngine.Rendering;
using UnityEngine;

namespace ReDefinition.Upscaler
{
    // The rig's part for the hooks other mods register (ReDefinition.Api.Hooks,
    // SharedFrame): motion vectors into the capture, the upscaled image, and an
    // overlay after the scene.
    public partial class UpscalerRig
    {
        // The buffer the after-upscaling handlers record into, made with the first of them and run in Present.
        private CommandBuffer afterUpscalingBuffer;

        // The overlay: a camera behind the last camera that draws the scene, made
        // only while a handler is registered, with a buffer at AfterEverything the
        // handlers fill as it culls.
        private GameObject overlayObject;
        private Camera overlayCamera;
        private CommandBuffer overlayBuffer;
        private float overlayDepthCheck;

        // Cached, so the hooks' calls allocate nothing per frame.
        private Action<Action<CommandBuffer, RenderTexture, RenderTexture, Camera>> callMotionVectorHook;
        private Action<Action<CommandBuffer, RenderTexture, Camera>> callAfterUpscalingHook;
        private Action<Action<CommandBuffer, Camera>> callOverlayHook;
        private RenderTexture afterUpscalingImage;

        // The frame the motion vector hooks were recorded for; -1 once RecordInputs has
        // recorded the capture anew.
        private int motionVectorHooksFrame = -1;

        // Unity calls it on the components of a camera's object before that camera culls:
        // the rig sits on the scene camera. By then the frame's state is decided
        // (SharedFrame), which a handler reads, and the distant planets and the active
        // vessel are placed (ScaledSpaceMotion, VesselMotionVectors).
        private void OnPreCull()
        {
            if (textures.LowRes == null || inputs.Capture == null) return;
            // After LateUpdate, where KSP places the scaled bodies and the flight camera.
            inputs.ScaledMotion.Sample();
            inputs.CloudMotion.Sample(cam);
            if (motionVectorHooksFrame == Time.frameCount) return;
            motionVectorHooksFrame = Time.frameCount;
            SharedFrame.EnsureBegun();
            // The vessel's own motion vectors before the hooks, so a mod's for a part
            // are drawn over them.
            vesselMotion.Begin(cam, renderSize);
            Probe.GpuProfiler.Mark(inputs.Capture, "ReDefinition inputs: capture before the vessel's depth");
            vesselMotion.RecordDepth(inputs.Capture, textures.DepthCopy);
            Probe.GpuProfiler.Mark(inputs.Capture, "ReDefinition inputs: the vessel's depth");
            vesselMotion.RecordRepair(inputs.Capture, textures.MotionVectors, textures.DepthCopy);
            Probe.GpuProfiler.Mark(inputs.Capture, "ReDefinition inputs: the vessel's motion vectors");
            RecordMotionVectorHooks();
            Probe.GpuProfiler.Mark(inputs.Capture, "ReDefinition inputs: other mods' motion vector hooks");
            // After the hooks: they read the motion vectors as the upscaler gets them.
            // Diagnostic measurements only (ReDefinitionAddon.DebugMode).
            if (ReDefinitionAddon.DebugMode)
            {
                motionAudit.Record(inputs.Capture, cam, textures.MotionVectors, textures.DepthCopy, renderSize);
                vesselMotion.RecordAudit(inputs.Capture, textures.MotionVectors, textures.DepthCopy);
            }
            vesselMotion.End();
            // The camera's matrices for the vessel's shadow, without the jitter.
            if (hudLessBuffer != null && hudLessWithShadow)
            {
                vesselShadow.Begin(cam, renderSize);
                RefreshHudLessComposition();
            }
        }

        // After every OnPreCull, where Unity has built the shadow cascades from the sun's
        // direction as it stands now (CameraRedirect): the vessel's shadow for frame
        // generation, appended to the capture.
        private int vesselShadowFrame = -1;

        // The scene camera about to render: once a frame, the vessel's shadow into the capture while the
        // HUD-less capture was recorded with it.
        private void OnPreRender()
        {
            if (textures.LowRes == null || inputs.Capture == null || vesselShadowFrame == Time.frameCount) return;
            vesselShadowFrame = Time.frameCount;
            if (hudLessBuffer != null && hudLessWithShadow)
                vesselShadow.Record(inputs.Capture, textures.DepthCopy, textures.MotionVectors);
        }

        // Into the capture at BeforeImageEffects, after Unity's motion vectors and
        // EVE's clouds are written into motionVectors.
        private void RecordMotionVectorHooks()
        {
            if (!SharedFrame.MotionVectorHooks.Any) return;
            if (callMotionVectorHook == null)
                callMotionVectorHook = handler => handler(inputs.Capture, textures.MotionVectors, textures.DepthCopy, cam);
            SharedFrame.MotionVectorHooks.Invoke(callMotionVectorHook);
        }

        // In Present, once the upscaler has written image, before TUFX's effects after
        // it and before frame generation's HUD-less copy.
        private void RunAfterUpscalingHooks(RenderTexture image)
        {
            if (!SharedFrame.AfterUpscalingHooks.Any || image == null) return;
            if (afterUpscalingBuffer == null)
                afterUpscalingBuffer = new CommandBuffer { name = "ReDefinition.AfterUpscalingHooks" };
            if (callAfterUpscalingHook == null)
                callAfterUpscalingHook = handler => handler(afterUpscalingBuffer, afterUpscalingImage, cam);

            afterUpscalingBuffer.Clear();
            afterUpscalingImage = image;
            SharedFrame.AfterUpscalingHooks.Invoke(callAfterUpscalingHook);
            afterUpscalingImage = null;
            Graphics.ExecuteCommandBuffer(afterUpscalingBuffer);
        }

        // From Update: the overlay camera made, placed and taken down as handlers come
        // and go.
        private void RefreshOverlay()
        {
            if (!SharedFrame.OverlayHooks.Any || cameras.Presenter == null)
            {
                DestroyOverlay();
                return;
            }

            if (overlayObject == null)
            {
                // One destroyed from outside leaves its buffer.
                if (overlayBuffer != null)
                {
                    overlayBuffer.Release();
                    overlayBuffer = null;
                }
                overlayObject = new GameObject("ReDefinitionOverlay");
                overlayCamera = overlayObject.AddComponent<Camera>();
                overlayCamera.cullingMask = 0;
                overlayCamera.clearFlags = CameraClearFlags.Nothing;
                overlayCamera.targetTexture = null;
                overlayCamera.allowHDR = false;
                overlayCamera.allowMSAA = false;
                overlayCamera.useOcclusionCulling = false;
                overlayCamera.depthTextureMode = DepthTextureMode.None;
                overlayBuffer = new CommandBuffer { name = "ReDefinition.OverlayHooks" };
                overlayCamera.AddCommandBuffer(CameraEvent.AfterEverything, overlayBuffer);
                overlayObject.AddComponent<UpscalerOverlay>().Rig = this;
                overlayDepthCheck = 0f;
            }

            // Behind the presenter and every effect camera after it -- the cameras frame
            // generation's HUD-less copy follows -- looked at once a second, as effect
            // cameras can come later.
            if (Time.unscaledTime >= overlayDepthCheck)
            {
                overlayDepthCheck = Time.unscaledTime + 1f;
                float depth = float.MinValue;
                foreach (Camera carrier in HudLessCarriers(cameras.Presenter))
                    if (carrier != null) depth = Mathf.Max(depth, carrier.depth);
                overlayCamera.depth = depth + 0.05f;
            }
        }

        // From the overlay camera's OnPreCull: every scene camera of the frame has
        // rendered, and the handlers see the frame's final camera.
        internal void FillOverlay()
        {
            if (overlayBuffer == null) return;
            overlayBuffer.Clear();
            if (callOverlayHook == null) callOverlayHook = handler => handler(overlayBuffer, cam);
            SharedFrame.OverlayHooks.Invoke(callOverlayHook);
        }

        // Takes the overlay camera down with its buffer, while no handler is registered or with the rig.
        private void DestroyOverlay()
        {
            if (overlayBuffer != null)
            {
                if (overlayCamera != null) overlayCamera.RemoveCommandBuffer(CameraEvent.AfterEverything, overlayBuffer);
                overlayBuffer.Release();
                overlayBuffer = null;
            }
            if (overlayObject != null) Destroy(overlayObject);
            overlayObject = null;
            overlayCamera = null;
        }

        // At teardown: the overlay camera and the after-upscaling buffer.
        private void ReleaseHooks()
        {
            DestroyOverlay();
            if (afterUpscalingBuffer != null)
            {
                afterUpscalingBuffer.Release();
                afterUpscalingBuffer = null;
            }
        }
    }
}
