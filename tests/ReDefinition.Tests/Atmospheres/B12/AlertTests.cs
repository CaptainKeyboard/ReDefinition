using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Atmospheres.Forecast;
using ReDefinition.Core;

namespace ReDefinition.Tests.Atmospheres.B12
{
    // B12: the alert logic on a field whose level is a function of the UT alone, so that the crossings are known exactly: the Approaching
    // and the Arrived of a jump of the time warp, the memory (no repeat after a reload, a first tick, a rewind), the hysteresis of one
    // clear hour, the summary of a jump and what lies beyond the stored steps, Worsening, Clearing, the wind and the launch check.
    [TestClass]
    public class AlertTests
    {
        private const double H = 3600.0;

        private static ForecastSite Site()
        {
            return new ForecastSite { BodyIndex = 1, LatDeg = 50.0, LonDeg = 10.0, Name = "Pad" };
        }

        private static float Stay(double ut, double fromH, double toH, float level)
        {
            return ut >= fromH * H && ut < toH * H ? level : 0f;
        }

        private static ForecastReport Report(ScriptedField field, double start, AlertRules rules, double stepSeconds = H, double horizonHours = 24.0)
        {
            var ctx = new ForecastContext(new ForecastRules(stepSeconds, horizonHours), 0.0, alerts: rules);
            return ForecastEngine.Compute(Site(), start, horizonHours, ctx, field);
        }

        private static AlertRules Rules(double lead = 3.0, PrecipBand min = PrecipBand.Heavy, float wind = 0f)
        {
            return new AlertRules(lead, min, wind);
        }

        private static int Count(IEnumerable<ForecastAlert> alerts, AlertKind kind)
        {
            return alerts.Count(a => a.Kind == kind);
        }

        [TestMethod]
        public void AJumpOverASystemsEtaGivesExactlyOneApproachingAndOneArrivedAndNoRepeatAfterAReload()
        {
            var field = new ScriptedField { Level = ut => Stay(ut, 10, 16, 0.7f) };
            AlertRules rules = Rules();
            var evaluator = new AlertEvaluator(rules);
            ForecastReport report = Report(field, 0.0, rules);
            Assert.AreEqual(0, evaluator.Tick(Site(), false, report, 0.0).Alerts.Count);
            Assert.AreEqual(0, evaluator.Tick(Site(), false, report, 1 * H).Alerts.Count);

            AlertTick jump = evaluator.Tick(Site(), false, report, 13 * H);
            Console.WriteLine("B12 alerts, jump 1 h -> 13 h over a stay from 10 h: " + string.Join("; ", jump.Alerts.Select(a => a.Kind + " at " + a.Ut / H + " h")));
            Assert.AreEqual(2, jump.Alerts.Count);
            Assert.AreEqual(1, Count(jump.Alerts, AlertKind.Approaching));
            Assert.AreEqual(1, Count(jump.Alerts, AlertKind.Arrived));
            Assert.AreEqual(10 * H, jump.Alerts.First(a => a.Kind == AlertKind.Arrived).Ut);
            Assert.AreEqual(7 * H, jump.Alerts.First(a => a.Kind == AlertKind.Approaching).Ut);
            Assert.AreEqual(7 * H, jump.FirstEventUt, "A warp stop uses the first event time.");
            Assert.IsTrue(jump.Summarised);

            Assert.AreEqual(0, evaluator.Tick(Site(), false, report, 14 * H).Alerts.Count);
            AlertTick clearing = evaluator.Tick(Site(), false, report, 17 * H);
            Assert.AreEqual(1, clearing.Alerts.Count);
            Assert.AreEqual(AlertKind.Clearing, clearing.Alerts[0].Kind);
            Assert.AreEqual(16 * H, clearing.Alerts[0].Ut);

            // A reload: the report is made again, the memory stays; the same ticks give nothing.
            ForecastReport again = Report(field, 12 * H, rules);
            Assert.AreEqual(0, evaluator.Tick(Site(), false, again, 17 * H).Alerts.Count);
            Assert.AreEqual(0, evaluator.Tick(Site(), false, again, 18 * H).Alerts.Count);
        }

