using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Atmospheres.Forecast;
using ReDefinition.Core;

namespace ReDefinition.Tests.Atmospheres.B12
{
    // B12: the cost of the forecast on the test field (the numbers go to the console for the report) and the offline run over 30 days of
    // the test field: the alerts of six sites against the stays of their hourly series, per week, and the continuity of the tracks.
    [TestClass]
    public class ForecastBudgetTests
    {
        private const double H = 3600.0;
        private const int Body = 1;

        private static ForecastSite Site(double lat, double lon)
        {
            return new ForecastSite { BodyIndex = Body, LatDeg = lat, LonDeg = lon, Name = "Site" };
        }

        private static ForecastContext Context(AlertRules alerts = null)
        {
            return new ForecastContext(new ForecastRules(3600.0, 24.0), MovingCellsField.RadiusM, alerts: alerts);
        }

        private static double Median(List<double> values)
        {
            values.Sort();
            return values[values.Count / 2];
        }

        [TestMethod]
        public void TheCostOfAForecastOnTheTestFieldIsWithinTheBudget()
        {
            var field = new MovingCellsField(61, 12);
            ForecastContext ctx = Context(new AlertRules(minBand: PrecipBand.Moderate));
            var rng = new Random(5);
            var stepsOnly = new List<double>();
            var full = new List<double>();
            var fullWithCachedTracks = new List<double>();
            var tracks = new List<double>();
            var hiddenTracks = new List<double>();
            var hidden = new HiddenSystemsField(field);
            double tick = 1000.0 / Stopwatch.Frequency;
            for (int i = 0; i < 230; i++)
            {
                ForecastSite site = Site(-70.0 + 140.0 * rng.NextDouble(), -180.0 + 360.0 * rng.NextDouble());
                double start = rng.NextDouble() * 20.0 * 86400.0;
                var sw = Stopwatch.StartNew();
                var job = new ChunkedCompute(site, start, 24.0, ctx, field);
                job.Advance(24);
                long afterSteps = sw.ElapsedTicks;
                job.Advance(int.MaxValue);
                long total = sw.ElapsedTicks;
                sw.Restart();
                ForecastTrack[] body = ForecastEngine.ComputeBodyTracks(Body, start, 24.0, ctx, field);
                long bodyTicks = sw.ElapsedTicks;
                sw.Restart();
                new ChunkedCompute(site, start, 24.0, ctx, field, null, body).Advance(int.MaxValue);
                long cached = sw.ElapsedTicks;
                if (i < 30) continue;
                stepsOnly.Add(afterSteps * tick);
                full.Add(total * tick);
                tracks.Add(bodyTicks * tick);
                fullWithCachedTracks.Add(cached * tick);
            }
            for (int i = 0; i < 3; i++)
            {
                var sw = Stopwatch.StartNew();
                ForecastEngine.ComputeBodyTracks(Body, i * 3.0 * 86400.0, 24.0, ctx, hidden);
                hiddenTracks.Add(sw.ElapsedTicks * tick);
            }
            double mSteps = Median(stepsOnly), mFull = Median(full), mCached = Median(fullWithCachedTracks), mTracks = Median(tracks), mHidden = Median(hiddenTracks);
            Console.WriteLine("B12 cost on the test field (median of 200 runs, 12 cells, " + Environment.ProcessorCount + " logical processors):");
            Console.WriteLine("  24 hourly steps, one site: " + (mSteps * 1000.0).ToString("F1") + " us (budget 100 us)");
            Console.WriteLine("  Compute 24 h, one site, with ETA, clearing, nearest system, tracks and alerts (systems listed by the field): " + mFull.ToString("F3") + " ms (budget 5 ms)");
            Console.WriteLine("  the same with the body tracks cached: " + mCached.ToString("F3") + " ms");
            Console.WriteLine("  body tracks, systems listed: " + mTracks.ToString("F3") + " ms (budget 3 ms)");
            Console.WriteLine("  body tracks, found by the global search of the field (36 x 72 samples x 49 times): " + mHidden.ToString("F1") + " ms (median of 3)");
            Assert.IsTrue(mSteps < 0.1, "24 steps: " + mSteps + " ms");
            Assert.IsTrue(mFull < 5.0, "Compute: " + mFull + " ms");
            Assert.IsTrue(mTracks < 3.0, "Body tracks: " + mTracks + " ms");
        }

