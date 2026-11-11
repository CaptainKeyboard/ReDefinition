using System;
using System.Collections.Generic;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // The small CPU copy of the painted maps (brief C23, item 3). EVE's ground effects, the particle field, droplets, wet surfaces,
    // lightning and the ambient sound, and our own readers (RtEveClouds, the solar dimming, the lamp scatter) read the cloud coverage
    // through CloudsRaymarchedVolume.SampleCoverage, which does a bilinear read of the wrapper's private Texture2D textureValue on the
    // CPU (TextureWrapper.cs:565-594) and sees nothing of a render texture. So the mirror is a readable 512 x 256 RGBA32 Texture2D (the value in every channel) in the
    // layout of the layer's map, built from the CPU map the field gives (no readback from the GPU), by the same rule as the painter
    // pass (MapPaintModel) and injected into the wrapper's textureValue (guarded by AssumptionsC23). The same texture is what EVE puts
    // on the material when it applies the layer again, so the sky shows the right map at lower resolution for the instant between
    // EVE's Apply and our rebind.
    //
    // The mirror is worked out a band of rows a frame (Rows rows) in a cycle every Interval seconds, so the main thread has about
    // 0.3 ms of work a frame at most; the textures are uploaded when a cycle ends and Version rises (CloudMapsState).
    internal sealed class CpuMirror
    {
        internal const int Width = 512, Height = 256;
        internal const float IntervalS = 1.0f;
        internal const int RowsPerFrame = 32;

        private sealed class Entry
        {
            internal PaintSet Set;
            internal MapPaintModel Coverage, Type;
            internal Texture2D CoverageTexture, TypeTexture;
            internal int Row = Height;
            internal float CycleDt;
            internal byte[] Rgba;
        }

        private readonly List<Entry> entries = new List<Entry>();
        private float lastCycleStart = float.NegativeInfinity;
        private readonly System.Diagnostics.Stopwatch watch = new System.Diagnostics.Stopwatch();

        // Raised each time a cycle ended and the textures were uploaded.
        internal long Version { get; private set; }

        // The main-thread milliseconds of the last frame's work, and the largest of all.
        internal double LastFrameMs { get; private set; }
        internal double WorstFrameMs { get; private set; }

        // Makes sure the layout has a mirror; the level-to-type table is the one of its layers.
        internal void Ensure(PaintSet set, byte[] lut)
        {
            foreach (Entry e in entries)
            {
                if (ReferenceEquals(e.Set, set)) return;
            }
            var entry = new Entry
            {
                Set = set,
                Coverage = new MapPaintModel(Width, Height),
                Type = new MapPaintModel(Width, Height, lut),
                CoverageTexture = NewTexture("ReDefinition Atmospheres mirror coverage"),
                TypeTexture = NewTexture("ReDefinition Atmospheres mirror type")
            };
            entries.Add(entry);
        }

        // The value goes into all four channels: EVE's TextureWrapper.Sample returns the raw colour, the red channel or the alpha
        // channel by the wrapper's alphaMask (ALPHAMAP_R or the default ALPHAMAP_A), and SampleCoverage reads .a of that. An R8 texture
        // has alpha 1, so a layer whose wrapper reads alpha would see coverage 1 everywhere.
        private static Texture2D NewTexture(string name)
        {
            return new Texture2D(Width, Height, TextureFormat.RGBA32, false, true)
            {
                name = name,
                wrapModeU = TextureWrapMode.Repeat,
                wrapModeV = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.HideAndDontSave
            };
        }

        // The mirror's textures for a layout, or null.
        internal bool TryGet(PaintSet set, out Texture2D coverage, out Texture2D type)
        {
            foreach (Entry e in entries)
            {
                if (!ReferenceEquals(e.Set, set)) continue;
                coverage = e.CoverageTexture;
                type = e.TypeTexture;
                return true;
            }
            coverage = null;
            type = null;
            return false;
        }

        // Puts the layout's mirror into the two wrappers of the layer, so that EVE's CPU readers see it.
        internal bool Inject(EveLayer layer, PaintSet set)
        {
            Texture2D coverage, type;
            if (!TryGet(set, out coverage, out type)) return false;
            if (!layer.MirrorInjected)
            {
                // What EVE had in the wrappers (null where it had not loaded the pack's map yet), for the end.
                layer.OriginalCoverage = EveLayerAccess.WrapperTexture(layer.CoverageWrapper);
                layer.OriginalType = EveLayerAccess.WrapperTexture(layer.TypeWrapper);
                if (ReferenceEquals(layer.OriginalCoverage, coverage) || IsMirror(layer.OriginalCoverage)) layer.OriginalCoverage = null;
                if (ReferenceEquals(layer.OriginalType, type) || IsMirror(layer.OriginalType)) layer.OriginalType = null;
            }
            EveLayerAccess.SetWrapperTexture(layer.CoverageWrapper, coverage);
            EveLayerAccess.SetWrapperTexture(layer.TypeWrapper, type);
            layer.MirrorInjected = true;
            return true;
        }

        // Whether a texture is one of the mirror's own.
        private bool IsMirror(Texture2D t)
        {
            if (t == null) return false;
            foreach (Entry e in entries)
            {
                if (ReferenceEquals(e.CoverageTexture, t) || ReferenceEquals(e.TypeTexture, t)) return true;
            }
            return false;
        }

        // Called every frame with the clock and the field's maps. A new cycle starts every IntervalS; the rows are worked through a band
        // a frame. snap fills the mirror at once (the first cycle of a body and a cut). Returns true in the frame a cycle ended.
        internal bool Tick(float now, WeatherMap coverage, WeatherMap level, float rate, bool snap, System.Func<PaintSet, WeatherMap> coverageOf = null)
        {
            if (entries.Count == 0 || coverage == null || level == null || coverage.Values.Length == 0 || level.Values.Length == 0) return false;
            watch.Reset();
            watch.Start();
            bool finished = false;
            if (now - lastCycleStart >= IntervalS || snap)
            {
                float dt = float.IsNegativeInfinity(lastCycleStart) ? 0f : now - lastCycleStart;
                lastCycleStart = now;
                foreach (Entry e in entries)
                {
                    e.Row = 0;
                    e.CycleDt = snap ? 1000f : dt;
                }
            }
            int rows = snap ? Height : RowsPerFrame;
            foreach (Entry e in entries)
            {
                if (e.Row >= Height) continue;
                float cycleRate = e.CycleDt >= 1000f ? 1000f : rate;
                // a set of a cloud layer at one height (MapsOnly) mirrors the coverage map of that height
                WeatherMap own = e.Set.LayerKey != 0 && coverageOf != null ? coverageOf(e.Set) : null;
                if (own != null && own.Values.Length > 0) e.Coverage.UpdateRows(own.Values, own.Width, own.Height, e.Set.ShiftDeg, e.Set.Mirrored, e.Row, rows, e.CycleDt, cycleRate);
                else e.Coverage.UpdateRows(coverage.Values, coverage.Width, coverage.Height, e.Set.ShiftDeg, e.Set.Mirrored, e.Row, rows, e.CycleDt, cycleRate);
                e.Type.UpdateRows(level.Values, level.Width, level.Height, e.Set.ShiftDeg, e.Set.Mirrored, e.Row, rows, e.CycleDt, cycleRate);
                e.Row += rows;
                if (e.Row >= Height)
                {
                    Upload(e);
                    finished = true;
                }
            }
            if (finished) Version++;
            watch.Stop();
            LastFrameMs = watch.Elapsed.TotalMilliseconds;
            if (LastFrameMs > WorstFrameMs) WorstFrameMs = LastFrameMs;
            return finished;
        }

        private static void Upload(Entry e)
        {
            if (e.Rgba == null) e.Rgba = new byte[Width * Height * 4];
            ExpandToRgba(e.Coverage.Current, e.Rgba);
            e.CoverageTexture.LoadRawTextureData(e.Rgba);
            e.CoverageTexture.Apply(false, false);
            ExpandToRgba(e.Type.Current, e.Rgba);
            e.TypeTexture.LoadRawTextureData(e.Rgba);
            e.TypeTexture.Apply(false, false);
        }

        // One byte per texel to four equal bytes per texel (red, green, blue and alpha all carry the value).
        internal static void ExpandToRgba(byte[] value, byte[] rgba)
        {
            for (int i = 0; i < value.Length; i++)
            {
                byte v = value[i];
                int o = i * 4;
                rgba[o] = v;
                rgba[o + 1] = v;
                rgba[o + 2] = v;
                rgba[o + 3] = v;
            }
        }

        // The mirror's current maps for the tests (and the consistency check against the GPU's read-back).
        internal bool TryGetMaps(PaintSet set, out byte[] coverage, out byte[] type)
        {
            foreach (Entry e in entries)
            {
                if (!ReferenceEquals(e.Set, set)) continue;
                coverage = e.Coverage.Current;
                type = e.Type.Current;
                return true;
            }
            coverage = null;
            type = null;
            return false;
        }

        internal void Clear()
        {
            foreach (Entry e in entries)
            {
                if (e.CoverageTexture != null) UnityEngine.Object.Destroy(e.CoverageTexture);
                if (e.TypeTexture != null) UnityEngine.Object.Destroy(e.TypeTexture);
            }
            entries.Clear();
            lastCycleStart = float.NegativeInfinity;
        }
    }
}
