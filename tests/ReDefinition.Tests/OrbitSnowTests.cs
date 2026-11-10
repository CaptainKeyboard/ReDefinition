using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Seasons;

namespace ReDefinition.Tests
{
    // The pure parts of the orbit and map snow: the texel convention, the mask that has to equal the ground's, the heights, the schedule.
    [TestClass]
    public class OrbitSnowTests
    {
        private const double Winter = 0.06, Summer = 0.56;
        private const double Line = 42;

        // PQS.BuildVertexMapCoords written out: latitude from y, longitude from the x and z components, u and v from them.
        private static void PqsCoordinates(double x, double y, double z, out double u, out double v)
        {
            double latitude = Math.Asin(y);
            double xz = Math.Sqrt(x * x + z * z);
            double longitude = z >= 0 ? Math.Asin(x / xz) : Math.PI - Math.Asin(x / xz);
            v = latitude / Math.PI + 0.5;
            u = longitude / Math.PI * 0.5;
        }

        [TestMethod]
        public void ATexelHasTheDirectionThePqsMapsToThatTexel()
        {
            for (double v = 0.05; v < 1; v += 0.1)
            {
                for (double u = 0.02; u < 1; u += 0.07)
                {
                    double x, y, z;
                    OrbitMask.Direction(u, v, out x, out y, out z);
                    Assert.AreEqual(1.0, x * x + y * y + z * z, 1e-9);
                    double pu, pv;
                    PqsCoordinates(x, y, z, out pu, out pv);
                    Assert.AreEqual(v, pv, 1e-9);
                    // The PQS u lies in -0.25 to 0.75 and the texture wraps.
                    double diff = ((pu - u) % 1.0 + 1.0) % 1.0;
                    if (diff > 0.5) diff = 1.0 - diff;
                    Assert.AreEqual(0.0, diff, 1e-9, "u " + u + " v " + v + " gave " + pu);
                }
            }
        }

        [TestMethod]
        public void TheRowsRunFromTheSouthPoleToTheNorthPole()
        {
            Assert.AreEqual(-90.0, OrbitMask.LatitudeDegrees(0), 1e-9);
            Assert.AreEqual(0.0, OrbitMask.LatitudeDegrees(0.5), 1e-9);
            Assert.AreEqual(90.0, OrbitMask.LatitudeDegrees(1), 1e-9);
            double x, y, z;
            OrbitMask.Direction(0.3, 0.75, out x, out y, out z);
            Assert.AreEqual(Math.Sin(45 * Math.PI / 180), y, 1e-9, "v 0.75 is 45 degrees north");
        }

        private static byte[] Mask(double phase, float strength, float[] heights, int hw, int hh, int w, int h, out sbyte[] noise)
        {
            noise = OrbitMask.BuildNoise(w, h);
            var rgba = new byte[w * h * 4];
            OrbitMask.Fill(rgba, w, h, noise, heights, hw, hh, phase, strength, Line);
            return rgba;
        }

        [TestMethod]
        public void TheMaskIsTheGroundFunctionAtEveryTexel()
        {
            const int w = 128, h = 64;
            sbyte[] noise;
            byte[] rgba = Mask(Winter, 1f, null, 0, 0, w, h, out noise);
            int checkedTexels = 0;
            for (int row = 0; row < h; row += 3)
            {
                double v = (row + 0.5) / h;
                for (int column = 0; column < w; column += 5)
                {
                    double u = (column + 0.5) / w;
                    double x, y, z;
                    OrbitMask.Direction(u, v, out x, out y, out z);
                    // The ground reads the noise of the PQS direction, unquantised; the table keeps 1/127 of it.
                    double ground = SnowMask.Coverage(OrbitMask.LatitudeDegrees(v), 0, Winter, SnowNoise.Value(x, y, z), Line);
                    int expected = (int)Math.Round(SnowPaint.Amount(ground, 1f) * 255f);
                    int alpha = rgba[(row * w + column) * 4 + 3];
                    Assert.IsTrue(Math.Abs(alpha - expected) <= 3, "row " + row + " column " + column + ": " + alpha + " against " + expected);
                    checkedTexels++;
                }
            }
            Assert.IsTrue(checkedTexels > 300);
            // The colour is the ground's snow colour.
            Assert.AreEqual((byte)Math.Round(SnowPaint.SnowRed * 255f), rgba[0]);
            Assert.AreEqual((byte)Math.Round(SnowPaint.SnowGreen * 255f), rgba[1]);
            Assert.AreEqual((byte)Math.Round(SnowPaint.SnowBlue * 255f), rgba[2]);
        }

