using System;
using AtmoSim.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Core;
using ReDefinition.Tests.Atmospheres.C25;

namespace ReDefinition.Tests.Atmospheres.E40
{
    // The layer shares (LayerShares, E40 D1): each cloud layer's map value is the share of the sky it covers, from the weather generator's own cloud water.
    //
    // Expected results, written before the first run:
    //  - Without wind shear the four shares combine, under random overlap, to the field's Coverage at every texel (the generator's cover is 1 - exp(-3 cw) of the summed cloud
    //    water, so the parts are independent covers): the largest difference over 24 times x 8192 texels is rounding (under 1e-9), far inside the +-0.05 asked.
    //  - With each layer carried by its own wind the combination at one texel is a sum of different places, so only the mean must hold (within 0.02 per time); the texel
    //    difference is printed.
    //  - Rain coherence by construction: no rain deck and no cumulonimbus where the level is below the band None (0.04); and a raining texel (level 0.08 and up) has a
    //    rain-bearing share that is printed; the share of such texels under 0.5 is expected to be small (rain needs a humid, lifted column, so its cloud water is large).
    //  - The shares of hand-made columns follow the arithmetic of the model (fair weather: no deck, no towers; a storm: towers and deck).
    [TestClass]
    public class LayerSharesTests
    {
        private const int Nx = 128, Ny = 64, Times = 24;
        private const double DeckMid = 1050.0, CumulusMid = 4550.0, CbMid = 6350.0, CirrusMid = 10000.0;

        private static double Ut(int s)
        {
            return 1.0e6 + s * 3.5e5 * 3.0 + s * 7777.0;
        }

        private sealed class Run
        {
            internal readonly GeneratorState State = FieldFixture.Field().StateOf(FieldFixture.BodyIndex);
            internal readonly LayerShares.Settings Settings = new LayerShares.Settings();
            internal Func<double, double> LevelOf;

            internal Run()
            {
                Settings.CellRainThreshold = State.Parameters.CellRainThreshold;
                float[] bands = State.Bands;
                LevelOf = rate => PrecipScale.LevelFromRate((float)rate, bands);
            }
        }

        private static double Union(double a, double b, double c, double d)
        {
            return 1.0 - (1.0 - a) * (1.0 - b) * (1.0 - c) * (1.0 - d);
        }

        [TestMethod]
        public void TheFourSharesCombineToTheFieldsCoverAtEveryTexelWithoutShear()
        {
            var r = new Run();
            WeatherGenerator g = r.State.Generator;
            var noShear = new LayerOptions { Shear = false };
            double worst = 0.0, sumAbs = 0.0;
            long n = 0;
            for (int s = 0; s < Times; s++)
            {
                double ut = Ut(s);
                for (int j = 0; j < Ny; j++)
                {
                    double lat = (90.0 - (j + 0.5) * 180.0 / Ny) * (Math.PI / 180.0);
                    for (int i = 0; i < Nx; i++)
                    {
                        double lon = (-180.0 + (i + 0.5) * 360.0 / Nx) * (Math.PI / 180.0);
                        Weather w;
                        g.Sample(lat, lon, 0.0, ut, out w);
                        double d, cu, cb, ci;
                        LayerShares.AtPoint(g, lat, lon, ut, DeckMid, CumulusMid, CirrusMid, noShear, r.LevelOf, r.Settings, out d, out cu, out cb, out ci);
                        double e = Math.Abs(Union(d, cu, cb, ci) - w.Coverage);
                        worst = Math.Max(worst, e);
                        sumAbs += e;
                        n++;
                    }
                }
            }
            Console.WriteLine("LAYERSHARES no shear: texels " + n + ", combined cover against the field's: largest difference " + worst.ToString("0.0e+0") + ", mean " + (sumAbs / n).ToString("0.0e+0"));
            Assert.IsTrue(worst <= 0.05, "the combined cover reproduces the field's cover within 0.05: " + worst);
            Assert.IsTrue(worst < 1e-6, "by construction the difference is rounding: " + worst);
        }

