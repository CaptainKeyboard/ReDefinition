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
    // The cost of the cloud light (package T03, -mode scene -scene light-cost -in <folder>, a timing run: exclusive lock, proxy timestamps): the
    // milliseconds of the layer pass at 1707x960 (half size march, 48 steps) on the jobs of <folder>/jobs.json for each light variant of the job
    // (its own light numbers, the cache on or off), and the milliseconds of the cache fill alone. The light variants are the jobs' own: the
    // job id is "<view>|<variant>"; cache jobs read an atlas that was filled before the timing. Output: cost.<job id>.march / composite / total
    // and cost.fill.<job id> as info lines.
    [WeatherScene("light-cost")]
    public sealed class LightCostScene : WeatherSceneBase
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
            LightJobFile file = JsonUtility.FromJson<LightJobFile>(File.ReadAllText(Path.Combine(input, "jobs.json")));
            var renderer = new LayerRenderer();
            if (!renderer.Init(shader)) { Sink.Flag("light_compiled", false); yield break; }
            var dummy = new RenderTexture(4, 4, 0, RenderTextureFormat.RFloat);
            dummy.Create();
            RenderTexture.active = dummy;
            GL.Clear(false, true, Color.black);
            RenderTexture.active = null;
            var depthId = new RenderTargetIdentifier(dummy);
            renderer.Attach(Cam, CameraEvent.AfterSkybox);
            var rt = new RenderTexture(1707, 960, 24, RenderTextureFormat.DefaultHDR) { name = "light cost target" };
            rt.Create();
            Cam.targetTexture = rt;
            var cache = new LightCache();
            var segments = new float[8];
            foreach (LightJob job in file.jobs)
            {
                LayersJob lj = job.layerJob;
                LayerFrame frame = LayersSetup.Load(lj, input, renderer);
                LightScene.Configure(job, frame.Light);
                frame.UseSceneDepth = true;
                frame.Resolution = 0.5f;
                frame.MaxSteps = 48;
                frame.MinStepM = 25f;
                frame.StepGrowth = 0.0002f;
                frame.Jitter = 1f;
                frame.EarlyOut = 0.01f;
                Cam.fieldOfView = lj.fovV;
                Cam.transform.position = LayersSetup.V(lj.position);
                Cam.transform.rotation = Quaternion.LookRotation(LayersSetup.V(lj.forward), LayersSetup.V(lj.up));
                if (job.cache != 0)
                {
                    cache.Min = LayersSetup.V(job.cacheMin);
                    cache.Size = LayersSetup.V(job.cacheSize);
                    cache.Enabled = false;
                    frame.Light.Cache = cache;
                    renderer.SetCamera(Cam.transform.position, Cam.transform.forward, Cam.transform.right, Cam.transform.up, Vector2.zero);
                    renderer.Apply(frame);
                    cache.Fill(renderer.Material);
                    yield return Frames(2);
                    cache.Enabled = true;
                }
                if (job.cache != 0)
                {
                    // The share of the light taps the cache saves: the taps of the aid view with the atlas on over with it off.
                    float on = TapSum(renderer, frame, lj, dummy, true), off = TapSum(renderer, frame, lj, dummy, false);
                    yield return new WaitForEndOfFrame();
                    Sink.Info("cost.taps_cache_over_march." + job.id, on / Math.Max(off, 1f));
                }
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
                Sink.Info("cost.march." + job.id, Median(march));
                Sink.Info("cost.composite." + job.id, Median(composite));
                Sink.Info("cost.total." + job.id, Median(march) + Median(composite));
                if (job.cache != 0)
                {
                    // The fill alone: a command buffer that marks around the fill pass.
                    var fill = new CommandBuffer { name = "light cache fill timing" };
                    var fills = new List<double>();
                    renderer.Detach();
                    Cam.AddCommandBuffer(CameraEvent.AfterSkybox, fill);
                    cache.Ensure();
                    int g2 = 0;
                    while (fills.Count < 60 && g2++ < 1200)
                    {
                        fill.Clear();
                        WeatherGpuTimer.Mark(fill, WeatherGpuTimer.SlabStart);
                        fill.Blit(BuiltinRenderTextureType.None, cache.Atlas, renderer.Material, LightCache.PassFill);
                        WeatherGpuTimer.Mark(fill, WeatherGpuTimer.SlabMarched);
                        WeatherGpuTimer.Mark(fill, WeatherGpuTimer.SlabComposited);
                        yield return Frames(1);
                        float total;
                        int number;
                        if (!WeatherGpuTimer.Read(segments, out total, out number)) continue;
                        float m = segments[WeatherGpuTimer.SlabMarched - WeatherGpuTimer.SlabStart];
                        if (m >= 0f) fills.Add(m);
                    }
                    Cam.RemoveCommandBuffer(CameraEvent.AfterSkybox, fill);
                    renderer.Attach(Cam, CameraEvent.AfterSkybox);
                    Sink.Info("cost.fill." + job.id, Median(fills));
                }
            }
            Cam.targetTexture = Target;
            rt.Release();
            cache.Dispose();
        }

        private static float TapSum(LayerRenderer renderer, LayerFrame frame, LayersJob lj, RenderTexture dummy, bool cacheOn)
        {
            const int w = 160, h = 90;
            float tan = Mathf.Tan(lj.fovV * Mathf.Deg2Rad * 0.5f);
            renderer.SetCamera(LayersSetup.V(lj.position), LayersSetup.V(lj.forward), LayersSetup.V(lj.right) * tan * (16f / 9f), LayersSetup.V(lj.up) * tan, Vector2.zero);
            frame.Light.Cache.Enabled = cacheOn;
            frame.AidMode = 4;
            renderer.Material.SetTexture("_SceneDepth", dummy);
            renderer.Apply(frame);
            var target = new RenderTexture(w, h, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear) { filterMode = FilterMode.Point };
            target.Create();
            Graphics.Blit(null, target, renderer.Material, LayerRenderer.PassMarch);
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = target;
            var copy = new Texture2D(w, h, TextureFormat.RGBAFloat, false, true);
            copy.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            copy.Apply(false);
            RenderTexture.active = previous;
            float sum = 0f;
            foreach (Color c in copy.GetPixels()) sum += c.g;
            UnityEngine.Object.Destroy(copy);
            target.Release();
            frame.AidMode = 0;
            frame.Light.Cache.Enabled = true;
            return sum;
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
