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
        public void TheWhiteBalancedSunIsWhiteAtTheZenithAndKeepsOnlyThePathBeyondItReddening()
        {
            double[] tau = { 0.0764, 0.138, 0.295 };
            // At the zenith above the point the colour is the frame's own (T03v2); the air mass enters as (m - 1).
            double zenith = CloudLightMath.SunAir(1.0, tau[2], 3000.0, 8000.0, true);
            Assert.AreEqual(1.0, zenith, 2e-3);
            double s35 = Math.Sin(35.0 * Math.PI / 180.0);
            double r = CloudLightMath.SunAir(s35, tau[0], 3000.0, 8000.0, true), b = CloudLightMath.SunAir(s35, tau[2], 3000.0, 8000.0, true);
            Assert.IsTrue(b / r > 0.85, "a cloud top in the sun at 35 degrees is white, not sand-beige: " + b / r);
            double unbalancedB = CloudLightMath.SunAir(s35, tau[2], 3000.0, 8000.0, false), unbalancedR = CloudLightMath.SunAir(s35, tau[0], 3000.0, 8000.0, false);
            Assert.IsTrue(unbalancedB / unbalancedR < b / r, "the unbalanced law is the more yellow");
            double s2 = Math.Sin(2.0 * Math.PI / 180.0);
            Assert.IsTrue(CloudLightMath.SunAir(s2, tau[2], 3000.0, 8000.0, true) / CloudLightMath.SunAir(s2, tau[0], 3000.0, 8000.0, true) < 0.1,
                "a sun at 2 degrees still loses its blue first");
        }

        [TestMethod]
        public void TheDiffusionShareBuildsUpAndThenFallsOffAndTheZeroDecayIsTheSaturatingTerm()
        {
            Assert.AreEqual(1.0 - Math.Exp(-10.0 / 35.8), CloudLightMath.DiffusionShare(10.0, 35.8, 0.0), 1e-12);
            double shallow = CloudLightMath.DiffusionShare(1.0, 8.0, 0.15), mid = CloudLightMath.DiffusionShare(5.0, 8.0, 0.15), deep = CloudLightMath.DiffusionShare(200.0, 8.0, 0.15);
            Assert.IsTrue(mid > shallow, "builds up with the depth");
            Assert.IsTrue(deep < mid, "and falls off in a thick cloud");
            Assert.IsTrue(deep < 0.05, "the far side of a very thick cloud is dark: " + deep);
        }

        [TestMethod]
        public void TheAmbientGainIsTheIsotropicShareOfTheHemisphereAndTheStructsWhiteBalanceStaysForTheFixtures()
        {
            var d = new CloudLightSettings();
            Assert.AreEqual(0.25f, d.AmbientGain, 1e-6f);
            Assert.IsTrue(d.SunAirWhiteBalance, "the struct's own default; the game's comes from the setting, which is off");
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

        // E40 L6 light: the sky's light that has diffused into the cloud follows the two-stream slab. A uniform slab of optical depth tau lit by a sky of radiance 1 returns, at the top, a
        // radiance of R = 1 - T (T = 1 / (1 + 0.11 tau)) and passes T of it to the base; the model's source (the occlusion plus SkyDiffusion, the gain 1/4 of an isotropic source) marched
        // like the shader does it lies within 25 % of the top's R and 25 % of the base's T for tau 25 to 200 (the python mirror, light_slab_check.py table, has the same numbers).
        [TestMethod]
        public void TheSkysDiffusionReturnsTheTwoStreamReflectionAtTheTopAndPassesItsTransmissionToTheBase()
        {
            const double ms = 3.5, decay = 0.11, occlusion = 1.2765, height = 1200.0, viewSin = 0.7986;
            foreach (double tau in new[] { 25.0, 60.0, 100.0, 200.0 })
            {
                double sigma = tau / height;
                double top = SlabRadiance(sigma, height, viewSin, false, ms, decay, occlusion), bottom = SlabRadiance(sigma, height, viewSin, true, ms, decay, occlusion);
                double t = 1.0 / (1.0 + decay * tau);
                Assert.AreEqual(1.0 - t, top, 0.25 * (1.0 - t) + 0.02, "top of tau " + tau);
                Assert.AreEqual(t, bottom, 0.25 * t + 0.002, "base of tau " + tau);
            }
            Assert.AreEqual(3.5, CloudLightMath.SkyDiffusion(0.0, 3.5, 0.11), 1e-12);
            Assert.AreEqual(0.0, CloudLightMath.SkyDiffusion(10.0, 0.0, 0.11), 1e-12);
        }

        // The radiance of a ray through a uniform slab (extinction sigma, height h) under a sky of radiance one: the source is the sky's isotropic source 1 / 4 times the occlusion
        // exp(-occlusion tau) plus SkyDiffusion at the optical depth tau to the slab's top, marched with the energy-conserving step; up: the camera below, looking at the base.
        private static double SlabRadiance(double sigma, double h, double viewSin, bool up, double ms, double decay, double occlusion)
        {
            const int n = 400;
            double ds = h / n / viewSin, trans = 1.0, acc = 0.0;
            for (int i = 0; i < n; i++)
            {
                double x = (i + 0.5) / n, z = (up ? x : 1.0 - x) * h;
                double tu = sigma * (h - z), ext = 1.0 - Math.Exp(-sigma * ds);
                double source = 0.25 * (Math.Exp(-tu * occlusion) + CloudLightMath.SkyDiffusion(tu, ms, decay));
                acc += trans * source * ext;
                trans *= 1.0 - ext;
            }
            return acc;
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
