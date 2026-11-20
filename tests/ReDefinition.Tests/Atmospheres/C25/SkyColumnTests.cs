using System;
using System.Collections.Generic;
using System.Linq;
using AtmoSim.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Core;

namespace ReDefinition.Tests.Atmospheres.C25
{
    // Package B of the cloud master plan: what the weather field gives the new sky per column (SkyColumn) and as maps. The existing outputs stay bit for bit
    // (GeneratorCoreTests' golden hashes; here the maps with and without the sky maps).
    [TestClass]
    public class SkyColumnTests
    {
        private const int Body = FieldFixture.BodyIndex;

        // A spread of columns of a generator: 64 x 32 points at three times, with the sky inputs.
        private static List<Weather> Columns(WeatherGenerator g)
        {
            var list = new List<Weather>();
            foreach (double ut in new[] { 1.0e6, 4.0e6, 1.3e7 })
            {
                for (int j = 0; j < 32; j++)
                {
                    double lat = (90.0 - (j + 0.5) * 180.0 / 32) * Math.PI / 180.0;
                    for (int i = 0; i < 64; i++)
                    {
                        Weather w;
                        g.Sample(lat, (-180.0 + (i + 0.5) * 360.0 / 64) * Math.PI / 180.0, 0.0, ut, out w, true);
                        list.Add(w);
                    }
                }
            }
            return list;
        }

        private static SkyColumn Derive(BodyParams p, Weather w)
        {
            SkyColumn s;
            SkyColumn.Derive(p, ref w, out s);
            return s;
        }

        [TestMethod]
        public void TheRendererAndTheGeneratorShareOneDewPoint()
        {
            for (double t = 250.0; t <= 320.0; t += 5.0)
                for (double rh = 0.05; rh <= 1.0; rh += 0.05)
                    Assert.AreEqual(HeightModel.DewPointK(t, rh), MoistAir.DewPointK(t, rh), 1e-9, t + " K, " + rh);
        }

        [TestMethod]
        public void TheSkyInputsLeaveEverythingElseOfASampleAsItWas()
        {
            var g = new WeatherGenerator(BodyParams.KerbinLike(), 2);
            var r = new Rng(9);
            for (int i = 0; i < 3000; i++)
            {
                double lat = DMath.Asin(2.0 * r.Uniform() - 1.0), lon = (2.0 * r.Uniform() - 1.0) * Math.PI, alt = 9000.0 * r.Uniform(), ut = 3.0e7 * r.Uniform();
                Weather a, b;
                g.Sample(lat, lon, alt, ut, out a);
                g.Sample(lat, lon, alt, ut, out b, true);
                Assert.AreEqual(WeatherGenerator.HashOf(ref a, 1), WeatherGenerator.HashOf(ref b, 1));
                Assert.AreEqual(a.Coverage, b.Coverage); Assert.AreEqual(a.Instability, b.Instability); Assert.AreEqual(a.Density3D, b.Density3D);
                Assert.AreEqual(a.CellPhase, b.CellPhase); Assert.AreEqual(a.SurfaceTempK, b.SurfaceTempK);
                Assert.AreEqual(0.0, a.CellReach01 + a.MoistureIndex + a.FrontAmp01 + a.CloudBackground, "no sky inputs without the flag");
                // the parts of the cloud water add up to it
                Assert.AreEqual(Math.Min(1.5, b.CloudBackground + b.CloudDynamic + b.CloudConvective), b.CloudWater, 1e-12);
            }
        }

        [TestMethod]
        public void TheConvectiveBaseIsLawrencesRuleOnEarthAndScalesWithTheBody()
        {
            // Lawrence (2005, eq. 22): z_LCL ~ 125 (t - td), accurate to about 2 % for RH 50 to 100 % and 0 to 30 C (his Table 3 against an iterative parcel).
            BodyParams earth = BodyParams.KerbinLike();
            for (double tc = 0.0; tc <= 30.0; tc += 5.0)
            {
                for (double rh = 0.5; rh < 0.999; rh += 0.1)
                {
                    double t = 273.15 + tc, td = MoistAir.DewPointK(t, rh);
                    Assert.AreEqual(125.0, MoistAir.LclMetresPerKelvin(earth, t, td), 125.0 * 0.03, tc + " C, " + rh);
                }
            }
            Assert.AreEqual(0.0, SkyColumn.ConvectiveBaseM(earth, 290.0, MoistAir.DewPointK(290.0, 1.0)), 1e-9, "saturated air: base at the ground");
            double last = -1.0;
            for (double rh = 1.0; rh >= 0.1; rh -= 0.05)
            {
                double zb = SkyColumn.ConvectiveBaseM(earth, 290.0, MoistAir.DewPointK(290.0, rh));
                Assert.IsTrue(zb > last && zb <= earth.TropopauseM, "drier air, higher base: " + rh);
                last = zb;
            }
        }

