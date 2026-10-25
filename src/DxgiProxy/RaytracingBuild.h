#pragma once

// The memory the meshes' acceleration structures are built in (RaytracingScene.cpp,
// BuildMeshesLocked; Config.h, rtBlasPool). NVIDIA's "Best Practices for Using NVIDIA RTX
// Ray Tracing (Updated)": "many BLASes can be stored in a single buffer by manually
// suballocating sections from the buffer. This allows even tighter backing of BLASes
// into memory as the suballocations only have to follow 256-byte alignment."

#include <windows.h>

#include <d3d12.h>
#include <wrl/client.h>

#include <cstdint>
#include <map>
#include <vector>

namespace redefinition
{
    // A range of an RtBufferPool's chunk.
    struct RtPoolRange
    {
        // The chunk's index (UINT32_MAX for none), the range's start in it, and its size, a
        // multiple of the pool's granularity.
        uint32_t chunk = UINT32_MAX;
        UINT64 offset = 0;
        UINT64 size = 0;
        // Whether the range was handed out and not freed.
        bool Valid() const { return chunk != UINT32_MAX; }
    };

    // Default-heap buffers of one kind -- their flags and the state they are made in --
    // in chunks, handed out in ranges of a fixed granularity, first fit; a range freed is
    // free again once the queue is past the fence value it was freed with (Reclaim), and a
    // chunk that has been wholly free for kIdleMs is let go of, but the last (Reclaim).
    class RtBufferPool
    {
    public:
        // The kind of its buffers, the granularity of its ranges, the least size of a chunk,
        // and the name its buffers carry; before the first Allocate.
        void Setup(D3D12_RESOURCE_FLAGS flags, D3D12_RESOURCE_STATES state, UINT64 granularity, UINT64 chunkSize,
                   const wchar_t* name);
        // A range of at least size bytes, the first free one that fits, else from a new chunk;
        // false where the chunk could not be made.
        bool Allocate(ID3D12Device* device, UINT64 size, RtPoolRange& range);
        // The range free once the queue is past fenceValue; 0 frees it at once.
        void Free(RtPoolRange& range, UINT64 fenceValue);
        // The ranges freed with a fence value the queue is past (completed) free again, and
        // the chunks idle for kIdleMs let go of.
        void Reclaim(UINT64 completed);
        // How long a chunk stays wholly free before it goes: a few seconds of frames, so
        // that a vessel's parts built, let go of and built again keep the memory between.
        static constexpr ULONGLONG kIdleMs = 5000;
        // Everything let go of: the device's lists are done with it.
        void Reset();
        // The buffer a range lies in.
        ID3D12Resource* Resource(const RtPoolRange& range) const { return chunks[range.chunk].resource.Get(); }
        // A range's address on the GPU.
        D3D12_GPU_VIRTUAL_ADDRESS Address(const RtPoolRange& range) const
        {
            return chunks[range.chunk].address + range.offset;
        }
        // The bytes in the chunks it holds.
        UINT64 Reserved() const { return reserved; }
        // The bytes of them handed out, freed ranges not yet reclaimed among them.
        UINT64 Used() const { return used; }

    private:
        // One buffer of the pool and its free ranges.
        struct Chunk
        {
            // The buffer (null for a chunk let go of), its address and its size.
            Microsoft::WRL::ComPtr<ID3D12Resource> resource;
            D3D12_GPU_VIRTUAL_ADDRESS address = 0;
            UINT64 size = 0;
            // Free ranges by their start, coalesced.
            std::map<UINT64, UINT64> free;
            // When it was first seen wholly free, 0 while it is not.
            ULONGLONG idleSince = 0;
        };
        // The range back into its chunk's free ranges at once, joined with its neighbours.
        void Release(const RtPoolRange& range);
        // The chunks wholly free for kIdleMs let go of, the last one kept. A chunk's index
        // is in the ranges handed out, so it stays in chunks, empty, for a new one to take.
        void ReleaseIdle(ULONGLONG now);

        // Setup's values; the chunks; the ranges freed with their fence values, waiting for
        // Reclaim; the bytes reserved and used (Reserved, Used).
        D3D12_RESOURCE_FLAGS flags = D3D12_RESOURCE_FLAG_NONE;
        D3D12_RESOURCE_STATES state = D3D12_RESOURCE_STATE_COMMON;
        UINT64 granularity = 256;
        UINT64 chunkSize = 0;
        const wchar_t* name = L"";
        std::vector<Chunk> chunks;
        std::vector<std::pair<RtPoolRange, UINT64>> retired;
        UINT64 reserved = 0;
        UINT64 used = 0;
    };

    // Buffers a slot's list takes ranges of one after the other, all free again once its
    // list is done (Rewind): the uploads of a frame's meshes (an upload heap, mapped
    // throughout) and their builds' scratch memory (UNORDERED_ACCESS).
    class RtLinearArena
    {
    public:
        // The heap and flags of its blocks, the least size of a block, and the name its blocks
        // carry; before the first Allocate.
        void Setup(D3D12_HEAP_TYPE heap, D3D12_RESOURCE_FLAGS flags, UINT64 blockSize, const wchar_t* name);
        // A range of size bytes at the alignment; cpu is its mapped address for an upload heap.
        bool Allocate(ID3D12Device* device, UINT64 size, UINT64 alignment, ID3D12Resource*& resource, UINT64& offset,
                      D3D12_GPU_VIRTUAL_ADDRESS& address, uint8_t*& cpu);
        // The slot's list is done: its ranges are free. Blocks past the first few go.
        void Rewind();
        // All blocks let go of: the device's lists are done with them.
        void Reset();
        // The bytes of all its blocks.
        UINT64 Reserved() const;

    private:
        // One buffer of the arena.
        struct Block
        {
            // The buffer, its mapped address (upload heap only), its size, the bytes taken.
            Microsoft::WRL::ComPtr<ID3D12Resource> resource;
            uint8_t* mapped = nullptr;
            UINT64 size = 0;
            UINT64 used = 0;
        };
        // Setup's values, and the blocks, taken in order.
        D3D12_HEAP_TYPE heap = D3D12_HEAP_TYPE_UPLOAD;
        D3D12_RESOURCE_FLAGS flags = D3D12_RESOURCE_FLAG_NONE;
        UINT64 blockSize = 0;
        const wchar_t* name = L"";
        std::vector<Block> blocks;
    };

    // A buffer one of the two ways: a resource of its own, or a range of a pool.
    struct RtStoredBuffer
    {
        // The resource of its own or the pool's range, whichever it is; its address and size.
        Microsoft::WRL::ComPtr<ID3D12Resource> committed;
        RtPoolRange range;
        D3D12_GPU_VIRTUAL_ADDRESS address = 0;
        UINT64 size = 0;
        // Whether it holds memory either way.
        bool Valid() const { return committed != nullptr || range.Valid(); }
    };
}