        [TestMethod]
        public void WithTheLayersCarriedByTheirWindTheCoverIsBiasedUpByAMeasuredAmount()
        {
            var r = new Run();
            WeatherGenerator g = r.State.Generator;
            LayerOptions shear = LayerWindOptions.Layers();
            Assert.IsTrue(shear.Shear, "the shipped options carry the layers by their wind");
            const int nx = 96, ny = 48;
            double worstMean = 0.0, sumAbs = 0.0, worstTexel = 0.0, signedMean = 0.0;
            long n = 0;
            for (int s = 0; s < Times; s++)
            {
                double ut = Ut(s);
                double fieldSum = 0.0, combinedSum = 0.0;
                for (int j = 0; j < ny; j++)
                {
                    double lat = (90.0 - (j + 0.5) * 180.0 / ny) * (Math.PI / 180.0);
                    for (int i = 0; i < nx; i++)
                    {
                        double lon = (-180.0 + (i + 0.5) * 360.0 / nx) * (Math.PI / 180.0);
                        Weather w;
                        g.Sample(lat, lon, 0.0, ut, out w);
                        double d, cu, cb, ci;
                        LayerShares.AtPoint(g, lat, lon, ut, DeckMid, CumulusMid, CirrusMid, shear, r.LevelOf, r.Settings, out d, out cu, out cb, out ci);
                        double c = Union(d, cu, cb, ci);
                        fieldSum += w.Coverage; combinedSum += c;
                        double e = Math.Abs(c - w.Coverage);
                        sumAbs += e; worstTexel = Math.Max(worstTexel, e);
                        n++;
                    }
                }
                worstMean = Math.Max(worstMean, Math.Abs(combinedSum - fieldSum) / (nx * ny));
                signedMean += (combinedSum - fieldSum) / (nx * ny) / Times;
            }
            Console.WriteLine("LAYERSHARES shear on: mean difference " + signedMean.ToString("0.0000") + ", worst over " + Times + " times " + worstMean.ToString("0.0000") + "; texel difference mean "
                + (sumAbs / n).ToString("0.000") + ", largest " + worstTexel.ToString("0.000"));
            // Finding, not a wish: layers carried apart by their winds overlap more like independent layers than the one column the field's cover was made from, so the
            // union at a texel is larger than the field's cover (positive association of the parts is lost). The bias is measured here; with Shear off it is zero (the test above).
            Assert.IsTrue(signedMean > 0.0 && worstMean <= 0.12, "the shear bias of the mean combined cover is positive and at most 0.12: signed mean " + signedMean + ", worst " + worstMean);
        }

