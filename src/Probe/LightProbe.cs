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
        // With it, the runway's light pools at night alone (RunwayShots).
        internal const string RunwayArgument = "-redefinitionProbeRunway";
        // With it, the launch pad's smoke at night as the vessel lifts off (PadFxShots).
        internal const string PadFxArgument = "-redefinitionProbePadFx";
        // With it, frame times by day on the pad with each of ReDefinition's costly parts
        // off in turn (PerfShots).
        internal const string PerfArgument = "-redefinitionProbePerf";
        // With it, Firefly's effects forced on and off again on the pad (FireflyShots).
        internal const string FireflyArgument = "-redefinitionProbeFirefly";

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

        // The ocean's cases (tools/sea_reflection_check.py, its CASES): a body's height over
        // the horizon, and for each the camera's height over the sea and how far it looks
        // down; one warp to each height, the cases at it one after the other. The camera
        // looks at the vessel, so from Height / sin(Down) metres out.
        private struct SeaCase
        {
            internal string Name;
            internal float Height, Down;
        }

        // -redefinitionProbeCases sun-case09,mun-case06: only these shots.
        private const string CasesArgument = "-redefinitionProbeCases";

        private static bool Wanted(string shot)
        {
            string[] args = Environment.GetCommandLineArgs();
            int at = Array.IndexOf(args, CasesArgument);
            if (at < 0 || at + 1 >= args.Length) return true;
            return Array.IndexOf(args[at + 1].Split(','), shot) >= 0;
        }

        private struct BodyHeight
        {
            internal float Elevation;
            internal SeaCase[] Cases;
        }

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
                if (Array.IndexOf(Environment.GetCommandLineArgs(), FireflyArgument) >= 0)
                {
                    yield return FireflyShots(folder);
                    yield break;
                }
                if (Array.IndexOf(Environment.GetCommandLineArgs(), PerfArgument) >= 0)
                {
                    yield return PerfShots(addon, original, heading);
                    yield break;
                }
                if (Array.IndexOf(Environment.GetCommandLineArgs(), PadFxArgument) >= 0)
                {
                    bool night = false;
                    yield return WarpToMoonNight(result => night = result);
                    yield return PadFxShots(folder);
                    yield break;
                }
                if (Array.IndexOf(Environment.GetCommandLineArgs(), RunwayArgument) >= 0)
                {
                    bool night = false;
                    yield return WarpToMoonNight(result => night = result);
                    yield return RunwayShots(folder);
                    yield break;
                }
                if (Array.IndexOf(Environment.GetCommandLineArgs(), OceanArgument) >= 0)
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
                                    RaytracingMode on = addon.Live.Raytracing == RaytracingMode.Off ? RaytracingMode.Full : addon.Live.Raytracing;
                                    Set(addon, RaytracingMode.Off, addon.Live.RaytracingMoonlight, addon.Live.RaytracingNightAmbient);
                                    for (int i = 0; i < 30; i++) yield return null;
                                    yield return Shot(folder, side + "-off");
                                    Set(addon, on, addon.Live.RaytracingMoonlight, addon.Live.RaytracingNightAmbient);
                                    for (int i = 0; i < 60; i++) yield return null;
                                    yield return Shot(folder, side + "-on");
                                    LogOceanTextures(folder, side + "-on", null);
                                    WriteNormals(folder, side + "-on-frame", RtCamera.Current);
                                    RtCamera.OceanDiscsHeldOff = true;
                                    for (int i = 0; i < 30; i++) yield return null;
                                    yield return Shot(folder, side + "-no-discs");
                                    RtCamera.OceanDiscsHeldOff = false;
                                    RtCamera.OceanHandoffHeldOff = true;
                                    for (int i = 0; i < 30; i++) yield return null;
                                    yield return Shot(folder, side + "-no-handoff");
                                    RtCamera.OceanHandoffHeldOff = false;
                                    MoonGlow.HeldOff = true;
                                    for (int i = 0; i < 30; i++) yield return null;
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
                                    for (int i = 0; i < 60; i++) yield return null;
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

        // At night on the runway, 3 m over its middle line a little in from one end, along
        // it: its lights as KSP draws them, without them, and dimmed (Scenery.RunwayLights).
        // The runway's line from its light meshes: their middle and the direction between
        // the two farthest apart.
        private static IEnumerator RunwayShots(string folder)
        {
            Vessel vessel = FlightGlobals.ActiveVessel;
            if (vessel == null) yield break;
            CelestialBody body = vessel.mainBody;
            var runway = new List<Renderer>();
            foreach (Renderer light in Scenery.RunwayLights.Lights)
                if (light != null && light.enabled && light.gameObject.activeInHierarchy) runway.Add(light);
            if (runway.Count < 2)
            {
                Debug.LogWarning(Log.Tag + " Probe: light: no runway lights found; no runway shots.");
                yield break;
            }
            Vector3 middle = Vector3.zero;
            foreach (Renderer light in runway) middle += light.bounds.center;
            middle /= runway.Count;
            Vector3 a = runway[0].bounds.center, b = a;
            foreach (Renderer p in runway)
                foreach (Renderer q in runway)
                    if ((p.bounds.center - q.bounds.center).sqrMagnitude > (b - a).sqrMagnitude)
                    {
                        a = p.bounds.center;
                        b = q.bounds.center;
                    }
            Vector3 up = (middle - (Vector3)body.position).normalized;
            Vector3 direction = Vector3.ProjectOnPlane(b - a, up).normalized;
            float half = (b - a).magnitude * 0.5f;
            Vector3 eye = middle - direction * (half * 0.9f) + up * 3f;
            Debug.Log(Log.Tag + " Probe: light: runway " + (half * 2f).ToString("0", CultureInfo.InvariantCulture) + " m by its "
                      + runway.Count + " light meshes.");
            // The flight camera's own placing held (FlightCamera.DeactivateUpdate) and the
            // camera put at the runway; back afterwards.
            FlightCamera flight = FlightCamera.fetch;
            Vector3 position = flight.transform.position;
            Quaternion rotation = flight.transform.rotation;
            flight.DeactivateUpdate();
            float before = Scenery.RunwayLights.Brightness;
            try
            {
                flight.transform.position = eye;
                flight.transform.rotation = Quaternion.LookRotation(direction, up);
                Scenery.RunwayLights.Brightness = 1f;
                yield return Shot(folder, "runway-ksp");
                foreach (Renderer light in runway) light.enabled = false;
                yield return Shot(folder, "runway-without");
                foreach (Renderer light in runway) light.enabled = true;
                Scenery.RunwayLights.Brightness = before;
                yield return Shot(folder, "runway-dimmed");
            }
            finally
            {
                foreach (Renderer light in runway) light.enabled = true;
                Scenery.RunwayLights.Brightness = before;
                flight.transform.position = position;
                flight.transform.rotation = rotation;
                flight.ActivateUpdate();
            }
        }

        // At night, the camera beside the pad, 60 m off and 15 m up, the vessel's next stage
        // activated: the pad's smoke (PadFXReceiver, PadSmokeLvl3) after 1, 2 and 4 seconds.
        private static IEnumerator PadFxShots(string folder)
        {
            Vessel vessel = FlightGlobals.ActiveVessel;
            if (vessel == null) yield break;
            Vector3 up = ((Vector3)(vessel.CoMD - vessel.mainBody.position)).normalized;
            Vector3 side = Vector3.ProjectOnPlane(Vector3.Cross(up, Vector3.forward), up).normalized;
            Vector3 pad = vessel.CoMD - up * 10f;
            Vector3 eye = pad + side * 60f + up * 15f;
            FlightCamera flight = FlightCamera.fetch;
            flight.DeactivateUpdate();
            try
            {
                flight.transform.position = eye;
                flight.transform.rotation = Quaternion.LookRotation(pad - eye, up);
                yield return Shot(folder, "padfx-before");
                KSP.UI.Screens.StageManager.ActivateNextStage();
                float started = Time.time;
                foreach (float at in new[] { 1f, 2f, 4f })
                {
                    while (Time.time - started < at) yield return null;
                    flight.transform.position = eye;
                    flight.transform.rotation = Quaternion.LookRotation(pad - eye, up);
                    yield return new WaitForEndOfFrame();
                    ScreenCapture.CaptureScreenshot(Path.Combine(folder, "padfx-" + at.ToString("0", CultureInfo.InvariantCulture) + "s.png"));
                }
                // Each pad smoke system's particles and its renderer's settings.
                foreach (ParticleSystem system in UnityEngine.Object.FindObjectsOfType<ParticleSystem>())
                {
                    if (!system.name.StartsWith("PadSmoke", StringComparison.Ordinal)) continue;
                    var renderer = system.GetComponent<ParticleSystemRenderer>();
                    Material m = renderer != null ? renderer.sharedMaterial : null;
                    Debug.Log(Log.Tag + " Probe: light: pad smoke '" + system.name + "' " + system.particleCount + " particles, mode "
                              + (renderer != null ? renderer.renderMode.ToString() : "-") + ", material " + (m != null ? m.name + " (" + m.shader.name + ")" : "-")
                              + (m != null && m.mainTexture != null ? ", texture " + m.mainTexture.name : ""));
                }
            }
            finally
            {
                flight.ActivateUpdate();
            }
        }

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

        // Firefly's re-entry effects forced through its own override (AtmoFxModule.
        // OverridePhysics, OverrideEffectStrength 2500, state 1, the air from below) and
        // taken off again: whether Scenery.FireflyIdle lets them be drawn and idles them
        // again, a shot of each.
        private static IEnumerator FireflyShots(string folder)
        {
            Vessel vessel = FlightGlobals.ActiveVessel;
            Type module = Core.TypeLookup.Find("Firefly.AtmoFxModule");
            VesselModule fx = null;
            if (vessel != null && module != null)
                foreach (VesselModule m in vessel.vesselModules)
                    if (m != null && m.GetType() == module) fx = m;
            if (fx == null)
            {
                Debug.LogWarning(Log.Tag + " Probe: light: no Firefly module on the vessel.");
                yield break;
            }
            const BindingFlags Public = BindingFlags.Public | BindingFlags.Instance;
            Action<string, object> set = (name, value) => module.GetProperty(name, Public).SetValue(fx, value, null);
            Vector3 up = ((Vector3)(vessel.CoMD - vessel.mainBody.position)).normalized;
            yield return Shot(folder, "firefly-idle");
            set("OverrideEntryDirection", -up);
            set("OverrideEffectState", 1f);
            set("OverrideEffectStrength", 2500f);
            set("OverridePhysics", true);
            yield return new WaitForSeconds(3f);
            yield return Shot(folder, "firefly-on");
            set("OverrideEffectStrength", 0f);
            yield return new WaitForSeconds(5f);
            yield return Shot(folder, "firefly-off");
            set("OverridePhysics", false);
        }

        // The VAB's place, west of the pad: its faces and the roads before it, seen flat.
        private const double VabLatitude = -0.0967, VabLongitude = -74.6187;

        private static IEnumerator BuildingShots(ReDefinitionAddon addon, string folder, string time)
        {
            Vessel vessel = FlightGlobals.ActiveVessel;
            if (vessel == null) yield break;
            CelestialBody body = vessel.mainBody;
            Vector3d vab = body.GetWorldSurfacePosition(VabLatitude, VabLongitude, 0.0);
            FlightCamera.fetch.SetDistance(Mathf.Min(40f, FlightCamera.fetch.maxDistance));
            Vector3 up = ((Vector3)(vessel.CoMD - body.position)).normalized;
            Vector3 flat = Vector3.ProjectOnPlane((Vector3)(vab - vessel.CoMD), up).normalized;
            Vector3 look = flat * Mathf.Cos(4f * Mathf.Deg2Rad) - up * Mathf.Sin(4f * Mathf.Deg2Rad);
            FlightCamera.fetch.SetCamCoordsFromPosition(-look * FlightCamera.fetch.Distance);
            for (int i = 0; i < 10; i++) yield return null;
            RaytracingMode on = addon.Live.Raytracing == RaytracingMode.Off ? RaytracingMode.Full : addon.Live.Raytracing;
            Set(addon, RaytracingMode.Off, 10f, 0f);
            yield return Shot(folder, time + "-buildings-off");
            Set(addon, on, 10f, 0f);
            yield return Shot(folder, time + "-buildings-on");
            // The same with the G-buffer's shine as Deferred gives it, and the surfaces' overlay.
            RtCamera.SurfacesAsDeferred = true;
            for (int i = 0; i < 30; i++) yield return null;
            yield return Shot(folder, time + "-buildings-deferred");
            RtCamera.SurfacesAsDeferred = false;
            RtCamera.SurfaceView = true;
            for (int i = 0; i < 30; i++) yield return null;
            yield return Shot(folder, time + "-buildings-overlay");
            RtCamera.SurfaceView = false;
        }

        private static IEnumerator Shots(ReDefinitionAddon addon, string folder, string time, float heading, bool night)
        {
            RaytracingMode on = addon.Live.Raytracing == RaytracingMode.Off ? RaytracingMode.Full : addon.Live.Raytracing;
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
                    for (int i = 0; i < 30; i++) yield return null;
                    yield return Shot(folder, time + "-" + pose.Name + "-deferred");
                    RtCamera.SurfacesAsDeferred = false;
                    RtCamera.SurfaceView = true;
                    for (int i = 0; i < 30; i++) yield return null;
                    yield return Shot(folder, time + "-" + pose.Name + "-overlay");
                    RtCamera.SurfaceView = false;
                }
            }
        }

        // The vessel and the ground under it seen from where they would mirror the Mun, 20 m
        // out: the raytracing off (Deferred's reflection probe), on with the parts'
        // smoothness as KSP's highlight (RtCommon.hlsli, LegacySmoothness) and on with
        // Deferred's.
        private static IEnumerator MirrorShots(ReDefinitionAddon addon, string folder)
        {
            CelestialBody mun = FlightGlobals.Bodies.Find(b => b.bodyName == "Mun");
            if (mun == null) yield break;
            RaytracingMode on = addon.Live.Raytracing == RaytracingMode.Off ? RaytracingMode.Full : addon.Live.Raytracing;
            yield return LookTowards(mun, Elevation(mun), 20f);
            Set(addon, RaytracingMode.Off, 10f, 0f);
            yield return Shot(folder, "mirror-off");
            Set(addon, on, 10f, 0f);
            foreach (bool deferred in new[] { false, true })
            {
                RtCamera.DeferredSmoothness = deferred;
                for (int i = 0; i < 60; i++) yield return null;
                yield return Shot(folder, "mirror-" + (deferred ? "deferred-smoothness" : "ksp-highlight"));
            }
            RtCamera.DeferredSmoothness = false;
            MoonGlow.HeldOff = true;
            for (int i = 0; i < 30; i++) yield return null;
            yield return Shot(folder, "mirror-no-glow");
            MoonGlow.HeldOff = false;
            // Straight down on the vessel from 30 m, as a player looks at a plane on the runway.
            FlightCamera.fetch.SetDistance(30f);
            FlightCamera.fetch.camPitch = 89f * Mathf.Deg2Rad;
            for (int i = 0; i < 60; i++) yield return null;
            yield return Shot(folder, "down-on");
            MoonGlow.HeldOff = true;
            for (int i = 0; i < 30; i++) yield return null;
            yield return Shot(folder, "down-no-glow");
            MoonGlow.HeldOff = false;
            Set(addon, RaytracingMode.Off, 10f, 0f);
            for (int i = 0; i < 30; i++) yield return null;
            yield return Shot(folder, "down-off");
            MoonGlow.HeldOff = true;
            for (int i = 0; i < 30; i++) yield return null;
            yield return Shot(folder, "mirror-off-no-glow");
            MoonGlow.HeldOff = false;
            Set(addon, on, 10f, 0f);
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
        private static IEnumerator LookTowards(CelestialBody body, float pitch, float distance, bool away = false)
        {
            Vessel vessel = FlightGlobals.ActiveVessel;
            if (vessel == null || body == null) yield break;
            FlightCamera.fetch.SetDistance(Mathf.Min(distance, FlightCamera.fetch.maxDistance));
            for (int attempt = 0; attempt < 2; attempt++)
            {
                Vector3 up = ((Vector3)(vessel.CoMD - vessel.mainBody.position)).normalized;
                Vector3 flat = Vector3.ProjectOnPlane((Vector3)(body.position - vessel.CoMD), up).normalized * (away ? -1f : 1f);
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
            Color[] mean = null;
            yield return AverageTraced(result => mean = result);
            LogOceanTextures(folder, name, mean);
            yield return WavesInPlace(folder, name);
            // KSP's own sea's tests as colour (ReDefinitionDeferredReflections.shader,
            // _ReDefinitionWaterDebug): which fails where.
            if (RtCamera.StockSeaTraced)
            {
                Shader.SetGlobalFloat(WaterDebugId, 1f);
                for (int i = 0; i < 5; i++) yield return null;
                yield return Shot(folder, name + "-water-debug");
                Shader.SetGlobalFloat(WaterDebugId, 0f);
                for (int i = 0; i < 5; i++) yield return null;
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

        // The ocean's raytraced reflections as the last frame left them: the share of
        // pixels the ocean covers with a weight, and of the traced ones that met something.
        // The traced reflections averaged over TracedSamples frames TracedSpacing seconds
        // apart, as the waves move on: near by, where a pixel shows single glints and a few
        // waves tilt all the water in view, what the sea gives over time.
        private static readonly int WaterDebugId = Shader.PropertyToID("_ReDefinitionWaterDebug");

        // Whether the waves stay on the water as the camera moves
        // (tools/sea_reflection_check.py --waves): the water's normals with the waves' time
        // held, before and after the camera moved a tenth further out, once with the sea's
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
                    for (int i = 0; i < 20; i++) yield return null;
                    yield return new WaitForEndOfFrame();
                    WriteNormals(folder, tag + "-a", camera);
                    FlightCamera.fetch.SetDistance(distance * 1.1f);
                    for (int i = 0; i < 60; i++) yield return null;
                    yield return new WaitForEndOfFrame();
                    WriteNormals(folder, tag + "-b", camera);
                    FlightCamera.fetch.SetDistance(distance);
                    for (int i = 0; i < 60; i++) yield return null;
                }
            }
            finally
            {
                RtCamera.SeaFrameLeftOut = false;
                RtCamera.WaterNormalsShown = false;
                RtCamera.WaveTimeHeld = null;
            }
        }

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
        private const int TracedSamples = 16;
        private const float TracedSpacing = 0.5f;

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
