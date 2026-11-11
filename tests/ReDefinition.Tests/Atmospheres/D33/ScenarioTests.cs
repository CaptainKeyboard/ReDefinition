using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Atmospheres.Audio;
using ReDefinition.Core;

namespace ReDefinition.Tests.Atmospheres.D33
{
    // The reference numbers of the K-W3a rows of package D33, rendered offline through the same callback the game uses (mapper, mixer, output
    // stage, probe meter). The rows in tests/series/rows/D33.json quote these numbers; a game start has to meet the same bounds.
    [TestClass]
    public class ScenarioTests
    {
        private const int Rate = 48000;

        internal static WeatherAudioInputs Weather(float level, float wind, float cover, float close = 0f, float pan = 0f, PrecipType type = PrecipType.Snow)
        {
            return new WeatherAudioInputs { Air = true, Type = type, Level01 = level, MeanWindMs = wind, SnowCover01 = cover, CellCloseness = close, CellPan = pan };
        }

        // Renders `seconds` and summarises from `measureFrom`. A gust wave of period 9 s is applied when gust is set.
        internal static MeterSummary Run(AudioSwitches sw, WeatherAudioInputs w, ListenerState l, double seconds, double measureFrom, bool gust = false,
            List<float> windowDb = null)
        {
            var shared = new SharedAudioState();
            var stream = new WeatherAudioStream(shared, new Mixer(Rate, 1234u), Rate);
            var meter = new ProbeMeter(Rate, 6000);
            AudioParamsMapper.Apply(w, l, sw, GameVolumes.Full(), EveSoundPolicyTable.Decide(sw.Eve, false), 1f, ref shared.Params);
            shared.MapBlend = l.MapView ? 1f : 0f;
            shared.HelmetBlend = l.Eva && !l.MapView ? 1f : 0f;
            var buf = new float[2048];
            long frames = (long)(seconds * Rate);
            long done = 0;
            int from = -1;
            while (done < frames)
            {
                if (gust) shared.Params.gust = (float)Math.Sin(2 * Math.PI * done / Rate / 9.0);
                stream.Fill(buf, 2);
                meter.Feed(buf, 2);
                done += 1024;
                if (from < 0 && done / (double)Rate >= measureFrom) from = meter.WindowCount;
            }
            if (windowDb != null)
            {
                for (int i = from; i < meter.WindowCount; i++) windowDb.Add((float)ProbeMeter.Db(meter.WindowEnergy(i)));
            }
            return meter.Summarise(from, meter.WindowCount);
        }

        private static readonly ListenerState Outside = new ListenerState();
        private static readonly ListenerState Cabin = new ListenerState { Cabin = true };
        private static readonly ListenerState MapView = new ListenerState { MapView = true };

        [TestMethod]
        public void CalmSnowCoverIsTheQuietAmbience()
        {
            MeterSummary s = Run(AudioSwitches.Defaults(), Weather(0f, 0f, 0.8f), Outside, 14, 9);
            Assert.IsTrue(s.RmsDb > -62 && s.RmsDb < -38, "calm rms " + s.RmsDb);
            Assert.IsTrue(s.PeakAbs < 0.05, "calm peak " + s.PeakAbs);
        }

        [TestMethod]
        public void TheScaleIsAudibleFromCalmToBlizzard()
        {
            AudioSwitches sw = AudioSwitches.Defaults();
            MeterSummary calm = Run(sw, Weather(0f, 0f, 0.8f), Outside, 14, 9);
            MeterSummary light = Run(sw, Weather(0.25f, 3f, 0.5f), Outside, 8, 4);
            MeterSummary moderate = Run(sw, Weather(0.5f, 12f, 0.5f), Outside, 8, 4);
            MeterSummary blizzard = Run(sw, Weather(0.95f, 22f, 0.5f), Outside, 8, 4);
            Assert.IsTrue(light.RmsDb > -58 && light.RmsDb < -42, "light " + light.RmsDb);
            Assert.IsTrue(light.CentroidHz > 2000, "the hiss is bright: " + light.CentroidHz);
            Assert.IsTrue(blizzard.RmsDb > -34 && blizzard.RmsDb < -16, "blizzard " + blizzard.RmsDb);
            Assert.IsTrue(moderate.RmsDb > light.RmsDb + 5 && blizzard.RmsDb > moderate.RmsDb + 5, "light " + light.RmsDb + " moderate " + moderate.RmsDb + " blizzard " + blizzard.RmsDb);
            Assert.IsTrue(blizzard.RmsDb - light.RmsDb >= 10 && blizzard.RmsDb - calm.RmsDb >= 15);
            Assert.IsTrue(blizzard.PeakAbs <= Mixer.Ceiling + 1e-3);
        }

