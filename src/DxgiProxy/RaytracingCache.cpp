// The radiance cache of the raytraced indirect light (Config.h, rtRadianceCache;
// RaytracingShaders/RadianceCache.hlsli, where the method and its sources are): its
// buffers, the anchor that ties its cells to the world near the camera, its constants, its
// own passes, and the counts it reads back.

#include "Raytracing.h"

#include "Config.h"
#include "D3d12Util.h"
#include "Log.h"
#include "RaytracingInternal.h"

#include <algorithm>
#include <cmath>
#include <cstring>
#include <memory>

using Microsoft::WRL::ComPtr;

namespace redefinition
{
    namespace
    {
        // The slots of a bucket, searched by linear probing (Capsaicin's tiles per bucket;
        // Binder et al.'s restricted probing).
        constexpr uint32_t kCacheProbes = 8;
        // Per entry: the fingerprint (4 bytes), the frame's sums (16), the value (16), the
        // frame it was last met in (4), its place on the resolve's list (4).
        constexpr uint64_t kCacheEntryBytes = 44;
        // The tile the anchor is counted in, RadianceCache.hlsli's kCacheTile.
        constexpr double kCacheTile = 4096.0;
        // A cell is read once it holds this share of the most samples it keeps; a path that
        // meets a cell it could read traces on all the same with this chance, and refreshes
        // it (RtCommon.hlsli, Radiance).
        constexpr float kCacheWarmShare = 1.0f / 8.0f;
        constexpr float kCacheRefresh = 1.0f / 8.0f;
        // Seconds between the cache's lines in the log.
        constexpr ULONGLONG kCacheLogMs = 10000;

        // The camera-relative point a clip-space point stands for, as RtCommon.hlsli's
        // ClipToWorld, in doubles.
        void ClipToWorld(const float* m, double x, double y, double z, double out[3])
        {
            double p[4];
            for (int r = 0; r < 4; ++r)
                p[r] = m[r * 4 + 0] * x + m[r * 4 + 1] * y + m[r * 4 + 2] * z + m[r * 4 + 3];
            for (int k = 0; k < 3; ++k)
                out[k] = p[k] / p[3];
        }
    }

