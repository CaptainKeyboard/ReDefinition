using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests
{
    // The look of a strong storm: a dense curtain of small flakes with a few large near ones, and the heavier veil at the horizon.
    [TestClass]
    public class StormBlizzardTests
    {
        [TestMethod]
        public void AStrongStormHoldsThousandsOfSmallFlakesAndFewLargeOnes()
        {
            int near = StormLook.FlakesAlive(1.0, StormLook.NearLayer);
            int mid = StormLook.FlakesAlive(1.0, StormLook.MidLayer);
            int far = StormLook.FlakesAlive(1.0, StormLook.FarLayer);
            Assert.IsTrue(mid + far > 10000, "thousands of mid and far flakes: " + (mid + far));
            Assert.IsTrue(near < mid / 4, "the large near flakes are few: " + near + " against " + mid);
            Assert.IsTrue(StormLook.FarLayer.SizeMax <= 0.06f && StormLook.MidLayer.SizeMax <= 0.08f, "far and mid flakes are small");
            Assert.IsTrue(StormLook.FarLayer.SizeMin >= 0.02f);
            Assert.IsTrue(StormLook.FarLayer.Alpha >= 0.95f && StormLook.MidLayer.Alpha >= 0.9f, "bright and opaque against the sky");
            Assert.IsTrue(StormLook.FlakesAlive(0.7, StormLook.FarLayer) > 8000);
        }

        [TestMethod]
        public void TheVeilIsHeavierTowardTheHorizonAndNotAtTheZenith()
        {
            double ground = 0.85;
            double atHorizon = StormLook.HorizonAlpha(ground, 0.0);
            Assert.AreEqual(ground + StormLook.HorizonBoost, atHorizon, 1e-9, "the full boost on the horizon");
            Assert.AreEqual(ground, StormLook.HorizonAlpha(ground, StormLook.HorizonBandSin), 1e-9, "none from the edge of the band");
            Assert.AreEqual(ground, StormLook.HorizonAlpha(ground, 1.0), 1e-9, "none at the zenith");
            Assert.AreEqual(atHorizon, StormLook.HorizonAlpha(ground, 0.0), 1e-12);
            Assert.AreEqual(StormLook.HorizonAlpha(ground, 0.1), StormLook.HorizonAlpha(ground, -0.1), 1e-12, "the same below the horizon");
            double last = 2;
            for (double s = 0; s <= StormLook.HorizonBandSin; s += 0.01)
            {
                double a = StormLook.HorizonAlpha(ground, s);
                Assert.IsTrue(a <= last + 1e-12, "falls with the elevation");
                last = a;
            }
            Assert.IsTrue(StormLook.HorizonAlpha(0.95, 0.0) <= 0.97 + 1e-12, "never above 0.97");
            Assert.AreEqual(0.0, StormLook.HorizonAlpha(0.0, 0.0), 0, "no veil, no boost");
            Assert.IsTrue(StormLook.HorizonAlpha(0.1, 0.0) < 0.1 + StormLook.HorizonBoost * 0.25, "a faint veil gets a faint boost");
        }
    }
}
