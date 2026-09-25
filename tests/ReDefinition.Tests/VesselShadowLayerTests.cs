using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Upscaler;
using UnityEngine;

namespace ReDefinition.Tests
{
    // CasterBounds.Plausible: which renderer bounds may size the light map.
    [TestClass]
    public class VesselShadowLayerTests
    {
        private static readonly Vector3 Vessel = new Vector3(10f, 2f, -5f);

        [TestMethod]
        public void PartsBoundsCount()
        {
            Assert.IsTrue(CasterBounds.Plausible(new Bounds(Vessel + new Vector3(3f, 0f, 1f), new Vector3(4f, 1f, 6f)), Vessel));
        }

        [TestMethod]
        public void BoundsAsMeasuredInFlightDoNot()
        {
            // One renderer of 74 on a spaceplane, from the dump of 25 September 2026.
            Bounds huge = new Bounds(Vector3.zero, 2f * new Vector3(7.150083E+17f, 7.89311635E+17f, 8.50714961E+17f));
            Assert.IsFalse(CasterBounds.Plausible(huge, Vessel));
        }

        [TestMethod]
        public void BoundsLongerThanAVesselDoNot()
        {
            Assert.IsFalse(CasterBounds.Plausible(new Bounds(Vessel, new Vector3(300f, 1f, 1f)), Vessel));
        }

        [TestMethod]
        public void BoundsFarFromTheVesselDoNot()
        {
            Assert.IsFalse(CasterBounds.Plausible(new Bounds(Vessel + new Vector3(600f, 0f, 0f), Vector3.one), Vessel));
        }

        [TestMethod]
        public void BoundsNotFiniteDoNot()
        {
            Assert.IsFalse(CasterBounds.Plausible(new Bounds(new Vector3(float.NaN, 0f, 0f), Vector3.one), Vessel));
            Assert.IsFalse(CasterBounds.Plausible(new Bounds(Vessel, new Vector3(float.PositiveInfinity, 1f, 1f)), Vessel));
        }
    }
}