        [TestMethod]
        public void TwentySitesOnOneWorkerAreDoneWellInsideTheLatencyBudgetAndTheMainThreadPaysAlmostNothing()
        {
            var field = new MovingCellsField(62, 12);
            ForecastContext ctx = Context();
            var env = new TestEnvironment { FieldValue = field, Ut = 5.0 * 86400.0, Contexts = b => ctx };
            var service = new ForecastService(env);
            service.Tick(0.016);
            var sites = new List<ForecastSite>();
            for (int i = 0; i < 20; i++) sites.Add(Site(-60.0 + 6.0 * i, -170.0 + 17.0 * i));
            var sw = Stopwatch.StartNew();
            foreach (ForecastSite s in sites) service.ForecastOf(s);
            long requested = sw.ElapsedTicks;
            Assert.IsTrue(service.WaitIdle(10000));
            double latency = sw.Elapsed.TotalMilliseconds;
            Assert.IsTrue(sites.All(s => service.ForecastOf(s).Available));
            Console.WriteLine("B12 latency: 20 sites, 24 h each, one worker: " + latency.ToString("F1") + " ms until all are served (budget 1500 ms); queueing them took "
                + (requested * 1000.0 / Stopwatch.Frequency).ToString("F3") + " ms on the calling thread");
            Assert.IsTrue(latency < 1500.0);

            var frame = new List<double>();
            for (int i = 0; i < 3000; i++)
            {
                var t = Stopwatch.StartNew();
                service.Tick(0.016);
                foreach (ForecastSite s in sites) service.ForecastOf(s);
                frame.Add(t.ElapsedTicks * 1000.0 / Stopwatch.Frequency);
            }
            double perFrame = Median(frame);
            double perCall = perFrame / (sites.Count + 1);
            Console.WriteLine("B12 main thread: one Tick and 20 cached ForecastOf per frame, median " + (perFrame * 1000.0).ToString("F1") + " us per frame, "
                + (perCall * 1000.0).ToString("F2") + " us per call (budget 100 us per frame for the seam)");
            Assert.IsTrue(perFrame < 0.1, "Per frame: " + perFrame + " ms");
        }

