using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using ReDefinition.Core;
using ReDefinition.Modules;
using ReDefinition.Raytracing;
using UnityEngine;

namespace ReDefinition.Probe
{
    // -redefinitionProbeCloudRatios: how bright the clouds are against the ground under them. It looks by day, in the dusk and
    // at night, with the raytracing on and off, from four camera heights over one place, 14 km up to 150 km.
    // Each measure is three shots from one place, the clouds switched off twice and on once. The clouds are the pixels whose
    // colour changed by more than the two shots without them differ among themselves, and the ground behind them is what the
    // first shot shows there. Only the middle of the picture counts. The log gets the numbers; the shots go to the folder.
    internal static partial class LightProbe
    {
        internal const string CloudRatiosArgument = "-redefinitionProbeCloudRatios";

        // The camera's heights over the vessel, in metres, and its pitch: looking down on the place.
        private static readonly float[] RatioHeights = { 14000f, 40000f, 90000f, 150000f };
        private const float RatioPitch = 70f;
        // Frames the clouds and their light need after they are switched, and after a mode changes.
        private const int RatioFramesAfterSwitch = 60, RatioFramesAfterMode = 90;

        // Whether each measure also looks at the clouds without the moon's light on them.
        private const bool MoonOnClouds = true;

        // EVE's components and renderers HideClouds finds, as far as they were switched off here, to switch on again.
        private static readonly List<Behaviour> switchedBehaviours = new List<Behaviour>();
        private static readonly List<Renderer> switchedRenderers = new List<Renderer>();

        // Which of EVE's cloud components are on: the volumetric layers, the flat ones, or both.
        private static bool IsVolumetric(string name)
        {
            return name.IndexOf("Raymarch", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("Volum", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // EVE's cloud components on or off, as the shot asks: all of them, none, or one kind of layer. The components are the
        // behaviours of EVE's namespace (Atmosphere) and every renderer drawing with one of its shaders (EVE/...). The ones
        // left on are told apart by the type's name and the shader's. The first call that turns something off logs the names.
        private static bool kindsLogged;

        private static void CloudsOn(bool volumetric, bool flat)
        {
            foreach (Behaviour b in switchedBehaviours) if (b != null) b.enabled = true;
            foreach (Renderer r in switchedRenderers) if (r != null) r.enabled = true;
            switchedBehaviours.Clear();
            switchedRenderers.Clear();
            if (volumetric && flat) return;
            var types = new Dictionary<string, int>();
            foreach (MonoBehaviour b in UnityEngine.Object.FindObjectsOfType<MonoBehaviour>())
                if (b != null && b.enabled && b.GetType().Namespace == "Atmosphere")
                {
                    bool isVolumetric = IsVolumetric(b.GetType().Name);
                    string key = "behaviour " + b.GetType().Name + (isVolumetric ? " (volumetric)" : "");
                    int n;
                    types.TryGetValue(key, out n);
                    types[key] = n + 1;
                    if (isVolumetric ? !volumetric : !flat)
                    {
                        b.enabled = false;
                        switchedBehaviours.Add(b);
                    }
                }
            foreach (Renderer r in UnityEngine.Object.FindObjectsOfType<Renderer>())
            {
                if (r == null || !r.enabled) continue;
                foreach (Material m in r.sharedMaterials)
                    if (m != null && m.shader != null && m.shader.name.StartsWith("EVE/", StringComparison.Ordinal))
                    {
                        bool isVolumetric = IsVolumetric(m.shader.name);
                        string key = "renderer " + m.shader.name + (isVolumetric ? " (volumetric)" : "");
                        int n;
                        types.TryGetValue(key, out n);
                        types[key] = n + 1;
                        if (isVolumetric ? !volumetric : !flat)
                        {
                            r.enabled = false;
                            switchedRenderers.Add(r);
                        }
                        break;
                    }
            }
            if (!kindsLogged)
            {
                kindsLogged = true;
                var text = new System.Text.StringBuilder();
                foreach (KeyValuePair<string, int> pair in types) text.Append(pair.Key).Append(" x").Append(pair.Value).Append("; ");
                Debug.Log(Log.Tag + " Probe: EVE's cloud components: " + text);
            }
        }

        // EVE's cloud components on or off, as the shot asks.
        private static void CloudsVisible(bool visible)
        {
            CloudsOn(visible, visible);
        }

        // Every pixel of the frame, linear, as floats. They come from the camera's own render target where it has one, in half
        // floats, otherwise from the screen, where the dark of a night is only a few 8-bit steps.
        private static IEnumerator ColourShot(Action<Color[], int, int> done)
        {
            yield return new WaitForEndOfFrame();
            Camera camera = FlightCamera.fetch.mainCamera;
            RenderTexture target = camera.targetTexture;
            Color[] pixels;
            int width, height;
            if (target != null)
            {
                RenderTexture active = RenderTexture.active;
                RenderTexture.active = target;
                var read = new Texture2D(target.width, target.height, TextureFormat.RGBAFloat, false);
                read.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0, false);
                RenderTexture.active = active;
                pixels = read.GetPixels();
                width = target.width;
                height = target.height;
                UnityEngine.Object.Destroy(read);
            }
            else
            {
                Texture2D frame = ScreenCapture.CaptureScreenshotAsTexture();
                pixels = frame.GetPixels();
                width = frame.width;
                height = frame.height;
                UnityEngine.Object.Destroy(frame);
            }
            // KSP's colour space is gamma: what the target holds is encoded.
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i].r = Mathf.GammaToLinearSpace(Mathf.Clamp01(pixels[i].r));
                pixels[i].g = Mathf.GammaToLinearSpace(Mathf.Clamp01(pixels[i].g));
                pixels[i].b = Mathf.GammaToLinearSpace(Mathf.Clamp01(pixels[i].b));
            }
            done(pixels, width, height);
        }

