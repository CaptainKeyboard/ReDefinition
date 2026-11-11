using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Atmospheres.Forecast;
using ReDefinition.Core;

namespace ReDefinition.Tests.Atmospheres.B12
{
    // B12: the forecast engine against the IWeatherField contract, on a deterministic field of moving cells. The equality of a step with
    // the live sample, the shape of the report, the ETA against a brute-force scan, the tracks, the rolling slide, the display-only
    // uncertainty and the rule that the compute path touches no engine type.
    [TestClass]
    public class ForecastEngineTests
    {
        private const int Body = 1;

        private static ForecastSite Site(double lat, double lon, double altAgl = 0.0)
        {
            return new ForecastSite { BodyIndex = Body, LatDeg = lat, LonDeg = lon, AltAgl = altAgl, Name = "Test site" };
        }

        private static ForecastRules Rules(double stepSeconds = 3600.0, double horizonHours = 24.0)
        {
            return new ForecastRules(stepSeconds, horizonHours);
        }

        private static ForecastContext Context(double stepSeconds = 3600.0, double horizonHours = 24.0, bool geometry = true, float uncertainty = 0f,
            AlertRules alerts = null)
        {
            return new ForecastContext(Rules(stepSeconds, horizonHours), geometry ? MovingCellsField.RadiusM : 0.0, uncertainty01: uncertainty, alerts: alerts);
        }

        private static ulong Bits(float value)
        {
            return (ulong)BitConverter.ToInt32(BitConverter.GetBytes(value), 0) & 0xFFFFFFFFUL;
        }

        // The number of steps of forecasts of random sites and times that differ from the field's own sample at the step's UT.
        private static int CountMismatches(IWeatherField forecastField, IWeatherField liveField, int reports, out int compared)
        {
            var rng = new Random(2024);
            compared = 0;
            int mismatches = 0;
            for (int i = 0; i < reports; i++)
            {
                ForecastSite site = Site(-80.0 + 160.0 * rng.NextDouble(), -180.0 + 360.0 * rng.NextDouble(), 100.0 * rng.Next(0, 4));
                double start = rng.NextDouble() * 20.0 * 86400.0;
                ForecastReport report = ForecastEngine.Compute(site, start, 24.0, Rules(), forecastField);
                for (int k = 0; k < report.Steps.Length; k++)
                {
                    ForecastStep step = report.Steps[k];
                    WeatherSample live;
                    liveField.Sample(Body, site.LatDeg, site.LonDeg, site.AltAgl, step.Ut, out live);
                    compared++;
                    if (Bits(step.Level01) != Bits(live.Level01) || step.Type != live.Type || Bits(step.VisibilityM) != Bits(live.VisibilityM)) mismatches++;
                }
            }
            return mismatches;
        }

        [TestMethod]
        public void ForecastStepsEqualTheLiveSampleBitwiseOverTenThousandRandomSitesAndTimes()
        {
            var field = new MovingCellsField(7);
            int compared;
            int mismatches = CountMismatches(field, field, 420, out compared);
            Console.WriteLine("B12 equality: " + compared + " steps compared with field.Sample at the same UT, " + mismatches + " differ");
            Assert.IsTrue(compared >= 10000, "Fewer than 10 000 steps compared: " + compared);
            Assert.AreEqual(0, mismatches);
        }

        [TestMethod]
        public void TheEqualityTestSeesAForecastThatCallsADifferentFunction()
        {
            var live = new MovingCellsField(7);
            var other = new RecordingField(live) { Mutate = (ut, level) => level + 1e-6f * (float)Math.Sin(ut) };
            int compared;
            int mismatches = CountMismatches(other, live, 20, out compared);
            Console.WriteLine("B12 negative control: " + mismatches + " of " + compared + " steps differ");
            Assert.IsTrue(mismatches > compared / 2, "The test did not notice a changed function.");
        }

