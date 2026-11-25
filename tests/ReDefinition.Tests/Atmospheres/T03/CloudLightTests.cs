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
        public void TheCellDiffusionEqualsTheCpuMirror()
        {
            // tools/weather_metrics/light_cpu.diffusion_terms with the default numbers at the same inputs (E40 LM2); the settings are floats, so 1e-6 relative
            CloudLightMath.CellLight k = new CloudLightSettings().CellLight();
            double iso;
            Assert.AreEqual(0.020233961634754292, CloudLightMath.CellDiffusion(10.0, 5.0, 0.05, 1200.0, 2000.0, 0.5, k, out iso), 2e-8);
            Assert.AreEqual(0.6642082039683284, iso, 1e-6);
            Assert.AreEqual(0.009620368404836354, CloudLightMath.CellDiffusion(40.0, 60.0, 0.03, 2700.0, 2000.0, 0.26, k, out iso), 1e-8);
            Assert.AreEqual(1.0131366122525767, iso, 1e-6);
            Assert.AreEqual(0.02621139489908524, CloudLightMath.CellDiffusion(2.0, 1.4, 0.02, 600.0, 20000.0, 0.7, k, out iso), 3e-8);
            Assert.AreEqual(0.8160975492390107, iso, 1e-6);
        }

        [TestMethod]
        public void TheCellDiffusionFollowsTheIncidenceAndFallsToTheFarSide()
        {
            CloudLightMath.CellLight k = new CloudLightSettings().CellLight();
            double iso, isoLow;
            // a stratiform layer lit from above: the incidence is the sun's elevation sine, so the source near the top grows with the sun as the photon walk's does (0.45 / 0.69 / 1.0 W at 0.5 / 0.7 / 0.94)
            Assert.AreEqual(0.5, CloudLightMath.CellIncidence(20.0, 10.0, 1e-4), 1e-3);
            double high = CloudLightMath.CellDiffusion(1.0, 0.94, 0.08, 1200.0, 20000.0, 0.94, k, out iso);
            double low = CloudLightMath.CellDiffusion(1.0, 0.26, 0.08, 1200.0, 20000.0, 0.26, k, out isoLow);
            Assert.IsTrue(high > 2.5 * low && iso > isoLow, "high " + high + " low " + low);
            // deeper towards the far side the source falls
            double near = CloudLightMath.CellDiffusion(5.0, 3.5, 0.08, 1200.0, 20000.0, 0.7, k, out iso);
            double far = CloudLightMath.CellDiffusion(120.0, 84.0, 0.08, 1200.0, 20000.0, 0.7, k, out iso);
            Assert.IsTrue(far < 0.5 * near, "near " + near + " far " + far);
            // the chord of a stratiform layer is its height over the sun's sine, of a cell as wide as high its diameter
            Assert.AreEqual(1200.0 / 0.5, CloudLightMath.CellChord(0.5, 1200.0, 1.0e7), 1.0);
            Assert.AreEqual(1600.0, CloudLightMath.CellChord(0.3, 1600.0, 1600.0), 1e-6);
            // no cell width: nothing, the octaves untouched
            Assert.AreEqual(0.0, CloudLightMath.CellDiffusion(5.0, 3.5, 0.08, 1200.0, 0.0, 0.7, k, out iso));
            Assert.AreEqual(1.0, iso);
        }

        [TestMethod]
        public void ACloudOfAnotherLayerPassesTheTwoStreamShareOfTheSunsFlux()
        {
            // PRUEFPROTOKOLL 271: a non-absorbing cloud of tau 2 / 10 passes 1 / (1 + 0.11 tau) of the flux, not exp(-tau)
            Assert.AreEqual(1.0, CloudLightMath.OtherLayersTransmission(0.0, 0.11), 1e-12);
            Assert.AreEqual(1.0 / 1.22, CloudLightMath.OtherLayersTransmission(2.0, 0.11), 1e-12);
            Assert.AreEqual(1.0 / 2.1, CloudLightMath.OtherLayersTransmission(10.0, 0.11), 1e-12);
            Assert.AreEqual(1.0, CloudLightMath.OtherLayersTransmission(-3.0, 0.11), 1e-12);
            // a stratus deck 10 m below its top (density 0.026 / m, sun at 45 degrees) under a cloud of tau 2 of another layer: the cell diffusion read with the own optical depth
            // and the two-stream share keeps about 0.8 of the lit deck's source; read with the total, as before, the incidence fell and the source to about a tenth
            CloudLightMath.CellLight k = new CloudLightSettings().CellLight();
            double iso, isoUnder, isoTotal;
            double up = 0.026 * 10.0, own = up / 0.71;
            double lit = CloudLightMath.CellDiffusion(own, up, 0.026, 1000.0, 20000.0, 0.71, k, out iso);
            double under = CloudLightMath.CellDiffusion(own, up, 0.026, 1000.0, 20000.0, 0.71, k, out isoUnder) * CloudLightMath.OtherLayersTransmission(2.0, 0.11);
            double total = CloudLightMath.CellDiffusion(own + 2.0, up, 0.026, 1000.0, 20000.0, 0.71, k, out isoTotal);
            Assert.AreEqual(1.0 / 1.22, under / lit, 1e-9);
            Assert.AreEqual(iso, isoUnder, 1e-12);
            Assert.IsTrue(total < 0.15 * lit && isoTotal < 0.5 * iso, "total " + total + " lit " + lit + " iso " + isoTotal + " / " + iso);
            // the split at the sample: a deck (cells of 20 km, type 1.7 km tall) is plane-parallel, its own share the column over the sine, the rest the other layer's
            Assert.AreEqual(1.0, CloudLightMath.PlaneShare(20000.0, 1700.0), 1e-12);
            Assert.AreEqual(0.0, CloudLightMath.PlaneShare(2000.0, 1000.0), 1e-12);
            Assert.AreEqual(0.0, CloudLightMath.PlaneShare(12000.0, 9000.0), 1e-12);
            Assert.AreEqual(2.0, CloudLightMath.OtherOpticalDepth(own + 2.0, up, 0.71, 1.0), 1e-9);
            Assert.AreEqual(0.0, CloudLightMath.OtherOpticalDepth(own + 2.0, up, 0.71, 0.0), 1e-12);
            Assert.AreEqual(0.0, CloudLightMath.OtherOpticalDepth(own * 0.5, up, 0.71, 1.0), 1e-12);
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
