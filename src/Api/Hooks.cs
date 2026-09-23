using System;
using ReDefinition.Shared;
using UnityEngine.Rendering;
using UnityEngine;

namespace ReDefinition.Api
{
    /// <summary>
    /// Places in the frame where a mod adds commands, while ReDefinition's upscaler or frame generation
    /// runs. A handler that throws is removed and logged; the others still run.
    /// </summary>
    public static class Hooks
    {
        /// <summary>
        /// Motion vectors the mod draws itself -- geometry a vertex shader moves, content that writes no
        /// depth. Called every frame as the scene camera culls, with the frame's state decided. The
        /// commands run in that camera's capture at <c>BeforeImageEffects</c>, after Unity's motion
        /// vectors and EVE's clouds are written; every upscaler and frame generation read the result.
        /// </summary>
        /// <param name="handler">Given the capture buffer; the motion vectors (<c>RGHalf</c> at render
        /// size, the current minus the previous viewport position, y up, as Unity encodes them for a camera
        /// that renders into a render texture -- see
        /// <c>ReDefinitionMotionVector</c> in ReDefinition.cginc); the scene's raw depth at the same size
        /// (<c>RFloat</c>); and the scene camera.</param>
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
        /// <see cref="D3D12.DispatchInto"/> into this buffer sees the upscaled image.
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
