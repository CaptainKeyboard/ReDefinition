using System;
using System.Collections.Generic;
using ReDefinition.Core;

namespace ReDefinition.Atmospheres.Forecast
{
    // The result of a site computation: the report, the entry the cache keeps, and the times of the first crossing of the alert level.
    internal sealed class ForecastResult
    {
        // What the player sees; with the display-only uncertainty on, its steps and tracks carry the error.
        internal ForecastReport Report;
        // The same report with the exact steps and tracks: the alerts, the ETA and the launch check are made from it. It is Report itself
        // where the uncertainty is off.
        internal ForecastReport AlertReport;
        internal ForecastCacheEntry Entry;
        // The first time at or after the start at which the smooth level reaches the alert level (the start itself where it already
        // does), to one second; NaN where it does not within the horizon. It is a level condition: the wind limit is not part of it.
        internal double ThresholdEtaUt = double.NaN;
        // The first time after that at which the level is below the alert level again; NaN where it stays within the horizon.
        internal double ClearUt = double.NaN;
        internal bool ActiveAtStart;
        // AlertRules/alerts-switch hash the report was made under (ForecastContext.AlertsHash).
        internal ulong AlertsHash;
        internal int ReusedSteps;
        internal int ComputedSteps;
    }

    // The first crossing of a level and the clearing after it.
    internal struct EtaResult
    {
        internal double ArrivalUt;
        internal double ClearUt;
        internal bool ActiveAtStart;
    }

    // A site computation that can be run in pieces. ForecastEngine.Compute runs it in one go on a worker; for a field that is not
    // ThreadSafe the service calls Advance on the main thread with a small budget per frame. A unit is at most one field sample (a step
    // of the report, a cell of the track search, a sample of the ETA search) or the assembly of the result, so no frame carries more than
    // the budget allows. The same code runs in both modes, so the two cannot differ.
    //   The steps are the field's exact numbers and are what the cache keeps. The display-only uncertainty is a second set of steps and
    //   tracks made from them for the report; the alerts and the ETA read the exact ones.
    internal sealed class ChunkedCompute
    {
        private enum Phase
        {
            Steps,
            Tracks,
            Shown,
            Eta,
            Assemble,
            Done
        }

        private readonly ForecastSite site;
        private readonly bool launchSite;
        private readonly double startUt;
        private readonly double horizonHours;
        private readonly ForecastContext ctx;
        private readonly IWeatherField field;
        private readonly ForecastTrack[] bodyTracks;
        private readonly long generation;
        private readonly long definitionId;
        private readonly ForecastCacheKey key;
        private readonly int stepCount;
        private readonly double step;
        private readonly double firstUt;
        private readonly ForecastStep[] steps;
        private readonly bool[] nowcast;
        private readonly bool[] have;
        private readonly ulong subject;
        private ForecastStep[] shown;
        private int shownNext;
        private WindForecast wind;
        private WeatherSource windLabel = WeatherSource.Ours();
        private bool haveLabel;
        private bool approximate;
        private int next;
        private Phase phase;
        private int reused, computed;
        private double[] trackTimes;
        private List<SystemPoint>[] found;
        private int trackNext;
        private bool scanning;
        private GridScan scan;
        private EtaSearch etaSearch;
        private ForecastTrack[] ownTracks;
        private ForecastResult result;

