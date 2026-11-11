using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Atmospheres.Forecast;
using ReDefinition.Core;
using ReDefinition.Tests.Atmospheres.C25;

namespace ReDefinition.Tests.Atmospheres.B12
{
    // B12 on the REAL weather field (C25's GeneratorField over a stock-like home planet): the forecast is the live weather at later times
    // at any place and height, through the engine and through the service, the ETA matches a one-second scan, the tracks are continuous
    // and stay near the field's own systems, and a 30-day run gives the alerts of six sites. The altitude argument is metres above sea
    // level at the forecast and at the live callers (CONTRACT.md, "Altitude argument"). The game rows of K-W3a stay with D30 and B12Probe.
    [TestClass]
    public class RealFieldTests
    {
        private const double H = 3600.0;
        private const double EtaCoarse = ForecastEngine.EtaCoarseSeconds;
        private static int Body { get { return FieldFixture.BodyIndex; } }
        private static double Radius { get { return 600000.0; } }
        private static GeneratorField field;

        [ClassInitialize]
        public static void Init(TestContext context)
        {
            field = FieldFixture.Field(5);
        }

        private static ForecastSite Site(double lat, double lon, double altitudeAsl)
        {
            return ForecastModule.SiteAt(Body, lat, lon, altitudeAsl, "Real field site");
        }

        private static ForecastContext Context(bool geometry, AlertRules alerts = null, double horizonHours = 24.0)
        {
            return new ForecastContext(new ForecastRules(3600.0, horizonHours), geometry ? Radius : 0.0, alerts: alerts);
        }

        private static uint Bits(float value)
        {
            return (uint)BitConverter.ToInt32(BitConverter.GetBytes(value), 0);
        }

        // The steps of a report that differ from the field's own sample at the step time, with the live caller's altitude.
        private static int Mismatches(ForecastReport report, ForecastSite site, double liveAltitude)
        {
            int bad = 0;
            foreach (ForecastStep step in report.Steps)
            {
                WeatherSample live;
                field.Sample(Body, site.LatDeg, site.LonDeg, liveAltitude, step.Ut, out live);
                float east = live.WindWorld.x, north = live.WindWorld.y;
                float wind = (float)Math.Sqrt(east * east + north * north);
                float gust = ForecastSampling.StatisticalGust(wind, float.IsNaN(live.Level01) ? 0f : live.Level01);
                if (Bits(step.Level01) != Bits(live.Level01) || step.Type != live.Type || Bits(step.VisibilityM) != Bits(live.VisibilityM)
                    || Bits(step.WindMs) != Bits(wind) || Bits(step.GustMs) != Bits(gust) || step.Band != PrecipScale.BandOf(live.Level01, wind))
                {
                    bad++;
                }
            }
            return bad;
        }

        [TestMethod]
        public void TheRealFieldIsReadyAndThreadSafe()
        {
            Assert.AreEqual(FieldReadiness.Ready, field.Readiness);
            Assert.IsTrue(field.ThreadSafe);
        }

        [TestMethod]
        public void ForecastStepsEqualTheLiveSampleBitwiseOnTheRealFieldOver9600StepsAtAnyHeightAboveSeaLevel()
        {
            var rng = new Random(2026);
            int compared = 0, mismatches = 0, offset = 0, typeOffset = 0;
            for (int i = 0; i < 400; i++)
            {
                double altitudeAsl = i % 4 == 0 ? 70.0 : 8000.0 * rng.NextDouble();
                ForecastSite site = Site(-80.0 + 160.0 * rng.NextDouble(), -180.0 + 360.0 * rng.NextDouble(), altitudeAsl);
                double start = rng.NextDouble() * 200.0 * 86400.0;
                ForecastReport report = ForecastEngine.Compute(site, start, 24.0, new ForecastRules(3600.0, 24.0), field);
                Assert.IsTrue(report.Available);
                compared += report.Steps.Length;
                mismatches += Mismatches(report, site, altitudeAsl);
                // The convention pinned: a live caller that passed another altitude (the height above the ground read as sea level of a
                // place 1400 m up) would see another wind.
                for (int k = 0; k < report.Steps.Length; k++)
                {
                    WeatherSample same, other;
                    field.Sample(Body, site.LatDeg, site.LonDeg, altitudeAsl, report.Steps[k].Ut, out same);
                    field.Sample(Body, site.LatDeg, site.LonDeg, altitudeAsl + 1400.0, report.Steps[k].Ut, out other);
                    if (Bits(same.WindWorld.x) != Bits(other.WindWorld.x) || Bits(same.WindWorld.y) != Bits(other.WindWorld.y)) offset++;
                    if (same.Type != other.Type) typeOffset++;
                }
            }
            Console.WriteLine("B12 real field equality: " + compared + " steps of 400 random sites (heights 0 to 8000 m above sea level, UT within 200 days), "
                + mismatches + " differ from field.Sample at the same UT and height; a height offset of 1400 m changes the wind in " + offset + " and the type in "
                + typeOffset + " of those steps");
            Assert.AreEqual(9600, compared);
            Assert.AreEqual(0, mismatches);
            Assert.IsTrue(offset > compared / 2, "The altitude argument must matter for the test to pin the convention.");
        }

