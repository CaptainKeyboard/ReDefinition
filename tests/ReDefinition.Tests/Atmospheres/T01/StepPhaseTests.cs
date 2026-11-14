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
    }
}
