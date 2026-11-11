using System;
using System.Collections.Generic;
using ReDefinition.Core;

namespace ReDefinition.Atmospheres.Forecast
{
    // The alerts one tick of the evaluator produced.
    internal sealed class AlertTick
    {
        internal readonly List<ForecastAlert> Alerts = new List<ForecastAlert>();
        // The time of the first alert (a time warp stops there); NaN where there is none.
        internal double FirstEventUt = double.NaN;
        // True where the tick spans more than one forecast step: the alerts are a summary of what the jump passed.
        internal bool Summarised;
        // True where the tick reaches past the stored forecast: what lies beyond is not summarised.
        internal bool BeyondHorizon;
    }

    // The alert logic, pure and without delivery (messages, warp stop and Kerbal Alarm Clock are the forecast window's). An alert is a
    // threshold crossing of the hourly steps of a SITE: the user's level or wind reached (Approaching one lead time ahead, Arrived at the
    // crossing), the band rising during the stay (Worsening), the level falling away (Clearing), and the start of a window in which the
    // launch limits hold (LaunchWindow). The id of a system is only a label (the CellKey of the alert).
    //   Hysteresis: a clear stretch shorter than one hour does not end the stay and does not re-arm; one hour of clear steps does. The
    //   evaluator remembers the last step that hit the site, so a stay is one stay however the report window slides over it.
    //   Dedupe: a crossing is keyed by the site, the kind and the step it happens in (and the band for Worsening); a key fires once.
    //   Tick: interval based. The events inside (previous tick, now] are what fires, so a jump of a time warp over a crossing gives the
    //   Approaching and the Arrived of it once each; events beyond the stored steps are not summarised.
    //   Memory: session only. A tick whose UT is before the last one (a revert or a quickload) forgets the keys of events after it.
    //   A first tick (no memory) fires nothing for events in the past; a stay that is already on fires one Arrived. A tick without a usable
    //   report changes nothing, so the next one summarises what the gap passed.
    internal sealed class AlertEvaluator
    {
        private struct Event
        {
            internal AlertKind Kind;
            internal double Ut;
            internal double ArrivalUt;
            internal PrecipBand Band;
            internal ulong Key;
            internal int StepIndex;
        }

        // What the evaluator knows of the stay before the first step of the report it is given.
        private struct History
        {
            internal bool Known;
            internal double LastHitUt;
            internal PrecipBand StayMax;
        }

        private sealed class SiteState
        {
            internal double LastUt = double.NaN;
            internal readonly Dictionary<ulong, double> Fired = new Dictionary<ulong, double>();
            internal bool OngoingFired;
            // The last step (its UT) that hit the site, and the highest band of the stay it belongs to.
            internal double LastHitUt = double.NaN;
            internal PrecipBand StayMax = PrecipBand.None;
        }

        // How long a key is kept after its event, in seconds.
        private const double KeepSeconds = 3.0 * 86400.0;

        private readonly Dictionary<ulong, SiteState> sites = new Dictionary<ulong, SiteState>();
        private AlertRules rules;

        internal AlertEvaluator(AlertRules rules)
        {
            this.rules = rules ?? AlertRules.Default;
        }

        // The thresholds; a change takes effect at the next tick and keeps the memory.
        internal AlertRules Rules
        {
            get { return rules; }
            set { rules = value ?? AlertRules.Default; }
        }

        // Forgets everything (a new game).
        internal void Reset()
        {
            sites.Clear();
        }

        // The key that stands for a site in the memory.
        internal static ulong SiteKey(ForecastSite site)
        {
            int lat = (int)Math.Round(site.LatDeg * 10.0);
            int lon = (int)Math.Round(ForecastMath.WrapLon(site.LonDeg) * 10.0);
            if (lon >= 1800) lon -= 3600;
            return ForecastUncertainty.SiteSubject(site.BodyIndex, lat, lon);
        }