        [TestMethod]
        public void TheModuleSiteCarriesTheAltitudeTheLiveCallerPasses()
        {
            // The live callers pass body.GetAltitude(position) / vessel.altitude (above sea level); the module builds its site with the same
            // number, so the forecast samples what WeatherFrameDriver samples.
            var rng = new Random(5);
            int bad = 0;
            for (int i = 0; i < 200; i++)
            {
                double lat = -80.0 + 160.0 * rng.NextDouble(), lon = -180.0 + 360.0 * rng.NextDouble(), altitude = 12000.0 * rng.NextDouble();
                ForecastSite site = ForecastModule.SiteAt(Body, lat, lon, altitude, "Vessel");
                Assert.AreEqual(altitude, site.AltAgl);
                double ut = rng.NextDouble() * 50.0 * 86400.0;
                ForecastReport report = ForecastEngine.Compute(site, ut, 6.0, new ForecastRules(3600.0, 6.0), field);
                bad += Mismatches(report, site, altitude);
            }
            Console.WriteLine("B12 module site: 200 sites, " + bad + " steps differ from the live call with the same altitude");
            Assert.AreEqual(0, bad);
        }

        [TestMethod]
        public void TheServiceServesTheRealFieldsForecastFromAWorkerAndTheTracksFromItsSystems()
        {
            ForecastContext ctx = Context(true, new AlertRules(3.0, PrecipBand.Heavy, 0f));
            double now = 40.0 * 86400.0 + 1234.0;
            var env = new TestEnvironment { FieldValue = field, Ut = now, Contexts = b => ctx };
            var service = new ForecastService(env);
            service.Tick(0.016);
            Assert.IsTrue(service.Available);
            var rng = new Random(77);
            var sites = new List<ForecastSite>();
            for (int i = 0; i < 60; i++) sites.Add(Site(-70.0 + 140.0 * rng.NextDouble(), -180.0 + 360.0 * rng.NextDouble(), 7000.0 * rng.NextDouble()));
            foreach (ForecastSite s in sites) service.ForecastOf(s);
            Assert.IsTrue(service.WaitIdle(20000));
            int compared = 0, bad = 0;
            foreach (ForecastSite s in sites)
            {
                ForecastReport r = service.ForecastOf(s);
                Assert.IsTrue(r.Available, "No report for " + s.LatDeg + ", " + s.LonDeg);
                Assert.AreEqual(24, r.Steps.Length);
                compared += r.Steps.Length;
                bad += Mismatches(r, s, s.AltAgl);
            }
            ForecastTrack[] tracks = service.BodyTracks(Body);
            if (tracks == null || tracks.Length == 0)
            {
                service.WaitIdle(20000);
                tracks = service.BodyTracks(Body);
            }
            int points = tracks == null ? 0 : tracks.Sum(t => t.Ut.Length);
            Console.WriteLine("B12 real field through the service: " + sites.Count + " sites, " + compared + " steps, " + bad + " differ from the live sample; "
                + (tracks == null ? 0 : tracks.Length) + " body tracks with " + points + " points");
            Assert.AreEqual(1440, compared);
            Assert.AreEqual(0, bad);
            Assert.IsNotNull(tracks);
            Assert.IsTrue(tracks.Length > 0 && points > 0, "The field lists its systems, so the tracks exist.");
            Assert.IsTrue(service.ForecastOf(sites[0]).Steps.All(st => !float.IsNaN(st.Level01)));
        }

