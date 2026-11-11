using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests
{
    // The pure parts of the orbit and map cover: the texel convention, the mask that has to equal the ground's depth, the heights, the
    // schedule. The ground's depth is CoverDepth on the field's map (CoverMap); the orbit mask is the same function per texel.
    [TestClass]
    public class OrbitSnowTests
    {
        private static CoverDef Snow()
        {
            return new CoverDef("snow", CoverKind.Snow, 0.93f, 0.95f, 0.98f, 0.8f, 0.5f, -90, 90, double.NegativeInfinity, double.PositiveInfinity, 60, true, true, 10800, 0.3f, 4f);
        }

        // PQS.BuildVertexMapCoords written out: latitude from y, longitude from the x and z components, u and v from them.
        private static void PqsCoordinates(double x, double y, double z, out double u, out double v)
        {
            double latitude = Math.Asin(y);
            double xz = Math.Sqrt(x * x + z * z);
            double longitude = z >= 0 ? Math.Asin(x / xz) : Math.PI - Math.Asin(x / xz);
            v = latitude / Math.PI + 0.5;
            u = longitude / Math.PI * 0.5;
        }

        [TestMethod]
        public void ATexelHasTheDirectionThePqsMapsToThatTexel()
        {
            for (double v = 0.05; v < 1; v += 0.1)
            {
                for (double u = 0.02; u < 1; u += 0.07)
                {
                    double x, y, z;
                    OrbitMask.Direction(u, v, out x, out y, out z);
                    Assert.AreEqual(1.0, x * x + y * y + z * z, 1e-9);
                    double pu, pv;
                    PqsCoordinates(x, y, z, out pu, out pv);
                    Assert.AreEqual(v, pv, 1e-9);
                    // The PQS u lies in -0.25 to 0.75 and the texture wraps.
                    double diff = ((pu - u) % 1.0 + 1.0) % 1.0;
                    if (diff > 0.5) diff = 1.0 - diff;
                    Assert.AreEqual(0.0, diff, 1e-9, "u " + u + " v " + v + " gave " + pu);
                }
            }
        }

        [TestMethod]
        public void TheRowsRunFromTheSouthPoleToTheNorthPole()
        {
            Assert.AreEqual(-90.0, OrbitMask.LatitudeDegrees(0), 1e-9);
            Assert.AreEqual(0.0, OrbitMask.LatitudeDegrees(0.5), 1e-9);
            Assert.AreEqual(90.0, OrbitMask.LatitudeDegrees(1), 1e-9);
            double x, y, z;
            OrbitMask.Direction(0.3, 0.75, out x, out y, out z);
            Assert.AreEqual(Math.Sin(45 * Math.PI / 180), y, 1e-9, "v 0.75 is 45 degrees north");
        }

        // A field map with a band of 0.3 m of snow north of 60 degrees and a patch of 0.5 m around 10 N, 30 E.
        private static CoverMap.Frame BandFrame(CoverMap map)
        {
            const int w = 256, h = 128;
            var values = new float[w * h];
            for (int j = 0; j < h; j++)
            {
                double lat = 90.0 - (j + 0.5) * 180.0 / h;
                for (int i = 0; i < w; i++)
                {
                    double lon = -180.0 + (i + 0.5) * 360.0 / w;
                    float v = lat > 60 ? 0.3f : 0f;
                    if (Math.Abs(lat - 10) < 8 && Math.Abs(lon - 30) < 16) v = 0.5f;
                    values[j * w + i] = v;
                }
            }
            CoverMap.Frame frame = map.Resample(values, w, h, 0.5f, 1, 100.0);
            map.Publish(frame);
            return frame;
        }

        private static byte[] Fill(CoverMap.Frame frame, CoverDef def, double strength, double edge, float[] heights, int hw, int hh, int w, int hgt, bool ocean)
        {
            sbyte[] noise = OrbitMask.BuildNoise(w, hgt);
            float[] lat, lon;
            OrbitMask.BuildCoordinates(w, hgt, KspFrame.Sphere, out lat, out lon);
            var rgba = new byte[w * hgt * 4];
            OrbitMask.Fill(rgba, w, hgt, noise, heights, hw, hh, lat, lon, frame, def, strength, edge, ocean);
            return rgba;
        }

        private static double MeanAlpha(byte[] rgba, int w, int h, double latFrom, double latTo)
        {
            double sum = 0;
            int n = 0;
            for (int row = 0; row < h; row++)
            {
                double lat = OrbitMask.LatitudeDegrees((row + 0.5) / h);
                if (lat < latFrom || lat > latTo) continue;
                for (int c = 0; c < w; c++)
                {
                    sum += rgba[(row * w + c) * 4 + 3] / 255.0;
                    n++;
                }
            }
            return sum / n;
        }

        [TestMethod]
        public void TheMaskIsTheGroundDepthAtEveryTexel()
        {
            const int w = 128, h = 64;
            CoverDef def = Snow();
            CoverMap.Frame frame = BandFrame(new CoverMap());
            sbyte[] noise;
            noise = OrbitMask.BuildNoise(w, h);
            float[] lat, lon;
            OrbitMask.BuildCoordinates(w, h, KspFrame.Sphere, out lat, out lon);
            var rgba = new byte[w * h * 4];
            OrbitMask.Fill(rgba, w, h, noise, null, OrbitMask.HeightWidth, OrbitMask.HeightHeight, lat, lon, frame, def, 1.0, 0.12, false);
            // On a sphere with the sphere's own frame, the game's longitude is the PQS longitude.
            for (int row = 0; row < h; row += 3)
            {
                for (int c = 0; c < w; c += 5)
                {
                    int at = row * w + c;
                    double depthM = frame.Sample01(lat[at], lon[at]) * def.MaxDepthM;
                    double mask = CoverDepth.Mask(def, lat[at], 0, 0, false);
                    double expected = CoverDepth.Depth01(def, depthM, mask, noise[at] / 127.0, 0.12, 1.0);
                    Assert.AreEqual(expected * 255.0, rgba[at * 4 + 3], 1.0, "row " + row + " column " + c);
                    Assert.AreEqual((byte)Math.Round(0.93 * 255), rgba[at * 4], "the layer's colour");
                }
            }
        }

        [TestMethod]
        public void TheCoverIsWhereTheFieldPutIt()
        {
            const int w = 256, h = 128;
            CoverMap.Frame frame = BandFrame(new CoverMap());
            byte[] mask = Fill(frame, Snow(), 1.0, 0.0, null, OrbitMask.HeightWidth, OrbitMask.HeightHeight, w, h, false);
            Assert.IsTrue(MeanAlpha(mask, w, h, 65, 90) > 0.45 && MeanAlpha(mask, w, h, 65, 90) < 0.7, "0.3 m of 0.5 m is 0.6 of the full depth");
            Assert.AreEqual(0.0, MeanAlpha(mask, w, h, -80, 0), 1e-9, "the south is bare");
            Assert.AreEqual(0.0, MeanAlpha(mask, w, h, 20, 55), 1e-9, "the middle latitudes are bare");
        }

        [TestMethod]
        public void TheStrengthScalesTheMaskAndTheSeaStaysBare()
        {
            const int w = 256, h = 128;
            CoverMap.Frame frame = BandFrame(new CoverMap());
            byte[] full = Fill(frame, Snow(), 1.0, 0.0, null, OrbitMask.HeightWidth, OrbitMask.HeightHeight, w, h, false);
            byte[] half = Fill(frame, Snow(), 0.5, 0.0, null, OrbitMask.HeightWidth, OrbitMask.HeightHeight, w, h, false);
            Assert.AreEqual(MeanAlpha(full, w, h, 65, 90) / 2.0, MeanAlpha(half, w, h, 65, 90), 0.01);
            // A grid that is below sea level everywhere on a body with an ocean: the layer does not lie on water.
            var sea = new float[OrbitMask.HeightWidth * OrbitMask.HeightHeight];
            for (int i = 0; i < sea.Length; i++) sea[i] = -50f;
            byte[] wet = Fill(frame, Snow(), 1.0, 0.0, sea, OrbitMask.HeightWidth, OrbitMask.HeightHeight, w, h, true);
            Assert.AreEqual(0.0, MeanAlpha(wet, w, h, -90, 90), 1e-9);
            byte[] dryBody = Fill(frame, Snow(), 1.0, 0.0, sea, OrbitMask.HeightWidth, OrbitMask.HeightHeight, w, h, false);
            Assert.IsTrue(MeanAlpha(dryBody, w, h, 65, 90) > 0.4, "no ocean, so no water");
        }

        [TestMethod]
        public void TheHeightGridIsBilinearWrapsInLongitudeAndIgnoresBadCells()
        {
            var grid = new float[4 * 2];
            for (int i = 0; i < grid.Length; i++) grid[i] = i * 100f;
            Assert.AreEqual(0f, OrbitMask.Height(grid, 4, 2, 0.125, 0.25), 1e-3);
            Assert.AreEqual(200f, OrbitMask.Height(grid, 4, 2, 0.625, 0.25), 1e-3);
            Assert.AreEqual(500f, OrbitMask.Height(grid, 4, 2, 0.375, 0.75), 1e-3);
            Assert.AreEqual(50f, OrbitMask.Height(grid, 4, 2, 0.25, 0.25), 1e-3);
            Assert.AreEqual(150f, OrbitMask.Height(grid, 4, 2, 0.0, 0.25), 1e-3);
            Assert.AreEqual(0f, OrbitMask.Height(grid, 4, 2, 0.125, 0.0), 1e-3);
            Assert.AreEqual(700f, OrbitMask.Height(grid, 4, 2, 0.875, 1.0), 1e-3);
            grid[1] = float.NaN;
            Assert.IsFalse(float.IsNaN(OrbitMask.Height(grid, 4, 2, 0.2, 0.25)));
            Assert.AreEqual(0f, OrbitMask.Height(null, 4, 2, 0.5, 0.5));
            Assert.AreEqual(0f, OrbitMask.Height(new float[3], 4, 2, 0.5, 0.5), "a grid that is too small counts as none");
        }

        [TestMethod]
        public void TheNoiseTableIsDeterministicInRangeAndTheOnesTheGroundUses()
        {
            sbyte[] a = OrbitMask.BuildNoise(64, 32);
            sbyte[] b = OrbitMask.BuildNoise(64, 32);
            CollectionAssert.AreEqual(a, b);
            int min = 127, max = -127;
            foreach (sbyte v in a)
            {
                min = Math.Min(min, v);
                max = Math.Max(max, v);
            }
            Assert.IsTrue(max - min > 40, "the table varies, " + min + " to " + max);
            double x, y, z;
            OrbitMask.Direction((10 + 0.5) / 64, (20 + 0.5) / 32, out x, out y, out z);
            Assert.AreEqual(Math.Round(SnowNoise.Value(x, y, z) * 127.0), a[20 * 64 + 10]);
        }

        [TestMethod]
        public void TheCompositionFollowsANewFrameAfterTheShortStepOnly()
        {
            Assert.IsTrue(OrbitMask.Due(0, 0, 0, false), "never composed");
            Assert.IsFalse(OrbitMask.Due(3, 3, 1000, false), "the same frame is not composed again");
            Assert.IsFalse(OrbitMask.Due(3, 4, 1, false), "a new frame waits the short step");
            Assert.IsTrue(OrbitMask.Due(3, 4, OrbitMask.LargeStepSeconds + 0.1, false));
            Assert.IsFalse(OrbitMask.Due(3, 3, 1, true));
            Assert.IsTrue(OrbitMask.Due(3, 3, 2.5, true), "changed settings or heights");
        }

        [TestMethod]
        public void TheWorkingMapKeepsTheAspectAndAFloorAndItsMemoryIsKnown()
        {
            int w, h;
            OrbitMask.WorkingSize(4096, 2048, 2048, out w, out h);
            Assert.AreEqual(2048, w);
            Assert.AreEqual(1024, h);
            OrbitMask.WorkingSize(1024, 512, 4096, out w, out h);
            Assert.AreEqual(1024, w, "never larger than the original");
            Assert.AreEqual(512, h);
            OrbitMask.WorkingSize(0, 0, 4096, out w, out h);
            Assert.IsTrue(w >= 64 && h >= 32);
            Assert.AreEqual(2048L * 1024 * 4 * 7 / 3, OrbitMask.Bytes(2048, 1024), 1);
            Assert.IsTrue(OrbitMask.Bytes(4096, 2048) < 130L * 1024 * 1024);
        }
    }
}
