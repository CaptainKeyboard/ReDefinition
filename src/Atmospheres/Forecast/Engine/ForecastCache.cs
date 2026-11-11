using System;
using System.Collections.Generic;
using ReDefinition.Core;

namespace ReDefinition.Atmospheres.Forecast
{
    // What a cached site report depends on: the site rounded to 0.1 degree (and its height to 100 m), the body, the rules hash, the number
    // of steps, and the field's DefinitionId (the hash of the seed, the rules and the algorithm; never Version, which may change at every
    // call and would empty the cache).
    internal struct ForecastCacheKey : IEquatable<ForecastCacheKey>
    {
        internal int Body;
        internal int LatTenth;
        internal int LonTenth;
        internal int AltHundred;
        internal ulong RulesHash;
        internal long DefinitionId;
        internal int StepCount;

        internal static ForecastCacheKey Of(ForecastSite site, ForecastContext ctx, long definitionId, int stepCount)
        {
            int lat = (int)Math.Round(site.LatDeg * 10.0);
            int lon = (int)Math.Round(ForecastMath.WrapLon(site.LonDeg) * 10.0);
            if (lon >= 1800) lon -= 3600;
            return new ForecastCacheKey
            {
                Body = site.BodyIndex,
                LatTenth = lat,
                LonTenth = lon,
                AltHundred = (int)Math.Round(site.AltAgl / 100.0),
                RulesHash = ctx.RulesHash,
                DefinitionId = definitionId,
                StepCount = stepCount
            };
        }

        public bool Equals(ForecastCacheKey o)
        {
            return Body == o.Body && LatTenth == o.LatTenth && LonTenth == o.LonTenth && AltHundred == o.AltHundred && RulesHash == o.RulesHash
                && DefinitionId == o.DefinitionId && StepCount == o.StepCount;
        }

