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
    // The cost of the layer pass (package T01, -mode scene -scene layers-cost -in <folder>, a timing run: the lock is exclusive and the proxy
    // gives the GPU timestamps): the milliseconds of the march and of the depth-aware composite on the view table's jobs (V-*) at 1707x960
    // and 2560x1440, the march at half size with 32, 48 and 64 steps per span, and at full size with 48. Production sampling: the interleaved
    // gradient noise, the step growing with the distance, the transmittance cut at 0.01, three sun steps (four on the pictures' jobs).
    [WeatherScene("layers-cost")]
    public sealed class LayersCostScene : WeatherSceneBase
    {
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
            var configs = new[] { new[] { 0.5f, 32f }, new[] { 0.5f, 48f }, new[] { 0.5f, 64f }, new[] { 1f, 48f } };
            var segments = new float[8];
            double worstHalf48At1707 = 0, worstHalf48At2560 = 0, worstHalf32At1707 = 0;
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
                    foreach (float[] config in configs)
                    {
                        frame.Resolution = config[0];
                        frame.MaxSteps = (int)config[1];
                        for (int i = 0; i < 60; i++)
                        {
                            frame.FrameNumber = FrameIndex;
                            renderer.Prepare(Cam, frame, depthId, markStart, markMarched, markEnd);
                            yield return Frames(1);
                        }
                        var march = new List<double>();
                        var composite = new List<double>();
                        int guard = 0;
                        while (march.Count < 120 && guard++ < 2400)
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
                        double ms = Median(march) + Median(composite);
                        string label = job.id + ".r" + config[0].ToString("0.##", CultureInfo.InvariantCulture) + ".s" + (int)config[1] + "." + sz;
                        Sink.Info("cost.march." + label, Median(march));
                        Sink.Info("cost.composite." + label, Median(composite));
                        Sink.Info("cost.total." + label, ms);
                        if (config[0] == 0.5f && config[1] == 48f) { if (size.x == 1707) worstHalf48At1707 = Math.Max(worstHalf48At1707, ms); else worstHalf48At2560 = Math.Max(worstHalf48At2560, ms); }
                        if (config[0] == 0.5f && config[1] == 32f && size.x == 1707) worstHalf32At1707 = Math.Max(worstHalf32At1707, ms);
                    }
                }
                Cam.targetTexture = Target;
                rt.Release();
            }
            Sink.Info("cost_half_32_1707x960_ms", worstHalf32At1707);
            Sink.Info("cost_half_48_1707x960_ms", worstHalf48At1707);
            Sink.Info("cost_half_48_2560x1440_ms", worstHalf48At2560);
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
