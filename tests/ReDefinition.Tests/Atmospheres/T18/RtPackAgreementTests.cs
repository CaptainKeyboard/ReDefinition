using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests.Atmospheres.T18
{
    // The selection of the five places and the scan that the in-game probe (T18Probe) runs, against a test sky that has all five kinds of place: a broken low
    // layer, a high cloud layer with an edge over part of it. The probe's rows (tests/series/rows/T18.json) are judged on the game's sky; here the code that picks
    // and measures is checked before a start of the game.
    [TestClass]
    public class RtPackAgreementTests
    {
        private static LatLonSource Sky()
        {
            return new LatLonSource(PackScenes.Kerbin)
                .Add("low", 1500, 3500, 1.6, PackScenes.Noise(7, 8))
                .Add("high", 6500, 7500, 1.0, PackScenes.OnMap((lat, lon) => lat > -1.5 && lat < 2.5 && lon > -2.0 && lon < 3.0 ? 1.0 : 0.0));
        }

        [TestMethod]
        public void AllFiveKindsOfPlaceAreFoundAndAgree()
        {
            LatLonSource s = Sky();
            RtCloudPack pack = PackScenes.Bake(s, s.Tables());
            string missing;
            List<RtAgreementPlace> places = RtPackAgreement.Pick(s, s.Tables(), pack, 0.0, 0.0, 4.0, 0.2, 45.0, out missing);
            Console.WriteLine("missing: '" + missing + "'");
            foreach (RtAgreementPlace p in places)
                Console.WriteLine(p.Name + " lat " + p.LatDeg.ToString("0.0") + " lon " + p.LonDeg.ToString("0.0") + " ref " + p.Reference.ToString("0.000") + " ray "
                                  + p.Ray.ToString("0.000") + " |dT| " + p.Diff.ToString("0.000") + " quick " + p.QuickDiff.ToString("0.000"));
            Assert.AreEqual("", missing, "every kind of place exists in this sky");
            Assert.AreEqual(5, places.Count);
            foreach (RtAgreementPlace p in places)
            {
                Assert.IsTrue(p.Reference > 0.02 && p.Reference < 0.98, p.Name + ": the shadow is neither 0 nor 1");
                Assert.IsTrue(p.Diff <= 0.05, p.Name + ": |dT| " + p.Diff);
            }
            Assert.AreEqual(6.0, places[4].ElevationDeg, "the terminator place has the low sun");
        }

        // A pack that is old by the wind's drift only is the fresh pack moved: taking the drift out must make the two agree, and the drift alone must account for the
        // difference between them (the probe's age rows, T18.scan45_stale_*).
        [TestMethod]
        public void AStandingPackThatIsOldByTheWindOnlyAgreesOnceTheDriftIsTakenOut()
        {
            const double windEast = 100.0, ageS = 30.0;
            double dLat, dLon;
            RtPackAgreement.DriftDegrees(PackScenes.Kerbin, 0.0, windEast, 0.0, ageS, out dLat, out dLon);
            Assert.AreEqual(0.0, dLat, 1e-12);
            Assert.AreEqual(windEast * ageS / PackScenes.Kerbin * 180.0 / Math.PI, dLon, 1e-9);
            var noise = PackScenes.Noise(7, 4);
            var old = new LatLonSource(PackScenes.Kerbin).Add("low", 1500, 3500, 1.6, noise);
            // The pattern has moved east by the drift since: the fresh sky at a place is the old sky at the place the drift west of it.
            var now = new LatLonSource(PackScenes.Kerbin).Add("low", 1500, 3500, 1.6, (lat, lon) => noise(lat, lon - dLon));
            RtCloudPack standing = PackScenes.Bake(old, old.Tables()), fresh = PackScenes.Bake(now, now.Tables());
            double raw, advected, expected;
            int places;
            RtPackAgreement.StaleScan(standing, fresh, 0.0, 0.0, 4.0, 0.2, 45.0, windEast, 0.0, ageS, out raw, out advected, out expected, out places);
            Console.WriteLine("places " + places + " raw " + raw.ToString("0.000") + " advected " + advected.ToString("0.000") + " expected " + expected.ToString("0.000"));
            Assert.IsTrue(places > 100, "places " + places);
            Assert.IsTrue(raw > 0.04, "the drift is a difference to explain (" + raw + ")");
            // Not exactly 0: the two packs are baked on the texel lattice and the drift is a fraction of a texel, so the bilinear reads differ by a little.
            Assert.IsTrue(advected < 0.06 && advected < raw / 5.0, "with the drift taken out the packs agree (" + advected + " against " + raw + ")");
            Assert.IsTrue(Math.Abs(expected - raw) < 0.03, "the drift alone is what the age cost (expected " + expected + ", raw " + raw + ")");
        }

        [TestMethod]
        public void AClearSkyHasNoPlaceAndSaysWhichKindsAreMissing()
        {
            var s = new LatLonSource(PackScenes.Kerbin).Add("low", 1500, 3500, 1.6, (lat, lon) => 0.0);
            RtCloudPack pack = PackScenes.Bake(s, s.Tables());
            string missing;
            List<RtAgreementPlace> places = RtPackAgreement.Pick(s, s.Tables(), pack, 0.0, 0.0, 2.0, 0.5, 45.0, out missing);
            Assert.AreEqual(0, places.Count);
            Assert.AreEqual("p1,p2,p3,p4,p5", missing);
        }

        [TestMethod]
        public void TheScanMeasuresTheLatticeAndAnEmptySkyHasNoPlaces()
        {
            LatLonSource s = Sky();
            RtCloudPack pack = PackScenes.Bake(s, s.Tables());
            RtAgreementScan scan = RtPackAgreement.Scan(s, s.Tables(), pack, 0.0, 0.0, 4.0, 0.2, 45.0);
            Console.WriteLine("scan 45: n " + scan.Places + " |dT| median " + scan.Median.ToString("0.0000") + " p90 " + scan.P90.ToString("0.0000") + " worst "
                              + scan.Worst.ToString("0.0000") + " tau ratio " + scan.TauRatioMedian.ToString("0.000"));
            Assert.IsTrue(scan.Places > 50);
            Assert.IsTrue(scan.P90 <= 0.03, "p90 |dT| " + scan.P90);
            Assert.IsTrue(Math.Abs(scan.TauRatioMedian - 1.0) <= 0.05, "tau ratio " + scan.TauRatioMedian);
            var clear = new LatLonSource(PackScenes.Kerbin).Add("low", 1500, 3500, 1.6, (lat, lon) => 0.0);
            RtAgreementScan none = RtPackAgreement.Scan(clear, clear.Tables(), PackScenes.Bake(clear, clear.Tables()), 0.0, 0.0, 2.0, 0.5, 45.0);
            Assert.AreEqual(0, none.Places);
        }
    }
}
