using System;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Atmospheres
{
    // The interior mask of the IVA: where the internal camera drew geometry (props, walls, seats), so that the host composite frosts only
    // the windows. With the upscaler's rig the internal camera is part of the picture it upscales, so the interior is in the image the
    // frost is drawn onto; the mask is what keeps it clear. A command buffer on the internal camera copies its depth buffer into a small
    // target as covered / not covered after its geometry is drawn.
    //
    // Premises that were not measured when this was written [VERIFY in the game, series K-W3b, rows D37.iva_*]: that the internal camera
    // clears depth before it draws (so its depth buffer holds the interior only), that it makes a depth texture at all (the rig asks
    // for one on the scene camera only), and at which event the depth is valid. So the mask is judged (FrostMaskJudge): it is read back
    // about once a second without stalling the GPU, and when the readings say empty or full the next way of taking it is tried (the
    // event and whether the camera is asked for the depth texture). When no way gives a plausible mask, Valid stays false and the
    // renderer draws the IVA by the fallback of its policy instead of the host composite.
    internal sealed class FrostInteriorMask : IDisposable
    {
        private const string CameraName = "InternalCamera";
        private const float ReadEveryS = 1f;
        private const int SettleFrames = 3;

        private Camera camera;
        private CommandBuffer buffer;
        private CameraEvent bufferEvent;
        private RenderTexture mask;
        private int builtFrame = -1;
        private int builtWay = -1;
        private DepthTextureMode savedDepthMode;
        private bool depthModeForced;
        private readonly FrostMaskJudge judge = new FrostMaskJudge();
        private bool pinned;
        private float pinnedCoverage = -1f;
        private bool requestPending;
        private float readAge = 1e9f;
        private bool asyncUnsupported;

        // The mask of the last frame it was written, or null where there is none.
        internal RenderTexture Mask
        {
            get { return mask; }
        }

        // The frame the buffer was last rebuilt for (diagnostics).
        internal int BuiltFrame
        {
            get { return builtFrame; }
        }

        // The way in use (an index into FrostMaskJudge.Ways) and whether all of them failed.
        internal int Way
        {
            get { return judge.Current; }
        }

        internal bool Exhausted
        {
            get { return judge.Exhausted; }
        }

        // The last coverage read back, or -1.
        internal float Coverage
        {
            get { return pinned ? pinnedCoverage : judge.Coverage; }
        }

        // The camera the buffer is on, or null.
        internal Camera Camera
        {
            get { return camera; }
        }

        // Whether the mask can be used: built, settled, and its readings plausible. Without asynchronous read-back the reading cannot be
        // taken, and the mask is trusted once built (the flaw the judge exists for is then not seen; the log says so).
        internal bool Valid
        {
            get
            {
                if (mask == null || builtFrame < 0 || Time.frameCount - builtFrame <= SettleFrames) return false;
                if (asyncUnsupported) return true;
                if (pinned) return FrostMaskJudge.Plausible(pinnedCoverage);
                return judge.Valid;
            }
        }

        // The share of the mask that is covered (1: geometry drawn), read back from the GPU with a wait; -1 where there is no mask. It
        // waits for the GPU and allocates: the series probe's diagnostic only.
        internal float ReadCoverage()
        {
            if (mask == null) return -1f;
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = mask;
            var copy = new Texture2D(mask.width, mask.height, TextureFormat.RGBA32, false, true);
            copy.ReadPixels(new Rect(0, 0, mask.width, mask.height), 0, 0);
            RenderTexture.active = previous;
            Color32[] pixels = copy.GetPixels32();
            UnityEngine.Object.Destroy(copy);
            int covered = 0;
            for (int i = 0; i < pixels.Length; i++) if (pixels[i].r > 127) covered++;
            return pixels.Length == 0 ? -1f : covered / (float)pixels.Length;
        }

        // The mask as 0 / 1 values, row 0 at the bottom (a render texture read back), read with a wait; null where there is no mask.
        // The series probe's diagnostic.
        internal float[] ReadMask(out int width, out int height)
        {
            width = height = 0;
            if (mask == null) return null;
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = mask;
            var copy = new Texture2D(mask.width, mask.height, TextureFormat.RGBA32, false, true);
            copy.ReadPixels(new Rect(0, 0, mask.width, mask.height), 0, 0);
            RenderTexture.active = previous;
            Color32[] pixels = copy.GetPixels32();
            UnityEngine.Object.Destroy(copy);
            width = mask.width;
            height = mask.height;
            var values = new float[pixels.Length];
            for (int i = 0; i < pixels.Length; i++) values[i] = pixels[i].r > 127 ? 1f : 0f;
            return values;
        }

        private static Camera FindCamera()
        {
            Camera[] all = Camera.allCameras;
            for (int i = 0; i < all.Length; i++)
                if (all[i] != null && all[i].name == CameraName) return all[i];
            return null;
        }

        private static CameraEvent EventOf(string name)
        {
            switch (name)
            {
                case "AfterForwardOpaque": return CameraEvent.AfterForwardOpaque;
                case "BeforeImageEffects": return CameraEvent.BeforeImageEffects;
                default: return CameraEvent.BeforeForwardAlpha;
            }
        }

        // Keeps the buffer on the internal camera while wanted, and takes it off when not. pass records the copy. pin is the series'
        // pinned way (frost.ivaMaskWay, below 0: the judge decides).
        internal void Update(bool wanted, FrostPass pass, int pin)
        {
            if (!wanted || pass == null)
            {
                Detach();
                return;
            }
            if (camera == null) camera = FindCamera();
            if (camera == null) return;
            bool wasPinned = pinned;
            pinned = pin >= 0;
            if (pinned) judge.Pin(pin);
            else if (wasPinned) judge.Reset();
            int w = Math.Max(2, camera.pixelWidth / 2), h = Math.Max(2, camera.pixelHeight / 2);
            if (mask == null || mask.width != w || mask.height != h)
            {
                ReleaseMask();
                mask = new RenderTexture(w, h, 0, RenderTextureFormat.R8, RenderTextureReadWrite.Linear)
                {
                    name = "ReDefinition Atmospheres frost interior mask",
                    filterMode = FilterMode.Bilinear,
                    hideFlags = HideFlags.HideAndDontSave
                };
                mask.Create();
                RemoveBuffer();
            }
            if (buffer != null && builtWay != judge.Current) RemoveBuffer();
            if (buffer == null)
            {
                FrostMaskJudge.Way way = FrostMaskJudge.Ways[judge.Current];
                buffer = new CommandBuffer { name = "ReDefinition Atmospheres frost interior mask" };
                pass.RecordMask(buffer, new RenderTargetIdentifier(BuiltinRenderTextureType.Depth), mask);
                // The camera goes on drawing into its own target.
                buffer.SetRenderTarget(new RenderTargetIdentifier(BuiltinRenderTextureType.CameraTarget));
                bufferEvent = EventOf(way.Event);
                if (way.ForceDepth)
                {
                    savedDepthMode = camera.depthTextureMode;
                    depthModeForced = true;
                    camera.depthTextureMode = savedDepthMode | DepthTextureMode.Depth;
                }
                camera.AddCommandBuffer(bufferEvent, buffer);
                builtFrame = Time.frameCount;
                builtWay = judge.Current;
                pinnedCoverage = -1f;
                requestPending = false;
                readAge = 0f;
                AtmospheresLog.Say("camera frost: the interior mask is taken at " + way.Event + (way.ForceDepth ? " with the depth texture asked for" : "")
                                   + " (way " + judge.Current + ")");
            }
            ReadBack();
        }

        // Asks for the mask's pixels about once a second; the answer arrives a few frames later and goes to the judge.
        private void ReadBack()
        {
            if (mask == null || asyncUnsupported || requestPending || Time.frameCount - builtFrame <= SettleFrames) return;
            readAge += Time.unscaledDeltaTime;
            if (readAge < ReadEveryS) return;
            readAge = 0f;
            if (!SystemInfo.supportsAsyncGPUReadback)
            {
                asyncUnsupported = true;
                AtmospheresLog.WarnOnce("camera frost: no asynchronous read-back; the interior mask cannot be judged and is trusted once built");
                return;
            }
            requestPending = true;
            int forWay = builtWay;
            AsyncGPUReadback.Request(mask, 0, TextureFormat.R8, request => OnRead(request, forWay));
        }

        private void OnRead(AsyncGPUReadbackRequest request, int forWay)
        {
            requestPending = false;
            if (request.hasError || mask == null || forWay != builtWay) return;
            NativeArray<byte> data = request.GetData<byte>();
            int covered = 0;
            for (int i = 0; i < data.Length; i++) if (data[i] > 127) covered++;
            float reading = data.Length == 0 ? -1f : covered / (float)data.Length;
            if (pinned)
            {
                pinnedCoverage = reading;
                return;
            }
            if (judge.Report(reading))
            {
                AtmospheresLog.Say("camera frost: the interior mask read " + reading.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)
                                   + " (empty or full); trying way " + judge.Current);
            }
            else if (judge.Exhausted)
            {
                AtmospheresLog.WarnOnce("camera frost: no way of taking the interior mask gave a plausible reading (last "
                                        + reading.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)
                                        + "); the IVA is not drawn by the host composite");
            }
        }

        internal void Detach()
        {
            RemoveBuffer();
            ReleaseMask();
            camera = null;
            judge.Reset();
            builtWay = -1;
            pinnedCoverage = -1f;
        }

        private void RemoveBuffer()
        {
            if (buffer == null) return;
            if (camera != null)
            {
                camera.RemoveCommandBuffer(bufferEvent, buffer);
                if (depthModeForced) camera.depthTextureMode = savedDepthMode;
            }
            depthModeForced = false;
            buffer.Release();
            buffer = null;
        }

        private void ReleaseMask()
        {
            if (mask == null) return;
            mask.Release();
            UnityEngine.Object.Destroy(mask);
            mask = null;
        }

        public void Dispose()
        {
            Detach();
        }
    }
}
