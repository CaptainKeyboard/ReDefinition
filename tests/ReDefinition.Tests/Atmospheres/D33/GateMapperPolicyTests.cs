using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Atmospheres.Audio;
using ReDefinition.Core;

namespace ReDefinition.Tests.Atmospheres.D33
{
    // The state machines and tables of the weather sound: the gate (arrival, warp, pause), the hand-off of parameters, the muffling table and the EVE
    // policy table.
    [TestClass]
    public class GateMapperPolicyTests
    {
        private static float Run(AudioGate gate, double seconds, bool enabled, bool paused, bool warp, float frame = 1f / 60f)
        {
            float g = gate.Gain;
            for (double t = 0; t < seconds - 1e-9; t += frame) g = gate.Step(frame, enabled, paused, warp);
            return g;
        }

        [TestMethod]
        public void TheGateFadesInOverOneSecondOnArrival()
        {
            var gate = new AudioGate();
            gate.Arrive();
            Assert.AreEqual(0f, gate.Gain);
            float half = Run(gate, 0.5, true, false, false);
            Assert.AreEqual(0.5f, half, 0.03f);
            Assert.AreEqual(1f, Run(gate, 0.6, true, false, false), 1e-4f);
            Assert.AreEqual(GateReason.Open, gate.Reason);
        }

        [TestMethod]
        public void AnOnRailsWarpFadesOutInHalfASecondAndStaysSilentUntilRateOne()
        {
            var gate = new AudioGate();
            gate.Arrive();
            Run(gate, 2, true, false, false);
            Assert.AreEqual(1f, gate.Gain, 1e-4f);
            Assert.AreEqual(0.5f, Run(gate, 0.25, true, false, true), 0.05f);
            Assert.AreEqual(0f, Run(gate, 0.3, true, false, true), 1e-4f);
            Assert.AreEqual(0f, Run(gate, 60, true, false, true), 0f, "silent as long as the warp lasts");
            Assert.AreEqual(GateReason.Warp, gate.Reason);
        }

        [TestMethod]
        public void RateOneFadesInOverOneAndAHalfSeconds()
        {
            var gate = new AudioGate();
            gate.Arrive();
            Run(gate, 2, true, false, false);
            Run(gate, 2, true, false, true);
            Assert.AreEqual(0.5f, Run(gate, 0.75, true, false, false), 0.03f);
            Assert.AreEqual(1f, Run(gate, 0.8, true, false, false), 1e-4f);
        }

        [TestMethod]
        public void ThePauseCutsFastAndReturnsInATenthOfASecondsMore()
        {
            var gate = new AudioGate();
            gate.Arrive();
            Run(gate, 2, true, false, false);
            Assert.AreEqual(0f, Run(gate, 0.15, true, true, false), 1e-4f);
            Assert.AreEqual(0.5f, Run(gate, 0.15, true, false, false), 0.06f);
            Assert.AreEqual(1f, Run(gate, 0.3, true, false, false), 1e-4f);
        }

        [TestMethod]
        public void WithoutAirTheGateIsClosedAndAHitchDoesNotSkipAFade()
        {
            var gate = new AudioGate();
            gate.Arrive();
            Run(gate, 2, true, false, false);
            Run(gate, 2, false, false, false);
            Assert.AreEqual(0f, gate.Gain, 1e-4f);
            gate.Arrive();
            // One frame of 5 seconds counts as 0.1 s.
            float g = gate.Step(5f, true, false, false);
            Assert.AreEqual(0.1f, g, 1e-4f);
        }

        [TestMethod]
        public void APauseOrAWarpWinsOverAViewThatWentStale()
        {
            var gate = new AudioGate();
            gate.Arrive();
            Run(gate, 2, true, false, false);
            // The view is stale (no air) while the game is paused: the cut still takes a tenth of a second, not a second.
            Assert.AreEqual(0f, Run(gate, 0.15, false, true, false), 1e-4f);
            Assert.AreEqual(GateReason.Paused, gate.Reason);
            gate.Arrive();
            Run(gate, 2, true, false, false);
            Run(gate, 0.6, false, false, true);
            Assert.AreEqual(0f, gate.Gain, 1e-4f, "a warp with a stale view fades in half a second");
            Assert.AreEqual(GateReason.Warp, gate.Reason);
        }

        [TestMethod]
        public void TheListenerFollowsTheCameraMode()
        {
            ListenerState cabin = ListenerModel.From(true, false, false);
            Assert.IsTrue(cabin.Cabin && !cabin.MapView && !cabin.Eva);
            ListenerState helmet = ListenerModel.From(true, false, true);
            Assert.IsFalse(helmet.Cabin, "a kerbal is not in a cabin");
            Assert.IsTrue(helmet.Eva);
            ListenerState map = ListenerModel.From(false, true, false);
            Assert.IsTrue(map.MapView);
        }

