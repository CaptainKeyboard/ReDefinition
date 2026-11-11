using System;
using ReDefinition.Api.Internal;
using UnityEngine.Rendering;
using UnityEngine;

namespace ReDefinition.Api
{
    /// <summary>
    /// Places in the frame where a mod adds commands, while ReDefinition's upscaler or frame generation
    /// runs. A handler that throws is removed and logged; the others still run.
    /// The motion vectors of <see cref="RegisterMotionVectors"/> are the current minus the previous viewport
    /// position, y up, as Unity encodes them for a camera that renders into a render texture.
    /// </summary>
    public static class Hooks
    {
        /// <summary>
        /// Motion vectors the mod draws itself, for geometry a vertex shader moves and content that writes
        /// no depth. Called every frame as the scene camera culls, with the frame's state decided. The
        /// commands run in that camera's capture at <c>BeforeImageEffects</c>, after Unity's motion
        /// vectors and EVE's clouds are written. Every upscaler and frame generation read the result.
        /// </summary>
        /// <param name="handler">Given the capture buffer. Next the motion vectors, <c>RGHalf</c> at render
        /// size (<c>ReDefinitionMotionVector</c> in ReDefinition.cginc).
        /// Next the scene's raw depth at the same size (<c>RFloat</c>), and the scene camera.</param>
        public static void RegisterMotionVectors(Action<CommandBuffer, RenderTexture, RenderTexture, Camera> handler)
        {
            SharedFrame.MotionVectorHooks.Add(handler);
        }

        /// <summary>
        /// Removes a handler registered with <see cref="RegisterMotionVectors"/>.
        /// </summary>
        /// <param name="handler">The handler registered.</param>
        public static void UnregisterMotionVectors(Action<CommandBuffer, RenderTexture, RenderTexture, Camera> handler)
        {
            SharedFrame.MotionVectorHooks.Remove(handler);
        }

        /// <summary>
        /// An effect on the upscaled image. Called every frame the rig presents, with a buffer executed
        /// at once from <c>OnRenderImage</c>, after the upscaler and before TUFX's effects after it and
        /// frame generation's copy of the scene. What is drawn is interpolated with the scene.
        /// A <see cref="D3D12.DispatchInto"/> into this buffer sees the upscaled image.
        /// </summary>
        /// <param name="handler">Given the buffer; the upscaled image at display size, HDR, to draw onto;
        /// and the scene camera.</param>
        public static void RegisterAfterUpscaling(Action<CommandBuffer, RenderTexture, Camera> handler)
        {
            SharedFrame.AfterUpscalingHooks.Add(handler);
        }

        /// <summary>
        /// Removes a handler registered with <see cref="RegisterAfterUpscaling"/>.
        /// </summary>
        /// <param name="handler">The handler registered.</param>
        public static void UnregisterAfterUpscaling(Action<CommandBuffer, RenderTexture, Camera> handler)
        {
            SharedFrame.AfterUpscalingHooks.Remove(handler);
        }

        /// <summary>
        /// An effect on the upscaled image that must be hidden behind geometry. Called in the same place as
        /// <see cref="RegisterAfterUpscaling"/>, from the same buffer, with the scene's depth as well. The
        /// depth is the rig's own copy, the texture the upscaler and frame generation read: the capture buffer
        /// at <c>BeforeImageEffects</c> of the scene camera blits Unity's resolved depth buffer
        /// (<c>BuiltinRenderTextureType.ResolvedDepth</c>, the rig's default <c>DepthSource</c>) into it, and the
        /// active vessel's depth is then drawn into it where the vessel is nearer. It holds what the deferred
        /// pass drew and the vessel's renderers, and nothing of what the forward pass draws after that (a forward-only
        /// object leaves 0). [VERIFY] That the copy holds the scene at that event is the product's own working
        /// (DLSS, FSR 3 and frame generation read it) and the wall test of series K-W1 (metric
        /// <c>C24.depth_hook_depth_ok</c>, with and without Scatterer, with <c>DepthSource</c>
        /// <c>ResolvedDepth</c> and <c>GlobalDepthTexture</c>) has not run. Unity's global
        /// <c>_CameraDepthTexture</c> and <c>BuiltinRenderTextureType.Depth</c> do not hold the scene at
        /// <c>BeforeImageEffects</c> (K-pre reconnaissance P6, <c>tests/data/recon/RECON.md</c>); an effect that needs
        /// the depth of the transparent queue takes its own copy at <c>BeforeForwardAlpha</c>.
        /// </summary>
        /// <param name="handler">Given the buffer; the upscaled image at display size, HDR, to draw onto;
        /// the scene's raw hardware depth at render size, <c>RFloat</c>, point-filtered (on Direct3D 11 a
        /// reversed Z, <c>SystemInfo.usesReversedZBuffer</c>: 1 is the near plane, 0 the far one [VERIFY the
        /// polarity in the wall test: a near and a far object in view]; read it with
        /// <c>ReDefinitionSampleSceneDepth</c> in ReDefinition.cginc, which follows <c>UNITY_REVERSED_Z</c>);
        /// and the scene camera.</param>
        public static void RegisterAfterUpscalingWithDepth(
            Action<CommandBuffer, RenderTexture, RenderTexture, Camera> handler)
        {
            SharedFrame.AfterUpscalingDepthHooks.Add(handler);
        }

        /// <summary>
        /// Removes a handler registered with <see cref="RegisterAfterUpscalingWithDepth"/>.
        /// </summary>
        /// <param name="handler">The handler registered.</param>
        public static void UnregisterAfterUpscalingWithDepth(
            Action<CommandBuffer, RenderTexture, RenderTexture, Camera> handler)
        {
            SharedFrame.AfterUpscalingDepthHooks.Remove(handler);
        }

        /// <summary>
        /// Lines and markers in the world, drawn after the scene. Called every frame as a camera of
        /// ReDefinition's culls, behind every camera that draws the scene and before the UI cameras; its
        /// buffer draws onto the backbuffer at display size at that camera's <c>AfterEverything</c>. Not
        /// upscaled, and UI to frame generation: not interpolated. The camera exists only while a handler
        /// is registered.
        /// </summary>
        /// <param name="handler">Given the buffer and the scene camera, whose
        /// <c>worldToCameraMatrix</c> and <c>nonJitteredProjectionMatrix</c> place the drawing.</param>
        public static void RegisterOverlay(Action<CommandBuffer, Camera> handler)
        {
            SharedFrame.OverlayHooks.Add(handler);
        }

        /// <summary>
        /// Removes a handler registered with <see cref="RegisterOverlay"/>.
        /// </summary>
        /// <param name="handler">The handler registered.</param>
        public static void UnregisterOverlay(Action<CommandBuffer, Camera> handler)
        {
            SharedFrame.OverlayHooks.Remove(handler);
        }
    }
}
