using System;
using ReDefinition.Core;

namespace ReDefinition.Atmospheres.Forecast
{
    // The single function that turns what a field says at a place and time into a forecast step. The forecast calls it at later times;
    // a test calls the field at the same time and compares: the smooth level, the type and the visibility are the field's own numbers,
    // untouched. The shower pulse and the gust of the picture are display-only multipliers and never enter here; the wind a step reports
    // is the mean wind and the statistical gust (mean x (1 + depth)). No UnityEngine, any thread.
    internal static class ForecastSampling
    {
        // Samples the field at the site (the height above the ground goes in as the altitude [VERIFY with C25: the field's altitude
        // reference]). False where the field does not cover the body; the sample is then clear.
        internal static bool Sample(IWeatherField field, ForecastSite site, double ut, out WeatherSample sample)
        {
            return field.Sample(site.BodyIndex, site.LatDeg, site.LonDeg, site.AltAgl, ut, out sample);
        }

        // The statistical gust of a mean wind at a level.
        internal static float StatisticalGust(float windMs, float level01)
        {
            return windMs * (1f + WindNoise.Depth(level01));
        }

        // The step of a sample with a wind (east and north in m/s) and its gust.
        internal static ForecastStep BuildStep(ref WeatherSample sample, double ut, float eastMs, float northMs, float gustMs, float confidence01)
        {
            float level = float.IsNaN(sample.Level01) ? 0f : sample.Level01;
            float wind = (float)Math.Sqrt(eastMs * eastMs + northMs * northMs);
            var step = new ForecastStep();
            step.Ut = ut;
            step.Level01 = level;
            step.Band = PrecipScale.BandOf(level, wind);
            step.Type = sample.Type;
            step.Intensity01 = level;
            step.VisibilityM = sample.VisibilityM;
            step.WindMs = wind;
            step.WindFromDeg = FromDegrees(eastMs, northMs);
            step.GustMs = gustMs;
            step.Confidence01 = confidence01;
            step.CellCount = 0;
            return step;
        }

        // The direction the wind comes from, in degrees clockwise from north, for a wind vector that points where the air goes.
        internal static float FromDegrees(float eastMs, float northMs)
        {
            if (eastMs * eastMs + northMs * northMs < 1e-8f) return 0f;
            double to = Math.Atan2(eastMs, northMs) * ForecastMath.RadToDeg;
            double from = (to + 180.0) % 360.0;
            if (from < 0.0) from += 360.0;
            return (float)from;
        }

        // The wind of a field sample in the body-fixed frame (x east, y north).
        internal static void OursWind(ref WeatherSample sample, out float eastMs, out float northMs, out float gustMs)
        {
            eastMs = sample.WindWorld.x;
            northMs = sample.WindWorld.y;
            float wind = (float)Math.Sqrt(eastMs * eastMs + northMs * northMs);
            gustMs = StatisticalGust(wind, float.IsNaN(sample.Level01) ? 0f : sample.Level01);
        }
    }

    // Where the wind of a step comes from. It uses the semantics of the live wind (windSource): Auto lets a provider with a priority above
    // the field's own win where one answers, Ours keeps the field's wind, Foreign takes a provider's wind wherever any answers, Off gives
    // no wind. A provider that has a forecast gives it; one that only answers "now" is carried forward and blended into the field's wind,
    // with a confidence that falls with the lead time. The label says which.
    internal sealed class WindForecast
    {
        private readonly ForecastContext ctx;
        private readonly ForecastSite site;
        private readonly double startUt;
        private readonly IWeatherSource[] candidates;
        private IWeatherSource nowProvider;
        private float nowEast, nowNorth, nowGust;

        internal WindForecast(ForecastContext ctx, ForecastSite site, double startUt)
        {
            this.ctx = ctx;
            this.site = site;
            this.startUt = startUt;
            candidates = Candidates(ctx);
            for (int i = 0; i < candidates.Length && nowProvider == null; i++)
            {
                WeatherSample s;
                if (!candidates[i].TrySample(site.BodyIndex, site.LatDeg, site.LonDeg, site.AltAgl, startUt, out s)) continue;
                nowProvider = candidates[i];
                nowEast = s.WindWorld.x;
                nowNorth = s.WindWorld.y;
                float wind = (float)Math.Sqrt(nowEast * nowEast + nowNorth * nowNorth);
                nowGust = ForecastSampling.StatisticalGust(wind, s.Level01);
            }
        }

        // Whether every provider that can be asked is safe to call from a worker.
        internal bool ThreadSafe
        {
            get
            {
                for (int i = 0; i < candidates.Length; i++)
                {
                    if (!candidates[i].ThreadSafe) return false;
                }
                return true;
            }
        }

        internal static bool AllThreadSafe(ForecastContext ctx)
        {
            IWeatherSource[] list = Candidates(ctx);
            for (int i = 0; i < list.Length; i++)
            {
                if (!list[i].ThreadSafe) return false;
            }
            return true;
        }

        private static IWeatherSource[] Candidates(ForecastContext ctx)
        {
            if (ctx.WindSource == WindSourceMode.Ours || ctx.WindSource == WindSourceMode.Off) return new IWeatherSource[0];
            IWeatherSource[] all = ctx.WindSources;
            if (ctx.WindSource == WindSourceMode.Foreign || all.Length == 0) return all;
            // Auto: only a provider above the field's own priority (0) owns the wind.
            int count = 0;
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i].Priority > 0) count++;
            }
            var kept = new IWeatherSource[count];
            int at = 0;
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i].Priority > 0) kept[at++] = all[i];
            }
            return kept;
        }

        // Whether the last Resolve carried a provider's "now" forward: such a step depends on the issue time and is not reused.
        internal bool LastWasNowcast { get; private set; }

        // The wind at a time: the field's wind (east, north, gust) in, the wind of the step out.
        internal void Resolve(double ut, float oursEast, float oursNorth, float oursGust, out float east, out float north, out float gust,
            out float confidence, out WeatherSource source)
        {
            LastWasNowcast = false;
            if (ctx.WindSource == WindSourceMode.Off)
            {
                east = 0f;
                north = 0f;
                gust = 0f;
                confidence = 1f;
                source = WeatherSource.Ours();
                return;
            }
            for (int i = 0; i < candidates.Length; i++)
            {
                float e, n, g, c;
                if (!candidates[i].TryWindForecast(site, ut, out e, out n, out g, out c)) continue;
                east = e;
                north = n;
                gust = g;
                confidence = c < 0f ? 0f : c > 1f ? 1f : c;
                source = WeatherSource.Foreign(candidates[i].Name);
                return;
            }
            if (nowProvider != null)
            {
                double lead = ut - startUt;
                double w = lead <= 0.0 ? 1.0 : 1.0 - lead / (ForecastContext.PersistenceHours * 3600.0);
                if (w < 0.0) w = 0.0;
                east = (float)(w * nowEast + (1.0 - w) * oursEast);
                north = (float)(w * nowNorth + (1.0 - w) * oursNorth);
                gust = (float)(w * nowGust + (1.0 - w) * oursGust);
                confidence = (float)w;
                source = WeatherSource.ExternalNowcast(nowProvider.Name);
                LastWasNowcast = true;
                return;
            }
            east = oursEast;
            north = oursNorth;
            gust = oursGust;
            confidence = 1f;
            source = WeatherSource.Ours();
        }
    }
}
