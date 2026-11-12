// Scenes of the weather player for the contour gate of the entry refinement (package T14), linked through the manifests of T01, T05 and T12 (the layer
// core, the temporal rig and EdgeKit):
//   -scene refine-cost -in <T01 inputs>     ms of the pass per view for the refinement off, on everywhere and gated (timing run, exclusive)
//   -scene refine-quality -in <T01 inputs>  per view the error against the reference (the mean of four full marches with the refinement everywhere) in
//                                           the band around the contour, in the thick interior and in the empty sky, plus the pictures
//                                           t14-<view>-before/after/aid-error/aid-refined.png
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using ReDefinition.Atmospheres;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.WeatherPlayer
{
    // One way of marching the entry of a cloud, as the tables name it.
    internal sealed class RefineVariant
    {
        public string Name;
        public ReconMode Mode = ReconMode.Full;
        public int Block = 4;
        public float Refine, Gate, GateFirst;

        internal ReconSettings Settings()
        {
            return new ReconSettings { Mode = Mode, Block = Block };
        }

        // full: the refinement on every ray (the reference's own pass); none: no refinement; g05, g12, g25: the contour gate with the alpha step 0.05, 0.12, 0.25
        // of the 2 x 2 quad; g12f: 0.12 together with the relative first-hit step 0.05; u4*: the same in the 4 x 4 block mode of T05.
        internal static List<RefineVariant> Table(string sweep)
        {
            var list = new List<RefineVariant>
            {
                new RefineVariant { Name = "full", Refine = 0.5f },
                new RefineVariant { Name = "none", Refine = 0f },
                new RefineVariant { Name = "u4full", Mode = ReconMode.Block, Refine = 0.5f },
                new RefineVariant { Name = "u4none", Mode = ReconMode.Block, Refine = 0f },
            };
            // Sweep 1 (the first of the three allowed for the constant group): the alpha step of the gate.
            if (sweep == "1")
            {
                list.Add(new RefineVariant { Name = "g05", Refine = 0.5f, Gate = 0.05f });
                list.Add(new RefineVariant { Name = "g12", Refine = 0.5f, Gate = 0.12f });
                list.Add(new RefineVariant { Name = "g25", Refine = 0.5f, Gate = 0.25f });
                list.Add(new RefineVariant { Name = "g12f", Refine = 0.5f, Gate = 0.12f, GateFirst = 0.05f });
                list.Add(new RefineVariant { Name = "u4g12", Mode = ReconMode.Block, Refine = 0.5f, Gate = 0.12f });
            }
            else if (sweep == "2")
            {
                // Sweep 2: sweep 1 showed that the alpha step alone flags nothing the refinement fixes (the unrefined alpha is smooth; the refinement's gain is
                // the quantised first hit), so the alpha step stays at 0.12 and the relative first-hit step is swept: 0.02, 0.01, 0.005.
                list.Add(new RefineVariant { Name = "f02", Refine = 0.5f, Gate = 0.12f, GateFirst = 0.02f });
                list.Add(new RefineVariant { Name = "f01", Refine = 0.5f, Gate = 0.12f, GateFirst = 0.01f });
                list.Add(new RefineVariant { Name = "f005", Refine = 0.5f, Gate = 0.12f, GateFirst = 0.005f });
                list.Add(new RefineVariant { Name = "u4f01", Mode = ReconMode.Block, Refine = 0.5f, Gate = 0.12f, GateFirst = 0.01f });
            }
            else if (sweep == "3")
            {
                list.Add(new RefineVariant { Name = "gA", Refine = 0.5f, Gate = Tune("T14_GA", 0.02f) });
                list.Add(new RefineVariant { Name = "gB", Refine = 0.5f, Gate = Tune("T14_GB", 0.08f) });
                list.Add(new RefineVariant { Name = "u4gB", Mode = ReconMode.Block, Refine = 0.5f, Gate = Tune("T14_GB", 0.08f) });
            }
            return list;
        }

        // A value of the sweep from the environment (so that a later sweep needs no rebuild of the player).
        private static float Tune(string name, float fallback)
        {
            string text = Environment.GetEnvironmentVariable(name);
            float v;
            return text != null && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v) ? v : fallback;
        }
    }

    [WeatherScene("refine-cost")]
    public sealed class RefineCostScene : WeatherSceneBase
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
            var cb = new CommandBuffer { name = "T14 cost" };
            Cam.AddCommandBuffer(CameraEvent.AfterSkybox, cb);
            var segments = new float[8];
            var clock = System.Diagnostics.Stopwatch.StartNew();
            const int coarse = WeatherGpuTimer.SlabMarched, recon = WeatherGpuTimer.CurtainEnd;
            Action<CommandBuffer> mStart = c => WeatherGpuTimer.Mark(c, WeatherGpuTimer.SlabStart), mCoarse = c => WeatherGpuTimer.Mark(c, coarse);
            Action<CommandBuffer> mEnd = c => WeatherGpuTimer.Mark(c, recon);
            rig.Recon.MarkMask = c => WeatherGpuTimer.Mark(c, WeatherGpuTimer.CurtainStart);
            rig.Recon.MarkEdge = c => WeatherGpuTimer.Mark(c, WeatherGpuTimer.SlabComposited);
            List<RefineVariant> variants = RefineVariant.Table(WeatherPass.Argument("-sweep"));
            var rt = new RenderTexture(1707, 960, 24, RenderTextureFormat.DefaultHDR) { name = "T14 cost target" };
            rt.Create();
            Cam.targetTexture = rt;
            foreach (LayersJob job in file.jobs)
            {
                if (!job.id.StartsWith("V-")) continue;
                rig.Load(job, input);
                LayerFrame frame = rig.Frame;
                frame.UseSceneDepth = true;
                frame.TransmittanceGrowth = 2f;
                rig.Pose(Cam, 0f, Vector3.zero);
                // Two passes over the table: the second repeats the first so that the medians carry the run-to-run noise (rows report both).
                for (int repeat = 0; repeat < 2; repeat++)
                    foreach (RefineVariant v in variants)
                    {
                        if (clock.Elapsed.TotalMinutes > 17.0) { Sink.Flag("cost_run_completed", false); yield break; }
                        frame.RefineBudget = v.Refine;
                        frame.RefineGate = v.Gate;
                        frame.RefineGateFirst = v.GateFirst;
                        ReconSettings settings = v.Settings();
                        rig.Recon.Reset();
                        var cCoarse = new List<double>();
                        var cRecon = new List<double>();
                        int guard = 0, n = 0;
                        while (cCoarse.Count < 60 && guard++ < 1200)
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
                            if (a >= 0f && r >= 0f) { cCoarse.Add(a); cRecon.Add(r); }
                        }
                        string label = job.id + "." + v.Name + ".1707x960";
                        double tc = Median(cCoarse), tr = Median(cRecon);
                        string tag = repeat == 0 ? "refcost" : "refcostb";
                        Sink.Info(tag + ".march." + label, tc);
                        Sink.Info(tag + ".recon." + label, tr);
                        Sink.Info(tag + ".total." + label, tc + tr);
                    }
            }
            Cam.targetTexture = Target;
            rt.Release();
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

    [WeatherScene("refine-quality")]
    public sealed class RefineQualityScene : WeatherSceneBase
    {
        private const int W = 640, H = 360, RefFrames = 4, Settle = 28, Band = 6;
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
            string pictures = Path.Combine(Output, "refine");
            Directory.CreateDirectory(pictures);
            int pixels = W * H;
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
                rig.Frame.RefineBudget = 0.5f;
                rig.Frame.RefineGate = 0f;
                rig.Frame.RefineGateFirst = 0f;
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
                Sink.Info("refq." + view + ".ref.band_share", (double)nBand / pixels);
                Sink.Info("refq." + view + ".ref.interior_share", (double)nInterior / pixels);
                float[] none = null;
                var shown = new Dictionary<string, float[]>();
                foreach (RefineVariant v in RefineVariant.Table(sweep))
                {
                    rig.Frame.RefineBudget = v.Refine;
                    rig.Frame.RefineGate = v.Gate;
                    rig.Frame.RefineGateFirst = v.GateFirst;
                    ReconSettings s = v.Settings();
                    rig.Recon.Reset();
                    var d = new float[pixels];
                    var a = new float[pixels];
                    int frames = v.Mode == ReconMode.Full ? 1 : Settle;
                    for (int i = 0; i < frames; i++)
                    {
                        // The variants of one mode use the same frame numbers, so that the per-pixel dither is the same and only the refinement differs.
                        EdgeKit.Render(Cam, rig, s, 100 + i);
                        if (i == frames - 1) EdgeKit.Read(rig.Recon.Output, ref readback, d, a);
                        if (i % 6 == 5) yield return null;
                    }
                    string key = "refq." + view + "." + v.Name;
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
                    shown[v.Name] = d;
                    if (v.Name == "none") none = d;
                    yield return null;
                }
                // Pictures: before = no refinement, after = the first gated variant of the table, the error aid (red: none against the reference, green: gated against
                // it, x8) and the refined aid (where gated and none differ: the rays the gate marched again).
                string gated = null;
                foreach (RefineVariant v in RefineVariant.Table(sweep)) if (gated == null && v.Gate > 0f && v.Mode == ReconMode.Full) gated = v.Name;
                if (none != null && gated != null)
                {
                    float[] g = shown[gated];
                    EdgeKit.SaveGrey(Path.Combine(pictures, "t14-" + view + "-before.png"), none, W, H);
                    EdgeKit.SaveGrey(Path.Combine(pictures, "t14-" + view + "-after.png"), g, W, H);
                    EdgeKit.SaveGrey(Path.Combine(pictures, "t14-" + view + "-reference.png"), refDisplay, W, H);
                    var errPx = new Color[pixels];
                    var refinedPx = new Color[pixels];
                    for (int p = 0; p < pixels; p++)
                    {
                        errPx[p] = new Color(Mathf.Clamp01(Mathf.Abs(none[p] - refDisplay[p]) * 8f), Mathf.Clamp01(Mathf.Abs(g[p] - refDisplay[p]) * 8f), 0f);
                        bool diff = Mathf.Abs(g[p] - none[p]) > 1e-4f;
                        refinedPx[p] = diff ? new Color(1f, 0.85f, 0.2f) * (0.45f + 0.55f * g[p]) : new Color(0.1f, 0.2f, 0.45f) * (0.45f + 0.55f * g[p]);
                    }
                    EdgeKit.SaveColour(Path.Combine(pictures, "t14-" + view + "-aid-error.png"), errPx, W, H);
                    EdgeKit.SaveColour(Path.Combine(pictures, "t14-" + view + "-aid-refined.png"), refinedPx, W, H);
                }
                yield return null;
            }
            rig.Dispose();
            Cam.targetTexture = Target;
            rt.Release();
            Sink.Flag("quality_completed", true);
        }
    }
}