        [TestMethod]
        public void TheForecastReadsTheFieldOnlyThroughSampleAtTheStepTimesOfTheSite()
        {
            var inner = new MovingCellsField(3);
            var field = new RecordingField(inner);
            ForecastSite site = Site(48.5, -20.25, 300.0);
            double start = 5.5 * 86400.0 + 1234.0;
            ForecastResult result = ForecastEngine.ComputeDetailed(site, start, 24.0, Context(), field);
            Assert.AreEqual(0, field.MapCalls);
            Assert.AreEqual(0, field.WindowCalls);
            foreach (ForecastStep step in result.Report.Steps)
            {
                bool found = field.Calls.Any(c => c[0] == Body && c[1] == site.LatDeg && c[2] == site.LonDeg && c[3] == site.AltAgl && c[4] == step.Ut);
                Assert.IsTrue(found, "No Sample call at the step time " + step.Ut);
            }
        }

        [TestMethod]
        public void TheReportHasTheShapeOfThePlan()
        {
            var field = new MovingCellsField(5);
            double start = 3.0 * 86400.0 + 1800.0;
            ForecastReport r = ForecastEngine.Compute(Site(60.0, 10.0), start, 24.0, Rules(), field);
            Assert.AreEqual(24, r.Steps.Length);
            Assert.AreEqual(3600.0, r.StepSeconds);
            Assert.AreEqual(start, r.IssuedUt);
            Assert.IsTrue(r.Available);
            double first = Math.Floor(start / 3600.0) * 3600.0;
            for (int k = 0; k < 24; k++)
            {
                ForecastStep s = r.Steps[k];
                Assert.AreEqual(first + k * 3600.0, s.Ut);
                Assert.AreEqual(PrecipScale.BandOf(s.Level01, s.WindMs), s.Band);
                Assert.AreEqual(s.Level01, s.Intensity01);
                Assert.AreEqual(1f, s.Confidence01);
                Assert.AreEqual(s.WindMs * (1f + WindNoise.Depth(s.Level01)), s.GustMs, 1e-4f, "The gust is the statistical gust, mean x (1 + depth).");
            }
            ForecastReport half = ForecastEngine.Compute(Site(60.0, 10.0), start, 6.0, Rules(1800.0, 6.0), field);
            Assert.AreEqual(12, half.Steps.Length);
            Assert.AreEqual(1800.0, half.Steps[1].Ut - half.Steps[0].Ut);
        }

        [TestMethod]
        public void TheWindIsReportedAsMeanSpeedAndTheDirectionItComesFrom()
        {
            foreach (var c in new[] { new { E = 5f, N = 0f, From = 270f }, new { E = -5f, N = 0f, From = 90f }, new { E = 0f, N = 5f, From = 180f } })
            {
                Assert.AreEqual(c.From, ForecastSampling.FromDegrees(c.E, c.N), 1e-3f);
            }
            var field = new ScriptedField { WindEast = ut => 5f, Level = ut => 0.5f };
            ForecastStep s = ForecastEngine.Compute(Site(10.0, 10.0), 0.0, 3.0, Rules(), field).Steps[0];
            Assert.AreEqual(5f, s.WindMs, 1e-5f);
            Assert.AreEqual(270f, s.WindFromDeg, 1e-3f);
        }

