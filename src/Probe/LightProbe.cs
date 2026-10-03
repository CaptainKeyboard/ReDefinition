using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using ReDefinition.Core;
using ReDefinition.Raytracing;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Probe
{
    // The raytracing's light, driven by the scene probe (-redefinitionProbeLight): at
    // the launch pad by day and, after a time warp, by night with the Mun high over
    // it, screenshots of the vessel from below and of the ground from above with the
    // raytracing off and on -- by day at the default moonlight, by night the moonlight at
    // 0, 10, 50 and 150 %, without KSP's night light -- each with the passes' GPU time into KSP.log
    // ("Probe: light ..."), then the settings put back. By day, the ground's albedo as
    // the G-buffer has it beside the one the rays take (RtTerrainColour).
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
                yield return GroundAlbedo(addon, heading);
                yield return Shots(addon, folder, "day", heading, false);

                Debug.Log(Log.Tag + " Probe: light: warping to a night with the Mun high.");
                bool found = false;
                yield return WarpToMoonNight(result => found = result);
                if (!found) Debug.LogWarning(Log.Tag + " Probe: light: no night with the Mun high found.");
                yield return Shots(addon, folder, found ? "night" : "later", heading, true);
            }
            finally
            {
                Set(addon, original.Raytracing, original.RaytracingMoonlight, original.RaytracingNightAmbient);
            }
            Debug.Log(Log.Tag + " Probe: light: done.");
        }

        private static IEnumerator Shots(ReDefinitionAddon addon, string folder, string time, float heading, bool night)
        {
            RaytracingMode on = addon.Live.Raytracing == RaytracingMode.Off ? RaytracingMode.Full : addon.Live.Raytracing;
            // Moonlight and night light in percent.
            float[][] variants = night
                ? new[] { new[] { 0f, 0f }, new[] { 10f, 0f }, new[] { 50f, 0f }, new[] { 150f, 0f } }
                : new[] { new[] { 10f, 0f } };
            foreach (Pose pose in Poses)
            {
                FlightCamera.fetch.camHdg = heading;
                FlightCamera.fetch.camPitch = pose.Pitch * Mathf.Deg2Rad;
                Set(addon, RaytracingMode.Off, 10f, 0f);
                yield return Shot(folder, time + "-" + pose.Name + "-off");
                foreach (float[] variant in variants)
                {
                    Set(addon, on, variant[0], variant[1]);
                    yield return Shot(folder, time + "-" + pose.Name + "-" + on.ToString().ToLowerInvariant() + "-moon"
                                              + variant[0].ToString("0", CultureInfo.InvariantCulture) + "-night"
                                              + variant[1].ToString("0", CultureInfo.InvariantCulture));
                }
            }
        }

        private static void Set(ReDefinitionAddon addon, RaytracingMode mode, float moonlight, float nightAmbient)
        {
            OwnSettings before = addon.Live.Clone();
            OwnSettings after = before.Clone();
            after.Raytracing = mode;
            after.RaytracingMoonlight = moonlight;
            after.RaytracingNightAmbient = nightAmbient;
            addon.Apply(before, after);
        }

        // From above: for terrain quads of the main body in view, the albedo the G-buffer
        // holds at each one's middle (what the terrain shader drew) beside the one the rays
        // take for it -- its material's colour plus its vertex colours times the factor
        // (RtTerrainColour) -- in Unity's colour space.
        private static IEnumerator GroundAlbedo(ReDefinitionAddon addon, float heading)
        {
            Camera scene = null;
            foreach (Camera c in Camera.allCameras)
                if (c.name == "Camera 00") scene = c;
            PQS pqs = FlightGlobals.currentMainBody != null ? FlightGlobals.currentMainBody.pqsController : null;
            if (scene == null || pqs == null || scene.actualRenderingPath != RenderingPath.DeferredShading)
            {
                Debug.Log(Log.Tag + " Probe: light: no G-buffer or terrain to compare the ground with.");
                yield break;
            }
            FlightCamera.fetch.camHdg = heading;
            FlightCamera.fetch.camPitch = 35f * Mathf.Deg2Rad;
            for (int i = 0; i < 30; i++) yield return null;

            // The quads in view, nearest first.
            List<KeyValuePair<float, MeshRenderer>> inView = new List<KeyValuePair<float, MeshRenderer>>();
            foreach (PQ quad in pqs.GetComponentsInChildren<PQ>(false))
            {
                if (quad == null || !quad.isVisible || quad.isSubdivided) continue;
                foreach (MeshRenderer renderer in quad.GetComponentsInChildren<MeshRenderer>(false))
                {
                    if (renderer == null || !renderer.enabled) continue;
                    Vector3 view = scene.WorldToViewportPoint(renderer.bounds.center);
                    if (view.z <= 0f || view.x < 0.05f || view.x > 0.95f || view.y < 0.05f || view.y > 0.95f) continue;
                    inView.Add(new KeyValuePair<float, MeshRenderer>(view.z, renderer));
                }
            }
            inView.Sort((a, b) => a.Key.CompareTo(b.Key));
            Debug.Log(Log.Tag + " Probe: light: " + inView.Count + " terrain meshes in view.");

            CommandBuffer cb = new CommandBuffer { name = "ReDefinition probe G-buffer" };
            RenderTexture albedo = new RenderTexture(scene.pixelWidth, scene.pixelHeight, 0, RenderTextureFormat.ARGB32,
                                                     RenderTextureReadWrite.Linear);
            cb.Blit(BuiltinRenderTextureType.GBuffer0, albedo);
            scene.AddCommandBuffer(CameraEvent.AfterGBuffer, cb);
            yield return new WaitForEndOfFrame();
            scene.RemoveCommandBuffer(CameraEvent.AfterGBuffer, cb);
            cb.Release();
            RenderTexture before = RenderTexture.active;
            RenderTexture.active = albedo;
            Texture2D read = new Texture2D(albedo.width, albedo.height, TextureFormat.RGBA32, false, true);
            read.ReadPixels(new Rect(0, 0, albedo.width, albedo.height), 0, 0, false);
            RenderTexture.active = before;
            albedo.Release();
            UnityEngine.Object.Destroy(albedo);

            for (int k = 0; k < Math.Min(8, inView.Count); k++)
            {
                MeshRenderer renderer = inView[k].Value;
                MeshFilter filter = renderer.GetComponent<MeshFilter>();
                Vector3 view = scene.WorldToViewportPoint(renderer.bounds.center);
                int px = Mathf.Clamp((int)(view.x * read.width), 4, read.width - 5);
                int py = Mathf.Clamp((int)(view.y * read.height), 4, read.height - 5);
                Color sum = Color.clear;
                for (int dy = -4; dy <= 4; dy++)
                    for (int dx = -4; dx <= 4; dx++)
                        sum += read.GetPixel(px + dx, py + dy);
                sum /= 81f;
                string rays;
                Color colour;
                float factor;
                if (filter != null && filter.sharedMesh != null && RtTerrainColour.Of(renderer.sharedMaterial, out colour, out factor))
                {
                    Color32[] colours = filter.sharedMesh.colors32;
                    double r = 0, g = 0, b = 0;
                    foreach (Color32 c in colours)
                    {
                        r += c.r;
                        g += c.g;
                        b += c.b;
                    }
                    double n = Math.Max(colours.Length, 1) * 255.0;
                    rays = Triple(colour.r + factor * r / n, colour.g + factor * g / n, colour.b + factor * b / n)
                           + " (colour " + Triple(colour.r, colour.g, colour.b) + ", " + colours.Length + " vertex colours averaging "
                           + Triple(r / n, g / n, b / n) + " times " + factor.ToString("0.000", CultureInfo.InvariantCulture) + ")";
                }
                else
                {
                    rays = "not Parallax's terrain with its influence map (" + renderer.sharedMaterial.shader.name + ")";
                }
                Debug.Log(Log.Tag + " Probe: light: terrain " + renderer.name + " at " + (int)inView[k].Key + " m: G-buffer albedo "
                          + Triple(sum.r, sum.g, sum.b) + ", the rays' " + rays + ".");
            }
            UnityEngine.Object.Destroy(read);
        }

        private static string Triple(double r, double g, double b)
        {
            return r.ToString("0.000", CultureInfo.InvariantCulture) + ", " + g.ToString("0.000", CultureInfo.InvariantCulture)
                   + ", " + b.ToString("0.000", CultureInfo.InvariantCulture);
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