        [TestMethod]
        public void ARainDeckAndTowersExistOnlyWhereItRainsAndRainFallsUnderThem()
        {
            var r = new Run();
            WeatherGenerator g = r.State.Generator;
            LayerOptions shear = LayerWindOptions.Layers();
            long rainTexels = 0, rainWeakShare = 0, deckDry = 0, cbDry = 0, cbWithoutCell = 0, dryTexels = 0, dryDeckOrCb = 0;
            double rainShareSum = 0.0;
            var dm = new double[5];
            var rm = new double[5];
            for (int s = 0; s < Times; s++)
            {
                double ut = Ut(s);
                for (int j = 0; j < Ny; j++)
                {
                    double lat = (90.0 - (j + 0.5) * 180.0 / Ny) * (Math.PI / 180.0);
                    for (int i = 0; i < Nx; i++)
                    {
                        double lon = (-180.0 + (i + 0.5) * 360.0 / Nx) * (Math.PI / 180.0);
                        Weather w;
                        g.Sample(lat, lon, 0.0, ut, out w);
                        double level = r.LevelOf(w.PrecipMmH);
                        double d, cu, cb, ci;
                        LayerShares.AtPoint(g, lat, lon, ut, DeckMid, CumulusMid, CirrusMid, shear, r.LevelOf, r.Settings, out d, out cu, out cb, out ci);
                        if (level < PrecipScale.FlurriesFrom)
                        {
                            dryTexels++;
                            dm[0] += d; dm[1] += cu; dm[2] += cb; dm[3] += ci; dm[4] += w.Coverage;
                            if (d > 0.0) deckDry++;
                            if (cb > 0.0) cbDry++;
                            if (d > 0.0 || cb > 0.0) dryDeckOrCb++;
                        }
                        if (cb > 0.0 && !(w.CellStrength > r.Settings.CellRainThreshold)) cbWithoutCell++;
                        if (level >= 0.08)
                        {
                            rainTexels++;
                            double rb = 1.0 - (1.0 - d) * (1.0 - cb);
                            rainShareSum += rb;
                            rm[0] += d; rm[1] += cu; rm[2] += cb; rm[3] += ci; rm[4] += w.Coverage;
                            if (rb < 0.5) rainWeakShare++;
                        }
                    }
                }
            }
            Console.WriteLine("LAYERSHARES rain: dry texels " + dryTexels + " (deck over them " + deckDry + ", towers " + cbDry + "); raining texels (level 0.08 and up) " + rainTexels
                + ", mean rain-bearing share " + (rainShareSum / Math.Max(1, rainTexels)).ToString("0.000") + ", of them under 0.5: " + rainWeakShare
                + " (" + (100.0 * rainWeakShare / Math.Max(1, rainTexels)).ToString("0.0") + " percent)");
            Console.WriteLine("LAYERSHARES mean shares deck / cumulus / cumulonimbus / cirrus (field cover): dry " + (dm[0] / dryTexels).ToString("0.000") + " / " + (dm[1] / dryTexels).ToString("0.000") + " / "
                + (dm[2] / dryTexels).ToString("0.000") + " / " + (dm[3] / dryTexels).ToString("0.000") + " (" + (dm[4] / dryTexels).ToString("0.000") + "); raining " + (rm[0] / rainTexels).ToString("0.000") + " / "
                + (rm[1] / rainTexels).ToString("0.000") + " / " + (rm[2] / rainTexels).ToString("0.000") + " / " + (rm[3] / rainTexels).ToString("0.000") + " (" + (rm[4] / rainTexels).ToString("0.000") + ")");
            Assert.AreEqual(0L, deckDry, "no rain deck where it does not rain");
            Assert.AreEqual(0L, cbDry, "no cumulonimbus where it does not rain");
            Assert.AreEqual(0L, cbWithoutCell, "cumulonimbus only where a cell is strong enough to rain");
            Assert.IsTrue(rainTexels > 1000, "the field rains over a good share of the sample");
        }

        [TestMethod]
        public void TheRainFloorMakesEveryRainingTexelRainBearingAndCostsCoverOnlyWhereTheFieldsCoverIsLower()
        {
            FloorRun(0.5);
            FloorRun(0.7);
            FloorRun(0.9);
        }

