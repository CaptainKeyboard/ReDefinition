// The radiance cache: a hash grid over the world that holds the indirect light arriving at
// the surfaces the rays meet (Raytracing.h, RadianceCache; the proxy's rtRadianceCache).
//
// After Binder, Fricke and Keller, "Massively Parallel Path Space Filtering" (2019): "Each
// vertex atomically adds its contribution [...] to a running sum of the voxel and increments
// the counter of the voxel. The average is finally calculated by dividing the sum by the
// counter" (3.2), in a hash table whose keys are the position quantized at a level of
// detail by the distance from the camera, jittered in the tangent plane before the
// quantization (algorithm 1), with "a shorter fingerprint [...] from a second, different
// hash function of the same key" (3.2.1) and linear probing restricted "to avoid
// performance penalties of extreme outliers" (3.2). As AMD's GI-1.0 (Boisse et al. 2023,
// 2.2) builds on it: a first hash picks a bucket, the second its fingerprint "which we use
// to perform linear probing inside the bucket"; the descriptor also holds "the boolean
// result of the (ray_length < cell_size) inequality test" against light leaking through
// thin walls (2.2.2); and "each cell is associated with a decay value, which is reset upon
// access [...] As a cell's decay reaches zero, we deallocate the entry" (2.2.1).
//
// The cell's size, the descriptor's hashes, the insertion and the lookup are adapted from
// AMD's Capsaicin (src/core/src/render_techniques/gi1/hash_grid_cache.hlsl,
// HashGridCache_GetDesc, HashGridCache_InsertCell, HashGridCache_FindCell, and
// src/core/src/math/hash.hlsl, xxHash), MIT, its notice in licenses/AMD-Capsaicin-MIT.txt:
//
//   Copyright (c) 2025 Advanced Micro Devices, Inc. All rights reserved.
//
//   Permission is hereby granted, free of charge, to any person obtaining a copy
//   of this software and associated documentation files (the "Software"), to deal
//   in the Software without restriction, including without limitation the rights
//   to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
//   copies of the Software, and to permit persons to whom the Software is
//   furnished to do so, subject to the following conditions:
//
//   The above copyright notice and this permission notice shall be included in
//   all copies or substantial portions of the Software.
//
//   THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
//   IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
//   FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.  IN NO EVENT SHALL THE
//   AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
//   LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
//   OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
//   THE SOFTWARE.
//
// ReDefinition's changes: one level of cells, no tiles of cells and no mip levels; cell
// sizes powers of two of a metre, not of a millimetre, so that the camera-relative
// positions find the same cell however far the cache's anchor has moved (CacheKeyAt); the
// surface's normal in the descriptor where Capsaicin has the ray's direction, as the cache
// holds the light arriving at a surface, not the light leaving it; the jitter (Binder);
// the lookup goes on past an empty slot, as eviction leaves holes in a bucket, and the
// insertion looks first; the sums rounded at random rather than to the nearest, so that
// faint light is not lost, and summed a wave at a time; the resolve takes the frame's
// entries from a list and looks for entries to evict in a slice of the table a frame
// (RadianceCache.hlsl).
//
// Positions are camera-relative (RtCommon.hlsli). The cache's frame is anchored to the
// world near the camera: the proxy sums the camera's moves (RtFramePacket.cameraDelta,
// which KSP's floating origin shifts are taken out of) into the anchor, split into a tile
// of kCacheTile metres and the place within it, so that the cell a point falls in stays
// the same as the camera moves on, at any distance from where the cache started. What
// moves against the camera's frame -- the ground under Krakensbane, a vessel in flight --
// moves through the cells; its old cells age out and the refresh rays keep the new ones
// fresh.

#ifndef REDEFINITION_RADIANCE_CACHE
#define REDEFINITION_RADIANCE_CACHE

