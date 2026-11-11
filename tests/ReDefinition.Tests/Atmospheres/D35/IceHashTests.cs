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
            new long[] { 0, 0, 0, 0, 2145236065 },
            new long[] { 1, 2, 3, 0, 3222142406 },
            new long[] { -1, -2, -3, 0, 3842970820 },
            new long[] { 17, -5, 40, 0, 2994501534 },
            new long[] { -100, 250, -7, 0, 2312358844 },
            new long[] { 2047, 2047, 2047, 0, 3641195982 },
            new long[] { -2048, 0, 99, 0, 4152974197 },
            new long[] { 123456, -654321, 31, 0, 1509752212 },
            new long[] { 0, 0, 0, 12345, 349591518 },
            new long[] { 1, 2, 3, 12345, 389716341 },
            new long[] { -1, -2, -3, 12345, 3351092849 },
            new long[] { 17, -5, 40, 12345, 3301146767 },
            new long[] { -100, 250, -7, 12345, 2627509635 },
            new long[] { 2047, 2047, 2047, 12345, 3579935578 },
            new long[] { -2048, 0, 99, 12345, 2372640566 },
            new long[] { 123456, -654321, 31, 12345, 2115696555 },
            new long[] { 0, 0, 0, 3735928559, 2924065781 },
            new long[] { 1, 2, 3, 3735928559, 3805400856 },
            new long[] { -1, -2, -3, 3735928559, 4144465669 },
            new long[] { 17, -5, 40, 3735928559, 1004458572 },
            new long[] { -100, 250, -7, 3735928559, 2681028392 },
            new long[] { 2047, 2047, 2047, 3735928559, 4127004540 },
            new long[] { -2048, 0, 99, 3735928559, 4027393337 },
            new long[] { 123456, -654321, 31, 3735928559, 1327889693 }
        };

        // IceHash.WorleyAt(px, py, pz, 12345): px, py, pz, f1, f2, cell hash, cell value
        private static readonly double[][] WorleyTable =
        {
            new double[] { 0.0, 0.0, 0.0, 0.42715366839124186, 0.6431047211163119, 349591518, 0.20334064960479736 },
            new double[] { 0.5, 0.5, 0.5, 0.5248379283112735, 0.7375567063010722, 349591518, 0.20334064960479736 },
            new double[] { 3.25, -1.75, 8.5, 0.3877824620060728, 0.6551576471511218, 811147944, 0.8281295895576477 },
            new double[] { -12.125, 40.5, -0.0625, 0.596422211085146, 0.7454804595141103, 1913227588, 0.952855110168457 },
            new double[] { 100.375, 200.625, -300.25, 0.5939006000221551, 0.793108302478986, 4180539211, 0.22052878141403198 },
            new double[] { 7.0, 7.0, 7.0, 0.4990295905437896, 0.5053571020978446, 868108958, 0.42794328927993774 },
            new double[] { -0.015625, 0.015625, 0.5, 0.5578482679645653, 0.5911055399990749, 349591518, 0.20334064960479736 },
            new double[] { 199.9375, -0.4375, 33.3125, 0.7341682303709534, 0.7813455222218681, 2364568201, 0.0521278977394104 }
        };

        // IceHash.ValueNoise(px, py, pz, 777): px, py, pz, expected
        private static readonly double[][] NoiseTable =
        {
            new double[] { 0.0, 0.0, 0.0, -0.8077657222747803 },
            new double[] { 0.5, 0.5, 0.5, 0.042400062084198 },
            new double[] { 3.25, -1.75, 8.5, 0.29493744572391734 },
            new double[] { -12.125, 40.5, -0.0625, 0.29718003960829265 },
            new double[] { 100.375, 200.625, -300.25, -0.17427855887001442 },
            new double[] { 7.0, 7.0, 7.0, -0.5203770399093628 },
            new double[] { -0.015625, 0.015625, 0.5, -0.32651341424631647 },
            new double[] { 199.9375, -0.4375, 33.3125, -0.013991258887382702 }
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
