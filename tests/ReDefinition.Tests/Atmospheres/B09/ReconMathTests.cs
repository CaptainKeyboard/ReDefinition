using System;
using System.Collections.Generic;
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
        public void MonotoneCheck()
        {
            Assert.IsTrue(ReconMath.IsMonotoneIncreasing(new List<double> { 0.1, 0.2, 0.3 }, 1e-6));
            Assert.IsFalse(ReconMath.IsMonotoneIncreasing(new List<double> { 0.1, 0.3, 0.2 }, 1e-6));
            Assert.IsFalse(ReconMath.IsMonotoneIncreasing(new List<double> { 0.1 }, 1e-6));
        }
    }
}
