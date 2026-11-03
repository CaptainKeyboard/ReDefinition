using System.Globalization;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Upscaler;
using UnityEngine;

namespace ReDefinition.Tests
{
    // The text and statistics of the rig's diagnostics that need no rig (RigReport): the clip range recovered from
    // a projection matrix, distances from reversed depth, and what the pixels read back from the images say.
    [TestClass]
    public class RigReportTests
    {
        private CultureInfo culture;

        // The report formats numbers with the current culture; the expected texts are the invariant ones.
        [TestInitialize]
        public void UseInvariantCulture()
        {
            culture = Thread.CurrentThread.CurrentCulture;
            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
        }

        [TestCleanup]
        public void RestoreCulture()
        {
            Thread.CurrentThread.CurrentCulture = culture;
        }

        // A perspective projection with near plane 1 and far plane 5 has m22 = -(5+1)/(5-1) and m23 = -2*5*1/(5-1).
        [TestMethod]
        public void ClipRangeRecoversNearAndFarFromAProjection()
        {
            Matrix4x4 projection = new Matrix4x4();
            projection.m22 = -1.5f;
            projection.m23 = -2.5f;

            Assert.AreEqual("n=1 f=5", RigReport.ClipRange(projection));
        }

        // Where m22 is 1 the near recovery divides by zero, and no range is given.
        [TestMethod]
        public void ClipRangeOfADegenerateProjectionIsNotDeterminable()
        {
            Matrix4x4 projection = new Matrix4x4();
            projection.m22 = 1f;
            projection.m23 = -1f;

            Assert.AreEqual("n/f not determinable", RigReport.ClipRange(projection));
        }

        // A far plane so far that m22 + 1 nearly cancels is flagged as unreliable.
        [TestMethod]
        public void ClipRangeFlagsAFarPlaneThatIsNoise()
        {
            Matrix4x4 projection = new Matrix4x4();
            projection.m22 = -0.999995f;
            projection.m23 = -1f;

            string range = RigReport.ClipRange(projection);

            StringAssert.StartsWith(range, "n=");
            StringAssert.EndsWith(range, "(unreliable)");
        }

        // Reversed depth gives distance = near / depth: metres below a thousand, kilometres from there, and
        // infinite at the far plane.
        [TestMethod]
        public void DistanceFollowsNearOverDepth()
        {
            Assert.AreEqual("1.0 m", RigReport.Distance(0.5f, 0.5f));
            Assert.AreEqual("500.0 m", RigReport.Distance(500f, 1f));
            Assert.AreEqual("2.0 km", RigReport.Distance(500f, 0.25f));
            Assert.AreEqual("infinite", RigReport.Distance(0.5f, 0f));
        }

        // The jitter terms are written with six decimals.
        [TestMethod]
        public void ShearHasSixDecimals()
        {
            Assert.AreEqual("(0.500000, -0.250000)", RigReport.Shear(0.5f, -0.25f));
        }

        // Two equal matrices differ by nothing; otherwise the largest element difference is given.
        [TestMethod]
        public void MaxElementDeltaIsTheLargestDifference()
        {
            Matrix4x4 a = Matrix4x4.identity;
            Matrix4x4 b = Matrix4x4.identity;
            Assert.AreEqual(0f, RigReport.MaxElementDelta(a, b));

            b.m03 = 0.5f;
            b.m12 = -2f;
            Assert.AreEqual(2f, RigReport.MaxElementDelta(a, b));
        }

        // The depth text reads the nearest and the median value of the unsorted readback.
        [TestMethod]
        public void DepthTextNamesTheNearestAndTheMedianPoint()
        {
            float[] values = { 0f, 0.5f, 0.25f, 0.5f };

            Assert.AreEqual("nearest point 1.0 m  median 1.0 m  (raw max 0.50000)", RigReport.DescribeDepth(0.5f, values));
        }

        // A buffer of zeros holds no depth at all, which the text shows as infinite.
        [TestMethod]
        public void DepthTextOfAnEmptyBufferIsInfinite()
        {
            Assert.AreEqual("nearest point infinite  median infinite  (raw max 0.00000)",
                            RigReport.DescribeDepth(0.5f, new float[4]));
        }

        // A motion vector in UV units is a length in pixels at the render size: 0.03 and 0.04 of 100 pixels make 3 and 4.
        [TestMethod]
        public void MotionMagnitudeIsALengthInRenderPixels()
        {
            float length = RigPreview.MotionMagnitude(new Color(0.03f, 0.04f, 0f, 0f), new Vector2Int(100, 100));

            Assert.AreEqual(5f, length, 1e-4f);
        }

        // Pixels of 0, 1, 2 and 5 px motion: three of four move more than a quarter pixel, and the percentiles are
        // read from the sorted lengths.
        [TestMethod]
        public void MotionTextCountsMovingPixelsAndPercentiles()
        {
            Color[] pixels =
            {
                new Color(0f, 0f, 0f, 0f),
                new Color(0.01f, 0f, 0f, 0f),
                new Color(0f, 0.02f, 0f, 0f),
                new Color(0.03f, 0.04f, 0f, 0f),
            };

            Assert.AreEqual("moving pixels 75.00%  p50 2.000  p95 5.000  max 5.000 px",
                            RigReport.DescribeMotion(pixels, new Vector2Int(100, 100)));
        }

        // A mask marks the pixels whose red is above 0.01; the mean counts all of them.
        [TestMethod]
        public void CoverageCountsMarkedPixels()
        {
            Color[] pixels =
            {
                new Color(0f, 0f, 0f, 0f),
                new Color(0.005f, 0f, 0f, 0f),
                new Color(0.495f, 0f, 0f, 0f),
                new Color(1f, 0f, 0f, 0f),
            };

            Assert.AreEqual("marks 50.00% (mean 0.375)", RigReport.Coverage(pixels));
        }
    }
}
