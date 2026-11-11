using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests
{
    // The storm cells (StormCells): where they are, how long they live, how strong they are at a place, and their settings.
    [TestClass]
    public class StormCellsTests
    {
        private const double Year = 9203545;
        private const double Radius = 600000;
        private const double Slot = StormModel.SlotHours * StormModel.HourSeconds;
        private const double North = 0.06;

        private static StormCells.Rules Rules(double phase = North, int seed = 1, double frequency = 1, double radiusKm = 12, bool scheduled = true)
        {
            return new StormCells.Rules
            {
                BodyRadius = Radius,
                YearSeconds = Year,
                ForcedPhase = phase,
                WinterLine = 42,
                Frequency = frequency,
                RadiusKm = radiusKm,
                Seed = seed,
                Scheduled = scheduled
            };
        }

        // Every cell of the planet at that time.
        private static List<StormCells.Cell> All(double ut, StormCells.Rules rules)
        {
            var buffer = new StormCells.Cell[4096];
            int n = StormCells.Collect(buffer, ut, 0, 0, Math.PI * Radius, rules);
            var list = new List<StormCells.Cell>();
            for (int i = 0; i < n; i++) list.Add(buffer[i]);
            return list;
        }

        // The first time from a start at which some cell is in its peak, in steps of ten minutes.
        private static double PeakTime(StormCells.Rules rules, double from = 0)
        {
            for (double t = from; t < from + 4 * Slot; t += 600)
            {
                foreach (StormCells.Cell c in All(t, rules)) if (c.Envelope >= 1) return t;
            }
            Assert.Fail("no cell in its peak in four slots");
            return 0;
        }

        [TestMethod]
        public void TheCellsAreTheSameEveryTimeAndFollowTheSeed()
        {
            for (double t = 0; t < 3 * Slot; t += 5000)
            {
                List<StormCells.Cell> a = All(t, Rules()), b = All(t, Rules());
                Assert.AreEqual(a.Count, b.Count);
                for (int i = 0; i < a.Count; i++)
                {
                    Assert.AreEqual(a[i].Key, b[i].Key);
                    Assert.AreEqual(a[i].Latitude, b[i].Latitude);
                    Assert.AreEqual(a[i].Longitude, b[i].Longitude);
                    Assert.AreEqual(a[i].Envelope, b[i].Envelope);
                }
            }
            double t0 = PeakTime(Rules());
            var one = new HashSet<ulong>();
            foreach (StormCells.Cell c in All(t0, Rules(seed: 1))) one.Add(c.Key);
            int shared = 0;
            foreach (StormCells.Cell c in All(t0, Rules(seed: 2))) if (one.Contains(c.Key)) shared++;
            Assert.AreEqual(0, shared, "another seed gives other cells");
        }

        [TestMethod]
        public void CellsStayInTheirBandsAndInTheWinterHemisphere()
        {
            for (double t = 0; t < 4 * Slot; t += 7200)
            {
                foreach (StormCells.Cell c in All(t, Rules(phase: 0.06)))
                {
                    Assert.IsTrue(c.Latitude >= 25 && c.Latitude <= 89.5, "northern winter: a cell at " + c.Latitude);
                    Assert.IsTrue(c.Longitude >= -180 && c.Longitude < 180);
                }
                foreach (StormCells.Cell c in All(t, Rules(phase: 0.56)))
                {
                    Assert.IsTrue(c.Latitude <= -25 && c.Latitude >= -89.5, "southern winter: a cell at " + c.Latitude);
                }
                Assert.AreEqual(0, All(t, Rules(phase: 0.3)).Count, "none in the seasons between");
            }
            Assert.IsTrue(BusyTime(Rules(), 5) >= 0, "several cells at once in a winter");
        }

        // A time (from 0, in steps of three hours) at which at least that many cells exist; -1 if there is none within two days.
        private static double BusyTime(StormCells.Rules rules, int cells)
        {
            for (double t = 0; t < 2 * Slot; t += 3 * 3600) if (All(t, rules).Count >= cells) return t;
            return -1;
        }

        [TestMethod]
        public void ACellLivesInsideItsSlotAndNeverOverlapsItsSuccessorInTheSameSector()
        {
            // The storm of every (hemisphere, band, sector, slot) lies wholly inside its slot, so the next slot's storm of the same
            // sector cannot overlap it.
            for (int seed = 1; seed <= 3; seed++)
            {
                for (int slot = -2; slot < 60; slot++)
                {
                    ulong slotKey = StormModel.SlotKey(seed, true, 6, slot);
                    for (int s = 0; s < StormCells.Sectors(6, Radius); s++)
                    {
                        StormModel.Storm storm = StormModel.Draw(StormModel.Mix(slotKey, (ulong)s + 4001UL), slot, 1, 2);
                        if (!storm.Present) continue;
                        Assert.IsTrue(storm.Start >= slot * Slot - 1e-6 && storm.End <= (slot + 1) * Slot + 1e-6, "slot " + slot + " storm " + storm.Start + ".." + storm.End);
                    }
                }
            }
            for (double t = 0; t < 3 * Slot; t += 3000)
            {
                foreach (StormCells.Cell c in All(t, Rules()))
                {
                    Assert.IsTrue(c.AgeSeconds > 0 && c.AgeSeconds < 17 * 3600 + 1);
                    Assert.IsTrue(c.Envelope > 0 && c.Envelope <= 1);
                }
            }
        }

        [TestMethod]
        public void TheCentreOfACellInItsPeakHasTheFullPeakIntensityAndOutsideTheRadiusThereIsNone()
        {
            double t = PeakTime(Rules());
            StormCells.Cell cell = default(StormCells.Cell);
            foreach (StormCells.Cell c in All(t, Rules())) if (c.Envelope >= 1) { cell = c; break; }
            Assert.IsTrue(cell.Height >= StormModel.PeakIntensityMin && cell.Height <= 1);
            Assert.AreEqual(cell.Height, StormCells.IntensityAt(ref cell, cell.Latitude, cell.Longitude, Radius), 1e-9);
            // 100 m inside 80 % of the radius: still the full intensity; at the radius and beyond: none.
            double perDegree = Radius * Math.PI / 180;
            double inside = cell.Latitude + (cell.RadiusM * 0.79) / perDegree;
            double edge = cell.Latitude + cell.RadiusM / perDegree;
            double outside = cell.Latitude + (cell.RadiusM + 100) / perDegree;
            if (cell.Latitude > 0) { inside = cell.Latitude - cell.RadiusM * 0.79 / perDegree; edge = cell.Latitude - cell.RadiusM / perDegree; outside = cell.Latitude - (cell.RadiusM + 100) / perDegree; }
            Assert.AreEqual(cell.Height, StormCells.IntensityAt(ref cell, inside, cell.Longitude, Radius), 1e-6);
            Assert.AreEqual(0, StormCells.IntensityAt(ref cell, edge, cell.Longitude, Radius), 1e-6);
            Assert.AreEqual(0, StormCells.IntensityAt(ref cell, outside, cell.Longitude, Radius), 1e-12);
            Assert.AreEqual(0, StormCells.IntensityAt(ref cell, cell.Latitude, cell.Longitude + 180, Radius), 1e-12);
        }

        [TestMethod]
        public void TheEdgeIsSoftOverATwentiethPercentOfTheRadius()
        {
            double r = 12000;
            Assert.AreEqual(1, StormCells.Shape(0, r), 1e-12);
            Assert.AreEqual(1, StormCells.Shape(r * 0.8, r), 1e-12);
            Assert.AreEqual(0.5, StormCells.Shape(r * 0.9, r), 1e-9);
            Assert.AreEqual(0, StormCells.Shape(r, r), 1e-12);
            Assert.AreEqual(0, StormCells.Shape(2 * r, r));
            double last = 1;
            for (double d = 0.8 * r; d <= r; d += r / 200)
            {
                double v = StormCells.Shape(d, r);
                Assert.IsTrue(v <= last + 1e-12, "the edge must fall");
                last = v;
            }
            Assert.AreEqual(0, StormCells.Shape(1, 0));
            Assert.AreEqual(0, StormCells.Shape(double.NaN, r));
        }

        [TestMethod]
        public void ACellDriftsAtItsOwnSpeedWithoutJumps()
        {
            double t0 = PeakTime(Rules());
            List<StormCells.Cell> before = All(t0, Rules()), after = All(t0 + 60, Rules());
            int compared = 0;
            foreach (StormCells.Cell a in before)
            {
                foreach (StormCells.Cell b in after)
                {
                    if (a.Key != b.Key) continue;
                    double moved = StormCells.Distance(a.Latitude, a.Longitude, b.Latitude, b.Longitude, Radius);
                    Assert.AreEqual(a.SpeedMs * 60, moved, a.SpeedMs * 60 * 0.06, "a cell moves its speed times the time");
                    compared++;
                }
            }
            Assert.IsTrue(compared >= 2, "cells compared: " + compared);
            // Over a whole slot no step of 30 s is longer than the highest speed allows.
            var last = new Dictionary<ulong, StormCells.Cell>();
            for (double t = Slot; t < 2 * Slot; t += 30)
            {
                foreach (StormCells.Cell c in All(t, Rules()))
                {
                    StormCells.Cell p;
                    if (last.TryGetValue(c.Key, out p))
                    {
                        double step = StormCells.Distance(p.Latitude, p.Longitude, c.Latitude, c.Longitude, Radius);
                        Assert.IsTrue(step <= StormCells.SpeedMaxMs * 30 * 1.1 + 1, "a step of " + step + " m");
                    }
                    last[c.Key] = c;
                }
            }
            Assert.IsTrue(last.Count >= 3);
            Assert.IsTrue(StormCells.MaxDriftM >= 100000 && StormCells.MaxDriftM <= 130000, "the drift bound: " + StormCells.MaxDriftM);
        }

        [TestMethod]
        public void TheIntensityAtAFixedPointChangesSmoothlyAsACellComesAndGoes()
        {
            double t0 = PeakTime(Rules());
            StormCells.Cell cell = default(StormCells.Cell);
            foreach (StormCells.Cell c in All(t0, Rules())) if (c.Envelope >= 1) { cell = c; break; }
            double lat = cell.Latitude, lon = cell.Longitude;
            double previous = -1, peak = 0;
            var buffer = new StormCells.Cell[64];
            for (double t = t0 - 3600; t < t0 + 3 * 3600; t += 30)
            {
                int n = StormCells.Collect(buffer, t, lat, lon, 1, Rules());
                double v = StormCells.IntensityAt(buffer, n, lat, lon, Radius);
                if (previous >= 0) Assert.IsTrue(Math.Abs(v - previous) < 0.05, "a step of " + Math.Abs(v - previous));
                previous = v;
                peak = Math.Max(peak, v);
            }
            Assert.IsTrue(peak > 0.4);
        }

        [TestMethod]
        public void CollectFindsTheCellsOfACircleAndNothingElse()
        {
            double t0 = PeakTime(Rules());
            List<StormCells.Cell> all = All(t0, Rules());
            StormCells.Cell target = all[0];
            var buffer = new StormCells.Cell[64];
            int n = StormCells.Collect(buffer, t0, target.Latitude, target.Longitude, 10, Rules());
            bool found = false;
            for (int i = 0; i < n; i++)
            {
                if (buffer[i].Key == target.Key) found = true;
                Assert.IsTrue(buffer[i].DistanceM <= 10 + buffer[i].RadiusM);
            }
            Assert.IsTrue(found);
            // A circle in the middle of the summer hemisphere's ocean of nothing: no cells there at all.
            Assert.AreEqual(0, StormCells.Collect(buffer, t0, 0, 0, 60000, Rules()));
            Assert.AreEqual(0, StormCells.Collect(buffer, double.NaN, 60, 0, 60000, Rules()));
            Assert.AreEqual(0, StormCells.Collect(buffer, t0, 60, 0, 60000, Rules(scheduled: false)), "storms off");
            Assert.AreEqual(0, StormCells.Collect(buffer, t0, 60, 0, 60000, Rules(frequency: 0)), "frequency 0");
            // A buffer that is too small is filled and not overrun.
            double busy = BusyTime(Rules(), 5);
            Assert.IsTrue(busy >= 0);
            Assert.AreEqual(3, StormCells.Collect(new StormCells.Cell[3], busy, 0, 0, Math.PI * Radius, Rules()));
        }

        [TestMethod]
        public void TheFrequencyAndTheRadiusSettingScaleTheCells()
        {
            double t0 = PeakTime(Rules());
            Assert.AreEqual(0, All(t0, Rules(frequency: 0)).Count);
            Assert.IsTrue(All(t0, Rules(frequency: 2)).Count > All(t0, Rules(frequency: 1)).Count);
            foreach (double km in new[] { 1.0, 5, 12, 25, 90, double.NaN })
            {
                foreach (StormCells.Cell c in All(t0, Rules(radiusKm: km)))
                    Assert.IsTrue(c.RadiusM >= 5000 && c.RadiusM <= 25000, "radius " + c.RadiusM + " for the setting " + km);
                foreach (StormCells.Cell c in All(t0, Rules(radiusKm: km)))
                    Assert.IsTrue(c.BaseAglM >= StormCells.BaseAglMinM && c.BaseAglM <= StormCells.BaseAglMaxM);
            }
            double small = 0, large = 0;
            foreach (StormCells.Cell c in All(t0, Rules(radiusKm: 8))) small += c.RadiusM;
            foreach (StormCells.Cell c in All(t0, Rules(radiusKm: 20))) large += c.RadiusM;
            Assert.IsTrue(large > small);
        }

        [TestMethod]
        public void OnlyTheNearestCellsAreKeptWhenThereAreMoreThanTheCap()
        {
            var cells = new StormCells.Cell[10];
            var rng = new Random(3);
            for (int i = 0; i < cells.Length; i++)
            {
                cells[i].Key = (ulong)(i + 10);
                cells[i].RadiusM = 5000 + rng.Next(20000);
                cells[i].DistanceM = rng.Next(80000);
            }
            var gaps = new List<double>();
            foreach (StormCells.Cell c in cells) gaps.Add(c.DistanceM - c.RadiusM);
            gaps.Sort();
            int kept = StormCells.Nearest(cells, cells.Length, 6);
            Assert.AreEqual(6, kept);
            for (int i = 0; i < kept; i++) Assert.AreEqual(gaps[i], cells[i].DistanceM - cells[i].RadiusM, 1e-9);
            Assert.AreEqual(3, StormCells.Nearest(cells, 3, 6));
            Assert.AreEqual(0, StormCells.Nearest(cells, 0, 6));
        }

        [TestMethod]
        public void TheForcedCellStandsAtItsPlaceAtItsPeakWithoutTheSchedule()
        {
            var rules = Rules(phase: 0.56, scheduled: false);
            rules.HasForced = true;
            rules.ForcedLatitude = 62;
            rules.ForcedLongitude = -30.5;
            rules.ForcedRadiusKm = 18;
            var buffer = new StormCells.Cell[8];
            int n = StormCells.Collect(buffer, 12345, 62, -30.5, 60000, rules);
            Assert.AreEqual(1, n);
            Assert.IsTrue(buffer[0].Forced);
            Assert.AreEqual(18000, buffer[0].RadiusM, 1e-9);
            Assert.AreEqual(1, StormCells.IntensityAt(buffer, n, 62, -30.5, Radius), 1e-12);
            Assert.AreEqual(1, StormCells.IntensityAt(buffer, n, 62.4, -30.5, Radius), 1e-12, "4 km from the centre is inside 80 % of 18 km");
            Assert.AreEqual(0, StormCells.IntensityAt(buffer, n, 65, -30.5, Radius), 1e-12, "31 km away");
            Assert.AreEqual(0, StormCells.Collect(buffer, 12345, 62, 150, 60000, rules), "the cell is not near a point on the other side");
            Assert.AreEqual(1, StormCells.Collect(buffer, 12345 + 5 * Slot, 62.2, -30.5, 60000, rules), "no time limit");
        }

        [TestMethod]
        public void TheForcedCellTextIsParsedAndBroughtIntoRange()
        {
            double lat, lon, km;
            Assert.IsTrue(StormCells.TryParseForced("62, -30.5 ,18", out lat, out lon, out km));
            Assert.AreEqual(62, lat); Assert.AreEqual(-30.5, lon); Assert.AreEqual(18, km);
            Assert.IsTrue(StormCells.TryParseForced("95,190,2", out lat, out lon, out km));
            Assert.AreEqual(90, lat); Assert.AreEqual(-170, lon, 1e-9); Assert.AreEqual(5, km);
            Assert.IsTrue(StormCells.TryParseForced("-10,0,400", out lat, out lon, out km));
            Assert.AreEqual(25, km);
            Assert.IsFalse(StormCells.TryParseForced("off", out lat, out lon, out km));
            Assert.IsFalse(StormCells.TryParseForced("", out lat, out lon, out km));
            Assert.IsFalse(StormCells.TryParseForced(null, out lat, out lon, out km));
            Assert.IsFalse(StormCells.TryParseForced("1,2", out lat, out lon, out km));
            Assert.IsFalse(StormCells.TryParseForced("1,2,x", out lat, out lon, out km));
            Assert.IsFalse(StormCells.TryParseForced("1,NaN,3", out lat, out lon, out km));
        }

        [TestMethod]
        public void TheCellSettingsComeBackAndAreBroughtIntoTheirRanges()
        {
            string folder = Path.Combine(Path.GetTempPath(), "cell-settings-" + Guid.NewGuid().ToString("N"));
            string file = Path.Combine(folder, "settings.cfg");
            ConfigNode saved = AtmospheresSettings.Snapshot();
            try
            {
                Assert.AreEqual(12f, AtmospheresSettings.StormCellRadiusDefaultKm);
                Assert.AreEqual(6, AtmospheresSettings.StormMaxCellsDefault);
                AtmospheresSettings.StormCellRadiusKm = 7.5f;
                AtmospheresSettings.StormVolume = false;
                AtmospheresSettings.StormMaxCells = 3;
                AtmospheresSettings.ForceCellOn = true;
                AtmospheresSettings.ForceCellLatitude = 62.25;
                AtmospheresSettings.ForceCellLongitude = -30.5;
                AtmospheresSettings.ForceCellRadiusKm = 18f;
                AtmospheresSettings.Write(file);
                AtmospheresSettings.StormCellRadiusKm = 12f;
                AtmospheresSettings.StormVolume = true;
                AtmospheresSettings.StormMaxCells = 6;
                AtmospheresSettings.ForceCellOn = false;
                AtmospheresSettings.Read(file);
                Assert.AreEqual(7.5f, AtmospheresSettings.StormCellRadiusKm);
                Assert.IsFalse(AtmospheresSettings.StormVolume);
                Assert.AreEqual(3, AtmospheresSettings.StormMaxCells);
                Assert.IsTrue(AtmospheresSettings.ForceCellOn);
                Assert.AreEqual(62.25, AtmospheresSettings.ForceCellLatitude);
                Assert.AreEqual(-30.5, AtmospheresSettings.ForceCellLongitude);
                Assert.AreEqual(18f, AtmospheresSettings.ForceCellRadiusKm);

                File.WriteAllText(file, "stormCellRadiusKm = 2\nstormMaxCells = 0\nforceStormCell = off\n");
                AtmospheresSettings.Read(file);
                Assert.AreEqual(5f, AtmospheresSettings.StormCellRadiusKm);
                Assert.AreEqual(1, AtmospheresSettings.StormMaxCells);
                Assert.IsFalse(AtmospheresSettings.ForceCellOn);
                File.WriteAllText(file, "stormCellRadiusKm = 90\nstormMaxCells = 99\nforceStormCell = 10,20,30\n");
                AtmospheresSettings.Read(file);
                Assert.AreEqual(25f, AtmospheresSettings.StormCellRadiusKm);
                Assert.AreEqual(12, AtmospheresSettings.StormMaxCells);
                Assert.IsTrue(AtmospheresSettings.ForceCellOn);
                Assert.AreEqual(25f, AtmospheresSettings.ForceCellRadiusKm);
                File.WriteAllText(file, "stormCellRadiusKm = x\nstormMaxCells = y\nstormVolume = z\nforceStormCell = nonsense\n");
                AtmospheresSettings.Read(file);
                Assert.AreEqual(25f, AtmospheresSettings.StormCellRadiusKm, "a value that is not a number is ignored");
                Assert.AreEqual(12, AtmospheresSettings.StormMaxCells);
                Assert.IsFalse(AtmospheresSettings.ForceCellOn, "text that is not a cell switches the forced cell off");
            }
            finally
            {
                AtmospheresSettings.Restore(saved);
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        }
    }
}
