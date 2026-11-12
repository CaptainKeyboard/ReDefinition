using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests
{
    // The schedule of the lightning (package T07): deterministic from the seed and the time, monotone in the level and the strength, gated by
    // the warp rate and the cover, the lattice stable under a change of the scan's centre, and the photosensitivity limit.
    [TestClass]
    public class LightningScheduleTests
    {
        private const double R = 600000.0;

        private sealed class FixedSource : ICellSource
        {
            internal CellSample Value = new CellSample { Strength = 1f, Level01 = 1f, TopM = 9000f, Coverage = 0.9f };
            internal double CentreLat, CentreLon, RadiusRad = 0.03;

            public bool Sample(double latRad, double lonRad, double ut, out CellSample sample)
            {
                sample = Value;
                double d = Math.Sqrt((latRad - CentreLat) * (latRad - CentreLat) + (lonRad - CentreLon) * (lonRad - CentreLon) * Math.Cos(latRad) * Math.Cos(latRad));
                return d < RadiusRad;
            }
        }

        private static List<LightningCell> Scan(LightningParams p, ICellSource source, double latDeg, double lonDeg, double rangeM)
        {
            var scanner = new LightningScanner(p);
            scanner.Begin(source, R, latDeg, lonDeg, rangeM, 1000.0);
            while (!scanner.Step(8)) { }
            return scanner.Result;
        }

        private static List<FlashEvent> Run(LightningParams p, uint seed, IList<LightningCell> cells, long from, long to)
        {
            var events = new List<FlashEvent>();
            for (long slot = from; slot < to; slot++) LightningSchedule.EventsInSlot(p, seed, slot, cells, events);
            return events;
        }

        private static List<LightningCell> Cells(float strength, float level)
        {
            var p = new LightningParams();
            var src = new FixedSource { Value = new CellSample { Strength = strength, Level01 = level, TopM = 9000f, Coverage = 0.9f } };
            return Scan(p, src, 0.0, 0.0, 30000.0);
        }

        [TestMethod]
        public void TheSameSeedAndSlotsGiveTheSameEventsAndAnotherSeedGivesOthers()
        {
            var p = new LightningParams();
            var cells = Cells(1f, 1f);
            var a = Run(p, 7u, cells, 5000, 6800);
            var b = Run(p, 7u, cells, 5000, 6800);
            var c = Run(p, 8u, cells, 5000, 6800);
            Assert.IsTrue(a.Count > 20, "events " + a.Count);
            Assert.AreEqual(a.Count, b.Count);
            for (int i = 0; i < a.Count; i++)
            {
                Assert.AreEqual(a[i].Ut, b[i].Ut, 0.0);
                Assert.AreEqual(a[i].LatDeg, b[i].LatDeg, 0.0);
                Assert.AreEqual(a[i].Seed, b[i].Seed);
                Assert.AreEqual(a[i].Kind, b[i].Kind);
            }
            Assert.IsFalse(a.Select(e => e.Ut).SequenceEqual(c.Select(e => e.Ut)), "another seed gave the same times");
        }

        [TestMethod]
        public void TheEventsOfASlotDoNotDependOnTheOrderTheSlotsAreEvaluatedIn()
        {
            var p = new LightningParams();
            var cells = Cells(1f, 1f);
            var forward = Run(p, 3u, cells, 100, 700);
            var backwards = new List<FlashEvent>();
            for (long slot = 699; slot >= 100; slot--) LightningSchedule.EventsInSlot(p, 3u, slot, cells, backwards);
            var key = new Func<FlashEvent, string>(e => e.Slot + ":" + e.Row + ":" + e.Col);
            CollectionAssert.AreEquivalent(forward.Select(key).ToList(), backwards.Select(key).ToList());
        }

        [TestMethod]
        public void ARiseInTheLevelAddsFlashesAndRemovesNone()
        {
            var p = new LightningParams();
            var seen = new HashSet<string>();
            int previous = 0;
            foreach (float level in new[] { 0.1f, 0.2f, 0.35f, 0.5f, 0.7f, 0.85f, 1f })
            {
                var events = Run(p, 11u, Cells(0.8f, level), 0, 3600);
                var keys = new HashSet<string>(events.Select(e => e.Slot + ":" + e.Row + ":" + e.Col));
                Assert.IsTrue(keys.Count >= previous, "level " + level + " has " + keys.Count + " after " + previous);
                foreach (string k in seen) Assert.IsTrue(keys.Contains(k), "the flash " + k + " vanished at level " + level);
                seen = keys;
                previous = keys.Count;
            }
            Assert.IsTrue(previous > 100, "the top level flashed " + previous + " times in an hour");
        }

        [TestMethod]
        public void ARiseInTheStrengthAddsFlashesAndRemovesNone()
        {
            var p = new LightningParams();
            HashSet<string> seen = new HashSet<string>();
            foreach (float strength in new[] { 0.15f, 0.3f, 0.5f, 0.75f, 1f })
            {
                var events = Run(p, 5u, Cells(strength, 0.9f), 0, 1800);
                var keys = new HashSet<string>(events.Select(e => e.Slot + ":" + e.Row + ":" + e.Col));
                foreach (string k in seen) Assert.IsTrue(keys.Contains(k), "the flash " + k + " vanished at strength " + strength);
                seen = keys;
            }
        }

        [TestMethod]
        public void NoRainAndNoStrengthMeanNoFlashes()
        {
            var p = new LightningParams();
            Assert.AreEqual(0.0, LightningSchedule.DensityPerKm2Min(p, 1f, 0f));
            Assert.AreEqual(0.0, LightningSchedule.DensityPerKm2Min(p, 1f, 0.04f));
            Assert.AreEqual(0.0, LightningSchedule.DensityPerKm2Min(p, 0.05f, 1f));
            Assert.AreEqual(0, Run(p, 1u, Cells(1f, 0f), 0, 3600).Count);
        }

        [TestMethod]
        public void TheMeasuredRateMatchesTheStatedDensity()
        {
            var p = new LightningParams();
            var cells = Cells(1f, 1f);
            double expectedPerMin = 0.0;
            for (int i = 0; i < cells.Count; i++)
            {
                var c = cells[i];
                expectedPerMin += LightningSchedule.FlashesPerMinute(p, ref c);
            }
            var events = Run(p, 21u, cells, 0, 6 * 3600);
            double measured = events.Count / (6.0 * 60.0);
            Assert.IsTrue(expectedPerMin > 5.0 && expectedPerMin < 60.0, "expected " + expectedPerMin + " a minute from " + cells.Count + " squares");
            Assert.AreEqual(expectedPerMin, measured, expectedPerMin * 0.06);
        }

        [TestMethod]
        public void ARealStormFlashesAboutAsOftenAsReportedForThunderstorms()
        {
            // A severe cell: 5 to 30 flashes a minute within a cell of some 15 km radius (the numbers behind LightningParams.MaxDensityPerKm2Min).
            var p = new LightningParams();
            var cells = Cells(1f, 1f);
            double perMin = 0.0;
            for (int i = 0; i < cells.Count; i++) { var c = cells[i]; perMin += LightningSchedule.FlashesPerMinute(p, ref c); }
            Assert.IsTrue(perMin >= 5.0 && perMin <= 30.0, "flashes a minute " + perMin);
            // A weak cell with light rain flashes much less.
            var weak = Cells(0.3f, 0.3f);
            double weakPerMin = 0.0;
            for (int i = 0; i < weak.Count; i++) { var c = weak[i]; weakPerMin += LightningSchedule.FlashesPerMinute(p, ref c); }
            Assert.IsTrue(weakPerMin < perMin * 0.2, "weak " + weakPerMin);
        }

        [TestMethod]
        public void NoBoltAtTheWarpRateOfFiveOrAboveOrUnderACoverOfOneTenth()
        {
            var p = new LightningParams();
            Assert.IsTrue(LightningSchedule.Allows(p, 4.9f, 0.11f));
            Assert.IsFalse(LightningSchedule.Allows(p, 5f, 0.9f));
            Assert.IsFalse(LightningSchedule.Allows(p, 50f, 0.9f));
            Assert.IsFalse(LightningSchedule.Allows(p, 1f, 0.1f));
            Assert.IsFalse(LightningSchedule.Allows(p, 1f, 0.02f));
            // The scan leaves out the squares with too little cover, so no event can start there.
            var src = new FixedSource { Value = new CellSample { Strength = 1f, Level01 = 1f, TopM = 9000f, Coverage = 0.08f } };
            Assert.AreEqual(0, Scan(p, src, 0.0, 0.0, 30000.0).Count);
        }

        [TestMethod]
        public void TheScanFindsTheSquaresOfTheCellWithinTheRangeAndNoOthers()
        {
            var p = new LightningParams();
            var src = new FixedSource { CentreLat = 0.0, CentreLon = 0.0, RadiusRad = 8000.0 / R };
            var cells = Scan(p, src, 0.0, 0.0, 40000.0);
            Assert.IsTrue(cells.Count >= 4 && cells.Count <= 12, "squares " + cells.Count);
            foreach (var c in cells)
            {
                double dLat = c.LatDeg * Math.PI / 180.0 * R, dLon = c.LonDeg * Math.PI / 180.0 * R;
                Assert.IsTrue(Math.Sqrt(dLat * dLat + dLon * dLon) < 10000.0, "square at " + dLat + ", " + dLon);
            }
            // Nothing is found when the camera is far from the cell and the range is short.
            Assert.AreEqual(0, Scan(p, src, 3.0, 3.0, 20000.0).Count);
        }

        [TestMethod]
        public void TheLatticeIsTheSameWhateverThePointTheScanIsCentredOn()
        {
            var p = new LightningParams();
            var src = new FixedSource { CentreLat = 0.2, CentreLon = 1.0, RadiusRad = 12000.0 / R };
            double lat = 0.2 * 180.0 / Math.PI, lon = 1.0 * 180.0 / Math.PI;
            var a = Scan(p, src, lat, lon, 30000.0);
            var b = Scan(p, src, lat + 0.05, lon - 0.07, 30000.0);
            var common = a.Select(c => c.Row + ":" + c.Col).Intersect(b.Select(c => c.Row + ":" + c.Col)).ToList();
            Assert.IsTrue(common.Count >= 3, "common " + common.Count);
            foreach (string key in common)
            {
                var ca = a.First(c => c.Row + ":" + c.Col == key);
                var cb = b.First(c => c.Row + ":" + c.Col == key);
                Assert.AreEqual(ca.LatDeg, cb.LatDeg, 0.0);
                Assert.AreEqual(ca.LonDeg, cb.LonDeg, 0.0);
            }
        }

        [TestMethod]
        public void TheScanRangeGrowsWithHeightAndIsCapped()
        {
            var p = new LightningParams();
            Assert.AreEqual(p.MinScanM, LightningSchedule.ScanRangeM(p, 100.0), 1e-9);
            Assert.AreEqual(40000.0, LightningSchedule.ScanRangeM(p, 20000.0), 1e-9);
            Assert.AreEqual(p.MaxScanM, LightningSchedule.ScanRangeM(p, 500000.0), 1e-9);
        }

        [TestMethod]
        public void FlashesAreInsideTheirSquareAndTheirSlot()
        {
            var p = new LightningParams();
            var cells = Cells(1f, 1f);
            foreach (var e in Run(p, 2u, cells, 400, 900))
            {
                Assert.IsTrue(e.Ut >= e.Slot * p.SlotS && e.Ut < (e.Slot + 1) * p.SlotS);
                var c = cells.First(x => x.Row == e.Row && x.Col == e.Col);
                Assert.IsTrue(Math.Abs(e.LatDeg - c.LatDeg) <= c.NorthDeg * 0.5 + 1e-9);
                Assert.IsTrue(Math.Abs(e.LonDeg - c.LonDeg) <= c.EastDeg * 0.5 + 1e-9);
                Assert.IsTrue(e.Bumps >= 1 && e.Bumps <= 3);
                Assert.IsTrue(e.StartHeightM > 0f && e.CloudLightHeightM > 0f);
            }
        }

        [TestMethod]
        public void AboutAThirdOfTheFlashesReachTheGround()
        {
            var p = new LightningParams();
            var events = Run(p, 4u, Cells(1f, 1f), 0, 7200);
            double share = events.Count(e => e.Kind == FlashKind.Ground) / (double)events.Count;
            Assert.AreEqual(p.GroundShare, share, 0.05);
        }

        // ---- the limit ----

        private static int MostInAnyWindow(List<double> times)
        {
            times.Sort();
            int most = 0;
            for (int i = 0; i < times.Count; i++)
            {
                int n = 0;
                for (int j = i; j < times.Count && times[j] < times[i] + 1.0; j++) n++;
                most = Math.Max(most, n);
            }
            return most;
        }

        [TestMethod]
        public void NoMoreStrokesThanTheLimitBeginInAnyOneSecond()
        {
            var p = new LightningParams { MaxDensityPerKm2Min = 0.2 };
            var cells = Cells(1f, 1f);
            var events = Run(p, 9u, cells, 0, 900);
            Assert.IsTrue(events.Count > 500, "events " + events.Count);
            events.Sort((a, b) => a.Ut.CompareTo(b.Ut));
            foreach (int limit in new[] { 1, 2, 3 })
            {
                var limiter = new FlashLimiter(limit);
                var strokes = new List<double>();
                var times = new double[3];
                foreach (var ev in events)
                {
                    var e = ev;
                    if (!limiter.TryAccept(ref e)) continue;
                    FlashEnvelope.BumpTimes(ref e, times);
                    for (int k = 0; k < e.Bumps; k++) strokes.Add(e.Ut + times[k]);
                }
                Assert.IsTrue(limiter.Accepted > 20, "limit " + limit + " accepted " + limiter.Accepted);
                Assert.IsTrue(limiter.Dropped > 0, "limit " + limit + " dropped nothing");
                Assert.IsTrue(MostInAnyWindow(strokes) <= limit, "limit " + limit + " most " + MostInAnyWindow(strokes));
            }
        }

        [TestMethod]
        public void ALimitOfThreeLeavesAQuietStormAlmostAlone()
        {
            // A weak storm: a flash is dropped only where two of them fall into the same second.
            var p = new LightningParams();
            var events = Run(p, 12u, Cells(0.3f, 0.3f), 0, 3600);
            events.Sort((a, b) => a.Ut.CompareTo(b.Ut));
            var limiter = new FlashLimiter(3);
            for (int i = 0; i < events.Count; i++) { var e = events[i]; limiter.TryAccept(ref e); }
            Assert.IsTrue(events.Count > 20, "events " + events.Count);
            Assert.IsTrue(limiter.Dropped <= events.Count * 0.05, "dropped " + limiter.Dropped + " of " + events.Count);
        }

        [TestMethod]
        public void TheLimitIsClampedToOneToThree()
        {
            Assert.AreEqual(1, new FlashLimiter(0).MaxBumpsPerSecond);
            Assert.AreEqual(3, new FlashLimiter(9).MaxBumpsPerSecond);
        }
    }
}
