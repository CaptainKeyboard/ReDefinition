using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Atmospheres.Forecast;
using ReDefinition.Core;

namespace ReDefinition.Tests.Atmospheres.B12
{
    // B12: the service around the engine (queue, worker, main-thread chunks, generation, slow tick, alerts) and the module that assigns
    // the seam, run without the game against a test environment.
    [TestClass]
    public class ForecastServiceTests
    {
        private const double H = 3600.0;
        private const int Body = 1;

        private sealed class Provider : WeatherSourceBase
        {
            internal bool Forecasts;
            internal bool SafeFlag = true;
            internal int PriorityValue = 10;
            internal float East = 12f;

            public override string Name { get { return "KWP"; } }
            public override int Priority { get { return PriorityValue; } }
            public override bool ThreadSafe { get { return SafeFlag; } }

            public override bool TrySample(int bodyIndex, double latDeg, double lonDeg, double altM, double ut, out WeatherSample sample)
            {
                sample = WeatherSample.Clear(bodyIndex);
                sample.WindWorld = new UnityEngine.Vector3(7f, 0f, 0f);
                return true;
            }

            public override bool TryWindForecast(ForecastSite site, double ut, out float eastMs, out float northMs, out float gustMs, out float confidence01)
            {
                eastMs = East;
                northMs = 0f;
                gustMs = East * 1.5f;
                confidence01 = 0.8f;
                return Forecasts;
            }
        }

        private static ForecastSite Site(double lat = 50.0, double lon = 10.0)
        {
            return new ForecastSite { BodyIndex = Body, LatDeg = lat, LonDeg = lon, Name = "Pad" };
        }

        private static ForecastContext Context(AlertRules alerts = null, IWeatherSource[] sources = null, WindSourceMode mode = WindSourceMode.Auto, bool geometry = true)
        {
            return new ForecastContext(new ForecastRules(3600.0, 24.0), geometry ? MovingCellsField.RadiusM : 0.0, windSource: mode, windSources: sources,
                alerts: alerts ?? new AlertRules(windMs: 0f));
        }

        private static TestEnvironment Env(IWeatherField field, double ut = 0.0, ForecastContext context = null)
        {
            ForecastContext ctx = context ?? Context();
            return new TestEnvironment { FieldValue = field, Ut = ut, Contexts = b => ctx };
        }

        private static ForecastReport Await(ForecastService service, ForecastSite site)
        {
            ForecastReport r = service.ForecastOf(site);
            service.WaitIdle(5000);
            service.DrainSynchronously();
            return service.ForecastOf(site);
        }

        [TestMethod]
        public void TheSeamIsNullBeforeTheFieldIsReadyAndSetAfterAndTakenBackWhenItIsNot()
        {
            Func<ForecastSite, ForecastReport> savedForecast = WeatherHooks.Forecast;
            Func<int, ForecastTrack[]> savedTracks = WeatherHooks.BodyTracks;
            try
            {
                WeatherHooks.Forecast = null;
                WeatherHooks.BodyTracks = null;
                var field = new ScriptedField { ReadyState = FieldReadiness.Seeding };
                var env = Env(field);
                var service = new ForecastService(env);
                var module = new ForecastModule(service);
                module.Init();
                module.Tick(0.016f);
                Assert.IsNull(WeatherHooks.Forecast, "Seeding: no forecast.");
                Assert.IsNull(WeatherHooks.BodyTracks);
                field.ReadyState = FieldReadiness.CatchingUp;
                module.Tick(0.016f);
                Assert.IsNull(WeatherHooks.Forecast, "CatchingUp: no forecast.");
                field.ReadyState = FieldReadiness.Ready;
                module.Tick(0.016f);
                Assert.IsNotNull(WeatherHooks.Forecast);
                Assert.IsNotNull(WeatherHooks.BodyTracks);
                ForecastReport first = WeatherHooks.Forecast(Site());
                Assert.IsFalse(first.Available, "The first answer is the placeholder while the report is computed.");
                service.WaitIdle(5000);
                Assert.IsTrue(WeatherHooks.Forecast(Site()).Available);
                field.ReadyState = FieldReadiness.Broken;
                module.Tick(0.016f);
                Assert.IsNull(WeatherHooks.Forecast, "Broken: the seam is taken back.");
                field.ReadyState = FieldReadiness.Ready;
                module.Tick(0.016f);
                Assert.IsNotNull(WeatherHooks.Forecast);
                // Whoever assigned last wins: a seam that is not ours stays.
                Func<ForecastSite, ForecastReport> foreign = s => null;
                WeatherHooks.Forecast = foreign;
                module.Shutdown();
                Assert.AreSame(foreign, WeatherHooks.Forecast);
                Assert.IsNull(WeatherHooks.BodyTracks);
            }
            finally
            {
                WeatherHooks.Forecast = savedForecast;
                WeatherHooks.BodyTracks = savedTracks;
            }
        }

