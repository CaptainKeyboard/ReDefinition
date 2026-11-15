using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests.Atmospheres.T01
{
    // The blue-noise tile of the layer march's per-pixel offsets (E40 F2): a permutation of the strata 0..255 with no low-frequency power.
    [TestClass]
    public class DitherMatrixTests
    {
        [TestMethod]
        public void TheTileHoldsEveryRankOnce()
        {
            var seen = new bool[DitherMatrix.Count];
            for (int y = 0; y < DitherMatrix.Side; y++)
                for (int x = 0; x < DitherMatrix.Side; x++)
                {
                    int r = DitherMatrix.RankAt(x, y);
                    Assert.IsTrue(r >= 0 && r < DitherMatrix.Count, "rank " + r);
                    Assert.IsFalse(seen[r], "rank " + r + " twice");
                    seen[r] = true;
                }
        }

        [TestMethod]
        public void TheValuesAreTheMiddlesOfTheStrataAndTheTileRepeats()
        {
            float[] v = DitherMatrix.Values();
            Assert.AreEqual(DitherMatrix.Count, v.Length);
            for (int i = 0; i < v.Length; i++)
            {
                Assert.AreEqual((DitherMatrix.RankAt(i % 16, i / 16) + 0.5f) / 256f, v[i], 1e-7f);
                Assert.IsTrue(v[i] > 0f && v[i] < 1f);
            }
            Assert.AreEqual(DitherMatrix.RankAt(3, 5), DitherMatrix.RankAt(3 + 16, 5 + 32), "the tile repeats");
        }

        // Blue noise: the power of the tile's spectrum below 0.15 cycles per texel is a small share (a white tile has about 7 percent there; the gradient noise has lines).
        [TestMethod]
        public void TheTileHasNoLowFrequencyPower()
        {
            int n = DitherMatrix.Side;
            double total = 0, low = 0;
            for (int ky = 0; ky < n; ky++)
                for (int kx = 0; kx < n; kx++)
                {
                    if (kx == 0 && ky == 0) continue;
                    double re = 0, im = 0;
                    for (int y = 0; y < n; y++)
                        for (int x = 0; x < n; x++)
                        {
                            double u = (DitherMatrix.RankAt(x, y) + 0.5) / DitherMatrix.Count - 0.5;
                            double a = -2.0 * Math.PI * (kx * x + ky * y) / n;
                            re += u * Math.Cos(a);
                            im += u * Math.Sin(a);
                        }
                    double p = re * re + im * im;
                    double fx = (kx > n / 2 ? kx - n : kx) / (double)n, fy = (ky > n / 2 ? ky - n : ky) / (double)n;
                    total += p;
                    if (Math.Sqrt(fx * fx + fy * fy) < 0.15) low += p;
                }
            Assert.IsTrue(low / total < 0.02, "low-frequency share " + low / total);
        }
    }
}
