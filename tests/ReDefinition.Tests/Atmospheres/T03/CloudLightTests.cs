using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests
{
    // The pure parts of the cloud light (package T03): the phase function and its normalisation, the silver lining ratio, the octave sum, the
    // tap layout of the light march, the powder factor, the ambient floor and the settings. The shader mirrors these functions; the weather
    // player measures the picture against the harness's reference (tools/weather_metrics/light.py).
    [TestClass]
    public class CloudLightMathTests
    {
        private const double G1 = 0.85, G2 = -0.3, W1 = 0.92;

        [TestMethod]
        public void ThePhaseFunctionIntegratesToOneOverTheSphereAtEveryEccentricity()
        {
            foreach (double ecc in new[] { 1.0, 0.5, 0.25, 0.125 })
                Assert.AreEqual(1.0, CloudLightMath.PhaseIntegral(G1, G2, W1, ecc, 200001), 0.01, "ecc " + ecc);
        }

        [TestMethod]
        public void TheForwardLobeBrightensDirectionsNearTheSunTwoToOneAtLeast()
        {
            double near = CloudLightMath.Phase(Math.Cos(10.0 * Math.PI / 180.0), G1, G2, W1, 1.0);
            double far = CloudLightMath.Phase(Math.Cos(75.0 * Math.PI / 180.0), G1, G2, W1, 1.0);
            Assert.IsTrue(near / far >= 2.0, "near " + near + " far " + far);
        }

        [TestMethod]
        public void TheNegatedForwardLobeLosesTheSilverLining()
        {
            double near = CloudLightMath.Phase(Math.Cos(10.0 * Math.PI / 180.0), -G1, -G2, W1, 1.0);
            double far = CloudLightMath.Phase(Math.Cos(75.0 * Math.PI / 180.0), -G1, -G2, W1, 1.0);
            Assert.IsTrue(near / far < 2.0, "ratio " + near / far);
        }

        [TestMethod]
        public void OneOctaveIsTheSingleScatteringAndMoreOctavesAddLightMonotonically()
        {
            double mu = 0.3, tau = 2.0;
            double single = CloudLightMath.Phase(mu, G1, G2, W1, 1.0) * Math.Exp(-tau);
            Assert.AreEqual(single, CloudLightMath.OctaveSum(mu, tau, 1, 0.5, 0.5, 0.5, 1.0, G1, G2, W1), 1e-12);
            double prev = single;
            for (int n = 2; n <= 4; n++)
            {
                double s = CloudLightMath.OctaveSum(mu, tau, n, 0.5, 0.5, 0.5, 1.0, G1, G2, W1);
                Assert.IsTrue(s > prev, "octave " + n);
                prev = s;
            }
        }

        [TestMethod]
        public void TheIsotropicShareMovesTheHigherOctavesTowardsOneOverFourPi()
        {
            double mu = Math.Cos(10.0 * Math.PI / 180.0), tau = 0.5;
            double directional = CloudLightMath.OctaveSum(mu, tau, 4, 0.5, 0.5, 0.5, 1.0, G1, G2, W1, 0.0);
            double isotropic = CloudLightMath.OctaveSum(mu, tau, 4, 0.5, 0.5, 0.5, 1.0, G1, G2, W1, 1.0);
            double first = CloudLightMath.Phase(mu, G1, G2, W1, 1.0) * Math.Exp(-tau);
            Assert.IsTrue(isotropic < directional, "near the sun the isotropic octaves carry less than the forward-peaked ones");
            double rest = 0, bn = 0.5, an = 0.5;
            for (int i = 1; i < 4; i++) { rest += bn * CloudLightMath.InvFourPi * Math.Exp(-tau * an); bn *= 0.5; an *= 0.5; }
            Assert.AreEqual(first + rest, isotropic, 1e-9);
        }

        [TestMethod]
        public void TheSunComesOutRedWhenItIsLow()
        {
            double[] tau = { 0.0764, 0.138, 0.295 };
            double highR = CloudLightMath.SunAir(Math.Sin(60.0 * Math.PI / 180.0), tau[0], 2000.0, 8000.0);
            double highB = CloudLightMath.SunAir(Math.Sin(60.0 * Math.PI / 180.0), tau[2], 2000.0, 8000.0);
            double lowR = CloudLightMath.SunAir(Math.Sin(2.0 * Math.PI / 180.0), tau[0], 2000.0, 8000.0);
            double lowB = CloudLightMath.SunAir(Math.Sin(2.0 * Math.PI / 180.0), tau[2], 2000.0, 8000.0);
            Assert.IsTrue(highB / highR > 0.8, "a high sun stays nearly white");
            Assert.IsTrue(lowB / lowR < 0.1, "a sun at 2 degrees loses its blue first: " + lowB / lowR);
            Assert.AreEqual(0.0, CloudLightMath.SunAir(-0.2, tau[0], 0.0, 8000.0), 1e-12);
            Assert.AreEqual(1.0, CloudLightMath.SunAir(0.5, tau[0], 0.0, 0.0), 1e-12, "a zero height keeps the frame's colour");
        }

        [TestMethod]
        public void TheOctavesLetMoreLightReachTheDeepCoreThanBeerLambertAlone()
        {
            double deep = 6.0;
            double single = CloudLightMath.OctaveSum(0.0, deep, 1, 0.5, 0.5, 0.5, 1.0, G1, G2, W1);
            double multi = CloudLightMath.OctaveSum(0.0, deep, 4, 0.5, 0.5, 0.5, 1.0, G1, G2, W1);
            Assert.IsTrue(multi > 5.0 * single);
        }

        [TestMethod]
        public void TheOctaveEnergyIsBelowTwoForUnitBrightnessAndTheFittedDefaultsStayInsideTheDiffuserBound()
        {
            Assert.IsTrue(CloudLightMath.OctaveEnergy(4, 0.5, 1.0) < 2.0);
            Assert.AreEqual(1.0, CloudLightMath.OctaveEnergy(1, 0.5, 1.0), 1e-12);
            var d = new CloudLightSettings();
            // The bound of a perfect diffuser: radiance E / pi is 4 times the isotropic source E / 4 pi.
            Assert.IsTrue(CloudLightMath.OctaveEnergy(d.Octaves, d.OctaveB, d.MsBrightness) <= CloudLightMath.EnergyBound);
        }

        [TestMethod]
        public void TheLightMarchTapsCoverTheSpanWithGrowingOrEqualSteps()
        {
            double[] e = CloudLightMath.TapEdges(1000.0, 3, 2.0);
            Assert.AreEqual(0.0, e[0], 1e-12);
            Assert.AreEqual(1000.0, e[3], 1e-9);
            Assert.AreEqual((e[2] - e[1]) / (e[1] - e[0]), 2.0, 1e-9);
            double[] u = CloudLightMath.TapEdges(1000.0, 4, 1.0);
            for (int i = 0; i < 4; i++) Assert.AreEqual(250.0, u[i + 1] - u[i], 1e-9);
        }

        [TestMethod]
        public void ABeerLambertSlabMarchedWithTheTapsIsTheExactTransmittance()
        {
            // The midpoint sum of a constant density over any tap layout is the exact optical depth: T(2h) = T(h)^2.
            double sigma = 0.002;
            Func<double, double> t = h =>
            {
                double[] e = CloudLightMath.TapEdges(h, 4, 2.0);
                double tau = 0;
                for (int i = 0; i < 4; i++) tau += sigma * (e[i + 1] - e[i]);
                return Math.Exp(-tau);
            };
            Assert.AreEqual(t(2000.0), t(1000.0) * t(1000.0), 1e-12);
        }

        [TestMethod]
        public void PowderDarkensThinPartsAndLeavesThickPartsAlone()
        {
            Assert.IsTrue(CloudLightMath.Powder(0.001, 150.0, 1.0) < CloudLightMath.Powder(0.05, 150.0, 1.0));
            Assert.AreEqual(1.0, CloudLightMath.Powder(0.001, 150.0, 0.0), 1e-12);
            Assert.IsTrue(CloudLightMath.Powder(0.5, 150.0, 1.0) > 0.99);
        }

        [TestMethod]
        public void TheAmbientFloorIsAShareOfTheSunsIsotropicLight()
        {
            Assert.AreEqual(0.03 * 3.0 / (4.0 * Math.PI), CloudLightMath.AmbientFloor(0.03, 3.0), 1e-12);
        }
    }

    [TestClass]
    public class CloudLightSettingsTests
    {
        [TestMethod]
        public void TheAmbientFloorNeverDropsBelowThreePercent()
        {
            var s = new CloudLightSettings { AmbientFloor = 0f };
            Assert.IsTrue(s.EffectiveFloor >= 0.03f && s.EffectiveFloor <= 0.031f);
            s.AmbientFloor = 0.1f;
            Assert.AreEqual(0.1f, s.EffectiveFloor, 1e-6f);
        }

        [TestMethod]
        public void TheQualityLevelsRaiseTheTapsAndTheOctaves()
        {
            CloudLightSettings low = CloudLightSettings.ForQuality(0), mid = CloudLightSettings.ForQuality(1), high = CloudLightSettings.ForQuality(2);
            Assert.IsTrue(low.SunStepsOverride < mid.SunStepsOverride && mid.SunStepsOverride < high.SunStepsOverride);
            Assert.IsTrue(low.Octaves < mid.Octaves && mid.Octaves <= high.Octaves);
            Assert.IsTrue(high.Octaves <= 4 && high.SunStepsOverride <= 64);
        }

        [TestMethod]
        public void TheDefaultLightHasTwoExtraLightsFourBoltsAndNoCache()
        {
            var s = new CloudLightSettings();
            Assert.AreEqual(CloudLightSettings.MaxLights, s.Lights.Length);
            Assert.AreEqual(CloudLightSettings.MaxBolts, s.Bolts.Length);
            Assert.IsNull(s.Cache);
            Assert.AreEqual(0, s.BoltCount);
        }
    }
}
