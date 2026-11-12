using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using ReDefinition.Atmospheres;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.WeatherPlayer
{
    // The cost of the noise (package T02, -mode scene -scene layers-noise-cost -in <folder>, a timing run: the lock is exclusive and the proxy
    // gives the GPU timestamps). The production sampling of the layer cost table (LayersCostScene, variant "prod": coverage bound, light cut-off
    // 0.05, every second step lit, 48 steps, half resolution, 1707 x 960) on the view table's jobs (V-*), once per job. A job names what it
    // switches off by its id suffix: "-nonoise" (the job's noise block), "-nountile" and "-nocurl" (the noise block's switches), "-nolod" (the
    // noise block's LOD switch) and "-noero" (the erosion off through the cost attribution mask, bit 2). The rows compare within one start.
    [WeatherScene("layers-noise-cost")]
    public sealed class LayersNoiseCostScene : WeatherSceneBase
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
            var segments = new float[8];
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var rt = new RenderTexture(1707, 960, 24, RenderTextureFormat.DefaultHDR) { name = "noise cost target" };
            rt.Create();
            Cam.targetTexture = rt;
            foreach (LayersJob job in file.jobs)
            {
                if (!job.id.StartsWith("V-")) continue;
                if (clock.Elapsed.TotalMinutes > 16.0) { Sink.Flag("cost_run_completed", false); yield break; }
                LayerFrame frame = LayersSetup.Load(job, input, renderer);
                frame.UseSceneDepth = true;
                frame.SunSteps = 3;
                frame.MinStepM = 25f;
                frame.StepGrowth = 0.0002f;
                frame.Jitter = 1f;
                frame.EarlyOut = 0.01f;
                frame.Resolution = 0.5f;
                frame.MaxSteps = 48;
                frame.CoverageBound = true;
                frame.EmptyLeap = 1f;
                frame.LightCutoff = 0.05f;
                frame.CheapShare = 1f;
                frame.FogShare = 1f;
                frame.LightStride = 2;
                frame.CostDebug = job.id.EndsWith("-noero") ? 4 : 0;
                Cam.fieldOfView = job.fovV;
                Cam.transform.position = LayersSetup.V(job.position);
                Cam.transform.rotation = Quaternion.LookRotation(LayersSetup.V(job.forward), LayersSetup.V(job.up));
                for (int i = 0; i < 24; i++)
                {
                    frame.FrameNumber = FrameIndex;
                    renderer.Prepare(Cam, frame, depthId, markStart, markMarched, markEnd);
                    yield return Frames(1);
                }
                var totals = new List<double>();
                int guard = 0;
                while (totals.Count < 60 && guard++ < 1200)
                {
                    frame.FrameNumber = FrameIndex;
                    renderer.Prepare(Cam, frame, depthId, markStart, markMarched, markEnd);
                    yield return Frames(1);
                    float total;
                    int number;
                    if (!WeatherGpuTimer.Read(segments, out total, out number)) continue;
                    float m = segments[WeatherGpuTimer.SlabMarched - WeatherGpuTimer.SlabStart], c = segments[WeatherGpuTimer.SlabComposited - WeatherGpuTimer.SlabStart];
                    if (m >= 0f && c >= 0f) totals.Add(m + c);
                }
                if (totals.Count < 60) Sink.Info("noisecost.short." + job.id, totals.Count);
                totals.Sort();
                Sink.Info("noisecost." + job.id + ".ms", totals.Count > 0 ? totals[totals.Count / 2] : double.NaN);
            }
            Cam.targetTexture = Target;
            rt.Release();
            Sink.Flag("cost_run_completed", true);
        }
    }
}