        [TestMethod]
        public void TheParametersReachTheAudioThreadAsTheTableSays()
        {
            AudioSwitches sw = AudioSwitches.Defaults();
            var shared = new SharedAudioState();
            var w = new WeatherAudioInputs { Air = true, Type = PrecipType.Snow, Level01 = 0.6f, MeanWindMs = 14f, GustWave = 0.5f, SnowCover01 = 0.4f };
            var volumes = new GameVolumes { Ambience = 0.5f, Ship = 0.8f };
            AudioParamsMapper.Apply(w, new ListenerState(), sw, volumes, EveSoundPolicyTable.Decide(EveSoundPolicy.Auto, false), 0.5f, ref shared.Params);
            AudioParams p = shared.Params;
            Assert.AreEqual(14f, p.windSpeed);
            Assert.AreEqual(0.5f, p.gust);
            Assert.AreEqual(WindNoise.Depth(0.6f), p.gustDepth, 1e-6f);
            Assert.AreEqual(AudioMapping.SoundLevel(0.6f), p.intensity, 1e-6f);
            Assert.AreEqual(0f, p.grit);
            Assert.AreEqual(0f, p.interior);
            Assert.AreEqual(0.4f, p.snowCover, 1e-6f);
            Assert.AreEqual(0.6f * 0.5f, p.volume, 1e-6f, "slider times the gate");
            Assert.AreEqual(0.5f, p.volWind, 1e-6f);
            Assert.AreEqual(0.5f, p.volPrecip, 1e-6f);
            Assert.AreEqual(0.8f, p.volTicks, 1e-6f, "the ticks follow the ship volume");
            Assert.AreEqual(0f, p.volRumble, "the distant storm is off by default");
            Assert.AreEqual(0.5f, p.volQuiet, 1e-6f);
        }

        [TestMethod]
        public void EachSwitchSilencesItsOwnCategory()
        {
            var w = new WeatherAudioInputs { Air = true, Type = PrecipType.Snow, Level01 = 0.6f, MeanWindMs = 14f, SnowCover01 = 0.4f, CellCloseness = 0.7f };
            Func<Func<AudioSwitches, AudioSwitches>, AudioParams> run = edit =>
            {
                AudioSwitches sw = AudioSwitches.Defaults();
                sw.DistantStorm = true;
                sw = edit(sw);
                var shared = new SharedAudioState();
                AudioParamsMapper.Apply(w, new ListenerState(), sw, GameVolumes.Full(), EveSoundPolicyTable.Decide(sw.Eve, false), 1f, ref shared.Params);
                return shared.Params;
            };
            Assert.AreEqual(0f, run(s => { s.Wind = false; return s; }).volWind);
            Assert.AreEqual(0f, run(s => { s.Precip = false; return s; }).volPrecip);
            Assert.AreEqual(0f, run(s => { s.Precip = false; return s; }).volTicks);
            Assert.AreEqual(0f, run(s => { s.QuietSnow = false; return s; }).volQuiet);
            Assert.AreEqual(0f, run(s => { s.DistantStorm = false; return s; }).volRumble);
            Assert.AreEqual(0f, run(s => { s.DistantStorm = false; return s; }).cellCloseness);
            Assert.AreEqual(0.7f, run(s => s).cellCloseness, 1e-6f);
            Assert.AreEqual(1f, run(s => s).volWind, "control: the others stay on");
        }

        [TestMethod]
        public void TheCabinMuffleOnlyWorksOutsideTheMapView()
        {
            var w = new WeatherAudioInputs { Air = true, Type = PrecipType.Snow, Level01 = 0.4f, MeanWindMs = 8f };
            var shared = new SharedAudioState();
            AudioParamsMapper.Apply(w, new ListenerState { Cabin = true }, AudioSwitches.Defaults(), GameVolumes.Full(), EveSoundPolicyTable.Decide(EveSoundPolicy.Auto, false), 1f, ref shared.Params);
            Assert.AreEqual(1f, shared.Params.interior);
            AudioParamsMapper.Apply(w, new ListenerState { Cabin = true, MapView = true }, AudioSwitches.Defaults(), GameVolumes.Full(), EveSoundPolicyTable.Decide(EveSoundPolicy.Auto, false), 1f, ref shared.Params);
            Assert.AreEqual(0f, shared.Params.interior);
        }

