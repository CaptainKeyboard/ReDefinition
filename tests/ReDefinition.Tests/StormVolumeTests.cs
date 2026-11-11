using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests
{
    // What is left of the legacy storm effect's pure parts: the terrain cull distance, the legacy step range, the sun terms of StormLook and the
    // disc EveStormMath erases. They go with StormEffect (C25); the curtain's tests are CurtainTests.
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

        [TestMethod]
        public void TheClearedDiscTakesTheCoverageAwayInsideAndKeepsItOutside()
        {
            const int width = 64, height = 32;
            var rect = new ReDefinition.Atmospheres.EveCoverageMath.Rect { Col0 = 0, Cols = width, Row0 = 0, Rows = height };
            var coverage = new byte[width * height];
            for (int i = 0; i < coverage.Length; i++) coverage[i] = 200;
            // The centre looks along +z at the equator: column width / 2 is longitude 0, row height / 2 is latitude 0.
            EveStormMath.EraseDisc(coverage, rect, width, height, 0, 0, 1, 300000.0, 600000.0);
            Assert.AreEqual(0, coverage[(height / 2) * width + width / 2], "gone at the centre");
            Assert.AreEqual(200, coverage[0], "kept at the pole, 90 degrees away");
            int kept = 0;
            for (int i = 0; i < coverage.Length; i++) if (coverage[i] == 200) kept++;
            Assert.IsTrue(kept > coverage.Length / 2, "most of the map is outside a disc of half a radian");
        }
    }
}
