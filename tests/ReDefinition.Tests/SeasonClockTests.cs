using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests
{
    // The season phase: from the time and the year length, for both hemispheres.
    [TestClass]
    public class SeasonClockTests
    {
        private const double Year = 9203544.6;

        [TestMethod]
        public void ThePhaseStartsAtZeroAndReachesOneHalfAtHalfAYear()
        {
            Assert.AreEqual(0, SeasonClock.Phase(0, Year), 1e-12);
            Assert.AreEqual(0.25, SeasonClock.Phase(Year / 4, Year), 1e-12);
            Assert.AreEqual(0.5, SeasonClock.Phase(Year / 2, Year), 1e-12);
        }

        [TestMethod]
        public void ThePhaseIsContinuousAndNeverLeavesZeroToOne()
        {
            double previous = SeasonClock.Phase(0, Year);
            for (int i = 1; i <= 4000; i++)
            {
                double phase = SeasonClock.Phase(i * Year / 1000, Year);
                Assert.IsTrue(phase >= 0 && phase < 1, "phase " + phase);
                double step = Math.Abs(phase - previous);
                // One step is a thousandth of a year; the only jump is the wrap from near 1 to 0.
                Assert.IsTrue(step < 0.0011 || step > 0.9989, "jump " + step + " at step " + i);
                previous = phase;
            }
        }

        [TestMethod]
        public void ThePhaseWrapsAfterOneYearAndForTimesBeforeZero()
        {
            Assert.AreEqual(SeasonClock.Phase(1234567, Year), SeasonClock.Phase(1234567 + 3 * Year, Year), 1e-9);
            Assert.AreEqual(0.75, SeasonClock.Phase(-Year / 4, Year), 1e-12);
            Assert.AreEqual(0, SeasonClock.Phase(Year, Year), 1e-12);
        }

        [TestMethod]
        public void TheHemispheresAreHalfAYearApart()
        {
            for (double phase = 0; phase < 1; phase += 0.037)
            {
                double north = SeasonClock.HemispherePhase(phase, true);
                double south = SeasonClock.HemispherePhase(phase, false);
                Assert.AreEqual(phase, north, 1e-12);
                double apart = SeasonClock.Wrap(south - north);
                Assert.AreEqual(0.5, apart, 1e-12);
                Assert.IsTrue(south >= 0 && south < 1);
            }
        }

        [TestMethod]
        public void TheYearLengthFactorScalesTheYear()
        {
            Assert.AreEqual(Year, SeasonClock.YearSeconds(Year, 1), 1e-6);
            Assert.AreEqual(Year / 10, SeasonClock.YearSeconds(Year, 0.1), 1e-6);
            // At a tenth of the year a tenth of the time gives the same phase.
            Assert.AreEqual(SeasonClock.Phase(Year / 8, Year), SeasonClock.Phase(Year / 80, SeasonClock.YearSeconds(Year, 0.1)), 1e-12);
        }

        [TestMethod]
        public void AFactorThatIsNotUsableCountsAsOneAndAnEmptyYearGivesZero()
        {
            Assert.AreEqual(Year, SeasonClock.YearSeconds(Year, 0), 1e-6);
            Assert.AreEqual(Year, SeasonClock.YearSeconds(Year, -2), 1e-6);
            Assert.AreEqual(Year, SeasonClock.YearSeconds(Year, double.NaN), 1e-6);
            Assert.AreEqual(Year, SeasonClock.YearSeconds(Year, double.PositiveInfinity), 1e-6);
            Assert.AreEqual(0, SeasonClock.Phase(1000, 0));
            Assert.AreEqual(0, SeasonClock.Phase(double.NaN, Year));
        }

        [TestMethod]
        public void TheEdgesOfTheWrapStayInsideTheYear()
        {
            Assert.AreEqual(0.0, SeasonClock.Wrap(-1e-17), "a hair below 0 rounds to 1 and counts as 0");
            Assert.AreEqual(0.0, SeasonClock.Wrap(1.0));
            Assert.AreEqual(0.0, SeasonClock.Wrap(-3.0));
            Assert.AreEqual(0.75, SeasonClock.Wrap(-0.25), 1e-12);
            Assert.AreEqual(0.0, SeasonClock.Wrap(double.NaN), "not a number is 0, not NaN");
            Assert.AreEqual(0.0, SeasonClock.Wrap(double.PositiveInfinity));
            Assert.AreEqual(0.0, SeasonClock.Wrap(double.NegativeInfinity));
            Assert.AreEqual(0.0, SeasonClock.Phase(double.PositiveInfinity, Year));
            Assert.AreEqual(0.0, SeasonClock.Phase(double.NegativeInfinity, Year));
            Assert.AreEqual(0.0, SeasonClock.Phase(1000, double.PositiveInfinity));
            Assert.AreEqual(0.0, SeasonClock.HemispherePhase(double.NaN, true));
            Assert.AreEqual(0.0, SeasonClock.HemispherePhase(double.NaN, false), "not a number is 0 on both sides");
            Assert.AreEqual(0.0, SeasonClock.PhaseDistance(double.NaN, 0.0));
        }

        [TestMethod]
        public void TheModelsClockDecidesThePhase()
        {
            var orbit = new SeasonModel(SeasonClockKind.Orbit, 0, Year, 0, 0.5, false);
            Assert.AreEqual(0.25, SeasonClock.Phase(Year / 4, orbit), 1e-12);
            var fixedClock = new SeasonModel(SeasonClockKind.Fixed, 0.3, Year, 0, 0.5, false);
            Assert.AreEqual(0.3, SeasonClock.Phase(Year / 4, fixedClock), 1e-12);
            Assert.AreEqual(0.0, SeasonClock.Phase(Year / 4, new SeasonModel(SeasonClockKind.Constant, 0, Year, 0, 0.5, false)));
            Assert.AreEqual(0.0, SeasonClock.Phase(Year / 4, SeasonModel.None));
            Assert.AreEqual(0.0, SeasonClock.Phase(Year / 4, (SeasonModel)null));
        }

        [TestMethod]
        public void TheYearFactorOfAModelScalesItsPhase()
        {
            var orbit = new SeasonModel(SeasonClockKind.Orbit, 0, Year, 0, 0.5, false);
            Assert.AreEqual(SeasonClock.Phase(Year / 8, orbit), SeasonClock.Phase(Year / 80, orbit.WithYearFactor(0.1)), 1e-12);
            Assert.AreEqual(Year, orbit.WithYearFactor(double.NaN).YearSeconds, 1e-6);
        }

        [TestMethod]
        public void ATiltedBodyIsHalfAYearApartWhateverIsConfiguredAndAnUntiltedEccentricOneHasOneYear()
        {
            var tilted = new SeasonModel(SeasonClockKind.Orbit, 0, Year, 25, 0.3, false);
            var eccentric = new SeasonModel(SeasonClockKind.Orbit, 0, Year, 0, 0.5, true);
            var artificial = new SeasonModel(SeasonClockKind.Orbit, 0, Year, 0, 0.5, false);
            Assert.AreEqual(0.5, tilted.EffectiveHemisphereOffset, 1e-12);
            Assert.AreEqual(0.0, eccentric.EffectiveHemisphereOffset, 1e-12);
            Assert.AreEqual(0.5, artificial.EffectiveHemisphereOffset, 1e-12);
            Assert.AreEqual(0.5, SeasonClock.Wrap(SeasonClock.HemispherePhase(0.2, false, tilted) - 0.2), 1e-12);
            Assert.AreEqual(0.0, SeasonClock.Wrap(SeasonClock.HemispherePhase(0.2, false, eccentric) - 0.2), 1e-12);
        }

        [TestMethod]
        public void TheSubsolarLatitudeFollowsTheTiltAndIsHighestAtMidsummer()
        {
            var tilted = new SeasonModel(SeasonClockKind.Orbit, 0, Year, 25, 0.5, false);
            Assert.AreEqual(-25.0, tilted.SubsolarLatitudeDegrees(0.0), 1e-9, "northern midwinter: the sun is over the southern tropic");
            Assert.AreEqual(25.0, tilted.SubsolarLatitudeDegrees(0.5), 1e-9);
            Assert.AreEqual(0.0, tilted.SubsolarLatitudeDegrees(0.25), 1e-9);
            Assert.AreEqual(0.0, new SeasonModel(SeasonClockKind.Orbit, 0, Year, 0, 0.5, false).SubsolarLatitudeDegrees(0.5));
        }

#pragma warning disable CS0618
        [TestMethod]
        public void TheObsoleteSingleBodyShimsStillAnswer()
        {
            Assert.IsTrue(SeasonClock.Applies(SeasonClock.BodyName));
            Assert.IsFalse(SeasonClock.Applies("Mun"));
            Assert.IsFalse(SeasonClock.Applies(null));
            Assert.AreEqual(0.5, SeasonClock.SouthernOffset);
        }
#pragma warning restore CS0618
    }
}
