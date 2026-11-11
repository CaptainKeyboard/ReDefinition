using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Core;

namespace ReDefinition.Tests.Atmospheres.D37
{
    // The geometry of the rim frost and the design table of its coverage.
    [TestClass]
    public class FrostShapeTests
    {
        private const float Aspect = 16f / 9f;
        private static byte[] atlas;
        private static FrostTuning tuning;

        [ClassInitialize]
        public static void Build(TestContext context)
        {
            atlas = FrostAtlas.Build();
            tuning = FrostTuning.Defaults();
        }

        [TestMethod]
        public void GrowthIsZeroAtZeroAndCappedAtOne()
        {
            Assert.AreEqual(0f, FrostShape.Growth(0f, ref tuning), 1e-6);
            Assert.AreEqual(FrostShape.MaxGrowth, FrostShape.Growth(1f, ref tuning), 1e-6);
            FrostTuning wild = tuning;
            wild.MaxGrowth = 0.9f;
            Assert.IsTrue(FrostShape.Growth(1f, ref wild) <= FrostShape.MaxGrowth + 1e-6);
            float previous = -1f;
            for (float e = 0f; e <= 1f; e += 0.05f)
            {
                float g = FrostShape.Growth(e, ref tuning);
                Assert.IsTrue(g >= previous);
                previous = g;
            }
        }

        [TestMethod]
        public void RadiusIsZeroAtTheCentreAndOneInACorner()
        {
            Assert.AreEqual(0f, FrostShape.Radius(0.5f, 0.5f, Aspect), 1e-6);
            Assert.AreEqual(1f, FrostShape.Radius(0f, 0f, Aspect), 1e-5);
            Assert.AreEqual(1f, FrostShape.Radius(1f, 1f, 4f / 3f), 1e-5);
        }

        [TestMethod]
        public void NoFrostInTheCentreWhateverTheNumbers()
        {
            // Even at full growth and a mask that pushes the edge inwards, the clear disc holds.
            for (float r = 0f; r < tuning.Centre - 0.001f; r += 0.02f)
                Assert.AreEqual(0f, FrostShape.Alpha(r, FrostShape.MaxGrowth, 1f, ref tuning, 1f, 1f, 1f), 1e-6, "r " + r);
            // And heavy snow reaches no further in than the growth plus the raggedness.
            float reach = 1f - FrostShape.MaxGrowth - tuning.Jag * (0.4f + FrostShape.MaxGrowth);
            Assert.IsTrue(reach > tuning.Centre, "the ragged edge stays outside the clear disc");
        }

        [TestMethod]
        public void DesignTable()
        {
            // The reference the player is judged against: the share of the picture outside the radius 1 - growth, at 16:9.
            float[] exposures = { 0f, 0.4f, 1f };
            foreach (float e in exposures)
            {
                float g = FrostShape.Growth(e, ref tuning);
                float design = FrostShape.GeometricCoverage(g, Aspect);
                string line = "D37 design table: exposure " + e.ToString("0.0") + " growth " + g.ToString("0.000") + " design " + (design * 100f).ToString("0.0") + " %";
                foreach (float threshold in new[] { 0.05f, 0.1f, 0.15f, 0.3f })
                    line += "; alpha>=" + threshold.ToString("0.00") + ": " + (FrostShape.CoverageOf(atlas, e, 1f, Aspect, ref tuning, threshold, 320, 180) * 100f).ToString("0.0") + " %";
                Console.WriteLine(line);
            }
            Assert.AreEqual(0f, FrostShape.GeometricCoverage(0f, Aspect), 1e-6);
            // Frozen: the design table at 16:9 (design 4.3: reach 0 to 0.35 of the half-diagonal on the curve 0.35 * exposure^0.7).
            Assert.AreEqual(0.124f, FrostShape.GeometricCoverage(FrostShape.Growth(0.4f, ref tuning), Aspect), 0.001f);
            Assert.AreEqual(0.333f, FrostShape.GeometricCoverage(FrostShape.Growth(1f, ref tuning), Aspect), 0.001f);
            Assert.IsTrue(FrostShape.GeometricCoverage(FrostShape.Growth(0.4f, ref tuning), Aspect) < FrostShape.GeometricCoverage(FrostShape.Growth(1f, ref tuning), Aspect));
        }

