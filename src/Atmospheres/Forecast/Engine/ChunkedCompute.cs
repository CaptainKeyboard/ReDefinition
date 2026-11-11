using System;
using System.Collections.Generic;
using ReDefinition.Core;

namespace ReDefinition.Atmospheres.Forecast
{
    // The result of a site computation: the report, the entry the cache keeps, and the times of the first crossing of the alert level.
    internal sealed class ForecastResult
    {
        internal ForecastReport Report;
        internal ForecastCacheEntry Entry;
        // The first time at or after the start at which the smooth level reaches the alert level (the start itself where it already
        // does), to one second; NaN where it does not within the horizon.
        internal double ThresholdEtaUt = double.NaN;
        // The first time after that at which the level is below the alert level again; NaN where it stays within the horizon.
        internal double ClearUt = double.NaN;
        internal bool ActiveAtStart;
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
    // ThreadSafe the module calls Advance with one unit per call on the main thread, so no frame carries more than a step or a track
    // sample. A unit is one step of the report, one time of the track search, the ETA search, or the assembly. The same code runs in
    // both modes, so the two cannot differ.
    internal sealed class ChunkedCompute
    {
        private enum Phase
        {
            Steps,
            Tracks,
            Eta,
            Assemble,
            Done
        }

        private readonly ForecastSite site;
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
        private readonly bool[] have;
        private readonly ulong subject;
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
        private GridScan scan;
        private int scanRow;
        private EtaSearch etaSearch;
        private ForecastTrack[] ownTracks;
        private ForecastResult result;

