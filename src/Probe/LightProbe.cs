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
    // The raytracing's light, driven by the scene probe (-redefinitionProbeLight). The probe works at
    // the launch pad by day and, after a time warp, by night with the Mun high over
    // it. It takes screenshots of the vessel from below and of the ground from above with the
    // raytracing off and on. By day it uses the default moonlight. By night it uses the moonlight at
    // 0, 10, 50 and 150 %, without KSP's night light. Each shot comes with the passes' GPU time in KSP.log
    // ("Probe: light ..."), and then the settings are put back. By day it also gives the ground's albedo as
    // the G-buffer has it beside the one the rays take (RtTerrainColour).
    // The screenshots land in ReDefinitionCaptures\<time>-probe.
    // The ocean, the clouds, the runway and the pad's smoke, and the frame times have a file each (LightProbe.Ocean.cs and the
    // others beside this one).
    internal static partial class LightProbe
    {
        // The switch for the light probe, by day and night at the launch pad.
        internal const string Argument = "-redefinitionProbeLight";
        // With it, the ocean out at sea instead: the sun's and the Mun's reflections, each
        // body low, half up and high, with the raytracing on in full.
        internal const string OceanArgument = "-redefinitionProbeOcean";
        // With it, the runway's light pools at night alone (RunwayShots).
        internal const string RunwayArgument = "-redefinitionProbeRunway";
        // With it, the launch pad's smoke at night as the vessel lifts off (PadFxShots).
        internal const string PadFxArgument = "-redefinitionProbePadFx";
        // With it, frame times by day on the pad with each of ReDefinition's costly parts
        // off in turn (PerfShots).
        internal const string PerfArgument = "-redefinitionProbePerf";
        // With it, EVE's clouds by the light of the sun rising and setting and of the Mun at night (CloudShots).
        internal const string CloudLightArgument = "-redefinitionProbeCloudLight";

        // With it, EVE's 2D clouds seen from orbit by the Mun's light at two phases and heights (CloudsFromOrbit).
        internal const string CloudsOrbitArgument = "-redefinitionProbeCloudsOrbit";

        // Seconds each shot waits for the denoiser to settle; the passes' times are averaged over its second half.
        private const float SettleSeconds = 4f;

        // Whether the light probe was asked for on the command line; the scene probe runs it then.
        internal static bool Requested
        {
            get { return CommandLine.Has(Argument); }
        }

        // A camera pose of the pad shots: its name in the file names and its pitch.
        private struct Pose
        {
            // The name in the shots' file names; the camera's pitch in degrees.
            internal string Name;
            internal float Pitch;
        }

        // Below the vessel looking up at its underside; above it looking down on the ground.
        private static readonly Pose[] Poses =
        {
            new Pose { Name = "below", Pitch = -15f },
            new Pose { Name = "above", Pitch = 35f },
        };

        // Runs the probe into folder. It runs one of the single runs a switch asks for (clouds, frame times,
        // the pad's smoke, the runway, the ocean), or else the day and night shots, the mirror and the runway.
        // The raytracing's mode and light are set back at the end.
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
                if (CommandLine.Has(CloudLightArgument))
                {
                    yield return CloudShots(addon, folder);
                    yield break;
                }
                if (CommandLine.Has(NodesArgument) && CommandLine.After("-redefinitionProbeFly") != null)
                {
                    yield return FlyShots(folder, CommandLine.After("-redefinitionProbeFly"));
                    yield break;
                }
                if (CommandLine.Has(NodesArgument))
                {
                    yield return NodeShots(folder);
                    yield break;
                }
                if (CommandLine.Has(MapPlannerArgument))
                {
                    yield return MapPlannerShots(folder);
                    yield break;
                }
                if (CommandLine.Has(CloudRatiosArgument))
                {
                    yield return CloudRatios(addon, folder);
                    yield break;
                }
                if (CommandLine.Has(CloudsOrbitArgument))
                {
                    yield return CloudsFromOrbit(addon, folder);
                    yield break;
                }
                if (CommandLine.Has(PerfArgument))
                {
                    yield return PerfShots(addon, original, heading);
                    yield break;
                }
                if (CommandLine.Has(PadFxArgument))
                {
                    bool night = false;
                    yield return WarpToMoonNight(result => night = result);
                    yield return PadFxShots(folder);
                    yield break;
                }
                if (CommandLine.Has(RunwayArgument))
                {
                    bool night = false;
                    yield return WarpToMoonNight(result => night = result);
                    yield return RunwayShots(folder);
                    yield break;
                }
                if (CommandLine.Has(OceanArgument))
                {
                    yield return OceanShots(addon, folder);
                    yield break;
                }
                Debug.Log(Log.Tag + " Probe: light: by day, into " + folder + ".");
                yield return GroundAlbedo(addon, heading);
                yield return Shots(addon, folder, "day", heading, false);

                Debug.Log(Log.Tag + " Probe: light: warping to a night with the Mun high.");
                bool found = false;
                yield return WarpToMoonNight(result => found = result);
                if (!found) Debug.LogWarning(Log.Tag + " Probe: light: no night with the Mun high found.");
                yield return Shots(addon, folder, found ? "night" : "later", heading, true);
                if (found) yield return MirrorShots(addon, folder);
                yield return RunwayShots(folder);
            }
            finally
            {
                Set(addon, original.Raytracing, original.RaytracingMoonlight, original.RaytracingNightAmbient);
            }
            Debug.Log(Log.Tag + " Probe: light: done.");
        }

        // The VAB's place, west of the pad: its faces and the roads before it, seen flat.
        private const double VabLatitude = -0.0967, VabLongitude = -74.6187;

        // By day, the camera low behind the vessel looking 4 degrees down towards the VAB: the raytracing off, on,
        // with the G-buffer's shine as Deferred gives it, and the surfaces' overlay.
        private static IEnumerator BuildingShots(ReDefinitionAddon addon, string folder, string time)
        {
            Vessel vessel = FlightGlobals.ActiveVessel;
            if (vessel == null) yield break;
            CelestialBody body = vessel.mainBody;
            Vector3d vab = body.GetWorldSurfacePosition(VabLatitude, VabLongitude, 0.0);
            FlightCamera.fetch.SetDistance(Mathf.Min(40f, FlightCamera.fetch.maxDistance));
            Vector3 up = ((Vector3)(((Vector3d)vessel.transform.position) - body.position)).normalized;
            Vector3 flat = Vector3.ProjectOnPlane((Vector3)(vab - ((Vector3d)vessel.transform.position)), up).normalized;
            Vector3 look = flat * Mathf.Cos(4f * Mathf.Deg2Rad) - up * Mathf.Sin(4f * Mathf.Deg2Rad);
            FlightCamera.fetch.SetCamCoordsFromPosition(-look * FlightCamera.fetch.Distance);
            yield return ProbeSteps.Frames(10);
            RaytracingMode on = RaytracingOn(addon);
            Set(addon, RaytracingMode.Off, 10f, 0f);
            yield return Shot(folder, time + "-buildings-off");
            Set(addon, on, 10f, 0f);
            yield return Shot(folder, time + "-buildings-on");
            // The same with the G-buffer's shine as Deferred gives it, and the surfaces' overlay.
            RtCamera.SurfacesAsDeferred = true;
            yield return ProbeSteps.Frames(30);
            yield return Shot(folder, time + "-buildings-deferred");
            RtCamera.SurfacesAsDeferred = false;
            RtCamera.SurfaceView = true;
            yield return ProbeSteps.Frames(30);
            yield return Shot(folder, time + "-buildings-overlay");
            RtCamera.SurfaceView = false;
        }

        // The pad shots of one time of day. By day the buildings come first. For each pose the raytracing is off, then
        // on with each moonlight variant, with the G-buffer's specular and the surfaces as PFM beside them. By
        // day the Deferred shine and the surfaces' overlay are also shot.
        private static IEnumerator Shots(ReDefinitionAddon addon, string folder, string time, float heading, bool night)
        {
            RaytracingMode on = RaytracingOn(addon);
            if (!night && Wanted(time + "-buildings")) yield return BuildingShots(addon, folder, time);
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
                    string shot = time + "-" + pose.Name + "-" + on.ToString().ToLowerInvariant() + "-moon"
                                  + variant[0].ToString("0", CultureInfo.InvariantCulture) + "-night"
                                  + variant[1].ToString("0", CultureInfo.InvariantCulture);
                    yield return Shot(folder, shot);
                    // The G-buffer's specular colour and smoothness, as the raytracing reads them.
                    RtCamera camera = RtCamera.Current;
                    if (camera != null && camera.SpecularSmoothnessTexture != null)
                        WritePfm(Path.Combine(folder, shot + "-gbuffer-specular.pfm"), Read(camera.SpecularSmoothnessTexture),
                                 camera.SpecularSmoothnessTexture.width, camera.SpecularSmoothnessTexture.height,
                                 c => new Color((c.r + c.g + c.b) / 3f, c.a, Mathf.Max(c.r, Mathf.Max(c.g, c.b))));
                    // The surfaces as their materials mean them: specular colour's largest
                    // channel, smoothness, how each was found (RtNative.Surface*).
                    if (camera != null && camera.SurfaceTexture != null && camera.SurfaceModeTexture != null)
                    {
                        Color[] intended = Read(camera.SurfaceTexture);
                        Color[] modes = Read(camera.SurfaceModeTexture);
                        for (int i = 0; i < intended.Length && i < modes.Length; i++)
                            intended[i] = new Color(Mathf.Max(intended[i].r, Mathf.Max(intended[i].g, intended[i].b)), intended[i].a,
                                                    modes[i].r * 8f);
                        WritePfm(Path.Combine(folder, shot + "-surface.pfm"), intended, camera.SurfaceTexture.width,
                                 camera.SurfaceTexture.height, c => c);
                    }
                }
                if (!night)
                {
                    // With the G-buffer's shine as Deferred gives it, and the surfaces' overlay.
                    RtCamera.SurfacesAsDeferred = true;
                    yield return ProbeSteps.Frames(30);
                    yield return Shot(folder, time + "-" + pose.Name + "-deferred");
                    RtCamera.SurfacesAsDeferred = false;
                    RtCamera.SurfaceView = true;
                    yield return ProbeSteps.Frames(30);
                    yield return Shot(folder, time + "-" + pose.Name + "-overlay");
                    RtCamera.SurfaceView = false;
                }
            }
        }

        // The vessel and the ground under it seen from where they would mirror the Mun, 20 m
        // out. The shots have the raytracing off (Deferred's reflection probe), on with the parts'
        // smoothness as KSP's highlight (RtCommon.hlsli, LegacySmoothness) and on with
        // Deferred's.
        private static IEnumerator MirrorShots(ReDefinitionAddon addon, string folder)
        {
            CelestialBody mun = FlightGlobals.Bodies.Find(b => b.bodyName == "Mun");
            if (mun == null) yield break;
            RaytracingMode on = RaytracingOn(addon);
            yield return LookTowards(mun, Elevation(mun), 20f);
            Set(addon, RaytracingMode.Off, 10f, 0f);
            yield return Shot(folder, "mirror-off");
            Set(addon, on, 10f, 0f);
            foreach (bool deferred in new[] { false, true })
            {
                RtCamera.DeferredSmoothness = deferred;
                yield return ProbeSteps.Frames(60);
                yield return Shot(folder, "mirror-" + (deferred ? "deferred-smoothness" : "ksp-highlight"));
            }
            RtCamera.DeferredSmoothness = false;
            MoonGlow.HeldOff = true;
            yield return ProbeSteps.Frames(30);
            yield return Shot(folder, "mirror-no-glow");
            MoonGlow.HeldOff = false;
            // Straight down on the vessel from 30 m, as a player looks at a plane on the runway.
            FlightCamera.fetch.SetDistance(30f);
            FlightCamera.fetch.camPitch = 89f * Mathf.Deg2Rad;
            yield return ProbeSteps.Frames(60);
            yield return Shot(folder, "down-on");
            MoonGlow.HeldOff = true;
            yield return ProbeSteps.Frames(30);
            yield return Shot(folder, "down-no-glow");
            MoonGlow.HeldOff = false;
            Set(addon, RaytracingMode.Off, 10f, 0f);
            yield return ProbeSteps.Frames(30);
            yield return Shot(folder, "down-off");
            MoonGlow.HeldOff = true;
            yield return ProbeSteps.Frames(30);
            yield return Shot(folder, "mirror-off-no-glow");
            MoonGlow.HeldOff = false;
            Set(addon, on, 10f, 0f);
        }

        // The raytracing mode the probe's "on" shots use: the player's, or the full one where the player's is off.
        private static RaytracingMode RaytracingOn(ReDefinitionAddon addon)
        {
            return addon.Live.Raytracing == RaytracingMode.Off ? RaytracingMode.Full : addon.Live.Raytracing;
        }

        // The angle at a body between the star and the active vessel, in degrees: 0 full.
        private static float PhaseAngle(CelestialBody body, CelestialBody star)
        {
            Vessel vessel = FlightGlobals.ActiveVessel;
            if (vessel == null || body == null || star == null) return 180f;
            return (float)Vector3d.Angle(star.position - body.position, ((Vector3d)vessel.transform.position) - body.position);
        }

        // A body's elevation over the active vessel's horizon, in degrees.
        private static float Elevation(CelestialBody body)
        {
            Vessel vessel = FlightGlobals.ActiveVessel;
            if (vessel == null || body == null) return -90f;
            Vector3d up = (((Vector3d)vessel.transform.position) - vessel.mainBody.position).normalized;
            return (float)(90.0 - Vector3d.Angle(up, body.position - ((Vector3d)vessel.transform.position)));
        }

        // Time warp on rails at 10 000 times, once the vessel moves less than 0.2 m/s. KSP
        // otherwise warps with physics, at most 4 times. It waits a minute at most for the waves
        // to let a floating capsule settle.
        private static IEnumerator RailsWarp()
        {
            Vessel vessel = FlightGlobals.ActiveVessel;
            float until = Time.realtimeSinceStartup + 60f;
            while (vessel != null && vessel.srfSpeed >= 0.2 && Time.realtimeSinceStartup < until)
                yield return null;
            TimeWarp.fetch.Mode = TimeWarp.Modes.HIGH;
            TimeWarp.SetRate(6, true);
            yield return ProbeSteps.Frames(10);
            Debug.Log(Log.Tag + " Probe: light: time warp at " + TimeWarp.CurrentRate.ToString("0", CultureInfo.InvariantCulture)
                      + " times (" + TimeWarp.WarpMode + "), the vessel at " + (vessel != null ? vessel.srfSpeed : 0.0).ToString("0.00", CultureInfo.InvariantCulture)
                      + " m/s.");
        }

        // Whether the probe steps the game's time on by setting it (a step of JumpStep seconds, two frames each) where it looks
        // for a time of day, as against the warp, which takes some seconds for each half hour. It falls back to the warp
        // where setting the time does not take.
        internal static bool JumpTime = true;
        private const double JumpStep = 120.0, SlowJumpStep = 30.0;

        // The game's time set `step` seconds on; false where it did not take.
        private static IEnumerator JumpOn(double step, Action<bool> took)
        {
            double target = Planetarium.GetUniversalTime() + step;
            Planetarium.SetUniversalTime(target);
            yield return ProbeSteps.Frames(2);
            bool ok = Math.Abs(Planetarium.GetUniversalTime() - target) < 5.0;
            if (!ok) JumpTime = false;
            took(ok);
        }

        // Time on until `wanted` holds, `longest` seconds of game time at most: set on by steps, or warped.
        private static IEnumerator WarpUntil(string what, Func<bool> wanted, Action<bool> done, double longest = 43200.0)
        {
            double started = Planetarium.GetUniversalTime();
            bool found = wanted();
            bool warping = false;
            if (!found && !JumpTime)
            {
                warping = true;
                yield return RailsWarp();
            }
            while (!found && Planetarium.GetUniversalTime() - started < longest)
            {
                if (JumpTime) yield return JumpOn(JumpStep, took => { });
                else
                {
                    if (!warping)
                    {
                        warping = true;
                        yield return RailsWarp();
                    }
                    yield return null;
                }
                found = wanted();
            }
            TimeWarp.SetRate(0, true);
            yield return new WaitForSeconds(3f);
            Debug.Log(Log.Tag + " Probe: light: warped " + (Planetarium.GetUniversalTime() - started).ToString("0", CultureInfo.InvariantCulture)
                      + " s; " + what + (found ? "." : " not found."));
            done(found);
        }

        // The flight camera turned towards a body's direction over the horizon, looking
        // down by `pitch`, from `distance` out. SetCamCoordsFromPosition takes the camera's
        // place relative to the vessel (KerbalEngineer's TargetSelector passes it so);
        // where the camera then does not look the body's way, its world place.
        private static IEnumerator LookTowards(CelestialBody body, float pitch, float distance, bool away = false)
        {
            Vessel vessel = FlightGlobals.ActiveVessel;
            if (vessel == null || body == null) yield break;
            Vector3 up = ((Vector3)(((Vector3d)vessel.transform.position) - vessel.mainBody.position)).normalized;
            Vector3 flat = Vector3.ProjectOnPlane((Vector3)(body.position - ((Vector3d)vessel.transform.position)), up).normalized * (away ? -1f : 1f);
            yield return LookAlong(flat, pitch, distance, body.bodyName + "'s direction, "
                                                        + Elevation(body).ToString("0", CultureInfo.InvariantCulture) + " degrees up");
        }

        // The same along a flat world direction.
        private static IEnumerator LookAlong(Vector3 flat, float pitch, float distance, string what)
        {
            Vessel vessel = FlightGlobals.ActiveVessel;
            if (vessel == null || flat.sqrMagnitude < 0.5f) yield break;
            FlightCamera.fetch.SetDistance(Mathf.Min(distance, FlightCamera.fetch.maxDistance));
            for (int attempt = 0; attempt < 2; attempt++)
            {
                Vector3 up = ((Vector3)(((Vector3d)vessel.transform.position) - vessel.mainBody.position)).normalized;
                Vector3 look = flat * Mathf.Cos(pitch * Mathf.Deg2Rad) - up * Mathf.Sin(pitch * Mathf.Deg2Rad);
                Vector3 place = -look * FlightCamera.fetch.Distance;
                FlightCamera.fetch.SetCamCoordsFromPosition(attempt == 0 ? place : (Vector3)((Vector3d)vessel.transform.position) + place);
                yield return ProbeSteps.Frames(10);
                Vector3 forward = Vector3.ProjectOnPlane(FlightCamera.fetch.transform.forward, up);
                float off = Vector3.Angle(forward, flat);
                Debug.Log(Log.Tag + " Probe: light: camera " + off.ToString("0", CultureInfo.InvariantCulture) + " degrees off "
                          + what + " (" + (attempt == 0 ? "relative" : "world") + " place).");
                if (off < 15f) yield break;
            }
        }

        // A colour PFM file (little-endian floats, bottom row first, as Unity's pixels come), each pixel through value.
        private static void WritePfm(string path, Color[] pixels, int width, int height, Func<Color, Color> value)
        {
            using (var file = new FileStream(path, FileMode.Create))
            using (var writer = new BinaryWriter(file))
            {
                writer.Write(System.Text.Encoding.ASCII.GetBytes("PF\n" + width + " " + height + "\n-1.0\n"));
                foreach (Color pixel in pixels)
                {
                    Color c = value(pixel);
                    writer.Write(c.r);
                    writer.Write(c.g);
                    writer.Write(c.b);
                }
            }
        }

        // A render texture's pixels read back to the CPU as linear floats.
        private static Color[] Read(RenderTexture texture)
        {
            RenderTexture before = RenderTexture.active;
            RenderTexture.active = texture;
            Texture2D read = new Texture2D(texture.width, texture.height, TextureFormat.RGBAFloat, false, true);
            read.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0, false);
            RenderTexture.active = before;
            Color[] pixels = read.GetPixels();
            UnityEngine.Object.Destroy(read);
            return pixels;
        }

        // The raytracing's mode, moonlight and night light in per cent applied as the settings window would.
        private static void Set(ReDefinitionAddon addon, RaytracingMode mode, float moonlight, float nightAmbient)
        {
            OwnSettings before = addon.Live.Clone();
            OwnSettings after = before.Clone();
            after.Raytracing = mode;
            after.RaytracingMoonlight = moonlight;
            after.RaytracingNightAmbient = nightAmbient;
            addon.Apply(before, after);
        }

        // Compares, from above, the albedo the G-buffer holds at the middle of each terrain quad
        // of the main body in view (what the terrain shader drew) with the one the rays
        // take for it, in Unity's colour space. The rays take its material's colour plus its
        // vertex colours times the factor (RtTerrainColour).
        private static IEnumerator GroundAlbedo(ReDefinitionAddon addon, float heading)
        {
            Camera scene = ProbeSteps.SceneCamera();
            PQS pqs = FlightGlobals.currentMainBody != null ? FlightGlobals.currentMainBody.pqsController : null;
            if (scene == null || pqs == null || scene.actualRenderingPath != RenderingPath.DeferredShading)
            {
                Debug.Log(Log.Tag + " Probe: light: no G-buffer or terrain to compare the ground with.");
                yield break;
            }
            FlightCamera.fetch.camHdg = heading;
            FlightCamera.fetch.camPitch = 35f * Mathf.Deg2Rad;
            yield return ProbeSteps.Frames(30);

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

        // Three values for the log, three decimals, comma-separated.
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
                Vector3d up = (((Vector3d)vessel.transform.position) - vessel.mainBody.position).normalized;
                double sunHeight = Vector3d.Dot(up, (sun.position - ((Vector3d)vessel.transform.position)).normalized);
                double munHeight = Vector3d.Dot(up, (mun.position - ((Vector3d)vessel.transform.position)).normalized);
                if (sunHeight < -0.3 && munHeight > 0.6)
                {
                    found = true;
                    break;
                }
            }
            TimeWarp.SetRate(0, true);
            yield return new WaitForSeconds(3f);
            Vector3d up2 = (((Vector3d)vessel.transform.position) - vessel.mainBody.position).normalized;
            Debug.Log(Log.Tag + " Probe: light: warped " + (Planetarium.GetUniversalTime() - started).ToString("0", CultureInfo.InvariantCulture)
                      + " s; the sun at " + Elevation(up2, sun.position - ((Vector3d)vessel.transform.position)) + " degrees, the Mun at "
                      + Elevation(up2, mun.position - ((Vector3d)vessel.transform.position)) + ", its phase angle "
                      + Vector3d.Angle(sun.position - mun.position, ((Vector3d)vessel.transform.position) - mun.position).ToString("0", CultureInfo.InvariantCulture) + " degrees.");
            done(found);
        }

        // The degrees towards stands over the plane up is normal to, whole, for the log.
        private static string Elevation(Vector3d up, Vector3d towards)
        {
            return (90.0 - Vector3d.Angle(up, towards)).ToString("0", CultureInfo.InvariantCulture);
        }
    }
}