        [TestMethod]
        public void CoverageFollowsTheDesignTable()
        {
            // The design table of design 4.3 (the share of the picture outside the radius 1 - growth: 0 %, 12.4 %, 33.3 % at 16:9 and
            // exposure 0 / 0.4 / 1) against the arithmetic of the shader on the atlas, the share of the picture whose frost opacity is
            // at least 0.15 (what the player's contribution mask counts), within 10 %. The edge bias of the tuning sets the ragged edge
            // so that this holds at 16:9 and 21:9; at 4:3 and 16:10 the corners dominate and the allowance is 15 %.
            Assert.IsTrue(FrostShape.CoverageOf(atlas, 0f, 1f, Aspect, ref tuning, 0.15f, 320, 180) < 0.002f, "exposure 0");
            foreach (float aspect in new[] { 16f / 9f, 21f / 9f, 4f / 3f, 16f / 10f })
            {
                float allowance = aspect > 1.7f ? 0.10f : 0.15f;
                foreach (float e in new[] { 0.4f, 1f })
                {
                    float geometric = FrostShape.GeometricCoverage(FrostShape.Growth(e, ref tuning), aspect);
                    float mirror = FrostShape.CoverageOf(atlas, e, 1f, aspect, ref tuning, 0.15f, 320, 180);
                    Assert.IsTrue(Math.Abs(mirror - geometric) <= allowance * geometric, "aspect " + aspect + " exposure " + e + ": geometric " + geometric + " mirror " + mirror);
                }
            }
        }

        [TestMethod]
        public void CentreStaysClearAtFullExposureOnTheAtlas()
        {
            for (int j = 0; j < 90; j++)
                for (int i = 0; i < 160; i++)
                {
                    float u = (i + 0.5f) / 160f, v = (j + 0.5f) / 90f;
                    float r = FrostShape.Radius(u, v, Aspect);
                    if (r >= tuning.Centre) continue;
                    float au, av, m, g, b, a;
                    FrostShape.AtlasUv(u, v, Aspect, tuning.AtlasScale, out au, out av);
                    FrostAtlas.Sample(atlas, au, av, out m, out g, out b, out a);
                    Assert.AreEqual(0f, FrostShape.Alpha(r, FrostShape.MaxGrowth, 1f, ref tuning, m, b, a), 1e-6);
                }
        }

        [TestMethod]
        public void FrostGrowsFromTheEdgesInward()
        {
            // At a middle exposure the frost is at the corners and not at the middle of the picture; more exposure covers more.
            float low = FrostShape.CoverageOf(atlas, 0.2f, 1f, Aspect, ref tuning, 0.15f, 160, 90);
            float mid = FrostShape.CoverageOf(atlas, 0.5f, 1f, Aspect, ref tuning, 0.15f, 160, 90);
            float high = FrostShape.CoverageOf(atlas, 1f, 1f, Aspect, ref tuning, 0.15f, 160, 90);
            Assert.IsTrue(low < mid && mid < high, low + " " + mid + " " + high);
            Assert.IsTrue(high < 0.6f, "heavy frost leaves the greater part of the view: " + high);
            float centre = FrostShape.Alpha(0.05f, FrostShape.Growth(0.5f, ref tuning), 0.5f, ref tuning, 1f, 1f, 1f);
            float corner = FrostShape.Alpha(1f, FrostShape.Growth(0.5f, ref tuning), 0.5f, ref tuning, 0.5f, 0.5f, 0.5f);
            Assert.AreEqual(0f, centre, 1e-6);
            Assert.IsTrue(corner > 0.2f, "corner " + corner);
        }

        [TestMethod]
        public void TuningIsClampedIntoRange()
        {
            FrostTuning bad = tuning;
            bad.MaxGrowth = 5f;
            bad.FlakeMax = 999;
            bad.DropMax = -4;
            bad.BuildS = 0f;
            bad.Centre = 0.9f;
            FrostTuning ok = bad.Clamped();
            Assert.IsTrue(ok.MaxGrowth <= FrostShape.MaxGrowth);
            Assert.AreEqual(CameraFrost.MaxFlakes, ok.FlakeMax);
            Assert.AreEqual(0, ok.DropMax);
            Assert.IsTrue(ok.BuildS >= 1f);
            Assert.IsTrue(ok.Centre <= 0.6f);
        }
    }
}
