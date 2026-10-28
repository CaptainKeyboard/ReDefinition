using System;
using System.Collections;
using System.Globalization;
using System.Reflection;
using ReDefinition.Core;
using ReDefinition.Modules;
using ReDefinition.Raytracing;
using UnityEngine;

namespace ReDefinition.Probe
{
    // -redefinitionProbeRatioSides: the clouds seen from below and from above in the same moonlight. At night it does so with the
    // moon high and again with it low. It looks up at the densest clouds from the ground and down at them from 14 km.
    //
    // -redefinitionProbeRatioAurora: the sky towards the pole and overhead, where the auroras are. The pod stands on high ground
    // (-redefinitionProbeLandLatitude), and the clouds are measured as in the other flows.
    internal static partial class LightProbe
    {
        // The moon high and low: the ground view looking up at the densest clouds and the view down from 14 km, each measured.
        private static IEnumerator CloudSides(ReDefinitionAddon addon, string folder)
        {
            CelestialBody sun = RtKspMods.StarBody();
            CelestialBody moon = BrightestMoon();
            if (sun == null || moon == null) yield break;
            double day = Math.Max(43200.0, 1.2 * FlightGlobals.currentMainBody.solarDayLength);
            Set(addon, RaytracingMode.Light, addon.Live.RaytracingMoonlight, addon.Live.RaytracingNightAmbient);
            yield return ProbeSteps.Frames(RatioFramesAfterMode);
            var moons = new[]
            {
                new { Name = "high", Wanted = (Func<bool>)(() => Elevation(sun) < -NightDepth && Elevation(moon) > 45f && PhaseAngle(moon, sun) < 100f) },
                new { Name = "low", Wanted = (Func<bool>)(() => Elevation(sun) < -NightDepth && Elevation(moon) > 8f && Elevation(moon) < 25f && PhaseAngle(moon, sun) < 110f) },
            };
            foreach (var position in moons)
            {
                bool there = false;
                yield return WarpUntilCloudy("the sun down, " + moon.bodyName + " " + position.Name, position.Wanted, result => there = result,
                                             4.0 * Math.Max(moon.orbit.period, day));
                if (!there) continue;
                Vessel vessel = FlightGlobals.ActiveVessel;
                Vector3 up = ((Vector3)(((Vector3d)vessel.transform.position) - vessel.mainBody.position)).normalized;
                Vector3 flat = Vector3.ProjectOnPlane(Vector3.forward, up).normalized;
                Vector3 densest;
                CloudCover(out densest);
                Vector3 towardsClouds = densest.sqrMagnitude > 0.5f ? Vector3.ProjectOnPlane(densest, up).normalized : flat;
                string label = "the sun " + Elevation(sun).ToString("0", CultureInfo.InvariantCulture) + ", " + moon.bodyName + " "
                               + Elevation(moon).ToString("0", CultureInfo.InvariantCulture) + " degrees";
                yield return LookAlong(towardsClouds, -25f, 60f, "the clouds from below");
                Debug.Log(Log.Tag + " Probe: cloud sides: the moon is "
                          + Vector3.Angle(FlightCamera.fetch.transform.forward, (Vector3)(moon.position - (Vector3d)vessel.transform.position)).ToString("0", CultureInfo.InvariantCulture)
                          + " degrees off the line of sight, looking up at the clouds.");
                yield return CloudRatio(folder, "sides-" + position.Name + "-below", "sides, " + label + ", from the ground looking up");
                // At the moon itself: the angle from the camera's line of sight to it is told, to say whether it is in the picture.
                Vector3 toMoon = (Vector3)(moon.position - (Vector3d)vessel.transform.position).normalized;
                Vector3 moonFlat = Vector3.ProjectOnPlane(toMoon, up).normalized;
                yield return LookAlong(moonFlat, -Elevation(moon), 60f, "the moon");
                Debug.Log(Log.Tag + " Probe: cloud sides: the moon is " + Vector3.Angle(FlightCamera.fetch.transform.forward, toMoon).ToString("0", CultureInfo.InvariantCulture)
                          + " degrees off the line of sight.");
                yield return CloudRatio(folder, "sides-" + position.Name + "-moon", "sides, " + label + ", from the ground looking at the moon");
                yield return LookAlong(flat, RatioPitch, 14000f, "the clouds from above");
                yield return CloudRatio(folder, "sides-" + position.Name + "-above", "sides, " + label + ", from 14 km looking down");
            }
            Debug.Log(Log.Tag + " Probe: cloud sides done.");
        }


