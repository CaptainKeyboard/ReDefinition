using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests.Atmospheres.D34
{
    // Package D34, the wind redistribution: pure, deterministic, follows the wind, and off at drift 0.
    [TestClass]
    public class CoverWindTests
    {
        private const double Radius = 600000.0;

        private static void Dir(double lat, double lon, out double x, out double y, out double z)
        {
            Geo.DirectionOf(lat * Math.PI / 180.0, lon * Math.PI / 180.0, out x, out y, out z);
        }

        private static double F(double lat, double lon, double we, double wn, double ut, double drift, double stretch = 4, bool ridged = false)
        {
            double x, y, z;
            Dir(lat, lon, out x, out y, out z);
            return CoverWind.Factor(x, y, z, we, wn, ut, drift, stretch, ridged, Radius);
        }

        [TestMethod]
        public void WithoutDriftTheFactorIsOneAndTheDepthIsUntouched()
        {
            for (double lon = 0; lon < 60; lon += 3.7)
                Assert.AreEqual(1.0, F(55, lon, 12, -3, 5000, 0.0), 0.0);
            Assert.AreEqual(0.4, CoverWind.Redistribute(0.4, 1.7, 0.0, 0.5, 0.5, 10, 0), 1e-12, "drift 0 returns the depth");
            Assert.AreEqual(1.0, F(55, 20, 5, 0, 0, double.NaN, 4), 0.0, "a bad drift is none");
        }

        [TestMethod]
        public void TheFactorIsDeterministicAndStaysInTheRangeOfTheDrift()
        {
            double min = 10, max = -10;
            for (double lat = -80; lat <= 80; lat += 9)
                for (double lon = -170; lon <= 170; lon += 11)
                {
                    double a = F(lat, lon, 8, 3, 123456, 0.6);
                    Assert.AreEqual(a, F(lat, lon, 8, 3, 123456, 0.6), 0.0, "the same inputs, the same value");
                    min = Math.Min(min, a);
                    max = Math.Max(max, a);
                }
            Assert.IsTrue(min >= 0.4 - 1e-9 && max <= 1.6 + 1e-9, "1 +- drift: " + min + " .. " + max);
            Assert.IsTrue(max - min > 0.2, "the pattern varies: " + min + " .. " + max);
        }

        [TestMethod]
        public void TheDriftShowsAPatternAndNoDriftShowsNone()
        {
            double spread0 = 0, spread1 = 0;
            for (double lon = 0; lon < 20; lon += 0.37)
            {
                spread0 = Math.Max(spread0, Math.Abs(F(55, lon, 10, 0, 0, 0.0) - 1.0));
                spread1 = Math.Max(spread1, Math.Abs(F(55, lon, 10, 0, 0, 1.0) - 1.0));
            }
            Assert.AreEqual(0.0, spread0, 0.0);
            Assert.IsTrue(spread1 > 0.2, "drift 1 gives a pattern: " + spread1);
        }

        [TestMethod]
        public void ThePatternMovesWithTheWindAndStaysPutInCalm()
        {
            // With a wind the pattern at a later time at a place downwind equals the pattern earlier at the place it came from.
            double ut = 10000.0;
            double travelM = 10.0 * CoverWind.AdvectionPerSecond * ut;
            double dLon = travelM / (Radius * Math.Cos(55 * Math.PI / 180.0)) * 180.0 / Math.PI;
            int equalHits = 0, total = 0;
            for (double lon = 0; lon < 5; lon += 0.31)
            {
                double earlier = F(55, lon, 10, 0, 0.0, 1.0, 1.0);
                double later = F(55, lon + dLon, 10, 0, ut, 1.0, 1.0);
                total++;
                if (Math.Abs(earlier - later) < 0.02) equalHits++;
            }
            Assert.IsTrue(equalHits >= total * 0.7, "the pattern is carried east: " + equalHits + " of " + total);
            // In calm the pattern does not depend on the time.
            Assert.AreEqual(F(55, 3, 0, 0, 0, 1.0), F(55, 3, 0, 0, 1.0e6, 1.0), 1e-12);
        }

        [TestMethod]
        public void SnowStretchesAlongTheWindAndDunesLieAcrossIt()
        {
            // Mean absolute change of the factor over a step along the wind and across it, for a stretch of 6.
            double along = Step(true, 6.0, false), across = Step(false, 6.0, false);
            Assert.IsTrue(along < across * 0.6, "snow drifts are long along the wind: along " + along + ", across " + across);
            double dAlong = Step(true, 6.0, true), dAcross = Step(false, 6.0, true);
            Assert.IsTrue(dAlong > dAcross * 1.5, "dune crests lie across the wind (the pattern changes fast along it): along " + dAlong + ", across " + dAcross);
        }

        private static double Step(bool alongWind, double stretch, bool ridged)
        {
            const double stepM = 400.0;
            double dDeg = stepM / Radius * 180.0 / Math.PI;
            double sum = 0;
            int n = 0;
            for (double lat = 40; lat < 70; lat += 1.3)
                for (double lon = 0; lon < 30; lon += 1.9)
                {
                    // Wind to the east: along the wind is east.
                    double a = F(lat, lon, 10, 0, 0, 1.0, stretch, ridged);
                    double b = alongWind ? F(lat, lon + dDeg / Math.Cos(lat * Math.PI / 180.0), 10, 0, 0, 1.0, stretch, ridged) : F(lat + dDeg, lon, 10, 0, 0, 1.0, stretch, ridged);
                    sum += Math.Abs(a - b);
                    n++;
                }
            return sum / n;
        }

        [TestMethod]
        public void TheLeeSlopeGetsMoreAndTheWindwardLess()
        {
            // The wind blows east; a normal that leans east is the lee face.
            double lee = CoverWind.Redistribute(0.4, 1.0, 1.0, 0.8, 0.0, 10, 0);
            double windward = CoverWind.Redistribute(0.4, 1.0, 1.0, -0.8, 0.0, 10, 0);
            double flat = CoverWind.Redistribute(0.4, 1.0, 1.0, 0.0, 0.0, 10, 0);
            Assert.AreEqual(0.4, flat, 1e-12);
            Assert.IsTrue(lee > flat && windward < flat, "lee " + lee + " windward " + windward);
            Assert.AreEqual(0.4, CoverWind.Redistribute(0.4, 1.0, 1.0, 0.8, 0.0, 0, 0), 1e-12, "no bias in calm");
            Assert.AreEqual(1.0, CoverWind.Redistribute(0.99, 2.0, 1.0, 0.8, 0.0, 10, 0), 1e-12, "the depth never exceeds the full depth");
            Assert.AreEqual(0.0, CoverWind.Redistribute(0.0, 2.0, 1.0, 0.8, 0.0, 10, 0), 1e-12, "bare ground stays bare");
        }

        [TestMethod]
        public void TheLocalFrameIsEastNorthUpAtAnyPlaceAndAtThePole()
        {
            for (double lat = -85; lat <= 85; lat += 17)
                for (double lon = -170; lon <= 170; lon += 43)
                {
                    double x, y, z, ex, ey, ez, nx, ny, nz;
                    Dir(lat, lon, out x, out y, out z);
                    CoverWind.LocalFrame(x, y, z, out ex, out ey, out ez, out nx, out ny, out nz);
                    Assert.AreEqual(0.0, ex * x + ey * y + ez * z, 1e-9, "east is tangent");
                    Assert.AreEqual(0.0, nx * x + ny * y + nz * z, 1e-9, "north is tangent");
                    Assert.AreEqual(0.0, ex * nx + ey * ny + ez * nz, 1e-9);
                    Assert.IsTrue(ny > 0, "north points to the pole");
                }
            double a, b, c, d, e, f;
            CoverWind.LocalFrame(0, 1, 0, out a, out b, out c, out d, out e, out f);
            Assert.AreEqual(1.0, Math.Sqrt(a * a + b * b + c * c), 1e-9, "a frame at the pole is still unit");
        }
    }
}
