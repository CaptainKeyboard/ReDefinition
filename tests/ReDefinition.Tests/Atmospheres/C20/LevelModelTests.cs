using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Core;

namespace ReDefinition.Tests.Atmospheres.C20
{
    // The look curves, the rate-to-level mapping and the type model of the precipitation scale (package C20), without the game.
    [TestClass]
    public class LevelModelTests
    {
        // The direction of each curve's key values: +1 rises with the level, -1 falls.
        private static int Direction(string name)
        {
            return name == "visibility" || name == "sun" || name == "ambient" || name == "shadow" || name == "flakeSize" || name == "fall" ? -1 : 1;
        }

        [TestMethod]
        public void EveryCurveIsMonotoneInTheLevel()
        {
            foreach (string name in PrecipitationLook.CurveNames)
            {
                LookCurve c = PrecipitationLook.Curve(name);
                int dir = Direction(name);
                float previous = c.Evaluate(0f);
                for (int i = 1; i <= 2000; i++)
                {
                    float v = c.Evaluate(i / 2000f);
                    Assert.IsTrue(dir * (v - previous) >= -1e-4f * Math.Max(1f, Math.Abs(previous)), name + " turns at " + i / 2000f + ": " + previous + " to " + v);
                    previous = v;
                }
            }
        }

        [TestMethod]
        public void TheSlopeOfEveryCurveStaysWithinThreeTimesTheSteepestStep()
        {
            foreach (string name in PrecipitationLook.CurveNames)
            {
                LookCurve c = PrecipitationLook.Curve(name);
                float steepest = 0f;
                float xPrev = 0f, yPrev = PrecipitationLook.StartValue(name, 0);
                for (int k = 0; k < LookCurve.KeyLevels.Length; k++)
                {
                    float x = LookCurve.KeyLevels[k], y = PrecipitationLook.StartValue(name, k + 1);
                    steepest = Math.Max(steepest, Math.Abs(y - yPrev) / (x - xPrev));
                    xPrev = x;
                    yPrev = y;
                }
                Assert.IsTrue(c.MaxSlope() <= 3f * steepest + 1e-3f, name + " slope " + c.MaxSlope() + " against the steepest step " + steepest);
            }
        }

        [TestMethod]
        public void TheCurvesPassThroughTheirKeyPointsAndTheTopValuesAreTheStartingValues()
        {
            foreach (string name in PrecipitationLook.CurveNames)
            {
                for (int k = 0; k < LookCurve.KeyLevels.Length; k++)
                {
                    float level = LookCurve.KeyLevels[k];
                    float expected = PrecipitationLook.StartValue(name, k + 1);
                    float got = PrecipitationLook.Curve(name).Evaluate(level);
                    Assert.AreEqual(expected, got, 0.004f * Math.Max(1f, Math.Abs(expected)) + 1e-5f, name + " at " + level);
                }
            }
            LookValues top = PrecipitationLook.Evaluate(1f);
            Assert.AreEqual(60f, top.VisibilityM, 1e-3f);
            Assert.AreEqual(0.85f, top.VeilAlpha, 1e-4f);
            Assert.AreEqual(2400f, top.FlakesPerS, 1e-2f);
            LookValues none = PrecipitationLook.Evaluate(0f);
            Assert.AreEqual(1f, none.SunDirect, 1e-5f);
            Assert.AreEqual(1f, none.AmbientFactor, 1e-5f);
            Assert.AreEqual(0f, none.VeilAlpha, 1e-5f);
        }

        [TestMethod]
        public void TheBandNamesStartAtTheBoundaries()
        {
            Assert.AreEqual(PrecipBand.None, PrecipScale.BandOf(0.039f, 0f));
            Assert.AreEqual(PrecipBand.Flurries, PrecipScale.BandOf(0.04f, 0f));
            Assert.AreEqual(PrecipBand.Light, PrecipScale.BandOf(0.20f, 0f));
            Assert.AreEqual(PrecipBand.Moderate, PrecipScale.BandOf(0.40f, 0f));
            Assert.AreEqual(PrecipBand.Heavy, PrecipScale.BandOf(0.60f, 0f));
            Assert.AreEqual(PrecipBand.Heavy, PrecipScale.BandOf(0.95f, 10f));
            Assert.AreEqual(PrecipBand.Blizzard, PrecipScale.BandOf(0.95f, 16f));
            Assert.AreEqual("Blizzard", PrecipScale.DefaultName(PrecipBand.Blizzard));
        }