        // EVE's aurora volumes of the body (name with "aurora"), found through its manager, and the coverage they give at a world point.
        private static readonly System.Collections.Generic.List<object> auroraVolumes = new System.Collections.Generic.List<object>();
        private static MethodInfo auroraSample;

        private static void FindAuroras(string bodyName)
        {
            auroraVolumes.Clear();
            Type manager = TypeLookup.Find("Atmosphere.CloudsManager");
            Type objectType = TypeLookup.Find("Atmosphere.CloudsObject");
            if (manager == null || objectType == null) return;
            FieldInfo list = null;
            for (Type t = manager; t != null && list == null; t = t.BaseType)
                list = t.GetField("ObjectList", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            IList objects = list != null ? list.GetValue(null) as IList : null;
            PropertyInfo name = objectType.GetProperty("Name", TypeLookup.Any), body = objectType.GetProperty("Body", TypeLookup.Any);
            PropertyInfo volume = objectType.GetProperty("LayerRaymarchedVolume", TypeLookup.Any);
            if (objects == null || name == null || body == null || volume == null) return;
            foreach (object cloudsObject in objects)
            {
                string layerName = name.GetValue(cloudsObject, null) as string;
                object layer = volume.GetValue(cloudsObject, null);
                if (layerName == null || layer == null || !CloudLayerKinds.ShinesByName(layerName)) continue;
                if ((body.GetValue(cloudsObject, null) as string) != bodyName) continue;
                auroraVolumes.Add(layer);
                if (auroraSample == null)
                    auroraSample = layer.GetType().GetMethod("SampleCoverage", new[] { typeof(Vector3), typeof(float).MakeByRefType(), typeof(bool) });
            }
        }

        // The strongest coverage of the auroras at a world point, 0 to 1; -1 where there is none to ask.
        private static float AuroraAt(Vector3 world)
        {
            if (auroraSample == null) return -1f;
            float best = 0f;
            foreach (object layer in auroraVolumes)
            {
                object[] arguments = { world, 0f, false };
                best = Mathf.Max(best, (float)auroraSample.Invoke(layer, arguments));
            }
            return best;
        }

        // Where the auroras stand on the body: their coverage 45 km up over a grid of latitudes and longitudes, the strongest per
        // latitude into the log.
        private static IEnumerator AuroraScan()
        {
            CelestialBody body = FlightGlobals.currentMainBody;
            FindAuroras(body.bodyName);
            Debug.Log(Log.Tag + " Probe: aurora scan: " + auroraVolumes.Count + " aurora volumes over " + body.bodyName + ".");
            for (double lat = 20.0; lat <= 89.0; lat += 3.0)
            {
                float best = 0f;
                double bestLon = 0.0;
                for (double lon = 0.0; lon < 360.0; lon += 15.0)
                {
                    float value = AuroraAt((Vector3)body.GetWorldSurfacePosition(lat, lon, 45000.0));
                    if (value > best) { best = value; bestLon = lon; }
                }
                Debug.Log(Log.Tag + " Probe: aurora scan: latitude " + lat.ToString("0", CultureInfo.InvariantCulture) + ", the most "
                          + best.ToString("0.00", CultureInfo.InvariantCulture) + " at longitude " + bestLon.ToString("0", CultureInfo.InvariantCulture) + ".");
                yield return null;
            }
        }

        // The same view of the auroras as the game draws it, and with one part of ReDefinition off at a time: the moon's light on the
        // clouds, the upscaler, the raytracing. Each is a shot of the screen as it is. An artifact that stays with a part off is not that part's.
        private static IEnumerator AuroraVariants(ReDefinitionAddon addon, string folder, string name)
        {
            float moonlight = addon.Live.RaytracingMoonlight, nightAmbient = addon.Live.RaytracingNightAmbient;
            RaytracingMode mode = addon.Live.Raytracing;
            bool upscaler = addon.Live.Enabled;
            Debug.Log(Log.Tag + " Probe: aurora variants: the upscaler " + (upscaler ? "on" : "off") + ", raytracing " + mode + ".");
            yield return AuroraShot(folder, name + "-all");
            EveCloudMoonlight.HeldOff = true;
            EveCloudRelight.HeldOff = true;
            yield return AuroraShot(folder, name + "-nomoon");
            EveCloudMoonlight.HeldOff = false;
            EveCloudRelight.HeldOff = false;
            OwnSettings before = addon.Live.Clone();
            OwnSettings after = before.Clone();
            after.Enabled = false;
            addon.Apply(before, after);
            yield return AuroraShot(folder, name + "-noupscaler");
            Set(addon, RaytracingMode.Off, moonlight, nightAmbient);
            yield return AuroraShot(folder, name + "-noupscaler-nort");
            before = addon.Live.Clone();
            after = before.Clone();
            after.Enabled = upscaler;
            addon.Apply(before, after);
            yield return AuroraShot(folder, name + "-nort");
            Set(addon, mode, moonlight, nightAmbient);
        }

        // A shot of the screen after the frames have settled.
        private static IEnumerator AuroraShot(string folder, string name)
        {
            yield return ProbeSteps.Frames(90);
            UnityEngine.ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(folder, name + ".png"));
            yield return ProbeSteps.Frames(3);
        }

