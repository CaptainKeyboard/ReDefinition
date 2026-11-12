// Scenes of the weather player for the cheap forms of the entry refinement (package T15), linked through the manifests of T01, T05 and T12 (the layer
// core, the temporal rig and EdgeKit):
//   -scene bisect-cost -in <T01 inputs>      ms of the cloud pass per view for the refinement off, on everywhere, the bisection steps and the edge mask
//                                            restriction, in the full mode, the 4 x 4 block mode and the edge mode, and the resolution levels 2 and 3
//                                            (timing run, exclusive)
//   -scene bisect-quality -in <T01 inputs>   per view the error of each variant against the reference (the mean of four full marches with the refinement on
//                                            every ray) in the band around the contour, in the thick interior and in the empty sky, the seam of the edge
//                                            mode, and the pictures t15-<view>-<variant>.png and t15-<view>-aid-error-<group>.png
//   -scene bisect-levels -in <T01 inputs>    the resolution levels 1 to 4 (and level 4 with the bisection and with the full refinement) through two turns:
//                                            ghosting, trail and shimmer against the full march of the same pose
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using ReDefinition.Atmospheres;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.WeatherPlayer
{
    // One way of finding the entry of a cloud, as the tables name it.
    internal sealed class BisectVariant
    {
        public string Name, Group;
        public ReconMode Mode = ReconMode.Full;
        public int Block = 4, Bisect;
        public float Refine, Cap;
        public EdgeSettings Edge;

        internal ReconSettings Settings()
        {
            return new ReconSettings { Mode = Mode, Block = Block, Edge = Edge };
        }

        private static BisectVariant Plain(string name, float refine, int bisect)
        {
            return new BisectVariant { Name = name, Group = "plain", Refine = refine, Bisect = bisect };
        }

        private static BisectVariant Block4(string name, float refine, int bisect)
        {
            return new BisectVariant { Name = name, Group = "recon4", Mode = ReconMode.Block, Refine = refine, Bisect = bisect };
        }

        private static BisectVariant Edged(string name, float refine, int bisect, bool band)
        {
            return new BisectVariant
            {
                Name = name, Group = "edge", Mode = ReconMode.Block, Refine = refine, Bisect = bisect,
                Edge = new EdgeSettings { Enabled = true, Threshold = EdgeSettings.ThresholdDefault, Dilation = EdgeSettings.DilationDefault, Persist = EdgeSettings.PersistDefault, StepBand = band }
            };
        }

        // full / none: the refinement on every ray / off (the two ends of every group); bN: N bisection steps with the refinement off; u4*: the same in the
        // 4 x 4 block mode (cloudResolution 4); eN / eNone: the edge mode with the refinement in every block / nowhere; eBN: the edge mode with the step band
        // (the refinement in the flagged blocks only, N bisection steps elsewhere; eB0 is the step band alone); lv2, lv3: the resolution levels without
        // refinement (the game's default switches; level 4 is u4none, level 1 is none).
        internal static List<BisectVariant> Table(string sweep)
        {
            var list = new List<BisectVariant>
            {
                Plain("full", 0.5f, 0), Plain("none", 0f, 0), Plain("b2", 0f, 2), Plain("b3", 0f, 3), Plain("b4", 0f, 4),
                Block4("u4full", 0.5f, 0), Block4("u4none", 0f, 0), Block4("u4b2", 0f, 2), Block4("u4b3", 0f, 3), Block4("u4b4", 0f, 4),
                Edged("eN", 0.5f, 0, false), Edged("eNone", 0f, 0, false), Edged("eB0", 0.5f, 0, true), Edged("eB2", 0.5f, 2, true), Edged("eB3", 0.5f, 3, true),
                Edged("eB4", 0.5f, 4, true),
                new BisectVariant { Name = "lv2", Group = "level", Mode = ReconMode.Block, Block = 2 },
                new BisectVariant { Name = "lv3", Group = "level", Mode = ReconMode.Block, Block = 3 },
            };
            if (sweep == "2")
            {
                // Sweep 2 is chosen after the first results (see tools/weather_metrics/t15.py); the entries are added here.
                list.AddRange(Extra2());
            }
            return list;
        }

        // The variants of the second sweep. Sweep 1 showed that the bisection recovers 0.1 to 0.3 of the refinement's interior gain: the entry position is not
        // what the refinement gains, the step budget inside the cloud is. cN: N bisection steps and the refinement's optical depth step budget (0.5) without the
        // entry walk; c0: the budget alone.
        private static IEnumerable<BisectVariant> Extra2()
        {
            yield return Capped(Plain("c0", 0f, 0));
            yield return Capped(Plain("c3", 0f, 3));
            yield return Capped(Block4("u4c0", 0f, 0));
            yield return Capped(Block4("u4c3", 0f, 3));
            BisectVariant e = Edged("eC3", 0.5f, 3, true);
            yield return Capped(e);
        }

        private static BisectVariant Capped(BisectVariant v)
        {
            v.Cap = 0.5f;
            return v;
        }
    }

    internal static class BisectKit
    {
        internal static void Apply(LayerFrame frame, BisectVariant v)
        {
            frame.RefineBudget = v.Refine;
            frame.RefineGate = 0f;
            frame.RefineGateFirst = 0f;
            frame.RefineBisect = v.Bisect;
            frame.RefineStepBudget = v.Cap;
        }

        internal static double Median(List<double> values)
        {
            if (values.Count == 0) return double.NaN;
            double[] sorted = values.ToArray();
            Array.Sort(sorted);
            return sorted[sorted.Length / 2];
        }
    }

    [WeatherScene("bisect-cost")]
    public sealed class BisectCostScene : WeatherSceneBase
    {
        public override IEnumerator Run()
        {
            string input = WeatherPass.Argument("-in");
            TemporalRig rig = input != null && File.Exists(Path.Combine(input, "jobs.json")) ? EdgeKit.Rig(input) : null;
            bool ready = rig != null && WeatherGpuTimer.Start();
            Sink.Flag("cost_timers_available", ready);
            if (!ready) yield break;
            LayersJobFile file = JsonUtility.FromJson<LayersJobFile>(File.ReadAllText(Path.Combine(input, "jobs.json")));
            var dummy = new RenderTexture(4, 4, 0, RenderTextureFormat.RFloat);
            dummy.Create();
            RenderTexture.active = dummy;
            GL.Clear(false, true, Color.black);
            RenderTexture.active = null;
            var depthId = new RenderTargetIdentifier(dummy);
            var cb = new CommandBuffer { name = "T15 cost" };
            Cam.AddCommandBuffer(CameraEvent.AfterSkybox, cb);
            var segments = new float[8];
            var clock = System.Diagnostics.Stopwatch.StartNew();
            const int coarse = WeatherGpuTimer.SlabMarched, mask = WeatherGpuTimer.CurtainStart, edgeMarch = WeatherGpuTimer.SlabComposited, recon = WeatherGpuTimer.CurtainEnd;
            Action<CommandBuffer> mStart = c => WeatherGpuTimer.Mark(c, WeatherGpuTimer.SlabStart), mCoarse = c => WeatherGpuTimer.Mark(c, coarse);
            Action<CommandBuffer> mEnd = c => WeatherGpuTimer.Mark(c, recon);
            rig.Recon.MarkMask = c => WeatherGpuTimer.Mark(c, mask);
            rig.Recon.MarkEdge = c => WeatherGpuTimer.Mark(c, edgeMarch);
            List<BisectVariant> variants = BisectVariant.Table(WeatherPass.Argument("-sweep"));
            var rt = new RenderTexture(1707, 960, 24, RenderTextureFormat.DefaultHDR) { name = "T15 cost target" };
            rt.Create();
            Cam.targetTexture = rt;
            foreach (LayersJob job in file.jobs)
            {
                if (job.id != "V-ground" && job.id != "V-inside" && job.id != "V-above" && job.id != "V-orbit-edge" && job.id != "V-stack-below") continue;
                rig.Load(job, input);
                LayerFrame frame = rig.Frame;
                frame.UseSceneDepth = true;
                frame.TransmittanceGrowth = 2f;
                rig.Pose(Cam, 0f, Vector3.zero);
                // Two passes over the table: the second repeats the first so that the medians carry the run-to-run noise (the tables report both).
                for (int repeat = 0; repeat < 2; repeat++)
                    foreach (BisectVariant v in variants)
                    {
                        if (clock.Elapsed.TotalMinutes > 17.0) { Sink.Flag("cost_run_completed", false); yield break; }
                        BisectKit.Apply(frame, v);
                        ReconSettings settings = v.Settings();
                        rig.Recon.Reset();
                        var cCoarse = new List<double>();
                        var cMask = new List<double>();
                        var cEdge = new List<double>();
                        var cRecon = new List<double>();
                        int guard = 0, n = 0;
                        while (cCoarse.Count < 50 && guard++ < 1200)
                        {
                            cb.Clear();
                            cb.SetGlobalTexture(Shader.PropertyToID("_SceneDepth"), depthId);
                            rig.Recon.Record(cb, rig.Renderer, frame, Cam, settings, FrameIndex, mStart, mCoarse, null);
                            rig.Recon.Composite(cb, rig.Renderer, BuiltinRenderTextureType.CameraTarget, mEnd);
                            yield return Frames(1);
                            if (n++ < 24) continue;
                            float total;
                            int number;
                            if (!WeatherGpuTimer.Read(segments, out total, out number)) continue;
                            float a = segments[coarse - WeatherGpuTimer.SlabStart], r = segments[recon - WeatherGpuTimer.SlabStart];
                            bool edge = v.Edge != null && v.Edge.Enabled;
                            float mk = edge ? segments[mask - WeatherGpuTimer.SlabStart] : 0f, em = edge ? segments[edgeMarch - WeatherGpuTimer.SlabStart] : 0f;
                            if (a >= 0f && r >= 0f && mk >= 0f && em >= 0f) { cCoarse.Add(a); cMask.Add(mk); cEdge.Add(em); cRecon.Add(r); }
                        }
                        string label = job.id + "." + v.Name + ".1707x960";
                        double tc = BisectKit.Median(cCoarse), tm = BisectKit.Median(cMask), te = BisectKit.Median(cEdge), tr = BisectKit.Median(cRecon);
                        string tag = repeat == 0 ? "bisectcost" : "bisectcostb";
                        Sink.Info(tag + ".total." + label, tc + tm + te + tr);
                        Sink.Info(tag + ".march." + label, tc + te);
                    }
            }
            Cam.targetTexture = Target;
            rt.Release();
            Cam.RemoveCommandBuffer(CameraEvent.AfterSkybox, cb);
            rig.Dispose();
            Sink.Flag("cost_run_completed", true);
        }
    }

    [WeatherScene("bisect-quality")]
    public sealed class BisectQualityScene : WeatherSceneBase
    {
        private const int W = 640, H = 360, RefFrames = 4, Settle = 28, Band = 6;
        // The variants whose pictures are saved, and the three of each group of the error aid (red, green, blue).
        private static readonly string[] Pictured = { "full", "none", "b2", "b3", "b4", "u4full", "u4none", "u4b3", "eN", "eNone", "eB0", "eB3" };
        private static readonly string[][] Aids =
        {
            new[] { "none", "b3", "full", "plain" }, new[] { "u4none", "u4b3", "u4full", "recon4" }, new[] { "eNone", "eB3", "eN", "edge" }
        };
        private Texture2D readback;

        public override IEnumerator Run()
        {
            string input = WeatherPass.Argument("-in");
            TemporalRig rig = input != null && File.Exists(Path.Combine(input, "jobs.json")) ? EdgeKit.Rig(input) : null;
            Sink.Flag("quality_available", rig != null);
            if (rig == null) yield break;
            string sweep = WeatherPass.Argument("-sweep");
            LayersJobFile file = JsonUtility.FromJson<LayersJobFile>(File.ReadAllText(Path.Combine(input, "jobs.json")));
            var rt = new RenderTexture(W, H, 24, RenderTextureFormat.DefaultHDR);
            rt.Create();
            Cam.targetTexture = rt;
            string pictures = Path.Combine(Output, "refine15");
            Directory.CreateDirectory(pictures);
            int pixels = W * H, bw = EdgeMath.Blocks(W), bh = EdgeMath.Blocks(H);
            var img = new float[pixels];
            var refAlpha = new float[pixels];
            var refDisplay = new float[pixels];
            var tmpAlpha = new float[pixels];
            foreach (string view in new[] { "ground", "inside", "above", "orbit-edge", "stack-below" })
            {
                LayersJob job = null;
                foreach (LayersJob j in file.jobs) if (j.id == "V-" + view) job = j;
                if (job == null) { Sink.Flag("quality_job_" + view, false); continue; }
                rig.Load(job, input);
                rig.Frame.UseProductCost();
                rig.Pose(Cam, 0f, Vector3.zero);
                BisectKit.Apply(rig.Frame, new BisectVariant { Refine = 0.5f });
                var full = new ReconSettings { Mode = ReconMode.Full };
                Array.Clear(refDisplay, 0, pixels);
                Array.Clear(refAlpha, 0, pixels);
                for (int k = 0; k < RefFrames; k++)
                {
                    rig.Recon.Reset();
                    EdgeKit.Render(Cam, rig, full, 9000 + k * 7);
                    EdgeKit.Read(rig.Recon.Output, ref readback, img, tmpAlpha);
                    for (int p = 0; p < pixels; p++) { refDisplay[p] += img[p] / RefFrames; refAlpha[p] += tmpAlpha[p] / RefFrames; }
                }
                bool[] inBand, interior, empty;
                EdgeKit.Classes(refAlpha, W, H, Band, out inBand, out interior, out empty);
                int nBand = 0, nInterior = 0, nEmpty = 0;
                for (int p = 0; p < pixels; p++) { if (inBand[p]) nBand++; if (interior[p]) nInterior++; if (empty[p]) nEmpty++; }
                Sink.Info("bisq." + view + ".ref.band_share", (double)nBand / pixels);
                Sink.Info("bisq." + view + ".ref.interior_share", (double)nInterior / pixels);
                var shown = new Dictionary<string, float[]>();
                foreach (BisectVariant v in BisectVariant.Table(sweep))
                {
                    BisectKit.Apply(rig.Frame, v);
                    ReconSettings s = v.Settings();
                    rig.Recon.Reset();
                    var d = new float[pixels];
                    var a = new float[pixels];
                    var prev = new float[pixels];
                    var flicker = new List<double>();
                    int frames = v.Mode == ReconMode.Full ? 1 : Settle;
                    for (int i = 0; i < frames; i++)
                    {
                        // The variants of one mode use the same frame numbers, so that the per-pixel dither is the same and only the entry search differs.
                        EdgeKit.Render(Cam, rig, s, 100 + i);
                        if (i >= frames - 6 && frames > 6)
                        {
                            Array.Copy(d, prev, pixels);
                            EdgeKit.Read(rig.Recon.Output, ref readback, d, a);
                            if (i > frames - 6)
                            {
                                double f = 0, c = 0;
                                for (int p = 0; p < pixels; p++) if (inBand[p]) { f += Math.Abs(d[p] - prev[p]); c += 1; }
                                if (c > 0) flicker.Add(f / c);
                            }
                        }
                        else if (i == frames - 1) EdgeKit.Read(rig.Recon.Output, ref readback, d, a);
                        if (i % 6 == 5) yield return null;
                    }
                    string key = "bisq." + view + "." + v.Name;
                    double sb = 0, si = 0, ci = 0, lumI = 0, leak = 0, sAll = 0;
                    for (int p = 0; p < pixels; p++)
                    {
                        double e = d[p] - refDisplay[p];
                        sAll += Math.Abs(e);
                        if (inBand[p]) sb += e * e;
                        if (interior[p]) { si += Math.Abs(e); lumI += refDisplay[p]; ci += 1; }
                        if (empty[p] && a[p] > 0.001f) leak += 1;
                    }
                    Sink.Info(key + ".band_rms", nBand > 0 ? Math.Sqrt(sb / nBand) : double.NaN);
                    Sink.Info(key + ".interior_err", ci > 0 ? si / lumI : double.NaN);
                    Sink.Info(key + ".empty_leak", nEmpty > 0 ? leak / nEmpty : 0.0);
                    Sink.Info(key + ".mean_abs", sAll / pixels);
                    if (flicker.Count > 0) Sink.Info(key + ".band_flicker", EdgeKit.Mean(flicker));
                    if (v.Edge != null && v.Edge.Enabled)
                    {
                        bool[] mask = EdgeKit.ReadMask(rig.Recon.Mask, bw, bh);
                        int flagged = 0;
                        for (int q = 0; q < mask.Length; q++) if (mask[q]) flagged++;
                        Sink.Info(key + ".mask_share", (double)flagged / mask.Length);
                        // The seam: the display step across the border between a flagged and an unflagged block against the same step in the reference.
                        double seam = 0, seamRef = 0, seamN = 0, lum = 0, lumN = 0;
                        for (int y = 0; y < H; y++)
                            for (int x = 3; x < W - 1; x += 4)
                            {
                                if (EdgeKit.Flagged(mask, bw, x, y) == EdgeKit.Flagged(mask, bw, x + 1, y)) continue;
                                if (refAlpha[y * W + x] < 0.02f && refAlpha[y * W + x + 1] < 0.02f) continue;
                                seam += Math.Abs(d[y * W + x + 1] - d[y * W + x]);
                                seamRef += Math.Abs(refDisplay[y * W + x + 1] - refDisplay[y * W + x]);
                                seamN += 1;
                                lum += refDisplay[y * W + x];
                                lumN += 1;
                            }
                        Sink.Info(key + ".seam", seamN > 0 ? Math.Max(0.0, (seam - seamRef) / seamN) / Math.Max(lum / lumN, 1e-6) : 0.0);
                    }
                    shown[v.Name] = d;
                    yield return null;
                }
                // Pictures: the reference and the pictured variants as grey, and per group an error aid (x8; red the cheapest end of the group, green the
                // bisection, blue the full refinement).
                EdgeKit.SaveGrey(Path.Combine(pictures, "t15-" + view + "-reference.png"), refDisplay, W, H);
                foreach (string name in Pictured)
                    if (shown.ContainsKey(name)) EdgeKit.SaveGrey(Path.Combine(pictures, "t15-" + view + "-" + name + ".png"), shown[name], W, H);
                foreach (string[] aid in Aids)
                {
                    if (!shown.ContainsKey(aid[0]) || !shown.ContainsKey(aid[1]) || !shown.ContainsKey(aid[2])) continue;
                    var px = new Color[pixels];
                    for (int p = 0; p < pixels; p++)
                        px[p] = new Color(Mathf.Clamp01(Mathf.Abs(shown[aid[0]][p] - refDisplay[p]) * 8f), Mathf.Clamp01(Mathf.Abs(shown[aid[1]][p] - refDisplay[p]) * 8f),
                                          Mathf.Clamp01(Mathf.Abs(shown[aid[2]][p] - refDisplay[p]) * 8f));
                    EdgeKit.SaveColour(Path.Combine(pictures, "t15-" + view + "-aid-error-" + aid[3] + ".png"), px, W, H);
                }
                yield return null;
            }
            rig.Dispose();
            Cam.targetTexture = Target;
            rt.Release();
            Sink.Flag("quality_completed", true);
        }
    }

    // The resolution levels through two turns: ghosting while the camera turns, the trail (pixels more than 4 percent off), and the shimmer of the settled picture.
    [WeatherScene("bisect-levels")]
    public sealed class BisectLevelsScene : WeatherSceneBase
    {
        private const int W = 640, H = 360, RefFrames = 3;
        private Texture2D readback;

        private sealed class Scenario
        {
            public string Name;
            public Func<int, float> Yaw = i => 0f;
            public int Frames = 40, LastMove;
        }

        public override IEnumerator Run()
        {
            string input = WeatherPass.Argument("-in");
            TemporalRig rig = input != null && File.Exists(Path.Combine(input, "jobs.json")) ? EdgeKit.Rig(input) : null;
            Sink.Flag("levels_available", rig != null);
            if (rig == null) yield break;
            LayersJobFile file = JsonUtility.FromJson<LayersJobFile>(File.ReadAllText(Path.Combine(input, "jobs.json")));
            var rt = new RenderTexture(W, H, 24, RenderTextureFormat.DefaultHDR);
            rt.Create();
            Cam.targetTexture = rt;
            int pixels = W * H;
            var img = new float[pixels];
            var tmpAlpha = new float[pixels];
            var scenarios = new List<Scenario>
            {
                new Scenario { Name = "turn2", Frames = 50, Yaw = i => Mathf.Clamp(i - 6, 0, 22) * 2f, LastMove = 28 },
                new Scenario { Name = "turn12", Frames = 40, Yaw = i => Mathf.Clamp(i - 6, 0, 10) * 12f, LastMove = 16 },
            };
            // The levels as the game's switches make them (refinement off), level 4 also with three bisection steps and with the full refinement.
            var variants = new List<BisectVariant>();
            foreach (BisectVariant v in BisectVariant.Table(null)) if (v.Name == "none" || v.Name == "lv2" || v.Name == "lv3" || v.Name == "u4none" || v.Name == "u4b3" || v.Name == "u4full") variants.Add(v);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            foreach (string jobId in new[] { "V-ground", "V-stack-below" })
            {
                LayersJob job = null;
                foreach (LayersJob j in file.jobs) if (j.id == jobId) job = j;
                if (job == null) continue;
                rig.Load(job, input);
                rig.Frame.UseProductCost();
                var full = new ReconSettings { Mode = ReconMode.Full };
                foreach (Scenario sc in scenarios)
                {
                    BisectKit.Apply(rig.Frame, new BisectVariant { Refine = 0.5f });
                    var refDisplay = new float[sc.Frames][];
                    var refAlpha = new float[sc.Frames][];
                    for (int i = 0; i < sc.Frames; i++)
                    {
                        rig.Pose(Cam, sc.Yaw(i), Vector3.zero);
                        var sum = new float[pixels];
                        var al = new float[pixels];
                        for (int k = 0; k < RefFrames; k++)
                        {
                            rig.Recon.Reset();
                            EdgeKit.Render(Cam, rig, full, 9000 + i * 7 + k);
                            EdgeKit.Read(rig.Recon.Output, ref readback, img, tmpAlpha);
                            for (int p = 0; p < pixels; p++) { sum[p] += img[p] / RefFrames; al[p] += tmpAlpha[p] / RefFrames; }
                        }
                        refDisplay[i] = sum;
                        refAlpha[i] = al;
                        if (i % 6 == 5) yield return null;
                    }
                    foreach (BisectVariant v in variants)
                    {
                        if (clock.Elapsed.TotalMinutes > 16.0) { Sink.Flag("levels_completed", false); yield break; }
                        BisectKit.Apply(rig.Frame, v);
                        ReconSettings s = v.Settings();
                        rig.Recon.Reset();
                        var moving = new List<double>();
                        var trail = new List<double>();
                        var shimmer = new List<double>();
                        float[] previous = null;
                        var outAlpha = new float[pixels];
                        string key = "levels." + jobId + "." + sc.Name + "." + v.Name;
                        for (int i = 0; i < sc.Frames; i++)
                        {
                            rig.Pose(Cam, sc.Yaw(i), Vector3.zero);
                            EdgeKit.Render(Cam, rig, s, 100 + i);
                            var d = new float[pixels];
                            EdgeKit.Read(rig.Recon.Output, ref readback, d, outAlpha);
                            double err = 0, trailCount = 0, region = 0, flicker = 0;
                            for (int p = 0; p < pixels; p++)
                            {
                                if (refAlpha[i][p] < 0.02f && outAlpha[p] < 0.02f) continue;
                                region += 1;
                                double e = Math.Abs(d[p] - refDisplay[i][p]);
                                err += e;
                                if (e > 0.04) trailCount += 1;
                                if (previous != null) flicker += Math.Abs(d[p] - previous[p]);
                            }
                            if (region > 0)
                            {
                                err /= region;
                                bool isMoving = i > 6 && i <= sc.LastMove;
                                bool settled = i > sc.LastMove + 10;
                                if (isMoving) { moving.Add(err); trail.Add(trailCount / region); }
                                else if (settled && previous != null) shimmer.Add(flicker / region);
                            }
                            previous = d;
                        }
                        Sink.Info(key + ".ghost_move", EdgeKit.Mean(moving));
                        Sink.Info(key + ".trail_px", EdgeKit.Mean(trail));
                        Sink.Info(key + ".shimmer", EdgeKit.Mean(shimmer));
                        yield return null;
                    }
                }
            }
            rig.Dispose();
            Cam.targetTexture = Target;
            rt.Release();
            Sink.Flag("levels_completed", true);
        }
    }
}
