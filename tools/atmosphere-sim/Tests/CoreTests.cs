using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using AtmoSim.Core;
using AtmoSim.Runner;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AtmoSim.Tests
{
    [TestClass]
    public class DMathTests
    {
        [TestMethod]
        public void ElementaryFunctionsAgreeWithTheLibrary()
        {
            for (double x = -20.0; x <= 20.0; x += 0.37)
            {
                Assert.AreEqual(Math.Exp(x), DMath.Exp(x), Math.Exp(x) * 1e-11, "exp " + x);
                Assert.AreEqual(Math.Sin(x), DMath.Sin(x), 1e-11, "sin " + x);
                Assert.AreEqual(Math.Cos(x), DMath.Cos(x), 1e-11, "cos " + x);
                Assert.AreEqual(Math.Atan(x), DMath.Atan(x), 1e-11, "atan " + x);
                Assert.AreEqual(Math.Tanh(x * 0.3), DMath.Tanh(x * 0.3), 1e-11, "tanh " + x);
            }
            for (double x = 0.001; x < 1e6; x *= 1.7) Assert.AreEqual(Math.Log(x), DMath.Log(x), 1e-11, "log " + x);
            Assert.AreEqual(Math.Atan2(-0.3, -2.0), DMath.Atan2(-0.3, -2.0), 1e-12);
            Assert.AreEqual(Math.Asin(0.4), DMath.Asin(0.4), 1e-12);
            Assert.AreEqual(Math.Exp(-600.0), DMath.Exp(-600.0), 1e-250);
            Assert.AreEqual(0.0, DMath.Exp(-800.0));
        }

        [TestMethod]
        public void TheFastTanhIsMonotoneAndCloseToTanh()
        {
            double prev = -2.0;
            for (double x = -4.0; x <= 4.0; x += 0.01)
            {
                double v = DMath.TanhFast(x);
                Assert.IsTrue(v >= prev - 1e-15, "monotone at " + x);
                Assert.AreEqual(Math.Tanh(x), v, 0.025, "tanh " + x);
                prev = v;
            }
            Assert.AreEqual(1.0, DMath.TanhFast(3.0), 1e-12);
        }

        [TestMethod]
        public void WrapPiLandsInTheHalfOpenInterval()
        {
            for (double x = -50.0; x < 50.0; x += 0.13)
            {
                double w = DMath.WrapPi(x);
                Assert.IsTrue(w >= -Math.PI - 1e-12 && w < Math.PI + 1e-12);
                Assert.AreEqual(Math.Cos(x), Math.Cos(w), 1e-9);
            }
        }
    }

    [TestClass]
    public class DeterminismTests
    {
        private static ulong HashSet(WeatherGenerator g, int n, ulong seed, int[] order, double utShift)
        {
            var r = new Rng(seed);
            var lat = new double[n]; var lon = new double[n]; var alt = new double[n]; var ut = new double[n];
            for (int i = 0; i < n; i++) { lat[i] = Math.Asin(2 * r.Uniform() - 1); lon[i] = (2 * r.Uniform() - 1) * Math.PI; alt[i] = 12000 * r.Uniform(); ut[i] = 3e7 * r.Uniform(); }
            var hs = new ulong[n];
            Weather w;
            for (int k = 0; k < n; k++)
            {
                int i = order == null ? k : order[k];
                g.Sample(lat[i], lon[i], alt[i], ut[i] + utShift, out w);
                hs[i] = WeatherGenerator.HashOf(ref w, (ulong)i);
            }
            ulong h = 1;
            foreach (ulong x in hs) h = Hash.Mix(h ^ x);
            return h;
        }

        [TestMethod]
        public void SameSeedGivesTheSameHashAndAnotherSeedDiffers()
        {
            var bp = BodyParams.KerbinLike();
            ulong a = HashSet(new WeatherGenerator(bp, 5), 3000, 1, null, 0);
            ulong b = HashSet(new WeatherGenerator(bp.Clone(), 5), 3000, 1, null, 0);
            ulong c = HashSet(new WeatherGenerator(bp, 6), 3000, 1, null, 0);
            Assert.AreEqual(a, b);
            Assert.AreNotEqual(a, c);
        }

        [TestMethod]
        public void ThreadsAndEvaluationOrderDoNotChangeTheResult()
        {
            var bp = BodyParams.KerbinLike();
            var g = new WeatherGenerator(bp, 5);
            ulong reference = HashSet(g, 3000, 2, null, 0);
            var res = new ulong[4];
            var th = new Thread[4];
            for (int t = 0; t < 4; t++) { int tt = t; th[t] = new Thread(() => { res[tt] = HashSet(g, 3000, 2, null, 0); }); th[t].Start(); }
            foreach (var t in th) t.Join();
            foreach (var h in res) Assert.AreEqual(reference, h);
            var order = new int[3000]; for (int i = 0; i < order.Length; i++) order[i] = i;
            var rng = new Rng(3);
            for (int i = order.Length - 1; i > 0; i--) { int k = (int)(rng.Uniform() * (i + 1)); int tmp = order[i]; order[i] = order[k]; order[k] = tmp; }
            Assert.AreEqual(reference, HashSet(g, 3000, 2, order, 0));
        }

        [TestMethod]
        public void MapsDoNotDependOnTheChunking()
        {
            var g = new WeatherGenerator(BodyParams.KerbinLike(), 9);
            float[] a = new float[64 * 32], b = new float[64 * 32];
            SubGrid.Map(g, 4.0e6, 64, 32, a, null, null, null, null, null);
            SubGrid.Map(g, 5.0e6, 64, 32, null, null, null, null, null, null);
            SubGrid.Map(g, 4.0e6, 64, 32, b, null, null, null, null, null);
            CollectionAssert.AreEqual(a, b);
        }

        [TestMethod]
        public void EvaluationAtAnEarlierUtEqualsTheFirstEvaluationBitForBit()
        {
            var g = new WeatherGenerator(BodyParams.KerbinLike(), 11);
            Weather w1, w2, w3;
            var q = new Rng(4);
            for (int i = 0; i < 200; i++)
            {
                double lat = Math.Asin(2 * q.Uniform() - 1), lon = (2 * q.Uniform() - 1) * Math.PI, ut = 1e6 + 5e6 * q.Uniform();
                g.Sample(lat, lon, 100.0, ut - 172800.0, out w1);
                g.Sample(lat, lon, 100.0, ut + 777777.0, out w3);   // jump forward
                g.Sample(lat, lon, 100.0, ut - 172800.0, out w2);   // and back
                Assert.AreEqual(WeatherGenerator.HashOf(ref w1, 0), WeatherGenerator.HashOf(ref w2, 0));
            }
        }
    }

    [TestClass]
    public class GeneratorTests
    {
        private static IEnumerable<BodyParams> Sets()
        {
            yield return BodyParams.KerbinLike(); yield return BodyParams.DunaLike(); yield return BodyParams.EveLike(); yield return BodyParams.LaytheLike();
            yield return BodyParams.SlowRotator(); yield return BodyParams.NoOcean(); yield return BodyParams.ColdWorld(); yield return BodyParams.TidallyLockedWorld();
        }

        [TestMethod]
        public void OutputsAreFiniteAndInRangeOnEverySet()
        {
            foreach (var bp in Sets())
            {
                var g = new WeatherGenerator(bp, 3);
                var r = new Rng(8);
                Weather w;
                for (int i = 0; i < 4000; i++)
                {
                    g.Sample(Math.Asin(2 * r.Uniform() - 1), (2 * r.Uniform() - 1) * Math.PI, 15000 * r.Uniform(), 6e7 * r.Uniform(), out w);
                    Assert.IsTrue(!double.IsNaN(w.TempK) && w.TempK > 50 && w.TempK < 500, bp.Name + " T " + w.TempK);
                    Assert.IsTrue(w.Humidity >= 0 && w.Humidity <= 1, bp.Name + " H");
                    Assert.IsTrue(w.Coverage >= 0 && w.Coverage <= 1.0000001, bp.Name + " cover");
                    Assert.IsTrue(w.PrecipMmH >= 0 && !double.IsNaN(w.PrecipMmH), bp.Name + " P");
                    Assert.IsTrue(!double.IsNaN(w.WindSpeed) && w.WindSpeed < 80, bp.Name + " wind " + w.WindSpeed);
                    Assert.IsTrue(w.SnowFrac + w.RainFrac <= 1.0000001, bp.Name + " fractions");
                    Assert.IsTrue(w.Density3D >= 0 && w.Density3D <= 1.5, bp.Name + " density");
                }
            }
        }

        [TestMethod]
        public void SampleAllocatesNothing()
        {
            var g = new WeatherGenerator(BodyParams.KerbinLike(), 1);
            Weather w;
            for (int i = 0; i < 2000; i++) g.Sample(0.8, 0.01 * i, 0.0, 1000.0 + i * 7, out w);
            long a0 = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 20000; i++) g.Sample(0.8, 0.0003 * i, 100.0, 1000.0 + i * 7, out w);
            Assert.AreEqual(0L, GC.GetAllocatedBytesForCurrentThread() - a0);
        }

        [TestMethod]
        public void SampleIsCheap()
        {
            // The gated number (3 us) is measured under the exclusive lock; this is a loose guard against a regression by an order of magnitude.
            var g = new WeatherGenerator(BodyParams.KerbinLike(), 1);
            Weather w;
            var r = new Rng(5);
            var lat = new double[20000]; var lon = new double[20000];
            for (int i = 0; i < lat.Length; i++) { lat[i] = Math.Asin(2 * r.Uniform() - 1); lon[i] = (2 * r.Uniform() - 1) * Math.PI; }
            for (int i = 0; i < lat.Length; i++) g.Sample(lat[i], lon[i], 0.0, 1e6, out w);
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < lat.Length; i++) g.Sample(lat[i], lon[i], 0.0, 1e6 + (i / 256) * 3600.0, out w);
            double us = sw.Elapsed.TotalMilliseconds * 1000.0 / lat.Length;
            Assert.IsTrue(us < 30.0, "us per sample " + us);
        }

        [TestMethod]
        public void WavesOffRemovesFrontsAndMidLatitudeCells()
        {
            var on = new WeatherGenerator(BodyParams.KerbinLike(), 4);
            var bp = BodyParams.KerbinLike(); bp.WavesEnabled = false;
            var off = new WeatherGenerator(bp, 4);
            double fOn = 0, fOff = 0, cOn = 0, cOff = 0;
            Weather w;
            var r = new Rng(2);
            for (int i = 0; i < 6000; i++)
            {
                double lat = (35 + 35 * r.Uniform()) * Math.PI / 180, lon = (2 * r.Uniform() - 1) * Math.PI, ut = 3e6 + 3e6 * r.Uniform();
                on.Sample(lat, lon, 0, ut, out w); fOn += w.FrontKPer100Km; cOn += w.CellStrength;
                off.Sample(lat, lon, 0, ut, out w); fOff += w.FrontKPer100Km; cOff += w.CellStrength;
            }
            Assert.IsTrue(fOn > 0);
            Assert.AreEqual(0.0, fOff);
            // cells that remain without waves come from unstable air alone (warm humid afternoons at the low edge of the belt)
            Assert.IsTrue(cOff < 0.3 * cOn, "cells on " + cOn + " off " + cOff);
        }

        [TestMethod]
        public void NoRotationRemovesTheJetDrivenSystems()
        {
            var bp = BodyParams.KerbinLike(); bp.RotationPeriodS = 2.0e8;
            var g = new WeatherGenerator(bp, 4);
            Assert.AreEqual(0.0, g.D.WaveGate);
            Assert.AreEqual(0, g.ActiveSystems(0, 4e6, null));
        }

        [TestMethod]
        public void SlotAmplitudeChangeAtHandOverIsBelowHalfAPercentPerHour()
        {
            // Criterion 1 popping measure: every slot, every generation boundary in 60 days, the amplitude difference over every one-hour interval
            // that contains the hand-over instant, relative to the largest amplitude a system can have (PressureAmp x 1.2 x WaveGate).
            // Bound by construction: sin^2(pi / 72) = 0.19 % for the shortest generation of 3 days.
            var g = new WeatherGenerator(BodyParams.KerbinLike(), 6);
            double scale = g.D.PressureAmpPa * 1.2 * g.D.WaveGate;
            double worst = 0.0; int boundaries = 0;
            for (int h = 0; h < 2; h++)
                for (int j = 0; j < g.D.SlotsPerHemisphere; j++)
                {
                    double life, off; g.SlotTiming(h, j, out life, out off);
                    for (int n = 1; n * life + off < 60 * 86400.0; n++)
                    {
                        double tb = off + n * life; boundaries++;
                        // every one-hour interval that contains the hand-over instant (the change per game hour at the hand-over)
                        for (double dt = -3600.0; dt <= 0.0; dt += 300.0)
                        {
                            double a1 = g.SlotAmplitude(h, j, tb + dt + 3600.0), a0 = g.SlotAmplitude(h, j, tb + dt);
                            worst = Math.Max(worst, Math.Abs(a1 - a0) / scale);
                        }
                    }
                }
            Assert.IsTrue(boundaries > 40);
            Assert.IsTrue(worst < 0.005, "largest amplitude change per hour at a hand-over " + worst);
        }

        [TestMethod]
        public void AmplitudeChangePerHourAtHandOverIsSmall()
        {
            var g = new WeatherGenerator(BodyParams.KerbinLike(), 6);
            var a0 = new double[3 * WeatherGenerator.MaxSlots]; var a1 = new double[3 * WeatherGenerator.MaxSlots];
            double worst = 0.0;
            double scale = g.D.PressureAmpPa * 1.2;
            // amplitude of every slot every 15 minutes over 20 days: the largest change per hour
            double prev = 0;
            var amps = new List<double[]>();
            for (int k = 0; k < 20 * 96; k++)
            {
                var buf = new double[3 * WeatherGenerator.MaxSlots];
                g.ActiveSystems(0, 2e6 + k * 900.0, buf);
                amps.Add(buf);
            }
            // total amplitude is the sum over active slots; check each active set's maximum single-step change
            for (int k = 4; k < amps.Count; k++)
            {
                double s1 = 0, s0 = 0;
                for (int j = 0; j < WeatherGenerator.MaxSlots; j++) { s1 += amps[k][3 * j + 2]; s0 += amps[k - 4][3 * j + 2]; }
                worst = Math.Max(worst, Math.Abs(s1 - s0) / scale);
                prev = s1;
            }
            // the window sin^2(pi u) over at least 3 days changes by at most pi / 72 of the amplitude per hour in the middle
            Assert.IsTrue(worst < 0.2, "worst total amplitude change per hour " + worst);
        }

        [TestMethod]
        public void LevelScaleIsMonotoneAndUsesTheBands()
        {
            var g = new WeatherGenerator(BodyParams.KerbinLike(), 1);
            double prev = 0;
            for (double p = 0; p < 40; p += 0.01)
            {
                double l = g.Level01(p);
                Assert.IsTrue(l >= prev - 1e-12 && l <= 1.0);
                prev = l;
            }
            Assert.AreEqual(0.0, g.Level01(0.04));
            Assert.AreEqual(0.2, g.Level01(g.D.BandsMmH[1]), 1e-9);
            Assert.AreEqual(PrecipLevel.Blizzard, g.LevelOf(9.0, PrecipKind.Snow, 20.0));
            Assert.AreEqual(PrecipLevel.Heavy, g.LevelOf(9.0, PrecipKind.Snow, 5.0));
            Assert.AreEqual(PrecipLevel.Heavy, g.LevelOf(9.0, PrecipKind.Rain, 20.0));
        }

        [TestMethod]
        public void DustShowsOnlyAboveTheWindThresholdOnADryBody()
        {
            var bp = BodyParams.DunaLike();
            var g = new WeatherGenerator(bp, 2);
            var r = new Rng(7);
            Weather w;
            int dusty = 0;
            for (int i = 0; i < 20000; i++)
            {
                g.Sample(Math.Asin(2 * r.Uniform() - 1), (2 * r.Uniform() - 1) * Math.PI, 0.0, 6e6 * r.Uniform(), out w);
                if (w.Dust > 0) { dusty++; Assert.IsTrue(w.WindSpeed >= 0.8 * bp.DustWindThresholdMs - 1e-9); }
                if (w.WindSpeed < 0.8 * bp.DustWindThresholdMs) Assert.AreEqual(0.0, w.Dust);
            }
            var kb = new WeatherGenerator(BodyParams.KerbinLike(), 2);
            for (int i = 0; i < 2000; i++) { kb.Sample(0.5, i * 0.003, 0.0, 1e6, out w); Assert.AreEqual(0.0, w.Dust); }
        }

        [TestMethod]
        public void TheWindowAgreesWithPointEvaluation()
        {
            var g = new WeatherGenerator(BodyParams.KerbinLike(), 3);
            var d = new float[8 * 8 * 6];
            SubGrid.Window(g, 0.9, 0.2, 3.0e6, 8, 8, 6, 400.0, 500.0, 0.0, d);
            Weather w;
            g.Sample(0.9 + (3 - 3.5) * 400.0 / 600000.0, 0.2 + (2 - 3.5) * 400.0 / (600000.0 * Math.Cos(0.9)), 1000.0, 3.0e6, out w);
            Assert.AreEqual((float)w.DensityAt(1000.0), d[(2 * 8 + 3) * 8 + 2], 1e-6);
            // the full point evaluation at that altitude gives the same density
            double lat = 0.9 + (3 - 3.5) * 400.0 / 600000.0, lon = 0.2 + (2 - 3.5) * 400.0 / (600000.0 * Math.Cos(0.9));
            Weather w2; g.Sample(lat, lon, 1000.0, 3.0e6, out w2);
            Assert.AreEqual(w2.Density3D, w.DensityAt(1000.0), 1e-12);
        }
    }

    [TestClass]
    public class CirculationTests
    {
        [TestMethod]
        public void CellCountFollowsTheRotationRate()
        {
            int prev = 4;
            foreach (double period in new[] { 5400.0, 21600.0, 43200.0, 86164.0, 200000.0, 864000.0 })
            {
                var bp = BodyParams.KerbinLike(); bp.RotationPeriodS = period;
                var d = new BodyDerived(bp);
                Assert.IsTrue(d.Cells <= prev, "cells must not increase with a longer period: " + period);
                prev = d.Cells;
            }
            Assert.AreEqual(3, new BodyDerived(BodyParams.KerbinLike()).Cells);
            Assert.AreEqual(1.0, new BodyDerived(BodyParams.TidallyLockedWorld()).SubSolarWeight);
            Assert.IsTrue(new BodyDerived(BodyParams.SlowRotator()).SubSolarWeight > 0.9);
        }

        [TestMethod]
        public void TheJetIsStrongerInWinterAndMovesPolewardInSummer()
        {
            var bp = BodyParams.KerbinLike();
            var g = new WeatherGenerator(bp, 1);
            double winter = 0.0, summer = 0.5 * bp.YearS;
            Assert.IsTrue(g.JetSpeed(1, winter) > g.JetSpeed(1, summer));
            Assert.IsTrue(g.JetLatitude(1, summer) > g.JetLatitude(1, winter));
            Assert.IsTrue(g.JetSpeed(-1, winter) < g.JetSpeed(-1, summer));
            // the shift amplitude is 0.22 of the obliquity
            Assert.AreEqual(0.22 * bp.ObliquityRad, 0.5 * (g.JetLatitude(1, summer) - g.JetLatitude(1, winter)), 0.01);
        }
    }

    [TestClass]
    public class SurfaceTests
    {
        [TestMethod]
        public void StefanLawHoldsWithinTwoPercent()
        {
            double tf = 271.26, tml = tf, h = 0.0;
            double cap = 4.186e6 * 12.0;
            // start from thin ice; constant air temperature of 253 K for 60 days
            h = 0.05;
            double fdd = 0;
            for (int d = 0; d < 60; d++)
            {
                IceModel.Step(ref tml, ref h, 253.0, 0.0, 0.0, tf, cap, 1.0);
                fdd += tf - 253.0;
            }
            double expect = Math.Sqrt(0.05 * 0.05 + 2.0 * IceModel.KIce / (IceModel.RhoIce * IceModel.LatentFusion) * fdd * 86400.0);
            Assert.AreEqual(expect, h, 0.02 * expect);
            Assert.AreEqual(IceModel.StefanThickness(0.05, fdd), h, 0.02 * expect);
        }

        [TestMethod]
        public void SnowInsulationSlowsGrowthAndWarmAirMeltsIce()
        {
            double tf = 271.26, cap = 4.186e6 * 12.0;
            double t1 = tf, h1 = 0.3, t2 = tf, h2 = 0.3;
            for (int d = 0; d < 20; d++)
            {
                IceModel.Step(ref t1, ref h1, 255.0, 0.0, 0.0, tf, cap, 1.0);
                IceModel.Step(ref t2, ref h2, 255.0, 0.0, 0.25, tf, cap, 1.0);
            }
            Assert.IsTrue(h2 < h1 && h2 > 0.3, "snow-covered " + h2 + " bare " + h1);
            double t3 = tf, h3 = 0.5;
            IceModel.Step(ref t3, ref h3, tf + 5.0, 0.0, 0.0, tf, cap, 1.0);
            Assert.AreEqual(0.5 - IceModel.MeltFactorMPerKDay * 5.0, h3, 1e-9);
            double t4 = tf, h4 = 0.5;
            IceModel.Step(ref t4, ref h4, tf + 5.0, 200.0, 0.0, tf, cap, 1.0);
            Assert.IsTrue(h4 < h3, "the solar term adds melt");
        }

        [TestMethod]
        public void TheHeatStoreDelaysFreezeUp()
        {
            double tf = 271.26, cap = 4.186e6 * 12.0, tml = 285.0, h = 0.0;
            int days = 0;
            while (h <= 0.0 && days < 400) { IceModel.Step(ref tml, ref h, 255.0, 0.0, 0.0, tf, cap, 1.0); days++; }
            Assert.IsTrue(days > 5 && days < 200, "days until ice forms " + days);
            // a deeper mixed layer takes longer
            double tml2 = 285.0, h2 = 0.0; int days2 = 0;
            while (h2 <= 0.0 && days2 < 800) { IceModel.Step(ref tml2, ref h2, 255.0, 0.0, 0.0, tf, 4.186e6 * 48.0, 1.0); days2++; }
            Assert.IsTrue(days2 > days);
        }

        [TestMethod]
        public void SalinityLowersTheFreezingPoint()
        {
            var fresh = BodyParams.KerbinLike(); fresh.SalinityPsu = 0;
            var sea = BodyParams.KerbinLike();
            Assert.IsTrue(sea.FreezePointK < fresh.FreezePointK);
            Assert.AreEqual(273.15, fresh.FreezePointK, 1e-9);
        }

        [TestMethod]
        public void NoIceWithoutOceanAndNoneInTheTropics()
        {
            var g = new WeatherGenerator(BodyParams.NoOcean(), 1);
            var site = new SurfaceSite(g, 1.2, 0.5);
            Assert.IsFalse(site.IsSea);
            for (int d = 0; d < 400; d += 20) Assert.AreEqual(0.0, site.Evaluate(1e6 + d * 86400.0).IceThicknessM);
            var k = new WeatherGenerator(BodyParams.KerbinLike(), 1);
            var sea = SeaSite(k, 0.0);
            for (int d = 0; d < 200; d += 7) Assert.AreEqual(0.0, sea.Evaluate(2e6 + d * 86400.0).IceThicknessM);
        }

        private static SurfaceSite SeaSite(WeatherGenerator g, double latRad)
        {
            double[] p = Timing.FindSea(g, latRad);
            return new SurfaceSite(g, p[0], p[1]);
        }

        [TestMethod]
        public void SnowStaysBoundedAndNonNegativeOverFiveYears()
        {
            var bp = BodyParams.KerbinLike();
            var g = new WeatherGenerator(bp, 2);
            foreach (double latDeg in new[] { 85.0, 60.0, 30.0, 0.0, -70.0 })
            {
                var site = new SurfaceSite(g, latDeg * Math.PI / 180.0, 0.7);
                for (double ut = 1e6; ut < 1e6 + 5 * bp.YearS; ut += 5 * 86400.0)
                {
                    var s = site.Evaluate(ut);
                    Assert.IsTrue(s.SnowSweMm >= 0 && s.SnowSweMm <= 2000.0 && !double.IsNaN(s.SnowDepthM), latDeg + " deg " + s.SnowSweMm);
                    Assert.IsTrue(s.Wet01 >= 0 && s.Wet01 <= 1);
                }
            }
        }

        [TestMethod]
        public void IceThicknessIsContinuousAtTheAnchorBoundaries()
        {
            var bp = BodyParams.KerbinLike();
            var g = new WeatherGenerator(bp, 2);
            var site = SeaSite(g, 1.15);
            Assert.IsTrue(site.IsSea);
            double q = bp.YearS / 4.0, worst = 0;
            for (int a = 4; a < 12; a++)
            {
                double prev = site.Evaluate(a * q - 7200.0).IceThicknessM;
                for (double dt = -7200.0 + 600.0; dt <= 7200.0; dt += 600.0)
                {
                    double v = site.Evaluate(a * q + dt).IceThicknessM;
                    worst = Math.Max(worst, Math.Abs(v - prev));
                    prev = v;
                }
            }
            Assert.IsTrue(worst < 0.03, "largest thickness change per 10 minutes across a boundary " + worst);
        }

        [TestMethod]
        public void DustDepositsOnlyOnDryLandAndStaysBounded()
        {
            var g = new WeatherGenerator(BodyParams.DunaLike(), 2);
            var site = new SurfaceSite(g, 0.3, 1.0);
            for (double ut = 1e6; ut < 1e6 + 40 * 86400.0; ut += 3 * 3600.0)
            {
                var s = site.Evaluate(ut);
                Assert.IsTrue(s.DustDepositMm >= 0 && s.DustDepositMm <= 5.0);
            }
            var k = new WeatherGenerator(BodyParams.KerbinLike(), 2);
            var ks = new SurfaceSite(k, 0.3, 1.0);
            Assert.AreEqual(0.0, ks.Evaluate(2e6).DustDepositMm);
        }
    }

    [TestClass]
    public class DetectorTests
    {
        private const int NLon = 256, NLat = 128;
        private const double Radius = 600000.0;

        // A planted low: Gaussian pressure anomaly of radius sigma at (lat, lon) moving east at u m/s.
        private static void PlantLows(MetricGrid g, double[] p, double latDeg, double[] lon0, double u, double tS, double depth, double sigmaM)
        {
            Array.Clear(p, 0, p.Length);
            foreach (double l0 in lon0)
            {
                double lat = latDeg * Math.PI / 180.0;
                double lon = l0 + u * tS / (Radius * Math.Cos(lat));
                for (int j = 0; j < NLat; j++)
                    for (int i = 0; i < NLon; i++)
                    {
                        double d = MetricGrid.Dist(Radius, lat, lon, g.LatRad[j], g.LonRad[i]);
                        if (d < 4 * sigmaM) p[j * NLon + i] -= depth * Math.Exp(-(d * d) / (sigmaM * sigmaM));
                    }
            }
        }

        [TestMethod]
        public void PlantedMovingLowsAreFoundTrackedAndTheirDriftIsRight()
        {
            var g = new MetricGrid(NLon, NLat, Radius);
            var fl = new FrontsAndLows(NLon, NLat, Radius, 101325.0, 10800.0, 21600.0, 120.0, 25.0, 20);
            var p = new double[NLon * NLat]; var t = new double[NLon * NLat]; var c = new double[NLon * NLat];
            for (int i = 0; i < t.Length; i++) { t[i] = 280.0; c[i] = 0.3; }
            double[] lons = { 0.3, 2.6, 4.7 };
            for (int f = 0; f < 24; f++)
            {
                PlantLows(g, p, 50.0, lons, 17.0, f * 10800.0, 2500.0, 150000.0);
                fl.Frame(p, t, c, false);
            }
            var s = fl.Summary(0, 24);
            double n = (double)s["d1_persistent_active_mean_per_hemisphere_north"];
            Assert.IsTrue(n > 2.4 && n <= 3.0, "persistent minima " + n);
            var tr = (Dictionary<string, object>)s["d1_tracker_north"];
            double drift = (double)tr["east_drift_ms_median"];
            Assert.AreEqual(17.0, drift, 2.0, "drift " + Json.Write(tr) + " minima per frame " + fl.MinimaByFrame.Count);
            Assert.AreEqual(0.0, (double)s["d1_persistent_active_mean_per_hemisphere_south"]);
        }

        [TestMethod]
        public void APlantedFrontIsFoundAndNegativeControlsFindNothing()
        {
            var g = new MetricGrid(NLon, NLat, Radius);
            var fl = new FrontsAndLows(NLon, NLat, Radius, 101325.0, 10800.0, 21600.0, 120.0, 25.0, 20);
            var p = new double[NLon * NLat]; var t = new double[NLon * NLat]; var c = new double[NLon * NLat];
            double lat0 = 50.0 * Math.PI / 180.0;
            for (int f = 0; f < 24; f++)
            {
                double lon0 = 1.0 + 17.0 * f * 10800.0 / (Radius * Math.Cos(lat0));
                for (int j = 0; j < NLat; j++)
                    for (int i = 0; i < NLon; i++)
                    {
                        double y = Radius * (g.LatRad[j] - lat0);
                        double x = Radius * Math.Cos(lat0) * DMath.WrapPi(g.LonRad[i] - lon0);
                        double along = x + 0.45 * y;
                        double env = Math.Exp(-(y * y) / (450000.0 * 450000.0) - (x * x) / (900000.0 * 900000.0));
                        t[j * NLon + i] = 280.0 + 5.0 * Math.Tanh(along / 50000.0) * env;
                        c[j * NLon + i] = 0.1 + 0.4 * env * (0.5 + 0.5 * Math.Tanh(along / 50000.0));
                    }
                Array.Clear(p, 0, p.Length);
                fl.Frame(p, t, c, false);
            }
            var s = fl.Summary(0, 24);
            double ev = (double)s["real_d2_events_total"];
            Assert.IsTrue(ev >= 1.0, "front events " + ev);
            Assert.IsTrue((double)s["control_shuffle_d2_events_total"] <= 0.05 * ev, "shuffled field finds fronts");
            Assert.IsTrue((double)s["control_noise_d2_events_total"] <= 0.05 * ev, "noise finds fronts");
        }

        [TestMethod]
        public void WhiteNoiseHasNoPersistentMinima()
        {
            var fl = new FrontsAndLows(NLon, NLat, Radius, 101325.0, 10800.0, 21600.0, 120.0, 25.0, 20);
            var rnd = new Random(5);
            var p = new double[NLon * NLat]; var t = new double[NLon * NLat]; var c = new double[NLon * NLat];
            for (int f = 0; f < 24; f++)
            {
                for (int i = 0; i < p.Length; i++) { p[i] = 300.0 * (rnd.NextDouble() - 0.5); t[i] = 280.0 + rnd.NextDouble(); c[i] = rnd.NextDouble() * 0.3; }
                fl.Frame(p, t, c, false);
            }
            var s = fl.Summary(0, 24);
            Assert.AreEqual(0.0, (double)s["d1_persistent_active_mean_per_hemisphere_north"], 0.1);
            Assert.AreEqual(0.0, (double)s["real_d2_events_total"], 0.5);
        }

        [TestMethod]
        public void TheGeneratorsOwnSystemsAreSeenByTheDetector()
        {
            // health check, not a criterion: with 3 h frames over 8 days the detector finds persistent minima on the Kerbin-like set
            var bp = BodyParams.KerbinLike();
            var g = new WeatherGenerator(bp, 1);
            var fl = new FrontsAndLows(NLon, NLat, Radius, bp.SurfacePressurePa, 10800.0, bp.RotationPeriodS, 100.0, 30.0, 20);
            var p = new double[NLon * NLat]; var t = new double[NLon * NLat]; var c = new double[NLon * NLat];
            for (int f = 0; f < 64; f++)
            {
                Study.Fields(g, 2.0e6 + f * 10800.0, NLon, NLat, p, t, c, null);
                fl.Frame(p, t, c, false);
            }
            var s = fl.Summary(0, 64);
            double n = (double)s["d1_persistent_active_mean_per_hemisphere_north"] + (double)s["d1_persistent_active_mean_per_hemisphere_south"];
            Assert.IsTrue(n > 0.5, "persistent minima (both hemispheres) " + n);
        }
    }

    [TestClass]
    public class ReconDataTests
    {
        private static string Dir()
        {
            string d = ReconData.FindDumpDir();
            if (d == null || !System.IO.File.Exists(System.IO.Path.Combine(d, "run1-heights-Kerbin.json")) || !System.IO.File.Exists(System.IO.Path.Combine(d, "run2-temperature-Kerbin.json")))
                Assert.Inconclusive("the B09 dumps are not in tests/data/recon/dumps");
            return d;
        }

        [TestMethod]
        public void TheKerbinGridIsReadNorthRowFirstWithTheMeasuredOceanFraction()
        {
            var g = ReconData.LoadHeights(System.IO.Path.Combine(Dir(), "run1-heights-Kerbin.json"));
            Assert.AreEqual(360, g.NLon); Assert.AreEqual(180, g.NLat);
            double oc = 0, w = 0;
            for (int j = 0; j < g.NLat; j++) { double c = Math.Cos(g.LatDeg(j) * Math.PI / 180.0); for (int i = 0; i < g.NLon; i++) { oc += g.Ocean[j * g.NLon + i] * c; w += c; } }
            Assert.AreEqual(0.52, oc / w, 0.03);
            Assert.IsTrue(g.LatDeg(0) > 89.0 && g.LatDeg(g.NLat - 1) < -89.0);
        }

        [TestMethod]
        public void TheFitReadsTheSeaLevelProfileAndTheGeneratorFollowsItAtTheSamplePoints()
        {
            string d = Dir();
            var bp = BodyParams.KerbinLike();
            var f = ReconData.Apply(bp, "Kerbin", d);
            Assert.AreEqual(309.7, f.ProfileK[0], 1.0);
            Assert.AreEqual(240.6, f.ProfileK[6], 1.0);
            Assert.IsTrue(f.LapseKPerM > 0.005 && f.LapseKPerM < 0.012, "lapse " + f.LapseKPerM);
            var der = new BodyDerived(bp);
            for (int k = 0; k <= 6; k++)
                Assert.AreEqual(f.ProfileK[k], der.AnnualK(k * 15.0 * Math.PI / 180.0, Math.Sin(k * 15.0 * Math.PI / 180.0)), 1e-9, "profile at " + k * 15);
            // between the samples it stays inside the neighbouring values plus a small overshoot
            for (double lat = 0; lat <= 90; lat += 0.5)
            {
                double v = der.AnnualK(lat * Math.PI / 180.0, Math.Sin(lat * Math.PI / 180.0));
                Assert.IsTrue(v > 235 && v < 315, "profile " + v + " at " + lat);
            }
            var g = new WeatherGenerator(bp, 1);
            Weather w;
            g.Sample(0.0, 0.3, 0.0, 1.0e6, out w);
            Assert.IsTrue(w.TempK > 295 && w.TempK < 325, "equator " + w.TempK);
        }
    }
}