        [TestMethod]
        public void TheWindRisesAndFallsWithTheGusts()
        {
            var db = new List<float>();
            Run(AudioSwitches.Defaults(), Weather(0.95f, 22f, 0.5f), Outside, 14, 5, gust: true, windowDb: db);
            db.Sort();
            double range = db[(int)(0.9 * (db.Count - 1))] - db[(int)(0.1 * (db.Count - 1))];
            Assert.IsTrue(range >= 2.0, "gust range " + range);
        }

        [TestMethod]
        public void InsideACabinTheSoundIsSixToTenDecibelsLowerAndDuller()
        {
            AudioSwitches sw = AudioSwitches.Defaults();
            WeatherAudioInputs w = Weather(0.25f, 4f, 0.5f);
            MeterSummary outside = Run(sw, w, Outside, 8, 4);
            MeterSummary inside = Run(sw, w, Cabin, 8, 4);
            double delta = outside.RmsDb - inside.RmsDb;
            Assert.IsTrue(delta >= 6 && delta <= 10, "IVA delta " + delta);
            Assert.IsTrue(inside.CentroidHz < outside.CentroidHz - 1, outside.CentroidHz + " -> " + inside.CentroidHz);
        }

        [TestMethod]
        public void TheMapViewIsTenToTwentyDecibelsUnderTheOutside()
        {
            AudioSwitches sw = AudioSwitches.Defaults();
            WeatherAudioInputs w = Weather(0.95f, 22f, 0.5f);
            MeterSummary outside = Run(sw, w, Outside, 8, 4);
            MeterSummary map = Run(sw, w, MapView, 8, 4);
            double delta = outside.RmsDb - map.RmsDb;
            Assert.IsTrue(delta >= 10 && delta <= 20, "map delta " + delta);
        }

        [TestMethod]
        public void TheDistantStormRisesAsItComesCloserAndComesFromItsSide()
        {
            AudioSwitches sw = AudioSwitches.Defaults();
            sw.DistantStorm = true;
            double previous = double.NegativeInfinity;
            double[] distances = { 30000, 20000, 12000, 6000, 3000 };
            for (int i = 0; i < distances.Length; i++)
            {
                MeterSummary s = Run(sw, Weather(0f, 0f, 0f, DistantCellModel.Closeness(distances[i]), 0.8f), Outside, 8, 5);
                if (i == 0) Assert.IsTrue(s.RmsDb < -90, "silent at 30 km: " + s.RmsDb);
                else
                {
                    Assert.IsTrue(s.RmsDb > previous + 1.0, distances[i] + " m: " + s.RmsDb + " after " + previous);
                    Assert.IsTrue(s.LeftShare < 0.2, "a source on the right: left share " + s.LeftShare);
                }
                previous = s.RmsDb;
            }
            MeterSummary left = Run(sw, Weather(0f, 0f, 0f, 1f, -0.8f), Outside, 8, 5);
            Assert.IsTrue(left.LeftShare > 0.8, "a source on the left: left share " + left.LeftShare);
        }

        [TestMethod]
        public void TheDistantStormSwitchOffSilencesTheRumble()
        {
            AudioSwitches sw = AudioSwitches.Defaults();
            sw.DistantStorm = false;
            MeterSummary s = Run(sw, Weather(0f, 0f, 0f, 1f, 0.5f), Outside, 8, 5);
            Assert.IsTrue(s.RmsDb < -90, "rumble with the switch off: " + s.RmsDb);
        }