        // prior is the cache entry of the same key (or null): its steps at the same times and the same place are reused. bodyTracks are the
        // cached tracks of the body (or null: the tracks around the site are searched here). generation is ForecastGeneration at the start.
        internal ChunkedCompute(ForecastSite site, double startUt, double horizonHours, ForecastContext ctx, IWeatherField field,
            ForecastCacheEntry prior = null, ForecastTrack[] bodyTracks = null, long generation = 0, bool launchSite = false)
        {
            if (ctx == null) throw new ArgumentNullException("ctx");
            if (field == null) throw new ArgumentNullException("field");
            this.site = site;
            this.launchSite = launchSite;
            this.startUt = startUt;
            this.horizonHours = horizonHours;
            this.ctx = ctx;
            this.field = field;
            this.bodyTracks = bodyTracks;
            this.generation = generation;
            definitionId = field.DefinitionId;
            step = ctx.Rules.StepSeconds;
            if (!(step > 0.0)) throw new ArgumentException("The step must be positive.");
            stepCount = StepCountOf(horizonHours, step);
            firstUt = ForecastMath.StepIndex(startUt, step) * step;
            key = ForecastCacheKey.Of(site, ctx, definitionId, stepCount);
            subject = ForecastUncertainty.SiteSubject(key.Body, key.LatTenth, key.LonTenth);
            steps = new ForecastStep[stepCount];
            nowcast = new bool[stepCount];
            have = new bool[stepCount];
            if (prior != null && prior.Key.Equals(key) && prior.StepSeconds == step && prior.Steps != null && prior.LatDeg == site.LatDeg
                && prior.LonDeg == site.LonDeg && prior.AltAgl == site.AltAgl)
            {
                double shift = (firstUt - prior.FirstUt) / step;
                long whole = (long)Math.Round(shift);
                if (Math.Abs(shift - whole) < 1e-9)
                {
                    for (int i = 0; i < stepCount; i++)
                    {
                        long j = i + whole;
                        if (j < 0 || j >= prior.Steps.Length) continue;
                        if (prior.Nowcast != null && prior.Nowcast[j]) continue;
                        steps[i] = prior.Steps[j];
                        have[i] = true;
                        reused++;
                    }
                    if (reused > 0)
                    {
                        windLabel = prior.Wind;
                        haveLabel = true;
                    }
                }
            }
            phase = Phase.Steps;
            Skip();
        }

        // The number of steps of a horizon: 24 for 24 hours of hourly steps.
        internal static int StepCountOf(double horizonHours, double stepSeconds)
        {
            return Math.Max(1, (int)Math.Ceiling(horizonHours * 3600.0 / stepSeconds - 1e-9));
        }

        internal bool Done { get { return phase == Phase.Done; } }
        internal ForecastResult Result { get { return result; } }
        internal ForecastReport Report { get { return result == null ? null : result.Report; } }
        internal int ReusedSteps { get { return reused; } }
        internal int ComputedSteps { get { return computed; } }
        internal ForecastCacheKey Key { get { return key; } }
        internal long Generation { get { return generation; } }

        // Whether the compute path may run on a worker: the field and every wind provider say ThreadSafe.
        internal bool ThreadSafe
        {
            get { return field.ThreadSafe && WindForecast.AllThreadSafe(ctx); }
        }

        // Runs up to that many units; true when the computation is finished.
        internal bool Advance(int units)
        {
            while (units > 0 && phase != Phase.Done)
            {
                switch (phase)
                {
                    case Phase.Steps:
                        steps[next] = ComputeStep(next, false);
                        have[next] = true;
                        computed++;
                        next++;
                        Skip();
                        break;
                    case Phase.Tracks:
                        TrackUnit();
                        break;
                    case Phase.Shown:
                        shown[shownNext] = ComputeStep(shownNext, true);
                        shownNext++;
                        if (shownNext >= stepCount) StartEta();
                        break;
                    case Phase.Eta:
                        if (etaSearch.Advance(1))
                        {
                            EtaDone();
                            phase = Phase.Assemble;
                        }
                        break;
                    case Phase.Assemble:
                        Assemble();
                        phase = Phase.Done;
                        break;
                }
                units--;
            }
            return phase == Phase.Done;
        }

