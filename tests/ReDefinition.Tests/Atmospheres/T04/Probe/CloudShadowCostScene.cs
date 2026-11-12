#if UNITY_2019_4_OR_NEWER
// A scene of the weather player (package T04 follow-up FX-T04, -mode scene -scene cloud-shadow-cost, a timing run: exclusive lock, proxy
// timestamps); linked by tests/WeatherPlayer/linked/T04.txt and compiled out of the test project.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using ReDefinition.Atmospheres;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.WeatherPlayer
{
    // The GPU milliseconds of the cloud shadow's passes in the forward buffer (the depth to half size and the shadow at half size, then the
    // multiply into the colour) at 853x480 and 1707x960, for one and three layers, the three quality levels (CloudShadowSwitches.Quality)
    // and two views: straight down (every pixel is ground) and a level look at the horizon (about half the pixels are sky, which the shadow
    // answers with 1 without marching). The cover is full (the worst case: every step reads a profile). The deferred path runs the same two first passes and replaces the colour multiply by the multiply into
    // the sun's mask (one full-screen blit of the same size), so "build" is its cost too. Output: cost.<view>.<res>.L<layers>.Q<quality>.build
    // and .multiply as info lines (the median of 90 frames).
    [WeatherScene("cloud-shadow-cost")]
    public sealed class CloudShadowCostScene : WeatherSceneBase
    {
        private const double R = 600000.0;
        private const int MapW = 512, MapH = 256;

        public override IEnumerator Run()
        {
            Shader shader = Shader.Find(CloudShadowRenderer.ShaderName);
            bool ready = shader != null && WeatherGpuTimer.Start();
            Sink.Flag("cost_timers_available", ready);
            if (!ready) yield break;
            var renderer = new CloudShadowRenderer();
            bool ok = renderer.Init(shader);
            Sink.Flag("shadow_compiled", ok);
            if (!ok) yield break;
            GameObject sea = GameObject.Find("Sea");
            if (sea != null) sea.transform.localScale = new Vector3(5000f, 1f, 5000f);
            var maps = new LayerMapStore();
            maps.Ensure(MapW, MapH);
            // A full cover (0.8 with the layer's coverage 0.5 and edge softness 0.2 gives a density factor of 1): the view spans under one
            // texel of a map this size, so a pattern would be constant over it; the full cover is the worst case, every step reads a profile.
            var cov = new byte[MapW * MapH];
            for (int i = 0; i < cov.Length; i++) cov[i] = 204;
            for (int l = 0; l < 3; l++) maps.SetTargetBytes(l, cov, new byte[MapW * MapH]);
            maps.Step(0f, MapSlew.DefaultRate, true);

            double e = 45.0 * Math.PI / 180.0;
            var toSun = new Vector3((float)Math.Cos(e), (float)Math.Sin(e), 0f);
            Sun.transform.rotation = Quaternion.LookRotation(-toSun);
            Matrix4x4 toMap = Matrix4x4.zero;
            toMap.SetRow(0, new Vector4(0f, 1f, 0f, 0f));
            toMap.SetRow(1, new Vector4(1f, 0f, 0f, 0f));
            toMap.SetRow(2, new Vector4(0f, 0f, 1f, 0f));
            toMap.SetRow(3, new Vector4(0f, 0f, 0f, 1f));
            var frame = new CloudShadowFrame
            {
                PlanetCenter = new Vector3(0f, (float)-R, 0f),
                RadiusUnits = R,
                MetresPerUnit = 1f,
                WorldToMap = toMap,
                TowardsSun = toSun
            };

            Cam.depthTextureMode |= DepthTextureMode.Depth;
            var segments = new float[8];
            int[][] sizes = { new[] { 853, 480 }, new[] { 1707, 960 } };
            var views = new[]
            {
                new { Name = "down", Pose = new CameraPose(new Vector3(0f, 1500f, 0f), 0f, 90f) },
                new { Name = "horizon", Pose = new CameraPose(new Vector3(-3000f, 1500f, 0f), 90f, 0f) }
            };
            foreach (int[] size in sizes)
            {
                var rt = new RenderTexture(size[0], size[1], 24, RenderTextureFormat.DefaultHDR) { name = "shadow cost target" };
                rt.Create();
                Cam.targetTexture = rt;
                foreach (var view in views)
                {
                    Pose(view.Pose);
                    foreach (int layers in new[] { 1, 3 })
                    {
                        renderer.Upload(Stack(layers), maps);
                        foreach (int quality in new[] { 0, 1, 2 })
                        {
                            // CloudShadowSwitches.Quality (that file is not linked into the player): steps per span and sun disc taps.
                            frame.StepsPerSpan = quality == 0 ? 4 : quality == 1 ? 8 : 12;
                            frame.DiscTaps = quality == 0 ? 1 : quality == 1 ? 4 : 6;
                            renderer.Apply(frame);
                            var cb = new CommandBuffer { name = "shadow cost" };
                            renderer.RecordForwardBuffer(cb, size[0], size[1],
                                c => WeatherGpuTimer.Mark(c, WeatherGpuTimer.SlabStart),
                                c => WeatherGpuTimer.Mark(c, WeatherGpuTimer.SlabMarched));
                            WeatherGpuTimer.Mark(cb, WeatherGpuTimer.SlabComposited);
                            Cam.AddCommandBuffer(CameraEvent.AfterForwardOpaque, cb);
                            yield return Frames(20);
                            var build = new List<double>();
                            var multiply = new List<double>();
                            int guard = 0;
                            while (build.Count < 90 && guard++ < 1500)
                            {
                                yield return Frames(1);
                                float total;
                                int number;
                                if (!WeatherGpuTimer.Read(segments, out total, out number)) continue;
                                float b = segments[WeatherGpuTimer.SlabMarched - WeatherGpuTimer.SlabStart], m = segments[WeatherGpuTimer.SlabComposited - WeatherGpuTimer.SlabStart];
                                if (b >= 0f && m >= 0f) { build.Add(b); multiply.Add(m); }
                            }
                            Cam.RemoveCommandBuffer(CameraEvent.AfterForwardOpaque, cb);
                            cb.Release();
                            string id = view.Name + "." + size[0] + "x" + size[1] + ".L" + layers + ".Q" + quality;
                            Sink.Info("cost." + id + ".build", Median(build));
                            Sink.Info("cost." + id + ".multiply", Median(multiply));
                            Sink.Info("cost." + id + ".samples", build.Count);
                        }
                    }
                }
                Cam.targetTexture = Target;
                rt.Release();
            }
            renderer.Dispose();
        }

        // A stack of one to three thick decks at 2, 5 and 9 km.
        private static LayerStackSource Stack(int layers)
        {
            var source = new LayerStackSource(0, R, true);
            float[] bases = { 2000f, 5000f, 9000f };
            for (int i = 0; i < layers; i++)
            {
                var def = new CloudLayerDef("deck" + i, i, bases[i], bases[i] + 300f, new[] { CloudTypeDef.Slab("s", 0f, 1f) }, 6f / 300f)
                {
                    Coverage = 0.5f,
                    EdgeSoftness = 0.2f
                };
                def.CoverageMapHandle = i;
                def.TypeMapHandle = i;
                source.Add(def, null, null);
            }
            return source;
        }

        private static double Median(List<double> v)
        {
            if (v.Count == 0) return double.NaN;
            v.Sort();
            return v[v.Count / 2];
        }
    }
}
#endif
