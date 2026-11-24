using System;
using AtmoSim.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Tests.Atmospheres.C25;

namespace ReDefinition.Tests.Atmospheres.T09
{
    // CELLS: the energy term of a convective cell (BodyParams.CellInstabilityWeight, CellEnergyFull, CellLiftEnergyShare) and the updraft of the full cell (CellBuoyancyNumber, CellUpdraftScale).
    // The tolerance of the term on the rain and cover of the fronts is stated here: it moves the rain of the front zone by less than 12 %, the global rain by less than 6 % and the cover by
    // less than 0.02 (measured at 144 x 72, 24 buckets x 6 phases: 8.1 %, 4.4 % and 0.005 for the shipped numbers, E40-cells-result.md).
    [TestClass]
    public class CellEnergyTests
    {
        private static WeatherGenerator Kerbin()
        {
            return CbGeneratorMeasure.Kerbin(1, true).StateOf(FieldFixture.BodyIndex).Generator;
        }

        [TestMethod]
        public void TheFactorIsTheSquareRootOfTheEnergyUnderTheFullEnergy()
        {
            WeatherGenerator g = Kerbin();
            double full = g.Body.CellEnergyFull;
            Assert.AreEqual(1.0, g.CellEnergyFactor(full), 0.0);
            Assert.AreEqual(1.0, g.CellEnergyFactor(3.0 * full), 0.0);
            Assert.AreEqual(0.0, g.CellEnergyFactor(0.0), 0.0);
            Assert.AreEqual(0.5, g.CellEnergyFactor(0.25 * full), 1e-12, "w_max = sqrt(2 CAPE): a quarter of the energy is half the updraft");
            Assert.IsTrue(g.CellEnergyFactor(0.3 * full) < g.CellEnergyFactor(0.6 * full), "monotone");
            // the weight is the exponent: 0 is off, 0.5 a fourth root
            WeatherGenerator off = CellEnergyMeasure.With(g, p => p.CellInstabilityWeight = 0.0);
            WeatherGenerator half = CellEnergyMeasure.With(g, p => p.CellInstabilityWeight = 0.5);
            Assert.AreEqual(1.0, off.CellEnergyFactor(0.01), 0.0, "weight 0: no term");
            Assert.AreEqual(Math.Pow(0.25, 0.25), half.CellEnergyFactor(0.25 * full), 1e-12, "weight 0.5: (E / full)^(w / 2)");
        }

        [TestMethod]
        public void TheShippedBodyHasThePhysicalTerm()
        {
            Assert.AreEqual(1.0, Kerbin().Body.CellInstabilityWeight, 0.0);
        }

        [TestMethod]
        public void TheTermNeverRaisesAStrengthAndLeavesTheWeatherOutsideTheCellsAlone()
        {
            WeatherGenerator on = Kerbin();
            WeatherGenerator off = CellEnergyMeasure.With(on, p => p.CellInstabilityWeight = 0.0);
            var rng = new Rng(5);
            int cells = 0, weaker = 0, quiet = 0, different = 0;
            for (int i = 0; i < 8000; i++)
            {
                double lat = Math.Asin(2.0 * rng.Uniform() - 1.0), lon = (2.0 * rng.Uniform() - 1.0) * Math.PI, ut = 1.0e6 + rng.Uniform() * on.Body.YearS;
                Weather a, b;
                on.Sample(lat, lon, 0.0, ut, out a);
                off.Sample(lat, lon, 0.0, ut, out b);
                Assert.IsTrue(a.CellStrength <= b.CellStrength + 1e-12, "the energy factor is at most 1");
                if (b.CellStrength > 0.0) { cells++; if (a.CellStrength < b.CellStrength - 1e-9) weaker++; }
                else { quiet++; if (WeatherGenerator.HashOf(ref a, 0) != WeatherGenerator.HashOf(ref b, 0)) different++; }
            }
            Console.WriteLine(cells + " points with a cell, " + weaker + " of them weaker with the term; " + quiet + " points without a cell, " + different + " differ");
            Assert.IsTrue(cells > 100 && weaker > cells / 4, "the term weakens a good part of the cells");
            Assert.AreEqual(0, different, "where there is no cell the weather is the same");
        }

