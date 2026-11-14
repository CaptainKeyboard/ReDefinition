using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests.Atmospheres.T09
{
    // E40 soft density, package B1: the product's density is the soft water content of CloudWater, through CloudLayerDef's Effective* numbers, the type table's profile and one
    // helper for the exponent. Expected results, written before the first run: with the default numbers the share the layer pass and every CPU twin form from the layer's
    // effective numbers equals CloudWater's water content (outline where the old share began) at a few hundred places of each of Kerbin's four layers; a map value of 0 is a clear
    // sky; the type table carries the rise by height and the density scale; the settings reach the march. (The switch layers.softDensity and the old density with it went in
    // E40 step (c): the tests of the knobs' arithmetic read the layer's own numbers through ThresholdCoverage and LookedEdgeSoftness.)

    [TestClass]
    public class CloudSoftDensityTests
    {
        private static int frame = 7000000;

        private static void Set(string key, float value)
        {
            Tuning.SetOverride(key, value);
            Tuning.Instance.BeginFrame(++frame);
        }

        private static CloudLayerDef[] Layers()
        {
            return new[] { CloudLookFixtures.Rain(), CloudLookFixtures.Cumulus(), CloudLookFixtures.Cumulonimbus(), CloudLookFixtures.Cirrus() };
        }

        [TestCleanup]
        public void Clean()
        {
            Tuning.ClearOverrides();
            Tuning.Instance.BeginFrame(++frame);
            CloudSoftDensity.Softness = CloudSoftDensity.DefaultSoftness;
            CloudSoftDensity.DetailMean = CloudSoftDensity.NominalDetailMean;
        }

        // The old share expression of the layer pass with the layer's effective numbers, at a noise rank n (the map value after the clear-sky rule).
        private static double Share(CloudLayerDef layer, double map, double n)
        {
            double amount = layer.EffectiveNoiseAmount, edge = Math.Max(layer.EffectiveEdgeSoftness, 1e-4);
            double x = (layer.ClearSkyMap(map) + (n - 0.5) * amount + layer.EffectiveCoverage - 1.0) / edge + 0.5;
            return x <= 0.0 ? 0.0 : x >= 1.0 ? 1.0 : x;
        }

        [TestMethod]
        public void TheLayersShareIsTheWaterContentWhoseOutlineIsWhereTheOldShareBegan()
        {
            var rng = new Random(11);
            foreach (CloudLayerDef layer in Layers())
            {
                Assert.IsTrue(layer.Soft, layer.Name);
                double w = CloudSoftDensity.Softness * layer.SoftnessScale, a = layer.EffectiveNoiseAmount, closed = layer.SoftClosed;
                Assert.AreEqual(a * w, layer.EffectiveEdgeSoftness, 1e-6, layer.Name + " edge = amount x width");
                var k = new CloudWaterNumbers { Width = w, Power = layer.WaterPower, BaseGain = 0.3, RiseShare = 0.5 };
                for (int i = 0; i < 300; i++)
                {
                    double map = rng.NextDouble(), n = rng.NextDouble();
                    // The outline: where the old share (coverage C, edge e, amount A) began, in noise rank.
                    double c = layer.ThresholdCoverage, e = layer.EffectiveEdgeSoftness;
                    double oldEdge = layer.EdgeSoftness;
                    double start = 0.5 + (1.0 - map - c - 0.5 * oldEdge) / a;
                    double water = CloudWater.Content(n, start, closed, k);
                    // The clear-sky rule moves a map value below K: the outline is that of the moved value.
                    double moved = layer.ClearSkyMap(map);
                    double startMoved = 0.5 + (1.0 - moved - c - 0.5 * oldEdge) / a;
                    double expected = CloudWater.Content(n, startMoved, closed, k);
                    Assert.AreEqual(expected, Share(layer, map, n), 2e-5, layer.Name + " map " + map.ToString("0.000") + " n " + n.ToString("0.000"));
                    if (map >= layer.ClearSkyK) Assert.AreEqual(water, expected, 1e-12);
                }
            }
        }

        [TestMethod]
        public void AMapValueOfZeroIsAClearSkyAndTheRuleIsIdentityAboveItsK()
        {
            foreach (CloudLayerDef layer in Layers())
            {
                for (int j = 0; j <= 20; j++) Assert.AreEqual(0.0, Share(layer, 0.0, j / 20.0), 1e-9, layer.Name + " n " + j);
                float k = layer.ClearSkyK;
                Assert.IsTrue(k > 0f && k < 1f, layer.Name + " K " + k);
                Assert.AreEqual(0.9f, layer.ClearSkyMap(0.9f), 1e-6f);
                Assert.IsTrue(layer.ClearSkyMap(k * 0.5f) < k * 0.5f, "below K the map value is moved down");
                Assert.AreEqual(k, layer.ClearSkyMap(k), 1e-6f, "K is the fixed point");
            }
        }

        [TestMethod]
        public void TheTypeTableCarriesTheRiseByHeightAndTheDensityScale()
        {
            CloudLayerDef layer = CloudLookFixtures.Cumulus();
            var table = new LayerTypeTable(layer);
            CloudTypeDef type = layer.Types[0];
            double h = 0.25;
            double plain = type.Profile.Evaluate((float)h);
            double rise = CloudWater.Rise(h, new CloudWaterNumbers { BaseGain = type.BaseGain, RiseShare = type.RiseShare });
            double shipped = type.DensityScale;   // the fitted opacity of the type (E40 B3)
            Assert.AreEqual(plain * rise * shipped, table.ProfileAt(0, h), 2e-3);
            // The base of the type is cut to the gain, the top reads the profile alone.
            Assert.AreEqual(type.Profile.Evaluate(1f), table.ProfileAt(0, 1.0), 1e-6);
            // A density scale is a factor on the whole profile and enters the water content at the outline.
            double closedBefore = layer.SoftClosed;
            foreach (CloudTypeDef t in layer.Types) t.DensityScale = 0.5f;
            var scaled = new LayerTypeTable(layer);
            Assert.AreEqual(0.5 * plain * rise, scaled.ProfileAt(0, h), 2e-3);
            Assert.IsTrue(layer.SoftClosed > closedBefore, "a thinner column needs more water to be closed");
        }

        [TestMethod]
        public void ALayerWithoutNoiseKeepsThePlainSoftEdgeOfItsOwnNumbers()
        {
            CloudLayerDef layer = CloudLookFixtures.Cumulus();
            layer.NoiseAmount = 0f;
            Assert.IsFalse(layer.Soft);
            Assert.AreEqual(layer.ThresholdCoverage, layer.EffectiveCoverage, 0f);
            Assert.AreEqual(layer.EdgeSoftness, layer.EffectiveEdgeSoftness, 1e-6f);
            Assert.AreEqual(0f, layer.ClearSkyK);
            Assert.AreEqual(0.7f, layer.ClearSkyMap(0.7f));
            Assert.AreEqual(1f, layer.EffectiveWaterPower);
            // Nothing hardens the edge: the eroded share is the plain eroded share.
            Assert.AreEqual(0.2, CloudShapeModel.Erode(0.6, 0.0, 0.5), 1e-12);
        }

        [TestMethod]
        public void TheSoftnessSettingAndTheLayersScaleWidenTheRim()
        {
            CloudLayerDef rain = CloudLookFixtures.Rain();
            float at50 = rain.EffectiveEdgeSoftness;
            CloudSoftDensity.Softness = 1.0;
            Assert.AreEqual(2f * at50, rain.EffectiveEdgeSoftness, 1e-6f);
            CloudSoftDensity.Softness = 0.1;
            Assert.AreEqual(CloudSoftDensity.MinSoftness, CloudSoftDensity.Softness, 0.0, "clamped to 0.3 to 1.0");
            rain.SoftnessScale = 2f;
            CloudSoftDensity.Softness = 0.5;
            // amount 0.3 x width (0.5 x 2)
            Assert.AreEqual(0.3f, rain.EffectiveEdgeSoftness, 1e-6f);
        }

        [TestMethod]
        public void TheGridPointsFollowTheSamplesChoiceByTheSameShareAtEveryStepQuality()
        {
            Assert.AreEqual(48, CloudSoftDensity.ScaleSamples(48, 48));
            Assert.AreEqual(24, CloudSoftDensity.ScaleSamples(48, 24));
            Assert.AreEqual(32, CloudSoftDensity.ScaleSamples(48, 32));
            Assert.AreEqual(32, CloudSoftDensity.ScaleSamples(64, 24));
            Assert.AreEqual(21, CloudSoftDensity.ScaleSamples(32, 32));
            Assert.AreEqual(24, OwnCloudOptions.SamplesOf(CloudDetailLevel.Low));
            Assert.AreEqual(32, OwnCloudOptions.SamplesOf(CloudDetailLevel.Medium));
            Assert.AreEqual(48, OwnCloudOptions.SamplesOf(CloudDetailLevel.High));
        }

        [TestMethod]
        public void TheOwnersTwoFavouritesAreReachableBySettingsAlone()
        {
            Func<string, double> v = key => AtmospheresSettings.Definition(key).Default;
            var frame = new LayerFrame();
            OwnCloudOptions.Apply(frame, v);
            Assert.AreEqual(48, frame.MaxSteps, "the starting values: softness 0.5 with 48 points");
            Assert.AreEqual(0.5, CloudSoftDensity.Softness, 1e-9);
            Func<string, double> low = key => key == "cloudSoftness" ? 1.0 : key == "cloudSamples" ? (double)(int)CloudDetailLevel.Low : v(key);
            OwnCloudOptions.Apply(frame, low);
            Assert.AreEqual(24, frame.MaxSteps, "softness 1.0 with 24 points");
            Assert.AreEqual(1.0, CloudSoftDensity.Softness, 1e-9);
        }

        [TestMethod]
        public void TheFarMeanOfTheShareToAPowerIsTheMeanOfThePowers()
        {
            double cov = 0.55, coverage = 0.4, edge = 0.2, amount = 0.8, erosion = 0.3, detailMean = 0.5;
            foreach (double power in new[] { 1.0, 2.0, 3.0 })
            {
                double sum = 0.0;
                const int n = 40000;
                for (int i = 0; i < n; i++)
                    sum += CloudWater.Pow(FarViewMath.ShapeSample(cov, coverage, edge, amount, erosion, detailMean, (i + 0.5) / n), power);
                Assert.AreEqual(sum / n, FarViewMath.ShapeMean(cov, coverage, edge, amount, erosion, detailMean, 0.0, power), 2e-4, "power " + power);
            }
        }

        [TestMethod]
        public void TheDetailMeanNominalMatchesTheGeneratedVolumes()
        {
            NoiseVolumeSet set = NoiseVolumeSet.Generate(NoiseSizes.ForLevel(1), 1u);
            Assert.AreEqual(CloudSoftDensity.NominalDetailMean, set.DetailMean, 0.03);
        }

        [TestMethod]
        public void TheShadowAndTheOracleReadTheSameShareAsTheLayerPass()
        {
            // LayerDensity.Share is the map-only form every no-noise reader uses (the shadow without the shape noise, HeightDensity, the bake): the clear-sky rule, the share, the exponent.
            foreach (CloudLayerDef layer in Layers())
                for (int i = 0; i <= 40; i++)
                {
                    double map = i / 40.0;
                    double expected = CloudWater.Pow(Share(layer, map, 0.5), layer.EffectiveWaterPower);
                    Assert.AreEqual(expected, LayerDensity.Share(layer, map), 1e-5, layer.Name + " " + map);
                }
        }
    }
}
