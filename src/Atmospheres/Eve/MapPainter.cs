using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // One set of painted maps: the coverage and the type target of a layout (shift and mirror), as EVE's own painter makes them
    // (Painting/PainterRenderTexture.cs:69-80: R8, Bilinear, Repeat, no mips). The RenderTextures are created once and live as long as
    // the painter; EVE's materials hold them by reference, so a paint never needs a rebind.
    internal sealed class PaintSet
    {
        internal RenderTexture Coverage, Type, Scratch;
        internal double ShiftDeg;
        internal bool Mirrored;
        // 0: the set paints from the field's shared coverage map. Above 0 (layerWindSteering MapsOnly, package E43): the set belongs to the cloud layers at one
        // height (LayerKeyOf) and paints from FieldCoverage, the coverage map of that height uploaded for it alone.
        internal int LayerKey;
        internal Texture2D FieldCoverage;
        // The number of texels still away from the field's map is unknown on the GPU; Settled counts the updates since the last jump.
        internal bool Primed;
        // The same for the type target: its pass is skipped while the level-to-type table is not set, so it is primed on its own first run.
        internal bool TypePrimed;
    }

    // The GPU painter of the weather field's maps (brief C23, item 1). From the field's maps (IWeatherField.GetMap: Coverage and
    // Precipitation) it paints, per layout of the EVE layers of the body, a coverage target and a type target by the passes of
    // ReDefinition/AtmospheresMapPaint, and the orbit picture. The field's map is uploaded as a small texture, the passes resample it
    // into the layer's frame (EveMapLayout) and move the target toward it by the slew limit (MapSlew).
    //
    // Cost: one upload of two maps (the field's size, 8 bit) and three full-screen passes per paint, at most every Interval seconds;
    // the main thread only uploads and issues the blits. The painter owns no EVE object. Pass the shader the painter draws with (the
    // atmospheres' bundle, or a player's resources).
    internal sealed class MapPainter : IDisposable
    {
        internal const int DefaultWidth = 2048, DefaultHeight = 1024;
        internal const int BaseWidth = 4096, BaseHeight = 2048;

        private const int PassCoverage = 0, PassType = 1, PassOrbit = 2;

        private readonly Material material;
        private readonly int width, height;
        private readonly System.Collections.Generic.List<PaintSet> sets = new System.Collections.Generic.List<PaintSet>();
        private Texture2D fieldCoverage, fieldLevel, lutTexture;
        private byte[] lutBytes;
        private readonly RenderTexture[] orbits = new RenderTexture[2];
        private Texture2D overlay;
        private readonly Stopwatch watch = new Stopwatch();

        // The field maps of the last paint, kept for the mirror and the swirl finder (the painter's caller reads them).
        internal readonly WeatherMap CoverageMap = new WeatherMap();
        internal readonly WeatherMap LevelMap = new WeatherMap();

        // The wall time the last paint took on the main thread, in ms (uploads and blit submission), and the sum and count for the mean.
        internal double LastMainThreadMs;
        internal double TotalMainThreadMs;
        internal int Paints;

        internal MapPainter(Shader shader, int width = DefaultWidth, int height = DefaultHeight)
        {
            if (shader == null) throw new ArgumentNullException("shader");
            this.width = width;
            this.height = height;
            material = new Material(shader) { name = "ReDefinition Atmospheres map paint", hideFlags = HideFlags.HideAndDontSave };
        }

        internal int Width { get { return width; } }
        internal int Height { get { return height; } }
        internal int SetCount { get { return sets.Count; } }

        // The paint sets (one per layout of the layers).
        internal System.Collections.Generic.IList<PaintSet> Sets { get { return sets; } }

        // The pictures for the orbit and map view: slot 0 is the one EVE's 2D layers show, slot 1 the one the own shell draws; null
        // until PaintOrbit ran for the slot.
        internal const int OrbitEve = 0, OrbitShell = 1;

        internal RenderTexture Orbit(int slot)
        {
            return orbits[slot];
        }

        // The set for a layout; created (and painted at once on the next Paint) where none is within a quarter degree.
        internal PaintSet SetFor(double shiftDeg, bool mirrored, int layerKey = 0)
        {
            foreach (PaintSet s in sets)
            {
                double d = Math.Abs(EveMapLayout.WrapDegrees(s.ShiftDeg - shiftDeg));
                if (s.Mirrored == mirrored && s.LayerKey == layerKey && d < 0.25) return s;
            }
            var set = new PaintSet
            {
                LayerKey = layerKey,
                Coverage = NewTarget("ReDefinition Atmospheres cloud coverage"),
                Type = NewTarget("ReDefinition Atmospheres cloud type"),
                Scratch = NewTarget("ReDefinition Atmospheres paint scratch"),
                ShiftDeg = shiftDeg,
                Mirrored = mirrored
            };
            sets.Add(set);
            return set;
        }

        private RenderTexture NewTarget(string name)
        {
            var rt = new RenderTexture(width, height, 0, RenderTextureFormat.R8, RenderTextureReadWrite.Linear)
            {
                name = name,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Repeat,
                useMipMap = false,
                autoGenerateMips = false
            };
            rt.Create();
            return rt;
        }

        // Uploads the field's maps for this paint: coverage and the precipitation level, 8 bit.
        internal void Upload(WeatherMap coverage, WeatherMap level)
        {
            coverage.PackBytes(0f, 1f);
            level.PackBytes(0f, 1f);
            Upload(ref fieldCoverage, coverage, "ReDefinition Atmospheres field coverage");
            Upload(ref fieldLevel, level, "ReDefinition Atmospheres field level");
        }

        // Uploads the coverage map of the cloud layers of one height for their set (MapsOnly), 8 bit.
        internal void UploadLayer(PaintSet set, WeatherMap coverage)
        {
            coverage.PackBytes(0f, 1f);
            Upload(ref set.FieldCoverage, coverage, "ReDefinition Atmospheres layer coverage");
        }

        private static void Upload(ref Texture2D texture, WeatherMap map, string name)
        {
            if (texture == null || texture.width != map.Width || texture.height != map.Height)
            {
                if (texture != null) UnityEngine.Object.Destroy(texture);
                texture = new Texture2D(map.Width, map.Height, TextureFormat.R8, false, true)
                {
                    name = name,
                    wrapModeU = TextureWrapMode.Repeat,
                    wrapModeV = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear,
                    hideFlags = HideFlags.HideAndDontSave
                };
            }
            texture.LoadRawTextureData(map.Bytes);
            texture.Apply(false, false);
        }

        // Sets the level-to-type table of the layers (CloudTypeEncoding.BuildLut); the type pass reads it.
        internal void SetLut(byte[] lut)
        {
            if (lut == null || lut.Length != CloudTypeEncoding.LutSize) return;
            if (lutTexture == null)
            {
                lutTexture = new Texture2D(CloudTypeEncoding.LutSize, 1, TextureFormat.R8, false, true)
                {
                    name = "ReDefinition Atmospheres type table",
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Point,
                    hideFlags = HideFlags.HideAndDontSave
                };
            }
            lutBytes = lut;
            lutTexture.LoadRawTextureData(lut);
            lutTexture.Apply(false, false);
        }

        // Paints one set: the coverage and the type target move toward the uploaded maps by rate x dt, or jump to them with snap.
        // Returns the main-thread milliseconds.
        internal double Paint(PaintSet set, float dt, float rate, bool snap)
        {
            Texture2D sourceCoverage = set.LayerKey != 0 ? set.FieldCoverage : fieldCoverage;
            if (sourceCoverage == null || fieldLevel == null) return 0;
            watch.Reset();
            watch.Start();
            float maxDelta = MapSlew.MaxDelta(rate, dt);
            float shift = (float)(set.ShiftDeg / 360.0);
            material.SetFloat("_Shift", shift);
            material.SetFloat("_Mirror", set.Mirrored ? 1f : 0f);
            material.SetFloat("_MaxDelta", maxDelta);
            material.SetFloat("_Snap", MapSlew.Jumps(snap, set.Primed) ? 1f : 0f);
            Copy(set.Coverage, set.Scratch);
            material.SetTexture("_Prev", set.Scratch);
            Blit(sourceCoverage, set.Coverage, PassCoverage);
            if (lutTexture != null)
            {
                material.SetFloat("_Snap", MapSlew.Jumps(snap, set.TypePrimed) ? 1f : 0f);
                Copy(set.Type, set.Scratch);
                material.SetTexture("_Prev", set.Scratch);
                material.SetTexture("_Lut", lutTexture);
                Blit(fieldLevel, set.Type, PassType);
                set.TypePrimed = true;
            }
            set.Primed = true;
            Paints++;
            watch.Stop();
            LastMainThreadMs = watch.Elapsed.TotalMilliseconds;
            TotalMainThreadMs += LastMainThreadMs;
            return LastMainThreadMs;
        }

        // Paints the orbit picture of a layout with the swirl overlay (RGBA, layer frame; may be null for none). The picture is
        // RGBA32 at EveSwirlMath's size and the one EVE's 2D layer and the own shell show.
        internal void PaintOrbit(int slot, double shiftDeg, bool mirrored, byte[] overlayRgba, float greyScale)
        {
            if (fieldCoverage == null || fieldLevel == null) return;
            if (orbits[slot] == null)
            {
                orbits[slot] = new RenderTexture(EveSwirlMath.Width, EveSwirlMath.Height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear)
                {
                    name = "ReDefinition Atmospheres orbit picture",
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Repeat,
                    useMipMap = false
                };
                orbits[slot].Create();
            }
            if (overlay == null)
            {
                overlay = new Texture2D(EveSwirlMath.Width, EveSwirlMath.Height, TextureFormat.RGBA32, false, true)
                {
                    name = "ReDefinition Atmospheres swirl cells",
                    wrapModeU = TextureWrapMode.Repeat,
                    wrapModeV = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear,
                    hideFlags = HideFlags.HideAndDontSave
                };
                var clear = new byte[EveSwirlMath.Width * EveSwirlMath.Height * 4];
                overlay.LoadRawTextureData(clear);
                overlay.Apply(false, false);
            }
            if (overlayRgba != null)
            {
                overlay.LoadRawTextureData(overlayRgba);
                overlay.Apply(false, false);
            }
            material.SetFloat("_Shift", (float)(shiftDeg / 360.0));
            material.SetFloat("_Mirror", mirrored ? 1f : 0f);
            material.SetFloat("_GreyScale", greyScale);
            material.SetFloat("_Faint", OrbitPicture.FaintOpacity);
            material.SetFloat("_SwirlFrom", OrbitPicture.SwirlFromLevel);
            material.SetTexture("_Level", fieldLevel);
            material.SetTexture("_Overlay", overlay);
            Blit(fieldCoverage, orbits[slot], PassOrbit);
        }

        // The engine calls are kept in methods of their own that are never inlined: the unit tests (no engine) run the code around them.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void Blit(Texture source, RenderTexture target, int pass)
        {
            Graphics.Blit(source, target, material, pass);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Copy(RenderTexture from, RenderTexture to)
        {
            Graphics.CopyTexture(from, to);
        }

        // Reads a target back for a test (a player scene or a probe; never on the game's frame path).
        internal static byte[] ReadBackRed(RenderTexture rt)
        {
            RenderTexture old = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false, true);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0, false);
            tex.Apply(false, false);
            byte[] rgba = tex.GetRawTextureData();
            UnityEngine.Object.Destroy(tex);
            RenderTexture.active = old;
            var red = new byte[rt.width * rt.height];
            for (int i = 0; i < red.Length; i++) red[i] = rgba[i * 4];
            return red;
        }

        public void Dispose()
        {
            foreach (PaintSet s in sets)
            {
                if (s.Coverage != null) s.Coverage.Release();
                if (s.Type != null) s.Type.Release();
                if (s.Scratch != null) s.Scratch.Release();
                Destroy(ref s.FieldCoverage);
            }
            sets.Clear();
            for (int i = 0; i < orbits.Length; i++)
            {
                if (orbits[i] != null) orbits[i].Release();
                orbits[i] = null;
            }
            Destroy(ref fieldCoverage);
            Destroy(ref fieldLevel);
            Destroy(ref lutTexture);
            Destroy(ref overlay);
            if (material != null) UnityEngine.Object.Destroy(material);
        }

        private static void Destroy(ref Texture2D t)
        {
            if (t != null) UnityEngine.Object.Destroy(t);
            t = null;
        }
    }
}
