using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using ReDefinition.Atmospheres;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.WeatherPlayer
{
    // The cost of the layer pass (package T01 and its cost work package T01c, -mode scene -scene layers-cost -in <folder>, a timing run: the
    // lock is exclusive and the proxy gives the GPU timestamps): the milliseconds of the march and of the depth-aware composite on the view
    // table's jobs (V-*) for a table of variants, each one named and run in the same start so that the rows compare. Production sampling:
    // the interleaved gradient noise, the step growing with the distance, the transmittance cut at 0.01, three sun steps. The variants
    // with a debug mask (cost attribution) switch parts of the shader off and are not pictures of the product.
    [WeatherScene("layers-cost")]
    public sealed class LayersCostScene : WeatherSceneBase
    {
        private sealed class Variant
        {
            public string Name;
            public float Resolution = 0.5f;
            public int Steps = 48;
            public bool Bound, Large = true;
            public float Leap = 1f, Cut, Cheap = 1f, Fog = 1f;
            public int Debug, Stride = 1;
        }

        // The table: the baseline is the state of the layer core before the cost work (no bound, no leap, every step marches to the sun).
        private static readonly Variant[] Variants =
        {
            new Variant { Name = "base", Bound = false },
            new Variant { Name = "bound", Bound = true },
            new Variant { Name = "attr-nolight", Bound = true, Debug = 1, Large = false },
            new Variant { Name = "attr-nonoise", Bound = true, Debug = 6, Large = false },
            new Variant { Name = "attr-bare", Bound = true, Debug = 7, Large = false },
            new Variant { Name = "stride2", Bound = true, Stride = 2, Large = false },
            new Variant { Name = "stride3", Bound = true, Stride = 3, Large = false },
            new Variant { Name = "stride4", Bound = true, Stride = 4, Large = false },
            new Variant { Name = "prod", Bound = true, Cut = 0.05f, Stride = 2 },
            new Variant { Name = "prod-s3", Bound = true, Cut = 0.05f, Stride = 3, Large = false },
            new Variant { Name = "prod-c10s3", Bound = true, Cut = 0.10f, Stride = 3, Large = false },
            new Variant { Name = "prod-s32", Bound = true, Cut = 0.05f, Stride = 2, Steps = 32 },
            new Variant { Name = "prod-q", Bound = true, Cut = 0.05f, Stride = 2, Resolution = 0.25f },
            new Variant { Name = "base-s32", Bound = false, Steps = 32 },
            new Variant { Name = "base-q", Bound = false, Resolution = 0.25f, Large = false },
            new Variant { Name = "prod-full", Bound = true, Cut = 0.05f, Stride = 2, Resolution = 1f, Large = false },
        };

        private readonly Action<CommandBuffer> markStart = cb => WeatherGpuTimer.Mark(cb, WeatherGpuTimer.SlabStart),
            markMarched = cb => WeatherGpuTimer.Mark(cb, WeatherGpuTimer.SlabMarched),
            markEnd = cb => WeatherGpuTimer.Mark(cb, WeatherGpuTimer.SlabComposited);

        public override IEnumerator Run()
        {
            string input = WeatherPass.Argument("-in");
            Shader shader = Shader.Find(LayerRenderer.ShaderName);
            bool ready = input != null && File.Exists(Path.Combine(input, "jobs.json")) && shader != null && WeatherGpuTimer.Start();
            Sink.Flag("cost_timers_available", ready);
            if (!ready) yield break;
            LayersJobFile file = JsonUtility.FromJson<LayersJobFile>(File.ReadAllText(Path.Combine(input, "jobs.json")));
            var renderer = new LayerRenderer();
            if (!renderer.Init(shader)) { Sink.Flag("layers_compiled", false); yield break; }
            var dummy = new RenderTexture(4, 4, 0, RenderTextureFormat.RFloat);
            dummy.Create();
            RenderTexture.active = dummy;
            GL.Clear(false, true, Color.black);
            RenderTexture.active = null;
            var depthId = new RenderTargetIdentifier(dummy);
            renderer.Attach(Cam, CameraEvent.AfterSkybox);
            var sizes = new[] { new Vector2Int(1707, 960), new Vector2Int(2560, 1440) };
            var segments = new float[8];
            var clock = System.Diagnostics.Stopwatch.StartNew();
            foreach (Vector2Int size in sizes)
            {
                var rt = new RenderTexture(size.x, size.y, 24, RenderTextureFormat.DefaultHDR) { name = "layers cost target" };
                rt.Create();
                Cam.targetTexture = rt;
                string sz = size.x + "x" + size.y;
                foreach (LayersJob job in file.jobs)
                {
                    if (!job.id.StartsWith("V-")) continue;
                    LayerFrame frame = LayersSetup.Load(job, input, renderer);
                    // The game always has the scene's depth: both passes read it (a zero depth means no limit here).
                    frame.UseSceneDepth = true;
                    frame.SunSteps = 3;
                    frame.MinStepM = 25f;
                    frame.StepGrowth = 0.0002f;
                    frame.Jitter = 1f;
                    frame.EarlyOut = 0.01f;
                    Cam.fieldOfView = job.fovV;
                    Cam.transform.position = LayersSetup.V(job.position);
                    Cam.transform.rotation = Quaternion.LookRotation(LayersSetup.V(job.forward), LayersSetup.V(job.up));
                    foreach (Variant v in Variants)
                    {
                        if (size.x > 1707 && !v.Large) continue;
                        if (clock.Elapsed.TotalMinutes > 16.0) { Sink.Flag("cost_run_completed", false); yield break; }
                        frame.Resolution = v.Resolution;
                        frame.MaxSteps = v.Steps;
                        frame.CoverageBound = v.Bound;
                        frame.EmptyLeap = v.Leap;
                        frame.LightCutoff = v.Cut;
                        frame.CheapShare = v.Cheap;
                        frame.FogShare = v.Fog;
                        frame.CostDebug = v.Debug;
                        frame.LightStride = v.Stride;
                        for (int i = 0; i < 24; i++)
                        {
                            frame.FrameNumber = FrameIndex;
                            renderer.Prepare(Cam, frame, depthId, markStart, markMarched, markEnd);
                            yield return Frames(1);
                        }
                        var march = new List<double>();
                        var composite = new List<double>();
                        int guard = 0;
                        while (march.Count < 60 && guard++ < 1200)
                        {
                            frame.FrameNumber = FrameIndex;
                            renderer.Prepare(Cam, frame, depthId, markStart, markMarched, markEnd);
                            yield return Frames(1);
                            float total;
                            int number;
                            if (!WeatherGpuTimer.Read(segments, out total, out number)) continue;
                            float m = segments[WeatherGpuTimer.SlabMarched - WeatherGpuTimer.SlabStart], c = segments[WeatherGpuTimer.SlabComposited - WeatherGpuTimer.SlabStart];
                            if (m >= 0f && c >= 0f) { march.Add(m); composite.Add(c); }
                        }
                        if (march.Count < 60) Sink.Info("cost.short." + job.id + "." + v.Name + "." + sz, march.Count);
                        double ms = Median(march) + Median(composite);
                        string label = job.id + "." + v.Name + "." + sz;
                        Sink.Info("cost.march." + label, Median(march));
                        Sink.Info("cost.composite." + label, Median(composite));
                        Sink.Info("cost.total." + label, ms);
                    }
                }
                Cam.targetTexture = Target;
                rt.Release();
            }
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
}
