using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Raytracing;

namespace ReDefinition.Tests
{
    // The clouds' one light from the sun and a moon as the sun sets (CloudLightBlend).
    [TestClass]
    public class CloudLightBlendTests
    {
        private const double Kerbin = 600000.0;
        private const double Degree = Math.PI / 180.0;

        [TestMethod]
        public void OnKerbinACloudAtTwoKilometresSeesTheSunFiveDegreesDownAndAtTenTen()
        {
            Assert.AreEqual(4.9, CloudLightBlend.Dip(Kerbin, Kerbin + 2200.0) / Degree, 0.1);
            Assert.AreEqual(10.4, CloudLightBlend.Dip(Kerbin, Kerbin + 10000.0) / Degree, 0.1);
            Assert.AreEqual(0.0, CloudLightBlend.Dip(Kerbin, Kerbin - 1.0));
        }

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

        [TestMethod]
        public void AThinLayerStillFadesOverTwoDegrees()
        {
            double lowest = 5.0 * Degree;
            Assert.AreEqual(1.0, CloudLightBlend.Reach(-5.0 * Degree, lowest, lowest), 1e-12);
            Assert.AreEqual(0.0, CloudLightBlend.Reach(-7.0 * Degree, lowest, lowest), 1e-12);
            Assert.IsTrue(CloudLightBlend.Reach(-6.0 * Degree, lowest, lowest) > 0.0);
        }

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

        [TestMethod]
        public void WithoutWeightOrWhereTheyCancelTheStrongerOneStays()
        {
            double[] sun = { 1.0, 0.0, 0.0 }, moon = { -1.0, 0.0, 0.0 }, towards = new double[3];
            CloudLightBlend.Direction(sun, 0.0, moon, 0.0, 0.5, towards);
            Assert.AreEqual(1.0, towards[0]);
            CloudLightBlend.Direction(sun, 1.0, moon, 1.0, 0.5, towards);
            Assert.AreEqual(1.0, Math.Abs(towards[0]), 1e-12);
        }

        private static void Normalize(double[] v)
        {
            double length = Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);
            for (int i = 0; i < 3; i++) v[i] /= length;
        }
    }
}
