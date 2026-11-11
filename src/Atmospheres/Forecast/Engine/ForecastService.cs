using System;
using System.Collections.Generic;
using System.Threading;
using ReDefinition.Core;

namespace ReDefinition.Atmospheres.Forecast
{
    // What the service needs from the game, behind an interface so that the whole service runs in a test without the game.
    internal interface IForecastEnvironment
    {
        // The field every forecast is made from.
        IWeatherField Field { get; }
        // The universal time now.
        double NowUt { get; }
        // Whether the forecast is switched on (forecastEnabled and weatherEffects).
        bool Enabled { get; }
        // The context of a body (rules, radius, wind sources, uncertainty, alert thresholds); null where the body has no forecast.
        ForecastContext ContextFor(int bodyIndex);
    }

    // The forecast of the running game: the per-site request queue, one background task for all sites, the rolling cache, the slow tick
    // and the alert tick. The module wraps it and assigns the seam; the service itself touches no game type.
    //   Main thread: ForecastOf / BodyTracks (the seam), Tick, Pump, Watch. They read the last complete report and queue work; they never
    //   compute (a cache read and a queue insert, well under 0.1 ms).
    //   Worker: one task drains the queue, each request is a ChunkedCompute (and a BodyTrackCompute where the body's tracks are not
    //   cached); the finished report is swapped in as a volatile reference. A field that is not ThreadSafe (or a wind provider that is
    //   not) is computed on the main thread instead, one unit per Pump call.
    // A request copies the context on the main thread; the compute path reads no live setting.
    internal sealed class ForecastService
    {
        // The slow tick: this much real time and this much game time, whichever is later.
        internal const double SlowTickRealSeconds = 2.0;
        internal const double SlowTickGameSeconds = 300.0;
        // A site nobody asked for or watches for this long is forgotten.
        internal const double SiteIdleSeconds = 600.0;
        // After a failed computation the site is not tried again for this long.
        internal const double RetrySeconds = 30.0;

        private sealed class SiteEntry
        {
            internal ForecastSite Site;
            internal bool Watched;
            internal bool LaunchSite;
            internal volatile ForecastReport Latest;
            internal volatile ForecastResult LatestResult;
            internal volatile bool Pending;
            internal double LastAskedClock;
            internal double RetryClock = double.NegativeInfinity;
            internal AlertTick LastTick;
        }

        private sealed class Request
        {
            internal bool TracksOnly;
            internal ForecastSite Site;
            internal ulong SiteId;
            internal int BodyIndex;
            internal double StartUt;
            internal double HorizonHours;
            internal ForecastContext Context;
            internal IWeatherField Field;
            internal long Generation;
        }

        private sealed class Run
        {
            private readonly Request request;
            private readonly ForecastCache cache;
            private BodyTrackCompute tracksJob;
            private ChunkedCompute siteJob;
            private ForecastTrack[] bodyTracks;
            private int stage;

            internal Run(Request request, ForecastCache cache)
            {
                this.request = request;
                this.cache = cache;
            }

            internal Request Request { get { return request; } }
            internal ForecastResult Result { get; private set; }
            internal ForecastTrack[] Tracks { get; private set; }
            internal long TracksBucket { get; private set; }
            internal bool Done { get; private set; }

            internal bool ThreadSafe
            {
                get { return request.Field.ThreadSafe && WindForecast.AllThreadSafe(request.Context); }
            }

