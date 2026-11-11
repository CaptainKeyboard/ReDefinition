using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests
{
    // What is left of the legacy storm effect's pure parts: the terrain cull distance, the legacy step range and the sun terms of StormLook.
    // They go with StormEffect; the curtain's tests are CurtainTests.
    [TestClass]
    public class StormVolumeTests
    {
        [TestMethod]
        public void TheCullDistanceAndTheStepRangeFollowTheirRules()
        {
            Assert.AreEqual(300.0, StormVolumeMath.CullDistanceM(60), 1e-9, "at least 300 m");
            Assert.AreEqual(1000.0, StormVolumeMath.CullDistanceM(200), 1e-9, "5 times the visibility");
            Assert.AreEqual(16, StormVolumeMath.Steps(1));
            Assert.AreEqual(28, StormVolumeMath.Steps(28));
            Assert.AreEqual(48, StormVolumeMath.Steps(500));
        }

        [TestMethod]
        public void TheSunLightsTheVeilOnlyWhileItIsUp()
        {
            Assert.AreEqual(0.0, StormLook.SunVisibility(-0.2), 1e-9, "no sun term at night");
            Assert.AreEqual(1.0, StormLook.SunVisibility(0.3), 1e-9, "the full sun term by day");
            double last = 0;
            for (double sin = -0.2; sin <= 0.3; sin += 0.01)
            {
                double now = StormLook.SunVisibility(sin);
                Assert.IsTrue(now >= last - 1e-12, "it rises with the sun");
                last = now;
            }
            Assert.AreEqual(1.0, StormLook.NightShare(-0.3), 1e-9, "blue-grey at night");
            Assert.AreEqual(0.0, StormLook.NightShare(0.2), 1e-9, "the day's grey by day");
            Assert.AreEqual(1.0, StormLook.WarmShare(0.0), 1e-9, "warm at the horizon");
            Assert.AreEqual(0.0, StormLook.WarmShare(0.5), 1e-9, "white high up");
        }
    }
}