        [TestMethod]
        public void TheSurfaceAirIsBelowSaturationSoTheBaseIsAboveTheGround()
        {
            BodyParams p = BodyParams.KerbinLike();
            // the open sea under a saturated free troposphere: the entrained air still dries the surface air (its water at the layer top is less than the surface's saturation)
            double sea = SkyColumn.SurfaceHumidityOf(p, 295.0, 1.0, 1.0, 1000.0);
            Assert.IsTrue(sea < 1.0 && sea > 0.9, "sea, moist above: " + sea);
            double dry = SkyColumn.SurfaceHumidityOf(p, 295.0, 1.0, 0.0, 1000.0);
            Assert.AreEqual(1.0 - p.SkyEntrainmentShare, dry, 1e-12, "sea, dry above: 1 - lambda (Bretherton eq. 16.38b)");
            int columns = 0;
            var bases = new List<double>();
            foreach (Weather w in Columns(new WeatherGenerator(p, 4)))
            {
                SkyColumn s = Derive(p, w);
                Assert.IsTrue(s.SurfaceHumidity < 1.0, "surface humidity " + s.SurfaceHumidity);
                Assert.IsTrue(s.BaseM > 0.0, "base " + s.BaseM + " (a base at the ground is fog, which is the deck rule's)");
                bases.Add(s.BaseM);
                columns++;
            }
            bases.Sort();
            Console.WriteLine("base m: 5 % " + bases[columns / 20].ToString("0") + ", median " + bases[columns / 2].ToString("0") + ", 95 % " + bases[19 * columns / 20].ToString("0"));
            Assert.IsTrue(bases[columns / 20] > 50.0, "the lowest bases stay above the ground");
        }

        // The body with twice the troposphere: half the gravity (and half the lapse rate), so the same surface air gives the same weather; every height doubles.
        private static BodyParams Twice(BodyParams b)
        {
            BodyParams q = b.Clone();
            q.GravityMs2 = b.GravityMs2 / 2.0;
            q.LapseKPerM = b.LapseKPerM / 2.0;
            q.TropopauseM = b.TropopauseM * 2.0;
            return q;
        }

        [TestMethod]
        public void TwiceTheTroposphereGivesTwiceTheHeights()
        {
            // The same weather (the same column state) on both bodies: the generator's own climate of the second body differs (its lapse rate enters the temperature), so the
            // columns of the first are derived on both.
            BodyParams p = BodyParams.KerbinLike(), q = Twice(p);
            List<Weather> a = Columns(new WeatherGenerator(p, 1));
            int deep = 0;
            foreach (Weather w in a)
            {
                SkyColumn sa = Derive(p, w), sb = Derive(q, w);
                Assert.AreEqual(2.0 * sa.BaseM, sb.BaseM, 1e-9 * (1.0 + sa.BaseM));
                Assert.AreEqual(2.0 * sa.TopM, sb.TopM, 1e-9 * (1.0 + sa.TopM));
                Assert.AreEqual(2.0 * sa.StableM, sb.StableM, 1e-9 * (1.0 + sa.StableM));
                Assert.AreEqual(sa.BaseShare, sb.BaseShare, 1e-12);
                Assert.AreEqual(sa.TopShare, sb.TopShare, 1e-12);
                Assert.AreEqual(sa.SurfaceHumidity, sb.SurfaceHumidity, 1e-12);
                Assert.AreEqual(sa.RhLow, sb.RhLow, 1e-9);
                Assert.AreEqual(sa.RhMid, sb.RhMid, 1e-9);
                Assert.AreEqual(sa.RhHigh, sb.RhHigh, 1e-9);
                Assert.AreEqual(sa.HeapAmount, sb.HeapAmount, 1e-12);
                if (sa.TopM > sa.BaseM + 100.0) deep++;
            }
            Assert.IsTrue(deep > 100, "columns with convection: " + deep);
        }

        [TestMethod]
        public void EveryQuantityStaysInItsRange()
        {
            BodyParams p = BodyParams.KerbinLike();
            int warm = 0, cold = 0, shallow = 0, storm = 0, ascent = 0;
            foreach (Weather w in Columns(new WeatherGenerator(p, 3)))
            {
                SkyColumn s = Derive(p, w);
                Assert.IsTrue(s.BaseM > 0.0 && s.BaseM <= s.TopM && s.TopM <= p.TropopauseM, "base " + s.BaseM + " top " + s.TopM);
                Assert.IsTrue(s.StableShare >= p.SkyStableShareMin && s.StableShare <= p.SkyStableShare + 1e-12, "stable " + s.StableShare);
                Assert.IsTrue(s.HeapAmount >= 0.0 && s.SheetAmount >= -1e-12 && s.DeckAmount >= 0.0 && s.DeckAmount <= s.SheetAmount + 1e-12);
                Assert.AreEqual(w.Coverage, s.HeapAmount + s.SheetAmount, 1e-12, "heap plus sheet is the column's cover");
                Assert.IsTrue(s.HeapAmount + s.SheetAmount <= 1.0 + 1e-12);
                foreach (double rh in new[] { s.RhLow, s.RhMid, s.RhHigh, s.SurfaceHumidity }) Assert.IsTrue(rh >= 0.0 && rh <= 1.0, "rh " + rh);
                foreach (double f in new[] { s.WarmFront01, s.ColdFront01, s.FrontAmp01, s.FrontSurfaceShare }) Assert.IsTrue(f >= 0.0 && f <= 1.0, "front " + f);
                Assert.IsTrue(w.CellReach01 >= 0.0 && w.CellReach01 <= 1.0);
                if (s.WarmFront01 > 0.3) warm++;
                if (s.ColdFront01 > 0.3) cold++;
                if (w.CellReach01 > 0.3) storm++;
                else if (s.TopM > s.BaseM + 200.0) shallow++;
                if (s.Ascent01 > 0.0) ascent++;
            }
            Console.WriteLine("warm " + warm + ", cold " + cold + ", shallow " + shallow + ", storm " + storm + ", net ascent " + ascent);
            Assert.IsTrue(warm > 10 && cold > 10, "both kinds of front");
            Assert.IsTrue(shallow > 10 && storm > 0, "shallow and deep convection");
        }

