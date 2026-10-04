using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Upscaler
{
    // What the upscalers and frame generation take beside the colour, prepared on the
    // scene camera: the capture buffer at BeforeImageEffects that copies depth and
    // motion vectors into the rig's textures, the distant planets' motion
    // (ScaledSpaceMotion) and EVE's clouds (CloudMotionVectors) in those, and the masks
    // (UpscalerMasks).
    internal sealed class UpscalerInputs
    {
        internal readonly UpscalerMasks Masks = new UpscalerMasks();
        internal readonly CloudMotionVectors CloudMotion = new CloudMotionVectors();
        internal readonly ScaledSpaceMotion ScaledMotion = new ScaledSpaceMotion();

        // On the camera that produces depth and motion vectors, where the built-in
        // identifiers are valid, before the image effects; recorded every frame.
        internal CommandBuffer Capture { get; private set; }
        private Camera captureCamera;

        // Made once: the capture buffer is recorded every frame.
        private static class DepthTargets
        {
            internal static readonly RenderTargetIdentifier Global = new RenderTargetIdentifier("_CameraDepthTexture");
            internal static readonly RenderTargetIdentifier Resolved = new RenderTargetIdentifier(BuiltinRenderTextureType.ResolvedDepth);
            internal static readonly RenderTargetIdentifier Depth = new RenderTargetIdentifier(BuiltinRenderTextureType.Depth);
        }

        internal static RenderTargetIdentifier DepthIdentifier(UpscalerRig.DepthSource source)
        {
            switch (source)
            {
                case UpscalerRig.DepthSource.GlobalDepthTexture:
                    return DepthTargets.Global;
                case UpscalerRig.DepthSource.ResolvedDepth:
                    return DepthTargets.Resolved;
                default:
                    return DepthTargets.Depth;
            }
        }

        internal void AttachCapture(Camera cam, UpscalerTextures textures, UpscalerRig.DepthSource depth)
        {
            Capture = new CommandBuffer { name = "ReDefinition.CaptureInputs" };
            Capture.Blit(BuiltinRenderTextureType.MotionVectors, textures.MotionVectors);
            Capture.Blit(DepthIdentifier(depth), textures.DepthCopy);
            cam.AddCommandBuffer(CameraEvent.BeforeImageEffects, Capture);
            captureCamera = cam;
        }

        // The scene camera's inputs, recorded anew every frame: the masks follow
        // the renderers in view and their materials. They are recorded earlier in
        // the frame, in a buffer of their own (UpscalerMasks); the reactive one
        // copies the scene's depth into the depth copy for itself, and the capture
        // here copies it again at BeforeImageEffects, so what the upscaler, TUFX and
        // frame generation read does not depend on the masks.
        internal void Record(Camera cam, UpscalerTextures textures, UpscalerRig.DepthSource depth, Vector2Int renderSize,
                             bool transparency, bool reactiveRenderers, bool reactiveAutomatic)
        {
            Masks.Record(cam, transparency, reactiveRenderers, reactiveAutomatic,
                DepthIdentifier(depth), textures.DepthCopy, textures.LowRes.format, renderSize);
            // EVE's clouds in the motion vectors, for every technique and frame
            // generation (CloudMotionVectors).
            CloudMotion.Record(cam, renderSize);
            Capture.Clear();
            Capture.Blit(DepthIdentifier(depth), textures.DepthCopy);
            // The distant planets' own motion where the scene camera drew nothing
            // (ScaledSpaceMotion), then EVE's clouds over it.
            if (ScaledMotion.Active)
            {
                ScaledMotion.Merge(Capture, textures.DepthCopy, renderSize);
                CloudMotion.Capture(Capture, ScaledMotion.Merged, textures.MotionVectors, textures.DepthCopy);
            }
            else
            {
                CloudMotion.Capture(Capture, BuiltinRenderTextureType.MotionVectors, textures.MotionVectors,
                                    textures.DepthCopy);
            }
        }

        // The buffers and textures of the masks, the clouds and the distant planets.
        internal void DisposeParts()
        {
            Masks.Dispose();
            CloudMotion.Dispose();
            ScaledMotion.Dispose();
        }

        internal void ReleaseCapture()
        {
            if (Capture == null) return;
            if (captureCamera != null) captureCamera.RemoveCommandBuffer(CameraEvent.BeforeImageEffects, Capture);
            Capture.Release();
            Capture = null;
        }
    }
}
