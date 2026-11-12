using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Atmospheres.Forecast;
using ReDefinition.Core;
using ReDefinition.Tests.Atmospheres.B12;
using ReDefinition.Tests.Atmospheres.C25;

namespace ReDefinition.Tests.Atmospheres.D30
{
    // K-W3a, D30 alert phase: the probe forced a snow disc at the Space Centre and waited 90 s for the engine to find it
    // (D30.alert_eta_found); it never did. This is the same sequence on the real GeneratorField without the game: a site is watched, a
    // report is made, the disc is forced, and the service has to serve a threshold ETA at the disc's start. The values are the probe's
    // (80 km, 400 mm/h of snow from 8 to 14 hours ahead, alerts from Heavy).
    [TestClass]
    public class ForcedStormForecastTests
    {
        private const double H = 3600.0;
        private const float StormRate = 400f;

        // Runs the service the way the module does until the worker is idle and the slow tick has run twice.
        private static void Settle(ForecastService service, double now)
        {
            for (int i = 0; i < 4; i++)
            {
                service.Tick(2.5);
                service.SlowTick(now);
                Assert.IsTrue(service.WaitIdle(30000), "the worker did not finish");
                service.Pump();
            }
        }

        private static ForecastResult RunProbeSequence(bool bumpGeneration, out double eta, out double now)
        {
            GeneratorField field = FieldFixture.Field(11);
            now = 40.0 * 86400.0 + 1234.0;
            var ctx = new ForecastContext(new ForecastRules(3600.0, 24.0), 600000.0, alerts: new AlertRules(3.0, PrecipBand.Heavy, 0f));
            var env = new TestEnvironment { FieldValue = field, Ut = now, Contexts = b => ctx };
            var service = new ForecastService(env);
            ForecastSite site = ForecastModule.SiteAt(FieldFixture.BodyIndex, -0.1, -74.6, 64.8, "Space Centre");
            service.SetAutoWatched(new List<ForecastSite> { site }, new List<bool> { true });
            Settle(service, now);
            ForecastResult before = service.DetailOf(site);
            Assert.IsNotNull(before, "a result exists before the disc");

            field.AddPrecipitationDisc(FieldFixture.BodyIndex, site.LatDeg, site.LonDeg, 80.0, StormRate, PrecipType.Snow, now + 8.0 * H, now + 14.0 * H);
            if (bumpGeneration) WeatherHooks.BumpForecastGeneration();
            now += 3.0;
            env.Ut = now;
            Settle(service, now);
            ForecastResult after = service.DetailOf(site);
            eta = after == null ? double.NaN : after.ThresholdEtaUt;
            return after;
        }

        // K-W3b2, D30: the game calls Tick only (the slow tick runs every 300 game seconds), nobody asks ForecastOf while the probe waits,
        // and 3 game seconds pass between the report and the disc. The sequence: a report, a scene change (the clock is NaN for a while), the
        // disc, the probe's generation bump, then only Tick with the real frame time of a second.
        private static ForecastResult RunGameSequence(bool bumpGeneration, bool sceneChange, out double eta, out double now)
        {
            GeneratorField field = FieldFixture.Field(11);
            now = 40.0 * 86400.0 + 1234.0;
            var ctx = new ForecastContext(new ForecastRules(3600.0, 24.0), 600000.0, alerts: new AlertRules(3.0, PrecipBand.Heavy, 0f));
            var env = new TestEnvironment { FieldValue = field, Ut = now, Contexts = b => ctx };
            var service = new ForecastService(env);
            ForecastSite site = ForecastModule.SiteAt(FieldFixture.BodyIndex, -0.1, -74.6, 64.8, "Space Centre");
            service.SetAutoWatched(new List<ForecastSite> { site }, new List<bool> { true });
            TickUntilIdle(service, 6);
            ForecastResult before = service.DetailOf(site);
            Assert.IsNotNull(before, "a result exists before the disc");
            // The fixture's own weather has a heavy spell 10.3 h ahead; the disc is earlier.
            Assert.IsTrue(before.ThresholdEtaUt > now + 9.0 * H, "the natural spell is later than the disc");

            if (sceneChange)
            {
                env.Ut = double.NaN;
                service.Tick(1.0);
                Assert.IsFalse(service.Available);
            }
            now += 10.0;
            env.Ut = now;
            field.AddPrecipitationDisc(FieldFixture.BodyIndex, site.LatDeg, site.LonDeg, 80.0, StormRate, PrecipType.Snow, now + 8.0 * H, now + 14.0 * H);
            if (bumpGeneration) WeatherHooks.BumpForecastGeneration();
            for (int i = 0; i < 12; i++)
            {
                now += 1.0;
                env.Ut = now;
                TickUntilIdle(service, 1);
            }
            ForecastResult after = service.DetailOf(site);
            eta = after == null ? double.NaN : after.ThresholdEtaUt;
            return after;
        }

