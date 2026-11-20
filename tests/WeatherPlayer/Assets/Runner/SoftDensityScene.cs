// Scenes of the weather player for the soft water content of the own clouds (E40 soft density; tools/weather_metrics/e40gs.py measures the output, the inputs
// are e40gn.py's):
//   -scene soft-temporal -in <e40gn inputs>   the layer pass as the game runs it (GrainProbeScene's rig and scenarios) for a list of MEDIA (the density model) on
//                                             the unchanged grid march; a converged reference of every written pose PER MEDIUM
//   -scene soft-cost -in <e40gn inputs>       the GPU milliseconds of the same passes at 1707 x 960 (a timing run)
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ReDefinition.Atmospheres;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.WeatherPlayer
{
    internal static class SoftTable
    {
        internal sealed class Medium
        {
            public string Name;
            // The PRODUCT's own density (E40 soft density, package B1): the job's layer numbers stay as the file gave them, CloudLayerDef's effective numbers, the type table's
            // rise by height, the exponent and the clear-sky rule make the water content (Width is the owner's softness setting).
            public float Width = 0.5f;
        }

        // The media of step (a) that built the water content out of the old density (hard, sig01, e50p2, e100p3, s30k2 .. s100k2, c50k2 .. c100k2w) went with the old density and
        // layers.edgePower (E40 step (c)); the product's own density at the owner's two softness values is what they were built to match.
        internal static readonly Medium[] Media =
        {
            // The cfg's per-layer numbers of the job's layer: no softnessScale, the exponent 2, the type's gain 0.3 and rise 0.5.
            new Medium { Name = "pr50", Width = 0.5f },
            new Medium { Name = "pr100", Width = 1f },
            // E40 V1: narrower soft density widths, to see what the width does to the outlines.
            new Medium { Name = "pr25", Width = 0.25f },
            new Medium { Name = "pr10", Width = 0.1f },
        };

        // E40 V1 (-light product): the sun the game gives the pass (the viewer's air divided out of the colour, the shader puts the air above each sample back: SunAirWhiteBalance off,
        // Kerbin's air), as TypeSheetScene sets it. The job's sunColour is the pass's colour.
        internal static void ProductLight(LayerFrame f)
        {
            f.Light.SunAirWhiteBalance = false;
            f.Light.UseAir(new Vector3(0.01534f, 0.03571f, 0.08755f) / 1000f, 3840f, 0.01214f / 0.9f / 1000f, 1000f);
        }

        // -dump last: the still scenario writes frame 39 only (a case's four frames are for the temporal noise, not for a picture).
        internal static bool DumpLast { get { return WeatherPass.Argument("-dump") == "last"; } }
        internal static bool ProductLightOn { get { return WeatherPass.Argument("-light") == "product"; } }

        // The frames of the still scenario that are written: -dumpframes 1,3,39 (E40 PP64: the frames after a history cut, to find how many frames of history the game's grain corresponds to),
        // else 39 with -dump last, else 36 to 39.
        internal static int[] StaticDump()
        {
            string arg = WeatherPass.Argument("-dumpframes");
            if (!string.IsNullOrEmpty(arg))
            {
                var frames = new System.Collections.Generic.List<int>();
                foreach (string part in arg.Split(','))
                {
                    int value;
                    if (int.TryParse(part, out value) && value >= 0 && value < 40) frames.Add(value);
                }
                if (frames.Count > 0) return frames.ToArray();
            }
            return DumpLast ? new[] { 39 } : new[] { 36, 37, 38, 39 };
        }

        // The marches every medium is drawn with (GrainTable's: the product's grid march with the golden-ratio offset; 32 and 24 grid points at most).
        // -marches a,b,c replaces the list; seq1m<N> is N grid points at most, tseq1m<N> the same on the span cut to the types present (LayerFrame.SpanTight).
        internal static string[] Marches
        {
            get
            {
                string arg = WeatherPass.Argument("-marches");
                return string.IsNullOrEmpty(arg) ? new[] { "seq1", "seq1m32", "seq1m24" } : arg.Split(',');
            }
        }
        internal static readonly string[] Modes = { "plain", "acc20" };

        // The media of a run: all, or the names of -media a,b,c.
        internal static List<Medium> Selected()
        {
            string arg = WeatherPass.Argument("-media");
            var list = new List<Medium>();
            foreach (Medium m in Media)
                if (string.IsNullOrEmpty(arg) || Array.IndexOf(arg.Split(','), m.Name) >= 0) list.Add(m);
            return list;
        }

        // The layer's numbers as the job file gave them (the rig is loaded with a medium's numbers in their place).
        internal struct Original
        {
            public float Softness, Sigma, Coverage, NoiseAmount;
            public float[] Profile;

            internal static Original Of(LayersJobLayer jl)
            {
                return new Original { Softness = jl.softness, Sigma = jl.sigma, Coverage = jl.coverage, NoiseAmount = jl.noiseAmount, Profile = (float[])jl.types[0].profile.Clone() };
            }

            internal void Restore(LayersJobLayer jl)
            {
                jl.softness = Softness;
                jl.sigma = Sigma;
                jl.coverage = Coverage;
                jl.noiseAmount = NoiseAmount;
                jl.types[0].profile = (float[])Profile.Clone();
            }
        }

        // Loads the rig with the medium's layer numbers. The product's medium rewrites nothing of the job's layer: the owner's softness and the detail mean reach the
        // layer through CloudSoftDensity (the game: OwnCloudOptions and the noise binding).
        internal static void Load(TemporalRig rig, LayersJob job, string input, Medium m, Original o, double detailMean)
        {
            LayersJobLayer jl = job.layers[0];
            o.Restore(jl);
            CloudSoftDensity.Softness = m.Width;
            CloudSoftDensity.DetailMean = detailMean;
            rig.Load(job, input);
        }

        internal static double DetailMean(TemporalRig rig)
        {
            NoiseVolumeSet set = rig.Renderer.Noise.ShapeVolumes;
            return set != null ? set.DetailMean : 0.5;
        }

        // The march of a name on the frame (GrainTable's, or seq1m<N> / tseq1m<N>).
        internal static void ApplyMarch(LayerFrame f, string name)
        {
            GrainTable.Apply(f, March(name));
        }

        internal static GrainTable.March March(string name)
        {
            bool tight = name.StartsWith("tseq1m");
            if (tight || name.StartsWith("seq1m"))
                return new GrainTable.March { Name = name, Tight = tight, MaxSteps = int.Parse(name.Substring(tight ? 6 : 5)) };
            return GrainTable.MarchByName(name);
        }

        // E40 ground cost (series e40-gc-*): a case is a march name and modifiers joined by '+', every modifier the player's twin of a tuning key or setting:
        //   r<NN>     the march target's side over the full target's, NN / 100 (recon.resolution: r40 is 0.4)      jit    the per-frame sub-pixel jitter (ReconSettings.Jitter; no key)
        //   acc<NN>   the history weight, NN / 100 (recon.accumulate)                                              blk<N> the block level N of cloudResolution (no march history of its own)
        //   clip<NN>  the history's clip width in standard deviations, NN / 10 (the product: 10; clip1000 is no clip in practice)
        //   ls<N>     layers.lightStride              lc<NNN>  layers.lightCutoff NNN / 1000                    ds<N> layers.ditherSequence
        //   lq<N>     cloudLightQuality 0 Low, 1 Medium, 2 High (taps and octaves; without it the frame's own: 3 sun taps, 4 octaves)   sun<N> the frame's sun steps
        //   skip      cloudSkipEmpty (the tile pass)    skipc    cloudSkipClosed                                  leap<N> layers.emptyLeap
        //   lm<N>     the layers that are marched, bit i = layer i (0: all; a measurement of what dropping the empty layers before the march would save)
        //   dbg<N>    the cost debug mask (1 no light, 2 no shape noise, 4 no erosion; not a picture of the product)
        //   og        the pattern's pixel grid of the pass before E40 F2 (the camera's size scales the uv)       cr<N>  the light cone's rotation pattern: 0 gradient noise, 1 fixed, 2 hash, 3 blue noise (the default)
        // The base is seq1 (the product's grid march with the 48 points of the High setting), seq1m<N> and tseq1m<N> (N grid points, the second on the cut span) or a GrainTable name.
        internal sealed class Case
        {
            public string Name, Base = "seq1";
            public float Resolution = 0.5f, Accumulate = 0.2f, Cutoff = -1f, ClipSigma = -1f;
            public bool Jitter, Skip, SkipClosed, OldGrid, NoReach, StepGrowth, TapLod, FarMean, DetailLod;
            public int ConePattern = -1, Block, Stride = -1, Dither = -1, Debug = -1, Quality = -1, Sun = -1, Leap = -1, Mask;
            // E40 C1: sp<NN> the widest grid spacing of a span in the layer's noise scales, NN / 10 (0: off); spm<N> the fewest grid points such a span keeps
            public float Spacing;
            public int SpacingMin = 20;
            // E40 C2: mn<N> a moon of N / 10000 of the sun's luminance (the game's moon is lit by any body in the sky, day or night; the player had none), mc<N> the product's light.moonMinShare
            // N / 1000 (a moon below that share of the sun is left out, OwnCloudOptions.MoonTooFaint), lp<NN> the longest leap over empty grid points in the layer's noise scales, NN / 100
            // (LayerFrame.LeapScales), ev the GPU's caches flushed before the pass (a copy of 64 MB; the game draws the rest of the frame between two passes, the player's loop only this one).
            public int Moon, MoonCut;
            public bool Evict;
            public float Leap100;
            // E40 C3: cg<N> the step growth along a span (cloudStepGrowth) of N / 1000000 per m (cg alone: the switch's own default), eo<N> the march's early-out transmittance N / 1000 (the product's 0.01).
            public int GrowthE6, EarlyOut;
            // E40 C4: wt<N> layers.warpTable (0 the warp's lattice values hashed at every sample, 1 the table of half floats, 2 the table of bytes; the product 0).
            public int WarpTable;
            // PUFF ceiling render: ceil the march and the light without the real-time compromises (256 grid points over the span at 6 m at least, no step growth, early-out 0.001, no
            // transmittance growth, every lit step marches to the sun, no light cutoff, 16 sun taps over 12 km). Used with r100+jit+acc<N>+clip1000: the reconstruction then accumulates jittered full-size frames.
            // cstep alone is the march's group (grid, minimum step, no growth, early-out), clight alone the light's group (stride, cutoff, taps, reach); ceil is both.
            public bool CeilStep, CeilTaps;
            // CLOUDR1 ablation of the ceiling's march group, one lever each: c256 256 grid points, cmin6 a minimum step of 6 m, cng no step and transmittance growth, ceo early-out 0.001,
            // cel no leap over empty grid points; cls every lit step marches to the sun (stride 1, no cutoff) with the product's taps.
            public bool C256, Cmin6, Cng, Ceo, Cel, Cls;
            // A4 search round 2 (owner 2026-11-18: save where clouds are hidden or contribute little, before the size): ctaps the ceiling's sun taps alone (16 over 12 km, the product's
            // stride and cutoff); cnsg no step growth along the ray but the product's growth with the lost transmittance; cst1 every lit step marches (stride 1) with the product's cutoff.
            public bool Ctaps, Cnsg, Cst1;
            // Round 3: cms<N> the shortest step in m; ctap<N> the sun taps (with ctaps' 12 km march). Round 4: csm<N> the sun march in km (with ctaps or ctap<N>).
            public int MinStepOverride, SunTaps, SunMarchKm;
            // CLOUDR3 P7: chm reads the reconstruction's history with the Catmull-Rom filter (ReconSettings.CatmullHistory).
            public bool CatmullHistory;
            // bexact: the block mode as a pixel-exact rotation (Settings); bacc: the same with the case's accumulation on the fresh sample.
            public bool BlockExact, BlockAcc;
            // far<km>: the far rotation beyond that distance (ReconSettings.FarRotateM); farb<N> its block side (2: every 4th frame).
            public int FarKm, FarBlock;
            // lrm: the reused sun optical depth moved by the ray's progress (LayerFrame.LightReuseMove).
            public bool ReuseMove;
            // cg<N> in grid points would clash with the growth cases: the grid is ng<N> (ng96 = 96 grid points).
            public int Grid;
            // The light cache (LightCache.cs, 2026-11-18): lcache<N> on with N near sun taps of the march's own; lcfill<N> the fill's taps, lcbox<km> the box's width,
            // lcgrid<N> its voxels across (the voxels up are a quarter of them).
            public int CacheNear, CacheFill, CacheKm, CacheGrid;
            // The light volume (LV1, cloud.lightVolume): lv<km> on with that hand-over distance (lvinf: on, hand-over at infinity), lvn<N> slices per frame (0: all), lvt<N> the far near taps,
            // lvw<N> the warp factor; set as tuning overrides by ApplyCase (no lv token: off).
            public bool LvOn;
            public float LvKm = -1f;
            public int LvSlices = -1, LvTaps = -1, LvWarp = -1;
            // The diagnosis levers: lvc<N> cascades, lvz<N> slices of a cascade, lvv<N> the near voxel in m, lvf<N> the fill's taps, lvr<N> their growth in hundredths, lvb<N> the band in percent.
            public int LvCascades = -1, LvGridZ = -1, LvVoxel = -1, LvFillTaps = -1, LvFillRatio = -1, LvBand = -1;
            // lvs<N>: the far cascade's voxel over the near one's.
            public int LvFarScale = -1;
            // LV3: lvsp<N> the spread's source, lvsr<N> its radius in m, lvsd<N> its depth in tenths of an optical depth, lvsm<N> its largest weight in percent.
            public int LvSpread = -1, LvSpreadRadius = -1, LvSpreadDepth = -1, LvSpreadMax = -1;
            // LV2: lvm<N> the fill (0 direct, 1 propagation, 2 / 3 reference), lvq<N> the near length's factor in hundredths, lvk<N> / lvx<N> the
            // propagation's least / most samples, lvl<N> its limit in voxels.
            public int LvFill = -1, LvNearScale = -1, LvPropSteps = -1, LvPropCap = -1, LvPropLimit = -1;
            // lvh<N> k of the stored transmittance, lvp1 the read past the footprint, lvo<N> the time warp's rate (LightVolume.TimeWarpRate), lvu<N> the warp limit.
            public int LvStoreK = -1, LvReadPast = -1, LvWarpRate = -1, LvWarpLimit = -1;
            // lvy1: the capped read.
            public int LvCapRead = -1;
        }

        private static LightCache cache;

        // Diagnosis of the light cache: the largest and the mean value of its texture, read back from the GPU (nothing while the cache is off).
        internal static void ReportCache(MetricSink sink, string key)
        {
            if (cache == null || !cache.Enabled || cache.Volume == null) return;
            RenderTexture v = cache.Volume;
            AsyncGPUReadbackRequest r = AsyncGPUReadback.Request(v, 0, 0, v.width, 0, v.height, 0, v.volumeDepth, TextureFormat.RFloat);
            r.WaitForCompletion();
            if (r.hasError) { sink.Info(key + ".readback_error", 1); return; }
            float max = 0f;
            double sum = 0;
            long n = 0;
            for (int layer = 0; layer < r.layerCount; layer++)
            {
                Unity.Collections.NativeArray<float> d = r.GetData<float>(layer);
                for (int i = 0; i < d.Length; i++) { max = Mathf.Max(max, d[i]); sum += d[i]; n++; }
            }
            sink.Info(key + ".max", max);
            sink.Info(key + ".mean", n > 0 ? sum / n : 0.0);
            sink.Info(key + ".layers", r.layerCount);
        }

        private static int Number(string token, int prefix)
        {
            return int.Parse(token.Substring(prefix));
        }

        internal static Case Parse(string name)
        {
            string[] parts = name.Split('+');
            var c = new Case { Name = name, Base = parts[0] };
            for (int i = 1; i < parts.Length; i++)
            {
                string t = parts[i];
                if (t == "jit") c.Jitter = true;
                else if (t == "skip") c.Skip = true;
                else if (t == "skipc") c.SkipClosed = true;
                else if (t == "nr") c.NoReach = true;   // E40 C1: the march without the leap over the empty reach (LayerFrame.EmptyReach false)
                else if (t == "cg") c.StepGrowth = true;   // T16 cloudStepGrowth
                else if (t == "tl") c.TapLod = true;   // T16 cloudTapLod
                else if (t == "fm") c.FarMean = true;   // cloudFarShapeMean
                else if (t == "dlod") c.DetailLod = true;   // cloudDetailLod (A4 perf program point 6, 2026-11-18)
                else if (t == "ev") c.Evict = true;   // E40 C2
                else if (t == "ceil") { c.CeilStep = true; c.CeilTaps = true; }   // PUFF ceiling render
                else if (t == "cstep") c.CeilStep = true;   // PUFF ceiling render: the march's group alone
                else if (t == "clight") c.CeilTaps = true;   // PUFF ceiling render: the light's group alone
                else if (t == "c256") c.C256 = true;
                else if (t.StartsWith("ng") && t.Length > 2) c.Grid = Number(t, 2);
                else if (t == "cmin6") c.Cmin6 = true;
                else if (t == "cng") c.Cng = true;
                else if (t == "ceo") c.Ceo = true;
                else if (t == "cel") c.Cel = true;
                else if (t == "cls") c.Cls = true;
                else if (t == "ctaps") c.Ctaps = true;
                else if (t.StartsWith("cms") && t.Length > 3) c.MinStepOverride = Number(t, 3);
                else if (t.StartsWith("csm") && t.Length > 3) c.SunMarchKm = Number(t, 3);
                else if (t.StartsWith("ctap") && t.Length > 4) { c.Ctaps = true; c.SunTaps = Number(t, 4); }
                else if (t == "cnsg") c.Cnsg = true;
                else if (t == "cst1") c.Cst1 = true;
                else if (t.StartsWith("cg") && t.Length > 2) { c.StepGrowth = true; c.GrowthE6 = Number(t, 2); }   // E40 C3
                else if (t.StartsWith("eo")) c.EarlyOut = Number(t, 2);   // E40 C3
                else if (t.StartsWith("mn")) c.Moon = Number(t, 2);   // E40 C2
                else if (t.StartsWith("mc")) c.MoonCut = Number(t, 2);   // E40 C2
                else if (t.StartsWith("lp")) c.Leap100 = Number(t, 2) / 100f;   // E40 C2
                else if (t.StartsWith("wt")) c.WarpTable = Number(t, 2);   // E40 C4
                else if (t == "og") c.OldGrid = true;   // E40 F2: the pattern's pixel grid of the pass before F2 (LayerFrame.DitherOwnGrid false)
                else if (t == "chm") c.CatmullHistory = true;   // CLOUDR3 P7: the history read with the Catmull-Rom filter
                else if (t == "ctl") { }   // a control: the same case under another name (the run-to-run noise of the history and the timing)
                else if (t.StartsWith("spm")) c.SpacingMin = Number(t, 3);
                else if (t.StartsWith("sp")) c.Spacing = Number(t, 2) / 10f;
                else if (t.StartsWith("cr")) c.ConePattern = Number(t, 2);   // E40 F2: the light cone's rotation pattern (LayerFrame.ConePattern)
                else if (t == "bexact") c.BlockExact = true;
                else if (t == "bacc") c.BlockAcc = true;
                else if (t == "lrm") c.ReuseMove = true;
                else if (t.StartsWith("farb")) c.FarBlock = Number(t, 4);
                else if (t.StartsWith("far")) c.FarKm = Number(t, 3);
                else if (t.StartsWith("blk")) c.Block = Number(t, 3);
                else if (t.StartsWith("clip")) c.ClipSigma = Number(t, 4) / 10f;
                else if (t.StartsWith("acc")) c.Accumulate = Number(t, 3) / 100f;
                else if (t == "lvinf") { c.LvOn = true; c.LvKm = -1f; }   // LV1: the light volume on, hand-over at infinity
                else if (t.StartsWith("lvn")) c.LvSlices = Number(t, 3);
                else if (t.StartsWith("lvt")) c.LvTaps = Number(t, 3);
                else if (t.StartsWith("lvw")) c.LvWarp = Number(t, 3);
                else if (t.StartsWith("lvc")) c.LvCascades = Number(t, 3);
                else if (t.StartsWith("lvsp")) c.LvSpread = Number(t, 4);
                else if (t.StartsWith("lvsr")) c.LvSpreadRadius = Number(t, 4);
                else if (t.StartsWith("lvsd")) c.LvSpreadDepth = Number(t, 4);
                else if (t.StartsWith("lvsm")) c.LvSpreadMax = Number(t, 4);
                else if (t.StartsWith("lvs")) c.LvFarScale = Number(t, 3);
                else if (t.StartsWith("lvm")) c.LvFill = Number(t, 3);
                else if (t.StartsWith("lvh")) c.LvStoreK = Number(t, 3);
                else if (t.StartsWith("lvy")) c.LvCapRead = Number(t, 3);
                else if (t.StartsWith("lvp")) c.LvReadPast = Number(t, 3);
                else if (t.StartsWith("lvo")) c.LvWarpRate = Number(t, 3);
                else if (t.StartsWith("lvu")) c.LvWarpLimit = Number(t, 3);
                else if (t.StartsWith("lvq")) c.LvNearScale = Number(t, 3);
                else if (t.StartsWith("lvk")) c.LvPropSteps = Number(t, 3);
                else if (t.StartsWith("lvx")) c.LvPropCap = Number(t, 3);
                else if (t.StartsWith("lvl")) c.LvPropLimit = Number(t, 3);
                else if (t.StartsWith("lvz")) c.LvGridZ = Number(t, 3);
                else if (t.StartsWith("lvv")) c.LvVoxel = Number(t, 3);
                else if (t.StartsWith("lvf")) c.LvFillTaps = Number(t, 3);
                else if (t.StartsWith("lvr")) c.LvFillRatio = Number(t, 3);
                else if (t.StartsWith("lvb")) c.LvBand = Number(t, 3);
                else if (t.StartsWith("lv")) { c.LvOn = true; c.LvKm = Number(t, 2); }
                else if (t.StartsWith("lcache")) c.CacheNear = Number(t, 6);
                else if (t.StartsWith("lcfill")) c.CacheFill = Number(t, 6);
                else if (t.StartsWith("lcbox")) c.CacheKm = Number(t, 5);
                else if (t.StartsWith("lcgrid")) c.CacheGrid = Number(t, 6);
                else if (t.StartsWith("lc")) c.Cutoff = Number(t, 2) / 1000f;
                else if (t.StartsWith("lq")) c.Quality = Number(t, 2);
                else if (t.StartsWith("ls")) c.Stride = Number(t, 2);
                else if (t.StartsWith("ds")) c.Dither = Number(t, 2);
                else if (t.StartsWith("sun")) c.Sun = Number(t, 3);
                else if (t.StartsWith("leap")) c.Leap = Number(t, 4);
                else if (t.StartsWith("lm")) c.Mask = Number(t, 2);
                else if (t.StartsWith("dbg")) c.Debug = Number(t, 3);
                else if (t.StartsWith("r") && t.Length > 1) c.Resolution = Number(t, 1) / 100f;
                else throw new ArgumentException("unknown modifier '" + t + "' in the case " + name);
            }
            return c;
        }

        // A light volume lever of a case: set to value * scale, or back to its default when the case does not name it (-1).
        private static void Lever(string key, int value, float scale)
        {
            if (value >= 0) Tuning.SetOverride(key, value * scale);
            else Tuning.ClearOverride(key);
        }

        private static readonly LayerFrame FrameDefaults = new LayerFrame();
        private static readonly CloudLightSettings LightDefaults = new CloudLightSettings();

        // Every field a case can name is set on every call, so that a case never keeps what the one before it set on the same frame.
        internal static void ApplyCase(LayerFrame f, Case c)
        {
            Tuning.SetOverride(CloudLookTuning.TuneLightVolume, c.LvOn ? 1f : 0f);
            Tuning.SetOverride(CloudLookTuning.TuneLvHandOver, c.LvKm);
            if (c.LvSlices >= 0) Tuning.SetOverride(CloudLookTuning.TuneLvSlices, c.LvSlices); else Tuning.ClearOverride(CloudLookTuning.TuneLvSlices);
            if (c.LvTaps >= 0) Tuning.SetOverride(CloudLookTuning.TuneLvFarTaps, c.LvTaps); else Tuning.ClearOverride(CloudLookTuning.TuneLvFarTaps);
            if (c.LvWarp >= 0) Tuning.SetOverride(CloudLookTuning.TuneLvWarp, c.LvWarp); else Tuning.ClearOverride(CloudLookTuning.TuneLvWarp);
            Lever(CloudLookTuning.TuneLvCascades, c.LvCascades, 1f);
            Lever(CloudLookTuning.TuneLvFarScale, c.LvFarScale, 1f);
            Lever(CloudLookTuning.TuneLvSpread, c.LvSpread, 1f);
            Lever(CloudLookTuning.TuneLvSpreadRadius, c.LvSpreadRadius, 1f);
            Lever(CloudLookTuning.TuneLvSpreadDepth, c.LvSpreadDepth, 0.1f);
            Lever(CloudLookTuning.TuneLvSpreadMax, c.LvSpreadMax, 0.01f);
            Lever(CloudLookTuning.TuneLvFill, c.LvFill, 1f);
            Lever(CloudLookTuning.TuneLvNearScale, c.LvNearScale, 0.01f);
            Lever(CloudLookTuning.TuneLvPropSteps, c.LvPropSteps, 1f);
            Lever(CloudLookTuning.TuneLvPropCap, c.LvPropCap, 1f);
            Lever(CloudLookTuning.TuneLvPropLimit, c.LvPropLimit, 1f);
            Lever(CloudLookTuning.TuneLvStoreK, c.LvStoreK, 1f);
            Lever(CloudLookTuning.TuneLvCapRead, c.LvCapRead, 1f);
            Lever(CloudLookTuning.TuneLvReadPast, c.LvReadPast, 1f);
            Lever(CloudLookTuning.TuneLvWarpLimit, c.LvWarpLimit, 1f);
            LightVolume.TimeWarpRate = c.LvWarpRate > 0 ? c.LvWarpRate : 1f;
            Lever(CloudLookTuning.TuneLvGridZ, c.LvGridZ, 1f);
            Lever(CloudLookTuning.TuneLvVoxel, c.LvVoxel, 1f);
            Lever(CloudLookTuning.TuneLvFillTaps, c.LvFillTaps, 1f);
            Lever(CloudLookTuning.TuneLvFillRatio, c.LvFillRatio, 0.01f);
            Lever(CloudLookTuning.TuneLvBand, c.LvBand, 0.01f);
            ApplyMarch(f, c.Base);
            f.LightStride = c.Stride >= 0 ? c.Stride : FrameDefaults.LightStride;
            f.LightReuseMove = c.ReuseMove;
            f.LightCutoff = c.Cutoff >= 0f ? c.Cutoff : FrameDefaults.LightCutoff;
            f.DitherSequence = c.Dither >= 0 ? c.Dither : FrameDefaults.DitherSequence;
            f.DitherOwnGrid = !c.OldGrid;
            f.ConePattern = c.ConePattern >= 0 ? c.ConePattern : FrameDefaults.ConePattern;
            f.FixedConeRotation = c.ConePattern == 1;   // cr1: the probe's fixed rotation (no per-pixel pattern of the cone at all)
            if (c.Debug >= 0) f.CostDebug = c.Debug;
            if (c.Leap > 0) f.EmptyLeap = c.Leap;
            f.SunSteps = c.Sun > 0 ? c.Sun : 3;
            f.LayerMask = c.Mask;
            f.Cull.SkipEmpty = c.Skip;
            f.Cull.SkipClosed = c.SkipClosed;
            f.Cull.StepGrowth = c.StepGrowth;
            f.Cull.StepGrowthPerM = c.GrowthE6 > 0 ? c.GrowthE6 * 1e-6f : new CullSettings().StepGrowthPerM;
            if (c.EarlyOut > 0) f.EarlyOut = c.EarlyOut / 1000f;   // the march's own value (GrainTable.Apply) otherwise
            f.Cull.TapLod = c.TapLod;
            f.Far.ShapeMean = c.FarMean;
            // dlod: the setting cloudDetailLod, which in the game also switches the far shape mean on (OwnCloudOptions).
            if (c.DetailLod)
            {
                f.Cull.DetailLod = true;
                f.Far.ShapeMean = true;
            }
            f.EmptyReach = !c.NoReach;
            f.SpanSpacing = c.Spacing;
            f.SpanSpacingMin = c.SpacingMin;
            f.LeapScales = c.Leap100;
            f.WarpTable = c.WarpTable;
            for (int i = 0; i < f.Light.Lights.Length; i++) f.Light.Lights[i] = new CloudLight { Towards = Vector3.up, Colour = Color.black };
            // The product's light.moonMinShare (OwnCloudOptions.MoonTooFaint, which the player does not link): a moon below that share of the sun is left out.
            if (c.Moon > 0 && !(c.MoonCut > 0 && c.Moon / 10000f < c.MoonCut / 1000f))
            {
                // A moon high in the sky, a little off the sun's direction; its colour is the sun's grey scaled (the cost does not depend on the colour, only on its being above zero).
                Vector3 towards = (Quaternion.AngleAxis(40f, Vector3.up) * f.TowardsSun).normalized;
                f.Light.Lights[0] = new CloudLight { Towards = towards, Colour = new Color(1f, 1f, 1f, 1f) * (f.SunColour.grayscale * c.Moon / 10000f) };
            }
            CloudLightSettings q = c.Quality >= 0 ? CloudLightSettings.ForQuality(c.Quality) : LightDefaults;
            f.Light.SunStepsOverride = q.SunStepsOverride;
            f.Light.MoonSteps = q.MoonSteps;
            f.Light.UpSteps = q.UpSteps;
            f.Light.Octaves = q.Octaves;
            f.SunMarchM = FrameDefaults.SunMarchM;
            if (c.CeilStep)
            {
                f.MaxSteps = 256;
                f.MinStepM = 6f;
                f.StepGrowth = 0f;
                f.EarlyOut = 0.001f;
                f.TransmittanceGrowth = 0f;
                f.EmptyLeap = 1f;
            }
            if (c.C256) f.MaxSteps = 256;
            if (c.Grid > 0) f.MaxSteps = c.Grid;
            if (c.Cmin6) f.MinStepM = 6f;
            if (c.Cng)
            {
                f.StepGrowth = 0f;
                f.TransmittanceGrowth = 0f;
            }
            if (c.Ceo) f.EarlyOut = 0.001f;
            if (c.Cel) f.EmptyLeap = 1f;
            if (c.Cls)
            {
                f.LightStride = 1;
                f.LightCutoff = 0f;
            }
            if (c.Cnsg) f.StepGrowth = 0f;
            if (c.Cst1) f.LightStride = 1;
            if (c.Ctaps)
            {
                f.SunMarchM = c.SunMarchKm > 0 ? c.SunMarchKm * 1000f : 12000f;
                f.Light.SunStepsOverride = c.SunTaps > 0 ? c.SunTaps : 16;
            }
            if (c.MinStepOverride > 0) f.MinStepM = c.MinStepOverride;
            if (c.CacheNear > 0)
            {
                if (cache == null) cache = new LightCache();
                cache.Enabled = true;
                cache.NearTaps = c.CacheNear;
                cache.SizeM = c.CacheKm > 0 ? c.CacheKm * 1000f : new LightCache().SizeM;
                cache.GridN = c.CacheGrid > 0 ? c.CacheGrid : new LightCache().GridN;
                cache.GridNz = Mathf.Max(cache.GridN / 4, 8);
                f.Light.CacheFillSteps = c.CacheFill > 0 ? c.CacheFill : LightDefaults.CacheFillSteps;
                f.Light.Cache = cache;
            }
            else
            {
                if (cache != null) cache.Enabled = false;
                f.Light.Cache = null;
            }
            if (c.CeilTaps)
            {
                f.LightStride = 1;
                f.LightCutoff = 0f;
                f.SunMarchM = 12000f;
                f.Light.SunStepsOverride = 16;
            }
        }

        // The reconstruction of a case in a mode of GrainTable (plain: the half-size march alone; acc20: with the history). A block level has its own history and ignores the mode.
        internal static ReconSettings Settings(Case c, string mode)
        {
            if (c.Block > 0)
            {
                var b = new ReconSettings { Mode = ReconMode.Block, Block = c.Block, CatmullHistory = c.CatmullHistory };
                // bexact (the owner's perf program point 5, 2026-11-19): every pixel marched every block^2 frames and replaced whole; the others the reprojected history alone (no
                // blend with the new samples, no clip to them).
                if (c.BlockExact)
                {
                    b.FreshWeight = 1f;
                    b.StaleWeight = 0f;
                    b.ClipSigma = 1000f;
                }
                // bacc: the same rotation with the accepted look's own smoothing, the fresh sample blended into the pixel's history by the case's accumulation (acc20: 0.2), the
                // others the clipped history alone.
                if (c.BlockAcc)
                {
                    b.FreshWeight = c.Accumulate;
                    b.StaleWeight = 0f;
                    if (c.ClipSigma >= 0f) b.ClipSigma = c.ClipSigma;
                }
                return b;
            }
            ReconSettings s = GrainTable.ModeByName(mode).Settings();
            s.Resolution = c.Resolution;
            s.Jitter = c.Jitter;
            s.CatmullHistory = c.CatmullHistory;
            if (s.Accumulate > 0f) s.Accumulate = c.Accumulate;
            if (c.ClipSigma >= 0f) s.ClipSigma = c.ClipSigma;
            s.FarRotateM = c.FarKm * 1000f;
            if (c.FarBlock > 0) s.FarRotateBlock = c.FarBlock;
            return s;
        }

        // The views of a run: -views a,b (the job id of a view is gn-<view>-prod-f0); default the two of the grain series.
        internal static string[] Views
        {
            get
            {
                string arg = WeatherPass.Argument("-views");
                return string.IsNullOrEmpty(arg) ? new[] { "ground", "above" } : arg.Split(',');
            }
        }

        // -motion all runs the moving scenarios and the cut for every case, not only for the first march of the list.
        internal static bool MotionForAll { get { return WeatherPass.Argument("-motion") == "all"; } }
    }

    [WeatherScene("soft-temporal")]
    public sealed class SoftTemporalScene : WeatherSceneBase
    {
        private const int Frames = 40;
        // The target's height: 960 as before, or -height N (PUFF: pictures at 1920 x 1080 and 2560 x 1440).
        private static int H
        {
            get
            {
                int height;
                return int.TryParse(WeatherPass.Argument("-height"), out height) && height >= 64 ? height : 960;
            }
        }
        // The target's width: 1708 as before, or -width N (E40 PP64: the game's frames are 1707 wide and its march target 563, so the parity series asks for 1707).
        private static int W
        {
            get
            {
                int width;
                return int.TryParse(WeatherPass.Argument("-width"), out width) && width >= 64 ? width : 1708;
            }
        }

        private sealed class Scenario
        {
            public string Name;
            public float YawPerFrame, SidePerFrame;
            public int ArriveAt;
            public int[] Dump;
            // LV1: the camera flies forward along its heading (m per frame), the sun rises (degrees per frame, about the horizontal axis across its azimuth); no converged reference.
            public float ForwardPerFrame, SunPerFrame;
            public bool NoReference;
        }

        // GrainTemporalScene's scenarios without the slow turn (the fast one is the harder case of the same motion).
        private static readonly Scenario[] Scenarios =
        {
            new Scenario { Name = "static", Dump = SoftTable.StaticDump() },
            new Scenario { Name = "fly", SidePerFrame = 5f, Dump = new[] { 37, 38 } },
            new Scenario { Name = "turnfast", YawPerFrame = 3f, Dump = new[] { 37, 38 } },
            new Scenario { Name = "turnin", YawPerFrame = 3f, ArriveAt = 38, Dump = new[] { 37, 38 } },
            new Scenario { Name = "flyfast", SidePerFrame = 40f, Dump = new[] { 37, 38 } },
            new Scenario { Name = "cut", Dump = new[] { 0, 2, 5, 10 } },
            // LV1 (only with -scenarios): 100 m a frame forward (4 km over the run: a dozen of the near cascade's voxels, the clouds through the hand-over band), and the sun rising
            // 1 degree a frame (a time warp of about 15000 at Kerbin's day; 33 degrees over the run).
            new Scenario { Name = "lvmove", ForwardPerFrame = 100f, NoReference = true, Dump = new[] { 6, 10, 14, 18, 22, 26, 30, 34, 38 } },
            new Scenario { Name = "lvsun", SunPerFrame = 1f, NoReference = true, Dump = new[] { 6, 10, 14, 18, 22, 26, 30, 34, 38 } },
            // LV2: the same motions, every frame of 16 (frames 20 to 35).
            new Scenario { Name = "lvmove16", ForwardPerFrame = 100f, NoReference = true, Dump = Range(20, 16) },
            new Scenario { Name = "lvsun16", SunPerFrame = 1f, NoReference = true, Dump = Range(20, 16) },
        };

        private static int[] Range(int first, int count)
        {
            var frames = new int[count];
            for (int k = 0; k < count; k++) frames[k] = first + k;
            return frames;
        }

        // -scenarios a,b: only these (the LV1 ones run only when named); without it every scenario but the LV1 ones (and -dump last: the still one).
        private static bool Selected(Scenario sc)
        {
            string arg = WeatherPass.Argument("-scenarios");
            if (!string.IsNullOrEmpty(arg)) return Array.IndexOf(arg.Split(','), sc.Name) >= 0;
            if (sc.NoReference) return false;
            return !SoftTable.DumpLast || sc.Name == "static";
        }

        private Texture2D readback;

        private void Dump(RenderTexture rt, string name)
        {
            if (readback == null || readback.width != rt.width || readback.height != rt.height)
                readback = new Texture2D(rt.width, rt.height, TextureFormat.RGBAHalf, false, true);
            RenderTexture.active = rt;
            readback.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0, false);
            RenderTexture.active = null;
            byte[] raw = readback.GetRawTextureData();
            string header = "{'descr': '<f2', 'fortran_order': False, 'shape': (" + rt.height + ", " + rt.width + ", 4), }";
            int total = 10 + header.Length + 1;
            header = header + new string(' ', (64 - total % 64) % 64) + "\n";
            using (var stream = new FileStream(Path.Combine(Output, name + ".npy"), FileMode.Create, FileAccess.Write))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(new byte[] { 0x93, (byte)'N', (byte)'U', (byte)'M', (byte)'P', (byte)'Y', 1, 0 });
                writer.Write((ushort)header.Length);
                writer.Write(Encoding.ASCII.GetBytes(header));
                writer.Write(raw);
            }
        }

        private void DumpVolume(LightVolume lv, string name)
        {
            RenderTexture v = lv.Volume;
            if (v == null) return;
            AsyncGPUReadbackRequest r = AsyncGPUReadback.Request(v, 0, 0, v.width, 0, v.height, 0, v.volumeDepth, TextureFormat.RFloat);
            r.WaitForCompletion();
            if (r.hasError) { Sink.Info("lvdump_error", 1); return; }
            string header = "{'descr': '<f4', 'fortran_order': False, 'shape': (" + r.layerCount + ", " + v.height + ", " + v.width + "), }";
            int total = 10 + header.Length + 1;
            header = header + new string(' ', (64 - total % 64) % 64) + "\n";
            using (var stream = new FileStream(Path.Combine(Output, name + ".npy"), FileMode.Create, FileAccess.Write))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(new byte[] { 0x93, (byte)'N', (byte)'U', (byte)'M', (byte)'P', (byte)'Y', 1, 0 });
                writer.Write((ushort)header.Length);
                writer.Write(Encoding.ASCII.GetBytes(header));
                for (int layer = 0; layer < r.layerCount; layer++)
                    foreach (float f in r.GetData<float>(layer)) writer.Write(f);
            }
            File.WriteAllText(Path.Combine(Output, name + ".json"), lv.Describe());
        }

        private void Render(TemporalRig rig, LayerReconstructor recon, ReconSettings s, int number)
        {
            var cb = new CommandBuffer { name = "soft temporal" };
            recon.Record(cb, rig.Renderer, rig.Frame, Cam, s, number, null, null, null);
            Graphics.ExecuteCommandBuffer(cb);
            cb.Release();
        }

        private static float Yaw(Scenario sc, int i)
        {
            // ArriveAt: the turn ends its frames looking along the still view (the owner compares a turn with the still and the flown pictures of the same view, 2026-11-19).
            if (sc.ArriveAt > 0) return sc.YawPerFrame * (i - sc.ArriveAt);
            return sc.YawPerFrame * Mathf.Max(i - 6, 0);
        }

        private static float Side(Scenario sc, int i)
        {
            return sc.SidePerFrame * Mathf.Max(i - 6, 0);
        }

        // LV1: the forward flight along the view's horizontal heading and the rising sun of frame i (both start after frame 6, as the other motions).
        private void Travel(Scenario sc, int i, LayersJob job, LayerFrame f, Vector3 sun0)
        {
            float k = Mathf.Max(i - 6, 0);
            Vector3 up = LayersSetup.V(job.position).normalized;
            if (sc.ForwardPerFrame != 0f)
            {
                Vector3 heading = Vector3.ProjectOnPlane(LayersSetup.V(job.forward), up).normalized;
                Cam.transform.position += heading * (sc.ForwardPerFrame * k);
            }
            Vector3 flat = Vector3.ProjectOnPlane(sun0, up);
            Vector3 axis = flat.sqrMagnitude > 1e-8f ? Vector3.Cross(up, flat.normalized) : Vector3.zero;
            f.TowardsSun = sc.SunPerFrame != 0f && axis != Vector3.zero ? (Quaternion.AngleAxis(-sc.SunPerFrame * k, axis) * sun0).normalized : sun0;
        }

        public override IEnumerator Run()
        {
            string input = WeatherPass.Argument("-in");
            var rig = new TemporalRig();
            bool ready = input != null && File.Exists(Path.Combine(input, "jobs.json")) && rig.Init();
            Sink.Flag("soft_temporal_available", ready);
            if (!ready) yield break;
            LayersJobFile file = JsonUtility.FromJson<LayersJobFile>(File.ReadAllText(Path.Combine(input, "jobs.json")));
            var rt = new RenderTexture(W, H, 24, RenderTextureFormat.DefaultHDR);
            rt.Create();
            Cam.targetTexture = rt;
            var refRecon = new LayerReconstructor();
            refRecon.Init(Shader.Find(LayerReconstructor.ShaderName));
            var full = new ReconSettings { Mode = ReconMode.Full };
            int dumped = 0;
            foreach (string view in SoftTable.Views)
            {
                LayersJob job = GrainTable.Find(file, "gn-" + view + "-prod-f0");
                if (job == null) { Sink.Flag("soft_temporal_job_" + view, false); continue; }
                SoftTable.Original original = SoftTable.Original.Of(job.layers[0]);
                rig.Load(job, input);
                double detailMean = SoftTable.DetailMean(rig);
                foreach (SoftTable.Medium medium in SoftTable.Selected())
                {
                    SoftTable.Load(rig, job, input, medium, original, detailMean);
                    if (SoftTable.ProductLightOn) SoftTable.ProductLight(rig.Frame);
                    Vector3 sun0 = rig.Frame.TowardsSun;
                    foreach (Scenario sc in Scenarios)
                    {
                        if (!Selected(sc)) continue;
                        // The converged reference of every pose that is written, of THIS medium.
                        // A case of an earlier scenario may have left taps, a cull or a cost mask on the frame: the references run on the frame's own defaults.
                        SoftTable.ApplyCase(rig.Frame, SoftTable.Parse("seq1"));
                        GrainTable.ApplyConverged(rig.Frame);
                        foreach (int i in sc.Dump)
                        {
                            if (sc.NoReference) break;
                            if ((sc.Name == "static" || sc.Name == "cut") && i != sc.Dump[0]) continue;
                            GrainTable.Pose(Cam, job, Yaw(sc, i), Side(sc, i));
                            refRecon.Reset();
                            Render(rig, refRecon, full, 0);
                            Dump(refRecon.Output, "gs-" + view + "-" + medium.Name + "-" + sc.Name + "-ref-f" + i);
                            yield return null;
                        }
                        foreach (string march in SoftTable.Marches)
                            foreach (string mode in SoftTable.Modes)
                            {
                                // The fewer grid points are measured with the history and a still camera only.
                                if (march != "seq1" && (mode == "plain" || (sc.Name != "static" && !SoftTable.MotionForAll))) continue;
                                SoftTable.Case marchCase = SoftTable.Parse(march);
                                SoftTable.ApplyCase(rig.Frame, marchCase);
                                ReconSettings settings = SoftTable.Settings(marchCase, mode);
                                rig.Recon.Reset();
                                for (int i = 0; i < Frames; i++)
                                {
                                    GrainTable.Pose(Cam, job, Yaw(sc, i), Side(sc, i));
                                    Travel(sc, i, job, rig.Frame, sun0);
                                    Render(rig, rig.Recon, settings, 100 + i);
                                    if (Array.IndexOf(sc.Dump, i) >= 0)
                                    {
                                        Dump(rig.Recon.Output, "gs-" + view + "-" + medium.Name + "-" + sc.Name + "-" + march + "-" + mode + "-f" + i);
                                        // E40 PP: the uniforms this frame ran with, for the comparison with the game's pass.json (tools/weather_metrics/e40pp.py).
                                        if (sc.Name == "static")
                                            File.WriteAllText(Path.Combine(Output, "gs-" + view + "-" + medium.Name + "-" + sc.Name + "-" + march + "-" + mode + "-f" + i + "-uniforms.json"),
                                                              LayerPassReadout.Uniforms(rig.Renderer.Material));
                                        dumped++;
                                        SoftTable.ReportCache(Sink, "lightcache." + view + "." + march);
                                        // LV1: the light volume of a still view as it stands (-lvdump 1): its slices (depth, voxels, voxels) as .npy and its layout as .json.
                                        if (sc.Name == "static" && marchCase.LvOn && WeatherPass.Argument("-lvdump") == "1" && rig.Recon.CurrentLightVolume != null)
                                            DumpVolume(rig.Recon.CurrentLightVolume, "lv-" + view + "-" + march + "-f" + i);
                                    }
                                    if (i % 8 == 7) yield return null;
                                }
                                rig.Frame.TowardsSun = sun0;
                            }
                    }
                }
                original.Restore(job.layers[0]);
            }
            refRecon.Dispose();
            rig.Dispose();
            Cam.targetTexture = Target;
            rt.Release();
            Sink.Info("soft_temporal_frames", dumped);
            Sink.Flag("soft_temporal_completed", true);
        }
    }

    [WeatherScene("soft-cost")]
    public sealed class SoftCostScene : WeatherSceneBase
    {
        public override IEnumerator Run()
        {
            string input = WeatherPass.Argument("-in");
            var rig = new TemporalRig();
            bool ready = input != null && File.Exists(Path.Combine(input, "jobs.json")) && WeatherGpuTimer.Start() && rig.Init();
            Sink.Flag("soft_cost_timers_available", ready);
            if (!ready) yield break;
            // The light cache's or the light volume's fill as its own segment (CurtainStart's mark; the march segment starts after it).
            rig.Recon.MarkCacheFilled = c => WeatherGpuTimer.Mark(c, WeatherGpuTimer.CurtainStart);
            LayersJobFile file = JsonUtility.FromJson<LayersJobFile>(File.ReadAllText(Path.Combine(input, "jobs.json")));
            var dummy = new RenderTexture(4, 4, 0, RenderTextureFormat.RFloat);
            dummy.Create();
            RenderTexture.active = dummy;
            GL.Clear(false, true, Color.black);
            RenderTexture.active = null;
            var depthId = new RenderTargetIdentifier(dummy);
            var cb = new CommandBuffer { name = "soft cost" };
            Cam.AddCommandBuffer(CameraEvent.AfterSkybox, cb);
            var segments = new float[8];
            var clock = System.Diagnostics.Stopwatch.StartNew();
            Action<CommandBuffer> mStart = c => WeatherGpuTimer.Mark(c, WeatherGpuTimer.SlabStart),
                mMarched = c => WeatherGpuTimer.Mark(c, WeatherGpuTimer.SlabMarched),
                mResolved = c => WeatherGpuTimer.Mark(c, WeatherGpuTimer.SlabComposited),
                mEnd = c => WeatherGpuTimer.Mark(c, WeatherGpuTimer.CurtainEnd);
            int costW, costH;
            // The cost target's size: 1707 x 960 as before, or -width N -height N (PUFF: the pass cost at 1920 x 1080 and 2560 x 1440).
            if (!int.TryParse(WeatherPass.Argument("-width"), out costW) || costW < 64) costW = 1707;
            if (!int.TryParse(WeatherPass.Argument("-height"), out costH) || costH < 64) costH = 960;
            var rt = new RenderTexture(costW, costH, 24, RenderTextureFormat.DefaultHDR) { name = "soft cost target" };
            rt.Create();
            Cam.targetTexture = rt;
            // E40 C2 (case ev): two 2048 x 2048 half-float targets, copied into each other before the timed pass: 64 MB through the caches (the L2 of the card is a few MB).
            var evictA = new RenderTexture(2048, 2048, 0, RenderTextureFormat.ARGBHalf);
            var evictB = new RenderTexture(2048, 2048, 0, RenderTextureFormat.ARGBHalf);
            evictA.Create();
            evictB.Create();
            List<SoftTable.Medium> media = SoftTable.Selected();
            // Two rounds in opposite order, so that a drift of the machine over the run falls on every case alike; the row is the median of both. -rounds N (at least 2) runs N
            // rounds, alternating the order, and also reports each round's march and fill medians (softcost.marchr<k>.<label>, softcost.fillr<k>.<label>).
            int rounds;
            if (!int.TryParse(WeatherPass.Argument("-rounds"), out rounds) || rounds < 2) rounds = 2;
            var samples = new Dictionary<string, List<double>[]>();
            var perRound = new Dictionary<string, List<double>>();
            var perRoundFill = new Dictionary<string, List<double>>();
            for (int round = 0; round < rounds; round++)
            {
                bool forward = round % 2 == 0;
                string[] views = SoftTable.Views;
                for (int vi = 0; vi < views.Length; vi++)
                {
                    string view = views[forward ? vi : views.Length - 1 - vi];
                    LayersJob job = GrainTable.Find(file, "gn-" + view + "-prod-f0");
                    if (job == null) { Sink.Flag("soft_cost_job_" + view, false); continue; }
                    SoftTable.Original original = SoftTable.Original.Of(job.layers[0]);
                    rig.Load(job, input);
                    double detailMean = SoftTable.DetailMean(rig);
                    for (int k = 0; k < media.Count; k++)
                    {
                        SoftTable.Medium medium = media[forward ? k : media.Count - 1 - k];
                        SoftTable.Load(rig, job, input, medium, original, detailMean);
                        rig.Frame.UseSceneDepth = true;
                        GrainTable.Pose(Cam, job, 0f, 0f);
                        string[] marches = SoftTable.Marches;
                        for (int mi = 0; mi < marches.Length; mi++)
                        {
                            string march = marches[forward ? mi : marches.Length - 1 - mi];
                            if (clock.Elapsed.TotalMinutes > 18.0 + 6.0 * (rounds - 2)) { Sink.Flag("soft_cost_completed", false); yield break; }
                            SoftTable.Case marchCase = SoftTable.Parse(march);
                            SoftTable.ApplyCase(rig.Frame, marchCase);
                            ReconSettings settings = SoftTable.Settings(marchCase, "acc20");
                            rig.Recon.Reset();
                            string label = view + "." + medium.Name + "." + march;
                            if (!samples.ContainsKey(label)) samples[label] = new[] { new List<double>(), new List<double>(), new List<double>(), new List<double>() };
                            List<double>[] s = samples[label];
                            int guard = 0, n = 0, taken = 0, first = s[0].Count, firstFill = s[3].Count;
                            int laysBefore = rig.Recon.CurrentLightVolume != null ? rig.Recon.CurrentLightVolume.Lays : 0;
                            while (taken < 40 && guard++ < 1200)
                            {
                                cb.Clear();
                                cb.SetGlobalTexture(Shader.PropertyToID("_SceneDepth"), depthId);
                                if (marchCase.Evict) cb.Blit(evictA, evictB);
                                rig.Recon.Record(cb, rig.Renderer, rig.Frame, Cam, settings, FrameIndex, mStart, mMarched, mResolved);
                                rig.Recon.Composite(cb, rig.Renderer, BuiltinRenderTextureType.CameraTarget, mEnd);
                                yield return Frames(1);
                                if (n++ < 20) continue;
                                float total;
                                int number;
                                if (!WeatherGpuTimer.Read(segments, out total, out number)) continue;
                                float m = segments[WeatherGpuTimer.SlabMarched - WeatherGpuTimer.SlabStart], r = segments[WeatherGpuTimer.SlabComposited - WeatherGpuTimer.SlabStart],
                                    e = segments[WeatherGpuTimer.CurtainEnd - WeatherGpuTimer.SlabStart];
                                if (m >= 0f && r >= 0f && e >= 0f)
                                {
                                    s[0].Add(m); s[1].Add(r); s[2].Add(e); taken++;
                                    // With the light cache or the light volume the march segment starts after the fill; the fill is its own segment (CurtainStart's mark).
                                    float fill = segments[WeatherGpuTimer.CurtainStart - WeatherGpuTimer.SlabStart];
                                    if (fill >= 0f) s[3].Add(fill);
                                }
                            }
                            if (s[0].Count > first)
                            {
                                if (!perRound.ContainsKey(label)) perRound[label] = new List<double>();
                                perRound[label].Add(Median(s[0].GetRange(first, s[0].Count - first)));
                            }
                            if (s[3].Count > firstFill)
                            {
                                if (!perRoundFill.ContainsKey(label)) perRoundFill[label] = new List<double>();
                                perRoundFill[label].Add(Median(s[3].GetRange(firstFill, s[3].Count - firstFill)));
                            }
                            // LV1: the light volume's lays in this case (one at the first frame after the Reset; more would be a relay the case did not ask for).
                            if (marchCase.LvOn && rig.Recon.CurrentLightVolume != null && round == 0)
                                Sink.Info("softcost.lvlays." + label, rig.Recon.CurrentLightVolume.Lays - laysBefore);
                            if (marchCase.Skip && round == 0)
                            {
                                // What the tile pass dropped: the share of the 16 x 16 tiles that march no layer at all, and of the (tile, layer) pairs that are not marched.
                                double emptyTiles, droppedPairs, byMaps, byTerrain, byShell, byClosed;
                                CullKit.TileStats(rig.Renderer, job.layers.Length, out emptyTiles, out droppedPairs, out byMaps, out byTerrain, out byShell, out byClosed);
                                Sink.Info("softcost.tilesempty." + label, emptyTiles);
                                Sink.Info("softcost.tilesdropped." + label, droppedPairs);
                            }
                        }
                    }
                    original.Restore(job.layers[0]);
                }
            }
            foreach (KeyValuePair<string, List<double>[]> kv in samples)
            {
                double tm = Median(kv.Value[0]), tr = Median(kv.Value[1]), tc = Median(kv.Value[2]);
                Sink.Info("softcost.march." + kv.Key, tm);
                Sink.Info("softcost.resolve." + kv.Key, tr);
                Sink.Info("softcost.composite." + kv.Key, tc);
                Sink.Info("softcost.total." + kv.Key, tm + tr + tc);
                Sink.Info("softcost.n." + kv.Key, kv.Value[0].Count);
                if (kv.Value[3].Count > 0) Sink.Info("softcost.fill." + kv.Key, Median(kv.Value[3]));
            }
            if (rounds > 2)
            {
                foreach (KeyValuePair<string, List<double>> kv in perRound)
                    for (int k = 0; k < kv.Value.Count; k++) Sink.Info("softcost.marchr" + k + "." + kv.Key, kv.Value[k]);
                foreach (KeyValuePair<string, List<double>> kv in perRoundFill)
                    for (int k = 0; k < kv.Value.Count; k++) Sink.Info("softcost.fillr" + k + "." + kv.Key, kv.Value[k]);
            }
            Cam.targetTexture = Target;
            rt.Release();
            evictA.Release();
            evictB.Release();
            Cam.RemoveCommandBuffer(CameraEvent.AfterSkybox, cb);
            rig.Recon.MarkCacheFilled = null;
            LightVolume.TimeWarpRate = 1f;
            rig.Dispose();
            Sink.Flag("soft_cost_completed", true);
        }

        private static double Median(List<double> values)
        {
            if (values.Count == 0) return double.NaN;
            double[] sorted = values.ToArray();
            Array.Sort(sorted);
            return sorted[sorted.Length / 2];
        }
    }
}
