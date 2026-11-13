using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // The bound maps of the coverage maps (package T16): per layer the dilated maximum and minimum of the map on a fine grid (128 x 64 cells) and a
    // coarse one (32 x 16), so that the tile pass proves a bundle of rays empty (maximum) or solid (minimum) with a few reads. Rebuilt from the bytes the
    // store uploaded, for the layers whose upload counter moved (CullMath.Cells, Coarsen and Dilate are the arithmetic; the tests hold them to a brute force over the map).
    internal sealed class CullBounds
    {
        private Texture2DArray maxFine, maxCoarse, minFine, minCoarse;
        private readonly long[] built = new long[CloudLayerDef.MaxLayers];
        private int mapW, mapH;
        private Color32[] scratchFine, scratchCoarse;
        private readonly byte[] cellMax = new byte[CullMath.FineW * CullMath.FineH], cellMin = new byte[CullMath.FineW * CullMath.FineH];

        internal Texture2DArray MaxFine { get { return maxFine; } }
        internal Texture2DArray MaxCoarse { get { return maxCoarse; } }
        internal Texture2DArray MinFine { get { return minFine; } }
        internal Texture2DArray MinCoarse { get { return minCoarse; } }

        // The milliseconds the last Refresh took and how many layers it rebuilt (the CPU cost of the bound maps).
        internal float LastMs;
        internal int LastRebuilt;

        private static Texture2DArray Make(string name, int w, int h)
        {
            return new Texture2DArray(w, h, CloudLayerDef.MaxLayers, TextureFormat.R8, false, true)
            {
                name = name,
                filterMode = FilterMode.Point,
                wrapModeU = TextureWrapMode.Repeat,
                wrapModeV = TextureWrapMode.Clamp
            };
        }

        private void Ensure()
        {
            if (maxFine != null) return;
            maxFine = Make("cull max fine", CullMath.FineW, CullMath.FineH);
            minFine = Make("cull min fine", CullMath.FineW, CullMath.FineH);
            maxCoarse = Make("cull max coarse", CullMath.CoarseW, CullMath.CoarseH);
            minCoarse = Make("cull min coarse", CullMath.CoarseW, CullMath.CoarseH);
            scratchFine = new Color32[CullMath.FineW * CullMath.FineH];
            scratchCoarse = new Color32[CullMath.CoarseW * CullMath.CoarseH];
            for (int i = 0; i < built.Length; i++) built[i] = -1;
            // A layer that was never uploaded holds nothing: its maximum is 0 and its minimum 0.
            var zeroFine = new byte[CullMath.FineW * CullMath.FineH];
            var zeroCoarse = new byte[CullMath.CoarseW * CullMath.CoarseH];
            for (int l = 0; l < CloudLayerDef.MaxLayers; l++)
            {
                Store(maxFine, l, zeroFine, scratchFine);
                Store(minFine, l, zeroFine, scratchFine);
                Store(maxCoarse, l, zeroCoarse, scratchCoarse);
                Store(minCoarse, l, zeroCoarse, scratchCoarse);
            }
            maxFine.Apply(false);
            minFine.Apply(false);
            maxCoarse.Apply(false);
            minCoarse.Apply(false);
        }

        private static void Store(Texture2DArray array, int slice, byte[] values, Color32[] scratch)
        {
            for (int i = 0; i < values.Length; i++) scratch[i] = new Color32(values[i], values[i], values[i], 255);
            array.SetPixels32(scratch, slice, 0);
        }

        // Rebuilds the layers whose coverage changed since the last call; cheap when none did.
        internal void Refresh(LayerMapStore maps, int layers)
        {
            LastRebuilt = 0;
            LastMs = 0f;
            if (maps == null || maps.Coverage == null) return;
            Ensure();
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            if (mapW != maps.Width || mapH != maps.Height)
            {
                mapW = maps.Width;
                mapH = maps.Height;
                for (int i = 0; i < built.Length; i++) built[i] = -1;
            }
            bool any = false;
            for (int l = 0; l < layers && l < CloudLayerDef.MaxLayers; l++)
            {
                long version = maps.CoverageVersion(l);
                if (built[l] == version) continue;
                byte[] bytes = maps.CoverageBytes(l);
                CullMath.Cells(bytes, mapW, mapH, CullMath.FineW, CullMath.FineH, cellMax, cellMin);
                byte[] coarseMax = CullMath.Coarsen(cellMax, CullMath.FineW, CullMath.FineH, CullMath.CoarseW, CullMath.CoarseH, true);
                byte[] coarseMin = CullMath.Coarsen(cellMin, CullMath.FineW, CullMath.FineH, CullMath.CoarseW, CullMath.CoarseH, false);
                Store(maxFine, l, CullMath.Dilate(cellMax, CullMath.FineW, CullMath.FineH, true), scratchFine);
                Store(minFine, l, CullMath.Dilate(cellMin, CullMath.FineW, CullMath.FineH, false), scratchFine);
                Store(maxCoarse, l, CullMath.Dilate(coarseMax, CullMath.CoarseW, CullMath.CoarseH, true), scratchCoarse);
                Store(minCoarse, l, CullMath.Dilate(coarseMin, CullMath.CoarseW, CullMath.CoarseH, false), scratchCoarse);
                built[l] = version;
                LastRebuilt++;
                any = true;
            }
            if (any)
            {
                maxFine.Apply(false);
                minFine.Apply(false);
                maxCoarse.Apply(false);
                minCoarse.Apply(false);
            }
            LastMs = (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000f / System.Diagnostics.Stopwatch.Frequency;
        }

        internal void Dispose()
        {
            if (maxFine != null) Object.Destroy(maxFine);
            if (minFine != null) Object.Destroy(minFine);
            if (maxCoarse != null) Object.Destroy(maxCoarse);
            if (minCoarse != null) Object.Destroy(minCoarse);
            maxFine = minFine = maxCoarse = minCoarse = null;
        }
    }
}
