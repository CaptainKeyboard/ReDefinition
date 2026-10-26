// The memory the meshes' acceleration structures are built in (RaytracingBuild.h).

#include "RaytracingBuild.h"

#include "FileUtil.h"
#include "Log.h"
#include "RaytracingInternal.h"

#include <algorithm>
#include <iterator>

namespace redefinition
{
    namespace
    {
        // The value rounded up to a multiple of the granularity.
        UINT64 RoundUp(UINT64 value, UINT64 granularity)
        {
            return (value + granularity - 1) / granularity * granularity;
        }
    }

    void RtBufferPool::Setup(D3D12_RESOURCE_FLAGS newFlags, D3D12_RESOURCE_STATES newState, UINT64 newGranularity,
                             UINT64 newChunkSize, const wchar_t* newName)
    {
        flags = newFlags;
        state = newState;
        granularity = newGranularity;
        chunkSize = newChunkSize;
        name = newName;
    }

    bool RtBufferPool::Allocate(ID3D12Device* device, UINT64 size, RtPoolRange& range)
    {
        const UINT64 wanted = RoundUp(std::max<UINT64>(size, 1), granularity);
        for (uint32_t c = 0; c < chunks.size(); ++c)
            for (auto block = chunks[c].free.begin(); block != chunks[c].free.end(); ++block)
            {
                if (block->second < wanted)
                    continue;
                range.chunk = c;
                range.offset = block->first;
                range.size = wanted;
                const UINT64 rest = block->second - wanted;
                const UINT64 restStart = block->first + wanted;
                chunks[c].free.erase(block);
                if (rest > 0)
                    chunks[c].free[restStart] = rest;
                used += wanted;
                return true;
            }
        // A chunk more, at least the chunk size; a buffer's start is 64 KB aligned
        // (D3D12_DEFAULT_RESOURCE_PLACEMENT_ALIGNMENT), so its ranges keep the granularity.
        Chunk chunk;
        chunk.size = RoundUp(std::max(wanted, chunkSize), 65536);
        D3D12_HEAP_PROPERTIES heap = {};
        heap.Type = D3D12_HEAP_TYPE_DEFAULT;
        const D3D12_RESOURCE_DESC desc = RtBufferDesc(chunk.size, flags);
        if (FAILED(device->CreateCommittedResource(&heap, D3D12_HEAP_FLAG_NONE, &desc, state, nullptr,
                                                   IID_PPV_ARGS(&chunk.resource))))
            return false;
        chunk.resource->SetName(name);
        chunk.address = chunk.resource->GetGPUVirtualAddress();
        if (chunk.size > wanted)
            chunk.free[wanted] = chunk.size - wanted;
        reserved += chunk.size;
        used += wanted;
        range.offset = 0;
        range.size = wanted;
        // The place of a chunk let go of, where there is one.
        uint32_t at = static_cast<uint32_t>(chunks.size());
        for (uint32_t c = 0; c < chunks.size(); ++c)
            if (chunks[c].resource == nullptr)
            {
                at = c;
                break;
            }
        range.chunk = at;
        if (at == chunks.size())
            chunks.push_back(std::move(chunk));
        else
            chunks[at] = std::move(chunk);
        return true;
    }

    void RtBufferPool::Release(const RtPoolRange& range)
    {
        auto& free = chunks[range.chunk].free;
        UINT64 start = range.offset;
        UINT64 end = range.offset + range.size;
        auto next = free.lower_bound(start);
        if (next != free.end() && next->first == end)
        {
            end += next->second;
            next = free.erase(next);
        }
        if (next != free.begin())
        {
            auto before = std::prev(next);
            if (before->first + before->second == start)
            {
                start = before->first;
                free.erase(before);
            }
        }
        free[start] = end - start;
        used -= range.size;
    }

    void RtBufferPool::Free(RtPoolRange& range, UINT64 fenceValue)
    {
        if (!range.Valid())
            return;
        if (fenceValue == 0)
            Release(range);
        else
            retired.emplace_back(range, fenceValue);
        range = RtPoolRange{};
    }

    void RtBufferPool::Reclaim(UINT64 completed)
    {
        for (auto it = retired.begin(); it != retired.end();)
        {
            if (it->second <= completed)
            {
                Release(it->first);
                it = retired.erase(it);
            }
            else
                ++it;
        }
        ReleaseIdle(GetTickCount64());
    }

