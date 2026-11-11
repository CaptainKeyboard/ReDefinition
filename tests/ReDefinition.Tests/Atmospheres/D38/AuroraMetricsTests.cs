using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests.Atmospheres.D38
{
    // The artefact metrics of the earlier aurora layer (lid, drip, band), the sky test and the pose arithmetic of the series probe (L0).
    // The metrics are the ones the weather player and the game's real-sky rows apply; here each is shown to see the artefact it is named
    // for and to stay low for a soft shell, so that a row of zero means "no artefact" and not "cannot see one".
    [TestClass]
    public class AuroraMetricsTests
    {
        private const int W = 120, H = 100;

        private static float[] Empty()
        {
            return new float[W * H];
        }

        // A soft band: rows 40 to 79 rise and fall as a half sine, the same in every column.
        private static float[] SoftBand()
        {
            float[] m = Empty();
            for (int y = 40; y < 80; y++)
            {
                float v = 0.05f * (float)Math.Sin((y - 40) / 40.0 * Math.PI);
                for (int x = 0; x < W; x++) m[y * W + x] = v;
            }
            return m;
        }

        [TestMethod]
        public void ASoftBandHasNoLidNoDripsAndNoHorizonBand()
        {
            AuroraMetrics.Result r = AuroraMetrics.MeasureMask(SoftBand(), W, H, 25.0, 60.0);
            Assert.IsTrue(r.Judged);
            Assert.IsTrue(r.Lid < 0.12f, "lid " + r.Lid);
            Assert.IsTrue(r.Drip < 0.03f, "drip " + r.Drip);
            Assert.IsTrue(r.Band < 0.02f, "band " + r.Band);
        }

        [TestMethod]
        public void AHardLowerEdgeIsSeenByTheLidMetric()
        {
            float[] m = Empty();
            for (int y = 40; y < 80; y++)
                for (int x = 0; x < W; x++) m[y * W + x] = 0.05f;
            AuroraMetrics.Result r = AuroraMetrics.MeasureMask(m, W, H, 25.0, 60.0);
            Assert.IsTrue(r.Lid > 0.9f, "a step from nothing to the full row mean reads about 1: " + r.Lid);
        }

        [TestMethod]
        public void DripsBelowTheBaseLineAreSeenByTheDripMetric()
        {
            float[] m = SoftBand();
            // Every fifth column hangs a lit streak 25 rows below the base of the band (more than 6 % of the height).
            int streaks = 0;
            for (int x = 0; x < W; x += 5)
            {
                streaks++;
                for (int y = 15; y < 55; y++) m[y * W + x] = 0.05f;
            }
            AuroraMetrics.Result r = AuroraMetrics.MeasureMask(m, W, H, 25.0, 60.0);
            Assert.IsTrue(r.Drip > 0.1f, "drip " + r.Drip + " for " + streaks + " streaks in " + W + " columns");
        }

        [TestMethod]
        public void LightOnTheHorizonIsSeenByTheBandMetric()
        {
            float[] m = SoftBand();
            // At a view elevation of 25 degrees and 60 degrees of field of view the rows near the bottom look below 3 degrees.
            for (int y = 0; y < 15; y++)
                for (int x = 0; x < W; x++) m[y * W + x] = 0.04f;
            AuroraMetrics.Result r = AuroraMetrics.MeasureMask(m, W, H, 25.0, 60.0);
            Assert.IsTrue(r.Band > 0.3f, "band " + r.Band);
            Assert.IsTrue(AuroraMetrics.RowElevationDeg(0, H, 25.0, 60.0) < 3.0, "row 0 looks below 3 degrees");
            Assert.IsTrue(AuroraMetrics.RowElevationDeg(H - 1, H, 25.0, 60.0) > 45.0, "the top row looks up");
        }

        [TestMethod]
        public void ADimPoseIsNotJudgedAndCarriesNoNumbers()
        {
            float[] m = SoftBand();
            for (int i = 0; i < m.Length; i++) m[i] *= 0.02f;
            AuroraMetrics.Result r = AuroraMetrics.MeasureMask(m, W, H, 25.0, 60.0);
            Assert.IsFalse(r.Judged);
            Assert.AreEqual(0f, r.Lid);
            Assert.AreEqual(0f, r.Drip);
            Assert.AreEqual(0f, r.Band);
        }

        [TestMethod]
        public void TheMaskIsThePositivePartOfTheDifference()
        {
            float[] mask = AuroraMetrics.Mask(new[] { 0.5f, 0.1f, 0.3f }, new[] { 0.2f, 0.4f, 0.3f });
            Assert.AreEqual(0.3f, mask[0], 1e-6f);
            Assert.AreEqual(0f, mask[1]);
            Assert.AreEqual(0f, mask[2]);
        }

        [TestMethod]
        public void TheRectangleMeansAndTheBrightestBlockFindTheShell()
        {
            float[] m = Empty();
            for (int y = 60; y < 70; y++)
                for (int x = 90; x < 100; x++) m[y * W + x] = 1f;
            int cx, cy;
            AuroraMetrics.BrightestBlock(m, W, H, 10, out cx, out cy);
            Assert.IsTrue(Math.Abs(cx - 95) <= 5 && Math.Abs(cy - 65) <= 5, "the block centre " + cx + ", " + cy);
            Assert.AreEqual(1.0, AuroraMetrics.MeanMask(m, W, H, 90, 60, 100, 70, true), 1e-9);
            Assert.AreEqual(0.0, AuroraMetrics.MeanMask(m, W, H, 0, 60, 80, 70, true), 1e-9);
            Assert.AreEqual(100.0 / (W * H), AuroraMetrics.LitShare(m, 0.5f), 1e-9);
        }

        [TestMethod]
        public void ASphereAtTheCentreHasTheAngularRadiusInPixels()
        {
            // 60 degrees of field of view over 100 rows: 30 degrees from the centre to the top edge are 50 rows (tan 30 = 0.577 maps to 50).
            double px = AuroraMetrics.SphereRadiusPx(Math.Atan(0.5 * Math.Tan(30.0 * Math.PI / 180.0)), 60.0, 100);
            Assert.AreEqual(25.0, px, 1e-6);
        }

        [TestMethod]
        public void TheMedianAndSpreadOfASample()
        {
            float median, spread;
            AuroraMetrics.MedianAndSpread(new[] { 0.3f, 0.31f, 0.3f, 0.29f, 0.3f, 0.3f, 0.3f, 0.3f, 0.3f, 0.3f }, out median, out spread);
            Assert.AreEqual(0.3f, median, 1e-6f);
            Assert.IsTrue(spread < 0.02f);
        }

        [TestMethod]
        public void TheSkyTestNamesAndNumbersRoundTrip()
        {
            foreach (AuroraSkyTest mode in Enum.GetValues(typeof(AuroraSkyTest)))
                Assert.AreEqual(mode, AuroraSkyTests.FromNumber((float)mode), mode.ToString());
            Assert.AreEqual(AuroraSkyTest.ZBuffer, AuroraSkyTests.FromNumber(17f), "out of range is the default");
            Assert.AreEqual(AuroraSkyTest.ZBuffer, AuroraSkyTests.FromNumber(-1f));
            Assert.AreEqual(AuroraEvent.AfterForwardAlpha, AuroraSkyTests.EventFromNumber(1f));
            Assert.AreEqual(AuroraEvent.BeforeForwardAlpha, AuroraSkyTests.EventFromNumber(0f));
            Assert.IsFalse(AuroraSkyTests.UsesCopy(AuroraSkyTest.ZBuffer));
            Assert.IsFalse(AuroraSkyTests.UsesCopy(AuroraSkyTest.None));
            Assert.IsTrue(AuroraSkyTests.UsesCopy(AuroraSkyTest.CopyResolved));
            Assert.IsFalse(AuroraSkyTests.NeedsDepthTexture(AuroraSkyTest.CopyResolved), "the resolved depth is a render target, not the global texture");
            Assert.IsTrue(AuroraSkyTests.NeedsDepthTexture(AuroraSkyTest.CopyGlobal));
            Assert.AreEqual(5, new System.Collections.Generic.HashSet<string>(new[]
            {
                AuroraSkyTests.Name(AuroraSkyTest.ZBuffer), AuroraSkyTests.Name(AuroraSkyTest.CopyDepth), AuroraSkyTests.Name(AuroraSkyTest.CopyResolved),
                AuroraSkyTests.Name(AuroraSkyTest.CopyGlobal), AuroraSkyTests.Name(AuroraSkyTest.None)
            }).Count, "five distinct names");
        }

        [TestMethod]
        public void TheCopyTestTakesTheFarValueOrTheMeasuredSkyValue()
        {
            // The first build's test: the far value only (1 for a normal buffer, 0 for a reversed one).
            Assert.IsTrue(AuroraSkyTests.IsSky(1f, false, 0f, 0f));
            Assert.IsFalse(AuroraSkyTests.IsSky(0.314f, false, 0f, 0f), "RECON P6 read 0.314 in an empty view with Scatterer: the far-value test discards it");
            Assert.IsTrue(AuroraSkyTests.IsSky(0f, true, 0f, 0f));
            Assert.IsFalse(AuroraSkyTests.IsSky(0.5f, true, 0f, 0f));
            // A measured sky value with a tolerance.
            Assert.IsTrue(AuroraSkyTests.IsSky(0.314f, false, 0.314f, 0.01f));
            Assert.IsFalse(AuroraSkyTests.IsSky(0.342f, false, 0.314f, 0.01f), "the nearest ball of P6 (0.342) is not sky");
        }

        [TestMethod]
        public void TheCalibrationSeparatesTwoGroupsOrSaysItCannot()
        {
            float reference, tolerance;
            Assert.IsTrue(AuroraSkyTests.Calibrate(0.314f, 0.002f, 0.342f, out reference, out tolerance));
            Assert.AreEqual(0.314f, reference, 1e-6f);
            Assert.IsTrue(tolerance > 0.002f && tolerance <= 0.014f, "tolerance " + tolerance);
            Assert.IsFalse(AuroraSkyTests.Calibrate(0.314f, 0.02f, 0.325f, out reference, out tolerance), "overlapping groups have no separating reference");
        }

        [TestMethod]
        public void AForcedActivityGivesTheModelsOwnFrame()
        {
            for (double ut = 0; ut < 3e6; ut += 98765.4)
            {
                AuroraFrame a = AuroraModel.Compute(ut, -12f, true, 60f, 1f);
                float strong = AuroraModel.StrongEvent(ut);
                AuroraFrame b = AuroraModel.FromActivity(Math.Max(AuroraModel.Activity(ut), strong), strong, -12f, 60f, 1f);
                Assert.AreEqual(a.Activity, b.Activity);
                Assert.AreEqual(a.RingLatDeg, b.RingLatDeg);
                Assert.AreEqual(a.Intensity, b.Intensity);
                Assert.AreEqual(a.Draw, b.Draw);
            }
            Assert.IsFalse(AuroraModel.FromActivity(1f, 0f, -12f, 60f, 0f).Draw, "no strength, no draw");
            Assert.IsFalse(AuroraModel.FromActivity(1f, 0f, 10f, 60f, 1f).Draw, "no night, no draw");
            Assert.AreEqual(60f, AuroraModel.FromActivity(1f, 1f, -12f, 60f, 1f).RingLatDeg, "the strong event puts the oval at the floor");
        }

        [TestMethod]
        public void TheCameraFacesTheOvalFromBothSides()
        {
            Assert.AreEqual(0f, AuroraModel.PoseYawDeg(66f, 60f), "the oval is poleward: look north");
            Assert.AreEqual(180f, AuroraModel.PoseYawDeg(66f, 70f), "the oval is equatorward: look south");
        }

        [TestMethod]
        public void TheProbeAsksForASunTheLatitudeReaches()
        {
            Assert.AreEqual(-12f, D38Probe.WantedSunDeg(60f));
            Assert.AreEqual(-12f, D38Probe.WantedSunDeg(70f));
            // At 78 degrees the midnight sun of a body without tilt stands at -12: one degree above is asked for.
            Assert.AreEqual(-11f, D38Probe.WantedSunDeg(78f), 1e-5f);
            Assert.IsTrue(D38Probe.WantedSunDeg(78f) >= -(90f - 78f));
        }
    }
}
