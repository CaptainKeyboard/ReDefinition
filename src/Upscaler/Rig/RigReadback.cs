using UnityEngine;

namespace ReDefinition.Upscaler
{
    // The CPU textures the rig's diagnostics and previews read the GPU images back into: one pixel for
    // SamplePixel, the downscaled image for ReadBack.
    internal sealed class RigReadback
    {
        private Texture2D probePixel;
        private Texture2D readback;

        // Read back downscaled: 320x180 is enough for a statistic.
        internal Color[] ReadBack(RenderTexture source)
        {
            const int width = UpscalerRig.PreviewWidth;
            const int height = UpscalerRig.PreviewHeight;

            RenderTexture small = RenderTexture.GetTemporary(
                width, height, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
            small.filterMode = FilterMode.Point;
            Graphics.Blit(source, small);

            if (readback == null)
                readback = new Texture2D(width, height, TextureFormat.RGBAHalf, false, true);

            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = small;
            readback.ReadPixels(new Rect(0f, 0f, width, height), 0, 0, false);
            readback.Apply(false);
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(small);

            return readback.GetPixels();
        }

        // The red channel of the downscaled readback: depth, or a mask, one value per pixel.
        internal float[] ReadLuminance(RenderTexture source)
        {
            Color[] pixels = ReadBack(source);
            float[] values = new float[pixels.Length];
            for (int i = 0; i < pixels.Length; i++) values[i] = pixels[i].r;
            return values;
        }

        // A single pixel tells an image from a black one.
        internal string SamplePixel(RenderTexture texture)
        {
            if (probePixel == null) probePixel = new Texture2D(1, 1, TextureFormat.RGBAFloat, false);

            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = texture;
            probePixel.ReadPixels(new Rect(texture.width / 2f, texture.height / 2f, 1, 1), 0, 0, false);
            probePixel.Apply(false);
            RenderTexture.active = previous;

            Color c = probePixel.GetPixel(0, 0);
            return "(" + c.r.ToString("0.000") + ", " + c.g.ToString("0.000")
                   + ", " + c.b.ToString("0.000") + ")";
        }

        // Destroys the textures, at the rig's teardown.
        internal void Release()
        {
            if (probePixel != null) { Object.Destroy(probePixel); probePixel = null; }
            if (readback != null) { Object.Destroy(readback); readback = null; }
        }
    }
}