        private static double MeanAlpha(byte[] rgba, int w, int h, double fromLat, double toLat)
        {
            double sum = 0;
            int n = 0;
            for (int row = 0; row < h; row++)
            {
                double lat = OrbitMask.LatitudeDegrees((row + 0.5) / h);
                if (lat < fromLat || lat > toLat) continue;
                for (int column = 0; column < w; column++)
                {
                    sum += rgba[(row * w + column) * 4 + 3];
                    n++;
                }
            }
            return sum / Math.Max(1, n) / 255.0;
        }

        [TestMethod]
        public void TheNorthIsSnowyInItsWinterAndBareInItsSummerAndTheSouthIsTheOtherWayRound()
        {
            const int w = 128, h = 64;
            sbyte[] noise;
            byte[] winter = Mask(Winter, 1f, null, 0, 0, w, h, out noise);
            byte[] summer = Mask(Summer, 1f, null, 0, 0, w, h, out noise);
            Assert.IsTrue(MeanAlpha(winter, w, h, 45, 70) > 0.8, "north band in its winter " + MeanAlpha(winter, w, h, 45, 70));
            Assert.IsTrue(MeanAlpha(summer, w, h, 45, 70) < 0.1, "north band in its summer " + MeanAlpha(summer, w, h, 45, 70));
            Assert.IsTrue(MeanAlpha(winter, w, h, -70, -45) < 0.1, "south band while the north has winter");
            Assert.IsTrue(MeanAlpha(summer, w, h, -70, -45) > 0.8, "south band while the north has summer");
            Assert.AreEqual(0.0, MeanAlpha(winter, w, h, -20, 20), 1e-9, "no snow at the equator");
            Assert.AreEqual(0.0, MeanAlpha(summer, w, h, -20, 20), 1e-9);
            Assert.IsTrue(MeanAlpha(summer, w, h, 84, 90) > 0.99, "the polar caps stay");
            Assert.IsTrue(MeanAlpha(summer, w, h, -90, -84) > 0.99);
        }

        [TestMethod]
        public void TheStrengthScalesTheMaskAndHeightLowersTheLine()
        {
            const int w = 128, h = 64;
            sbyte[] noise;
            byte[] full = Mask(Winter, 1f, null, 0, 0, w, h, out noise);
            byte[] half = Mask(Winter, 0.5f, null, 0, 0, w, h, out noise);
            Assert.AreEqual(MeanAlpha(full, w, h, 40, 80) * 0.5, MeanAlpha(half, w, h, 40, 80), 0.01);

            // A uniform grid of 4 km lowers the line by 6 degrees: the band between 33 and 39 degrees gets snow only with the heights.
            var heights = new float[16 * 8];
            for (int i = 0; i < heights.Length; i++) heights[i] = 4000f;
            byte[] flat = Mask(Summer, 1f, null, 0, 0, w, h, out noise);
            byte[] mountains = Mask(Summer, 1f, heights, 16, 8, w, h, out noise);
            Assert.IsTrue(MeanAlpha(mountains, w, h, 55, 78) > MeanAlpha(flat, w, h, 55, 78) + 0.2);
            Assert.AreEqual(MeanAlpha(flat, w, h, -20, 20), MeanAlpha(mountains, w, h, -20, 20), 0.2 + 1e-9);
        }

