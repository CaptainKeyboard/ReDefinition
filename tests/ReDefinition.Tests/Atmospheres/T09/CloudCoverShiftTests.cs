using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using UnityEngine;

namespace ReDefinition.Tests.Atmospheres.T09
{
    // The runtime shift of the layers' coverage threshold (CloudCoverShift, tuning cloud.coverageShift and cloud.coverageShiftRain): default 0 changes nothing, the shift reaches every
    // reader through CloudLayerDef.EffectiveCoverage, the group key acts on the rain deck only, and the model's expected numbers for the K-T2d variants.
    [TestClass]
    public class CloudCoverShiftTests
    {
        private static int frame = 2000000;

        private static CloudLayerDef Layer(string group, float coverage)
        {
            return new CloudLayerDef("layer", 0, 0f, 2100f, new List<CloudTypeDef> { CloudTypeDef.Slab("Stratus", 0.1f, 0.5f) }, 0.045f)
            {
                Coverage = coverage,
                EdgeSoftness = 0.2f,
                NoiseAmount = 0f,   // a layer without noise keeps the plain soft edge of its own numbers: the knobs' arithmetic is read on those
                Group = group
            };
        }

        private static void Set(string key, float value)
        {
            Tuning.SetOverride(key, value);
            Tuning.Instance.BeginFrame(++frame);
        }

        // These tests are about the knobs' arithmetic on the layer's own numbers, which a layer without noise uploads as they are (with noise the uploaded constants are derived
        // from them: CloudSoftDensityTests and CloudCoverOracleTests pin that).
        private static void Clear()
        {
            Tuning.ClearOverrides();
            Tuning.Instance.BeginFrame(++frame);
        }

        [TestInitialize]
        public void Setup()
        {
            Clear();
        }

        [TestCleanup]
        public void Cleanup()
        {
            Tuning.ClearOverrides();
            Tuning.Instance.BeginFrame(++frame);
        }

        [TestMethod]
        public void TheDefaultShiftOfZeroGivesTheConfiguredCoverageAndTheSameDensityAndShare()
        {
            Clear();
            foreach (float coverage in new[] { 0f, 0.12f, 0.5f, 1f })
            {
                CloudLayerDef rain = Layer("weather", coverage), other = Layer("", coverage);
                Assert.AreEqual(coverage, rain.EffectiveCoverage, "default 0, rain group");
                Assert.AreEqual(coverage, other.EffectiveCoverage, "default 0, no group");
                var table = new LayerTypeTable(rain);
                Assert.AreEqual(LayerDensity.Shape(0.47, coverage, 0.2) * 0.045 * table.Profile(0.5, 0.3), LayerDensity.Extinction(rain, table, 630.0, 0.47, 0.5), 1e-6);
            }
            var noise = new double[401];
            for (int i = 0; i < noise.Length; i++) noise[i] = (i + 0.5) / noise.Length;
            foreach (double map in new[] { 0.3, 0.473, 0.6 })
                Assert.AreEqual(CloudCoverModel.ClosedShare(map, noise, 0.3, 0.5, 0.2, 0.045, 622.0, 3.24),
                                CloudCoverModel.ClosedShareShifted(map, noise, 0.3, 0.5f, "weather", 0f, 0f, 0.2, 0.045, 622.0, 3.24), 0.0, "map " + map);
        }

        [TestMethod]
        public void TheShiftIsAddedToTheThresholdOfEveryLayerAndClamped()
        {
            Set(CloudCoverShift.TuneShift, -0.08f);
            Assert.AreEqual(0.42f, Layer("weather", 0.5f).EffectiveCoverage, 1e-6f);
            Assert.AreEqual(0.42f, Layer("", 0.5f).EffectiveCoverage, 1e-6f);
            Assert.AreEqual(0.04f, Layer("", 0.12f).EffectiveCoverage, 1e-6f);
            Set(CloudCoverShift.TuneShift, -0.5f);
            Assert.AreEqual(0f, Layer("", 0.12f).EffectiveCoverage, "clamped at 0");
            Set(CloudCoverShift.TuneShift, 0.9f);
            Assert.AreEqual(1f, Layer("", 0.5f).EffectiveCoverage, "clamped at 1");
            Set(CloudCoverShift.TuneShift, float.NaN);
            Assert.AreEqual(0.5f, Layer("", 0.5f).EffectiveCoverage, "a NaN is no shift");
        }

        [TestMethod]
        public void TheRainKeyActsOnTheWeatherGroupOnlyAndAddsToTheGeneralOne()
        {
            Set(CloudCoverShift.TuneShiftRain, -0.08f);
            Assert.AreEqual(0.42f, Layer("weather", 0.5f).EffectiveCoverage, 1e-6f);
            Assert.AreEqual(0.35f, Layer("", 0.35f).EffectiveCoverage, 1e-6f, "the cumulus is left alone");
            Assert.AreEqual(0.3f, Layer("other", 0.3f).EffectiveCoverage, 1e-6f);
            Set(CloudCoverShift.TuneShift, -0.05f);
            Assert.AreEqual(0.37f, Layer("weather", 0.5f).EffectiveCoverage, 1e-6f);
            Assert.AreEqual(0.30f, Layer("", 0.35f).EffectiveCoverage, 1e-6f);
        }