        // The game's Kerbin parameter set (from the dumps): what the columns look like there, for the report.
        [TestMethod]
        public void TheKerbinColumnsArePrinted()
        {
            GeneratorState state = FieldFixture.Field().StateOf(Body);
            BodyParams p = state.Parameters;
            var lists = new Dictionary<string, List<double>>();
            Action<string, double> add = (k, v) => { if (!lists.ContainsKey(k)) lists[k] = new List<double>(); lists[k].Add(v); };
            foreach (Weather w in Columns(state.Generator))
            {
                SkyColumn s = Derive(p, w);
                add("base m", s.BaseM); add("stable m", s.StableM); add("top m", s.TopM); add("shallow depth m", s.TopM - s.BaseM); add("surface RH", s.SurfaceHumidity);
                add("heap", s.HeapAmount); add("sheet", s.SheetAmount); add("RH low", s.RhLow); add("RH mid", s.RhMid); add("RH high", s.RhHigh);
            }
            Console.WriteLine("Kerbin: tropopause " + p.TropopauseM.ToString("0") + " m; quartiles:");
            foreach (KeyValuePair<string, List<double>> kv in lists)
            {
                List<double> l = kv.Value;
                l.Sort();
                Console.WriteLine("  " + kv.Key + ": " + l[l.Count / 4].ToString("0.00") + " / " + l[l.Count / 2].ToString("0.00") + " / " + l[3 * l.Count / 4].ToString("0.00"));
            }
            Assert.IsTrue(lists["base m"].Count > 0);
        }

        [TestMethod]
        public void MoreInstabilityGivesADeeperTopAndStrongerDescentOrInversionALowerStableLayer()
        {
            BodyParams p = BodyParams.KerbinLike();
            var w = new Weather { SurfaceTempK = 295.0, MoistureSource = 1.0, MoistureIndex = 0.8, Heating01 = 1.0, Descent01 = 0.1, Coverage = 0.5, CloudBackground = 0.2 };
            double last = -1.0;
            for (double inst = 0.0; inst <= 1.0; inst += 0.05)
            {
                w.Instability = inst;
                SkyColumn s = Derive(p, w);
                Assert.IsTrue(s.TopM >= last, "instability " + inst);
                Assert.IsTrue(s.TopM <= s.StableM + 1e-9, "shallow convection stops at the stable layer");
                if (inst < 0.99) Assert.IsTrue(s.TopM < s.StableM - 1.0, "only the top of the index reaches the stable layer: " + inst);
                last = s.TopM;
            }
            w.Instability = 0.3;
            last = -1.0;
            for (double reach = 0.0; reach <= 1.0; reach += 0.1)
            {
                w.CellReach01 = reach;
                SkyColumn s = Derive(p, w);
                Assert.IsTrue(s.TopM >= last && s.TopM <= p.TropopauseM, "cell reach " + reach);
                last = s.TopM;
            }
            Assert.AreEqual(p.TropopauseM, last, 1e-6, "a full cell at mid-life reaches the tropopause");

            double previous = 2.0;
            foreach (double gain in new[] { 0.0, 0.5, 1.0, 2.0, 4.0 })
            {
                BodyParams q = p.Clone();
                q.SkyInversionGain = gain;
                double s = SkyColumn.StableShareOf(q, 1.0, 0.6, 0.0);
                Assert.IsTrue(s < previous, "a stronger inversion, a lower stable layer: " + gain);
                previous = s;
            }
            previous = 2.0;
            for (double d = 0.0; d <= 1.0; d += 0.1)
            {
                double s = SkyColumn.StableShareOf(p, 1.0, d, 0.0);
                Assert.IsTrue(s <= previous, "more descent, lower: " + d);
                previous = s;
            }
            Assert.AreEqual(p.SkyStableShare, SkyColumn.StableShareOf(p, 1.0, 0.0, 0.0), 1e-12, "the reference");
            Assert.AreEqual(p.SkyStableShareMin, SkyColumn.StableShareOf(p, 0.0, 0.3, 0.0), 1e-12, "no heating: the floor");
        }

