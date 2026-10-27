using System;
using System.Collections.Generic;
using ReDefinition.Core;
using ReDefinition.Proxy;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Raytracing
{
    // The part of RtCamera that traces after the frame, for the next one, on the test players' path (RtLateTrace).
    internal sealed partial class RtCamera
    {
        // The trace after the frame, for the next one, through RtLateTrace's OnRenderImage.
        // It is a component of its own, as a camera with any OnRenderImage draws into a texture
        // of Unity's in between and copies it each frame, which the trace before the
        // lighting, the game's, does not need.
        private RtLateTrace lateTrace;

        // Adds RtLateTrace where the trace goes after the frame, and removes it where it goes in the frame.
        private void KeepLateTrace()
        {
            bool wanted = !TraceBeforeLighting && capture != null;
            if (wanted == (lateTrace != null)) return;
            if (wanted)
            {
                lateTrace = gameObject.AddComponent<RtLateTrace>();
                lateTrace.Owner = this;
            }
            else
            {
                Destroy(lateTrace);
                lateTrace = null;
            }
        }

        // The trace after the frame (RtLateTrace): copies the frame's depth, motion and sky, sends the packet, and keeps
        // this frame's view for the next one's lighting; the frame passes through unchanged.
        internal void RenderLate(RenderTexture source, RenderTexture destination)
        {
            if (capture == null || source == null || TraceBeforeLighting)
            {
                Graphics.Blit(source, destination);
                return;
            }
            EnsureTargets(source.width, source.height);
            // The jittered projection where an upscaler set one, read once the frame is drawn.
            if (RenderedProjection != null) renderedProjection = RenderedProjection();

            command.Clear();
            command.Blit(source, depth, capture, 0);
            // The G-buffer was copied after it was filled; at a new size, not yet.
            bool deferred = cam.actualRenderingPath == RenderingPath.DeferredShading && gbufferReady;
            gbufferReady = cam.actualRenderingPath == RenderingPath.DeferredShading;
            bool withMotion = MotionVectors && (cam.depthTextureMode & DepthTextureMode.MotionVectors) != 0;
            if (withMotion) command.Blit(source, motion, capture, 4);
            Texture cube = Environment();
            if (cube != null)
            {
                command.SetGlobalTexture(RtCube, cube);
                command.SetGlobalVector(RtCubeHdr, new Vector4(1f, 1f, 0f, 0f));
                command.Blit(source, environment, capture, 5);
            }

            bool withGpu = GatherGpuInstances();
            Matrix4x4 worldToClip = FillPacket(deferred, withMotion, cube != null);
            if (withGpu) packet.Inputs[RtNative.InputInstances] = gpuInstancesHandle;
            ring.Issue(command, RtNative.RenderEvent, RtNative.FrameEvent, ref packet);
            Graphics.ExecuteCommandBuffer(command);
            Graphics.Blit(source, destination);

            previousWorldToClip = worldToClip;
            previousPosition = position;
            haveHistory = true;
            frameIndex++;
        }
    }

    // RtCamera's trace after the frame (RtCamera.RenderLate), on the same camera.
    internal sealed class RtLateTrace : MonoBehaviour
    {
        // The camera's RtCamera, which traces.
        internal RtCamera Owner;

        // Hands the frame to the owner's RenderLate; without an owner it passes through.
        private void OnRenderImage(RenderTexture source, RenderTexture destination)
        {
            if (Owner != null) Owner.RenderLate(source, destination);
            else Graphics.Blit(source, destination);
        }
    }
}