        // One tick for a site at the time now. bodyRadiusM lets the alert name the system (CellKey) from the report's tracks; 0 leaves it 0.
        internal AlertTick Tick(ForecastSite site, bool launchSite, ForecastReport report, double nowUt, double bodyRadiusM = 0.0)
        {
            var result = new AlertTick();
            if (report == null || !report.Available || report.Steps.Length == 0 || report.StepSeconds <= 0.0) return result;
            ulong siteKey = SiteKey(site);
            SiteState state;
            if (!sites.TryGetValue(siteKey, out state))
            {
                state = new SiteState();
                sites[siteKey] = state;
            }
            double prev = state.LastUt;
            bool first = double.IsNaN(prev);
            if (!first && nowUt < prev - 1e-6)
            {
                Rearm(state, nowUt);
                first = true;
            }
            if (first) prev = nowUt;
            Prune(state, nowUt);

            ForecastStep[] steps = report.Steps;
            double step = report.StepSeconds;
            double firstUt = steps[0].Ut;
            double endUt = firstUt + steps.Length * step;
            result.Summarised = !first && nowUt - prev > step;
            result.BeyondHorizon = nowUt > endUt;

            var events = new List<Event>();
            var history = new History { Known = !first, LastHitUt = state.LastHitUt, StayMax = state.StayMax };
            Extract(site, launchSite, report, rules, history, events);
            double lead = rules.LeadHours * 3600.0;
            for (int i = 0; i < events.Count; i++)
            {
                Event e = events[i];
                if (state.Fired.ContainsKey(e.Key)) continue;
                switch (e.Kind)
                {
                    case AlertKind.Approaching:
                        if (e.ArrivalUt <= prev)
                        {
                            state.Fired[e.Key] = e.ArrivalUt;
                        }
                        else if (e.ArrivalUt - lead <= nowUt)
                        {
                            state.Fired[e.Key] = e.ArrivalUt;
                            double at = Math.Max(prev, e.ArrivalUt - lead);
                            if (at > nowUt) at = nowUt;
                            result.Alerts.Add(Make(site, report, e, at, e.ArrivalUt - at, bodyRadiusM, result.Summarised, rules));
                        }
                        break;
                    case AlertKind.Arrived:
                    case AlertKind.Worsening:
                    case AlertKind.Clearing:
                    case AlertKind.LaunchWindow:
                        if (e.Ut <= prev)
                        {
                            state.Fired[e.Key] = e.Ut;
                        }
                        else if (e.Ut <= nowUt)
                        {
                            state.Fired[e.Key] = e.Ut;
                            result.Alerts.Add(Make(site, report, e, e.Ut, 0.0, bodyRadiusM, result.Summarised, rules));
                        }
                        break;
                }
            }

            int nowIndex = LaunchConditions.IndexAt(steps, nowUt);
            bool inWindow = nowUt >= firstUt && nowUt < endUt;
            bool hitNow = inWindow && rules.Hits(steps[nowIndex]);
            if (!hitNow)
            {
                state.OngoingFired = false;
            }
            else if (first && !state.OngoingFired)
            {
                state.OngoingFired = true;
                var ongoing = new Event { Kind = AlertKind.Arrived, Ut = nowUt, ArrivalUt = nowUt, Band = steps[nowIndex].Band, StepIndex = nowIndex };
                result.Alerts.Add(Make(site, report, ongoing, nowUt, 0.0, bodyRadiusM, false, rules));
            }
            else if (!first)
            {
                state.OngoingFired = true;
            }

            // What the next tick compares with: the last step that hit, and the highest band of its stay.
            if (inWindow)
            {
                int minClear = Math.Max(1, (int)Math.Ceiling(3600.0 / step - 1e-9));
                int from = first ? nowIndex : Math.Min(LaunchConditions.IndexAt(steps, prev), nowIndex);
                for (int i = from; i <= nowIndex; i++)
                {
                    if (!rules.Hits(steps[i])) continue;
                    if (double.IsNaN(state.LastHitUt) || steps[i].Ut - state.LastHitUt > minClear * step + 1e-6) state.StayMax = PrecipBand.None;
                    state.LastHitUt = steps[i].Ut;
                    if ((int)steps[i].Band > (int)state.StayMax) state.StayMax = steps[i].Band;
                }
            }
            state.LastUt = nowUt;

            result.Alerts.Sort(CompareAlerts);
            if (result.Alerts.Count > 0) result.FirstEventUt = result.Alerts[0].Ut;
            return result;
        }