        [TestMethod]
        public void NoAirZeroesWindFallAndCover()
        {
            var w = new WeatherAudioInputs { Air = false, Type = PrecipType.Snow, Level01 = 0.9f, MeanWindMs = 30f, SnowCover01 = 1f, CellCloseness = 1f };
            var shared = new SharedAudioState();
            AudioSwitches sw = AudioSwitches.Defaults();
            sw.DistantStorm = true;
            AudioParamsMapper.Apply(w, new ListenerState(), sw, GameVolumes.Full(), EveSoundPolicyTable.Decide(EveSoundPolicy.Auto, false), 1f, ref shared.Params);
            Assert.AreEqual(0f, shared.Params.windSpeed);
            Assert.AreEqual(0f, shared.Params.intensity);
            Assert.AreEqual(0f, shared.Params.snowCover);
            Assert.AreEqual(0f, shared.Params.cellCloseness);
        }

        [TestMethod]
        public void GritForSandDustAshAndTheBodyFlag()
        {
            var shared = new SharedAudioState();
            AudioSwitches sw = AudioSwitches.Defaults();
            Func<PrecipType, bool, float> grit = (type, body) =>
            {
                var w = new WeatherAudioInputs { Air = true, Type = type, Level01 = 0.5f, MeanWindMs = 5f, BodyGrit = body };
                AudioParamsMapper.Apply(w, new ListenerState(), sw, GameVolumes.Full(), EveSoundPolicyTable.Decide(EveSoundPolicy.Auto, false), 1f, ref shared.Params);
                return shared.Params.grit;
            };
            Assert.AreEqual(0f, grit(PrecipType.Snow, false));
            Assert.AreEqual(1f, grit(PrecipType.Sand, false));
            Assert.AreEqual(1f, grit(PrecipType.Dust, false));
            Assert.AreEqual(1f, grit(PrecipType.Ash, false));
            Assert.AreEqual(1f, grit(PrecipType.Snow, true));
            Assert.AreEqual(0f, grit(PrecipType.Rain, false));
            // Rain is not ours: no fall sound.
            var rain = new WeatherAudioInputs { Air = true, Type = PrecipType.Rain, Level01 = 0.9f };
            AudioParamsMapper.Apply(rain, new ListenerState(), sw, GameVolumes.Full(), EveSoundPolicyTable.Decide(EveSoundPolicy.Auto, false), 1f, ref shared.Params);
            Assert.AreEqual(0f, shared.Params.intensity);
        }

        [TestMethod]
        public void TheBedSourceSettingAndTheMixerAgree()
        {
            Assert.AreEqual(BedSource.Synth, (int)AudioBedSource.Synth);
            Assert.AreEqual(BedSource.Recorded, (int)AudioBedSource.Recorded);
            Assert.AreEqual(BedSource.Mix, (int)AudioBedSource.Mix);
            Assert.AreEqual(BedSource.Synth, BedSourceRule.Effective(AudioBedSource.Recorded, false), "no texture: the synthesised bed plays");
            Assert.AreEqual(BedSource.Synth, BedSourceRule.Effective(AudioBedSource.Mix, false));
            Assert.AreEqual(BedSource.Recorded, BedSourceRule.Effective(AudioBedSource.Recorded, true));
            Assert.AreEqual(BedSource.Mix, BedSourceRule.Effective(AudioBedSource.Mix, true));
            Assert.AreEqual(BedSource.Synth, BedSourceRule.Effective(AudioBedSource.Synth, true));
        }