        [TestMethod]
        public void TheEtaOfTheFirstCrossingIsWithinOneSecondOfABruteForceScanOn200Cases()
        {
            var field = new MovingCellsField(11, 8);
            const float threshold = PrecipScale.LightFrom;
            var rules = new AlertRules(minBand: PrecipBand.Light, windMs: 0f);
            var rng = new Random(99);
            int crossings = 0, active = 0, none = 0, worst = 0;
            double worstClear = 0.0;
            for (int i = 0; i < 200; i++)
            {
                double start = rng.NextDouble() * 10.0 * 86400.0;
                ForecastSite site;
                if (rng.NextDouble() < 0.75)
                {
                    var list = new List<SystemPoint>();
                    field.TryGetSystems(Body, start + rng.NextDouble() * 24.0 * 3600.0, list);
                    if (list.Count == 0) { i--; continue; }
                    SystemPoint p = list[rng.Next(list.Count)];
                    site = Site(Math.Max(-85.0, Math.Min(85.0, p.LatDeg + (rng.NextDouble() - 0.5) * 0.8)), p.LonDeg + (rng.NextDouble() - 0.5) * 0.8);
                }
                else
                {
                    site = Site(-80.0 + 160.0 * rng.NextDouble(), -180.0 + 360.0 * rng.NextDouble());
                }
                ForecastResult result = ForecastEngine.ComputeDetailed(site, start, 24.0, Context(alerts: rules), field);

                double end = start + 24.0 * 3600.0;
                double brute = double.NaN, bruteClear = double.NaN;
                for (double t = start; t <= end; t += 1.0)
                {
                    WeatherSample s;
                    field.Sample(Body, site.LatDeg, site.LonDeg, site.AltAgl, t, out s);
                    if (double.IsNaN(brute))
                    {
                        if (s.Level01 >= threshold) brute = t;
                    }
                    else if (s.Level01 < threshold)
                    {
                        bruteClear = t;
                        break;
                    }
                }
                Assert.AreEqual(double.IsNaN(brute), double.IsNaN(result.ThresholdEtaUt), "A crossing was missed or invented at case " + i);
                if (double.IsNaN(brute)) { none++; continue; }
                crossings++;
                if (result.ActiveAtStart) active++;
                int error = (int)Math.Ceiling(Math.Abs(result.ThresholdEtaUt - brute));
                if (error > worst) worst = error;
                Assert.IsTrue(Math.Abs(result.ThresholdEtaUt - brute) <= 1.0, "ETA " + result.ThresholdEtaUt + " against brute force " + brute + " at case " + i);
                Assert.AreEqual(double.IsNaN(bruteClear), double.IsNaN(result.ClearUt), "Clearing missed or invented at case " + i);
                if (!double.IsNaN(bruteClear))
                {
                    worstClear = Math.Max(worstClear, Math.Abs(result.ClearUt - bruteClear));
                    Assert.IsTrue(Math.Abs(result.ClearUt - bruteClear) <= 1.0, "Clearing " + result.ClearUt + " against brute force " + bruteClear);
                }
            }
            Console.WriteLine("B12 ETA vs 1-s brute force: 200 cases, " + crossings + " with a crossing (" + active + " already on at the start), " + none
                + " without; worst arrival error " + worst + " s, worst clearing error " + worstClear.ToString("F2") + " s");
            Assert.IsTrue(crossings >= 100, "Too few cases with a crossing: " + crossings);
        }

        [TestMethod]
        public void ARollingSlideReusesTheOverlappingStepsAndGivesTheSameNumbersAsAFreshComputation()
        {
            var field = new MovingCellsField(21);
            ForecastContext ctx = Context();
            ForecastSite site = Site(52.0, 33.0);
            double t0 = 7.0 * 86400.0 + 100.0;
            ForecastResult first = ForecastEngine.ComputeDetailed(site, t0, 24.0, ctx, field);
            Assert.AreEqual(0, first.ReusedSteps);
            Assert.AreEqual(24, first.ComputedSteps);
            foreach (int slide in new[] { 1, 2, 6 })
            {
                ForecastResult slid = ForecastEngine.ComputeDetailed(site, t0 + slide * 3600.0, 24.0, ctx, field, first.Entry);
                ForecastResult fresh = ForecastEngine.ComputeDetailed(site, t0 + slide * 3600.0, 24.0, ctx, field);
                double share = slid.ReusedSteps / 24.0;
                Console.WriteLine("B12 slide by " + slide + " step(s): reused " + slid.ReusedSteps + " of 24 (" + (share * 100.0).ToString("F1") + " %), computed "
                    + slid.ComputedSteps);
                Assert.AreEqual(24 - slide, slid.ReusedSteps);
                Assert.AreEqual(slide, slid.ComputedSteps);
                for (int k = 0; k < 24; k++)
                {
                    Assert.AreEqual(Bits(fresh.Report.Steps[k].Level01), Bits(slid.Report.Steps[k].Level01), "Level at step " + k + " after a slide of " + slide);
                    Assert.AreEqual(fresh.Report.Steps[k].Ut, slid.Report.Steps[k].Ut);
                    Assert.AreEqual(fresh.Report.Steps[k].WindMs, slid.Report.Steps[k].WindMs);
                    Assert.AreEqual(fresh.Report.Steps[k].CellCount, slid.Report.Steps[k].CellCount);
                }
                Assert.AreEqual(fresh.ThresholdEtaUt, slid.ThresholdEtaUt);
            }
            Assert.IsTrue(first.Entry != null);
            ForecastResult far = ForecastEngine.ComputeDetailed(site, t0 + 40 * 3600.0, 24.0, ctx, field, first.Entry);
            Assert.AreEqual(0, far.ReusedSteps, "Steps that do not overlap are not reused.");
        }