        public override bool Equals(object obj)
        {
            return obj is ForecastCacheKey && Equals((ForecastCacheKey)obj);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int h = Body;
                h = h * 31 + LatTenth;
                h = h * 31 + LonTenth;
                h = h * 31 + AltHundred;
                h = h * 31 + (int)(RulesHash ^ (RulesHash >> 32));
                h = h * 31 + (int)(DefinitionId ^ (DefinitionId >> 32));
                h = h * 31 + StepCount;
                return h;
            }
        }
    }

    // The sampled steps of one site, on the grid of the step (the first one at a whole multiple of the step), as they were computed.
    internal sealed class ForecastCacheEntry
    {
        internal ForecastCacheKey Key;
        internal long Generation;
        internal double FirstUt;
        internal double StepSeconds;
        internal ForecastStep[] Steps;
        internal WeatherSource Wind;
        internal long LastUse;
    }

    // The rolling cache of the forecast. A site report slides: the next report of the same site reuses the steps both cover and computes
    // only the new tail. Tracks are cached per body and 30-minute bucket. An entry from before the last ForecastGeneration bump is
    // dropped on the next access. Thread safe: the worker stores, the main thread reads.
    internal sealed class ForecastCache
    {
        internal const int SiteCapacity = 64;
        internal const int TrackCapacity = 16;

        private struct TrackKey : IEquatable<TrackKey>
        {
            internal int Body;
            internal ulong RulesHash;
            internal long DefinitionId;
            internal long Bucket;

            public bool Equals(TrackKey o)
            {
                return Body == o.Body && RulesHash == o.RulesHash && DefinitionId == o.DefinitionId && Bucket == o.Bucket;
            }

            public override bool Equals(object obj)
            {
                return obj is TrackKey && Equals((TrackKey)obj);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return (int)((Body * 397) ^ (long)RulesHash ^ DefinitionId ^ (Bucket * 31));
                }
            }
        }

        private readonly object gate = new object();
        private readonly Func<long> generation;
        private readonly Dictionary<ForecastCacheKey, ForecastCacheEntry> entries = new Dictionary<ForecastCacheKey, ForecastCacheEntry>();
        private readonly Dictionary<TrackKey, ForecastTrack[]> tracks = new Dictionary<TrackKey, ForecastTrack[]>();
        private long seenGeneration;
        private long clock;
        private long reusedSteps, computedSteps, hits, trackHits, trackMisses;

        // generation reads the number that rises when the inputs of the forecast change; the default is WeatherHooks.ForecastGeneration.
        internal ForecastCache(Func<long> generation = null)
        {
            this.generation = generation ?? (() => WeatherHooks.ForecastGeneration);
            seenGeneration = this.generation();
        }

        // The generation the cache holds entries for.
        internal long Generation
        {
            get { return Validate(); }
        }

        internal int SiteCount
        {
            get
            {
                lock (gate)
                {
                    return entries.Count;
                }
            }
        }

        internal long ReusedSteps { get { return System.Threading.Interlocked.Read(ref reusedSteps); } }
        internal long ComputedSteps { get { return System.Threading.Interlocked.Read(ref computedSteps); } }
        internal long Hits { get { return System.Threading.Interlocked.Read(ref hits); } }
        internal long TrackHits { get { return System.Threading.Interlocked.Read(ref trackHits); } }
        internal long TrackMisses { get { return System.Threading.Interlocked.Read(ref trackMisses); } }

        private long Validate()
        {
            long g = generation();
            lock (gate)
            {
                if (g != seenGeneration)
                {
                    entries.Clear();
                    tracks.Clear();
                    seenGeneration = g;
                }
            }
            return g;
        }

        // The entry of a key, or null where there is none or it is from an earlier generation.
        internal ForecastCacheEntry Find(ForecastCacheKey key)
        {
            Validate();
            lock (gate)
            {
                ForecastCacheEntry e;
                if (!entries.TryGetValue(key, out e)) return null;
                e.LastUse = ++clock;
                System.Threading.Interlocked.Increment(ref hits);
                return e;
            }
        }

        // Stores the entry of a computed report (it replaces the older entry of the key). An entry computed under an earlier generation is
        // not stored.
        internal void Store(ForecastCacheEntry entry, long computedUnderGeneration)
        {
            long g = Validate();
            if (computedUnderGeneration != g) return;
            lock (gate)
            {
                entry.Generation = g;
                entry.LastUse = ++clock;
                entries[entry.Key] = entry;
                if (entries.Count > SiteCapacity) EvictOldest();
            }
        }

        private void EvictOldest()
        {
            ForecastCacheKey oldest = default(ForecastCacheKey);
            long best = long.MaxValue;
            foreach (KeyValuePair<ForecastCacheKey, ForecastCacheEntry> kv in entries)
            {
                if (kv.Value.LastUse < best)
                {
                    best = kv.Value.LastUse;
                    oldest = kv.Key;
                }
            }
            entries.Remove(oldest);
        }

        internal void CountSteps(int reused, int computed)
        {
            System.Threading.Interlocked.Add(ref reusedSteps, reused);
            System.Threading.Interlocked.Add(ref computedSteps, computed);
        }

        // The bucket (30 minutes by default) a time falls in.
        internal static long BucketOf(double ut, double trackStepSeconds)
        {
            return ForecastMath.StepIndex(ut, trackStepSeconds);
        }

        // The tracks of a body for a bucket, or null.
        internal ForecastTrack[] FindTracks(int bodyIndex, ForecastContext ctx, long definitionId, long bucket)
        {
            Validate();
            lock (gate)
            {
                ForecastTrack[] t;
                if (tracks.TryGetValue(new TrackKey { Body = bodyIndex, RulesHash = ctx.RulesHash, DefinitionId = definitionId, Bucket = bucket }, out t))
                {
                    System.Threading.Interlocked.Increment(ref trackHits);
                    return t;
                }
                System.Threading.Interlocked.Increment(ref trackMisses);
                return null;
            }
        }

        // The tracks of the nearest earlier bucket of the body, for the keys a new set inherits; null where there are none.
        internal ForecastTrack[] FindEarlierTracks(int bodyIndex, ForecastContext ctx, long definitionId, long bucket)
        {
            lock (gate)
            {
                long best = long.MinValue;
                ForecastTrack[] found = null;
                foreach (KeyValuePair<TrackKey, ForecastTrack[]> kv in tracks)
                {
                    TrackKey k = kv.Key;
                    if (k.Body != bodyIndex || k.RulesHash != ctx.RulesHash || k.DefinitionId != definitionId || k.Bucket >= bucket) continue;
                    if (k.Bucket > best)
                    {
                        best = k.Bucket;
                        found = kv.Value;
                    }
                }
                return found;
            }
        }

        internal void StoreTracks(int bodyIndex, ForecastContext ctx, long definitionId, long bucket, ForecastTrack[] set, long computedUnderGeneration)
        {
            long g = Validate();
            if (computedUnderGeneration != g) return;
            lock (gate)
            {
                tracks[new TrackKey { Body = bodyIndex, RulesHash = ctx.RulesHash, DefinitionId = definitionId, Bucket = bucket }] = set;
                if (tracks.Count > TrackCapacity)
                {
                    TrackKey oldest = default(TrackKey);
                    long lowest = long.MaxValue;
                    foreach (TrackKey k in tracks.Keys)
                    {
                        if (k.Bucket < lowest)
                        {
                            lowest = k.Bucket;
                            oldest = k;
                        }
                    }
                    tracks.Remove(oldest);
                }
            }
        }

        internal void Clear()
        {
            lock (gate)
            {
                entries.Clear();
                tracks.Clear();
            }
        }
    }
}
