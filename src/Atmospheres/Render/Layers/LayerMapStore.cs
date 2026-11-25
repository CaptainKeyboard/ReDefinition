using System;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // The R8 maps of the layer stack on the GPU: one coverage slice and one type slice per layer in two texture arrays, equirectangular
    // with row 0 at the north pole and column 0 at longitude -180 (the field's WeatherMap layout, so a map is copied as it is). A map
    // moves toward its target with the slew limit of MapSlew, texel by texel, so that no change is a flash (the same limit as the
    // painted maps of EVE; the upscalers and the frame generation follow a drifting edge, not a jump). The slew runs on the GPU from a
    // pair of maps per slice (LayerMapStore.GpuSlew.cs); the old per-frame step of every texel on the CPU is gone (cleanup 2 E, 2026-11-23).
    internal sealed partial class LayerMapStore
    {
        // The bytes of a layer's coverage map as it shows now, computed from the from/to pair (a whole map, for the dump and the tests; the
        // consumers that run every frame read CoverageBoundPair and CoverageReachPair).
        internal byte[] CoverageBytes(int layer)
        {
            return engine != null ? engine.NowBytes(0, layer) : null;
        }

        internal long CoverageVersion(int layer)
        {
            return engine != null ? engine.Version(0, layer) : 0;
        }

        // The bytes a layer's coverage map is moving toward, and the number of targets given so far (E40 C1: the empty-reach map of the march is built from the largest of the
        // map as it is and the map as it will be, which holds for every step of the slew between them, and is rebuilt when a target is given, not on every step of the slew).
        internal byte[] CoverageGoalBytes(int layer)
        {
            return engine != null ? engine.GoalBytes(0, layer) : null;
        }

        internal long CoverageGoalVersion(int layer)
        {
            return engine != null ? engine.Version(0, layer) : 0;
        }

        // The two maps every value the layer's coverage shows lies between, texel by texel (the culling bounds read the maximum of both: valid for
        // every moment of the slew). False where the layer has no maps yet.
        internal bool CoverageBoundPair(int layer, out byte[] a, out byte[] b)
        {
            if (engine != null) return engine.Pair(0, layer, out a, out b);
            a = b = null;
            return false;
        }

        // The pair the empty-reach map is built from: the maps as they were and as they will be.
        internal bool CoverageReachPair(int layer, out byte[] a, out byte[] b)
        {
            return CoverageBoundPair(layer, out a, out b);
        }

        // The bytes of a layer's type map as it shows now, and the number of uploads so far.
        internal byte[] TypeBytes(int layer)
        {
            return engine != null ? engine.NowBytes(1, layer) : null;
        }

        // The bytes a layer's type map is moving toward (E40 PP: the probe dumps both, the map as shown and its target).
        internal byte[] TypeGoalBytes(int layer)
        {
            return engine != null ? engine.GoalBytes(1, layer) : null;
        }

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
            engine = new SlewEngine(this);
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

        // Sets the targets of a layer as bytes (either may be null: that map keeps its target), values 0 to 1. The first target of a map jumps
        // (it is primed by it), later ones are reached with the slew limit.
        internal void SetTargetBytes(int layer, byte[] coverageBytes, byte[] typeBytes)
        {
            if (engine == null) return;
            if (coverageBytes != null) engine.Stage(0, layer, coverageBytes);
            if (typeBytes != null) engine.Stage(1, layer, typeBytes);
        }

        // Diagnosis (K-T2d): the value of a layer's map at a place as the store holds it, bilinear like WeatherMap.Sample and like the pass's sample of the uploaded array
        // (equirectangular, row 0 north, column 0 at -180). goal false: the value shown now; true: the target the slew moves toward (what SetTargetBytes was given). False where
        // the store has no maps or the layer is out of range.
        internal bool SampleBytes(int layer, double latDeg, double lonDeg, bool goal, out float coverage, out float type)
        {
            coverage = type = float.NaN;
            return engine != null && layer >= 0 && layer < CloudLayerDef.MaxLayers && Width >= 2 && engine.Sample(layer, latDeg, lonDeg, goal, out coverage, out type);
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

        // Advances the store's clock by dt seconds and puts the pairs that are due on the GPU; the shader slews between them. snap jumps to the
        // targets given so far (a cut), as does the first target of each map. Returns the goals still being made or waiting for their upload.
        internal int Step(float dt, float rate, bool snap)
        {
            return engine != null ? engine.Step(dt, rate, snap) : 0;
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