        [TestMethod]
        public void TheNullFieldAndADisabledForecastGiveNoSeam()
        {
            var service = new ForecastService(Env(NullField.Instance));
            service.Tick(0.5);
            Assert.IsFalse(service.Available);
            var env = Env(new ScriptedField());
            env.EnabledValue = false;
            service = new ForecastService(env);
            service.Tick(0.5);
            Assert.IsFalse(service.Available);
            Assert.IsFalse(service.ForecastOf(Site()).Available);
            Assert.IsNull(service.BodyTracks(Body));
        }

        [TestMethod]
        public void AnEstimateIsReportedAsUnavailableAndNotCached()
        {
            var field = new ScriptedField { Level = ut => 0.7f, Approx = true };
            var service = new ForecastService(Env(field, 0.0, Context(geometry: false)));
            service.Tick(0.016);
            ForecastReport report = Await(service, Site());
            Assert.IsFalse(report.Available, "Approximate data is not a forecast.");
            Assert.AreEqual(0, service.Cache.SiteCount);
            field.Approx = false;
            field.ReadyState = FieldReadiness.Ready;
        }

        [TestMethod]
        public void AFieldThatIsNotThreadSafeIsComputedOnTheMainThreadOneUnitPerCall()
        {
            foreach (bool hidden in new[] { false, true })
            {
                var inner = new MovingCellsField(5, 12, false);
                IWeatherField field = hidden ? (IWeatherField)new HiddenSystemsField(inner) : inner;
                var service = new ForecastService(Env(field, 3.0 * 86400.0));
                service.Tick(0.016);
                ForecastReport placeholder = service.ForecastOf(Site(60.0, 20.0));
                Assert.IsFalse(placeholder.Available);
                int max = 0, calls = 0, ticks = 0;
                while (!service.ForecastOf(Site(60.0, 20.0)).Available)
                {
                    int before = inner.SampleCalls;
                    service.Pump(0.0, 1);
                    max = Math.Max(max, inner.SampleCalls - before);
                    calls += inner.SampleCalls - before;
                    Assert.IsTrue(++ticks < 400000, "The chunked computation did not finish.");
                }
                Console.WriteLine("B12 chunks (" + (hidden ? "search of the field" : "systems listed") + "): " + ticks + " Pump calls, at most " + max
                    + " field samples in one call, " + calls + " in all");
                Assert.IsTrue(max <= 1, "A call took " + max + " samples.");

                var reference = new MovingCellsField(5, 12, true);
                var other = new ForecastService(Env(hidden ? (IWeatherField)new HiddenSystemsField(reference) : reference, 3.0 * 86400.0));
                other.Tick(0.016);
                ForecastReport worker = Await(other, Site(60.0, 20.0));
                ForecastReport chunked = service.ForecastOf(Site(60.0, 20.0));
                for (int k = 0; k < 24; k++)
                {
                    Assert.AreEqual(worker.Steps[k].Level01, chunked.Steps[k].Level01);
                    Assert.AreEqual(worker.Steps[k].CellCount, chunked.Steps[k].CellCount);
                }
                Assert.AreEqual(worker.Tracks.Length, chunked.Tracks.Length);
                Assert.AreEqual(worker.NearestSystemEtaUt.ToString(), chunked.NearestSystemEtaUt.ToString());
            }
        }

