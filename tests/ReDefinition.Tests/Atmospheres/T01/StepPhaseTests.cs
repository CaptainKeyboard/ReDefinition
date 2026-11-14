using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests.Atmospheres.T01
{
    // The frame's phase of the layer march's step offset (E40 L2 E1): every sequence stays in [0, 1), the golden one is the one the pass always had, the ordered ones
    // are what they say they are.
    [TestClass]
    public class StepPhaseTests
    {
        [TestMethod]
        public void EverySequenceStaysInTheUnitInterval()
        {
            for (int sequence = 1; sequence <= StepPhase.Count; sequence++)
                for (int frame = 0; frame < 5000; frame++)
                {
                    double p = StepPhase.Of(sequence, frame);
                    Assert.IsTrue(p >= 0.0 && p < 1.0, "sequence " + sequence + " frame " + frame + " gave " + p);
                }
        }

        [TestMethod]
        public void TheGoldenSequenceIsTheFractionOfTheFrameTimesTheGoldenRatio()
        {
            foreach (int frame in new[] { 0, 1, 2, 7, 100, 123456 })
            {
                double expected = frame * 0.6180339887498949;
                expected -= Math.Floor(expected);
                Assert.AreEqual(expected, StepPhase.Of(StepPhase.Golden, frame), 1e-12);
            }
            Assert.AreEqual(StepPhase.Of(StepPhase.Golden, 33), StepPhase.Of(0, 33), "an unknown sequence is the golden one");
            Assert.AreEqual(StepPhase.Of(StepPhase.Count, 33), StepPhase.Of(99, 33), "an unknown sequence above the last clamps to the last");
        }

        [TestMethod]
        public void TheRadicalInverseMirrorsTheBitsOfTheFrame()
        {
            Assert.AreEqual(0.0, StepPhase.Of(StepPhase.RadicalInverse, 0), 1e-12);
            Assert.AreEqual(0.5, StepPhase.Of(StepPhase.RadicalInverse, 1), 1e-12);
            Assert.AreEqual(0.25, StepPhase.Of(StepPhase.RadicalInverse, 2), 1e-12);
            Assert.AreEqual(0.75, StepPhase.Of(StepPhase.RadicalInverse, 3), 1e-12);
            Assert.AreEqual(0.125, StepPhase.Of(StepPhase.RadicalInverse, 4), 1e-12);
        }

        [TestMethod]
        public void EveryBlockOfEightFramesOfTheShuffledSequenceHoldsOnePhaseInEachEighth()
        {
            for (int block = 0; block < 200; block++)
            {
                var seen = new bool[StepPhase.Block];
                for (int i = 0; i < StepPhase.Block; i++)
                {
                    int stratum = (int)(StepPhase.Of(StepPhase.ShuffledStrata, block * StepPhase.Block + i) * StepPhase.Block);
                    Assert.IsFalse(seen[stratum], "block " + block + " has two phases in the eighth " + stratum);
                    seen[stratum] = true;
                }
            }
        }

        [TestMethod]
        public void TheShuffledAndTheBlockSequencesAreNotTheSameInEveryBlock()
        {
            // The order of the strata changes from block to block (it is what keeps a slow harmonic from carrying over).
            var orders = new System.Collections.Generic.HashSet<string>();
            for (int block = 0; block < 50; block++)
            {
                string order = "";
                for (int i = 0; i < StepPhase.Block; i++) order += (int)(StepPhase.Of(StepPhase.ShuffledStrata, block * StepPhase.Block + i) * StepPhase.Block);
                orders.Add(order);
            }
            Assert.IsTrue(orders.Count > 8, "the order of the strata repeats: " + orders.Count + " different in 50 blocks");
            Assert.AreNotEqual(StepPhase.Of(StepPhase.GoldenBlocks, 1) - StepPhase.Of(StepPhase.GoldenBlocks, 0), StepPhase.Of(StepPhase.GoldenBlocks, 9) - StepPhase.Of(StepPhase.GoldenBlocks, 8), 1e-9);
        }

        [TestMethod]
        public void TheShiftIsZeroWithoutAScaleAndWrapsAtLongSessions()
        {
            Assert.AreEqual(0f, StepPhase.Shift(12345, 0f));
            Assert.AreEqual(3f, StepPhase.Shift(3, 1f), 1e-6f);
            Assert.AreEqual(StepPhase.Shift(5, 0.5f), StepPhase.Shift(5 + 4096, 0.5f), 1e-6f);
        }

        private static int tuningFrame = 910000;

        private static void NextFrame()
        {
            Tuning.Instance.BeginFrame(++tuningFrame);
        }

        [TestCleanup]
        public void Clean()
        {
            Tuning.ClearOverrides();
            NextFrame();
        }

        [TestMethod]
        public void TheKeysDefaultToTheGoldenSequenceAndAStillPatternAndClamp()
        {
            Tuning.ClearOverrides();
            NextFrame();
            var frame = new LayerFrame();
            Assert.AreEqual(StepPhase.Golden, frame.DitherPhase);
            Assert.AreEqual(0f, frame.DitherShift);
            Assert.AreEqual(StepPhase.Golden, OwnCloudOptions.DitherPhaseDefault);
            Func<string, double> defaults = key =>
            {
                SettingDef def = AtmospheresSettings.Definition(key);
                return def.Kind == SettingKind.Text ? 0.0 : def.Default;
            };
            OwnCloudOptions.Apply(frame, defaults);
            Assert.AreEqual(StepPhase.Golden, frame.DitherPhase);
            Assert.AreEqual(0f, frame.DitherShift);
            Tuning.SetOverride(OwnCloudOptions.TuneDitherPhase, 3f);
            Tuning.SetOverride(OwnCloudOptions.TuneDitherShift, 0.25f);
            NextFrame();
            OwnCloudOptions.Apply(frame, defaults);
            Assert.AreEqual(3, frame.DitherPhase);
            Assert.AreEqual(0.25f, frame.DitherShift, 1e-6f);
            Tuning.SetOverride(OwnCloudOptions.TuneDitherPhase, 99f);
            NextFrame();
            OwnCloudOptions.Apply(frame, defaults);
            Assert.AreEqual(StepPhase.Count, frame.DitherPhase);
            Tuning.SetOverride(OwnCloudOptions.TuneDitherPhase, -4f);
            NextFrame();
            OwnCloudOptions.Apply(frame, defaults);
            Assert.AreEqual(1, frame.DitherPhase);
        }
    }
}
