using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using ReDefinition.Core;
using ReDefinition.Modules;
using ReDefinition.Proxy;
using ReDefinition.Raytracing;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Probe
{
    // The light probe's frame times by day on the pad with each of ReDefinition's costly parts off in turn.
    internal static partial class LightProbe
    {
        // Frame times by day at the pad, from the "above" pose, the frame rate free (no
        // vertical sync, no limit): the player's settings, then each costly part off in
        // turn, then the player's settings again for the drift. Per variant 3 s to settle
        // and 6 s measured: the mean and 95th percentile frame time and the raytracing's
        // passes on the GPU.
        private static IEnumerator PerfShots(ReDefinitionAddon addon, OwnSettings original, float heading)
        {
            int sync = QualitySettings.vSyncCount, target = Application.targetFrameRate;
            FlightCamera.fetch.camHdg = heading;
            FlightCamera.fetch.camPitch = 35f * Mathf.Deg2Rad;
            var variants = new List<KeyValuePair<string, Action<OwnSettings>>>
            {
                new KeyValuePair<string, Action<OwnSettings>>("player's", s => { }),
                new KeyValuePair<string, Action<OwnSettings>>("raytracing off", s => s.Raytracing = RaytracingMode.Off),
                new KeyValuePair<string, Action<OwnSettings>>("raytracing 1 bounce", s => s.RaytracingBounces = 1),
                new KeyValuePair<string, Action<OwnSettings>>("frame generation off", s => s.FrameGeneration = false),
                new KeyValuePair<string, Action<OwnSettings>>("upscaler off", s => { s.Enabled = false; s.FrameGeneration = false; }),
                new KeyValuePair<string, Action<OwnSettings>>("space centre grass off", s => s.KscGrass = false),
                new KeyValuePair<string, Action<OwnSettings>>("interface extras off", s =>
                {
                    s.FlightPanel = s.RoundGauge = s.NavballSpeeds = s.NavballDockingMarker = s.NavballDrag = false;
                    s.StageDetailColumns = s.StageTwrRange = s.DeltaVTools = s.HudTooltips = false;
                }),
                new KeyValuePair<string, Action<OwnSettings>>("player's again", s => { }),
            };
            try
            {
                foreach (KeyValuePair<string, Action<OwnSettings>> variant in variants)
                {
                    OwnSettings before = addon.Live.Clone();
                    OwnSettings after = original.Clone();
                    variant.Value(after);
                    addon.Apply(before, after);
                    QualitySettings.vSyncCount = 0;
                    Application.targetFrameRate = -1;
                    yield return new WaitForSecondsRealtime(3f);
                    QualitySettings.vSyncCount = 0;
                    Application.targetFrameRate = -1;
                    var times = new List<float>();
                    float[] timings = new float[8], sum = new float[8];
                    int timed = 0;
                    float until = Time.realtimeSinceStartup + 6f;
                    while (Time.realtimeSinceStartup < until)
                    {
                        yield return null;
                        times.Add(Time.unscaledDeltaTime * 1000f);
                        try
                        {
                            RtNative.KspRtPassTimings(timings, timings.Length);
                            for (int i = 0; i < sum.Length; i++) sum[i] += timings[i];
                            timed++;
                        }
                        catch (Exception)
                        {
                        }
                    }
                    times.Sort();
                    float mean = 0f;
                    foreach (float t in times) mean += t;
                    mean /= Mathf.Max(times.Count, 1);
                    float n = Mathf.Max(timed, 1);
                    Debug.Log(Log.Tag + " Probe: perf " + variant.Key + ": " + times.Count + " frames, "
                              + mean.ToString("0.00", CultureInfo.InvariantCulture) + " ms mean, "
                              + times[Mathf.Min(times.Count - 1, (int)(times.Count * 0.95f))].ToString("0.00", CultureInfo.InvariantCulture)
                              + " ms 95th percentile; GPU ms sun " + (sum[0] / n).ToString("0.000", CultureInfo.InvariantCulture)
                              + ", occlusion " + (sum[1] / n).ToString("0.000", CultureInfo.InvariantCulture)
                              + ", diffuse " + (sum[2] / n).ToString("0.000", CultureInfo.InvariantCulture)
                              + ", reflections " + (sum[3] / n).ToString("0.000", CultureInfo.InvariantCulture)
                              + ", rest " + (sum[4] / n).ToString("0.000", CultureInfo.InvariantCulture) + "/"
                              + (sum[5] / n).ToString("0.000", CultureInfo.InvariantCulture) + "/"
                              + (sum[6] / n).ToString("0.000", CultureInfo.InvariantCulture) + "/"
                              + (sum[7] / n).ToString("0.000", CultureInfo.InvariantCulture) + ".");
                }
            }
            finally
            {
                addon.Apply(addon.Live.Clone(), original.Clone());
                QualitySettings.vSyncCount = sync;
                Application.targetFrameRate = target;
            }
        }
    }
}
