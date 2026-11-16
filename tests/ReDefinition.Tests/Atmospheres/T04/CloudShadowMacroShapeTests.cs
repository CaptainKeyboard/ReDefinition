using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests
{
    // CONSIST: the cloud shadow reads the macro shape as the pass does: the type's LIFT (with the top's closure) lowers the map by its share of the layer's headroom, in the CPU twin
    // (CloudShadowShapeNoise) and in the shader (ReDefinitionAtmospheresCloudShadow.shader: cov -= _CsParams2.z * lift * (coverage + 0.5 amount + 0.5 edge)), both on the same type table.
    [TestClass]
    public class CloudShadowMacroShapeTests
    {
        private static readonly NoiseVolumeSet Volumes = NoiseVolumeSet.Generate(NoiseSizes.ForLevel(0), 5u);

        private static CloudShadowShapeNoise Noise()
        {
            var n = new CloudShadowShapeNoise();
            NoiseLayerParams prm = NoiseLayerParams.Default(500.0);
            prm.OffsetX = 12345.0;
            prm.OffsetY = 6789.0;
            prm.OffsetZ = 4321.0;
            n.Set(Volumes, new[] { prm }, null);
            return n;
        }

        private static CloudLayerDef Layer(float noiseAmount, float topClose, out LayerTypeTable table)
        {
            CloudTypeDef type = CloudTypeDef.Slab("s", 0.1f, 0.9f);
            type.Lift = new Curve(new[] { 0f, 0.5f, 1f }, new[] { 0f, 0.1f, 0.3f });
            type.TopClose = topClose;
            type.EdgePower = 0.5f;
            var layer = new CloudLayerDef("l", 1, 1000f, 3000f, new[] { type }, 0.001f);
            layer.Coverage = 0.35f;
            layer.EdgeSoftness = 0.15f;
            layer.NoiseAmount = noiseAmount;
            layer.NoiseScaleM = 500f;
            table = new LayerTypeTable(layer);
            return layer;
        }

        // The shader's lift for a height of the layer, float as the shader has it: the profile texture's channel (bilinear in the height of the type) times the headroom.
        private static float ShaderLift(LayerTypeTable table, CloudLayerDef layer, float layerShare)
        {
            float b, t, e;
            table.Info(0, out b, out t, out e);
            float h = (layerShare - b) / (t - b);
            float x = Math.Max(0f, Math.Min(1f, h)) * (LayerTypeTable.ProfileSamples - 1);
            int i = Math.Min((int)x, LayerTypeTable.ProfileSamples - 2);
            float f = x - i;
            float lift = table.LiftData[i] * (1f - f) + table.LiftData[i + 1] * f;
            float edge = Math.Max(layer.EffectiveEdgeSoftness, 1e-4f);
            return lift * (layer.EffectiveCoverage + 0.5f * layer.EffectiveNoiseAmount + 0.5f * edge);
        }

        [TestMethod]
        public void TheTwinLowersTheMapByTheShadersLiftAtEveryHeight()
        {
            LayerTypeTable table;
            CloudLayerDef layer = Layer(1f, 0.25f, out table);
            CloudShadowShapeNoise noise = Noise();
            // The same shape with the lift taken into the map value by hand must be what the twin returns with the lift in its own call.
            LayerTypeTable none;
            CloudLayerDef flat = Layer(1f, 0f, out none);
            flat.Types[0].Lift = null;
            none = new LayerTypeTable(flat);
            double worst = 0.0;
            int lifted = 0;
            for (int k = 0; k <= 20; k++)
            {
                double share = 0.12 + 0.76 * k / 20.0;
                foreach (double cov in new[] { 0.5, 0.62, 0.75 })
                {
                    double x = 1234.0 + 211.0 * k, y = 987.0, z = 555.0 + 37.0 * k;
                    double twin = noise.Share(0, layer, x, y, z, cov, table, 0.0, share);
                    double byHand = noise.Share(0, flat, x, y, z, cov - ShaderLift(table, layer, (float)share), none, 0.0, share);
                    worst = Math.Max(worst, Math.Abs(twin - byHand));
                    if (ShaderLift(table, layer, (float)share) > 0f) lifted++;
                }
            }
            Assert.IsTrue(lifted > 20, "the lift acts at many of the points (" + lifted + ")");
            Assert.IsTrue(worst < 2e-3, "twin against the shader's formula, worst " + worst);
        }

        [TestMethod]
        public void TheLiftNarrowsTheShadowTowardsTheTypesTop()
        {
            LayerTypeTable table;
            CloudLayerDef layer = Layer(1f, 0.25f, out table);
            CloudShadowShapeNoise noise = Noise();
            double lowSum = 0.0, highSum = 0.0, highWithout = 0.0;
            for (int k = 0; k < 200; k++)
            {
                double x = 100.0 * k, y = 31.0 * k, z = 77.0 * k;
                lowSum += noise.Share(0, layer, x, y, z, 0.62, table, 0.0, 0.2);
                highSum += noise.Share(0, layer, x, y, z, 0.62, table, 0.0, 0.85);
                highWithout += noise.Share(0, layer, x, y, z, 0.62, table, 0.0, double.NaN);
            }
            Assert.IsTrue(highSum < 0.5 * highWithout, "near the top the lift closes the columns: " + highSum + " against " + highWithout);
            Assert.IsTrue(lowSum > highSum);
        }

        [TestMethod]
        public void TheTypesShapeSmoothingMovesTheShadowsShapeLikeThePass()
        {
            LayerTypeTable table;
            CloudLayerDef layer = Layer(1f, 0f, out table);
            layer.Types[0].Lift = null;
            layer.Types[0].ShapeSmoothing = 3f;
            table = new LayerTypeTable(layer);
            LayerTypeTable sharp;
            CloudLayerDef plainLayer = Layer(1f, 0f, out sharp);
            plainLayer.Types[0].Lift = null;
            sharp = new LayerTypeTable(plainLayer);
            CloudShadowShapeNoise noise = Noise();
            double smooth = 0.0, rough = 0.0, diff = 0.0;
            for (int k = 0; k < 200; k++)
            {
                double x = 150.0 * k, y = 41.0 * k, z = 63.0 * k;
                double a = noise.Share(0, layer, x, y, z, 0.62, table, 0.0, 0.5), b = noise.Share(0, plainLayer, x, y, z, 0.62, sharp, 0.0, 0.5);
                diff += Math.Abs(a - b);
                smooth += a;
                rough += b;
            }
            Assert.IsTrue(diff > 1.0, "the smoothed shape differs from the unsmoothed one (" + diff + ")");
            Assert.AreEqual(3.0, table.SmoothingOf(0.0), 1e-6);
        }

        [TestMethod]
        public void ALayerWithoutNoiseGetsTheLiftToo()
        {
            LayerTypeTable table;
            CloudLayerDef layer = Layer(0.02f, 0.25f, out table);
            CloudShadowShapeNoise noise = Noise();
            layer.NoiseAmount = 0f;
            double nolift = noise.Share(0, layer, 1.0, 2.0, 3.0, 0.62, table, 0.0, double.NaN);
            Assert.AreEqual(-1.0, nolift, 0.0, "no height given and no noise: the plain path");
            double top = noise.Share(0, layer, 1.0, 2.0, 3.0, 0.62, table, 0.0, 0.88);
            Assert.IsTrue(top >= 0.0 && top < LayerDensity.Share(layer, 0.62, table, 0.0), "the top's closure lowers the share where the map alone does not");
        }
    }
}