        // A frame of linear colours as an 8-bit PNG, in the colour space of the screen.
        private static void SavePng(string path, Color[] pixels, int width, int height)
        {
            if (pixels == null) return;
            var encoded = new Color[pixels.Length];
            for (int i = 0; i < pixels.Length; i++)
                encoded[i] = new Color(Mathf.LinearToGammaSpace(pixels[i].r), Mathf.LinearToGammaSpace(pixels[i].g),
                                       Mathf.LinearToGammaSpace(pixels[i].b), 1f);
            var picture = new Texture2D(width, height, TextureFormat.RGB24, false);
            picture.SetPixels(encoded);
            File.WriteAllBytes(path, picture.EncodeToPNG());
            UnityEngine.Object.Destroy(picture);
        }

        // The largest change of a linear channel between two pixels.
        private static float LargestChange(Color a, Color b)
        {
            return Mathf.Max(Mathf.Abs(a.r - b.r), Mathf.Max(Mathf.Abs(a.g - b.g), Mathf.Abs(a.b - b.b)));
        }

        private static float Luminance(Color c)
        {
            return 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;
        }

        // One measure at the camera's place now: the clouds off, off again, then on. The log gets the share of the middle of the
        // picture the clouds cover, their luminance and the luminance of the ground behind them, their ratio, and how much the
        // noise between the two shots without clouds was. The shot with the clouds on is saved as `name`.
        private static IEnumerator CloudRatio(string folder, string name, string label)
        {
            Color[] off = null, again = null, on = null;
            int width = 0, height = 0;
            CloudsVisible(false);
            yield return ProbeSteps.Frames(RatioFramesAfterSwitch);
            yield return ColourShot((p, w, h) => { off = p; width = w; height = h; });
            SavePng(Path.Combine(folder, name + "-off.png"), off, width, height);
            yield return ProbeSteps.Frames(3);
            yield return ColourShot((p, w, h) => again = p);
            CloudsVisible(true);
            yield return ProbeSteps.Frames(RatioFramesAfterSwitch);
            yield return ColourShot((p, w, h) => on = p);
            SavePng(Path.Combine(folder, name + ".png"), on, width, height);
            if (off == null || on == null || again == null || off.Length != on.Length || off.Length != again.Length)
            {
                Debug.Log(Log.Tag + " Probe: cloud ratio " + label + ": no shots.");
                yield break;
            }
            // The middle of the picture: a quarter of its width and height round the centre, where the camera looks at the place.
            int x0 = width / 4, x1 = width * 3 / 4, y0 = height / 4, y1 = height * 3 / 4;
            var maskPixels = new List<int>();
            double cloudSum = 0.0, behindSum = 0.0, noiseSum = 0.0, groundSum = 0.0;
            var cloudColour = new double[3];
            var behindColour = new double[3];
            int clouds = 0, ground = 0, total = 0;
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                {
                    int i = y * width + x;
                    total++;
                    float noise = LargestChange(off[i], again[i]);
                    float change = LargestChange(on[i], off[i]);
                    noiseSum += noise;
                    if (change > 4f * noise + 0.00005f + 0.05f * Mathf.Max(off[i].r, Mathf.Max(off[i].g, off[i].b)))
                    {
                        cloudSum += Luminance(on[i]);
                        behindSum += Luminance(off[i]);
                        maskPixels.Add(i);
                        cloudColour[0] += on[i].r; cloudColour[1] += on[i].g; cloudColour[2] += on[i].b;
                        behindColour[0] += off[i].r; behindColour[1] += off[i].g; behindColour[2] += off[i].b;
                        clouds++;
                    }
                    else
                    {
                        groundSum += Luminance(off[i]);
                        ground++;
                    }
                }
            double share = clouds / (double)total;
            double cloud = clouds > 0 ? cloudSum / clouds : 0.0, behind = clouds > 0 ? behindSum / clouds : 0.0;
            double elsewhere = ground > 0 ? groundSum / ground : 0.0;
            // The same pixels without the moon's light on the clouds: what the moon adds to them.
            double without = 0.0;
            if (MoonOnClouds && maskPixels.Count > 0)
            {
                EveCloudMoonlight.HeldOff = true;
                EveCloudRelight.HeldOff = true;
                yield return ProbeSteps.Frames(RatioFramesAfterSwitch);
                Color[] held = null;
                yield return ColourShot((p, w, h) => held = p);
                EveCloudMoonlight.HeldOff = false;
                EveCloudRelight.HeldOff = false;
                double heldSum = 0.0;
                foreach (int i in maskPixels) heldSum += Luminance(held[i]);
                without = heldSum / maskPixels.Count;
                yield return ProbeSteps.Frames(RatioFramesAfterSwitch);
            }
            Debug.Log(Log.Tag + " Probe: cloud ratio " + label + ": clouds " + (100.0 * share).ToString("0.0", CultureInfo.InvariantCulture)
                      + " % of the middle, cloud luminance " + cloud.ToString("0.00000", CultureInfo.InvariantCulture)
                      + ", the ground behind them " + behind.ToString("0.00000", CultureInfo.InvariantCulture)
                      + ", ratio " + (behind > 1e-6 ? cloud / behind : 0.0).ToString("0.00", CultureInfo.InvariantCulture)
                      + ", the ground elsewhere " + elsewhere.ToString("0.00000", CultureInfo.InvariantCulture)
                      + ", the same clouds without the moon's light " + without.ToString("0.00000", CultureInfo.InvariantCulture)
                      + ", noise " + (noiseSum / total).ToString("0.00000", CultureInfo.InvariantCulture)
                      + "; colour of the clouds " + Chromaticity(cloudColour) + ", of the ground behind them " + Chromaticity(behindColour)
                      + "; the clouds' light " + EveCloudMoonlight.Describe() + "; " + EveCloudRelight.Describe() + ".");
        }