        [TestMethod]
        public void TheRegimesJoinWithoutAJumpAndAscentDoesNotLiftTheShallowTop()
        {
            BodyParams p = BodyParams.KerbinLike();
            // across the edge of the equilibrium (net descent 0): continuous; in net ascent the share keeps its value at the edge
            double atEdge = SkyColumn.StableShareOf(p, 0.8, 0.0, 0.0);
            for (double u = 0.0; u <= 1.4; u += 0.1) Assert.AreEqual(atEdge, SkyColumn.StableShareOf(p, 0.8, 0.0, u), 1e-12, "uplift " + u);
            double prev = SkyColumn.StableShareOf(p, 0.8, 0.3, 0.0);
            for (double u = 0.0; u <= 0.6; u += 0.005)
            {
                double s = SkyColumn.StableShareOf(p, 0.8, 0.3, u);
                Assert.IsTrue(Math.Abs(s - prev) < 0.003, "uplift " + u + ": " + prev + " -> " + s);
                prev = s;
            }
            // strong uplift with no cell: the top stays in the lower troposphere (deep convection is the cells')
            var w = new Weather { SurfaceTempK = 295.0, MoistureSource = 1.0, MoistureIndex = 0.8, Heating01 = 1.0, Uplift = 1.4, Instability = 1.0, Coverage = 0.9, CloudDynamic = 1.0 };
            SkyColumn c = Derive(p, w);
            Assert.IsTrue(c.TopShare <= p.SkyStableShare + 1e-12, "top share " + c.TopShare);
            Assert.AreEqual(1.0, c.Ascent01, 1e-12);
            // the deck turns the background off as cumulus, continuously
            double heapPrev = -1.0;
            for (double deck = 1.0; deck >= 0.0; deck -= 0.01)
            {
                w = new Weather { SurfaceTempK = 295.0, MoistureSource = 1.0, MoistureIndex = 0.6, Heating01 = 1.0, Instability = 0.7, CloudBackground = 0.3, CloudDynamic = deck };
                w.Coverage = 1.0 - Math.Exp(-3.0 * (0.3 + deck));
                double heap = Derive(p, w).HeapAmount;
                Assert.IsTrue(heap >= heapPrev - 1e-12 && (heapPrev < 0.0 || heap - heapPrev < 0.02), "deck water " + deck);
                heapPrev = heap;
            }
        }

        [TestMethod]
        public void TheHeapAmountIsTheHeapFamilysShareOfTheSky()
        {
            BodyParams p = BodyParams.KerbinLike();
            // no heap water and a stable column: no heap clouds
            var w = new Weather { SurfaceTempK = 290.0, MoistureSource = 1.0, MoistureIndex = 0.6, Heating01 = 1.0, Descent01 = 0.3, Instability = 0.0, CloudBackground = 0.2, CloudDynamic = 0.3 };
            w.Coverage = 1.0 - Math.Exp(-3.0 * 0.5);
            SkyColumn s = Derive(p, w);
            Assert.AreEqual(0.0, s.HeapAmount, 1e-15);
            Assert.AreEqual(w.Coverage, s.SheetAmount, 1e-12);
            // monotonic in the convective water; heap + sheet = cover <= 1
            double prev = -1.0;
            for (double c = 0.0; c <= 2.0; c += 0.05)
            {
                w.CloudConvective = c;
                w.Coverage = 1.0 - Math.Exp(-3.0 * Math.Min(1.5, 0.5 + c));
                s = Derive(p, w);
                Assert.IsTrue(s.HeapAmount > prev || (c == 0.0 && s.HeapAmount == 0.0), "convective water " + c);
                Assert.IsTrue(s.HeapAmount + s.SheetAmount <= 1.0 + 1e-12);
                prev = s.HeapAmount;
            }
            // only heap water: the whole cover is heap
            w = new Weather { SurfaceTempK = 290.0, MoistureSource = 1.0, MoistureIndex = 0.6, Heating01 = 1.0, CloudConvective = 0.4, Coverage = 1.0 - Math.Exp(-1.2) };
            Assert.AreEqual(w.Coverage, Derive(p, w).HeapAmount, 1e-12);
        }

