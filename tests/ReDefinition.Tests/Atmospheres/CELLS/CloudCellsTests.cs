using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests.Atmospheres.CELLS
{
    // CELLS: the single clouds from the map's share (CloudCells, the C# twin of CloudCells.cginc). The discs cover the share the radius table promises, the lattice has no seam at the
    // longitude's wrap, and the empty reach never leaps into a disc.
    [TestClass]
    public class CloudCellsTests
    {
        private const double Rm = 600000.0 + 4550.0, Pitch = 3000.0;
        private const uint Seed = 12345u;

        // The share of random points of a patch at the equator (about 60 x 60 pitches) that lie inside a disc (its profile above 0.5, the threshold).
        private static double Covered(double f, int points, int seed)
        {
            var rng = new Random(seed);
            double du = 60.0 * Pitch / (2.0 * Math.PI * Rm), dv = 60.0 * Pitch / (Math.PI * Rm);
            int inside = 0;
            for (int i = 0; i < points; i++)
            {
                double u = 0.3 + rng.NextDouble() * du, v = 0.5 - 0.5 * dv + rng.NextDouble() * dv;
                double size, reach;
                if (CloudCells.Profile(u, v, Pitch, Rm, f, Seed, out size, out reach) > 0.5) inside++;
            }
            return inside / (double)points;
        }

        [TestMethod]
        public void TheDiscsCoverTheShareTheTablePromises()
        {
            // Up to a share of 0.45 no disc reaches the radius cap; above it the cap takes a little (the deck blend takes over at 0.8).
            foreach (double f in new[] { 0.05, 0.15, 0.25, 0.35, 0.45 })
            {
                double c = Covered(f, 40000, 7);
                Assert.AreEqual(f, c, 0.03, "share " + f + ": covered " + c);
            }
        }

        [TestMethod]
        public void NoShareNoCloud()
        {
            double size, reach;
            Assert.AreEqual(0.0, CloudCells.Profile(0.3, 0.5, Pitch, Rm, 0.0, Seed, out size, out reach));
            Assert.IsTrue(reach > 0.0);
        }

        [TestMethod]
        public void TheLatticeHasNoSeamAtTheLongitudesWrap()
        {
            double eps = 1.0 / (2.0 * Math.PI * Rm);   // a metre
            for (int k = 0; k < 200; k++)
            {
                double v = 0.45 + k * 0.0005;
                double s1, s2, r1, r2;
                double a = CloudCells.Profile(1.0 - eps, v, Pitch, Rm, 0.3, Seed, out s1, out r1);
                double b = CloudCells.Profile(eps, v, Pitch, Rm, 0.3, Seed, out s2, out r2);
                Assert.AreEqual(a, b, 0.05, "v " + v);
            }
        }

        [TestMethod]
        public void TheEmptyReachNeverLeapsIntoADisc()
        {
            var rng = new Random(3);
            int checkedPoints = 0;
            for (int i = 0; i < 4000; i++)
            {
                double u = 0.3 + rng.NextDouble() * 0.01, v = 0.49 + rng.NextDouble() * 0.02;
                double f = 0.05 + 0.6 * rng.NextDouble();
                double size, reach;
                if (CloudCells.Profile(u, v, Pitch, Rm, f, Seed, out size, out reach) > 0.0 || reach <= 0.0) continue;
                checkedPoints++;
                for (int k = 0; k < 8; k++)
                {
                    double a = rng.NextDouble() * 2.0 * Math.PI, d = rng.NextDouble() * reach;
                    double lat = (0.5 - v) * Math.PI;
                    double u2 = u + d * Math.Cos(a) / (2.0 * Math.PI * Rm * Math.Cos(lat)), v2 = v - d * Math.Sin(a) / (Math.PI * Rm);
                    double s2, r2;
                    Assert.AreEqual(0.0, CloudCells.Profile(u2, v2, Pitch, Rm, f, Seed, out s2, out r2), 1e-9, "a point " + d.ToString("0") + " m from an empty one within its reach " + reach.ToString("0") + " m");
                }
            }
            Assert.IsTrue(checkedPoints > 100, "empty points checked: " + checkedPoints);
        }

        [TestMethod]
        public void TheCoverIsTheMapAboveTheDeckShare()
        {
            double reach, wMap;
            // A map value whose share is 1 (a narrow noise swing, 0.1, keeps the whole ramp above the edge): the deck, the map's own value.
            Assert.AreEqual(1.0, CloudCells.Share(1.0, 0.35, 0.15, 0.1), 1e-9);
            double c = CloudCells.Cover(1.0, 0.3, 0.5, Pitch, Rm, 0.35, 0.15, 0.1, 0.0, Seed, out reach, out wMap);
            Assert.AreEqual(1.0, wMap, 1e-9);
            Assert.AreEqual(1.0, c, 1e-9);
        }

        private static CloudCells.CellRead Disc(double xBest)
        {
            CloudCells.CellRead read;
            read.XBest = xBest;
            read.RBest = 1000.0;
            read.Core = 0.5;
            read.WMap = 0.0;
            return read;
        }

        [TestMethod]
        public void TheBodyIsOutsideInsideAndHalfOnTheBareSurface()
        {
            // Well outside the disc (1.5 of its profile radius), at the base: nothing.
            Assert.AreEqual(0.0, CloudCells.CellBody(Disc(1.5), 0.0, 0.5, 0.0, 0.0, 1000.0, 0.5, 10.0, 0.0), 1e-12);
            // At the centre, on the base, the depth is the shorter semi-axis (725 m) over a band of 10 m: fully inside.
            Assert.AreEqual(1.0, CloudCells.CellBody(Disc(0.0), 0.0, 0.5, 0.0, 0.0, 1000.0, 0.5, 10.0, 0.0), 1e-12);
            // On the ellipsoid's surface (x at the visible radius, h 0) with the billow rank at its mean and no fine octaves: the depth is 0, the share half.
            Assert.AreEqual(0.5, CloudCells.CellBody(Disc(CloudCells.EdgeX), 0.0, 0.5, 0.0, 0.0, 1000.0, 0.5, 10.0, 0.0), 1e-12);
            // A rank above the mean moves the surface out: the same point is inside.
            Assert.IsTrue(CloudCells.CellBody(Disc(CloudCells.EdgeX), 0.0, 1.0, 0.0, 0.0, 1000.0, 0.5, 10.0, 0.0) > 0.5);
        }

        [TestMethod]
        public void TheProfilesNearestDiscIsReturnedWithItsDistance()
        {
            for (int k = 0; k < 200; k++)
            {
                double u = 0.3 + k * 0.00003, v = 0.5 + (k % 7) * 0.00002;
                double size, reach, x, r;
                double p = CloudCells.Profile(u, v, Pitch, Rm, 0.3, Seed, out size, out reach, out x, out r);
                double t = Math.Min(Math.Max((x - 0.45) / 0.55, 0.0), 1.0);
                Assert.AreEqual(1.0 - t * t * (3.0 - 2.0 * t), p, 1e-12, "k " + k);
                Assert.IsTrue(x >= 0.0 && r > 0.0 && r <= CloudCells.RMax * Pitch + 1e-9, "k " + k + " x " + x + " r " + r);
                double p0 = CloudCells.Profile(u, v, Pitch, Rm, 0.3, Seed, out size, out reach);
                Assert.AreEqual(p0, p, 0.0);
            }
        }
    }
}
