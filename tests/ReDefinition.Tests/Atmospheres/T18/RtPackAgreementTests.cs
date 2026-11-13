using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests.Atmospheres.T18
{
    // The selection of the five places and the scan that the in-game probe (T18Probe) runs, against a test sky that has all five kinds of place: a broken low
    // layer, a high deck with an edge over part of it. The probe's rows (tests/series/rows/T18.json) are judged on the game's sky; here the code that picks
    // and measures is checked before a start of the game.
    [TestClass]
    public class RtPackAgreementTests
    {
        private static LatLonSource Sky()
        {
            return new LatLonSource(PackScenes.Kerbin)
                .Add("low", 1500, 3500, 1.6, PackScenes.Noise(7, 8))
                .Add("deck", 6500, 7500, 1.0, PackScenes.OnMap((lat, lon) => lat > -1.5 && lat < 2.5 && lon > -2.0 && lon < 3.0 ? 1.0 : 0.0));
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
                                  + p.Ray.ToString("0.000") + " diff " + (100 * p.Diff).ToString("0.00") + " % quick " + (100 * p.QuickDiff).ToString("0.00") + " %");
            Assert.AreEqual("", missing, "every kind of place exists in this sky");
            Assert.AreEqual(5, places.Count);
            foreach (RtAgreementPlace p in places)
            {
                Assert.IsTrue(p.Reference > 0.02 && p.Reference < 0.98, p.Name + ": the shadow is neither 0 nor 1");
                Assert.IsTrue(p.Diff <= 0.10, p.Name + ": " + p.Diff);
            }
            Assert.AreEqual(6.0, places[4].ElevationDeg, "the terminator place has the low sun");
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
            Console.WriteLine("scan 45: n " + scan.Places + " median " + (100 * scan.Median).ToString("0.00") + " % p90 " + (100 * scan.P90).ToString("0.00") + " % worst "
                              + (100 * scan.Worst).ToString("0.00") + " %");
            Assert.IsTrue(scan.Places > 50);
            Assert.IsTrue(scan.P90 <= 0.10, "p90 " + scan.P90);
            var clear = new LatLonSource(PackScenes.Kerbin).Add("low", 1500, 3500, 1.6, (lat, lon) => 0.0);
            RtAgreementScan none = RtPackAgreement.Scan(clear, clear.Tables(), PackScenes.Bake(clear, clear.Tables()), 0.0, 0.0, 2.0, 0.5, 45.0);
            Assert.AreEqual(0, none.Places);
        }
    }
}
