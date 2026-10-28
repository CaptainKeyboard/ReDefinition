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
    // The light probe's cloud shots (-redefinitionProbeCloudLight): EVE's clouds by the light of the sun and the Mun, the
    // cloud cover and the warps that look for a time with clouds in the sky.
    internal static partial class LightProbe
    {
        // The light's times over the pad for the clouds: the sun rising and setting at 3 degrees (the clouds
        // take its light alone), dusk with the Mun up, and a night with the Mun high. At dusk the sun is 9, 13
        // and 17 degrees under the horizon, with the sunset on the clouds still in it and the Mun's light added
        // per pixel on the others (EveCloudMoonlight, EveCloudRelight). The night has the per-pixel light's
        // calibration (CloudCalibration).
        // Each time has clouds in the sky where they come (WarpUntilCloudy). CloudShotsAt takes the shots.
        private static IEnumerator CloudShots(ReDefinitionAddon addon, string folder)
        {
            CelestialBody sun = RtKspMods.StarBody();
            CelestialBody mun = FlightGlobals.Bodies.Find(b => b.bodyName == SceneProbe.MoonName);
            if (sun == null) yield break;
            RaytracingMode on = addon.Live.Raytracing == RaytracingMode.Off ? RaytracingMode.Full : addon.Live.Raytracing;
            float moonlight = addon.Live.RaytracingMoonlight, nightAmbient = addon.Live.RaytracingNightAmbient;
            double day = Math.Max(43200.0, 1.2 * FlightGlobals.currentMainBody.solarDayLength);
            Debug.Log(Log.Tag + " Probe: light: clouds, into " + folder + ".");
            // The raytracing on from the start: the cloud cover is read from its layer (RtEveClouds).
            Set(addon, on, moonlight, nightAmbient);
            yield return ProbeSteps.Frames(30);
            // Each time looked for with clouds in the sky as well (Cloudy), and without them where
            // none come within the time allowed.
            foreach (bool rising in new[] { true, false })
            {
                bool reached = false;
                Func<bool> moving = SunMoving(sun, rising, 1f, 6f);
                yield return WarpUntilCloudy("the sun " + (rising ? "rising" : "setting") + " at 1 to 6 degrees", moving,
                                             result => reached = result, 3.0 * day);
                if (reached) yield return CloudShotsAt(addon, folder, rising ? "morning03" : "evening03", sun, on, moonlight, nightAmbient);
            }
            if (mun == null) yield break;

            // A night with the moon high and more than half of it lit, the sun deep enough down
            // that no cloud in view sees it (CloudLightBlend.FarthestDip); first, so that the dusk's
            // shots have the calibration's gains.
            bool night = false;
            yield return WarpUntilCloudy("the sun down, " + mun.bodyName + " high and full",
                                         () => Elevation(sun) < -25f && Elevation(mun) > 30f && PhaseAngle(mun, sun) < 60f,
                                         result => night = result, 4.0 * mun.orbit.period);
            if (night)
            {
                yield return RaysCloudCheck(folder, mun);
                yield return CloudMatrix(folder, sun, mun);
                yield return CloudCalibration(folder, mun);
                yield return CloudShotsAt(addon, folder, "night", mun, on, moonlight, nightAmbient);
            }

            // Dusk with the Mun up and more than a crescent of it lit: found near the horizon, where the
            // window is wide enough for the rails warp, then slowly down to each depth; at 22 degrees
            // the clouds' one light turns to the Mun (CloudLightBlend.Handoff).
            bool dusk = false;
            Func<bool> setting = SunMoving(sun, false, -2f, 3f);
            yield return WarpUntilCloudy("the sun setting at -2 to 3 degrees, " + mun.bodyName + " up",
                                         () => setting() && Elevation(mun) > 20f && PhaseAngle(mun, sun) < 100f,
                                         result => dusk = result, 3.0 * mun.orbit.period);
            if (dusk)
                foreach (float depth in new[] { 9f, 13f, 17f, 22f })
                {
                    bool deep = false;
                    yield return SlowWarpUntil("the sun " + depth + " degrees down", () => Elevation(sun) < -depth,
                                               result => deep = result);
                    if (deep)
                        yield return CloudShotsAt(addon, folder, "dusk" + depth.ToString("00", CultureInfo.InvariantCulture), mun, on,
                                                  moonlight, nightAmbient);
                }
            Debug.Log(Log.Tag + " Probe: light: clouds done.");
        }

        // At night, the moon's light added per pixel (EveCloudRelight) against the light the
        // raymarching itself gives the clouds in the moon's light, on the same clouds.
        // First the clouds' one light is the sun's (deep down, it lights none of them), with the
        // moon's direct light per pixel alone and the sky's light it gives alone read.
        // Then the one light is the moon's, EVE's buffer read again. The two gains then make the
        // per-pixel light as bright and of the same colour as the raymarching's, so that the light
        // handed over after the dusk does not jump.
        private static IEnumerator CloudCalibration(string folder, CelestialBody mun)
        {
            Camera camera = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null;
            if (camera == null) yield break;
            Vector3 densest;
            CloudCover(out densest);
            if (densest.sqrMagnitude > 0.5f) yield return LookAlong(densest, -10f, 60f, "the densest clouds");
            else yield return LookTowards(mun, -6f, 60f);
            float directBefore = EveCloudRelight.DirectGain, skyBefore = EveCloudRelight.SkyGain;
            EveCloudRelight.ProbeAlways = true;
            EveCloudMoonlight.ProbeHandoff = 0f;
            EveCloudMoonlight.ProbeShare = 1f;
            // The moon's direct light alone, the sky's light it gives alone, and EVE's clouds in
            // the sun's.
            Color[] direct = new Color[3], sky = new Color[3], raymarched = new Color[3];
            EveCloudRelight.DebugMode = 2f;
            yield return MeanClouds(camera, direct);
            EveCloudRelight.DebugMode = 3f;
            yield return MeanClouds(camera, sky);
            // The parts of the moon's direct light, each a mean over the clouds (times the
            // opacity, over its mean): the transmittance towards the moon, the table's part of
            // it, the planet's and the cloud's own shadow.
            Color[] part = new Color[3];
            yield return MeanCloudsIn(camera, 7f, part);
            Color opacity = part[1];
            string[] parts = { "the transmittance", "the table", "the shadows" };
            string partsLog = "";
            for (int i = 0; i < parts.Length; i++)
            {
                yield return MeanCloudsIn(camera, 4f + i, part);
                partsLog += (i > 0 ? ", " : "") + parts[i] + " " + Rgb(opacity.g > 0f ? part[1] / opacity.g : Color.black);
            }
            Debug.Log(Log.Tag + " Probe: light: clouds' parts over the clouds (opacity " + opacity.g.ToString("0.0000", CultureInfo.InvariantCulture)
                      + "): " + partsLog + "; the moon's light " + Rgb(EveCloudRelight.LastMoonLight)
                      + ", the clouds' colour " + Rgb(EveCloudRelight.LastCloudColour) + ", the sky's scale "
                      + EveCloudRelight.LastSkyScale.ToString("0.000", CultureInfo.InvariantCulture) + ".");
            EveCloudRelight.DebugMode = 0f;
            EveCloudMoonlight.ProbeHandoff = 1f;
            EveCloudMoonlight.ProbeShare = 0f;
            yield return MeanClouds(camera, raymarched);
            // The raymarching's moonlight over what the sun leaves, as direct times a and sky
            // times b, least squares over red, green and blue; neither below 0.
            Color d = direct[1], s = sky[1], t = raymarched[0] - direct[0];
            double dd = d.r * d.r + d.g * d.g + d.b * d.b, ss = s.r * s.r + s.g * s.g + s.b * s.b, ds = d.r * s.r + d.g * s.g + d.b * s.b;
            double dt = d.r * t.r + d.g * t.g + d.b * t.b, st = s.r * t.r + s.g * t.g + s.b * t.b;
            double det = dd * ss - ds * ds;
            double a = det > 1e-20 ? (dt * ss - st * ds) / det : 0.0, b = det > 1e-20 ? (st * dd - dt * ds) / det : 0.0;
            // Where the sky's light is off (its gain 0) or takes a weight below 0, the direct
            // light alone; where that does, the sky's alone.
            if (det <= 1e-20 || b < 0.0)
            {
                b = 0.0;
                a = dd > 1e-20 ? dt / dd : 0.0;
            }
            if (a < 0.0)
            {
                a = 0.0;
                b = ss > 1e-20 ? st / ss : 0.0;
            }
            bool read = direct[2].r > 0f && sky[2].r > 0f && raymarched[2].r > 0f && (a > 0.0 || b > 0.0);
            if (read)
            {
                EveCloudRelight.DirectGain = (float)(directBefore * a);
                EveCloudRelight.SkyGain = (float)(skyBefore * b);
            }
            Color fitted = d * (float)a + s * (float)b;
            Debug.Log(Log.Tag + " Probe: light: clouds calibration" + (read ? "" : " (not read)") + ": EVE's clouds in the sun's light "
                      + Rgb(direct[0]) + "; the moon's direct light per pixel " + Rgb(d) + " at gain "
                      + directBefore.ToString("0.000", CultureInfo.InvariantCulture) + ", its sky's " + Rgb(s) + " at gain "
                      + skyBefore.ToString("0.000", CultureInfo.InvariantCulture) + "; the raymarching's moonlight " + Rgb(t)
                      + ", fitted " + Rgb(fitted) + ": gains now " + EveCloudRelight.DirectGain.ToString("0.000", CultureInfo.InvariantCulture)
                      + " and " + EveCloudRelight.SkyGain.ToString("0.000", CultureInfo.InvariantCulture) + ".");

            // The views of the per-pixel light: where each pixel's cloud point lies, the moon's
            // direct light alone, its sky's, the clouds with both, and the raymarching's own
            // moonlight to compare.
            EveCloudMoonlight.ProbeHandoff = 0f;
            EveCloudMoonlight.ProbeShare = 1f;
            EveCloudRelight.DebugMode = 1f;
            yield return Shot(folder, "clouds-night-perpixel-points");
            EveCloudRelight.DebugMode = 2f;
            yield return Shot(folder, "clouds-night-perpixel-moon");
            EveCloudRelight.DebugMode = 3f;
            yield return Shot(folder, "clouds-night-perpixel-sky");
            EveCloudRelight.DebugMode = 0f;
            yield return Shot(folder, "clouds-night-perpixel");
            EveCloudMoonlight.ProbeHandoff = 1f;
            EveCloudMoonlight.ProbeShare = 0f;
            yield return ProbeSteps.Frames(120);
            yield return Shot(folder, "clouds-night-raymarched");
            EveCloudRelight.ProbeAlways = false;
            EveCloudMoonlight.ProbeHandoff = -1f;
            EveCloudMoonlight.ProbeShare = -1f;
        }

        // The mean light over the frame of EVE's clouds and of the copy with the moon's light
        // per pixel, over eight frames once EVE's light volume and history have settled; the
        // frames read in [2].r.
        private static IEnumerator MeanClouds(Camera camera, Color[] result)
        {
            yield return ProbeSteps.Frames(120);
            Color eve = Color.black, relit = Color.black;
            int frames = 0;
            for (int i = 0; i < 8; i++)
            {
                yield return new WaitForEndOfFrame();
                Color e, r;
                if (EveCloudRelight.MeanColour(camera, out e, out r))
                {
                    eve += e;
                    relit += r;
                    frames++;
                }
                yield return null;
            }
            result[0] = frames > 0 ? eve / frames : Color.black;
            result[1] = frames > 0 ? relit / frames : Color.black;
            result[2] = new Color(frames, 0f, 0f, 0f);
        }

        // MeanClouds with EveCloudRelight's debug mode set to mode first.
        private static IEnumerator MeanCloudsIn(Camera camera, float mode, Color[] result)
        {
            EveCloudRelight.DebugMode = mode;
            yield return MeanClouds(camera, result);
        }

        // A colour's red, green and blue for the log, six decimals, in brackets.
        private static string Rgb(Color c)
        {
            return "(" + c.r.ToString("0.000000", CultureInfo.InvariantCulture) + ", " + c.g.ToString("0.000000", CultureInfo.InvariantCulture)
                   + ", " + c.b.ToString("0.000000", CultureInfo.InvariantCulture) + ")";
        }

        // The cloud cover (CloudCover) from which the sky counts as cloudy.
        private const float CloudyEnough = 0.15f;

        // How much cloud there is to see from the vessel. It is EVE's density along rays 8 degrees up
        // in 16 directions, through the layer's shell, the densest direction's mean. The
        // direction goes into `towards` (flat, world). It is -1 where EVE's layer is not in the rays.
        private static float CloudCover(out Vector3 towards)
        {
            towards = Vector3.zero;
            Vessel vessel = FlightGlobals.ActiveVessel;
            RtEveClouds clouds = RtEveClouds.Last;
            float inner, outer;
            if (vessel == null || clouds == null || !clouds.Shell(out inner, out outer)) return -1f;
            Vector3d centre = vessel.mainBody.position;
            Vector3 up = ((Vector3)(vessel.CoMD - centre)).normalized;
            Vector3 north = Vector3.ProjectOnPlane(vessel.mainBody.transform.up, up).normalized;
            Vector3 east = Vector3.Cross(up, north);
            float best = -1f;
            for (int a = 0; a < 16; a++)
            {
                float angle = a * Mathf.PI / 8f;
                Vector3 flat = north * Mathf.Cos(angle) + east * Mathf.Sin(angle);
                Vector3 ray = (flat * Mathf.Cos(8f * Mathf.Deg2Rad) + up * Mathf.Sin(8f * Mathf.Deg2Rad)).normalized;
                float sum = 0f;
                int samples = 0;
                // Every 2 km out to 120 km; the points inside the shell count.
                for (float t = 1000f; t <= 120000f; t += 2000f)
                {
                    Vector3d at = vessel.CoMD + (Vector3d)(ray * t);
                    double height = (at - centre).magnitude;
                    if (height < inner || height > outer) continue;
                    float density = clouds.DensityAt((Vector3)at);
                    if (density < 0f) return -1f;
                    sum += density;
                    samples++;
                }
                float mean = samples > 0 ? sum / samples : 0f;
                if (mean > best)
                {
                    best = mean;
                    towards = flat;
                }
            }
            return best;
        }

        // Whether the cloud cover reaches CloudyEnough.
        private static bool Cloudy()
        {
            Vector3 towards;
            return CloudCover(out towards) >= CloudyEnough;
        }

        // WarpUntil with clouds in the sky as well, checked only where the rest holds (the
        // densities cost a few hundred calls); and without them where none come within the
        // first two thirds of the time.
        private static IEnumerator WarpUntilCloudy(string what, Func<bool> wanted, Action<bool> done, double longest)
        {
            bool found = false;
            yield return WarpUntil(what + ", with clouds", () => wanted() && Cloudy(), result => found = result, longest * 2.0 / 3.0);
            if (!found) yield return WarpUntil(what + ", the clouds as they are", wanted, result => found = result, longest / 3.0);
            Vector3 towards;
            Debug.Log(Log.Tag + " Probe: light: cloud cover " + CloudCover(out towards).ToString("0.00", CultureInfo.InvariantCulture) + ".");
            done(found);
        }

        // Whether the sun stands between `lowest` and `highest` degrees, rising or setting as asked.
        private static Func<bool> SunMoving(CelestialBody sun, bool rising, float lowest, float highest)
        {
            float last = Elevation(sun);
            return () =>
            {
                float now = Elevation(sun);
                // Unmoved since the last look (the first, or a frame without time warp), it is neither.
                bool moved = now != last, up = now > last;
                last = now;
                return moved && up == rising && now > lowest && now < highest;
            };
        }

        // Time warp at 50 times on rails until `wanted` holds, a minute at most: the sun moves a
        // degree in some seconds then, so a narrow window is not stepped over.
        private static IEnumerator SlowWarpUntil(string what, Func<bool> wanted, Action<bool> done)
        {
            bool found = wanted();
            float until = Time.realtimeSinceStartup + 60f;
            double limit = Planetarium.GetUniversalTime() + 6.0 * 3600.0;
            bool warping = false;
            while (!found && Time.realtimeSinceStartup < until)
            {
                if (JumpTime && Planetarium.GetUniversalTime() < limit) yield return JumpOn(SlowJumpStep, took => { });
                else
                {
                    if (!warping)
                    {
                        warping = true;
                        TimeWarp.fetch.Mode = TimeWarp.Modes.HIGH;
                        TimeWarp.SetRate(3, true);
                    }
                    yield return null;
                }
                found = wanted();
            }
            TimeWarp.SetRate(0, true);
            yield return new WaitForSeconds(3f);
            Debug.Log(Log.Tag + " Probe: light: slow warp; " + what + (found ? "." : " not reached."));
            done(found);
        }

        // The ground under clouds at night with the clouds in the rays and without. Each is shot twice from 60 m, 25 degrees
        // down, so that the difference of the pairs shows the denoiser's noise.
        private static IEnumerator RaysCloudCheck(string folder, CelestialBody mun)
        {
            Debug.Log(Log.Tag + " Probe: light: the clouds in the rays, the Mun at " + Elevation(mun).ToString("0.0", CultureInfo.InvariantCulture)
                      + " degrees; " + EveCloudMoonlight.LayerSunBrightness() + ".");
            yield return LookTowards(mun, 25f, 60f);
            foreach (string pass in new[] { "with", "without", "with-again", "without-again" })
            {
                RtKsp.NoCloudsInRays = pass.StartsWith("without", StringComparison.Ordinal);
                yield return ProbeSteps.Frames(150);
                yield return Shot(folder, "rays-clouds-" + pass);
            }
            RtKsp.NoCloudsInRays = false;
        }

        // EVE's 2D clouds from orbit, the pod at 600 km (-redefinitionProbeOrbit), by the Mun's light at two phases and
        // heights. The sun is well under the pod's horizon, the Mun high and mostly lit, then low and a crescent.
        // Each time looks towards the Mun and away from it, 60 degrees down, the clouds' moonlight held off ("before")
        // and on ("after"). The difference of the two is the light the 2D layers take from the Mun's slot (EveCloudMoonlight).
        private static IEnumerator CloudsFromOrbit(ReDefinitionAddon addon, string folder)
        {
            CelestialBody sun = RtKspMods.StarBody();
            CelestialBody mun = FlightGlobals.Bodies.Find(b => b.bodyName == SceneProbe.MoonName);
            if (sun == null || mun == null) yield break;
            RaytracingMode on = addon.Live.Raytracing == RaytracingMode.Off ? RaytracingMode.Full : addon.Live.Raytracing;
            float moonlight = addon.Live.RaytracingMoonlight, nightAmbient = addon.Live.RaytracingNightAmbient;
            Debug.Log(Log.Tag + " Probe: light: 2D clouds from orbit, into " + folder + ".");
            Set(addon, on, moonlight, nightAmbient);
            yield return ProbeSteps.Frames(30);
            // Where the pod stands is its transform's place, as the game's own bodies' are: the centre of mass of a vessel
            // on rails need not move with the orbit.
            Func<CelestialBody, float> over = body =>
            {
                Vessel pod = FlightGlobals.ActiveVessel;
                Vector3d at = pod.transform.position;
                Vector3d up = (at - pod.mainBody.position).normalized;
                return (float)(90.0 - Vector3d.Angle(up, body.position - at));
            };
            Func<float> phase = () => (float)Vector3d.Angle(sun.position - mun.position, (Vector3d)FlightGlobals.ActiveVessel.transform.position - mun.position);
            var times = new[]
            {
                new { Name = "mun-high-gibbous", Test = (Func<bool>)(() => over(sun) < -25f && over(mun) > 30f && phase() < 60f) },
                new { Name = "mun-mid-quarter", Test = (Func<bool>)(() => over(sun) < -25f && over(mun) > 40f && over(mun) < 60f
                                                                          && phase() > 60f && phase() < 100f) },
                new { Name = "mun-low-crescent", Test = (Func<bool>)(() => over(sun) < -25f && over(mun) > 12f && over(mun) < 35f
                                                                           && phase() > 90f && phase() < 150f) },
            };
            foreach (var time in times)
            {
                bool found = time.Test();
                double started = Planetarium.GetUniversalTime();
                int frames = 0;
                // Time on rails, 1000 times: a vessel in orbit that physics carries stands still where the game's time is
                // set on, and only a warp on rails moves it along its orbit.
                if (!found)
                {
                    TimeWarp.fetch.Mode = TimeWarp.Modes.HIGH;
                    TimeWarp.SetRate(5, true);
                }
                while (!found && Planetarium.GetUniversalTime() - started < 4.0 * mun.orbit.period)
                {
                    yield return null;
                    found = time.Test();
                    if (++frames % 120 == 0)
                        Debug.Log(Log.Tag + " Probe: light: 2D clouds looking for " + time.Name + ": the sun at " + over(sun).ToString("0", CultureInfo.InvariantCulture)
                                  + ", the Mun at " + over(mun).ToString("0", CultureInfo.InvariantCulture) + ", phase angle "
                                  + phase().ToString("0", CultureInfo.InvariantCulture) + ", rate " + TimeWarp.CurrentRate.ToString("0", CultureInfo.InvariantCulture) + ".");
                }
                TimeWarp.SetRate(0, true);
                yield return new WaitForSeconds(3f);
                Debug.Log(Log.Tag + " Probe: light: 2D clouds " + time.Name + (found ? " found" : " not found") + " after "
                          + (Planetarium.GetUniversalTime() - started).ToString("0", CultureInfo.InvariantCulture) + " s.");
                if (!found) continue;
                Debug.Log(Log.Tag + " Probe: light: 2D clouds " + time.Name + ", the Mun at " + over(mun).ToString("0.0", CultureInfo.InvariantCulture)
                          + " degrees, its phase angle " + phase().ToString("0", CultureInfo.InvariantCulture) + ", the sun at "
                          + over(sun).ToString("0.0", CultureInfo.InvariantCulture) + ".");
                foreach (bool away in new[] { false, true })
                {
                    string name = "clouds2d-" + time.Name + (away ? "-away" : "-towards");
                    yield return LookTowards(mun, 60f, 40f, away);
                    EveCloudMoonlight.HeldOff = true;
                    yield return ProbeSteps.Frames(90);
                    yield return Shot(folder, name + "-before");
                    EveCloudMoonlight.HeldOff = false;
                    yield return ProbeSteps.Frames(90);
                    Debug.Log(Log.Tag + " Probe: light: " + name + ": the clouds' light " + EveCloudMoonlight.Describe() + "; " + EveCloudRelight.Describe()
                              + "; " + EveCloudMoonlight.LayerSunBrightness() + ".");
                    yield return Shot(folder, name + "-after");
                }
            }
            Debug.Log(Log.Tag + " Probe: light: 2D clouds done.");
        }

        // The cloud shots at one time of CloudShots, by the light of the body named: logged with the body's and the
        // sun's elevation, then the views below. The clouds are shot in the sun's light alone ("before") and with
        // the Mun's light on them ("after"), the raytracing on.
        private static IEnumerator CloudShotsAt(ReDefinitionAddon addon, string folder, string time, CelestialBody light,
                                                RaytracingMode on, float moonlight, float nightAmbient)
        {
            CelestialBody sun = RtKspMods.StarBody();
            Debug.Log(Log.Tag + " Probe: light: clouds " + time + ", " + light.bodyName + " at "
                      + Elevation(light).ToString("0.0", CultureInfo.InvariantCulture) + " degrees, the sun at "
                      + Elevation(sun).ToString("0.0", CultureInfo.InvariantCulture) + ".");
            // Towards the light and away from it, towards the densest clouds in view (CloudCover),
            // and from 14 km out looking down towards the light. Before and after with the
            // raytracing on alike: shot with it off, the clouds still carried the light of the
            // shot before, which EVE's light volume and its clouds' history hold for some time.
            Set(addon, on, moonlight, nightAmbient);
            Vector3 densest;
            float cover = CloudCover(out densest);
            Debug.Log(Log.Tag + " Probe: light: clouds " + time + ": cloud cover " + cover.ToString("0.00", CultureInfo.InvariantCulture)
                      + " towards the densest.");
            var views = new[]
            {
                new { Name = "towards", Pitch = -6f, Distance = 60f, Away = false, Along = Vector3.zero },
                new { Name = "away", Pitch = -6f, Distance = 60f, Away = true, Along = Vector3.zero },
                new { Name = "densest", Pitch = -10f, Distance = 60f, Away = false, Along = densest },
                new { Name = "above", Pitch = 35f, Distance = 14000f, Away = false, Along = Vector3.zero },
            };
            string mode = on.ToString().ToLowerInvariant();
            foreach (var view in views)
            {
                if (view.Name == "densest" && view.Along.sqrMagnitude < 0.5f) continue;
                string name = "clouds-" + time + "-" + view.Name;
                if (view.Along.sqrMagnitude > 0.5f) yield return LookAlong(view.Along, view.Pitch, view.Distance, "the densest clouds");
                else yield return LookTowards(light, view.Pitch, view.Distance, view.Away);
                // EVE's light volume takes some frames to fill again (LightVolume, time slicing).
                EveCloudMoonlight.HeldOff = true;
                yield return ProbeSteps.Frames(90);
                yield return Shot(folder, name + "-" + mode + "-before");
                EveCloudMoonlight.HeldOff = false;
                yield return ProbeSteps.Frames(90);
                Debug.Log(Log.Tag + " Probe: light: " + name + ": the clouds' light " + EveCloudMoonlight.Describe() + "; "
                          + EveCloudRelight.Describe() + ".");
                yield return Shot(folder, name + "-" + mode + "-after");
                // At dusk, where the moon's light is added per pixel: where each pixel's cloud
                // point lies, and that light alone.
                if (view.Name == "densest" && time.StartsWith("dusk", StringComparison.Ordinal))
                {
                    EveCloudRelight.DebugMode = 1f;
                    yield return Shot(folder, name + "-perpixel-points");
                    EveCloudRelight.DebugMode = 2f;
                    yield return Shot(folder, name + "-perpixel-moon");
                    EveCloudRelight.DebugMode = 0f;
                }
            }
        }
    }
}