        // prior is the cache entry of the same key (or null): its steps at the same times are reused. bodyTracks are the cached tracks of
        // the body (or null: the tracks around the site are searched here). generation is ForecastGeneration at the start.
        internal ChunkedCompute(ForecastSite site, double startUt, double horizonHours, ForecastContext ctx, IWeatherField field,
            ForecastCacheEntry prior = null, ForecastTrack[] bodyTracks = null, long generation = 0)
        {
            if (ctx == null) throw new ArgumentNullException("ctx");
            if (field == null) throw new ArgumentNullException("field");
            this.site = site;
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
            have = new bool[stepCount];
            if (prior != null && ctx.StepsReusable && prior.StepSeconds == step && prior.Steps != null)
            {
                double shift = (firstUt - prior.FirstUt) / step;
                long whole = (long)Math.Round(shift);
                if (Math.Abs(shift - whole) < 1e-9)
                {
                    for (int i = 0; i < stepCount; i++)
                    {
                        long j = i + whole;
                        if (j < 0 || j >= prior.Steps.Length) continue;
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
                        steps[next] = ComputeStep(next);
                        have[next] = true;
                        computed++;
                        next++;
                        Skip();
                        break;
                    case Phase.Tracks:
                        TrackUnit();
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
                scan = GridScan.Local(field, site.BodyIndex, ctx.BodyRadiusM, site.LatDeg, site.LonDeg, site.AltAgl, ctx.MapRangeKm * 1000.0 * 1.25, 12);
                phase = Phase.Tracks;
            }
            else
            {
                StartEta();
            }
        }

        private ForecastStep ComputeStep(int i)
        {
            double ut = firstUt + i * step;
            double lead = ut - startUt;
            double sampleUt = ut;
            float confidence = 1f;
            if (ctx.Uncertainty01 > 0f)
            {
                ulong issue = ForecastUncertainty.IssueKey(subject, startUt);
                sampleUt = ut + ForecastUncertainty.TimingShiftSeconds(issue, ctx.Uncertainty01, lead);
                confidence = ForecastUncertainty.Confidence(issue, ctx.Uncertainty01, lead);
            }
            WeatherSample sample;
            ForecastSampling.Sample(field, site, sampleUt, out sample);
            if (sample.Approximate || sample.Readiness != FieldReadiness.Ready) approximate = true;
            float east, north, gust;
            ForecastSampling.OursWind(ref sample, out east, out north, out gust);
            if (wind == null) wind = new WindForecast(ctx, site, startUt);
            float e2, n2, g2, c2;
            WeatherSource source;
            wind.Resolve(ut, east, north, gust, out e2, out n2, out g2, out c2, out source);
            if (!haveLabel)
            {
                windLabel = source;
                haveLabel = true;
            }
            return ForecastSampling.BuildStep(ref sample, ut, e2, n2, g2, Math.Min(confidence, c2));
        }

        // One unit of the track search: the systems the field lists at a time (no sample), or one row of the local grid.
        private void TrackUnit()
        {
            int i = trackNext;
            if (scanRow == 0)
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
            }
            scan.FillRow(scanRow++);
            if (scanRow >= scan.Height)
            {
                scan.Collect(found[i]);
                scanRow = 0;
                NextTrackTime();
            }
        }

        private void NextTrackTime()
        {
            trackNext++;
            if (trackNext < trackTimes.Length) return;
            ownTracks = SystemTracker.Build(trackTimes, found, site.BodyIndex, ctx.BodyRadiusM, scan.SpacingM);
            found = null;
            StartEta();
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

        // The level the report shows at a time (with the display-only shift where the uncertainty is on); the engine's ETA search reads it.
        private float LevelAt(double ut)
        {
            double at = ut;
            if (ctx.Uncertainty01 > 0f)
            {
                ulong issue = ForecastUncertainty.IssueKey(subject, startUt);
                at = ut + ForecastUncertainty.TimingShiftSeconds(issue, ctx.Uncertainty01, ut - startUt);
            }
            WeatherSample s;
            ForecastSampling.Sample(field, site, at, out s);
            return float.IsNaN(s.Level01) ? 0f : s.Level01;
        }

        private void Assemble()
        {
            var report = new ForecastReport();
            report.BodyIndex = site.BodyIndex;
            report.IssuedUt = startUt;
            report.StepSeconds = step;
            // Data that is an estimate (the field is catching up or seeding) is not a forecast: "unavailable", and nothing is cached.
            report.Available = !approximate;
            report.Generation = generation;
            report.Wind = windLabel;

            var entrySteps = new ForecastStep[stepCount];
            Array.Copy(steps, entrySteps, stepCount);
            var entry = new ForecastCacheEntry();
            entry.Key = key;
            entry.FirstUt = firstUt;
            entry.StepSeconds = step;
            entry.Steps = entrySteps;
            entry.Wind = windLabel;

            ForecastTrack[] tracks = new ForecastTrack[0];
            if (ctx.HasGeometry)
            {
                double range = ctx.MapRangeKm * 1000.0;
                ForecastTrack[] source = bodyTracks ?? ownTracks ?? new ForecastTrack[0];
                tracks = SystemTracker.ClipToRange(source, site.LatDeg, site.LonDeg, range, ctx.BodyRadiusM);
                ForecastUncertainty.ShiftTracks(tracks, ctx.Uncertainty01, startUt, ctx.BodyRadiusM);
                for (int i = 0; i < stepCount; i++)
                {
                    steps[i].CellCount = SystemTracker.CountCovering(tracks, site.LatDeg, site.LonDeg, steps[i].Ut, ctx.BodyRadiusM);
                }
                double eta, distance;
                SystemTracker.Nearest(tracks, site.LatDeg, site.LonDeg, startUt, ctx.BodyRadiusM, out eta, out distance);
                report.NearestSystemEtaUt = eta;
                report.NearestSystemDistanceM = distance;
            }
            else
            {
                for (int i = 0; i < stepCount; i++) steps[i].CellCount = 0;
            }
            report.Steps = steps;
            report.Tracks = tracks;
            if (ctx.Alerts != null && ctx.AlertsOn)
            {
                report.Alerts = AlertEvaluator.Preview(site, false, report, ctx.Alerts, startUt, ctx.BodyRadiusM);
            }
            result.Report = report;
            result.Entry = approximate ? null : entry;
            result.ReusedSteps = reused;
            result.ComputedSteps = computed;
        }

        internal ForecastCacheKey Key { get { return key; } }
        internal long Generation { get { return generation; } }
    }

    // The tracks of a whole body, in pieces like ChunkedCompute: one time of the search per unit. The systems come from the field when it
    // lists them (IWeatherSystemSource), else from a coarse global scan; the keys are inherited from the earlier set of the body.
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
        private int row;
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
                if (row == 0)
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
                }
                scan.FillRow(row++);
                if (row >= scan.Height)
                {
                    scan.Collect(found[next]);
                    row = 0;
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
            ForecastTrack[] built = SystemTracker.Build(times, found, bodyIndex, ctx.BodyRadiusM, gate);
            SystemTracker.InheritKeys(earlier, built, ctx.BodyRadiusM, gate);
            Tracks = built;
        }
    }
}