            internal bool Advance(int units)
            {
                for (int u = 0; u < units && !Done; u++)
                {
                    if (stage == 0)
                    {
                        ForecastContext ctx = request.Context;
                        long def = request.Field.DefinitionId;
                        if (ctx.HasGeometry)
                        {
                            long bucket = ForecastCache.BucketOf(request.StartUt, ctx.TrackStepSeconds);
                            bodyTracks = cache.FindTracks(request.BodyIndex, ctx, def, bucket);
                            if (bodyTracks == null)
                            {
                                tracksJob = new BodyTrackCompute(request.BodyIndex, request.StartUt, request.HorizonHours, ctx, request.Field,
                                    cache.FindEarlierTracks(request.BodyIndex, ctx, def, bucket), request.Generation);
                            }
                        }
                        stage = 1;
                        continue;
                    }
                    if (stage == 1)
                    {
                        if (tracksJob != null)
                        {
                            if (!tracksJob.Advance(1)) continue;
                            bodyTracks = tracksJob.Tracks;
                            Tracks = bodyTracks;
                            TracksBucket = tracksJob.Bucket;
                            cache.StoreTracks(request.BodyIndex, request.Context, request.Field.DefinitionId, tracksJob.Bucket, bodyTracks, request.Generation);
                            tracksJob = null;
                        }
                        if (request.TracksOnly)
                        {
                            Done = true;
                            continue;
                        }
                        ForecastContext ctx = request.Context;
                        int count = ChunkedCompute.StepCountOf(request.HorizonHours, ctx.Rules.StepSeconds);
                        ForecastCacheKey key = ForecastCacheKey.Of(request.Site, ctx, request.Field.DefinitionId, count);
                        siteJob = new ChunkedCompute(request.Site, request.StartUt, request.HorizonHours, ctx, request.Field, cache.Find(key), bodyTracks,
                            request.Generation);
                        stage = 2;
                        continue;
                    }
                    if (siteJob.Advance(1))
                    {
                        Result = siteJob.Result;
                        if (Result.Entry != null)
                        {
                            cache.Store(Result.Entry, request.Generation);
                            cache.CountSteps(Result.ReusedSteps, Result.ComputedSteps);
                        }
                        Done = true;
                    }
                }
                return Done;
            }
        }

        private readonly IForecastEnvironment env;
        private readonly ForecastCache cache;
        private readonly AlertEvaluator evaluator;
        private readonly Action<ForecastAlert> raise;
        private readonly Action<string> warn;
        private readonly object gate = new object();
        private readonly Dictionary<ulong, SiteEntry> sites = new Dictionary<ulong, SiteEntry>();
        private readonly Queue<Request> workerQueue = new Queue<Request>();
        private readonly Queue<Request> mainQueue = new Queue<Request>();
        private readonly Dictionary<int, ForecastTrack[]> lastTracks = new Dictionary<int, ForecastTrack[]>();
        private readonly HashSet<int> tracksPending = new HashSet<int>();
        private readonly Dictionary<int, ulong> rulesByBody = new Dictionary<int, ulong>();
        private Run mainRun;
        private int workerActive;
        private double clock;
        private double slowReal;
        private double lastSlowUt = double.NaN;
        private long lastDefinitionId = long.MinValue;
        private bool available;
        private bool warnedFailure;
        private volatile Exception lastFailure;

        // raise delivers an alert (WeatherHooks.RaiseAlert in the game); warn gets one line for a failure. Both may be null.
        internal ForecastService(IForecastEnvironment env, ForecastCache cache = null, Action<ForecastAlert> raise = null, Action<string> warn = null)
        {
            if (env == null) throw new ArgumentNullException("env");
            this.env = env;
            this.cache = cache ?? new ForecastCache();
            this.raise = raise;
            this.warn = warn;
            evaluator = new AlertEvaluator(AlertRules.Default);
        }

        internal ForecastCache Cache { get { return cache; } }
        internal AlertEvaluator Evaluator { get { return evaluator; } }
        // Whether forecasts are served: the service is enabled and the field is Ready. The module assigns the seam only then.
        internal bool Available { get { return available; } }
        internal Exception LastFailure { get { return lastFailure; } }

        // The number of requests not yet finished.
        internal int PendingCount
        {
            get
            {
                lock (gate)
                {
                    return workerQueue.Count + mainQueue.Count + (mainRun != null ? 1 : 0) + (Volatile.Read(ref workerActive) != 0 ? 1 : 0);
                }
            }
        }

        // --- the seam ---

        // WeatherHooks.Forecast: the last complete report of the site; a placeholder (Available false) while the first one is computed.
        // Asking watches the site for a while; a report that is a step old or from before a generation bump is refreshed in the background.
        internal ForecastReport ForecastOf(ForecastSite site)
        {
            if (!available) return Unavailable(site, 0.0);
            ForecastContext ctx = env.ContextFor(site.BodyIndex);
            if (ctx == null) return Unavailable(site, 0.0);
            NoteRules(site.BodyIndex, ctx);
            SiteEntry entry = EntryOf(site, true);
            entry.LastAskedClock = clock;
            ForecastReport latest = entry.Latest;
            double now = env.NowUt;
            long generation = cache.Generation;
            bool current = latest != null && latest.Generation == generation;
            if (!current || !latest.Available || now >= latest.IssuedUt + ctx.Rules.StepSeconds || now < latest.IssuedUt) Enqueue(entry, ctx);
            if (current) return latest;
            return Unavailable(site, now);
        }

