using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using UnityEngine;

namespace ReDefinition.Tests
{
    // The pure parts of the snow on the ground: the noise, the colour blend, the repaint of a quad's colours and the phase choice.
    [TestClass]
    public class SnowGroundTests
    {
        [TestMethod]
        public void TheNoiseIsDeterministicAndInRange()
        {
            for (double lat = -85; lat <= 85; lat += 5)
                for (double lon = 0; lon < 360; lon += 17)
                {
                    double y = Math.Sin(lat * Math.PI / 180);
                    double r = Math.Cos(lat * Math.PI / 180);
                    double x = r * Math.Cos(lon * Math.PI / 180), z = r * Math.Sin(lon * Math.PI / 180);
                    double a = SnowNoise.Value(x, y, z);
                    Assert.AreEqual(a, SnowNoise.Value(x, y, z));
                    Assert.AreEqual(a, SnowNoise.Value(x * 3, y * 3, z * 3), 1e-12, "the length of the direction does not matter");
                    Assert.IsTrue(a >= -1 && a <= 1, lat + "," + lon + ": " + a);
                }
        }

        [TestMethod]
        public void TheNoiseVariesAndIsSmooth()
        {
            double min = 1, max = -1, step = 0;
            double previous = SnowNoise.Value(1, 0.3, 0);
            for (int i = 1; i <= 2000; i++)
            {
                double angle = i * 0.0004;
                double value = SnowNoise.Value(Math.Cos(angle), 0.3, Math.Sin(angle));
                min = Math.Min(min, value);
                max = Math.Max(max, value);
                step = Math.Max(step, Math.Abs(value - previous));
                previous = value;
            }
            Assert.IsTrue(max - min > 0.5, "range " + min + " to " + max);
            Assert.IsTrue(step < 0.2, "largest step between neighbours " + step);
        }

        [TestMethod]
        public void TheNoiseOfADegenerateDirectionIsZero()
        {
            Assert.AreEqual(0, SnowNoise.Value(0, 0, 0));
            Assert.AreEqual(0, SnowNoise.Value(double.NaN, 1, 0));
            Assert.AreEqual(0, SnowNoise.Value(double.PositiveInfinity, 1, 0));
        }

        [TestMethod]
        public void TheAmountIsTheCoverTimesTheStrengthInRange()
        {
            Assert.AreEqual(0f, SnowPaint.Amount(0.8, 0f));
            Assert.AreEqual(0.4f, SnowPaint.Amount(0.8, 0.5f), 1e-6);
            Assert.AreEqual(1f, SnowPaint.Amount(5, 1f));
            Assert.AreEqual(0f, SnowPaint.Amount(-1, 1f));
            Assert.AreEqual(0f, SnowPaint.Amount(double.NaN, 1f));
        }

        [TestMethod]
        public void TheBlendKeepsTheColourAtZeroAndTheSnowAtOneAndAlphaAlways()
        {
            var ground = new Color(0.2f, 0.4f, 0.1f, 0.35f);
            Assert.AreEqual(ground, SnowPaint.Blend(ground, 0f));
            Color full = SnowPaint.Blend(ground, 1f);
            Assert.AreEqual(SnowPaint.SnowRed, full.r, 1e-6);
            Assert.AreEqual(SnowPaint.SnowGreen, full.g, 1e-6);
            Assert.AreEqual(SnowPaint.SnowBlue, full.b, 1e-6);
            Assert.AreEqual(0.35f, full.a, 1e-6);
            Color half = SnowPaint.Blend(ground, 0.5f);
            Assert.AreEqual((0.2f + SnowPaint.SnowRed) / 2, half.r, 1e-6);
            Assert.AreEqual(0.35f, half.a, 1e-6);
            Assert.AreEqual(full.r, SnowPaint.Blend(ground, 7f).r, 1e-6, "an amount above 1 counts as 1");
        }

        [TestMethod]
        public void TheByteBlendAgreesWithTheFloatBlendWithinARoundingStep()
        {
            var ground = new Color32(40, 100, 20, 90);
            foreach (float amount in new[] { 0f, 0.25f, 0.5f, 0.9f, 1f })
            {
                Color32 b = SnowPaint.Blend(ground, amount);
                Color f = SnowPaint.Blend((Color)ground, amount);
                Assert.AreEqual(f.r * 255f, b.r, 1.01f);
                Assert.AreEqual(f.g * 255f, b.g, 1.01f);
                Assert.AreEqual(f.b * 255f, b.b, 1.01f);
                Assert.AreEqual(90, b.a);
            }
        }

        private static QuadSnow Quad(double latitude, double height, Color32 colour)
        {
            var quad = new QuadSnow(9);
            for (int i = 0; i < 9; i++) quad.Set(i, colour, latitude + i * 0.01, height, 0);
            return quad;
        }