        [TestMethod]
        public void TheCurtainGateHasHysteresisAndTheShareIsSmooth()
        {
            var gate = new CurtainGate();
            Assert.IsFalse(gate.Update(0.44f));
            Assert.IsTrue(gate.Update(0.45f));
            Assert.IsTrue(gate.Update(0.41f), "stays on above 0.40");
            Assert.IsFalse(gate.Update(0.39f));
            Assert.IsFalse(gate.Update(0.44f), "needs 0.45 to come on again");
            Assert.AreEqual(0f, PrecipitationLook.CurtainShare(0.45f), 1e-6f);
            Assert.AreEqual(1f, PrecipitationLook.CurtainShare(0.9f), 1e-6f);
            Assert.AreEqual(0.5f, PrecipitationLook.CurtainShare(0.675f), 1e-4f);
        }

        [TestMethod]
        public void TheLevelFollowsItsTargetAtMostOneNinetiethPerGameSecond()
        {
            float level = 0f;
            for (int s = 0; s < 45; s++) level = LevelLimiter.Step(level, 1f, 1.0, false);
            Assert.AreEqual(0.5f, level, 1e-4f);
            for (int s = 0; s < 100; s++) level = LevelLimiter.Step(level, 1f, 1.0, false);
            Assert.AreEqual(1f, level, 1e-6f);
            Assert.AreEqual(0.2f, LevelLimiter.Step(1f, 0.2f, 1.0, true), 1e-6f, "a jump sets the target");
        }

        [TestMethod]
        public void TheRateToLevelMappingIsMonotoneAndKeepsTheAbsoluteThresholds()
        {
            float[] bands = PrecipRules.PlaceholderBandRates;
            var scratch = new float[bands.Length];
            float previous = -1f;
            for (int i = 0; i <= 4000; i++)
            {
                float rate = i * 0.003f;
                float level = PrecipRateToLevel.Level(rate, bands, 1f, 1f, scratch);
                Assert.IsTrue(level >= previous, "monotone at " + rate);
                previous = level;
            }
            // The level of a threshold rate is the level where the band starts.
            Assert.AreEqual(PrecipScale.FlurriesFrom, PrecipRateToLevel.Level(bands[0], bands, 1f, 1f, scratch), 1e-5f);
            Assert.AreEqual(PrecipScale.LightFrom, PrecipRateToLevel.Level(bands[1], bands, 1f, 1f, scratch), 1e-5f);
            Assert.AreEqual(PrecipScale.ModerateFrom, PrecipRateToLevel.Level(bands[2], bands, 1f, 1f, scratch), 1e-5f);
            Assert.AreEqual(PrecipScale.HeavyFrom, PrecipRateToLevel.Level(bands[3], bands, 1f, 1f, scratch), 1e-5f);
            Assert.AreEqual(PrecipScale.BlizzardFrom, PrecipRateToLevel.Level(bands[4], bands, 1f, 1f, scratch), 1e-5f);
        }

        [TestMethod]
        public void TheBandSharesOfAScriptedFieldEqualTheThresholds()
        {
            float[] bands = PrecipRules.PlaceholderBandRates;
            var scratch = new float[bands.Length];
            var byRate = new int[6];
            var byLevel = new int[6];
            // A scripted field: 20000 rates spread over 0.01 to 12 mm/h on a log scale.
            for (int i = 0; i < 20000; i++)
            {
                float rate = (float)(0.01 * Math.Pow(1200.0, i / 20000.0));
                int expected = 0;
                for (int b = 0; b < 5; b++)
                {
                    if (rate >= bands[b]) expected = b + 1;
                }
                byRate[expected]++;
                PrecipBand band = PrecipScale.BandOf(PrecipRateToLevel.Level(rate, bands, 1f, 1f, scratch), 20f);
                byLevel[(int)band]++;
            }
            for (int b = 0; b < 6; b++) Assert.AreEqual(byRate[b], byLevel[b], 1, "band " + b);
        }