        // WeatherHooks.BodyTracks: the cached tracks of the body (the last ones where the current bucket is not there yet); null while none.
        internal ForecastTrack[] BodyTracks(int bodyIndex)
        {
            if (!available) return null;
            ForecastContext ctx = env.ContextFor(bodyIndex);
            if (ctx == null || !ctx.HasGeometry) return null;
            IWeatherField field = env.Field;
            long bucket = ForecastCache.BucketOf(env.NowUt, ctx.TrackStepSeconds);
            ForecastTrack[] tracks = cache.FindTracks(bodyIndex, ctx, field.DefinitionId, bucket);
            if (tracks != null)
            {
                lock (gate)
                {
                    lastTracks[bodyIndex] = tracks;
                }
                return tracks;
            }
            EnqueueTracks(bodyIndex, ctx);
            lock (gate)
            {
                ForecastTrack[] last;
                return lastTracks.TryGetValue(bodyIndex, out last) ? last : null;
            }
        }

        // The ETA of the alert level, the clearing time and the like for the last report of a site; null where there is none.
        internal ForecastResult DetailOf(ForecastSite site)
        {
            SiteEntry entry = EntryOf(site, false);
            return entry == null ? null : entry.LatestResult;
        }

        // The last alert tick of a site (the first event time for a warp stop); null where there was none.
        internal AlertTick LastAlertTick(ForecastSite site)
        {
            SiteEntry entry = EntryOf(site, false);
            return entry == null ? null : entry.LastTick;
        }

        // Puts a site on the alert list (a launch site also gets LaunchWindow alerts); it is kept up to date until Unwatch.
        internal void Watch(ForecastSite site, bool launchSite)
        {
            SiteEntry entry = EntryOf(site, true);
            entry.Watched = true;
            entry.LaunchSite = launchSite;
        }

        internal void Unwatch(ForecastSite site)
        {
            SiteEntry entry = EntryOf(site, false);
            if (entry != null) entry.Watched = false;
        }

        // The launch check for a site from its last report.
        internal LaunchAssessment AssessLaunch(ForecastSite site)
        {
            SiteEntry entry = EntryOf(site, false);
            ForecastContext ctx = env.ContextFor(site.BodyIndex);
            AlertRules rules = ctx != null && ctx.Alerts != null ? ctx.Alerts : AlertRules.Default;
            return LaunchConditions.Assess(entry == null ? null : entry.Latest, env.NowUt, rules);
        }

        // --- the tick ---

        // Once per frame: the readiness, the inputs, the work on the main thread, and every so often the slow tick.
        internal void Tick(double realDt)
        {
            IWeatherField field = env.Field;
            bool ready = env.Enabled && field != null && !(field is NullField) && field.Readiness == FieldReadiness.Ready;
            if (!ready)
            {
                available = false;
                return;
            }
            available = true;
            long def = field.DefinitionId;
            if (def != lastDefinitionId)
            {
                if (lastDefinitionId != long.MinValue) WeatherHooks.BumpForecastGeneration();
                lastDefinitionId = def;
            }
            clock += realDt;
            slowReal += realDt;
            Pump();
            double now = env.NowUt;
            bool rewound = !double.IsNaN(lastSlowUt) && now < lastSlowUt;
            if (slowReal >= SlowTickRealSeconds && (double.IsNaN(lastSlowUt) || rewound || now - lastSlowUt >= SlowTickGameSeconds))
            {
                slowReal = 0.0;
                SlowTick(now);
                lastSlowUt = now;
            }
        }

