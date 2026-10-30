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

        // EngineerFormat.Mass shows 552.255 t as "552.3 t" and 12.525 t as "12.53 t", four figures in tonnes, and 0.95 t
        // as "950 kg", kilograms below one tonne.
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

        // Distances are kilometres, one place after the point below 100 km; a text wraps at spaces and a long word is cut.
        [TestMethod]
        public void DistancesAreKilometresAndTextsWrapAtSpaces()
        {
            Assert.AreEqual("85 km", EngineerFormat.Distance(85000));
            Assert.AreEqual("12.4 km", EngineerFormat.Distance(12400));
            Assert.AreEqual("1,234 km", EngineerFormat.Distance(1234000));
            CollectionAssert.AreEqual(new[] { "aaa bbb", "ccc" }, EngineerFormat.Wrap("aaa bbb ccc", 8));
            CollectionAssert.AreEqual(new[] { "abcdefgh", "ij" }, EngineerFormat.Wrap("abcdefghij", 8));
        }

        // A negative distance keeps its sign and the same places as a positive one.
        [TestMethod]
        public void NegativeDistancesKeepTheirSign()
        {
            Assert.AreEqual("-12.4 km", EngineerFormat.Distance(-12400));
            Assert.AreEqual("-1,234 km", EngineerFormat.Distance(-1234000));
            Assert.AreEqual("0 km", EngineerFormat.Distance(0));
        }

        // A distance that rounds to zero is written as 0 km, never as a negative zero.
        [TestMethod]
        public void TinyNegativeDistanceIsNotNegativeZero()
        {
            Assert.AreEqual("0 km", EngineerFormat.Distance(-40));
            Assert.AreEqual("0 km", EngineerFormat.Distance(-0.0));
        }

        // Repeated, leading and trailing spaces are dropped and never make an empty line.
        [TestMethod]
        public void WrapCollapsesRepeatedSpaces()
        {
            CollectionAssert.AreEqual(new[] { "aaa bbb", "ccc" }, EngineerFormat.Wrap("  aaa    bbb   ccc  ", 8));
            Assert.AreEqual(0, EngineerFormat.Wrap("     ", 20).Count);
        }

        // No text, and null, give no lines.
        [TestMethod]
        public void WrapOfNoTextHasNoLines()
        {
            Assert.AreEqual(0, EngineerFormat.Wrap("", 20).Count);
            Assert.AreEqual(0, EngineerFormat.Wrap(null, 20).Count);
        }

        // A line may be exactly as wide as asked; one more character starts the next line.
        [TestMethod]
        public void WrapFillsALineToItsWidth()
        {
            CollectionAssert.AreEqual(new[] { "aaaa bbb" }, EngineerFormat.Wrap("aaaa bbb", 8));
            CollectionAssert.AreEqual(new[] { "aaaa", "bbbb" }, EngineerFormat.Wrap("aaaa bbbb", 8));
            CollectionAssert.AreEqual(new[] { "abcdefgh" }, EngineerFormat.Wrap("abcdefgh", 8));
        }

        // A word longer than the width is cut into full lines; the short rest starts the next line and takes the following word.
        [TestMethod]
        public void WrapCutsAWordLongerThanTheWidth()
        {
            CollectionAssert.AreEqual(new[] { "ab", "cdefghij", "klmnopqr", "s" }, EngineerFormat.Wrap("ab cdefghijklmnopqrs", 8));
            CollectionAssert.AreEqual(new[] { "abcdefgh", "ijklmnop" }, EngineerFormat.Wrap("abcdefghijklmnop", 8));
            CollectionAssert.AreEqual(new[] { "abcdefgh", "ij kl" }, EngineerFormat.Wrap("abcdefghij kl", 8));
        }

        // A width under eight is taken as eight.
        [TestMethod]
        public void WrapNeverGoesBelowEightCharacters()
        {
            CollectionAssert.AreEqual(new[] { "aaa bbb", "ccc" }, EngineerFormat.Wrap("aaa bbb ccc", 3));
            CollectionAssert.AreEqual(new[] { "aaa bbb", "ccc" }, EngineerFormat.Wrap("aaa bbb ccc", -5));
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
