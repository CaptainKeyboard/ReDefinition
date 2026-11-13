#if UNITY_2019_4_OR_NEWER
// The step-policy part of the weather player's scene cloud-shadow (package T04, run as -mode scene -scene cloud-shadow -shadowsteps 1 -out <folder>); linked by
// tests/WeatherPlayer/linked/T04.txt and compiled out of the test project.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using ReDefinition.Atmospheres;
using UnityEngine;

namespace ReDefinition.WeatherPlayer
{
    // The shader against its CPU twin with the step policy: a slab 20 m thick at 2000 m whose layer interval is widened by a height reach (HeightDownM and HeightUpM,
    // no height maps, so the density is the table's) and so has more steps under the scaled policy. For both policies, two reaches (40 m: 100 m interval, 5 times,
    // 12 x 5 = 60 steps; 180 m: 380 m interval, 19 times, capped at 64 steps) and one and eight sun disc taps, the ground factor along the camera's middle row
    // (the sea plane) is read from the picture and compared with CloudShadowField.DirectTransmission at the same place with the same policy, steps and taps.
    // The slab's vertical optical depth is 1, the sun at 45 degrees, the camera looks down from 1500 m.
    public sealed partial class CloudShadowScene
    {
        private LayerStackSource currentSource;

        private IEnumerator RunSteps()
        {
            const double elevation = 45.0;
            var parity = new List<double>();
            NaturalMode(false);
            foreach (float reach in new[] { 40f, 180f })
                foreach (int policy in new[] { 1, 0 })
                    foreach (int taps in new[] { 1, 8 })
                    {
                        string id = "p" + policy + ".reach" + (int)reach + ".taps" + taps;
                        SetCloud(1f, false, reach);
                        SetSun(elevation);
                        CloudShadowSteps steps = policy == 1 ? CloudShadowSteps.Scaled : CloudShadowSteps.Fixed;
                        frame.StepPolicy = steps;
                        frame.DiscTaps = taps;
                        Pose(new CameraPose(new Vector3(-1300f, 1500f, 0f), 0f, 90f));
                        yield return Capture();
                        double[] xs, ratio;
                        RowRatio(out xs, out ratio);
                        var cpu = new CloudShadowField(currentSource, currentSource.Tables()) { StepsPerSpan = frame.StepsPerSpan, StepPolicy = steps, DiscSamples = taps };
                        double e = elevation * Math.PI / 180.0;
                        double sx = Math.Sin(e), sy = Math.Cos(e);
                        var diffs = new List<double>();
                        double meanGpu = 0.0, meanCpu = 0.0, loGpu = double.MaxValue, hiGpu = double.MinValue;
                        int mismatches = 0;
                        for (int i = 0; i < xs.Length; i++)
                        {
                            if (double.IsNaN(ratio[i]) || xs[i] > -150.0) continue;
                            // The map frame: x up, y east, z north; the planet's centre is R below the origin.
                            double want = cpu.DirectTransmission(R, xs[i], 0.0, sx, sy, 0.0);
                            double d = Math.Abs(ratio[i] - want) / Math.Max(want, 1e-4);
                            diffs.Add(d);
                            if (d > 0.05) mismatches++;
                            meanGpu += ratio[i];
                            meanCpu += want;
                            loGpu = Math.Min(loGpu, ratio[i]);
                            hiGpu = Math.Max(hiGpu, ratio[i]);
                        }
                        diffs.Sort();
                        int n = diffs.Count;
                        double median = n > 0 ? diffs[n / 2] : double.NaN;
                        double p90 = n > 0 ? diffs[(int)(n * 0.9)] : double.NaN;
                        double worst = n > 0 ? diffs[n - 1] : double.NaN;
                        Sink.Info("steps." + id + ".layer_steps", CloudShadowStepPolicy.Count(steps, frame.StepsPerSpan, currentSource.LayerAt(0)));
                        Sink.Info("steps." + id + ".n", n);
                        Sink.Info("steps." + id + ".gpu_mean", n > 0 ? meanGpu / n : double.NaN);
                        Sink.Info("steps." + id + ".cpu_mean", n > 0 ? meanCpu / n : double.NaN);
                        Sink.Info("steps." + id + ".gpu_min", loGpu);
                        Sink.Info("steps." + id + ".gpu_max", hiGpu);
                        Sink.Info("steps." + id + ".analytic", Math.Exp(-1.0 / Math.Sin(e)));
                        Sink.Info("steps." + id + ".parity_median", median);
                        Sink.Info("steps." + id + ".parity_p90", p90);
                        Sink.Info("steps." + id + ".parity_worst", worst);
                        Sink.Info("steps." + id + ".mismatch_share", n > 0 ? mismatches / (double)n : double.NaN);
                        // The old parity tolerance of the T04 scene (the R12 check, 5 %) on the median; the worst and the share are reported.
                        Sink.AtMost("steps.parity." + id, median, 0.05);
                        parity.Add(median);
                        SavePictures("e40-shq-" + id.Replace('.', '-'));
                    }
            Sink.Flag("steps_ran", parity.Count == 8);
            rig.Stop();
            renderer.Dispose();
        }
    }
}
#endif
