using System;
using ReDefinition.Core;

namespace ReDefinition.Atmospheres.Forecast
{
    // The user's thresholds for alerts and for the launch check. Immutable; the module copies the settings into one on the main thread.
    internal sealed class AlertRules
    {
        internal static readonly AlertRules Default = new AlertRules();

        internal AlertRules(double leadHours = 3.0, PrecipBand minBand = PrecipBand.Heavy, float windMs = 15f, PrecipBand launchMaxBand = PrecipBand.Heavy,
            float launchMaxWindMs = 10f, float launchMinVisibilityM = 1000f, double launchLeadHours = 2.0)
        {
            LeadHours = leadHours;
            MinBand = minBand;
            WindMs = windMs;
            LaunchMaxBand = launchMaxBand;
            LaunchMaxWindMs = launchMaxWindMs;
            LaunchMinVisibilityM = launchMinVisibilityM;
            LaunchLeadHours = launchLeadHours;
        }

        // How long before a threshold crossing the Approaching alert fires (alertLeadHours).
        internal double LeadHours { get; }
        // The band from which the site counts as hit (alertMinLevel).
        internal PrecipBand MinBand { get; }
        // The sustained wind from which the site counts as hit; zero or less switches the wind condition off (alertWindMs).
        internal float WindMs { get; }
        // The launch limits: the highest band, the highest wind, the lowest visibility, and how long they must hold ahead.
        internal PrecipBand LaunchMaxBand { get; }
        internal float LaunchMaxWindMs { get; }
        internal float LaunchMinVisibilityM { get; }
        internal double LaunchLeadHours { get; }

        // Whether a step hits the site under these rules.
        internal bool Hits(ForecastStep step)
        {
            if ((int)step.Band >= (int)MinBand) return true;
            return WindMs > 0f && step.WindMs >= WindMs;
        }
    }

    // Everything a forecast depends on besides the site, the time and the field, copied once on the main thread so that the compute path
    // reads no live setting. Immutable. The hash of it (RulesHash) goes into the cache key.
    internal sealed class ForecastContext
    {
        internal const double DefaultMapRangeKm = 400.0;
        internal const double DefaultTrackStepSeconds = 1800.0;
        // The wind of a provider that only answers "now" is carried forward and fades out over this many hours.
        internal const double PersistenceHours = 6.0;

        private readonly IWeatherSource[] windSources;

        // radiusM is the body's radius (0 or less: the report has no geometry, no tracks and no distances).
        internal ForecastContext(ForecastRules rules, double bodyRadiusM, double mapRangeKm = DefaultMapRangeKm,
            double trackStepSeconds = DefaultTrackStepSeconds, float uncertainty01 = 0f, WindSourceMode windSource = WindSourceMode.Auto,
            IWeatherSource[] windSources = null, AlertRules alerts = null, bool alertsOn = true)
        {
            if (rules == null) throw new ArgumentNullException("rules");
            Rules = rules;
            BodyRadiusM = bodyRadiusM;
            MapRangeKm = mapRangeKm;
            TrackStepSeconds = trackStepSeconds;
            Uncertainty01 = uncertainty01 < 0f ? 0f : uncertainty01 > 1f ? 1f : uncertainty01;
            WindSource = windSource;
            windSources = windSources ?? new IWeatherSource[0];
            var copy = new IWeatherSource[windSources.Length];
            Array.Copy(windSources, copy, copy.Length);
            // Highest priority first, equal priorities in the order given (a stable insertion sort).
            for (int i = 1; i < copy.Length; i++)
            {
                IWeatherSource s = copy[i];
                int j = i - 1;
                while (j >= 0 && copy[j].Priority < s.Priority)
                {
                    copy[j + 1] = copy[j];
                    j--;
                }
                copy[j + 1] = s;
            }
            this.windSources = copy;
            Alerts = alerts;
            AlertsOn = alertsOn;
            RulesHash = ComputeHash();
        }

        internal ForecastRules Rules { get; }
        internal double BodyRadiusM { get; }
        internal double MapRangeKm { get; }
        internal double TrackStepSeconds { get; }
        // Zero is exact (the default); above zero the displayed steps and tracks carry the deterministic error of ForecastUncertainty.
        internal float Uncertainty01 { get; }
        internal WindSourceMode WindSource { get; }
        // The providers the wind may come from, highest priority first.
        internal IWeatherSource[] WindSources { get { return windSources; } }
        // The thresholds of the alerts, the ETA search and the launch check (null: the defaults).
        internal AlertRules Alerts { get; }
        // Whether alerts are delivered (alertsEnabled): the reports list them and the service's tick raises them.
        internal bool AlertsOn { get; }
        // The hash of everything that changes a step or a track.
        internal ulong RulesHash { get; }

        internal bool HasGeometry { get { return BodyRadiusM > 0.0; } }

        // Whether the steps of an earlier report at the same times may be reused: not with the display-only error (its shift depends on the
        // issue time) and not with a provider whose "now" is carried forward (it depends on the issue time too).
        internal bool StepsReusable
        {
            get { return Uncertainty01 <= 0f && (WindSource == WindSourceMode.Ours || WindSource == WindSourceMode.Off || windSources.Length == 0); }
        }

        // A context with other alert rules; steps and tracks (and so the cache key) are the same.
        internal ForecastContext WithAlerts(AlertRules alerts, bool alertsOn = true)
        {
            return new ForecastContext(Rules, BodyRadiusM, MapRangeKm, TrackStepSeconds, Uncertainty01, WindSource, windSources, alerts, alertsOn);
        }

        private ulong ComputeHash()
        {
            ulong h = 0x1F3D5B79UL;
            h = ForecastMath.Mix(h, Rules.StepSeconds);
            h = ForecastMath.Mix(h, Rules.HorizonHours);
            h = ForecastMath.Mix(h, BodyRadiusM);
            h = ForecastMath.Mix(h, MapRangeKm);
            h = ForecastMath.Mix(h, TrackStepSeconds);
            h = ForecastMath.Mix(h, (double)Uncertainty01);
            h = ForecastMath.Mix(h, (long)WindSource);
            for (int i = 0; i < windSources.Length; i++)
            {
                IWeatherSource s = windSources[i];
                h = ForecastMath.Mix(h, (long)s.Priority);
                string name = s.Name ?? "";
                for (int c = 0; c < name.Length; c++) h = ForecastMath.Mix(h, (long)name[c]);
            }
            return h;
        }
    }
}