        [TestMethod]
        public void ABumpOfTheGenerationInvalidatesTheCacheAndTheOldReport()
        {
            var field = new MovingCellsField(9);
            var service = new ForecastService(Env(field, 86400.0));
            service.Tick(0.016);
            ForecastReport first = Await(service, Site());
            Assert.IsTrue(first.Available);
            long computed = service.Cache.ComputedSteps;
            Assert.AreEqual(1, service.Cache.SiteCount);
            long generation = WeatherHooks.BumpForecastGeneration();
            ForecastReport during = service.ForecastOf(Site());
            Assert.IsFalse(during.Available, "A report from before the bump is stale and not served.");
            service.WaitIdle(5000);
            ForecastReport after = service.ForecastOf(Site());
            Assert.IsTrue(after.Available);
            Assert.AreEqual(generation, after.Generation);
            Assert.AreEqual(computed + 24, service.Cache.ComputedSteps, "Nothing is reused across a generation.");
        }

        [TestMethod]
        public void ASeedChangeGivesAnotherForecastAndNeverTheOldCachedOne()
        {
            var env = Env(new MovingCellsField(7), 5.0 * 86400.0);
            var service = new ForecastService(env);
            service.Tick(0.016);
            ForecastReport a = Await(service, Site(55.0, 12.0));
            env.FieldValue = new MovingCellsField(8);
            service.Tick(0.016);
            ForecastReport b = Await(service, Site(55.0, 12.0));
            ForecastReport direct = ForecastEngine.Compute(Site(55.0, 12.0), 5.0 * 86400.0, 24.0, Context(), new MovingCellsField(8));
            int differ = 0;
            for (int k = 0; k < 24; k++)
            {
                if (a.Steps[k].Level01 != b.Steps[k].Level01) differ++;
                Assert.AreEqual(direct.Steps[k].Level01, b.Steps[k].Level01, "The new report is the new field's own.");
            }
            Console.WriteLine("B12 seed change: " + differ + " of 24 steps differ");
            Assert.IsTrue(differ > 0);
            // The same seed again, another instance: the same numbers.
            ForecastReport c = ForecastEngine.Compute(Site(55.0, 12.0), 5.0 * 86400.0, 24.0, Context(), new MovingCellsField(8));
            for (int k = 0; k < 24; k++) Assert.AreEqual(direct.Steps[k].Level01, c.Steps[k].Level01);
        }

        [TestMethod]
        public void ASwapToAnotherKindOfFieldGivesThatFieldsOwnForecast()
        {
            var env = Env(new MovingCellsField(7), 4.0 * 86400.0);
            var service = new ForecastService(env);
            service.Tick(0.016);
            Assert.IsTrue(Await(service, Site()).Available);
            env.FieldValue = new ScriptedField { Level = ut => 0.3f, Id = 99 };
            service.Tick(0.016);
            ForecastReport swapped = Await(service, Site());
            Assert.IsTrue(swapped.Available);
            Assert.IsTrue(swapped.Steps.All(s => s.Level01 == 0.3f && s.Band == PrecipBand.Light));
        }

        [TestMethod]
        public void ARulesChangeRaisesTheGenerationAndDropsTheOldReports()
        {
            var field = new MovingCellsField(9);
            ForecastContext first = Context();
            ForecastContext second = new ForecastContext(new ForecastRules(3600.0, 24.0), MovingCellsField.RadiusM, uncertainty01: 0.5f);
            var env = Env(field, 86400.0, first);
            var service = new ForecastService(env);
            service.Tick(0.016);
            ForecastReport a = Await(service, Site());
            long generation = WeatherHooks.ForecastGeneration;
            env.Contexts = b => second;
            ForecastReport during = service.ForecastOf(Site());
            Assert.IsTrue(WeatherHooks.ForecastGeneration > generation, "Changed rules raise the generation.");
            Assert.IsFalse(during.Available);
            ForecastReport b2 = Await(service, Site());
            Assert.IsTrue(b2.Available);
            Assert.AreEqual(WeatherHooks.ForecastGeneration, b2.Generation);
            Assert.IsTrue(b2.Steps[23].Confidence01 < 1f, "The new rules (uncertainty) are in the new report.");
            Assert.AreEqual(1f, a.Steps[23].Confidence01);
        }