        [TestMethod]
        public void TheStampRisesWhenTheShiftChangesSoThatTheFrameValuesAreUploadedAgain()
        {
            Clear();
            long before = CloudCoverShift.Stamp;
            Assert.AreEqual(before, CloudCoverShift.Stamp, "no change, no new stamp");
            Set(CloudCoverShift.TuneShift, -0.08f);
            long shifted = CloudCoverShift.Stamp;
            Assert.IsTrue(shifted > before);
            Set(CloudCoverShift.TuneShiftRain, 0f);
            Assert.AreEqual(shifted, CloudCoverShift.Stamp, "a key set to its value does not count");
            Clear();
            Assert.IsTrue(CloudCoverShift.Stamp > shifted, "back to 0 is a change");
        }

        [TestMethod]
        public void TheDensityFollowsTheShiftWhereTheMapSitsBetweenTheTwoThresholds()
        {
            Clear();
            CloudLayerDef rain = Layer("weather", 0.5f);
            var table = new LayerTypeTable(rain);
            double map = 0.40;
            double before = LayerDensity.Extinction(rain, table, 630.0, map, 0.5);
            Set(CloudCoverShift.TuneShiftRain, 0.1f);
            double more = LayerDensity.Extinction(rain, table, 630.0, map, 0.5);
            Assert.IsTrue(more > before, "a higher threshold clouds more at the same map value: " + before + " -> " + more);
            Set(CloudCoverShift.TuneShiftRain, -0.08f);
            Assert.IsTrue(LayerDensity.Extinction(rain, table, 630.0, map, 0.5) < before);
        }

        [TestMethod]
        public void TheModelsExpectedClosedSharesOfTheK_T2dVariants()
        {
            // The numbers written into tests/series/K-T2d.json before the run: Stratus of the rain deck, ground view (slant 3.24), coverage 0.5 / 0.42 / 0.35.
            double[] map = { 0.473, 0.514, 0.60 };
            double[] c50 = { 0.71, 0.85, 1.00 }, c42 = { 0.44, 0.58, 0.87 }, c35 = { 0.21, 0.35, 0.63 };
            for (int i = 0; i < map.Length; i++)
            {
                Assert.AreEqual(c50[i], CloudCoverModel.RainStratusClosed(map[i], 0.0, 3.24), 0.03, "0.5 at " + map[i]);
                Assert.AreEqual(c42[i], CloudCoverModel.RainStratusClosed(map[i], -0.08, 3.24), 0.03, "0.42 at " + map[i]);
                Assert.AreEqual(c35[i], CloudCoverModel.RainStratusClosed(map[i], -0.15, 3.24), 0.03, "0.35 at " + map[i]);
            }
            // At the zenith the deck is thinner to look through: less is closed.
            Assert.IsTrue(CloudCoverModel.RainStratusClosed(0.473, 0.0, 1.0) < CloudCoverModel.RainStratusClosed(0.473, 0.0, 3.24));
            // The coverage that closes half the sky at map 0.5 is 0.41 to 0.43 for the ground view: a shift of -0.08 is the file's answer to the owner's question.
            double half = CloudCoverModel.RainStratusClosed(0.5, -0.08, 3.24);
            Assert.IsTrue(half > 0.45 && half < 0.65, "closed share at map 0.5 with coverage 0.42: " + half);
        }

        [TestMethod]
        public void TheClosedShareOfAPictureIsTheSamplesWhoseColourChangedByTheThreshold()
        {
            const int w = 80, h = 60;
            var baseline = new Color[w * h];
            for (int i = 0; i < baseline.Length; i++) baseline[i] = new Color(0.2f, 0.4f, 0.9f, 1f);
            // The sky above the horizon at v 0.4: the left half a closed beige deck of the sky's own luminance whose brightness varies by 15 percent across it (K-T2d start 48's
            // ground-low sheet), a quarter of it a thin veil (change 0.05), the rest clear.
            var picture = (Color[])baseline.Clone();
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    if (x < w / 2)
                    {
                        float k = 0.85f + 0.3f * x / (w / 2);
                        picture[y * w + x] = new Color(0.5f * k, 0.4f * k, 0.3f * k, 1f);
                    }
                    else if (x < 3 * w / 4) picture[y * w + x] = new Color(0.25f, 0.42f, 0.9f, 1f);
                }
            }
            T09Readout.Reading off = T09Readout.Measure(baseline, w, h, 0.4, null);
            T09Readout.Reading r = T09Readout.Measure(picture, w, h, 0.4, off);
            Assert.AreEqual(0.5, r.SkyClosedShare, 0.03, "half of the sky is the closed deck; the veil is changed but not closed");
            Assert.IsTrue(r.SkyDeckShare < 0.4, "the luminance rule of the deck misses part of a beige sheet over a sky of the same luminance: " + r.SkyDeckShare);
            Assert.AreEqual(0.5, r.GroundClosedShare, 0.03, "the picture is the same below the horizon");
            T09Readout.Reading clear = T09Readout.Measure(baseline, w, h, 0.4, off);
            Assert.AreEqual(0.0, clear.SkyClosedShare, 1e-9, "no change, nothing closed");
            // Opacity: a grey cloud over the blue sky changes the pixel by a times its distance; 0.9 is closed, 0.3 is not.
            Color cloud = new Color(0.6f, 0.6f, 0.6f, 1f), sky = new Color(0.2f, 0.4f, 0.9f, 1f);
            var thick = new Color[w * h];
            var thin = new Color[w * h];
            for (int i = 0; i < thick.Length; i++)
            {
                thick[i] = Color.Lerp(sky, cloud, 0.9f);
                thin[i] = Color.Lerp(sky, cloud, 0.3f);
            }
            Assert.AreEqual(1.0, T09Readout.Measure(thick, w, h, 0.4, off).SkyClosedShare, 1e-9);
            Assert.AreEqual(0.0, T09Readout.Measure(thin, w, h, 0.4, off).SkyClosedShare, 1e-9);
        }
    }
}
