using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests.Atmospheres.CELLS
{
    // Master plan 46.4, the far view's middle stage: the field's spread inside a texel of each mip level (CloudField.MipSpread), from which FieldCloud takes the
    // share of a pixel's footprint inside the clouds.
    [TestClass]
    public class CloudFieldSpreadTests
    {
        private static float[] Field(int size, Func<int, int, float> value)
        {
            var f = new float[size * size * 4];
            for (int j = 0; j < size; j++)
                for (int i = 0; i < size; i++) f[(j * size + i) * 4] = value(i, j);
            return f;
        }

        [TestMethod]
        public void ACheckerboardIsWhollySpreadInsideTheFirstMipLevel()
        {
            // 0 and 1 alternate: variance 1/4; every 2 x 2 mean is 1/2, so the whole variance lies inside a level-1 texel: sqrt(3 / 4).
            float[] a = CloudField.MipSpread(Field(8, (i, j) => (i + j) % 2), 8, 0);
            Assert.AreEqual(CloudField.SpreadLevels, a.Length);
            Assert.AreEqual(0f, a[0], 1e-6f, "a texel of the finest level holds one value");
            for (int l = 1; l < a.Length; l++) Assert.AreEqual((float)Math.Sqrt(0.75), a[l], 1e-5f, "level " + l);
        }

        [TestMethod]
        public void AnEvenFieldHasNoSpreadAndBlocksKeepTheirsUntilTheirOwnLevel()
        {
            foreach (float s in CloudField.MipSpread(Field(8, (i, j) => 0.3f), 8, 0)) Assert.AreEqual(0f, s, 1e-6f);
            // Blocks of 4 x 4 texels of 0 and 1: nothing inside a texel of the levels 0 to 2, the whole variance from level 3 on.
            float[] a = CloudField.MipSpread(Field(8, (i, j) => (i / 4 + j / 4) % 2), 8, 0);
            Assert.AreEqual(0f, a[0], 1e-6f);
            Assert.AreEqual(0f, a[1], 1e-6f);
            Assert.AreEqual(0f, a[2], 1e-6f);
            Assert.AreEqual((float)Math.Sqrt(0.75), a[3], 1e-5f);
        }

        [TestMethod]
        public void TheRealFieldsSpreadRisesFromNothingToTheRankUniformHalf()
        {
            // The rank-uniform field has the variance 1 / 12: its coarsest levels spread over mean +- 0.5, and the share of a footprint inside {u < f} is then f itself.
            float[] field = CloudField.Build(256);
            float[] a = CloudField.MipSpread(field, 256, 0);
            Assert.AreEqual(0f, a[0], 1e-6f);
            for (int l = 1; l < a.Length; l++) Assert.IsTrue(a[l] >= a[l - 1] - 1e-6f, "the spread does not fall with the level (" + l + ")");
            Assert.AreEqual(0.5f, a[a.Length - 1], 0.01f);
        }
    }
}
