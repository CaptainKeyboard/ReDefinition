using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests.Atmospheres
{
    // The scene depth's distance from the projection the camera renders with (PRUEFPROTOKOLL 374, 375): read back with the camera's near and far plane properties
    // every far depth was kilometres too near and the march ended before the clouds over the far sea.
    [TestClass]
    public class SceneDepthPlanTests
    {
        private const float Near = 0.21f, Far = 750000f;

        // A perspective's third row in OpenGL's convention, in floats as a matrix holds them.
        private static void Row(float near, float far, out float m22, out float m23)
        {
            m22 = -(far + near) / (far - near);
            m23 = -2f * far * near / (far - near);
        }

        [TestMethod]
        public void ADepthTheCardWroteReadsBackAsItsDistance()
        {
            float m22, m23, a, b;
            Row(Near, Far, out m22, out m23);
            SceneDepthPlan.Terms(m22, m23, out a, out b);
            foreach (bool reversed in new[] { true, false })
                foreach (double e in new[] { 1.0, 150.0, 5000.0, 50000.0, 135000.0, 200000.0, 370000.0 })
                {
                    double raw = SceneDepthPlan.RawDepth(e, a, b, reversed);
                    Assert.AreEqual(e, SceneDepthPlan.EyeDepth(raw, a, b, reversed), e * 1e-9, "distance " + e + (reversed ? ", reversed" : ", forward"));
                }
        }

        [TestMethod]
        public void TheCamerasPlanesReadAFarDepthKilometresTooNear()
        {
            // KSP's flight camera: 0.21 m and 750 km. The matrix of those planes, in floats, is the matrix of another far plane; LinearEyeDepth reads with the planes.
            float m22, m23, a, b;
            Row(Near, Far, out m22, out m23);
            SceneDepthPlan.Terms(m22, m23, out a, out b);
            double far = SceneDepthPlan.ImpliedFar(a, b);
            Assert.IsTrue(Math.Abs(far - Far) > 40000.0, "the float matrix stands for a far plane of " + far.ToString("0") + " m, not the camera's 750000");
            double shift = 1.0 / Far - 1.0 / far;   // the constant the planes' reading adds to the inverse distance
            foreach (double e in new[] { 50000.0, 135000.0, 200000.0 })
            {
                double raw = SceneDepthPlan.RawDepth(e, a, b, true);
                double planes = SceneDepthPlan.PropertiesEyeDepth(raw, Near, Far);
                double expected = 1.0 / (1.0 / e + shift);
                Assert.AreEqual(expected, planes, e * 2e-3, "the planes' reading at " + e + " m");
                Assert.AreEqual(e, SceneDepthPlan.EyeDepth(raw, a, b, true), 1.0, "the projection's reading at " + e + " m");
            }
            // What the game's log showed at 130 km (PRUEFPROTOKOLL 374): the sea 135 km away read about 4 % too near.
            double rawSea = SceneDepthPlan.RawDepth(135000.0, a, b, true);
            double read = SceneDepthPlan.PropertiesEyeDepth(rawSea, Near, Far);
            Assert.IsTrue(Math.Abs(135000.0 - read) > 1500.0, "at 135 km the planes read " + read.ToString("0") + " m");
        }

        [TestMethod]
        public void TheNearSceneIsReadTheSameByBoth()
        {
            // Within a few kilometres (every accepted near view's ground and vessel) the two readings agree to centimetres: the far plane's term does not matter there.
            float m22, m23, a, b;
            Row(Near, Far, out m22, out m23);
            SceneDepthPlan.Terms(m22, m23, out a, out b);
            foreach (double e in new[] { 2.0, 150.0, 1000.0 })
            {
                double raw = SceneDepthPlan.RawDepth(e, a, b, true);
                Assert.AreEqual(SceneDepthPlan.PropertiesEyeDepth(raw, Near, Far), SceneDepthPlan.EyeDepth(raw, a, b, true), Math.Max(e * e * 3e-7, 1e-4), "distance " + e);
            }
        }

        [TestMethod]
        public void OnlyAPerspectiveWithADepthRangeAFloatHoldsIsRead()
        {
            float m22, m23, a, b;
            Row(Near, Far, out m22, out m23);
            SceneDepthPlan.Terms(m22, m23, out a, out b);
            Assert.IsTrue(SceneDepthPlan.Usable(-1f, 0f, a, b), "KSP's flight camera");
            Assert.IsFalse(SceneDepthPlan.Usable(0f, 1f, a, b), "an orthographic projection's fourth row");
            // A far plane so far that m22 rounds to -1: nothing of it is left in the matrix (b = 0), the shader keeps LinearEyeDepth.
            SceneDepthPlan.Terms(-1f, -0.42f, out a, out b);
            Assert.IsFalse(SceneDepthPlan.Usable(-1f, 0f, a, b));
        }

        [TestMethod]
        public void TheDepthAtAPlaceIsTheMiddleOfItsNeighbourhoodAgainstTheSeasSphere()
        {
            // A 5 x 5 depth of 1000 m with one vessel pixel at the place itself: the middle of the 3 x 3 is the sea's.
            var data = new float[25];
            for (int i = 0; i < 25; i++) data[i] = 1000f;
            data[2 * 5 + 2] = 150f;
            string text = OwnCloudModule.DepthPointsText(data, 5, 5, new[] { 0.5f, 0.5f }, new[] { 1040.0 });
            StringAssert.Contains(text, "at (0.50, 0.50): 1000 m, the sea's sphere 1040 m (0.9615)");
            Assert.AreEqual("", OwnCloudModule.DepthPointsText(null, 5, 5, new[] { 0.5f, 0.5f }, new[] { 1.0 }));
        }

        [TestMethod]
        public void ADepthAtTheFarPlaneIsNotANumberBelowZero()
        {
            float a, b;
            SceneDepthPlan.Terms(-1.0000005f, -0.42f, out a, out b);
            Assert.IsTrue(a > 0f && b > 0f);
            Assert.IsTrue(SceneDepthPlan.EyeDepth(0.0, a, b, true) > 0.0);
            Assert.IsTrue(double.IsPositiveInfinity(SceneDepthPlan.EyeDepth(-b, a, b, true)));
        }
    }
}
