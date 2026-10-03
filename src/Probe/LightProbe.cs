using System;
using System.Collections;
using System.Globalization;
using System.IO;
using ReDefinition.Core;
using ReDefinition.Raytracing;
using UnityEngine;

namespace ReDefinition.Probe
{
    // The raytracing's light, driven by the scene probe (-redefinitionProbeLight): at
    // the launch pad by day and, after a time warp, by night with the Mun high over
    // it, screenshots of the vessel from below and of the ground from above with the
    // raytracing off and on, the moonlight at 0, 100 and 300 %, each with the passes'
    // GPU time into KSP.log ("Probe: light ..."), then the settings put back.
    // The screenshots land in ReDefinitionCaptures\<time>-light.
    internal static class LightProbe
    {
        internal const string Argument = "-redefinitionProbeLight";

        private const float SettleSeconds = 4f;

        internal static bool Requested
        {
            get { return Array.IndexOf(Environment.GetCommandLineArgs(), Argument) >= 0; }
        }

        private struct Pose
        {
            internal string Name;
            internal float Pitch;
        }

        // Below the vessel looking up at its underside; above it looking down on the ground.
        private static readonly Pose[] Poses =
        {
            new Pose { Name = "below", Pitch = -15f },
            new Pose { Name = "above", Pitch = 35f },
        };

        internal static IEnumerator Run(string folder)
        {
            ReDefinitionAddon addon = ReDefinitionAddon.Instance;
            if (addon == null)
            {
                Debug.LogWarning(Log.Tag + " Probe: light: no add-on.");
                yield break;
            }
            OwnSettings original = addon.Live.Clone();
            float heading = FlightCamera.fetch.camHdg;
            try
            {
                Debug.Log(Log.Tag + " Probe: light: by day, into " + folder + ".");
                yield return Shots(addon, folder, "day", heading);

                Debug.Log(Log.Tag + " Probe: light: warping to a night with the Mun high.");
                bool found = false;
                yield return WarpToMoonNight(result => found = result);
                if (!found) Debug.LogWarning(Log.Tag + " Probe: light: no night with the Mun high found.");
                yield return Shots(addon, folder, found ? "night" : "later", heading);
            }
            finally
            {
                Set(addon, original.Raytracing, original.RaytracingNightLight);
            }
            Debug.Log(Log.Tag + " Probe: light: done.");
        }

        private static IEnumerator Shots(ReDefinitionAddon addon, string folder, string time, float heading)
        {
            RaytracingMode on = addon.Live.Raytracing == RaytracingMode.Off ? RaytracingMode.Full : addon.Live.Raytracing;
            foreach (Pose pose in Poses)
            {
                FlightCamera.fetch.camHdg = heading;
                FlightCamera.fetch.camPitch = pose.Pitch * Mathf.Deg2Rad;
                Set(addon, RaytracingMode.Off, 100f);
                yield return Shot(folder, time + "-" + pose.Name + "-off");
                foreach (float percent in new[] { 0f, 100f, 300f })
                {
                    Set(addon, on, percent);
                    yield return Shot(folder, time + "-" + pose.Name + "-" + on.ToString().ToLowerInvariant() + "-"
                                              + percent.ToString("0", CultureInfo.InvariantCulture));
                }
            }
        }

        private static void Set(ReDefinitionAddon addon, RaytracingMode mode, float percent)
        {
            OwnSettings before = addon.Live.Clone();
            OwnSettings after = before.Clone();
            after.Raytracing = mode;
            after.RaytracingNightLight = percent;
            addon.Apply(before, after);
        }

        // Settled for the denoiser, the passes' GPU time averaged meanwhile, then the
        // finished frame.
        private static IEnumerator Shot(string folder, string name)
        {
            float until = Time.realtimeSinceStartup + SettleSeconds;
            float[] timings = new float[8];
            float[] sum = new float[8];
            int frames = 0;
            while (Time.realtimeSinceStartup < until)
            {
                yield return null;
                if (Time.realtimeSinceStartup < until - SettleSeconds / 2f) continue;
                try
                {
                    RtNative.KspRtPassTimings(timings, timings.Length);
                }
                catch (Exception)
                {
                    continue;
                }
                for (int i = 0; i < sum.Length; i++) sum[i] += timings[i];
                frames++;
            }
            yield return new WaitForEndOfFrame();
            ScreenCapture.CaptureScreenshot(Path.Combine(folder, name + ".png"));
            float n = Mathf.Max(frames, 1);
            Debug.Log(Log.Tag + " Probe: light " + name + ": GPU ms sun " + (sum[0] / n).ToString("0.000", CultureInfo.InvariantCulture)
                      + ", occlusion " + (sum[1] / n).ToString("0.000", CultureInfo.InvariantCulture)
                      + ", diffuse " + (sum[2] / n).ToString("0.000", CultureInfo.InvariantCulture)
                      + ", reflections " + (sum[3] / n).ToString("0.000", CultureInfo.InvariantCulture)
                      + " over " + frames + " frames; " + RtReflectors.Describe() + ".");
            yield return null;
        }

        // Time warp until the sun is under the pad's horizon and the Mun high over it.
        private static IEnumerator WarpToMoonNight(Action<bool> done)
        {
            Vessel vessel = FlightGlobals.ActiveVessel;
            CelestialBody mun = FlightGlobals.Bodies.Find(b => b.bodyName == "Mun");
            CelestialBody sun = RtKspMods.StarBody();
            if (vessel == null || mun == null || sun == null)
            {
                done(false);
                yield break;
            }
            double started = Planetarium.GetUniversalTime();
            // Kerbin turns 2.8 degrees a frame at 60 frames a second at this rate.
            TimeWarp.SetRate(6, true);
            bool found = false;
            // Two of the Mun's orbits at most.
            while (Planetarium.GetUniversalTime() - started < 2.0 * mun.orbit.period)
            {
                yield return null;
                Vector3d up = (vessel.CoMD - vessel.mainBody.position).normalized;
                double sunHeight = Vector3d.Dot(up, (sun.position - vessel.CoMD).normalized);
                double munHeight = Vector3d.Dot(up, (mun.position - vessel.CoMD).normalized);
                if (sunHeight < -0.3 && munHeight > 0.6)
                {
                    found = true;
                    break;
                }
            }
            TimeWarp.SetRate(0, true);
            yield return new WaitForSeconds(3f);
            Vector3d up2 = (vessel.CoMD - vessel.mainBody.position).normalized;
            Debug.Log(Log.Tag + " Probe: light: warped " + (Planetarium.GetUniversalTime() - started).ToString("0", CultureInfo.InvariantCulture)
                      + " s; the sun at " + Elevation(up2, sun.position - vessel.CoMD) + " degrees, the Mun at "
                      + Elevation(up2, mun.position - vessel.CoMD) + ", its phase angle "
                      + Vector3d.Angle(sun.position - mun.position, vessel.CoMD - mun.position).ToString("0", CultureInfo.InvariantCulture) + " degrees.");
            done(found);
        }

        private static string Elevation(Vector3d up, Vector3d towards)
        {
            return (90.0 - Vector3d.Angle(up, towards)).ToString("0", CultureInfo.InvariantCulture);
        }
    }
}
