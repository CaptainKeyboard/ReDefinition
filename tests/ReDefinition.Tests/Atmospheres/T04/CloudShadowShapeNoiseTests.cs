using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests
{
    // The shape noise in the cloud shadow's CPU twin (E40 FL-FIX): the shadow of a cell field has cells, the switch off or a missing volume leaves the map's threshold alone, and
    // the share the shadow reads is the shape model's (the shader's NoiseShareNear is compared with it in the weather player, series e40-ff).
    [TestClass]
    public class CloudShadowShapeNoiseTests
    {
        private static readonly NoiseVolumeSet Volumes = NoiseVolumeSet.Generate(NoiseSizes.ForLevel(0), 5u);

        // A slab of vertical optical depth 1 at a constant map value, the layer's coverage 0.35 and softness 0.15 (the shipped cumulus layer's), noise amount 1 at 500 m.
        private static PatchSource Source(float noiseAmount)
        {
            var s = new PatchSource(ShadowScenes.Flat).AddSlab(2000.0, 2020.0, 1.0, (e, n) => 0.665);
            CloudLayerDef layer = s.LayerAt(0);
            layer.Coverage = 0.35f;
            layer.EdgeSoftness = 0.15f;
            layer.NoiseAmount = noiseAmount;
            layer.NoiseScaleM = 500f;
            return s;
        }

        private static CloudShadowShapeNoise Noise(NoiseVolumeSet set)
        {
            var n = new CloudShadowShapeNoise();
            NoiseLayerParams prm = NoiseLayerParams.Default(500.0);
            prm.OffsetX = 12345.0;
            prm.OffsetY = 6789.0;
            prm.OffsetZ = 4321.0;
            n.Set(set, new[] { prm }, null);
            return n;
        }

        // The sun's direct transmission at a grid of ground points 400 m apart (60 by 60, 24 km a side: several cells of 2 to 4 km), the sun straight up.
        private static double[] Factors(CloudShadowField field)
        {
            var f = new double[3600];
            for (int i = 0; i < 60; i++)
                for (int j = 0; j < 60; j++)
                    f[i * 60 + j] = field.DirectTransmission(ShadowScenes.Flat, i * 400.0, j * 400.0, 1.0, 0.0, 0.0);
            return f;
        }

        private static void MeanAndSpread(double[] v, out double mean, out double spread)
        {
            mean = 0.0;
            foreach (double x in v) mean += x;
            mean /= v.Length;
            spread = 0.0;
            foreach (double x in v) spread += (x - mean) * (x - mean);
            spread = Math.Sqrt(spread / v.Length);
        }

        [TestMethod]
        public void WithoutTheNoiseTheFieldIsTheMapsThresholdAlone()
        {
            PatchSource s = Source(1f);
            CloudShadowField plain = ShadowScenes.Field(s, 1, 12);
            double[] a = Factors(plain);
            // A missing volume, an inactive object and a layer without noise amount all give the same numbers as no object.
            CloudShadowField none = ShadowScenes.Field(s, 1, 12);
            none.ShapeNoise = Noise(null);
            double[] b = Factors(none);
            CollectionAssert.AreEqual(a, b);
            PatchSource flat = Source(0f);
            CloudShadowField zero = ShadowScenes.Field(flat, 1, 12);
            zero.ShapeNoise = Noise(Volumes);
            CollectionAssert.AreEqual(Factors(ShadowScenes.Field(flat, 1, 12)), Factors(zero));
            double mean, spread;
            MeanAndSpread(a, out mean, out spread);
            Assert.AreEqual(0.0, spread, 1e-6, "a constant map under the plain threshold is one number everywhere");
        }

        [TestMethod]
        public void TheShadowOfACellFieldHasCellsAndTheMeanFollowsTheShare()
        {
            PatchSource s = Source(1f);
            CloudShadowField noisy = ShadowScenes.Field(s, 1, 12);
            noisy.ShapeNoise = Noise(Volumes);
            double[] f = Factors(noisy);
            double mean, spread;
            MeanAndSpread(f, out mean, out spread);
            // The plain threshold at the map 0.665: s = (0.665 + 0.35 - 1) / 0.15 + 0.5 = 0.6 everywhere, tau 0.6, factor 0.549. With the noise at amount 1 the share is 0 or 1
            // in most cells (the threshold moves by up to half the amount), so the ground is lit in some and dark in others.
            Assert.IsTrue(spread > 0.15, "the shadow has cells: spread " + spread);
            double lit = 0.0, dark = 0.0;
            foreach (double x in f) { if (x > 0.9) lit++; if (x < 0.5) dark++; }
            Assert.IsTrue(lit / f.Length > 0.15 && dark / f.Length > 0.15, "lit " + lit / f.Length + " dark " + dark / f.Length);
            // The mean optical depth equals the mean share (the noise is uniform): about the plain 0.6 within the sample's scatter of a few cells.
            double meanShare = 0.0;
            NoiseLayerParams prm = NoiseLayerParams.Default(500.0);
            prm.OffsetX = 12345.0;
            prm.OffsetY = 6789.0;
            prm.OffsetZ = 4321.0;
            for (int i = 0; i < 60; i++)
                for (int j = 0; j < 60; j++)
                    meanShare += CloudShapeModel.Share(Volumes, ref prm, ShadowScenes.Flat + 2010.0, i * 400.0, j * 400.0, 0.665, 0.35, 0.15, 1.0, 0.0, false);
            meanShare /= 3600.0;
            double meanTau = 0.0;
            foreach (double x in f) meanTau += -Math.Log(Math.Max(x, 1e-12));
            meanTau /= f.Length;
            Assert.AreEqual(meanShare, meanTau, 0.12, "the shadow's mean optical depth is the share's mean (tau 1 slab)");
        }

        [TestMethod]
        public void TheSharePassedToTheFieldIsTheShapeModelsAtTheRotatedPoint()
        {
            NoiseLayerParams prm = NoiseLayerParams.Default(500.0);
            prm.OffsetX = 12345.0;
            prm.OffsetY = 6789.0;
            prm.OffsetZ = 4321.0;
            // The map frame turned so that up is world +y and east world +x (the player's): the world-to-map rows (0 1 0, 1 0 0, 0 0 1).
            var n = new CloudShadowShapeNoise();
            n.Set(Volumes, new[] { prm }, new double[] { 0, 1, 0, 1, 0, 0, 0, 0, 1 });
            PatchSource s = Source(1f);
            CloudLayerDef layer = s.LayerAt(0);
            // The map point (x up, y east, z north) is the world point (east, up, north) = (y, x, z).
            double got = n.Share(0, layer, 2.0e9 + 2010.0, 321.0, 123.0, 0.665);
            double want = CloudShapeModel.Share(Volumes, ref prm, 321.0, 2.0e9 + 2010.0, 123.0, 0.665, 0.35, 0.15, 1.0, 0.0, false);
            Assert.AreEqual(want, got, 1e-12);
            Assert.AreEqual(-1.0, new CloudShadowShapeNoise().Share(0, layer, 0, 0, 0, 0.5), "not set: not given");
        }
    }
}
