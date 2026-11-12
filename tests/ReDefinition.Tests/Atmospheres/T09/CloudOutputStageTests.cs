using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using UnityEngine;

namespace ReDefinition.Tests.Atmospheres.T09
{
    // The units of the own cloud pass (CloudOutputStage) and what a cover value does in the layer shape (CloudCoverModel), on the numbers of K-T2d start 44
    // (.local/plans/atmospheres/K-T2d-result.md): ground view at the high cover, sun elevation 15.6 degrees, the scene's sun colour (0.536, 0.400, 0.214)
    // (luminance 0.415), ambient luminance 0.2475, the Gamma-space half-float target, baseline sky 0.458 and sunlit sea 0.443 (display values after Deferred).
    // The raw deck medians of the pass at cloud.sunGain 1, 4, 16, 64 were 0.0145, 0.0584, 0.219, 0.755 (proportional to the gain: nothing after the pass clamps).
    [TestClass]
    public class CloudOutputStageTests
    {
        private const float White = 0.415f, Sea = 0.443f, Sky = 0.458f;
        private const float RawG1 = 0.0145f, RawG4 = 0.0584f, RawG16 = 0.219f, RawG64 = 0.755f;

        // What the product's stage makes of the raw radiance the pass wrote at the gain: the sun unit multiplies the (all direct) deck, then the display encoding.
        private static float Product(float rawDeck, float exposure = 1f, float sunUnit = CloudOutputStage.SunUnitDefault, float gamma = CloudOutputStage.GammaDefault)
        {
            return CloudOutputStage.Encode(rawDeck * sunUnit, White, exposure, gamma);
        }

        [TestMethod]
        public void ThePassIsGivenPiTimesTheScenesSunColourAndTheGainStillScalesIt()
        {
            var scene = new Color(0.536f, 0.400f, 0.214f, 1f);
            Assert.AreEqual(0.415f, scene.grayscale, 0.005f, "the K-T2d log's frame sun luminance");
            float pass = CloudOutputStage.SunForPass(scene.grayscale, CloudOutputStage.SunUnitDefault, 1f);
            Assert.AreEqual(0.415f * Math.PI, pass, 0.02);
            Assert.AreEqual(4f * pass, CloudOutputStage.SunForPass(scene.grayscale, CloudOutputStage.SunUnitDefault, 4f), 1e-4f);
            Assert.AreEqual(scene.grayscale, CloudOutputStage.SunForPass(scene.grayscale, 1f, 1f), 1e-6f, "unit 1 is the pass as K-T2d start 44 measured it");
            // Next to the harness the light was tuned in (sun 2.9): the product's pass sun is 0.45 of it, the flash gain follows (OwnCloudPlan.FlashPeakScale).
            Assert.AreEqual(0.45f, OwnCloudPlan.FlashPeakScale(pass), 0.01f);
        }

        [TestMethod]
        public void AWhiteCardInTheSunKeepsItsDisplayValueWhateverTheGammaAndGammaOneIsOff()
        {
            foreach (float gamma in new[] { 1.4f, 2.0f, 2.2f, 3.0f }) Assert.AreEqual(White, CloudOutputStage.Encode(White, White, 1f, gamma), 1e-5f, "gamma " + gamma);
            Assert.AreEqual(0.0456f, CloudOutputStage.Encode(0.0456f, White, 1f, 1f), 1e-7f, "gamma 1: the radiance as it is");
            Assert.AreEqual(0.0456f, CloudOutputStage.Encode(0.0456f, White, 3f, 1f), 1e-7f, "gamma 1: the stage is off, exposure included");
            Assert.AreEqual(0f, CloudOutputStage.Encode(0f, White, 1f, 2.2f), 0f);
            // Exposure 2 raises a value below the white by 2 ^ (1 / gamma).
            float one = CloudOutputStage.Encode(0.05f, White, 1f, 2.2f), two = CloudOutputStage.Encode(0.05f, White, 2f, 2.2f);
            Assert.AreEqual(Math.Pow(2.0, 1.0 / 2.2), two / one, 1e-4);
        }

        [TestMethod]
        public void ThePremultipliedColourIsEncodedWithoutItsCoverageAndPutBackWithIt()
        {
            float c = 0.12f, a = 0.4f;
            float expected = a * CloudOutputStage.Encode(c, White, 1f, 2.2f);
            Assert.AreEqual(expected, CloudOutputStage.EncodePremultiplied(c * a, a, White, 1f, 2.2f), 1e-6f);
            Assert.AreEqual(c * a, CloudOutputStage.EncodePremultiplied(c * a, a, White, 1f, 1f), 1e-7f);
            Assert.AreEqual(0f, CloudOutputStage.EncodePremultiplied(0f, 0f, White, 1f, 2.2f), 0f, "no cloud, nothing added");
        }

