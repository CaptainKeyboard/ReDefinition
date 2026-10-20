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
        uint32_t chunk = UINT32_MAX;
        UINT64 offset = 0;
        UINT64 size = 0;
        bool Valid() const { return chunk != UINT32_MAX; }
    };

    // Default-heap buffers of one kind -- their flags and the state they are made in --
    // in chunks, handed out in ranges of a fixed granularity, first fit; a range freed is
    // free again once the queue is past the fence value it was freed with (Reclaim).
    class RtBufferPool
    {
    public:
        void Setup(D3D12_RESOURCE_FLAGS flags, D3D12_RESOURCE_STATES state, UINT64 granularity, UINT64 chunkSize,
                   const wchar_t* name);
        bool Allocate(ID3D12Device* device, UINT64 size, RtPoolRange& range);
        // The range free once the queue is past fenceValue; 0 frees it at once.
        void Free(RtPoolRange& range, UINT64 fenceValue);
        void Reclaim(UINT64 completed);
        // Everything let go of: the device's lists are done with it.
        void Reset();
        ID3D12Resource* Resource(const RtPoolRange& range) const { return chunks[range.chunk].resource.Get(); }
        D3D12_GPU_VIRTUAL_ADDRESS Address(const RtPoolRange& range) const
        {
            return chunks[range.chunk].address + range.offset;
        }
        UINT64 Reserved() const { return reserved; }
        UINT64 Used() const { return used; }

    private:
        struct Chunk
        {
            Microsoft::WRL::ComPtr<ID3D12Resource> resource;
            D3D12_GPU_VIRTUAL_ADDRESS address = 0;
            UINT64 size = 0;
            // Free ranges by their start, coalesced.
            std::map<UINT64, UINT64> free;
        };
        void Release(const RtPoolRange& range);

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
        void Setup(D3D12_HEAP_TYPE heap, D3D12_RESOURCE_FLAGS flags, UINT64 blockSize, const wchar_t* name);
        // A range of size bytes at the alignment; cpu is its mapped address for an upload heap.
        bool Allocate(ID3D12Device* device, UINT64 size, UINT64 alignment, ID3D12Resource*& resource, UINT64& offset,
                      D3D12_GPU_VIRTUAL_ADDRESS& address, uint8_t*& cpu);
        // The slot's list is done: its ranges are free. Blocks past the first few go.
        void Rewind();
        void Reset();
        UINT64 Reserved() const;

    private:
        struct Block
        {
            Microsoft::WRL::ComPtr<ID3D12Resource> resource;
            uint8_t* mapped = nullptr;
            UINT64 size = 0;
            UINT64 used = 0;
        };
        D3D12_HEAP_TYPE heap = D3D12_HEAP_TYPE_UPLOAD;
        D3D12_RESOURCE_FLAGS flags = D3D12_RESOURCE_FLAG_NONE;
        UINT64 blockSize = 0;
        const wchar_t* name = L"";
        std::vector<Block> blocks;
    };

    // A buffer one of the two ways: a resource of its own, or a range of a pool.
    struct RtStoredBuffer
    {
        Microsoft::WRL::ComPtr<ID3D12Resource> committed;
        RtPoolRange range;
        D3D12_GPU_VIRTUAL_ADDRESS address = 0;
        UINT64 size = 0;
        bool Valid() const { return committed != nullptr || range.Valid(); }
    };
}
