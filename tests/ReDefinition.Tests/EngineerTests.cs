using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Engineer;

namespace ReDefinition.Tests
{
    // The planner's numbers: how they are written (EngineerFormat) and the rocket
    // equation (StageTable).
    [TestClass]
    public class EngineerTests
    {
        // Speeds are whole metres per second with thousands separated by commas; NaN is written as 0.
        [TestMethod]
        public void SpeedsAreWholeMetresPerSecondWithThousands()
        {
            Assert.AreEqual("6,008 m/s", EngineerFormat.Speed(6008.4));
            Assert.AreEqual("0 m/s", EngineerFormat.Speed(double.NaN));
        }

        // Masses are tonnes with four figures, kilograms below one tonne.
        [TestMethod]
        public void MassesAreTonnesAndKilogramsBelowOne()
        {
            Assert.AreEqual("552.3 t", EngineerFormat.Mass(552.255));
            Assert.AreEqual("12.53 t", EngineerFormat.Mass(12.525));
            Assert.AreEqual("950 kg", EngineerFormat.Mass(0.95));
        }

        // Forces are kilonewtons with four figures, meganewtons from a thousand kilonewtons on.
        [TestMethod]
        public void ForcesAreKilonewtonsAndMeganewtonsFromAThousand()
        {
            Assert.AreEqual("14.78 kN", EngineerFormat.Force(14.783));
            Assert.AreEqual("791.6 kN", EngineerFormat.Force(791.59));
            Assert.AreEqual("4.00 MN", EngineerFormat.Force(4000));
        }

        // Times are tenths of seconds below a minute, then minutes and seconds, then hours, minutes and seconds;
        // an infinite time is written as none.
        [TestMethod]
        public void TimesAreSecondsThenMinutesThenHours()
        {
            Assert.AreEqual("23.7s", EngineerFormat.Time(23.7));
            Assert.AreEqual("1m 53s", EngineerFormat.Time(113));
            Assert.AreEqual("1h 02m 03s", EngineerFormat.Time(3723));
            Assert.AreEqual("0.0s", EngineerFormat.Time(double.PositiveInfinity));
        }

        // Delta-v is Isp times KSP's standard gravity of 9.80665 m/s^2 times the log of the mass ratio, and none
        // where the mass does not fall.
        [TestMethod]
        public void RocketDeltaVIsTsiolkovskyWithKspsStandardGravity()
        {
            // 300 s, from 10 t to 5 t: 300 * 9.80665 * ln 2.
            Assert.AreEqual(2039.2, StageTable.RocketDeltaV(300, 10, 5), 0.1);
            Assert.AreEqual(0.0, StageTable.RocketDeltaV(300, 5, 10), "no propellant burnt");
        }
    }
}
