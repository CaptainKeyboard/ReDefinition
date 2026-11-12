using System;
using System.Collections;
using System.Collections.Generic;
using ReDefinition.Atmospheres;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.WeatherPlayer
{
    // The cost of the wet-surface overlay and of the droplet pass (package T08, -mode scene -scene wet-cost, a timing run: exclusive lock,
    // proxy timestamps): the GPU milliseconds of the overlay in Unity's deferred G-buffer with puddles, and of the droplet host composite
    // at full wetness, at the player's 1280 x 720 target. Output: cost.wet_gbuffer_ms, cost.wet_gbuffer_ms_per_mpix, cost.droplet_host_ms
    // and cost.droplet_host_ms_per_mpix as info lines, and the limits of the plan: the overlay at most 0.3 ms and the droplets at most
    // 0.25 ms per 1920 x 1080 frame (per-megapixel figure times 2.07).
    [WeatherScene("wet-cost")]
    public sealed class WetCostScene : WeatherSceneBase
    {
        private const int Warm = 60, Samples = 120;

        public override IEnumerator Run()
        {
            Shader wetShader = Shader.Find(WetSurfacesPass.ShaderName), dropShader = Shader.Find(DropletPass.ShaderName);
            bool ready = wetShader != null && dropShader != null && WeatherGpuTimer.Start();
            Sink.Flag("cost_timers_available", ready);
            if (!ready) yield break;
            var wet = new WetSurfacesPass(wetShader);
            var drops = new DropletPass(dropShader);
            Cam.renderingPath = RenderingPath.DeferredShading;
            yield return Frames(4);
            const float X = 3000f, Z = -800f;
            Pose(new CameraPose(new Vector3(X, TerrainHeight(X, Z) + 400f, Z), 0f, 55f));
            double mpix = Width * (double)Height / 1.0e6;

            // --- The overlay at BeforeLighting with a puddle field and rings.
            var tuning = WetnessTuning.Defaults();
            WetLook look = WetnessLookMath.Eval(1f, 6f, 0.3f, 288f, 273.15f, 0f, ref tuning);
            var draw = new WetDraw
            {
                Look = look,
                Tuning = tuning,
                CentreRel = new Vector3(0f, -6.0e6f, 0f),
                East = Vector3.right,
                North = Vector3.forward,
                EastM = Mathf.Repeat(X, WetSurfacesPass.WrapM),
                NorthM = Mathf.Repeat(Z, WetSurfacesPass.WrapM),
                ClockS = 1f,
                Strength = 1f
            };
            var cmd = new CommandBuffer { name = "wet-cost" };
            Cam.AddCommandBuffer(CameraEvent.BeforeLighting, cmd);
            var segments = new float[8];
            var wetMs = new List<double>();
            for (int i = 0; i < Warm + 2400 && wetMs.Count < Samples; i++)
            {
                cmd.Clear();
                WeatherGpuTimer.Mark(cmd, WeatherGpuTimer.SlabStart);
                wet.RecordGBuffer(cmd, ref draw);
                WeatherGpuTimer.Mark(cmd, WeatherGpuTimer.SlabMarched);
                WeatherGpuTimer.Mark(cmd, WeatherGpuTimer.SlabComposited);
                yield return Frames(1);
                float total;
                int number;
                if (i < Warm || !WeatherGpuTimer.Read(segments, out total, out number)) continue;
                float m = segments[WeatherGpuTimer.SlabMarched - WeatherGpuTimer.SlabStart];
                if (m >= 0f) wetMs.Add(m);
            }
            Cam.RemoveCommandBuffer(CameraEvent.BeforeLighting, cmd);
            double wetMedian = Median(wetMs);
            Sink.Info("cost.wet_gbuffer_ms", wetMedian);
            Sink.Info("cost.wet_gbuffer_ms_per_mpix", wetMedian / mpix);
            Sink.AtMost("cost.wet_gbuffer_ms_at_1080p", wetMedian / mpix * 1920.0 * 1080.0 / 1.0e6, 0.3);

            // --- The droplet host composite at full wetness, after everything, onto the camera's own target.
            var dropTuning = DropletTuning.Defaults();
            var dd = new DropletDraw
            {
                Tuning = dropTuning,
                Wet01 = 1f,
                ClockS = 2f,
                Slide = new Vector2(0f, -0.045f),
                Aspect = Width / (float)Height,
                FlipY = SystemInfo.graphicsUVStartsAtTop,
                Light = 0.8f,
                QuadColour = Color.white
            };
            var cmd2 = new CommandBuffer { name = "droplet-cost" };
            Cam.AddCommandBuffer(CameraEvent.AfterImageEffects, cmd2);
            var dropMs = new List<double>();
            for (int i = 0; i < Warm + 2400 && dropMs.Count < Samples; i++)
            {
                cmd2.Clear();
                WeatherGpuTimer.Mark(cmd2, WeatherGpuTimer.SlabStart);
                drops.RecordHost(cmd2, new RenderTargetIdentifier(Target), Target.width, Target.height, ref dd);
                WeatherGpuTimer.Mark(cmd2, WeatherGpuTimer.SlabMarched);
                WeatherGpuTimer.Mark(cmd2, WeatherGpuTimer.SlabComposited);
                yield return Frames(1);
                float total;
                int number;
                if (i < Warm || !WeatherGpuTimer.Read(segments, out total, out number)) continue;
                float m = segments[WeatherGpuTimer.SlabMarched - WeatherGpuTimer.SlabStart];
                if (m >= 0f) dropMs.Add(m);
            }
            Cam.RemoveCommandBuffer(CameraEvent.AfterImageEffects, cmd2);
            double dropMedian = Median(dropMs);
            Sink.Info("cost.droplet_host_ms", dropMedian);
            Sink.Info("cost.droplet_host_ms_per_mpix", dropMedian / mpix);
            Sink.AtMost("cost.droplet_host_ms_at_1080p", dropMedian / mpix * 1920.0 * 1080.0 / 1.0e6, 0.25);
        }

        private static double Median(List<double> v)
        {
            if (v.Count == 0) return double.NaN;
            var sorted = new List<double>(v);
            sorted.Sort();
            return sorted[sorted.Count / 2];
        }
    }
}