        [TestMethod]
        public void AWarpOfOneHundredThousandTimesJumpsPastTheHorizonEveryTickWithoutRepeatsOrFailures()
        {
            // A stay of 3 hours every 20 hours; each tick moves the UT by 2.5 s x 100 000 = 250 000 s, more than the 24 h of the report.
            var field = new ScriptedField { Level = ut => (ut % (20 * H)) >= 10 * H && (ut % (20 * H)) < 13 * H ? 0.7f : 0f };
            var raised = new List<ForecastAlert>();
            var env = Env(field, 0.0, Context(new AlertRules(3.0, PrecipBand.Heavy, 0f), geometry: false));
            var service = new ForecastService(env, null, raised.Add);
            service.Tick(0.016);
            ForecastSite site = Site();
            service.Watch(site, false);
            Await(service, site);
            for (int i = 0; i < 60; i++)
            {
                env.Ut += 2.5 * 100000.0;
                service.Tick(2.5);
                Assert.IsTrue(service.WaitIdle(5000));
            }
            Assert.IsNull(service.LastFailure);
            ForecastReport last = service.ForecastOf(site);
            Assert.IsTrue(last.Available);
            Assert.AreEqual(env.Ut, last.IssuedUt, 1.0, "The newest report is the one of the last UT.");
            Console.WriteLine("B12 warp 100000x: 60 ticks of 250000 s, " + raised.Count + " alerts, " + raised.Select(a => a.Kind + "@" + a.Ut).Distinct().Count() + " distinct");
            Assert.IsTrue(raised.Count > 0);
            Assert.AreEqual(raised.Count, raised.Select(a => a.Kind + "@" + a.Ut).Distinct().Count(), "No alert is repeated.");
        }

        [TestMethod]
        public void AFieldWhoseVersionChangesAtEveryCallStillSlidesThroughTheCache()
        {
            var field = new MovingCellsField(14, 12, true, true);
            var env = Env(field, 2.0 * 86400.0);
            var service = new ForecastService(env);
            service.Tick(0.016);
            Await(service, Site());
            long reusedBefore = service.Cache.ReusedSteps;
            env.Ut += H;
            service.SlowTick(env.Ut);
            service.WaitIdle(5000);
            Assert.IsTrue(field.Version > 100);
            Assert.AreEqual(reusedBefore + 23, service.Cache.ReusedSteps, "The slide reuses 23 of 24 steps although Version moved.");
        }

        [TestMethod]
        public void AWarpJumpOverASystemsEtaThroughTheServiceGivesOneApproachingAndOneArrivedAndARewindRearms()
        {
            var field = new ScriptedField { Level = ut => ut >= 10 * H && ut < 16 * H ? 0.7f : 0f };
            var raised = new List<ForecastAlert>();
            var env = Env(field, 0.0, Context(new AlertRules(3.0, PrecipBand.Heavy, 0f), geometry: false));
            var service = new ForecastService(env, null, raised.Add);
            service.Tick(0.016);
            ForecastSite site = Site();
            service.Watch(site, false);
            Await(service, site);
            double[] path = { 0.0, 2 * H, 13 * H, 14 * H, 20 * H };
            foreach (double ut in path)
            {
                env.Ut = ut;
                service.Tick(2.5);
                service.WaitIdle(5000);
            }
            Console.WriteLine("B12 service, warp 0 -> 13 h -> 20 h: " + string.Join("; ", raised.Select(a => a.Kind + " " + a.Ut / H)));
            Assert.AreEqual(1, raised.Count(a => a.Kind == AlertKind.Approaching));
            Assert.AreEqual(1, raised.Count(a => a.Kind == AlertKind.Arrived));
            Assert.AreEqual(1, raised.Count(a => a.Kind == AlertKind.Clearing));
            Assert.IsNotNull(service.LastAlertTick(site));

            raised.Clear();
            foreach (double ut in new[] { 2 * H, 8 * H })
            {
                env.Ut = ut;
                service.Tick(2.5);
                service.WaitIdle(5000);
                service.Tick(2.5);
            }
            Assert.AreEqual(1, raised.Count(a => a.Kind == AlertKind.Approaching), "A revert to 2 h puts the Approaching in the future again: "
                + string.Join("; ", raised.Select(a => a.Kind + " " + a.Ut / H)));
            Assert.AreEqual(0, raised.Count(a => a.Kind == AlertKind.Arrived), "The Arrived is still ahead at 8 h.");
            env.Ut = 11 * H;
            service.Tick(2.5);
            service.WaitIdle(5000);
            service.Tick(2.5);
            Assert.AreEqual(1, raised.Count(a => a.Kind == AlertKind.Arrived));
            Assert.AreEqual(1, raised.Count(a => a.Kind == AlertKind.Approaching), "Nothing else repeats.");
            Assert.AreEqual(2, raised.Count);
        }

