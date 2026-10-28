using System;
using System.Collections;
using System.Globalization;
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
                yield return CloudRatio(folder, "sides-" + position.Name + "-below", "sides, " + label + ", from the ground looking up");
                yield return LookAlong(flat, RatioPitch, 14000f, "the clouds from above");
                yield return CloudRatio(folder, "sides-" + position.Name + "-above", "sides, " + label + ", from 14 km looking down");
            }
            Debug.Log(Log.Tag + " Probe: cloud sides done.");
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
            yield return LookAlong(north, -15f, 60f, "the pole's sky");
            yield return CloudRatio(folder, "aurora-north", "aurora, towards the pole");
            yield return LookAlong(north, -60f, 60f, "overhead");
            yield return CloudRatio(folder, "aurora-up", "aurora, overhead");
            Debug.Log(Log.Tag + " Probe: aurora done.");
        }
    }
}
