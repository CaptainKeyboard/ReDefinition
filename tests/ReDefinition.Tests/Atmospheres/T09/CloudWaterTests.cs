using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests.Atmospheres.T09
{
    // E40 soft density: the soft water content (CloudWater) as one mapping. Expected results, written before the first run: the old share's expression with the
    // numbers of CloudWater.Coverage equals the water content at every noise rank and map value (to 1e-12); a column of the water content at the outline has the
    // optical depth 0.92 at the detail's mean; the content rises with the noise rank and is full only `Width` above the place it starts; a map value of 0 is empty.
    [TestClass]
    public class CloudWaterTests
    {
        private static double OldShare(double map, double n, double amount, double coverage, double edge)
        {
            double x = (map + (n - 0.5) * amount + coverage - 1.0) / edge + 0.5;
            return x <= 0.0 ? 0.0 : x >= 1.0 ? 1.0 : x;
        }

        [TestMethod]
        public void TheOldSharesExpressionWithTheMovedNumbersIsTheWaterContent()
        {
            CloudWaterNumbers k = CloudWaterNumbers.Default;
            foreach (double closed in new[] { 0.0, 0.26, 0.34 })
                for (int i = 0; i <= 20; i++)
                    for (int j = 0; j <= 20; j++)
                    {
                        double map = i / 20.0, n = j / 20.0;
                        // A map value of 0 is a clear sky whatever the noise.
                        Assert.AreEqual(0.0, CloudWater.Content(n, CloudWater.Threshold(0.0, 0.0, closed, k), closed, k), 1e-12);
                        if (map < closed * k.Width) continue;
                        // The cover meaning: the outline at the noise rank 1 - map value.
                        double water = CloudWater.Content(n, CloudWater.Threshold(map, 0.0, closed, k), closed, k);
                        Assert.AreEqual(water, OldShare(map, n, 1.0, CloudWater.Coverage(true, 0.35, 0.15, closed, k), k.Width), 1e-12);
                        // The old outline of a layer with the noise amount 1 (coverage 0.35, edge 0.15): the place its share began.
                        double start = 0.5 + (1.0 - map - 0.35 - 0.5 * 0.15);
                        Assert.AreEqual(CloudWater.Content(n, start, closed, k), OldShare(map, n, 1.0, CloudWater.Coverage(false, 0.35, 0.15, closed, k), k.Width), 1e-12);
                    }
        }

        [TestMethod]
        public void AColumnAtTheOutlineIsJustClosed()
        {
            CloudWaterNumbers k = CloudWaterNumbers.Default;
            Func<double, double> profile = h => h < 0.15 ? 0.9 * h / 0.15 : h < 0.6 ? 0.9 + 0.1 * (h - 0.15) / 0.45 : (1.0 - h) / 0.4;
            double mean = CloudWater.ColumnMean(profile, k), sigma = 0.05, thickness = 627.0, erosion = 0.3, detailMean = 0.55;
            double closed = CloudWater.Closed(sigma * thickness * mean, erosion, detailMean, k);
            Assert.IsTrue(closed > 0.0 && closed < 1.0);
            // The column: the content at the outline, eroded at the detail's mean, integrated over the height.
            double eroded = CloudWater.Erode(closed, detailMean, erosion), tau = 0.0;
            const int n = 2000;
            for (int i = 0; i < n; i++)
            {
                double h = (i + 0.5) / n;
                tau += CloudWater.Density(sigma, eroded, profile(h), h, k) * thickness / n;
            }
            Assert.AreEqual(CloudWater.ClosedTau, tau, 0.01);
        }

        [TestMethod]
        public void TheContentRisesWithTheNoiseRankAndAMapOfZeroIsEmpty()
        {
            CloudWaterNumbers k = CloudWaterNumbers.Default;
            double closed = 0.3, t = CloudWater.Threshold(0.5, 0.0, closed, k), last = -1.0;
            for (int j = 0; j <= 100; j++)
            {
                double u = CloudWater.Content(j / 100.0, t, closed, k);
                Assert.IsTrue(u >= last);
                last = u;
            }
            Assert.AreEqual(closed, CloudWater.Content(t, t, closed, k), 1e-12);
            Assert.AreEqual(0.0, CloudWater.Content(t - closed * k.Width, t, closed, k), 1e-12);
            Assert.AreEqual(1.0, CloudWater.Content(t + (1.0 - closed) * k.Width, t, closed, k), 1e-12);
            Assert.AreEqual(0.5, t, 1e-12);
            Assert.IsTrue(CloudWater.Empty(CloudWater.Threshold(0.0, 0.0, closed, k), closed, k));
            Assert.IsFalse(CloudWater.Empty(CloudWater.Threshold(0.05, 0.0, closed, k), closed, k));
            // A small map value: the water begins at the rank 1 - 2 cc.
            Assert.AreEqual(0.0, CloudWater.Content(1.0 - 2.0 * 0.05, CloudWater.Threshold(0.05, 0.0, closed, k), closed, k), 1e-12);
            // The rise by height: the gain at the base, 1 from the rise's share upward; full water content is not eroded.
            Assert.AreEqual(k.BaseGain, CloudWater.Rise(0.0, k), 1e-12);
            Assert.AreEqual(1.0, CloudWater.Rise(k.RiseShare, k), 1e-12);
            Assert.AreEqual(1.0, CloudWater.Erode(1.0, 0.0, 0.5), 1e-12);
        }
    }
}