        [TestMethod]
        public void TheEtaOnTheRealFieldIsWithinOneSecondOfABruteForceScan()
        {
            const float threshold = PrecipScale.LightFrom;
            var rules = new AlertRules(minBand: PrecipBand.Light, windMs: 0f);
            ForecastContext ctx = Context(false, rules);
            var rng = new Random(314);
            int crossings = 0, active = 0, none = 0, worst = 0, cases = 0, shortDips = 0;
            double worstClear = 0.0;
            while (cases < 30)
            {
                double start = (10.0 + 150.0 * rng.NextDouble()) * 86400.0;
                ForecastSite site;
                if (rng.NextDouble() < 0.8)
                {
                    var list = new List<SystemPoint>();
                    field.TryGetSystems(Body, start + rng.NextDouble() * 24.0 * H, list);
                    if (list.Count == 0) continue;
                    SystemPoint p = list[rng.Next(list.Count)];
                    site = Site(Math.Max(-85.0, Math.Min(85.0, p.LatDeg + (rng.NextDouble() - 0.5) * 4.0)), p.LonDeg + (rng.NextDouble() - 0.5) * 4.0, 70.0);
                }
                else
                {
                    site = Site(-80.0 + 160.0 * rng.NextDouble(), -180.0 + 360.0 * rng.NextDouble(), 70.0);
                }
                cases++;
                ForecastResult result = ForecastEngine.ComputeDetailed(site, start, 24.0, ctx, field);
                double end = start + 24.0 * H;
                double brute = double.NaN, bruteClear = double.NaN, dip = double.NaN;
                for (double t = start; t <= end; t += 1.0)
                {
                    WeatherSample s;
                    field.Sample(Body, site.LatDeg, site.LonDeg, site.AltAgl, t, out s);
                    if (double.IsNaN(brute))
                    {
                        if (s.Level01 >= threshold) brute = t;
                    }
                    else if (double.IsNaN(bruteClear))
                    {
                        if (s.Level01 < threshold) bruteClear = t;
                    }
                    else
                    {
                        // The dip below the threshold ends: its length decides whether a 300-s step can see it.
                        if (s.Level01 >= threshold) { dip = t - bruteClear; break; }
                        if (t - bruteClear > EtaCoarse) break;
                    }
                }
                Assert.AreEqual(double.IsNaN(brute), double.IsNaN(result.ThresholdEtaUt), "A crossing was missed or invented at case " + cases + " (" + site.LatDeg + ", " + site.LonDeg + ")");
                if (double.IsNaN(brute)) { none++; continue; }
                crossings++;
                if (result.ActiveAtStart) active++;
                int error = (int)Math.Ceiling(Math.Abs(result.ThresholdEtaUt - brute));
                if (error > worst) worst = error;
                Assert.IsTrue(Math.Abs(result.ThresholdEtaUt - brute) <= 1.0, "ETA " + result.ThresholdEtaUt + " against brute force " + brute + " at case " + cases);
                if (!double.IsNaN(bruteClear) && !double.IsNaN(dip) && dip < EtaCoarse && Math.Abs(result.ClearUt - bruteClear) > 1.0)
                {
                    // A dip shorter than the 300-s coarse step lies between two samples of the search: the clearing it reports is a later,
                    // lasting one (or none inside the horizon).
                    shortDips++;
                    Assert.IsTrue(double.IsNaN(result.ClearUt) || result.ClearUt > bruteClear, "Clearing " + result.ClearUt + " before the dip at " + bruteClear);
                    continue;
                }
                Assert.AreEqual(double.IsNaN(bruteClear), double.IsNaN(result.ClearUt), "Clearing missed or invented at case " + cases);
                if (!double.IsNaN(bruteClear))
                {
                    worstClear = Math.Max(worstClear, Math.Abs(result.ClearUt - bruteClear));
                    Assert.IsTrue(Math.Abs(result.ClearUt - bruteClear) <= 1.0, "Clearing " + result.ClearUt + " against brute force " + bruteClear);
                }
            }
            Console.WriteLine("B12 real field ETA vs 1-s brute force: " + cases + " cases, " + crossings + " with a crossing (" + active + " already on at the start), "
                + none + " without; worst arrival error " + worst + " s, worst clearing error " + worstClear.ToString("F2") + " s; "
                + shortDips + " clearings sit behind a dip shorter than the 300-s coarse step (invisible to the search by construction)");
            Assert.IsTrue(crossings >= 10, "Too few cases with a crossing: " + crossings);
        }