        [TestMethod]
        public void TheHeightGridIsBilinearWrapsInLongitudeAndIgnoresBadCells()
        {
            var grid = new float[4 * 2];
            for (int i = 0; i < grid.Length; i++) grid[i] = i * 100f;
            // At a cell centre the value is the cell's.
            Assert.AreEqual(0f, OrbitMask.Height(grid, 4, 2, 0.125, 0.25), 1e-3);
            Assert.AreEqual(200f, OrbitMask.Height(grid, 4, 2, 0.625, 0.25), 1e-3);
            Assert.AreEqual(500f, OrbitMask.Height(grid, 4, 2, 0.375, 0.75), 1e-3);
            // Halfway between two cells of a row.
            Assert.AreEqual(50f, OrbitMask.Height(grid, 4, 2, 0.25, 0.25), 1e-3);
            // Across the seam between the last and the first column: 300 and 0.
            Assert.AreEqual(150f, OrbitMask.Height(grid, 4, 2, 0.0, 0.25), 1e-3);
            // The poles do not wrap in latitude.
            Assert.AreEqual(0f, OrbitMask.Height(grid, 4, 2, 0.125, 0.0), 1e-3);
            Assert.AreEqual(700f, OrbitMask.Height(grid, 4, 2, 0.875, 1.0), 1e-3);
            grid[1] = float.NaN;
            Assert.IsFalse(float.IsNaN(OrbitMask.Height(grid, 4, 2, 0.2, 0.25)));
            Assert.AreEqual(0f, OrbitMask.Height(null, 4, 2, 0.5, 0.5));
            Assert.AreEqual(0f, OrbitMask.Height(new float[3], 4, 2, 0.5, 0.5), "a grid that is too small counts as none");
        }

        [TestMethod]
        public void TheNoiseTableIsDeterministicInRangeAndTheOnesTheGroundUses()
        {
            sbyte[] a = OrbitMask.BuildNoise(64, 32);
            sbyte[] b = OrbitMask.BuildNoise(64, 32);
            CollectionAssert.AreEqual(a, b);
            int min = 127, max = -127;
            foreach (sbyte v in a)
            {
                min = Math.Min(min, v);
                max = Math.Max(max, v);
            }
            Assert.IsTrue(max - min > 40, "the table varies, " + min + " to " + max);
            double x, y, z;
            OrbitMask.Direction((10 + 0.5) / 64, (20 + 0.5) / 32, out x, out y, out z);
            Assert.AreEqual(Math.Round(SnowNoise.Value(x, y, z) * 127.0), a[20 * 64 + 10]);
        }

        [TestMethod]
        public void TheCompositionRunsOnTheScheduleOnly()
        {
            Assert.IsTrue(OrbitMask.Due(double.NaN, 0.3, 0, false), "never composed");
            Assert.IsFalse(OrbitMask.Due(0.3, 0.305, 1000, false), "a small move is waited out");
            Assert.IsFalse(OrbitMask.Due(0.3, 0.311, 10, false), "a step of 0.01 waits 30 s");
            Assert.IsTrue(OrbitMask.Due(0.3, 0.311, 31, false));
            Assert.IsFalse(OrbitMask.Due(0.3, 0.36, 1, false), "a large step waits 2 s");
            Assert.IsTrue(OrbitMask.Due(0.3, 0.36, 2.5, false));
            Assert.IsFalse(OrbitMask.Due(0.3, 0.3, 1, true));
            Assert.IsTrue(OrbitMask.Due(0.3, 0.3, 2.5, true), "changed settings or heights");
            // The year wraps.
            Assert.IsTrue(OrbitMask.Due(0.99, 0.01, 31, false));
            Assert.IsFalse(OrbitMask.Due(0.99, 0.995, 100, false));
        }

        [TestMethod]
        public void TheWorkingMapKeepsTheAspectAndAFloorAndItsMemoryIsKnown()
        {
            int w, h;
            OrbitMask.WorkingSize(4096, 2048, 2048, out w, out h);
            Assert.AreEqual(2048, w);
            Assert.AreEqual(1024, h);
            OrbitMask.WorkingSize(1024, 512, 4096, out w, out h);
            Assert.AreEqual(1024, w, "never larger than the original");
            Assert.AreEqual(512, h);
            OrbitMask.WorkingSize(0, 0, 4096, out w, out h);
            Assert.IsTrue(w >= 64 && h >= 32);
            Assert.AreEqual(2048L * 1024 * 4 * 7 / 3, OrbitMask.Bytes(2048, 1024), 1);
            Assert.IsTrue(OrbitMask.Bytes(4096, 2048) < 130L * 1024 * 1024);
        }
    }
}