        [TestMethod]
        public void AReportIssuedAfterNowIsNotServedAfterARevert()
        {
            var field = new MovingCellsField(3);
            var env = Env(field, 10.0 * 86400.0, Context(geometry: false));
            var service = new ForecastService(env);
            service.Tick(0.016);
            Assert.IsTrue(Await(service, Site()).Available);
            env.Ut = 2.0 * 86400.0;
            ForecastReport during = service.ForecastOf(Site());
            Assert.IsFalse(during.Available, "The report issued at day 10 is not the forecast of day 2.");
            service.WaitIdle(5000);
            ForecastReport after = service.ForecastOf(Site());
            Assert.IsTrue(after.Available);
            Assert.AreEqual(2.0 * 86400.0, after.IssuedUt);
        }

        [TestMethod]
        public void AVesselThatMovedOnIsServedTheReportOfThePlaceNearbyUntilItsOwnIsReady()
        {
            var field = new MovingCellsField(4);
            var env = Env(field, 86400.0, Context(geometry: false));
            var service = new ForecastService(env);
            service.Tick(0.016);
            ForecastReport first = Await(service, Site(50.0, 10.0));
            Assert.IsTrue(first.Available);
            ForecastReport moved = service.ForecastOf(Site(50.4, 10.3));
            Assert.IsTrue(moved.Available, "The vessel crossed into another tenth of a degree; the report nearby stands in.");
            Assert.AreSame(first, moved);
            ForecastReport far = service.ForecastOf(Site(52.5, 10.0));
            Assert.IsFalse(far.Available, "A place far away has no stand-in.");
            service.WaitIdle(5000);
        }

        [TestMethod]
        public void ANewThresholdRefreshesTheReportButNotTheSteps()
        {
            var field = new ScriptedField { Level = ut => ut >= 10 * H && ut < 16 * H ? 0.5f : 0f };
            ForecastContext moderate = new ForecastContext(new ForecastRules(3600.0, 24.0), 0.0, alerts: new AlertRules(3.0, PrecipBand.Moderate, 0f));
            ForecastContext heavy = new ForecastContext(new ForecastRules(3600.0, 24.0), 0.0, alerts: new AlertRules(3.0, PrecipBand.Heavy, 0f));
            var env = Env(field, 0.0, moderate);
            var service = new ForecastService(env);
            service.Tick(0.016);
            Assert.IsTrue(Await(service, Site()).Alerts.Length > 0, "Moderate snow is an alert at 10 h.");
            long generation = WeatherHooks.ForecastGeneration;
            long reusedBefore = service.Cache.ReusedSteps;
            env.Contexts = b => heavy;
            service.ForecastOf(Site());
            service.WaitIdle(5000);
            ForecastReport again = service.ForecastOf(Site());
            Assert.AreEqual(0, again.Alerts.Length, "Under the Heavy threshold the same weather is no alert.");
            Assert.AreEqual(generation, WeatherHooks.ForecastGeneration, "Thresholds are not part of the rules hash.");
            Assert.AreEqual(reusedBefore + 24, service.Cache.ReusedSteps, "The steps were reused, not computed again.");
        }

        [TestMethod]
        public void AFieldThatDoesNotCoverTheBodyGivesNoForecast()
        {
            var inner = new ScriptedField { Level = ut => 0.5f };
            var field = new RecordingField(inner) { Covers = false };
            var service = new ForecastService(Env(field, 0.0, Context(geometry: false)));
            service.Tick(0.016);
            Assert.IsFalse(Await(service, Site()).Available);
        }