    void Raytracing::PrepareRadianceCacheLocked(FrameWork& frame)
    {
        const ProxyConfig& config = Config();
        const RtFramePacket& packet = *frame.packet;
        cache.active = false;
        cache.update = false;
        const bool traced = (packet.signals & (kRtSignalDiffuse | kRtSignalSpecular)) != 0;
        bool built = true;
        for (const char* name : { "RT_CacheResolve", "RT_CacheClear", "RT_CacheBegin", "RT_DiffuseLightCached",
                                  "RT_SpecularLightCached" })
            built = built && denoisePipelines.count(name) != 0;
        if (!config.rtRadianceCache || !built || !traced)
        {
            // Turned off, or raytracing without the indirect light: its memory goes, once
            // the lists that use it are done.
            if (cache.values != nullptr && !frame.extra)
                ReleaseRadianceCacheLocked(frame.slot);
            return;
        }

        const uint32_t log2Entries = static_cast<uint32_t>(std::clamp(config.rtRadianceCacheEntries, 16, 22));
        if (cache.values == nullptr || cache.log2Entries != log2Entries)
        {
            ReleaseRadianceCacheLocked(frame.slot);
            const uint64_t entries = 1ull << log2Entries;
            const D3D12_RESOURCE_FLAGS uav = D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS;
            const D3D12_RESOURCE_STATES state = D3D12_RESOURCE_STATE_UNORDERED_ACCESS;
            bool made = EnsureDefaultBuffer(cache.checksums, entries * 4, state, uav)
                        && EnsureDefaultBuffer(cache.accumulation, entries * 16, state, uav)
                        && EnsureDefaultBuffer(cache.values, entries * 16, state, uav)
                        && EnsureDefaultBuffer(cache.meta, (entries + kCacheStats) * 4, state, uav)
                        && EnsureDefaultBuffer(cache.touched, entries * 4, state, uav);
            if (made)
            {
                D3D12_HEAP_PROPERTIES readback = {};
                readback.Type = D3D12_HEAP_TYPE_READBACK;
                const D3D12_RESOURCE_DESC desc = RtBufferDesc(static_cast<UINT64>(kSlots) * kCacheStats * 4, D3D12_RESOURCE_FLAG_NONE);
                made = SUCCEEDED(device->CreateCommittedResource(&readback, D3D12_HEAP_FLAG_NONE, &desc,
                                                                 D3D12_RESOURCE_STATE_COPY_DEST, nullptr,
                                                                 IID_PPV_ARGS(&cache.statsReadback)));
            }
            if (!made)
            {
                ReleaseRadianceCacheLocked(nullptr);
                SetProblem("the radiance cache's buffers could not be made (" + std::to_string(entries) + " entries)");
                return;
            }
            cache.checksums->SetName(L"ReDefinition radiance cache: fingerprints");
            cache.accumulation->SetName(L"ReDefinition radiance cache: sums");
            cache.values->SetName(L"ReDefinition radiance cache: values");
            cache.meta->SetName(L"ReDefinition radiance cache: last met, counts");
            cache.touched->SetName(L"ReDefinition radiance cache: the frame's entries");
            cache.log2Entries = log2Entries;
            cache.clear = true;
            LogLine("Raytracing: the radiance cache made, " + std::to_string(entries) + " entries in "
                    + std::to_string(entries / kCacheProbes) + " buckets of " + std::to_string(kCacheProbes) + ", "
                    + std::to_string(static_cast<double>(entries * kCacheEntryBytes) / 1e6) + " MB");
        }

        if (!frame.extra)
        {
            // The camera's history broke off (a cut, another camera): what the cache holds is
            // not this camera's world. Otherwise the anchor follows the camera's move, the
            // previous position less this one (RtFramePacket.cameraDelta, floating origin
            // shifts taken out), so that a point at rest keeps its cell.
            if ((packet.flags & kRtFrameReset) != 0)
                cache.clear = true;
            else
                for (int k = 0; k < 3; ++k)
                    cache.anchor[k] -= static_cast<double>(packet.cameraDelta[k]);
            ++cache.frame;
            cache.update = true;
        }

        // The angle of rtRadianceCacheCellPixels pixels at the middle of the view, as
        // Capsaicin's cell size takes rtRadianceCacheCellPixels times a pixel's angle, here
        // from the camera's own matrix: its rays through two pixels a row apart.
        double a[3], b[3];
        ClipToWorld(packet.clipToWorld, 0.0, 0.0, 0.5, a);
        ClipToWorld(packet.clipToWorld, 0.0, 2.0 / static_cast<double>(std::max<uint32_t>(packet.height, 1)), 0.5, b);
        const double la = std::sqrt(a[0] * a[0] + a[1] * a[1] + a[2] * a[2]);
        const double lb = std::sqrt(b[0] * b[0] + b[1] * b[1] + b[2] * b[2]);
        double cosine = la > 0.0 && lb > 0.0 ? (a[0] * b[0] + a[1] * b[1] + a[2] * b[2]) / (la * lb) : 1.0;
        cosine = std::clamp(cosine, -1.0, 1.0);
        const double pixel = std::acos(cosine);
        cache.cellAngle = static_cast<float>(std::tan(std::min(pixel * config.rtRadianceCacheCellPixels, 1.4)));
        cache.active = true;
    }