        // The slow tick: refresh stale reports of the sites that are watched or were asked for lately, and run the alerts.
        internal void SlowTick(double now)
        {
            var idle = new List<ulong>();
            var list = new List<SiteEntry>();
            lock (gate)
            {
                foreach (KeyValuePair<ulong, SiteEntry> kv in sites)
                {
                    SiteEntry e = kv.Value;
                    if (!e.Watched && clock - e.LastAskedClock > SiteIdleSeconds) idle.Add(kv.Key);
                    else list.Add(e);
                }
                for (int i = 0; i < idle.Count; i++) sites.Remove(idle[i]);
            }
            long generation = cache.Generation;
            for (int i = 0; i < list.Count; i++)
            {
                SiteEntry e = list[i];
                ForecastContext ctx = env.ContextFor(e.Site.BodyIndex);
                if (ctx == null) continue;
                NoteRules(e.Site.BodyIndex, ctx);
                generation = cache.Generation;
                ForecastReport latest = e.Latest;
                bool stale = latest == null || !latest.Available || latest.Generation != generation || now >= latest.IssuedUt + ctx.Rules.StepSeconds
                    || now < latest.IssuedUt;
                if (stale) Enqueue(e, ctx);
                if (!e.Watched || ctx.Alerts == null || !ctx.AlertsOn) continue;
                evaluator.Rules = ctx.Alerts;
                ForecastReport usable = latest != null && latest.Generation == generation ? latest : null;
                AlertTick tick = evaluator.Tick(e.Site, e.LaunchSite, usable, now, ctx.BodyRadiusM);
                e.LastTick = tick;
                if (raise == null) continue;
                for (int a = 0; a < tick.Alerts.Count; a++) raise(tick.Alerts[a]);
            }
        }

        // Main-thread work: one unit of the run that cannot go to the worker, then the next.
        internal void Pump()
        {
            if (mainRun == null)
            {
                lock (gate)
                {
                    if (mainQueue.Count > 0) mainRun = new Run(mainQueue.Dequeue(), cache);
                }
            }
            if (mainRun == null) return;
            try
            {
                if (mainRun.Advance(1)) Publish(mainRun);
                else return;
            }
            catch (Exception e)
            {
                Fail(mainRun.Request, e);
            }
            mainRun = null;
        }

        // Runs everything queued, on the calling thread: the main-thread queue to its end, then the worker queue. For tests.
        internal void DrainSynchronously()
        {
            while (true)
            {
                Request r = null;
                lock (gate)
                {
                    if (mainQueue.Count > 0) r = mainQueue.Dequeue();
                    else if (workerQueue.Count > 0) r = workerQueue.Dequeue();
                }
                if (r == null && mainRun == null) return;
                if (r != null) mainRun = new Run(r, cache);
                try
                {
                    mainRun.Advance(int.MaxValue);
                    Publish(mainRun);
                }
                catch (Exception e)
                {
                    Fail(mainRun.Request, e);
                }
                mainRun = null;
            }
        }

        // Waits until the worker has nothing left; true when it did within the time. For tests.
        internal bool WaitIdle(int timeoutMs)
        {
            DateTime end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < end)
            {
                lock (gate)
                {
                    if (workerQueue.Count == 0 && Volatile.Read(ref workerActive) == 0) return true;
                }
                Thread.Sleep(1);
            }
            return false;
        }

        // --- internals ---

        // Raises ForecastGeneration when the rules of a body changed (a setting, the wind providers, the horizon): the reports made under the
        // old rules are stale. The seed and the algorithm are in the field's DefinitionId, watched in Tick.
        private void NoteRules(int bodyIndex, ForecastContext ctx)
        {
            ulong old;
            if (rulesByBody.TryGetValue(bodyIndex, out old) && old != ctx.RulesHash) WeatherHooks.BumpForecastGeneration();
            rulesByBody[bodyIndex] = ctx.RulesHash;
        }

        private static ForecastReport Unavailable(ForecastSite site, double now)
        {
            var report = new ForecastReport();
            report.BodyIndex = site.BodyIndex;
            report.IssuedUt = now;
            report.Available = false;
            return report;
        }

        private static ulong IdOf(ForecastSite site)
        {
            int lat = (int)Math.Round(site.LatDeg * 10.0);
            int lon = (int)Math.Round(ForecastMath.WrapLon(site.LonDeg) * 10.0);
            if (lon >= 1800) lon -= 3600;
            return ForecastMath.Mix(ForecastUncertainty.SiteSubject(site.BodyIndex, lat, lon), (long)Math.Round(site.AltAgl / 100.0));
        }

