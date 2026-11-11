using System;
using System.Collections.Generic;
using System.Globalization;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // The probe of package D32 inside the integrated series K-W3b (start "lights"): the lamp scatter against the running game. What it
    // measures from inside the game, as rows of the series:
    //   - D32.lamps_known: the lamps found on the vessels in reach (the series craft carries lights);
    //   - clear air: no lamp is drawn and no command buffer is attached (zero cost): D32.clear_air_draws, D32.clear_air_buffers;
    //   - snow forced on a disc round the vessel: with all sources on the lamps are drawn, the buffers are attached and the depth copy
    //     runs (D32.snow_draws, D32.snow_buffers, D32.snow_depth_copies);
    //   - the variants all / fog / snow / cloud / dust switch each medium source on its own and report its sigma at the first lamp
    //     (D32.light_sigma_fog, D32.light_sigma_snow, D32.light_sigma_cloud, D32.light_sigma_dust, with the variant's name);
    //   - the cloud density at the nearest EVE layer's mid shell in eight directions round the vessel (D32.cloud_density_max): the source
    //     the lamp reads inside a cloud; flying into a cloud is not scripted;
    //   - the CPU milliseconds of the module's frame work (D32.cpu_ms).
    // What it does not measure, and why: the beam's luminance (a pixel read needs the frame source of the game, which the
    // reconnaissance found unreliable [VERIFY]; the player's lamp-check measures it against the maths), the smear under DLSS, the ghost
    // metrics and the frame-generation duplicates (proxy runs, E40). Their rows are in tests/series/rows/D32.json as not required.
    [AtmospheresModule(986)]
    internal sealed class LampProbe : SeriesProbe
    {
        internal const string SeriesId = "K-W3b";
        private static readonly string[] Sources = { "fog", "precip", "cloud", "dust" };

        protected override string SeriesName
        {
            get { return SeriesId; }
        }

        protected override double MaxSeconds
        {
            get { return 12 * 60; }
        }

        protected override IList<string> DefaultVariants
        {
            get { return new[] { "all", "fog", "snow", "cloud", "dust" }; }
        }

        protected override void ApplyVariant(string name)
        {
            string only = name == "snow" ? "precip" : name;
            foreach (string source in Sources)
            {
                bool on = name == "all" || source == only;
                ForceTuning("lamp.src." + source, on ? 1f : 0f);
            }
        }

        protected override void RevertVariant(string name)
        {
            foreach (string source in Sources) ForceTuning("lamp.src." + source, 1f);
        }

        private static string Num(double v)
        {
            return v.ToString("0.#####", CultureInfo.InvariantCulture);
        }

        protected override IEnumerator<ProbeWait> Scenario()
        {
            yield return ProbeWait.Until(() => HighLogic.LoadedScene == GameScenes.FLIGHT && FlightGlobals.ready && FlightGlobals.ActiveVessel != null,
                330, "the flight");
            if (HighLogic.LoadedScene != GameScenes.FLIGHT)
            {
                Fail("D32: no flight to measure in");
                yield break;
            }
            SetSetting(AtmospheresSettings.Keys.Enabled, 1.0);
            SetSetting(AtmospheresSettings.Keys.WeatherEffects, 1.0);
            SetSetting(AtmospheresSettings.Keys.LightInMedium, 1.0);
            SetSetting(AtmospheresSettings.Keys.LightInMediumStrength, 1.0);
            SetSetting(AtmospheresSettings.Keys.LightCount, 4.0);
            yield return ProbeWait.Until(() => LampScatter.Instance != null && LampScatter.KnownLights > 0, 150, "lamps on the vessels");
            Emit("D32.lamps_known", LampScatter.KnownLights);
            if (LampScatter.Instance == null || LampScatter.KnownLights == 0)
            {
                Fail("D32: no lamp was found (the series craft needs lights, and the module needs its shader)");
                yield break;
            }
            Vessel vessel = FlightGlobals.ActiveVessel;
            CelestialBody body = vessel.mainBody;

            // Clear air: the module draws nothing and attaches nothing. The forced precipitation is not there yet; the fog of a clear day
            // is below the least sigma, else the row says so.
            yield return ProbeWait.Seconds(4);
            Emit("D32.clear_air_draws", LampScatter.LastDrawCount);
            Emit("D32.clear_air_buffers", LampScatter.BuffersAttached);
            Emit("D32.clear_air_sigma", LampScatter.LastSigma.Total);

            // Snow round the vessel, the lamps on.
            double ut = Planetarium.GetUniversalTime();
            bool forced = ForcePrecipitation(body.flightGlobalsIndex, vessel.latitude, vessel.longitude, 40.0, 25f, PrecipType.Snow, ut - 10.0, ut + 3600.0);
            Emit("D32.snow_forced", forced);
            if (forced)
            {
                yield return ProbeWait.Until(() => WeatherHooks.View.Active && WeatherHooks.View.Level > 0.25f, 90, "snow at the vessel");
            }
            Emit("D32.snow_level", WeatherHooks.View.Level);

            foreach (string name in new List<string>(Variants))
            {
                BeginVariant(name);
                // The slew of sigma and the fade of the lamps settle.
                yield return ProbeWait.Seconds(5);
                LampSigma s = LampScatter.LastSigma;
                if (name == "all" || name == "fog") Emit("D32.light_sigma_fog", s.Fog);
                if (name == "all" || name == "snow") Emit("D32.light_sigma_snow", s.Precip);
                if (name == "all" || name == "cloud") Emit("D32.light_sigma_cloud", s.Cloud);
                if (name == "all" || name == "dust") Emit("D32.light_sigma_dust", s.Dust);
                Emit("D32.light_sigma_total", s.Total);
                Emit("D32.variant_draws", LampScatter.LastDrawCount);
                if (name == "all")
                {
                    Emit("D32.snow_draws", LampScatter.LastDrawCount);
                    Emit("D32.snow_buffers", LampScatter.BuffersAttached);
                    int before = LampScatter.DepthCopies;
                    yield return ProbeWait.Frames(30);
                    Emit("D32.snow_depth_copies", LampScatter.DepthCopies - before);
                    Emit("D32.mode_scene", LampScatter.ActiveMode == WeatherCompose.Scene);
                    Emit("D32.depth_copy_varies", LampScatter.Instance.ProbeDepthSpread());
                    Emit("D32.cpu_ms", LampScatter.CpuMs);
                }
                EndVariant();
            }

            // The cloud density the lamp would read in EVE's layers: eight directions round the vessel, at the nearest layer's mid shell.
            float best = 0f;
            Vector3d up = (vessel.GetWorldPos3D() - body.position).normalized;
            Vector3d north = body.transform.up;
            Vector3d east = Vector3d.Cross(north, up).normalized;
            double radius = (vessel.GetWorldPos3D() - body.position).magnitude;
            Emit("D32.eve_layers", LampScatter.EveLayers);
            for (int i = 0; i < 8; i++)
            {
                double a = i * Math.PI / 4.0;
                Vector3d dir = (up * Math.Cos(0.01 * (1 + i % 3)) + (east * Math.Cos(a) + north * Math.Sin(a)) * Math.Sin(0.01 * (1 + i % 3))).normalized;
                for (double h = 500.0; h <= 4000.0; h += 500.0)
                {
                    Vector3 at = (Vector3)(body.position + dir * (body.Radius + h));
                    float density = LampScatter.Instance.ProbeCloudDensity(at, body);
                    if (density > best) best = density;
                }
            }
            Emit("D32.cloud_density_max", best);
            Emit("D32.cloud_sigma_max", LampMath.CloudSigma(best, LampTuning.Defaults().CloudSigmaFull));
        }
    }
}