        private static int CompareAlerts(ForecastAlert a, ForecastAlert b)
        {
            int c = a.Ut.CompareTo(b.Ut);
            return c != 0 ? c : ((int)a.Kind).CompareTo((int)b.Kind);
        }

        // The alerts a report foresees, without memory: every event of it at or after fromUt, in time order. The report carries them in
        // ForecastReport.Alerts for the window that lists what is coming.
        internal static ForecastAlert[] Preview(ForecastSite site, bool launchSite, ForecastReport report, AlertRules rules, double fromUt, double bodyRadiusM)
        {
            var events = new List<Event>();
            Extract(site, launchSite, report, rules, new History { Known = false, LastHitUt = double.NaN, StayMax = PrecipBand.None }, events);
            var list = new List<ForecastAlert>();
            for (int i = 0; i < events.Count; i++)
            {
                Event e = events[i];
                double ut = e.Kind == AlertKind.Approaching ? Math.Max(fromUt, e.ArrivalUt - rules.LeadHours * 3600.0) : e.Ut;
                if (e.Kind == AlertKind.Approaching ? e.ArrivalUt < fromUt : e.Ut < fromUt) continue;
                list.Add(Make(site, report, e, ut, e.Kind == AlertKind.Approaching ? e.ArrivalUt - ut : 0.0, bodyRadiusM, false, rules));
            }
            list.Sort(CompareAlerts);
            return list.ToArray();
        }

        // Forgets the keys of events after a time: after a rewind they are in the future again.
        private static void Rearm(SiteState state, double nowUt)
        {
            var drop = new List<ulong>();
            foreach (KeyValuePair<ulong, double> kv in state.Fired)
            {
                if (kv.Value > nowUt) drop.Add(kv.Key);
            }
            for (int i = 0; i < drop.Count; i++) state.Fired.Remove(drop[i]);
            state.OngoingFired = false;
            state.LastHitUt = double.NaN;
            state.StayMax = PrecipBand.None;
        }

        private static void Prune(SiteState state, double nowUt)
        {
            if (state.Fired.Count < 64) return;
            var drop = new List<ulong>();
            foreach (KeyValuePair<ulong, double> kv in state.Fired)
            {
                if (kv.Value < nowUt - KeepSeconds) drop.Add(kv.Key);
            }
            for (int i = 0; i < drop.Count; i++) state.Fired.Remove(drop[i]);
        }

        // The crossings of a report: one stay per run of hits, with the clear-hour hysteresis. history says what came before the first step.
        private static void Extract(ForecastSite site, bool launchSite, ForecastReport report, AlertRules rules, History history, List<Event> into)
        {
            ForecastStep[] steps = report.Steps;
            if (steps.Length == 0 || report.StepSeconds <= 0.0) return;
            double step = report.StepSeconds;
            ulong siteKey = SiteKey(site);
            int minClear = Math.Max(1, (int)Math.Ceiling(3600.0 / step - 1e-9));
            double lead = rules.LeadHours * 3600.0;
            bool inStay = false;
            int clearRun = 0;
            PrecipBand maxBand = PrecipBand.None;
            if (history.Known && !double.IsNaN(history.LastHitUt))
            {
                // The stay is still open when fewer than one clear hour lies between its last hit and the first step.
                long k = (long)Math.Round((steps[0].Ut - history.LastHitUt) / step);
                if (k >= 0 && k <= minClear)
                {
                    inStay = true;
                    clearRun = (int)Math.Max(0L, k - 1);
                    maxBand = history.StayMax;
                }
            }
            for (int i = 0; i < steps.Length; i++)
            {
                if (rules.Hits(steps[i]))
                {
                    if (!inStay)
                    {
                        inStay = true;
                        clearRun = 0;
                        maxBand = steps[i].Band;
                        // At the first step of a report without history the stay may have begun before it: no arrival.
                        if (i > 0 || history.Known)
                        {
                            into.Add(Make(AlertKind.Approaching, steps[i].Ut - lead, steps[i].Ut, steps[i].Band, siteKey, i, step));
                            into.Add(Make(AlertKind.Arrived, steps[i].Ut, steps[i].Ut, steps[i].Band, siteKey, i, step));
                        }
                    }
                    else
                    {
                        clearRun = 0;
                        if ((int)steps[i].Band > (int)maxBand)
                        {
                            maxBand = steps[i].Band;
                            into.Add(Make(AlertKind.Worsening, steps[i].Ut, steps[i].Ut, steps[i].Band, siteKey, i, step));
                        }
                    }
                }
                else if (inStay)
                {
                    clearRun++;
                    if (clearRun == minClear)
                    {
                        double ut = steps[i].Ut - (minClear - 1) * step;
                        into.Add(Make(AlertKind.Clearing, ut, ut, steps[i].Band, siteKey, Math.Max(0, i - minClear + 1), step));
                        inStay = false;
                    }
                }
            }
            if (launchSite)
            {
                var windows = new List<double[]>();
                LaunchConditions.Windows(report, rules, windows);
                for (int w = 0; w < windows.Count; w++)
                {
                    int at = (int)Math.Round((windows[w][0] - steps[0].Ut) / step);
                    into.Add(Make(AlertKind.LaunchWindow, windows[w][0], windows[w][0], PrecipBand.None, siteKey, at, step));
                }
            }
        }

