using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Raytracing;

namespace ReDefinition.Tests
{
    // The clouds' one light from the sun and a moon as the sun sets (CloudLightBlend).
    [TestClass]
    public class CloudLightBlendTests
    {
        // Kerbin's radius in metres, and one degree in radians.
        private const double Kerbin = 600000.0;
        private const double Degree = Math.PI / 180.0;

        // The dip at which the sun leaves a cloud: about 4.9 degrees at 2.2 km on Kerbin, 10.4 at 10 km, none
        // below the ground.
        [TestMethod]
        public void OnKerbinACloudAtTwoKilometresSeesTheSunFiveDegreesDownAndAtTenTen()
        {
            Assert.AreEqual(4.9, CloudLightBlend.Dip(Kerbin, Kerbin + 2200.0) / Degree, 0.1);
            Assert.AreEqual(10.4, CloudLightBlend.Dip(Kerbin, Kerbin + 10000.0) / Degree, 0.1);
            Assert.AreEqual(0.0, CloudLightBlend.Dip(Kerbin, Kerbin - 1.0));
        }

        // The sun's share is full down to the base's dip, none beyond the top's, and falls steadily between.
        [TestMethod]
        public void TheSunLightsAloneDownToTheBaseAndNotBeyondTheTop()
        {
            double lowest = 5.0 * Degree, highest = 10.0 * Degree;
            Assert.AreEqual(1.0, CloudLightBlend.Reach(30.0 * Degree, lowest, highest));
            Assert.AreEqual(1.0, CloudLightBlend.Reach(-5.0 * Degree, lowest, highest), 1e-12);
            Assert.AreEqual(0.0, CloudLightBlend.Reach(-10.0 * Degree, lowest, highest), 1e-12);
            Assert.AreEqual(0.0, CloudLightBlend.Reach(-40.0 * Degree, lowest, highest));
            Assert.AreEqual(0.5, CloudLightBlend.Reach(-7.5 * Degree, lowest, highest), 1e-12);
            for (double e = -10.0; e < -5.0; e += 0.25)
                Assert.IsTrue(CloudLightBlend.Reach((e + 0.25) * Degree, lowest, highest)
                              > CloudLightBlend.Reach(e * Degree, lowest, highest));
        }

        // The farthest clouds of a layer topping out at 10 km see the sun some 21 degrees
        // under the camera's horizon on Kerbin: the moon takes over only by then.
        [TestMethod]
        public void TheSunLeavesTheFarthestCloudsTwiceAsDeep()
        {
            double top = CloudLightBlend.Dip(Kerbin, Kerbin + 10000.0);
            Assert.AreEqual(20.8, CloudLightBlend.FarthestDip(top) / Degree, 0.2);
            Assert.AreEqual(1.0, CloudLightBlend.Reach(-10.0 * Degree, top, CloudLightBlend.FarthestDip(top)), 1e-12);
            Assert.IsTrue(CloudLightBlend.Reach(-15.0 * Degree, top, CloudLightBlend.FarthestDip(top)) > 0.3);
            Assert.AreEqual(0.0, CloudLightBlend.Reach(-21.0 * Degree, top, CloudLightBlend.FarthestDip(top)), 1e-12);
        }

        // From 3 km up the camera sees past the ground's horizon, some 5.7 degrees round
        // the planet further, and the clouds there keep the sun so much longer.
        [TestMethod]
        public void FromAboveTheFarthestCloudsLieFurtherRound()
        {
            double top = CloudLightBlend.Dip(Kerbin, Kerbin + 10000.0);
            double camera = CloudLightBlend.Dip(Kerbin, Kerbin + 3000.0);
            Assert.AreEqual(5.7, camera / Degree, 0.1);
            Assert.AreEqual(CloudLightBlend.FarthestDip(top) + camera, CloudLightBlend.FarthestDip(top, camera), 1e-12);
            Assert.AreEqual(CloudLightBlend.FarthestDip(top), CloudLightBlend.FarthestDip(top, -1.0), 1e-12);
        }

