// Scenes of the weather player for the visibility culling and the distance LOD of the own clouds (package T16), linked through the manifests of T01 and T05
// (the layer core and the temporal rig) and T16:
//   -scene cull-cost -in <T16 inputs>      ms of the cloud pass per view, size (1707x960, 2560x1440) and variant, in the full mode and the 4 x 4 block mode,
//                                          split into the tile pass, the march and the reconstruction (timing run, exclusive)
//   -scene cull-quality -in <T16 inputs>   per view the error of each variant against the reference (the mean of four full marches without any switch) in the band
//                                          around the contour, the thick interior and the empty sky, the A/B difference to the same frame without the switch, the
//                                          share of tiles and layers dropped, and the pictures t16-<view>-before / after / aid-skip / aid-lod / aid-diff
//   -scene cull-sweep -in <T16 inputs>     the camera sweeps (altitude, layer crossing, a turn of 360 degrees, the distance): the largest frame-to-frame change of
//                                          the picture's luminance that the switch adds to the baseline's own
//   -scene cull-levels -in <T16 inputs>    the 4 x 4 block mode through two turns with and without the switches: ghosting, trail and shimmer against the full
//                                          march of the same pose
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using ReDefinition.Atmospheres;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.WeatherPlayer
{
    // One setting of the culling and the distance LOD, as the tables name it.
    internal sealed class CullVariant
    {
        public string Name, Baseline = "off";
        public bool Skip, Closed, Cut, Growth, Tap, Detail, Haze, Mean;
        public float CapKm;
        public int Tests = 7;
        public ReconMode Mode = ReconMode.Full;
        public int Block = 4;

        internal ReconSettings Settings()
        {
            return new ReconSettings { Mode = Mode, Block = Block };
        }

        internal void Apply(LayerFrame f)
        {
            CullSettings c = f.Cull;
            c.SkipEmpty = Skip;
            c.SkipClosed = Closed;
            c.DistanceCut = Cut;
            c.StepGrowth = Growth;
            c.TapLod = Tap;
            c.DetailLod = Detail;
            c.CapDistanceM = CapKm * 1000f;
            c.Tests = Tests;
            f.Far.Haze = Haze || Cut;
            // A measurement lever: the Earth coefficients leave a layer at 60 km with a weight above 0.5, so the distance cut could never act; the haze rows use
            // eight times the optical depth (a dusty or thick atmosphere), the same for the baseline and the switch.
            f.Far.HazeStrength = Haze || Cut ? 8f : 1f;
            f.Far.ShapeMean = Mean || Detail;
        }

        private static CullVariant Make(string name, string baseline, bool block, Action<CullVariant> set)
        {
            var v = new CullVariant { Name = name, Baseline = baseline };
            if (block) v.Mode = ReconMode.Block;
            if (set != null) set(v);
            return v;
        }

        // off / haze / mean / hm: the baselines (nothing, the aerial perspective, the far shape mean, both); skip, skipG, skipT, skipM: the empty-sky skip with all
        // tests and with the shells, the terrain and the maps alone; closed; cut (needs the haze, baseline haze); growth; tap; detail (needs the shape mean,
        // baseline mean); cap; all (baseline hm). The u4 variants are the same in the 4 x 4 block mode (cloudResolution 4), whose baseline is u4off.
        internal static List<CullVariant> Table()
        {
            var list = new List<CullVariant>();
            foreach (bool block in new[] { false, true })
            {
                string p = block ? "u4" : "";
                list.Add(Make(p + "off", p + "off", block, null));
                list.Add(Make(p + "haze", p + "off", block, v => v.Haze = true));
                list.Add(Make(p + "mean", p + "off", block, v => v.Mean = true));
                list.Add(Make(p + "hm", p + "off", block, v => { v.Haze = true; v.Mean = true; }));
                list.Add(Make(p + "skip", p + "off", block, v => v.Skip = true));
                list.Add(Make(p + "skipG", p + "off", block, v => { v.Skip = true; v.Tests = 1; }));
                list.Add(Make(p + "skipT", p + "off", block, v => { v.Skip = true; v.Tests = 2; }));
                list.Add(Make(p + "skipM", p + "off", block, v => { v.Skip = true; v.Tests = 4; }));
                list.Add(Make(p + "closed", p + "off", block, v => { v.Closed = true; }));
                list.Add(Make(p + "cut", p + "haze", block, v => v.Cut = true));
                list.Add(Make(p + "growth", p + "off", block, v => v.Growth = true));
                list.Add(Make(p + "tap", p + "off", block, v => v.Tap = true));
                list.Add(Make(p + "detail", p + "mean", block, v => v.Detail = true));
                list.Add(Make(p + "cap", p + "off", block, v => v.CapKm = 100f));
                list.Add(Make(p + "all", p + "hm", block, v =>
                {
                    v.Skip = v.Closed = v.Cut = v.Growth = v.Tap = v.Detail = true;
                    v.CapKm = 100f;
                }));
            }
            return list;
        }

        internal static CullVariant Named(string name)
        {
            foreach (CullVariant v in Table()) if (v.Name == name) return v;
            return null;
        }
    }

    internal static class CullKit
    {
        internal const string Views = "V-ground V-inside V-above V-orbit-edge V-sky3 V-horizon V-stack-below";

        internal static bool IsView(string id)
        {
            return Array.IndexOf(Views.Split(' '), id) >= 0;
        }

        // The variants the 2560x1440 pass measures (the others only at 1707x960, to stay inside the time limit).
        internal static bool Large(string name)
        {
            return name == "off" || name == "hm" || name == "skip" || name == "all" || name == "u4off" || name == "u4hm" || name == "u4skip" || name == "u4all";
        }

        internal static double Median(List<double> values)
        {
            if (values.Count == 0) return double.NaN;
            double[] sorted = values.ToArray();
            Array.Sort(sorted);
            return sorted[sorted.Length / 2];
        }

        // The raw colours of a render texture.
        internal static Color[] ReadColours(RenderTexture rt, ref Texture2D readback)
        {
            if (readback == null || readback.width != rt.width || readback.height != rt.height) readback = new Texture2D(rt.width, rt.height, TextureFormat.RGBAHalf, false, true);
            RenderTexture.active = rt;
            readback.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0, false);
            RenderTexture.active = null;
            return readback.GetPixels();
        }

        // The natural picture as colour: the premultiplied radiance over the sky colour, tone mapped.
        internal static void SaveNatural(string path, Color[] px, int w, int h)
        {
            var outPx = new Color[px.Length];
            for (int i = 0; i < px.Length; i++)
            {
                Color c = px[i];
                float r = c.r + (1f - c.a) * EdgeKit.Sky.r, g = c.g + (1f - c.a) * EdgeKit.Sky.g, b = c.b + (1f - c.a) * EdgeKit.Sky.b;
                outPx[i] = new Color(r / (1f + r), g / (1f + g), b / (1f + b));
            }
            EdgeKit.SaveColour(path, outPx, w, h);
        }

        internal static void SaveAid(string path, Color[] px, int w, int h)
        {
            var outPx = new Color[px.Length];
            for (int i = 0; i < px.Length; i++) outPx[i] = new Color(Mathf.Clamp01(px[i].r), Mathf.Clamp01(px[i].g), Mathf.Clamp01(px[i].b));
            EdgeKit.SaveColour(path, outPx, w, h);
        }

        // The share of tiles that march no layer, and of the layer-tile pairs dropped, by reason, from the tile map of the last render.
        internal static void TileStats(LayerRenderer renderer, int layers, out double emptyTiles, out double droppedPairs, out double byMaps, out double byTerrain,
                                       out double byShell, out double byClosed)
        {
            emptyTiles = droppedPairs = byMaps = byTerrain = byShell = byClosed = double.NaN;
            RenderTexture tiles = renderer.Tiles.Tiles;
            if (tiles == null) return;
            var tex = new Texture2D(tiles.width, tiles.height, TextureFormat.RGBAHalf, false, true);
            RenderTexture.active = tiles;
            tex.ReadPixels(new Rect(0, 0, tiles.width, tiles.height), 0, 0, false);
            RenderTexture.active = null;
            Color[] px = tex.GetPixels();
            UnityEngine.Object.Destroy(tex);
            int n = px.Length, empty = 0, dropped = 0, maps = 0, terrain = 0, shell = 0, closed = 0;
            foreach (Color c in px)
            {
                int bits = Mathf.RoundToInt(c.r), aid = Mathf.RoundToInt(c.g);
                if (bits == 0) empty++;
                for (int l = 0; l < layers; l++) if ((bits & (1 << l)) == 0) dropped++;
                maps += aid & 1;
                terrain += (aid >> 1) & 1;
                shell += (aid >> 2) & 1;
                closed += (aid >> 3) & 1;
            }
            emptyTiles = (double)empty / n;
            droppedPairs = (double)dropped / (n * (double)layers);
            byMaps = (double)maps / n;
            byTerrain = (double)terrain / n;
            byShell = (double)shell / n;
            byClosed = (double)closed / n;
        }
    }

    [WeatherScene("cull-cost")]
    public sealed class CullCostScene : WeatherSceneBase
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
            var cb = new CommandBuffer { name = "T16 cost" };
            Cam.AddCommandBuffer(CameraEvent.AfterSkybox, cb);
            var segments = new float[8];
            var clock = System.Diagnostics.Stopwatch.StartNew();
            const int march = WeatherGpuTimer.SlabMarched, tile = WeatherGpuTimer.CurtainStart, recon = WeatherGpuTimer.CurtainEnd;
            Action<CommandBuffer> mStart = c => WeatherGpuTimer.Mark(c, WeatherGpuTimer.SlabStart), mMarch = c => WeatherGpuTimer.Mark(c, march);
            Action<CommandBuffer> mEnd = c => WeatherGpuTimer.Mark(c, recon);
            rig.Recon.MarkCull = null;
            rig.Recon.MarkCullEnd = c => WeatherGpuTimer.Mark(c, tile);
            List<CullVariant> variants = CullVariant.Table();
            var sizes = new[] { new Vector2Int(1707, 960), new Vector2Int(2560, 1440) };
            foreach (Vector2Int size in sizes)
            {
                var rt = new RenderTexture(size.x, size.y, 24, RenderTextureFormat.DefaultHDR) { name = "T16 cost target" };
                rt.Create();
                Cam.targetTexture = rt;
                string sz = size.x + "x" + size.y;
                foreach (LayersJob job in file.jobs)
                {
                    if (!CullKit.IsView(job.id)) continue;
                    rig.Load(job, input);
                    LayerFrame frame = rig.Frame;
                    frame.UseSceneDepth = true;
                    frame.TransmittanceGrowth = 2f;
                    rig.Pose(Cam, 0f, Vector3.zero);
                    bool refreshLogged = false;
                    // Two passes over the table: the second repeats the first so that the medians carry the run-to-run noise (the tables report both).
                    for (int repeat = 0; repeat < 2; repeat++)
                        foreach (CullVariant v in variants)
                        {
                            if (clock.Elapsed.TotalMinutes > 17.0) { Sink.Flag("cost_run_completed", false); Sink.Info("cullcost.stopped_at." + job.id + "." + sz, 1); yield break; }
                            if (size.x > 1707 && !CullKit.Large(v.Name)) continue;
                            v.Apply(frame);
                            ReconSettings settings = v.Settings();
                            rig.Recon.Reset();
                            var cTile = new List<double>();
                            var cMarch = new List<double>();
                            var cRecon = new List<double>();
                            int guard = 0, n = 0;
                            while (cMarch.Count < 40 && guard++ < 1200)
                            {
                                cb.Clear();
                                cb.SetGlobalTexture(Shader.PropertyToID("_SceneDepth"), depthId);
                                rig.Recon.Record(cb, rig.Renderer, frame, Cam, settings, FrameIndex, mStart, mMarch, null);
                                rig.Recon.Composite(cb, rig.Renderer, BuiltinRenderTextureType.CameraTarget, mEnd);
                                yield return Frames(1);
                                if (!refreshLogged && rig.Renderer.Bounds.LastRebuilt > 0)
                                {
                                    refreshLogged = true;
                                    Sink.Info("cullcost.refresh_ms." + job.id + "." + sz, rig.Renderer.Bounds.LastMs);
                                    Sink.Info("cullcost.refresh_layers." + job.id + "." + sz, rig.Renderer.Bounds.LastRebuilt);
                                }
                                if (n++ < 20) continue;
                                float total;
                                int number;
                                if (!WeatherGpuTimer.Read(segments, out total, out number)) continue;
                                float a = segments[march - WeatherGpuTimer.SlabStart], r = segments[recon - WeatherGpuTimer.SlabStart], tl = segments[tile - WeatherGpuTimer.SlabStart];
                                bool tiled = v.Skip || v.Closed;
                                if (a >= 0f && r >= 0f && (!tiled || tl >= 0f)) { cMarch.Add(a); cRecon.Add(r); cTile.Add(tiled ? tl : 0f); }
                            }
                            string label = job.id + "." + v.Name + "." + sz;
                            double tm = CullKit.Median(cMarch), tt = CullKit.Median(cTile), tr = CullKit.Median(cRecon);
                            string tag = repeat == 0 ? "cullcost" : "cullcostb";
                            Sink.Info(tag + ".total." + label, tm + tt + tr);
                            Sink.Info(tag + ".march." + label, tm);
                            Sink.Info(tag + ".tile." + label, tt);
                        }
                }
                Cam.targetTexture = Target;
                rt.Release();
            }
            Cam.RemoveCommandBuffer(CameraEvent.AfterSkybox, cb);
            rig.Dispose();
            Sink.Flag("cost_run_completed", true);
        }
    }

    [WeatherScene("cull-quality")]
    public sealed class CullQualityScene : WeatherSceneBase
    {
        private const int W = 640, H = 360, RefFrames = 4, Settle = 28, Band = 6;
        private Texture2D readback;

        public override IEnumerator Run()
        {
            string input = WeatherPass.Argument("-in");
            TemporalRig rig = input != null && File.Exists(Path.Combine(input, "jobs.json")) ? EdgeKit.Rig(input) : null;
            Sink.Flag("quality_available", rig != null);
            if (rig == null) yield break;
            LayersJobFile file = JsonUtility.FromJson<LayersJobFile>(File.ReadAllText(Path.Combine(input, "jobs.json")));
            var rt = new RenderTexture(W, H, 24, RenderTextureFormat.DefaultHDR);
            rt.Create();
            Cam.targetTexture = rt;
            string pictures = Path.Combine(Output, "cull16");
            Directory.CreateDirectory(pictures);
            int pixels = W * H;
            var refDisplay = new float[pixels];
            var refAlpha = new float[pixels];
            var tmpAlpha = new float[pixels];
            var img = new float[pixels];
            List<CullVariant> table = CullVariant.Table();
            foreach (string viewId in CullKit.Views.Split(' '))
            {
                LayersJob job = null;
                foreach (LayersJob j in file.jobs) if (j.id == viewId) job = j;
                string view = viewId.Substring(2);
                if (job == null) { Sink.Flag("quality_job_" + view, false); continue; }
                rig.Load(job, input);
                rig.Frame.UseProductCost();
                rig.Pose(Cam, 0f, Vector3.zero);
                int layerCount = job.layers.Length;
                var full = new ReconSettings { Mode = ReconMode.Full };
                // The reference: the mean of four full marches without any switch (the baselines with their own haze or shape mean are references of their own).
                var refs = new Dictionary<string, KeyValuePair<float[], float[]>>();
                foreach (string baseName in new[] { "off", "haze", "mean", "hm" })
                {
                    CullVariant.Named(baseName).Apply(rig.Frame);
                    var rd = new float[pixels];
                    var ra = new float[pixels];
                    for (int k = 0; k < RefFrames; k++)
                    {
                        rig.Recon.Reset();
                        EdgeKit.Render(Cam, rig, full, 9000 + k * 7);
                        EdgeKit.Read(rig.Recon.Output, ref readback, img, tmpAlpha);
                        for (int p = 0; p < pixels; p++) { rd[p] += img[p] / RefFrames; ra[p] += tmpAlpha[p] / RefFrames; }
                    }
                    refs[baseName] = new KeyValuePair<float[], float[]>(rd, ra);
                    yield return null;
                }
                Array.Copy(refs["off"].Key, refDisplay, pixels);
                Array.Copy(refs["off"].Value, refAlpha, pixels);
                bool[] inBand, interior, empty;
                EdgeKit.Classes(refAlpha, W, H, Band, out inBand, out interior, out empty);
                int nBand = 0, nInterior = 0, nEmpty = 0;
                for (int p = 0; p < pixels; p++) { if (inBand[p]) nBand++; if (interior[p]) nInterior++; if (empty[p]) nEmpty++; }
                Sink.Info("cullq." + view + ".ref.band_share", (double)nBand / pixels);
                Sink.Info("cullq." + view + ".ref.interior_share", (double)nInterior / pixels);
                Sink.Info("cullq." + view + ".ref.empty_share", (double)nEmpty / pixels);
                var shown = new Dictionary<string, float[]>();
                var shownA = new Dictionary<string, float[]>();
                foreach (CullVariant v in table)
                {
                    v.Apply(rig.Frame);
                    ReconSettings s = v.Settings();
                    rig.Recon.Reset();
                    var d = new float[pixels];
                    var a = new float[pixels];
                    var prev = new float[pixels];
                    var flicker = new List<double>();
                    int frames = v.Mode == ReconMode.Full ? 1 : Settle;
                    for (int i = 0; i < frames; i++)
                    {
                        // The same frame numbers for every variant of a mode, so that the per-pixel dither is the same and only the switches differ.
                        EdgeKit.Render(Cam, rig, s, v.Mode == ReconMode.Full ? 9000 : 100 + i);
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
                    string key = "cullq." + view + "." + v.Name;
                    // The reference of the variant is the mean of the baseline without the switches of its own (the same haze and shape mean).
                    string refName = v.Haze || v.Cut ? (v.Mean || v.Detail ? "hm" : "haze") : (v.Mean || v.Detail ? "mean" : "off");
                    float[] rd2 = refs[refName].Key, ra2 = refs[refName].Value;
                    double sb = 0, si = 0, ci = 0, lumI = 0, leak = 0, sAll = 0, lost = 0, lostMax = 0;
                    float[] baseAlpha = v.Mode == ReconMode.Full && v.Name != v.Baseline && shownA.ContainsKey(v.Baseline) ? shownA[v.Baseline] : null;
                    for (int p = 0; p < pixels; p++)
                    {
                        double e = d[p] - rd2[p];
                        sAll += Math.Abs(e);
                        if (inBand[p]) sb += e * e;
                        if (interior[p]) { si += Math.Abs(e); lumI += rd2[p]; ci += 1; }
                        if (empty[p] && a[p] > 0.001f) leak += 1;
                        // The alpha lost against the SAME frame of the baseline (one dithered frame against a mean would count the dither).
                        double loss = baseAlpha != null ? baseAlpha[p] - a[p] : 0.0;
                        if (loss > 0.01) lost += 1;
                        if (loss > lostMax) lostMax = loss;
                    }
                    Sink.Info(key + ".band_rms", nBand > 0 ? Math.Sqrt(sb / nBand) : double.NaN);
                    Sink.Info(key + ".interior_err", ci > 0 ? si / lumI : double.NaN);
                    Sink.Info(key + ".empty_leak", nEmpty > 0 ? leak / nEmpty : 0.0);
                    Sink.Info(key + ".mean_abs", sAll / pixels);
                    Sink.Info(key + ".cloud_lost", lost / pixels);
                    Sink.Info(key + ".cloud_lost_max", lostMax);
                    if (flicker.Count > 0) Sink.Info(key + ".band_flicker", EdgeKit.Mean(flicker));
                    if (v.Skip || v.Closed)
                    {
                        double et, dp, bm, bt, bs, bc;
                        CullKit.TileStats(rig.Renderer, layerCount, out et, out dp, out bm, out bt, out bs, out bc);
                        Sink.Info(key + ".tiles_empty", et);
                        Sink.Info(key + ".pairs_dropped", dp);
                        Sink.Info(key + ".tiles_by_maps", bm);
                        Sink.Info(key + ".tiles_by_terrain", bt);
                        Sink.Info(key + ".tiles_by_shell", bs);
                        Sink.Info(key + ".tiles_by_closed", bc);
                    }
                    shown[v.Name] = d;
                    shownA[v.Name] = a;
                    // The A/B difference to the single frame of the baseline (full mode): max and share of pixels changed above 0.5 percent display.
                    if (v.Mode == ReconMode.Full && v.Name != v.Baseline && shown.ContainsKey(v.Baseline))
                    {
                        double changed = 0, maxDiff = 0, sum = 0;
                        float[] b = shown[v.Baseline];
                        for (int p = 0; p < pixels; p++)
                        {
                            double e = Math.Abs(d[p] - b[p]);
                            sum += e;
                            if (e > 0.005) changed += 1;
                            if (e > maxDiff) maxDiff = e;
                        }
                        Sink.Info(key + ".ab_changed", changed / pixels);
                        Sink.Info(key + ".ab_max", maxDiff);
                        Sink.Info(key + ".ab_mean", sum / pixels);
                    }
                    yield return null;
                }
                // The pictures. before: the baseline; after: every switch on; aid-skip: the tile map; aid-lod: the LOD map; aid-diff: the difference to the baseline x8.
                CullVariant off = CullVariant.Named("off"), all = CullVariant.Named("all"), skip = CullVariant.Named("skip");
                off.Apply(rig.Frame);
                rig.Recon.Reset();
                EdgeKit.Render(Cam, rig, full, 9000);
                Color[] before = CullKit.ReadColours(rig.Recon.Output, ref readback);
                CullKit.SaveNatural(Path.Combine(pictures, "t16-" + view + "-before.png"), before, W, H);
                all.Apply(rig.Frame);
                rig.Recon.Reset();
                EdgeKit.Render(Cam, rig, full, 9000);
                CullKit.SaveNatural(Path.Combine(pictures, "t16-" + view + "-after.png"), CullKit.ReadColours(rig.Recon.Output, ref readback), W, H);
                rig.Frame.AidMode = 6;
                rig.Recon.Reset();
                EdgeKit.Render(Cam, rig, full, 9000);
                CullKit.SaveAid(Path.Combine(pictures, "t16-" + view + "-aid-skip.png"), CullKit.ReadColours(rig.Recon.Output, ref readback), W, H);
                rig.Frame.AidMode = 7;
                rig.Recon.Reset();
                EdgeKit.Render(Cam, rig, full, 9000);
                CullKit.SaveAid(Path.Combine(pictures, "t16-" + view + "-aid-lod.png"), CullKit.ReadColours(rig.Recon.Output, ref readback), W, H);
                rig.Frame.AidMode = 0;
                if (shown.ContainsKey("all") && shown.ContainsKey("hm"))
                {
                    var diff = new Color[pixels];
                    for (int p = 0; p < pixels; p++)
                    {
                        float e = Mathf.Clamp01(Mathf.Abs(shown["all"][p] - shown["hm"][p]) * 8f);
                        diff[p] = new Color(e, e * 0.4f, 0f);
                    }
                    EdgeKit.SaveColour(Path.Combine(pictures, "t16-" + view + "-aid-diff.png"), diff, W, H);
                }
                skip.Apply(rig.Frame);
                yield return null;
            }
            rig.Dispose();
            Cam.targetTexture = Target;
            rt.Release();
            Sink.Flag("quality_completed", true);
        }
    }

    // The camera sweeps: for each switch the largest change from one frame to the next that the switch adds to the baseline's own change, relative to the
    // picture's mean luminance. Both renders use the same frame number, so the per-pixel dither cancels.
    [WeatherScene("cull-sweep")]
    public sealed class CullSweepScene : WeatherSceneBase
    {
        private const int W = 480, H = 270;
        private Texture2D readback;

        private sealed class Path3
        {
            public string Name, Job;
            public int Frames;
            public Func<int, float> Yaw = i => 0f, Pitch = i => 0f;
            // Altitude above the job's position (m), distance travelled along the forward direction (m).
            public Func<int, float> Rise = i => 0f, Travel = i => 0f;
        }

        // The mean display value of each 16 x 16 block (a local measure: a layer that vanishes from a part of the picture moves a block, not the whole mean).
        private static float[] Blocks(float[] display)
        {
            const int B = 16;
            int bw = (W + B - 1) / B, bh = (H + B - 1) / B;
            var sum = new float[bw * bh];
            var count = new float[bw * bh];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    int k = (y / B) * bw + x / B;
                    sum[k] += display[y * W + x];
                    count[k] += 1f;
                }
            for (int k = 0; k < sum.Length; k++) sum[k] /= Math.Max(count[k], 1f);
            return sum;
        }

        // The largest change of one block between two frames that the variant adds to the baseline's, and the largest offset of a block from the baseline.
        private static double AddedChange(float[] now, float[] before, float[] baseNow, float[] baseBefore)
        {
            double worst = 0;
            for (int k = 0; k < now.Length; k++) worst = Math.Max(worst, Math.Abs((now[k] - before[k]) - (baseNow[k] - baseBefore[k])));
            return worst;
        }

        public override IEnumerator Run()
        {
            string input = WeatherPass.Argument("-in");
            TemporalRig rig = input != null && File.Exists(Path.Combine(input, "jobs.json")) ? EdgeKit.Rig(input) : null;
            Sink.Flag("sweep_available", rig != null);
            if (rig == null) yield break;
            LayersJobFile file = JsonUtility.FromJson<LayersJobFile>(File.ReadAllText(Path.Combine(input, "jobs.json")));
            var rt = new RenderTexture(W, H, 24, RenderTextureFormat.DefaultHDR);
            rt.Create();
            Cam.targetTexture = rt;
            int pixels = W * H;
            var paths = new List<Path3>
            {
                new Path3 { Name = "altitude", Job = "V-inside", Frames = 120, Rise = i => -1990f + i * 100f },
                new Path3 { Name = "crossing", Job = "V-ground", Frames = 120, Rise = i => 600f + i * 22f },
                new Path3 { Name = "turn", Job = "V-above", Frames = 120, Yaw = i => i * 3f },
                // The distance: from the horizon (the cloud at its farthest) up to overhead, so that the distance of the first cloud falls through the LOD bands.
                new Path3 { Name = "distance", Job = "V-horizon", Frames = 120, Pitch = i => i * 0.1f },
                new Path3 { Name = "travel", Job = "V-above", Frames = 120, Travel = i => i * 2500f },
                new Path3 { Name = "sky3turn", Job = "V-sky3", Frames = 120, Yaw = i => i * 3f },
            };
            var variants = new List<CullVariant>();
            foreach (CullVariant v in CullVariant.Table()) if (v.Mode == ReconMode.Full) variants.Add(v);
            var full = new ReconSettings { Mode = ReconMode.Full };
            var dOff = new float[pixels];
            var aOff = new float[pixels];
            var dOn = new float[pixels];
            var aOn = new float[pixels];
            var clock = System.Diagnostics.Stopwatch.StartNew();
            foreach (Path3 path in paths)
            {
                LayersJob job = null;
                foreach (LayersJob j in file.jobs) if (j.id == path.Job) job = j;
                if (job == null) continue;
                rig.Load(job, input);
                rig.Frame.UseProductCost();
                Vector3 pos0 = LayersSetup.V(job.position);
                // Per frame the baseline luminance, then for each variant its own and the largest added change.
                var baselines = new Dictionary<string, float[][]>();
                foreach (CullVariant v in variants)
                {
                    if (clock.Elapsed.TotalMinutes > 17.0) { Sink.Flag("sweep_completed", false); yield break; }
                    // The variant is compared with its baseline (the same haze and shape mean without the switches).
                    string baseName = v.Name == v.Baseline ? v.Name : v.Baseline;
                    if (!baselines.ContainsKey(baseName))
                    {
                        var lumB = new float[path.Frames][];
                        CullVariant b = CullVariant.Named(baseName);
                        for (int i = 0; i < path.Frames; i++)
                        {
                            Place(rig, job, path, pos0, i);
                            b.Apply(rig.Frame);
                            rig.Recon.Reset();
                            EdgeKit.Render(Cam, rig, full, 4000 + i);
                            EdgeKit.Read(rig.Recon.Output, ref readback, dOff, aOff);
                            lumB[i] = Blocks(dOff);
                            if (i % 8 == 7) yield return null;
                        }
                        baselines[baseName] = lumB;
                    }
                    if (v.Name == v.Baseline) continue;
                    float[][] lum0 = baselines[baseName];
                    double worst = 0, worstFrame = -1, mean0 = 0;
                    for (int i = 0; i < path.Frames; i++) { double m = 0; foreach (float x in lum0[i]) m += x / lum0[i].Length; mean0 += m / path.Frames; }
                    var lumV = new float[path.Frames][];
                    for (int i = 0; i < path.Frames; i++)
                    {
                        Place(rig, job, path, pos0, i);
                        v.Apply(rig.Frame);
                        rig.Recon.Reset();
                        EdgeKit.Render(Cam, rig, full, 4000 + i);
                        EdgeKit.Read(rig.Recon.Output, ref readback, dOn, aOn);
                        lumV[i] = Blocks(dOn);
                        if (i > 0)
                        {
                            double added = AddedChange(lumV[i], lumV[i - 1], lum0[i], lum0[i - 1]) / Math.Max(mean0, 1e-6);
                            if (added > worst) { worst = added; worstFrame = i; }
                        }
                        if (i % 8 == 7) yield return null;
                    }
                    double drift = 0;
                    for (int i = 0; i < path.Frames; i++)
                        for (int k = 0; k < lumV[i].Length; k++) drift = Math.Max(drift, Math.Abs(lumV[i][k] - lum0[i][k]) / Math.Max(mean0, 1e-6));
                    Sink.Info("cullsweep." + path.Name + "." + v.Name + ".pop", worst);
                    Sink.Info("cullsweep." + path.Name + "." + v.Name + ".pop_frame", worstFrame);
                    Sink.Info("cullsweep." + path.Name + "." + v.Name + ".offset", drift);
                }
            }
            rig.Dispose();
            Cam.targetTexture = Target;
            rt.Release();
            Sink.Flag("sweep_completed", true);
        }

        // The pose of frame i: the job's position raised by Rise and moved along the great circle of its forward direction by Travel, turned by Yaw.
        private void Place(TemporalRig rig, LayersJob job, Path3 path, Vector3 pos0, int i)
        {
            Vector3 up0 = rig.Up, fwd0 = rig.Forward;
            Vector3 pos = pos0 + up0 * path.Rise(i);
            Vector3 up = up0, fwd = fwd0;
            float travel = path.Travel(i);
            if (travel != 0f)
            {
                Vector3 axis = Vector3.Cross(up0, Vector3.ProjectOnPlane(fwd0, up0).normalized).normalized;
                float angle = travel / pos0.magnitude * Mathf.Rad2Deg;
                Quaternion q = Quaternion.AngleAxis(angle, axis);
                pos = q * pos;
                up = q * up0;
                fwd = q * fwd0;
            }
            Cam.fieldOfView = job.fovV;
            Cam.transform.position = pos;
            Cam.transform.rotation = Quaternion.AngleAxis(path.Yaw(i), up) * Quaternion.LookRotation(fwd, up) * Quaternion.AngleAxis(-path.Pitch(i), Vector3.right);
        }
    }

    // The 4 x 4 block mode through two turns with and without the switches.
    [WeatherScene("cull-levels")]
    public sealed class CullLevelsScene : WeatherSceneBase
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
            var variants = new List<CullVariant>();
            foreach (string name in new[] { "u4off", "u4skip", "u4hm", "u4all" }) variants.Add(CullVariant.Named(name));
            var clock = System.Diagnostics.Stopwatch.StartNew();
            foreach (string jobId in new[] { "V-ground", "V-sky3" })
            {
                LayersJob job = null;
                foreach (LayersJob j in file.jobs) if (j.id == jobId) job = j;
                if (job == null) continue;
                rig.Load(job, input);
                rig.Frame.UseProductCost();
                var full = new ReconSettings { Mode = ReconMode.Full };
                foreach (Scenario sc in scenarios)
                {
                    // The references: the full march of the pose without any switch, with the haze and shape mean of the variants that have them ("hm") or without.
                    var refSets = new Dictionary<string, float[][]>();
                    var refAlphaSets = new Dictionary<string, float[][]>();
                    foreach (string refName in new[] { "off", "hm" })
                    {
                        CullVariant.Named(refName).Apply(rig.Frame);
                        var rd = new float[sc.Frames][];
                        var ra = new float[sc.Frames][];
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
                            rd[i] = sum;
                            ra[i] = al;
                            if (i % 6 == 5) yield return null;
                        }
                        refSets[refName] = rd;
                        refAlphaSets[refName] = ra;
                    }
                    foreach (CullVariant v in variants)
                    {
                        if (clock.Elapsed.TotalMinutes > 16.0) { Sink.Flag("levels_completed", false); yield break; }
                        string refKey = v.Haze || v.Cut ? "hm" : "off";
                        float[][] refDisplay = refSets[refKey], refAlpha = refAlphaSets[refKey];
                        v.Apply(rig.Frame);
                        ReconSettings s = v.Settings();
                        rig.Recon.Reset();
                        var moving = new List<double>();
                        var trail = new List<double>();
                        var shimmer = new List<double>();
                        float[] previous = null;
                        var outAlpha = new float[pixels];
                        string key = "culllevels." + jobId + "." + sc.Name + "." + v.Name;
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