        [TestMethod]
        public void TheKnobsShiftOnlyTheThresholdsTheyName()
        {
            float[] bands = PrecipRules.PlaceholderBandRates;
            var shifted = new float[bands.Length];
            PrecipRateToLevel.Shift(bands, 1f, 1f, shifted);
            CollectionAssert.AreEqual(bands, shifted, "neutral knobs change nothing");

            PrecipRateToLevel.Shift(bands, 2f, 1f, shifted);
            Assert.AreEqual(bands[0] / 2f, shifted[0], 1e-6f);
            Assert.AreEqual(bands[1] / 2f, shifted[1], 1e-6f);
            Assert.AreEqual(bands[2], shifted[2]);
            Assert.AreEqual(bands[3], shifted[3]);
            Assert.AreEqual(bands[4], shifted[4]);

            PrecipRateToLevel.Shift(bands, 1f, 2f, shifted);
            Assert.AreEqual(bands[0], shifted[0]);
            Assert.AreEqual(bands[1], shifted[1]);
            Assert.AreEqual(bands[2], shifted[2]);
            Assert.AreEqual(bands[3] / 2f, shifted[3], 1e-6f);
            Assert.AreEqual(bands[4] / 2f, shifted[4], 1e-6f);

            // The extremes keep the table ascending and the lower bands below Moderate, the upper above it.
            foreach (float light in new[] { 0f, 0.1f, 2f })
            {
                foreach (float heavy in new[] { 0f, 0.1f, 2f })
                {
                    PrecipRateToLevel.Shift(bands, light, heavy, shifted);
                    for (int i = 1; i < shifted.Length; i++) Assert.IsTrue(shifted[i] > shifted[i - 1], "ascending at " + i + " for " + light + "/" + heavy);
                    Assert.AreEqual(bands[2], shifted[2]);
                }
            }
        }

        [TestMethod]
        public void TheKnobsMoveTheFrequencyOfTheirBandsTheRightWay()
        {
            float[] bands = PrecipRules.PlaceholderBandRates;
            var scratch = new float[bands.Length];
            // A rate of 0.1 mm/h is below Flurries by the table and Flurries once light weather is twice as frequent.
            Assert.AreEqual(PrecipBand.None, PrecipScale.BandOf(PrecipRateToLevel.Level(0.04f, bands, 1f, 1f, scratch), 0f));
            Assert.AreEqual(PrecipBand.Flurries, PrecipScale.BandOf(PrecipRateToLevel.Level(0.04f, bands, 2f, 1f, scratch), 0f));
            // A rate of 2 mm/h is Moderate by the table and Heavy when the heavy share is doubled (Heavy starts at 1.25).
            Assert.AreEqual(PrecipBand.Moderate, PrecipScale.BandOf(PrecipRateToLevel.Level(2.0f, bands, 1f, 1f, scratch), 0f));
            Assert.AreEqual(PrecipBand.Heavy, PrecipScale.BandOf(PrecipRateToLevel.Level(2.0f, bands, 1f, 2f, scratch), 0f));
        }

        [TestMethod]
        public void TheTypeFollowsTheTemperatureOfTheField()
        {
            // KSC: warm, rain. 45 N in winter: snow or sleet. 70 N: snow. Mountain top at the equator: snow.
            Assert.AreEqual(PrecipType.Rain, TypeAt(300f, 0.3f));
            PrecipType winter = TypeAt(272.65f, 0.3f);
            Assert.IsTrue(winter == PrecipType.Snow || winter == PrecipType.Sleet, "45 N winter " + winter);
            Assert.AreEqual(PrecipType.Snow, TypeAt(268f, 0.3f));
            Assert.AreEqual(PrecipType.Snow, TypeAt(250f, 0.6f));
            Assert.AreEqual(PrecipType.Snow, TypeAt(265f, 0.2f));
            // Sleet where the two overlap.
            Assert.AreEqual(PrecipType.Sleet, TypeAt(273.15f + 1.0f + 0.7f, 0.2f));
            TypeMix mix = PrecipTypeModel.Mix(274f, 0.3f, 1f, 0.9f);
            Assert.AreEqual(1f, mix.Snow + mix.Sleet + mix.Rain, 1e-5f);
        }