        // A summed colour as its channels over its luminance: 1.00 1.00 1.00 is grey, more red is warmer, more blue cooler.
        private static string Chromaticity(double[] sum)
        {
            double luminance = 0.2126 * sum[0] + 0.7152 * sum[1] + 0.0722 * sum[2];
            if (luminance <= 1e-9) return "-";
            return (sum[0] / luminance).ToString("0.00", CultureInfo.InvariantCulture) + " " + (sum[1] / luminance).ToString("0.00", CultureInfo.InvariantCulture)
                   + " " + (sum[2] / luminance).ToString("0.00", CultureInfo.InvariantCulture);
        }

        // At one light, four views that show something: from the ground to the densest clouds and to the sun or the moon, and from
        // 150 km, slanted towards the light and steeply down on the densest clouds. The raytracing is on, each a shot of its own.
        private static IEnumerator TransitionShots(ReDefinitionAddon addon, string folder, string time)
        {
            CelestialBody sun = RtKspMods.StarBody();
            CelestialBody moon = BrightestMoon();
            Vessel vessel = FlightGlobals.ActiveVessel;
            Set(addon, RaytracingMode.Light, addon.Live.RaytracingMoonlight, addon.Live.RaytracingNightAmbient);
            yield return ProbeSteps.Frames(RatioFramesAfterMode);
            Vector3 densest;
            float cover = CloudCover(out densest);
            // The sun while it is near the horizon and lights the clouds, the moon after.
            CelestialBody light = Elevation(sun) > -15f || moon == null ? sun : moon;
            Debug.Log(Log.Tag + " Probe: cloud shots " + time + ": the moon's phase angle "
                      + (moon != null ? PhaseAngle(moon, sun).ToString("0", CultureInfo.InvariantCulture) : "-") + ", cloud cover " + cover.ToString("0.00", CultureInfo.InvariantCulture) + ", towards the "
                      + (light == sun ? "sun" : light.bodyName) + " at " + Elevation(light).ToString("0.0", CultureInfo.InvariantCulture) + " degrees.");
            Vector3 up = ((Vector3)(((Vector3d)vessel.transform.position) - vessel.mainBody.position)).normalized;
            Vector3 toLight = Vector3.ProjectOnPlane((Vector3)(light.position - (Vector3d)vessel.transform.position), up).normalized;
            Vector3 towardsClouds = densest.sqrMagnitude > 0.5f ? densest : toLight;
            var views = new[]
            {
                new { Name = "ground-clouds", Direction = towardsClouds, Pitch = -10f, Distance = 60f },
                new { Name = "ground-light", Direction = toLight, Pitch = -4f, Distance = 60f },
                new { Name = "zoomed-slanted", Direction = toLight, Pitch = 30f, Distance = 150000f },
                new { Name = "zoomed-down", Direction = towardsClouds, Pitch = 60f, Distance = 150000f },
            };
            foreach (var view in views)
            {
                if (view.Direction.sqrMagnitude < 0.5f) continue;
                yield return LookAlong(view.Direction, view.Pitch, view.Distance, view.Name);
                yield return ProbeSteps.Frames(RatioFramesAfterMode);
                yield return Shot(folder, "transition-" + time + "-" + view.Name);
            }
        }