        [TestMethod]
        public void TheMufflingDecisionTable()
        {
            // mode, RSE installed, RSE routes our clips, a step source seen with RSE's group
            //   -> own on procedural, own on samples, RSE carries samples, steps leave RSE's group
            object[][] rows =
            {
                new object[] { AudioMuffling.Auto, false, false, false, true, true, false, false },
                new object[] { AudioMuffling.Auto, true, false, false, true, true, false, false },
                new object[] { AudioMuffling.Auto, true, true, false, true, true, false, false },
                new object[] { AudioMuffling.Auto, true, true, true, true, false, true, false },
                new object[] { AudioMuffling.Auto, true, false, true, true, true, false, false },
                new object[] { AudioMuffling.Own, false, false, false, true, true, false, false },
                new object[] { AudioMuffling.Own, true, true, false, true, true, false, true },
                new object[] { AudioMuffling.Own, true, true, true, true, true, false, true },
                new object[] { AudioMuffling.Rse, false, false, false, true, true, false, false },
                new object[] { AudioMuffling.Rse, true, false, false, true, false, true, false },
                new object[] { AudioMuffling.Rse, true, true, true, true, false, true, false }
            };
            foreach (object[] r in rows)
            {
                MufflingPlan plan = MufflingTable.Decide((AudioMuffling)r[0], (bool)r[1], (bool)r[2], (bool)r[3]);
                string label = r[0] + " rse=" + r[1] + " routes=" + r[2] + " seen=" + r[3];
                Assert.AreEqual((bool)r[4], plan.OwnOnProcedural, label);
                Assert.AreEqual((bool)r[5], plan.OwnOnSamples, label);
                Assert.AreEqual((bool)r[6], plan.RseCarriesSamples, label);
                Assert.AreEqual((bool)r[7], plan.DetachFromRse, label);
                // Never muffled twice: the own low-pass and RSE's muffler do not both carry the samples, and with RSE present the own
                // setting takes the sources out of RSE's group.
                Assert.IsFalse(plan.OwnOnSamples && plan.RseCarriesSamples, label);
                if ((AudioMuffling)r[0] == AudioMuffling.Own && (bool)r[1]) Assert.IsTrue(plan.DetachFromRse, label);
            }
        }

        [TestMethod]
        public void TheEveSoundPolicyTable()
        {
            EveSoundDecision auto = EveSoundPolicyTable.Decide(EveSoundPolicy.Auto, false);
            Assert.AreEqual(1f, auto.BedFactor);
            Assert.AreEqual(1f, auto.HissFactor);
            Assert.IsFalse(auto.SilenceEve);
            EveSoundDecision autoAudible = EveSoundPolicyTable.Decide(EveSoundPolicy.Auto, true);
            Assert.AreEqual(0.398f, autoAudible.BedFactor, 1e-3f, "-8 dB");
            Assert.AreEqual(0f, autoAudible.HissFactor, "no static hiss");
            Assert.IsFalse(autoAudible.SilenceEve, "auto never writes EVE's source");
            Assert.IsTrue(autoAudible.EveCarriesBed);
            EveSoundDecision eve = EveSoundPolicyTable.Decide(EveSoundPolicy.Eve, true);
            Assert.AreEqual(0f, eve.BedFactor);
            Assert.AreEqual(0f, eve.HissFactor);
            Assert.IsFalse(eve.SilenceEve);
            EveSoundDecision ours = EveSoundPolicyTable.Decide(EveSoundPolicy.Ours, true);
            Assert.AreEqual(1f, ours.BedFactor);
            Assert.IsTrue(ours.SilenceEve);
            EveSoundDecision both = EveSoundPolicyTable.Decide(EveSoundPolicy.Both, true);
            Assert.AreEqual(1f, both.BedFactor);
            Assert.AreEqual(1f, both.HissFactor);
            Assert.IsFalse(both.SilenceEve);
        }

        [TestMethod]
        public void NextToAnAudibleEveBedTheSumStaysWithinThreeDecibelsOfTheLarger()
        {
            // Offline model of the series row: our bed under the Auto decision and EVE's loop as an independent noise of the same strength as
            // the full bed (the worst case for doubling): the sum of the two uncorrelated beds stays within 3 dB of the larger one.
            double ours = EveSoundPolicyTable.BedUnderEve;
            double eve = 1.0;
            double sum = EveSoundPolicyTable.SumRms(ours, eve);
            double db = 20 * Math.Log10(sum / Math.Max(ours, eve));
            Assert.IsTrue(db >= 0 && db <= 3.0, "sum over larger " + db);
            // Without the policy (both full) the two beds would add 3.01 dB.
            Assert.IsTrue(20 * Math.Log10(EveSoundPolicyTable.SumRms(1, 1)) > 3.0);
        }

        [TestMethod]
        public void TheModuleIsRegisteredAndTheOverridesAreEmptyInPlay()
        {
            object[] attributes = typeof(WeatherAudioModule).GetCustomAttributes(typeof(AtmospheresModuleAttribute), false);
            Assert.AreEqual(1, attributes.Length);
            Assert.IsTrue(typeof(IAtmospheresModule).IsAssignableFrom(typeof(WeatherAudioModule)));
            Assert.IsNull(AudioOverrides.Inputs);
            Assert.IsNull(AudioOverrides.Switches);
            Assert.IsFalse(AudioOverrides.Walking);
            AudioOverrides.Walking = true;
            AudioOverrides.Volumes = GameVolumes.Full();
            AudioOverrides.Clear();
            Assert.IsFalse(AudioOverrides.Walking);
            Assert.IsNull(AudioOverrides.Volumes);
        }
    }
}