        [TestMethod]
        public void AQuadIsPaintedWinterAndSummerFromTheSameOriginals()
        {
            var green = new Color32(60, 110, 40, 255);
            QuadSnow quad = Quad(55, 100, green);
            double winter = SnowMask.DefaultWinterLineDegrees;
            float winterMean = quad.Paint(0.06, 1f, winter);
            Color32 snowy = quad.Painted[0];
            float summerMean = quad.Paint(0.56, 1f, winter);
            Color32 bare = quad.Painted[0];
            Assert.IsTrue(winterMean > 0.95f, "winter " + winterMean);
            Assert.IsTrue(summerMean < 0.05f, "summer " + summerMean);
            Assert.IsTrue(snowy.r > 200 && snowy.g > 200 && snowy.b > 200, snowy.ToString());
            Assert.AreEqual(green.r, bare.r, 12);
            Assert.AreEqual(green.g, bare.g, 12);
            Assert.AreEqual(green, quad.Original[0], "the originals stay");
            Assert.AreEqual(255, snowy.a);
        }

        [TestMethod]
        public void AQuadAtTheEquatorStaysBareAllYear()
        {
            QuadSnow quad = Quad(2, 20, new Color32(90, 90, 60, 255));
            for (double phase = 0; phase < 1; phase += 0.05)
                Assert.AreEqual(0f, quad.Paint(phase, 1f, SnowMask.DefaultWinterLineDegrees), 1e-6f, "phase " + phase);
        }

        [TestMethod]
        public void TheStrengthSettingScalesTheSnowAndMeanAmountMatchesPaint()
        {
            QuadSnow quad = Quad(55, 100, new Color32(60, 110, 40, 255));
            double winter = SnowMask.DefaultWinterLineDegrees;
            float full = quad.Paint(0.06, 1f, winter);
            float half = quad.Paint(0.06, 0.5f, winter);
            Assert.AreEqual(full / 2, half, 1e-4f);
            Assert.AreEqual(half, quad.MeanAmount(0.06, 0.5f, winter), 1e-6f);
            Assert.AreEqual(half, quad.LastMean, 1e-6f);
        }

        [TestMethod]
        public void AVertexOutsideTheQuadIsIgnoredAndTheLengthIsTheVertexCount()
        {
            var quad = new QuadSnow(4);
            quad.Set(-1, new Color32(1, 2, 3, 4), 0, 0, 0);
            quad.Set(4, new Color32(1, 2, 3, 4), 0, 0, 0);
            Assert.AreEqual(4, quad.Length);
            Assert.AreEqual(0, quad.Original[0].a);
        }

        [TestMethod]
        public void AQuadNeedsPaintOnlyWhenThePhaseMovedAndTheYearWraps()
        {
            var quad = new QuadSnow(1);
            Assert.IsTrue(quad.NeedsPaint(0.3, 1e-5), "never painted");
            quad.Paint(0.999, 1f, 42);
            Assert.IsFalse(quad.NeedsPaint(0.999, 1e-5));
            Assert.IsFalse(quad.NeedsPaint(0.999 + 5e-6, 1e-5));
            Assert.IsTrue(quad.NeedsPaint(0.01, 1e-5));
            Assert.AreEqual(0.02, SeasonClock.PhaseDistance(0.99, 0.01), 1e-12);
            Assert.AreEqual(0.5, SeasonClock.PhaseDistance(0, 0.5), 1e-12);
            Assert.AreEqual(0, SeasonClock.PhaseDistance(0.25, 1.25), 1e-12);
        }

        [TestMethod]
        public void AForcedPhaseReplacesTheClockOnlyInsideTheYear()
        {
            Assert.AreEqual(0.3, SeasonState.Choose(0.3f, 0.9).Value, 1e-6);
            Assert.AreEqual(0.9, SeasonState.Choose(-1f, 0.9).Value, 1e-12);
            Assert.AreEqual(0.9, SeasonState.Choose(1f, 0.9).Value, 1e-12);
            Assert.AreEqual(0.0, SeasonState.Choose(0f, 0.9).Value);
        }

        [TestMethod]
        public void WithoutAClockAndWithoutAForcedPhaseThereIsNoPhaseAndNothingIsPainted()
        {
            Assert.IsNull(SeasonState.Choose(-1f, null), "no Kerbin yet: no phase, not a winter");
            Assert.IsNull(SeasonState.Choose(1.5f, null), "a forced phase outside the year does not count");
            Assert.AreEqual(0.25, SeasonState.Choose(0.25f, null).Value, 1e-6, "a forced phase needs no clock");
        }
    }
}
