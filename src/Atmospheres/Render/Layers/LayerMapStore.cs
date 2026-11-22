using System;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // The R8 maps of the layer stack on the GPU: one coverage slice and one type slice per layer in two texture arrays, equirectangular
    // with row 0 at the north pole and column 0 at longitude -180 (the field's WeatherMap layout, so a map is copied as it is). A map
    // moves toward its target with the slew limit of MapSlew, texel by texel, so that no change is a flash (the same limit as the
    // painted maps of EVE; the upscalers and the frame generation follow a drifting edge, not a jump).
    internal sealed partial class LayerMapStore
    {
        private readonly byte[][] coverageNow = new byte[CloudLayerDef.MaxLayers][];
        private readonly byte[][] coverageGoal = new byte[CloudLayerDef.MaxLayers][];
        private readonly byte[][] typeNow = new byte[CloudLayerDef.MaxLayers][];
        private readonly byte[][] typeGoal = new byte[CloudLayerDef.MaxLayers][];
        private readonly bool[] coveragePrimed = new bool[CloudLayerDef.MaxLayers];
        private readonly bool[] typePrimed = new bool[CloudLayerDef.MaxLayers];
        private readonly bool[] dirtyCoverage = new bool[CloudLayerDef.MaxLayers];
        private readonly bool[] dirtyType = new bool[CloudLayerDef.MaxLayers];
        private byte[] pairsScratch;
        // Rises each time a layer's coverage is uploaded (package T16: the culling's bound maps are rebuilt from the bytes of that frame).
        private readonly long[] coverageVersion = new long[CloudLayerDef.MaxLayers];

        // The bytes of a layer's coverage map as uploaded last, and the number of uploads so far.
        // With GpuSlew the maps are not held as one shown copy: this is then the shown value computed now from the from/to pair (a whole map, for the dump and the tests; the
        // consumers that run every frame read CoverageBoundPair and CoverageReachPair).
        internal byte[] CoverageBytes(int layer)
        {
            return engine != null ? engine.NowBytes(0, layer) : coverageNow[layer];
        }

        internal long CoverageVersion(int layer)
        {
            return engine != null ? engine.Version(0, layer) : coverageVersion[layer];
        }

        // The bytes a layer's coverage map is moving toward, and the number of targets given so far (E40 C1: the empty-reach map of the march is built from the largest of the
        // map as it is and the map as it will be, which holds for every step of the slew between them, and is rebuilt when a target is given, not on every step of the slew).
        internal byte[] CoverageGoalBytes(int layer)
        {
            return engine != null ? engine.GoalBytes(0, layer) : coverageGoal[layer];
        }

        internal long CoverageGoalVersion(int layer)
        {
            return engine != null ? engine.Version(0, layer) : coverageGoalVersion[layer];
        }

        // The two maps every value the layer's coverage shows lies between, texel by texel (the culling bounds read the maximum of both: valid for
        // every moment of the slew). Without GpuSlew both are the shown map. False where the layer has no maps yet.
        internal bool CoverageBoundPair(int layer, out byte[] a, out byte[] b)
        {
            if (engine != null) return engine.Pair(0, layer, out a, out b);
            a = b = coverageNow[layer];
            return a != null;
        }

        // The pair the empty-reach map is built from: the maps as they were and as they will be (the shown map and its goal without GpuSlew).
        internal bool CoverageReachPair(int layer, out byte[] a, out byte[] b)
        {
            if (engine != null) return engine.Pair(0, layer, out a, out b);
            a = coverageNow[layer];
            b = coverageGoal[layer];
            return a != null && b != null;
        }

        private readonly long[] coverageGoalVersion = new long[CloudLayerDef.MaxLayers];

        // The bytes of a layer's type map as uploaded last, and the number of uploads so far.
        internal byte[] TypeBytes(int layer)
        {
            return engine != null ? engine.NowBytes(1, layer) : typeNow[layer];
        }

        internal long TypeVersion(int layer)
        {
            return engine != null ? engine.Version(1, layer) : typeVersion[layer];
        }

        private readonly long[] typeVersion = new long[CloudLayerDef.MaxLayers];

        // The bytes a layer's type map is moving toward (E40 PP: the probe dumps both, the map as uploaded and its target).
        internal byte[] TypeGoalBytes(int layer)
        {
            return engine != null ? engine.GoalBytes(1, layer) : typeGoal[layer];
        }

        internal Texture2DArray Coverage { get; private set; }
        internal Texture2DArray Types { get; private set; }
        internal int Width { get; private set; }
        internal int Height { get; private set; }

        // Makes the arrays at a size (all layers share it); a size change drops the contents.
        internal void Ensure(int width, int height)
        {
            Ensure(width, height, false);
        }

        // gpuSlew: the maps slew on the GPU (LayerMapStore.GpuSlew.cs); a change of the mode drops the contents like a change of size.
        internal void Ensure(int width, int height, bool gpuSlew)
        {
            if (width < 2 || height < 2) throw new ArgumentOutOfRangeException("width");
            if (Coverage != null && Width == width && Height == height && GpuSlew == gpuSlew) return;
            Dispose();
            Width = width;
            Height = height;
            GpuSlew = gpuSlew;
            Coverage = Make("layer coverage maps");
            Types = Make("layer type maps");
            pairsScratch = new byte[width * height * 2];
            if (gpuSlew) engine = new SlewEngine(this);
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
            var array = new Texture2DArray(Width, Height, CloudLayerDef.MaxLayers, TextureFormat.RG16, false, true)
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
            if (engine != null)
            {
                if (coverageMap != null) { Copy(coverageMap, engine.ScratchGoal); engine.Stage(0, layer, engine.ScratchGoal); }
                if (typeMap != null) { Copy(typeMap, engine.ScratchGoal); engine.Stage(1, layer, engine.ScratchGoal); }
                return;
            }
            if (coverageMap != null) { Copy(coverageMap, coverageGoal[layer]); coverageGoalVersion[layer]++; }
            if (typeMap != null) Copy(typeMap, typeGoal[layer]);
        }

        // A map given as bytes directly (a test map, the player's input).
        internal void SetTargetBytes(int layer, byte[] coverageBytes, byte[] typeBytes)
        {
            if (engine != null)
            {
                if (coverageBytes != null) engine.Stage(0, layer, coverageBytes);
                if (typeBytes != null) engine.Stage(1, layer, typeBytes);
                return;
            }
            if (coverageBytes != null) { Array.Copy(coverageBytes, coverageGoal[layer], coverageGoal[layer].Length); coverageGoalVersion[layer]++; }
            if (typeBytes != null) Array.Copy(typeBytes, typeGoal[layer], typeGoal[layer].Length);
        }

        // Diagnosis (K-T2d): the value of a layer's map at a place as the store holds it, bilinear like WeatherMap.Sample and like the pass's sample of the uploaded array
        // (equirectangular, row 0 north, column 0 at -180). goal false: the bytes the GPU array holds now (the slewed copy that is uploaded); true: the target the slew moves
        // toward (what SetTargetBytes was given). False where the store has no arrays or the layer is out of range.
        internal bool SampleBytes(int layer, double latDeg, double lonDeg, bool goal, out float coverage, out float type)
        {
            coverage = type = float.NaN;
            if (engine != null) return layer >= 0 && layer < CloudLayerDef.MaxLayers && Width >= 2 && engine.Sample(layer, latDeg, lonDeg, goal, out coverage, out type);
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
            if (engine != null) return engine.Step(dt, rate, snap);
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

        // The old path's upload: the shown map as pairs (value, value), so that the shader's slew reads it with a reach of zero.
        private void Upload(Texture2DArray array, int slice, byte[] bytes)
        {
            MapSlewField.InterleaveSame(bytes, pairsScratch);
            array.SetPixelData(pairsScratch, 0, slice, 0);
        }

        internal void Dispose()
        {
            if (engine != null) { engine.Stop(); engine = null; }
            if (Coverage != null) UnityEngine.Object.Destroy(Coverage);
            if (Types != null) UnityEngine.Object.Destroy(Types);
            Coverage = null;
            Types = null;
        }
    }
}
