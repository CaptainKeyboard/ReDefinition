using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests
{
    // E40 L6 shape: the mip chain of the shape volume and the shape smoothing that reads it (cfg key shapeSmoothing).
    [TestClass]
    public class CloudShapeMipTests
    {
        private static NoiseVolumeSet medium;

        private static NoiseVolumeSet Medium
        {
            get { return medium ?? (medium = NoiseVolumeSet.Generate(NoiseSizes.ForLevel(1), 1u)); }
        }

        // Every level is the box mean of the one above, equalised again: its values are as uniform as the volume's own, so the share of the sky a map value covers does not depend on the
        // level; and the fine octaves are gone from it.
        [TestMethod]
        public void TheShapeMipChainKeepsTheUniformSpreadAndLosesTheFineOctaves()
        {
            Assert.AreEqual(7, NoiseVolumeSet.MipLevels(64));
            Assert.AreEqual(Medium.ShapeN >> 1, (int)Math.Round(Math.Pow(Medium.ShapeMips[0].Length, 1.0 / 3.0)));
            Assert.AreEqual(NoiseVolumeSet.MipLevels(Medium.ShapeN) - 1, Medium.ShapeMips.Length);
            for (int m = 1; m <= 3; m++)
            {
                byte[] level = Medium.ShapeMips[m - 1];
                int below = 0, above = 0;
                foreach (byte v in level) { if (v < 64) below++; if (v >= 192) above++; }
                Assert.AreEqual(0.25, below / (double)level.Length, 0.02, "level " + m + ": a quarter of the voxels in the lowest quarter of the values");
                Assert.AreEqual(0.25, above / (double)level.Length, 0.02, "level " + m + ": a quarter in the highest");
            }
            // The fine octaves: a smoothed read differs less over a lag of 0.6 of the finest cell than the volume itself does.
            NoiseLayerParams full = NoiseLayerParams.Default(500.0);
            full.Untile = false;
            NoiseLayerParams smooth = full;
            smooth.ShapeLod = 2.0;
            double lag = 500.0 * 0.6, diffFull = 0.0, diffSmooth = 0.0;
            for (int i = 0; i < 200; i++)
            {
                double x = i * 613.0, y = i * 271.0 + 40.0, z = i * 419.0 + 7.0;
                diffFull += Math.Abs(CloudShapeModel.ShapeAt(Medium, ref full, x, y, z) - CloudShapeModel.ShapeAt(Medium, ref full, x + lag, y, z));
                diffSmooth += Math.Abs(CloudShapeModel.ShapeAt(Medium, ref smooth, x, y, z) - CloudShapeModel.ShapeAt(Medium, ref smooth, x + lag, y, z));
            }
            Assert.IsTrue(diffSmooth < 0.7 * diffFull, "a smoothed read differs less over the same lag: " + diffSmooth / 200 + " against " + diffFull / 200);
            // Level 0 is the volume itself; the lod blends between two levels and stays inside their range.
            NoiseLayerParams zero = full;
            zero.ShapeLod = 0.0;
            Assert.AreEqual(CloudShapeModel.ShapeAt(Medium, ref full, 1234.0, 987.0, 456.0), CloudShapeModel.ShapeAt(Medium, ref zero, 1234.0, 987.0, 456.0), 0.0);
            NoiseLayerParams half = full;
            half.ShapeLod = 1.5;
            NoiseLayerParams one = full, two = full;
            one.ShapeLod = 1.0;
            two.ShapeLod = 2.0;
            double h = CloudShapeModel.ShapeAt(Medium, ref half, 1234.0, 987.0, 456.0);
            double a1 = CloudShapeModel.ShapeAt(Medium, ref one, 1234.0, 987.0, 456.0), a2 = CloudShapeModel.ShapeAt(Medium, ref two, 1234.0, 987.0, 456.0);
            Assert.AreEqual(0.5 * (a1 + a2), h, 1e-12);
        }

        // The cfg key shapeSmoothing is a width in noise scales; the level it asks for follows the volume's size, so that a smoothing of one scale is the same smoothing at every size of
        // the volume.
        [TestMethod]
        public void TheShapeSmoothingIsAWidthInNoiseScalesWhateverTheVolumesSize()
        {
            Assert.AreEqual(0.0, CloudShapeModel.SmoothingLod(0.0, 64), 0.0);
            Assert.AreEqual(0.0, CloudShapeModel.SmoothingLod(0.2, 64), 0.0, "narrower than a voxel: nothing to average");
            Assert.AreEqual(2.0, CloudShapeModel.SmoothingLod(1.0, 64), 1e-12, "a scale is 4 voxels of a 64 volume (16 scales across)");
            Assert.AreEqual(3.0, CloudShapeModel.SmoothingLod(1.0, 128), 1e-12);
            Assert.AreEqual(1.0, CloudShapeModel.SmoothingLod(1.0, 32), 1e-12);
            Assert.AreEqual(6.0, CloudShapeModel.SmoothingLod(100.0, 64), 1e-12, "never past the last level");
        }
    }
}
