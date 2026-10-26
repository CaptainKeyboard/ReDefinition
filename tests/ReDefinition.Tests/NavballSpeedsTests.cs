using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Hud;

namespace ReDefinition.Tests
{
    // How the navball's speed fields write a speed (NavballSpeeds.Speed).
    [TestClass]
    public class NavballSpeedsTests
    {
        // Below 100 m/s either way a speed is written with tenths, above it in whole metres per second.
        [TestMethod]
        public void TenthsBelowAHundredWholeAbove()
        {
            Assert.AreEqual("12.3m/s", NavballSpeeds.Speed(12.34));
            Assert.AreEqual("-4.6m/s", NavballSpeeds.Speed(-4.56));
            Assert.AreEqual("123m/s", NavballSpeeds.Speed(123.4));
            Assert.AreEqual("-2290m/s", NavballSpeeds.Speed(-2290.2));
        }

        // A small negative speed that rounds to nothing is written without a minus sign.
        [TestMethod]
        public void NothingIsNeverNegative()
        {
            Assert.AreEqual("0.0m/s", NavballSpeeds.Speed(-0.01));
        }
    }
}
