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

        // A spread of columns of the Kerbin-like generator: 64 x 32 points at three times.
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
                        g.Sample(lat, (-180.0 + (i + 0.5) * 360.0 / 64) * Math.PI / 180.0, 0.0, ut, out w);
                        list.Add(w);
                    }
                }
            }
            return list;
        }

        [TestMethod]
        public void TheRendererAndTheGeneratorShareOneDewPoint()
        {
            for (double t = 250.0; t <= 320.0; t += 5.0)
                for (double rh = 0.05; rh <= 1.0; rh += 0.05)
                    Assert.AreEqual(HeightModel.DewPointK(t, rh), MoistAir.DewPointK(t, rh), 1e-9, t + " K, " + rh);
        }

        [TestMethod]
        public void TheConvectiveBaseIsLawrencesRuleOnEarthAndScalesWithTheBody()
        {
            // Lawrence (2005, eq. 22): z_LCL ~ 125 (t - td), accurate to about 2 % for RH 50 to 100 % and 0 to 30 C (his Table 3 against an iterative parcel).
            for (double tc = 0.0; tc <= 30.0; tc += 5.0)
            {
                for (double rh = 0.5; rh < 0.999; rh += 0.1)
                {
                    double t = 273.15 + tc, td = MoistAir.DewPointK(t, rh);
                    double k = MoistAir.LclMetresPerKelvin(9.81, 1004.0, 287.0, t, td);
                    Assert.AreEqual(125.0, k, 125.0 * 0.03, tc + " C, " + rh);
                }
            }
            BodyParams b = BodyParams.KerbinLike();
            Assert.AreEqual(0.0, SkyColumn.ConvectiveBaseM(b, 290.0, MoistAir.DewPointK(290.0, 1.0)), 1e-9, "saturated air: base at the ground");
            double last = -1.0;
            for (double rh = 1.0; rh >= 0.1; rh -= 0.05)
            {
                double zb = SkyColumn.ConvectiveBaseM(b, 290.0, MoistAir.DewPointK(290.0, rh));
                Assert.IsTrue(zb > last, "drier air, higher base: " + rh);
                Assert.IsTrue(zb <= b.TropopauseM);
                last = zb;
            }
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
            for (int i = 0; i < a.Count; i++)
            {
                Weather wa = a[i], wb = a[i];
                SkyColumn sa, sb;
                SkyColumn.Derive(p, ref wa, out sa);
                SkyColumn.Derive(q, ref wb, out sb);
                Assert.AreEqual(2.0 * sa.BaseM, sb.BaseM, 1e-9 * (1.0 + sa.BaseM), "base " + i);
                Assert.AreEqual(2.0 * sa.TopM, sb.TopM, 1e-9 * (1.0 + sa.TopM), "top " + i);
                Assert.AreEqual(2.0 * sa.StableM, sb.StableM, 1e-9 * (1.0 + sa.StableM), "stable " + i);
                Assert.AreEqual(sa.BaseShare, sb.BaseShare, 1e-12);
                Assert.AreEqual(sa.TopShare, sb.TopShare, 1e-12);
                Assert.AreEqual(sa.RhLow, sb.RhLow, 1e-9);
                Assert.AreEqual(sa.RhMid, sb.RhMid, 1e-12);
                Assert.AreEqual(sa.RhHigh, sb.RhHigh, 1e-12);
                Assert.AreEqual(sa.ConvectiveAmount, sb.ConvectiveAmount, 1e-12);
                if (sa.TopM > sa.BaseM + 100.0) deep++;
            }
            Assert.IsTrue(deep > 100, "columns with convection: " + deep);
        }

        [TestMethod]
        public void EveryQuantityStaysInItsRange()
        {
            BodyParams p = BodyParams.KerbinLike();
            var g = new WeatherGenerator(p, 3);
            int warm = 0, cold = 0, shallow = 0, storm = 0, saturatedLow = 0, dryHigh = 0;
            foreach (Weather col in Columns(g))
            {
                Weather w = col;
                SkyColumn s;
                SkyColumn.Derive(p, ref w, out s);
                Assert.IsTrue(s.BaseM >= 0.0 && s.BaseM <= s.TopM && s.TopM <= p.TropopauseM, "base " + s.BaseM + " top " + s.TopM);
                Assert.IsTrue(s.StableShare >= p.SkyStableShareMin && s.StableShare <= 1.0, "stable " + s.StableShare);
                Assert.IsTrue(s.ConvectiveFraction >= 0.0 && s.ConvectiveFraction <= 1.0);
                Assert.IsTrue(s.ConvectiveAmount >= 0.0 && s.ConvectiveAmount <= w.Coverage + 1e-12, "the amount stays the map's");
                foreach (double rh in new[] { s.RhLow, s.RhMid, s.RhHigh }) Assert.IsTrue(rh >= 0.0 && rh <= 1.0, "rh " + rh);
                Assert.IsTrue(s.FrontSign == 0.0 || s.FrontSign == 1.0 || s.FrontSign == -1.0);
                if (s.FrontSign != 0.0)
                {
                    Assert.IsTrue(Math.Abs(s.FrontDistanceM) <= s.FrontQuarterM * (1.0 + 1e-9), "nearest front within a quarter wavelength");
                    Assert.IsTrue(s.FrontAmp01 > 0.0 && s.FrontAmp01 <= 1.0);
                    if (s.FrontAmp01 > 0.3) { if (s.FrontSign > 0) warm++; else cold++; }
                }
                Assert.IsTrue(w.CellReach01 >= 0.0 && w.CellReach01 <= 1.0);
                if (w.CellReach01 > 0.3) storm++;
                else if (s.TopM > s.BaseM + 200.0) shallow++;
                if (s.RhLow > 0.999) saturatedLow++;
                if (s.RhHigh < 0.3) dryHigh++;
            }
            Console.WriteLine("warm " + warm + ", cold " + cold + ", shallow " + shallow + ", storm " + storm + ", saturated low " + saturatedLow + ", dry high " + dryHigh);
            Assert.IsTrue(warm > 10 && cold > 10, "both kinds of front");
            Assert.IsTrue(shallow > 10 && storm > 0, "shallow and deep convection");
        }

        // The game's Kerbin parameter set (from the dumps): what the columns look like there, for the report (the quartiles of the heights).
        [TestMethod]
        public void TheKerbinColumnsArePrinted()
        {
            GeneratorState state = FieldFixture.Field().StateOf(Body);
            BodyParams p = state.Parameters;
            var bases = new List<double>(); var tops = new List<double>(); var stables = new List<double>(); var depths = new List<double>();
            var low = new List<double>(); var mid = new List<double>(); var high = new List<double>();
            foreach (Weather col in Columns(state.Generator))
            {
                Weather w = col;
                SkyColumn s;
                SkyColumn.Derive(p, ref w, out s);
                bases.Add(s.BaseM); tops.Add(s.TopM); stables.Add(s.StableM); depths.Add(s.TopM - s.BaseM);
                low.Add(s.RhLow); mid.Add(s.RhMid); high.Add(s.RhHigh);
            }
            Func<List<double>, string> q = l => { l.Sort(); return l[l.Count / 4].ToString("0.00") + " / " + l[l.Count / 2].ToString("0.00") + " / " + l[3 * l.Count / 4].ToString("0.00"); };
            Console.WriteLine("Kerbin: tropopause " + p.TropopauseM.ToString("0") + " m, g " + p.GravityMs2 + ", cp " + p.Cp + ", R " + p.GasConstant);
            Console.WriteLine("quartiles base m " + q(bases) + "; stable m " + q(stables) + "; top m " + q(tops) + "; depth m " + q(depths));
            Console.WriteLine("quartiles RH low " + q(low) + "; mid " + q(mid) + "; high " + q(high));
            Assert.IsTrue(bases.Count > 0);
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

        [TestMethod]
        public void MoreInstabilityGivesADeeperTopAndStrongerDescentOrInversionALowerStableLayer()
        {
            BodyParams p = BodyParams.KerbinLike();
            var w = new Weather { SurfaceTempK = 295.0, RelHumidity = 0.75, Heating01 = 1.0, Descent01 = 0.2, Uplift = 0.0, Coverage = 0.5 };
            double last = -1.0;
            for (double inst = 0.0; inst <= 0.5; inst += 0.05)
            {
                w.Instability = inst;
                SkyColumn s;
                SkyColumn.Derive(p, ref w, out s);
                Assert.IsTrue(s.TopM >= last, "instability " + inst);
                Assert.IsTrue(s.TopM <= s.StableM + 1e-9, "shallow convection stops at the stable layer");
                last = s.TopM;
            }
            w.Instability = 0.3;
            last = -1.0;
            for (double reach = 0.0; reach <= 1.0; reach += 0.1)
            {
                w.CellReach01 = reach;
                SkyColumn s;
                SkyColumn.Derive(p, ref w, out s);
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
            Assert.AreEqual(p.SkyStableShareMin, SkyColumn.StableShareOf(p, 0.0, 0.3, 0.0), 1e-12, "a night over land: the floor");
            Assert.AreEqual(1.0, SkyColumn.StableShareOf(p, 0.0, 0.0, 1.0), 1e-12, "strong ascent: no cap below the tropopause");
            // continuous through the ascent where the equilibrium ends
            double prev = SkyColumn.StableShareOf(p, 0.3, 0.0, 0.0);
            for (double u = 0.01; u <= 1.0; u += 0.01)
            {
                double s = SkyColumn.StableShareOf(p, 0.3, 0.0, u);
                Assert.IsTrue(s >= prev - 1e-12 && s - prev < 0.12, "uplift " + u + ": " + prev + " -> " + s);
                prev = s;
            }
        }

        [TestMethod]
        public void TheFrontsDistanceGrowsEastwardThroughTheFront()
        {
            var g = new WeatherGenerator(BodyParams.KerbinLike(), 1);
            int crossings = 0;
            for (int row = 0; row < 40; row++)
            {
                double lat = (35.0 + row) * Math.PI / 180.0;
                Weather prev;
                g.Sample(lat, -Math.PI, 0.0, 4.0e6, out prev);
                for (int i = 1; i < 2000; i++)
                {
                    Weather w;
                    g.Sample(lat, -Math.PI + i * 2.0 * Math.PI / 2000, 0.0, 4.0e6, out w);
                    bool sameFront = w.FrontSign == prev.FrontSign && w.FrontSign != 0.0 && Math.Abs(w.FrontQuarterM - prev.FrontQuarterM) < 0.05 * w.FrontQuarterM;
                    if (sameFront && prev.FrontDistanceM < 0.0 && w.FrontDistanceM >= 0.0 && w.FrontDistanceM - prev.FrontDistanceM < 0.5 * w.FrontQuarterM)
                        crossings++;
                    if (sameFront && prev.FrontDistanceM > 0.0 && w.FrontDistanceM <= 0.0 && prev.FrontDistanceM - w.FrontDistanceM < 0.5 * w.FrontQuarterM)
                        Assert.Fail("a front crossed from ahead to behind going east at " + lat + ", " + i);
                    prev = w;
                }
            }
            Assert.IsTrue(crossings > 5, "fronts crossed: " + crossings);
        }

        [TestMethod]
        public void TheSkyIsAPureFunction()
        {
            BodyParams p = BodyParams.KerbinLike();
            List<Weather> a = Columns(new WeatherGenerator(p, 7)), b = Columns(new WeatherGenerator(p, 7));
            for (int i = 0; i < a.Count; i++)
            {
                Weather wa = a[i], wb = b[i];
                SkyColumn sa, sb;
                SkyColumn.Derive(p, ref wa, out sa);
                SkyColumn.Derive(p, ref wb, out sb);
                Assert.AreEqual(sa, sb, "column " + i);
            }
        }

        [TestMethod]
        public void TheSkyMapsLeaveTheOtherMapsAsTheyWereAndMoveSmoothly()
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
            var amount = new WeatherMap();
            Assert.IsTrue(sky.GetMap(Body, WeatherMapKind.ConvectiveAmount, ut, amount), "a sky map renders on its first read");
            foreach (WeatherMapKind k in kinds)
            {
                Assert.IsTrue(sky.GetMap(Body, k, ut, map));
                CollectionAssert.AreEqual(before[k], map.Values, k + ": the same with the sky maps on");
            }
            var baseMap = new WeatherMap();
            var top = new WeatherMap();
            var inst = new WeatherMap();
            var lift = new WeatherMap();
            var cover = new WeatherMap();
            Assert.IsTrue(sky.GetMap(Body, WeatherMapKind.ConvectiveBase, ut, baseMap));
            Assert.IsTrue(sky.GetMap(Body, WeatherMapKind.ConvectiveTop, ut, top));
            Assert.IsTrue(sky.GetMap(Body, WeatherMapKind.Instability, ut, inst));
            Assert.IsTrue(sky.GetMap(Body, WeatherMapKind.Uplift, ut, lift));
            Assert.IsTrue(sky.GetMap(Body, WeatherMapKind.Coverage, ut, cover));
            for (int i = 0; i < amount.Values.Length; i++)
            {
                Assert.IsTrue(amount.Values[i] >= 0f && amount.Values[i] <= cover.Values[i] + 1e-6f);
                Assert.IsTrue(baseMap.Values[i] >= 0f && baseMap.Values[i] <= top.Values[i] && top.Values[i] <= 1f);
                Assert.IsTrue(inst.Values[i] >= 0f && inst.Values[i] <= 1f && lift.Values[i] >= 0f && lift.Values[i] <= 1.4f + 1e-6f);
            }
            Assert.IsTrue(amount.Values.Max() > 0.05f && top.Values.Max() > 0.5f, "convection somewhere: amount " + amount.Values.Max() + ", top " + top.Values.Max());

            // Ten seconds of UT later (a map update is at most every 0.25 s of real time): every texel moves by a few thousandths of full scale, so the painters' slew
            // (MapSlew.DefaultRate, 0.08 of full scale a second) hands it over without a jump.
            var watched = new[] { WeatherMapKind.ConvectiveAmount, WeatherMapKind.ConvectiveBase, WeatherMapKind.ConvectiveTop, WeatherMapKind.Coverage };
            var firsts = new Dictionary<WeatherMapKind, float[]>();
            foreach (WeatherMapKind k in watched)
            {
                Assert.IsTrue(sky.GetMap(Body, k, ut, map));
                firsts[k] = (float[])map.Values.Clone();
            }
            Assert.IsTrue(sky.Maps.RenderNow(Body, ut + 10.0));
            var later = new WeatherMap();
            foreach (WeatherMapKind k in watched)
            {
                float[] first = firsts[k];
                Assert.IsTrue(sky.GetMap(Body, k, ut + 10.0, later));
                Assert.AreEqual(ut + 10.0, later.Ut);
                float worst = 0f;
                for (int i = 0; i < first.Length; i++) worst = Math.Max(worst, Math.Abs(later.Values[i] - first[i]));
                Console.WriteLine(k + ": largest change in 10 s of UT " + worst.ToString("0.0000"));
                Assert.IsTrue(worst < MapSlew.DefaultRate, k + " " + worst);
            }
        }
    }
}
