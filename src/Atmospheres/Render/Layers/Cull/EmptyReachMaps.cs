using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // The empty-reach maps of the layer stack on the GPU (E40 C1): one R8 slice per layer, the map's own size, a texel the number of texels to the nearest texel that can hold density
    // (EmptyReachMath). The march reads it where a sample fails the coverage bound and leaps over the grid points within the reach. A layer's slice is rebuilt when a target of its coverage
    // map was given or the numbers of its coverage bound changed, from the map as it is and as it will be (a slew moves every texel between the two, so the slice holds for the whole
    // slew); the game rebuilds one layer a frame (SpreadReach), the player and the tests all at once. A slice that was not built yet holds zeros: no reach, the march as it was.
    internal sealed class EmptyReachMaps
    {
        private Texture2DArray array, zero;
        private int mapW, mapH;
        private readonly long[] builtGoal = new long[CloudLayerDef.MaxLayers];
        private readonly Vector4[] builtNumbers = new Vector4[CloudLayerDef.MaxLayers];
        private readonly bool[] occupied = new bool[256];
        private byte[] reach;
        private float[] work;
        private Color32[] scratch;

        // The milliseconds the last Refresh took and how many layers it rebuilt (the CPU cost of the maps).
        internal float LastMs;
        internal int LastRebuilt;

        // The array the march reads: the maps, or a zero texture where the reach is off or no map exists yet.
        internal Texture2DArray Texture(bool on)
        {
            if (on && array != null) return array;
            if (zero == null)
            {
                zero = new Texture2DArray(1, 1, CloudLayerDef.MaxLayers, TextureFormat.R8, false, true) { name = "empty reach (off)", filterMode = FilterMode.Point };
                var black = new Color32[] { new Color32(0, 0, 0, 255) };
                for (int l = 0; l < CloudLayerDef.MaxLayers; l++) zero.SetPixels32(black, l, 0);
                zero.Apply(false);
            }
            return zero;
        }

        private void Ensure(int w, int h)
        {
            if (array != null && mapW == w && mapH == h) return;
            if (array != null) Object.Destroy(array);
            mapW = w;
            mapH = h;
            array = new Texture2DArray(w, h, CloudLayerDef.MaxLayers, TextureFormat.R8, false, true)
            {
                name = "empty reach",
                filterMode = FilterMode.Point,
                wrapModeU = TextureWrapMode.Repeat,
                wrapModeV = TextureWrapMode.Clamp
            };
            scratch = new Color32[w * h];
            reach = new byte[w * h];
            work = new float[(w + 256) * h];
            var black = new Color32(0, 0, 0, 255);
            for (int i = 0; i < scratch.Length; i++) scratch[i] = black;
            for (int l = 0; l < CloudLayerDef.MaxLayers; l++)
            {
                array.SetPixels32(scratch, l, 0);
                builtGoal[l] = -1;
            }
            array.Apply(false);
        }

        // Rebuilds up to `budget` layers whose coverage target or bound numbers changed since their slice was built. layerB.y the coverage, .z the edge, layerD.x the noise amount,
        // cullL.y the clear-sky K of the layer: the numbers the shader's bound reads.
        internal void Refresh(LayerMapStore maps, int layers, Vector4[] layerB, Vector4[] layerD, Vector4[] cullL, int budget)
        {
            LastRebuilt = 0;
            LastMs = 0f;
            if (maps == null || maps.Coverage == null || maps.Width < 2 || maps.Height < 2) return;
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            Ensure(maps.Width, maps.Height);
            bool any = false;
            for (int l = 0; l < layers && l < CloudLayerDef.MaxLayers && LastRebuilt < budget; l++)
            {
                var numbers = new Vector4(layerB[l].y, layerD[l].x, layerB[l].z, cullL[l].y);
                long goal = maps.CoverageGoalVersion(l);
                if (builtGoal[l] == goal && builtNumbers[l] == numbers) continue;
                byte[] now, target;
                if (!maps.CoverageReachPair(l, out now, out target) || now == null || target == null) continue;
                EmptyReachMath.Table(occupied, numbers.x, numbers.y, numbers.z, numbers.w);
                EmptyReachMath.Build(now, target, mapW, mapH, occupied, reach, work);
                for (int i = 0; i < reach.Length; i++) scratch[i] = new Color32(reach[i], reach[i], reach[i], 255);
                array.SetPixels32(scratch, l, 0);
                builtGoal[l] = goal;
                builtNumbers[l] = numbers;
                LastRebuilt++;
                any = true;
            }
            if (any) array.Apply(false);
            LastMs = (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000f / System.Diagnostics.Stopwatch.Frequency;
        }

        internal void Dispose()
        {
            if (array != null) Object.Destroy(array);
            if (zero != null) Object.Destroy(zero);
            array = zero = null;
        }
    }
}
