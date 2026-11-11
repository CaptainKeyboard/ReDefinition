using System;
using System.Collections.Generic;
using System.Globalization;
using ReDefinition.Core;

namespace ReDefinition.Atmospheres.Forecast
{
    // Whether a launch may go.
    internal enum LaunchStatus
    {
        // No forecast to judge by.
        Unavailable = 0,
        // The limits hold now and for the lead window ahead.
        Go = 1,
        // The limits hold now but break inside the lead window.
        Marginal = 2,
        // A limit is broken now.
        NoGo = 3
    }

    // The answer of the launch check: the status, the reasons in words, and the best window of the next hours.
    internal sealed class LaunchAssessment
    {
        internal LaunchStatus Status;
        internal string[] Reasons = new string[0];
        // The best window in the next 24 hours (the longest run of steps within the limits that is at least the lead window long; the
        // earliest of equals); NaN where there is none.
        internal double WindowStartUt = double.NaN;
        internal double WindowEndUt = double.NaN;
    }

    // The launch check: max level, max wind, min visibility and a lead window, applied to the steps of a forecast. Pure, any thread.
    // Information only; it never blocks a launch.
    internal static class LaunchConditions
    {
        // Whether a step is within the limits; reason names the broken limit (or is null).
        internal static bool StepOk(ForecastStep step, AlertRules rules, out string reason)
        {
            if ((int)step.Band > (int)rules.LaunchMaxBand)
            {
                reason = PrecipLabel(step) + " above the limit of " + PrecipScale.DefaultName(rules.LaunchMaxBand);
                return false;
            }
            if (step.WindMs > rules.LaunchMaxWindMs)
            {
                reason = "Wind " + Num(step.WindMs) + " m/s above the limit of " + Num(rules.LaunchMaxWindMs) + " m/s";
                return false;
            }
            if (step.VisibilityM < rules.LaunchMinVisibilityM)
            {
                reason = "Visibility " + Num(step.VisibilityM) + " m below the limit of " + Num(rules.LaunchMinVisibilityM) + " m";
                return false;
            }
            reason = null;
            return true;
        }

        // The assessment at a time from a report: the status for now and the lead window, the reasons, and the best window.
        internal static LaunchAssessment Assess(ForecastReport report, double nowUt, AlertRules rules, double lookaheadHours = 24.0)
        {
            var result = new LaunchAssessment();
            if (report == null || !report.Available || report.Steps.Length == 0)
            {
                result.Status = LaunchStatus.Unavailable;
                return result;
            }
            ForecastStep[] steps = report.Steps;
            double stepSeconds = report.StepSeconds;
            int now = IndexAt(steps, nowUt);
            double leadEnd = nowUt + rules.LaunchLeadHours * 3600.0;
            var reasons = new List<string>();
            string reason;
            LaunchStatus status = LaunchStatus.Go;
            if (!StepOk(steps[now], rules, out reason))
            {
                status = LaunchStatus.NoGo;
                reasons.Add(reason + " now");
            }
            for (int i = now + 1; i < steps.Length && steps[i].Ut <= leadEnd; i++)
            {
                if (StepOk(steps[i], rules, out reason)) continue;
                if (status == LaunchStatus.Go) status = LaunchStatus.Marginal;
                reasons.Add(reason + " " + InText(steps[i].Ut - nowUt));
                break;
            }
            result.Status = status;
            result.Reasons = reasons.ToArray();

            double need = rules.LaunchLeadHours * 3600.0;
            double lookEnd = nowUt + lookaheadHours * 3600.0;
            double bestLen = 0.0;
            int runStart = -1;
            for (int i = now; i <= steps.Length; i++)
            {
                bool ok = i < steps.Length && steps[i].Ut < lookEnd && StepOk(steps[i], rules, out reason);
                if (ok)
                {
                    if (runStart < 0) runStart = i;
                    continue;
                }
                if (runStart >= 0)
                {
                    double len = (i - runStart) * stepSeconds;
                    if (len >= need && len > bestLen)
                    {
                        bestLen = len;
                        result.WindowStartUt = steps[runStart].Ut;
                        result.WindowEndUt = steps[runStart].Ut + len;
                    }
                    runStart = -1;
                }
                if (i >= steps.Length || steps[i].Ut >= lookEnd) break;
            }
            return result;
        }

        // The windows of a report: runs of steps within the limits at least the lead window long, as start and end times. A run that
        // already holds at the first step is left out (it did not begin inside the report).
        internal static void Windows(ForecastReport report, AlertRules rules, List<double[]> into)
        {
            ForecastStep[] steps = report.Steps;
            double need = rules.LaunchLeadHours * 3600.0;
            int runStart = -1;
            string reason;
            for (int i = 0; i <= steps.Length; i++)
            {
                bool ok = i < steps.Length && StepOk(steps[i], rules, out reason);
                if (ok)
                {
                    if (runStart < 0) runStart = i;
                    continue;
                }
                if (runStart > 0)
                {
                    double len = (i - runStart) * report.StepSeconds;
                    if (len >= need) into.Add(new[] { steps[runStart].Ut, steps[runStart].Ut + len });
                }
                runStart = -1;
            }
        }

        // The index of the step a time falls in (the last step at or before it; the first where it is before all).
        internal static int IndexAt(ForecastStep[] steps, double ut)
        {
            int at = 0;
            for (int i = 0; i < steps.Length; i++)
            {
                if (steps[i].Ut <= ut) at = i;
                else break;
            }
            return at;
        }

        internal static string PrecipLabel(ForecastStep step)
        {
            return PrecipScale.DefaultName(step.Band) + " " + TypeName(step.Type);
        }

        internal static string TypeName(PrecipType type)
        {
            switch (type)
            {
                case PrecipType.Snow: return "snow";
                case PrecipType.Rain: return "rain";
                case PrecipType.Sleet: return "sleet";
                case PrecipType.FreezingRain: return "freezing rain";
                case PrecipType.Dust: return "dust";
                case PrecipType.Sand: return "sand";
                case PrecipType.Ash: return "ash";
                case PrecipType.Methane: return "methane rain";
                default: return "precipitation";
            }
        }

        internal static string InText(double seconds)
        {
            if (seconds < 60.0) return "now";
            int minutes = (int)Math.Round(seconds / 60.0);
            int h = minutes / 60, m = minutes % 60;
            if (h == 0) return "in " + m.ToString(CultureInfo.InvariantCulture) + " min";
            if (m == 0) return "in " + h.ToString(CultureInfo.InvariantCulture) + " h";
            return "in " + h.ToString(CultureInfo.InvariantCulture) + " h " + m.ToString(CultureInfo.InvariantCulture) + " min";
        }

        private static string Num(float value)
        {
            return Math.Round(value).ToString(CultureInfo.InvariantCulture);
        }
    }
}
