using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
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
        // With it, the ocean out at sea instead: the sun's and the Mun's reflections, each
        // body low, half up and high, with the raytracing on in full.
        internal const string OceanArgument = "-redefinitionProbeOcean";

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

        // The bodies' heights for the ocean: low, its reflection a long path towards the
        // horizon; half up; high, its disc seen steeply in the water. The camera looks
        // down by Pitch, or, for a high body, 10 degrees less than the body stands, so that
        // its reflection lies below the vessel in the frame; from Distance metres out, and
        // once more for a high body from 2 km, where the glints blend into the round glow
        // of the waves' slopes.
        private struct Angle
        {
            internal string Name;
            internal float Lowest, Highest, Pitch, Distance;
            internal bool Steep;
        }

        private static readonly Angle[] Angles =
        {
            new Angle { Name = "low", Lowest = 4f, Highest = 10f, Pitch = 2f, Distance = 50f },
            new Angle { Name = "half", Lowest = 22f, Highest = 32f, Pitch = 5f, Distance = 50f },
            new Angle { Name = "high", Lowest = 55f, Highest = 72f, Steep = true, Distance = 50f },
            new Angle { Name = "high-far", Lowest = 55f, Highest = 72f, Steep = true, Distance = 2000f },
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
                if (Array.IndexOf(Environment.GetCommandLineArgs(), OceanArgument) >= 0)
                {
                    // Out at sea (SceneProbe, -redefinitionProbeSea): the sun, then the Mun at
                    // night, each low, half up and high over the horizon (Angles); the camera
                    // 50 m out towards the body, its reflection in the frame.
                    CelestialBody sun = RtKspMods.StarBody();
                    CelestialBody mun = FlightGlobals.Bodies.Find(b => b.bodyName == "Mun");
                    foreach (CelestialBody body in new[] { sun, mun })
                        foreach (Angle angle in Angles)
                        {
                            if (body == null) continue;
                            Angle a = angle;
                            CelestialBody b = body;
                            Func<bool> wanted = b == sun
                                ? (Func<bool>)(() => Elevation(sun) > a.Lowest && Elevation(sun) < a.Highest)
                                : () => Elevation(sun) < -12f && Elevation(b) > a.Lowest && Elevation(b) < a.Highest && PhaseAngle(b, sun) < 60f;
                            string name = (b == sun ? "sun" : "mun") + "-" + a.Name;
                            bool reached = false;
                            yield return WarpUntil(name, wanted, result => reached = result,
                                                   b == sun ? 43200.0 : 4.0 * b.orbit.period);
                            if (!reached) continue;
                            float elevation = Elevation(b);
                            yield return LookTowards(b, a.Steep ? elevation - 10f : a.Pitch, a.Distance);
                            yield return OceanShot(addon, folder, name, elevation);
                        }
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

        // The angle at a body between the star and the active vessel, in degrees: 0 full.
        private static float PhaseAngle(CelestialBody body, CelestialBody star)
        {
            Vessel vessel = FlightGlobals.ActiveVessel;
            if (vessel == null || body == null || star == null) return 180f;
            return (float)Vector3d.Angle(star.position - body.position, vessel.CoMD - body.position);
        }

        // A body's elevation over the active vessel's horizon, in degrees.
        private static float Elevation(CelestialBody body)
        {
            Vessel vessel = FlightGlobals.ActiveVessel;
            if (vessel == null || body == null) return -90f;
            Vector3d up = (vessel.CoMD - vessel.mainBody.position).normalized;
            return (float)(90.0 - Vector3d.Angle(up, body.position - vessel.CoMD));
        }

        // Time warp on rails at 10 000 times: once the vessel moves less than 0.2 m/s, as
        // KSP otherwise warps with physics, at most 4 times; a minute at most for the waves
        // to let a floating capsule settle.
        private static IEnumerator RailsWarp()
        {
            Vessel vessel = FlightGlobals.ActiveVessel;
            float until = Time.realtimeSinceStartup + 60f;
            while (vessel != null && vessel.srfSpeed >= 0.2 && Time.realtimeSinceStartup < until)
                yield return null;
            TimeWarp.fetch.Mode = TimeWarp.Modes.HIGH;
            TimeWarp.SetRate(6, true);
            for (int i = 0; i < 10; i++) yield return null;
            Debug.Log(Log.Tag + " Probe: light: time warp at " + TimeWarp.CurrentRate.ToString("0", CultureInfo.InvariantCulture)
                      + " times (" + TimeWarp.WarpMode + "), the vessel at " + (vessel != null ? vessel.srfSpeed : 0.0).ToString("0.00", CultureInfo.InvariantCulture)
                      + " m/s.");
        }

        // Time warp until `wanted` holds, `longest` seconds of game time at most.
        private static IEnumerator WarpUntil(string what, Func<bool> wanted, Action<bool> done, double longest = 43200.0)
        {
            double started = Planetarium.GetUniversalTime();
            bool found = wanted();
            if (!found) yield return RailsWarp();
            while (!found && Planetarium.GetUniversalTime() - started < longest)
            {
                yield return null;
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
        private static IEnumerator LookTowards(CelestialBody body, float pitch, float distance)
        {
            Vessel vessel = FlightGlobals.ActiveVessel;
            if (vessel == null || body == null) yield break;
            FlightCamera.fetch.SetDistance(Mathf.Min(distance, FlightCamera.fetch.maxDistance));
            for (int attempt = 0; attempt < 2; attempt++)
            {
                Vector3 up = ((Vector3)(vessel.CoMD - vessel.mainBody.position)).normalized;
                Vector3 flat = Vector3.ProjectOnPlane((Vector3)(body.position - vessel.CoMD), up).normalized;
                Vector3 look = flat * Mathf.Cos(pitch * Mathf.Deg2Rad) - up * Mathf.Sin(pitch * Mathf.Deg2Rad);
                Vector3 place = -look * FlightCamera.fetch.Distance;
                FlightCamera.fetch.SetCamCoordsFromPosition(attempt == 0 ? place : (Vector3)vessel.CoMD + place);
                for (int i = 0; i < 10; i++) yield return null;
                Vector3 forward = Vector3.ProjectOnPlane(FlightCamera.fetch.transform.forward, up);
                float off = Vector3.Angle(forward, flat);
                Debug.Log(Log.Tag + " Probe: light: camera " + off.ToString("0", CultureInfo.InvariantCulture) + " degrees off "
                          + body.bodyName + "'s direction, " + Elevation(body).ToString("0", CultureInfo.InvariantCulture)
                          + " degrees up (" + (attempt == 0 ? "relative" : "world") + " place).");
                if (off < 15f) yield break;
            }
        }

        // The clouds out of the frame for the ocean's checks: EVE's components (namespace
        // Atmosphere) and every renderer drawing with one of its shaders (EVE/...).
        private static bool cloudsHidden;

        internal static void HideClouds()
        {
            if (cloudsHidden) return;
            cloudsHidden = true;
            int behaviours = 0, renderers = 0;
            foreach (MonoBehaviour b in UnityEngine.Object.FindObjectsOfType<MonoBehaviour>())
                if (b != null && b.enabled && b.GetType().Namespace == "Atmosphere")
                {
                    b.enabled = false;
                    behaviours++;
                }
            foreach (Renderer r in UnityEngine.Object.FindObjectsOfType<Renderer>())
            {
                if (r == null || !r.enabled) continue;
                foreach (Material m in r.sharedMaterials)
                    if (m != null && m.shader != null && m.shader.name.StartsWith("EVE/", StringComparison.Ordinal))
                    {
                        r.enabled = false;
                        renderers++;
                        break;
                    }
            }
            // Their shadows on the ocean: Scatterer writes them into its ocean's command
            // buffers with EVE's AddOceanShadowCommands when it makes them (Scatterer
            // 0.0908, OceanCommandBuffer.RecreateCommandBuffers): EVE's delegate dropped,
            // and the buffers made again as for a new size.
            bool shadows = false;
            try
            {
                Type scattererType = TypeLookup.Find("Scatterer.Scatterer");
                object scatterer = scattererType?.GetProperty("Instance", TypeLookup.Any)?.GetValue(null, null)
                                   ?? scattererType?.GetField("Instance", TypeLookup.Any)?.GetValue(null);
                object handler = scatterer?.GetType().GetField("EveReflectionHandler", TypeLookup.Any)?.GetValue(scatterer);
                FieldInfo shadowDelegate = handler?.GetType().GetField("EVEOceanShadowsMethodDelegate", TypeLookup.Any);
                if (shadowDelegate != null)
                {
                    shadowDelegate.SetValue(handler, null);
                    Type buffers = TypeLookup.Find("Scatterer.OceanCommandBuffer");
                    FieldInfo width = buffers?.GetField("width", TypeLookup.Any);
                    if (buffers != null && width != null)
                        foreach (UnityEngine.Object buffer in UnityEngine.Object.FindObjectsOfType(buffers))
                            width.SetValue(buffer, 0);
                    shadows = true;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning(Log.Tag + " Probe: the clouds' shadows on the ocean stay: " + e.Message);
            }
            Debug.Log(Log.Tag + " Probe: light: clouds hidden, " + behaviours + " components, " + renderers + " renderers"
                      + (shadows ? ", their shadows off the ocean." : "."));
        }

        // The ocean with the raytracing on in full, the clouds hidden, the interface away.
        private static IEnumerator OceanShot(ReDefinitionAddon addon, string folder, string name, float elevation)
        {
            HideClouds();
            GameEvents.onHideUI.Fire();
            Set(addon, RaytracingMode.Full, addon.Live.RaytracingMoonlight, addon.Live.RaytracingNightAmbient);
            yield return Shot(folder, name);
            LogOceanTextures(folder, name);
            Camera camera = FlightCamera.fetch.mainCamera;
            Debug.Log(Log.Tag + " Probe: light " + name + ": the body " + elevation.ToString("0", CultureInfo.InvariantCulture)
                      + " degrees up, the camera " + (FlightCamera.fetch.camPitch * Mathf.Rad2Deg).ToString("0", CultureInfo.InvariantCulture)
                      + " degrees down, its field of view " + (camera != null ? camera.fieldOfView : 0f).ToString("0", CultureInfo.InvariantCulture)
                      + " degrees high, sun reflection " + addon.Live.SunReflection.ToString("0", CultureInfo.InvariantCulture)
                      + " %, moon reflection " + addon.Live.RaytracingMoonReflection.ToString("0", CultureInfo.InvariantCulture)
                      + " %, the sun's colour " + (RtCamera.Current != null ? RtCamera.Current.SunColor().ToString() : "-")
                      + ", traced " + (RtCamera.Current != null ? RtCamera.Current.TracedSunColour.ToString() : "-") + ".");
        }

        // The ocean's raytraced reflections as the last frame left them: the share of
        // pixels the ocean covers with a weight, and of the traced ones that met something.
        private static void LogOceanTextures(string folder, string name)
        {
            RtCamera camera = RtCamera.Current;
            RenderTexture reflections = camera != null ? camera.OceanReflectionsTexture : null;
            RenderTexture traced = camera != null ? camera.OceanTracedTexture : null;
            if (reflections == null || traced == null)
            {
                Debug.Log(Log.Tag + " Probe: light " + name + ": no ocean reflections.");
                return;
            }
            Color[] r = Read(reflections), t = Read(traced);
            // The traced reflections as a picture beside the screenshot, x / (1 + x), a
            // binary PPM from the top row down.
            using (var file = new FileStream(Path.Combine(folder, name + "-traced.ppm"), FileMode.Create))
            {
                byte[] header = System.Text.Encoding.ASCII.GetBytes("P6\n" + traced.width + " " + traced.height + "\n255\n");
                file.Write(header, 0, header.Length);
                var row = new byte[traced.width * 3];
                for (int y = traced.height - 1; y >= 0; y--)
                {
                    for (int x = 0; x < traced.width; x++)
                    {
                        Color c = t[y * traced.width + x];
                        row[x * 3] = (byte)(255f * Mathf.Clamp01(c.r / (1f + Mathf.Max(c.r, 0f))));
                        row[x * 3 + 1] = (byte)(255f * Mathf.Clamp01(c.g / (1f + Mathf.Max(c.g, 0f))));
                        row[x * 3 + 2] = (byte)(255f * Mathf.Clamp01(c.b / (1f + Mathf.Max(c.b, 0f))));
                    }
                    file.Write(row, 0, row.Length);
                }
            }
            // The traced hits by distance: what the reflected rays meet.
            int[] near = new int[6];
            float[] bounds = { 1f, 10f, 100f, 1000f, 10000f, 60000f };
            foreach (Color c in t)
                for (int k = 0; k < bounds.Length; k++)
                    if (c.a > 0f && c.a < bounds[k]) { near[k]++; break; }
            // The light of the sun's and the moons' discs where the rays met nothing (w 60000
            // and more), and what the ocean is given for it: the brightest, the number above
            // a hundredth.
            float bodies = 0f, given = 0f;
            int lit = 0;
            for (int i = 0; i < t.Length; i++)
            {
                if (t[i].a < 60000f) continue;
                float value = Mathf.Max(t[i].r, Mathf.Max(t[i].g, t[i].b));
                bodies = Mathf.Max(bodies, value);
                if (value > 0.01f) lit++;
                given = Mathf.Max(given, r[i].g * r[i].a);
            }
            Debug.Log(Log.Tag + " Probe: light " + name + ": the discs' light in the reflections up to "
                      + bodies.ToString("0.000", CultureInfo.InvariantCulture) + ", above 0.01 on " + lit
                      + " pixels; the ocean given up to " + given.ToString("0.000", CultureInfo.InvariantCulture) + " (weight times colour).");
            Debug.Log(Log.Tag + " Probe: light " + name + ": hits within 1, 10, 100, 1000, 10000, 60000 m: "
                      + string.Join(", ", Array.ConvertAll(near, n => n.ToString(CultureInfo.InvariantCulture))) + ".");
            int weighted = 0, hit = 0;
            double sum = 0;
            for (int i = 0; i < r.Length; i++)
            {
                if (r[i].a > 0.5f) { weighted++; sum += r[i].r + r[i].g + r[i].b; }
                if (t[i].a > 0f && t[i].a < 60000f) hit++;
            }
            // The ocean's roughness (GGX's alpha from the capture's smoothness) in the frame's
            // upper, middle and lower third: far, half way, near.
            RenderTexture rough = camera.OceanRoughnessTexture;
            if (rough != null)
            {
                Color[] g = Read(rough);
                var bands = new List<float>[3] { new List<float>(), new List<float>(), new List<float>() };
                for (int i = 0; i < g.Length; i += 7)
                {
                    if (g[i].r <= 0f) continue;
                    float perceptual = 1f - g[i].a;
                    int row = i / rough.width;
                    bands[Mathf.Clamp(2 - row * 3 / rough.height, 0, 2)].Add(perceptual * perceptual);
                }
                string[] names = { "far", "half way", "near" };
                var parts = new List<string>();
                for (int k = 0; k < 3; k++)
                {
                    bands[k].Sort();
                    if (bands[k].Count == 0) { parts.Add(names[k] + " none"); continue; }
                    Func<float, string> at = q => bands[k][Mathf.Min((int)(q * bands[k].Count), bands[k].Count - 1)]
                        .ToString("0.0000", CultureInfo.InvariantCulture);
                    parts.Add(names[k] + " " + at(0.1f) + "/" + at(0.5f) + "/" + at(0.9f));
                }
                Debug.Log(Log.Tag + " Probe: light " + name + ": ocean alpha at 10/50/90 %: " + string.Join("; ", parts.ToArray()) + ".");
            }
            Debug.Log(Log.Tag + " Probe: light " + name + ": ocean reflections weighted on " + weighted + " of " + r.Length
                      + " pixels (mean " + (weighted > 0 ? (sum / weighted / 3).ToString("0.000", CultureInfo.InvariantCulture) : "-")
                      + "), traced rays met something on " + hit + ".");
        }

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
