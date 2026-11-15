using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests.Atmospheres.T16
{
    // The empty reach of the layer march (E40 C1): the reach map never promises more empty space than there is (against a brute force over the map, with the wrap in longitude and the
    // slew's two ends), a texel that can hold cloud has none, a map with no cloud has the longest reach, the CPU twin of the shader's metres is a chord that grows with the reach and
    // shrinks towards the poles, and the switch reaches the frame (on by default, off through the tuning key). The shader mirrors EmptyReachMath; the weather player measures that the
    // picture is the same with and without the leap (tools/weather_metrics/e40c1.py).
    [TestClass]
    public class EmptyReachTests
    {
        private const int W = 512, H = 256;

        private static byte[] Blobs(int seed, int blobs, int maxRadius)
        {
            var map = new byte[W * H];
            var rng = new Random(seed);
            for (int b = 0; b < blobs; b++)
            {
                int cx = rng.Next(W), cy = rng.Next(H), r = 1 + rng.Next(maxRadius);
                byte v = (byte)(40 + rng.Next(200));
                for (int y = Math.Max(0, cy - r); y <= Math.Min(H - 1, cy + r); y++)
                    for (int x = cx - r; x <= cx + r; x++)
                        if ((x - cx) * (x - cx) + (y - cy) * (y - cy) <= r * r)
                            map[y * W + (x % W + W) % W] = v;
            }
            return map;
        }

        // Layer numbers under the soft density with the cover meaning (the noise amount 1, the edge the softness 0.5, the clear-sky K 0.2, a coverage that puts the bound at twice the map
        // value over K): a map value of a few steps passes the bound, zero does not.
        private static bool[] Table()
        {
            var t = new bool[256];
            EmptyReachMath.Table(t, 0.2f, 1f, 0.5f, 0.2f);
            return t;
        }

        private static double Euclid(byte[] map, bool[] occupied, int x, int y)
        {
            double best = double.MaxValue;
            for (int yy = 0; yy < H; yy++)
                for (int xx = 0; xx < W; xx++)
                {
                    if (!occupied[map[yy * W + xx]]) continue;
                    int dx = Math.Abs(xx - x);
                    dx = Math.Min(dx, W - dx);
                    double d = Math.Sqrt(dx * dx + (yy - y) * (yy - y));
                    if (d < best) best = d;
                }
            return best;
        }

        [TestMethod]
        public void TheReachNeverExceedsTheDistanceToTheNearestTexelThatCanHoldCloudLessTheMargin()
        {
            bool[] occupied = Table();
            foreach (int seed in new[] { 1, 2, 3 })
            {
                byte[] map = Blobs(seed, 9, 22);
                var reach = new byte[W * H];
                EmptyReachMath.Build(map, null, W, H, occupied, reach, new float[(W + 256) * H]);
                var rng = new Random(seed * 17);
                for (int i = 0; i < 400; i++)
                {
                    int x = rng.Next(W), y = rng.Next(H);
                    double d = Euclid(map, occupied, x, y);
                    double limit = d - EmptyReachMath.MarginTexels;
                    Assert.IsTrue(reach[y * W + x] <= Math.Max(0.0, Math.Min(limit, EmptyReachMath.MaxReach)) + 1e-9,
                        "reach " + reach[y * W + x] + " at " + x + "," + y + " against the distance " + d + " (seed " + seed + ")");
                }
            }
        }

        [TestMethod]
        public void ATexelThatCanHoldCloudHasNoReachAndTheReachIsUsefulBetweenClouds()
        {
            bool[] occupied = Table();
            byte[] map = Blobs(5, 6, 12);
            var reach = new byte[W * H];
            EmptyReachMath.Build(map, null, W, H, occupied, reach, new float[(W + 256) * H]);
            int far = 0, empty = 0;
            for (int i = 0; i < map.Length; i++)
            {
                if (occupied[map[i]]) Assert.AreEqual(0, reach[i], "an occupied texel has no reach");
                else
                {
                    empty++;
                    if (reach[i] >= 10) far++;
                }
            }
            Assert.IsTrue(far > empty / 4, "six small clouds leave most of the map a long way from the nearest one: " + far + " of " + empty);
        }

        [TestMethod]
        public void TheWrapInLongitudeCountsACloudAcrossTheSeam()
        {
            bool[] occupied = Table();
            var map = new byte[W * H];
            for (int y = 100; y < 110; y++) map[y * W + W - 1] = 200;
            var reach = new byte[W * H];
            EmptyReachMath.Build(map, null, W, H, occupied, reach, new float[(W + 256) * H]);
            // column 0 is next to the cloud in column W - 1: nothing to leap over; column 20 is 21 texels from it
            Assert.AreEqual(0, reach[105 * W + 0]);
            Assert.IsTrue(reach[105 * W + 20] <= 21 - 2 && reach[105 * W + 20] >= 21 / 1.0824 - 3.5, "the reach across the seam is the distance less the margin: " + reach[105 * W + 20]);
            Assert.IsTrue(reach[105 * W + W - 40] >= 30 && reach[105 * W + W - 40] <= 38, "and on the near side: " + reach[105 * W + W - 40]);
        }

        [TestMethod]
        public void ASlewIsCoveredByTheLargerOfItsTwoEnds()
        {
            bool[] occupied = Table();
            byte[] now = Blobs(7, 4, 10), goal = Blobs(8, 4, 10);
            var reach = new byte[W * H];
            EmptyReachMath.Build(now, goal, W, H, occupied, reach, new float[(W + 256) * H]);
            var middle = new byte[W * H];
            for (int i = 0; i < middle.Length; i++) middle[i] = (byte)((now[i] + goal[i]) / 2);
            var rng = new Random(3);
            for (int i = 0; i < 300; i++)
            {
                int x = rng.Next(W), y = rng.Next(H);
                double d = Math.Min(Euclid(now, occupied, x, y), Math.Min(Euclid(goal, occupied, x, y), Euclid(middle, occupied, x, y)));
                Assert.IsTrue(reach[y * W + x] <= Math.Max(0.0, d - EmptyReachMath.MarginTexels) + 1e-9, "a reach inside the slew's occupied texels at " + x + "," + y);
            }
        }

        [TestMethod]
        public void AMapWithNoCloudHasTheLongestReachEverywhere()
        {
            bool[] occupied = Table();
            var reach = new byte[W * H];
            EmptyReachMath.Build(new byte[W * H], null, W, H, occupied, reach, new float[(W + 256) * H]);
            foreach (byte r in reach) Assert.AreEqual(EmptyReachMath.MaxReach, r);
        }

        [TestMethod]
        public void TheOccupiedTableIsTheShadersBoundWithItsSlack()
        {
            var t = new bool[256];
            // the layer of the shipped cfg under the soft density: a map value passes the bound once it exceeds what the bound's constants leave
            EmptyReachMath.Table(t, 0.25f, 1f, 0.5f, 0f);
            Assert.IsFalse(t[0], "a map of zero holds nothing");
            Assert.IsTrue(t[255]);
            bool seen = false;
            for (int v = 0; v < 256; v++)
            {
                if (t[v]) seen = true;
                else Assert.IsFalse(seen, "monotone in the map value: a value below an occupied one cannot be empty again (" + v + ")");
            }
            // the exact bound: cov + coverage - 1 + 0.5 noise + 0.5 edge above zero (K 0: the clear-sky rule changes nothing)
            for (int v = 0; v < 256; v++)
            {
                double bound = v / 255.0 + 0.25 - 1.0 + 0.5 + 0.25;
                Assert.AreEqual(bound > EmptyReachMath.BoundEpsilon, t[v], "value " + v);
            }
        }

        [TestMethod]
        public void TheShippedLayersHoldNothingWhereTheMapIsZeroAndSomethingFromTheFirstStep()
        {
            Tuning.ClearOverrides();
            Tuning.Instance.BeginFrame(9100001);
            foreach (CloudLayerDef layer in ReDefinition.Tests.Atmospheres.T09.TypeMixFixture.ShippedLayers())
            {
                var t = new bool[256];
                EmptyReachMath.Table(t, layer.EffectiveCoverage, layer.EffectiveNoiseAmount, layer.EffectiveEdgeSoftness, layer.ClearSkyK);
                Assert.IsFalse(t[0], layer.Name + ": a map of zero holds no cloud");
                Assert.IsTrue(t[1], layer.Name + ": a map value of one step can hold some (the noise at its highest)");
            }
        }

        [TestMethod]
        public void TheReachInMetresIsAChordThatGrowsWithTheReachAndShrinksTowardsThePoles()
        {
            const double R = 600000.0;
            Assert.AreEqual(0.0, EmptyReachMath.ReachMetres(0, 0.5, W, H, R), 1e-9);
            double a = EmptyReachMath.ReachMetres(10, 0.5, W, H, R), b = EmptyReachMath.ReachMetres(20, 0.5, W, H, R);
            Assert.IsTrue(b > a && a > 0.0);
            // at the equator a texel along a parallel is as wide as along a meridian (2 pi / 512 = pi / 256); the cosine is that of the latitude the reach can touch: the arc of 10 texels, as a chord
            double texel = Math.PI / H;
            Assert.AreEqual(R * Math.Sin(10 * Math.Min(texel, texel * Math.Cos(10 * texel))), a, 0.5);
            double polar = EmptyReachMath.ReachMetres(10, 0.04, W, H, R);
            Assert.IsTrue(polar < 0.5 * a, "near a pole the same texels are narrower along a parallel: " + polar + " against " + a);
            Assert.IsTrue(EmptyReachMath.ReachMetres(120, 0.5, W, H, R) < Math.PI * R, "never more than the planet's half circle");
        }

        [TestMethod]
        public void TheSpanSpacingKeysReachTheFrameAndAreOffByDefault()
        {
            var frame = new LayerFrame();
            Tuning.ClearOverrides();
            Tuning.Instance.BeginFrame(9100020);
            OwnCloudOptions.Apply(frame, key => AtmospheresSettings.Definition(key).Default);
            Assert.AreEqual(OwnCloudOptions.SpanSpacingDefault, frame.SpanSpacing);
            Assert.AreEqual(OwnCloudOptions.SpanSpacingMinDefault, frame.SpanSpacingMin);
            Tuning.SetOverride(OwnCloudOptions.TuneSpanSpacing, 1.5f);
            Tuning.SetOverride(OwnCloudOptions.TuneSpanSpacingMin, 12f);
            try
            {
                Tuning.Instance.BeginFrame(9100021);
                OwnCloudOptions.Apply(frame, key => AtmospheresSettings.Definition(key).Default);
                Assert.AreEqual(1.5f, frame.SpanSpacing);
                Assert.AreEqual(12, frame.SpanSpacingMin);
            }
            finally
            {
                Tuning.ClearOverrides();
                Tuning.Instance.BeginFrame(9100022);
            }
        }

        [TestMethod]
        public void TheLeapIsOnByDefaultAndTheTuningKeySwitchesItOff()
        {
            var frame = new LayerFrame();
            Assert.IsTrue(frame.EmptyReach, "the frame as compiled leaps");
            Tuning.ClearOverrides();
            Tuning.Instance.BeginFrame(9100010);
            OwnCloudOptions.Apply(frame, key => AtmospheresSettings.Definition(key).Default);
            Assert.IsTrue(frame.EmptyReach);
            Tuning.SetOverride(OwnCloudOptions.TuneEmptyReach, 0f);
            try
            {
                Tuning.Instance.BeginFrame(9100011);
                OwnCloudOptions.Apply(frame, key => AtmospheresSettings.Definition(key).Default);
                Assert.IsFalse(frame.EmptyReach);
            }
            finally
            {
                Tuning.ClearOverrides();
                Tuning.Instance.BeginFrame(9100012);
            }
        }
    }
}