// The cache's buffers, as root UAVs (RaytracingDevice.cpp, BuildPipelinesLocked): per entry
// its fingerprint (0 for none), the frame's sums (rgb quantized, and the count), its value
// (rgb, and the samples it holds) and the frame it was last met in; past the entries, in
// cacheMeta, the counts (RT_CACHE_STAT_*); the entries the frame's samples went into, as
// many as RT_CACHE_STAT_TOUCHED counts, for the resolve.
RWStructuredBuffer<uint> cacheChecksums : register(u0, space4);
RWStructuredBuffer<uint4> cacheAccumulation : register(u1, space4);
RWStructuredBuffer<float4> cacheValues : register(u2, space4);
RWStructuredBuffer<uint> cacheMeta : register(u3, space4);
RWStructuredBuffer<uint> cacheTouched : register(u4, space4);

// The cache's state from cbuffer Frame (RaytracingCache.cpp, WriteRadianceCacheConstantsLocked):
// whether the frame reads it and writes it; the frame's count and the cells' angle; the least
// cell, the most samples an entry keeps, the samples a cell is read from, the chance a path
// refreshes a warm cell; the buckets, the slots of a bucket, the entries, the frames an entry
// is kept unmet, and the debug view (rtRadianceCacheDebug: 1 the values, 2 the cells; 0 none).
#define CACHE_ON ((frameFlags & RT_FLAG_RADIANCE_CACHE) != 0u)
#define CACHE_UPDATE ((frameFlags & RT_FLAG_RADIANCE_CACHE_UPDATE) != 0u)
#define CACHE_FRAME (asuint(cacheTile.w))
#define CACHE_ANGLE (cacheOffset.w)
#define CACHE_MIN_CELL (cacheParams.x)
#define CACHE_MAX_SAMPLES (cacheParams.y)
#define CACHE_WARM_SAMPLES (cacheParams.z)
#define CACHE_REFRESH (cacheParams.w)
#define CACHE_BUCKETS (cacheSizes.x)
#define CACHE_PROBES (cacheSizes.y)
#define CACHE_ENTRIES (cacheSizes.x * cacheSizes.y)
#define CACHE_MAX_AGE (cacheSizes.z)
#define CACHE_DEBUG (cacheSizes.w)

// The counts, at CACHE_ENTRIES + these in cacheMeta (Raytracing.h, kCacheStats): the
// frame's, zeroed before its traces (CacheBegin), and the entries live, kept.
#define RT_CACHE_STAT_INSERTED 0u
#define RT_CACHE_STAT_FULL 1u
#define RT_CACHE_STAT_QUERIES 2u
#define RT_CACHE_STAT_WARM 3u
#define RT_CACHE_STAT_LIVE 4u
#define RT_CACHE_STAT_EVICTED 5u
#define RT_CACHE_STAT_SAMPLES 6u
#define RT_CACHE_STAT_TOUCHED 7u
#define RT_CACHE_STATS 8u
// The resolve looks at one slice of the table in this many each frame for entries to evict:
// an entry goes up to that many frames after CACHE_MAX_AGE.
#define RT_CACHE_AGE_SLICES 8u

// The entry index of no entry: a lookup that found none.
static const uint kCacheNone = 0xFFFFFFFFu;
// The tile the anchor is counted in, in metres: the largest cell, so that every cell size,
// a power of two, divides it.
static const float kCacheTile = 4096.0f;
// Capsaicin's kHashGridCache_FloatQuantize: the sums as integers, for atomic adds. A
// sample's channel is held to kCacheMostSample, so that a frame's sum of a cell overflows
// only past four thousand of the brightest samples.
static const float kCacheQuantize = 1000.0f;
static const float kCacheMostSample = 1000.0f;
// How many frames take turns at writing a pixel's own ray into its cell (Raytracing.hlsl,
// DiffuseLightAt).
static const uint kCachePrimaryFrames = 4u;

// The counts summed over the wave, one atomic add for it (stat RT_CACHE_STAT_*); taken off
// where `less`.
void CacheCount(uint stat, bool counted, bool less = false)
{
    uint n = WaveActiveCountBits(counted);
    if (WaveIsFirstLane() && n > 0u)
        InterlockedAdd(cacheMeta[CACHE_ENTRIES + stat], less ? 0u - n : n);
}

