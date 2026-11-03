using UnityEngine;

namespace ReDefinition.Upscaler
{
    // The rig's part for the preview of what the upscaler receives, in the diagnostics window (RigPreview).
    public partial class UpscalerRig
    {
        // The size of the tinted previews, whatever the render size; the source is read back scaled to it.
        public const int PreviewWidth = 320;
        public const int PreviewHeight = 180;

        // The tinted preview of the last source asked for.
        private readonly RigPreview previewer = new RigPreview();

        // The scale of the last tinted preview: the 99th percentile of motion in pixels, the nearest depth, or 1.
        public float PreviewGain { get { return previewer.Gain; } }

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

        // Preview of what the upscaler receives and returns: the colour and the result as they are, the others
        // tinted and scaled (RigPreview).
        public Texture GetPreview(PreviewMode mode)
        {
            if (mode == PreviewMode.LowRes) return textures.LowRes;
            if (mode == PreviewMode.Upscaled) return textures.Upscaled;

            RenderTexture source = mode == PreviewMode.Depth ? textures.DepthCopy
                : mode == PreviewMode.TransparencyMask ? inputs.Masks.TransparencyTexture
                : mode == PreviewMode.ReactiveMask ? ReactiveTexture()
                : textures.MotionVectors;
            return previewer.Get(mode, source, renderSize, readback);
        }

        // The reactive mask FSR is given: the drawn one, or what FSR's own generator made.
        private RenderTexture ReactiveTexture()
        {
            if (ReactiveMask == UpscalerMasks.ReactiveSource.Automatic) return context != null ? context.AutoReactive : null;
            return inputs.Masks.ReactiveTexture;
        }
    }
}
