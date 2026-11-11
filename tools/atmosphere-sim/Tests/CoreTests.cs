using System;
using System.IO;
using System.Threading;
using AtmoSim.Core;
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
            }
            for (double x = 0.001; x < 1e6; x *= 1.7) Assert.AreEqual(Math.Log(x), DMath.Log(x), 1e-11, "log " + x);
            Assert.AreEqual(Math.Atan2(-0.3, -2.0), DMath.Atan2(-0.3, -2.0), 1e-12);
            Assert.AreEqual(Math.Asin(0.4), DMath.Asin(0.4), 1e-12);
        }
    }

    [TestClass]
    public class SpectralTests
    {
        [TestMethod]
        public void GaussWeightsSumToTwoAndNodesAreSymmetric()
        {
            var g = new Spectral(64, 32, 21);
            double s = 0; foreach (double w in g.Weight) s += w;
            Assert.AreEqual(2.0, s, 1e-12);
            for (int j = 0; j < 32; j++) Assert.AreEqual(-g.Mu[31 - j], g.Mu[j], 1e-13);
        }

        [TestMethod]
        public void SynthesisAndAnalysisRoundTrip()
        {
            var g = new Spectral(64, 32, 21);
            var rng = new Rng(5);
            var re = new double[g.NCoef]; var im = new double[g.NCoef];
            for (int m = 0; m <= g.M; m++) for (int n = m; n <= g.M; n++) { int i = g.Index(m, n); re[i] = rng.Gauss(); im[i] = m == 0 ? 0 : rng.Gauss(); }
            var grid = new double[64 * 32];
            g.Synthesize(re, im, grid);
            var re2 = new double[g.NCoef]; var im2 = new double[g.NCoef];
            g.Analyze(grid, re2, im2);
            for (int i = 0; i < g.NCoef; i++) { Assert.AreEqual(re[i], re2[i], 1e-9); Assert.AreEqual(im[i], im2[i], 1e-9); }
        }

        [TestMethod]
        public void SolidBodyRotationGivesTheExpectedWind()
        {
            var g = new Spectral(64, 32, 21);
            double a = 600000.0, u0 = 20.0;
            // psi = -a u0 sin(lat): u = u0 cos(lat), v = 0
            var grid = new double[64 * 32];
            for (int j = 0; j < 32; j++) for (int i = 0; i < 64; i++) grid[j * 64 + i] = -a * u0 * g.Mu[j];
            var re = new double[g.NCoef]; var im = new double[g.NCoef];
            g.Analyze(grid, re, im);
            var uc = new double[64 * 32]; var vc = new double[64 * 32];
            g.SynthesizeWindCos(re, im, a, uc, vc);
            for (int j = 0; j < 32; j++) for (int i = 0; i < 64; i++)
            {
                Assert.AreEqual(u0 * g.CosLat[j] * g.CosLat[j], uc[j * 64 + i], 1e-6, "U");
                Assert.AreEqual(0.0, vc[j * 64 + i], 1e-6, "V");
            }
        }

        [TestMethod]
        public void FluxDivergenceOfASolidBodyFlowCarriesAPatternWithoutChangingIt()
        {
            // advecting q = P_2^2-like pattern by solid-body rotation changes it only by a phase shift: global integral of the tendency is zero
            var g = new Spectral(64, 32, 21);
            double a = 600000.0;
            var q = new double[64 * 32]; var A = new double[64 * 32]; var B = new double[64 * 32];
            for (int j = 0; j < 32; j++) for (int i = 0; i < 64; i++)
            {
                double lon = g.LonRad[i];
                q[j * 64 + i] = g.CosLat[j] * g.CosLat[j] * DMath.Cos(2 * lon);
                A[j * 64 + i] = 20.0 * g.CosLat[j] * g.CosLat[j] * q[j * 64 + i]; // U q with U = u cos(lat)
                B[j * 64 + i] = 0.0;
            }
            var tr = new double[g.NCoef]; var ti = new double[g.NCoef];
            g.FluxDivergence(A, B, a, tr, ti);
            var back = new double[64 * 32];
            g.Synthesize(tr, ti, back);
            // expected: -u0 cos(lat)/(a cos(lat)) dq/dlon = (u0/a) 2 cos^2 sin(2 lon)
            for (int j = 0; j < 32; j++) for (int i = 0; i < 64; i++)
            {
                double expect = (20.0 / a) * 2.0 * g.CosLat[j] * g.CosLat[j] * DMath.Sin(2 * g.LonRad[i]);
                Assert.AreEqual(expect, back[j * 64 + i], 2e-8);
            }
        }
    }

    [TestClass]
    public class SimulationTests
    {
        private static Atmosphere Small(ulong seed, Action<SimConfig> tweak = null)
        {
            var cfg = new SimConfig { NLon = 64, NLat = 32, Levels = 12, DynM = 15, Seed = seed };
            if (tweak != null) tweak(cfg);
            return new Atmosphere(BodyParams.KerbinLike(), cfg);
        }

        private static void Run(Atmosphere s, int steps) { for (int i = 0; i < steps; i++) s.Step(); }

        [TestMethod]
        public void SameSeedGivesTheSameStateHash()
        {
            var a = Small(3); var b = Small(3);
            Run(a, 48); Run(b, 48);
            Assert.AreEqual(a.StateHash(), b.StateHash());
            var c = Small(4); Run(c, 48);
            Assert.AreNotEqual(a.StateHash(), c.StateHash());
        }

        [TestMethod]
        public void TheHashDoesNotDependOnTheThread()
        {
            ulong main = 0, other = 0;
            var a = Small(9); Run(a, 30); main = a.StateHash();
            var t = new Thread(() => { var b = Small(9); Run(b, 30); other = b.StateHash(); });
            t.Start(); t.Join();
            Assert.AreEqual(main, other);
        }

        [TestMethod]
        public void CheckpointRestoreEqualsTheStraightRunBitForBit()
        {
            var straight = Small(2); Run(straight, 60);
            var s = Small(2); Run(s, 30);
            var ms = new MemoryStream(); s.SaveCheckpoint(ms);
            var r = Small(2); // a different instance, a different history
            Run(r, 5);
            ms.Position = 0; r.LoadCheckpoint(ms);
            Assert.AreEqual(s.StateHash(), r.StateHash());
            Run(r, 30);
            Assert.AreEqual(straight.StateHash(), r.StateHash());
        }

        [TestMethod]
        public void CheckpointLateInALongRunEqualsTheStraightRun()
        {
            // The sub-step count of a step depends on the winds of the previous step, so the checkpoint carries them (found by the year-long proof run).
            var straight = Small(5); Run(straight, 1500);
            var s = Small(5); Run(s, 1300);
            var ms = new MemoryStream(); s.SaveCheckpoint(ms);
            var r = Small(5); ms.Position = 0; r.LoadCheckpoint(ms);
            Run(r, 200);
            Assert.AreEqual(straight.StateHash(), r.StateHash());
        }

        [TestMethod]
        public void ACloneRunsForwardExactlyLikeTheOriginal()
        {
            var a = Small(6); Run(a, 20);
            var c = a.Clone();
            Assert.AreEqual(a.StateHash(), c.StateHash());
            Run(a, 24); Run(c, 24);
            Assert.AreEqual(a.StateHash(), c.StateHash());
        }

        [TestMethod]
        public void TheSimulationStaysFiniteAndBoundedForThirtyDays()
        {
            var s = Small(1);
            Run(s, 120);
            Assert.IsFalse(double.IsNaN(s.MaxWind));
            Assert.IsTrue(s.MaxWind < 150.0, "max wind " + s.MaxWind);
            for (int i = 0; i < s.T3.Length; i++) Assert.IsTrue(s.T3[i] > 100 && s.T3[i] < 450, "T " + s.T3[i]);
        }

        [TestMethod]
        public void TheWaterLedgerCloses()
        {
            var s = Small(1);
            Run(s, 120);
            Assert.AreEqual(0.0, s.WaterLedgerResidual, 1e-6);
        }

        [TestMethod]
        public void SwitchesChangeTheTrajectory()
        {
            var a = Small(1); var b = Small(1, c => c.CoriolisOn = false); var d = Small(1, c => c.OceanOn = false);
            Run(a, 24); Run(b, 24); Run(d, 24);
            Assert.AreNotEqual(a.StateHash(), b.StateHash());
            Assert.AreNotEqual(a.StateHash(), d.StateHash());
            foreach (double o in d.OceanFrac) Assert.AreEqual(0.0, o);
        }

        [TestMethod]
        public void NoOceanBodyHasNoSeaIceAndNoWaterSource()
        {
            var cfg = new SimConfig { NLon = 64, NLat = 32, Levels = 12, DynM = 15, Seed = 1 };
            var s = new Atmosphere(BodyParams.NoOcean(), cfg);
            Run(s, 60);
            foreach (double h in s.IceH) Assert.AreEqual(0.0, h);
            Assert.AreEqual(0.0, s.OceanSourceKgM2, 1e-9);
        }
    }

    [TestClass]
    public class EpochAndSubGridTests
    {
        [TestMethod]
        public void EpochLengthFollowsTheMeasuredStepCost()
        {
            Assert.AreEqual(666 - 2 * 100 - 2 * 24, EpochField.DeriveEpochSteps(0.030, 20.0, 100, 24));
            Assert.AreEqual(1, EpochField.DeriveEpochSteps(1.0, 20.0, 100, 24));
        }

        [TestMethod]
        public void TheStateAtATimeDoesNotDependOnTheOrderOfQueries()
        {
            var cfg = new SimConfig { NLon = 64, NLat = 32, Levels = 12, DynM = 15, Seed = 11 };
            var ut = 40 * 3600.0;
            var f1 = new EpochField(BodyParams.KerbinLike(), cfg, 30, 12, 6);
            var f2 = new EpochField(BodyParams.KerbinLike(), cfg, 30, 12, 6);
            Atmosphere n1, o1, n2, o2; double w1, w2;
            f1.StateAt(5 * 3600.0, out n1, out o1, out w1); // an earlier query first
            f1.StateAt(ut, out n1, out o1, out w1);
            f2.StateAt(ut, out n2, out o2, out w2);
            Assert.AreEqual(n1.StateHash(), n2.StateHash());
            Assert.AreEqual(w1, w2);
        }

        [TestMethod]
        public void TheSubGridRuleIsDeterministicAndKeepsTheCoreSizes()
        {
            var a = new SubCore[4]; var b = new SubCore[4];
            for (long cell = 0; cell < 200; cell++)
            {
                double w = 2.0 + cell % 30, depth = 3000.0 + 120.0 * (cell % 90);
                int na = SubGrid.Cores(cell, 123456.0, 7, w, depth, 29.5, 29.5, a);
                int nb = SubGrid.Cores(cell, 123456.0, 7, w, depth, 29.5, 29.5, b);
                Assert.AreEqual(na, nb);
                for (int k = 0; k < na; k++)
                {
                    Assert.AreEqual(a[k].RadiusKm, b[k].RadiusKm);
                    Assert.IsTrue(a[k].RadiusKm >= SubGrid.MinRadiusKm && a[k].RadiusKm <= SubGrid.MaxRadiusKm);
                    Assert.IsTrue(Math.Abs(a[k].OffsetEastKm) <= 14.75 && Math.Abs(a[k].OffsetNorthKm) <= 14.75);
                }
            }
            Assert.AreEqual(0, SubGrid.Cores(1, 0.0, 7, 0.5, 5000.0, 29.5, 29.5, a));
        }
    }
}