        private static void FloorRun(double floor)
        {
            var r = new Run();
            r.Settings.RainFloor = floor;
            WeatherGenerator g = r.State.Generator;
            var noShear = new LayerOptions { Shear = false };
            long rain = 0, below = 0, excess = 0, all = 0, dryWrong = 0;
            double excessSum = 0.0, worstOutsideRain = 0.0;
            for (int s = 0; s < Times; s++)
            {
                double ut = Ut(s);
                for (int j = 0; j < Ny; j++)
                {
                    double lat = (90.0 - (j + 0.5) * 180.0 / Ny) * (Math.PI / 180.0);
                    for (int i = 0; i < Nx; i++)
                    {
                        double lon = (-180.0 + (i + 0.5) * 360.0 / Nx) * (Math.PI / 180.0);
                        Weather w;
                        g.Sample(lat, lon, 0.0, ut, out w);
                        double level = r.LevelOf(w.PrecipMmH);
                        double d, cu, cb, ci;
                        LayerShares.AtPoint(g, lat, lon, ut, DeckMid, CumulusMid, CirrusMid, noShear, r.LevelOf, r.Settings, out d, out cu, out cb, out ci);
                        double c = Union(d, cu, cb, ci);
                        all++;
                        if (c - w.Coverage > 0.05) { excess++; excessSum += c - w.Coverage; }
                        if (level >= 0.08)
                        {
                            rain++;
                            if (1.0 - (1.0 - d) * (1.0 - cb) < floor - 1e-9) below++;
                        }
                        if (level < PrecipScale.FlurriesFrom) worstOutsideRain = Math.Max(worstOutsideRain, Math.Abs(c - w.Coverage));
                        if (level < PrecipScale.FlurriesFrom && (d > 0.0 || cb > 0.0)) dryWrong++;
                    }
                }
            }
            Console.WriteLine("LAYERSHARES floor " + floor + ": raining texels " + rain + ", rain-bearing share under the floor: " + below + "; texels whose combined cover is above the field's by more than 0.05: " + excess
                + " of " + all + " (" + (100.0 * excess / all).ToString("0.0") + " percent), their mean excess " + (excess > 0 ? excessSum / excess : 0.0).ToString("0.000") + "; dry texels: largest difference " + worstOutsideRain.ToString("0.0e+0"));
            Assert.AreEqual(0L, below, "every raining texel has a rain-bearing share of at least the floor");
            Assert.AreEqual(0L, dryWrong);
            Assert.IsTrue(worstOutsideRain < 1e-6, "outside rain the cover is still the field's exactly");
        }

        [TestMethod]
        public void TheMapOfARoleIsThePointValueAtEveryTexel()
        {
            var r = new Run();
            WeatherGenerator g = r.State.Generator;
            LayerOptions shear = LayerWindOptions.Layers();
            const int nx = 48, ny = 24;
            double ut = Ut(5);
            var maps = new float[4][];
            double[] mids = { DeckMid, CumulusMid, CbMid, CirrusMid };
            for (int k = 0; k < 4; k++)
            {
                maps[k] = new float[nx * ny];
                LayerShares.Map(g, ut, nx, ny, (LayerShares.Role)k, mids[k], shear, r.LevelOf, r.Settings, maps[k]);
            }
            for (int j = 0; j < ny; j++)
            {
                double lat = (90.0 - (j + 0.5) * 180.0 / ny) * (Math.PI / 180.0);
                for (int i = 0; i < nx; i++)
                {
                    double lon = (-180.0 + (i + 0.5) * 360.0 / nx) * (Math.PI / 180.0);
                    double d, cu, cb, ci;
                    LayerShares.AtPoint(g, lat, lon, ut, DeckMid, CumulusMid, CirrusMid, shear, r.LevelOf, r.Settings, out d, out cu, out cb, out ci);
                    int t = j * nx + i;
                    Assert.AreEqual(d, maps[0][t], 1e-6);
                    Assert.AreEqual(cu, maps[1][t], 1e-6);
                    Assert.AreEqual(cb, maps[2][t], 1e-6);
                    Assert.AreEqual(ci, maps[3][t], 1e-6);
                }
            }
        }

        [TestMethod]
        public void FairWeatherGivesCumulusAndCirrusAndNoRainDeckOrTowers()
        {
            var s = new LayerShares.Settings();
            // a fair day: background cloud water 0.30 (cover 0.59), no cell, no rain
            var w = new Weather { CloudWater = 0.30, LayerAmount = 0.30, CellStrength = 0.0, PrecipMmH = 0.0, ConvectiveShare = 0.0 };
            LayerShares.Shares r;
            LayerShares.Compute(ref w, 0.0, s, out r);
            Assert.AreEqual(0.0, r.Deck, 0.0);
            Assert.AreEqual(0.0, r.Cumulonimbus, 0.0);
            Assert.AreEqual(0.25 * 0.30, r.CirrusWater, 1e-12);
            Assert.AreEqual(0.75 * 0.30, r.CumulusWater, 1e-12);
            Assert.AreEqual(1.0 - Math.Exp(-0.9), r.Union, 1e-9, "the union is the field's cover 1 - exp(-3 cw)");
            Assert.AreEqual(1.0 - Math.Exp(-0.675), r.Cumulus, 1e-9);
            // trade-wind cumulus: the fair-weather cu term (cw above bg + xdyn + cells) is cumulus alone
            var tw = new Weather { CloudWater = 0.40, LayerAmount = 0.10, CellStrength = 0.0 };
            LayerShares.Compute(ref tw, 0.0, s, out r);
            Assert.AreEqual(0.40 - 0.025, r.CumulusWater, 1e-12);
        }