// Capsaicin's xxHash (math/hash.hlsl), after Jarzynski and Olano, "Hash Functions for GPU
// Rendering" (2020); the PCG hash is RtCommon.hlsli's Pcg, the same as Capsaicin's pcgHash.
uint CacheXxHash(uint value)
{
    const uint prime32_2 = 2246822519u, prime32_3 = 3266489917u;
    const uint prime32_4 = 668265263u, prime32_5 = 374761393u;
    uint ret = value + prime32_5;
    ret = prime32_4 * ((ret << 17) | (ret >> (32 - 17)));
    ret = prime32_2 * (ret ^ (ret >> 15));
    ret = prime32_3 * (ret ^ (ret >> 13));
    return ret ^ (ret >> 16);
}

// The cell's size at a distance from the camera: as Capsaicin's HashGridCache_GetDesc, the
// distance times the angle of rtRadianceCacheCellPixels pixels, at least the least cell,
// down to a power of two -- here of a metre --, at most kCacheTile.
float CacheCellSize(float distance)
{
    float step = max(distance * CACHE_ANGLE, CACHE_MIN_CELL);
    return clamp(exp2(floor(log2(step))), CACHE_MIN_CELL, kCacheTile);
}

// Where a cell lies in the table: its bucket, and the fingerprint its slot holds (CacheKeyAt).
struct CacheKey
{
    // The bucket's index (CACHE_BUCKETS of them), and the fingerprint, never 0.
    uint bucket;
    uint checksum;
};

// The key of a point on a surface: Binder's algorithm 1, the level by the distance, the
// point jittered by up to half a cell along the surface, its level again, its cell; then
// Capsaicin's descriptor -- the level, the cell, the direction quantized to five steps an
// axis, GI-1.0's test of the ray's length against the cell -- through the PCG hash for the
// bucket and xxHash for the fingerprint, never 0, which marks an empty slot.
CacheKey CacheKeyAt(float3 position, float3 normal, float rayLength, bool jitter, inout Random random)
{
    float size = CacheCellSize(length(position));
    if (jitter)
    {
        float3 t, b;
        Basis(normal, t, b);
        float2 u = float2(NextFloat(random), NextFloat(random)) - 0.5f;
        position += (t * u.x + b * u.y) * size;
        size = CacheCellSize(length(position));
    }
    // Within the anchor's tile, then counted from the tile's corner in cells: the tile's
    // cells as integers wrap with the unsigned arithmetic, and the same point always gives
    // the same cell.
    float3 within = position + cacheOffset.xyz;
    uint perTile = uint(kCacheTile / size);
    uint3 c = asuint(cacheTile.xyz) * perTile + asuint(int3(floor(within / size)));
    uint3 d = uint3(floor(0.5f + (0.5f * normal + 0.5f) * 4.0f));
    uint l = asuint(size);
    uint t = rayLength < size ? 1u : 0u;
    CacheKey key;
    key.bucket = Pcg(l + Pcg(c.x + Pcg(c.y + Pcg(c.z + Pcg(d.x + Pcg(d.y + Pcg(d.z + Pcg(t)))))))) % CACHE_BUCKETS;
    key.checksum = max(1u, CacheXxHash(l + CacheXxHash(c.x + CacheXxHash(c.y + CacheXxHash(c.z
                           + CacheXxHash(d.x + CacheXxHash(d.y + CacheXxHash(d.z + CacheXxHash(t)))))))));
    return key;
}

// The entry of a key, or kCacheNone: its bucket searched to the end (HashGridCache_FindCell
// stops at the first empty slot; here an evicted entry leaves one in the middle).
uint CacheFind(CacheKey key)
{
    uint first = key.bucket * CACHE_PROBES;
    for (uint i = 0u; i < CACHE_PROBES; ++i)
        if (cacheChecksums[first + i] == key.checksum)
            return first + i;
    return kCacheNone;
}