        private SiteEntry EntryOf(ForecastSite site, bool create)
        {
            ulong id = IdOf(site);
            lock (gate)
            {
                SiteEntry e;
                if (sites.TryGetValue(id, out e))
                {
                    if (create) e.Site = site;
                    return e;
                }
                if (!create) return null;
                e = new SiteEntry { Site = site, LastAskedClock = clock };
                sites[id] = e;
                return e;
            }
        }

        private void Enqueue(SiteEntry entry, ForecastContext ctx)
        {
            if (entry.Pending || clock < entry.RetryClock) return;
            IWeatherField field = env.Field;
            var r = new Request
            {
                Site = entry.Site,
                SiteId = IdOf(entry.Site),
                BodyIndex = entry.Site.BodyIndex,
                StartUt = env.NowUt,
                HorizonHours = ctx.Rules.HorizonHours,
                Context = ctx,
                Field = field,
                Generation = cache.Generation
            };
            entry.Pending = true;
            Submit(r);
        }

        private void EnqueueTracks(int bodyIndex, ForecastContext ctx)
        {
            lock (gate)
            {
                if (!tracksPending.Add(bodyIndex)) return;
            }
            var r = new Request
            {
                TracksOnly = true,
                BodyIndex = bodyIndex,
                StartUt = env.NowUt,
                HorizonHours = ctx.Rules.HorizonHours,
                Context = ctx,
                Field = env.Field,
                Generation = cache.Generation
            };
            Submit(r);
        }

        private void Submit(Request r)
        {
            bool threadSafe = r.Field.ThreadSafe && WindForecast.AllThreadSafe(r.Context);
            lock (gate)
            {
                if (threadSafe) workerQueue.Enqueue(r);
                else mainQueue.Enqueue(r);
            }
            if (threadSafe) StartWorker();
        }

        private void StartWorker()
        {
            if (Interlocked.CompareExchange(ref workerActive, 1, 0) != 0) return;
            ThreadPool.QueueUserWorkItem(delegate { WorkerLoop(); });
        }

        private void WorkerLoop()
        {
            bool clean = false;
            try
            {
                while (true)
                {
                    Request r;
                    lock (gate)
                    {
                        if (workerQueue.Count == 0)
                        {
                            Volatile.Write(ref workerActive, 0);
                            clean = true;
                            return;
                        }
                        r = workerQueue.Dequeue();
                    }
                    try
                    {
                        var run = new Run(r, cache);
                        run.Advance(int.MaxValue);
                        Publish(run);
                    }
                    catch (Exception e)
                    {
                        Fail(r, e);
                    }
                }
            }
            finally
            {
                // Reached on an exception outside the loop's own handlers; the flag must not stay set.
                if (!clean)
                {
                    lock (gate)
                    {
                        Volatile.Write(ref workerActive, 0);
                    }
                }
            }
        }

        private void Publish(Run run)
        {
            Request r = run.Request;
            if (run.Tracks != null)
            {
                lock (gate)
                {
                    lastTracks[r.BodyIndex] = run.Tracks;
                }
            }
            if (r.TracksOnly)
            {
                lock (gate)
                {
                    tracksPending.Remove(r.BodyIndex);
                }
                return;
            }
            SiteEntry entry;
            lock (gate)
            {
                sites.TryGetValue(r.SiteId, out entry);
            }
            if (entry == null) return;
            // A report made under an earlier generation is not served.
            if (run.Result != null && r.Generation == cache.Generation)
            {
                entry.LatestResult = run.Result;
                entry.Latest = run.Result.Report;
                // An estimate is not served as a forecast; ask again in a little while, not at every call.
                if (!run.Result.Report.Available) entry.RetryClock = clock + 2.0;
            }
            entry.Pending = false;
        }

        private void Fail(Request r, Exception e)
        {
            lastFailure = e;
            if (r.TracksOnly)
            {
                lock (gate)
                {
                    tracksPending.Remove(r.BodyIndex);
                }
            }
            else
            {
                SiteEntry entry;
                lock (gate)
                {
                    sites.TryGetValue(r.SiteId, out entry);
                }
                if (entry != null)
                {
                    entry.Pending = false;
                    entry.RetryClock = clock + RetrySeconds;
                }
            }
            if (!warnedFailure && warn != null)
            {
                warnedFailure = true;
                warn("the forecast could not be computed (" + e.GetType().Name + ": " + e.Message + ")");
            }
        }
    }
}
