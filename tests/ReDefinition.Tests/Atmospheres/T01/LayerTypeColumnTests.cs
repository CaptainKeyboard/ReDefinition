using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests.Atmospheres.T01
{
    // PRUEFPROTOKOLL 273, 307: the profile's column above each sample (LayerTypeTable.Column) is the integral of the profile to the type's top, in type heights; the shader
    // multiplies it by the layer's extinction and the type's height for the optical depth straight up through a plane-parallel deck.
    [TestClass]
    public class LayerTypeColumnTests
    {
        private const int N = LayerTypeTable.ProfileSamples;

        [TestMethod]
        public void AFullProfilesColumnIsTheHeightLeftToTheTop()
        {
            var profile = new float[N];
            for (int i = 0; i < N; i++) profile[i] = 1f;
            float[] col = LayerTypeTable.Column(profile, 1);
            for (int i = 0; i < N; i++)
                Assert.AreEqual(1.0 - (double)i / (N - 1), col[i], 1e-5, "sample " + i);
        }

        [TestMethod]
        public void ALinearProfileIntegratesExactly()
        {
            // profile h: the integral from h to 1 is (1 - h^2) / 2; the trapezoids are exact for a linear profile
            var profile = new float[2 * N];
            for (int i = 0; i < N; i++) profile[N + i] = (float)i / (N - 1);
            float[] col = LayerTypeTable.Column(profile, 2);
            for (int i = 0; i < N; i++)
            {
                double h = (double)i / (N - 1);
                Assert.AreEqual(0.0, col[i], 1e-7, "the first type is empty");
                Assert.AreEqual((1.0 - h * h) / 2.0, col[N + i], 1e-5, "sample " + i);
            }
        }

        [TestMethod]
        public void TheColumnFallsTowardsTheTopAndIsZeroThere()
        {
            var profile = new float[N];
            for (int i = 0; i < N; i++) profile[i] = i < N / 2 ? 0.2f : 0.9f;
            float[] col = LayerTypeTable.Column(profile, 1);
            Assert.AreEqual(0f, col[N - 1]);
            for (int i = 1; i < N; i++) Assert.IsTrue(col[i] <= col[i - 1], "never rises towards the top");
        }
    }
}
