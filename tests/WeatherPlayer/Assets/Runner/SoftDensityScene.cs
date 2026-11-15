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
        };

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
            public bool Jitter, Skip, SkipClosed, OldGrid, NoReach, StepGrowth, TapLod, FarMean;
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
                else if (t == "ev") c.Evict = true;   // E40 C2
                else if (t.StartsWith("mn")) c.Moon = Number(t, 2);   // E40 C2
                else if (t.StartsWith("mc")) c.MoonCut = Number(t, 2);   // E40 C2
                else if (t.StartsWith("lp")) c.Leap100 = Number(t, 2) / 100f;   // E40 C2
                else if (t == "og") c.OldGrid = true;   // E40 F2: the pattern's pixel grid of the pass before F2 (LayerFrame.DitherOwnGrid false)
                else if (t == "ctl") { }   // a control: the same case under another name (the run-to-run noise of the history and the timing)
                else if (t.StartsWith("spm")) c.SpacingMin = Number(t, 3);
                else if (t.StartsWith("sp")) c.Spacing = Number(t, 2) / 10f;
                else if (t.StartsWith("cr")) c.ConePattern = Number(t, 2);   // E40 F2: the light cone's rotation pattern (LayerFrame.ConePattern)
                else if (t.StartsWith("blk")) c.Block = Number(t, 3);
                else if (t.StartsWith("clip")) c.ClipSigma = Number(t, 4) / 10f;
                else if (t.StartsWith("acc")) c.Accumulate = Number(t, 3) / 100f;
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

        private static readonly LayerFrame FrameDefaults = new LayerFrame();
        private static readonly CloudLightSettings LightDefaults = new CloudLightSettings();

        // Every field a case can name is set on every call, so that a case never keeps what the one before it set on the same frame.
        internal static void ApplyCase(LayerFrame f, Case c)
        {
            ApplyMarch(f, c.Base);
            f.LightStride = c.Stride >= 0 ? c.Stride : FrameDefaults.LightStride;
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
            f.Cull.TapLod = c.TapLod;
            f.Far.ShapeMean = c.FarMean;
            f.EmptyReach = !c.NoReach;
            f.SpanSpacing = c.Spacing;
            f.SpanSpacingMin = c.SpacingMin;
            f.LeapScales = c.Leap100;
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
        }

        // The reconstruction of a case in a mode of GrainTable (plain: the half-size march alone; acc20: with the history). A block level has its own history and ignores the mode.
        internal static ReconSettings Settings(Case c, string mode)
        {
            if (c.Block > 0) return new ReconSettings { Mode = ReconMode.Block, Block = c.Block };
            ReconSettings s = GrainTable.ModeByName(mode).Settings();
            s.Resolution = c.Resolution;
            s.Jitter = c.Jitter;
            if (s.Accumulate > 0f) s.Accumulate = c.Accumulate;
            if (c.ClipSigma >= 0f) s.ClipSigma = c.ClipSigma;
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
        private const int W = 1708, H = 960, Frames = 40;

        private sealed class Scenario
        {
            public string Name;
            public float YawPerFrame, SidePerFrame;
            public int[] Dump;
        }

        // GrainTemporalScene's scenarios without the slow turn (the fast one is the harder case of the same motion).
        private static readonly Scenario[] Scenarios =
        {
            new Scenario { Name = "static", Dump = new[] { 36, 37, 38, 39 } },
            new Scenario { Name = "fly", SidePerFrame = 5f, Dump = new[] { 37, 38 } },
            new Scenario { Name = "turnfast", YawPerFrame = 3f, Dump = new[] { 37, 38 } },
            new Scenario { Name = "flyfast", SidePerFrame = 40f, Dump = new[] { 37, 38 } },
            new Scenario { Name = "cut", Dump = new[] { 0, 2, 5, 10 } },
        };

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

        private void Render(TemporalRig rig, LayerReconstructor recon, ReconSettings s, int number)
        {
            var cb = new CommandBuffer { name = "soft temporal" };
            recon.Record(cb, rig.Renderer, rig.Frame, Cam, s, number, null, null, null);
            Graphics.ExecuteCommandBuffer(cb);
            cb.Release();
        }

        private static float Yaw(Scenario sc, int i)
        {
            return sc.YawPerFrame * Mathf.Max(i - 6, 0);
        }

        private static float Side(Scenario sc, int i)
        {
            return sc.SidePerFrame * Mathf.Max(i - 6, 0);
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
                    foreach (Scenario sc in Scenarios)
                    {
                        // The converged reference of every pose that is written, of THIS medium.
                        // A case of an earlier scenario may have left taps, a cull or a cost mask on the frame: the references run on the frame's own defaults.
                        SoftTable.ApplyCase(rig.Frame, SoftTable.Parse("seq1"));
                        GrainTable.ApplyConverged(rig.Frame);
                        foreach (int i in sc.Dump)
                        {
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
                                    Render(rig, rig.Recon, settings, 100 + i);
                                    if (Array.IndexOf(sc.Dump, i) >= 0)
                                    {
                                        Dump(rig.Recon.Output, "gs-" + view + "-" + medium.Name + "-" + sc.Name + "-" + march + "-" + mode + "-f" + i);
                                        dumped++;
                                    }
                                    if (i % 8 == 7) yield return null;
                                }
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
            var rt = new RenderTexture(1707, 960, 24, RenderTextureFormat.DefaultHDR) { name = "soft cost target" };
            rt.Create();
            Cam.targetTexture = rt;
            // E40 C2 (case ev): two 2048 x 2048 half-float targets, copied into each other before the timed pass: 64 MB through the caches (the L2 of the card is a few MB).
            var evictA = new RenderTexture(2048, 2048, 0, RenderTextureFormat.ARGBHalf);
            var evictB = new RenderTexture(2048, 2048, 0, RenderTextureFormat.ARGBHalf);
            evictA.Create();
            evictB.Create();
            List<SoftTable.Medium> media = SoftTable.Selected();
            // Two rounds in opposite order, so that a drift of the machine over the run falls on every case alike; the row is the median of both.
            var samples = new Dictionary<string, List<double>[]>();
            for (int round = 0; round < 2; round++)
            {
                string[] views = SoftTable.Views;
                for (int vi = 0; vi < views.Length; vi++)
                {
                    string view = views[round == 0 ? vi : views.Length - 1 - vi];
                    LayersJob job = GrainTable.Find(file, "gn-" + view + "-prod-f0");
                    if (job == null) { Sink.Flag("soft_cost_job_" + view, false); continue; }
                    SoftTable.Original original = SoftTable.Original.Of(job.layers[0]);
                    rig.Load(job, input);
                    double detailMean = SoftTable.DetailMean(rig);
                    for (int k = 0; k < media.Count; k++)
                    {
                        SoftTable.Medium medium = media[round == 0 ? k : media.Count - 1 - k];
                        SoftTable.Load(rig, job, input, medium, original, detailMean);
                        rig.Frame.UseSceneDepth = true;
                        GrainTable.Pose(Cam, job, 0f, 0f);
                        string[] marches = SoftTable.Marches;
                        for (int mi = 0; mi < marches.Length; mi++)
                        {
                            string march = marches[round == 0 ? mi : marches.Length - 1 - mi];
                            if (clock.Elapsed.TotalMinutes > 18.0) { Sink.Flag("soft_cost_completed", false); yield break; }
                            SoftTable.Case marchCase = SoftTable.Parse(march);
                            SoftTable.ApplyCase(rig.Frame, marchCase);
                            ReconSettings settings = SoftTable.Settings(marchCase, "acc20");
                            rig.Recon.Reset();
                            string label = view + "." + medium.Name + "." + march;
                            if (!samples.ContainsKey(label)) samples[label] = new[] { new List<double>(), new List<double>(), new List<double>() };
                            List<double>[] s = samples[label];
                            int guard = 0, n = 0, taken = 0;
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
                                if (m >= 0f && r >= 0f && e >= 0f) { s[0].Add(m); s[1].Add(r); s[2].Add(e); taken++; }
                            }
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
            }
            Cam.targetTexture = Target;
            rt.Release();
            evictA.Release();
            evictB.Release();
            Cam.RemoveCommandBuffer(CameraEvent.AfterSkybox, cb);
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