        // Moves past reused steps and, at the end of the steps, into the next phase.
        private void Skip()
        {
            while (next < stepCount && have[next]) next++;
            if (next < stepCount) return;
            if (bodyTracks == null && ctx.HasGeometry)
            {
                double trackStep = ctx.TrackStepSeconds;
                long bucket = ForecastMath.StepIndex(startUt, trackStep);
                int count = (int)Math.Ceiling(horizonHours * 3600.0 / trackStep - 1e-9) + 1;
                trackTimes = new double[count];
                found = new List<SystemPoint>[count];
                for (int i = 0; i < count; i++) trackTimes[i] = (bucket + i) * trackStep;
                scan = GridScan.Local(field, site.BodyIndex, ctx.BodyRadiusM, site.LatDeg, site.LonDeg, site.AltAgl, ctx.MapRangeM * 1.25, 12);
                phase = Phase.Tracks;
            }
            else
            {
                AfterTracks();
            }
        }

        private void AfterTracks()
        {
            if (ctx.Uncertainty01 > 0f)
            {
                shown = new ForecastStep[stepCount];
                shownNext = 0;
                phase = Phase.Shown;
            }
            else
            {
                StartEta();
            }
        }

        // One step: the field's exact numbers, or (shifted) what an issued forecast with the display-only error shows at that time.
        private ForecastStep ComputeStep(int i, bool shifted)
        {
            double ut = firstUt + i * step;
            double lead = ut - startUt;
            double sampleUt = ut;
            float confidence = 1f;
            if (shifted)
            {
                ulong issue = ForecastUncertainty.IssueKey(subject, startUt);
                sampleUt = ut + ForecastUncertainty.TimingShiftSeconds(issue, ctx.Uncertainty01, lead);
                confidence = ForecastUncertainty.Confidence(issue, ctx.Uncertainty01, lead);
            }
            WeatherSample sample;
            // A field that does not cover the body, or answers with an estimate, gives no forecast.
            if (!ForecastSampling.Sample(field, site, sampleUt, out sample) || sample.Approximate || sample.Readiness != FieldReadiness.Ready) approximate = true;
            float east, north, gust;
            ForecastSampling.OursWind(ref sample, out east, out north, out gust);
            if (wind == null) wind = new WindForecast(ctx, site, startUt);
            float e2, n2, g2, c2;
            WeatherSource source;
            wind.Resolve(ut, east, north, gust, out e2, out n2, out g2, out c2, out source);
            if (!shifted)
            {
                nowcast[i] = wind.LastWasNowcast;
                if (!haveLabel)
                {
                    windLabel = source;
                    haveLabel = true;
                }
            }
            return ForecastSampling.BuildStep(ref sample, ut, e2, n2, g2, Math.Min(confidence, c2));
        }

        // One unit of the track search: the systems the field lists at a time (no sample), or one cell of the local grid.
        private void TrackUnit()
        {
            int i = trackNext;
            if (!scanning)
            {
                var list = new List<SystemPoint>();
                var source = field as IWeatherSystemSource;
                found[i] = list;
                if (source != null && source.TryGetSystems(site.BodyIndex, trackTimes[i], list))
                {
                    NextTrackTime();
                    return;
                }
                list.Clear();
                scan.Begin(trackTimes[i]);
                scanning = true;
            }
            if (scan.FillNext())
            {
                scan.Collect(found[i]);
                scanning = false;
                NextTrackTime();
            }
        }

        private void NextTrackTime()
        {
            trackNext++;
            if (trackNext < trackTimes.Length) return;
            ownTracks = SystemTracker.Build(trackTimes, found, site.BodyIndex, ctx.BodyRadiusM, scan.SpacingM, ctx.MaxSystemSpeedMs);
            found = null;
            AfterTracks();
        }

        private void StartEta()
        {
            AlertRules rules = ctx.Alerts ?? AlertRules.Default;
            float threshold = PrecipScale.StartOf(rules.MinBand);
            etaSearch = new EtaSearch(LevelAt, startUt, startUt + horizonHours * 3600.0, threshold);
            phase = Phase.Eta;
        }