        [TestMethod]
        public void TheHumidityProfileIsContinuousInHeightAndTime()
        {
            BodyParams p = BodyParams.KerbinLike();
            var g = new WeatherGenerator(p, 5);
            int checkedColumns = 0;
            foreach (Weather col in Columns(g).Where((c, i) => i % 7 == 0))
            {
                Weather w = col;
                SkyColumn s = Derive(p, w);
                // the largest step at a resolution of 1/1000 of the troposphere, then that stretch at 1/100 000: a jump keeps its size, a steep smooth edge shrinks with the step
                double prev = SkyColumn.HumidityAt(p, ref w, ref s, 0.0), worst = 0.0, at = 0.0;
                for (double share = 0.001; share <= 1.0; share += 0.001)
                {
                    double rh = SkyColumn.HumidityAt(p, ref w, ref s, share);
                    if (Math.Abs(rh - prev) > worst) { worst = Math.Abs(rh - prev); at = share - 0.001; }
                    prev = rh;
                }
                double fine = 0.0;
                prev = SkyColumn.HumidityAt(p, ref w, ref s, at);
                for (int k = 1; k <= 100; k++)
                {
                    double rh = SkyColumn.HumidityAt(p, ref w, ref s, at + k * 1e-5);
                    fine = Math.Max(fine, Math.Abs(rh - prev));
                    prev = rh;
                }
                Assert.IsTrue(worst < 1e-6 || fine < 0.1 * worst, "column " + checkedColumns + " near " + at + ": step " + worst + ", at the finer step " + fine + " (base " + s.BaseShare + ", stable " + s.StableShare + ", top " + s.TopShare + ")");
                checkedColumns++;
            }
            // in time: one place every 20 s over six hours
            foreach (double latDeg in new[] { 5.0, 30.0, 50.0 })
            {
                SkyColumn before = default(SkyColumn);
                for (int k = 0; k < 1080; k++)
                {
                    Weather w;
                    g.Sample(latDeg * Math.PI / 180.0, 0.4, 0.0, 2.0e6 + 20.0 * k, out w, true);
                    SkyColumn s = Derive(p, w);
                    if (k > 0)
                    {
                        Assert.IsTrue(Math.Abs(s.RhLow - before.RhLow) < 0.03 && Math.Abs(s.RhMid - before.RhMid) < 0.03 && Math.Abs(s.RhHigh - before.RhHigh) < 0.03, latDeg + " step " + k);
                        Assert.IsTrue(Math.Abs(s.TopShare - before.TopShare) < 0.03 && Math.Abs(s.BaseShare - before.BaseShare) < 0.01 && Math.Abs(s.HeapAmount - before.HeapAmount) < 0.03, latDeg + " step " + k);
                    }
                    before = s;
                }
            }
        }

        [TestMethod]
        public void ACellsTowerGrowsAndSinksWithoutAJumpAtBirthAndDeath()
        {
            var g = new WeatherGenerator(BodyParams.KerbinLike(), 1);
            var cells = new List<ConvectiveCell>();
            g.Cells(4.0e6, cells, 0.2, true);
            Assert.IsTrue(cells.Count > 5, "cells: " + cells.Count);
            int births = 0, deaths = 0;
            foreach (ConvectiveCell c in cells.Take(12))
            {
                Assert.IsTrue(c.TopShare > 0.0 && c.TopShare <= 1.0 && c.Energy01 > 0.0);
                // the cell's whole life at its centre, every 10 s (its centre drifts with the steering wind a little over the life: the footprint is wide against it)
                double born = 4.0e6 - c.AgeS, prev = double.NaN;
                for (double t = born - 300.0; t <= born + c.LifeS + 300.0; t += 10.0)
                {
                    Weather w;
                    g.Sample(c.LatRad, c.LonRad, 0.0, t, out w, true);
                    if (!double.IsNaN(prev)) Assert.IsTrue(Math.Abs(w.CellReach01 - prev) < 0.03, "reach " + prev + " -> " + w.CellReach01 + " at " + (t - born) + " s of " + c.LifeS);
                    if (Math.Abs(t - born) < 10.0 && w.CellReach01 < 0.05) births++;
                    if (Math.Abs(t - born - c.LifeS) < 10.0 && w.CellReach01 < 0.05) deaths++;
                    prev = w.CellReach01;
                }
            }
            Console.WriteLine("births near 0: " + births + ", deaths near 0: " + deaths);
            Assert.IsTrue(births > 0 && deaths > 0);
        }

        [TestMethod]
        public void TheFrontsAreContinuousAcrossTheirSwitches()
        {
            var g = new WeatherGenerator(BodyParams.KerbinLike(), 1);
            int nearFronts = 0;
            for (int row = 0; row < 40; row++)
            {
                double lat = (30.0 + row) * Math.PI / 180.0;
                Weather prev;
                g.Sample(lat, -Math.PI, 0.0, 4.0e6, out prev, true);
                for (int i = 1; i < 4000; i++)
                {
                    Weather w;
                    g.Sample(lat, -Math.PI + i * 2.0 * Math.PI / 4000, 0.0, 4.0e6, out w, true);
                    Assert.IsTrue(Math.Abs(w.WarmFront01 - prev.WarmFront01) < 0.05 && Math.Abs(w.ColdFront01 - prev.ColdFront01) < 0.05, "front nearness at " + lat + ", " + i);
                    // the surface share is a mean over the systems; where they are all weak it may move fast, but it only acts through the nearness
                    if (w.FrontAmp01 > 0.2 && prev.FrontAmp01 > 0.2)
                        Assert.IsTrue(Math.Abs(w.FrontSurfaceShare - prev.FrontSurfaceShare) < 0.05, "frontal surface at " + lat + ", " + i + ": " + prev.FrontSurfaceShare + " -> " + w.FrontSurfaceShare);
                    if (w.WarmFront01 > 0.5 || w.ColdFront01 > 0.5) nearFronts++;
                    prev = w;
                }
            }
            Assert.IsTrue(nearFronts > 50, "near fronts: " + nearFronts);
        }