        [TestMethod]
        public void TheCostOnTheRealFieldIsReported()
        {
            ForecastSite site = Site(30.0, 20.0, 70.0);
            var rules = new ForecastRules(3600.0, 24.0);
            ForecastContext withGeometry = Context(true);
            ForecastContext without = Context(false);
            for (int i = 0; i < 20; i++)
            {
                ForecastEngine.Compute(site, 1e6 + i * H, 24.0, rules, field);
                ForecastEngine.ComputeDetailed(site, 1e6 + i * H, 24.0, withGeometry, field);
            }
            double steps = Measure(() => ForecastEngine.Compute(site, 2e6, 24.0, rules, field));
            double local = Measure(() => ForecastEngine.ComputeDetailed(site, 2e6, 24.0, withGeometry, field));
            double tracks = Measure(() => ForecastEngine.ComputeBodyTracks(Body, 2e6, 24.0, withGeometry, field), 20);
            Console.WriteLine("B12 real field cost (warm, mean of runs): 24 steps of one site " + steps.ToString("F3") + " ms; the same with the local track search "
                + local.ToString("F3") + " ms; body tracks of 24 h " + tracks.ToString("F3") + " ms; the service path pays the cached body tracks, not the local search"
                + " [" + without.Rules.StepSeconds + " s steps]");
            Assert.IsTrue(steps < 20.0, "24 steps of one site took " + steps + " ms");
        }

        private static double Measure(Action action, int runs = 100)
        {
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < runs; i++) action();
            return sw.Elapsed.TotalMilliseconds / runs;
        }