        // Whether the moon's light changes the auroras' pixels. The moon is turned to 45 degrees up on the side the camera looks to, the
        // per-pixel light forced on, and the picture taken with the clouds off, on, and on with the moon's light held off. Of the
        // pixels the clouds change, those the moon's light changes too are weather; the rest are the auroras', and must not change.
        private static IEnumerator AuroraMoonCheck(string folder, string name, Vector3 up, Vector3 toward)
        {
            EveCloudMoonlight.ProbeMoonDirection = (up * 0.7071f + toward * 0.7071f).normalized;
            EveCloudMoonlight.ProbeShare = 1f;
            EveCloudRelight.ProbeAlways = true;
            Color[] off = null, lit = null, held = null;
            int width = 0, height = 0;
            CloudsVisible(false);
            yield return ProbeSteps.Frames(RatioFramesAfterSwitch);
            yield return ColourShot((p, w, h) => { off = p; width = w; height = h; });
            CloudsVisible(true);
            yield return ProbeSteps.Frames(RatioFramesAfterSwitch);
            yield return ColourShot((p, w, h) => lit = p);
            SavePng(System.IO.Path.Combine(folder, name + "-moon.png"), lit, width, height);
            EveCloudMoonlight.HeldOff = true;
            EveCloudRelight.HeldOff = true;
            yield return ProbeSteps.Frames(RatioFramesAfterSwitch);
            yield return ColourShot((p, w, h) => held = p);
            EveCloudMoonlight.HeldOff = false;
            EveCloudRelight.HeldOff = false;
            EveCloudMoonlight.ProbeMoonDirection = Vector3.zero;
            EveCloudMoonlight.ProbeShare = -1f;
            EveCloudRelight.ProbeAlways = false;
            if (off == null || lit == null || held == null || off.Length != lit.Length || off.Length != held.Length) yield break;
            int weather = 0, aurora = 0;
            double weatherLit = 0.0, weatherHeld = 0.0, auroraLit = 0.0, auroraHeld = 0.0, worst = 0.0;
            for (int i = 0; i < lit.Length; i++)
            {
                float change = LargestChange(lit[i], off[i]);
                if (change < 0.002f) continue;
                float moon = LargestChange(lit[i], held[i]);
                if (moon > 0.0005f + 0.02f * Mathf.Max(lit[i].r, Mathf.Max(lit[i].g, lit[i].b)))
                {
                    weather++;
                    weatherLit += Luminance(lit[i]);
                    weatherHeld += Luminance(held[i]);
                }
                else
                {
                    aurora++;
                    auroraLit += Luminance(lit[i]);
                    auroraHeld += Luminance(held[i]);
                    worst = Math.Max(worst, moon);
                }
            }
            var culture = CultureInfo.InvariantCulture;
            Debug.Log(Log.Tag + " Probe: aurora moon check " + name + ": " + aurora + " pixels the moon's light leaves alone, luminance "
                      + (aurora > 0 ? auroraLit / aurora : 0.0).ToString("0.00000", culture) + " with it and "
                      + (aurora > 0 ? auroraHeld / aurora : 0.0).ToString("0.00000", culture) + " without, at most " + worst.ToString("0.00000", culture)
                      + " apart; " + weather + " pixels it changes, " + (weather > 0 ? weatherLit / weather : 0.0).ToString("0.00000", culture)
                      + " with it and " + (weather > 0 ? weatherHeld / weather : 0.0).ToString("0.00000", culture) + " without.");
        }

