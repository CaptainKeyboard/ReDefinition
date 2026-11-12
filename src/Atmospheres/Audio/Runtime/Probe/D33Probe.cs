using System;
using System.Collections.Generic;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres.Audio
{
    // The probe of package D33 inside the integrated series K-W3a (start with -atmoSeries K-W3a -atmoProbe D33Probe, in a flight).
    //
    // Reference expectation (written before the build, numbers from the offline renders of tests/ReDefinition.Tests/Atmospheres/D33):
    //   scenario        inputs (set by AudioOverrides, the other mods' sources muted by the solo switch)   expected at the listener
    //   calm cover      level 0, wind 0, cover 0.8                          quiet ambience, rms -62 to -38 dBFS
    //   light snowfall  level 0.25, wind 3, cover 0.5                       soft hiss, rms -58 to -42, centroid above 2 kHz
    //   blizzard        level 0.95, wind 22, gust wave +-1 over 9 s          rms -34 to -16, p90 minus p10 of the 100 ms windows at least 2 dB
    //   IVA             level 0.25, wind 4, cabin                            6 to 10 dB under the same scene outside, lower centroid
    //   map view        blizzard, map                                        10 to 20 dB under the same scene outside
    //   distant storm   edge distance 30, 20, 12, 6, 3 km, pan +0.8          rms silent at 30 km, rising at every step, left share under 0.2;
    //                                                                        pan -0.8 at 3 km: left share over 0.8
    //   EVA walk        simulated walker 2 m/s over cover 0.8, then 0       steps = metres / 0.7 within 10 %, none on bare ground, the high
    //                                                                        band 6 dB above the same scene standing
    //   warp            on-rails warp during the blizzard                    silent (under -80 dBFS) from 1.5 s, back within 3 dB 3.5 s after rate 1
    //   pause           the game's pause during the blizzard                 under -80 dBFS from 1.5 s
    //   master off      the switch off                                       no object; on again: a new one
    // Views: the natural view is the sound itself (WAV renders of the offline test, listened to by the owner, T5); the aid view is this table
    // with the band energies and the spectral centroid per section.
    //
    // Which rows read the game and which read a forced value:
    //   FORCED (AudioOverrides: inputs, listener, pause, warp, volumes, switches, walker, cover): rms_*, centroid_light_hz, param_*,
    //     iva_*, map_*, warp_*, pause_*, cell_*, sim_step_*, steps_*, eve_*. These rows test the gate, the mixer, the mapping and the
    //     stream with exact inputs; they pass whatever the game reports, so they are consistency rows, not proof of the game's state.
    //     peak_blizzard, clipped_samples and nan_samples measure the mixer's limiter, not the game.
    //   REAL (the module's own reads, section "real game state"): real_pause_* (FlightDriver.SetPause), real_warp_* and real_physwarp_*
    //     (TimeWarp on rails and physical, needs a landed vessel), real_iva_* (CameraManager.SetCameraIVA, needs crew), real_map_*
    //     (MapView), real_weather_* (the view of the frame driver), real_volume_* (GameSettings), real_cell_* (one forced ForecastTrack in
    //     WeatherHooks.BodyTracks, the pan from the listener transform), real_eva_* (a kerbal that walks; the probe does not drive one).
    // The preconditions of the real section are part of the scenario: a landed vessel with crew in the flight scene; a missing one is a
    // named FAIL, not a silent pass.
    [AtmospheresModule(986)]
    internal sealed class D33Probe : SeriesProbe
    {
        internal const string Series = "K-W3a";
        private const string Chatterer = "Chatterer/Sounds/AAE/wind/mario1298__weak-wind";

        private ProbeMeter meter;
        private AudioProbeTap tap;
        private MeterSummary last;
        private readonly List<float> windowDb = new List<float>();
        // What the real section has changed in the game and must put back, however the scenario ends.
        private bool pausedByProbe;
        private bool warpedByProbe;
        private bool ivaByProbe;
        private bool mapByProbe;
        private bool tracksForced;
        private Func<int, ForecastTrack[]> savedTracks;

        protected override string SeriesName
        {
            get { return Series; }
        }

        protected override double MaxSeconds
        {
            get { return 12 * 60; }
        }

        private static WeatherAudioInputs Weather(float level, float wind, float cover, PrecipType type = PrecipType.Snow)
        {
            return new WeatherAudioInputs { Air = true, Type = type, Level01 = level, MeanWindMs = wind, SnowCover01 = cover };
        }

        private static AudioSwitches Switches(bool master = true, bool distant = false, bool solo = true, EveSoundPolicy eve = EveSoundPolicy.Auto)
        {
            AudioSwitches sw = AudioSwitches.Defaults();
            sw.Master = master;
            sw.DistantStorm = distant;
            sw.Solo = solo;
            sw.Eve = eve;
            return sw;
        }

        // Waits for the settle time, then measures for the measure time; the result is in `last`.
        private IEnumerable<ProbeWait> Measure(double settleSeconds, double measureSeconds)
        {
            yield return ProbeWait.Seconds(settleSeconds);
            int from = meter.WindowCount;
            yield return ProbeWait.Seconds(measureSeconds);
            last = meter.Summarise(from, meter.WindowCount);
        }

        // The same with real seconds: a pause sets Time.timeScale to 0, and ProbeWait.Seconds counts the scaled frame time of the game.
        private IEnumerable<ProbeWait> RealMeasure(double settleSeconds, double measureSeconds)
        {
            double t0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t0 < settleSeconds) yield return ProbeWait.Frames(1);
            int from = meter.WindowCount;
            t0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t0 < measureSeconds) yield return ProbeWait.Frames(1);
            last = meter.Summarise(from, meter.WindowCount);
        }

        // After a camera change the active listener may be another object: the tap goes there.
        private void Retap(WeatherAudioModule module)
        {
            AudioListener now = module.RescanListener();
            if (now == null || meter == null) return;
            if (tap != null && tap.gameObject == now.gameObject) return;
            if (tap != null) UnityEngine.Object.Destroy(tap);
            tap = now.gameObject.AddComponent<AudioProbeTap>();
            tap.Meter = meter;
        }

        // Puts back whatever the real section changed in the game.
        private void RestoreGame()
        {
            try { if (pausedByProbe) FlightDriver.SetPause(false, false); } catch (Exception) { }
            pausedByProbe = false;
            try { if (warpedByProbe && TimeWarp.fetch != null) TimeWarp.SetRate(0, true, false); } catch (Exception) { }
            warpedByProbe = false;
            try { if (ivaByProbe && CameraManager.Instance != null) CameraManager.Instance.SetCameraFlight(); } catch (Exception) { }
            ivaByProbe = false;
            try { if (mapByProbe && MapView.MapIsEnabled) MapView.ExitMapView(); } catch (Exception) { }
            mapByProbe = false;
            if (tracksForced) WeatherHooks.BodyTracks = savedTracks;
            tracksForced = false;
        }

        // The same with a gust wave that is moved every frame; the 100 ms windows of the measure time go to windowDb.
        private IEnumerable<ProbeWait> MeasureGusting(double settleSeconds, double measureSeconds, float level, float wind, float cover)
        {
            double t0 = Time.realtimeSinceStartup;
            int from = -1;
            while (Time.realtimeSinceStartup - t0 < settleSeconds + measureSeconds)
            {
                double t = Time.realtimeSinceStartup - t0;
                WeatherAudioInputs w = Weather(level, wind, cover);
                w.GustWave = (float)Math.Sin(2.0 * Math.PI * t / 9.0);
                AudioOverrides.Inputs = w;
                if (from < 0 && t >= settleSeconds) from = meter.WindowCount;
                yield return ProbeWait.Frames(1);
            }
            last = meter.Summarise(from, meter.WindowCount);
            windowDb.Clear();
            for (int i = from; i < meter.WindowCount; i++) windowDb.Add((float)ProbeMeter.Db(meter.WindowEnergy(i)));
        }

        private static double Percentile(List<float> values, double p)
        {
            if (values.Count == 0) return 0.0;
            var copy = new List<float>(values);
            copy.Sort();
            double index = p * (copy.Count - 1);
            int lo = (int)Math.Floor(index);
            int hi = Math.Min(copy.Count - 1, lo + 1);
            return copy[lo] + (copy[hi] - copy[lo]) * (index - lo);
        }

        // A percentile of the 2 to 5 kHz energy of the windows from (inclusive) to to (exclusive).
        private double HighPercentile(int from, int to, double p)
        {
            var values = new List<float>();
            for (int i = Math.Max(0, from); i < to; i++) values.Add((float)meter.HighEnergy(i));
            return Percentile(values, p);
        }

        // The real game state: the pause, the warp, the camera modes, the weather read, the volume sliders. Only the inputs of the weather
        // (and, for the camera rows, the listener) stay forced where a row needs an exact scene; the gate, the camera rules and the
        // reads are the module's own. Needs a landed vessel with crew in the flight scene.
        private IEnumerable<ProbeWait> RealState(WeatherAudioModule module)
        {
            Vessel vessel = FlightGlobals.ActiveVessel;
            AudioOverrides.Switches = Switches();
            AudioOverrides.Inputs = Weather(0.95f, 22f, 0.5f);
            AudioOverrides.Listener = null;
            AudioOverrides.Paused = null;
            AudioOverrides.OnRailsWarp = null;
            AudioOverrides.Volumes = GameVolumes.Full();
            foreach (ProbeWait w in RealMeasure(3.0, 2.0)) yield return w;
            double rmsOpen = last.RmsDb;
            Emit("D33.real_idle_reads_open", module.Gate.Reason == GateReason.Open && !WeatherAudioModule.ReadPaused() && !WeatherAudioModule.ReadOnRailsWarp());

            // The game's own pause (FlightDriver.SetPause), read by the module.
            try
            {
                FlightDriver.SetPause(true, false);
                pausedByProbe = true;
            }
            catch (Exception e)
            {
                Check("the real pause could be set", false, e.GetType().Name + " " + e.Message);
            }
            foreach (ProbeWait w in RealMeasure(1.5, 2.0)) yield return w;
            Emit("D33.real_pause_read", WeatherAudioModule.ReadPaused());
            Emit("D33.real_pause_gate_paused", module.Gate.Reason == GateReason.Paused);
            Emit("D33.real_pause_silent_db", last.RmsDb);
            Emit("D33.real_pause_listener_flag", AudioListener.pause);
            Emit("D33.real_pause_time_scale", Time.timeScale);
            try
            {
                FlightDriver.SetPause(false, false);
            }
            catch (Exception e)
            {
                Check("the real pause could be lifted", false, e.GetType().Name + " " + e.Message);
            }
            pausedByProbe = false;
            foreach (ProbeWait w in RealMeasure(3.5, 2.0)) yield return w;
            Emit("D33.real_pause_return_delta_db", last.RmsDb - rmsOpen);

            // The game's own time warp: on rails (high mode) and physical (low mode), landed.
            bool landed = vessel != null && (vessel.Landed || vessel.Splashed) && TimeWarp.fetch != null;
            Emit("D33.real_warp_landed", landed);
            if (landed)
            {
                TimeWarp.Modes savedMode = TimeWarp.fetch.Mode;
                TimeWarp.fetch.Mode = TimeWarp.Modes.HIGH;
                TimeWarp.SetRate(3, true, false);
                warpedByProbe = true;
                yield return ProbeWait.Frames(10);
                Emit("D33.real_warp_rate", TimeWarp.CurrentRate);
                foreach (ProbeWait w in RealMeasure(1.5, 2.0)) yield return w;
                Emit("D33.real_warp_read", WeatherAudioModule.ReadOnRailsWarp());
                Emit("D33.real_warp_gate_warp", module.Gate.Reason == GateReason.Warp);
                Emit("D33.real_warp_silent_db", last.RmsDb);
                TimeWarp.SetRate(0, true, false);
                TimeWarp.fetch.Mode = savedMode;
                warpedByProbe = false;
                foreach (ProbeWait w in RealMeasure(3.5, 2.0)) yield return w;
                Emit("D33.real_warp_return_delta_db", last.RmsDb - rmsOpen);

                // Physical warp is unchanged: the sound stays open.
                TimeWarp.fetch.Mode = TimeWarp.Modes.LOW;
                TimeWarp.SetRate(2, true, false);
                warpedByProbe = true;
                yield return ProbeWait.Frames(10);
                Emit("D33.real_physwarp_rate", TimeWarp.CurrentRate);
                foreach (ProbeWait w in RealMeasure(1.5, 2.0)) yield return w;
                Emit("D33.real_physwarp_gate_open", module.Gate.Reason == GateReason.Open);
                Emit("D33.real_physwarp_delta_db", last.RmsDb - rmsOpen);
                TimeWarp.SetRate(0, true, false);
                TimeWarp.fetch.Mode = savedMode;
                warpedByProbe = false;
                yield return ProbeWait.Frames(30);
            }

            // The map view with the game's own camera rule.
            bool mapEntered = false;
            try
            {
                if (!MapView.MapIsEnabled) MapView.EnterMapView();
                mapEntered = MapView.MapIsEnabled;
                mapByProbe = mapEntered;
            }
            catch (Exception e)
            {
                Check("the map view could be entered", false, e.GetType().Name + " " + e.Message);
            }
            Emit("D33.real_map_entered", mapEntered);
            if (mapEntered)
            {
                yield return ProbeWait.Frames(30);
                Retap(module);
                foreach (ProbeWait w in RealMeasure(3.0, 3.0)) yield return w;
                Emit("D33.real_map_flag", module.Listener.MapView);
                Emit("D33.real_map_delta_db", rmsOpen - last.RmsDb);
                try { MapView.ExitMapView(); } catch (Exception e) { Check("the map view could be left", false, e.GetType().Name + " " + e.Message); }
                mapByProbe = false;
                yield return ProbeWait.Frames(30);
                Retap(module);
            }

            // The IVA with the game's own camera rule (needs crew).
            AudioOverrides.Inputs = Weather(0.25f, 4f, 0.5f);
            foreach (ProbeWait w in RealMeasure(3.0, 3.0)) yield return w;
            double rmsOutside = last.RmsDb;
            double centroidOutside = last.CentroidHz;
            Emit("D33.real_outside_not_cabin", !module.Listener.Cabin && !module.Listener.MapView);
            bool iva = false;
            try
            {
                iva = CameraManager.Instance != null && CameraManager.Instance.SetCameraIVA();
                ivaByProbe = iva;
            }
            catch (Exception e)
            {
                LogLine("D33: the IVA could not be entered (" + e.GetType().Name + " " + e.Message + ")");
            }
            Emit("D33.real_iva_available", iva);
            if (iva)
            {
                yield return ProbeWait.Frames(30);
                Retap(module);
                foreach (ProbeWait w in RealMeasure(3.0, 3.0)) yield return w;
                Emit("D33.real_iva_cabin_flag", module.Listener.Cabin);
                Emit("D33.real_iva_delta_db", rmsOutside - last.RmsDb);
                Emit("D33.real_iva_centroid_drop_hz", centroidOutside - last.CentroidHz);
                CameraManager.Instance.SetCameraFlight();
                ivaByProbe = false;
                yield return ProbeWait.Frames(30);
                Retap(module);
            }

            // The weather read of the frame driver, with the inputs not forced.
            AudioOverrides.Inputs = null;
            foreach (ProbeWait w in RealMeasure(3.0, 1.0)) yield return w;
            WeatherAudioInputs read = module.LastInputs;
            WeatherView view = WeatherHooks.View;
            Emit("D33.real_weather_air", read.Air);
            Emit("D33.real_view_fresh", !view.IsStale(Time.frameCount));
            Emit("D33.real_level_delta", Math.Abs(read.Level01 - view.Level));

            // The volume sliders of the game.
            AudioOverrides.Volumes = null;
            foreach (ProbeWait w in RealMeasure(1.5, 0.5)) yield return w;
            Emit("D33.real_volume_ambience_error", Math.Abs(module.Volumes.Ambience - Mathf.Clamp01(GameSettings.AMBIENCE_VOLUME)));
            Emit("D33.real_volume_ship_error", Math.Abs(module.Volumes.Ship - Mathf.Clamp01(GameSettings.SHIP_VOLUME)));

            // Back to the forced scene for the rows that follow.
            AudioOverrides.Volumes = GameVolumes.Full();
            AudioOverrides.Inputs = Weather(0.95f, 22f, 0.5f);
            AudioOverrides.Listener = new ListenerState();
            AudioOverrides.Paused = false;
            AudioOverrides.OnRailsWarp = false;
        }

        // One forced forecast track 10 km to the right of the listener and one to the left: the module reads the track, the body and the
        // listener transform and finds the pan; the expectation is the construction (a point along the listener's right-hand axis).
        private IEnumerable<ProbeWait> RealCellState(WeatherAudioModule module)
        {
            Vessel vessel = FlightGlobals.ActiveVessel;
            CelestialBody body = vessel != null ? vessel.mainBody : null;
            AudioListener listener = module.RescanListener();
            Emit("D33.real_cell_listener", listener != null && body != null);
            if (listener == null || body == null) yield break;
            savedTracks = WeatherHooks.BodyTracks;
            tracksForced = true;
            AudioOverrides.Switches = Switches(distant: true);
            AudioOverrides.Inputs = Weather(0f, 0f, 0f);
            AudioOverrides.RealCell = true;
            int index = body.flightGlobalsIndex;
            for (int side = 1; side >= -1; side -= 2)
            {
                Transform t = listener.transform;
                Vector3d position = (Vector3d)t.position;
                Vector3d up = (position - body.position).normalized;
                Vector3d right = (Vector3d)t.right;
                right = (right - up * Vector3d.Dot(right, up)).normalized * side;
                Vector3d target = position + right * 10000.0;
                var track = new ForecastTrack
                {
                    CellKey = 33,
                    Ut = new[] { Planetarium.GetUniversalTime() },
                    Lat = new[] { body.GetLatitude(target) },
                    Lon = new[] { body.GetLongitude(target) },
                    RadiusM = new[] { 2000f },
                    Strength01 = new[] { 1f }
                };
                ForecastTrack[] only = { track };
                WeatherHooks.BodyTracks = i => i == index ? only : null;
                foreach (ProbeWait w in RealMeasure(5.0, 3.0)) yield return w;
                string tag = side > 0 ? "right" : "left";
                Emit("D33.real_cell_found_" + tag, module.Cell.Found);
                Emit("D33.real_cell_edge_m_" + tag, module.Cell.EdgeDistanceM);
                Emit("D33.real_cell_pan_error_" + tag, Math.Abs(module.CellPanNow - side));
                Emit("D33.real_cell_left_share_" + tag, last.LeftShare);
            }
            WeatherHooks.BodyTracks = savedTracks;
            tracksForced = false;
            AudioOverrides.RealCell = false;
            AudioOverrides.Switches = Switches();
            AudioOverrides.Inputs = Weather(0f, 0f, 0f);
        }

        // A kerbal that walks during the window (the probe does not drive one): the metres come from the change of latitude and longitude,
        // not from the integrator's own input, and the steps from the steps the module played.
        private IEnumerable<ProbeWait> RealEvaWalk(WeatherAudioModule module)
        {
            Vessel vessel = FlightGlobals.ActiveVessel;
            bool eva = vessel != null && vessel.isEVA;
            Emit("D33.real_eva_active", eva);
            if (!eva) yield break;
            AudioOverrides.ForcedCover = 0.8f;
            module.ResetStepCounters();
            double metres = 0.0;
            double lat = vessel.latitude, lon = vessel.longitude;
            double until = Time.realtimeSinceStartup + 15.0, nextSample = Time.realtimeSinceStartup + 0.25;
            while (Time.realtimeSinceStartup < until)
            {
                if (Time.realtimeSinceStartup >= nextSample)
                {
                    nextSample += 0.25;
                    double d = DistantCellModel.DistanceM(lat, lon, vessel.latitude, vessel.longitude, vessel.mainBody.Radius);
                    if (d > 0.02) metres += d;
                    lat = vessel.latitude;
                    lon = vessel.longitude;
                }
                yield return ProbeWait.Frames(1);
            }
            Emit("D33.real_eva_metres", metres);
            Emit("D33.real_eva_steps_played", module.StepsPlayed);
            Emit("D33.real_eva_step_ratio", metres >= 10.0 ? module.StepsPlayed / (metres / StrideIntegrator.WalkStrideM) : -1.0);
            AudioOverrides.ForcedCover = null;
        }

        protected override IEnumerator<ProbeWait> Scenario()
        {
            try
            {
                foreach (ProbeWait w in Body()) yield return w;
            }
            finally
            {
                // However the scenario ends, the game's own values are back.
                RestoreGame();
                AudioOverrides.Clear();
                if (tap != null) UnityEngine.Object.Destroy(tap);
                tap = null;
            }
        }

        private IEnumerable<ProbeWait> Body()
        {
            yield return ProbeWait.Until(() => HighLogic.LoadedScene == GameScenes.FLIGHT && FlightGlobals.ready && FlightGlobals.ActiveVessel != null,
                330, "the flight");
            if (HighLogic.LoadedScene != GameScenes.FLIGHT)
            {
                Fail("D33: no flight to measure in");
                yield break;
            }
            int gc0 = GC.CollectionCount(0);
            AudioOverrides.Clear();
            AudioOverrides.Volumes = GameVolumes.Full();
            AudioOverrides.Switches = Switches();
            AudioOverrides.Inputs = Weather(0f, 0f, 0f);
            AudioOverrides.Listener = new ListenerState();
            AudioOverrides.Paused = false;
            AudioOverrides.OnRailsWarp = false;
            yield return ProbeWait.Until(() => WeatherAudioModule.Instance != null && WeatherAudioModule.Instance.HasObjects, 60, "the weather sound objects");
            WeatherAudioModule module = WeatherAudioModule.Instance;
            if (module == null || !module.HasObjects)
            {
                Fail("D33: the weather sound made no objects in 60 s");
                yield break;
            }
            Emit("D33.objects_created", module.CreatedObjects);
            Emit("D33.output_rate_hz", module.Objects.SampleRate);
            Emit("D33.footstep_clips", module.FootstepClips);

            // The tap on the listener of the active camera.
            AudioListener listener = module.ListenerNow();
            Emit("D33.listener_found", listener != null);
            if (listener == null)
            {
                Fail("D33: no active AudioListener");
                yield break;
            }
            meter = new ProbeMeter(module.Objects.SampleRate, 6000);
            tap = listener.gameObject.AddComponent<AudioProbeTap>();
            tap.Meter = meter;
            yield return ProbeWait.Seconds(2.0);
            Emit("D33.meter_windows", meter.WindowCount);

            // 0 Calm over bare ground: nothing sounds (the quiet ambience needs snow cover, so digital silence is the right answer here).
            foreach (ProbeWait w in Measure(1.0, 2.0)) yield return w;
            Emit("D33.rms_bare_calm_db", last.RmsDb);

            // 1 Calm over deep snow cover: the quiet ambience (fades in over seconds; its gain is the snow cover times the calm factor).
            AudioOverrides.Inputs = Weather(0f, 0f, 1f);
            foreach (ProbeWait w in Measure(9.0, 3.0)) yield return w;
            double rmsCalm = last.RmsDb;
            Emit("D33.rms_calm_db", rmsCalm);

            // 2 Light snowfall.
            AudioOverrides.Inputs = Weather(0.25f, 3f, 0.5f);
            foreach (ProbeWait w in Measure(3.0, 3.0)) yield return w;
            double rmsLight = last.RmsDb;
            Emit("D33.rms_light_db", rmsLight);
            Emit("D33.centroid_light_hz", last.CentroidHz);
            Emit("D33.param_intensity_light", module.Shared.Params.intensity);

            // 3 Blizzard with gusts.
            foreach (ProbeWait w in MeasureGusting(3.0, 9.0, 0.95f, 22f, 0.5f)) yield return w;
            double rmsBlizzard = last.RmsDb;
            Emit("D33.rms_blizzard_db", rmsBlizzard);
            Emit("D33.blizzard_over_light_db", rmsBlizzard - rmsLight);
            Emit("D33.blizzard_over_calm_db", rmsBlizzard - rmsCalm);
            Emit("D33.gust_range_db", Percentile(windowDb, 0.9) - Percentile(windowDb, 0.1));
            Emit("D33.param_wind_blizzard", module.Shared.Params.windSpeed);
            Emit("D33.peak_blizzard", last.PeakAbs);
            if (rmsBlizzard < -80.0)
            {
                AtmospheresLog.Say("PROBE FAILED: silent in a blizzard (rms " + rmsBlizzard.ToString("F1") + " dBFS at level 0.95)");
                Check("blizzard is audible", false, "rms " + rmsBlizzard.ToString("F1"));
            }

            // 4 IVA against the same scene outside.
            AudioOverrides.Inputs = Weather(0.25f, 4f, 0.5f);
            foreach (ProbeWait w in Measure(3.0, 3.0)) yield return w;
            double rmsOutside = last.RmsDb;
            double centroidOutside = last.CentroidHz;
            AudioOverrides.Listener = new ListenerState { Cabin = true };
            foreach (ProbeWait w in Measure(3.0, 3.0)) yield return w;
            Emit("D33.iva_delta_db", rmsOutside - last.RmsDb);
            Emit("D33.iva_centroid_drop_hz", centroidOutside - last.CentroidHz);
            AudioOverrides.Listener = new ListenerState();

            // 5 Map view against the blizzard outside.
            AudioOverrides.Inputs = Weather(0.95f, 22f, 0.5f);
            foreach (ProbeWait w in Measure(3.0, 3.0)) yield return w;
            double rmsBlizzardOutside = last.RmsDb;
            AudioOverrides.Listener = new ListenerState { MapView = true };
            foreach (ProbeWait w in Measure(3.0, 3.0)) yield return w;
            Emit("D33.map_delta_db", rmsBlizzardOutside - last.RmsDb);
            AudioOverrides.Listener = new ListenerState();

            // 6 Warp: the blizzard goes silent, then returns.
            AudioOverrides.OnRailsWarp = true;
            foreach (ProbeWait w in Measure(1.5, 2.0)) yield return w;
            Emit("D33.warp_silent_db", last.RmsDb);
            Emit("D33.warp_gate_gain", module.Gate.Gain);
            AudioOverrides.OnRailsWarp = false;
            foreach (ProbeWait w in Measure(3.5, 2.0)) yield return w;
            Emit("D33.warp_return_delta_db", last.RmsDb - rmsBlizzardOutside);

            // 7 Pause.
            AudioOverrides.Paused = true;
            foreach (ProbeWait w in Measure(1.5, 2.0)) yield return w;
            Emit("D33.pause_silent_db", last.RmsDb);
            AudioOverrides.Paused = false;
            foreach (ProbeWait w in Measure(3.5, 2.0)) yield return w;
            Emit("D33.pause_return_delta_db", last.RmsDb - rmsBlizzardOutside);

            // 7b The real game state: pause, warp, map view, IVA, the weather read and the volume sliders.
            foreach (ProbeWait w in RealState(module)) yield return w;

            // 8 The distant storm: edge distance 30 km down to 3 km, to the right; then to the left.
            AudioOverrides.Switches = Switches(distant: true);
            double[] distances = { 30000, 20000, 12000, 6000, 3000 };
            double previous = double.NegativeInfinity;
            bool monotonic = true;
            double rightShare = 0.5;
            for (int i = 0; i < distances.Length; i++)
            {
                WeatherAudioInputs cellW = Weather(0f, 0f, 0f);
                cellW.CellCloseness = DistantCellModel.Closeness(distances[i]);
                cellW.CellPan = 0.8f;
                AudioOverrides.Inputs = cellW;
                foreach (ProbeWait w in Measure(4.0, 2.0)) yield return w;
                Emit("D33.rms_cell_" + (int)(distances[i] / 1000) + "km_db", last.RmsDb);
                if (i > 0 && !(last.RmsDb > previous + 1.0)) monotonic = false;
                if (i == 0) Emit("D33.cell_far_db", last.RmsDb);
                previous = last.RmsDb;
                rightShare = last.LeftShare;
            }
            Emit("D33.cell_monotonic", monotonic);
            Emit("D33.cell_left_share_right_source", rightShare);
            WeatherAudioInputs leftW = Weather(0f, 0f, 0f);
            leftW.CellCloseness = 1f;
            leftW.CellPan = -0.8f;
            AudioOverrides.Inputs = leftW;
            foreach (ProbeWait w in Measure(4.0, 2.0)) yield return w;
            Emit("D33.cell_left_share_left_source", last.LeftShare);
            AudioOverrides.Switches = Switches();

            // 8b The real cell chain: a forced track, the module's own pan from the listener transform.
            foreach (ProbeWait w in RealCellState(module)) yield return w;

            // 9 EVA walk: a simulated walker at 2 m/s over snow, standing, and over bare ground.
            AudioOverrides.Inputs = Weather(0f, 0f, 0f);
            AudioOverrides.ForcedCover = 0.8f;
            yield return ProbeWait.Seconds(4.0);
            int standFrom = meter.WindowCount;
            yield return ProbeWait.Seconds(3.0);
            double hiStanding = HighPercentile(standFrom, meter.WindowCount, 0.9);
            module.ResetStepCounters();
            AudioOverrides.WalkSpeedMs = 2.0f;
            AudioOverrides.Walking = true;
            int walkFrom = meter.WindowCount;
            yield return ProbeWait.Seconds(12.5);
            AudioOverrides.Walking = false;
            // The steps come every 0.35 s, so the loud 100 ms windows are about a third of them: the 90th percentile of the 2 to 5 kHz energy per
            // window is the burst level.
            double hiWalking = HighPercentile(walkFrom + 15, meter.WindowCount, 0.9);
            yield return ProbeWait.Seconds(1.5);
            double expected = module.DistanceWalkedM / StrideIntegrator.WalkStrideM;
            Emit("D33.walk_metres", module.DistanceWalkedM);
            Emit("D33.steps_found", module.StepsFound);
            Emit("D33.steps_played", module.StepsPlayed);
            // The metres are the integrator's own input (speed x frame time), so this ratio checks the integrator against it: a
            // consistency row. The real kerbal's metres come from the position (section 9b).
            Emit("D33.sim_step_count_ratio", expected > 0 ? module.StepsPlayed / expected : 0.0);
            Emit("D33.step_hi_ratio_db", ProbeMeter.Db(hiWalking) - ProbeMeter.Db(hiStanding));
            EmitText("D33.step_mixer_group", module.Objects.StepMixerGroup);
            // RSE: 0 absent, 1 present, routing node complete and a step source seen with an output mixer group, -1 present without the
            // complete routing node, -2 routed but no step source was ever given a group.
            Emit("D33.rse_state", !module.RsePresent ? 0 : !module.RseRoutesOurClips ? -1 : module.RseGroupEverSeen ? 1 : -2);
            module.ResetStepCounters();
            AudioOverrides.ForcedCover = 0.0f;
            yield return ProbeWait.Seconds(1.0);
            module.ResetStepCounters();
            AudioOverrides.Walking = true;
            yield return ProbeWait.Seconds(6.0);
            AudioOverrides.Walking = false;
            Emit("D33.steps_found_bare", module.StepsFound);
            Emit("D33.steps_played_bare", module.StepsPlayed);
            AudioOverrides.ForcedCover = null;

            // 9b A real kerbal on foot, if the active vessel is one.
            foreach (ProbeWait w in RealEvaWalk(module)) yield return w;
            AudioOverrides.Inputs = Weather(0f, 0f, 0f);

            // 10 EVE's own loop (informative: it needs a snow storm at the camera).
            EveSoundAccess.Scan(FlightGlobals.ActiveVessel.mainBody != null ? FlightGlobals.ActiveVessel.mainBody.name : "");
            Emit("D33.eve_loops", EveSoundAccess.Count);
            Emit("D33.eve_audible_volume", EveSoundAccess.AudibleVolume());
            if (EveSoundAccess.Count > 0)
            {
                AudioOverrides.Inputs = Weather(0.95f, 22f, 0.5f);
                double[] rms = new double[3];
                EveSoundPolicy[] policies = { EveSoundPolicy.Auto, EveSoundPolicy.Ours, EveSoundPolicy.Eve };
                for (int i = 0; i < policies.Length; i++)
                {
                    AudioOverrides.Switches = Switches(solo: false, eve: policies[i]);
                    foreach (ProbeWait w in Measure(4.0, 3.0)) yield return w;
                    rms[i] = last.RmsDb;
                    Emit("D33.eve_rms_" + policies[i].ToString().ToLowerInvariant() + "_db", last.RmsDb);
                    if (policies[i] == EveSoundPolicy.Ours) Emit("D33.eve_ours_volume", EveSoundAccess.VolumeNow());
                }
                Emit("D33.eve_sum_vs_larger_db", rms[0] - Math.Max(rms[1], rms[2]));
                AudioOverrides.Switches = Switches();
            }

            // 11 The switch off: no object; on again: a new one.
            int created = module.CreatedObjects;
            AudioOverrides.Switches = Switches(master: false);
            yield return ProbeWait.Seconds(2.0);
            Emit("D33.master_off_objects", (module.HasObjects ? 1 : 0) + (GameObject.Find(WeatherAudioObjects.ObjectName) != null ? 1 : 0));
            AudioOverrides.Switches = Switches();
            yield return ProbeWait.Until(() => module.HasObjects, 20, "the objects after the switch is on again");
            Emit("D33.master_on_again_objects", module.CreatedObjects - created);

            // 12 Cost and health of the audio thread, and the verdicts that need no scenario.
            SharedAudioState s = module.Shared;
            double blockMs = WeatherAudioStream.MeanBlockMs(s);
            double blockDurationMs = s.LastFrames > 0 ? 1000.0 * s.LastFrames / module.Objects.SampleRate : 0.0;
            AtmospheresLog.Say("weather-audio: cpu=" + blockMs.ToString("F4") + " blocks=" + s.Blocks + " underruns=" + s.Underruns);
            Emit("D33.cpu_ms_per_block", blockMs);
            Emit("D33.block_ms", blockDurationMs);
            // The game runs Mono; this is measured there. The 1 % budget is the Release build's; a Debug build costs several times as
            // much and is held to the plan's fail line (5 %). The gated row is the used share of the limit of the build that runs.
            double cpuPct = blockDurationMs > 0 ? 100.0 * blockMs / blockDurationMs : 0.0;
            bool release = CpuGate.IsReleaseBuild;
            Emit("D33.build_release", release);
            Emit("D33.cpu_limit_pct", CpuGate.LimitPct(release));
            Emit("D33.cpu_pct_core", cpuPct);
            Emit("D33.cpu_budget_used", blockDurationMs > 0 ? CpuGate.BudgetUsed(cpuPct, release) : 99.0);
            Emit("D33.blocks", s.Blocks);
            Emit("D33.underruns", s.Underruns);
            Emit("D33.nan_samples", meter.NonFiniteSamples);
            Emit("D33.clipped_samples", meter.ClippedSamples);
            Emit("D33.gc_gen0_collections", GC.CollectionCount(0) - gc0);
            string reason = meter.FailureReason(false, 0.0);
            if (reason != null)
            {
                AtmospheresLog.Say("PROBE FAILED: " + reason);
                Check("the mix is finite and unclipped", false, reason);
            }

            // Interaction rows (informative).
            Emit("D33.rse_present", module.RsePresent);
            Emit("D33.rse_routes_our_clips", module.RseRoutesOurClips);
            Emit("D33.chatterer_wind_clip_present", GameDatabase.Instance != null && GameDatabase.Instance.ExistsAudioClip(Chatterer));
            Emit("D33.frame_generation_on", WeatherHooks.Caps.FrameGenerationOn);
            Emit("D33.solo_was_on", true);
        }
    }
}