        [TestMethod]
        public void TheGroundDeckOfK_T2dLandsBelowTheSeaAndTheSkyAtGainOneAndIsNoLongerBlack()
        {
            float units = Product(RawG1, 1f, CloudOutputStage.SunUnitDefault, 1f), product = Product(RawG1);
            Assert.AreEqual(0.0456f, units, 0.001f, "units alone: pi times the raw 0.0145");
            Assert.AreEqual(0.152f, product, 0.01f, "the encoded stage: W (0.0456 / W) ^ (1 / 2.2)");
            // Commensurable: well below the sunlit sea (a thick base lit by diffuse light), but not 3 percent of it as in start 44 (0.0145 / 0.443).
            Assert.IsTrue(product / Sea > 0.27f && product / Sea < 0.43f, "deck / sea " + product / Sea);
            Assert.IsTrue(product / Sky > 0.27f && product / Sky < 0.40f, "deck / sky " + product / Sky);
            Assert.IsTrue(RawG1 / Sea < 0.04f, "start 44: " + RawG1 / Sea);
        }

        [TestMethod]
        public void TheGainLadderStaysBelowTheWhiteAtGain64AndRisesMonotonically()
        {
            float[] raw = { RawG1, RawG4, RawG16, RawG64 };
            float previous = 0f;
            foreach (float r in raw)
            {
                float d = Product(r);
                Assert.IsTrue(d > previous, "monotone " + d);
                previous = d;
            }
            Assert.AreEqual(0.286f, Product(RawG4), 0.015f);
            Assert.AreEqual(0.522f, Product(RawG16), 0.03f);
            Assert.IsTrue(Product(RawG64) > White && Product(RawG64) < 1.0f, "gain 64 is brighter than the white but not clipped: " + Product(RawG64));
            // The ladder is proportional in the radiance, the product's output is not (slope 1 / gamma): the kt2d verdict rows therefore come from the raw ladder.
            Assert.AreEqual(1.0 / 2.2, Math.Log(Product(RawG16) / Product(RawG4)) / Math.Log(RawG16 / RawG4), 0.1);
        }

        [TestMethod]
        public void TheDeckSeenFromAboveLandsNearTheSeaWithTheAmbientKeptInItsOwnUnits()
        {
            // Start 44, view above: deck median 0.311 / 0.643 / 1.683 at gain 1 / 4 / 16, the sea of the baseline 0.311 / 0.72 = 0.432. The deck is the ambient (display
            // units already: it is not scaled) plus the direct part, which grows with the gain: direct = (g4 - g1) / 3.
            float direct = (0.643f - 0.311f) / 3f, ambient = 0.311f - direct, sea = 0.432f;
            float radiance = ambient + direct * CloudOutputStage.SunUnitDefault;
            float product = CloudOutputStage.Encode(radiance, White, 1f, CloudOutputStage.GammaDefault);
            Assert.AreEqual(0.20f, ambient, 0.02f);
            Assert.AreEqual(0.47f, product, 0.04f);
            Assert.IsTrue(product / sea > 0.8f && product / sea < 1.4f, "top / sea " + product / sea + ": a cloud top is about as bright as the sunlit sea");
        }

        [TestMethod]
        public void TheFarViewsInScatterIsNowAboveTheAmbientFloorAtTheLowSun()
        {
            // CloudBridgeAir.Inscatter takes the pass's sun colour: with the scene's own colour (start 44) the 23 percent horizon share (calibrated on the harness's sun
            // 3.0 / 2.9 / 2.7) gave 0.095, below the ambient floor 0.25; with pi times it, 0.30.
            var scene = new Color(0.536f, 0.400f, 0.214f, 1f);
            var ambient = new Color(0.231f, 0.239f, 0.243f, 1f);
            Vector3 rayleigh = CloudBridgeAir.EarthRayleighPerM;
            Color before = CloudBridgeAir.Inscatter(scene, Color.black, ambient, rayleigh);
            Color after = CloudBridgeAir.Inscatter(scene * CloudOutputStage.SunUnitDefault, Color.black, ambient, rayleigh);
            Assert.AreEqual(ambient.grayscale, before.grayscale, 0.03f, "before: the ambient floor");
            Assert.IsTrue(after.grayscale > 0.26f && after.grayscale < 0.40f, "after " + after.grayscale);
        }

        // ---- the cover value ----

        // The rank-equalised shape volume is uniform on 0..1 (NoiseVolumes.Equalize), so a grid of uniform samples stands for it.
        private static double[] UniformNoise(int n)
        {
            var samples = new double[n];
            for (int i = 0; i < n; i++) samples[i] = (i + 0.5) / n;
            return samples;
        }

        [TestMethod]
        public void TheShapeNoiseOfTheVolumeIsUniformSoTheGridStandsForIt()
        {
            byte[] shape = NoiseVolumes.Shape(32, 1u);
            double sum = 0.0;
            int below = 0;
            foreach (byte b in shape)
            {
                sum += b;
                if (b < 128) below++;
            }
            Assert.AreEqual(0.5, sum / (255.0 * shape.Length), 0.01);
            Assert.AreEqual(0.5, below / (double)shape.Length, 0.02);
        }