        [TestMethod]
        public void TheModuleFollowsTheUsersAlertSitesThroughAutoWatch()
        {
            var field = new ScriptedField { Level = ut => ut >= 10 * H && ut < 16 * H ? 0.7f : 0f };
            var raised = new List<ForecastAlert>();
            var env = Env(field, 0.0, Context(new AlertRules(3.0, PrecipBand.Heavy, 0f), geometry: false));
            var service = new ForecastService(env, null, raised.Add);
            service.Tick(0.016);
            ForecastSite pad = Site(50.0, 10.0);
            service.SetAutoWatched(new[] { pad }, new[] { true });
            Await(service, pad);
            foreach (double ut in new[] { 0.0, 8 * H, 11 * H })
            {
                env.Ut = ut;
                service.Tick(2.5);
                service.WaitIdle(5000);
                service.Tick(2.5);
            }
            Assert.AreEqual(1, raised.Count(a => a.Kind == AlertKind.Approaching));
            Assert.AreEqual(1, raised.Count(a => a.Kind == AlertKind.Arrived));
            // Taken off the list, a site gets no more alerts, and one watched by hand stays.
            raised.Clear();
            service.SetAutoWatched(new ForecastSite[0], new bool[0]);
            env.Ut = 20 * H;
            service.Tick(2.5);
            service.WaitIdle(5000);
            service.Tick(2.5);
            Assert.AreEqual(0, raised.Count, "A site that is no longer watched is not evaluated.");
            service.Watch(pad, false);
            service.SetAutoWatched(new ForecastSite[0], new bool[0]);
            env.Ut = 30 * H;
            service.Tick(2.5);
            service.WaitIdle(5000);
            service.Tick(2.5);
            Assert.IsNotNull(service.LastAlertTick(pad));
        }

        [TestMethod]
        public void AFailedTrackSearchIsNotRetriedAtEveryCall()
        {
            var inner = new MovingCellsField(5);
            var field = new RecordingField(new HiddenSystemsField(inner));
            var env = Env(field, 86400.0);
            var lines = new List<string>();
            var service = new ForecastService(env, null, null, lines.Add);
            service.Tick(0.016);
            field.MutateWind = (ut, w) => { throw new InvalidOperationException("broken field"); };
            for (int i = 0; i < 50; i++)
            {
                service.BodyTracks(Body);
                service.WaitIdle(2000);
            }
            Assert.IsNotNull(service.LastFailure);
            Assert.IsTrue(field.Calls.Count < 5000, "The search was started again at every call: " + field.Calls.Count + " samples.");
        }

        [TestMethod]
        public void TheModuleIsFoundByTheRegistryWithAnOrderAfterTheRenderModules()
        {
            Type t = typeof(ForecastModule);
            var attribute = t.GetCustomAttribute<AtmospheresModuleAttribute>();
            Assert.IsNotNull(attribute);
            Assert.AreEqual(70, attribute.Order);
            Assert.IsTrue(ModuleRegistry.Discover(t.Assembly).Contains(t));
            Assert.IsTrue(typeof(IAtmospheresModule).IsAssignableFrom(t));
        }

