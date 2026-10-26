using System;
using UnityEngine;

namespace ReDefinition.Upscaler
{
    // The rig's part for the preview of what the upscaler receives, in the diagnostics window.
    public partial class UpscalerRig
    {
        // The size of the tinted previews, whatever the render size; the source is read back scaled to it.
        public const int PreviewWidth = 320;
        public const int PreviewHeight = 180;

        // The tinted preview and its pixels, the time it is next read back, and the scale it was tinted by.
        private Texture2D preview;
        private Color[] previewPixels;
        private float nextPreview;
        private float previewGain = 1f;

        // The scale of the last tinted preview: the 99th percentile of motion in pixels, the nearest depth, or 1.
        public float PreviewGain { get { return previewGain; } }

        // What the diagnostics window's preview shows.
        public enum PreviewMode
        {
            // The motion vectors, tinted by direction and scaled to their 99th percentile.
            MotionVectors,
            // The depth copy, scaled to the nearest depth in it.
            Depth,
            // The rendered image as the upscaler receives it.
            LowRes,
            // The upscaler's output.
            Upscaled,
            // The transparency and composition mask (UpscalerMasks).
            TransparencyMask,
            // The reactive mask: the renderers' or FSR's automatic one.
            ReactiveMask,
        }

        // Preview of what the upscaler receives and returns.
        //
        // Colour and result can be drawn directly; motion vectors and depth
        // cannot, their raw values are too small to be visible. Those get tinted
        // and scaled by the 99th percentile of the respective image instead of by
        // a fixed factor.
        public Texture GetPreview(PreviewMode mode)
        {
            if (mode == PreviewMode.LowRes) return textures.LowRes;
            if (mode == PreviewMode.Upscaled) return textures.Upscaled;

            RenderTexture source = mode == PreviewMode.Depth ? textures.DepthCopy
                : mode == PreviewMode.TransparencyMask ? inputs.Masks.TransparencyTexture
                : mode == PreviewMode.ReactiveMask ? ReactiveTexture()
                : textures.MotionVectors;
            if (source == null) return null;

            // Four times a second: every readback stalls the frame briefly.
            if (preview != null && Time.unscaledTime < nextPreview) return preview;
            nextPreview = Time.unscaledTime + 0.25f;

            Color[] pixels = ReadBack(source);
            if (preview == null)
            {
                preview = new Texture2D(PreviewWidth, PreviewHeight, TextureFormat.RGBA32, false);
                preview.filterMode = FilterMode.Point;
                preview.wrapMode = TextureWrapMode.Clamp;
                previewPixels = new Color[pixels.Length];
            }

            if (mode == PreviewMode.Depth) ColourDepth(pixels);
            else if (mode == PreviewMode.TransparencyMask || mode == PreviewMode.ReactiveMask) ColourMask(pixels);
            else ColourMotion(pixels);

            preview.SetPixels(previewPixels);
            preview.Apply(false);
            return preview;
        }

        // Red is motion to the right, green upwards, grey is still.
        private void ColourMotion(Color[] pixels)
        {
            float[] magnitudes = new float[pixels.Length];
            for (int i = 0; i < pixels.Length; i++)
            {
                float dx = pixels[i].r * renderSize.x;
                float dy = pixels[i].g * renderSize.y;
                magnitudes[i] = Mathf.Sqrt(dx * dx + dy * dy);
            }

            float[] sorted = (float[])magnitudes.Clone();
            System.Array.Sort(sorted);
            previewGain = Mathf.Max(sorted[(int)(sorted.Length * 0.99f)], 0.5f);

            float sx = renderSize.x / previewGain;
            float sy = renderSize.y / previewGain;

            for (int i = 0; i < pixels.Length; i++)
            {
                float dx = Mathf.Clamp(pixels[i].r * sx, -1f, 1f);
                float dy = Mathf.Clamp(pixels[i].g * sy, -1f, 1f);
                previewPixels[i] = new Color(0.5f + 0.5f * dx, 0.5f + 0.5f * dy, 0.5f, 1f);
            }
        }

        // Near is bright, far is dark. The raw value would be almost black
        // throughout, hence normalising to the nearest point in the image.
        private void ColourDepth(Color[] pixels)
        {
            float nearest = 0f;
            for (int i = 0; i < pixels.Length; i++)
                if (pixels[i].r > nearest) nearest = pixels[i].r;

            previewGain = Mathf.Max(nearest, 1e-6f);
            for (int i = 0; i < pixels.Length; i++)
            {
                float value = Mathf.Sqrt(Mathf.Clamp01(pixels[i].r / previewGain));
                previewPixels[i] = new Color(value, value, value, 1f);
            }
        }

        // A mask as it is: white is 1.
        private void ColourMask(Color[] pixels)
        {
            previewGain = 1f;
            for (int i = 0; i < pixels.Length; i++)
            {
                float value = Mathf.Clamp01(pixels[i].r);
                previewPixels[i] = new Color(value, value, value, 1f);
            }
        }

        // The reactive mask FSR is given: the drawn one, or what FSR's own generator made.
        private RenderTexture ReactiveTexture()
        {
            if (ReactiveMask == UpscalerMasks.ReactiveSource.Automatic) return context != null ? context.AutoReactive : null;
            return inputs.Masks.ReactiveTexture;
        }
    }
}