        private void EtaDone()
        {
            EtaResult eta = etaSearch.Result;
            result = new ForecastResult();
            result.ThresholdEtaUt = eta.ArrivalUt;
            result.ClearUt = eta.ClearUt;
            result.ActiveAtStart = eta.ActiveAtStart;
        }

        // The exact level at a time; the ETA search reads it.
        private float LevelAt(double ut)
        {
            WeatherSample s;
            ForecastSampling.Sample(field, site, ut, out s);
            return float.IsNaN(s.Level01) ? 0f : s.Level01;
        }

        private ForecastReport NewReport(ForecastStep[] reportSteps, ForecastTrack[] tracks, bool available)
        {
            var report = new ForecastReport();
            report.BodyIndex = site.BodyIndex;
            report.IssuedUt = startUt;
            report.StepSeconds = step;
            // Data that is an estimate (the field is catching up or seeding) is not a forecast: "unavailable", and nothing is cached.
            report.Available = available;
            report.Generation = generation;
            report.Wind = windLabel;
            report.Steps = reportSteps;
            report.Tracks = tracks;
            return report;
        }

        private void Assemble()
        {
            var entrySteps = new ForecastStep[stepCount];
            Array.Copy(steps, entrySteps, stepCount);
            var entry = new ForecastCacheEntry();
            entry.Key = key;
            entry.FirstUt = firstUt;
            entry.StepSeconds = step;
            entry.Steps = entrySteps;
            entry.LatDeg = site.LatDeg;
            entry.LonDeg = site.LonDeg;
            entry.AltAgl = site.AltAgl;
            entry.Nowcast = nowcast;
            entry.Wind = windLabel;

            ForecastTrack[] exactTracks = new ForecastTrack[0];
            ForecastTrack[] shownTracks = exactTracks;
            if (ctx.HasGeometry)
            {
                double range = ctx.MapRangeM;
                ForecastTrack[] source = bodyTracks ?? ownTracks ?? new ForecastTrack[0];
                exactTracks = SystemTracker.ClipToRange(source, site.LatDeg, site.LonDeg, range, ctx.BodyRadiusM);
                shownTracks = exactTracks;
                if (ctx.Uncertainty01 > 0f)
                {
                    shownTracks = SystemTracker.ClipToRange(source, site.LatDeg, site.LonDeg, range, ctx.BodyRadiusM);
                    ForecastUncertainty.ShiftTracks(shownTracks, ctx.Uncertainty01, startUt, ctx.BodyRadiusM);
                }
            }

            ForecastStep[] shownSteps = ctx.Uncertainty01 > 0f ? shown : steps;
            CountCells(steps, exactTracks);
            if (ctx.Uncertainty01 > 0f) CountCells(shownSteps, shownTracks);

            ForecastReport exact = NewReport(steps, exactTracks, !approximate);
            ForecastReport display = ctx.Uncertainty01 > 0f ? NewReport(shownSteps, shownTracks, !approximate) : exact;
            if (ctx.HasGeometry)
            {
                double eta, distance;
                SystemTracker.Nearest(shownTracks, site.LatDeg, site.LonDeg, startUt, ctx.BodyRadiusM, out eta, out distance);
                display.NearestSystemEtaUt = eta;
                display.NearestSystemDistanceM = distance;
                if (!ReferenceEquals(display, exact))
                {
                    SystemTracker.Nearest(exactTracks, site.LatDeg, site.LonDeg, startUt, ctx.BodyRadiusM, out eta, out distance);
                }
                exact.NearestSystemEtaUt = eta;
                exact.NearestSystemDistanceM = distance;
            }
            if (ctx.Alerts != null && ctx.AlertsOn)
            {
                exact.Alerts = AlertEvaluator.Preview(site, launchSite, exact, ctx.Alerts, startUt, ctx.BodyRadiusM);
                display.Alerts = exact.Alerts;
            }
            result.Report = display;
            result.AlertReport = exact;
            result.Entry = approximate ? null : entry;
            result.AlertsHash = ctx.AlertsHash;
            result.ReusedSteps = reused;
            result.ComputedSteps = computed;
        }

