using System;
using ReDefinition.Api.Internal;
using UnityEngine.Rendering;
using UnityEngine;

namespace ReDefinition.Upscaler
{
    // The hooks other mods register (ReDefinition.Api.Hooks, SharedFrame) as the rig runs them: motion vectors into
    // the capture, the upscaled image, and an overlay after the scene.
    internal sealed class RigHooks
    {
        // The buffer the after-upscaling handlers record into, made with the first of them and run in Present.
        private CommandBuffer afterUpscalingBuffer;

        // The overlay: a camera behind the last camera that draws the scene, made
        // only while a handler is registered, with a buffer at AfterEverything the
        // handlers fill as it culls.
        private GameObject overlayObject;
        private Camera overlayCamera;
        private readonly AttachedBuffer overlayAttached = new AttachedBuffer("ReDefinition.OverlayHooks", CameraEvent.AfterEverything);
        private CommandBuffer overlayBuffer { get { return overlayAttached.Buffer; } }
        private float overlayDepthCheck;
        // How often the overlay camera's depth is looked at again, as effect cameras can come later.
        private const float OverlayDepthCheckSeconds = 1f;

        // Cached, so the hooks' calls allocate nothing per frame. The cached calls read what each call is about
        // from the fields below, set before it; Release lets go of them.
        private Action<Action<CommandBuffer, RenderTexture, RenderTexture, Camera>> callMotionVectorHook;
        private Action<Action<CommandBuffer, RenderTexture, Camera>> callAfterUpscalingHook;
        private Action<Action<CommandBuffer, Camera>> callOverlayHook;
        private CommandBuffer motionCapture;
        private RenderTexture motionVectorsImage;
        private RenderTexture motionDepth;
        private Camera motionCamera;
        private RenderTexture afterUpscalingImage;
        private Camera afterUpscalingCamera;
        private Camera overlaySceneCamera;

        // Into the capture at BeforeImageEffects, after Unity's motion vectors and
        // EVE's clouds are written into motionVectors.
        internal void RecordMotionVectors(CommandBuffer capture, RenderTexture motionVectors, RenderTexture depth, Camera cam)
        {
            if (!SharedFrame.MotionVectorHooks.Any) return;
            if (callMotionVectorHook == null)
                callMotionVectorHook = handler => handler(motionCapture, motionVectorsImage, motionDepth, motionCamera);
            motionCapture = capture;
            motionVectorsImage = motionVectors;
            motionDepth = depth;
            motionCamera = cam;
            SharedFrame.MotionVectorHooks.Invoke(callMotionVectorHook);
        }

        // In Present, once the upscaler has written image, before TUFX's effects after
        // it and before frame generation's HUD-less copy.
        internal void RunAfterUpscaling(RenderTexture image, Camera cam)
        {
            if (!SharedFrame.AfterUpscalingHooks.Any || image == null) return;
            if (afterUpscalingBuffer == null)
                afterUpscalingBuffer = new CommandBuffer { name = "ReDefinition.AfterUpscalingHooks" };
            if (callAfterUpscalingHook == null)
                callAfterUpscalingHook = handler => handler(afterUpscalingBuffer, afterUpscalingImage, afterUpscalingCamera);

            afterUpscalingBuffer.Clear();
            afterUpscalingImage = image;
            afterUpscalingCamera = cam;
            SharedFrame.AfterUpscalingHooks.Invoke(callAfterUpscalingHook);
            afterUpscalingImage = null;
            Graphics.ExecuteCommandBuffer(afterUpscalingBuffer);
        }

        // From Update: the overlay camera made, placed and taken down as handlers come
        // and go.
        internal void RefreshOverlay(UpscalerRig rig, Camera presenter)
        {
            if (!SharedFrame.OverlayHooks.Any || presenter == null)
            {
                DestroyOverlay();
                return;
            }

            if (overlayObject == null)
            {
                // One destroyed from outside leaves its buffer.
                overlayAttached.Detach();
                overlayObject = new GameObject("ReDefinitionOverlay");
                overlayCamera = HelperCamera.AddTo(overlayObject, false);
                overlayAttached.AttachTo(overlayCamera);
                overlayObject.AddComponent<UpscalerOverlay>().Rig = rig;
                overlayDepthCheck = 0f;
            }

            // Behind the presenter and every effect camera after it -- the cameras frame
            // generation's HUD-less copy follows -- looked at once a second, as effect
            // cameras can come later.
            if (Time.unscaledTime >= overlayDepthCheck)
            {
                overlayDepthCheck = Time.unscaledTime + OverlayDepthCheckSeconds;
                float depth = float.MinValue;
                foreach (Camera carrier in HudLessCapture.Carriers(presenter))
                    if (carrier != null) depth = Mathf.Max(depth, carrier.depth);
                overlayCamera.depth = depth + 0.05f;
            }
        }

        // From the overlay camera's OnPreCull: every scene camera of the frame has
        // rendered, and the handlers see the frame's final camera.
        internal void FillOverlay(Camera cam)
        {
            if (overlayBuffer == null) return;
            overlayBuffer.Clear();
            if (callOverlayHook == null) callOverlayHook = handler => handler(overlayBuffer, overlaySceneCamera);
            overlaySceneCamera = cam;
            SharedFrame.OverlayHooks.Invoke(callOverlayHook);
        }

        // Takes the overlay camera down with its buffer, while no handler is registered or with the rig.
        private void DestroyOverlay()
        {
            overlayAttached.Detach();
            if (overlayObject != null) UnityEngine.Object.Destroy(overlayObject);
            overlayObject = null;
            overlayCamera = null;
        }

        // At teardown: the overlay camera, the after-upscaling buffer and what the cached calls refer to.
        internal void Release()
        {
            DestroyOverlay();
            motionCapture = null;
            motionVectorsImage = null;
            motionDepth = null;
            motionCamera = null;
            afterUpscalingCamera = null;
            overlaySceneCamera = null;
            if (afterUpscalingBuffer != null)
            {
                afterUpscalingBuffer.Release();
                afterUpscalingBuffer = null;
            }
        }
    }
}
