using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests.Atmospheres.B09
{
    // The arithmetic of the reconnaissance probe: pixel statistics, Unity's fog formulas, the parachute drift model and the grid sums.
    [TestClass]
    public class ReconMathTests
    {
        [TestMethod]
        public void UniformFractionIsOneForAFlatRegionAndLowForNoise()
        {
            var flat = new List<float>();
            var noisy = new List<float>();
            var random = new Random(3);
            for (int i = 0; i < 400; i++)
            {
                flat.Add(0.8f + (i % 3) * 0.002f);
                noisy.Add((float)random.NextDouble());
            }
            Assert.AreEqual(1.0, ReconMath.UniformFraction(flat, 0.03f), 1e-9);
            Assert.IsTrue(ReconMath.UniformFraction(noisy, 0.03f) < 0.2);
            Assert.AreEqual(0.0, ReconMath.UniformFraction(new List<float>(), 0.03f));
        }

        [TestMethod]
        public void MedianOfOddAndEvenCountsAndTheInputIsKept()
        {
            var odd = new List<float> { 3f, 1f, 2f };
            Assert.AreEqual(2f, ReconMath.Median(odd));
            Assert.AreEqual(3f, odd[0]);
            Assert.AreEqual(2.5f, ReconMath.Median(new List<float> { 4f, 1f, 2f, 3f }), 1e-6);
        }

        [TestMethod]
        public void FogShareFollowsUnitysFormulas()
        {
            // Exp2: the share is 1 - exp(-(density * depth)^2); the chosen density gives one half at 2500 m.
            Assert.AreEqual(0.5, ReconMath.FogShare(3, 3.33e-4, 0, 0, 2500), 0.002);
            Assert.AreEqual(1.0 - Math.Exp(-0.5), ReconMath.FogShare(2, 0.5, 0, 0, 1), 1e-9);
            Assert.AreEqual(0.25, ReconMath.FogShare(1, 0, 100, 500, 200), 1e-9);
            Assert.AreEqual(0.0, ReconMath.FogShare(3, 3.33e-4, 0, 0, 0), 1e-12);
            Assert.AreEqual(0.0, ReconMath.FogShare(0, 1, 0, 0, 100));
        }

        [TestMethod]
        public void MeasuredFogShareRecoversTheShareAndCancelsTheExposure()
        {
            // An object of channel value 0.9 under a fog of colour 0.5 and a share of 0.4: 0.9 * 0.6 + 0.5 * 0.4 = 0.74. The reference
            // object (0.6) is not fogged, but the exposure rose by 10 %: both observed values are multiplied by 1.1.
            double share = 0.4, exposure = 1.1;
            double farOn = (0.9 * (1 - share) + 0.5 * share) * exposure;
            double refOn = 0.6 * exposure;
            Assert.AreEqual(share, ReconMath.MeasuredFogShare(0.9, farOn, 0.6, refOn, 0.5), 1e-9);
            Assert.AreEqual(0.0, ReconMath.MeasuredFogShare(0.9, 0.9, 0.6, 0.6, 0.5), 1e-12);
            Assert.IsTrue(double.IsNaN(ReconMath.MeasuredFogShare(0.0, 0.1, 0.6, 0.6, 0.5)));
        }

        [TestMethod]
        public void ChuteDriftIsNearWindTimesTimeAndZeroWithoutWind()
        {
            double drift = ReconMath.PredictChuteDrift(20.0, 6.0, 9.81, 30.0);
            Assert.IsTrue(drift > 570.0 && drift < 600.0, "drift " + drift);
            Assert.AreEqual(0.0, ReconMath.PredictChuteDrift(0.0, 6.0, 9.81, 30.0), 1e-9);
            Assert.AreEqual(0.0, ReconMath.PredictChuteDrift(20.0, 0.0, 9.81, 30.0));
            // A slower canopy (a larger time constant) lags the wind more.
            Assert.IsTrue(ReconMath.PredictChuteDrift(20.0, 12.0, 9.81, 30.0) < drift);
        }

        [TestMethod]
        public void ChuteDriftBetweenTwoTimesIsTheDifferenceOfTheDriftsFromRest()
        {
            double full = ReconMath.PredictChuteDrift(20.0, 6.0, 9.81, 30.0);
            double early = ReconMath.PredictChuteDrift(20.0, 6.0, 9.81, 15.0);
            double late = ReconMath.PredictChuteDriftBetween(20.0, 6.0, 9.81, 15.0, 30.0);
            Assert.AreEqual(full - early, late, 1e-6);
            // After the canopy has caught up with the wind the late stretch is wind times time.
            Assert.IsTrue(late > 285.0 && late <= 300.0, "late " + late);
            Assert.AreEqual(0.0, ReconMath.PredictChuteDriftBetween(20.0, 6.0, 9.81, 30.0, 15.0));
        }

        private static double[] Cross(double[] a, double[] b)
        {
            return new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] };
        }

        private static double[] Sub(double[] a, double[] b)
        {
            return new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] };
        }

        // Unity's front face is the one for which (b - a) x (c - a) points at the viewer.
        [TestMethod]
        public void DiscTrianglesFaceUp()
        {
            const int segments = 32, rings = 8;
            // up = (0,1,0), t1 = (1,0,0), t2 = Cross(up, t1) = (0,0,-1): the vertex layout of ReconDisc.Update.
            var up = new[] { 0.0, 1.0, 0.0 };
            var t1 = new[] { 1.0, 0.0, 0.0 };
            double[] t2 = Cross(up, t1);
            var vertices = new double[1 + segments * rings][];
            vertices[0] = new[] { 0.0, 0.0, 0.0 };
            for (int r = 1; r <= rings; r++)
            {
                for (int s = 0; s < segments; s++)
                {
                    double phi = 2.0 * Math.PI * s / segments, radius = r / (double)rings;
                    vertices[1 + (r - 1) * segments + s] = new[]
                    {
                        radius * (Math.Cos(phi) * t1[0] + Math.Sin(phi) * t2[0]),
                        radius * (Math.Cos(phi) * t1[1] + Math.Sin(phi) * t2[1]),
                        radius * (Math.Cos(phi) * t1[2] + Math.Sin(phi) * t2[2])
                    };
                }
            }
            int[] triangles = ReconMath.DiscTriangles(segments, rings);
            Assert.AreEqual(segments * 3 + (rings - 1) * segments * 6, triangles.Length);
            for (int i = 0; i < triangles.Length; i += 3)
            {
                double[] a = vertices[triangles[i]], b = vertices[triangles[i + 1]], c = vertices[triangles[i + 2]];
                double[] normal = Cross(Sub(b, a), Sub(c, a));
                Assert.IsTrue(normal[1] > 0.0, "triangle " + i / 3 + " faces down, normal y " + normal[1]);
            }
        }

        [TestMethod]
        public void SphereTrianglesFaceOutward()
        {
            const int segments = 32, rings = 20;
            var vertices = new double[(segments + 1) * (rings + 1)][];
            for (int r = 0; r <= rings; r++)
            {
                double theta = Math.PI * r / rings;
                for (int s = 0; s <= segments; s++)
                {
                    double phi = 2.0 * Math.PI * s / segments;
                    vertices[r * (segments + 1) + s] = new[] { Math.Sin(theta) * Math.Cos(phi), Math.Cos(theta), Math.Sin(theta) * Math.Sin(phi) };
                }
            }
            int[] triangles = ReconMath.SphereTriangles(segments, rings);
            int faced = 0, degenerate = 0;
            for (int i = 0; i < triangles.Length; i += 3)
            {
                double[] a = vertices[triangles[i]], b = vertices[triangles[i + 1]], c = vertices[triangles[i + 2]];
                double[] normal = Cross(Sub(b, a), Sub(c, a));
                double length = Math.Sqrt(normal[0] * normal[0] + normal[1] * normal[1] + normal[2] * normal[2]);
                if (length < 1e-12)
                {
                    degenerate++;
                    continue;
                }
                double outward = normal[0] * (a[0] + b[0] + c[0]) + normal[1] * (a[1] + b[1] + c[1]) + normal[2] * (a[2] + b[2] + c[2]);
                Assert.IsTrue(outward > 0.0, "triangle " + i / 3 + " faces inward");
                faced++;
            }
            Assert.IsTrue(faced > 0 && degenerate <= 2 * segments, "degenerate " + degenerate);
        }

        [TestMethod]
        public void OceanFractionWeighsRowsByTheirArea()
        {
            int w = ReconMath.GridWidth, h = ReconMath.GridHeight;
            var all = new float[w * h];
            for (int i = 0; i < all.Length; i++) all[i] = -5f;
            Assert.AreEqual(1.0, ReconMath.OceanFraction(all, w, h, 0f), 1e-12);

            // The northern half is ocean: half of the sphere's area.
            var north = new float[w * h];
            for (int row = 0; row < h; row++)
            {
                for (int column = 0; column < w; column++) north[row * w + column] = row >= h / 2 ? -1f : 100f;
            }
            Assert.AreEqual(0.5, ReconMath.OceanFraction(north, w, h, 0f), 1e-9);

            // A band at the equator, 10 rows high, is worth more than a band of the same height at the pole.
            var equator = new float[w * h];
            var pole = new float[w * h];
            for (int i = 0; i < equator.Length; i++) equator[i] = pole[i] = 100f;
            for (int row = h / 2 - 5; row < h / 2 + 5; row++) for (int c = 0; c < w; c++) equator[row * w + c] = -1f;
            for (int row = 0; row < 10; row++) for (int c = 0; c < w; c++) pole[row * w + c] = -1f;
            Assert.IsTrue(ReconMath.OceanFraction(equator, w, h, 0f) > 3 * ReconMath.OceanFraction(pole, w, h, 0f));
            Assert.IsTrue(double.IsNaN(ReconMath.OceanFraction(new float[3], w, h, 0f)));
        }

        [TestMethod]
        public void GridDirectionIsAUnitVectorWithTheNorthPoleOnY()
        {
            double x, y, z;
            ReconMath.GridDirection(0, ReconMath.GridHeight - 1, ReconMath.GridWidth, ReconMath.GridHeight, out x, out y, out z);
            Assert.AreEqual(1.0, x * x + y * y + z * z, 1e-12);
            Assert.IsTrue(y > 0.99);
            ReconMath.GridDirection(180, 90, ReconMath.GridWidth, ReconMath.GridHeight, out x, out y, out z);
            Assert.AreEqual(1.0, x * x + y * y + z * z, 1e-12);
            Assert.IsTrue(x > 0.99, "longitude 0, latitude 0 points along +x");
        }

        [TestMethod]
        public void PanIndexAndAddedRms()
        {
            Assert.AreEqual(1.0, ReconMath.PanIndex(0.2, 0.0), 1e-9);
            Assert.AreEqual(-1.0, ReconMath.PanIndex(0.0, 0.2), 1e-9);
            Assert.AreEqual(0.0, ReconMath.PanIndex(0.0, 0.0));
            Assert.AreEqual(0.0, ReconMath.AddedRms(0.1, 0.2));
            Assert.AreEqual(0.3, ReconMath.AddedRms(Math.Sqrt(0.3 * 0.3 + 0.04), 0.2), 1e-9);
        }

        [TestMethod]
        public void DepthExpectedFollowsTheProjection()
        {
            // Reversed z: 1 at the near plane, falling with 1/d towards 0 at the far plane; the plain case is its complement.
            double[] atNear = ReconMath.DepthExpected(0.21, 0.21, 750000, true);
            Assert.AreEqual(1.0, atNear[0], 1e-9);
            double[] atThree = ReconMath.DepthExpected(3.0, 0.21, 750000, true);
            Assert.AreEqual(0.21 * (750000 - 3.0) / (3.0 * (750000 - 0.21)), atThree[0], 1e-12);
            Assert.AreEqual(0.0699, atThree[0], 1e-4);
            Assert.AreEqual(1.0 - atThree[0], ReconMath.DepthExpected(3.0, 0.21, 750000, false)[0], 1e-12);
            Assert.AreEqual(3.0 / 750000, atThree[2], 1e-15);
            Assert.AreEqual(1.0, ReconMath.DepthExpected(750000, 0.21, 750000, false)[0], 1e-9);
        }

        [TestMethod]
        public void DepthAgreesAcceptsThePredictedValuesAndRejectsTheRun2Readings()
        {
            var depths = new List<double> { 2.5, 5, 10, 20, 40 };
            var predicted = new List<double>();
            foreach (double d in depths) predicted.Add(ReconMath.DepthExpected(d, 0.21, 750000, true)[0]);
            int best;
            double error;
            Assert.IsTrue(ReconMath.DepthAgrees(predicted, depths, 0.21, 750000, true, 0.1, 5e-4, 5e-4, out best, out error));
            Assert.AreEqual(0, best);
            Assert.IsTrue(error < 1e-9);
            // Within 10 %: still agrees; 30 % off: does not.
            var near = predicted.Select(v => v * 1.08).ToList();
            Assert.IsTrue(ReconMath.DepthAgrees(near, depths, 0.21, 750000, true, 0.1, 5e-4, 5e-4, out best, out error));
            var far = predicted.Select(v => v * 1.3).ToList();
            Assert.IsFalse(ReconMath.DepthAgrees(far, depths, 0.21, 750000, true, 0.1, 5e-4, 5e-4, out best, out error));
            // Run2's ball readings (0.3692 at 3 m, 0.3650 at 9 m) are ordered by 0.004 but match no encoding of the projection.
            Assert.IsFalse(ReconMath.DepthAgrees(new List<double> { 0.3692, 0.3650 }, new List<double> { 3, 9 }, 0.21, 750000, true, 0.1, 5e-4, 5e-4, out best, out error));
            Assert.IsTrue(error > 0.5);
            // Equal values are not an order; a missing value fails.
            Assert.IsFalse(ReconMath.DepthAgrees(new List<double> { 0.4, 0.4, 0.4 }, new List<double> { 2, 4, 8 }, 0.21, 750000, true, 0.1, 5e-4, 5e-4, out best, out error));
            Assert.IsFalse(ReconMath.DepthAgrees(new List<double> { 0.4, double.NaN }, new List<double> { 2, 4 }, 0.21, 750000, true, 0.1, 5e-4, 5e-4, out best, out error));
        }

        [TestMethod]
        public void MonotoneCheck()
        {
            Assert.IsTrue(ReconMath.IsMonotoneIncreasing(new List<double> { 0.1, 0.2, 0.3 }, 1e-6));
            Assert.IsFalse(ReconMath.IsMonotoneIncreasing(new List<double> { 0.1, 0.3, 0.2 }, 1e-6));
            Assert.IsFalse(ReconMath.IsMonotoneIncreasing(new List<double> { 0.1 }, 1e-6));
        }
    }
}
