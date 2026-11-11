using System;
using System.Collections.Generic;
using System.Text;
using ReDefinition.Core;

namespace ReDefinition.Atmospheres.Forecast
{
    // The switches of the delivery; each delivery can be switched on its own. The Dashboard's push is the Dashboard's own listener on
    // WeatherHooks.AlertRaised, so the switch for it is read there.
    internal sealed class AlertDeliverySettings
    {
        internal bool Enabled = true;
        internal bool ScreenMessage = true;
        internal bool MessageApp = true;
        internal bool WarpStop;
    }

    // Where a delivered alert goes. The game's implementation posts a screen message, adds a message to the stock message app and stops
    // time warp; a test records the calls.
    internal interface IAlertSink
    {
        void ScreenMessage(string text, float seconds);
        void MessageApp(string title, string text, AlertKind kind);
        void StopWarp();
        // Whether the site is the one the active vessel is at (a warp stops only for it).
        bool IsActiveVesselSite(ForecastSite site);
        // Whether time warp runs now.
        bool WarpRunning { get; }
    }

    // What was delivered: the message and the alerts it stands for.
    internal sealed class DeliveredMessage
    {
        internal string Text;
        internal readonly List<ForecastAlert> Alerts = new List<ForecastAlert>();
        internal bool Summary;
        internal bool WarpStopped;
    }

    // The delivery of alerts to the player, as a state machine over the alerts the forecast raises (WeatherHooks.AlertRaised):
    //   Receive      queues an alert (a handler of the event, main thread).
    //   Flush        once per frame, delivers what was queued: ONE message for the alerts of one site that arrived together, so that a jump
    //                of the time warp over a system's arrival is one message and not an Approaching, an Arrived and a Clearing.
    //   Dedupe       an alert is delivered once: its key (site, kind, band, minute) is remembered for the session; a rewind of the game time
    //                (a reload) forgets the keys of events after it, as the evaluator does, so an event that lies ahead again can fire.
    //   Suppression  nothing is delivered while the forecast is off, the field is not Ready or the switch is off; what arrives then is
    //                dropped, not held (the evaluator's memory of it already counts it as fired).
    //   Warp stop    only an Approaching alert for the active vessel's site stops time warp, once, when warp runs and the switch is on.
    // Session memory only.
    internal sealed class AlertDelivery
    {
        private const float MessageSeconds = 8f;
        private readonly IAlertSink sink;
        private readonly Func<AlertDeliverySettings> settings;
        private readonly Func<bool> forecastReady;
        private readonly List<ForecastAlert> queue = new List<ForecastAlert>();
        private readonly Dictionary<ulong, double> delivered = new Dictionary<ulong, double>();
        private double lastNowUt = double.NaN;

        internal AlertDelivery(IAlertSink sink, Func<AlertDeliverySettings> settings, Func<bool> forecastReady)
        {
            if (sink == null) throw new ArgumentNullException("sink");
            this.sink = sink;
            this.settings = settings ?? (() => new AlertDeliverySettings());
            this.forecastReady = forecastReady ?? (() => true);
        }

        // Every message delivered so far, newest last (the probe and the tests count them).
        internal readonly List<DeliveredMessage> Log = new List<DeliveredMessage>();

        internal int QueuedCount { get { return queue.Count; } }

        // The handler of WeatherHooks.AlertRaised.
        internal void Receive(ForecastAlert alert)
        {
            queue.Add(alert);
        }

        internal static ulong KeyOf(ForecastAlert a)
        {
            ulong h = AlertEvaluator.SiteKey(a.Site);
            h = ForecastMath.Mix(h, (long)a.Kind);
            h = ForecastMath.Mix(h, (long)a.Band);
            return ForecastMath.Mix(h, (long)Math.Floor(a.Ut / 60.0));
        }

