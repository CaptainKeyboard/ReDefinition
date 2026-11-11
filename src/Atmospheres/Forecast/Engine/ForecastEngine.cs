using System;
using ReDefinition.Core;

namespace ReDefinition.Atmospheres.Forecast
{
    // The forecast engine: the live weather evaluated at later times. The field is a pure function of its arguments and the UT, so the
    // forecast of a site is the same function at the UTs of the steps (exact by construction); nothing is simulated and nothing is stored
    // but a cache of results. Steps, tracks, the ETA of the first crossing of a level and the nearest system are computed here; the
    // alerts are AlertEvaluator's. Pure and thread safe: no UnityEngine access in the compute path (the sample struct carries a Vector3
    // by value and nothing else of the engine), no static state, so any thread may call it where the field says ThreadSafe.
    // Technique reference for the shape of the report (an hourly strip with a summary line): the forecast panels of MSFS, yr.no and
    // windy.com.
    internal static class ForecastEngine
    {
        // The step of the coarse search for a crossing, in seconds; the bisection below it runs to one second.
        internal const double EtaCoarseSeconds = 300.0;

        // The forecast of a site over the horizon, hourly (or by rules.StepSeconds), from the field. The steps are on the grid of the
        // step; the first one is the grid time at or before startUt. Without the body's radius there are no tracks and no distances.
        internal static ForecastReport Compute(ForecastSite site, double startUt, double horizonHours, ForecastRules rules, IWeatherField field)
        {
            return Compute(site, startUt, horizonHours, new ForecastContext(rules, 0.0), field);
        }

        // The same with a context (the body's radius, map range, wind source, uncertainty, alert thresholds).
        internal static ForecastReport Compute(ForecastSite site, double startUt, double horizonHours, ForecastContext ctx, IWeatherField field)
        {
            return ComputeDetailed(site, startUt, horizonHours, ctx, field).Report;
        }

        // The report with the ETA of the alert level, the clearing time and the cache entry. prior (the cache entry of the same key) and
        // bodyTracks (the cached tracks of the body) make it cheaper; generation is WeatherHooks.ForecastGeneration at the start.
        internal static ForecastResult ComputeDetailed(ForecastSite site, double startUt, double horizonHours, ForecastContext ctx, IWeatherField field,
            ForecastCacheEntry prior = null, ForecastTrack[] bodyTracks = null, long generation = 0)
        {
            var job = new ChunkedCompute(site, startUt, horizonHours, ctx, field, prior, bodyTracks, generation);
            job.Advance(int.MaxValue);
            return job.Result;
        }

        // The tracks of all systems of a body, sampled every ctx.TrackStepSeconds from the bucket of startUt over the horizon.
        internal static ForecastTrack[] ComputeBodyTracks(int bodyIndex, double startUt, double horizonHours, ForecastContext ctx, IWeatherField field,
            ForecastTrack[] earlier = null)
        {
            var job = new BodyTrackCompute(bodyIndex, startUt, horizonHours, ctx, field, earlier);
            job.Advance(int.MaxValue);
            return job.Tracks;
        }

        // The first time in [fromUt, toUt] at which level(ut) is at or above the threshold, and the first time after that at which it is
        // below, each to one second: stepping by EtaCoarseSeconds, then bisection inside the bracket. A crossing shorter than the coarse
        // step can be missed. NaN where there is none.
        internal static EtaResult FindEvent(Func<double, float> level, double fromUt, double toUt, float threshold)
        {
            var search = new EtaSearch(level, fromUt, toUt, threshold);
            search.Advance(int.MaxValue);
            return search.Result;
        }
    }

    // The search of ForecastEngine.FindEvent as a state machine that takes one sample per unit, so that the main thread can run it in
    // pieces: the arrival is found by stepping EtaCoarseSeconds and bisecting the bracket to one second, then the clearing the same way.
    internal sealed class EtaSearch
    {
        private enum State
        {
            Start,
            Coarse,
            Bisect,
            Done
        }

        private readonly Func<double, float> level;
        private readonly double fromUt;
        private readonly double toUt;
        private readonly float threshold;
        private State state = State.Start;
        private bool wantAbove = true;
        private double prev, next, lo, hi;
        private EtaResult result = new EtaResult { ArrivalUt = double.NaN, ClearUt = double.NaN };

        internal EtaSearch(Func<double, float> level, double fromUt, double toUt, float threshold)
        {
            this.level = level;
            this.fromUt = fromUt;
            this.toUt = toUt;
            this.threshold = threshold;
        }

        internal bool Done { get { return state == State.Done; } }
        internal EtaResult Result { get { return result; } }

        // Takes up to that many samples; true when the search is finished.
        internal bool Advance(int samples)
        {
            for (int n = 0; n < samples && state != State.Done; n++)
            {
                switch (state)
                {
                    case State.Start:
                        if (level(fromUt) >= threshold)
                        {
                            result.ArrivalUt = fromUt;
                            result.ActiveAtStart = true;
                            wantAbove = false;
                        }
                        Begin(fromUt);
                        break;
                    case State.Coarse:
                    {
                        double at = Math.Min(next, toUt);
                        if ((level(at) >= threshold) == wantAbove)
                        {
                            lo = prev;
                            hi = at;
                            state = State.Bisect;
                        }
                        else if (at >= toUt)
                        {
                            state = State.Done;
                        }
                        else
                        {
                            prev = at;
                            next += ForecastEngine.EtaCoarseSeconds;
                        }
                        break;
                    }
                    case State.Bisect:
                        if (hi - lo <= 1.0)
                        {
                            Found(hi);
                        }
                        else
                        {
                            double mid = 0.5 * (lo + hi);
                            if ((level(mid) >= threshold) == wantAbove) hi = mid;
                            else lo = mid;
                        }
                        break;
                }
            }
            return state == State.Done;
        }

        private void Begin(double from)
        {
            prev = from;
            next = from + ForecastEngine.EtaCoarseSeconds;
            state = State.Coarse;
        }

        private void Found(double ut)
        {
            if (wantAbove)
            {
                result.ArrivalUt = ut;
                wantAbove = false;
                Begin(ut);
            }
            else
            {
                result.ClearUt = ut;
                state = State.Done;
            }
        }
    }
}