        // The tolerance: what the term does to the rain and the cover of the fronts and of the globe.
        [TestMethod]
        public void TheTermKeepsTheRainAndTheCoverOfTheFrontsWithinTheStatedTolerance()
        {
            WeatherGenerator on = Kerbin();
            WeatherGenerator off = CellEnergyMeasure.With(on, p => p.CellInstabilityWeight = 0.0);
            CellEnergyMeasure.Table a = CellEnergyMeasure.Measure(on, 96, 48, 12, 4), b = CellEnergyMeasure.Measure(off, 96, 48, 12, 4);
            Console.WriteLine("front zone rain " + a.Fronts.MeanRain.ToString("0.000") + " against " + b.Fronts.MeanRain.ToString("0.000") + ", globe rain " + a.Globe.MeanRain.ToString("0.000") + " against " + b.Globe.MeanRain.ToString("0.000")
                + ", front cover " + a.Fronts.MeanCover.ToString("0.0000") + " against " + b.Fronts.MeanCover.ToString("0.0000") + ", globe cover " + a.Globe.MeanCover.ToString("0.0000") + " against " + b.Globe.MeanCover.ToString("0.0000"));
            Assert.AreEqual(b.Fronts.MeanRain, a.Fronts.MeanRain, 0.12 * b.Fronts.MeanRain, "rain of the front zone within 12 %");
            Assert.AreEqual(b.MidLat.MeanRain, a.MidLat.MeanRain, 0.12 * b.MidLat.MeanRain, "rain of the mid-latitudes within 12 %");
            Assert.AreEqual(b.Tropics.MeanRain, a.Tropics.MeanRain, 0.12 * b.Tropics.MeanRain, "rain of the tropics within 12 %");
            Assert.AreEqual(b.Globe.MeanRain, a.Globe.MeanRain, 0.06 * b.Globe.MeanRain, "rain of the globe within 6 %");
            Assert.AreEqual(b.Fronts.MeanCover, a.Fronts.MeanCover, 0.02, "cover of the front zone within 0.02");
            Assert.AreEqual(b.Globe.MeanCover, a.Globe.MeanCover, 0.02, "cover of the globe within 0.02");
            Assert.AreEqual(b.Fronts.PrecipShare, a.Fronts.PrecipShare, 1.0, "the precipitating share of the front zone within one point");
        }

        // The contrast of the afternoon of the warm tropics against cold sea at night widens, and the tropics keep strong cells (literature: 1 to 3 % of the tropics hold deep convection).
        [TestMethod]
        public void ColdSeaAtNightFallsFurtherBehindTheWarmAfternoon()
        {
            WeatherGenerator on = Kerbin();
            WeatherGenerator off = CellEnergyMeasure.With(on, p => p.CellInstabilityWeight = 0.0);
            CellEnergyMeasure.Table a = CellEnergyMeasure.Measure(on, 144, 72, 24, 6), b = CellEnergyMeasure.Measure(off, 144, 72, 24, 6);
            double ratioOn = a.WarmAfternoon.P03 / Math.Max(a.ColdSeaNight.P03, 1e-6), ratioOff = b.WarmAfternoon.P03 / Math.Max(b.ColdSeaNight.P03, 1e-6);
            Console.WriteLine("warm afternoon over cold sea at night (above 0.3): " + ratioOff.ToString("0.0") + " without the term, " + ratioOn.ToString("0.0") + " with it; tropics " + b.Tropics.P03.ToString("0.00") + " % and " + a.Tropics.P03.ToString("0.00") + " %");
            Assert.IsTrue(ratioOn > 1.4 * ratioOff, "the contrast widens by at least 40 %");
            Assert.IsTrue(a.Tropics.P03 > 0.8 && a.Tropics.P03 < 3.0, "the tropics keep strong cells on one to three hundredths of their area");
            Assert.IsTrue(a.WarmAfternoon.P03 > a.Tropics.P03, "the afternoon of the warm season has more than the mean");
            Assert.IsTrue(a.ColdSeaNight.P06 < 0.1 * a.WarmAfternoon.P06, "cold sea at night has hardly a cell above 0.6");
        }

        [TestMethod]
        public void TheUpdraftOfTheFullCellFollowsTheBody()
        {
            WeatherGenerator k = Kerbin();
            Console.WriteLine("Kerbin full-cell updraft " + k.CellUpdraftMs.ToString("0.000") + " m/s (g " + k.Body.GravityMs2 + ", H " + k.Body.TropopauseM + ")");
            Assert.AreEqual(6.0, k.CellUpdraftMs, 0.05, "6 m/s on Kerbin");
            WeatherGenerator second = FieldFixture.SecondField(1).StateOf(FieldFixture.BodyIndex).Generator;
            double expect = Math.Sqrt(second.Body.CellBuoyancyNumber * second.Body.GravityMs2 * second.Body.TropopauseM);
            Assert.AreEqual(expect, second.CellUpdraftMs, 1e-9, "sqrt(B g H) of the body");
            Console.WriteLine("second body full-cell updraft " + second.CellUpdraftMs.ToString("0.000") + " m/s (g " + second.Body.GravityMs2 + ", H " + second.Body.TropopauseM + ")");
            Assert.AreNotEqual(k.CellUpdraftMs, second.CellUpdraftMs, 1e-3, "a second body does not get Kerbin's 6 m/s");
            WeatherGenerator doubled = CellEnergyMeasure.With(k, p => p.CellUpdraftScale = 2.0);
            Assert.AreEqual(2.0 * k.CellUpdraftMs, doubled.CellUpdraftMs, 1e-9, "the body's multiplier");
            // the updraft goes with the square root of g H: a body with four times the g H has twice the updraft
            WeatherGenerator heavy = CellEnergyMeasure.With(k, p => p.GravityMs2 *= 4.0);
            Assert.AreEqual(2.0 * k.CellUpdraftMs, heavy.CellUpdraftMs, 1e-9);
        }
    }
}
