using System;
using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests.Atmospheres.D34
{
    // Package D34, the depth: the mask, the depth from the field's metres, the sub-grid noise, the R16 map and its tiles.
    [TestClass]
    public class CoverDepthTests
    {
        internal static CoverDef Snow(double minLat = -90, double maxLat = 90, double minAlt = double.NegativeInfinity, double maxAlt = double.PositiveInfinity,
            double maxSlope = 60, bool noWater = true, float maxDepth = 0.5f)
        {
            return new CoverDef("snow", CoverKind.Snow, 0.93f, 0.95f, 0.98f, 0.8f, maxDepth, minLat, maxLat, minAlt, maxAlt, maxSlope, noWater, true, 10800, 0.3f, 4f);
        }

        internal static CoverDef Sand(bool noWater = false)
        {
            return new CoverDef("sand", CoverKind.Sand, 0.62f, 0.52f, 0.36f, 0.9f, 0.1f, -90, 90, double.NegativeInfinity, double.PositiveInfinity, 60, noWater, true, 86400, 0.8f, 6f);
        }

        [TestMethod]
        public void TheMaskIsOneInsideTheRangesAndFadesAtTheirEdges()
        {
            CoverDef d = Snow(minLat: 30, maxLat: 80, minAlt: 100, maxAlt: 3000, maxSlope: 40);
            Assert.AreEqual(1.0, CoverDepth.Mask(d, 55, 1000, 5, false), 1e-9);
            Assert.AreEqual(0.0, CoverDepth.Mask(d, 20, 1000, 5, false), 1e-9, "south of the range");
            Assert.AreEqual(0.0, CoverDepth.Mask(d, 85, 1000, 5, false), 1e-9, "north of the range");
            Assert.AreEqual(0.5, CoverDepth.Mask(d, 30, 1000, 5, false), 1e-9, "half at the edge");
            Assert.AreEqual(0.0, CoverDepth.Mask(d, 55, -200, 5, false), 1e-9, "below the altitude range");
            Assert.AreEqual(0.0, CoverDepth.Mask(d, 55, 4000, 5, false), 1e-9, "above the altitude range");
            Assert.AreEqual(0.0, CoverDepth.Mask(d, 55, 1000, 60, false), 1e-9, "too steep");
            double edge = CoverDepth.Mask(d, 55, 1000, 34, false);
            Assert.IsTrue(edge > 0.05 && edge < 0.95, "the slope fades over its edge: " + edge);
            Assert.AreEqual(0.0, CoverDepth.Mask(d, 55, 1000, 5, true), 1e-9, "no cover on water where the layer says so");
            Assert.AreEqual(0.0, CoverDepth.Mask(d, double.NaN, 0, 0, false));
            Assert.AreEqual(0.0, CoverDepth.Mask(null, 10, 0, 0, false));
        }

        [TestMethod]
        public void ABoundAtThePoleIsNoBound()
        {
            CoverDef d = Snow();
            Assert.AreEqual(1.0, CoverDepth.Mask(d, 90, 0, 0, false), 1e-9);
            Assert.AreEqual(1.0, CoverDepth.Mask(d, -90, 0, 0, false), 1e-9);
        }

        [TestMethod]
        public void SandDustAndAshNeverLieOnWaterAndSnowFollowsItsRule()
        {
            Assert.AreEqual(0.0, CoverDepth.Mask(Sand(noWater: false), 10, 0, 0, true), 1e-9, "sand on a dry body only");
            Assert.AreEqual(1.0, CoverDepth.Mask(Sand(), 10, 0, 0, false), 1e-9);
            var dust = new CoverDef("dust", CoverKind.Dust, 0.5f, 0.4f, 0.3f, 0.9f, 0.05f, -90, 90, double.NegativeInfinity, double.PositiveInfinity, 60, false, true, 86400, 0.8f, 6f);
            var ash = new CoverDef("ash", CoverKind.Ash, 0.2f, 0.2f, 0.2f, 0.9f, 0.05f, -90, 90, double.NegativeInfinity, double.PositiveInfinity, 60, false, true, 86400, 0.1f, 2f);
            Assert.IsTrue(dust.NoWater && ash.NoWater && Sand().NoWater);
            Assert.AreEqual(0.0, CoverDepth.Mask(dust, 0, 0, 0, true), 1e-9);
            Assert.AreEqual(0.0, CoverDepth.Mask(ash, 0, 0, 0, true), 1e-9);
            CoverDef wetSnow = Snow(noWater: false);
            Assert.AreEqual(1.0, CoverDepth.Mask(wetSnow, 60, 0, 0, true), 1e-9, "a snow rule may allow it");
            Assert.IsTrue(Sand().Dunes && dust.Dunes && !Snow().Dunes && !ash.Dunes);
        }

        [TestMethod]
        public void TheDepthIsTheFieldsDepthTimesTheMaskAndTheStrengthWhereTheNoiseIsOff()
        {
            CoverDef d = Snow();
            for (double m = 0; m <= 0.6; m += 0.05)
            {
                double depth = CoverDepth.Depth01(d, m, 1.0, 0.3, 0.0, 1.0);
                Assert.AreEqual(Math.Min(1.0, m / 0.5), depth, 1e-12, "field " + m);
            }
            Assert.AreEqual(0.5, CoverDepth.Depth01(d, 0.25, 1.0, 0.0, 0.0, 1.0), 1e-12, "0.25 m of 0.5 m");
            Assert.AreEqual(0.25, CoverDepth.Depth01(d, 0.25, 1.0, 0.0, 0.0, 0.5), 1e-12, "strength");
            Assert.AreEqual(0.25, CoverDepth.Depth01(d, 0.25, 0.5, 0.0, 0.0, 1.0), 1e-12, "mask");
            Assert.AreEqual(0.0, CoverDepth.Depth01(d, 0.0, 1.0, 0.0, 0.12, 1.0), "bare stays bare whatever the noise");
            Assert.AreEqual(0.0, CoverDepth.Depth01(d, -1.0, 1.0, 0.0, 0.12, 1.0));
            Assert.AreEqual(0.0, CoverDepth.Depth01(d, double.NaN, 1.0, 0.0, 0.12, 1.0));
        }

        [TestMethod]
        public void TheNoiseErodesTheEdgeOnlyAndAFullDepthStaysFull()
        {
            CoverDef d = Snow();
            const double edge = 0.12;
            for (double noise = -1; noise <= 1.0001; noise += 0.25)
            {
                Assert.AreEqual(1.0, CoverDepth.Depth01(d, 0.5, 1.0, noise, edge, 1.0), 1e-12, "full stays full at noise " + noise);
                double thin = CoverDepth.Depth01(d, 0.025, 1.0, noise, edge, 1.0);
                double plain = 0.05;
                Assert.IsTrue(thin <= plain + 1e-12 && thin >= 0.0, "the noise only takes away: " + thin);
            }
            Assert.AreEqual(0.05, CoverDepth.Depth01(d, 0.025, 1.0, 1.0, edge, 1.0), 1e-12, "noise +1 takes nothing");
            Assert.AreEqual(0.0, CoverDepth.Depth01(d, 0.025, 1.0, -1.0, edge, 1.0), 1e-12, "noise -1 takes the thin edge off");
            double middle = CoverDepth.Depth01(d, 0.25, 1.0, -1.0, edge, 1.0);
            Assert.IsTrue(middle > 0.5 - edge - 1e-9 && middle < 0.5, "a half depth loses at most the erosion: " + middle);
        }

        [TestMethod]
        public void TheR16ValueRoundTripsWithinOneStep()
        {
            for (double v = 0; v <= 1.0; v += 0.0137)
            {
                ushort q = CoverDepth.Quantise(v);
                Assert.AreEqual(v, CoverDepth.Dequantise(q), 1.0 / CoverDepth.Quanta);
            }
            Assert.AreEqual((ushort)0, CoverDepth.Quantise(-1));
            Assert.AreEqual((ushort)CoverDepth.Quanta, CoverDepth.Quantise(7));
        }

        [TestMethod]
        public void TheSlopeOfANormalIsItsAngleToTheUp()
        {
            Assert.AreEqual(0.0, CoverDepth.SlopeDegrees(1.0), 1e-9);
            Assert.AreEqual(90.0, CoverDepth.SlopeDegrees(0.0), 1e-9);
            Assert.AreEqual(60.0, CoverDepth.SlopeDegrees(0.5), 1e-9);
            Assert.AreEqual(0.0, CoverDepth.SlopeDegrees(3.0), 1e-9, "clamped");
        }

        // ---- the map ----------------------------------------------------------------------------------------------------------

        private static float[] Grid(int w, int h, Func<double, double, float> f)
        {
            var v = new float[w * h];
            for (int j = 0; j < h; j++)
                for (int i = 0; i < w; i++)
                    v[j * w + i] = f(90.0 - (j + 0.5) * 180.0 / h, -180.0 + (i + 0.5) * 360.0 / w);
            return v;
        }

        [TestMethod]
        public void TheMapKeepsTheFieldsValueWithinQuantisationAndBilinearBetweenTexels()
        {
            var map = new CoverMap();
            float[] values = Grid(256, 128, (lat, lon) => (float)(0.2 + 0.1 * Math.Sin(lat / 20.0) * Math.Cos(lon / 30.0)));
            CoverMap.Frame frame = map.Resample(values, 256, 128, 0.5f, 7, 123.0);
            map.Publish(frame);
            Assert.AreEqual(7L, map.SourceVersion);
            Assert.AreEqual(123.0, frame.Ut);
            // At the source texel centres the map has the source value (every 4th texel of the 1024 x 512 map sits between sources).
            for (int j = 4; j < 128; j += 17)
                for (int i = 3; i < 256; i += 31)
                {
                    double lat = 90.0 - (j + 0.5) * 180.0 / 128, lon = -180.0 + (i + 0.5) * 360.0 / 256;
                    Assert.AreEqual(values[j * 256 + i] / 0.5, frame.Sample01(lat, lon), 2.0 / CoverDepth.Quanta + 1e-4, "lat " + lat + " lon " + lon);
                }
            Assert.AreEqual(frame.Sample01(10, 179.99), frame.Sample01(10, -180.01), 1e-3, "the longitude wraps");
            Assert.IsTrue(frame.Peak > 0);
        }

        [TestMethod]
        public void AnEmptyOrBadFieldGivesABareMapAndTheFirstFrameIsNumberZero()
        {
            var map = new CoverMap();
            Assert.AreEqual(0, map.Current.Number);
            Assert.AreEqual(-1L, map.SourceVersion);
            Assert.AreEqual(0.0, map.Current.Sample01(50, 20));
            CoverMap.Frame bare = map.Resample(null, 0, 0, 0.5f, 1, 0.0);
            Assert.AreEqual((ushort)0, bare.Peak);
            CoverMap.Frame nan = map.Resample(new[] { float.NaN, float.PositiveInfinity, -3f, 0.1f }, 2, 2, 0.5f, 1, 0.0);
            Assert.IsTrue(nan.Sample01(0, 0) >= 0.0 && nan.Sample01(0, 0) <= 1.0);
            Assert.AreEqual(0.0, map.Resample(new float[4], 2, 2, 0f, 1, 0.0).Sample01(0, 0), "a layer without a depth is bare");
        }

        [TestMethod]
        public void OnlyTheTilesThatChangedGetANewVersion()
        {
            var map = new CoverMap();
            float[] a = Grid(256, 128, (lat, lon) => lat > 40 ? 0.2f : 0f);
            CoverMap.Frame f1 = map.Resample(a, 256, 128, 0.5f, 1, 0.0);
            map.Publish(f1);
            // A system forms over the south: a patch at 30 S, 100 E.
            float[] b = Grid(256, 128, (lat, lon) => lat > 40 ? 0.2f : Math.Abs(lat + 30) < 10 && Math.Abs(lon - 100) < 15 ? 0.3f : 0f);
            CoverMap.Frame f2 = map.Resample(b, 256, 128, 0.5f, 2, 100.0);
            map.Publish(f2);
            int tx, ty;
            CoverMap.TileOf(-30, 100, out tx, out ty);
            Assert.AreEqual(f2.Number, f2.TileVersions[ty * CoverMap.TilesX + tx], "the tile under the patch changed");
            CoverMap.TileOf(70, -50, out tx, out ty);
            Assert.AreEqual(f1.Number, f2.TileVersions[ty * CoverMap.TilesX + tx], "the tile of the north did not");
            CoverMap.TileOf(0, -150, out tx, out ty);
            Assert.AreEqual(0, f2.TileVersions[ty * CoverMap.TilesX + tx], "a tile that was never touched has version 0");
            int changed = 0;
            for (int i = 0; i < f2.TileVersions.Length; i++) if (f2.TileVersions[i] == f2.Number) changed++;
            Assert.IsTrue(changed >= 1 && changed <= 8, "only the tiles around the patch changed: " + changed);
            Assert.AreEqual(f2.Number, CoverMap.MaxVersion(f2.TileVersions, -35, -25, 95, 105));
            Assert.AreEqual(f1.Number, CoverMap.MaxVersion(f2.TileVersions, 60, 80, -60, -40));
        }

        [TestMethod]
        public void ASlowDriftBelowTheThresholdChangesNoTile()
        {
            var map = new CoverMap();
            CoverMap.Frame f1 = map.Resample(Grid(256, 128, (lat, lon) => lat > 40 ? 0.2f : 0f), 256, 128, 0.5f, 1, 0.0);
            map.Publish(f1);
            CoverMap.Frame f2 = map.Resample(Grid(256, 128, (lat, lon) => lat > 40 ? 0.2005f : 0f), 256, 128, 0.5f, 2, 1.0);
            CollectionAssert.AreEqual(f1.TileVersions, f2.TileVersions);
            CollectionAssert.AreEqual(f1.Texels, f2.Texels, "the old texels stay, so that nothing is repainted for a drift of a thousandth");
        }

        [TestMethod]
        public void TheResampleOfOneBodyTakesUnderFiveMillisecondsOnAWorker()
        {
            var map = new CoverMap();
            float[] values = Grid(256, 128, (lat, lon) => (float)(0.25 + 0.2 * Math.Sin(lat / 7.0) * Math.Cos(lon / 11.0)));
            map.Resample(values, 256, 128, 0.5f, 0, 0.0);
            double best = double.MaxValue;
            for (int run = 0; run < 8; run++)
            {
                for (int i = 0; i < values.Length; i++) values[i] += 0.001f * (run + 1);
                var watch = Stopwatch.StartNew();
                map.Resample(values, 256, 128, 0.5f, run + 1, run);
                best = Math.Min(best, watch.Elapsed.TotalMilliseconds);
            }
            Console.WriteLine("D34 coverMap resample best of 8: " + best.ToString("0.00") + " ms");
            Assert.IsTrue(best < 5.0, "resample took " + best + " ms");
        }

        [TestMethod]
        public void TheMapIsOneMegabyteAndTheTilesCoverIt()
        {
            Assert.AreEqual(1024 * 512 * 2, new CoverMap().Current.Texels.Length * sizeof(ushort));
            Assert.AreEqual(CoverMap.Width * CoverMap.Height, CoverMap.TilesX * CoverMap.TilesY * CoverMap.TileSize * CoverMap.TileSize);
            int tx, ty;
            CoverMap.TileOf(90, -180, out tx, out ty);
            Assert.AreEqual(0, tx);
            Assert.AreEqual(0, ty);
            CoverMap.TileOf(-90, 179.999, out tx, out ty);
            Assert.AreEqual(CoverMap.TilesX - 1, tx);
            Assert.AreEqual(CoverMap.TilesY - 1, ty);
        }
    }
}
