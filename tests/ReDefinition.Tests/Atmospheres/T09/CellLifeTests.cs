using System;
using System.Collections.Generic;
using AtmoSim.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Tests.Atmospheres.C25;

namespace ReDefinition.Tests.Atmospheres.T09
{
    // CBGEN: the life of the generator's convective cells (BodyParams.StationaryCells, tuning round 5). The cells are drawn per 3-hour bucket; until round 4 a cell lived wholly inside its
    // bucket, so no cell was alive at the edges of a bucket and the globe had none of them to show there (the game's clock scene at UT 1686294 lies at 0.14 of a bucket). Round 5 lets a cell
    // be born at any time of its bucket and live on into the next one: the area under cells is the same at every time of the day. The tests measure areas (cos(latitude) weighted) on a coarse
    // grid at sampled times; the numbers they pin are the ones printed (CbGeneratorMeasure has the long runs).
    [TestClass]
    public class CellLifeTests
    {
        private const int Nx = 96, Ny = 48;
        private const double Bucket = WeatherGenerator.CellHourBucketS;

        private sealed class Share
        {
            internal double Area, Over03, Over06, Max;
            internal void Add(double weight, double strength)
            {
                Area += weight;
                if (strength > 0.3) Over03 += weight;
                if (strength > 0.6) Over06 += weight;
                if (strength > Max) Max = strength;
            }
            internal double Percent03 { get { return Area > 0.0 ? 100.0 * Over03 / Area : 0.0; } }
            internal double Percent06 { get { return Area > 0.0 ? 100.0 * Over06 / Area : 0.0; } }
        }

        // The sub-solar longitude of the generator's frame (SunLon = WrapPi(-(Omega - 2 pi / year) ut)).
        private static double SunLon(WeatherGenerator g, double ut)
        {
            return DMath.WrapPi(-(g.Body.Omega - 2.0 * Math.PI / g.Body.YearS) * ut);
        }

        // The local solar hour (0 midnight, 12 noon) of a longitude at a time.
        private static double LocalHour(WeatherGenerator g, double lonRad, double ut)
        {
            double hour = 12.0 + DMath.WrapPi(lonRad - SunLon(g, ut)) / Math.PI * 12.0;
            return hour >= 24.0 ? hour - 24.0 : hour < 0.0 ? hour + 24.0 : hour;
        }

        // The share of the globe under cells above 0.3 at the given phase (0 to 1) of the bucket, over several buckets spread over the year.
        private static Share GlobeAtPhase(WeatherGenerator g, double phase, int buckets)
        {
            var share = new Share();
            for (int b = 0; b < buckets; b++)
            {
                double ut = Math.Floor((b / (double)buckets * g.Body.YearS + 0.37 * g.Body.RotationPeriodS * b) / Bucket) * Bucket + phase * Bucket;
                for (int y = 0; y < Ny; y++)
                {
                    double lat = (90.0 - (y + 0.5) / Ny * 180.0) * Math.PI / 180.0;
                    double weight = Math.Cos(lat);
                    for (int x = 0; x < Nx; x++)
                    {
                        Weather w;
                        g.Sample(lat, ((x + 0.5) / Nx * 360.0 - 180.0) * Math.PI / 180.0, 0.0, ut, out w);
                        share.Add(weight, w.CellStrength);
                    }
                }
            }
            return share;
        }

        private static WeatherGenerator Kerbin(bool stationary)
        {
            return CbGeneratorMeasure.Kerbin(1, stationary).StateOf(FieldFixture.BodyIndex).Generator;
        }

        [TestMethod]
        public void TheAreaUnderStrongCellsDoesNotSwingWithTheBucket()
        {
            WeatherGenerator g = Kerbin(true);
            double lo = double.MaxValue, hi = 0.0;
            foreach (double phase in new[] { 0.02, 0.1, 0.25, 0.5, 0.75, 0.9, 0.98 })
            {
                Share s = GlobeAtPhase(g, phase, 8);
                Console.WriteLine("stationary, phase " + phase + ": cells above 0.3 on " + s.Percent03.ToString("0.000") + " % of the globe, above 0.6 on " + s.Percent06.ToString("0.000") + " %");
                lo = Math.Min(lo, s.Percent03);
                hi = Math.Max(hi, s.Percent03);
            }
            Assert.IsTrue(lo > 0.5 * hi, "the share at the quietest time of the bucket is more than half of the one at the busiest: " + lo + " against " + hi);
        }

