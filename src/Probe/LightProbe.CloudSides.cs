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

        // The pole's sky and overhead at the sun's lowest on this latitude, measured as the clouds are.
        private static IEnumerator AuroraView(ReDefinitionAddon addon, string folder)
        {
            CelestialBody sun = RtKspMods.StarBody();
            if (sun == null) yield break;
            Set(addon, RaytracingMode.Light, addon.Live.RaytracingMoonlight, addon.Live.RaytracingNightAmbient);
            yield return ProbeSteps.Frames(RatioFramesAfterMode);
            Debug.Log(Log.Tag + " Probe: cloud layers over " + FlightGlobals.currentMainBody.bodyName + ": "
                      + EveCloudMoonlight.DescribeLayers(FlightGlobals.currentMainBody.bodyName) + ".");
            bool dark = false;
            yield return WarpUntil("the sun below 20 degrees", () => Elevation(sun) < -20f, result => dark = result, 6.0 * 3600.0);
            Vessel vessel = FlightGlobals.ActiveVessel;
            Vector3 up = ((Vector3)(((Vector3d)vessel.transform.position) - vessel.mainBody.position)).normalized;
            Vector3 north = Vector3.ProjectOnPlane((Vector3)vessel.north, up).normalized;
            Debug.Log(Log.Tag + " Probe: aurora: the pod at latitude " + vessel.latitude.ToString("0.0", CultureInfo.InvariantCulture)
                      + ", the sun at " + Elevation(sun).ToString("0.0", CultureInfo.InvariantCulture) + " degrees" + (dark ? "." : ", not dark enough."));
            // The direction round the horizon with the strongest aurora 45 km up and 150 km away.
            FindAuroras(vessel.mainBody.bodyName);
            Vector3 toward = north;
            float strongest = -1f;
            for (int step = 0; step < 12; step++)
            {
                Vector3 direction = Quaternion.AngleAxis(30f * step, up) * north;
                float value = AuroraAt((Vector3)((Vector3d)vessel.transform.position) + direction * 150000f + up * 60000f);
                Debug.Log(Log.Tag + " Probe: aurora: " + (30 * step) + " degrees from north, coverage " + value.ToString("0.00", CultureInfo.InvariantCulture) + ".");
                if (value > strongest) { strongest = value; toward = direction; }
            }
            Debug.Log(Log.Tag + " Probe: aurora: the strongest coverage " + strongest.ToString("0.00", CultureInfo.InvariantCulture) + ", looked at.");
            yield return LookAlong(toward, -12f, 60f, "the aurora");
            yield return CloudRatio(folder, "aurora-view", "aurora, at its strongest direction");
            Debug.Log(Log.Tag + " Probe: aurora done.");
        }
    }
}