        [TestMethod]
        public void AForecastingProviderOwnsTheWindInAutoAndForeignModeAndOursKeepsTheFields()
        {
            var field = new ScriptedField { Level = ut => 0.5f, WindEast = ut => 5f };
            ForecastSite site = Site();
            var provider = new Provider { Forecasts = true };
            ForecastReport auto = ForecastEngine.Compute(site, 0.0, 6.0, Context(sources: new IWeatherSource[] { provider }, geometry: false), field);
            Assert.AreEqual("Foreign(KWP)", auto.Wind.ToString());
            Assert.AreEqual(12f, auto.Steps[2].WindMs, 1e-5f);
            Assert.AreEqual(18f, auto.Steps[2].GustMs, 1e-5f);
            Assert.AreEqual(0.8f, auto.Steps[2].Confidence01, 1e-6f);

            ForecastReport ours = ForecastEngine.Compute(site, 0.0, 6.0, Context(sources: new IWeatherSource[] { provider }, mode: WindSourceMode.Ours, geometry: false), field);
            Assert.AreEqual("Ours", ours.Wind.ToString());
            Assert.AreEqual(5f, ours.Steps[2].WindMs, 1e-5f);

            var low = new Provider { Forecasts = true, PriorityValue = 0 };
            ForecastReport autoLow = ForecastEngine.Compute(site, 0.0, 6.0, Context(sources: new IWeatherSource[] { low }, geometry: false), field);
            Assert.AreEqual("Ours", autoLow.Wind.ToString(), "Auto: a provider must rank above the field.");
            ForecastReport foreignLow = ForecastEngine.Compute(site, 0.0, 6.0, Context(sources: new IWeatherSource[] { low }, mode: WindSourceMode.Foreign, geometry: false), field);
            Assert.AreEqual("Foreign(KWP)", foreignLow.Wind.ToString());

            ForecastReport off = ForecastEngine.Compute(site, 0.0, 6.0, Context(sources: new IWeatherSource[] { provider }, mode: WindSourceMode.Off, geometry: false), field);
            Assert.AreEqual(0f, off.Steps[2].WindMs);
            ForecastReport foreignNone = ForecastEngine.Compute(site, 0.0, 6.0, Context(mode: WindSourceMode.Foreign, geometry: false), field);
            Assert.AreEqual("Ours", foreignNone.Wind.ToString(), "Foreign with no provider falls back to ours, never to silence.");
            Assert.AreEqual(5f, foreignNone.Steps[0].WindMs, 1e-5f);
        }

        [TestMethod]
        public void AProviderThatOnlyAnswersNowIsCarriedForwardWithFallingConfidence()
        {
            var field = new ScriptedField { Level = ut => 0.5f, WindEast = ut => 5f };
            var provider = new Provider { Forecasts = false };
            ForecastReport r = ForecastEngine.Compute(Site(), 0.0, 24.0, Context(sources: new IWeatherSource[] { provider }, geometry: false), field);
            Assert.AreEqual("ExternalNowcast(KWP)", r.Wind.ToString());
            Assert.AreEqual(7f, r.Steps[0].WindMs, 1e-4f, "At lead 0 the provider's own wind.");
            Assert.AreEqual(1f, r.Steps[0].Confidence01, 1e-5f);
            Assert.IsTrue(r.Steps[3].Confidence01 < r.Steps[1].Confidence01 && r.Steps[1].Confidence01 < 1f);
            Assert.AreEqual(0f, r.Steps[6].Confidence01, 1e-5f);
            Assert.AreEqual(5f, r.Steps[12].WindMs, 1e-4f, "Past the persistence time the wind is ours again.");
            Assert.IsTrue(r.Steps[3].WindMs < 7f && r.Steps[3].WindMs > 5f);
            // A provider that is not thread safe keeps the computation on the main thread.
            var unsafeProvider = new Provider { SafeFlag = false };
            var job = new ChunkedCompute(Site(), 0.0, 24.0, Context(sources: new IWeatherSource[] { unsafeProvider }, geometry: false), field);
            Assert.IsFalse(job.ThreadSafe);
            var safeJob = new ChunkedCompute(Site(), 0.0, 24.0, Context(sources: new IWeatherSource[] { provider }, geometry: false), field);
            Assert.IsTrue(safeJob.ThreadSafe);
        }

        [TestMethod]
        public void AFailureInTheComputationIsContainedAndRetriedLater()
        {
            var field = new RecordingField(new ScriptedField { Level = ut => 0.2f });
            var env = Env(field, 0.0, Context(geometry: false));
            var lines = new List<string>();
            var service = new ForecastService(env, null, null, lines.Add);
            service.Tick(0.016);
            field.Mutate = (ut, level) => { throw new InvalidOperationException("broken field"); };
            service.ForecastOf(Site());
            service.WaitIdle(5000);
            Assert.IsNotNull(service.LastFailure);
            Assert.AreEqual(1, lines.Count);
            Assert.IsFalse(service.ForecastOf(Site()).Available);
        }
    }
}