        private static Event Make(AlertKind kind, double ut, double arrivalUt, PrecipBand band, ulong siteKey, int stepIndex, double step)
        {
            // The key is the site, the kind and the step the crossing happens in (an absolute number of steps), and the band for a
            // Worsening, so that it stays the same while the report slides.
            ulong key = ForecastMath.Mix(ForecastMath.Mix(siteKey, (long)kind), (long)Math.Floor(arrivalUt / step));
            if (kind == AlertKind.Worsening) key = ForecastMath.Mix(key, (long)band);
            return new Event { Kind = kind, Ut = ut, ArrivalUt = arrivalUt, Band = band, Key = key, StepIndex = stepIndex };
        }

        private static ForecastAlert Make(ForecastSite site, ForecastReport report, Event e, double ut, double etaSeconds, double bodyRadiusM, bool summarised,
            AlertRules rules)
        {
            var alert = new ForecastAlert();
            alert.Kind = e.Kind;
            alert.Ut = ut;
            alert.EtaSeconds = etaSeconds;
            alert.Band = e.Band;
            alert.Site = site;
            ForecastStep s = report.Steps[Math.Min(Math.Max(e.StepIndex, 0), report.Steps.Length - 1)];
            if (bodyRadiusM > 0.0 && report.Tracks.Length > 0)
            {
                double at = e.Kind == AlertKind.Approaching ? e.ArrivalUt : e.Ut;
                alert.CellKey = SystemTracker.CoveringKey(report.Tracks, site.LatDeg, site.LonDeg, at, bodyRadiusM);
            }
            alert.Text = TextOf(e, s, site, etaSeconds, rules) + (summarised ? " (passed during time warp)" : "");
            return alert;
        }

        private static string TextOf(Event e, ForecastStep s, ForecastSite site, double etaSeconds, AlertRules rules)
        {
            string where = string.IsNullOrEmpty(site.Name) ? "the site" : site.Name;
            bool windOnly = (int)s.Band < (int)rules.MinBand;
            string what = windOnly ? "Wind " + Math.Round(s.WindMs).ToString(System.Globalization.CultureInfo.InvariantCulture) + " m/s" : LaunchConditions.PrecipLabel(s);
            switch (e.Kind)
            {
                case AlertKind.Approaching: return what + " approaching " + where + " " + LaunchConditions.InText(etaSeconds);
                case AlertKind.Arrived: return what + " at " + where;
                case AlertKind.Worsening: return what + " at " + where + ", getting worse";
                case AlertKind.Clearing: return "Weather at " + where + " is clearing";
                default: return "Launch window opens at " + where;
            }
        }
    }
}