    void Raytracing::WriteRadianceCacheConstantsLocked(RtFrameConstants& constants) const
    {
        if (!cache.active)
            return;
        const ProxyConfig& config = Config();
        for (int k = 0; k < 3; ++k)
        {
            // The tile as an integer, wrapping past 2^31 as the shader's cells do; the place
            // in it within a float's reach.
            const double tile = std::floor(cache.anchor[k] / kCacheTile);
            const int64_t whole = static_cast<int64_t>(tile);
            constants.cacheTile[k] = static_cast<int32_t>(static_cast<uint32_t>(static_cast<uint64_t>(whole)));
            constants.cacheOffset[k] = static_cast<float>(cache.anchor[k] - tile * kCacheTile);
        }
        std::memcpy(&constants.cacheTile[3], &cache.frame, sizeof(cache.frame));
        constants.cacheOffset[3] = cache.cellAngle;
        // The least cell, a power of two of a metre, as every cell is (RadianceCache.hlsli,
        // CacheCellSize).
        const float leastCell = std::exp2(std::floor(std::log2(std::max(config.rtRadianceCacheMinCell, 1.0f / 64.0f))));
        const float samples = static_cast<float>(std::max(config.rtRadianceCacheSamples, 1));
        constants.cacheParams[0] = leastCell;
        constants.cacheParams[1] = samples;
        constants.cacheParams[2] = std::max(1.0f, samples * kCacheWarmShare);
        constants.cacheParams[3] = kCacheRefresh;
        constants.cacheSizes[0] = (1u << cache.log2Entries) / kCacheProbes;
        constants.cacheSizes[1] = kCacheProbes;
        constants.cacheSizes[2] = static_cast<uint32_t>(std::max(config.rtRadianceCacheMaxAge, 1));
        constants.cacheSizes[3] = static_cast<uint32_t>(std::clamp(config.rtRadianceCacheDebug, 0, 2));
    }

    ID3D12PipelineState* Raytracing::CachedPipelineLocked(uint32_t signal, ID3D12PipelineState* plain)
    {
        if (!cache.active || (signal != kRtSignalDiffuse && signal != kRtSignalSpecular))
            return plain;
        auto found = denoisePipelines.find(signal == kRtSignalDiffuse ? "RT_DiffuseLightCached" : "RT_SpecularLightCached");
        return found != denoisePipelines.end() ? found->second.Get() : plain;
    }

    void Raytracing::BindRadianceCacheLocked()
    {
        // Bound where the cache is there; its shaders touch it only where the frame's flags
        // say it runs (RT_FLAG_RADIANCE_CACHE).
        if (cache.values == nullptr)
            return;
        list->SetComputeRootUnorderedAccessView(12, cache.checksums->GetGPUVirtualAddress());
        list->SetComputeRootUnorderedAccessView(13, cache.accumulation->GetGPUVirtualAddress());
        list->SetComputeRootUnorderedAccessView(14, cache.values->GetGPUVirtualAddress());
        list->SetComputeRootUnorderedAccessView(15, cache.meta->GetGPUVirtualAddress());
        list->SetComputeRootUnorderedAccessView(16, cache.touched->GetGPUVirtualAddress());
    }

    void Raytracing::BeginRadianceCacheLocked(FrameWork& frame)
    {
        if (!cache.active)
            return;
        // Cleared where it is new or its camera's history broke off; otherwise the frame's
        // counts zeroed, not in a further trace, which comes after the frame's resolve.
        const bool clearing = cache.clear;
        if (!clearing && frame.extra)
            return;
        const D3D12_RESOURCE_BARRIER written = UavBarrier(nullptr);
        list->ResourceBarrier(1, &written);
        const uint32_t entries = 1u << cache.log2Entries;
        list->SetPipelineState(denoisePipelines[clearing ? "RT_CacheClear" : "RT_CacheBegin"].Get());
        list->Dispatch(clearing ? (entries + 255) / 256 : 1, 1, 1);
        list->ResourceBarrier(1, &written);
        cache.clear = false;
    }

