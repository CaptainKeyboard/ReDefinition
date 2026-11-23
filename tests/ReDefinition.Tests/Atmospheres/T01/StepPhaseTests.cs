using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests.Atmospheres.T01
{
    // The frame's phase of the layer march's step offset: the golden-ratio sequence, in [0, 1).
    [TestClass]
    public class StepPhaseTests
    {
        [TestMethod]
        public void ThePhaseStaysInTheUnitInterval()
        {
            for (int frame = 0; frame < 5000; frame++)
            {
                double p = StepPhase.Of(frame);
                Assert.IsTrue(p >= 0.0 && p < 1.0, "frame " + frame + " gave " + p);
            }
        }

        [TestMethod]
        public void ThePhaseIsTheFractionOfTheFrameTimesTheGoldenRatio()
        {
            foreach (int frame in new[] { 0, 1, 2, 7, 100, 123456 })
            {
                double expected = frame * 0.6180339887498949;
                expected -= Math.Floor(expected);
                Assert.AreEqual(expected, StepPhase.Of(frame), 1e-12);
            }
            Assert.AreEqual(StepPhase.Of(0), StepPhase.Of(-5), "a negative frame counts as 0");
        }

        // Under the 4 x 4 rotation each cell of the block is marched once in 16 frames (ReconPattern.BlockOffset): its successive offsets must step by the golden ratio
        // (the pixel's own samples), and every cell of a cycle must get the same phase, so that no cell keeps a bias of its own (the grid of PRUEFPROTOKOLL 137).
        [TestMethod]
        public void UnderTheRotationEachCellsSamplesFollowTheGoldenRatio()
        {
            const int n = 4, cycle = n * n;
            for (int cell = 0; cell < cycle; cell++)
            {
                int first = -1;
                for (int index = 0; index < cycle; index++)
                {
                    int x, y;
                    ReconPattern.BlockOffset(n, index, out x, out y);
                    if (x + y * n == cell) first = index;
                }
                Assert.IsTrue(first >= 0, "cell " + cell + " is visited in the first cycle");
                for (int m = 0; m < 6; m++)
                    Assert.AreEqual(StepPhase.Of(m), StepPhase.OfSample(first + m * cycle, cycle), 1e-12, "cell " + cell + ", its sample " + m);
            }
            Assert.AreEqual(StepPhase.Of(0), StepPhase.OfSample(15, cycle), 1e-12, "every cell of the first cycle shares the phase");
            Assert.AreEqual(StepPhase.Of(7), StepPhase.OfSample(7, 1), 1e-12, "a cycle of one is the frame's phase");
        }
    }
}
