using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Atmospheres
{
    // The interior mask of the IVA: where the internal camera drew geometry (props, walls, seats), so that the host composite frosts only
    // the windows. With the upscaler's rig the internal camera is part of the picture it upscales, so the interior is in the image the
    // frost is drawn onto; the mask is what keeps it clear. A command buffer on the internal camera copies its depth buffer into a small
    // target as covered / not covered after its geometry is drawn.
    //
    // [VERIFY in the game] that the internal camera clears depth before it draws (so its depth buffer holds the interior only) and that
    // BuiltinRenderTextureType.Depth is valid at AfterForwardAlpha for it. The series row D37.iva_mask_valid records both; where the mask
    // is not valid the renderer uses the quad fallback in the IVA instead of the host composite.
    internal sealed class FrostInteriorMask : IDisposable
    {
        private const string CameraName = "InternalCamera";
        private const CameraEvent Event = CameraEvent.AfterForwardAlpha;

        private Camera camera;
        private CommandBuffer buffer;
        private RenderTexture mask;
        private int builtFrame = -1;

        // The mask of the last frame it was written, or null where there is none.
        internal RenderTexture Mask
        {
            get { return mask; }
        }

        internal bool CameraFound
        {
            get { return camera != null; }
        }

        // The frame the buffer was last rebuilt for (diagnostics).
        internal int BuiltFrame
        {
            get { return builtFrame; }
        }

        private static Camera FindCamera()
        {
            Camera[] all = Camera.allCameras;
            for (int i = 0; i < all.Length; i++)
                if (all[i] != null && all[i].name == CameraName) return all[i];
            return null;
        }

        // Keeps the buffer on the internal camera while wanted, and takes it off when not. pass records the copy.
        internal void Update(bool wanted, FrostPass pass)
        {
            if (!wanted || pass == null)
            {
                Detach();
                return;
            }
            if (camera == null) camera = FindCamera();
            if (camera == null) return;
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
            if (buffer == null)
            {
                buffer = new CommandBuffer { name = "ReDefinition Atmospheres frost interior mask" };
                pass.RecordMask(buffer, new RenderTargetIdentifier(BuiltinRenderTextureType.Depth), mask);
                // The camera goes on drawing into its own target.
                buffer.SetRenderTarget(new RenderTargetIdentifier(BuiltinRenderTextureType.CameraTarget));
                camera.AddCommandBuffer(Event, buffer);
                builtFrame = Time.frameCount;
            }
        }

        internal void Detach()
        {
            RemoveBuffer();
            ReleaseMask();
            camera = null;
        }

        private void RemoveBuffer()
        {
            if (buffer == null) return;
            if (camera != null) camera.RemoveCommandBuffer(Event, buffer);
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