        // With the moon's light per pixel, the one light stays the sun's until the sun
        // reaches no cloud in view, then turns to the moon over two degrees.
        [TestMethod]
        public void TheHandoffStartsWhereTheSunLeavesTheLastCloud()
        {
            double farthest = 20.8 * Degree;
            Assert.AreEqual(0.0, CloudLightBlend.Handoff(5.0 * Degree, farthest));
            Assert.AreEqual(0.0, CloudLightBlend.Handoff(-20.8 * Degree, farthest), 1e-12);
            Assert.AreEqual(0.5, CloudLightBlend.Handoff(-21.8 * Degree, farthest), 1e-9);
            Assert.AreEqual(1.0, CloudLightBlend.Handoff(-22.8 * Degree, farthest), 1e-12);
            Assert.AreEqual(1.0, CloudLightBlend.Handoff(-80.0 * Degree, farthest));
            for (double e = -22.8; e < -20.8; e += 0.1)
                Assert.IsTrue(CloudLightBlend.Handoff(e * Degree, farthest) >= CloudLightBlend.Handoff((e + 0.1) * Degree, farthest));
        }

        // The moon's light per pixel is none from 5 degrees of sun elevation up, full from the horizon down, and
        // half at 2.5 degrees.
        [TestMethod]
        public void TheMoonsLightPerPixelEasesInAsTheSunGoesDown()
        {
            Assert.AreEqual(0.0, CloudLightBlend.Dusk(30.0 * Degree));
            Assert.AreEqual(0.0, CloudLightBlend.Dusk(5.0 * Degree), 1e-12);
            Assert.AreEqual(0.5, CloudLightBlend.Dusk(2.5 * Degree), 1e-9);
            Assert.AreEqual(1.0, CloudLightBlend.Dusk(0.0), 1e-12);
            Assert.AreEqual(1.0, CloudLightBlend.Dusk(-30.0 * Degree));
        }

        // A layer whose base and top see the sun leave at the same dip still fades over the narrowest window
        // of two degrees, not at once.
        [TestMethod]
        public void AThinLayerStillFadesOverTwoDegrees()
        {
            double lowest = 5.0 * Degree;
            Assert.AreEqual(1.0, CloudLightBlend.Reach(-5.0 * Degree, lowest, lowest), 1e-12);
            Assert.AreEqual(0.0, CloudLightBlend.Reach(-7.0 * Degree, lowest, lowest), 1e-12);
            Assert.IsTrue(CloudLightBlend.Reach(-6.0 * Degree, lowest, lowest) > 0.0);
        }

        // As the sun's reach falls from 1 to 0 the light's direction turns steadily towards the moon, unit
        // length throughout: the sun's direction at full reach, the moon's at none.
        [TestMethod]
        public void TheLightMovesFromTheSunToTheMoonWithoutAJump()
        {
            double[] sun = { 1.0, -0.1, 0.0 }, moon = { -0.6, 0.8, 0.0 }, towards = new double[3];
            Normalize(sun);
            double before = -2.0;
            double[] last = null;
            for (int i = 0; i <= 100; i++)
            {
                double reach = 1.0 - i / 100.0;
                CloudLightBlend.Direction(sun, 1.0, moon, 0.006, reach, towards);
                Assert.AreEqual(1.0, Math.Sqrt(towards[0] * towards[0] + towards[1] * towards[1] + towards[2] * towards[2]), 1e-9);
                double towardsMoon = towards[0] * moon[0] + towards[1] * moon[1];
                Assert.IsTrue(towardsMoon >= before - 1e-12);
                before = towardsMoon;
                last = (double[])towards.Clone();
            }
            CloudLightBlend.Direction(sun, 1.0, moon, 0.006, 1.0, towards);
            Assert.AreEqual(sun[0], towards[0], 1e-12);
            Assert.AreEqual(moon[0], last[0], 1e-12);
            Assert.AreEqual(moon[1], last[1], 1e-12);
        }

        // Where neither light has any weight, or the two cancel, the light keeps a direction of unit length
        // along one of them instead of none.
        [TestMethod]
        public void WithoutWeightOrWhereTheyCancelTheStrongerOneStays()
        {
            double[] sun = { 1.0, 0.0, 0.0 }, moon = { -1.0, 0.0, 0.0 }, towards = new double[3];
            CloudLightBlend.Direction(sun, 0.0, moon, 0.0, 0.5, towards);
            Assert.AreEqual(1.0, towards[0]);
            CloudLightBlend.Direction(sun, 1.0, moon, 1.0, 0.5, towards);
            Assert.AreEqual(1.0, Math.Abs(towards[0]), 1e-12);
        }

        // Scales a three-component vector to unit length in place.
        private static void Normalize(double[] v)
        {
            double length = Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);
            for (int i = 0; i < 3; i++) v[i] /= length;
        }
    }
}
