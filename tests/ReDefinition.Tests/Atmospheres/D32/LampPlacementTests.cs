using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests.Atmospheres.D32
{
    // The camera placement of the lamp probe (K-W3 finding: no camera showed the beam, 0 of 8 points, and the probe still printed
    // PASSED): the choice among the placements tried, and the lamp axis it is aimed from.
    [TestClass]
    public class LampPlacementTests
    {
        [TestMethod]
        public void NoPlacementWithTheNeededPointsInViewGivesNone()
        {
            Assert.AreEqual(-1, LampProbeMath.ChoosePlacement(new[] { 0, 3, 6 }, new[] { 0.0, 0.2, 0.4 }, 7, 0.25));
            Assert.AreEqual(-1, LampProbeMath.ChoosePlacement(new int[0], new double[0], 7, 0.25));
            Assert.AreEqual(-1, LampProbeMath.ChoosePlacement(new[] { 8 }, new[] { 0.3, 0.4 }, 7, 0.25), "lists of unequal length");
            Assert.AreEqual(-1, LampProbeMath.ChoosePlacement(null, null, 7, 0.25));
        }

        [TestMethod]
        public void TheFirstGoodPlacementIsTakenAtOnce()
        {
            Assert.AreEqual(1, LampProbeMath.ChoosePlacement(new[] { 8, 8, 8 }, new[] { 0.1, 0.3, 0.6 }, 8, 0.25));
        }

        [TestMethod]
        public void WithoutAGoodSpanTheLongestSpanAmongTheValidOnesWins()
        {
            var inView = new List<int> { 8, 5, 8, 8 };
            var span = new List<double> { 0.10, 0.20, 0.18, 0.05 };
            Assert.AreEqual(2, LampProbeMath.ChoosePlacement(inView, span, 8, 0.25), "index 1 has too few points in view");
        }

        [TestMethod]
        public void AFallbackWithOnePointLessIsFoundByLoweringTheNeed()
        {
            var inView = new[] { 7, 4 };
            var span = new[] { 0.12, 0.5 };
            Assert.AreEqual(-1, LampProbeMath.ChoosePlacement(inView, span, 8, 0.25));
            Assert.AreEqual(0, LampProbeMath.ChoosePlacement(inView, span, 7, 0.25));
        }

        [TestMethod]
        public void TheAxisElevationIsTheAngleAboveTheHorizontal()
        {
            Assert.AreEqual(90.0, LampProbeMath.ElevationDeg(0, 1, 0, 0, 1, 0), 1e-9, "along the vertical");
            Assert.AreEqual(0.0, LampProbeMath.ElevationDeg(1, 0, 0, 0, 1, 0), 1e-9, "horizontal");
            Assert.AreEqual(-45.0, LampProbeMath.ElevationDeg(1, -1, 0, 0, 1, 0), 1e-9, "down and forward");
            Assert.AreEqual(0.0, LampProbeMath.ElevationDeg(0, 0, 0, 0, 1, 0), 1e-9, "no axis");
        }
    }
}