        [TestMethod]
        public void ANewSessionFiresNothingForThePastAndOneArrivedForAStayThatIsOn()
        {
            var field = new ScriptedField { Level = ut => Stay(ut, 10, 16, 0.7f) };
            AlertRules rules = Rules();
            ForecastReport report = Report(field, 12 * H, rules);
            var evaluator = new AlertEvaluator(rules);
            AlertTick first = evaluator.Tick(Site(), false, report, 13 * H);
            Assert.AreEqual(1, first.Alerts.Count);
            Assert.AreEqual(AlertKind.Arrived, first.Alerts[0].Kind);
            Assert.AreEqual(13 * H, first.Alerts[0].Ut);
            Assert.AreEqual(0, evaluator.Tick(Site(), false, report, 14 * H).Alerts.Count);
            Assert.AreEqual(0, evaluator.Tick(Site(), false, report, 15 * H).Alerts.Count);
            Assert.AreEqual(1, Count(evaluator.Tick(Site(), false, report, 17 * H).Alerts, AlertKind.Clearing));

            // A stay that is over before the first tick gives nothing.
            var quiet = new AlertEvaluator(rules);
            Assert.AreEqual(0, quiet.Tick(Site(), false, Report(field, 18 * H, rules), 19 * H).Alerts.Count);
        }

        [TestMethod]
        public void AClearStretchUnderOneHourDoesNotRearmAndOneHourDoes()
        {
            AlertRules rules = Rules();
            // A dip of one half-hour step inside the stay: one stay, one of each.
            var shortDip = new ScriptedField { Level = ut => (ut >= 6 * H && ut < 8 * H && !(ut >= 7 * H && ut < 7.5 * H)) ? 0.7f : 0f };
            var evaluator = new AlertEvaluator(rules);
            ForecastReport report = Report(shortDip, 0.0, rules, 1800.0, 12.0);
            var all = new List<ForecastAlert>();
            for (double t = 0; t <= 12 * H; t += 1800.0) all.AddRange(evaluator.Tick(Site(), false, report, t).Alerts);
            Console.WriteLine("B12 hysteresis, dip of 30 min: " + string.Join("; ", all.Select(a => a.Kind + " " + a.Ut / H)));
            Assert.AreEqual(1, Count(all, AlertKind.Approaching));
            Assert.AreEqual(1, Count(all, AlertKind.Arrived));
            Assert.AreEqual(1, Count(all, AlertKind.Clearing));
            Assert.AreEqual(8 * H, all.First(a => a.Kind == AlertKind.Clearing).Ut);

            // A dip of two half-hour steps (one hour): the stay ends and a new one starts.
            var longDip = new ScriptedField { Level = ut => (ut >= 6 * H && ut < 9 * H && !(ut >= 7 * H && ut < 8 * H)) ? 0.7f : 0f };
            evaluator = new AlertEvaluator(rules);
            report = Report(longDip, 0.0, rules, 1800.0, 12.0);
            all.Clear();
            for (double t = 0; t <= 12 * H; t += 1800.0) all.AddRange(evaluator.Tick(Site(), false, report, t).Alerts);
            Console.WriteLine("B12 hysteresis, dip of one hour: " + string.Join("; ", all.Select(a => a.Kind + " " + a.Ut / H)));
            Assert.AreEqual(2, Count(all, AlertKind.Approaching));
            Assert.AreEqual(2, Count(all, AlertKind.Arrived));
            Assert.AreEqual(2, Count(all, AlertKind.Clearing));
        }

        [TestMethod]
        public void AWarpJumpIsSummarisedFromTheStoredStepsAndNothingBeyondThemIsMade()
        {
            var field = new ScriptedField { Level = ut => Stay(ut, 4, 6, 0.7f) + Stay(ut, 10, 12, 0.7f) + Stay(ut, 30, 32, 0.7f) };
            AlertRules rules = Rules();
            ForecastReport report = Report(field, 0.0, rules);
            var evaluator = new AlertEvaluator(rules);
            evaluator.Tick(Site(), false, report, 0.0);
            AlertTick jump = evaluator.Tick(Site(), false, report, 20 * H);
            Console.WriteLine("B12 summary of a jump 0 -> 20 h: " + string.Join("; ", jump.Alerts.Select(a => a.Kind + " " + a.Ut / H)));
            Assert.AreEqual(6, jump.Alerts.Count);
            Assert.IsTrue(jump.Summarised);
            Assert.IsFalse(jump.BeyondHorizon);
            Assert.AreEqual(1 * H, jump.FirstEventUt);
            for (int i = 1; i < jump.Alerts.Count; i++) Assert.IsTrue(jump.Alerts[i].Ut >= jump.Alerts[i - 1].Ut);
            Assert.IsTrue(jump.Alerts.All(a => a.Text.Contains("passed during time warp")));

            // Past the stored 24 hours: the stay at 30 h is not known and not summarised.
            AlertTick beyond = evaluator.Tick(Site(), false, report, 40 * H);
            Assert.IsTrue(beyond.BeyondHorizon);
            Assert.AreEqual(0, beyond.Alerts.Count);
            var other = new AlertEvaluator(rules);
            other.Tick(Site(), false, report, 0.0);
            AlertTick far = other.Tick(Site(), false, report, 40 * H);
            Assert.AreEqual(6, far.Alerts.Count, "The events inside the stored steps are summarised, those beyond are not.");
            Assert.IsTrue(far.BeyondHorizon);
        }