        [TestMethod]
        public void TheCacheKeyIsTheSiteTenthDegreeTheRulesAndTheDefinitionIdAndNeverTheVersion()
        {
            var field = new MovingCellsField(5, 12, true, true);
            ForecastContext ctx = Context();
            ForecastCacheKey a = ForecastCacheKey.Of(Site(50.01, 20.04), ctx, field.DefinitionId, 24);
            WeatherSample s;
            for (int i = 0; i < 100; i++) field.Sample(Body, 0.0, 0.0, 0.0, i, out s);
            Assert.IsTrue(field.Version >= 100, "The test field must bump its Version at every call.");
            ForecastCacheKey b = ForecastCacheKey.Of(Site(50.04, 20.01), ctx, field.DefinitionId, 24);
            Assert.AreEqual(a, b, "The same tenth of a degree and the same definition is the same key whatever the Version.");
            Assert.AreNotEqual(a, ForecastCacheKey.Of(Site(50.2, 20.0), ctx, field.DefinitionId, 24));
            Assert.AreNotEqual(a, ForecastCacheKey.Of(Site(50.01, 20.04), ctx, field.DefinitionId + 1, 24));
            Assert.AreNotEqual(a, ForecastCacheKey.Of(Site(50.01, 20.04), Context(1800.0), field.DefinitionId, 24));
            Assert.AreEqual(ForecastCacheKey.Of(Site(0.0, 180.0), ctx, 1, 24), ForecastCacheKey.Of(Site(0.0, -180.0), ctx, 1, 24));
        }

        [TestMethod]
        public void TheTracksFollowTheSystemsTheFieldListsAndKeepTheirKeys()
        {
            var field = new MovingCellsField(13);
            ForecastContext ctx = Context();
            double t0 = 4.0 * 86400.0 + 600.0;
            ForecastTrack[] tracks = ForecastEngine.ComputeBodyTracks(Body, t0, 24.0, ctx, field);
            Assert.IsTrue(tracks.Length > 0);
            foreach (ForecastTrack t in tracks)
            {
                Assert.AreNotEqual(0UL, t.CellKey);
                for (int i = 1; i < t.Ut.Length; i++) Assert.AreEqual(1800.0, t.Ut[i] - t.Ut[i - 1], "Samples are 30 game minutes apart.");
            }
            // A second set, one bucket later, carries the same keys for the same systems.
            ForecastTrack[] later = ForecastEngine.ComputeBodyTracks(Body, t0 + 1800.0, 24.0, ctx, field, tracks);
            int shared = 0;
            foreach (ForecastTrack t in later)
            {
                if (tracks.Any(e => e.CellKey == t.CellKey)) shared++;
            }
            Assert.IsTrue(shared >= later.Length - 2, "Keys must stay with the systems: " + shared + " of " + later.Length);
        }

