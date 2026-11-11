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
    // The inputs are forced (AudioOverrides) so that every row is exact; the module's read of the view, the field and the cell tracks is
    // covered by the offline tests and by the informative rows at the end. The kerbal's own walk (KerbalEVA) is not driven by this probe.
    [AtmospheresModule(986)]
    internal sealed class D33Probe : SeriesProbe
    {
        internal const string Series = "K-W3a";
        private const string Chatterer = "Chatterer/Sounds/AAE/wind/mario1298__weak-wind";

        private ProbeMeter meter;
        private AudioProbeTap tap;
        private MeterSummary last;
        private readonly List<float> windowDb = new List<float>();

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

        protected override IEnumerator<ProbeWait> Scenario()
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
            AudioOverrides.Switches = Switches();
            AudioOverrides.Inputs = Weather(0f, 0f, 0f);
            AudioOverrides.Listener = new ListenerState();
            AudioOverrides.Paused = false;
            AudioOverrides.OnRailsWarp = false;
            yield return ProbeWait.Until(() => WeatherAudioModule.Instance != null && WeatherAudioModule.Instance.HasObjects, 60, "the weather sound objects");
            WeatherAudioModule module = WeatherAudioModule.Instance;
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

            // 1 Calm snow cover: the quiet ambience (fades in over seconds).
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

            // 9 EVA walk: a simulated walker at 2 m/s over snow, standing, and over bare ground.
            AudioOverrides.Inputs = Weather(0f, 0f, 0f);
            AudioOverrides.ForcedCover = 0.8f;
            foreach (ProbeWait w in Measure(4.0, 3.0)) yield return w;
            double hiStanding = last.Hi;
            module.ResetStepCounters();
            AudioOverrides.WalkSpeedMs = 2.0f;
            AudioOverrides.Walking = true;
            int walkFrom = meter.WindowCount;
            yield return ProbeWait.Seconds(12.5);
            AudioOverrides.Walking = false;
            MeterSummary walking = meter.Summarise(walkFrom + 5, meter.WindowCount);
            yield return ProbeWait.Seconds(1.5);
            double expected = module.DistanceWalkedM / StrideIntegrator.WalkStrideM;
            Emit("D33.walk_metres", module.DistanceWalkedM);
            Emit("D33.steps_found", module.StepsFound);
            Emit("D33.steps_played", module.StepsPlayed);
            Emit("D33.step_count_ratio", expected > 0 ? module.StepsPlayed / expected : 0.0);
            Emit("D33.step_hi_ratio_db", ProbeMeter.Db(walking.Hi) - ProbeMeter.Db(hiStanding));
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
            Emit("D33.cpu_pct_core", blockDurationMs > 0 ? 100.0 * blockMs / blockDurationMs : 0.0);
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

            AudioOverrides.Clear();
            if (tap != null) UnityEngine.Object.Destroy(tap);
        }
    }
}