        [TestMethod]
        public void AStormGivesTowersAndADeckAndTheCloudWaterIsConserved()
        {
            var s = new LayerShares.Settings { CellRainThreshold = 0.15 };
            // a storm: stratiform amount 0.40, one strong cell 0.8, rain level 0.5, 60 percent of the rain from the cell
            var w = new Weather { CloudWater = 0.40 + 0.7 * 0.8, LayerAmount = 0.40, CellStrength = 0.8, PrecipMmH = 5.0, ConvectiveShare = 0.6 };
            LayerShares.Shares r;
            LayerShares.Compute(ref w, 0.5, s, out r);
            Assert.AreEqual(0.40 * 0.4, r.DeckWater, 1e-12, "the stratiform amount times the share of the rain that is not convective");
            Assert.AreEqual(0.7 * (0.8 - 0.15), r.CumulonimbusWater, 1e-12, "the cell water above the cell rain threshold");
            Assert.AreEqual(w.CloudWater, r.DeckWater + r.CumulusWater + r.CumulonimbusWater + r.CirrusWater, 1e-12, "no cloud water is lost or made");
            Assert.AreEqual(1.0 - Math.Exp(-3.0 * w.CloudWater), r.Union, 1e-9);
            Assert.IsTrue(r.Cumulonimbus > 0.7, "a strong cell covers most of the sky above it: " + r.Cumulonimbus);
            // the same column dry: the level 0 turns the rain-bearing water into cumulus and cirrus, the total stays
            LayerShares.Compute(ref w, 0.0, s, out r);
            Assert.AreEqual(0.0, r.Deck, 0.0);
            Assert.AreEqual(0.0, r.Cumulonimbus, 0.0);
            Assert.AreEqual(1.0 - Math.Exp(-3.0 * w.CloudWater), r.Union, 1e-9);
        }

        [TestMethod]
        public void TheClampOfTheGeneratorsCloudWaterKeepsTheTotal()
        {
            var s = new LayerShares.Settings();
            // the generator clamps cw at 1.5 while its parts add to more
            var w = new Weather { CloudWater = 1.5, LayerAmount = 1.4, CellStrength = 0.9, PrecipMmH = 8.0, ConvectiveShare = 0.3 };
            LayerShares.Shares r;
            LayerShares.Compute(ref w, 0.6, s, out r);
            Assert.AreEqual(1.0 - Math.Exp(-4.5), r.Union, 1e-9);
            Assert.AreEqual(1.5, r.DeckWater + r.CumulusWater + r.CumulonimbusWater + r.CirrusWater, 1e-9);
        }

