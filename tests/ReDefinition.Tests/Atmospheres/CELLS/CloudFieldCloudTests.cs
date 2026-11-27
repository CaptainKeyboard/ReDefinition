using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests.Atmospheres.CELLS
{
    // Master plan 47.U stage 1: the CPU twin of the shader's FieldCloud (CloudField.Cloud), so that the CPU readers of the clouds' place (the cloud shadow's field: the sun's dimming)
    // stand a cloud where the picture draws it. The shader side is judged by the weather player's scene cloud-shadow -shadownoise 3 (the series u1-field).
    [TestClass]
    public class CloudFieldCloudTests
    {
        private const int Size = 256;
        private const double Rm = 604000.0, Coverage = 0.35, Edge = 0.15, Amount = 1.0, TypeHeightM = 1200.0, Bulge = 0.5, Band = 10.0, Fine = 1.0;
        private static readonly float[] FieldData = CloudField.Build(Size);
        private static readonly float[] Mediocris = CloudField.Classes[CloudField.ClassOf("mediocris")];

        // The map value whose cover share (CloudCells.Share) is f, by bisection (the share rises with the map value).
        private static double CovOf(double f)
        {
            double lo = 0.0, hi = 2.0;
            for (int i = 0; i < 60; i++)
            {
                double mid = 0.5 * (lo + hi);
                if (CloudCells.Share(mid, Coverage, Edge, Amount) < f) lo = mid; else hi = mid;
            }
            return 0.5 * (lo + hi);
        }

        private static int At(double cov, double u, double v, double h, out double covOut, out CloudCells.CellRead read)
        {
            return CloudField.Cloud(FieldData, Size, Mediocris, 1.0, 0.0, cov, Coverage, Edge, Amount, u, v, Rm, h, TypeHeightM, Bulge, Band, Fine, out covOut, out read);
        }

        [TestMethod]
        public void TheShareOfPlacesInsideAFootprintIsTheMapsShare()
        {
            // The field is rank-uniform: a point lies in a cloud's footprint (the field below the share: a body's horizontal coordinate below 1) with the probability f.
            foreach (double f in new[] { 0.2, 0.5, 0.7 })
            {
                double cov = CovOf(f);
                var rng = new Random(11);
                int inside = 0, n = 20000;
                for (int i = 0; i < n; i++)
                {
                    double covOut;
                    CloudCells.CellRead read;
                    // Places within 10 degrees of the equator, over a span far wider than the field's tile.
                    int c = At(cov, rng.NextDouble(), 0.5 + (rng.NextDouble() - 0.5) * 0.1, 0.0, out covOut, out read);
                    if (c > 0 && read.XBest / CloudCells.EdgeX < 1.0) inside++;
                }
                Assert.AreEqual(f, inside / (double)n, 0.03, "share " + f);
            }
        }

        [TestMethod]
        public void ABodyIsReadInsideItsFootprintAndNothingHighAboveItsTop()
        {
            double cov = CovOf(0.5);
            var rng = new Random(5);
            int bodies = 0, above = 0;
            for (int i = 0; i < 2000; i++)
            {
                double u = rng.NextDouble(), v = 0.5 + (rng.NextDouble() - 0.5) * 0.1, covOut;
                CloudCells.CellRead read;
                if (At(cov, u, v, 0.0, out covOut, out read) <= 0) continue;
                if (read.XBest / CloudCells.EdgeX > 0.5) continue;   // well inside the footprint
                bodies++;
                Assert.IsTrue(read.Body, "a body's read is a body");
                Assert.IsTrue(CloudCells.CellBody(read, 0.0, 0.5, 0.0, 0.0, TypeHeightM, Bulge, Band, Fine) > 0.99, "the base inside the footprint is cloud");
                // Three times the type's height above the base no body reaches (its top is at most the type's height, the bulges' swing at most half the shorter semi-axis).
                double c2;
                CloudCells.CellRead r2;
                if (At(cov, u, v, 3.0, out c2, out r2) == 0) above++;
            }
            Assert.IsTrue(bodies > 100, "bodies found: " + bodies);
            Assert.AreEqual(bodies, above, "no body three type heights up");
        }

        [TestMethod]
        public void FromACoverShareOfFourFifthsTheMapDrawsAValueThatStartsAtTheFootprintsProfileAndEndsAtTheMapsOwn()
        {
            double below = CovOf(0.799), just = CovOf(0.8001), third = CovOf(0.85), full = CovOf(0.97);
            var rng = new Random(3);
            double minJust = double.MaxValue, minThird = double.MaxValue, maxThird = double.MinValue;
            for (int i = 0; i < 4000; i++)
            {
                double u = rng.NextDouble(), v = 0.5 + (rng.NextDouble() - 0.5) * 0.1, covOut;
                CloudCells.CellRead read;
                Assert.AreNotEqual(-1, At(below, u, v, 0.2, out covOut, out read), "below 0.8 the field draws");
                Assert.AreEqual(below, covOut, 0.0, "the map value is untouched where the field draws");
                Assert.AreEqual(-1, At(just, u, v, 0.2, out covOut, out read), "from 0.8 the map draws");
                Assert.IsFalse(read.Body, "the map's read is no body");
                minJust = Math.Min(minJust, covOut);
                Assert.AreEqual(-1, At(third, u, v, 0.2, out covOut, out read));
                minThird = Math.Min(minThird, covOut);
                maxThird = Math.Max(maxThird, covOut);
                Assert.AreEqual(-1, At(full, u, v, 0.2, out covOut, out read));
                Assert.AreEqual(full, covOut, 1e-12, "from 0.95 the map's own value");
            }
            // The value handed on is the footprint's profile blended to the map's own by the weight (share - 0.8) / 0.15. Far outside every footprint the profile is half a
            // unit below the threshold, 1 - coverage - 0.5: right above 0.8 the value there is that number (NOT the map's own, about 0.95), a third of the way it is a third
            // of the way to the map's value. An implementation that hands the map's value on unblended fails both.
            double outside = 1.0 - Coverage - 0.5;
            Assert.AreEqual(outside + (just - outside) * (0.0001 / 0.15), minJust, 1e-9, "the value far outside a footprint right above 0.8");
            Assert.AreEqual(outside + (third - outside) / 3.0, minThird, 1e-9, "the value far outside a footprint at a share of 0.85");
            // Inside a footprint the profile lies above the threshold by at most the core strength (0.5 times 1 + the class's top gain).
            Assert.IsTrue(maxThird > 1.0 - Coverage + 0.2 && maxThird <= 1.0 - Coverage + 0.5 * (1.0 + Mediocris[1]) + (third - (1.0 - Coverage)) / 3.0 + 1e-9, "the value deep inside: " + maxThird);
        }

        // The map coordinate of a place given in metres east and north of the map's origin on the layer's sphere.
        private static void Place(double xM, double yM, out double u, out double v)
        {
            double lat = yM / Rm;
            v = 0.5 - lat / Math.PI;
            u = xM / (2.0 * Math.PI * Rm * Math.Cos(lat));
        }

        private static CloudCells.CellRead ReadAt(float[] cls, double windDeg, double xM, double yM)
        {
            double u, v, covOut, a = windDeg * Math.PI / 180.0;
            Place(xM, yM, out u, out v);
            CloudCells.CellRead read;
            int cloud = CloudField.Cloud(FieldData, Size, cls, Math.Cos(a), Math.Sin(a), CovOf(0.5), Coverage, Edge, Amount, u, v, Rm, 0.0, TypeHeightM, Bulge, 1e6, Fine, out covOut, out read);
            Assert.AreEqual(1, cloud, "with a band this wide every place is within a body's reach");
            return read;
        }

        [TestMethod]
        public void TheStreetDirectionTheStretchAndTheScaleMoveTheFieldsLookupAsTheShaderDoes()
        {
            float[] plain = { 1f, 0f, 1f, 1f }, streets = { 1f, 0f, 1f, 4f }, fine = { 1f, 0f, 2f, 1f };
            const double wind = 37.0;
            double c = Math.Cos(wind * Math.PI / 180.0), s = Math.Sin(wind * Math.PI / 180.0);
            var rng = new Random(9);
            int compared = 0;
            for (int i = 0; i < 300; i++)
            {
                double along = (rng.NextDouble() - 0.5) * 80000.0, across = (rng.NextDouble() - 0.5) * 30000.0 + 20000.0;
                double x = along * c - across * s, y = along * s + across * c;
                // Without a stretch the street direction changes nothing (the lookup is turned into the wind's frame and back).
                CloudCells.CellRead a = ReadAt(plain, wind, x, y), b = ReadAt(plain, 0.0, x, y);
                Assert.AreEqual(b.XBest, a.XBest, 1e-7, "no stretch, place " + i);
                Assert.AreEqual(b.Core, a.Core, 1e-9);
                // With a stretch the field is read at 1 / stretch of the distance ALONG the wind, the distance across unchanged.
                a = ReadAt(streets, wind, x, y);
                b = ReadAt(plain, 0.0, along / 4.0 * c - across * s, along / 4.0 * s + across * c);
                Assert.AreEqual(b.XBest, a.XBest, 1e-7, "stretch 4 along " + wind + " degrees, place " + i);
                Assert.AreEqual(b.Core, a.Core, 1e-9);
                // A class scale z reads the field z times as fine: the place p reads what the scale 1 reads at z p, and the distance to the footprint's edge is 1 / z of it.
                a = ReadAt(fine, 0.0, x, y);
                b = ReadAt(plain, 0.0, 2.0 * x, 2.0 * y);
                Assert.AreEqual(b.Core, a.Core, 1e-9, "scale 2, place " + i);
                double da = 1.0 - a.XBest / CloudCells.EdgeX, db = 1.0 - b.XBest / CloudCells.EdgeX;   // the distance over the body's horizontal semi-axis
                if (Math.Abs(db) < 0.9 && Math.Abs(da) < 0.45)
                {
                    Assert.AreEqual(db, 2.0 * da, 1e-7, "scale 2: the edge's distance, place " + i);
                    compared++;
                }
            }
            Assert.IsTrue(compared > 20, "places near a footprint's edge: " + compared);
        }

        [TestMethod]
        public void TheCoreStrengthFollowsTheMapsShareAndTheCoarseFieldWithTheClasssGain()
        {
            // The core strength (the body's height as a share of the type's): clamp(0.5 (cov + coverage - 1) + 0.35, 0.15, 0.5), times 1 + gain (1 - 2 b) with the coarse
            // channel b at the place (low in a cluster's core: taller there) and the class's top gain.
            float[] gained = { 1f, 0.3f, 1f, 1f };
            double cov = CovOf(0.5), flat = Math.Min(0.5, Math.Max(0.15, 0.5 * (cov + Coverage - 1.0) + 0.35));
            var rng = new Random(11);
            double lo = double.MaxValue, hi = double.MinValue;
            for (int i = 0; i < 300; i++)
            {
                double x = (rng.NextDouble() - 0.5) * 80000.0, y = (rng.NextDouble() - 0.5) * 30000.0;
                double coarse = CloudField.Sample(FieldData, Size, 2, x, y, 1.0);
                double want = flat * (1.0 + gained[1] * (1.0 - 2.0 * coarse));
                Assert.AreEqual(want, ReadAt(gained, 0.0, x, y).Core, 1e-9, "place " + i);
                lo = Math.Min(lo, want);
                hi = Math.Max(hi, want);
            }
            Assert.IsTrue(hi - lo > 0.1 * flat, "the coarse field moves the core strength over the places: " + lo + " to " + hi);
        }
    }
}
