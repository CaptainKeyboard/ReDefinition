using System;
using System.Collections.Generic;
using ReDefinition.Core;
using ReDefinition.Proxy;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Raytracing
{
    // The part of RtCamera that traces the sea: the reflections Scatterer's ocean and KSP's own sea take from the
    // rays, the discs added to the ocean once it is drawn, and the targets those passes use.
    internal sealed partial class RtCamera
    {
        // Scatterer's ocean's reflections from the rays, in this frame. Its surface goes from
        // its G-buffer into the shape of Unity's. It is traced as a second frame of the same
        // camera and scene with the reflections alone, and handed to the ocean where it
        // reads Deferred's screen-space reflections (ssrOutput). Where a reflected ray
        // meets nothing the weight is 0, and the ocean keeps its own reflection of Scatterer's sky.
        // KSP's own sea (`stockSea`) is traced the same way, as StockSeaTraced says.
        private void TraceOcean(bool wanted, bool stockSea, CommandBuffer into = null)
        {
            StockSeaTraced = wanted && stockSea;
            bool scatterer = wanted && !stockSea;
            AttachOceanDiscs(scatterer && !OceanDiscsHeldOff);
            int mode = !wanted ? 0 : stockSea ? 1 : 2;
            if (mode != oceanMode)
            {
                Debug.Log("[ReDefinition] Raytracing: " + (mode == 0 ? "the sea's own reflections back."
                                                               : mode == 1 ? "KSP's sea takes the raytraced reflections."
                                                               : "Scatterer's ocean takes the raytraced reflections."));
                oceanMode = mode;
            }
            if (scatterer != oceanAttached)
            {
                if (scatterer) cam.AddCommandBuffer(OceanEvent, oceanCommand);
                else cam.RemoveCommandBuffer(OceanEvent, oceanCommand);
                oceanAttached = scatterer;
            }
            if (!wanted) return;
            if (stockSea && into == null) return;
            EnsureOceanTargets();
            RenderTargetIdentifier source = BuiltinRenderTextureType.CameraTarget;
            if (scatterer)
            {
                into = oceanCommand;
                // The camera's screen to world directions, its position taken out: the
                // projection without the upscaler's jitter, as the reflections' rays go.
                Matrix4x4 view = cam.worldToCameraMatrix;
                view.SetColumn(3, new Vector4(0f, 0f, 0f, 1f));
                Shader.SetGlobalMatrix(OceanScreenToWorldId, (cam.nonJitteredProjectionMatrix * view).inverse);
                oceanCommand.Clear();
                oceanCommand.Blit(source, oceanDepth, capture, 7);
                oceanCommand.Blit(source, oceanSurface1, capture, 8);
                oceanCommand.Blit(source, oceanSurface2, capture, 9);
            }

            packet.CopyTo(ref oceanPacket);
            oceanPacket.Signals = RtNative.SignalSpecular;
            oceanPacket.Flags = RtNative.FlagSameFrame | RtNative.FlagExtra | RtNative.FlagWater
                                | (WaterNormalsShown ? RtNative.FlagWaterNormals : 0u);
            // A G-buffer where there is one: Scatterer's ocean brings its surfaces as one, KSP's
            // sea under the deferred path has Unity's; without a G-buffer the water takes its
            // normal from the depth.
            oceanPacket.Params[ParamGBuffer] = scatterer || packet.Params[ParamGBuffer] > 0.5f ? 1f : 0f;
            for (int i = 0; i < 8; i++) oceanPacket.Outputs[i] = IntPtr.Zero;
            if (scatterer)
            {
                oceanPacket.Inputs[RtNative.InputDepth] = oceanDepthHandle;
                oceanPacket.Inputs[RtNative.InputGBuffer0] = oceanSurface1Handle;
                oceanPacket.Inputs[RtNative.InputGBuffer1] = oceanSurface1Handle;
                oceanPacket.Inputs[RtNative.InputGBuffer2] = oceanSurface2Handle;
            }
            oceanPacket.Inputs[RtNative.InputMotion] = IntPtr.Zero;
            oceanPacket.Outputs[RtNative.OutputSpecular] = oceanTracedHandle;
            ring.Issue(into, RtNative.RenderEvent, RtNative.FrameEvent, ref oceanPacket);

            into.SetGlobalTexture(RtOcean, oceanTraced);
            if (stockSea) return;
            oceanCommand.Blit(source, oceanReflections, capture, 10);
            oceanCommand.SetGlobalTexture(SsrOutput, OceanHandoffHeldOff ? (Texture)Texture2D.blackTexture : oceanReflections);
        }

        // Scatterer's ocean weighs the reflections it reads (ssrOutput) down from 1 km of the
        // camera's height and to nothing from 2 km. The discs the waves mirror go onto it
        // once it is drawn instead, through its tonemapper (fragOceanDiscs).
        private void AttachOceanDiscs(bool wanted)
        {
            if (wanted == oceanDiscsAttached) return;
            oceanDiscsAttached = wanted;
            if (!wanted)
            {
                cam.RemoveCommandBuffer(OceanDiscsEvent, oceanDiscs);
                return;
            }
            oceanDiscs.Clear();
            oceanDiscs.GetTemporaryRT(OceanFrameId, -1, -1, 0, FilterMode.Point, RenderTextureFormat.DefaultHDR);
            oceanDiscs.Blit(BuiltinRenderTextureType.CameraTarget, OceanFrameId);
            // Drawn as the camera's own full-screen quad, which reads the screen's textures
            // where the camera's projection puts them (ComputeScreenPos), whatever the
            // camera draws into.
            oceanDiscs.SetGlobalTexture(OceanFrameId, OceanFrameId);
            oceanDiscs.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
            oceanDiscs.DrawMesh(screenQuad, Matrix4x4.identity, capture, 0, OceanDiscsPass);
            oceanDiscs.ReleaseTemporaryRT(OceanFrameId);
            cam.AddCommandBuffer(OceanDiscsEvent, oceanDiscs);
        }

        // The ocean's targets at the frame's size, made anew where the size changed or Unity lost them.
        private void EnsureOceanTargets()
        {
            if (oceanDepth != null && oceanDepth.width == width && oceanDepth.height == height && oceanDepth.IsCreated()
                && oceanTraced.IsCreated())
                return;
            ReleaseOceanTargets();
            oceanDepth = Target(RenderTextureFormat.RFloat);
            oceanSurface1 = Target(RenderTextureFormat.ARGB32);
            oceanSurface2 = Target(RenderTextureFormat.ARGB2101010);
            oceanTraced = Target(RenderTextureFormat.ARGBHalf);
            oceanReflections = Target(RenderTextureFormat.ARGBFloat);
            oceanDepthHandle = oceanDepth.GetNativeTexturePtr();
            oceanSurface1Handle = oceanSurface1.GetNativeTexturePtr();
            oceanSurface2Handle = oceanSurface2.GetNativeTexturePtr();
            oceanTracedHandle = oceanTraced.GetNativeTexturePtr();
        }

        // Releases the ocean's targets and forgets their native textures.
        private void ReleaseOceanTargets()
        {
            foreach (RenderTexture texture in new[] { oceanDepth, oceanSurface1, oceanSurface2, oceanTraced, oceanReflections })
                if (texture != null)
                {
                    texture.Release();
                    Destroy(texture);
                }
            oceanDepth = oceanSurface1 = oceanSurface2 = oceanTraced = oceanReflections = null;
            oceanDepthHandle = oceanSurface1Handle = oceanSurface2Handle = oceanTracedHandle = IntPtr.Zero;
        }
    }
}
