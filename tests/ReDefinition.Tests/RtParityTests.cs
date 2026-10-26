using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Raytracing;

namespace ReDefinition.Tests
{
    // Where the rays' scene and the picture disagree (RtParity).
    [TestClass]
    public class RtParityTests
    {
        // One pixel of the primary check's output: the distance met, the distance the depth gives, the instance plus one.
        private static float[] Pixel(float hit, float expected, int instance)
        {
            return new[] { hit, expected, (float)instance, 1f };
        }

        private static RtParity.Kind Kind(float hit, float expected, bool terrain = false)
        {
            return RtParity.Classify(hit, expected, terrain);
        }

        // Both give the same surface within half a per cent and 5 cm.
        [TestMethod]
        public void TheSameSurfaceIsAMatch()
        {
            Assert.AreEqual(RtParity.Kind.Match, Kind(100.2f, 100f));
            Assert.AreEqual(RtParity.Kind.Match, Kind(10f, 10.04f));
        }

        // A surface before or behind the picture's is a disagreement.
        [TestMethod]
        public void ASurfaceBeforeOrBehindThePicturesIsNamedSo()
        {
            Assert.AreEqual(RtParity.Kind.InFront, Kind(90f, 100f));
            Assert.AreEqual(RtParity.Kind.Behind, Kind(120f, 100f));
        }

        // Nothing met where the picture has a surface, and a surface met under sky.
        [TestMethod]
        public void MissesAndSurfacesOverSkyAreNamed()
        {
            Assert.AreEqual(RtParity.Kind.Missing, Kind(0f, 50f));
            Assert.AreEqual(RtParity.Kind.OverSky, Kind(400f, 0f));
            Assert.AreEqual(RtParity.Kind.Sky, Kind(0f, 0f));
        }

        // The ground, which Parallax displaces off its mesh, may be 2 m and 3 per cent off.
        [TestMethod]
        public void TheGroundMayBeFurtherOffThanOtherSurfaces()
        {
            Assert.AreEqual(RtParity.Kind.InFront, Kind(97f, 100f));
            Assert.AreEqual(RtParity.Kind.Match, Kind(97f, 100f, true));
            Assert.AreEqual(RtParity.Kind.InFront, Kind(90f, 100f, true));
        }

        // A frame's counts, and the instances that make most of the disagreement.
        [TestMethod]
        public void AFrameIsCountedAndItsWorstInstanceNamed()
        {
            var data = new System.Collections.Generic.List<float>();
            data.AddRange(Pixel(100f, 100f, 1));
            data.AddRange(Pixel(80f, 100f, 3));
            data.AddRange(Pixel(80f, 100f, 3));
            data.AddRange(Pixel(300f, 0f, 2));
            data.AddRange(Pixel(0f, 0f, 0));
            RtParity.Result result = RtParity.Analyse(data.ToArray(), 5, 1, id => false);
            Assert.AreEqual(1, result.Count[(int)RtParity.Kind.Match]);
            Assert.AreEqual(2, result.Count[(int)RtParity.Kind.InFront]);
            Assert.AreEqual(1, result.Count[(int)RtParity.Kind.OverSky]);
            Assert.AreEqual(1, result.Count[(int)RtParity.Kind.Sky]);
            string report = RtParity.Report(result, id => "instance " + id, 1);
            StringAssert.Contains(report, "match 20.0 %");
            StringAssert.Contains(report, "instance 2: in front 2");
        }
    }
}
