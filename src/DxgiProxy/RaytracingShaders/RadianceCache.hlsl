// The radiance cache's own passes (RadianceCache.hlsli; RaytracingCache.cpp): its clearing,
// the frame's counts zeroed before the traces, and the resolve after them.

#include "RtCommon.hlsli"

// Every entry empty, and the counts zeroed: a cache made anew, or a camera whose history
// broke off (RT_FLAG_RESET).
[numthreads(256, 1, 1)]
void CacheClear(uint3 id : SV_DispatchThreadID)
{
    uint i = id.x;
    if (i < RT_CACHE_STATS)
        cacheMeta[CACHE_ENTRIES + i] = 0u;
    if (i >= CACHE_ENTRIES)
        return;
    cacheChecksums[i] = 0u;
    cacheAccumulation[i] = 0u;
    cacheValues[i] = 0.0f;
    cacheMeta[i] = 0u;
}

// The frame's counts zeroed, before its traces count; the entries live kept.
[numthreads(8, 1, 1)]
void CacheBegin(uint3 id : SV_DispatchThreadID)
{
    if (id.x != RT_CACHE_STAT_LIVE)
        cacheMeta[CACHE_ENTRIES + id.x] = 0u;
}

// After the frame's traces, a thread per entry of the table: the entries the frame's samples
// went into (cacheTouched), their samples blended into their values; and in one slice of
// the table, a different one each frame, an entry no ray met for CACHE_MAX_AGE frames let
// go (GI-1.0, 2.2.1). Neither reads the whole table each frame: its 44 bytes an entry would
// pass through the GPU's caches every frame. The blend is Binder et al.'s temporal
// integration, "setting alpha := N_old / (N_old + N_new) correctly integrates across
// frames", with their first heuristic for a world that changes, "to accumulate samples over
// time up to a certain degree [...] using a fixed threshold for the number of samples"
// (3.4.1): N_old is held to CACHE_MAX_SAMPLES, so that the value follows a change in the
// light within some frames.
[numthreads(256, 1, 1)]
void CacheResolve(uint3 id : SV_DispatchThreadID)
{
    uint i = id.x;
    if (i < cacheMeta[CACHE_ENTRIES + RT_CACHE_STAT_TOUCHED] && i < CACHE_ENTRIES)
    {
        uint entry = cacheTouched[i];
        uint4 sum = cacheAccumulation[entry];
        float4 value = cacheValues[entry];
        float added = float(sum.w);
        float kept = min(value.w, CACHE_MAX_SAMPLES);
        float3 mean = float3(sum.xyz) / (kCacheQuantize * max(added, 1.0f));
        value.rgb = (value.rgb * kept + mean * added) / max(kept + added, 1.0f);
        value.w = min(kept + added, CACHE_MAX_SAMPLES);
        cacheValues[entry] = value;
        cacheAccumulation[entry] = 0u;
    }
    // The slice's entries: one in RT_CACHE_AGE_SLICES of the table, in turn.
    uint slice = CACHE_ENTRIES / RT_CACHE_AGE_SLICES;
    bool evicted = false;
    if (i < slice)
    {
        uint entry = (CACHE_FRAME % RT_CACHE_AGE_SLICES) * slice + i;
        // The age in the frames the cache counts, which wrap: a cell met this frame is 0, and
        // so is one the frame's samples went into, which the blend above holds.
        if (cacheChecksums[entry] != 0u && CACHE_FRAME - cacheMeta[entry] > CACHE_MAX_AGE)
        {
            evicted = true;
            cacheChecksums[entry] = 0u;
            cacheAccumulation[entry] = 0u;
            cacheValues[entry] = 0.0f;
            cacheMeta[entry] = 0u;
        }
    }
    CacheCount(RT_CACHE_STAT_EVICTED, evicted);
    CacheCount(RT_CACHE_STAT_LIVE, evicted, true);
}