        [TestMethod]
        public void NeighbouringColumnsDifferLittleWhereNoCellStands()
        {
            // The convective cells are smaller than a map texel (radius 3.4 to 6 km against 7.4 km), so a texel at a cell's centre and its neighbour differ in cover, and with it in
            // the heap share and the top, as the Coverage map does already. Everywhere else every new quantity changes from texel to texel by no more than the cover does.
            GeneratorState state = FieldFixture.Field().StateOf(Body);
            BodyParams p = state.Parameters;
            const int W = GeneratorMaps.Width, H = GeneratorMaps.Height;
            var cols = new Weather[W * H];
            var sky = new SkyColumn[W * H];
            for (int j = 0; j < H; j++)
                for (int i = 0; i < W; i++)
                {
                    Weather w;
                    state.Generator.Sample((90.0 - (j + 0.5) * 180.0 / H) * Math.PI / 180.0, (-180.0 + (i + 0.5) * 360.0 / W) * Math.PI / 180.0, 0.0, 4.0e6, out w, true);
                    cols[j * W + i] = w;
                    sky[j * W + i] = Derive(p, w);
                }
            var quantities = new Dictionary<string, Func<int, double>>
            {
                { "cover", k => cols[k].Coverage }, { "heap", k => sky[k].HeapAmount }, { "sheet", k => sky[k].SheetAmount }, { "base share", k => sky[k].BaseShare },
                { "top share", k => sky[k].TopShare }, { "stable share", k => sky[k].StableShare }, { "rh low", k => sky[k].RhLow }, { "rh mid", k => sky[k].RhMid },
                { "rh high", k => sky[k].RhHigh }, { "warm front", k => sky[k].WarmFront01 }, { "cold front", k => sky[k].ColdFront01 }, { "surface rh", k => sky[k].SurfaceHumidity }
            };
            var worst = quantities.Keys.ToDictionary(k => k, k => 0.0);
            var where = quantities.Keys.ToDictionary(k => k, k => Tuple.Create(0, 0));
            for (int j = 0; j < H; j++)
                for (int i = 0; i < W; i++)
                {
                    int a = j * W + i;
                    foreach (int b in new[] { j * W + (i + 1) % W, j + 1 < H ? a + W : -1 })
                    {
                        if (b < 0 || cols[a].CellStrength > 0.01 || cols[b].CellStrength > 0.01) continue;
                        foreach (KeyValuePair<string, Func<int, double>> q in quantities)
                        {
                            double d = Math.Abs(q.Value(a) - q.Value(b));
                            if (d > worst[q.Key]) { worst[q.Key] = d; where[q.Key] = Tuple.Create(a, b); }
                        }
                    }
                }
            foreach (KeyValuePair<string, double> kv in worst)
            {
                Console.WriteLine(kv.Key + ": largest step between neighbouring texels without a cell " + kv.Value.ToString("0.000"));
                foreach (int k in new[] { where[kv.Key].Item1, where[kv.Key].Item2 })
                    Console.WriteLine("    cover " + cols[k].Coverage.ToString("0.000") + " bg " + cols[k].CloudBackground.ToString("0.000") + " dyn " + cols[k].CloudDynamic.ToString("0.000")
                        + " conv " + cols[k].CloudConvective.ToString("0.000") + " inst " + cols[k].Instability.ToString("0.000") + " sat " + cols[k].LiftSat01.ToString("0.000")
                        + " desc " + cols[k].Descent01.ToString("0.000") + " up " + cols[k].Uplift.ToString("0.000") + " heat " + cols[k].Heating01.ToString("0.000")
                        + " | base " + sky[k].BaseShare.ToString("0.000") + " stable " + sky[k].StableShare.ToString("0.000") + " top " + sky[k].TopShare.ToString("0.000")
                        + " heap " + sky[k].HeapAmount.ToString("0.000") + " rh " + sky[k].RhLow.ToString("0.00") + "/" + sky[k].RhMid.ToString("0.00") + "/" + sky[k].RhHigh.ToString("0.00")
                        + " fronts " + sky[k].WarmFront01.ToString("0.00") + "/" + sky[k].ColdFront01.ToString("0.00") + "/" + sky[k].FrontSurfaceShare.ToString("0.00"));
            }
            // The humidity of a level blends between regimes (mixed layer or free air, below or above the frontal surface, before or after the stratiform rain onset) whose edges
            // move with the field's own steepest inputs (the frontal uplift, the rain onset over RainSoftness): smooth, but steeper than the cover; bounded well below a full jump.
            // The heap and sheet shares split the cover by the background's cumulus share, which follows the instability and the deck: a little steeper than the cover.
            foreach (KeyValuePair<string, double> kv in worst)
                Assert.IsTrue(kv.Value <= (kv.Key.StartsWith("rh ") ? 0.35 : Math.Max(worst["cover"], 0.2)) + 1e-9, kv.Key + " " + kv.Value);
        }

        [TestMethod]
        public void TheSkyIsAPureFunction()
        {
            BodyParams p = BodyParams.KerbinLike();
            List<Weather> a = Columns(new WeatherGenerator(p, 7)), b = Columns(new WeatherGenerator(p, 7));
            for (int i = 0; i < a.Count; i++) Assert.AreEqual(Derive(p, a[i]), Derive(p, b[i]), "column " + i);
        }