        [TestMethod]
        public void TheShapeFormulaPinsTheLayersThreshold()
        {
            // Rain deck: coverage 0.5, edge 0.2, noise amount 0.3. At the map value 0.5 the median noise gives share 0.5; the formula is the shader's.
            Assert.AreEqual(0.5, CloudCoverModel.Share(0.5, 0.5, 0.3, 0.5, 0.2), 1e-9);
            Assert.AreEqual(0.0, CloudCoverModel.Share(0.2, 0.5, 0.3, 0.5, 0.2), 1e-9);
            Assert.AreEqual(1.0, CloudCoverModel.Share(0.9, 0.5, 0.3, 0.5, 0.2), 1e-9);
            // 0.473, the K-T2d low pick: (0.473 + 0.5 - 1) / 0.2 + 0.5 = 0.365 at the median noise.
            Assert.AreEqual(0.365, CloudCoverModel.Share(0.473, 0.5, 0.3, 0.5, 0.2), 1e-6);
        }

        [TestMethod]
        public void ACoverOfPoint47ClosesMostOfTheSkyUnderTheRainDeckAsStartedAndAHalfClosedSkyNeedsACoverageOfAbout0Point4()
        {
            double[] noise = UniformNoise(2001);
            // The rain deck's Stratus (baseShare 0.1 to topShare 0.5 of 2100 m, the profile's mean 0.74): equivalent thickness 622 m; sigma 0.045 per m; looking up at
            // 18 degrees (the K-T2d ground view) the slant factor is 3.24; at the zenith 1.
            double thickness = 0.4 * 2100.0 * 0.74, sigma = 0.045;
            foreach (double slant in new[] { 3.24, 1.0 })
            {
                double atPoint47 = CloudCoverModel.ClosedShare(0.473, noise, 0.3, 0.5, 0.2, sigma, thickness, slant);
                double atPoint3 = CloudCoverModel.ClosedShare(0.3, noise, 0.3, 0.5, 0.2, sigma, thickness, slant);
                double atPoint6 = CloudCoverModel.ClosedShare(0.6, noise, 0.3, 0.5, 0.2, sigma, thickness, slant);
                Assert.IsTrue(atPoint47 > 0.6, "slant " + slant + ": cover 0.473 closes " + atPoint47 + " of the sky, not 0.47");
                Assert.IsTrue(atPoint3 > 0.0 && atPoint3 < 0.45, "slant " + slant + ": cover 0.3 closes " + atPoint3);
                Assert.IsTrue(atPoint6 > 0.95, "slant " + slant + ": cover 0.6 closes " + atPoint6);
                double centred = CloudCoverModel.CentredCoverage(0.5, noise, 0.3, 0.2, sigma, thickness, slant);
                Assert.IsTrue(centred > 0.38 && centred < 0.47, "slant " + slant + ": the coverage that closes half the sky at cover 0.5 is " + centred + ", the file has 0.5");
                Assert.AreEqual(0.5, CloudCoverModel.ClosedShare(0.5, noise, 0.3, centred, 0.2, sigma, thickness, slant), 0.01);
            }
        }

        [TestMethod]
        public void TheFogAndNimbostratusTypesOfTheSameDeckCloseDifferentSharesAtTheSameCover()
        {
            // The type value is the precipitation level: 0 Fog (210 m, mean profile 0.565), 0.5 Stratus, 1 Nimbostratus (1995 m, mean 0.804). The same map value 0.514
            // (start 42's cover at the view height) closes a different share by type (the Player's table 1: 21 / 85 / 100 percent).
            double[] noise = UniformNoise(2001);
            double fog = CloudCoverModel.ClosedShare(0.514, noise, 0.3, 0.5, 0.2, 0.045, 0.1 * 2100.0 * 0.565, 3.24);
            double stratus = CloudCoverModel.ClosedShare(0.514, noise, 0.3, 0.5, 0.2, 0.045, 0.4 * 2100.0 * 0.74, 3.24);
            double nimbo = CloudCoverModel.ClosedShare(0.514, noise, 0.3, 0.5, 0.2, 0.045, 0.95 * 2100.0 * 0.804, 3.24);
            Assert.IsTrue(fog < stratus && stratus <= nimbo, "closed share fog / stratus / nimbostratus " + fog + " / " + stratus + " / " + nimbo);
            // The share with any cloud at all is 1 - n* = 0.88 here (noise amount 0.3 moves the threshold by only +-0.15): the thickest type cannot close more.
            Assert.IsTrue(nimbo > 0.8 && nimbo < 0.9 && stratus > 0.8, fog + " / " + stratus + " / " + nimbo);
        }
    }
}