    void Raytracing::ResolveRadianceCacheLocked(FrameWork& frame)
    {
        if (!cache.active || frame.extra)
            return;
        const D3D12_RESOURCE_BARRIER written = UavBarrier(nullptr);
        list->ResourceBarrier(1, &written);
        const uint32_t entries = 1u << cache.log2Entries;
        list->SetPipelineState(denoisePipelines["RT_CacheResolve"].Get());
        list->Dispatch((entries + 255) / 256, 1, 1);
        // The frame's counts into the slot's place in the readback buffer.
        D3D12_RESOURCE_BARRIER toCopy[2] = { written, TransitionBarrier(cache.meta.Get(), D3D12_RESOURCE_STATE_UNORDERED_ACCESS,
                                                                         D3D12_RESOURCE_STATE_COPY_SOURCE) };
        list->ResourceBarrier(2, toCopy);
        list->CopyBufferRegion(cache.statsReadback.Get(), static_cast<UINT64>(frame.slotNumber) * kCacheStats * 4,
                               cache.meta.Get(), static_cast<UINT64>(entries) * 4, kCacheStats * 4);
        const D3D12_RESOURCE_BARRIER back = TransitionBarrier(cache.meta.Get(), D3D12_RESOURCE_STATE_COPY_SOURCE,
                                                              D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
        list->ResourceBarrier(1, &back);
        cache.statsCopied[frame.slotNumber] = true;
    }

    void Raytracing::ReadRadianceCacheStatsLocked(UINT slotNumber)
    {
        if (slotNumber >= kSlots || !cache.statsCopied[slotNumber] || cache.statsReadback == nullptr)
            return;
        cache.statsCopied[slotNumber] = false;
        const D3D12_RANGE range = { static_cast<SIZE_T>(slotNumber) * kCacheStats * 4,
                                    static_cast<SIZE_T>(slotNumber + 1) * kCacheStats * 4 };
        void* mapped = nullptr;
        if (FAILED(cache.statsReadback->Map(0, &range, &mapped)))
            return;
        uint32_t counts[kCacheStats];
        std::memcpy(counts, static_cast<const uint8_t*>(mapped) + range.Begin, sizeof(counts));
        const D3D12_RANGE none = { 0, 0 };
        cache.statsReadback->Unmap(0, &none);
        float resolveMs = 0.0f;
        {
            std::lock_guard<std::mutex> status(statusLock);
            std::memcpy(cache.stats, counts, sizeof(counts));
            cache.reported = true;
            resolveMs = passMs[kRtPassCache];
        }
        // What it does in the game, every few seconds into the log.
        const ULONGLONG now = GetTickCount64();
        if (now < cache.nextLog)
            return;
        cache.nextLog = now + kCacheLogMs;
        const uint32_t entries = 1u << cache.log2Entries;
        char line[400];
        _snprintf_s(line, sizeof(line), _TRUNCATE,
                    "Raytracing: radiance cache: %u of %u entries live (%.1f %%), %u made and %u evicted this frame, %u not"
                    " placed (bucket full); %u samples written, %u of %u path hits ended in it; resolve %.3f ms",
                    counts[4], entries, 100.0 * counts[4] / entries, counts[0], counts[5], counts[1], counts[6], counts[3],
                    counts[2], resolveMs);
        LogLine(line);
    }

    void Raytracing::ReleaseRadianceCacheLocked(Slot* holder)
    {
        const bool had = cache.values != nullptr;
        if (holder != nullptr)
            // ComPtr's & releases; std::addressof takes the address.
            for (ComPtr<ID3D12Resource>* buffer : { std::addressof(cache.checksums), std::addressof(cache.accumulation),
                                                     std::addressof(cache.values), std::addressof(cache.meta),
                                                     std::addressof(cache.touched), std::addressof(cache.statsReadback) })
                if (*buffer != nullptr)
                    holder->held.push_back(*buffer);
        cache.checksums.Reset();
        cache.accumulation.Reset();
        cache.values.Reset();
        cache.meta.Reset();
        cache.touched.Reset();
        cache.statsReadback.Reset();
        cache.log2Entries = 0;
        cache.clear = true;
        cache.active = false;
        cache.update = false;
        for (bool& copied : cache.statsCopied)
            copied = false;
        {
            std::lock_guard<std::mutex> status(statusLock);
            cache.reported = false;
        }
        if (had)
            LogLine("Raytracing: the radiance cache let go");
    }
}
