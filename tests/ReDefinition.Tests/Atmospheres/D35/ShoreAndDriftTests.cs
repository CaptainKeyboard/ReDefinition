using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests.Atmospheres.D35
{
    // Package D35, the coast map and the drift of the floes.
    [TestClass]
    public class ShoreAndDriftTests
    {
        private const float Radius = 600000f;

        // A planet with one continent: land in a box of longitudes and latitudes, sea elsewhere, a shelf that deepens with the distance.
        private static float[] Planet(Func<double, double, bool> land)
        {
            var heights = new float[ShoreGrid.Width * ShoreGrid.Height];
            for (int row = 0; row < ShoreGrid.Height; row++)
                for (int col = 0; col < ShoreGrid.Width; col++)
                {
                    double lat = ((row + 0.5) / ShoreGrid.Height - 0.5) * 180.0;
                    double lon = (col + 0.5) / ShoreGrid.Width * 360.0;
                    heights[row * ShoreGrid.Width + col] = land(lat, lon) ? 500f : -4000f;
                }
            return heights;
        }

        [TestMethod]
        public void TheDistanceOfASeaTexelToLandIsTheGreatCircleDistanceWithinTheChamferError()
        {
            // Land: longitudes 100 to 140 between 60 degrees south and 60 north: the distance at the equator is the arc to the nearest edge.
            float[] h = Planet((lat, lon) => lon >= 100.0 && lon < 140.0 && Math.Abs(lat) < 60.0);
            var sea = new bool[h.Length];
            for (int i = 0; i < h.Length; i++) sea[i] = h[i] <= 0f;
            float[] d = ShoreGrid.Distance(sea, Radius);
            int row = ShoreGrid.Height / 2;
            double metresPerDegree = Radius * Math.PI / 180.0;
            foreach (double lon in new[] { 80.0, 60.0, 160.0, 180.0 })
            {
                int col = (int)(lon / 360.0 * ShoreGrid.Width);
                double expectedDeg = lon < 100.0 ? 100.0 - lon : lon - 140.0;
                Assert.AreEqual(expectedDeg * metresPerDegree, d[row * ShoreGrid.Width + col], expectedDeg * metresPerDegree * 0.06 + 6000.0, "distance at longitude " + lon);
            }
            Assert.AreEqual(0f, d[row * ShoreGrid.Width + (int)(120.0 / 360.0 * ShoreGrid.Width)], "land is 0");
        }

        [TestMethod]
        public void ARockLessPlanetHasNoShoreTermFromTheCoastAndAFullSeaFlag()
        {
            float[] h = Planet((lat, lon) => false);
            byte[] map = ShoreGrid.Build(h, 0f, Radius);
            for (int i = 0; i < ShoreGrid.Width * ShoreGrid.Height; i += 997)
            {
                Assert.AreEqual(255, map[i * 4 + 2], "sea flag");
                Assert.AreEqual(0, map[i * 4], "no coast, deep: no shore term");
            }
        }

        [TestMethod]
        public void TheShoreTermFallsWithTheDistanceAndLandIsNotSea()
        {
            float[] h = Planet((lat, lon) => lon >= 100.0 && lon < 140.0);
            byte[] map = ShoreGrid.Build(h, 0f, Radius);
            int row = ShoreGrid.Height / 2;
            Func<double, float> shoreAt = lon => map[(row * ShoreGrid.Width + (int)(lon / 360.0 * ShoreGrid.Width)) * 4] / 255f;
            Assert.IsTrue(shoreAt(99.0) > shoreAt(97.0) && shoreAt(97.0) > shoreAt(94.0) && shoreAt(94.0) > shoreAt(90.0) && shoreAt(90.0) > shoreAt(80.0),
                "the shore term falls offshore");
            Assert.IsTrue(shoreAt(99.0) > 0.4f);
            Assert.AreEqual(0, map[(row * ShoreGrid.Width + (int)(120.0 / 360.0 * ShoreGrid.Width)) * 4 + 2], "land: sea flag 0");
            Assert.IsFalse(ShoreGrid.IsSeaAt(map, Math.Sin(120.0 * Math.PI / 180.0), 0.0, Math.Cos(120.0 * Math.PI / 180.0)), "the land direction is not sea");
            Assert.IsTrue(ShoreGrid.IsSeaAt(map, Math.Sin(30.0 * Math.PI / 180.0), 0.0, Math.Cos(30.0 * Math.PI / 180.0)), "the sea direction is sea");
        }

        [TestMethod]
        public void TheShoreTermMinusItsMeanHasZeroMeanOverTheFootprintOfAFieldTexel()
        {
            float[] h = Planet((lat, lon) => lon >= 100.0 && lon < 140.0);
            byte[] map = ShoreGrid.Build(h, 0f, Radius);
            // The field's texel is 4 x 4 texels of the map; around its centre, term minus mean averages to about 0.
            for (int col = 4; col < ShoreGrid.Width - 4; col += 61)
            {
                double sum = 0;
                int n = 0;
                int row = ShoreGrid.Height / 2;
                for (int dr = -2; dr < 2; dr++)
                    for (int dc = -2; dc < 2; dc++)
                    {
                        int i = ((row + dr) * ShoreGrid.Width + col + dc) * 4;
                        if (map[i + 2] == 0) continue;
                        sum += map[i] / 255.0 - map[(((row) * ShoreGrid.Width + col)) * 4 + 1] / 255.0;
                        n++;
                    }
                if (n > 0) Assert.AreEqual(0.0, sum / n, 0.12, "term minus mean at column " + col);
            }
        }

        [TestMethod]
        public void TheIceShareInTheCoastZoneKeepsTheFieldsFractionWithinTheBias()
        {
            // One continent at 100 to 140 degrees longitude, sea shelf around it; a fixed field fraction on the sea. The coast zone is
            // where the shore term moves the ice and where the clamps of IceMask.Evaluate bite, so this is the place the mean can drift.
            float[] h = Planet((lat, lon) => lon >= 100.0 && lon < 140.0 && Math.Abs(lat) < 70.0);
            for (int i = 0; i < h.Length; i++)
                if (h[i] < 0f) h[i] = (i % ShoreGrid.Width) % 64 < 32 ? -30f : -4000f;
            var input = new IceInputs { Shore = ShoreGrid.Build(h, 0f, Radius) };
            input.Params.RadiusM = Radius;
            double worst = 0;
            var report = new System.Text.StringBuilder();
            foreach (float f in new[] { 0.1f, 0.3f, 0.5f, 0.7f, 0.9f })
            {
                input.ForcedFraction = f;
                double ice, field;
                int n;
                IceOrbitPicture.CoastCoverage(input, -60.0, 60.0, 60.0, 180.0, 0.1, IceOrbitPicture.CoastZoneShore, out ice, out field, out n);
                Assert.IsTrue(n > 5000, "the coast zone holds " + n + " samples at fraction " + f);
                double error = ice - field;
                report.Append(" f=" + f + ":" + error.ToString("F4", System.Globalization.CultureInfo.InvariantCulture));
                worst = Math.Max(worst, Math.Abs(error));
            }
            Assert.IsTrue(worst <= CoastBiasLimit, "the coast-zone bias over the fractions is " + worst + " (limit " + CoastBiasLimit + ");" + report);
        }

        // The bias of the ice share against the field's fraction that is accepted in the coast zone: measured 0.002 to 0.004 on this
        // planet at the five fractions (sampling noise of this grid is about 0.005); the series row D35.coast.error_cpu allows 0.05
        // because the real field varies inside a window.
        private const double CoastBiasLimit = 0.02;

        [TestMethod]
        public void ShallowWaterFreezesBeforeDeepWater()
        {
            // No land at all, two depths: the shallow half has the higher term.
            var h = new float[ShoreGrid.Width * ShoreGrid.Height];
            for (int i = 0; i < h.Length; i++) h[i] = (i % ShoreGrid.Width) < ShoreGrid.Width / 2 ? -20f : -3000f;
            byte[] map = ShoreGrid.Build(h, 0f, Radius);
            int row = ShoreGrid.Height / 2;
            Assert.IsTrue(map[(row * ShoreGrid.Width + 100) * 4] > map[(row * ShoreGrid.Width + 800) * 4] + 50, "shallow against deep");
        }

        [TestMethod]
        public void TheMapSampleReadsBilinearAndWrapsInLongitude()
        {
            var map = new byte[ShoreGrid.Width * ShoreGrid.Height * 4];
            for (int row = 0; row < ShoreGrid.Height; row++)
            {
                map[(row * ShoreGrid.Width) * 4] = 200;
                map[(row * ShoreGrid.Width + ShoreGrid.Width - 1) * 4] = 100;
            }
            // At u = 0 the sample is the mean of the last and the first column.
            Assert.AreEqual(150.0 / 255.0, ShoreGrid.Sample(map, 0, 0.0, 0.5), 1e-4);
            Assert.AreEqual(0f, ShoreGrid.Sample(null, 0, 0.0, 0.5));
        }

        // ------------------------------------------------------------------------------------------------- the drift

        [TestMethod]
        public void TheDriftIsContinuousAndANoOpAcrossATimeJump()
        {
            var drift = new IceDrift();
            double ut = 1.0e7;
            Assert.IsFalse(drift.Advance(ut, 10, 0, 1, 0, 0, 0, 1, 0, 3000.0), "the first call only sets the clock");
            Assert.IsFalse(drift.Advance(ut + 30, 10, 0, 1, 0, 0, 0, 1, 0, 3000.0), "less than a game minute: nothing");
            Assert.IsTrue(drift.Advance(ut + 600, 10, 0, 1, 0, 0, 0, 1, 0, 3000.0));
            // 0.02 x 10 m/s x 600 s = 120 m east, in cells of 3 km, the domain moving the other way.
            Assert.AreEqual(-120.0 / 3000.0, drift.X, 1e-9);
            Assert.AreEqual(0.0, drift.Y, 1e-12);
            double x = drift.X;
            Assert.IsFalse(drift.Advance(ut + 600 + 30 * 86400.0, 10, 0, 1, 0, 0, 0, 1, 0, 3000.0), "a month of time warp does not move the pattern");
            Assert.AreEqual(x, drift.X, 1e-15);
            Assert.IsFalse(drift.Advance(ut, 10, 0, 1, 0, 0, 0, 1, 0, 3000.0), "a revert does not move it either");
            Assert.AreEqual(x, drift.X, 1e-15);
            Assert.IsTrue(drift.Advance(ut + 600, 0, 5, 1, 0, 0, 0, 0, 1, 3000.0), "it runs on from the new time");
            Assert.AreEqual(-60.0 / 3000.0, drift.Z, 1e-9);
        }

        [TestMethod]
        public void TheDriftIgnoresNotANumberAndAResetForgetsTheClock()
        {
            var drift = new IceDrift();
            drift.Advance(100.0, 1, 1, 1, 0, 0, 0, 1, 0, 1000.0);
            Assert.IsFalse(drift.Advance(1000.0, double.NaN, 1, 1, 0, 0, 0, 1, 0, 1000.0));
            Assert.IsFalse(drift.Advance(double.NaN, 1, 1, 1, 0, 0, 0, 1, 0, 1000.0));
            Assert.IsFalse(drift.Advance(2000.0, 1, 1, 1, 0, 0, 0, 1, 0, 0.0));
            Assert.AreEqual(0.0, drift.X);
            drift.Advance(5000.0, 4, 0, 1, 0, 0, 0, 1, 0, 1000.0);
            Assert.AreNotEqual(0.0, drift.X);
            drift.Reset();
            Assert.AreEqual(0.0, drift.X);
            Assert.IsTrue(double.IsNaN(drift.LastUt));
        }
    }
}