        // The number of systems that cover the site in each step.
        private void CountCells(ForecastStep[] list, ForecastTrack[] tracks)
        {
            for (int i = 0; i < list.Length; i++)
            {
                list[i].CellCount = ctx.HasGeometry ? SystemTracker.CountCovering(tracks, site.LatDeg, site.LonDeg, list[i].Ut, ctx.BodyRadiusM) : 0;
            }
        }
    }

    // The tracks of a whole body, in pieces like ChunkedCompute: at most one field sample per unit. The systems come from the field when
    // it lists them (IWeatherSystemSource), else from a coarse global scan; the keys are inherited from the earlier set of the body.
    internal sealed class BodyTrackCompute
    {
        private readonly int bodyIndex;
        private readonly ForecastContext ctx;
        private readonly IWeatherField field;
        private readonly ForecastTrack[] earlier;
        private readonly double[] times;
        private readonly List<SystemPoint>[] found;
        private readonly long generation;
        private readonly GridScan scan;
        private readonly IWeatherSystemSource source;
        private bool scanning;
        private int next;

        internal BodyTrackCompute(int bodyIndex, double startUt, double horizonHours, ForecastContext ctx, IWeatherField field, ForecastTrack[] earlier = null,
            long generation = 0)
        {
            if (ctx == null) throw new ArgumentNullException("ctx");
            if (field == null) throw new ArgumentNullException("field");
            if (!ctx.HasGeometry) throw new ArgumentException("A body needs its radius for tracks.");
            this.bodyIndex = bodyIndex;
            this.ctx = ctx;
            this.field = field;
            this.earlier = earlier;
            this.generation = generation;
            Bucket = ForecastCache.BucketOf(startUt, ctx.TrackStepSeconds);
            int count = (int)Math.Ceiling(horizonHours * 3600.0 / ctx.TrackStepSeconds - 1e-9) + 1;
            times = new double[count];
            found = new List<SystemPoint>[count];
            for (int i = 0; i < count; i++) times[i] = (Bucket + i) * ctx.TrackStepSeconds;
            scan = GridScan.Global(field, bodyIndex, ctx.BodyRadiusM, 0.0);
            source = field as IWeatherSystemSource;
        }

        internal long Bucket { get; }
        internal long Generation { get { return generation; } }
        internal long DefinitionId { get { return field.DefinitionId; } }
        internal int BodyIndex { get { return bodyIndex; } }
        internal ForecastTrack[] Tracks { get; private set; }
        internal bool Done { get { return Tracks != null; } }

        internal bool ThreadSafe { get { return field.ThreadSafe; } }

        internal bool Advance(int units)
        {
            while (units > 0 && Tracks == null)
            {
                if (!scanning)
                {
                    var list = new List<SystemPoint>();
                    found[next] = list;
                    if (source != null && source.TryGetSystems(bodyIndex, times[next], list))
                    {
                        NextTime();
                        units--;
                        continue;
                    }
                    list.Clear();
                    scan.Begin(times[next]);
                    scanning = true;
                }
                if (scan.FillNext())
                {
                    scan.Collect(found[next]);
                    scanning = false;
                    NextTime();
                }
                units--;
            }
            return Tracks != null;
        }

        private void NextTime()
        {
            next++;
            if (next < times.Length) return;
            double gate = scan.SpacingM;
            ForecastTrack[] built = SystemTracker.Build(times, found, bodyIndex, ctx.BodyRadiusM, gate, ctx.MaxSystemSpeedMs);
            SystemTracker.InheritKeys(earlier, built, ctx.BodyRadiusM, gate);
            Tracks = built;
        }
    }
}