        [TestMethod]
        public void TheRoundFourDrawHasNoCellAtTheEdgesOfABucket()
        {
            WeatherGenerator g = Kerbin(false);
            Share edge = GlobeAtPhase(g, 0.02, 8), middle = GlobeAtPhase(g, 0.5, 8);
            Console.WriteLine("round 4: cells above 0.3 on " + edge.Percent03.ToString("0.000") + " % at the edge of the bucket, " + middle.Percent03.ToString("0.000") + " % in its middle");
            Assert.IsTrue(edge.Percent03 < 0.05 * middle.Percent03, "no strong cell is alive at the edge of a bucket");
        }

        [TestMethod]
        public void TheMeanAreaUnderCellsIsTheSameWithBothDraws()
        {
            double[] phases = { 0.04, 0.12, 0.2, 0.28, 0.36, 0.44, 0.52, 0.6, 0.68, 0.76, 0.84, 0.92 };
            double mean4 = 0.0, mean5 = 0.0;
            WeatherGenerator g4 = Kerbin(false), g5 = Kerbin(true);
            foreach (double phase in phases)
            {
                mean4 += GlobeAtPhase(g4, phase, 8).Percent03 / phases.Length;
                mean5 += GlobeAtPhase(g5, phase, 8).Percent03 / phases.Length;
            }
            Console.WriteLine("mean share above 0.3: round 4 " + mean4.ToString("0.000") + " %, round 5 " + mean5.ToString("0.000") + " %");
            Assert.AreEqual(mean4, mean5, 0.15 * mean4, "the time-mean area under strong cells is kept");
        }

        // Everything the draw of the cells does not touch is the same in both rounds: with the cells off the two generators agree bit for bit, and where neither draw has a cell at the point
        // the whole weather (cloud, rain, wind, temperature) agrees.
        [TestMethod]
        public void OutsideTheCellsTheWeatherIsTheSameAsInRoundFour()
        {
            WeatherGenerator g4 = Kerbin(false), g5 = Kerbin(true);
            BodyParams noCells4 = g4.Body.Clone(), noCells5 = g5.Body.Clone();
            noCells4.CellsEnabled = false;
            noCells5.CellsEnabled = false;
            var off4 = new WeatherGenerator(noCells4, g4.Seed);
            var off5 = new WeatherGenerator(noCells5, g5.Seed);
            var rng = new Rng(11);
            int quiet = 0, total = 6000, different = 0;
            for (int i = 0; i < total; i++)
            {
                double lat = Math.Asin(2.0 * rng.Uniform() - 1.0), lon = (2.0 * rng.Uniform() - 1.0) * Math.PI, ut = 1.0e6 + rng.Uniform() * g4.Body.YearS;
                Weather a, b, c, d;
                off4.Sample(lat, lon, 0.0, ut, out a);
                off5.Sample(lat, lon, 0.0, ut, out b);
                Assert.AreEqual(WeatherGenerator.HashOf(ref a, 0), WeatherGenerator.HashOf(ref b, 0), "with the cells off the two draws are one");
                g4.Sample(lat, lon, 0.0, ut, out c);
                g5.Sample(lat, lon, 0.0, ut, out d);
                if (c.CellStrength > 0.0 || d.CellStrength > 0.0) continue;
                quiet++;
                if (WeatherGenerator.HashOf(ref c, 0) != WeatherGenerator.HashOf(ref d, 0)) different++;
            }
            Console.WriteLine(quiet + " of " + total + " points have no cell in either draw; " + different + " of them differ");
            Assert.IsTrue(quiet > total / 2, "most points are outside the cells");
            Assert.AreEqual(0, different, "outside the cells the weather is the same");
        }

        // Cells in the tropics of the warm hemisphere in the afternoon, and on cold sea at night, for a body.
        private static void Regions(WeatherGenerator g, out Share warmAfternoon, out Share coldSeaNight)
        {
            warmAfternoon = new Share();
            coldSeaNight = new Share();
            const int buckets = 24;
            for (int b = 0; b < buckets; b++)
            {
                for (int p = 0; p < 6; p++)
                {
                    double ut = Math.Floor((b / (double)buckets * g.Body.YearS + 0.37 * g.Body.RotationPeriodS * b) / Bucket) * Bucket + (p + 0.5) / 6.0 * Bucket;
                    // the declination of the sub-solar point: positive in the northern summer (the generator's DLand = -eps cos(2 pi year phase))
                    double declination = -g.Body.ObliquityRad * Math.Cos(2.0 * Math.PI * (ut / g.Body.YearS - Math.Floor(ut / g.Body.YearS)));
                    for (int y = 0; y < Ny; y++)
                    {
                        double lat = (90.0 - (y + 0.5) / Ny * 180.0) * Math.PI / 180.0;
                        double weight = Math.Cos(lat);
                        bool tropicsOfTheSummer = Math.Abs(lat) < 25.0 * Math.PI / 180.0 && lat * declination > 0.0;
                        for (int x = 0; x < Nx; x++)
                        {
                            double lon = ((x + 0.5) / Nx * 360.0 - 180.0) * Math.PI / 180.0;
                            Weather w;
                            g.Sample(lat, lon, 0.0, ut, out w);
                            double hour = LocalHour(g, lon, ut);
                            if (tropicsOfTheSummer && hour >= 12.0 && hour < 18.0) warmAfternoon.Add(weight, w.CellStrength);
                            if (w.OceanFraction >= 0.9 && w.SurfaceTempK < 283.0 && (hour >= 21.0 || hour < 5.0)) coldSeaNight.Add(weight, w.CellStrength);
                        }
                    }
                }
            }
        }

