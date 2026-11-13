using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests
{
    // E40 grain against TVC: the altitude interval the march searches this frame (LayerSpanBound). The bound has to be conservative: every type value and every
    // coverage value a bilinear read of the region can return lies inside what the bound was taken over.
    [TestClass]
    public class LayerSpanBoundTests
    {
        private const int W = 64, H = 32;

        // A layer like Kerbin's cumulus layer: 1700 to 7400 m, four types of rising tops and one middle-level type.
        private static CloudLayerDef Layer()
        {
            return new CloudLayerDef("c", 0, 1700f, 7400f, new[]
            {
                CloudTypeDef.Slab("sc", 0f, 0.11f), CloudTypeDef.Slab("hum", 0f, 0.21f), CloudTypeDef.Slab("med", 0f, 0.47f), CloudTypeDef.Slab("con", 0f, 1f),
                CloudTypeDef.Slab("ac", 0.54f, 0.68f)
            }, 0.05f);
        }

        private static byte[] Map(byte value)
        {
            var m = new byte[W * H];
            for (int i = 0; i < m.Length; i++) m[i] = value;
            return m;
        }

        private static LayerSpanBound.Result Bound(byte[] cov, byte[] type, double lat, double lon, double region, CloudLayerDef layer = null, float coverage = 0.35f)
        {
            layer = layer ?? Layer();
            float inner, outer;
            layer.TightAltitudes(out inner, out outer);
            return LayerSpanBound.Bound(cov, type, W, H, lat, lon, region, new LayerTypeTable(layer), layer, coverage, 1f, 0.15f, inner, outer);
        }

        [TestMethod]
        public void TheLowestTypeAloneGivesItsOwnInterval()
        {
            LayerSpanBound.Result r = Bound(Map(170), Map(0), 0.0, 0.0, 10.0);
            Assert.IsTrue(r.Tightened);
            Assert.IsFalse(r.Empty);
            Assert.AreEqual(1700f, r.InnerM, 0.5f);
            Assert.AreEqual(1700f + 0.11f * 5700f, r.OuterM, 0.5f);
        }

        [TestMethod]
        public void ATypeBetweenTheSmallestAndTheLargestValueCounts()
        {
            // Values 0 and 1 side by side: a bilinear read between them passes every type of the table, also the tallest in the middle of it.
            byte[] type = Map(0);
            int row = H / 2;
            type[row * W + W / 2] = 255;
            LayerSpanBound.Result r = Bound(Map(170), type, 0.0, 0.0, 10.0);
            Assert.AreEqual(7400f, r.OuterM, 0.5f);
            Assert.AreEqual(1700f, r.InnerM, 0.5f);
            // Only the last type (the middle level sheet): base and top are its own.
            LayerSpanBound.Result top = Bound(Map(170), Map(255), 0.0, 0.0, 10.0);
            Assert.AreEqual(1700f + 0.54f * 5700f, top.InnerM, 1f);
            Assert.AreEqual(1700f + 0.68f * 5700f, top.OuterM, 1f);
        }

        [TestMethod]
        public void ATexelOutsideTheRegionIsNotRead()
        {
            byte[] type = Map(0);
            // 90 degrees of longitude away from the camera.
            type[(H / 2) * W + (W / 2 + W / 4)] = 255;
            LayerSpanBound.Result near = Bound(Map(170), type, 0.0, 0.0, 10.0);
            Assert.AreEqual(1700f + 0.11f * 5700f, near.OuterM, 0.5f);
            LayerSpanBound.Result wide = Bound(Map(170), type, 0.0, 0.0, 95.0);
            Assert.IsTrue(!wide.Tightened || wide.OuterM > 7000f);
        }

        [TestMethod]
        public void TheRegionWrapsAroundTheDateLineAndTakesEveryColumnAtAPole()
        {
            byte[] type = Map(0);
            type[(H / 2) * W + 0] = 255;   // longitude -180
            LayerSpanBound.Result r = Bound(Map(170), type, 0.0, 178.0, 10.0);
            Assert.AreEqual(7400f, r.OuterM, 0.5f);
            byte[] polar = Map(0);
            polar[1 * W + 5] = 255;
            LayerSpanBound.Result p = Bound(Map(170), polar, 86.0, 100.0, 5.0);
            Assert.AreEqual(7400f, p.OuterM, 0.5f);
        }

        [TestMethod]
        public void ACoverageThatCannotReachTheThresholdIsEmpty()
        {
            // The shader's bound: cov + coverage - 1 + 0.5 amount + 0.5 edge <= 0. With coverage 0.12, amount 1 and edge 0.15 the map has to stay below 0.305.
            Assert.IsTrue(Bound(Map(70), Map(0), 0.0, 0.0, 10.0, null, 0.12f).Empty);
            Assert.IsFalse(Bound(Map(80), Map(0), 0.0, 0.0, 10.0, null, 0.12f).Empty);
            byte[] cov = Map(0);
            cov[(H / 2 + 1) * W + W / 2 + 1] = 200;
            Assert.IsFalse(Bound(cov, Map(0), 0.0, 0.0, 10.0, null, 0.12f).Empty);
        }

        [TestMethod]
        public void TheHeightVariationWidensTheIntervalAndTheTableIsNeverLeft()
        {
            CloudLayerDef layer = Layer();
            layer.HeightDownM = 300f;
            layer.HeightUpM = 400f;
            LayerSpanBound.Result r = Bound(Map(170), Map(0), 0.0, 0.0, 10.0, layer);
            Assert.AreEqual(1400f, r.InnerM, 0.5f);
            Assert.AreEqual(1700f + 0.11f * 5700f + 400f, r.OuterM, 0.5f);
            LayerSpanBound.Result all = Bound(Map(170), Map(153), 0.0, 0.0, 10.0, layer);
            float inner, outer;
            layer.TightAltitudes(out inner, out outer);
            Assert.IsTrue(all.InnerM >= inner && all.OuterM <= outer);
        }

        [TestMethod]
        public void TheReachIsWhatTheHeightMapsHoldInTheRegion()
        {
            // Root -0.5 and +0.25 of 800 m, top 0.5 of 1200 m, variation -0.25 and +0.5 of 400 m near the camera; a far texel with the full ranges is not read.
            var bytes = new byte[W * H * 3];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = 128;
            int near = ((H / 2) * W + W / 2) * 3, near2 = ((H / 2 + 1) * W + W / 2 + 1) * 3, far = ((H / 2) * W + W / 2 + W / 4) * 3;
            bytes[near] = (byte)(128 - 64);
            bytes[near2] = (byte)(128 + 32);
            bytes[near + 1] = (byte)(128 + 64);
            bytes[near + 2] = (byte)(128 - 32);
            bytes[near2 + 2] = (byte)(128 + 64);
            bytes[far] = 1;
            bytes[far + 1] = 255;
            LayerSpanBound.HeightExtremes e = LayerSpanBound.Widen(LayerSpanBound.HeightExtremes.Neutral, bytes, W, H, 0.0, 0.0, 10.0);
            Assert.IsTrue(e.Scanned);
            Assert.AreEqual(-64f / 127f, e.RootMin, 1e-6f);
            Assert.AreEqual(32f / 127f, e.RootMax, 1e-6f);
            Assert.AreEqual(64f / 127f, e.TopMax, 1e-6f);
            float down, up;
            LayerSpanBound.Reach(e, 800f, 1200f, 400f, 110f, 120f, out down, out up);
            Assert.AreEqual(800f * 64f / 127f + 400f * 32f / 127f + 220f, down, 0.01f);
            Assert.AreEqual(800f * 32f / 127f + 400f * 64f / 127f + 1200f * 64f / 127f + 220f + 120f, up, 0.01f);
            // Neutral maps: only the ragged boundary and the thinnest cloud remain; a layer without height variation has no reach.
            LayerSpanBound.Reach(LayerSpanBound.HeightExtremes.Neutral, 800f, 1200f, 400f, 110f, 120f, out down, out up);
            Assert.AreEqual(220f, down, 1e-4f);
            Assert.AreEqual(340f, up, 1e-4f);
            LayerSpanBound.Reach(e, 800f, 1200f, 400f, 110f, 0f, out down, out up);
            Assert.AreEqual(0f, down);
            Assert.AreEqual(0f, up);
            // A second map widens the first; a region too large to scan gives the whole ranges.
            var other = new byte[W * H * 3];
            for (int i = 0; i < other.Length; i++) other[i] = 128;
            other[near] = 1;
            LayerSpanBound.HeightExtremes both = LayerSpanBound.Widen(e, other, W, H, 0.0, 0.0, 10.0);
            Assert.AreEqual(-1f, both.RootMin, 1e-6f);
            Assert.AreEqual(e.TopMax, both.TopMax, 1e-6f);
            LayerSpanBound.HeightExtremes whole = LayerSpanBound.Widen(LayerSpanBound.HeightExtremes.Neutral, new byte[512 * 256 * 3], 512, 256, 0.0, 0.0, 80.0);
            Assert.IsFalse(whole.Scanned);
            LayerSpanBound.Reach(whole, 800f, 1200f, 400f, 110f, 120f, out down, out up);
            Assert.AreEqual(800f + 400f + 220f, down, 1e-3f);
            Assert.AreEqual(800f + 400f + 1200f + 220f + 120f, up, 1e-3f);
        }

        [TestMethod]
        public void TheRegionsReachReplacesTheLayersWorstCaseAndNeverExceedsIt()
        {
            CloudLayerDef layer = Layer();
            layer.HeightDownM = 1270f;
            layer.HeightUpM = 2590f;
            LayerSpanBound.Result worst = Bound(Map(170), Map(0), 0.0, 0.0, 10.0, layer);
            Assert.AreEqual(430f, worst.InnerM, 0.5f);
            Assert.AreEqual(1700f + 0.11f * 5700f + 2590f, worst.OuterM, 0.5f);
            float inner, outer;
            layer.TightAltitudes(out inner, out outer);
            var table = new LayerTypeTable(layer);
            LayerSpanBound.Result r = LayerSpanBound.Bound(Map(170), Map(0), W, H, 0.0, 0.0, 10.0, table, layer, 0.35f, 1f, 0.15f, inner, outer, 300f, 500f);
            Assert.AreEqual(1400f, r.InnerM, 0.5f);
            Assert.AreEqual(1700f + 0.11f * 5700f + 500f, r.OuterM, 0.5f);
            LayerSpanBound.Result capped = LayerSpanBound.Bound(Map(170), Map(0), W, H, 0.0, 0.0, 10.0, table, layer, 0.35f, 1f, 0.15f, inner, outer, 9000f, 9000f);
            Assert.AreEqual(worst.InnerM, capped.InnerM, 0.5f);
            Assert.AreEqual(worst.OuterM, capped.OuterM, 0.5f);
        }

        [TestMethod]
        public void ARegionOfHalfTheMapKeepsTheTable()
        {
            var big = new byte[512 * 256];
            CloudLayerDef layer = Layer();
            float inner, outer;
            layer.TightAltitudes(out inner, out outer);
            LayerSpanBound.Result r = LayerSpanBound.Bound(big, big, 512, 256, 0.0, 0.0, 80.0, new LayerTypeTable(layer), layer, 0.35f, 1f, 0.15f, inner, outer);
            Assert.IsFalse(r.Tightened);
        }

        [TestMethod]
        public void TheRegionIsTheHorizonOfTheCameraPlusTheHorizonOfTheShell()
        {
            // From 264 m to a shell top at 7400 m on Kerbin: 1.70 + 8.95 degrees, plus the light march's 4 km (0.38 degrees).
            Assert.AreEqual(1.70 + 8.95 + 0.38, LayerSpanBound.RegionDeg(600000.0, 264.0, 7400.0, 4000.0), 0.03);
            Assert.AreEqual(LayerSpanBound.RegionDeg(600000.0, 0.0, 7400.0, 0.0), LayerSpanBound.RegionDeg(600000.0, -50.0, 7400.0, 0.0), 1e-12);
        }

        [TestMethod]
        public void TheShareRangeFollowsTheTablesInterpolation()
        {
            var table = new LayerTypeTable(Layer());
            float lo, hi;
            // Halfway between the first two types: the top is the mean of theirs.
            LayerSpanBound.ShareRange(table, 0.125f, 0.125f, out lo, out hi);
            Assert.AreEqual(0f, lo, 1e-6f);
            Assert.AreEqual(0.16f, hi, 1e-4f);
            // Between the tallest type and the middle sheet the base rises linearly: the lowest base is at the low end.
            LayerSpanBound.ShareRange(table, 0.875f, 1f, out lo, out hi);
            Assert.AreEqual(0.27f, lo, 1e-4f);
            Assert.AreEqual(0.84f, hi, 1e-4f);
        }
    }
}
