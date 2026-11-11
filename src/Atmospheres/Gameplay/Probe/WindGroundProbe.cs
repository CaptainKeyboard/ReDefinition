using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // The probe of package D31 inside the integrated series K-W3a, start "windground": the wind on craft that stand on the runway. The
    // series flies the pad vessel of the probe save first; the probe then launches two stock craft from the runway in turn.
    //
    // Reference expectations (written before the code):
    //  - Runway aircraft, brakes on, a forced wind of 15 m/s (level 0.8): the vessel is landed, so the wind is the ground share of it
    //    (the body's groundFactor, 0.1): the horizontal part of the root part's air-relative velocity (dragVector) rises by 1.5 m/s, within
    //    15 %. The aircraft does not move (under 0.5 m in 20 s): wheels and friction are untouched.
    //  - Parked rover, brakes on: below the friction threshold (15 m/s, ground share 0.1) it stays within 0.5 m in 30 s; above it (ground
    //    factor 1 by the probe's seam, windStrength 2, a forced 900 m/s that the cap holds at 50 m/s) the wind is strong on the ground:
    //    the displacement is recorded and is not smaller than the one below (informative: a heavy rover may hold even then).
    // The runway rows are the part of the acceptance that the pad and the canopy drop cannot cover; the numbers are the evidence.
    [AtmospheresModule(988)]
    internal sealed class WindGroundProbe : SeriesProbe
    {
        protected override string SeriesName
        {
            get { return WindProbe.Series; }
        }

        protected override double MaxSeconds
        {
            get { return 14 * 60; }
        }

        private ForcedWindField forced;
        private IWeatherField original;

        private static string Num(double v)
        {
            return v.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static double Ut()
        {
            return Planetarium.GetUniversalTime();
        }

        private void Say(string message)
        {
            LogLine("Probe: D31: " + message);
        }

        protected override IEnumerator<ProbeWait> Scenario()
        {
            yield return ProbeWait.Until(() => HighLogic.LoadedScene == GameScenes.FLIGHT && FlightGlobals.ready && GameGlobals.ActiveVessel != null, 330, "the flight");
            if (HighLogic.LoadedScene != GameScenes.FLIGHT)
            {
                Fail("D31: no flight to measure in");
                yield break;
            }
            SetSetting(AtmospheresSettings.Keys.Enabled, 1.0);
            SetSetting(AtmospheresSettings.Keys.WeatherEffects, 1.0);
            SetSetting(AtmospheresSettings.Keys.WindForces, 1.0);
            SetSetting(AtmospheresSettings.Keys.WindStrength, 1.0);
            SetSetting(AtmospheresSettings.Keys.GustStrength, 0.0);
            SetSetting(AtmospheresSettings.Keys.WindOnLanded, 1.0);
            SetSetting(AtmospheresSettings.Keys.WindSource, (double)(int)WindSourceMode.Auto);
            SetSetting(AtmospheresSettings.Keys.WindInjection, (double)(int)WindInjection.Auto);
            original = WeatherHub.Field;
            forced = new ForcedWindField(original) { Level01 = 0.8f };
            WeatherHub.Field = forced;

            foreach (ProbeWait w in Aircraft()) yield return w;
            foreach (ProbeWait w in Rover()) yield return w;

            WindService.GroundFactorOverride = null;
            if (ReferenceEquals(WeatherHub.Field, forced)) WeatherHub.Field = original;
        }

        private IEnumerable<ProbeWait> Launch(string craft)
        {
            string path = Path.Combine(Path.Combine(Path.Combine(KSPUtil.ApplicationRootPath, "Ships"), "SPH"), craft + ".craft");
            if (!File.Exists(path))
            {
                Fail("D31: the craft file " + path + " does not exist");
                yield break;
            }
            Say("launching " + craft + " from the runway");
            FlightDriver.StartWithNewLaunch(path, "Squad/Flags/default", "Runway", new VesselCrewManifest());
            yield return ProbeWait.Seconds(5);
            yield return ProbeWait.Until(() => HighLogic.LoadedScene == GameScenes.FLIGHT && FlightGlobals.ready && Ready(craft), 240, "the " + craft + " on the runway");
            if (!Ready(craft)) Fail("D31: " + craft + " did not come up on the runway");
        }

        private static bool Ready(string craft)
        {
            Vessel v = GameGlobals.ActiveVessel;
            return v != null && v.loaded && !v.packed && v.vesselName == craft;
        }

        private static double HorizontalDrag(Vessel v, Part part)
        {
            Vector3 up = ((Vector3)(v.CoMD - v.mainBody.position)).normalized;
            return Vector3.ProjectOnPlane(part.dragVector, up).magnitude;
        }

        private IEnumerable<ProbeWait> Settle(Vessel v)
        {
            v.ActionGroups.SetGroup(KSPActionGroup.Brakes, true);
            yield return ProbeWait.Until(() => v.Landed && v.srfSpeed < 0.3, 90, "the vessel at rest on the runway");
            Emit("D31." + v.vesselName.Replace(" ", "_") + "_at_rest", v.Landed && v.srfSpeed < 0.3);
            yield return ProbeWait.Seconds(3);
        }

        private IEnumerable<ProbeWait> Aircraft()
        {
            const string Craft = "Aeris 4A";
            foreach (ProbeWait w in Launch(Craft)) yield return w;
            if (Finished) yield break;
            Vessel v = GameGlobals.ActiveVessel;
            foreach (ProbeWait w in Settle(v)) yield return w;
            Part root = v.rootPart;

            forced.EastMs = forced.NorthMs = 0f;
            yield return ProbeWait.Seconds(6);
            double control = double.NaN;
            foreach (ProbeWait w in Mean(v, root, 6.0, m => control = m)) yield return w;
            Emit("D31.runway_control_horizontal_ms", control);

            double lat0 = v.latitude, lon0 = v.longitude;
            forced.EastMs = 15f;
            forced.NorthMs = 0f;
            yield return ProbeWait.Seconds(8);
            double windy = double.NaN;
            foreach (ProbeWait w in Mean(v, root, 8.0, m => windy = m)) yield return w;
            BodyClimate climate = Bodies.Get(v.mainBody);
            double expected = 15.0 * (climate != null ? climate.Wind.GroundFactor : 0.1);
            Emit("D31.runway_wind_horizontal_ms", windy);
            Emit("D31.runway_expected_rise_ms", expected);
            Emit("D31.runway_rise_error_rel", expected > 0.0 ? Math.Abs((windy - control) - expected) / expected : double.NaN);
            double e, n;
            Displacement(v.mainBody, lat0, lon0, v.latitude, v.longitude, v.altitude, out e, out n);
            Emit("D31.runway_moved_m", Math.Sqrt(e * e + n * n));
            forced.EastMs = 0f;
        }

        // The mean horizontal air-relative speed of a part over that many seconds of game time, sampled every frame.
        private IEnumerable<ProbeWait> Mean(Vessel v, Part part, double seconds, Action<double> result)
        {
            double sum = 0.0;
            int count = 0;
            double end = Ut() + seconds;
            double realEnd = Time.realtimeSinceStartup + 3.0 * seconds + 10.0;
            while (Ut() < end && Time.realtimeSinceStartup < realEnd)
            {
                sum += HorizontalDrag(v, part);
                count++;
                yield return ProbeWait.Frames(1);
            }
            result(count > 0 ? sum / count : double.NaN);
        }

        private IEnumerable<ProbeWait> Rover()
        {
            const string Craft = "Bug-E Buggy";
            foreach (ProbeWait w in Launch(Craft)) yield return w;
            if (Finished) yield break;
            Vessel v = GameGlobals.ActiveVessel;
            foreach (ProbeWait w in Settle(v)) yield return w;

            // Below the friction threshold: the default ground share.
            forced.EastMs = 15f;
            forced.NorthMs = 0f;
            double lat0 = v.latitude, lon0 = v.longitude;
            double end = Ut() + 30.0;
            while (Ut() < end) yield return ProbeWait.Frames(1);
            double e, n;
            Displacement(v.mainBody, lat0, lon0, v.latitude, v.longitude, v.altitude, out e, out n);
            double below = Math.Sqrt(e * e + n * n);
            Emit("D31.rover_below_moved_m", below);
            Emit("D31.rover_below_applied_ms", WindService.LastApplied.Length);

            // Above: the ground factor 1 by the seam, the strongest strength, a forced wind the cap holds at 50 m/s.
            forced.EastMs = 0f;
            yield return ProbeWait.Seconds(6);
            WindService.GroundFactorOverride = 1.0;
            SetSetting(AtmospheresSettings.Keys.WindStrength, 2.0);
            forced.EastMs = 900f;
            lat0 = v.latitude;
            lon0 = v.longitude;
            end = Ut() + 30.0;
            double maxApplied = 0.0;
            while (Ut() < end)
            {
                maxApplied = Math.Max(maxApplied, WindService.LastApplied.HorizontalLength);
                yield return ProbeWait.Frames(1);
            }
            Displacement(v.mainBody, lat0, lon0, v.latitude, v.longitude, v.altitude, out e, out n);
            double above = Math.Sqrt(e * e + n * n);
            Emit("D31.rover_above_moved_m", above);
            Emit("D31.rover_above_applied_max_ms", maxApplied);
            Emit("D31.rover_above_not_less_than_below", above >= below);
            Emit("D31.rover_finite", !double.IsNaN(above) && !double.IsNaN(below));
            WindService.GroundFactorOverride = null;
            SetSetting(AtmospheresSettings.Keys.WindStrength, 1.0);
            forced.EastMs = 0f;
        }

        private static void Displacement(CelestialBody body, double lat0, double lon0, double lat1, double lon1, double altitude, out double east, out double north)
        {
            double radius = body.Radius + altitude;
            double dLon = lon1 - lon0;
            while (dLon > 180.0) dLon -= 360.0;
            while (dLon < -180.0) dLon += 360.0;
            north = (lat1 - lat0) * Math.PI / 180.0 * radius;
            east = dLon * Math.PI / 180.0 * radius * Math.Cos(0.5 * (lat0 + lat1) * Math.PI / 180.0);
        }
    }
}
