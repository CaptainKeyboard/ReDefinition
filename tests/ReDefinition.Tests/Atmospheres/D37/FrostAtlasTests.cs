using System;
using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests.Atmospheres.D37
{
    // The procedural atlas of the camera frost: size, build time, tiling, channels, determinism.
    [TestClass]
    public class FrostAtlasTests
    {
        private static byte[] atlas;

        [ClassInitialize]
        public static void Build(TestContext context)
        {
            atlas = FrostAtlas.Build();
        }

        private static byte At(int x, int y, int channel)
        {
            return atlas[(((y + FrostAtlas.Size) % FrostAtlas.Size) * FrostAtlas.Size + ((x + FrostAtlas.Size) % FrostAtlas.Size)) * 4 + channel];
        }

        [TestMethod]
        public void SizeIsFourMegabytes()
        {
            Assert.AreEqual(1024 * 1024 * 4, atlas.Length);
            Assert.AreEqual(4 * 1024 * 1024, FrostAtlas.Bytes);
        }

        [TestMethod]
        public void BuildTakesUnderSixtyMilliseconds()
        {
            // The first call pays for the JIT; the brief's figure is for the build, so the best of five is held to the limit and the
            // first call to twice the limit.
            var first = Stopwatch.StartNew();
            FrostAtlas.Build(7);
            double firstMs = first.Elapsed.TotalMilliseconds;
            double best = double.MaxValue;
            for (int i = 0; i < 5; i++)
            {
                var watch = Stopwatch.StartNew();
                FrostAtlas.Build(100 + i);
                best = Math.Min(best, watch.Elapsed.TotalMilliseconds);
            }
            Console.WriteLine("D37 atlas build: first " + firstMs.ToString("0.0") + " ms, best of five " + best.ToString("0.0") + " ms");
            Assert.IsTrue(best < 60.0, "best build " + best + " ms");
            Assert.IsTrue(firstMs < 120.0, "first build " + firstMs + " ms");
        }

        [TestMethod]
        public void SameSeedSameImageOtherSeedOtherImage()
        {
            byte[] again = FrostAtlas.Build();
            CollectionAssert.AreEqual(atlas, again);
            byte[] other = FrostAtlas.Build(99);
            int differing = 0;
            for (int i = 0; i < atlas.Length; i += 4 * 257)
                if (atlas[i] != other[i]) differing++;
            Assert.IsTrue(differing > 100, "differing samples " + differing);
        }

        [TestMethod]
        public void FrostMaskAndReliefTile()
        {
            // The mean step across every line of the image (1024 columns and 1024 rows, the last one being the seam to the first): a seam
            // that is a real break would be the largest of them; a tiling one lies among the others, below their 99th percentile.
            foreach (int channel in new[] { 0, 2, 3 })
            {
                var columns = new double[FrostAtlas.Size];
                var rows = new double[FrostAtlas.Size];
                for (int line = 0; line < FrostAtlas.Size; line++)
                {
                    for (int i = 0; i < FrostAtlas.Size; i++)
                    {
                        columns[line] += Math.Abs(At(line, i, channel) - At(line + 1, i, channel));
                        rows[line] += Math.Abs(At(i, line, channel) - At(i, line + 1, channel));
                    }
                }
                double seamColumn = columns[FrostAtlas.Size - 1], seamRow = rows[FrostAtlas.Size - 1];
                Array.Sort(columns);
                Array.Sort(rows);
                double limitColumn = columns[(int)(FrostAtlas.Size * 0.99)], limitRow = rows[(int)(FrostAtlas.Size * 0.99)];
                Assert.IsTrue(seamColumn <= limitColumn && seamRow <= limitRow,
                    "channel " + channel + ": seam " + seamColumn + " / " + seamRow + " against the 99th percentile " + limitColumn + " / " + limitRow);
            }
        }

        [TestMethod]
        public void FrostMaskSpreadsOverTheRange()
        {
            double sum = 0, sum2 = 0;
            int n = 0;
            int low = 0, high = 0;
            for (int y = 0; y < FrostAtlas.Size; y += 4)
                for (int x = 0; x < FrostAtlas.Size; x += 4)
                {
                    double v = At(x, y, 0) / 255.0;
                    sum += v;
                    sum2 += v * v;
                    n++;
                    if (v < 0.25) low++;
                    if (v > 0.75) high++;
                }
            double mean = sum / n, std = Math.Sqrt(sum2 / n - mean * mean);
            Console.WriteLine("D37 mask mean " + mean.ToString("0.000") + " std " + std.ToString("0.000"));
            Assert.IsTrue(mean > 0.35 && mean < 0.65, "mean " + mean);
            Assert.IsTrue(std > 0.12, "std " + std);
            Assert.IsTrue(low > n / 20 && high > n / 20, "low " + low + " high " + high + " of " + n);
        }

        [TestMethod]
        public void SixteenFlakeCellsAreDiscsWithClearBorders()
        {
            int cell = FrostAtlas.Size / FrostAtlas.FlakeGrid;
            for (int k = 0; k < 16; k++)
            {
                int cx = (k % 4) * cell, cy = (k / 4) * cell;
                Assert.IsTrue(At(cx + cell / 2, cy + cell / 2, 1) > 230, "cell " + k + " centre " + At(cx + cell / 2, cy + cell / 2, 1));
                for (int i = 0; i < cell; i += 5)
                {
                    Assert.AreEqual(0, At(cx + i, cy, 1), "cell " + k + " top edge");
                    Assert.AreEqual(0, At(cx, cy + i, 1), "cell " + k + " left edge");
                }
                // The rim is soft: somewhere between centre and edge the value is partly covered.
                bool soft = false;
                for (int x = cell / 2; x < cell; x++)
                {
                    int v = At(cx + x, cy + cell / 2, 1);
                    if (v > 40 && v < 215) soft = true;
                }
                Assert.IsTrue(soft, "cell " + k + " has a soft rim");
            }
        }

        [TestMethod]
        public void ReliefNormalIsCentredAndVaries()
        {
            double sumX = 0, sumY = 0, mag = 0;
            int n = 0;
            for (int y = 0; y < FrostAtlas.Size; y += 8)
                for (int x = 0; x < FrostAtlas.Size; x += 8)
                {
                    double nx = At(x, y, 2) / 255.0 * 2 - 1, ny = At(x, y, 3) / 255.0 * 2 - 1;
                    sumX += nx;
                    sumY += ny;
                    mag += Math.Sqrt(nx * nx + ny * ny);
                    n++;
                }
            Assert.IsTrue(Math.Abs(sumX / n) < 0.05 && Math.Abs(sumY / n) < 0.05, "mean " + sumX / n + ", " + sumY / n);
            Assert.IsTrue(mag / n > 0.05 && mag / n < 0.6, "mean slope " + mag / n);
        }
    }
}