// The entry of a key, made where there is none (HashGridCache_InsertCell: the fingerprint
// set into the first empty slot by compare-and-exchange); kCacheNone where its bucket is
// full.
uint CacheInsert(CacheKey key)
{
    uint found = CacheFind(key);
    if (found != kCacheNone)
        return found;
    uint first = key.bucket * CACHE_PROBES;
    for (uint i = 0u; i < CACHE_PROBES; ++i)
    {
        uint previous;
        InterlockedCompareExchange(cacheChecksums[first + i], 0u, key.checksum, previous);
        if (previous == 0u || previous == key.checksum)
        {
            CacheCount(RT_CACHE_STAT_INSERTED, previous == 0u);
            CacheCount(RT_CACHE_STAT_LIVE, previous == 0u);
            return first + i;
        }
    }
    CacheCount(RT_CACHE_STAT_FULL, true);
    return kCacheNone;
}

// The cache's value at a surface point, the light arriving there as Radiance gives it for
// a cosine-weighted ray (rgb), and the samples it holds (w); false where it has no entry.
// Meeting it keeps it from ageing out.
bool CacheLookup(float3 position, float3 normal, float rayLength, inout Random random, out float4 value)
{
    value = 0.0f;
    uint entry = CacheFind(CacheKeyAt(position, normal, rayLength, true, random));
    if (entry == kCacheNone)
        return false;
    // Written only where it changes: the pixels side by side meet the same cell.
    if (cacheMeta[entry] != CACHE_FRAME)
        cacheMeta[entry] = CACHE_FRAME;
    value = cacheValues[entry];
    return true;
}

// A sample of the light arriving at a surface point into the frame's sums of its cell. The
// sum rounds at random (floor of value + u), which keeps its mean. The lanes of a wave that
// add to the same entry are summed first and one of them adds for all: the pixels side by
// side share their cells, and atomic adds to one address wait for each other -- GI-1.0,
// 2.2.1: "The main bottleneck of a global hash map, such as the one we are describing, is
// the extensive use of atomic operations for each insertion into the cache."
void CacheAdd(float3 position, float3 normal, float rayLength, float3 sample, inout Random random)
{
    if (!all(isfinite(sample)))
        return;
    uint entry = CacheInsert(CacheKeyAt(position, normal, rayLength, true, random));
    if (entry == kCacheNone)
        return;
    uint3 q = uint3(floor(clamp(sample, 0.0f, kCacheMostSample) * kCacheQuantize + NextFloat(random)));
    CacheCount(RT_CACHE_STAT_SAMPLES, true);
    // The lanes with this entry (WaveMatch, shader model 6.5), what those before this one
    // add, and the last of them adds the whole: no loop over the entries, whose lanes the
    // compiler need not keep apart.
    uint4 group = WaveMatch(entry);
    uint3 before = WaveMultiPrefixSum(q, group);
    uint last = group.w != 0u ? 96u + firstbithigh(group.w)
              : group.z != 0u ? 64u + firstbithigh(group.z)
              : group.y != 0u ? 32u + firstbithigh(group.y) : firstbithigh(group.x);
    if (WaveGetLaneIndex() == last)
    {
        uint3 sum = before + q;
        uint count = countbits(group.x) + countbits(group.y) + countbits(group.z) + countbits(group.w);
        InterlockedAdd(cacheAccumulation[entry].x, sum.x);
        InterlockedAdd(cacheAccumulation[entry].y, sum.y);
        InterlockedAdd(cacheAccumulation[entry].z, sum.z);
        uint counted;
        InterlockedAdd(cacheAccumulation[entry].w, count, counted);
        cacheMeta[entry] = CACHE_FRAME;
        // The frame's first samples of the entry: onto the resolve's list.
        if (counted == 0u)
        {
            uint at;
            InterlockedAdd(cacheMeta[CACHE_ENTRIES + RT_CACHE_STAT_TOUCHED], 1u, at);
            cacheTouched[at] = entry;
        }
    }
}

#endif
