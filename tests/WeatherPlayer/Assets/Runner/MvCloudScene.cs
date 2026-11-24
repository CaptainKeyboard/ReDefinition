// Scene of the weather player for the cloud's motion vectors (E40 MVCLOUD; tools/weather_metrics/e40mv.py reads the output):
//   -scene mv-cloud -in <e40gc inputs> -views l4ground,l4above   the layer pass as the game runs it (the soft density of the owner's 0.5, the march target a third, the history 0.2)
//                                                                on the four-layer stack of the game, the camera flying sideways at 300 and 2400 m/s and turning, and
//                                                                (1) the shader's motion vector of the pass against the CPU model (LayerMotionModel) at the cloud pixels,
//                                                                (2) the GPU milliseconds of the history pass with and without the layers' places, and of the motion pass.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using ReDefinition.Atmospheres;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.WeatherPlayer
{
    [WeatherScene("mv-cloud")]
    public sealed class MvCloudScene : WeatherSceneBase
    {
        private const int W = 1707, H = 960, Frames = 14, Measured = 3, Stride = 6;

        private sealed class Scenario
        {
            public string Name;
            public float YawPerFrame, SidePerFrame;
            // The pass criterion of the vector's error against the model (px), or 0: none.
            public double Limit;
        }

        // 300 m/s and 2400 m/s are 5 and 40 m a frame at 60 frames a second, the turn 30 degrees a second.
        private static readonly Scenario[] Scenarios =
        {
            new Scenario { Name = "fly300", SidePerFrame = 5f, Limit = 0.3 },
            new Scenario { Name = "fly2400", SidePerFrame = 40f },
            new Scenario { Name = "turn", YawPerFrame = 0.5f },
        };

        // The cloud's coverage map of a layer on the CPU: bytes, row 0 = north, the map's longitude shift (the shader's MvPresence sampled bilinearly).
        private sealed class CoverMap
        {
            public byte[] Bytes;
            public int Width, Height;
            public bool Any;

            public float Sample(float u, float v)
            {
                float x = u * Width - 0.5f, y = Mathf.Clamp(v, 0.5f / Height, 1f - 0.5f / Height) * Height - 0.5f;
                int x0 = Mathf.FloorToInt(x), y0 = Mathf.FloorToInt(y);
                float fx = x - x0, fy = y - y0;
                return Mathf.Lerp(Mathf.Lerp(At(x0, y0), At(x0 + 1, y0), fx), Mathf.Lerp(At(x0, y0 + 1), At(x0 + 1, y0 + 1), fx), fy);
            }

            private float At(int x, int y)
            {
                x = ((x % Width) + Width) % Width;
                y = Mathf.Clamp(y, 0, Height - 1);
                return Bytes[y * Width + x] / 255f;
            }
        }

        private Texture2D halfTexture;

        private Color[] Read(RenderTexture rt)
        {
            if (halfTexture == null || halfTexture.width != rt.width || halfTexture.height != rt.height)
                halfTexture = new Texture2D(rt.width, rt.height, TextureFormat.RGBAHalf, false, true);
            RenderTexture.active = rt;
            halfTexture.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0, false);
            RenderTexture.active = null;
            return halfTexture.GetPixels();
        }

        private static void Clear(RenderTexture rt, Color colour)
        {
            RenderTexture.active = rt;
            GL.Clear(false, true, colour);
            RenderTexture.active = null;
        }

        private static double Percentile(List<double> values, double p)
        {
            if (values.Count == 0) return double.NaN;
            values.Sort();
            return values[Math.Min(values.Count - 1, (int)(p * values.Count))];
        }

        private static double Mean(List<double> values)
        {
            if (values.Count == 0) return double.NaN;
            double sum = 0;
            foreach (double v in values) sum += v;
            return sum / values.Count;
        }

        public override IEnumerator Run()
        {
            string input = WeatherPass.Argument("-in");
            var rig = new TemporalRig();
            bool ready = input != null && File.Exists(Path.Combine(input, "jobs.json")) && rig.Init();
            Sink.Flag("mv_cloud_available", ready);
            if (!ready) yield break;
            LayersJobFile file = JsonUtility.FromJson<LayersJobFile>(File.ReadAllText(Path.Combine(input, "jobs.json")));
            var rt = new RenderTexture(W, H, 24, RenderTextureFormat.DefaultHDR) { name = "mv cloud target" };
            rt.Create();
            Cam.targetTexture = rt;
            var motion = new RenderTexture(W, H, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear) { name = "mv cloud vectors" };
            motion.Create();
            var depth = new RenderTexture(W, H, 0, RenderTextureFormat.RFloat, RenderTextureReadWrite.Linear) { name = "mv cloud scene depth" };
            depth.Create();
            // The scene depth the pass is given: nothing in front of the cloud (the far plane: 0 with a reversed Z buffer, 1 without).
            Clear(depth, new Color(SystemInfo.usesReversedZBuffer ? 0f : 1f, 0f, 0f, 0f));
            bool timers = WeatherGpuTimer.Start();
            Sink.Flag("mv_cloud_timers_available", timers);

            SoftTable.Medium medium = SoftTable.Media[0];
            SoftTable.Case product = SoftTable.Parse("seq1m24");
            var layers = new MotionLayer[LayerMotionModel.MaxLayers];
            foreach (string view in SoftTable.Views)
            {
                LayersJob job = GrainTable.Find(file, "gn-" + view + "-prod-f0");
                if (job == null) { Sink.Flag("mv_cloud_job_" + view, false); continue; }
                SoftTable.Original original = SoftTable.Original.Of(job.layers[0]);
                rig.Load(job, input);
                double detailMean = SoftTable.DetailMean(rig);
                SoftTable.Load(rig, job, input, medium, original, detailMean);
                SoftTable.ApplyCase(rig.Frame, product);
                rig.Frame.UseSceneDepth = false;
                ReconSettings settings = SoftTable.Settings(product, "acc20");
                var maps = new CoverMap[job.layers.Length];
                for (int l = 0; l < maps.Length; l++)
                {
                    string name = job.layers[l].coverageMap;
                    maps[l] = new CoverMap { Width = job.mapWidth, Height = job.mapHeight };
                    maps[l].Any = !string.IsNullOrEmpty(name);
                    maps[l].Bytes = maps[l].Any ? File.ReadAllBytes(Path.Combine(input, name)) : null;
                }
                Sink.Info("mvcloud." + view + ".layers", job.layers.Length);

                foreach (Scenario sc in Scenarios)
                {
                    foreach (bool layerMotion in new[] { true, false })
                    {
                        settings.LayerMotion = layerMotion;
                        rig.Recon.Reset();
                        var errors = new List<double>();
                        var firstShell = new List<double>();
                        var unity = new List<double>();
                        int covered = 0, total = 0;
                        for (int i = 0; i < Frames; i++)
                        {
                            GrainTable.Pose(Cam, job, sc.YawPerFrame * Mathf.Max(i - 6, 0), sc.SidePerFrame * Mathf.Max(i - 6, 0));
                            var cb = new CommandBuffer { name = "mv cloud" };
                            rig.Recon.Record(cb, rig.Renderer, rig.Frame, Cam, settings, 100 + i, null, null, null);
                            Graphics.ExecuteCommandBuffer(cb);
                            cb.Release();
                            if (i >= Frames - Measured)
                            {
                                Clear(motion, Color.clear);
                                cb = new CommandBuffer { name = "mv cloud vectors" };
                                bool drawn = rig.Recon.RecordMotion(cb, motion, depth);
                                Graphics.ExecuteCommandBuffer(cb);
                                cb.Release();
                                if (!drawn) { Sink.Flag("mv_cloud_drawn_" + view + "_" + sc.Name, false); continue; }
                                Measure(rig, job, maps, layers, Read(motion), Read(rig.Recon.Output), errors, firstShell, unity, ref covered, ref total);
                            }
                            if (i % 4 == 3) yield return null;
                        }
                        string key = "mvcloud." + view + "." + sc.Name + (layerMotion ? ".layers" : ".shell1");
                        Sink.Info(key + ".cloud_px", covered);
                        Sink.Info(key + ".err_mean", Mean(errors));
                        Sink.Info(key + ".err_p95", Percentile(errors, 0.95));
                        Sink.Info(key + ".err_max", Percentile(errors, 1.0));
                        Sink.Info(key + ".firstshell_err_mean", Mean(firstShell));
                        Sink.Info(key + ".hostvector_err_mean", sc.Name == "turn" ? double.NaN : Mean(unity));
                        if (layerMotion && sc.Limit > 0.0 && covered > 0) Sink.AtMost(key + ".err_mean_limit", Mean(errors), sc.Limit);
                        yield return null;
                    }
                }
                original.Restore(job.layers[0]);
            }

            // The cost: the GPU milliseconds of the history pass (resolve) with the layers' places and with the first layer's sphere, and of the motion pass, at 1707 x 960, static camera.
            if (timers)
            {
                var cbCost = new CommandBuffer { name = "mv cloud cost" };
                Cam.AddCommandBuffer(CameraEvent.AfterSkybox, cbCost);
                var segments = new float[8];
                Action<CommandBuffer> mStart = c => WeatherGpuTimer.Mark(c, WeatherGpuTimer.SlabStart),
                    mMarched = c => WeatherGpuTimer.Mark(c, WeatherGpuTimer.SlabMarched),
                    mResolved = c => WeatherGpuTimer.Mark(c, WeatherGpuTimer.SlabComposited),
                    mEnd = c => WeatherGpuTimer.Mark(c, WeatherGpuTimer.PraecipitatioEnd);
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var dummy = new RenderTexture(4, 4, 0, RenderTextureFormat.RFloat);
                dummy.Create();
                Clear(dummy, new Color(SystemInfo.usesReversedZBuffer ? 0f : 1f, 0f, 0f, 0f));
                var dummyId = new RenderTargetIdentifier(dummy);
                for (int round = 0; round < 2; round++)
                {
                    string[] views = SoftTable.Views;
                    for (int vi = 0; vi < views.Length; vi++)
                    {
                        string view = views[round == 0 ? vi : views.Length - 1 - vi];
                        LayersJob job = GrainTable.Find(file, "gn-" + view + "-prod-f0");
                        if (job == null) continue;
                        SoftTable.Original original = SoftTable.Original.Of(job.layers[0]);
                        rig.Load(job, input);
                        double detailMean = SoftTable.DetailMean(rig);
                        SoftTable.Load(rig, job, input, medium, original, detailMean);
                        SoftTable.ApplyCase(rig.Frame, product);
                        rig.Frame.UseSceneDepth = true;
                        GrainTable.Pose(Cam, job, 0f, 0f);
                        ReconSettings settings = SoftTable.Settings(product, "acc20");
                        for (int c = 0; c < 3; c++)
                        {
                            // 0: the history pass with the layers' places, 1: with the first layer's sphere, 2: the motion pass (after a recorded frame with the layers' places).
                            int kind = round == 0 ? c : 2 - c;
                            settings.LayerMotion = kind != 1;
                            rig.Recon.Reset();
                            string label = view + "." + (kind == 2 ? "motion" : kind == 0 ? "layers" : "shell1");
                            if (!samples.ContainsKey(label)) samples[label] = new List<double>[] { new List<double>(), new List<double>() };
                            List<double>[] s = samples[label];
                            int guard = 0, n = 0, taken = 0;
                            while (taken < 40 && guard++ < 1200)
                            {
                                if (clock.Elapsed.TotalMinutes > 14.0) { Sink.Flag("mv_cloud_cost_completed", false); yield break; }
                                cbCost.Clear();
                                cbCost.SetGlobalTexture(Shader.PropertyToID("_SceneDepth"), dummyId);
                                bool motionDrawn = true;
                                if (kind == 2 && n >= 2)
                                {
                                    // After two recorded frames (the second has a frame before it): only the motion pass over the last frame's full target and pose.
                                    mStart(cbCost);
                                    motionDrawn = rig.Recon.RecordMotion(cbCost, motion, depth);
                                    mMarched(cbCost);
                                    mResolved(cbCost);
                                    mEnd(cbCost);
                                }
                                else
                                {
                                    rig.Recon.Record(cbCost, rig.Renderer, rig.Frame, Cam, settings, FrameIndex, mStart, mMarched, mResolved);
                                    rig.Recon.Composite(cbCost, rig.Renderer, BuiltinRenderTextureType.CameraTarget, mEnd);
                                }
                                yield return Frames(1);
                                if (n++ < 20 || !motionDrawn) continue;
                                float totalMs;
                                int number;
                                if (!WeatherGpuTimer.Read(segments, out totalMs, out number)) continue;
                                float march = segments[WeatherGpuTimer.SlabMarched - WeatherGpuTimer.SlabStart], resolve = segments[WeatherGpuTimer.SlabComposited - WeatherGpuTimer.SlabStart];
                                if (march >= 0f && resolve >= 0f) { s[0].Add(march); s[1].Add(resolve); taken++; }
                            }
                        }
                        original.Restore(job.layers[0]);
                    }
                }
                foreach (KeyValuePair<string, List<double>[]> kv in samples)
                {
                    List<double> first = kv.Value[0], second = kv.Value[1];
                    first.Sort();
                    second.Sort();
                    // For the history rows the first segment is the march (the same in both), the second the reconstruction; for the motion row the first is the pass.
                    Sink.Info("mvcloud.cost." + kv.Key + ".first_ms", first.Count == 0 ? double.NaN : first[first.Count / 2]);
                    Sink.Info("mvcloud.cost." + kv.Key + ".second_ms", second.Count == 0 ? double.NaN : second[second.Count / 2]);
                    Sink.Info("mvcloud.cost." + kv.Key + ".n", first.Count);
                }
                Cam.RemoveCommandBuffer(CameraEvent.AfterSkybox, cbCost);
                cbCost.Release();
                Sink.Flag("mv_cloud_cost_completed", true);
            }
            Cam.targetTexture = Target;
            rt.Release();
            motion.Release();
            depth.Release();
            rig.Dispose();
            Sink.Flag("mv_cloud_completed", true);
        }

        private readonly Dictionary<string, List<double>[]> samples = new Dictionary<string, List<double>[]>();

        // The motion vector the shader wrote at the pixels the cloud covers (alpha above 0.5) against the model's: the layers' places blended by the share of the pixel (presence from the
        // job's own coverage maps on the CPU, the numbers of the shader from the renderer), and against the first layer's sphere. The vector over the cleared target is the cloud's blended
        // in by the alpha, so the model's is taken by the alpha too (errors); firstShell and unity are full vectors. With layerMotion false the shader took the first sphere, and the model of
        // the layers is the true answer it misses.
        private void Measure(TemporalRig rig, LayersJob job, CoverMap[] maps, MotionLayer[] layers, Color[] vectors, Color[] cloud, List<double> errors,
                             List<double> firstShell, List<double> unity, ref int covered, ref int total)
        {
            ReconCamera current, previous;
            if (!rig.Recon.LastPoses(out current, out previous)) return;
            int n = rig.Renderer.FillMotionLayers(layers, 0f);
            double planetR = rig.Renderer.PlanetRadiusM;
            var parameters = new Vector4[n][];
            for (int l = 0; l < n; l++)
            {
                Vector4 a, b;
                rig.Renderer.MotionParameters(l, out a, out b);
                parameters[l] = new[] { a, b };
            }
            Matrix4x4 worldToMap = rig.Frame.WorldToMap;
            for (int y = Stride / 2; y < H; y += Stride)
                for (int x = Stride / 2; x < W; x += Stride)
                {
                    int index = y * W + x;
                    total++;
                    if (cloud[index].a <= 0.5f) continue;
                    double u = (x + 0.5) / W, v = (y + 0.5) / H;
                    // The presence of every layer at the point its ray meets the layer's middle sphere.
                    double dx = current.Fx + (u * 2 - 1) * current.Rx * current.TanX + (v * 2 - 1) * current.Ux * current.TanY;
                    double dy = current.Fy + (u * 2 - 1) * current.Ry * current.TanX + (v * 2 - 1) * current.Uy * current.TanY;
                    double dz = current.Fz + (u * 2 - 1) * current.Rz * current.TanX + (v * 2 - 1) * current.Uz * current.TanY;
                    double len = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                    dx /= len; dy /= len; dz /= len;
                    for (int l = 0; l < n; l++)
                    {
                        double t;
                        layers[l].Presence = 0.0;
                        if (!LayerMotionModel.Hit(current.Px, current.Py, current.Pz, dx, dy, dz, layers[l].ShellR, out t)) continue;
                        float cov = 1f;
                        if (parameters[l][0].w >= 0f && maps[l].Any)
                        {
                            Vector3 q = worldToMap.MultiplyVector(new Vector3((float)(current.Px + dx * t), (float)(current.Py + dy * t), (float)(current.Pz + dz * t))).normalized;
                            float lat = Mathf.Asin(Mathf.Clamp(q.z, -1f, 1f)), lon = Mathf.Atan2(q.y, q.x);
                            float mu = (lon * Mathf.Rad2Deg - parameters[l][1].y + 180f) / 360f, mv = (90f - lat * Mathf.Rad2Deg) / 180f;
                            cov = maps[l].Sample(mu, mv);
                        }
                        layers[l].Presence = LayerMotionModel.Presence(cov, parameters[l][0].y, parameters[l][0].z, parameters[l][1].z);
                    }
                    double pu, pv, depth;
                    if (!LayerMotionModel.Previous(current, previous, u, v, layers, n, planetR, out pu, out pv, out depth)) continue;
                    covered++;
                    double ex = (u - pu) * W, ey = (v - pv) * H;
                    double rx = vectors[index].r * W, ry = vectors[index].g * H, alpha = cloud[index].a;
                    errors.Add(Math.Sqrt((rx - ex * alpha) * (rx - ex * alpha) + (ry - ey * alpha) * (ry - ey * alpha)));
                    double ou, ov;
                    if (ReconReprojection.PreviousUv(current, previous, u, v, 0, 0, 0, layers[0].ShellR, 50000.0, out ou, out ov))
                    {
                        double fx = (u - ou) * W, fy = (v - ov) * H;
                        firstShell.Add(Math.Sqrt((fx - ex) * (fx - ex) + (fy - ey) * (fy - ey)));
                    }
                    // What the host's own vector leaves where the camera only translates: the sky's rotation-only vector is zero, so the whole expected vector is the error.
                    unity.Add(Math.Sqrt(ex * ex + ey * ey));
                }
        }
    }
}
