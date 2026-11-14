using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests
{
    // The CPU parts of the edge-adaptive resolution (package T12): the block classification, the mask's dilation and persistence, the step band,
    // the cut detector of the driver and the switches' defaults. The probe-camera policy and the shaders are measured in the weather player.
    [TestClass]
    public sealed class EdgeAdaptiveTests
    {
        private static float[,] Disc(int w, int h, double cx, double cy, double r)
        {
            var a = new float[w, h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    a[x, y] = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) < r ? 1f : 0f;
            return a;
        }

        [TestMethod]
        public void ClassifyFlagsTheContourAndNotTheInteriorOrTheEmptySky()
        {
            float[,] alpha = Disc(21, 21, 10, 10, 6);
            bool[,] edge = EdgeMath.Classify(alpha, 0.06f);
            Assert.IsFalse(edge[10, 10], "the interior of a thick cloud is not an edge");
            Assert.IsFalse(edge[0, 0], "empty sky is not an edge");
            Assert.IsTrue(edge[16, 10], "a sample next to the contour is an edge");
            Assert.IsTrue(edge[4, 10], "the other side too");
            // Every block that touches the contour is flagged, whichever side of it.
            for (int y = 0; y < 21; y++)
                for (int x = 0; x < 21; x++)
                    if (EdgeMath.PlusRange(alpha, x, y) > 0.5f) Assert.IsTrue(edge[x, y], x + "," + y);
        }

        [TestMethod]
        public void ClassifyKeepsAFaintGradientBelowTheThresholdCoarse()
        {
            var alpha = new float[10, 10];
            for (int y = 0; y < 10; y++)
                for (int x = 0; x < 10; x++)
                    alpha[x, y] = 0.02f * x / 9f;
            bool[,] edge = EdgeMath.Classify(alpha, 0.06f);
            for (int y = 0; y < 10; y++)
                for (int x = 0; x < 10; x++)
                    Assert.IsFalse(edge[x, y]);
        }

        [TestMethod]
        public void DilationWidensByTheRadiusOnEverySideAndKeepsASinglePixelSquare()
        {
            var m = new bool[11, 11];
            m[5, 5] = true;
            bool[,] d = EdgeMath.Dilate(m, 2);
            for (int y = 0; y < 11; y++)
                for (int x = 0; x < 11; x++)
                    Assert.AreEqual(Math.Abs(x - 5) <= 2 && Math.Abs(y - 5) <= 2, d[x, y], x + "," + y);
            Assert.IsTrue(EdgeMath.Dilate(m, 0)[5, 5]);
            Assert.IsFalse(EdgeMath.Dilate(m, 0)[6, 5]);
        }

        [TestMethod]
        public void DilationGrowsWithTheMotionAndIsCapped()
        {
            Assert.AreEqual(0, EdgeMath.DilationFor(0, 0f));
            Assert.AreEqual(1, EdgeMath.DilationFor(0, 3.9f), "a few pixels of motion: one block");
            Assert.AreEqual(3, EdgeMath.DilationFor(1, 6f));
            Assert.AreEqual(EdgeSettings.MaxDilation, EdgeMath.DilationFor(2, 5000f));
            Assert.AreEqual(2, EdgeMath.DilationFor(2, -10f), "no negative motion");
        }

        [TestMethod]
        public void PersistenceHoldsABlockForTheConfiguredFrames()
        {
            float level = EdgeMath.Persist(0f, true, 4);
            Assert.AreEqual(1f, level);
            int frames = 0;
            while (level > 0f && frames < 20)
            {
                level = EdgeMath.Persist(level, false, 4);
                if (level > 0f) frames++;
            }
            Assert.AreEqual(3, frames, "detected at frame 0, flagged for three more frames, then released");
            Assert.AreEqual(1f, EdgeMath.Persist(0.3f, true, 4), "a new detection restarts the hold");
        }

        [TestMethod]
        public void TheBandMeetsTheSixPixelRequirement()
        {
            Assert.AreEqual(2, EdgeMath.DilationForBand(6), "N = 6 pixels of the edge study need two dilation blocks in the worst frame");
            Assert.IsTrue(EdgeMath.GuaranteedBandPixels(EdgeMath.DilationForBand(6)) >= 6);
            Assert.AreEqual(5, EdgeMath.GuaranteedBandPixels(1));
            Assert.AreEqual(0, EdgeMath.DilationForBand(1));
        }

        [TestMethod]
        public void RayShareIsBetweenTheCoarseAndTheUniformTwoByTwo()
        {
            Assert.AreEqual(1.0 / 16.0, EdgeMath.RayShare(0.0), 1e-12);
            Assert.AreEqual(1.0 / 16.0 + 1.0 / 4.0, EdgeMath.RayShare(1.0), 1e-12);
            Assert.IsTrue(EdgeMath.RayShare(0.2) < 1.0 / 4.0, "a fifth of the blocks at the edge pattern marches fewer rays than the uniform 2 x 2");
            Assert.AreEqual(1, EdgeMath.RaysPerBlock(false));
            Assert.AreEqual(4, EdgeMath.RaysPerBlock(true));
        }

        [TestMethod]
        public void TheSwitchesAreOffByDefault()
        {
            EdgeSettings d = EdgeSettings.From((key, def) => def);
            Assert.IsFalse(d.Enabled);
            Assert.IsFalse(EdgeSettings.ReconstructorOn((key, def) => def));
            EdgeSettings on = EdgeSettings.From((key, def) => key == EdgeSettings.TuneEdgeAdaptive ? 1f : (key == EdgeSettings.TuneDilation ? 99f : def));
            Assert.IsTrue(on.Enabled);
            Assert.AreEqual(EdgeSettings.MaxDilation, on.Dilation);
        }

        [TestMethod]
        public void TheHistoryIsResetOnEveryKindOfCut()
        {
            var cuts = new ReconCutDetector();
            Assert.IsNull(cuts.Observe(0, 0, 6e5, 1, 0, 0, 7), "the first frame has nothing to compare with");
            Assert.IsNull(cuts.Observe(0, 1, 6e5, 1, 0, 0, 7), "a metre of flight");
            Assert.AreEqual("teleport", cuts.Observe(0, 50000, 6e5, 1, 0, 0, 7));
            Assert.IsNull(cuts.Observe(0, 50001, 6e5, 1, 0, 0, 7), "the frame after a cut is steady again");
            Assert.AreEqual("scene change", cuts.Observe(0, 50001, 6e5, 2, 0, 0, 7));
            Assert.AreEqual("history reset request", cuts.Observe(0, 50001, 6e5, 2, 1, 0, 7));
            Assert.AreEqual("weather cut", cuts.Observe(0, 50001, 6e5, 2, 1, 1, 7));
            Assert.AreEqual("layer stack change", cuts.Observe(0, 50001, 6e5, 2, 1, 1, 8));
            cuts.Forget();
            Assert.IsNull(cuts.Observe(5e7, 0, 0, 9, 4, 4, 1), "after Forget the next frame starts afresh");
        }

        [TestMethod]
        public void AFastFlightIsNotACutButAJumpAtRestIs()
        {
            var cuts = new ReconCutDetector();
            cuts.Observe(0, 0, 6e5, 1, 0, 0, 1);
            double y = 0;
            for (int i = 0; i < 6; i++)
            {
                y += 1500.0;   // 1500 m a frame: an orbit's speed at 60 fps, below the floor of 2000 m
                Assert.IsNull(cuts.Observe(0, y, 6e5, 1, 0, 0, 1), "frame " + i);
            }
            y += 10000.0;   // a jump beyond four steps of the speed and the floor
            Assert.AreEqual("teleport", cuts.Observe(0, y, 6e5, 1, 0, 0, 1));
        }
    }
}
