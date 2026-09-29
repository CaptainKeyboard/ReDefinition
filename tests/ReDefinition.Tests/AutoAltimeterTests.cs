using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Flight;

namespace ReDefinition.Tests
{
    // The altimeter's rule (AutoAltimeter.Rule): the ground below 5 km and on it,
    // the sea above 6 km, and between them what it was.
    [TestClass]
    public class AutoAltimeterTests
    {
        [TestMethod]
        public void GroundBelowLowSeaAboveHigh()
        {
            Assert.AreEqual(AltimeterDisplayState.AGL, AutoAltimeter.Rule(false, 4000, AltimeterDisplayState.ASL));
            Assert.AreEqual(AltimeterDisplayState.ASL, AutoAltimeter.Rule(false, 7000, AltimeterDisplayState.AGL));
        }

        [TestMethod]
        public void BetweenKeepsWhatItWas()
        {
            Assert.AreEqual(AltimeterDisplayState.AGL, AutoAltimeter.Rule(false, 5500, AltimeterDisplayState.AGL));
            Assert.AreEqual(AltimeterDisplayState.ASL, AutoAltimeter.Rule(false, 5500, AltimeterDisplayState.ASL));
            Assert.AreEqual(AltimeterDisplayState.ASL, AutoAltimeter.Rule(false, 5500, AltimeterDisplayState.DEFAULT));
        }

        [TestMethod]
        public void OnTheGroundIsTheGround()
        {
            Assert.AreEqual(AltimeterDisplayState.AGL, AutoAltimeter.Rule(true, 70000, AltimeterDisplayState.ASL));
        }
    }
}