    void RtBufferPool::ReleaseIdle(ULONGLONG now)
    {
        // Wholly free: one free range from the start to the end. A range freed but not yet
        // reclaimed is not in it, so no list that used the chunk is still running.
        size_t live = 0;
        std::vector<uint32_t> idle;
        for (uint32_t c = 0; c < chunks.size(); ++c)
        {
            Chunk& chunk = chunks[c];
            if (chunk.resource == nullptr)
                continue;
            ++live;
            const bool wholly = chunk.free.size() == 1 && chunk.free.begin()->first == 0
                                && chunk.free.begin()->second == chunk.size;
            if (!wholly)
                chunk.idleSince = 0;
            else if (chunk.idleSince == 0)
                chunk.idleSince = now;
            else if (now - chunk.idleSince >= kIdleMs)
                idle.push_back(c);
        }
        // The larger first: a chunk made for one large mesh goes before the usual ones.
        std::sort(idle.begin(), idle.end(), [&](uint32_t a, uint32_t b) { return chunks[a].size > chunks[b].size; });
        UINT64 freed = 0;
        size_t count = 0;
        for (const uint32_t c : idle)
        {
            if (live <= 1)
                break;
            freed += chunks[c].size;
            reserved -= chunks[c].size;
            chunks[c] = Chunk{};
            --live;
            ++count;
        }
        if (count > 0)
            LogLine("Raytracing: " + std::to_string(count) + " idle chunk" + (count == 1 ? "" : "s") + " of "
                    + Narrow(name) + " released, " + std::to_string(freed >> 20) + " MB; " + std::to_string(reserved >> 20)
                    + " MB reserved in " + std::to_string(live) + " chunk" + (live == 1 ? "" : "s"));
    }

    void RtBufferPool::Reset()
    {
        chunks.clear();
        retired.clear();
        reserved = used = 0;
    }

    void RtLinearArena::Setup(D3D12_HEAP_TYPE newHeap, D3D12_RESOURCE_FLAGS newFlags, UINT64 newBlockSize,
                              const wchar_t* newName)
    {
        heap = newHeap;
        flags = newFlags;
        blockSize = newBlockSize;
        name = newName;
    }

    bool RtLinearArena::Allocate(ID3D12Device* device, UINT64 size, UINT64 alignment, ID3D12Resource*& resource,
                                 UINT64& offset, D3D12_GPU_VIRTUAL_ADDRESS& address, uint8_t*& cpu)
    {
        Block* into = nullptr;
        for (Block& block : blocks)
            if (RoundUp(block.used, alignment) + size <= block.size)
            {
                into = &block;
                break;
            }
        if (into == nullptr)
        {
            Block block;
            block.size = RoundUp(std::max(size, blockSize), 65536);
            D3D12_HEAP_PROPERTIES properties = {};
            properties.Type = heap;
            const D3D12_RESOURCE_DESC desc = RtBufferDesc(block.size, flags);
            // Buffers start in COMMON whatever is asked for; an upload heap's in GENERIC_READ.
            const D3D12_RESOURCE_STATES initial =
                heap == D3D12_HEAP_TYPE_UPLOAD ? D3D12_RESOURCE_STATE_GENERIC_READ : D3D12_RESOURCE_STATE_COMMON;
            if (FAILED(device->CreateCommittedResource(&properties, D3D12_HEAP_FLAG_NONE, &desc, initial, nullptr,
                                                       IID_PPV_ARGS(&block.resource))))
                return false;
            block.resource->SetName(name);
            if (heap == D3D12_HEAP_TYPE_UPLOAD)
            {
                const D3D12_RANGE none = { 0, 0 };
                void* mapped = nullptr;
                if (FAILED(block.resource->Map(0, &none, &mapped)))
                    return false;
                block.mapped = static_cast<uint8_t*>(mapped);
            }
            blocks.push_back(std::move(block));
            into = &blocks.back();
        }
        offset = RoundUp(into->used, alignment);
        into->used = offset + size;
        resource = into->resource.Get();
        address = into->resource->GetGPUVirtualAddress() + offset;
        cpu = into->mapped != nullptr ? into->mapped + offset : nullptr;
        return true;
    }

    void RtLinearArena::Rewind()
    {
        // What a flight's first frame needed stays for the next, but no more than a few
        // blocks of the usual size: a block made for one large mesh goes.
        size_t kept = 0;
        for (auto it = blocks.begin(); it != blocks.end();)
        {
            if (kept >= 4 || it->size > RoundUp(blockSize, 65536))
                it = blocks.erase(it);
            else
            {
                it->used = 0;
                ++kept;
                ++it;
            }
        }
    }

    void RtLinearArena::Reset()
    {
        blocks.clear();
    }

    UINT64 RtLinearArena::Reserved() const
    {
        UINT64 total = 0;
        for (const Block& block : blocks)
            total += block.size;
        return total;
    }
}