        // The auroras at the sun's heights. The pod hangs in the air over -redefinitionProbeHoverLatitude (with -redefinitionProbeOrbit
        // and -redefinitionProbeHover) and is set down in turn where the sun stands 40, 5, -12 and -25 degrees high. Each time the
        // camera looks along the line of sight with the strongest aurora coverage, and the clouds are measured as in the other flows.
        private static IEnumerator AuroraView(ReDefinitionAddon addon, string folder)
        {
            CelestialBody sun = RtKspMods.StarBody();
            if (sun == null) yield break;
            Set(addon, RaytracingMode.Light, addon.Live.RaytracingMoonlight, addon.Live.RaytracingNightAmbient);
            yield return ProbeSteps.Frames(RatioFramesAfterMode);
            double altitude, latitude;
            bool hanging = LandProbe.HoverAltitude(out altitude) & LandProbe.HoverLatitude(out latitude);
            if (hanging)
            {
                ReDefinitionAddon.Instance.StartCoroutine(LandProbe.Hover(altitude));
                yield return new WaitForSecondsRealtime(8f);
            }
            Debug.Log(Log.Tag + " Probe: cloud layers over " + FlightGlobals.currentMainBody.bodyName + ": "
                      + EveCloudMoonlight.DescribeLayers(FlightGlobals.currentMainBody.bodyName) + ".");
            foreach (double elevation in hanging ? new[] { 40.0, 5.0, -12.0, -25.0 } : new[] { double.NaN })
            {
                Vessel vessel = FlightGlobals.ActiveVessel;
                CelestialBody body = vessel.mainBody;
                if (hanging)
                {
                    LandProbe.HoverAt(vessel, LandProbe.HoverDirection(body, latitude, elevation), altitude);
                    yield return ProbeSteps.Frames(90);
                }
                Vector3 up = ((Vector3)(((Vector3d)vessel.transform.position) - body.position)).normalized;
                FindAuroras(body.bodyName);
                // Round the horizon, 12 directions, 50 to 300 km out and 45 km over the ground: the strongest coverage.
                Vector3d pod = (Vector3d)vessel.transform.position;
                Vector3 north = Vector3.ProjectOnPlane((Vector3)vessel.north, up).normalized;
                Vector3 toward = north;
                float strongest = -1f, climb = 10f;
                for (int step = 0; step < 12; step++)
                {
                    Vector3 direction = Quaternion.AngleAxis(30f * step, up) * north;
                    for (float distance = 50000f; distance <= 300000f; distance += 25000f)
                    {
                        Vector3d around = (pod - body.position) + (Vector3d)direction * distance;
                        Vector3d point = body.position + around.normalized * (body.Radius + 45000.0);
                        float value = AuroraAt((Vector3)point);
                        if (value > strongest)
                        {
                            strongest = value;
                            toward = direction;
                            climb = Mathf.Asin(Mathf.Clamp(Vector3.Dot(((Vector3)(point - pod)).normalized, up), -1f, 1f)) * Mathf.Rad2Deg;
                        }
                    }
                }
                string label = "aurora, the sun " + Elevation(sun).ToString("0", CultureInfo.InvariantCulture) + " degrees, latitude "
                               + vessel.latitude.ToString("0", CultureInfo.InvariantCulture) + ", coverage " + strongest.ToString("0.00", CultureInfo.InvariantCulture)
                               + " at " + climb.ToString("0", CultureInfo.InvariantCulture) + " degrees up";
                Debug.Log(Log.Tag + " Probe: " + label + ".");
                yield return LookAlong(toward, -climb, 60f, "the aurora");
                string name = "aurora-sun" + (double.IsNaN(elevation) ? "x" : ((int)elevation).ToString(CultureInfo.InvariantCulture));
                if (CommandLine.Has("-redefinitionProbeAuroraVariants"))
                {
                    yield return AuroraVariants(addon, folder, name);
                    continue;
                }
                yield return CloudRatio(folder, name, label);
                yield return AuroraMoonCheck(folder, name, up, toward);
            }
            Debug.Log(Log.Tag + " Probe: aurora done.");
        }
    }
}
