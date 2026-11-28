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

        private static int At(double cov, double u, double v, double h, out CloudCells.CellRead read)
        {
            // The two steps of the readers: the field's read, then the body's reach at the point's height in the body (no outline curve: the height over the body's top).
            if (CloudField.Cloud(FieldData, Size, Mediocris, 1.0, 0.0, cov, Coverage, Edge, Amount, u, v, Rm, TypeHeightM, out read) == 0) return 0;
            return CloudField.Body(ref read, h / read.Top, TypeHeightM, Bulge, Band, Fine);
        }

        // Master plan 47.U stage 2: the four form numbers are keys of the type.
        [TestMethod]
        public void ATypeWithoutNumbersOfItsOwnHasItsClassesSlotAndOwnNumbersGetASlotOfTheirOwn()
        {
            int mediocris = CloudField.ClassOf("mediocris");
            Assert.AreEqual(mediocris, CloudField.SlotOf("mediocris", -1f, -1f, -1f, -1f));
            Assert.AreEqual(0, CloudField.SlotOf("none", -1f, -1f, -1f, -1f));
            int own = CloudField.SlotOf("mediocris", 1.7f, -1f, -1f, -1f);
            Assert.IsTrue(own >= CloudField.Classes.Length, "a slot after the classes'");
            Assert.AreEqual(own, CloudField.SlotOf("mediocris", 1.7f, -1f, -1f, -1f), "the same numbers, the same slot");
            float[] numbers = CloudField.Form(own), cls = CloudField.Classes[mediocris];
            Assert.AreEqual(1.7f, numbers[0]);
            Assert.AreEqual(cls[1], numbers[1]);
            Assert.AreEqual(cls[2], numbers[2]);
            Assert.AreEqual(cls[3], numbers[3]);
            // A type with no class and numbers of its own has a form (the none class's numbers where it sets none).
            int bare = CloudField.SlotOf("none", 3f, 0.1f, -1f, -1f);
            Assert.IsTrue(bare > 0);
            Assert.AreEqual(1f, CloudField.Form(bare)[2]);
        }

        [TestMethod]
        public void ANumberOfZeroIsNotATypesOwn()
        {
            // An aspect, a scale or a stretch of 0 would divide by zero in the lookup: the class's own stands. A top gain of 0 is a number (no lift of the top).
            Assert.AreEqual(CloudField.ClassOf("humilis"), CloudField.SlotOf("humilis", 0f, -1f, 0f, 0f));
            int flat = CloudField.SlotOf("humilis", -1f, 0f, -1f, -1f);
            Assert.IsTrue(flat >= CloudField.Classes.Length);
            Assert.AreEqual(0f, CloudField.Form(flat)[1]);
        }

        // Master plan 47.U stage 3: the outline of a type with a form, in the type table as the shaders read it.
        [TestMethod]
        public void TheTypeTableCarriesTheNearerTypesFormAndItsOutline()
        {
            CloudTypeDef dome = CloudTypeDef.Slab("dome", 0.1f, 0.9f), deck = CloudTypeDef.Slab("deck", 0.1f, 0.9f), bare = CloudTypeDef.Slab("bare", 0.1f, 0.9f);
            dome.Form = CloudField.ClassOf("mediocris");
            deck.Form = CloudField.ClassOf("stratocumulus");
            // The whole footprint up to 0.8 of the height, closed at the top.
            deck.Outline = new Curve(new[] { 0f, 0.8f, 1f }, new[] { 1f, 1f, 0f });
            // An outline without a form draws nothing: the type keeps its lift.
            bare.Outline = new Curve(new[] { 0f, 1f }, new[] { 1f, 1f });
            var table = new LayerTypeTable(new CloudLayerDef("l", 1, 1000f, 3000f, new[] { dome, deck, bare }, 0.001f));
            int near;
            CollectionAssert.AreEqual(CloudField.Classes[dome.Form], table.FormAtTypeValue(0.2, out near));
            Assert.AreEqual(0, near);
            CollectionAssert.AreEqual(CloudField.Classes[deck.Form], table.FormAtTypeValue(0.3, out near));
            Assert.AreEqual(1, near);
            Assert.IsNull(table.FormAtTypeValue(0.9, out near));
            Assert.AreEqual(2, near);
            // The shaders' info value: the slot, plus a half where the type has a form and an outline.
            Assert.AreEqual((float)dome.Form, table.FormInfoOfType(0));
            Assert.AreEqual(deck.Form + 0.5f, table.FormInfoOfType(1));
            Assert.AreEqual(0f, table.FormInfoOfType(2));
            // The point's height in the body. No outline curve: the height over the body's top itself, above the top too.
            Assert.AreEqual(0.4, table.FormHeight(0, 0.4), 1e-12);
            Assert.AreEqual(1.3, table.FormHeight(0, 1.3), 1e-12);
            // The deck: the whole footprint is the half ellipsoid's base (0) up to 0.8; at 0.9 the width is 0.5, the half ellipsoid's at sqrt(0.75); closed at the top (1), and
            // the height runs on above it.
            Assert.AreEqual(0.0, table.FormHeight(1, 0.5), 1e-6);
            Assert.AreEqual(Math.Sqrt(0.75), table.FormHeight(1, 0.9), 0.02);
            Assert.AreEqual(1.0, table.FormHeight(1, 1.0), 1e-6);
            Assert.AreEqual(1.25, table.FormHeight(1, 1.25), 1e-6);
            // The baked channel: the outline's equivalent height for the deck, the lift for the others.
            Assert.AreEqual(0.0, deck.BakedLiftAt(0.5), 1e-6);
            Assert.AreEqual(1.0, deck.BakedLiftAt(1.0), 1e-6);
            Assert.AreEqual(bare.LiftAt(0.5), bare.BakedLiftAt(0.5), 1e-12);
        }

        [TestMethod]
        public void TheShareOfPlacesInsideAFootprintIsTheMapsShare()
        {
            // The field is rank-uniform: a point lies in a cloud's footprint (the field below the share: a body's horizontal coordinate below 1) with the probability f.
            // Every share, the nearly closed layer too (master plan 47.U stage 2: no hand-over to the map's value above 0.8).
            foreach (double f in new[] { 0.2, 0.5, 0.7, 0.9, 0.97 })
            {
                double cov = CovOf(f);
                var rng = new Random(11);
                int inside = 0, n = 20000;
                for (int i = 0; i < n; i++)
                {
                    CloudCells.CellRead read;
                    // Places within 10 degrees of the equator, over a span far wider than the field's tile.
                    int c = At(cov, rng.NextDouble(), 0.5 + (rng.NextDouble() - 0.5) * 0.1, 0.0, out read);
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
                double u = rng.NextDouble(), v = 0.5 + (rng.NextDouble() - 0.5) * 0.1;
                CloudCells.CellRead read;
                if (At(cov, u, v, 0.0, out read) <= 0) continue;
                if (read.XBest / CloudCells.EdgeX > 0.5) continue;   // well inside the footprint
                bodies++;
                Assert.IsTrue(read.Body, "a body's read is a body");
                Assert.IsTrue(CloudCells.CellBody(read, 0.5, 0.0, 0.0, TypeHeightM, Bulge, Band, Fine) > 0.99, "the base inside the footprint is cloud");
                // Three times the type's height above the base no body reaches (its top is at most the type's height, the bulges' swing at most half the shorter semi-axis).
                CloudCells.CellRead r2;
                if (At(cov, u, v, 3.0, out r2) == 0) above++;
            }
            Assert.IsTrue(bodies > 100, "bodies found: " + bodies);
            Assert.AreEqual(bodies, above, "no body three type heights up");
        }

        [TestMethod]
        public void TheClosedLayerIsTheFieldAtAShareOfOneAndTheMapsSurplusMergesItsClouds()
        {
            // The share is 1 from the map value 1 - coverage + half the edge + half the noise's swing on. There every place is inside a footprint, but the field's highest
            // places lie right at an edge (a pit). A map above that value carries its surplus on: the distance to the edge grows by surplus / gradient, the same at every
            // place, so the pits close. A map with no share at all draws nothing, whatever the field holds.
            double full = 1.0 - Coverage + 0.5 * (Edge + Amount);
            var rng = new Random(3);
            int n = 4000, nearEdgeAtOne = 0, nearEdgeWithSurplus = 0;
            double sumAtOne = 0.0, sumWithSurplus = 0.0;
            for (int i = 0; i < n; i++)
            {
                double u = rng.NextDouble(), v = 0.5 + (rng.NextDouble() - 0.5) * 0.1;
                CloudCells.CellRead one, more, none;
                Assert.AreEqual(1, At(full, u, v, 0.0, out one), "a share of 1: a body everywhere");
                Assert.AreEqual(1, At(full + 0.3, u, v, 0.0, out more));
                Assert.AreEqual(0, At(1.0 - Coverage - 0.5 * (Edge + Amount) - 0.01, u, v, 0.0, out none), "no share: no cloud");
                Assert.IsFalse(none.Body);
                double xOne = one.XBest / CloudCells.EdgeX, xMore = more.XBest / CloudCells.EdgeX;   // 0 the body's middle, 1 its edge
                Assert.IsTrue(xOne <= 1.0 + 1e-9 && xMore <= xOne + 1e-9, "the surplus moves no place outward: " + xOne + " -> " + xMore);
                sumAtOne += xOne;
                sumWithSurplus += xMore;
                if (xOne > 0.8) nearEdgeAtOne++;
                if (xMore > 0.8) nearEdgeWithSurplus++;
            }
            // The field's gradient is not bounded, so a steep place can stay near an edge: the count falls to a small part, it is not asserted to be none.
            Assert.IsTrue(nearEdgeAtOne > 20, "at a share of exactly 1 some places still lie at an edge: " + nearEdgeAtOne);
            Assert.IsTrue(nearEdgeWithSurplus * 20 <= nearEdgeAtOne, "a map 0.3 above the full share leaves at most a twentieth of them: " + nearEdgeAtOne + " -> " + nearEdgeWithSurplus);
            Assert.IsTrue(sumWithSurplus < 0.5 * sumAtOne, "the mean distance from the middle falls: " + sumAtOne / n + " -> " + sumWithSurplus / n);
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
            double u, v, a = windDeg * Math.PI / 180.0;
            Place(xM, yM, out u, out v);
            CloudCells.CellRead read;
            int cloud = CloudField.Cloud(FieldData, Size, cls, Math.Cos(a), Math.Sin(a), CovOf(0.5), Coverage, Edge, Amount, u, v, Rm, TypeHeightM, out read);
            if (cloud != 0) cloud = CloudField.Body(ref read, 0.0, TypeHeightM, Bulge, 1e6, Fine);
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
