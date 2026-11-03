using UnityEngine;

namespace ReDefinition.Upscaler
{
    // The preview of what the upscaler receives and returns, in the diagnostics window: a source image read back
    // small and tinted so that motion vectors, depth and masks can be seen.
    internal sealed class RigPreview
    {
        // How often the preview is read back: four times a second.
        private const float IntervalSeconds = 0.25f;

        // The tinted preview and its pixels, the time it is next read back, and the scale it was tinted by.
        private Texture2D preview;
        private Color[] previewPixels;
        private float nextPreview;
        // The motion preview's magnitudes, one per pixel, kept between readbacks.
        private float[] previewMagnitudes;
        private float previewGain = 1f;

        // The scale of the last tinted preview: the 99th percentile of motion in pixels, the nearest depth, or 1.
        internal float Gain { get { return previewGain; } }

        // Preview of what the upscaler receives and returns.
        //
        // Colour and result can be drawn directly; motion vectors and depth
        // cannot, their raw values are too small to be visible. Those get tinted
        // and scaled by the 99th percentile of the respective image instead of by
        // a fixed factor.
        internal Texture Get(UpscalerRig.PreviewMode mode, RenderTexture source, Vector2Int renderSize, RigReadback readback)
        {
            if (source == null) return null;

            // Four times a second: every readback stalls the frame briefly.
            if (preview != null && Time.unscaledTime < nextPreview) return preview;
            nextPreview = Time.unscaledTime + IntervalSeconds;

            Color[] pixels = readback.ReadBack(source);
            if (preview == null)
            {
                preview = new Texture2D(UpscalerRig.PreviewWidth, UpscalerRig.PreviewHeight, TextureFormat.RGBA32, false);
                preview.filterMode = FilterMode.Point;
                preview.wrapMode = TextureWrapMode.Clamp;
                previewPixels = new Color[pixels.Length];
                previewMagnitudes = new float[pixels.Length];
            }

            if (mode == UpscalerRig.PreviewMode.Depth) ColourDepth(pixels);
            else if (mode == UpscalerRig.PreviewMode.TransparencyMask || mode == UpscalerRig.PreviewMode.ReactiveMask) ColourMask(pixels);
            else ColourMotion(pixels, renderSize);

            preview.SetPixels(previewPixels);
            preview.Apply(false);
            return preview;
        }

        // A motion vector pixel's length in pixels at render size: the vectors are in UV units,
        // and the render resolution turns them into pixels.
        internal static float MotionMagnitude(Color pixel, Vector2Int renderSize)
        {
            float dx = pixel.r * renderSize.x;
            float dy = pixel.g * renderSize.y;
            return Mathf.Sqrt(dx * dx + dy * dy);
        }

        // Red is motion to the right, green upwards, grey is still.
        private void ColourMotion(Color[] pixels, Vector2Int renderSize)
        {
            // Sorted in place: only the 99th percentile is wanted from them.
            float[] magnitudes = previewMagnitudes;
            for (int i = 0; i < pixels.Length; i++) magnitudes[i] = MotionMagnitude(pixels[i], renderSize);
            System.Array.Sort(magnitudes);
            previewGain = Mathf.Max(magnitudes[(int)(magnitudes.Length * 0.99f)], 0.5f);

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

        // Destroys the preview texture, at the rig's teardown.
        internal void Release()
        {
            if (preview != null) { Object.Destroy(preview); preview = null; }
        }
    }
}
