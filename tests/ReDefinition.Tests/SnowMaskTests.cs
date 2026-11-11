using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests
{
    // The snow mask: monotonic in latitude and height, bounded, the caps stay, the hemispheres run opposite.
    [TestClass]
    public class SnowMaskTests
    {
        private const double Winter = SnowMask.DefaultWinterLineDegrees;

        private static double Cover(double latitude, double height, double phase, double noise = 0)
        {
            return SnowMask.Coverage(latitude, height, phase, noise, Winter);
        }

        [TestMethod]
        public void TheCoverIsAlwaysBetweenZeroAndOne()
        {
            for (double latitude = -90; latitude <= 90; latitude += 3)
                for (double height = -500; height <= 8000; height += 700)
                    for (double phase = 0; phase < 1; phase += 0.05)
                        foreach (double noise in new[] { -1.0, 0, 1.0, 5.0, -5.0 })
                        {
                            double cover = Cover(latitude, height, phase, noise);
                            Assert.IsTrue(cover >= 0 && cover <= 1, latitude + "," + height + "," + phase + "," + noise + ": " + cover);
                        }
        }

        [TestMethod]
        public void TheCoverDoesNotFallTowardThePoleAtAnyPhase()
        {
            for (double phase = 0; phase < 1; phase += 0.05)
            {
                double previous = 0;
                for (double latitude = 0; latitude <= 90; latitude += 0.5)
                {
                    double cover = Cover(latitude, 100, phase);
                    Assert.IsTrue(cover >= previous - 1e-12, "latitude " + latitude + " phase " + phase);
                    previous = cover;
                }
            }
        }

        [TestMethod]
        public void TheCoverDoesNotFallWithHeight()
        {
            for (double phase = 0; phase < 1; phase += 0.1)
                foreach (double latitude in new[] { 10.0, 30, 45, 60, 75, -45, -60 })
                {
                    double previous = 0;
                    for (double height = 0; height <= 8000; height += 100)
                    {
                        double cover = Cover(latitude, height, phase);
                        Assert.IsTrue(cover >= previous - 1e-12, "height " + height);
                        previous = cover;
                    }
                }
        }

        [TestMethod]
        public void HeightMovesTheLineTowardTheEquator()
        {
            // Winter in the north (phase 0.06 after the lag): the line is at its winter latitude.
            double low = Cover(40, 0, SnowMask.LagYears);
            double high = Cover(40, 3000, SnowMask.LagYears);
            Assert.IsTrue(high > low, low + " vs " + high);
            Assert.AreEqual(0, Cover(10, 0, SnowMask.LagYears), 0);
        }

        [TestMethod]
        public void TheLineIsAtItsWinterLatitudeInWinterAndItsSummerLatitudeInSummer()
        {
            Assert.AreEqual(Winter, SnowMask.LineDegrees(SnowMask.LagYears, Winter), 1e-9);
            Assert.AreEqual(2 * SnowMask.MeanLineDegrees - Winter, SnowMask.LineDegrees(SnowMask.LagYears + 0.5, Winter), 1e-9);
            Assert.AreEqual(SnowMask.MeanLineDegrees, SnowMask.LineDegrees(SnowMask.LagYears + 0.25, Winter), 1e-9);
        }

        [TestMethod]
        public void ThePolarCapsStayInEveryHemisphereAtEveryPhase()
        {
            for (double phase = 0; phase < 1; phase += 0.05)
            {
                Assert.AreEqual(1, Cover(90, 0, phase), 1e-12);
                Assert.AreEqual(1, Cover(-90, 0, phase), 1e-12);
                Assert.AreEqual(1, Cover(83, 0, phase), 1e-12);
                Assert.AreEqual(1, Cover(-83, 0, phase), 1e-12);
            }
        }

        [TestMethod]
        public void TheHemispheresHaveOppositeSeasons()
        {
            double northWinter = SnowMask.LagYears;
            double southWinter = SnowMask.LagYears + 0.5;
            // At 50 degrees: snow in the winter hemisphere, bare in the summer one.
            Assert.IsTrue(Cover(50, 0, northWinter) > 0.9);
            Assert.AreEqual(0, Cover(-50, 0, northWinter), 1e-12);
            Assert.AreEqual(0, Cover(50, 0, southWinter), 1e-12);
            Assert.IsTrue(Cover(-50, 0, southWinter) > 0.9);
            // The same latitude in both hemispheres, half a year apart, is the same cover.
            for (double phase = 0; phase < 1; phase += 0.07)
                Assert.AreEqual(Cover(55, 400, phase), Cover(-55, 400, phase + 0.5), 1e-9);
        }

        [TestMethod]
        public void TheEquatorStaysBareAndTheYearIsPeriodic()
        {
            for (double phase = 0; phase < 1; phase += 0.05)
            {
                Assert.AreEqual(0, Cover(0, 0, phase), 1e-12);
                Assert.AreEqual(Cover(52, 200, phase), Cover(52, 200, phase + 1), 1e-9);
            }
        }

        [TestMethod]
        public void NoiseMovesTheLineByAtMostItsAmplitude()
        {
            double phase = SnowMask.LagYears;
            double line = SnowMask.LineDegrees(phase, Winter);
            Assert.IsTrue(Cover(line, 0, phase, 1) > Cover(line, 0, phase, 0));
            Assert.IsTrue(Cover(line, 0, phase, -1) < Cover(line, 0, phase, 0));
            // Past the amplitude the noise does nothing more.
            Assert.AreEqual(Cover(line, 0, phase, 1), Cover(line, 0, phase, 7), 1e-12);
        }

        [TestMethod]
        public void TheLatitudeLineBetweenTheHemispheresAndBeyondThePoles()
        {
            foreach (double phase in new[] { 0.0, 0.25, 0.5, 0.8 })
            {
                // Latitude 0 and -0 are both the northern side and both bare.
                Assert.AreEqual(Cover(0, 0, phase), Cover(-0.0, 0, phase));
                Assert.AreEqual(0, Cover(-0.0, 0, phase), 1e-12);
                // Beyond the pole counts as the pole.
                Assert.AreEqual(Cover(90, 0, phase), Cover(95, 0, phase));
                Assert.AreEqual(Cover(-90, 0, phase), Cover(-400, 0, phase));
                Assert.AreEqual(1.0, Cover(90, 0, phase), 1e-12);
                Assert.AreEqual(0, Cover(double.NaN, 0, phase));
            }
            Assert.AreEqual(0, SnowMask.Coverage(50, 0, double.NaN, 0, Winter), "a phase that is not a number gives no snow");
            Assert.AreEqual(0, SnowMask.Coverage(50, 0, 0, double.NaN, Winter), "a noise that is not a number gives no snow");
            Assert.AreEqual(1.0, Cover(double.PositiveInfinity, 0, 0.3), 1e-12, "infinity is the pole");
        }

        [TestMethod]
        public void ANotANumberGivesNoSnowAndAWinterLineOutsideItsRangeIsBroughtIn()
        {
            Assert.AreEqual(0, SnowMask.Coverage(double.NaN, 0, 0, 0, Winter));
            Assert.AreEqual(0, SnowMask.Coverage(50, double.NaN, 0, 0, Winter));
            Assert.AreEqual(SnowMask.LineDegrees(0.3, SnowMask.HighestWinterLineDegrees), SnowMask.LineDegrees(0.3, 90), 1e-12);
            Assert.AreEqual(SnowMask.LineDegrees(0.3, SnowMask.LowestWinterLineDegrees), SnowMask.LineDegrees(0.3, 0), 1e-12);
        }
    }
}
