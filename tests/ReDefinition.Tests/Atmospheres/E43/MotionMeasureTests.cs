using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests.Atmospheres.E43
{
    // The measurement of the rendered layer's motion (review R1): synthetic images shifted by a known amount must give that shift, and a wrong sense or a wrong axis must
    // give a verdict that fails the row.
    [TestClass]
    public class MotionMeasureTests
    {
        private const int W = 96, H = 64;

        // A smooth random field (sum of a few random plane waves), sampled with a shift: value(x - sx, y - sy), so the content moves by (sx, sy).
        private static float[] Field(double sx, double sy, int seed)
        {
            var rnd = new Random(seed);
            const int waves = 14;
            var kx = new double[waves]; var ky = new double[waves]; var ph = new double[waves]; var amp = new double[waves];
            for (int i = 0; i < waves; i++)
            {
                kx[i] = (rnd.NextDouble() * 2 - 1) * 0.5; ky[i] = (rnd.NextDouble() * 2 - 1) * 0.5; ph[i] = rnd.NextDouble() * 6.283; amp[i] = 0.5 + rnd.NextDouble();
            }
            var f = new float[W * H];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    double v = 0.0;
                    for (int i = 0; i < waves; i++) v += amp[i] * Math.Sin(kx[i] * (x - sx) + ky[i] * (y - sy) + ph[i]);
                    f[y * W + x] = (float)v;
                }
            LayerMotionMeasure.Normalise(f);
            return f;
        }

        [TestMethod]
        public void AKnownShiftIsMeasuredToAFractionOfAPixel()
        {
            foreach (var s in new[] { new[] { 3.4, -2.0 }, new[] { -5.0, 1.6 }, new[] { 0.0, 0.0 }, new[] { 6.5, 4.5 } })
            {
                float[] a = Field(0, 0, 7), b = Field(s[0], s[1], 7);
                double dx, dy, conf;
                Assert.IsTrue(LayerMotionMeasure.Estimate(a, b, W, H, 8, out dx, out dy, out conf));
                Assert.AreEqual(s[0], dx, 0.4, "dx for " + s[0] + "," + s[1]);
                Assert.AreEqual(s[1], dy, 0.4, "dy for " + s[0] + "," + s[1]);
                Assert.IsTrue(conf > 0.3, "confidence " + conf);
            }
        }

        [TestMethod]
        public void FlatImagesGiveNoMeasurement()
        {
            var flat = new float[W * H];
            double dx, dy, conf;
            Assert.IsFalse(LayerMotionMeasure.Estimate(flat, flat, W, H, 8, out dx, out dy, out conf));
        }

        [TestMethod]
        public void TheVerdictSeparatesEastwardWestwardAndAWrongAxis()
        {
            // east on the screen is +x, 0.5 px per metre. A layer pinned to +R moves east by 40 m in the interval, pinned to -R west by 40 m (plus a common pattern motion of 10 m).
            double cos, metres;
            LayerMotionMeasure.Verdict(0.5 * (40 + 10), 0.0, 0.5 * (-40 + 10), 0.0, 0.5, 0.0, out cos, out metres);
            Assert.AreEqual(1.0, cos, 1e-9); Assert.AreEqual(40.0, metres, 1e-9);
            // the sense reversed (the rotation sign wrong): the rotation about the same axis turns the other way
            LayerMotionMeasure.Verdict(0.5 * (-40 + 10), 0.0, 0.5 * (40 + 10), 0.0, 0.5, 0.0, out cos, out metres);
            Assert.AreEqual(-1.0, cos, 1e-9); Assert.AreEqual(-40.0, metres, 1e-9);
            // a wrong axis: the pinned rate moves the picture along the screen's y, not along east
            LayerMotionMeasure.Verdict(0.0, 0.5 * 40, 0.0, 0.5 * -40, 0.5, 0.0, out cos, out metres);
            Assert.AreEqual(0.0, cos, 1e-9); Assert.AreEqual(0.0, metres, 1e-9);
        }

        [TestMethod]
        public void TheMeasurementGoesFromRgbBytesToTheVerdict()
        {
            // an RGB screen of the shifted fields: the whole chain (luminance, matching, verdict) with the east direction along +x
            byte[] Rgb(float[] f)
            {
                var o = new byte[W * H * 3];
                for (int i = 0; i < f.Length; i++) { byte v = (byte)Math.Max(0, Math.Min(255, 128 + 40 * f[i])); o[3 * i] = o[3 * i + 1] = o[3 * i + 2] = v; }
                return o;
            }
            float[] p0 = LayerMotionMeasure.Luma(Rgb(Field(0, 0, 3)), W, H, W, H), p1 = LayerMotionMeasure.Luma(Rgb(Field(4.0, 0.0, 3)), W, H, W, H);
            float[] m0 = LayerMotionMeasure.Luma(Rgb(Field(0, 0, 3)), W, H, W, H), m1 = LayerMotionMeasure.Luma(Rgb(Field(-4.0, 0.0, 3)), W, H, W, H);
            double pdx, pdy, pc, mdx, mdy, mc;
            Assert.IsTrue(LayerMotionMeasure.Estimate(p0, p1, W, H, 8, out pdx, out pdy, out pc));
            Assert.IsTrue(LayerMotionMeasure.Estimate(m0, m1, W, H, 8, out mdx, out mdy, out mc));
            double cos, metres;
            LayerMotionMeasure.Verdict(pdx, pdy, mdx, mdy, 0.1, 0.0, out cos, out metres);
            Assert.IsTrue(cos > 0.95, "cosine " + cos);
            Assert.AreEqual(40.0, metres, 4.0);
        }
    }
}
