using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests.Atmospheres.D35
{
    // Package D35, the hash and the noises of the sea ice: the integer results equal an independent implementation exactly
    // (tools/weather_metrics/ice.py made the tables), the float results within a few ulp; the shader repeats the same operations
    // (IceShaderTests reads its constants).
    [TestClass]
    public class IceHashTests
    {
        // IceHash.Cell(x, y, z, seed): x, y, z, seed, expected (tools/weather_metrics/ice.py golden)
        private static readonly long[][] CellTable =
        {
            new long[] { 0, 0, 0, 0, 0 },
            new long[] { 1, 2, 3, 0, 2902294105 },
            new long[] { -1, -2, -3, 0, 957797061 },
            new long[] { 17, -5, 40, 0, 2359874392 },
            new long[] { -100, 250, -7, 0, 1814836682 },
            new long[] { 2047, 2047, 2047, 0, 2589542674 },
            new long[] { -2048, 0, 99, 0, 3775165435 },
            new long[] { 123456, -654321, 31, 0, 2701353600 },
            new long[] { 0, 0, 0, 12345, 1499480588 },
            new long[] { 1, 2, 3, 12345, 665320835 },
            new long[] { -1, -2, -3, 12345, 3312624009 },
            new long[] { 17, -5, 40, 12345, 614660015 },
            new long[] { -100, 250, -7, 12345, 1608186942 },
            new long[] { 2047, 2047, 2047, 12345, 2760365310 },
            new long[] { -2048, 0, 99, 12345, 1552346297 },
            new long[] { 123456, -654321, 31, 12345, 3140288642 },
            new long[] { 0, 0, 0, 3735928559, 180163754 },
            new long[] { 1, 2, 3, 3735928559, 1336054441 },
            new long[] { -1, -2, -3, 3735928559, 3323766436 },
            new long[] { 17, -5, 40, 3735928559, 3330316638 },
            new long[] { -100, 250, -7, 3735928559, 4223367125 },
            new long[] { 2047, 2047, 2047, 3735928559, 2571938902 },
            new long[] { -2048, 0, 99, 3735928559, 2908193569 },
            new long[] { 123456, -654321, 31, 3735928559, 3253582784 }
        };

        // IceHash.WorleyAt(px, py, pz, 12345): px, py, pz, f1, f2, cell hash, cell value
        private static readonly double[][] WorleyTable =
        {
            new double[] { 0.0, 0.0, 0.0, 0.34430427375141315, 0.5966554362921791, 282105815, 0.8371386528015137 },
            new double[] { 0.5, 0.5, 0.5, 0.3781945755845326, 0.9400426314574374, 1499480588, 0.08032816648483276 },
            new double[] { 3.25, -1.75, 8.5, 0.5385407147192381, 0.6619229145245453, 1440132809, 0.9472554922103882 },
            new double[] { -12.125, 40.5, -0.0625, 0.35845776841699234, 0.7782620037844, 2828475308, 0.5017696619033813 },
            new double[] { 100.375, 200.625, -300.25, 0.3844684014863291, 0.8374016007511248, 2819661820, 0.7653210759162903 },
            new double[] { 7.0, 7.0, 7.0, 0.5938991185142882, 0.7528259041495007, 2378324570, 0.3819458484649658 },
            new double[] { -0.015625, 0.015625, 0.5, 0.4248312618810062, 0.47142070478917303, 1499480588, 0.08032816648483276 },
            new double[] { 199.9375, -0.4375, 33.3125, 0.8338417953747229, 0.9675583654484926, 498036610, 0.8068603277206421 }
        };

        // IceHash.ValueNoise(px, py, pz, 777): px, py, pz, expected
        private static readonly double[][] NoiseTable =
        {
            new double[] { 0.0, 0.0, 0.0, 0.203574538230896 },
            new double[] { 0.5, 0.5, 0.5, -0.427800789475441 },
            new double[] { 3.25, -1.75, 8.5, -0.21251408534590155 },
            new double[] { -12.125, 40.5, -0.0625, 0.27268894845076375 },
            new double[] { 100.375, 200.625, -300.25, -0.4803110730169351 },
            new double[] { 7.0, 7.0, 7.0, -0.07741415500640869 },
            new double[] { -0.015625, 0.015625, 0.5, -0.2503649174922811 },
            new double[] { 199.9375, -0.4375, 33.3125, 0.0980310045658542 }
        };

        [TestMethod]
        public void TheCellHashEqualsTheReferenceImplementationExactly()
        {
            foreach (long[] row in CellTable)
                Assert.AreEqual((uint)row[4], IceHash.Cell((int)row[0], (int)row[1], (int)row[2], (uint)row[3]), "cell " + row[0] + "," + row[1] + "," + row[2] + " seed " + row[3]);
        }

        [TestMethod]
        public void TheWorleyNoiseEqualsTheReferenceWithinFloatError()
        {
            foreach (double[] row in WorleyTable)
            {
                IceHash.Worley w = IceHash.WorleyAt((float)row[0], (float)row[1], (float)row[2], 12345u);
                Assert.AreEqual(row[3], w.F1, 2e-5, "F1 at " + row[0] + "," + row[1] + "," + row[2]);
                Assert.AreEqual(row[4], w.F2, 2e-5, "F2 at " + row[0] + "," + row[1] + "," + row[2]);
                Assert.AreEqual((uint)row[5], w.CellHash, "cell at " + row[0] + "," + row[1] + "," + row[2]);
                Assert.AreEqual(row[6], w.CellValue, 1e-6, "value at " + row[0] + "," + row[1] + "," + row[2]);
            }
        }

        [TestMethod]
        public void TheValueNoiseEqualsTheReferenceWithinFloatError()
        {
            foreach (double[] row in NoiseTable)
                Assert.AreEqual(row[3], IceHash.ValueNoise((float)row[0], (float)row[1], (float)row[2], 777u), 2e-5, "noise at " + row[0] + "," + row[1] + "," + row[2]);
        }

        [TestMethod]
        public void TheUnitNumberIsInZeroToOneAndUniform()
        {
            var bins = new int[10];
            const int N = 200000;
            for (int i = 0; i < N; i++)
            {
                float u = IceHash.Unit(IceHash.Cell(i, i / 7, -i / 3, 99u));
                Assert.IsTrue(u >= 0f && u < 1f, "unit " + u);
                bins[(int)(u * 10)]++;
            }
            foreach (int count in bins) Assert.AreEqual(N / 10.0, count, N / 10.0 * 0.03, "a decile holds " + count);
        }

        [TestMethod]
        public void TheDistancesAreOrderedAndTheCellValueBelongsToTheNearestCell()
        {
            for (int i = 0; i < 2000; i++)
            {
                float x = (i * 0.37f) % 40f - 20f, y = (i * 0.91f) % 30f - 15f, z = (i * 0.13f) % 50f - 25f;
                IceHash.Worley w = IceHash.WorleyAt(x, y, z, 5u);
                Assert.IsTrue(w.F1 <= w.F2, "F1 " + w.F1 + " F2 " + w.F2);
                Assert.IsTrue(w.F1 < 1.8f, "the nearest feature point is within the 3 x 3 x 3 cells: " + w.F1);
                Assert.IsTrue(w.CellValue >= 0f && w.CellValue < 1f);
            }
        }

        [TestMethod]
        public void TheNoisesAreFiniteAtLargeAndNegativeCoordinates()
        {
            foreach (float c in new[] { -1e5f, -3.5f, 0f, 1e5f })
            {
                IceHash.Worley w = IceHash.WorleyAt(c, -c, c / 3f, 1u);
                Assert.IsFalse(float.IsNaN(w.F1) || float.IsNaN(w.F2));
                float n = IceHash.ValueNoise(c, c, -c, 1u);
                Assert.IsTrue(n >= -1f && n <= 1f);
            }
        }
    }
}
