// Scenes of the weather player for the edge-adaptive resolution of the cloud pass (package T12, the plan's T05b), linked with
// tests/WeatherPlayer/linked/T12.txt (the layer core and the temporal rig come from T01.txt and T05.txt):
//   -scene edge-cost -in <T01 inputs>      ms per view of the coarse, mask, edge and reconstruction passes (timing run, exclusive)
//   -scene edge-quality -in <T01 inputs>   edge, interior and sky error against the reference, the seams, the mask, the pictures
//   -scene edge-temporal -in <T01 inputs>  turns, a fly-by and a camera cut: ghosting, trails, shimmer, convergence, the mask's cover
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using ReDefinition.Atmospheres;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.WeatherPlayer
{
    // One way of filling the cloud picture, as the tables name it.
    internal sealed class EdgeVariant
    {
        public string Name;
        public ReconMode Mode = ReconMode.Block;
        public int Block = 4;
        // The entry refinement the variant's rays march with (the frame's RefineBudget).
        public float Refine;
        public EdgeSettings Edge;

        internal static EdgeSettings Edged(float threshold, int dilation, int persist, bool band)
        {
            return new EdgeSettings { Enabled = true, Threshold = threshold, Dilation = dilation, Persist = persist, StepBand = band };
        }

        internal ReconSettings Settings()
        {
            return new ReconSettings { Mode = Mode, Block = Block, Edge = Edge };
        }

        // The modes of the tables. full: every pixel every frame with the refinement (the reference's own pass); u4, u2: the uniform block modes of T05 as
        // they run today (no refinement); u4r, u2r: the same with the entry refinement on every ray; e: the edge-adaptive mode (the 4 x 4 coarse pattern,
        // the 2 x 2 pattern with the refinement in the flagged blocks); eN: the same without the band (the refinement in every block).
        internal static List<EdgeVariant> Table(bool sweep)
        {
            var list = new List<EdgeVariant>
            {
                new EdgeVariant { Name = "full", Mode = ReconMode.Full, Refine = 0.5f },
                new EdgeVariant { Name = "u4", Refine = 0f },
                new EdgeVariant { Name = "u4r", Refine = 0.5f },
                new EdgeVariant { Name = "u2", Block = 2, Refine = 0f },
                new EdgeVariant { Name = "u2r", Block = 2, Refine = 0.5f },
                new EdgeVariant { Name = "e", Refine = 0.5f, Edge = Edged(EdgeSettings.ThresholdDefault, EdgeSettings.DilationDefault, EdgeSettings.PersistDefault, true) },
                new EdgeVariant { Name = "eN", Refine = 0.5f, Edge = Edged(EdgeSettings.ThresholdDefault, EdgeSettings.DilationDefault, EdgeSettings.PersistDefault, false) },
            };
            if (sweep)
            {
                // One sweep of the three tuned numbers, one at a time around the default (threshold, dilation, persistence).
                list.Add(new EdgeVariant { Name = "eT03", Refine = 0.5f, Edge = Edged(0.03f, EdgeSettings.DilationDefault, EdgeSettings.PersistDefault, true) });
                list.Add(new EdgeVariant { Name = "eT12", Refine = 0.5f, Edge = Edged(0.12f, EdgeSettings.DilationDefault, EdgeSettings.PersistDefault, true) });
                list.Add(new EdgeVariant { Name = "eD0", Refine = 0.5f, Edge = Edged(EdgeSettings.ThresholdDefault, 0, EdgeSettings.PersistDefault, true) });
                list.Add(new EdgeVariant { Name = "eD2", Refine = 0.5f, Edge = Edged(EdgeSettings.ThresholdDefault, 2, EdgeSettings.PersistDefault, true) });
                list.Add(new EdgeVariant { Name = "eP1", Refine = 0.5f, Edge = Edged(EdgeSettings.ThresholdDefault, EdgeSettings.DilationDefault, 1, true) });
                list.Add(new EdgeVariant { Name = "eP8", Refine = 0.5f, Edge = Edged(EdgeSettings.ThresholdDefault, EdgeSettings.DilationDefault, 8, true) });
            }
            return list;
        }
    }

    // The shared helpers of the three scenes.
    internal static class EdgeKit
    {
        internal static readonly Color Sky = new Color(0.42f, 0.52f, 0.72f);

        internal static TemporalRig Rig(string input)
        {
            var rig = new TemporalRig();
            if (!rig.Init()) return null;
            rig.Recon.InitEdge(Shader.Find(LayerReconstructor.EdgeShaderName));
            return rig;
        }

        internal static float Display(Color c)
        {
            float a = c.a;
            float lr = c.r + (1f - a) * Sky.r, lg = c.g + (1f - a) * Sky.g, lb = c.b + (1f - a) * Sky.b;
            float x = 0.2126f * lr + 0.7152f * lg + 0.0722f * lb;
            return x / (1f + x);
        }

        internal static void Read(RenderTexture rt, ref Texture2D readback, float[] display, float[] alpha)
        {
            if (readback == null || readback.width != rt.width || readback.height != rt.height) readback = new Texture2D(rt.width, rt.height, TextureFormat.RGBAHalf, false, true);
            RenderTexture.active = rt;
            readback.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0, false);
            RenderTexture.active = null;
            Color[] px = readback.GetPixels();
            for (int i = 0; i < px.Length; i++)
            {
                display[i] = Display(px[i]);
                if (alpha != null) alpha[i] = px[i].a;
            }
        }

        // The mask as a flag per 4 x 4 block (empty where the variant has none).
        internal static bool[] ReadMask(RenderTexture mask, int bw, int bh)
        {
            var flags = new bool[bw * bh];
            if (mask == null) return flags;
            var tex = new Texture2D(mask.width, mask.height, TextureFormat.RGBA32, false, true);
            RenderTexture.active = mask;
            tex.ReadPixels(new Rect(0, 0, mask.width, mask.height), 0, 0, false);
            RenderTexture.active = null;
            Color32[] px = tex.GetPixels32();
            for (int i = 0; i < flags.Length && i < px.Length; i++) flags[i] = px[i].r > 127;
            UnityEngine.Object.Destroy(tex);
            return flags;
        }

        // A square dilation by r pixels of a pixel set (two passes of the running maximum).
        internal static bool[] Grow(bool[] set, int w, int h, int r)
        {
            var tmp = new bool[w * h];
            var res = new bool[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    bool on = false;
                    for (int d = Math.Max(0, x - r); d <= Math.Min(w - 1, x + r) && !on; d++) on = set[y * w + d];
                    tmp[y * w + x] = on;
                }
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    bool on = false;
                    for (int d = Math.Max(0, y - r); d <= Math.Min(h - 1, y + r) && !on; d++) on = tmp[d * w + x];
                    res[y * w + x] = on;
                }
            return res;
        }

        // The classes of the reference picture: the soft contour (alpha between 5 and 95 percent), the band of N pixels around it, the thick
        // interior (alpha above 95 percent outside the band) and the empty sky (no alpha outside the band).
        internal static void Classes(float[] refAlpha, int w, int h, int band, out bool[] inBand, out bool[] interior, out bool[] empty)
        {
            var contour = new bool[w * h];
            for (int i = 0; i < contour.Length; i++) contour[i] = refAlpha[i] > 0.05f && refAlpha[i] < 0.95f;
            inBand = Grow(contour, w, h, band);
            interior = new bool[w * h];
            empty = new bool[w * h];
            for (int i = 0; i < contour.Length; i++)
            {
                interior[i] = !inBand[i] && refAlpha[i] >= 0.95f;
                empty[i] = !inBand[i] && refAlpha[i] <= 0.001f;
            }
        }

        // The flag of a pixel's 4 x 4 block.
        internal static bool Flagged(bool[] mask, int bw, int x, int y)
        {
            return mask[(y / 4) * bw + x / 4];
        }

        internal static double Mean(List<double> values)
        {
            if (values.Count == 0) return double.NaN;
            double s = 0;
            foreach (double v in values) s += v;
            return s / values.Count;
        }

        // A picture of one float plane (display values) as grey.
        internal static void SaveGrey(string path, float[] plane, int w, int h)
        {
            var tex = new Texture2D(w, h, TextureFormat.RGB24, false, true);
            var px = new Color[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float v = plane[y * w + x];
                    px[y * w + x] = new Color(v, v, v);
                }
            tex.SetPixels(px);
            File.WriteAllBytes(path, tex.EncodeToPNG());
            UnityEngine.Object.Destroy(tex);
        }

        internal static void SaveColour(string path, Color[] px, int w, int h)
        {
            var tex = new Texture2D(w, h, TextureFormat.RGB24, false, true);
            tex.SetPixels(px);
            File.WriteAllBytes(path, tex.EncodeToPNG());
            UnityEngine.Object.Destroy(tex);
        }

        internal static void Render(Camera cam, TemporalRig rig, ReconSettings s, int number)
        {
            var cb = new CommandBuffer { name = "T12" };
            rig.Recon.Record(cb, rig.Renderer, rig.Frame, cam, s, number, null, null, null);
            Graphics.ExecuteCommandBuffer(cb);
            cb.Release();
        }
    }

    // ------------------------------------------------------------------------------------------------------------------------------
    // Cost
    // ------------------------------------------------------------------------------------------------------------------------------
    [WeatherScene("edge-cost")]
    public sealed class EdgeCostScene : WeatherSceneBase
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
            var cb = new CommandBuffer { name = "T12 cost" };
            Cam.AddCommandBuffer(CameraEvent.AfterSkybox, cb);
            var sizes = new[] { new Vector2Int(1707, 960), new Vector2Int(2560, 1440) };
            var segments = new float[8];
            var clock = System.Diagnostics.Stopwatch.StartNew();
            const int coarse = WeatherGpuTimer.SlabMarched, mask = WeatherGpuTimer.CurtainStart, edgeMarch = WeatherGpuTimer.SlabComposited, recon = WeatherGpuTimer.CurtainEnd;
            Action<CommandBuffer> mStart = c => WeatherGpuTimer.Mark(c, WeatherGpuTimer.SlabStart), mCoarse = c => WeatherGpuTimer.Mark(c, coarse);
            rig.Recon.MarkMask = c => WeatherGpuTimer.Mark(c, mask);
            rig.Recon.MarkEdge = c => WeatherGpuTimer.Mark(c, edgeMarch);
            Action<CommandBuffer> mEnd = c => WeatherGpuTimer.Mark(c, recon);
            List<EdgeVariant> variants = EdgeVariant.Table(WeatherPass.Argument("-sweep") == "1");
            foreach (Vector2Int size in sizes)
            {
                var rt = new RenderTexture(size.x, size.y, 24, RenderTextureFormat.DefaultHDR) { name = "T12 cost target" };
                rt.Create();
                Cam.targetTexture = rt;
                string sz = size.x + "x" + size.y;
                foreach (LayersJob job in file.jobs)
                {
                    if (!job.id.StartsWith("V-")) continue;
                    rig.Load(job, input);
                    LayerFrame frame = rig.Frame;
                    frame.UseSceneDepth = true;
                    frame.TransmittanceGrowth = 2f;
                    rig.Pose(Cam, 0f, Vector3.zero);
                    foreach (EdgeVariant v in variants)
                    {
                        if (clock.Elapsed.TotalMinutes > 17.0) { Sink.Flag("cost_run_completed", false); yield break; }
                        frame.RefineBudget = v.Refine;
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
                        string label = job.id + "." + v.Name + "." + sz;
                        double tc = Median(cCoarse), tm = Median(cMask), te = Median(cEdge), tr = Median(cRecon);
                        Sink.Info("edgecost.coarse." + label, tc);
                        Sink.Info("edgecost.mask." + label, tm);
                        Sink.Info("edgecost.edge." + label, te);
                        Sink.Info("edgecost.recon." + label, tr);
                        Sink.Info("edgecost.total." + label, tc + tm + te + tr);
                    }
                }
                Cam.targetTexture = Target;
                rt.Release();
            }
            Cam.RemoveCommandBuffer(CameraEvent.AfterSkybox, cb);
            rig.Dispose();
            Sink.Flag("cost_run_completed", true);
        }

        private static double Median(List<double> values)
        {
            if (values.Count == 0) return double.NaN;
            double[] sorted = values.ToArray();
            Array.Sort(sorted);
            return sorted[sorted.Length / 2];
        }
    }

    // ------------------------------------------------------------------------------------------------------------------------------
    // Quality of a still picture and the pictures
    // ------------------------------------------------------------------------------------------------------------------------------
    [WeatherScene("edge-quality")]
    public sealed class EdgeQualityScene : WeatherSceneBase
    {
        private const int W = 640, H = 360, RefFrames = 4, Settle = 28, Band = 6;
        private Texture2D readback;

        public override IEnumerator Run()
        {
            string input = WeatherPass.Argument("-in");
            TemporalRig rig = input != null && File.Exists(Path.Combine(input, "jobs.json")) ? EdgeKit.Rig(input) : null;
            Sink.Flag("quality_available", rig != null);
            if (rig == null) yield break;
            bool sweep = WeatherPass.Argument("-sweep") == "1";
            LayersJobFile file = JsonUtility.FromJson<LayersJobFile>(File.ReadAllText(Path.Combine(input, "jobs.json")));
            var rt = new RenderTexture(W, H, 24, RenderTextureFormat.DefaultHDR);
            rt.Create();
            Cam.targetTexture = rt;
            string pictures = Path.Combine(Output, "edge");
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
                // The reference: the mean of four full marches with the entry refinement (the full-resolution render of the same density field).
                rig.Frame.RefineBudget = 0.5f;
                var full = new ReconSettings { Mode = ReconMode.Full };
                Array.Clear(refDisplay, 0, pixels);
                Array.Clear(refAlpha, 0, pixels);
                for (int k = 0; k < RefFrames; k++)
                {
                    rig.Recon.Reset();
                    EdgeKit.Render(Cam, rig,full, 9000 + k * 7);
                    EdgeKit.Read(rig.Recon.Output, ref readback, img, tmpAlpha);
                    for (int p = 0; p < pixels; p++) { refDisplay[p] += img[p] / RefFrames; refAlpha[p] += tmpAlpha[p] / RefFrames; }
                }
                bool[] inBand, interior, empty;
                EdgeKit.Classes(refAlpha, W, H, Band, out inBand, out interior, out empty);
                int nBand = 0, nInterior = 0, nEmpty = 0;
                for (int p = 0; p < pixels; p++) { if (inBand[p]) nBand++; if (interior[p]) nInterior++; if (empty[p]) nEmpty++; }
                Sink.Info("edgeq." + view + ".ref.band_share", (double)nBand / pixels);
                Sink.Info("edgeq." + view + ".ref.interior_share", (double)nInterior / pixels);
                float[] naturalBefore = null;
                foreach (EdgeVariant v in EdgeVariant.Table(sweep))
                {
                    rig.Frame.RefineBudget = v.Refine;
                    ReconSettings s = v.Settings();
                    rig.Recon.Reset();
                    var d = new float[pixels];
                    var a = new float[pixels];
                    var prev = new float[pixels];
                    var flicker = new List<double>();
                    for (int i = 0; i < Settle; i++)
                    {
                        EdgeKit.Render(Cam, rig,s, 100 + i);
                        if (i >= Settle - 6)
                        {
                            Array.Copy(d, prev, pixels);
                            EdgeKit.Read(rig.Recon.Output, ref readback, d, a);
                            if (i > Settle - 6)
                            {
                                double f = 0, c = 0;
                                for (int p = 0; p < pixels; p++) if (inBand[p]) { f += Math.Abs(d[p] - prev[p]); c += 1; }
                                if (c > 0) flicker.Add(f / c);
                            }
                        }
                        if (i % 6 == 5) yield return null;
                    }
                    bool[] mask = EdgeKit.ReadMask(rig.Recon.Mask, bw, bh);
                    string key = "edgeq." + view + "." + v.Name;
                    double sb = 0, si = 0, ci = 0, lumI = 0, leak = 0;
                    for (int p = 0; p < pixels; p++)
                    {
                        double e = d[p] - refDisplay[p];
                        if (inBand[p]) sb += e * e;
                        if (interior[p]) { si += Math.Abs(e); lumI += refDisplay[p]; ci += 1; }
                        if (empty[p] && a[p] > 0.001f) leak += 1;
                    }
                    Sink.Info(key + ".band_rms", nBand > 0 ? Math.Sqrt(sb / nBand) : double.NaN);
                    Sink.Info(key + ".interior_err", ci > 0 ? si / lumI : double.NaN);
                    Sink.Info(key + ".empty_leak", nEmpty > 0 ? leak / nEmpty : 0.0);
                    Sink.Info(key + ".band_flicker", EdgeKit.Mean(flicker));
                    if (v.Edge != null && v.Edge.Enabled)
                    {
                        int flagged = 0, covered = 0, bandCount = 0;
                        for (int by = 0; by < bh; by++) for (int bx = 0; bx < bw; bx++) if (mask[by * bw + bx]) flagged++;
                        for (int y = 0; y < H; y++)
                            for (int x = 0; x < W; x++)
                                if (inBand[y * W + x] && refAlpha[y * W + x] > 0.05f && refAlpha[y * W + x] < 0.95f) { bandCount++; if (EdgeKit.Flagged(mask, bw, x, y)) covered++; }
                        Sink.Info(key + ".mask_share", (double)flagged / (bw * bh));
                        bool[] state = EdgeKit.ReadMask(rig.Recon.MaskState, bw, bh);
                        int stateCount = 0;
                        for (int q = 0; q < state.Length; q++) if (state[q]) stateCount++;
                        Sink.Info(key + ".state_share", (double)stateCount / state.Length);
                        Sink.Info(key + ".dilation", rig.Recon.LastDilation);
                        bool[] horizontal = EdgeKit.ReadMask(rig.Recon.MaskHorizontal, bw, bh);
                        int hCount = 0;
                        for (int q = 0; q < horizontal.Length; q++) if (horizontal[q]) hCount++;
                        Sink.Info(key + ".h_share", (double)hCount / horizontal.Length);
                        Sink.Info(key + ".contour_cover", bandCount > 0 ? (double)covered / bandCount : double.NaN);
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
                    if (v.Name == "u4") naturalBefore = (float[])d.Clone();
                    // The pictures of the two views of the rule: before (uniform 4 x 4), after (edge-adaptive), the aids.
                    if (v.Name == "e" && naturalBefore != null)
                    {
                        EdgeKit.SaveGrey(Path.Combine(pictures, "t12-" + view + "-before.png"), naturalBefore, W, H);
                        EdgeKit.SaveGrey(Path.Combine(pictures, "t12-" + view + "-after.png"), d, W, H);
                        EdgeKit.SaveGrey(Path.Combine(pictures, "t12-" + view + "-reference.png"), refDisplay, W, H);
                        var maskPx = new Color[pixels];
                        var rayPx = new Color[pixels];
                        var diffPx = new Color[pixels];
                        for (int y = 0; y < H; y++)
                            for (int x = 0; x < W; x++)
                            {
                                int p = y * W + x;
                                bool f = EdgeKit.Flagged(mask, bw, x, y);
                                float g = d[p];
                                maskPx[p] = f ? new Color(Mathf.Min(1f, g * 0.5f + 0.5f), g * 0.35f, g * 0.35f) : new Color(g, g, g);
                                // Rays per block: the coarse pattern marches 1 of 16 pixels, the flagged blocks 1 + 4 of 16.
                                rayPx[p] = f ? new Color(1f, 0.85f, 0.2f) * (0.45f + 0.55f * g) : new Color(0.1f, 0.2f, 0.45f) * (0.45f + 0.55f * g);
                                float eBefore = Mathf.Abs(naturalBefore[p] - refDisplay[p]) * 8f, eAfter = Mathf.Abs(d[p] - refDisplay[p]) * 8f;
                                diffPx[p] = new Color(Mathf.Clamp01(eBefore), Mathf.Clamp01(eAfter), 0f);
                            }
                        EdgeKit.SaveColour(Path.Combine(pictures, "t12-" + view + "-aid-mask.png"), Flip(maskPx), W, H);
                        EdgeKit.SaveColour(Path.Combine(pictures, "t12-" + view + "-aid-rays.png"), Flip(rayPx), W, H);
                        EdgeKit.SaveColour(Path.Combine(pictures, "t12-" + view + "-aid-error.png"), Flip(diffPx), W, H);
                    }
                    yield return null;
                }
                // The step-count heat map of the full pass (the layer shader's aid 5), the aid of the step band.
                rig.Frame.AidMode = 5;
                rig.Frame.RefineBudget = 0.5f;
                rig.Recon.Reset();
                EdgeKit.Render(Cam, rig,full, 5);
                rig.Frame.AidMode = 0;
                RenderTexture.active = rig.Recon.Output;
                var stepTex = new Texture2D(W, H, TextureFormat.RGBAHalf, false, true);
                stepTex.ReadPixels(new Rect(0, 0, W, H), 0, 0, false);
                RenderTexture.active = null;
                Color[] steps = stepTex.GetPixels();
                for (int p = 0; p < steps.Length; p++) steps[p] = new Color(steps[p].r, steps[p].g, steps[p].b);
                EdgeKit.SaveColour(Path.Combine(pictures, "t12-" + view + "-aid-steps.png"), steps, W, H);
                UnityEngine.Object.Destroy(stepTex);
                yield return null;
            }
            rig.Dispose();
            Cam.targetTexture = Target;
            rt.Release();
            Sink.Flag("quality_completed", true);
        }

        // The readback order and the texture's order are the same (the pictures of T05 are saved this way).
        private static Color[] Flip(Color[] px)
        {
            return px;
        }
    }

    // ------------------------------------------------------------------------------------------------------------------------------
    // Motion: turns, a fly-by, a camera cut
    // ------------------------------------------------------------------------------------------------------------------------------
    [WeatherScene("edge-temporal")]
    public sealed class EdgeTemporalScene : WeatherSceneBase
    {
        private const int W = 640, H = 360, RefFrames = 3, Band = 6;
        private Texture2D readback;

        private sealed class Scenario
        {
            public string Name;
            public Func<int, float> Yaw = i => 0f;
            public Func<int, Vector3> Shift = i => Vector3.zero;
            public int Frames = 40, LastMove;
        }

        public override IEnumerator Run()
        {
            string input = WeatherPass.Argument("-in");
            TemporalRig rig = input != null && File.Exists(Path.Combine(input, "jobs.json")) ? EdgeKit.Rig(input) : null;
            Sink.Flag("temporal_available", rig != null);
            if (rig == null) yield break;
            LayersJobFile file = JsonUtility.FromJson<LayersJobFile>(File.ReadAllText(Path.Combine(input, "jobs.json")));
            var rt = new RenderTexture(W, H, 24, RenderTextureFormat.DefaultHDR);
            rt.Create();
            Cam.targetTexture = rt;
            int pixels = W * H, bw = EdgeMath.Blocks(W), bh = EdgeMath.Blocks(H);
            var img = new float[pixels];
            var tmpAlpha = new float[pixels];
            var scenarios = new List<Scenario>
            {
                new Scenario { Name = "turn2", Frames = 50, Yaw = i => Mathf.Clamp(i - 6, 0, 22) * 2f, LastMove = 28 },
                new Scenario { Name = "turn12", Frames = 40, Yaw = i => Mathf.Clamp(i - 6, 0, 10) * 12f, LastMove = 16 },
                new Scenario { Name = "fly100", Frames = 36, Shift = i => new Vector3(1.667f * Mathf.Max(i - 6, 0), 0f, 0f), LastMove = 35 },
                new Scenario { Name = "cut", Frames = 36 },
            };
            var variants = new List<EdgeVariant>();
            foreach (EdgeVariant v in EdgeVariant.Table(false)) if (v.Name == "full" || v.Name == "u4r" || v.Name == "u2r" || v.Name == "e") variants.Add(v);
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
                    rig.Frame.RefineBudget = 0.5f;
                    var refDisplay = new float[sc.Frames][];
                    var refAlpha = new float[sc.Frames][];
                    for (int i = 0; i < sc.Frames; i++)
                    {
                        rig.Pose(Cam, sc.Yaw(i), sc.Shift(i));
                        var sum = new float[pixels];
                        var al = new float[pixels];
                        for (int k = 0; k < RefFrames; k++)
                        {
                            rig.Recon.Reset();
                            EdgeKit.Render(Cam, rig,full, 9000 + i * 7 + k);
                            EdgeKit.Read(rig.Recon.Output, ref readback, img, tmpAlpha);
                            for (int p = 0; p < pixels; p++) { sum[p] += img[p] / RefFrames; al[p] += tmpAlpha[p] / RefFrames; }
                        }
                        refDisplay[i] = sum;
                        refAlpha[i] = al;
                        if (i % 6 == 5) yield return null;
                    }
                    foreach (EdgeVariant v in variants)
                    {
                        if (clock.Elapsed.TotalMinutes > 16.0) { Sink.Flag("temporal_completed", false); yield break; }
                        rig.Frame.RefineBudget = v.Refine;
                        ReconSettings s = v.Settings();
                        rig.Recon.Reset();
                        var moving = new List<double>();
                        var trail = new List<double>();
                        var shimmer = new List<double>();
                        var cover = new List<double>();
                        var errs = new List<double>();
                        float[] previous = null;
                        var outAlpha = new float[pixels];
                        string key = "edget." + jobId + "." + sc.Name + "." + v.Name;
                        for (int i = 0; i < sc.Frames; i++)
                        {
                            rig.Pose(Cam, sc.Yaw(i), sc.Shift(i));
                            // The cut scenario starts the history afresh in the middle: the picture jumps to the other pose of the scene's end.
                            if (sc.Name == "cut" && i == 8) rig.Recon.Reset();
                            EdgeKit.Render(Cam, rig,s, 100 + i);
                            var d = new float[pixels];
                            EdgeKit.Read(rig.Recon.Output, ref readback, d, outAlpha);
                            double err = 0, trailCount = 0, region = 0, lum = 0, flicker = 0;
                            for (int p = 0; p < pixels; p++)
                            {
                                if (refAlpha[i][p] < 0.02f && outAlpha[p] < 0.02f) continue;
                                region += 1;
                                double e = Math.Abs(d[p] - refDisplay[i][p]);
                                err += e;
                                lum += refDisplay[i][p];
                                if (e > 0.04) trailCount += 1;
                                if (previous != null) flicker += Math.Abs(d[p] - previous[p]);
                            }
                            if (region > 0)
                            {
                                err /= region;
                                errs.Add(err);
                                bool isMoving = sc.LastMove > 0 && i > 6 && i <= sc.LastMove;
                                bool settled = sc.LastMove == 0 ? i >= 20 : i > sc.LastMove + 10;
                                if (isMoving) { moving.Add(err); trail.Add(trailCount / region); }
                                else if (settled && previous != null) shimmer.Add(flicker / region);
                            }
                            if (v.Edge != null && v.Edge.Enabled && (sc.LastMove == 0 ? i >= 8 : (i > 6 && i <= sc.LastMove)))
                            {
                                // The share of the contour pixels of the reference that lie in a flagged block (the mask's cover and so its lag).
                                bool[] mask = EdgeKit.ReadMask(rig.Recon.Mask, bw, bh);
                                int contour = 0, hit = 0;
                                for (int y = 0; y < H; y++)
                                    for (int x = 0; x < W; x++)
                                        if (refAlpha[i][y * W + x] > 0.05f && refAlpha[i][y * W + x] < 0.95f) { contour++; if (EdgeKit.Flagged(mask, bw, x, y)) hit++; }
                                if (contour > 0) cover.Add((double)hit / contour);
                                if (sc.Name == "cut" && i >= 8 && i <= 12) Sink.Info(key + ".cover_f" + (i - 8), contour > 0 ? (double)hit / contour : double.NaN);
                            }
                            previous = d;
                        }
                        if (sc.Name == "cut")
                        {
                            // Frames after the cut until the error is within 15 percent of the plateau of the last ten frames.
                            double plateau = 0;
                            int c = 0;
                            for (int i = errs.Count - 10; i < errs.Count; i++) { plateau += errs[i]; c++; }
                            plateau /= Math.Max(c, 1);
                            int converged = -1;
                            for (int i = 8; i < errs.Count; i++) if (errs[i] <= 1.15 * plateau + 1e-4) { converged = i - 8; break; }
                            Sink.Info(key + ".converge_frames", converged);
                            Sink.Info(key + ".plateau", plateau);
                        }
                        else
                        {
                            Sink.Info(key + ".ghost_move", EdgeKit.Mean(moving));
                            Sink.Info(key + ".trail_px", EdgeKit.Mean(trail));
                            Sink.Info(key + ".shimmer", EdgeKit.Mean(shimmer));
                        }
                        if (cover.Count > 0)
                        {
                            double min = 1.0;
                            foreach (double cv in cover) min = Math.Min(min, cv);
                            Sink.Info(key + ".cover_mean", EdgeKit.Mean(cover));
                            Sink.Info(key + ".cover_min", min);
                        }
                        yield return null;
                    }
                }
            }
            rig.Dispose();
            Cam.targetTexture = Target;
            rt.Release();
            Sink.Flag("temporal_completed", true);
        }
    }
}
