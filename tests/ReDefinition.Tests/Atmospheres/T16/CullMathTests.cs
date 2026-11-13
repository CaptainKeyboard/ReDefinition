using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests.Atmospheres.T16
{
    // The pure parts of the visibility culling and the distance LOD (package T16): the bound maps against a brute force over the map (a planted
    // contributing texel is never lost), the cone against sphere test against sampled rays, the tile decision of the shader as a CPU twin against planted
    // clouds, the fades (zero and one at their ends, monotone, continuous) and the option plumbing (every switch off by default). The shader mirrors
    // these functions; the weather player measures the picture (tools/weather_metrics/cull.py).
    [TestClass]
    public class CullMathTests
    {
        private const int W = 1024, H = 512;

        private static byte[] Map(int seed, int blobs)
        {
            var map = new byte[W * H];
            var rng = new Random(seed);
            for (int b = 0; b < blobs; b++)
            {
                int cx = rng.Next(W), cy = rng.Next(H), r = 2 + rng.Next(14);
                byte v = (byte)(120 + rng.Next(135));
                for (int y = Math.Max(0, cy - r); y <= Math.Min(H - 1, cy + r); y++)
                    for (int x = cx - r; x <= cx + r; x++)
                        if ((x - cx) * (x - cx) + (y - cy) * (y - cy) <= r * r)
                        {
                            int xx = (x % W + W) % W;
                            if (map[y * W + xx] < v) map[y * W + xx] = v;
                        }
            }
            return map;
        }

        // The extreme over every texel within the footprint of a point (in texels), widened by the bilinear read's one texel.
        private static byte Brute(byte[] map, double px, double py, double radius, bool max)
        {
            byte best = max ? (byte)0 : (byte)255;
            int x0 = (int)Math.Floor(px - radius - 1.0), x1 = (int)Math.Ceiling(px + radius + 1.0);
            int y0 = Math.Max(0, (int)Math.Floor(py - radius - 1.0)), y1 = Math.Min(H - 1, (int)Math.Ceiling(py + radius + 1.0));
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    byte v = map[y * W + ((x % W + W) % W)];
                    if (max ? v > best : v < best) best = v;
                }
            return best;
        }

        [TestMethod]
        public void TheBoundMapsContainEveryTexelOfAFootprintWithinACell()
        {
            byte[] map = Map(7, 260);
            var cellMax = new byte[CullMath.FineW * CullMath.FineH];
            var cellMin = new byte[CullMath.FineW * CullMath.FineH];
            CullMath.Cells(map, W, H, CullMath.FineW, CullMath.FineH, cellMax, cellMin);
            byte[] fineMax = CullMath.Dilate(cellMax, CullMath.FineW, CullMath.FineH, true), fineMin = CullMath.Dilate(cellMin, CullMath.FineW, CullMath.FineH, false);
            byte[] coarseMax = CullMath.Dilate(CullMath.Coarsen(cellMax, CullMath.FineW, CullMath.FineH, CullMath.CoarseW, CullMath.CoarseH, true), CullMath.CoarseW, CullMath.CoarseH, true);
            byte[] coarseMin = CullMath.Dilate(CullMath.Coarsen(cellMin, CullMath.FineW, CullMath.FineH, CullMath.CoarseW, CullMath.CoarseH, false), CullMath.CoarseW, CullMath.CoarseH, false);
            var rng = new Random(3);
            for (int n = 0; n < 4000; n++)
            {
                double px = rng.NextDouble() * W, py = rng.NextDouble() * H;
                // Level 0: the footprint may use 0.85 of a cell (8 texels), level 1 0.85 of a coarse cell (32 texels).
                bool coarse = (n & 1) == 1;
                double cellTexels = coarse ? W / (double)CullMath.CoarseW : W / (double)CullMath.FineW;
                double radius = rng.NextDouble() * CullMath.CellUse * cellTexels;
                int gw = coarse ? CullMath.CoarseW : CullMath.FineW, gh = coarse ? CullMath.CoarseH : CullMath.FineH;
                int cx = Math.Min(gw - 1, (int)(px * gw / W)), cy = Math.Min(gh - 1, (int)(py * gh / H));
                byte bmax = (coarse ? coarseMax : fineMax)[cy * gw + cx], bmin = (coarse ? coarseMin : fineMin)[cy * gw + cx];
                Assert.IsTrue(Brute(map, px, py, radius, true) <= bmax, "the maximum bound lost a texel at " + px + ", " + py + " radius " + radius);
                Assert.IsTrue(Brute(map, px, py, radius, false) >= bmin, "the minimum bound lost a texel at " + px + ", " + py + " radius " + radius);
            }
        }

        [TestMethod]
        public void ALevelIsChosenByTheFootprintAndNoneIsGivenAboveTheCoarseCell()
        {
            Assert.AreEqual(0, CullMath.Level(0.01, 0.0));
            Assert.AreEqual(1, CullMath.Level(0.1, 0.0));
            Assert.AreEqual(-1, CullMath.Level(0.2, 0.0));
            // Near the pole the footprint is wider in longitude: the same angle needs a coarser level.
            Assert.AreEqual(1, CullMath.Level(0.02, 1.4));
            Assert.AreEqual(0, CullMath.Level(0.02, 0.0));
            Assert.AreEqual(-1, CullMath.Level(0.1, 1.5));
        }

        [TestMethod]
        public void TheEmptyBoundIsTheMarchsOwnCoverageBound()
        {
            // The shader's LayerDensity: cov + coverage - 1 + 0.5 amount + 0.5 edge <= 0 is empty. The slack moves it by two map steps toward "may contribute".
            Assert.IsTrue(CullMath.EmptyMargin(0.2, 0.62, 0.45, 0.18, 0.0) > 0.0 == (0.2 + 0.62 - 1.0 + 0.225 + 0.09 > 0.0));
            Assert.IsTrue(CullMath.BoundIsEmpty(0.0, 0.62, 0.45, 0.18), "a clear map is empty");
            Assert.IsFalse(CullMath.BoundIsEmpty(0.4, 0.62, 0.45, 0.18), "a map above the threshold may contribute");
            double edgeOfEmpty = 1.0 - 0.62 - 0.225 - 0.09;
            Assert.IsTrue(CullMath.BoundIsEmpty(edgeOfEmpty - 2.0 * CullMath.Slack, 0.62, 0.45, 0.18));
            Assert.IsFalse(CullMath.BoundIsEmpty(edgeOfEmpty, 0.62, 0.45, 0.18), "the slack keeps the margin");
            Assert.IsTrue(CullMath.BoundIsSolid(1.0, 0.62, 0.45, 0.18));
            Assert.IsFalse(CullMath.BoundIsSolid(0.5, 0.62, 0.45, 0.18));
        }

        [TestMethod]
        public void TheConeTestNeverMissesARayThatHitsTheSphere()
        {
            var rng = new Random(11);
            const double R = 603000.0;
            for (int n = 0; n < 3000; n++)
            {
                double alt = rng.NextDouble() * 900000.0;
                double ox = 0, oy = 0, oz = 600000.0 + alt;
                double ax = rng.NextDouble() * 2 - 1, ay = rng.NextDouble() * 2 - 1, az = rng.NextDouble() * 2 - 1;
                double l = Math.Sqrt(ax * ax + ay * ay + az * az);
                if (l < 1e-3) continue;
                ax /= l; ay /= l; az /= l;
                double rho = rng.NextDouble() * 0.03;
                bool anyHit = false;
                for (int s = 0; s < 40 && !anyHit; s++)
                {
                    // A ray inside the cone: the axis turned by an angle up to rho around a random perpendicular.
                    double ph = rng.NextDouble() * 2 * Math.PI, th = rho * (s == 0 ? 0 : Math.Sqrt(rng.NextDouble()));
                    double ux = -ay, uy = ax, uz = 0;
                    double ul = Math.Sqrt(ux * ux + uy * uy);
                    if (ul < 1e-6) { ux = 1; uy = 0; uz = 0; ul = 1; }
                    ux /= ul; uy /= ul;
                    double vx = ay * uz - az * uy, vy = az * ux - ax * uz, vz = ax * uy - ay * ux;
                    double dx = ax * Math.Cos(th) + (ux * Math.Cos(ph) + vx * Math.Sin(ph)) * Math.Sin(th);
                    double dy = ay * Math.Cos(th) + (uy * Math.Cos(ph) + vy * Math.Sin(ph)) * Math.Sin(th);
                    double dz = az * Math.Cos(th) + (uz * Math.Cos(ph) + vz * Math.Sin(ph)) * Math.Sin(th);
                    double b = ox * dx + oy * dy + oz * dz, c = ox * ox + oy * oy + oz * oz - R * R;
                    if (b * b - c > 0 && -b + Math.Sqrt(b * b - c) > 0) anyHit = true;
                }
                if (anyHit) Assert.IsTrue(CullMath.ConeMayHit(ox, oy, oz, ax, ay, az, rho, R), "the cone test lost a hit, altitude " + alt);
            }
            Assert.IsFalse(CullMath.ConeMayHit(0, 0, 700000.0, 0, 0, 1, 0.01, R), "looking away from the planet, far out");
            Assert.IsTrue(CullMath.ConeMayHit(0, 0, 600500.0, 0, 0, 1, 0.01, R), "inside the shell every direction hits");
        }

        // The tile decision of the shader on the CPU: the bundle's centre ray sampled twelve times with the footprint ho + half a step against a
        // dilated maximum map; true where the tile is proved empty.
        private static bool TileProvedEmpty(byte[] fineMax, byte[] coarseMax, double[] o, double[] d, double rho, double tA, double tB, double coverage, double amount, double edge)
        {
            for (int j = 0; j < CullMath.BundleSamples; j++)
            {
                double step = (tB - tA) / CullMath.BundleSamples, t = tA + step * (j + 0.5), rad = rho * t + 0.5 * step;
                double px = o[0] + d[0] * t, py = o[1] + d[1] * t, pz = o[2] + d[2] * t;
                double len = Math.Sqrt(px * px + py * py + pz * pz), lat = Math.Asin(pz / len), lon = Math.Atan2(py, px);
                int level = CullMath.Level(rad / len, lat);
                if (level < 0) return false;
                double u = (lon * 180.0 / Math.PI + 180.0) / 360.0, v = (90.0 - lat * 180.0 / Math.PI) / 180.0;
                int gw = level == 0 ? CullMath.FineW : CullMath.CoarseW, gh = level == 0 ? CullMath.FineH : CullMath.CoarseH;
                int cx = Math.Min(gw - 1, (int)(u * gw)), cy = Math.Min(gh - 1, (int)(v * gh));
                double bound = (level == 0 ? fineMax : coarseMax)[cy * gw + cx] / 255.0;
                if (!CullMath.BoundIsEmpty(bound, coverage, amount, edge)) return false;
            }
            return true;
        }

        [TestMethod]
        public void ATileWithAPlantedContributingTexelUnderItsRaysIsNeverProvedEmpty()
        {
            const double R = 600000.0;
            var rng = new Random(5);
            int decided = 0, provedEmpty = 0;
            for (int n = 0; n < 600; n++)
            {
                // A map that is clear except for one blob.
                var map = new byte[W * H];
                int bx = rng.Next(W), by = 100 + rng.Next(H - 200);
                for (int y = by - 2; y <= by + 2; y++)
                    for (int x = bx - 2; x <= bx + 2; x++) map[y * W + ((x % W + W) % W)] = 200;
                var cellMax = new byte[CullMath.FineW * CullMath.FineH];
                CullMath.Cells(map, W, H, CullMath.FineW, CullMath.FineH, cellMax, new byte[cellMax.Length]);
                byte[] fineMax = CullMath.Dilate(cellMax, CullMath.FineW, CullMath.FineH, true);
                byte[] coarseMax = CullMath.Dilate(CullMath.Coarsen(cellMax, CullMath.FineW, CullMath.FineH, CullMath.CoarseW, CullMath.CoarseH, true), CullMath.CoarseW, CullMath.CoarseH, true);
                // A camera on the ground below the blob's direction, aiming at the blob's texel at a random elevation: the ray passes over it at the layer.
                double lon = ((bx + 0.5) / W * 360.0 - 180.0) * Math.PI / 180.0, lat = (90.0 - (by + 0.5) / H * 180.0) * Math.PI / 180.0;
                double[] target = { Math.Cos(lat) * Math.Cos(lon), Math.Cos(lat) * Math.Sin(lon), Math.Sin(lat) };
                double distance = 20000.0 + rng.NextDouble() * 250000.0, layerAlt = 1500.0;
                // The camera sits on the ground at an angle (distance / R) away from the blob along a great circle.
                double ang = distance / R, bearing = rng.NextDouble() * 2 * Math.PI;
                double[] east = { -Math.Sin(lon), Math.Cos(lon), 0 }, north = { -Math.Sin(lat) * Math.Cos(lon), -Math.Sin(lat) * Math.Sin(lon), Math.Cos(lat) };
                double[] dir = new double[3];
                for (int k = 0; k < 3; k++) dir[k] = east[k] * Math.Cos(bearing) + north[k] * Math.Sin(bearing);
                double[] camUnit = new double[3];
                for (int k = 0; k < 3; k++) camUnit[k] = target[k] * Math.Cos(ang) + dir[k] * Math.Sin(ang);
                double[] o = { camUnit[0] * (R + 2.0), camUnit[1] * (R + 2.0), camUnit[2] * (R + 2.0) };
                double[] hit = { target[0] * (R + layerAlt), target[1] * (R + layerAlt), target[2] * (R + layerAlt) };
                double[] d = { hit[0] - o[0], hit[1] - o[1], hit[2] - o[2] };
                double t0 = Math.Sqrt(d[0] * d[0] + d[1] * d[1] + d[2] * d[2]);
                for (int k = 0; k < 3; k++) d[k] /= t0;
                double rho = 0.0136;
                // The bundle's range around the hit; the planted blob is at the ray's t0 (a ray of the bundle can reach it: the footprint at t0 is rho t0 wide
                // and the blob 2.5 texels = 11 km, so only the offset within the footprint counts: the centre ray goes through it exactly).
                decided++;
                bool empty = TileProvedEmpty(fineMax, coarseMax, o, d, rho, Math.Max(0, t0 * 0.8), t0 * 1.2, 0.62, 0.45, 0.18);
                if (empty) provedEmpty++;
                Assert.IsFalse(empty, "a tile whose centre ray goes through a planted cloud was proved empty (distance " + distance + ")");
            }
            Assert.AreEqual(600, decided);
            Assert.AreEqual(0, provedEmpty);
        }

        [TestMethod]
        public void ATileOverAClearMapIsProvedEmptyAtAllDistances()
        {
            var map = new byte[W * H];
            var cellMax = new byte[CullMath.FineW * CullMath.FineH];
            CullMath.Cells(map, W, H, CullMath.FineW, CullMath.FineH, cellMax, new byte[cellMax.Length]);
            byte[] fineMax = CullMath.Dilate(cellMax, CullMath.FineW, CullMath.FineH, true);
            byte[] coarseMax = CullMath.Dilate(CullMath.Coarsen(cellMax, CullMath.FineW, CullMath.FineH, CullMath.CoarseW, CullMath.CoarseH, true), CullMath.CoarseW, CullMath.CoarseH, true);
            double[] o = { 600002.0, 0, 0 };
            double[] d = { Math.Cos(1.2), 0, Math.Sin(1.2) };
            Assert.IsTrue(TileProvedEmpty(fineMax, coarseMax, o, d, 0.0136, 5000.0, 60000.0, 0.62, 0.45, 0.18));
        }

        [TestMethod]
        public void TheFadesAreZeroAndOneAtTheirEndsMonotoneAndContinuous()
        {
            Assert.AreEqual(0.0, CullMath.DistanceFade(0.02, 0.02), 1e-12);
            Assert.AreEqual(0.0, CullMath.DistanceFade(0.001, 0.02), 1e-12);
            Assert.AreEqual(1.0, CullMath.DistanceFade(0.04, 0.02), 1e-12);
            Assert.AreEqual(1.0, CullMath.DistanceFade(0.9, 0.02), 1e-12);
            double last = -1.0, lastStep = 0.0;
            for (int i = 0; i <= 400; i++)
            {
                double w = 0.02 + 0.02 * i / 400.0, f = CullMath.DistanceFade(w, 0.02);
                Assert.IsTrue(f >= last, "monotone");
                if (i > 0) Assert.IsTrue(f - last < 0.02, "no jump");
                last = f;
                lastStep = f;
            }
            Assert.AreEqual(1.0, lastStep, 1e-12);
            Assert.AreEqual(1.0, CullMath.CapFade(0, 2, 500000.0, 100000.0), "the nearest spans stay");
            Assert.AreEqual(1.0, CullMath.CapFade(3, 2, 100000.0, 100000.0), 1e-12, "a far span is whole at the cap distance");
            Assert.AreEqual(0.0, CullMath.CapFade(3, 2, 125000.0, 100000.0), 1e-12, "and gone a quarter further");
            Assert.AreEqual(1.0, CullMath.CapFade(3, 2, 900000.0, 0.0), "no cap distance, no cap");
            Assert.AreEqual(1.0, CullMath.StepScale(-5.0, 4e-5));
            Assert.AreEqual(2.0, CullMath.StepScale(25000.0, 4e-5), 1e-12);
            Assert.AreEqual(1.0, CullMath.TapFactor(1000.0, 20000.0, 120000.0, 3.0));
            Assert.AreEqual(3.0, CullMath.TapFactor(200000.0, 20000.0, 120000.0, 3.0));
            Assert.AreEqual(1.0, CullMath.TapFactor(1e6, 20000.0, 120000.0, 1.0), "factor 1 is off");
            Assert.AreEqual(3.5, CullMath.Foot(1000.0, 3.5, 1e-3), "never below the spacing");
            Assert.AreEqual(100.0, CullMath.Foot(100000.0, 3.5, 1e-3), 1e-9);
        }

        [TestMethod]
        public void TheClearestChannelDecidesTheAirWeight()
        {
            Assert.AreEqual(Math.Exp(-0.5), CullMath.AirWeight(0.5, 1.3, 3.9), 1e-12);
        }

        [TestMethod]
        public void TheLeastVerticalOpticalDepthIsTheProductOfTheLeastShareAndMeanProfile()
        {
            double tau = CullMath.MinVerticalTau(0.012, 2000.0, 3, t => new[] { 0.3, 0.65, 1.0 }[t], t => new[] { 0.5, 0.6, 0.4 }[t]);
            Assert.AreEqual(0.012 * 2000.0 * 0.3 * 0.4, tau, 1e-9);
            Assert.AreEqual(0.0, CullMath.MinVerticalTau(0.012, 2000.0, 0, t => 1.0, t => 1.0));
        }
    }
}