        private static PrecipType TypeAt(float kelvin, float level)
        {
            return PrecipTypeModel.Mix(kelvin, level, PrecipTypeModel.DefaultCentreC, PrecipTypeModel.DefaultWidthC).Dominant;
        }

        [TestMethod]
        public void TheWetBulbOffsetRunsFromLightToHeavy()
        {
            Assert.AreEqual(0.7f, PrecipTypeModel.WetBulbOffsetK(0.1f), 1e-6f);
            Assert.AreEqual(0.3f, PrecipTypeModel.WetBulbOffsetK(0.8f), 1e-6f);
            float previous = 1f;
            for (int i = 0; i <= 100; i++)
            {
                float o = PrecipTypeModel.WetBulbOffsetK(i / 100f);
                Assert.IsTrue(o <= previous + 1e-6f);
                previous = o;
            }
        }

        [TestMethod]
        public void TheFractionsStepAtMostAtenthPerGameMinuteAtTheRainSnowLine()
        {
            // The air warms 1 K every 10 game minutes, then 30 K a minute (a teleport down a mountain without the jump flag).
            foreach (double kelvinPerSecond in new[] { 1.0 / 600.0, 0.5 })
            {
                TypeMix mix = PrecipTypeModel.Mix(268f, 0.3f, 1f, 0.9f);
                TypeMix atMinuteStart = mix;
                double temperature = 268.0;
                double worst = 0.0;
                for (int s = 1; s <= 7200; s++)
                {
                    temperature += kelvinPerSecond;
                    if (temperature > 280.0) temperature = 280.0;
                    mix = PrecipTypeModel.Limit(mix, PrecipTypeModel.Mix((float)temperature, 0.3f, 1f, 0.9f), 1.0, false);
                    if (s % 60 == 0)
                    {
                        worst = Math.Max(worst, Math.Max(Math.Abs(mix.Snow - atMinuteStart.Snow), Math.Max(Math.Abs(mix.Sleet - atMinuteStart.Sleet), Math.Abs(mix.Rain - atMinuteStart.Rain))));
                        atMinuteStart = mix;
                    }
                    Assert.AreEqual(1f, mix.Snow + mix.Sleet + mix.Rain, 1e-4f);
                }
                Assert.IsTrue(worst <= 0.1 + 1e-4, "worst step per game minute " + worst + " at " + kelvinPerSecond + " K/s");
                Assert.IsTrue(mix.Rain > 0.98f, "ends as rain, " + mix.Rain);
            }
        }

        [TestMethod]
        public void FreezingRainIsRareAndDeterministic()
        {
            int hits = 0;
            for (int slot = 0; slot < 20000; slot++)
            {
                bool a = PrecipTypeModel.FreezingRainFlag(slot * 600.0 + 5.0, 7);
                Assert.AreEqual(a, PrecipTypeModel.FreezingRainFlag(slot * 600.0 + 300.0, 7), "one answer per ten minutes");
                if (a) hits++;
            }
            Assert.IsTrue(hits > 20000 * 0.06 && hits < 20000 * 0.10, "share " + hits / 20000.0);
        }

        [TestMethod]
        public void TheBodyKindOfDustAndSandComesFromItsRule()
        {
            var dust = new PrecipRules(new[] { new PrecipTypeRule("dust", PrecipType.Dust) });
            Assert.AreEqual(PrecipType.Dust, PrecipTypeModel.BodyKind(dust));
            Assert.AreEqual(PrecipType.None, PrecipTypeModel.BodyKind(PrecipRules.Default));
            float centre, width;
            PrecipTypeModel.Parameters(new PrecipRules(new[] { new PrecipTypeRule("snow", PrecipType.Snow, 2.0, 1.5) }), out centre, out width);
            Assert.AreEqual(2.0f, centre);
            Assert.AreEqual(1.5f, width);
        }
    }
}
