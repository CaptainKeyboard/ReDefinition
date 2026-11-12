using System;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // The height maps of the layer stack on the GPU: one slice per layer in a texture array (RGBA32: root, top, variation, unused),
    // equirectangular like the coverage and type maps, bilinear, linear colour space. The byte b means (b - 128) / 127 of the layer's
    // range (HeightMaps), so a texel of 128 is exactly zero. A layer without maps has a neutral slice and its shader flag off.
    internal sealed class HeightMapStore
    {
        private Color32[] scratch;

        internal Texture2DArray Maps { get; private set; }
        internal int Width { get; private set; }
        internal int Height { get; private set; }

        // Makes the array at a size with every slice neutral; a size change drops the contents.
        internal void Ensure(int width, int height)
        {
            if (width < 2 || height < 2) throw new ArgumentOutOfRangeException("width");
            if (Maps != null && Width == width && Height == height) return;
            Dispose();
            Width = width;
            Height = height;
            Maps = new Texture2DArray(width, height, CloudLayerDef.MaxLayers, TextureFormat.RGBA32, false, true)
            {
                name = "layer height maps",
                filterMode = FilterMode.Bilinear,
                wrapModeU = TextureWrapMode.Repeat,
                wrapModeV = TextureWrapMode.Clamp
            };
            scratch = new Color32[width * height];
            for (int i = 0; i < scratch.Length; i++) scratch[i] = new Color32(128, 128, 128, 255);
            for (int l = 0; l < CloudLayerDef.MaxLayers; l++) Maps.SetPixels32(scratch, l, 0);
            Maps.Apply(false);
        }

        private LayerHeightField queued;
        private int queuedCount, nextSlice;

        // Whether slices of a queued upload are still waiting (Step writes them).
        internal bool Pending
        {
            get { return queued != null; }
        }

        // Writes the slices of the stack's height field (null maps give a neutral slice) and applies the array once. On the calling frame:
        // 4 layers of 512 x 256 texels are several milliseconds; the game queues the upload instead (Queue, Step).
        internal void Upload(LayerHeightField field, int layerCount)
        {
            Queue(field, layerCount);
            Step(CloudLayerDef.MaxLayers);
        }

        // Starts an upload of the field's slices that Step writes a few at a time (the maps of the next frames lag the build by up to
        // MaxLayers frames, which a height field that changes every 20 s does not show). A queue that waits is replaced.
        internal void Queue(LayerHeightField field, int layerCount)
        {
            int width = 2, height = 2;
            for (int l = 0; l < layerCount; l++)
            {
                HeightMaps m = field.MapsOf(l);
                if (m != null)
                {
                    width = m.Width;
                    height = m.Height;
                    break;
                }
            }
            Ensure(width, height);
            queued = field;
            queuedCount = layerCount;
            nextSlice = 0;
        }

        // Writes up to that many queued slices and applies the array; does nothing when none waits.
        internal void Step(int slices)
        {
            if (queued == null) return;
            while (slices-- > 0 && nextSlice < CloudLayerDef.MaxLayers)
            {
                int l = nextSlice++;
                HeightMaps m = l < queuedCount ? queued.MapsOf(l) : null;
                if (m != null && (m.Width != Width || m.Height != Height)) m = null;
                var neutral = new Color32(128, 128, 128, 255);
                for (int i = 0; i < scratch.Length; i++)
                    scratch[i] = m == null ? neutral : new Color32(m.Bytes[i * 3], m.Bytes[i * 3 + 1], m.Bytes[i * 3 + 2], 255);
                Maps.SetPixels32(scratch, l, 0);
            }
            Maps.Apply(false);
            if (nextSlice >= CloudLayerDef.MaxLayers) queued = null;
        }

        internal void Dispose()
        {
            if (Maps != null) UnityEngine.Object.Destroy(Maps);
            Maps = null;
            queued = null;
        }
    }
}