        [TestMethod]
        public void ThirtyDaysOnTheRealFieldGiveTheAlertsOfTheHourlyStaysAndContinuousTracks()
        {
            var rules = new AlertRules(3.0, PrecipBand.Moderate, 0f);
            ForecastContext ctx = Context(false, rules);
            var rng = new Random(81);
            const int days = 30;
            const double span = days * 86400.0;
            const double origin = 20.0 * 86400.0;
            var sites = new List<ForecastSite>();
            for (int i = 0; i < 6; i++) sites.Add(Site(-60.0 + 24.0 * i + rng.NextDouble() * 10.0, -180.0 + 360.0 * rng.NextDouble(), 70.0));

            var perSiteWeek = new int[sites.Count, 5];
            var risesWeek = new int[sites.Count, 5];
            var totals = new int[sites.Count, 4];
            int anyStay = 0;
            for (int s = 0; s < sites.Count; s++)
            {
                var evaluator = new AlertEvaluator(rules);
                ForecastCacheEntry prior = null;
                ForecastReport report = null;
                var hit = new List<bool>();
                for (double t = 0.0; t <= span + 24.0 * H; t += H)
                {
                    WeatherSample sample;
                    field.Sample(Body, sites[s].LatDeg, sites[s].LonDeg, sites[s].AltAgl, origin + t, out sample);
                    float wind = (float)Math.Sqrt(sample.WindWorld.x * sample.WindWorld.x + sample.WindWorld.y * sample.WindWorld.y);
                    hit.Add((int)PrecipScale.BandOf(sample.Level01, wind) >= (int)PrecipBand.Moderate);
                }
                // Bounds from the hourly series: every rise is at most one stay; rises that follow at least two clear hours are surely new.
                int rises = 0, sure = 0, clearRun = 99;
                for (int i = 0; i < hit.Count && i * H <= span; i++)
                {
                    if (hit[i] && (i == 0 || !hit[i - 1]))
                    {
                        rises++;
                        risesWeek[s, Math.Min(4, (int)(i * H / (7.0 * 86400.0)))]++;
                        if (clearRun >= 2) sure++;
                    }
                    clearRun = hit[i] ? 0 : clearRun + 1;
                }
                anyStay += rises;
                for (double now = 0.0; now <= span; now += 1800.0)
                {
                    if (report == null || origin + now >= report.IssuedUt + H)
                    {
                        ForecastResult r = ForecastEngine.ComputeDetailed(sites[s], origin + now, 24.0, ctx, field, prior);
                        prior = r.Entry;
                        report = r.Report;
                    }
                    AlertTick tick = evaluator.Tick(sites[s], false, report, origin + now, Radius);
                    int week = Math.Min(4, (int)(now / (7.0 * 86400.0)));
                    foreach (ForecastAlert a in tick.Alerts)
                    {
                        perSiteWeek[s, week]++;
                        switch (a.Kind)
                        {
                            case AlertKind.Approaching: totals[s, 0]++; break;
                            case AlertKind.Arrived: totals[s, 1]++; break;
                            case AlertKind.Clearing: totals[s, 2]++; break;
                            case AlertKind.Worsening: totals[s, 3]++; break;
                        }
                    }
                }
                Console.WriteLine("B12 L1 real field site " + s + " (" + Math.Abs(sites[s].LatDeg).ToString("F1") + " deg " + (sites[s].LatDeg < 0 ? "S" : "N") + " latitude, "
                    + Math.Abs(sites[s].LonDeg).ToString("F1") + " deg " + (sites[s].LonDeg < 0 ? "W" : "E") + " longitude): hourly rises " + rises + " (after two clear hours "
                    + sure + "); Approaching " + totals[s, 0] + ", Arrived " + totals[s, 1] + ", Clearing " + totals[s, 2] + ", Worsening " + totals[s, 3]
                    + "; alerts per week " + string.Join("/", Enumerable.Range(0, 5).Select(w => perSiteWeek[s, w].ToString())));
                Assert.IsTrue(totals[s, 1] >= sure - 1 && totals[s, 1] <= rises + 1, "Arrived " + totals[s, 1] + " outside " + sure + " to " + rises);
                Assert.IsTrue(Math.Abs(totals[s, 0] - totals[s, 1]) <= 2, "Approaching and Arrived stay together");
                Assert.IsTrue(Math.Abs(totals[s, 2] - totals[s, 1]) <= 2, "Clearing follows Arrived");
            }
            // The alerts of a week are bounded by the stays of that week: at most five per stay (Approaching, Arrived, Clearing and the
            // Worsening steps), plus one stay of slack for an Approaching that precedes a week or a Clearing that follows it.
            int maxWeek = 0, worstExcess = int.MinValue;
            for (int s = 0; s < sites.Count; s++)
            {
                for (int w = 0; w < 5; w++)
                {
                    maxWeek = Math.Max(maxWeek, perSiteWeek[s, w]);
                    worstExcess = Math.Max(worstExcess, perSiteWeek[s, w] - 5 * (risesWeek[s, w] + 1));
                }
            }
            Console.WriteLine("B12 L1 real field: most alerts at one site in one week " + maxWeek + " (bound: five per hourly rise of that week plus five), hourly rises over all sites "
                + anyStay + ", worst alerts minus bound " + worstExcess);
            Assert.IsTrue(worstExcess <= 0, "A week has more alerts than its stays allow: " + worstExcess);
            Assert.IsTrue(anyStay > 0, "The 30 days gave no stay at all; the run proves nothing.");

            // Tracks: continuous, within the speed bound, and near the field's own systems.
            ForecastContext tctx = Context(true, null, 12.0);
            double spacing = Radius * GridScan.GlobalDegrees * Math.PI / 180.0;
            int trackCount = 0, samples = 0, near = 0, jumps = 0, segments = 0;
            for (int set = 0; set < 20; set++)
            {
                double t0 = origin + set * (span / 20.0);
                ForecastTrack[] tracks = ForecastEngine.ComputeBodyTracks(Body, t0, 12.0, tctx, field);
                trackCount += tracks.Length;
                foreach (ForecastTrack t in tracks)
                {
                    for (int i = 0; i < t.Ut.Length; i++)
                    {
                        samples++;
                        var list = new List<SystemPoint>();
                        field.TryGetSystems(Body, t.Ut[i], list);
                        if (list.Any(p => ForecastMath.DistanceM(p.LatDeg, p.LonDeg, t.Lat[i], t.Lon[i], Radius) <= spacing + p.RadiusM)) near++;
                        if (i == 0) continue;
                        segments++;
                        double dt = t.Ut[i] - t.Ut[i - 1];
                        double speed = ForecastMath.DistanceM(t.Lat[i - 1], t.Lon[i - 1], t.Lat[i], t.Lon[i], Radius) / dt;
                        if (dt <= 0.0 || speed > tctx.MaxSystemSpeedMs * 1.05) jumps++;
                    }
                }
            }
            Console.WriteLine("B12 L1 real field tracks (20 sets of 12 h): " + trackCount + " tracks, " + samples + " samples, " + near + " near a system of the field, "
                + jumps + " of " + segments + " segments faster than the speed bound");
            Assert.IsTrue(samples > 0, "No track in any set.");
            Assert.IsTrue(near >= 0.9 * samples, "Samples near a system: " + near + " of " + samples);
            Assert.AreEqual(0, jumps);
        }
    }
}
