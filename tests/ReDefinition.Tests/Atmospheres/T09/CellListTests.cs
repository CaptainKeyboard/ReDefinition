using System;
using System.Collections.Generic;
using AtmoSim.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Tests.Atmospheres.C25;

namespace ReDefinition.Tests.Atmospheres.T09
{
    // CELLS: the generator's list of living convective cells (WeatherGenerator.Cells) describes the same cells as the point version: one entry per lattice cell with its own centre, radius,
    // strength and age, from the same hash draws, a pure function of the seed, the body and the time.
    [TestClass]
    public class CellListTests
    {
        private static WeatherGenerator Kerbin()
        {
            return CbGeneratorMeasure.Kerbin(1, true).StateOf(FieldFixture.BodyIndex).Generator;
        }

        private static List<ConvectiveCell> List(WeatherGenerator g, double ut, double min = 0.0)
        {
            var list = new List<ConvectiveCell>();
            g.Cells(ut, list, min);
            return list;
        }

        [TestMethod]
        public void TheListIsAPureFunctionOfTheTime()
        {
            WeatherGenerator g = Kerbin();
            List<ConvectiveCell> a = List(g, 1686294.0), b = List(g, 1686294.0), other = List(g, 1686294.0 + 1800.0);
            Assert.IsTrue(a.Count > 100, "the globe has cells: " + a.Count);
            Assert.AreEqual(a.Count, b.Count);
            for (int i = 0; i < a.Count; i++)
            {
                Assert.AreEqual(a[i].LatRad, b[i].LatRad, 0.0);
                Assert.AreEqual(a[i].LonRad, b[i].LonRad, 0.0);
                Assert.AreEqual(a[i].Strength, b[i].Strength, 0.0);
            }
            Assert.AreNotEqual(a.Count, other.Count, "half an hour later other cells live");
            Console.WriteLine("cells at the game clock: " + a.Count + ", above 0.3: " + a.FindAll(c => c.Strength > 0.3).Count + ", above 0.6: " + a.FindAll(c => c.Strength > 0.6).Count);
        }

        [TestMethod]
        public void EveryCellOfTheListIsTheOnePointSamplingFindsAtItsCentre()
        {
            WeatherGenerator g = Kerbin();
            int equal = 0, total = 0;
            foreach (double ut in new[] { 1686294.0, 1686294.0 + 7777.0, 9.0e6 + 3000.0 })
            {
                foreach (ConvectiveCell c in List(g, ut))
                {
                    Weather w;
                    g.Sample(c.LatRad, c.LonRad, 0.0, ut, out w);
                    Assert.IsTrue(w.CellStrength >= c.Strength - 1e-9, "the point has at least the cell's strength: " + w.CellStrength + " against " + c.Strength);
                    total++;
                    if (Math.Abs(w.CellStrength - c.Strength) < 1e-9) equal++;
                    Assert.IsTrue(c.RadiusM > 0.3 * 0.75 * g.D.CellLatticeM - 1.0 && c.RadiusM < 0.3 * 1.25 * g.D.CellLatticeM + 1.0, "the radius is the lattice's");
                    Assert.IsTrue(c.Phase > 0.0 && c.Phase < 1.0 && c.AgeS > 0.0 && c.AgeS < c.LifeS);
                    Assert.IsTrue(c.TopM >= 0.35 * g.Body.TropopauseM - 1.0 && c.TopM <= g.Body.TropopauseM + 1.0);
                }
            }
            Console.WriteLine(equal + " of " + total + " cells are the strongest at their own centre");
            Assert.IsTrue(equal > 0.9 * total, "a cell is the strongest one at its centre unless a stronger neighbour overlaps it");
        }

        // A map drawn from the list (the strongest footprint at every point) agrees with the point version where it is looked at: windows of the tropics and of the storm track.
        [TestMethod]
        public void TheListDrawnAsAMapAgreesWithThePointSamples()
        {
            WeatherGenerator g = Kerbin();
            const int n = 90;
            double d2r = Math.PI / 180.0, ut = 1686294.0 + 4000.0;
            List<ConvectiveCell> list = List(g, ut);
            int points = 0, close = 0, listStrong = 0, pointStrong = 0;
            foreach (double[] window in new[] { new[] { -5.0, 40.0 }, new[] { 10.0, 100.0 }, new[] { 45.0, -60.0 }, new[] { -20.0, 170.0 } })
            {
                for (int y = 0; y < n; y++)
                    for (int x = 0; x < n; x++)
                    {
                        double lat = (window[0] + y * 0.1) * d2r, lon = (window[1] + x * 0.1) * d2r;
                        Weather w;
                        g.Sample(lat, lon, 0.0, ut, out w);
                        double best = 0.0;
                        foreach (ConvectiveCell c in list)
                        {
                            double dy = g.Body.RadiusM * (lat - c.LatRad);
                            if (Math.Abs(dy) > 3.0 * c.RadiusM) continue;
                            double dx = g.Body.RadiusM * Math.Cos(c.LatRad) * DMath.WrapPi(lon - c.LonRad);
                            double r2 = (dx * dx + dy * dy) / (c.RadiusM * c.RadiusM);
                            if (r2 > 9.0) continue;
                            best = Math.Max(best, c.Strength * Math.Exp(-r2));
                        }
                        points++;
                        if (Math.Abs(best - w.CellStrength) < 0.05) close++;
                        if (best > 0.3) listStrong++;
                        if (w.CellStrength > 0.3) pointStrong++;
                    }
            }
            Console.WriteLine(close + " of " + points + " points within 0.05; strong (above 0.3): list " + listStrong + ", point " + pointStrong);
            Assert.IsTrue(close > 0.97 * points, "the list and the points agree on almost every point: " + close + " of " + points);
            Assert.IsTrue(Math.Abs(listStrong - pointStrong) <= 0.25 * Math.Max(pointStrong, 20), "the same strong area");
        }

        [TestMethod]
        public void TheListFollowsTheBodyAndTheSwitches()
        {
            WeatherGenerator g = Kerbin();
            WeatherGenerator off = CellEnergyMeasure.With(g, p => p.CellsEnabled = false);
            Assert.AreEqual(0, List(off, 1686294.0).Count, "cells off: an empty list");
            WeatherGenerator round4 = CellEnergyMeasure.With(g, p => { p.StationaryCells = false; p.CellInstabilityWeight = 0.0; });
            List<ConvectiveCell> r4 = List(round4, 1686294.0);
            Assert.IsTrue(r4.Count > 0);
            // the energy term only lowers strengths: the same cells, none stronger
            WeatherGenerator noTerm = CellEnergyMeasure.With(g, p => p.CellInstabilityWeight = 0.0);
            List<ConvectiveCell> a = List(g, 1686294.0), b = List(noTerm, 1686294.0);
            Assert.AreEqual(b.Count, a.Count, "the term weakens cells but removes none");
            for (int i = 0; i < a.Count; i++) Assert.IsTrue(a[i].Strength <= b[i].Strength + 1e-12);
            WeatherGenerator second = FieldFixture.SecondField(1).StateOf(FieldFixture.BodyIndex).Generator;
            List<ConvectiveCell> s = List(second, 1686294.0);
            Console.WriteLine("second body: " + s.Count + " cells, lattice " + second.D.CellLatticeM + " m");
            Assert.IsTrue(s.Count > 0);
            foreach (ConvectiveCell c in s) Assert.AreEqual(0.3, c.RadiusM / second.D.CellLatticeM, 0.0751, "the radius follows the body's lattice");
        }
    }
}