        // The frames' GPU time (the proxy's timestamps, with -redefinitionGpuProfile) and wall time over a run of frames at this view.
        private static IEnumerator FrameCosts(string label)
        {
            yield return ProbeSteps.Frames(60);
            double gpu = 0.0, wall = 0.0;
            const int Frames = 120;
            for (int i = 0; i < Frames; i++)
            {
                yield return null;
                wall += Time.unscaledDeltaTime;
                gpu += GpuProfiler.LastFrameMs;
            }
            Debug.Log(Log.Tag + " Probe: cloud cost " + label + ": GPU " + (gpu / Frames).ToString("0.00", CultureInfo.InvariantCulture)
                      + " ms, wall " + (1000.0 * wall / Frames).ToString("0.00", CultureInfo.InvariantCulture) + " ms a frame.");
        }

        // How far below the horizon the sun must be for a night. It then reaches no layer, the highest ones included. At 27 degrees
        // a layer above 70 km on Kerbin is still in its light, which a measure of the moon's light on the clouds took for the moon's.
        private const float NightDepth = 45f;

        // What the moon's light on the clouds costs. At night with the moon up it times the frame, with the raytracing on and off,
        // from the ground towards the clouds and from 14 and 150 km. It times three ways: as it is, without the light per pixel,
        // and without the moon's light on the clouds.
        private static IEnumerator CloudPerf(ReDefinitionAddon addon, string folder)
        {
            CelestialBody sun = RtKspMods.StarBody();
            CelestialBody moon = BrightestMoon();
            if (sun == null || moon == null) yield break;
            double day = Math.Max(43200.0, 1.2 * FlightGlobals.currentMainBody.solarDayLength);
            float moonlight = addon.Live.RaytracingMoonlight, nightAmbient = addon.Live.RaytracingNightAmbient;
            Debug.Log(Log.Tag + " Probe: cloud cost, the GPU profiler " + (GpuProfiler.On ? "on" : "OFF") + ".");
            bool night = false;
            yield return WarpUntilCloudy("the sun down, " + moon.bodyName + " high and lit",
                                         () => Elevation(sun) < -NightDepth && Elevation(moon) > 30f && PhaseAngle(moon, sun) < 100f,
                                         result => night = result, 4.0 * Math.Max(moon.orbit.period, day));
            if (!night) yield break;
            Vessel vessel = FlightGlobals.ActiveVessel;
            Vector3 up = ((Vector3)(((Vector3d)vessel.transform.position) - vessel.mainBody.position)).normalized;
            Vector3 flat = Vector3.ProjectOnPlane(Vector3.forward, up).normalized;
            Vector3 densest;
            CloudCover(out densest);
            Vector3 towardsClouds = densest.sqrMagnitude > 0.5f ? densest : flat;
            var views = new[]
            {
                new { Name = "ground, towards the clouds", Direction = towardsClouds, Pitch = -10f, Distance = 60f },
                new { Name = "14 km", Direction = flat, Pitch = RatioPitch, Distance = 14000f },
                new { Name = "150 km", Direction = flat, Pitch = RatioPitch, Distance = 150000f },
            };
            foreach (RaytracingMode mode in new[] { RaytracingMode.Light, RaytracingMode.Off })
            {
                Set(addon, mode, moonlight, nightAmbient);
                yield return ProbeSteps.Frames(RatioFramesAfterMode);
                foreach (var view in views)
                {
                    yield return LookAlong(view.Direction, view.Pitch, view.Distance, view.Name);
                    string where = view.Name + ", raytracing " + mode.ToString().ToLowerInvariant();
                    EveCloudMoonlight.HeldOff = false;
                    EveCloudRelight.HeldOff = false;
                    yield return FrameCosts(where + ", the moon's light on");
                    EveCloudRelight.HeldOff = true;
                    yield return FrameCosts(where + ", without the light per pixel");
                    EveCloudRelight.HeldOff = false;
                    EveCloudMoonlight.HeldOff = true;
                    yield return FrameCosts(where + ", without the moon's light on the clouds");
                    EveCloudMoonlight.HeldOff = false;
                }
            }
            Debug.Log(Log.Tag + " Probe: cloud cost done.");
        }