        [TestMethod]
        public void ThirtyDaysOnTheTestFieldGiveTheAlertsOfTheHourlyStaysAndContinuousTracks()
        {
            var field = new MovingCellsField(71, 14);
            var rules = new AlertRules(3.0, PrecipBand.Moderate, 0f);
            ForecastContext ctx = Context(rules);
            var rng = new Random(8);
            const int days = 30;
            const double span = days * 86400.0;
            var sites = new List<ForecastSite>();
            for (int i = 0; i < 6; i++) sites.Add(Site(-60.0 + 24.0 * i + rng.NextDouble() * 10.0, -180.0 + 360.0 * rng.NextDouble()));

            var perSiteWeek = new int[sites.Count, 5];
            var totals = new int[sites.Count, 4];
            var expectedStays = new int[sites.Count];
            for (int s = 0; s < sites.Count; s++)
            {
                var evaluator = new AlertEvaluator(rules);
                ForecastCacheEntry prior = null;
                ForecastReport report = null;
                // The truth: the hourly series and its stays with the clear-hour rule.
                var hit = new List<bool>();
                for (double t = 0.0; t <= span + 24.0 * H; t += H)
                {
                    WeatherSample sample;
                    field.Sample(Body, sites[s].LatDeg, sites[s].LonDeg, 0.0, t, out sample);
                    float wind = (float)Math.Sqrt(sample.WindWorld.x * sample.WindWorld.x + sample.WindWorld.y * sample.WindWorld.y);
                    hit.Add((int)PrecipScale.BandOf(sample.Level01, wind) >= (int)PrecipBand.Moderate);
                }
                int stays = 0;
                for (int i = 1; i < hit.Count && i * H <= span; i++)
                {
                    if (hit[i] && !hit[i - 1]) stays++;
                }
                expectedStays[s] = stays;
                for (double now = 0.0; now <= span; now += 1800.0)
                {
                    if (report == null || now >= report.IssuedUt + H)
                    {
                        ForecastResult r = ForecastEngine.ComputeDetailed(sites[s], now, 24.0, ctx, field, prior);
                        prior = r.Entry;
                        report = r.Report;
                    }
                    AlertTick tick = evaluator.Tick(sites[s], false, report, now, MovingCellsField.RadiusM);
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
            }
            int maxWeek = 0;
            for (int s = 0; s < sites.Count; s++)
            {
                for (int w = 0; w < 5; w++) maxWeek = Math.Max(maxWeek, perSiteWeek[s, w]);
                Console.WriteLine("B12 L1 site " + s + " (" + Math.Abs(sites[s].LatDeg).ToString("F1") + " deg " + (sites[s].LatDeg < 0 ? "S" : "N") + " latitude, " + Math.Abs(sites[s].LonDeg).ToString("F1")
                    + " deg " + (sites[s].LonDeg < 0 ? "W" : "E") + " longitude): hourly stays " + expectedStays[s]
                    + "; Approaching " + totals[s, 0] + ", Arrived " + totals[s, 1] + ", Clearing " + totals[s, 2] + ", Worsening " + totals[s, 3] + "; alerts per week "
                    + string.Join("/", Enumerable.Range(0, 5).Select(w => perSiteWeek[s, w].ToString())));
                Assert.IsTrue(Math.Abs(totals[s, 1] - expectedStays[s]) <= 1, "Arrived " + totals[s, 1] + " against " + expectedStays[s] + " stays");
                Assert.IsTrue(Math.Abs(totals[s, 0] - totals[s, 1]) <= 1, "Approaching and Arrived stay together");
                Assert.IsTrue(Math.Abs(totals[s, 2] - totals[s, 1]) <= 1, "Clearing follows Arrived");
            }
            Console.WriteLine("B12 L1 most alerts at one site in one week: " + maxWeek + " (bound 40)");
            Assert.IsTrue(maxWeek <= 40);

            // Track continuity of the generic search: 20 sets of 12 hours over the 30 days.
            var hidden = new HiddenSystemsField(field);
            double spacing = MovingCellsField.RadiusM * GridScan.GlobalDegrees * Math.PI / 180.0;
            int visible = 0, found = 0, links = 0, breaks = 0, samples = 0, real = 0;
            for (int set = 0; set < 20; set++)
            {
                double t0 = set * (span / 20.0);
                ForecastContext tctx = new ForecastContext(new ForecastRules(3600.0, 12.0), MovingCellsField.RadiusM);
                ForecastTrack[] tracks = ForecastEngine.ComputeBodyTracks(Body, t0, 12.0, tctx, hidden);
                long bucket = ForecastCache.BucketOf(t0, tctx.TrackStepSeconds);
                var previousTrack = new Dictionary<ulong, int>();
                for (int j = 0; j <= 24; j++)
                {
                    double ut = (bucket + j) * tctx.TrackStepSeconds;
                    var truth = new List<SystemPoint>();
                    field.Truth(ut, truth);
                    var current = new Dictionary<ulong, int>();
                    foreach (SystemPoint p in truth)
                    {
                        if (p.Strength01 < 0.3f || p.RadiusM < 0.5 * spacing || Math.Abs(p.LatDeg) > 80.0) continue;
                        visible++;
                        int best = -1;
                        double bestDistance = spacing;
                        for (int t = 0; t < tracks.Length; t++)
                        {
                            double lat, lon;
                            float r, st;
                            if (!SystemTracker.PositionAt(tracks[t], ut, out lat, out lon, out r, out st)) continue;
                            double d = ForecastMath.DistanceM(lat, lon, p.LatDeg, p.LonDeg, MovingCellsField.RadiusM);
                            if (d <= bestDistance)
                            {
                                bestDistance = d;
                                best = t;
                            }
                        }
                        if (best < 0) continue;
                        found++;
                        current[p.Key] = best;
                        int before;
                        if (previousTrack.TryGetValue(p.Key, out before))
                        {
                            links++;
                            if (before != best) breaks++;
                        }
                    }
                    previousTrack = current;
                }
                // Every sample of a track is near a real system (nothing invented).
                var truthAt = new Dictionary<int, List<SystemPoint>>();
                foreach (ForecastTrack t in tracks)
                {
                    for (int i = 0; i < t.Ut.Length; i++)
                    {
                        samples++;
                        List<SystemPoint> list;
                        int key = (int)Math.Round(t.Ut[i] / 1800.0);
                        if (!truthAt.TryGetValue(key, out list))
                        {
                            list = new List<SystemPoint>();
                            field.Truth(t.Ut[i], list);
                            truthAt[key] = list;
                        }
                        if (list.Any(p => ForecastMath.DistanceM(p.LatDeg, p.LonDeg, t.Lat[i], t.Lon[i], MovingCellsField.RadiusM) <= spacing + p.RadiusM)) real++;
                    }
                }
            }
            Console.WriteLine("B12 L1 tracks (generic search, 20 sets of 12 h): " + found + " of " + visible + " visible system samples found within one grid cell, " + breaks + " track changes in "
                + links + " links of one system, " + real + " of " + samples + " track samples near a real system");
            Assert.IsTrue(found >= 0.9 * visible);
            Assert.IsTrue(breaks <= 0.05 * links, "Track changes: " + breaks + " of " + links);
            Assert.IsTrue(real >= 0.9 * samples);
        }
    }
}
