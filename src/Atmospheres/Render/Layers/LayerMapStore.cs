using System;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // The R8 maps of the layer stack on the GPU: one coverage slice and one type slice per layer in two texture arrays, equirectangular
    // with row 0 at the north pole and column 0 at longitude -180 (the field's WeatherMap layout, so a map is copied as it is). A map
    // moves toward its target with the slew limit of MapSlew, texel by texel, so that no change is a flash (the same limit as the
    // painted maps of EVE; the upscalers and the frame generation follow a drifting edge, not a jump).
    internal sealed class LayerMapStore
    {
        private readonly byte[][] coverageNow = new byte[CloudLayerDef.MaxLayers][];
        private readonly byte[][] coverageGoal = new byte[CloudLayerDef.MaxLayers][];
        private readonly byte[][] typeNow = new byte[CloudLayerDef.MaxLayers][];
        private readonly byte[][] typeGoal = new byte[CloudLayerDef.MaxLayers][];
        private readonly bool[] coveragePrimed = new bool[CloudLayerDef.MaxLayers];
        private readonly bool[] typePrimed = new bool[CloudLayerDef.MaxLayers];
        private readonly bool[] dirtyCoverage = new bool[CloudLayerDef.MaxLayers];
        private readonly bool[] dirtyType = new bool[CloudLayerDef.MaxLayers];
        private Color32[] scratch;
        // Rises each time a layer's coverage is uploaded (package T16: the culling's bound maps are rebuilt from the bytes of that frame).
        private readonly long[] coverageVersion = new long[CloudLayerDef.MaxLayers];

        // The bytes of a layer's coverage map as uploaded last, and the number of uploads so far.
        internal byte[] CoverageBytes(int layer)
        {
            return coverageNow[layer];
        }

        internal long CoverageVersion(int layer)
        {
            return coverageVersion[layer];
        }

        // The bytes of a layer's type map as uploaded last, and the number of uploads so far (LayerSpanBound reads both maps of the frame).
        internal byte[] TypeBytes(int layer)
        {
            return typeNow[layer];
        }

        internal long TypeVersion(int layer)
        {
            return typeVersion[layer];
        }

        private readonly long[] typeVersion = new long[CloudLayerDef.MaxLayers];

        internal Texture2DArray Coverage { get; private set; }
        internal Texture2DArray Types { get; private set; }
        internal int Width { get; private set; }
        internal int Height { get; private set; }

        // Makes the arrays at a size (all layers share it); a size change drops the contents.
        internal void Ensure(int width, int height)
        {
            if (width < 2 || height < 2) throw new ArgumentOutOfRangeException("width");
            if (Coverage != null && Width == width && Height == height) return;
            Dispose();
            Width = width;
            Height = height;
            Coverage = Make("layer coverage maps");
            Types = Make("layer type maps");
            scratch = new Color32[width * height];
            for (int i = 0; i < CloudLayerDef.MaxLayers; i++)
            {
                coverageNow[i] = new byte[width * height];
                coverageGoal[i] = new byte[width * height];
                typeNow[i] = new byte[width * height];
                typeGoal[i] = new byte[width * height];
                coveragePrimed[i] = typePrimed[i] = false;
                dirtyCoverage[i] = dirtyType[i] = true;
            }
        }

        private Texture2DArray Make(string name)
        {
            var array = new Texture2DArray(Width, Height, CloudLayerDef.MaxLayers, TextureFormat.R8, false, true)
            {
                name = name,
                filterMode = FilterMode.Bilinear,
                wrapModeU = TextureWrapMode.Repeat,
                wrapModeV = TextureWrapMode.Clamp
            };
            return array;
        }

        // Sets the targets of a layer from the field's maps (either may be null: that map keeps its target). Values are 0 to 1 as bytes;
        // the first target of a map jumps (it is primed by it), later ones are reached with the slew limit.
        internal void SetTargets(int layer, WeatherMap coverageMap, WeatherMap typeMap)
        {
            if (coverageMap != null) Copy(coverageMap, coverageGoal[layer]);
            if (typeMap != null) Copy(typeMap, typeGoal[layer]);
        }

        // A map given as bytes directly (a test map, the player's input).
        internal void SetTargetBytes(int layer, byte[] coverageBytes, byte[] typeBytes)
        {
            if (coverageBytes != null) Array.Copy(coverageBytes, coverageGoal[layer], coverageGoal[layer].Length);
            if (typeBytes != null) Array.Copy(typeBytes, typeGoal[layer], typeGoal[layer].Length);
        }

        // Diagnosis (K-T2d): the value of a layer's map at a place as the store holds it, bilinear like WeatherMap.Sample and like the pass's sample of the uploaded array
        // (equirectangular, row 0 north, column 0 at -180). goal false: the bytes the GPU array holds now (the slewed copy that is uploaded); true: the target the slew moves
        // toward (what SetTargetBytes was given). False where the store has no arrays or the layer is out of range.
        internal bool SampleBytes(int layer, double latDeg, double lonDeg, bool goal, out float coverage, out float type)
        {
            coverage = type = float.NaN;
            if (layer < 0 || layer >= CloudLayerDef.MaxLayers || Width < 2 || coverageNow[layer] == null) return false;
            coverage = Bilinear(goal ? coverageGoal[layer] : coverageNow[layer], latDeg, lonDeg);
            type = Bilinear(goal ? typeGoal[layer] : typeNow[layer], latDeg, lonDeg);
            return true;
        }

        private float Bilinear(byte[] bytes, double latDeg, double lonDeg)
        {
            double x = (lonDeg + 180.0) / 360.0 * Width - 0.5;
            double y = (90.0 - latDeg) / 180.0 * Height - 0.5;
            y = Math.Max(0.0, Math.Min(Height - 1, y));
            double x0 = Math.Floor(x), fx = x - x0;
            int ix0 = ((int)x0 % Width + Width) % Width, ix1 = (ix0 + 1) % Width;
            int iy0 = (int)Math.Floor(y), iy1 = Math.Min(iy0 + 1, Height - 1);
            double fy = y - iy0;
            double top = bytes[iy0 * Width + ix0] * (1 - fx) + bytes[iy0 * Width + ix1] * fx;
            double bottom = bytes[iy1 * Width + ix0] * (1 - fx) + bytes[iy1 * Width + ix1] * fx;
            return (float)((top * (1 - fy) + bottom * fy) / 255.0);
        }

        private void Copy(WeatherMap map, byte[] target)
        {
            if (map.Width != Width || map.Height != Height) throw new ArgumentException("The map is " + map.Width + "x" + map.Height + ", the store " + Width + "x" + Height + ".");
            map.PackBytes(0f, 1f);
            Array.Copy(map.Bytes, target, target.Length);
        }

        // Moves every layer's maps toward their targets by the slew limit over dt seconds and uploads what changed. snap jumps to the
        // targets (a cut), as does the first step of each map. Returns the number of texels still short of their target.
        internal int Step(float dt, float rate, bool snap)
        {
            int open = 0;
            for (int l = 0; l < CloudLayerDef.MaxLayers; l++)
            {
                open += StepOne(coverageNow[l], coverageGoal[l], ref coveragePrimed[l], ref dirtyCoverage[l], dt, rate, snap);
                open += StepOne(typeNow[l], typeGoal[l], ref typePrimed[l], ref dirtyType[l], dt, rate, snap);
            }
            bool coverageDirty = false, typeDirty = false;
            for (int l = 0; l < CloudLayerDef.MaxLayers; l++)
            {
                if (dirtyCoverage[l]) { Upload(Coverage, l, coverageNow[l]); dirtyCoverage[l] = false; coverageDirty = true; coverageVersion[l]++; }
                if (dirtyType[l]) { Upload(Types, l, typeNow[l]); dirtyType[l] = false; typeDirty = true; typeVersion[l]++; }
            }
            if (coverageDirty) Coverage.Apply(false);
            if (typeDirty) Types.Apply(false);
            return open;
        }

        private static int StepOne(byte[] now, byte[] goal, ref bool primed, ref bool dirty, float dt, float rate, bool snap)
        {
            if (MapSlew.Jumps(snap, primed))
            {
                if (!primed || !SameBytes(now, goal)) dirty = true;
                Array.Copy(goal, now, now.Length);
                primed = true;
                return 0;
            }
            // A map at its target is not stepped or uploaded (one comparison per texel); a step that starts short of the target is an upload.
            if (SameBytes(now, goal)) return 0;
            int open = MapSlew.StepMap(now, goal, rate, dt);
            dirty = true;
            return open;
        }

        private static bool SameBytes(byte[] a, byte[] b)
        {
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }

        private void Upload(Texture2DArray array, int slice, byte[] bytes)
        {
            for (int i = 0; i < bytes.Length; i++) scratch[i] = new Color32(bytes[i], bytes[i], bytes[i], 255);
            array.SetPixels32(scratch, slice, 0);
        }

        internal void Dispose()
        {
            if (Coverage != null) UnityEngine.Object.Destroy(Coverage);
            if (Types != null) UnityEngine.Object.Destroy(Types);
            Coverage = null;
            Types = null;
        }
    }
}