        // The four heights at one light, with the raytracing on and off, the clouds measured at each. `time` names the light.
        private static IEnumerator CloudRatiosAt(ReDefinitionAddon addon, string folder, string time, float moonlight, float nightAmbient)
        {
            CelestialBody sun = RtKspMods.StarBody();
            CelestialBody body = FlightGlobals.currentMainBody;
            Debug.Log(Log.Tag + " Probe: cloud ratios " + time + " over " + body.bodyName + ": the sun at "
                      + Elevation(sun).ToString("0.0", CultureInfo.InvariantCulture) + " degrees.");
            // The raytracing off as well at the night and by day: the clouds must be the same without it.
            foreach (RaytracingMode mode in time == "night" || time == "day" ? new[] { RaytracingMode.Light, RaytracingMode.Off }
                                                                            : new[] { RaytracingMode.Light })
            {
                Set(addon, mode, moonlight, nightAmbient);
                yield return ProbeSteps.Frames(RatioFramesAfterMode);
                Vessel vessel = FlightGlobals.ActiveVessel;
                Vector3 up = ((Vector3)(((Vector3d)vessel.transform.position) - vessel.mainBody.position)).normalized;
                Vector3 flat = Vector3.ProjectOnPlane(Vector3.forward, up);
                if (flat.sqrMagnitude < 0.01f) flat = Vector3.ProjectOnPlane(Vector3.right, up);
                flat.Normalize();
                foreach (float height in RatioHeights)
                {
                    yield return LookAlong(flat, RatioPitch, height, "the place from " + (height / 1000f).ToString("0") + " km");
                    yield return CloudRatio(folder, "ratio-" + time + "-" + mode.ToString().ToLowerInvariant() + "-" + ((int)(height / 1000f)) + "km",
                                            time + ", raytracing " + mode.ToString().ToLowerInvariant() + ", from " + (height / 1000f).ToString("0") + " km");
                    // The Night clouds slider at three more settings, from the lowest height, with the raytracing on.
                    if (mode == RaytracingMode.Light && time == "night" && height == RatioHeights[0])
                    {
                        OwnSettings original = addon.Live.Clone();
                        foreach (float percent in new[] { 0f, 30f, 70f, 100f })
                        {
                            OwnSettings changed = original.Clone();
                            changed.NightClouds = percent;
                            addon.Apply(addon.Live.Clone(), changed);
                            yield return ProbeSteps.Frames(RatioFramesAfterMode);
                            yield return CloudRatio(folder, "ratio-night-slider" + (int)percent, "night, Night clouds " + percent.ToString("0") + " %, from 14 km");
                        }
                        addon.Apply(addon.Live.Clone(), original);
                        yield return ProbeSteps.Frames(RatioFramesAfterMode);
                    }
                    // The light per pixel alone and where it finds each pixel's cloud: whether it reaches the clouds from this height.
                    if (mode == RaytracingMode.Light && time == "night")
                        foreach (float debug in new[] { 1f, 2f })
                        {
                            EveCloudRelight.DebugMode = debug;
                            yield return ProbeSteps.Frames(40);
                            ScreenCapture.CaptureScreenshot(Path.Combine(folder, "debug" + (int)debug + "-" + ((int)(height / 1000f)) + "km.png"));
                            yield return ProbeSteps.Frames(2);
                            EveCloudRelight.DebugMode = 0f;
                        }
                }
            }
        }

