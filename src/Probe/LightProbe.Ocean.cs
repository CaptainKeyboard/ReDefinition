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
    // The light probe's ocean shots (-redefinitionProbeOcean): the sun's and the Mun's reflections on the sea at the
    // heights of Heights, with the traced reflections, the waves and the textures written beside each shot.
    internal static partial class LightProbe
    {
        // The ocean's cases (tools/sea_reflection_check.py, its CASES). A case is a body's height over
        // the horizon, and for each the camera's height over the sea and how far it looks
        // down. There is one warp to each height, the cases at it one after the other. The camera
        // looks at the vessel, so from Height / sin(Down) metres out.
        private struct SeaCase
        {
            // The case's name in the shots ("case01"); the camera's metres over the sea and degrees down.
            internal string Name;
            internal float Height, Down;
        }

        // -redefinitionProbeCases sun-case09,mun-case06: only these shots.
        private const string CasesArgument = "-redefinitionProbeCases";

        // Whether the shot is to be taken: every shot without -redefinitionProbeCases, else only those it names.
        private static bool Wanted(string shot)
        {
            string cases = CommandLine.After(CasesArgument);
            return cases == null || Array.IndexOf(cases.Split(','), shot) >= 0;
        }

        // One height of a body over the horizon, with the sea cases shot there.
        private struct BodyHeight
        {
            // The body's elevation in degrees (warped to within 2.5 of it); the cases at it.
            internal float Elevation;
            internal SeaCase[] Cases;
        }

        // The ocean's heights, lowest first, each with its cases.
        private static readonly BodyHeight[] Heights =
        {
            new BodyHeight { Elevation = 5f, Cases = new[] { new SeaCase { Name = "case01", Height = 2f, Down = 1f },
                                                             new SeaCase { Name = "case10", Height = 15000f, Down = 8f } } },
            new BodyHeight { Elevation = 10f, Cases = new[] { new SeaCase { Name = "case05", Height = 300f, Down = 5f } } },
            new BodyHeight { Elevation = 15f, Cases = new[] { new SeaCase { Name = "case02", Height = 10f, Down = 5f } } },
            new BodyHeight { Elevation = 25f, Cases = new[] { new SeaCase { Name = "case03", Height = 50f, Down = 10f },
                                                              new SeaCase { Name = "case06", Height = 1000f, Down = 30f } } },
            new BodyHeight { Elevation = 35f, Cases = new[] { new SeaCase { Name = "case09", Height = 5000f, Down = 30f } } },
            new BodyHeight { Elevation = 45f, Cases = new[] { new SeaCase { Name = "case07", Height = 1000f, Down = 45f } } },
            new BodyHeight { Elevation = 60f, Cases = new[] { new SeaCase { Name = "case04", Height = 50f, Down = 50f } } },
            new BodyHeight { Elevation = 65f, Cases = new[] { new SeaCase { Name = "case08", Height = 2000f, Down = 55f } } },
        };

        // The ocean's shots, out at sea (SceneProbe, -redefinitionProbeSea). The sun comes first, then the Mun at night, each at
        // every height of Heights and each case's camera looking towards it. Extras are the Mun's waterline, vessel and zoom shots.
        private static IEnumerator OceanShots(ReDefinitionAddon addon, string folder)
        {
                // Out at sea (SceneProbe, -redefinitionProbeSea): the sun, then the Mun at
                // night, at each height (Heights), each case's camera looking towards it.
                CelestialBody sun = RtKspMods.StarBody();
                // "mun-" stands for the moon asked for (SceneProbe.MoonName), the Mun by default.
                CelestialBody mun = FlightGlobals.Bodies.Find(b => b.bodyName == SceneProbe.MoonName);
                foreach (CelestialBody body in new[] { sun, mun })
                    foreach (BodyHeight height in Heights)
                    {
                        if (body == null) continue;
                        string bodyPrefix = body == sun ? "sun-" : "mun-";
                        if (!Array.Exists(height.Cases, c => Wanted(bodyPrefix + c.Name))
                            && !(body == mun && height.Elevation <= 5f && Wanted("mun-zoom"))
                            && !(body == mun && height.Elevation == 25f && (Wanted("mun-vessel") || Wanted("mun-waterline"))))
                            continue;
                        float lowest = height.Elevation - 2.5f, highest = height.Elevation + 2.5f;
                        CelestialBody b = body;
                        Func<bool> wanted = b == sun
                            ? (Func<bool>)(() => Elevation(sun) > lowest && Elevation(sun) < highest)
                            : () => Elevation(sun) < -12f && Elevation(b) > lowest && Elevation(b) < highest && PhaseAngle(b, sun) < 60f;
                        string prefix = b == sun ? "sun-" : "mun-";
                        bool reached = false;
                        yield return WarpUntil(prefix + height.Elevation.ToString("0", CultureInfo.InvariantCulture), wanted,
                                               result => reached = result,
                                               b == sun ? Math.Max(43200.0, 1.2 * FlightGlobals.currentMainBody.solarDayLength) : 4.0 * b.orbit.period);
                        if (!reached) continue;
                        foreach (SeaCase c in height.Cases)
                        {
                            if (!Wanted(prefix + c.Name)) continue;
                            float distance = c.Height / Mathf.Sin(c.Down * Mathf.Deg2Rad);
                            yield return LookTowards(b, c.Down, distance);
                            yield return OceanShot(addon, folder, prefix + c.Name, Elevation(b));
                        }
                        // The vessel 10 m away at its waterline, towards the Mun and away from
                        // it: the raytracing off, on, without the discs on Scatterer's ocean,
                        // without the reflections handed to it -- which lights what.
                        if (b == mun && height.Elevation == 25f && Wanted("mun-waterline"))
                        {
                            foreach (bool away in new[] { false, true })
                            {
                                yield return LookTowards(b, 12f, 10f, away);
                                string side = "mun-waterline-" + (away ? "away" : "towards");
                                RaytracingMode on = RaytracingOn(addon);
                                Set(addon, RaytracingMode.Off, addon.Live.RaytracingMoonlight, addon.Live.RaytracingNightAmbient);
                                yield return ProbeSteps.Frames(30);
                                yield return Shot(folder, side + "-off");
                                Set(addon, on, addon.Live.RaytracingMoonlight, addon.Live.RaytracingNightAmbient);
                                yield return ProbeSteps.Frames(60);
                                yield return Shot(folder, side + "-on");
                                LogOceanTextures(folder, side + "-on", null);
                                WriteNormals(folder, side + "-on-frame", RtCamera.Current);
                                RtCamera.OceanDiscsHeldOff = true;
                                yield return ProbeSteps.Frames(30);
                                yield return Shot(folder, side + "-no-discs");
                                RtCamera.OceanDiscsHeldOff = false;
                                RtCamera.OceanHandoffHeldOff = true;
                                yield return ProbeSteps.Frames(30);
                                yield return Shot(folder, side + "-no-handoff");
                                RtCamera.OceanHandoffHeldOff = false;
                                MoonGlow.HeldOff = true;
                                yield return ProbeSteps.Frames(30);
                                yield return Shot(folder, side + "-no-glow");
                                MoonGlow.HeldOff = false;
                            }
                        }
                        // The vessel 10 m away, its top mirroring the Mun to the camera, with
                        // the parts' smoothness as Deferred writes it and as the raytracing
                        // takes it (RtCommon.hlsli, LegacySmoothness): a mirror or a sheen.
                        if (b == mun && height.Elevation == 25f && Wanted("mun-vessel"))
                        {
                            float elevation = Elevation(b);
                            yield return LookTowards(b, elevation, 10f);
                            foreach (bool deferred in new[] { true, false })
                            {
                                RtCamera.DeferredSmoothness = deferred;
                                yield return ProbeSteps.Frames(60);
                                yield return OceanShot(addon, folder, "mun-vessel-" + (deferred ? "deferred" : "legacy"), elevation);
                            }
                            RtCamera.DeferredSmoothness = false;
                        }
                        // The Mun low once more, looked at level through a narrow field of
                        // view that just holds it: its glow (MoonGlow) on its disc at any
                        // field of view. The flight camera stays over the sea, so it cannot
                        // look up to a higher Mun.
                        if (b == mun && height.Elevation <= 5f && Wanted("mun-zoom"))
                        {
                            float elevation = Elevation(b);
                            FlightCamera.fetch.SetFoV(Mathf.Max(15f, 2f * elevation + 6f));
                            yield return LookTowards(b, 0f, 50f);
                            yield return OceanShot(addon, folder, "mun-zoom", elevation);
                            FlightCamera.fetch.SetFoV(FlightCamera.fetch.fovDefault);
                        }
                    }
        }

        // Whether HideClouds has hidden the clouds already.
        private static bool cloudsHidden;

        // The clouds out of the frame for the ocean's checks: EVE's components (namespace
        // Atmosphere) and every renderer drawing with one of its shaders (EVE/...).
        internal static void HideClouds()
        {
            // -redefinitionProbeClouds: the clouds kept, for the raytraced clouds' checks.
            if (cloudsHidden || CommandLine.Has("-redefinitionProbeClouds")) return;
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
            Color[] mean = null;
            yield return AverageTraced(result => mean = result);
            LogOceanTextures(folder, name, mean);
            yield return WavesInPlace(folder, name);
            // KSP's own sea's tests as colour (ReDefinitionDeferredReflections.shader,
            // _ReDefinitionWaterDebug): which fails where.
            if (RtCamera.StockSeaTraced)
            {
                Shader.SetGlobalFloat(WaterDebugId, 1f);
                yield return ProbeSteps.Frames(5);
                yield return Shot(folder, name + "-water-debug");
                Shader.SetGlobalFloat(WaterDebugId, 0f);
                yield return ProbeSteps.Frames(5);
            }
            Camera camera = FlightCamera.fetch.mainCamera;
            Debug.Log(Log.Tag + " Probe: light " + name + ": the body " + elevation.ToString("0", CultureInfo.InvariantCulture)
                      + " degrees up, the camera " + (FlightCamera.fetch.camPitch * Mathf.Rad2Deg).ToString("0", CultureInfo.InvariantCulture)
                      + " degrees down, its field of view " + (camera != null ? camera.fieldOfView : 0f).ToString("0", CultureInfo.InvariantCulture)
                      + " degrees high, sun reflection " + addon.Live.SunReflection.ToString("0", CultureInfo.InvariantCulture)
                      + " %, moon reflection " + addon.Live.RaytracingMoonReflection.ToString("0", CultureInfo.InvariantCulture)
                      + " %, the sun's colour " + (RtCamera.Current != null ? RtCamera.Current.SunColor().ToString() : "-")
                      + ", traced " + (RtCamera.Current != null ? RtCamera.Current.TracedSunColour.ToString() : "-") + ".");
        }

        // The shader property of the water debug view (ReDefinitionDeferredReflections.shader), which OceanShot turns on
        // for one shot of KSP's own sea.
        private static readonly int WaterDebugId = Shader.PropertyToID("_ReDefinitionWaterDebug");

        // Whether the waves stay on the water as the camera moves
        // (tools/sea_reflection_check.py --waves). It reads the water's normals with the waves' time
        // held, before and after the camera moved a tenth further out. It does so once with the sea's
        // frame fixed to the body (RtCamera.Sea) and once with the plane under the camera.
        private static IEnumerator WavesInPlace(string folder, string name)
        {
            RtCamera camera = RtCamera.Current;
            if (camera == null || camera.OceanTracedTexture == null || FlightCamera.fetch == null) yield break;
            float distance = FlightCamera.fetch.Distance;
            RtCamera.WaveTimeHeld = Time.time % 3600f;
            RtCamera.WaterNormalsShown = true;
            try
            {
                foreach (bool fixedToBody in new[] { true, false })
                {
                    RtCamera.SeaFrameLeftOut = !fixedToBody;
                    string tag = name + "-waves-" + (fixedToBody ? "body" : "camera");
                    yield return ProbeSteps.Frames(20);
                    yield return new WaitForEndOfFrame();
                    WriteNormals(folder, tag + "-a", camera);
                    FlightCamera.fetch.SetDistance(distance * 1.1f);
                    yield return ProbeSteps.Frames(60);
                    yield return new WaitForEndOfFrame();
                    WriteNormals(folder, tag + "-b", camera);
                    FlightCamera.fetch.SetDistance(distance);
                    yield return ProbeSteps.Frames(60);
                }
            }
            finally
            {
                RtCamera.SeaFrameLeftOut = false;
                RtCamera.WaterNormalsShown = false;
                RtCamera.WaveTimeHeld = null;
            }
        }

        // The ocean's traced reflections, the frame's depth and the ocean's own depth as PFM, with the shot's data.
        private static void WriteNormals(string folder, string name, RtCamera camera)
        {
            RenderTexture traced = camera.OceanTracedTexture;
            if (traced == null) return;
            WritePfm(Path.Combine(folder, name + "-traced.pfm"), Read(traced), traced.width, traced.height, c => c);
            if (camera.DepthTexture != null)
                WritePfm(Path.Combine(folder, name + "-depth.pfm"), Read(camera.DepthTexture), camera.DepthTexture.width,
                         camera.DepthTexture.height, c => new Color(c.r, 0f, 0f));
            RenderTexture oceanDepth = camera.OceanDepthTexture;
            if (oceanDepth != null && !RtCamera.StockSeaTraced)
                WritePfm(Path.Combine(folder, name + "-ocean-depth.pfm"), Read(oceanDepth), oceanDepth.width, oceanDepth.height,
                         c => new Color(c.r, 0f, 0f));
            WriteShotData(Path.Combine(folder, name + ".json"), camera, traced);
        }

        // How many frames of the traced reflections AverageTraced reads, and the seconds between them.
        private const int TracedSamples = 16;
        private const float TracedSpacing = 0.5f;

        // The ocean's traced reflections averaged over TracedSamples frames, into done; null where there are none or
        // the texture changes meanwhile.
        private static IEnumerator AverageTraced(Action<Color[]> done)
        {
            RtCamera camera = RtCamera.Current;
            RenderTexture traced = camera != null ? camera.OceanTracedTexture : null;
            if (traced == null)
            {
                done(null);
                yield break;
            }
            Color[] sum = null;
            var wait = new WaitForSeconds(TracedSpacing);
            for (int k = 0; k < TracedSamples; k++)
            {
                yield return wait;
                yield return new WaitForEndOfFrame();
                if (camera.OceanTracedTexture != traced)
                {
                    done(null);
                    yield break;
                }
                Color[] sample = Read(traced);
                if (sum == null) sum = sample;
                else
                    for (int i = 0; i < sum.Length; i++) sum[i] += sample[i];
            }
            for (int i = 0; i < sum.Length; i++) sum[i] /= TracedSamples;
            done(sum);
        }

        // The ocean's reflection textures of a shot: written as pictures beside it (the traced light, its mean when
        // given, roughness, normals, depth, G-buffer) with the shot's data, and summed up into the log.
        private static void LogOceanTextures(string folder, string name, Color[] mean)
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
            // The traced light as it is, and the ocean's roughness, as float pictures (PFM,
            // from the bottom row up), and what was given for them: the numbers
            // tools/sea_reflection_check.py checks against its model.
            WritePfm(Path.Combine(folder, name + "-traced.pfm"), t, traced.width, traced.height, c => c);
            WritePfm(Path.Combine(folder, name + "-traced-w.pfm"), t, traced.width, traced.height, c => new Color(c.a, 0f, 0f));
            if (mean != null)
                WritePfm(Path.Combine(folder, name + "-traced-mean.pfm"), mean, traced.width, traced.height, c => c);
            RenderTexture roughness = camera.OceanRoughnessTexture;
            if (roughness != null)
                WritePfm(Path.Combine(folder, name + "-alpha.pfm"), Read(roughness), roughness.width, roughness.height,
                         c => c.r > 0f ? new Color((1f - c.a) * (1f - c.a), 0f, 0f) : Color.clear);
            RenderTexture normals = camera.OceanSurfaceTexture;
            if (normals != null)
                WritePfm(Path.Combine(folder, name + "-normal.pfm"), Read(normals), normals.width, normals.height,
                         c => new Color(c.r * 2f - 1f, c.g * 2f - 1f, c.b * 2f - 1f));
            // The frame's depth (the device's, as Unity's depth texture holds it) and G-buffer
            // normal, as the reflections' shader composes KSP's own sea with them.
            if (camera.DepthTexture != null)
                WritePfm(Path.Combine(folder, name + "-depth.pfm"), Read(camera.DepthTexture), camera.DepthTexture.width,
                         camera.DepthTexture.height, c => new Color(c.r, 0f, 0f));
            if (camera.SpecularSmoothnessTexture != null)
                WritePfm(Path.Combine(folder, name + "-gbuffer-specular.pfm"), Read(camera.SpecularSmoothnessTexture),
                         camera.SpecularSmoothnessTexture.width, camera.SpecularSmoothnessTexture.height,
                         c => new Color((c.r + c.g + c.b) / 3f, c.a, 0f));
            if (camera.NormalTexture != null)
                WritePfm(Path.Combine(folder, name + "-gbuffer-normal.pfm"), Read(camera.NormalTexture), camera.NormalTexture.width,
                         camera.NormalTexture.height, c => new Color(c.r * 2f - 1f, c.g * 2f - 1f, c.b * 2f - 1f));
            WriteShotData(Path.Combine(folder, name + ".json"), camera, traced);
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

        // The camera, the sea's sphere and the lights as the raytracing was given them for a
        // shot, world space, JSON.
        private static void WriteShotData(string path, RtCamera camera, RenderTexture traced)
        {
            Camera view = camera.GetComponent<Camera>();
            CelestialBody main = FlightGlobals.currentMainBody;
            var lights = new float[16];
            var surfaces = new float[8];
            int count = camera.Lights(lights, surfaces);
            Vector4 sun = camera.Sun();
            Color sunColour = camera.TracedSunColour;
            Func<float, string> f = v => v.ToString("R", CultureInfo.InvariantCulture);
            Func<double, string> d = v => v.ToString("R", CultureInfo.InvariantCulture);
            Func<Matrix4x4, string> m = x =>
            {
                var parts = new string[16];
                for (int i = 0; i < 16; i++) parts[i] = f(x[i / 4, i % 4]);
                return "[" + string.Join(", ", parts) + "]";
            };
            Func<float[], string> a = x => "[" + string.Join(", ", Array.ConvertAll(x, v => f(v))) + "]";
            Vector3d eye = view.transform.position;
            var text = new System.Text.StringBuilder();
            text.Append("{\n");
            text.Append("  \"width\": " + traced.width + ", \"height\": " + traced.height + ",\n");
            text.Append("  \"projection\": " + m(view.nonJitteredProjectionMatrix) + ",\n");
            text.Append("  \"cameraToWorld\": " + m(view.cameraToWorldMatrix) + ",\n");
            text.Append("  \"eye\": [" + d(eye.x) + ", " + d(eye.y) + ", " + d(eye.z) + "],\n");
            text.Append("  \"bodyCentre\": [" + d(main.position.x) + ", " + d(main.position.y) + ", " + d(main.position.z) + "],\n");
            text.Append("  \"bodyRadius\": " + d(main.Radius) + ",\n");
            text.Append("  \"sun\": [" + f(sun.x) + ", " + f(sun.y) + ", " + f(sun.z) + ", " + f(sun.w) + "],\n");
            text.Append("  \"sunColour\": [" + f(sunColour.r) + ", " + f(sunColour.g) + ", " + f(sunColour.b) + "],\n");
            text.Append("  \"sunMirror\": " + f(camera.SunMirror()) + ",\n");
            text.Append("  \"gammaInputs\": " + (QualitySettings.activeColorSpace == ColorSpace.Gamma ? "true" : "false") + ",\n");
            text.Append("  \"stockWater\": " + (camera.StockWater() ? "true" : "false") + ",\n");
            ReflectionProbe probe = FlightCamera.fetch != null && FlightCamera.fetch.reflectionProbe != null
                ? FlightCamera.fetch.reflectionProbe.probeComponent : null;
            if (probe != null)
                text.Append("  \"reflectionProbe\": {\"enabled\": " + (probe.enabled && probe.gameObject.activeInHierarchy ? "true" : "false")
                            + ", \"position\": [" + f(probe.transform.position.x) + ", " + f(probe.transform.position.y) + ", "
                            + f(probe.transform.position.z) + "], \"size\": " + f(probe.size.x) + ", \"blendDistance\": "
                            + f(probe.blendDistance) + "},\n");
            text.Append("  \"defaultReflection\": \"" + RenderSettings.defaultReflectionMode + "\",\n");
            text.Append("  \"lightCount\": " + count + ",\n");
            text.Append("  \"lights\": " + a(lights) + ",\n");
            text.Append("  \"surfaces\": " + a(surfaces) + "\n");
            text.Append("}\n");
            File.WriteAllText(path, text.ToString());
        }
    }
}