        [TestMethod]
        public void SandAndDustGratInsteadOfHiss()
        {
            AudioSwitches sw = AudioSwitches.Defaults();
            MeterSummary snow = Run(sw, Weather(0.6f, 6f, 0f, type: PrecipType.Snow), Outside, 8, 4);
            MeterSummary sand = Run(sw, Weather(0.6f, 6f, 0f, type: PrecipType.Sand), Outside, 8, 4);
            Assert.IsTrue(sand.RmsDb > -80 && Math.Abs(sand.CentroidHz - snow.CentroidHz) > 50, "snow " + snow.CentroidHz + " sand " + sand.CentroidHz);
        }

        [TestMethod]
        public void RainIsNotOurs()
        {
            MeterSummary rain = Run(AudioSwitches.Defaults(), Weather(0.8f, 0f, 0f, type: PrecipType.Rain), Outside, 8, 4);
            Assert.IsTrue(rain.RmsDb < -50, "rain: " + rain.RmsDb);
        }

        [TestMethod]
        public void NoAirIsSilent()
        {
            WeatherAudioInputs w = Weather(0.95f, 22f, 0.8f);
            w.Air = false;
            MeterSummary s = Run(AudioSwitches.Defaults(), w, Outside, 8, 4);
            Assert.IsTrue(s.RmsDb < -90, "vacuum: " + s.RmsDb);
        }

        [TestMethod]
        public void TheGateSilencesTheStreamInAWarpAndBringsItBack()
        {
            var shared = new SharedAudioState();
            var stream = new WeatherAudioStream(shared, new Mixer(Rate, 7u), Rate);
            var meter = new ProbeMeter(Rate, 6000);
            var gate = new AudioGate();
            gate.Arrive();
            AudioSwitches sw = AudioSwitches.Defaults();
            WeatherAudioInputs w = Weather(0.95f, 22f, 0.5f);
            var buf = new float[2048];
            Func<double, bool, bool, MeterSummary> play = (seconds, warp, pauseOn) =>
            {
                long frames = (long)(seconds * Rate);
                int from = meter.WindowCount;
                for (long done = 0; done < frames; done += 1024)
                {
                    // One main-thread frame per 1024 samples.
                    float g = gate.Step(1024f / Rate, true, pauseOn, warp);
                    AudioParamsMapper.Apply(w, Outside, sw, GameVolumes.Full(), EveSoundPolicyTable.Decide(sw.Eve, false), g, ref shared.Params);
                    stream.Fill(buf, 2);
                    meter.Feed(buf, 2);
                }
                return meter.Summarise(from, meter.WindowCount);
            };
            play(4.0, false, false);
            MeterSummary steady = play(2.0, false, false);
            play(1.5, true, false);
            MeterSummary warped = play(2.0, true, false);
            Assert.IsTrue(warped.RmsDb < -80, "warp: " + warped.RmsDb);
            play(3.5, false, false);
            MeterSummary back = play(2.0, false, false);
            Assert.IsTrue(Math.Abs(back.RmsDb - steady.RmsDb) < 3, "back " + back.RmsDb + " steady " + steady.RmsDb);
            play(1.5, false, true);
            MeterSummary paused = play(2.0, false, true);
            Assert.IsTrue(paused.RmsDb < -80, "pause: " + paused.RmsDb);
        }

        [TestMethod]
        public void ThePrecipitationLevelRaisesTheHighBand()
        {
            // The sampled steps are Unity clips; offline only the mixer's own layers are rendered. Ticks at the hull rise with the level.
            AudioSwitches sw = AudioSwitches.Defaults();
            MeterSummary few = Run(sw, Weather(0.1f, 0f, 0f), Outside, 8, 4);
            MeterSummary many = Run(sw, Weather(0.9f, 0f, 0f), Outside, 8, 4);
            Assert.IsTrue(many.Hi > few.Hi * 4.0, "high band " + few.Hi + " -> " + many.Hi);
        }
    }
}