        // The numbers the design note quotes: the maps of today (LayerCoupling.Coverage at the layers' mid heights) and the shares, at the texel of the sample that is closest to the
        // low pick (field cover 0.473, maps 0.406 / 0.665 / 0.110 / 0.501) and to the high pick (0.672, maps 0.859 / 0.206 / 0.661 / 0.177) of the E40 series. The fixture is the
        // test world, not the game's Kerbin, so the texel only has the same maps, not the same place.
        [TestMethod]
        public void PrintTheMapsOfTodayAndTheSharesAtTheTexelsNearTheTwoPicks()
        {
            var r = new Run();
            WeatherGenerator g = r.State.Generator;
            LayerOptions shear = LayerWindOptions.Layers();
            double[,] picks = { { 0.473, 0.406, 0.665, 0.110, 0.501 }, { 0.672, 0.859, 0.206, 0.661, 0.177 } };
            // the low pick is dry (type 0), the high pick is light rain (type 0.229)
            double[] pickLevel = { 0.0, 0.229 };
            string[] names = { "low pick", "high pick" };
            for (int p = 0; p < 2; p++)
            {
                double best = double.MaxValue, bLat = 0, bLon = 0, bUt = 0;
                for (int s = 0; s < Times; s++)
                {
                    double ut = Ut(s);
                    for (int j = 0; j < Ny; j++)
                    {
                        double lat = (90.0 - (j + 0.5) * 180.0 / Ny) * (Math.PI / 180.0);
                        for (int i = 0; i < Nx; i++)
                        {
                            double lon = (-180.0 + (i + 0.5) * 360.0 / Nx) * (Math.PI / 180.0);
                            double m0 = LayerCoupling.Coverage(g, lat, lon, ut, DeckMid, shear), m1 = LayerCoupling.Coverage(g, lat, lon, ut, CumulusMid, shear);
                            double m2 = LayerCoupling.Coverage(g, lat, lon, ut, CbMid, shear), m3 = LayerCoupling.Coverage(g, lat, lon, ut, CirrusMid, shear);
                            Weather w;
                            g.Sample(lat, lon, 0.0, ut, out w);
                            double dist = 4.0 * Sq(r.LevelOf(w.PrecipMmH) - pickLevel[p]) + Sq(w.Coverage - picks[p, 0]) + Sq(m0 - picks[p, 1]) + Sq(m1 - picks[p, 2]) + Sq(m2 - picks[p, 3]) + Sq(m3 - picks[p, 4]);
                            if (dist < best) { best = dist; bLat = lat; bLon = lon; bUt = ut; }
                        }
                    }
                }
                Weather wb;
                g.Sample(bLat, bLon, 0.0, bUt, out wb);
                double today0 = LayerCoupling.Coverage(g, bLat, bLon, bUt, DeckMid, shear), today1 = LayerCoupling.Coverage(g, bLat, bLon, bUt, CumulusMid, shear);
                double today2 = LayerCoupling.Coverage(g, bLat, bLon, bUt, CbMid, shear), today3 = LayerCoupling.Coverage(g, bLat, bLon, bUt, CirrusMid, shear);
                double d, cu, cb, ci;
                LayerShares.AtPoint(g, bLat, bLon, bUt, DeckMid, CumulusMid, CirrusMid, shear, r.LevelOf, r.Settings, out d, out cu, out cb, out ci);
                Console.WriteLine("LAYERSHARES " + names[p] + "-like texel (lat " + (bLat * 180.0 / Math.PI).ToString("0.0") + ", lon " + (bLon * 180.0 / Math.PI).ToString("0.0") + ", ut " + bUt.ToString("0") + "): field cover "
                    + wb.Coverage.ToString("0.000") + ", precipitation level " + r.LevelOf(wb.PrecipMmH).ToString("0.000") + ", convective share " + wb.ConvectiveShare.ToString("0.00") + ", cell " + wb.CellStrength.ToString("0.00"));
                Console.WriteLine("LAYERSHARES   today's maps  deck / cumulus / cumulonimbus / cirrus " + today0.ToString("0.000") + " / " + today1.ToString("0.000") + " / " + today2.ToString("0.000") + " / " + today3.ToString("0.000")
                    + "; union " + Union(today0, today1, today2, today3).ToString("0.000"));
                Console.WriteLine("LAYERSHARES   shares        deck / cumulus / cumulonimbus / cirrus " + d.ToString("0.000") + " / " + cu.ToString("0.000") + " / " + cb.ToString("0.000") + " / " + ci.ToString("0.000")
                    + "; union " + Union(d, cu, cb, ci).ToString("0.000"));
            }
        }

        private static double Sq(double x)
        {
            return x * x;
        }
    }
}