        // By day, in the dusk at 9, 13, 17 and 22 degrees under the horizon, and at night, each with clouds in the sky and the
        // moon where it lights them. Each is measured at the four heights, by day and night with the raytracing on and off.
        private static IEnumerator CloudRatios(ReDefinitionAddon addon, string folder)
        {
            CelestialBody sun = RtKspMods.StarBody();
            CelestialBody moon = BrightestMoon();
            if (sun == null) yield break;
            float moonlight = addon.Live.RaytracingMoonlight, nightAmbient = addon.Live.RaytracingNightAmbient;
            double day = Math.Max(43200.0, 1.2 * FlightGlobals.currentMainBody.solarDayLength);
            Debug.Log(Log.Tag + " Probe: cloud ratios, into " + folder + ", over " + FlightGlobals.currentMainBody.bodyName + "; the moon "
                      + (moon != null ? moon.bodyName : "none") + ".");
            Set(addon, RaytracingMode.Light, moonlight, nightAmbient);
            yield return ProbeSteps.Frames(30);
            Debug.Log(Log.Tag + " Probe: cloud layers over " + FlightGlobals.currentMainBody.bodyName + ": "
                      + EveCloudMoonlight.DescribeLayers(FlightGlobals.currentMainBody.bodyName) + ".");

            if (CommandLine.Has("-redefinitionProbeRatioPerf"))
            {
                yield return CloudPerf(addon, folder);
                yield break;
            }

            if (CommandLine.Has("-redefinitionProbeRatioSides"))
            {
                yield return CloudSides(addon, folder);
                yield break;
            }
            if (CommandLine.Has("-redefinitionProbeRatioAurora"))
            {
                yield return AuroraView(addon, folder);
                yield break;
            }

            // -redefinitionProbeRatioNight: only the nights, for a look at the night alone.
            bool nightOnly = CommandLine.Has("-redefinitionProbeRatioNight");
            // -redefinitionProbeRatioShots: the views of the transition only, without the measures.
            bool shotsOnly = CommandLine.Has("-redefinitionProbeRatioShots");
            bool found = false;
            if (!nightOnly)
            {
                yield return WarpUntilCloudy("the sun high", () => Elevation(sun) > 40f, result => found = result, 3.0 * day);
                if (found) yield return shotsOnly ? TransitionShots(addon, folder, "day") : CloudRatiosAt(addon, folder, "day", moonlight, nightAmbient);
            }
            if (moon == null) yield break;

            // The dusk with the moon up and more than a crescent of it lit, then at each depth.
            bool dusk = false;
            Func<bool> setting = SunMoving(sun, false, -2f, 3f);
            if (!nightOnly)
                yield return WarpUntilCloudy("the sun setting at -2 to 3 degrees, " + moon.bodyName + " up",
                                             () => setting() && Elevation(moon) > 20f && PhaseAngle(moon, sun) < 100f,
                                             result => dusk = result, 4.0 * Math.Max(moon.orbit.period, day));
            if (dusk)
                foreach (float depth in new[] { 9f, 13f, 17f, 22f, 30f, 40f, 60f })
                {
                    bool deep = false;
                    yield return SlowWarpUntil("the sun " + depth + " degrees down", () => Elevation(sun) < -depth, result => deep = result);
                    string name = "dusk" + depth.ToString("00", CultureInfo.InvariantCulture);
                    if (deep) yield return shotsOnly ? TransitionShots(addon, folder, name) : CloudRatiosAt(addon, folder, name, moonlight, nightAmbient);
                }

            // -redefinitionProbeRatioDirections: one night, one cloud field, the moon turned by hand: its light on the clouds with
            // the moon at several heights, in front of the camera and behind it, from 14 km.
            if (CommandLine.Has("-redefinitionProbeRatioDirections") && moon != null)
            {
                bool there = false;
                yield return WarpUntilCloudy("the sun down, " + moon.bodyName + " high and lit",
                                             () => Elevation(sun) < -NightDepth && Elevation(moon) > 30f && PhaseAngle(moon, sun) < 100f,
                                             result => there = result, 4.0 * Math.Max(moon.orbit.period, day));
                if (!there) yield break;
                Vessel pod = FlightGlobals.ActiveVessel;
                Vector3 up = ((Vector3)(((Vector3d)pod.transform.position) - pod.mainBody.position)).normalized;
                Vector3 flat = Vector3.ProjectOnPlane(Vector3.forward, up).normalized;
                yield return LookAlong(flat, RatioPitch, RatioHeights[0], "the place");
                foreach (float sign in new[] { 1f, -1f })
                    foreach (float elevation in new[] { 20f, 40f, 60f, 75f, 85f })
                    {
                        float radians = elevation * Mathf.Deg2Rad;
                        EveCloudMoonlight.ProbeMoonDirection = (up * Mathf.Sin(radians) + flat * (sign * Mathf.Cos(radians))).normalized;
                        yield return ProbeSteps.Frames(RatioFramesAfterMode);
                        yield return CloudRatio(folder, "direction-" + (sign > 0f ? "front" : "behind") + "-" + (int)elevation,
                                                "the moon turned to " + elevation.ToString("0") + " degrees " + (sign > 0f ? "in front of" : "behind") + " the camera");
                    }
                EveCloudMoonlight.ProbeMoonDirection = Vector3.zero;
                yield break;
            }

            // -redefinitionProbeRatioElevations: the moon's light on the clouds at several heights of the moon in the sky, from 14 km,
            // to see whether it grows with them.
            if (CommandLine.Has("-redefinitionProbeRatioElevations") && moon != null)
            {
                foreach (float wanted in new[] { 20f, 40f, 60f, 75f, 85f })
                {
                    bool reached = false;
                    yield return WarpUntilCloudy("the sun down, " + moon.bodyName + " at " + wanted + " degrees",
                                                 () => Elevation(sun) < -NightDepth && Mathf.Abs(Elevation(moon) - wanted) < 3f && PhaseAngle(moon, sun) < 120f,
                                                 result => reached = result, 4.0 * Math.Max(moon.orbit.period, day));
                    if (!reached) continue;
                    yield return ProbeSteps.Frames(10);
                    Debug.Log(Log.Tag + " Probe: cloud elevation " + wanted + ": the moons' light for the clouds: " + RtReflectors.DescribeCloudLight());
                    Debug.Log(Log.Tag + " Probe: cloud elevation " + wanted + ": the moon at " + Elevation(moon).ToString("0.0", CultureInfo.InvariantCulture)
                              + " degrees, phase angle " + PhaseAngle(moon, sun).ToString("0", CultureInfo.InvariantCulture) + ".");
                    Vessel vessel = FlightGlobals.ActiveVessel;
                    Vector3 up = ((Vector3)(((Vector3d)vessel.transform.position) - vessel.mainBody.position)).normalized;
                    Vector3 flat = Vector3.ProjectOnPlane(Vector3.forward, up).normalized;
                    yield return LookAlong(flat, RatioPitch, RatioHeights[0], "the place");
                    yield return CloudRatio(folder, "elevation-" + (int)wanted, "the moon at " + wanted.ToString("0") + " degrees, from 14 km");
                    yield return LookAlong(flat, RatioPitch, RatioHeights[3], "the place");
                    yield return CloudRatio(folder, "elevation-" + (int)wanted + "-150km", "the moon at " + wanted.ToString("0") + " degrees, from 150 km");
                }
                yield break;
            }

            // The night: the sun deep enough down that it lights no cloud, the moon high and more than half lit. Another planet's
            // moon may be small or low in the sky for most of the night, so it counts from lower up.
            float highMoon = FlightGlobals.currentMainBody.bodyName == "Kerbin" ? 30f : 12f;
            bool night = false;
            yield return WarpUntilCloudy("the sun down, " + moon.bodyName + " high and lit",
                                         () => Elevation(sun) < -NightDepth && Elevation(moon) > highMoon && PhaseAngle(moon, sun) < 100f,
                                         result => night = result, 4.0 * Math.Max(moon.orbit.period, day));
            if (night) yield return shotsOnly ? TransitionShots(addon, folder, "night") : CloudRatiosAt(addon, folder, "night", moonlight, nightAmbient);

            // The moon low, as in a night with it rising: its light reddened by the air it crosses.
            bool low = false;
            yield return WarpUntilCloudy("the sun down, " + moon.bodyName + " low and lit",
                                         () => Elevation(sun) < -NightDepth && Elevation(moon) > 8f && Elevation(moon) < 25f && PhaseAngle(moon, sun) < 110f,
                                         result => low = result, 4.0 * Math.Max(moon.orbit.period, day));
            if (low) yield return shotsOnly ? TransitionShots(addon, folder, "night-low") : CloudRatiosAt(addon, folder, "night-low", moonlight, nightAmbient);
            Debug.Log(Log.Tag + " Probe: cloud ratios done.");
        }

        // The moon of the body the camera is at that lights it most: the largest in the sky, by size over distance squared.
        private static CelestialBody BrightestMoon()
        {
            CelestialBody main = FlightGlobals.currentMainBody;
            CelestialBody star = RtKspMods.StarBody();
            var bodies = new List<CelestialBody>();
            if (main == null || star == null || !RtReflectors.SystemOf(main, star, bodies)) return null;
            CelestialBody best = null;
            double bestSize = 0.0;
            foreach (CelestialBody body in bodies)
            {
                if (body == null || body == main || body == star) continue;
                double distance = (body.position - main.position).magnitude;
                double size = body.Radius * body.Radius / (distance * distance);
                if (size > bestSize)
                {
                    bestSize = size;
                    best = body;
                }
            }
            return best;
        }
    }
}