        // The cost of the weather maps of one body with and without the sky maps (printed; the cost row of the field is GeneratorFieldTests.TheCostRowsOfTheProofInManagedCode).
        [TestMethod]
        public void TheCostOfTheSkyMapsIsPrinted()
        {
            GeneratorState state = FieldFixture.Field().StateOf(Body);
            var plain = new GeneratorMaps.Weather2D();
            var sky = new GeneratorMaps.Weather2D();
            sky.EnsureSky();
            GeneratorMaps.RenderWeather(state, 3.0e6, plain);
            GeneratorMaps.RenderWeather(state, 3.0e6, sky);
            double plainMs = double.MaxValue, skyMs = double.MaxValue;
            for (int rep = 0; rep < 3; rep++)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                GeneratorMaps.RenderWeather(state, 4.0e6 + rep, plain);
                plainMs = Math.Min(plainMs, sw.Elapsed.TotalMilliseconds);
                sw.Restart();
                GeneratorMaps.RenderWeather(state, 4.0e6 + rep, sky);
                skyMs = Math.Min(skyMs, sw.Elapsed.TotalMilliseconds);
            }
            Console.WriteLine("512x256 weather maps, best of 3: " + plainMs.ToString("0") + " ms without the sky maps, " + skyMs.ToString("0") + " ms with them");
            CollectionAssert.AreEqual(plain.Coverage, sky.Coverage);
        }

        // The largest step between neighbouring texels of a map (east-west, wrapping, and north-south).
        private static float LargestStep(float[] v)
        {
            const int W = GeneratorMaps.Width, H = GeneratorMaps.Height;
            float worst = 0f;
            for (int j = 0; j < H; j++)
            {
                for (int i = 0; i < W; i++)
                {
                    float a = v[j * W + i];
                    worst = Math.Max(worst, Math.Abs(a - v[j * W + (i + 1) % W]));
                    if (j + 1 < H) worst = Math.Max(worst, Math.Abs(a - v[(j + 1) * W + i]));
                }
            }
            return worst;
        }

        [TestMethod]
        public void TheSkyMapsLeaveTheOtherMapsAsTheyWereAndAreSmoothInSpaceAndTime()
        {
            const double ut = 4.0e6;
            GeneratorField plain = FieldFixture.Field(), sky = FieldFixture.Field();
            var before = new Dictionary<WeatherMapKind, float[]>();
            var kinds = new[] { WeatherMapKind.Coverage, WeatherMapKind.Type, WeatherMapKind.Precipitation, WeatherMapKind.Humidity, WeatherMapKind.SurfaceTemperature, WeatherMapKind.WindSpeed };
            var map = new WeatherMap();
            foreach (WeatherMapKind k in kinds)
            {
                Assert.IsTrue(plain.GetMap(Body, k, ut, map));
                before[k] = (float[])map.Values.Clone();
            }
            Assert.IsTrue(sky.GetMap(Body, WeatherMapKind.ConvectiveAmount, ut, map), "a sky map renders on its first read");
            foreach (WeatherMapKind k in kinds)
            {
                Assert.IsTrue(sky.GetMap(Body, k, ut, map));
                CollectionAssert.AreEqual(before[k], map.Values, k + ": the same with the sky maps on");
            }
            var skyKinds = new[]
            {
                WeatherMapKind.ConvectiveAmount, WeatherMapKind.StratiformAmount, WeatherMapKind.ConvectiveBase, WeatherMapKind.ConvectiveTop, WeatherMapKind.Instability, WeatherMapKind.Uplift,
                WeatherMapKind.HumidityLow, WeatherMapKind.HumidityMid, WeatherMapKind.HumidityHigh, WeatherMapKind.Descent, WeatherMapKind.CellReach, WeatherMapKind.DeckAmount,
                WeatherMapKind.StableShare
            };
            var maps = new Dictionary<WeatherMapKind, float[]>();
            foreach (WeatherMapKind k in skyKinds.Concat(new[] { WeatherMapKind.Coverage }))
            {
                Assert.IsTrue(sky.GetMap(Body, k, ut, map), k.ToString());
                maps[k] = (float[])map.Values.Clone();
            }
            float[] heap = maps[WeatherMapKind.ConvectiveAmount], sheet = maps[WeatherMapKind.StratiformAmount], cover = maps[WeatherMapKind.Coverage];
            float[] baseMap = maps[WeatherMapKind.ConvectiveBase], top = maps[WeatherMapKind.ConvectiveTop];
            for (int i = 0; i < heap.Length; i++)
            {
                Assert.IsTrue(heap[i] >= 0f && sheet[i] >= -1e-6f && Math.Abs(heap[i] + sheet[i] - cover[i]) < 1e-5f, "heap + sheet = cover at " + i);
                Assert.IsTrue(baseMap[i] > 0f && baseMap[i] <= top[i] && top[i] <= 1f);
            }
            Assert.IsTrue(heap.Max() > 0.05f && top.Max() > 0.5f, "convection somewhere: heap " + heap.Max() + ", top " + top.Max());

            // in space: the new maps step between neighbouring texels about as much as the cover map does (cells smaller than a texel and fronts make the cover's sharpest edges;
            // a cell moves the split between heap and sheet as well as the cover; NeighbouringColumnsDifferLittleWhereNoCellStands checks the rest)
            float coverStep = LargestStep(cover);
            foreach (WeatherMapKind k in skyKinds)
            {
                float step = LargestStep(maps[k]);
                Console.WriteLine(k + ": largest step between neighbouring texels " + step.ToString("0.000") + " (coverage " + coverStep.ToString("0.000") + ")");
                // the top of a cell's tower stands in the one texel at its centre (up to the tropopause), so the top map steps there by up to the whole troposphere; the cells' reach
                // is that tower's share, and the level humidities rise to saturation inside the tower's gate
                if (k != WeatherMapKind.ConvectiveTop && k != WeatherMapKind.CellReach && k != WeatherMapKind.HumidityMid && k != WeatherMapKind.HumidityHigh)
                    Assert.IsTrue(step <= coverStep + 0.15f, k + " " + step);
            }

            // in time: ten seconds of UT later every texel moves by less than the painters' slew allows in a second (MapSlew.DefaultRate, 0.08 of full scale)
            Assert.IsTrue(sky.Maps.RenderNow(Body, ut + 10.0));
            var later = new WeatherMap();
            foreach (WeatherMapKind k in skyKinds.Concat(new[] { WeatherMapKind.Coverage }))
            {
                Assert.IsTrue(sky.GetMap(Body, k, ut + 10.0, later));
                Assert.AreEqual(ut + 10.0, later.Ut);
                float worst = 0f;
                for (int i = 0; i < later.Values.Length; i++) worst = Math.Max(worst, Math.Abs(later.Values[i] - maps[k][i]));
                Console.WriteLine(k + ": largest change in 10 s of UT " + worst.ToString("0.0000"));
                Assert.IsTrue(worst < MapSlew.DefaultRate, k + " " + worst);
            }
        }

