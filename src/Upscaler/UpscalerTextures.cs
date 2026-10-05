using System;
using UnityEngine;

namespace ReDefinition.Upscaler
{
    // The rig's textures: the 3D stack's image at render size, the upscaler's output at
    // display size, the rig's own copies of depth and motion vectors, frame generation's
    // HUD-less copy, and the native handles the proxy is given.
    internal sealed class UpscalerTextures
    {
        internal RenderTexture LowRes { get { return lowRes; } }
        internal RenderTexture Upscaled { get { return upscaled; } }
        internal RenderTexture MotionVectors { get { return motionVectors; } }
        internal RenderTexture DepthCopy { get { return depthCopy; } }
        internal RenderTexture HudLess { get { return hudLess; } }

        private RenderTexture lowRes;
        private RenderTexture upscaled;

        // The rig's own copies of depth and motion vectors.
        //
        // BuiltinRenderTextureType.MotionVectors is valid only inside the rendering
        // of the camera that produces it; in the presenter camera, where the
        // dispatch runs, the same identifier names the presenter's own buffers,
        // which do not exist. A CommandBuffer on the producing camera copies them.
        private RenderTexture motionVectors;
        private RenderTexture depthCopy;
        private RenderTexture hudLess;

        // The proxy hands the rig's textures to its D3D12 device, which relies on
        // their underlying ID3D11Resource staying the same. Captured once at setup and
        // compared on demand -- GetNativeTexturePtr flushes the render thread,
        // so it must never be called per frame.
        internal IntPtr UpscaledHandle { get; private set; }
        internal IntPtr LowResHandle { get; private set; }
        internal IntPtr DepthHandle { get; private set; }
        internal IntPtr MotionHandle { get; private set; }
        internal int HandleFrame { get; private set; } = -1;

        // The textures at render and display size; null, or the reason where one
        // could not be made. No output without an upscaler to write it (passThrough).
        internal string Make(Vector2Int renderSize, Vector2Int displaySize, bool hdr, bool passThrough)
        {
            lowRes = new RenderTexture(renderSize.x, renderSize.y, 24,
                hdr ? RenderTextureFormat.DefaultHDR : RenderTextureFormat.Default,
                RenderTextureReadWrite.Default);
            lowRes.name = "ReDefinition_LowRes";
            lowRes.filterMode = FilterMode.Bilinear;
            if (!RenderTextures.Made(lowRes))
                return "RenderTexture " + renderSize.x + "x" + renderSize.y
                       + " could not be created -- memory, most likely.";

            if (!passThrough)
            {
                upscaled = new RenderTexture(displaySize.x, displaySize.y, 0,
                    RenderTextureFormat.DefaultHDR, RenderTextureReadWrite.Default);
                upscaled.name = "ReDefinition_Upscaled";
                upscaled.enableRandomWrite = true;   // FSR writes into it via UAV
                upscaled.filterMode = FilterMode.Bilinear;
                if (!RenderTextures.Made(upscaled))
                    return "Output texture could not be created -- memory, most likely.";
            }

            motionVectors = new RenderTexture(renderSize.x, renderSize.y, 0,
                RenderTextureFormat.RGHalf, RenderTextureReadWrite.Linear);
            motionVectors.name = "ReDefinition_MotionVectors";
            motionVectors.filterMode = FilterMode.Point;

            depthCopy = new RenderTexture(renderSize.x, renderSize.y, 0,
                RenderTextureFormat.RFloat, RenderTextureReadWrite.Linear);
            depthCopy.name = "ReDefinition_Depth";
            depthCopy.filterMode = FilterMode.Point;

            if (!RenderTextures.Made(motionVectors) || !RenderTextures.Made(depthCopy))
                return "Depth and motion vector buffers could not be created -- memory, most likely.";

            // In the backbuffer's own format, ARGB32, so frame generation compares
            // like with like.
            hudLess = new RenderTexture(displaySize.x, displaySize.y, 0,
                RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
            hudLess.name = "ReDefinition_HudLess";
            hudLess.filterMode = FilterMode.Point;
            if (!RenderTextures.Made(hudLess))
                return "HUD-less texture could not be created -- memory, most likely.";
            return null;
        }

        internal void CaptureHandles()
        {
            UpscaledHandle = upscaled != null ? upscaled.GetNativeTexturePtr() : IntPtr.Zero;
            LowResHandle = lowRes.GetNativeTexturePtr();
            DepthHandle = depthCopy.GetNativeTexturePtr();
            MotionHandle = motionVectors.GetNativeTexturePtr();
            HandleFrame = Time.frameCount;
        }

        // Every texture whose native pointer went to the proxy at setup, and extra where
        // there is one. Unity: "render texture contents can become "lost" on certain
        // events, like loading a new level, system going to a screensaver mode, in and
        // out of fullscreen", and they "will become "not yet created" again, you can
        // check for that with IsCreated function" (ScriptReference, RenderTexture,
        // 2019.4). The rig is rebuilt rather than handing on pointers to what is gone.
        // The native handles asked for once after the textures are made and again when the
        // game window has its focus back: asking synchronises with the render thread, and
        // once a second that held a frame for up to 18.6 ms (measured 2026-10-06). Lost
        // textures show in IsCreated each frame, as Unity documents.
        private bool nativeWanted = true;

        internal void WantNativeCheck()
        {
            nativeWanted = true;
        }

        internal bool Created(bool passThrough, RenderTexture extra)
        {
            if (!(RenderTextures.Created(lowRes) && (passThrough || RenderTextures.Created(upscaled))
                  && RenderTextures.Created(motionVectors) && RenderTextures.Created(depthCopy)
                  && RenderTextures.Created(hudLess)
                  && (extra == null || RenderTextures.Created(extra))))
                return false;

            // IsCreated() is true for a texture Direct3D never made (see Made). The
            // native handle settles it, but asking for it synchronises with the render
            // thread: asked only when wanted (nativeWanted).
            if (!nativeWanted) return true;
            nativeWanted = false;
            return RenderTextures.Native(lowRes) && (passThrough || RenderTextures.Native(upscaled))
                   && RenderTextures.Native(motionVectors) && RenderTextures.Native(depthCopy)
                   && RenderTextures.Native(hudLess)
                   && (extra == null || RenderTextures.Native(extra));
        }

        // Released only once the cameras are restored: until then they still render
        // into lowRes, and Unity reports "Releasing render texture that is set as
        // Camera.targetTexture!".
        internal void ReleaseImages()
        {
            RenderTextures.Release(ref lowRes);
            RenderTextures.Release(ref upscaled);
        }

        internal void ReleaseInputs()
        {
            RenderTextures.Release(ref motionVectors);
            RenderTextures.Release(ref depthCopy);
            RenderTextures.Release(ref hudLess);
        }
    }
}
