#if UNITY_2019_4_OR_NEWER
// Scenes of the weather player for the temporal work (package T05), linked by tests/WeatherPlayer/linked/T05.txt and compiled out of the test
// project (Unity defines the symbol):
//   -scene layers-cost5 -in <T01 inputs>      the cost of the pass per view for the cost measures and the reconstruction modes (timing run)
//   -scene layers-temporal -in <T01 inputs>   the reconstruction against the reference along camera paths (no upscaler)
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using ReDefinition.Atmospheres;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.WeatherPlayer
{
    // The things both scenes share: a job loaded into the mod's own renderer, the camera of a job, and the modes of the table.
    internal sealed class TemporalRig
    {
        internal LayerRenderer Renderer = new LayerRenderer();
        internal LayerReconstructor Recon = new LayerReconstructor();
        internal LayerFrame Frame;
        internal LayersJob Job;
        internal Vector3 Up, Forward;

        internal bool Init()
        {
            Shader layers = Shader.Find(LayerRenderer.ShaderName), recon = Shader.Find(LayerReconstructor.ShaderName);
            return layers != null && recon != null && Renderer.Init(layers) && Recon.Init(recon);
        }

        internal void Load(LayersJob job, string input)
        {
            Job = job;
            Frame = LayersSetup.Load(job, input, Renderer);
            Frame.UseSceneDepth = false;
            Frame.SunSteps = 3;
            Frame.MinStepM = 25f;
            Frame.StepGrowth = 0.0002f;
            Frame.Jitter = 1f;
            Frame.EarlyOut = 0.01f;
            Frame.MaxSteps = 48;
            Up = LayersSetup.V(job.up);
            Forward = LayersSetup.V(job.forward);
            Recon.Reset();
        }

        internal void Pose(Camera cam, float yawDeg, Vector3 shift)
        {
            cam.fieldOfView = Job.fovV;
            cam.transform.position = LayersSetup.V(Job.position) + shift;
            cam.transform.rotation = Quaternion.AngleAxis(yawDeg, Up) * Quaternion.LookRotation(Forward, Up);
        }

        // The ambient's up term as the tables name it: 0 the local density (the default), 1 taps at every lit sample, 2 taps reused with the sun
        // march, 3 taps reused and moved by the ray's progress.
        internal static void SetUp(CloudLightSettings light, int mode)
        {
            light.AmbientOcclusionTaps = mode >= 1;
            light.UpReuse = mode >= 2 ? mode - 1 : 0;
        }

        internal void Dispose()
        {
            Recon.Dispose();
            Renderer.Dispose();
        }
    }

    // ------------------------------------------------------------------------------------------------------------------------------
    // Cost
    // ------------------------------------------------------------------------------------------------------------------------------
    [WeatherScene("layers-cost5")]
    public sealed class LayersCostTemporalScene : WeatherSceneBase
    {
        private sealed class Variant
        {
            public string Name;
            public int UpMode, Stride = 2;
            public float Growth;
            public int SunSteps;       // 0: the frame's 3
            public float TapRatio = 2f;
            public float Occlusion = -1f;   // < 0: the default
            public ReconMode Mode = ReconMode.Full;
            public int Block = 2;
            public float Resolution = 0.5f;
            public bool Large;
        }

        // The table: base is the layer pass as the main branch leaves it (the up term from the local density); taps is the same with the up taps of
        // T03 (the pass before FX-T03); p is base with the step growth 2, the product of this package; the reduced rows run on p. Large rows also
        // run at 2560x1440.
        private static readonly Variant[] Variants =
        {
            new Variant { Name = "taps", UpMode = 1, Large = true },
            new Variant { Name = "taps-moved", UpMode = 3, Growth = 2f },
            new Variant { Name = "base", Large = true },
            new Variant { Name = "p", Growth = 2f, Large = true },
            new Variant { Name = "p-b2", Growth = 2f, Mode = ReconMode.Block, Block = 2, Large = true },
            new Variant { Name = "p-b3", Growth = 2f, Mode = ReconMode.Block, Block = 3, Large = true },
            new Variant { Name = "p-b4", Growth = 2f, Mode = ReconMode.Block, Block = 4, Large = true },
            new Variant { Name = "p-l50", Growth = 2f, Mode = ReconMode.Low, Resolution = 0.5f },
            new Variant { Name = "p-l25", Growth = 2f, Mode = ReconMode.Low, Resolution = 0.25f, Large = true },
        };

        public override IEnumerator Run()
        {
            string input = WeatherPass.Argument("-in");
            var rig = new TemporalRig();
            bool ready = input != null && File.Exists(Path.Combine(input, "jobs.json")) && WeatherGpuTimer.Start() && rig.Init();
            Sink.Flag("cost_timers_available", ready);
            if (!ready) yield break;
            LayersJobFile file = JsonUtility.FromJson<LayersJobFile>(File.ReadAllText(Path.Combine(input, "jobs.json")));
            var dummy = new RenderTexture(4, 4, 0, RenderTextureFormat.RFloat);
            dummy.Create();
            RenderTexture.active = dummy;
            GL.Clear(false, true, Color.black);
            RenderTexture.active = null;
            var depthId = new RenderTargetIdentifier(dummy);
            var cb = new CommandBuffer { name = "T05 cost" };
            Cam.AddCommandBuffer(CameraEvent.AfterSkybox, cb);
            var sizes = new[] { new Vector2Int(1707, 960), new Vector2Int(2560, 1440) };
            var segments = new float[8];
            var clock = System.Diagnostics.Stopwatch.StartNew();
            Action<CommandBuffer> mStart = c => WeatherGpuTimer.Mark(c, WeatherGpuTimer.SlabStart),
                mMarched = c => WeatherGpuTimer.Mark(c, WeatherGpuTimer.SlabMarched),
                mResolved = c => WeatherGpuTimer.Mark(c, WeatherGpuTimer.SlabComposited),
                mEnd = c => WeatherGpuTimer.Mark(c, WeatherGpuTimer.CurtainEnd);
            foreach (Vector2Int size in sizes)
            {
                var rt = new RenderTexture(size.x, size.y, 24, RenderTextureFormat.DefaultHDR) { name = "T05 cost target" };
                rt.Create();
                Cam.targetTexture = rt;
                string sz = size.x + "x" + size.y;
                foreach (LayersJob job in file.jobs)
                {
                    if (!job.id.StartsWith("V-")) continue;
                    rig.Load(job, input);
                    LayerFrame frame = rig.Frame;
                    frame.UseSceneDepth = true;
                    rig.Pose(Cam, 0f, Vector3.zero);
                    foreach (Variant v in Variants)
                    {
                        if (size.x > 1707 && !v.Large) continue;
                        if (clock.Elapsed.TotalMinutes > 16.0) { Sink.Flag("cost_run_completed", false); yield break; }
                        frame.TransmittanceGrowth = v.Growth;
                        frame.LightStride = v.Stride;
                        TemporalRig.SetUp(frame.Light, v.UpMode);
                        frame.Light.SunStepsOverride = v.SunSteps;
                        frame.Light.TapRatio = v.TapRatio;
                        frame.Light.AmbientOcclusion = v.Occlusion >= 0f ? v.Occlusion : new CloudLightSettings().AmbientOcclusion;
                        var settings = new ReconSettings { Mode = v.Mode, Block = v.Block, Resolution = v.Resolution };
                        rig.Recon.Reset();
                        var march = new List<double>();
                        var resolve = new List<double>();
                        var composite = new List<double>();
                        int guard = 0, n = 0;
                        while (march.Count < 60 && guard++ < 1500)
                        {
                            cb.Clear();
                            cb.SetGlobalTexture(Shader.PropertyToID("_SceneDepth"), depthId);
                            rig.Recon.Record(cb, rig.Renderer, frame, Cam, settings, FrameIndex, mStart, mMarched, mResolved);
                            rig.Recon.Composite(cb, rig.Renderer, BuiltinRenderTextureType.CameraTarget, mEnd);
                            yield return Frames(1);
                            if (n++ < 24) continue;
                            float total;
                            int number;
                            if (!WeatherGpuTimer.Read(segments, out total, out number)) continue;
                            float m = segments[WeatherGpuTimer.SlabMarched - WeatherGpuTimer.SlabStart], r = segments[WeatherGpuTimer.SlabComposited - WeatherGpuTimer.SlabStart],
                                c = segments[WeatherGpuTimer.CurtainEnd - WeatherGpuTimer.SlabStart];
                            if (m >= 0f && r >= 0f && c >= 0f) { march.Add(m); resolve.Add(r); composite.Add(c); }
                        }
                        double tm = Median(march), tr = Median(resolve), tc = Median(composite);
                        string label = job.id + "." + v.Name + "." + sz;
                        Sink.Info("cost5.march." + label, tm);
                        Sink.Info("cost5.resolve." + label, tr);
                        Sink.Info("cost5.composite." + label, tc);
                        Sink.Info("cost5.total." + label, tm + tr + tc);
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
            var sorted = values.ToArray();
            Array.Sort(sorted);
            return sorted[sorted.Length / 2];
        }
    }

    // ------------------------------------------------------------------------------------------------------------------------------
    // Quality
    // ------------------------------------------------------------------------------------------------------------------------------
    [WeatherScene("layers-temporal")]
    public sealed class LayersTemporalScene : WeatherSceneBase
    {
        private const int W = 640, H = 360, RefFrames = 4;
        private static readonly Color Sky = new Color(0.42f, 0.52f, 0.72f);

        private sealed class Mode
        {
            public string Name;
            public ReconSettings Settings;
        }

        private sealed class Scenario
        {
            public string Name;
            // The camera's yaw and shift per frame, the cloud's drift per frame (m), the frame after which nothing moves any more.
            public Func<int, float> Yaw = i => 0f;
            public Func<int, Vector3> Shift = i => Vector3.zero;
            public Vector3 Drift;
            public int Frames = 48, LastMove;
        }

        private static List<Mode> Modes()
        {
            return new List<Mode>
            {
                new Mode { Name = "full", Settings = new ReconSettings { Mode = ReconMode.Full } },
                new Mode { Name = "b2", Settings = new ReconSettings { Mode = ReconMode.Block, Block = 2 } },
                new Mode { Name = "b3", Settings = new ReconSettings { Mode = ReconMode.Block, Block = 3 } },
                new Mode { Name = "b4", Settings = new ReconSettings { Mode = ReconMode.Block, Block = 4 } },
                // The weights of the history control (the one sweep of them: the weight of the pixels without a new sample, the clip width).
                new Mode { Name = "b3s05", Settings = new ReconSettings { Mode = ReconMode.Block, Block = 3, StaleWeight = 0.05f } },
                new Mode { Name = "b3s20", Settings = new ReconSettings { Mode = ReconMode.Block, Block = 3, StaleWeight = 0.2f } },
                new Mode { Name = "b3c15", Settings = new ReconSettings { Mode = ReconMode.Block, Block = 3, ClipSigma = 1.5f } },
                new Mode { Name = "b4s05", Settings = new ReconSettings { Mode = ReconMode.Block, Block = 4, StaleWeight = 0.05f } },
                new Mode { Name = "b4c15", Settings = new ReconSettings { Mode = ReconMode.Block, Block = 4, ClipSigma = 1.5f } },
                new Mode { Name = "l50", Settings = new ReconSettings { Mode = ReconMode.Low, Resolution = 0.5f } },
                new Mode { Name = "l25", Settings = new ReconSettings { Mode = ReconMode.Low, Resolution = 0.25f } },
                new Mode { Name = "l25j", Settings = new ReconSettings { Mode = ReconMode.Low, Resolution = 0.25f, Jitter = true } },
            };
        }

        private static List<Scenario> Scenarios()
        {
            // The drift is in the planet frame; (1, 0, 0) is not tied to the camera, the scenes measure what the history does with it.
            return new List<Scenario>
            {
                new Scenario { Name = "static", Frames = 40 },
                new Scenario { Name = "turn2", Frames = 62, Yaw = i => Mathf.Clamp(i - 6, 0, 26) * 2f, LastMove = 32 },
                new Scenario { Name = "turn12", Frames = 46, Yaw = i => Mathf.Clamp(i - 6, 0, 10) * 12f, LastMove = 16 },
                new Scenario { Name = "fly100", Frames = 40, Shift = i => new Vector3(1.667f * Mathf.Max(i - 6, 0), 0f, 0f), LastMove = 39 },
                new Scenario { Name = "wind", Frames = 40, Drift = new Vector3(0.5f, 0.5f, 0f), LastMove = 39 },
                // 25 m a frame is about 2 pixels a frame at the ground view's cloud distance (a trail test, not a weather).
                new Scenario { Name = "windfast", Frames = 40, Drift = new Vector3(25f, 0f, 0f), LastMove = 39 },
            };
        }

        private static float Display(Color c)
        {
            float a = c.a;
            float lr = c.r + (1f - a) * Sky.r, lg = c.g + (1f - a) * Sky.g, lb = c.b + (1f - a) * Sky.b;
            float x = 0.2126f * lr + 0.7152f * lg + 0.0722f * lb;
            return x / (1f + x);
        }

        private Texture2D readback;

        private void Read(RenderTexture rt, float[] display, float[] alpha)
        {
            if (readback == null) readback = new Texture2D(rt.width, rt.height, TextureFormat.RGBAHalf, false, true);
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

        private void SetDrift(TemporalRig rig, Vector3 drift, int frame)
        {
            for (int l = 0; l < rig.Job.layers.Length; l++) rig.Renderer.Noise.SetState(l, drift * frame, 0f);
        }

        private void Render(TemporalRig rig, LayerReconstructor recon, ReconSettings s, int number)
        {
            var cb = new CommandBuffer { name = "T05 quality" };
            recon.Record(cb, rig.Renderer, rig.Frame, Cam, s, number, null, null, null);
            Graphics.ExecuteCommandBuffer(cb);
            cb.Release();
        }

        public override IEnumerator Run()
        {
            string input = WeatherPass.Argument("-in");
            var rig = new TemporalRig();
            bool ready = input != null && File.Exists(Path.Combine(input, "jobs.json")) && rig.Init();
            Sink.Flag("temporal_available", ready);
            if (!ready) yield break;
            LayersJobFile file = JsonUtility.FromJson<LayersJobFile>(File.ReadAllText(Path.Combine(input, "jobs.json")));
            var rt = new RenderTexture(W, H, 24, RenderTextureFormat.DefaultHDR);
            rt.Create();
            Cam.targetTexture = rt;
            var refRecon = new LayerReconstructor();
            refRecon.Init(Shader.Find(LayerReconstructor.ShaderName));
            var full = new ReconSettings { Mode = ReconMode.Full };
            int pixels = W * H;
            var img = new float[pixels];
            var alpha = new float[pixels];
            var sum = new float[pixels];
            var clock = System.Diagnostics.Stopwatch.StartNew();
            string artefacts = Path.Combine(Output, "temporal");
            Directory.CreateDirectory(artefacts);
            foreach (string jobId in new[] { "V-ground", "V-stack-below" })
            {
                LayersJob job = null;
                foreach (LayersJob j in file.jobs) if (j.id == jobId) job = j;
                if (job == null) { Sink.Flag("temporal_job_" + jobId, false); continue; }
                rig.Load(job, input);
                rig.Frame.UseProductCost();
                foreach (Scenario sc in Scenarios())
                {
                    // The references of the scenario: the mean of RefFrames marches of the full pass at every pose.
                    var refDisplay = new float[sc.Frames][];
                    var refAlpha = new float[sc.Frames][];
                    for (int i = 0; i < sc.Frames; i++)
                    {
                        rig.Pose(Cam, sc.Yaw(i), sc.Shift(i));
                        SetDrift(rig, sc.Drift, i);
                        Array.Clear(sum, 0, pixels);
                        Array.Clear(alpha, 0, pixels);
                        var a = new float[pixels];
                        for (int k = 0; k < RefFrames; k++)
                        {
                            Render(rig, refRecon, full, 9000 + i * 7 + k);
                            Read(refRecon.Output, img, a);
                            for (int p = 0; p < pixels; p++) { sum[p] += img[p] / RefFrames; alpha[p] += a[p] / RefFrames; }
                        }
                        refDisplay[i] = (float[])sum.Clone();
                        refAlpha[i] = (float[])alpha.Clone();
                        if (i % 8 == 7) yield return null;
                    }
                    double fullFloor = 1e9;
                    foreach (Mode mode in Modes())
                    {
                        if (clock.Elapsed.TotalMinutes > 14.0) { Sink.Flag("temporal_completed", false); yield break; }
                        rig.Recon.Reset();
                        var movingErr = new List<double>();
                        var stillErr = new List<double>();
                        var trail = new List<double>();
                        var shimmer = new List<double>();
                        float[] previous = null;
                        int converged = -1, convergedFloor = -1;
                        var outAlpha = new float[pixels];
                        string key = "temporal." + jobId + "." + sc.Name + "." + mode.Name;
                        for (int i = 0; i < sc.Frames; i++)
                        {
                            rig.Pose(Cam, sc.Yaw(i), sc.Shift(i));
                            SetDrift(rig, sc.Drift, i);
                            mode.Settings.DriftPerFrame = sc.Drift;
                            Render(rig, rig.Recon, mode.Settings, 100 + i);
                            var d = new float[pixels];
                            Read(rig.Recon.Output, d, outAlpha);
                            double err = 0, trailCount = 0, region = 0, lum = 0, flicker = 0;
                            for (int p = 0; p < pixels; p++)
                            {
                                // The region is where the reference or the output holds cloud, so that a trail in a pixel the cloud left counts.
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
                                if (converged < 0 && sc.Name == "static" && err <= 0.02 * lum / region) converged = i;
                                if (convergedFloor < 0 && sc.Name == "static" && err <= 1.25 * fullFloor) convergedFloor = i;
                                bool moving = sc.LastMove > 0 && i > 6 && i <= sc.LastMove;
                                bool settled = sc.LastMove == 0 ? i >= 12 : i > sc.LastMove + 10;
                                if (moving) { movingErr.Add(err); trail.Add(trailCount / region); }
                                else if (settled) { stillErr.Add(err); if (previous != null) shimmer.Add(flicker / region); }
                            }
                            previous = d;
                            if (jobId == "V-stack-below" && (sc.Name == "turn12" && i == 10 || sc.Name == "static" && i == sc.Frames - 1))
                                File.WriteAllBytes(Path.Combine(artefacts, key.Replace("temporal.", "") + "_f" + i + ".png"), Png(d, refDisplay[i], W, H));
                        }
                        Sink.Info(key + ".ghost_move", Mean(movingErr));
                        Sink.Info(key + ".ghost_still", Mean(stillErr));
                        Sink.Info(key + ".trail_px", Mean(trail));
                        Sink.Info(key + ".shimmer", Mean(shimmer));
                        if (sc.Name == "static")
                        {
                            // The full pass's own error (one jittered march against the mean of four) is the noise floor of the reference.
                            if (mode.Name == "full") fullFloor = Mean(stillErr);
                            Sink.Info(key + ".converge_frames", converged);
                            Sink.Info(key + ".converge_floor_frames", convergedFloor);
                            Sink.Info(key + ".floor", fullFloor);
                        }
                        yield return null;
                    }
                }
                // A floating-origin shift of 5 km: the same frames with and without a shift of the camera and the planet's centre, compared at the last frame.
                foreach (string name in new[] { "b2", "b3" })
                {
                    Mode mode = Modes().Find(m => m.Name == name);
                    var plain = new float[pixels];
                    var shifted = new float[pixels];
                    for (int run = 0; run < 2; run++)
                    {
                        rig.Recon.Reset();
                        rig.Frame.PlanetCenter = Vector3.zero;
                        for (int i = 0; i < 14; i++)
                        {
                            Vector3 shift = run == 1 && i >= 8 ? new Vector3(5000f, 0f, 0f) : Vector3.zero;
                            rig.Frame.PlanetCenter = shift;
                            rig.Pose(Cam, 0f, shift);
                            Render(rig, rig.Recon, mode.Settings, 300 + i);
                        }
                        Read(rig.Recon.Output, run == 0 ? plain : shifted, null);
                    }
                    double diff = 0, mean = 0;
                    for (int p = 0; p < pixels; p++) { diff += Math.Abs(plain[p] - shifted[p]); mean += plain[p]; }
                    Sink.Info("temporal." + jobId + ".origin_shift." + name, diff / Math.Max(mean, 1e-9));
                    rig.Frame.PlanetCenter = Vector3.zero;
                }
            }
            refRecon.Dispose();
            rig.Dispose();
            Cam.targetTexture = Target;
            rt.Release();
            Sink.Flag("temporal_completed", true);
        }

        private static double Mean(List<double> values)
        {
            if (values.Count == 0) return double.NaN;
            double s = 0;
            foreach (double v in values) s += v;
            return s / values.Count;
        }

        // A picture: the output (left), the reference (middle) and the difference as a heat map (right).
        private static byte[] Png(float[] output, float[] reference, int w, int h)
        {
            var tex = new Texture2D(w * 3, h, TextureFormat.RGB24, false, true);
            var px = new Color[w * 3 * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float o = output[y * w + x], r = reference[y * w + x];
                    float e = Mathf.Clamp01(Mathf.Abs(o - r) * 8f);
                    px[y * w * 3 + x] = new Color(o, o, o);
                    px[y * w * 3 + w + x] = new Color(r, r, r);
                    px[y * w * 3 + 2 * w + x] = new Color(e, e * 0.4f, 1f - e);
                }
            tex.SetPixels(px);
            byte[] png = tex.EncodeToPNG();
            UnityEngine.Object.Destroy(tex);
            return png;
        }
    }

    // ------------------------------------------------------------------------------------------------------------------------------
    // Look of the cost measures
    // ------------------------------------------------------------------------------------------------------------------------------
    // Each cost measure of the pass against the pass without it, on every view of the table, the same pose, the same frame number (so the same
    // jitter pattern): the mean absolute difference of the display value over the pixels either picture covers, and the bias of the mean. The
    // pictures (colour, left to right: without the measure, with it, the difference times 8) go to <out>/look5/.
    [WeatherScene("layers-look5")]
    public sealed class LayersLookTemporalScene : WeatherSceneBase
    {
        private sealed class Variant
        {
            public string Name;
            public int UpMode, Stride = 2, SunSteps;
            public float K = 0.5f, Growth, Ratio = 2f;
            public bool Picture;
        }

        private static readonly Variant[] Variants =
        {
            new Variant { Name = "base" },
            // The measures against the main branch's pass: the step growth at 1, 2 and 3, the sun march reused over three lit steps, and the up taps
            // reused and moved by the ray's progress (against the same taps at every sample: its own base is the taps variant).
            new Variant { Name = "tg1", Growth = 1f },
            new Variant { Name = "tg2", Growth = 2f, Picture = true },
            new Variant { Name = "tg3", Growth = 3f },
            new Variant { Name = "tg2s3", Growth = 2f, Stride = 3 },
            new Variant { Name = "tg2s3x", Growth = 2f, Stride = 3, SunSteps = 2, Ratio = 3f },
        };

        private static Color Mapped(Color c)
        {
            var sky = new Color(0.42f, 0.52f, 0.72f);
            float r = c.r + (1f - c.a) * sky.r, g = c.g + (1f - c.a) * sky.g, b = c.b + (1f - c.a) * sky.b;
            return new Color(r / (1f + r), g / (1f + g), b / (1f + b));
        }

        public override IEnumerator Run()
        {
            string input = WeatherPass.Argument("-in");
            var rig = new TemporalRig();
            bool ready = input != null && File.Exists(Path.Combine(input, "jobs.json")) && rig.Init();
            Sink.Flag("look5_available", ready);
            if (!ready) yield break;
            LayersJobFile file = JsonUtility.FromJson<LayersJobFile>(File.ReadAllText(Path.Combine(input, "jobs.json")));
            var rt = new RenderTexture(640, 360, 24, RenderTextureFormat.DefaultHDR);
            rt.Create();
            Cam.targetTexture = rt;
            var tex = new Texture2D(640, 360, TextureFormat.RGBAHalf, false, true);
            string folder = Path.Combine(Output, "look5");
            Directory.CreateDirectory(folder);
            foreach (LayersJob job in file.jobs)
            {
                if (!job.id.StartsWith("V-")) continue;
                rig.Load(job, input);
                rig.Pose(Cam, 0f, Vector3.zero);
                Color[] baseline = null;
                foreach (Variant v in Variants)
                {
                    LayerFrame f = rig.Frame;
                    f.TransmittanceGrowth = v.Growth;
                    f.LightStride = v.Stride;
                    TemporalRig.SetUp(f.Light, v.UpMode);
                    f.Light.SunStepsOverride = v.SunSteps;
                    f.Light.TapRatio = v.Ratio;
                    rig.Recon.Reset();
                    var cb = new CommandBuffer { name = "T05 look" };
                    rig.Recon.Record(cb, rig.Renderer, f, Cam, new ReconSettings { Mode = ReconMode.Full }, 777, null, null, null);
                    Graphics.ExecuteCommandBuffer(cb);
                    cb.Release();
                    RenderTexture.active = rig.Recon.Output;
                    tex.ReadPixels(new Rect(0, 0, 640, 360), 0, 0, false);
                    RenderTexture.active = null;
                    Color[] px = tex.GetPixels();
                    if (v.Name == "base") baseline = px;
                    double diff = 0, region = 0, sumBase = 0, sumVar = 0, aDiff = 0;
                    for (int i = 0; i < px.Length; i++)
                    {
                        if (baseline[i].a < 0.02f && px[i].a < 0.02f) continue;
                        Color a = Mapped(baseline[i]), b = Mapped(px[i]);
                        diff += (Math.Abs(a.r - b.r) + Math.Abs(a.g - b.g) + Math.Abs(a.b - b.b)) / 3.0;
                        sumBase += (a.r + a.g + a.b) / 3.0;
                        sumVar += (b.r + b.g + b.b) / 3.0;
                        aDiff += Math.Abs(baseline[i].a - px[i].a);
                        region += 1;
                    }
                    string key = "look5." + job.id + "." + v.Name;
                    Sink.Info(key + ".diff", region > 0 ? diff / region : 0.0);
                    Sink.Info(key + ".bias", sumBase > 0 ? (sumVar - sumBase) / sumBase : 0.0);
                    Sink.Info(key + ".alpha_diff", region > 0 ? aDiff / region : 0.0);
                    if (v.Picture && (job.id == "V-stack-below" || job.id == "V-ground" || job.id == "V-above"))
                    {
                        var pic = new Texture2D(640 * 3, 360, TextureFormat.RGB24, false, true);
                        var all = new Color[640 * 3 * 360];
                        for (int y = 0; y < 360; y++)
                            for (int x = 0; x < 640; x++)
                            {
                                Color a = Mapped(baseline[y * 640 + x]), b = Mapped(px[y * 640 + x]);
                                all[y * 1920 + x] = a;
                                all[y * 1920 + 640 + x] = b;
                                all[y * 1920 + 1280 + x] = new Color(Mathf.Abs(a.r - b.r) * 8f, Mathf.Abs(a.g - b.g) * 8f, Mathf.Abs(a.b - b.b) * 8f);
                            }
                        pic.SetPixels(all);
                        File.WriteAllBytes(Path.Combine(folder, job.id + "." + v.Name + ".png"), pic.EncodeToPNG());
                        UnityEngine.Object.Destroy(pic);
                    }
                }
                yield return null;
            }
            rig.Dispose();
            Cam.targetTexture = Target;
            rt.Release();
            Sink.Flag("look5_completed", true);
        }
    }
}
#endif