        [TestMethod]
        public void TheGenericSearchFindsTheSystemsOfAFieldThatCannotListThem()
        {
            var inner = new MovingCellsField(13);
            var hidden = new HiddenSystemsField(inner);
            ForecastContext ctx = Context();
            double t0 = 4.0 * 86400.0 + 600.0;
            ForecastTrack[] found = ForecastEngine.ComputeBodyTracks(Body, t0, 12.0, ctx, hidden);
            long bucket = ForecastCache.BucketOf(t0, ctx.TrackStepSeconds);
            int truthSamples = 0, matched = 0;
            double spacing = MovingCellsField.RadiusM * GridScan.GlobalDegrees * Math.PI / 180.0;
            for (int j = 0; j <= 24; j++)
            {
                double ut = (bucket + j) * ctx.TrackStepSeconds;
                var truth = new List<SystemPoint>();
                inner.Truth(ut, truth);
                foreach (SystemPoint p in truth)
                {
                    // Only systems the five-degree grid can see: a footprint of at least two cells, away from the poles.
                    if (p.Strength01 < 0.3f || p.RadiusM < 0.5 * spacing || Math.Abs(p.LatDeg) > 80.0) continue;
                    truthSamples++;
                    bool near = false;
                    foreach (ForecastTrack t in found)
                    {
                        double lat, lon;
                        float r, s;
                        if (!SystemTracker.PositionAt(t, ut, out lat, out lon, out r, out s)) continue;
                        if (ForecastMath.DistanceM(lat, lon, p.LatDeg, p.LonDeg, MovingCellsField.RadiusM) <= spacing) near = true;
                    }
                    if (near) matched++;
                }
            }
            Console.WriteLine("B12 generic search: " + found.Length + " tracks; " + matched + " of " + truthSamples + " visible systems within one grid cell of a track sample");
            Assert.IsTrue(truthSamples > 20, "The scene must hold systems the grid can see.");
            Assert.IsTrue(matched >= 0.9 * truthSamples, "The generic search lost systems: " + matched + " of " + truthSamples);
        }

        [TestMethod]
        public void TheNearestSystemIsReportedWithItsArrivalAndDistance()
        {
            var field = new MovingCellsField(17);
            ForecastContext ctx = Context();
            double t0 = 2.0 * 86400.0;
            var list = new List<SystemPoint>();
            field.TryGetSystems(Body, t0 + 6.0 * 3600.0, list);
            Assert.IsTrue(list.Count > 0);
            SystemPoint target = list.OrderByDescending(p => p.Strength01).First();
            ForecastSite site = Site(target.LatDeg, target.LonDeg);
            ForecastReport report = ForecastEngine.Compute(site, t0, 24.0, ctx, field);
            Assert.IsFalse(double.IsNaN(report.NearestSystemEtaUt), "A system passes over the site at about 6 hours.");
            Assert.IsTrue(report.NearestSystemEtaUt >= t0 && report.NearestSystemEtaUt <= t0 + 6.0 * 3600.0 + 1.0, "ETA " + (report.NearestSystemEtaUt - t0) / 3600.0 + " h");
            Assert.IsFalse(double.IsNaN(report.NearestSystemDistanceM));
            Assert.IsTrue(report.Tracks.Length > 0);
            Assert.IsTrue(report.Steps.Any(s => s.CellCount > 0), "The step where the system covers the site counts it.");
            ForecastReport noGeometry = ForecastEngine.Compute(site, t0, 24.0, Rules(), field);
            Assert.IsTrue(double.IsNaN(noGeometry.NearestSystemEtaUt));
            Assert.AreEqual(0, noGeometry.Tracks.Length);
        }

