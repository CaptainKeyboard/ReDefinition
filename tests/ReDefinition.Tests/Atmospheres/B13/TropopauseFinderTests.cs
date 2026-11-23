using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests.Atmospheres.B13
{
    // The WMO lapse-rate rule of the measured tropopause (TropopauseFinder; owner 2026-11-23 "Messen, dann Anteile").
    [TestClass]
    public class TropopauseFinderTests
    {
        private static void Profile(Func<double, double> t, double top, out double[] z, out double[] k)
        {
            int n = (int)(top / 100.0) + 1;
            z = new double[n];
            k = new double[n];
            for (int i = 0; i < n; i++)
            {
                z[i] = i * 100.0;
                k[i] = t(z[i]);
            }
        }

        [TestMethod]
        public void TheStandardAtmospheresTropopauseIsAtElevenKilometres()
        {
            double[] z, k;
            Profile(h => h < 11000.0 ? 288.15 - 0.0065 * h : 216.65, 20000.0, out z, out k);
            double at;
            Assert.AreEqual(11000.0, TropopauseFinder.Find(z, k, out at), 1e-6);
            Assert.AreEqual(216.65, at, 1e-6);
        }

        [TestMethod]
        public void AProfileThatCoolsToItsTopHasNone()
        {
            double[] z, k;
            Profile(h => 300.0 - 0.006 * h, 30000.0, out z, out k);
            double at;
            Assert.IsTrue(double.IsNaN(TropopauseFinder.Find(z, k, out at)));
        }

        [TestMethod]
        public void AShortStableLayerBelowDoesNotCount()
        {
            // An inversion of 500 m at 2 km (the lapse rate falls to 0 there), then cooling again: the 2 km check above rejects it; the real tropopause is at 12 km.
            double[] z, k;
            Profile(h =>
            {
                if (h < 2000.0) return 290.0 - 0.0065 * h;
                if (h < 2500.0) return 277.0;
                if (h < 12000.0) return 277.0 - 0.0065 * (h - 2500.0);
                return 277.0 - 0.0065 * 9500.0;
            }, 20000.0, out z, out k);
            double at;
            Assert.AreEqual(12000.0, TropopauseFinder.Find(z, k, out at), 1e-6);
        }

        [TestMethod]
        public void AProfileTooShortToCheckHasNone()
        {
            double[] z, k;
            Profile(h => h < 9000.0 ? 288.0 - 0.0065 * h : 229.5, 10000.0, out z, out k);
            double at;
            Assert.IsTrue(double.IsNaN(TropopauseFinder.Find(z, k, out at)), "only 1 km above the candidate");
        }
    }
}
