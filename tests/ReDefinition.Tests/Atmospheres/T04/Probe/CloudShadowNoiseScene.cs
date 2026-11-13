#if UNITY_2019_4_OR_NEWER
// The shape-noise part of the weather player's scene cloud-shadow (E40 FL-FIX, run as -mode scene -scene cloud-shadow -shadownoise 1 -out <folder>); linked by
// tests/WeatherPlayer/linked/T04.txt and compiled out of the test project.
using System;
using System.Collections;
using System.Collections.Generic;
using ReDefinition.Atmospheres;
using UnityEngine;

namespace ReDefinition.WeatherPlayer
{
    // The shadow shader with the layer pass's shape noise against its CPU twin (CloudShadowField with CloudShadowShapeNoise): a slab 20 m thick at 2000 m of vertical optical depth 1,
    // the shipped cumulus layer's coverage 0.35, softness 0.15 and noise amount 1, a constant map of 0.667; the noise scale 100 m (cells of 400 to 800 m, many along the row) and
    // 500 m (the cfg's: cells of 2 to 4 km), the sun at 45 degrees with one and eight disc taps. For every case the ground factor along the camera's middle row is compared with
    // CloudShadowField.DirectTransmission at the same place with the same steps and taps. Noise off (the shader's amounts 0, the CPU twin without the object) is the map's threshold
    // alone, a constant factor along the row. Pictures: shadow-e40-ff-<case>.png and -aid.png.
    public sealed partial class CloudShadowScene
    {
        private IEnumerator RunShapeNoise()
        {
            const double elevation = 45.0;
            var noise = new LayerNoise();
            noise.EnsureReady();
            renderer.Noise = noise;
            Sink.Flag("noise_volumes_ready", noise.Ready);
            NaturalMode(false);
            int ran = 0;
            foreach (float scale in new[] { 100f, 500f })
                foreach (int on in new[] { 1, 0 })
                    foreach (int taps in new[] { 1, 8 })
                    {
                        if (on == 0 && taps == 8) continue;
                        string id = "s" + (int)scale + ".noise" + on + ".taps" + taps;
                        SetNoiseCloud(scale);
                        SetSun(elevation);
                        frame.DiscTaps = taps;
                        frame.ShapeNoise = on == 1;
                        Pose(new CameraPose(new Vector3(-1300f, 1500f, 0f), 0f, 90f));
                        yield return Capture();
                        double[] xs, ratio;
                        RowRatio(out xs, out ratio);
                        var cpu = new CloudShadowField(currentSource, currentSource.Tables()) { StepsPerSpan = frame.StepsPerSpan, StepPolicy = frame.StepPolicy, DiscSamples = taps };
                        var plain = new CloudShadowField(currentSource, currentSource.Tables()) { StepsPerSpan = frame.StepsPerSpan, StepPolicy = frame.StepPolicy, DiscSamples = taps };
                        if (on == 1)
                        {
                            var sn = new CloudShadowShapeNoise();
                            // The map frame of the scene: up is world +y, east +x (SetSun's matrix: rows 0 1 0, 1 0 0, 0 0 1).
                            sn.Update(noise, currentSource, new[] { 0f, 1f, 0f, 1f, 0f, 0f, 0f, 0f, 1f }, true);
                            cpu.ShapeNoise = sn;
                        }
                        double e = elevation * Math.PI / 180.0;
                        double sx = Math.Sin(e), sy = Math.Cos(e);
                        var diffs = new List<double>();
                        var vsPlain = new List<double>();
                        double meanGpu = 0.0, loGpu = double.MaxValue, hiGpu = double.MinValue, loCpu = double.MaxValue, hiCpu = double.MinValue;
                        int mismatches = 0;
                        for (int i = 0; i < xs.Length; i++)
                        {
                            if (double.IsNaN(ratio[i]) || xs[i] > -150.0) continue;
                            double want = cpu.DirectTransmission(R, xs[i], 0.0, sx, sy, 0.0);
                            double without = plain.DirectTransmission(R, xs[i], 0.0, sx, sy, 0.0);
                            double d = Math.Abs(ratio[i] - want) / Math.Max(want, 1e-4);
                            diffs.Add(d);
                            vsPlain.Add(Math.Abs(ratio[i] - without) / Math.Max(without, 1e-4));
                            if (d > 0.05) mismatches++;
                            meanGpu += ratio[i];
                            loGpu = Math.Min(loGpu, ratio[i]);
                            hiGpu = Math.Max(hiGpu, ratio[i]);
                            loCpu = Math.Min(loCpu, want);
                            hiCpu = Math.Max(hiCpu, want);
                        }
                        diffs.Sort();
                        vsPlain.Sort();
                        int n = diffs.Count;
                        double median = n > 0 ? diffs[n / 2] : double.NaN;
                        Sink.Info("noise." + id + ".n", n);
                        Sink.Info("noise." + id + ".gpu_mean", n > 0 ? meanGpu / n : double.NaN);
                        Sink.Info("noise." + id + ".gpu_min", loGpu);
                        Sink.Info("noise." + id + ".gpu_max", hiGpu);
                        Sink.Info("noise." + id + ".cpu_min", loCpu);
                        Sink.Info("noise." + id + ".cpu_max", hiCpu);
                        Sink.Info("noise." + id + ".parity_median", median);
                        Sink.Info("noise." + id + ".parity_p90", n > 0 ? diffs[(int)(n * 0.9)] : double.NaN);
                        Sink.Info("noise." + id + ".parity_worst", n > 0 ? diffs[n - 1] : double.NaN);
                        Sink.Info("noise." + id + ".mismatch_share", n > 0 ? mismatches / (double)n : double.NaN);
                        // What the shadow without the noise would be against this picture: the disagreement the switch removes (the map's threshold alone is one factor along the row).
                        Sink.Info("noise." + id + ".against_map_alone_median", n > 0 ? vsPlain[n / 2] : double.NaN);
                        // The old parity tolerance of the T04 scene (the R12 check, 5 %) on the median.
                        Sink.AtMost("noise.parity." + id, median, 0.05);
                        // The cells are there when the noise is on (the factor along the row spans a range) and the row is one number when it is off.
                        // At 500 m the cells are 2 to 4 km: the 5 km row of this view can lie inside one clear cell (run 1: factor 0.96 to 1), so only the 100 m scale must show cells.
                        if (on == 1 && scale < 200f) Sink.AtLeast("noise.cells." + id, hiGpu - loGpu, 0.10);
                        else if (on == 1) Sink.Info("noise.cells." + id, hiGpu - loGpu);
                        else Sink.AtMost("noise.flat." + id, hiGpu - loGpu, 0.03);
                        SavePictures("e40-ff-" + id.Replace('.', '-'));
                        ran++;
                    }
            Sink.Flag("noise_ran", ran == 6);
            rig.Stop();
            renderer.Dispose();
        }

        private void SetNoiseCloud(float scaleM)
        {
            var source = new LayerStackSource(0, R, true);
            currentSource = source;
            var def = new CloudLayerDef("slab", 0, SlabBase, SlabTop, new[] { CloudTypeDef.Slab("s", 0f, 1f) }, 1f / (SlabTop - SlabBase))
            {
                Coverage = 0.35f,
                EdgeSoftness = 0.15f,
                NoiseAmount = 1f,
                NoiseScaleM = scaleM
            };
            def.CoverageMapHandle = 0;
            def.TypeMapHandle = 0;
            // The CPU twin reads the same constant map as the texture below (170 of 255).
            var cpuMap = new ReDefinition.Core.WeatherMap();
            cpuMap.Resize(2, 2);
            for (int i = 0; i < cpuMap.Values.Length; i++) cpuMap.Values[i] = 170f / 255f;
            source.Add(def, cpuMap, null);
            var cov = new byte[MapW * MapH];
            for (int i = 0; i < cov.Length; i++) cov[i] = 170;
            maps.SetTargetBytes(0, cov, new byte[MapW * MapH]);
            maps.Step(0f, MapSlew.DefaultRate, true);
            renderer.Upload(source, maps);
        }
    }
}
#endif
