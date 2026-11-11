using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests.Atmospheres.D32
{
    // L0 of the lamp probe's frame figures (D32, review R1 to R3): the arithmetic that turns frames into the rows of K-W3b, and the cost
    // of choosing among many lamps.
    [TestClass]
    public class LampProbeMathTests
    {
        [TestMethod]
        public void LumaIsRec709AndWhiteIsOne()
        {
            Assert.AreEqual(1.0, LampProbeMath.Luma(1, 1, 1), 1e-9);
            Assert.AreEqual(0.0, LampProbeMath.Luma(0, 0, 0), 1e-12);
            Assert.IsTrue(LampProbeMath.Luma(0, 1, 0) > LampProbeMath.Luma(1, 0, 0));
        }

        [TestMethod]
        public void TheDeltaAndNoiseNeedEqualLists()
        {
            Assert.AreEqual(0.5, LampProbeMath.MeanDelta(new[] { 1.0, 2.0 }, new[] { 0.5, 1.5 }), 1e-12);
            Assert.IsTrue(double.IsNaN(LampProbeMath.MeanDelta(new[] { 1.0 }, new[] { 1.0, 2.0 })));
            Assert.IsTrue(double.IsNaN(LampProbeMath.MeanDelta(null, new[] { 1.0 })));
            Assert.AreEqual(1.0, LampProbeMath.MeanAbsDelta(new[] { 1.0, 3.0 }, new[] { 2.0, 2.0 }), 1e-12);
        }

        [TestMethod]
        public void SegmentPointsSpreadOverTheRequestedFractions()
        {
            double x, y;
            LampProbeMath.SegmentPoint(0, 0, 10, 20, 0, 8, 0.15, 0.75, out x, out y);
            Assert.AreEqual(1.5, x, 1e-9);
            Assert.AreEqual(3.0, y, 1e-9);
            LampProbeMath.SegmentPoint(0, 0, 10, 20, 7, 8, 0.15, 0.75, out x, out y);
            Assert.AreEqual(7.5, x, 1e-9);
            Assert.AreEqual(15.0, y, 1e-9);
            LampProbeMath.SegmentPoint(0, 0, 10, 20, 0, 1, 0.2, 0.6, out x, out y);
            Assert.AreEqual(4.0, x, 1e-9);
        }

        [TestMethod]
        public void InViewHonoursTheMargin()
        {
            Assert.IsTrue(LampProbeMath.InView(0.5, 0.5, 1, 1, 0.03));
            Assert.IsFalse(LampProbeMath.InView(0.02, 0.5, 1, 1, 0.03));
            Assert.IsFalse(LampProbeMath.InView(-1, -1, 1, 1, 0.03));
            Assert.IsFalse(LampProbeMath.InView(0.5, 1.01, 1, 1, 0.0));
        }

        [TestMethod]
        public void TheSignalRatioIsReadAgainstControlNoiseAndFloor()
        {
            Assert.AreEqual(10.0, LampProbeMath.SignalRatio(0.05, 0.005, 0.002, 0.001), 1e-9);
            Assert.AreEqual(5.0, LampProbeMath.SignalRatio(0.05, 0.001, 0.01, 0.001), 1e-9);
            // A floor keeps a quiet control from making a ratio of nothing large.
            Assert.AreEqual(5.0, LampProbeMath.SignalRatio(0.01, 0.0, 0.0, 0.002), 1e-9);
            // A negative control counts by its size.
            Assert.AreEqual(5.0, LampProbeMath.SignalRatio(0.05, -0.01, 0.0, 0.0), 1e-9);
        }

        [TestMethod]
        public void TheReachStopsAtTheFirstGap()
        {
            Assert.AreEqual(0.0, LampProbeMath.Reach(new double[] { 0, 0, 0 }, 0.5));
            Assert.AreEqual(1.0, LampProbeMath.Reach(new[] { 1.0, 0.9, 0.8, 0.7 }, 0.5), 1e-12);
            Assert.AreEqual(0.5, LampProbeMath.Reach(new[] { 1.0, 0.9, 0.1, 0.9 }, 0.5), 1e-12);
            Assert.AreEqual(0.0, LampProbeMath.Reach(null, 0.5));
        }

        [TestMethod]
        public void SmearIsTheFirstFrameWhereTheBeamsExtraErrorIsSmall()
        {
            // The beam's own contribution is 0.1; a quarter of it is 0.025.
            var with = new[] { 0.20, 0.12, 0.04, 0.03, 0.02 };
            var without = new[] { 0.01, 0.01, 0.01, 0.01, 0.01 };
            Assert.AreEqual(4, LampProbeMath.SmearFrames(with, without, 0.1, 0.25));
            // Never within the window: Count + 1.
            Assert.AreEqual(6, LampProbeMath.SmearFrames(new[] { 0.3, 0.3, 0.3, 0.3, 0.3 }, without, 0.1, 0.25));
            // At once: 1.
            Assert.AreEqual(1, LampProbeMath.SmearFrames(without, without, 0.1, 0.25));
            // A beam of the other sign counts by its size.
            Assert.AreEqual(4, LampProbeMath.SmearFrames(with, without, -0.1, 0.25));
            // A frame within the allowance by chance does not end the smear when a later one leaves it.
            Assert.AreEqual(4, LampProbeMath.SmearFrames(new[] { 0.01, 0.01, 0.21, 0.01, 0.01 }, without, 0.1, 0.25));
            // The allowance never falls below the noise of the reading.
            Assert.AreEqual(1, LampProbeMath.SmearFrames(new[] { 0.012, 0.012 }, new[] { 0.01, 0.01 }, 0.004, 0.25, 0.003));
            Assert.AreEqual(3, LampProbeMath.SmearFrames(new[] { 0.012, 0.012 }, new[] { 0.01, 0.01 }, 0.004, 0.25));
            // A missing frame (NaN) is outside the allowance.
            Assert.AreEqual(3, LampProbeMath.SmearFrames(new[] { 0.01, double.NaN }, new[] { 0.01, 0.01 }, 0.1, 0.25));
            // Lists of unequal length or none are no measurement: the figure is far above any limit, never a pass.
            Assert.IsTrue(LampProbeMath.SmearFrames(with, new[] { 0.0 }, 0.1, 0.25) >= LampProbeMath.NoMeasurement);
            Assert.IsTrue(LampProbeMath.SmearFrames(null, without, 0.1, 0.25) >= LampProbeMath.NoMeasurement);
            Assert.IsTrue(LampProbeMath.FramesUntil(null, 0.01) >= LampProbeMath.NoMeasurement);
        }

        [TestMethod]
        public void FramesUntilFindsTheFirstValueAtOrBelowTheFloor()
        {
            Assert.AreEqual(3, LampProbeMath.FramesUntil(new[] { 0.5, 0.2, 0.01, 0.0 }, 0.01));
            Assert.AreEqual(4, LampProbeMath.FramesUntil(new[] { 0.5, 0.2, 0.1 }, 0.01));
        }

        [TestMethod]
        public void TheBestIndexNeedsAMinimum()
        {
            Assert.AreEqual(2, LampProbeMath.BestIndex(new[] { 0.1, 0.4, 0.9, 0.3 }, 0.5));
            Assert.AreEqual(-1, LampProbeMath.BestIndex(new[] { 0.1, 0.2 }, 0.5));
            Assert.AreEqual(-1, LampProbeMath.BestIndex(null, 0.5));
        }

        [TestMethod]
        public void TheMeanOfNothingIsNaN()
        {
            Assert.IsTrue(double.IsNaN(LampProbeMath.Mean(new List<double>())));
            Assert.AreEqual(2.0, LampProbeMath.Mean(new[] { 1.0, 3.0 }), 1e-12);
        }

        // 50 lights on a vessel (review R6): the scoring of a lamp and the choice of the best four stay far below the module's CPU budget
        // of 0.05 ms per frame, which the choice only has at the rescoring rate (5 Hz). The bound is a generous guard on a loaded machine.
        [TestMethod]
        public void ChoosingAmongFiftyLampsCostsLittle()
        {
            var t = LampTuning.Defaults();
            var candidates = new List<LampCandidate>(50);
            var selector = new LampSelector();
            // Warm up the JIT.
            for (int w = 0; w < 50; w++) Choose(candidates, selector, t, w);
            const int rounds = 2000;
            Stopwatch clock = Stopwatch.StartNew();
            for (int r = 0; r < rounds; r++) Choose(candidates, selector, t, r);
            clock.Stop();
            double perChoiceMs = clock.Elapsed.TotalMilliseconds / rounds;
            Assert.AreEqual(4, selector.Count);
            Assert.IsTrue(perChoiceMs < 0.05, "choosing among 50 lamps took " + perChoiceMs.ToString("0.0000") + " ms");
        }

        private static void Choose(List<LampCandidate> candidates, LampSelector selector, LampTuning t, int round)
        {
            candidates.Clear();
            for (int i = 0; i < 50; i++)
            {
                float intensity = 0.5f + (i % 7) * 0.2f;
                float range = 10f + (i % 11) * 3f;
                float d2 = 4f + ((i * 37 + round) % 400);
                if (!LampMath.IsLamp(intensity, range, ref t)) continue;
                candidates.Add(new LampCandidate(1000 + i, LampMath.Score(intensity, range, d2)));
            }
            selector.Rescore(candidates, 4);
            // A lamp that is no longer wanted leaves the set once it has faded out, as the module steps the fades every frame.
            selector.Step(1f, 0.3f);
        }
    }
}