        [TestMethod]
        public void StrongCellsStandInTheTropicsOfTheWarmSeasonInTheAfternoonAndNotOnColdSeaAtNight()
        {
            WeatherGenerator g = Kerbin(true);
            Share warm, cold;
            Regions(g, out warm, out cold);
            Console.WriteLine("Kerbin: warm tropics, afternoon: above 0.3 on " + warm.Percent03.ToString("0.000") + " %, above 0.6 on " + warm.Percent06.ToString("0.000") + " %, max " + warm.Max.ToString("0.00")
                + "; cold sea at night: above 0.3 on " + cold.Percent03.ToString("0.000") + " %, above 0.6 on " + cold.Percent06.ToString("0.000") + " % (area " + cold.Area.ToString("0") + ")");
            // Measured (CBGEN, seed 1): warm afternoon 2.2 % / 0.48 %, cold sea at night 0.59 % / 0.19 %: the cells are not absent over cold sea at night (the front term and the lift of a low
            // make them there; the generator's cell strength has no term of the instability, see E40-cb-generator-result.md), but they are a quarter as many as in the warm afternoon.
            Assert.IsTrue(warm.Percent03 > 1.0 && warm.Percent06 > 0.2, "strong cells stand over more than a hundredth of the warm tropics in the afternoon");
            Assert.IsTrue(warm.Max > 0.9, "the strongest cell reaches full strength");
            Assert.IsTrue(cold.Percent03 < 0.5 * warm.Percent03, "cold sea at night has less than half the area under strong cells of the warm tropics in the afternoon");
        }

        [TestMethod]
        public void TheSecondBodyHasItsOwnNumbers()
        {
            WeatherGenerator g = FieldFixture.SecondField(1).StateOf(FieldFixture.BodyIndex).Generator;
            Share warm, cold;
            Regions(g, out warm, out cold);
            Console.WriteLine("second body: lattice " + g.D.CellLatticeM + " m, troposphere " + g.Body.TropopauseM + " m");
            Console.WriteLine("second body: warm tropics, afternoon: above 0.3 on " + warm.Percent03.ToString("0.000") + " %, above 0.6 on " + warm.Percent06.ToString("0.000") + " %, max " + warm.Max.ToString("0.00")
                + "; cold sea at night: above 0.3 on " + cold.Percent03.ToString("0.000") + " %, above 0.6 on " + cold.Percent06.ToString("0.000") + " % (area " + cold.Area.ToString("0") + ")");
            WeatherGenerator k = Kerbin(true);
            Assert.AreNotEqual(k.D.CellLatticeM, g.D.CellLatticeM, "the cell lattice follows the body's troposphere");
            Assert.AreEqual(1.46 * g.Body.TropopauseM, g.D.CellLatticeM, 1.0, "a lattice of 1.46 tropospheres, not Kerbin's metres");
            Assert.IsTrue(warm.Percent03 > 1.0 && warm.Max > 0.9, "the second body has strong cells in its warm tropics in the afternoon");
            Assert.IsTrue(cold.Percent03 < 0.5 * warm.Percent03, "and fewer on cold sea at night");
            // the draw is stationary on this body too
            double lo = double.MaxValue, hi = 0.0;
            foreach (double phase in new[] { 0.02, 0.1, 0.5, 0.9, 0.98 })
            {
                Share s = GlobeAtPhase(g, phase, 8);
                lo = Math.Min(lo, s.Percent03);
                hi = Math.Max(hi, s.Percent03);
            }
            Assert.IsTrue(lo > 0.5 * hi, "the area under strong cells does not swing with the bucket: " + lo + " against " + hi);
        }
    }
}