        [TestMethod]
        public void TheMemoryRearmsWhenTheUtGoesBackwards()
        {
            var field = new ScriptedField { Level = ut => Stay(ut, 10, 12, 0.7f) };
            AlertRules rules = Rules();
            ForecastReport report = Report(field, 0.0, rules);
            var evaluator = new AlertEvaluator(rules);
            var seen = new List<ForecastAlert>();
            foreach (double t in new[] { 0.0, 8 * H, 11 * H })
            {
                seen.AddRange(evaluator.Tick(Site(), false, report, t).Alerts);
            }
            Assert.AreEqual(1, Count(seen, AlertKind.Approaching));
            Assert.AreEqual(1, Count(seen, AlertKind.Arrived));
            // A revert to 2 h: the same events lie in the future again.
            seen.Clear();
            foreach (double t in new[] { 2 * H, 8 * H, 11 * H })
            {
                seen.AddRange(evaluator.Tick(Site(), false, report, t).Alerts);
            }
            Console.WriteLine("B12 rewind 11 h -> 2 h and forward again: " + string.Join("; ", seen.Select(a => a.Kind + " " + a.Ut / H)));
            Assert.AreEqual(1, Count(seen, AlertKind.Approaching));
            Assert.AreEqual(1, Count(seen, AlertKind.Arrived));
        }

        [TestMethod]
        public void WorseningAndClearingFollowTheBandsOfTheStay()
        {
            var field = new ScriptedField { Level = ut => Stay(ut, 5, 7, 0.65f) + Stay(ut, 7, 9, 0.85f), WindEast = ut => 20f };
            AlertRules rules = Rules(2.0);
            ForecastReport report = Report(field, 0.0, rules);
            Assert.AreEqual(PrecipBand.Heavy, report.Steps[5].Band);
            Assert.AreEqual(PrecipBand.Blizzard, report.Steps[7].Band, "A level of 0.85 with 20 m/s is a Blizzard.");
            var evaluator = new AlertEvaluator(rules);
            var seen = new List<ForecastAlert>();
            for (double t = 0; t <= 12 * H; t += H) seen.AddRange(evaluator.Tick(Site(), false, report, t).Alerts);
            Console.WriteLine("B12 stay: " + string.Join("; ", seen.Select(a => a.Kind + " " + a.Ut / H + " " + a.Band)));
            CollectionAssert.AreEqual(
                new[] { AlertKind.Approaching, AlertKind.Arrived, AlertKind.Worsening, AlertKind.Clearing },
                seen.Select(a => a.Kind).ToArray());
            Assert.AreEqual(7 * H, seen[2].Ut);
            Assert.AreEqual(PrecipBand.Blizzard, seen[2].Band);
            Assert.AreEqual(9 * H, seen[3].Ut);
            // The shape of the report lists what is coming.
            Assert.AreEqual(4, Report(field, 0.0, rules).Alerts.Length);
        }

        [TestMethod]
        public void AWindAboveTheUsersLimitIsAnAlertOnItsOwn()
        {
            var field = new ScriptedField { Level = ut => 0f, WindEast = ut => ut >= 6 * H && ut < 10 * H ? 18f : 3f };
            AlertRules rules = Rules(3.0, PrecipBand.Heavy, 15f);
            ForecastReport report = Report(field, 0.0, rules);
            var evaluator = new AlertEvaluator(rules);
            var seen = new List<ForecastAlert>();
            for (double t = 0; t <= 12 * H; t += H) seen.AddRange(evaluator.Tick(Site(), false, report, t).Alerts);
            Assert.AreEqual(1, Count(seen, AlertKind.Arrived));
            Assert.IsTrue(seen.First(a => a.Kind == AlertKind.Arrived).Text.StartsWith("Wind 18 m/s"), seen.First(a => a.Kind == AlertKind.Arrived).Text);
            Assert.AreEqual(1, Count(seen, AlertKind.Clearing));
            // With the wind condition off nothing fires.
            var quiet = new AlertEvaluator(Rules(3.0, PrecipBand.Heavy, 0f));
            Assert.AreEqual(0, quiet.Tick(Site(), false, Report(field, 0.0, Rules(3.0, PrecipBand.Heavy, 0f)), 0.0).Alerts.Count);
        }

