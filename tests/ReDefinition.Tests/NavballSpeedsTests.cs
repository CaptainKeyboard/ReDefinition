using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Flight;

namespace ReDefinition.Tests
{
    // How the navball's speed fields write a speed (NavballSpeeds.Speed).
    [TestClass]
    public class NavballSpeedsTests
    {
        [TestMethod]
        public void TenthsBelowAHundredWholeAbove()
        {
            Assert.AreEqual("12.3m/s", NavballSpeeds.Speed(12.34));
            Assert.AreEqual("-4.6m/s", NavballSpeeds.Speed(-4.56));
            Assert.AreEqual("123m/s", NavballSpeeds.Speed(123.4));
            Assert.AreEqual("-2290m/s", NavballSpeeds.Speed(-2290.2));
        }

        [TestMethod]
        public void NothingIsNeverNegative()
        {
            Assert.AreEqual("0.0m/s", NavballSpeeds.Speed(-0.01));
        }
    }
}