        [TestMethod]
        public void ADeterministicDisplayOnlyUncertaintyIsZeroAtLeadZeroAndGrowsWithLead()
        {
            var field = new MovingCellsField(31);
            ForecastSite site = Site(55.0, 15.0);
            double start = 3.0 * 86400.0;
            ForecastReport exact = ForecastEngine.Compute(site, start, 24.0, Context(), field);
            ForecastReport off = ForecastEngine.Compute(site, start, 24.0, Context(uncertainty: 0f), field);
            ForecastReport on = ForecastEngine.Compute(site, start, 24.0, Context(uncertainty: 1f), field);
            ForecastReport again = ForecastEngine.Compute(site, start, 24.0, Context(uncertainty: 1f), field);
            for (int k = 0; k < 24; k++)
            {
                Assert.AreEqual(Bits(exact.Steps[k].Level01), Bits(off.Steps[k].Level01), "Off is exact.");
                Assert.AreEqual(Bits(on.Steps[k].Level01), Bits(again.Steps[k].Level01), "The same issue gives the same picture.");
                Assert.AreEqual(on.Steps[k].Confidence01, again.Steps[k].Confidence01);
            }
            Assert.AreEqual(Bits(exact.Steps[0].Level01), Bits(on.Steps[0].Level01), "Zero at lead 0.");
            Assert.AreEqual(1f, on.Steps[0].Confidence01);
            Assert.IsTrue(on.Steps[23].Confidence01 < 1f, "Confidence falls with the lead.");
            for (int k = 1; k < 24; k++) Assert.IsTrue(on.Steps[k].Confidence01 <= on.Steps[k - 1].Confidence01 + 1e-6f);
            int differing = 0;
            for (int k = 1; k < 24; k++)
            {
                if (Bits(exact.Steps[k].Level01) != Bits(on.Steps[k].Level01)) differing++;
            }
            Console.WriteLine("B12 uncertainty A/B: " + differing + " of 23 later steps differ, last confidence " + on.Steps[23].Confidence01.ToString("F3"));
            Assert.IsTrue(differing > 0, "The error must show somewhere.");
            // The live weather is untouched: the field answers the same before and after.
            WeatherSample s1, s2;
            field.Sample(Body, site.LatDeg, site.LonDeg, 0.0, start + 5 * 3600.0, out s1);
            field.Sample(Body, site.LatDeg, site.LonDeg, 0.0, start + 5 * 3600.0, out s2);
            Assert.AreEqual(Bits(s1.Level01), Bits(s2.Level01));
            // A new 3-hour issue bucket gives another error.
            ForecastReport next = ForecastEngine.Compute(site, start + 3.0 * 3600.0, 24.0, Context(uncertainty: 1f), field);
            Assert.AreNotEqual(ForecastUncertainty.IssueKey(1UL, start), ForecastUncertainty.IssueKey(1UL, start + 3.0 * 3600.0));
            Assert.AreEqual(ForecastUncertainty.IssueKey(1UL, start), ForecastUncertainty.IssueKey(1UL, start + 3.0 * 3600.0 - 1.0));
            Assert.IsNotNull(next);
        }

        [TestMethod]
        public void TheComputePathRunsOnAPlainThreadAndGivesTheSameNumbers()
        {
            var field = new MovingCellsField(41);
            ForecastSite site = Site(61.0, -45.0);
            ForecastReport onMain = ForecastEngine.Compute(site, 86400.0, 24.0, Context(), field);
            ForecastReport onThread = null;
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    onThread = ForecastEngine.Compute(site, 86400.0, 24.0, Context(), field);
                }
                catch (Exception e)
                {
                    failure = e;
                }
            });
            thread.Start();
            thread.Join();
            Assert.IsNull(failure, failure == null ? "" : failure.ToString());
            for (int k = 0; k < 24; k++)
            {
                Assert.AreEqual(Bits(onMain.Steps[k].Level01), Bits(onThread.Steps[k].Level01));
                Assert.AreEqual(onMain.Steps[k].WindMs, onThread.Steps[k].WindMs);
            }
        }

        [TestMethod]
        public void TheEngineSourcesNameNoEngineTypeExceptTheModule()
        {
            string folder = Path.Combine(RepoRoot.Root("src", "ReDefinition.csproj"), "src", "Atmospheres", "Forecast", "Engine");
            string[] files = Directory.GetFiles(folder, "*.cs");
            Assert.IsTrue(files.Length >= 10);
            var forbidden = new[] { "using UnityEngine", "UnityEngine.", "Planetarium.", "FlightGlobals.", ": MonoBehaviour", "ScreenMessages.", "HighLogic.", "CelestialBody " };
            foreach (string file in files)
            {
                if (Path.GetFileName(file) == "ForecastModule.cs") continue;
                string text = File.ReadAllText(file);
                foreach (string word in forbidden) Assert.IsFalse(text.Contains(word), Path.GetFileName(file) + " names " + word);
            }
        }
    }
}