        [TestMethod]
        public void TheLaunchCheckSaysGoMarginalOrNoGoAndFindsTheBestWindow()
        {
            var field = new ScriptedField { WindEast = ut => ut < 4 * H || ut >= 10 * H ? 14f : 5f };
            AlertRules rules = new AlertRules();
            ForecastReport report = Report(field, 0.0, rules);
            LaunchAssessment now0 = LaunchConditions.Assess(report, 0.0, rules);
            Assert.AreEqual(LaunchStatus.NoGo, now0.Status);
            Assert.IsTrue(now0.Reasons.Any(r => r.StartsWith("Wind 14 m/s above the limit of 10 m/s")), string.Join("|", now0.Reasons));
            Assert.AreEqual(4 * H, now0.WindowStartUt);
            Assert.AreEqual(10 * H, now0.WindowEndUt);
            Assert.AreEqual(LaunchStatus.Go, LaunchConditions.Assess(report, 4 * H, rules).Status);
            LaunchAssessment marginal = LaunchConditions.Assess(report, 9 * H, rules);
            Assert.AreEqual(LaunchStatus.Marginal, marginal.Status);
            Assert.IsTrue(marginal.Reasons[0].Contains("in 1 h"), marginal.Reasons[0]);
            Assert.AreEqual(LaunchStatus.Unavailable, LaunchConditions.Assess(null, 0.0, rules).Status);

            // A window shorter than the lead window is not offered; the alert fires when a window begins.
            var narrow = new ScriptedField { WindEast = ut => ut >= 4 * H && ut < 5 * H ? 5f : 14f };
            Assert.IsTrue(double.IsNaN(LaunchConditions.Assess(Report(narrow, 0.0, rules), 0.0, rules).WindowStartUt));
            var evaluator = new AlertEvaluator(rules);
            var seen = new List<ForecastAlert>();
            for (double t = 0; t <= 12 * H; t += H) seen.AddRange(evaluator.Tick(Site(), true, report, t).Alerts);
            Assert.AreEqual(1, Count(seen, AlertKind.LaunchWindow));
            Assert.AreEqual(4 * H, seen.First(a => a.Kind == AlertKind.LaunchWindow).Ut);
            var noLaunch = new AlertEvaluator(rules);
            Assert.AreEqual(0, Count(noLaunch.Tick(Site(), false, report, 12 * H).Alerts, AlertKind.LaunchWindow));
        }

        [TestMethod]
        public void AReportThatIsUnavailableGivesNoAlertsAndNoLaunchAnswer()
        {
            var field = new ScriptedField { Level = ut => 0.9f, Approx = true };
            AlertRules rules = Rules();
            ForecastReport report = Report(field, 0.0, rules);
            Assert.IsFalse(report.Available);
            var evaluator = new AlertEvaluator(rules);
            Assert.AreEqual(0, evaluator.Tick(Site(), true, report, 0.0).Alerts.Count);
            Assert.AreEqual(LaunchStatus.Unavailable, LaunchConditions.Assess(report, 0.0, rules).Status);
        }

        [TestMethod]
        public void ASystemHandingOverInsideTheHorizonIsOneStayAtTheSite()
        {
            // Two systems with different keys overlap: A fades out while B grows. The level at the site stays above Heavy throughout.
            var field = new ScriptedField
            {
                Level = ut =>
                {
                    float a = Stay(ut, 6, 12, 0.75f) * (ut < 9 * H ? 1f : (float)Math.Max(0.0, 1.0 - (ut - 9 * H) / (3 * H)));
                    float b = ut >= 9 * H && ut < 16 * H ? 0.65f : 0f;
                    return Math.Max(a, b);
                }
            };
            AlertRules rules = Rules();
            ForecastReport report = Report(field, 0.0, rules);
            var evaluator = new AlertEvaluator(rules);
            var seen = new List<ForecastAlert>();
            for (double t = 0; t <= 20 * H; t += H) seen.AddRange(evaluator.Tick(Site(), false, report, t).Alerts);
            Console.WriteLine("B12 hand-over: " + string.Join("; ", seen.Select(a => a.Kind + " " + a.Ut / H)));
            Assert.AreEqual(1, Count(seen, AlertKind.Approaching));
            Assert.AreEqual(1, Count(seen, AlertKind.Arrived));
            Assert.AreEqual(1, Count(seen, AlertKind.Clearing));
        }
    }
}