        private static void TickUntilIdle(ForecastService service, int ticks)
        {
            for (int i = 0; i < ticks; i++)
            {
                service.Tick(1.0);
                Assert.IsTrue(service.WaitIdle(30000), "the worker did not finish");
                service.Pump();
            }
        }

        [TestMethod]
        public void TheTickAloneFindsAForcedDiscAfterTheProbesGenerationBump()
        {
            double eta, now;
            RunGameSequence(true, false, out eta, out now);
            Assert.IsFalse(double.IsNaN(eta), "the stale report stays: the tick does not refresh a report of an older generation");
            Assert.AreEqual(now - 2.0 + 8.0 * H, eta, 1800.0);
        }

        [TestMethod]
        public void TheTickAloneFindsAForcedDiscAfterTheDefinitionChangeAndASceneChange()
        {
            double eta, now;
            RunGameSequence(false, true, out eta, out now);
            Assert.AreEqual(now - 2.0 + 8.0 * H, eta, 1800.0, "the stale report stays after a definition change");
        }

        [TestMethod]
        public void AReportComputedWhileTheGenerationRoseIsComputedAgain()
        {
            GeneratorField field = FieldFixture.Field(11);
            double now = 40.0 * 86400.0;
            var ctx = new ForecastContext(new ForecastRules(3600.0, 24.0), 600000.0, alerts: new AlertRules(3.0, PrecipBand.Heavy, 0f));
            var env = new TestEnvironment { FieldValue = field, Ut = now, Contexts = b => ctx };
            var service = new ForecastService(env);
            ForecastSite site = ForecastModule.SiteAt(FieldFixture.BodyIndex, -0.1, -74.6, 64.8, "Space Centre");
            service.SetAutoWatched(new List<ForecastSite> { site }, new List<bool> { true });
            service.Tick(1.0);
            // The first request is in flight (or done); the generation rises under it.
            WeatherHooks.BumpForecastGeneration();
            Assert.IsTrue(service.WaitIdle(30000));
            for (int i = 0; i < 4; i++)
            {
                now += 1.0;
                env.Ut = now;
                TickUntilIdle(service, 1);
            }
            ForecastResult r = service.DetailOf(site);
            Assert.IsNotNull(r, "a report of the current generation exists");
            Assert.AreEqual(WeatherHooks.ForecastGeneration, r.Report.Generation);
        }

        [TestMethod]
        public void AForcedDiscAfterAnEarlierReportIsFoundByTheServiceThroughTheDefinitionChangeAlone()
        {
            double eta, now;
            ForecastResult result = RunProbeSequence(false, out eta, out now);
            Assert.IsNotNull(result);
            Assert.IsFalse(double.IsNaN(eta), "the forced snow disc is not found: the report served is the one from before the disc");
            Assert.AreEqual(now + 8.0 * H, eta, 1800.0, "the ETA is at the disc's start");
        }

        [TestMethod]
        public void AForcedDiscIsFoundWhenTheProbeBumpsTheGenerationAsWell()
        {
            double eta, now;
            RunProbeSequence(true, out eta, out now);
            Assert.IsFalse(double.IsNaN(eta), "the forced snow disc is not found after the generation bump");
            Assert.AreEqual(now + 8.0 * H, eta, 1800.0);
        }
    }
}