        // The maps of kinds 16 to 22 are the column quantities at the texel's centre (the same samples as the other sky maps), each in its range.
        [TestMethod]
        public void TheLevelHumidityDescentReachDeckAndStableMapsAreTheColumnsQuantities()
        {
            const double ut = 4.0e6;
            GeneratorField field = FieldFixture.Field();
            var kinds = new[]
            {
                WeatherMapKind.HumidityLow, WeatherMapKind.HumidityMid, WeatherMapKind.HumidityHigh, WeatherMapKind.Descent, WeatherMapKind.CellReach, WeatherMapKind.DeckAmount,
                WeatherMapKind.StableShare, WeatherMapKind.StratiformAmount
            };
            var maps = new Dictionary<WeatherMapKind, float[]>();
            var map = new WeatherMap();
            foreach (WeatherMapKind k in kinds)
            {
                Assert.IsTrue(field.GetMap(Body, k, ut, map), k.ToString());
                Assert.AreEqual(GeneratorMaps.Width, map.Width);
                maps[k] = (float[])map.Values.Clone();
            }
            WeatherGenerator g = field.StateOf(Body).Generator;
            const int W = GeneratorMaps.Width, H = GeneratorMaps.Height;
            int checkedTexels = 0, inCell = 0;
            for (int j = 3; j < H; j += 17)
            {
                double lat = (90.0 - (j + 0.5) * 180.0 / H) * (Math.PI / 180.0);
                for (int i = 5; i < W; i += 23)
                {
                    double lon = (-180.0 + (i + 0.5) * 360.0 / W) * (Math.PI / 180.0);
                    Weather w;
                    g.Sample(lat, lon, 0.0, ut, out w, true);
                    SkyColumn s;
                    SkyColumn.Derive(g.Body, ref w, out s);
                    int k = j * W + i;
                    Assert.AreEqual((float)s.RhLow, maps[WeatherMapKind.HumidityLow][k], "low at " + k);
                    Assert.AreEqual((float)s.RhMid, maps[WeatherMapKind.HumidityMid][k], "mid at " + k);
                    Assert.AreEqual((float)s.RhHigh, maps[WeatherMapKind.HumidityHigh][k], "high at " + k);
                    Assert.AreEqual((float)w.Descent01, maps[WeatherMapKind.Descent][k], "descent at " + k);
                    Assert.AreEqual((float)w.CellReach01, maps[WeatherMapKind.CellReach][k], "reach at " + k);
                    Assert.AreEqual((float)s.DeckAmount, maps[WeatherMapKind.DeckAmount][k], "deck at " + k);
                    Assert.AreEqual((float)s.StableShare, maps[WeatherMapKind.StableShare][k], "stable at " + k);
                    checkedTexels++;
                }
            }
            for (int k = 0; k < W * H; k++)
            {
                foreach (WeatherMapKind kind in kinds) Assert.IsTrue(maps[kind][k] >= -1e-6f && maps[kind][k] <= 1f + 1e-6f, kind + " at " + k + ": " + maps[kind][k]);
                Assert.IsTrue(maps[WeatherMapKind.DeckAmount][k] <= maps[WeatherMapKind.StratiformAmount][k] + 1e-5f, "the deck is a part of the sheet at " + k);
                if (maps[WeatherMapKind.CellReach][k] > 0f) inCell++;
            }
            Console.WriteLine("checked " + checkedTexels + " texels against the columns; texels with a cell's reach " + inCell);
            Assert.IsTrue(checkedTexels > 100);
        }
    }
}
