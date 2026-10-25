using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Flight;

namespace ReDefinition.Tests
{
    // The altimeter's rule (AutoAltimeter.Rule): the ground below 5 km and on it,
    // the sea above 6 km, and between them what it was.
    [TestClass]
    public class AutoAltimeterTests
    {
        // In flight below 5 km it switches to the height above the ground, above 6 km to the height above the sea.
        [TestMethod]
        public void GroundBelowLowSeaAboveHigh()
        {
            Assert.AreEqual(AltimeterDisplayState.AGL, AutoAltimeter.Rule(false, 4000, AltimeterDisplayState.ASL));
            Assert.AreEqual(AltimeterDisplayState.ASL, AutoAltimeter.Rule(false, 7000, AltimeterDisplayState.AGL));
        }

        // Between 5 and 6 km it keeps the mode it had, so it does not flip back and forth around one height;
        // from KSP's DEFAULT it takes the sea.
        [TestMethod]
        public void BetweenKeepsWhatItWas()
        {
            Assert.AreEqual(AltimeterDisplayState.AGL, AutoAltimeter.Rule(false, 5500, AltimeterDisplayState.AGL));
            Assert.AreEqual(AltimeterDisplayState.ASL, AutoAltimeter.Rule(false, 5500, AltimeterDisplayState.ASL));
            Assert.AreEqual(AltimeterDisplayState.ASL, AutoAltimeter.Rule(false, 5500, AltimeterDisplayState.DEFAULT));
        }

        // On the ground the altimeter shows the height above it, however high the ground lies.
        [TestMethod]
        public void OnTheGroundIsTheGround()
        {
            Assert.AreEqual(AltimeterDisplayState.AGL, AutoAltimeter.Rule(true, 70000, AltimeterDisplayState.ASL));
        }
    }
}