        // Delivers the queued alerts at the game time now. Returns the messages made by this call (empty for none).
        internal List<DeliveredMessage> Flush(double nowUt)
        {
            var made = new List<DeliveredMessage>();
            if (!double.IsNaN(lastNowUt) && nowUt < lastNowUt - 1e-6) ForgetAfter(nowUt);
            lastNowUt = nowUt;
            if (queue.Count == 0) return made;
            AlertDeliverySettings s = settings();
            bool deliver = s.Enabled && forecastReady();
            var groups = new List<List<ForecastAlert>>();
            var siteOf = new List<ulong>();
            for (int i = 0; i < queue.Count; i++)
            {
                ForecastAlert a = queue[i];
                if (!deliver) continue;
                ulong key = KeyOf(a);
                if (delivered.ContainsKey(key)) continue;
                delivered[key] = a.Ut;
                ulong site = AlertEvaluator.SiteKey(a.Site);
                int g = siteOf.IndexOf(site);
                if (g < 0)
                {
                    g = siteOf.Count;
                    siteOf.Add(site);
                    groups.Add(new List<ForecastAlert>());
                }
                groups[g].Add(a);
            }
            queue.Clear();
            bool stopped = false;
            for (int g = 0; g < groups.Count; g++)
            {
                List<ForecastAlert> list = groups[g];
                list.Sort((x, y) => x.Ut.CompareTo(y.Ut));
                var message = new DeliveredMessage();
                message.Alerts.AddRange(list);
                message.Summary = list.Count > 1;
                message.Text = message.Summary ? SummaryText(list) : list[0].Text;
                if (s.ScreenMessage) sink.ScreenMessage(message.Text, MessageSeconds);
                if (s.MessageApp) sink.MessageApp(ForecastStrings.MessageTitle, message.Text, Worst(list).Kind);
                if (s.WarpStop && !stopped && sink.WarpRunning && StopsWarp(list))
                {
                    sink.StopWarp();
                    stopped = true;
                    message.WarpStopped = true;
                }
                Log.Add(message);
                made.Add(message);
            }
            return made;
        }

        // The alert a summary is headed by: the one that matters most to a player (an arrival, a worsening, an approach, then the rest).
        private static ForecastAlert Worst(List<ForecastAlert> list)
        {
            ForecastAlert best = list[0];
            for (int i = 1; i < list.Count; i++)
            {
                if (Rank(list[i].Kind) > Rank(best.Kind)) best = list[i];
            }
            return best;
        }

        private static int Rank(AlertKind kind)
        {
            switch (kind)
            {
                case AlertKind.Worsening: return 4;
                case AlertKind.Arrived: return 3;
                case AlertKind.Approaching: return 2;
                case AlertKind.Clearing: return 1;
                default: return 0;
            }
        }

        private bool StopsWarp(List<ForecastAlert> list)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].Kind == AlertKind.Approaching && sink.IsActiveVesselSite(list[i].Site)) return true;
            }
            return false;
        }

        // "Heavy snow at KSC: approaching, arrived, clearing (passed during time warp)": the text of the alert that matters most, with the
        // events the jump passed.
        internal static string SummaryText(List<ForecastAlert> list)
        {
            ForecastAlert head = Worst(list);
            string text = head.Text;
            int cut = text.IndexOf(ForecastStrings.WarpSummarySuffix, StringComparison.Ordinal);
            if (cut >= 0) text = text.Substring(0, cut);
            var kinds = new StringBuilder();
            var seen = new HashSet<AlertKind>();
            for (int i = 0; i < list.Count; i++)
            {
                if (!seen.Add(list[i].Kind)) continue;
                if (kinds.Length > 0) kinds.Append(", ");
                kinds.Append(KindWord(list[i].Kind));
            }
            return text + " [" + kinds + "]" + ForecastStrings.WarpSummarySuffix;
        }

        internal static string KindWord(AlertKind kind)
        {
            switch (kind)
            {
                case AlertKind.Approaching: return "approaching";
                case AlertKind.Arrived: return "arrived";
                case AlertKind.Worsening: return "worsening";
                case AlertKind.Clearing: return "clearing";
                default: return "launch window";
            }
        }

        private void ForgetAfter(double nowUt)
        {
            var drop = new List<ulong>();
            foreach (KeyValuePair<ulong, double> kv in delivered)
            {
                if (kv.Value > nowUt) drop.Add(kv.Key);
            }
            for (int i = 0; i < drop.Count; i++) delivered.Remove(drop[i]);
        }
    }
}
