// The raytracing service's scene: the meshes and their acceleration structures, the
// material textures, and each frame's instances, the CPU's and the GPU's, in the scene's
// acceleration structure.

#include "Raytracing.h"

#include "Config.h"
#include "D3d12Util.h"
#include "Log.h"
#include "RaytracingInternal.h"

#include <algorithm>
#include <cmath>
#include <cstring>
#include <deque>

using Microsoft::WRL::ComPtr;

namespace redefinition
{
    namespace
    {
        // The inverse transpose of a 3x3 given as the first three columns of a 3x4,
        // rows first, into three padded rows.
        void NormalMatrix(const float* m, float* out)
        {
            const float a = m[0], b = m[1], c = m[2];
            const float d = m[4], e = m[5], f = m[6];
            const float g = m[8], h = m[9], i = m[10];
            const float A = e * i - f * h, B = -(d * i - f * g), C = d * h - e * g;
            const float D = -(b * i - c * h), E = a * i - c * g, F = -(a * h - b * g);
            const float G = b * f - c * e, H = -(a * f - c * d), I = a * e - b * d;
            const float det = a * A + b * B + c * C;
            const float s = std::fabs(det) > 1e-20f ? 1.0f / det : 0.0f;
            // inverse = adjugate / det; its transpose has the cofactors in place.
            const float rows[12] = { A * s, B * s, C * s, 0.0f, D * s, E * s, F * s, 0.0f, G * s, H * s, I * s, 0.0f };
            memcpy(out, rows, sizeof(rows));
        }
    }

    bool Raytracing::TakeDescriptorSlotLocked(uint32_t& slot)
    {
        // Slots retired by lists the queue is past come back first.
        const UINT64 completed = fence12 != nullptr ? fence12->GetCompletedValue() : 0;
        for (auto it = retiredSlots.begin(); it != retiredSlots.end();)
        {
            if (it->second <= completed)
            {
                freeSlots.push_back(it->first);
                it = retiredSlots.erase(it);
            }
            else
                ++it;
        }
        if (freeSlots.empty())
            return false;
        slot = freeSlots.back();
        freeSlots.pop_back();
        return true;
    }

    void Raytracing::RetireBlasLocked(RtStoredBuffer& blas, Slot& slot)
    {
        if (blas.committed != nullptr)
        {
            slot.held.push_back(blas.committed);
            blasMemory.committedBlas -= blas.size;
        }
        blasMemory.blas.Free(blas.range, fenceValue + 1);
        blas = RtStoredBuffer{};
    }

    void Raytracing::RetireMeshLocked(Mesh& mesh, Slot& slot)
    {
        slot.held.push_back(mesh.positions);
        slot.held.push_back(mesh.attributes);
        slot.held.push_back(mesh.indices);
        slot.held.push_back(mesh.updateScratch);
        blasMemory.committedGeometry -= mesh.geometryBytes;
        mesh.geometryBytes = 0;
        blasMemory.geometry.Free(mesh.geometry, fenceValue + 1);
        RetireBlasLocked(mesh.blas, slot);
        retiredSlots.emplace_back(mesh.slot, fenceValue + 1);
    }

    void Raytracing::FreeMeshNowLocked(Mesh& mesh)
    {
        blasMemory.committedGeometry -= mesh.geometryBytes;
        mesh.geometryBytes = 0;
        blasMemory.geometry.Free(mesh.geometry, 0);
        if (mesh.blas.committed != nullptr)
            blasMemory.committedBlas -= mesh.blas.size;
        blasMemory.blas.Free(mesh.blas.range, 0);
        mesh.blas = RtStoredBuffer{};
        freeSlots.push_back(mesh.slot);
    }

    void Raytracing::BlasMemory::Reset()
    {
        blas.Reset();
        geometry.Reset();
        committedBlas = committedGeometry = 0;
        for (PerSlot& slot : slots)
            slot = PerSlot{};
        toRefine.clear();
        setUp = false;
    }

    bool Raytracing::IsCutoutLocked(int32_t material) const
    {
        const auto found = materialData.find(material);
        return found != materialData.end() && found->second.albedo[3] > 0.0f;
    }

    namespace
    {
        UINT64 RoundUp(UINT64 value, UINT64 granularity)
        {
            return (value + granularity - 1) / granularity * granularity;
        }

        // The most compacted sizes one list asks for: one a mesh.
        constexpr UINT kMaxSizesAsked = 16384;

        D3D12_RAYTRACING_GEOMETRY_DESC GeometryOf(D3D12_GPU_VIRTUAL_ADDRESS positions, uint32_t vertexCount,
                                                  D3D12_GPU_VIRTUAL_ADDRESS indices, uint32_t indexCount)
        {
            D3D12_RAYTRACING_GEOMETRY_DESC geometry = {};
            geometry.Type = D3D12_RAYTRACING_GEOMETRY_TYPE_TRIANGLES;
            geometry.Flags = D3D12_RAYTRACING_GEOMETRY_FLAG_OPAQUE;
            geometry.Triangles.VertexBuffer.StartAddress = positions;
            geometry.Triangles.VertexBuffer.StrideInBytes = 3 * sizeof(float);
            geometry.Triangles.VertexFormat = DXGI_FORMAT_R32G32B32_FLOAT;
            geometry.Triangles.VertexCount = vertexCount;
            geometry.Triangles.IndexBuffer = indices;
            geometry.Triangles.IndexFormat = DXGI_FORMAT_R32_UINT;
            geometry.Triangles.IndexCount = indexCount;
            return geometry;
        }
    }

    // One frame's work on the meshes' structures. With rtBlasBatch the copies of all its
    // meshes come first, then one barrier for all the buffers they wrote, then every build
    // back to back, each with scratch memory of its own, and the one UAV barrier before the
    // scene's structure (BuildMeshesLocked) -- NVIDIA, "Best Practices for Using NVIDIA RTX
    // Ray Tracing (Updated)": "Consecutively execute ... all BLAS build calls. This allows
    // the driver to parallelize the calls to an extent. Do not place resource barriers
    // between consecutive calls. All BLAS build calls need unique scratch memory to allow
    // execution without barriers. ... you can have a single global UAV barrier before TLAS
    // build to ensure all BLAS builds are completed, regardless of the resource where they
    // reside." Without it each mesh's copies, barrier and build go in turn.
    struct Raytracing::BuildFrame
    {
        UINT slotNumber = 0;
        bool batched = true;
        bool pooled = true;
        int compaction = 0;
        // The mesh being prepared, for the compacted size its build asks for.
        int32_t meshId = 0;
        // The geometry buffers' states on this list. Buffers decay to COMMON once a list is
        // done on the GPU ("Using Resource Barriers to Synchronize Resource States in
        // Direct3D 12", State decay to common), and a copy promotes them to COPY_DEST.
        std::unordered_map<ID3D12Resource*, D3D12_RESOURCE_STATES> states;
        std::vector<D3D12_RESOURCE_BARRIER> barriers;
        // The geometry buffers the queued builds read.
        std::vector<ID3D12Resource*> reads;
        // The copies from the uploads, one where they follow each other in both buffers.
        struct Copy
        {
            ID3D12Resource* destination;
            UINT64 destinationOffset;
            ID3D12Resource* source;
            UINT64 sourceOffset;
            UINT64 size;
        };
        std::vector<Copy> copies;
        void CopyInto(ID3D12Resource* destination, UINT64 destinationOffset, ID3D12Resource* source, UINT64 sourceOffset,
                      UINT64 size)
        {
            if (!copies.empty())
            {
                Copy& last = copies.back();
                if (last.destination == destination && last.source == source
                    && last.destinationOffset + last.size == destinationOffset && last.sourceOffset + last.size == sourceOffset)
                {
                    last.size += size;
                    return;
                }
            }
            copies.push_back({ destination, destinationOffset, source, sourceOffset, size });
        }
        struct Build
        {
            D3D12_RAYTRACING_GEOMETRY_DESC geometry = {};
            D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_DESC desc = {};
            bool askSize = false;
            UINT sizeIndex = 0;
        };
        // A deque, so that a build's inputs keep pointing at its geometry.
        std::deque<Build> builds;
        UINT sizesAsked = 0;
        ID3D12Resource* sizes = nullptr;
    };

    void Raytracing::ToCopyLocked(BuildFrame& frame, ID3D12Resource* buffer)
    {
        D3D12_RESOURCE_STATES& state = frame.states.emplace(buffer, D3D12_RESOURCE_STATE_COMMON).first->second;
        if (state == D3D12_RESOURCE_STATE_COPY_DEST)
            return;
        if (state == D3D12_RESOURCE_STATE_COMMON)
        {
            // The copy promotes it.
            state = D3D12_RESOURCE_STATE_COPY_DEST;
            return;
        }
        // Readable for builds queued before: they go first, then it is written again (a
        // range pool's buffer, mesh by mesh without rtBlasBatch).
        FlushBuildsLocked(frame);
        const D3D12_RESOURCE_BARRIER back = TransitionBarrier(buffer, state, D3D12_RESOURCE_STATE_COPY_DEST);
        list->ResourceBarrier(1, &back);
        state = D3D12_RESOURCE_STATE_COPY_DEST;
    }

    void Raytracing::ToReadLocked(BuildFrame& frame, ID3D12Resource* buffer)
    {
        // A build reads its vertices and indices in NON_PIXEL_SHADER_RESOURCE (DirectX-Specs,
        // Raytracing, D3D12_RAYTRACING_GEOMETRY_TRIANGLES_DESC: "The memory pointed to must be
        // in state D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE"), as the passes read them;
        // the barrier comes with the builds (FlushBuildsLocked), so that the next mesh's copy
        // into the same pool buffer finds it still in COPY_DEST.
        if (std::find(frame.reads.begin(), frame.reads.end(), buffer) == frame.reads.end())
            frame.reads.push_back(buffer);
    }

    void Raytracing::FlushBuildsLocked(BuildFrame& frame)
    {
        for (const BuildFrame::Copy& copy : frame.copies)
            list->CopyBufferRegion(copy.destination, copy.destinationOffset, copy.source, copy.sourceOffset, copy.size);
        frame.copies.clear();
        for (ID3D12Resource* buffer : frame.reads)
        {
            D3D12_RESOURCE_STATES& state = frame.states.emplace(buffer, D3D12_RESOURCE_STATE_COMMON).first->second;
            if (state != D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE)
                frame.barriers.push_back(TransitionBarrier(buffer, state, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE));
            state = D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE;
        }
        frame.reads.clear();
        if (!frame.barriers.empty())
        {
            list->ResourceBarrier(static_cast<UINT>(frame.barriers.size()), frame.barriers.data());
            frame.barriers.clear();
        }
        for (BuildFrame::Build& build : frame.builds)
        {
            // The compacted size as a further output of the build, "without extra barrier
            // calls between doing the build and getting postbuild info" (DirectX-Specs,
            // Raytracing, v0.91).
            D3D12_RAYTRACING_ACCELERATION_STRUCTURE_POSTBUILD_INFO_DESC info = {};
            if (build.askSize)
            {
                info.DestBuffer = frame.sizes->GetGPUVirtualAddress() + static_cast<UINT64>(build.sizeIndex) * sizeof(UINT64);
                info.InfoType = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_POSTBUILD_INFO_COMPACTED_SIZE;
            }
            list->BuildRaytracingAccelerationStructure(&build.desc, build.askSize ? 1 : 0, build.askSize ? &info : nullptr);
        }
        frame.builds.clear();
    }

    bool Raytracing::UploadSpaceLocked(BuildFrame& frame, Slot& slot, UINT64 size, ID3D12Resource*& buffer, UINT64& offset,
                                      uint8_t*& cpu)
    {
        if (frame.pooled)
        {
            D3D12_GPU_VIRTUAL_ADDRESS unused = 0;
            return blasMemory.slots[frame.slotNumber].upload.Allocate(device.Get(), size, 16, buffer, offset, unused, cpu);
        }
        ComPtr<ID3D12Resource> upload;
        void* mapped = nullptr;
        const D3D12_RANGE none = { 0, 0 };
        if (!EnsureUploadBuffer(upload, size) || FAILED(upload->Map(0, &none, &mapped)))
            return false;
        slot.held.push_back(upload);
        buffer = upload.Get();
        offset = 0;
        cpu = static_cast<uint8_t*>(mapped);
        return true;
    }

    bool Raytracing::ScratchLocked(BuildFrame& frame, Slot& slot, UINT64 size, D3D12_GPU_VIRTUAL_ADDRESS& address)
    {
        // "The address must be aligned to 256 bytes
        // (D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BYTE_ALIGNMENT). ... The memory pointed
        // to must be in state D3D12_RESOURCE_STATE_UNORDERED_ACCESS" (DirectX-Specs,
        // Raytracing, D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_DESC), to which a buffer
        // in COMMON is promoted.
        if (frame.pooled)
        {
            ID3D12Resource* buffer = nullptr;
            UINT64 offset = 0;
            uint8_t* cpu = nullptr;
            return blasMemory.slots[frame.slotNumber].scratch.Allocate(
                device.Get(), std::max<UINT64>(size, 256), D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BYTE_ALIGNMENT, buffer,
                offset, address, cpu);
        }
        ComPtr<ID3D12Resource> scratch;
        if (!EnsureDefaultBuffer(scratch, size, D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS))
            return false;
        slot.held.push_back(scratch);
        address = scratch->GetGPUVirtualAddress();
        return true;
    }

    bool Raytracing::MakeBlasLocked(UINT64 size, bool pooled, RtStoredBuffer& blas)
    {
        blas = RtStoredBuffer{};
        if (pooled)
        {
            if (!blasMemory.blas.Allocate(device.Get(), size, blas.range))
                return false;
            blas.address = blasMemory.blas.Address(blas.range);
            blas.size = blas.range.size;
            return true;
        }
        if (!EnsureDefaultBuffer(blas.committed, size, D3D12_RESOURCE_STATE_RAYTRACING_ACCELERATION_STRUCTURE,
                                 D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS))
            return false;
        blas.address = blas.committed->GetGPUVirtualAddress();
        blas.size = RoundUp(std::max<UINT64>(size, 256), 65536);
        blasMemory.committedBlas += blas.size;
        return true;
    }

    uint32_t Raytracing::BuildMeshesLocked(Slot& slot)
    {
        LARGE_INTEGER frequency = {}, start = {}, end = {};
        QueryPerformanceFrequency(&frequency);
        QueryPerformanceCounter(&start);

        const UINT64 completed = fence12 != nullptr ? fence12->GetCompletedValue() : 0;
        if (!blasMemory.setUp)
        {
            // The structures in RAYTRACING_ACCELERATION_STRUCTURE, which they never leave,
            // at 256 bytes; the geometry at 48, a multiple of a raw view's 16
            // (D3D12_RAW_UAV_SRV_BYTE_ALIGNMENT) and of the attributes' 24-byte stride, for
            // the views' first elements.
            blasMemory.blas.Setup(D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS,
                                  D3D12_RESOURCE_STATE_RAYTRACING_ACCELERATION_STRUCTURE,
                                  D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BYTE_ALIGNMENT, 32ull << 20,
                                  L"ReDefinition raytracing structures");
            blasMemory.geometry.Setup(D3D12_RESOURCE_FLAG_NONE, D3D12_RESOURCE_STATE_COMMON, 48, 16ull << 20,
                                      L"ReDefinition raytracing geometry");
            for (BlasMemory::PerSlot& perSlot : blasMemory.slots)
            {
                perSlot.upload.Setup(D3D12_HEAP_TYPE_UPLOAD, D3D12_RESOURCE_FLAG_NONE, 4ull << 20,
                                     L"ReDefinition raytracing uploads");
                perSlot.scratch.Setup(D3D12_HEAP_TYPE_DEFAULT, D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS, 8ull << 20,
                                      L"ReDefinition raytracing scratch");
            }
            blasMemory.setUp = true;
        }
        blasMemory.blas.Reclaim(completed);
        blasMemory.geometry.Reclaim(completed);

        BuildFrame frame;
        frame.slotNumber = static_cast<UINT>(&slot - slots);
        frame.batched = Config().rtBlasBatch;
        frame.pooled = Config().rtBlasPool;
        frame.compaction = Config().rtBlasCompaction;
        BlasMemory::PerSlot& perSlot = blasMemory.slots[frame.slotNumber];
        // The slot's list is done: its uploads and scratch are free again.
        perSlot.upload.Rewind();
        perSlot.scratch.Rewind();
        perSlot.changed = 0;
        perSlot.builtMeshes = 0;
        perSlot.builtTriangles = 0;

        // Meshes: releases first, then the structures compacted whose sizes came back, then
        // what is new, each into its own acceleration structure.
        for (int32_t id : pendingReleases)
        {
            auto found = meshes.find(id);
            if (found == meshes.end())
                continue;
            RetireMeshLocked(found->second, slot);
            meshes.erase(found);
        }
        pendingReleases.clear();
        uint32_t changed = CompactMeshesLocked(frame, slot);

        // SetMesh told the managed side the mesh is taken, and it does not hand it over
        // again: a mesh leaves the pending ones once it is built. Where the descriptor
        // slots run out, it and the rest wait for a later frame; one whose buffers
        // cannot be made is tried again in later frames, after the others, and
        // dropped after kMeshAttempts. Past the frame's budget of triangles
        // (rtBlasBudget), the rest wait for the next frames.
        const uint64_t budget = static_cast<uint64_t>(std::max(0, Config().rtBlasBudget));
        uint32_t built = 0;
        uint32_t uploaded = 0;
        uint64_t builtIndices = 0;
        uint64_t builtVertices = 0;
        uint64_t largestIndices = 0;
        for (auto entry = pendingMeshes.begin(); entry != pendingMeshes.end();)
        {
            if (budget > 0 && built > 0 && (builtIndices + entry->second.indices.size()) / 3 > budget)
                break;
            frame.meshId = entry->first;
            // Handed over again with the same triangles (a skinned part's bake, the KSC's
            // flags twice a second): into the buffers it has, its structure updated.
            auto existing = meshes.find(entry->first);
            if (existing != meshes.end() && existing->second.built
                && existing->second.vertexCount == entry->second.vertexCount
                && existing->second.indexCount == entry->second.indices.size()
                && existing->second.indexHash == entry->second.indexHash
                && RefitMeshLocked(frame, existing->second, entry->second, slot))
            {
                if (!frame.batched)
                    FlushBuildsLocked(frame);
                builtIndices += entry->second.indices.size();
                builtVertices += entry->second.vertexCount;
                largestIndices = std::max<uint64_t>(largestIndices, entry->second.indices.size());
                entry = pendingMeshes.erase(entry);
                ++built;
                continue;
            }
            Mesh mesh;
            if (!TakeDescriptorSlotLocked(mesh.slot))
            {
                if (!meshSlotsFull)
                {
                    meshSlotsFull = true;
                    SetProblem("more meshes than the service holds (" + std::to_string(kMaxMeshes)
                               + "); the rest wait until meshes are released");
                }
                break;
            }
            if (!UploadMeshLocked(frame, mesh, entry->second, slot))
            {
                // Nothing of it is queued; the list may already copy into buffers made
                // before the failure.
                RetireMeshLocked(mesh, slot);
                if (++entry->second.failures >= kMeshAttempts)
                {
                    SetProblem("a mesh could not be built in " + std::to_string(kMeshAttempts)
                               + " frames and is left out");
                    entry = pendingMeshes.erase(entry);
                }
                else
                    ++entry;
                continue;
            }
            if (!frame.batched)
                FlushBuildsLocked(frame);
            if (existing != meshes.end())
                RetireMeshLocked(existing->second, slot);
            if (mesh.refine == kRefineRebuild)
                blasMemory.toRefine.push_back(entry->first);
            meshes[entry->first] = std::move(mesh);
            builtIndices += entry->second.indices.size();
            builtVertices += entry->second.vertexCount;
            largestIndices = std::max<uint64_t>(largestIndices, entry->second.indices.size());
            entry = pendingMeshes.erase(entry);
            ++built;
            ++uploaded;
        }
        // The structures built fast rebuilt for tracing, in frames that build no new mesh;
        // the refits, a skinned part's every frame, do not hold them back.
        if (uploaded == 0)
            changed += RefineMeshesLocked(frame, slot);
        FlushBuildsLocked(frame);
        // The compacted sizes asked for back to the CPU, read when the slot comes round
        // again (CompactMeshesLocked).
        if (frame.sizesAsked > 0)
        {
            const D3D12_RESOURCE_BARRIER toCopy = TransitionBarrier(perSlot.sizes.Get(), D3D12_RESOURCE_STATE_UNORDERED_ACCESS,
                                                                    D3D12_RESOURCE_STATE_COPY_SOURCE);
            list->ResourceBarrier(1, &toCopy);
            list->CopyBufferRegion(perSlot.sizesReadback.Get(), 0, perSlot.sizes.Get(), 0,
                                   static_cast<UINT64>(frame.sizesAsked) * sizeof(UINT64));
        }
        QueryPerformanceCounter(&end);
        const double cpuMs = static_cast<double>(end.QuadPart - start.QuadPart) * 1000.0 / static_cast<double>(frequency.QuadPart);
        perSlot.builtMeshes = built;
        perSlot.builtTriangles = builtIndices / 3;
        perSlot.cpuMs = cpuMs;
        // Many at once, a few times a session: what they are, for the frame's time
        // (the slow frame's line, RaytracingDevice; the GPU's, NoteMeshBuildTimeLocked).
        if (built >= 50 && bigBuildsLogged < 10)
        {
            ++bigBuildsLogged;
            LogLine("Raytracing: " + std::to_string(built) + " meshes built in one frame, " + std::to_string(builtIndices / 3)
                    + " triangles, " + std::to_string(builtVertices) + " vertices, the largest "
                    + std::to_string(largestIndices / 3) + " triangles, " + std::to_string(cpuMs)
                    + " ms of the CPU (rtBlasBatch=" + (frame.batched ? "1" : "0") + " rtBlasPool=" + (frame.pooled ? "1" : "0")
                    + " rtBlasBudget=" + std::to_string(budget) + " rtBlasCompaction=" + std::to_string(frame.compaction) + ")");
        }
        changed += built;
        perSlot.changed = changed;
        if (changed > 0)
        {
            D3D12_RESOURCE_BARRIER barrier = UavBarrier(nullptr);
            list->ResourceBarrier(1, &barrier);
        }
        {
            std::lock_guard<std::mutex> status(statusLock);
            if (built > 0)
                meshBuildCount = built;
            meshBuildCpuMs = static_cast<float>(cpuMs);
            blasBytes = blasMemory.blas.Used() + blasMemory.committedBlas;
            geometryBytes = blasMemory.geometry.Used() + blasMemory.committedGeometry;
            buildReservedBytes = blasMemory.blas.Reserved() + blasMemory.geometry.Reserved();
            for (const BlasMemory::PerSlot& each : blasMemory.slots)
                buildReservedBytes += each.upload.Reserved() + each.scratch.Reserved();
        }
        return changed;
    }

    void Raytracing::NoteMeshBuildTimeLocked(UINT slotNumber, double ms)
    {
        // Under statusLock, from WaitForSlotLocked: the GPU's time from the list's start to
        // its meshes built.
        BlasMemory::PerSlot& perSlot = blasMemory.slots[slotNumber];
        if (perSlot.changed == 0)
            return;
        meshBuildGpuMs = static_cast<float>(ms);
        ++meshBuildsTimed;
        if (perSlot.builtMeshes >= 50 && bigBuildsTimedLogged < 10)
        {
            ++bigBuildsTimedLogged;
            LogLine("Raytracing: the " + std::to_string(perSlot.builtMeshes) + " meshes built in one frame ("
                    + std::to_string(perSlot.builtTriangles) + " triangles) took " + std::to_string(ms)
                    + " ms of the GPU and " + std::to_string(perSlot.cpuMs) + " ms of the CPU");
        }
        perSlot.changed = 0;
        perSlot.builtMeshes = 0;
    }

    bool Raytracing::UploadMeshLocked(BuildFrame& frame, Mesh& mesh, const PendingMesh& pending, Slot& slot)
    {
        const UINT64 positionsSize = pending.positions.size() * sizeof(float);
        const UINT64 attributesSize = pending.attributes.size() * sizeof(float);
        const UINT64 indicesSize = pending.indices.size() * sizeof(uint32_t);
        // In a range of the geometry pool: the attributes, the positions, the indices, each
        // at 16 bytes; the upload in the same layout.
        const UINT64 positionsAt = RoundUp(attributesSize, 16);
        const UINT64 indicesAt = RoundUp(positionsAt + positionsSize, 16);
        const UINT64 total = indicesAt + indicesSize;

        HRESULT hr = S_OK;
        if (frame.pooled)
        {
            if (!blasMemory.geometry.Allocate(device.Get(), total, mesh.geometry))
                hr = E_OUTOFMEMORY;
            else
            {
                ID3D12Resource* buffer = blasMemory.geometry.Resource(mesh.geometry);
                mesh.attributesBuffer = mesh.positionsBuffer = mesh.indicesBuffer = buffer;
                mesh.attributesOffset = mesh.geometry.offset;
                mesh.positionsOffset = mesh.geometry.offset + positionsAt;
                mesh.indicesOffset = mesh.geometry.offset + indicesAt;
            }
        }
        else
        {
            D3D12_HEAP_PROPERTIES defaultHeap = {};
            defaultHeap.Type = D3D12_HEAP_TYPE_DEFAULT;
            const D3D12_RESOURCE_DESC positionsDesc = RtBufferDesc(positionsSize, D3D12_RESOURCE_FLAG_NONE);
            const D3D12_RESOURCE_DESC attributesDesc = RtBufferDesc(attributesSize, D3D12_RESOURCE_FLAG_NONE);
            const D3D12_RESOURCE_DESC indicesDesc = RtBufferDesc(indicesSize, D3D12_RESOURCE_FLAG_NONE);
            // Buffers start in COMMON whatever is asked for, and the copy promotes them
            // to COPY_DEST.
            hr = device->CreateCommittedResource(&defaultHeap, D3D12_HEAP_FLAG_NONE, &positionsDesc,
                                                 D3D12_RESOURCE_STATE_COMMON, nullptr, IID_PPV_ARGS(&mesh.positions));
            if (SUCCEEDED(hr))
                hr = device->CreateCommittedResource(&defaultHeap, D3D12_HEAP_FLAG_NONE, &attributesDesc,
                                                     D3D12_RESOURCE_STATE_COMMON, nullptr, IID_PPV_ARGS(&mesh.attributes));
            if (SUCCEEDED(hr))
                hr = device->CreateCommittedResource(&defaultHeap, D3D12_HEAP_FLAG_NONE, &indicesDesc,
                                                     D3D12_RESOURCE_STATE_COMMON, nullptr, IID_PPV_ARGS(&mesh.indices));
            if (SUCCEEDED(hr))
            {
                mesh.positionsBuffer = mesh.positions.Get();
                mesh.attributesBuffer = mesh.attributes.Get();
                mesh.indicesBuffer = mesh.indices.Get();
                mesh.positionsOffset = mesh.attributesOffset = mesh.indicesOffset = 0;
                // A committed buffer takes at least 64 KB (D3D12_DEFAULT_RESOURCE_PLACEMENT_ALIGNMENT).
                mesh.geometryBytes = RoundUp(positionsSize, 65536) + RoundUp(attributesSize, 65536)
                                     + RoundUp(indicesSize, 65536);
                blasMemory.committedGeometry += mesh.geometryBytes;
            }
        }
        if (FAILED(hr))
        {
            SetProblem("a mesh's buffers could not be made (" + Hr(hr) + ")");
            return false;
        }

        BuildFrame::Build build;
        build.geometry = GeometryOf(mesh.positionsBuffer->GetGPUVirtualAddress() + mesh.positionsOffset, pending.vertexCount,
                                    mesh.indicesBuffer->GetGPUVirtualAddress() + mesh.indicesOffset,
                                    static_cast<UINT>(pending.indices.size()));
        // rtBlasCompaction 2: built fast now, for tracing later (RefineMeshesLocked); 1: for
        // tracing, compacted once its size is back (CompactMeshesLocked).
        D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_INPUTS& inputs = build.desc.Inputs;
        inputs.Type = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_TYPE_BOTTOM_LEVEL;
        inputs.Flags = frame.compaction == 2 ? D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BUILD_FLAG_PREFER_FAST_BUILD
                                             : D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BUILD_FLAG_PREFER_FAST_TRACE;
        if (frame.compaction == 1)
            inputs.Flags |= D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BUILD_FLAG_ALLOW_COMPACTION;
        inputs.NumDescs = 1;
        inputs.DescsLayout = D3D12_ELEMENTS_LAYOUT_ARRAY;
        inputs.pGeometryDescs = &build.geometry;
        D3D12_RAYTRACING_ACCELERATION_STRUCTURE_PREBUILD_INFO info = {};
        device->GetRaytracingAccelerationStructurePrebuildInfo(&inputs, &info);
        if (info.ResultDataMaxSizeInBytes == 0)
        {
            SetProblem("a mesh's acceleration structure has no size");
            return false;
        }

        // Everything made before anything is recorded.
        ID3D12Resource* upload = nullptr;
        UINT64 uploadOffset = 0;
        uint8_t* mapped = nullptr;
        if (!MakeBlasLocked(info.ResultDataMaxSizeInBytes, frame.pooled, mesh.blas)
            || !ScratchLocked(frame, slot, info.ScratchDataSizeInBytes, build.desc.ScratchAccelerationStructureData)
            || !UploadSpaceLocked(frame, slot, frame.pooled ? mesh.geometry.size : total, upload, uploadOffset, mapped))
        {
            SetProblem("a mesh's acceleration structure could not be made");
            return false;
        }
        memcpy(mapped, pending.attributes.data(), static_cast<size_t>(attributesSize));
        memcpy(mapped + positionsAt, pending.positions.data(), static_cast<size_t>(positionsSize));
        memcpy(mapped + indicesAt, pending.indices.data(), static_cast<size_t>(indicesSize));

        if (frame.pooled)
        {
            ToCopyLocked(frame, mesh.attributesBuffer);
            frame.CopyInto(mesh.attributesBuffer, mesh.attributesOffset, upload, uploadOffset, mesh.geometry.size);
        }
        else
        {
            for (ID3D12Resource* buffer : { mesh.positionsBuffer, mesh.attributesBuffer, mesh.indicesBuffer })
                ToCopyLocked(frame, buffer);
            frame.CopyInto(mesh.positionsBuffer, 0, upload, uploadOffset + positionsAt, positionsSize);
            frame.CopyInto(mesh.attributesBuffer, 0, upload, uploadOffset, attributesSize);
            frame.CopyInto(mesh.indicesBuffer, 0, upload, uploadOffset + indicesAt, indicesSize);
        }
        for (ID3D12Resource* buffer : { mesh.positionsBuffer, mesh.attributesBuffer, mesh.indicesBuffer })
            ToReadLocked(frame, buffer);

        build.desc.DestAccelerationStructureData = mesh.blas.address;
        mesh.blasSerial = blasMemory.nextSerial++;
        mesh.refine = frame.compaction == 2 ? kRefineRebuild : kRefineNone;
        frame.builds.push_back(build);
        // The queued copy's geometry, where the deque holds it.
        BuildFrame::Build& queued = frame.builds.back();
        queued.desc.Inputs.pGeometryDescs = &queued.geometry;
        const UINT index = frame.sizesAsked;
        if (frame.compaction == 1 && AskSizeLocked(frame, frame.meshId, mesh.blasSerial))
        {
            queued.askSize = true;
            queued.sizeIndex = index;
            mesh.refine = kRefineCompact;
        }

        // The index and attribute views the passes read at a hit, from where the mesh's
        // lie in their buffers.
        const D3D12_CPU_DESCRIPTOR_HANDLE heapCpu = heap->GetCPUDescriptorHandleForHeapStart();
        D3D12_CPU_DESCRIPTOR_HANDLE indexView = heapCpu;
        indexView.ptr += static_cast<SIZE_T>(kSlots * kFrameDescriptors + mesh.slot) * increment;
        D3D12_SHADER_RESOURCE_VIEW_DESC raw = {};
        raw.Format = DXGI_FORMAT_R32_TYPELESS;
        raw.ViewDimension = D3D12_SRV_DIMENSION_BUFFER;
        raw.Shader4ComponentMapping = D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING;
        raw.Buffer.FirstElement = mesh.indicesOffset / sizeof(uint32_t);
        raw.Buffer.NumElements = static_cast<UINT>(pending.indices.size());
        raw.Buffer.Flags = D3D12_BUFFER_SRV_FLAG_RAW;
        device->CreateShaderResourceView(mesh.indicesBuffer, &raw, indexView);
        D3D12_CPU_DESCRIPTOR_HANDLE attributeView = heapCpu;
        attributeView.ptr += static_cast<SIZE_T>(kSlots * kFrameDescriptors + kMaxMeshes + mesh.slot) * increment;
        D3D12_SHADER_RESOURCE_VIEW_DESC structured = {};
        structured.Format = DXGI_FORMAT_UNKNOWN;
        structured.ViewDimension = D3D12_SRV_DIMENSION_BUFFER;
        structured.Shader4ComponentMapping = D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING;
        structured.Buffer.FirstElement = mesh.attributesOffset / (kAttributeFloats * sizeof(float));
        structured.Buffer.NumElements = pending.vertexCount;
        structured.Buffer.StructureByteStride = kAttributeFloats * sizeof(float);
        device->CreateShaderResourceView(mesh.attributesBuffer, &structured, attributeView);

        mesh.built = true;
        mesh.vertexCount = pending.vertexCount;
        mesh.indexCount = static_cast<uint32_t>(pending.indices.size());
        mesh.indexHash = pending.indexHash;
        return true;
    }

    bool Raytracing::AskSizeLocked(BuildFrame& frame, int32_t id, uint32_t serial)
    {
        // The slot's buffer for the compacted sizes, made once: one a mesh at most. Its
        // entries are "aligned to the natural alignment for the members of the particular
        // output structure ... (e.g. 8 bytes for a struct with the largest membes being
        // UINT64)", and it is "in state D3D12_RESOURCE_STATE_UNORDERED_ACCESS" (DirectX-Specs,
        // Raytracing, D3D12_RAYTRACING_ACCELERATION_STRUCTURE_POSTBUILD_INFO_DESC).
        BlasMemory::PerSlot& perSlot = blasMemory.slots[frame.slotNumber];
        if (perSlot.sizes == nullptr)
        {
            const UINT64 bytes = static_cast<UINT64>(kMaxSizesAsked) * sizeof(UINT64);
            D3D12_HEAP_PROPERTIES readback = {};
            readback.Type = D3D12_HEAP_TYPE_READBACK;
            const D3D12_RESOURCE_DESC readbackDesc = RtBufferDesc(bytes, D3D12_RESOURCE_FLAG_NONE);
            if (!EnsureDefaultBuffer(perSlot.sizes, bytes, D3D12_RESOURCE_STATE_COMMON,
                                     D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS)
                || FAILED(device->CreateCommittedResource(&readback, D3D12_HEAP_FLAG_NONE, &readbackDesc,
                                                          D3D12_RESOURCE_STATE_COPY_DEST, nullptr,
                                                          IID_PPV_ARGS(&perSlot.sizesReadback))))
            {
                perSlot.sizes.Reset();
                perSlot.sizesReadback.Reset();
                return false;
            }
            perSlot.sizesCapacity = kMaxSizesAsked;
        }
        if (frame.sizesAsked >= perSlot.sizesCapacity)
            return false;
        if (frame.sizesAsked == 0)
        {
            frame.sizes = perSlot.sizes.Get();
            frame.barriers.push_back(
                TransitionBarrier(perSlot.sizes.Get(), D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_UNORDERED_ACCESS));
            perSlot.asked.clear();
        }
        perSlot.asked.emplace_back(id, serial);
        ++frame.sizesAsked;
        return true;
    }

    uint32_t Raytracing::CompactMeshesLocked(BuildFrame& frame, Slot& slot)
    {
        // The compacted sizes the slot's list before asked for, back now that it is done:
        // each structure copied into memory of that size and the larger one let go of --
        // "Compact mode also fits the destination into a potentially smaller memory
        // footprint (certainly no larger). The size required for the destination can be
        // retrieved beforehand from EmitRaytracingAccelerationStructurePostbuildInfo()"
        // (DirectX-Specs, Raytracing, D3D12_RAYTRACING_ACCELERATION_STRUCTURE_COPY_MODE).
        BlasMemory::PerSlot& perSlot = blasMemory.slots[frame.slotNumber];
        if (perSlot.asked.empty() || perSlot.sizesReadback == nullptr)
            return 0;
        const D3D12_RANGE range = { 0, perSlot.asked.size() * sizeof(UINT64) };
        void* mapped = nullptr;
        if (FAILED(perSlot.sizesReadback->Map(0, &range, &mapped)))
        {
            perSlot.asked.clear();
            return 0;
        }
        std::vector<UINT64> sizes(perSlot.asked.size());
        memcpy(sizes.data(), mapped, sizes.size() * sizeof(UINT64));
        const D3D12_RANGE none = { 0, 0 };
        perSlot.sizesReadback->Unmap(0, &none);

        uint32_t compacted = 0;
        for (size_t i = 0; i < perSlot.asked.size(); ++i)
        {
            auto found = meshes.find(perSlot.asked[i].first);
            // A mesh released or built anew since has another structure, or none.
            if (found == meshes.end() || found->second.blasSerial != perSlot.asked[i].second
                || found->second.refine != kRefineCompact)
                continue;
            Mesh& mesh = found->second;
            mesh.refine = kRefineNone;
            RtStoredBuffer into;
            if (sizes[i] == 0 || sizes[i] >= mesh.blas.size || !MakeBlasLocked(sizes[i], frame.pooled, into))
                continue;
            list->CopyRaytracingAccelerationStructure(into.address, mesh.blas.address,
                                                      D3D12_RAYTRACING_ACCELERATION_STRUCTURE_COPY_MODE_COMPACT);
            RetireBlasLocked(mesh.blas, slot);
            mesh.blas = into;
            mesh.blasSerial = blasMemory.nextSerial++;
            ++compacted;
        }
        perSlot.asked.clear();
        return compacted;
    }

    uint32_t Raytracing::RefineMeshesLocked(BuildFrame& frame, Slot& slot)
    {
        // rtBlasCompaction 2: the structures built PREFER_FAST_BUILD built again
        // PREFER_FAST_TRACE with ALLOW_COMPACTION, rtBlasRefineBudget triangles a frame, at
        // least one; their compacted size asked for (CompactMeshesLocked). The DXR spec on
        // ALLOW_COMPACTION: "It's generally a good idea to do this on all static geometry
        // to reclaim (potentially significant) amounts of memory" (Choosing acceleration
        // structure build flags).
        const uint64_t budget = static_cast<uint64_t>(std::max(1, Config().rtBlasRefineBudget));
        uint64_t triangles = 0;
        uint32_t rebuilt = 0;
        while (!blasMemory.toRefine.empty())
        {
            const int32_t id = blasMemory.toRefine.front();
            auto found = meshes.find(id);
            if (found == meshes.end() || found->second.refine != kRefineRebuild || !found->second.built)
            {
                blasMemory.toRefine.pop_front();
                continue;
            }
            Mesh& mesh = found->second;
            if (rebuilt > 0 && triangles + mesh.indexCount / 3 > budget)
                break;
            blasMemory.toRefine.pop_front();
            mesh.refine = kRefineNone;

            BuildFrame::Build build;
            build.geometry = GeometryOf(mesh.positionsBuffer->GetGPUVirtualAddress() + mesh.positionsOffset, mesh.vertexCount,
                                        mesh.indicesBuffer->GetGPUVirtualAddress() + mesh.indicesOffset, mesh.indexCount);
            D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_INPUTS& inputs = build.desc.Inputs;
            inputs.Type = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_TYPE_BOTTOM_LEVEL;
            inputs.Flags = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BUILD_FLAG_PREFER_FAST_TRACE
                           | D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BUILD_FLAG_ALLOW_COMPACTION;
            inputs.NumDescs = 1;
            inputs.DescsLayout = D3D12_ELEMENTS_LAYOUT_ARRAY;
            inputs.pGeometryDescs = &build.geometry;
            D3D12_RAYTRACING_ACCELERATION_STRUCTURE_PREBUILD_INFO info = {};
            device->GetRaytracingAccelerationStructurePrebuildInfo(&inputs, &info);
            RtStoredBuffer into;
            if (info.ResultDataMaxSizeInBytes == 0 || !MakeBlasLocked(info.ResultDataMaxSizeInBytes, frame.pooled, into))
                continue;
            if (!ScratchLocked(frame, slot, info.ScratchDataSizeInBytes, build.desc.ScratchAccelerationStructureData))
            {
                RetireBlasLocked(into, slot);
                continue;
            }
            ToReadLocked(frame, mesh.positionsBuffer);
            ToReadLocked(frame, mesh.indicesBuffer);
            build.desc.DestAccelerationStructureData = into.address;
            RetireBlasLocked(mesh.blas, slot);
            mesh.blas = into;
            mesh.blasSerial = blasMemory.nextSerial++;
            frame.builds.push_back(build);
            BuildFrame::Build& queued = frame.builds.back();
            queued.desc.Inputs.pGeometryDescs = &queued.geometry;
            const UINT index = frame.sizesAsked;
            if (AskSizeLocked(frame, id, mesh.blasSerial))
            {
                queued.askSize = true;
                queued.sizeIndex = index;
                mesh.refine = kRefineCompact;
            }
            if (!frame.batched)
                FlushBuildsLocked(frame);
            triangles += mesh.indexCount / 3;
            ++rebuilt;
        }
        return rebuilt;
    }

    bool Raytracing::RefitMeshLocked(BuildFrame& frame, Mesh& mesh, const PendingMesh& pending, Slot& slot)
    {
        // DXR's update constraints (DirectX-Specs, Raytracing, "Acceleration structure
        // update constraints"): "The VertexBuffer and/or Transform members of
        // D3D12_RAYTRACING_GEOMETRY_TRIANGLES_DESC can change ... Essentially this means
        // vertex positions can change. ... Nothing else can change, so note that in
        // particular this means no changes to properties like ... VertexCount, ... geometry
        // flags, data formats, index buffer contents and so on." BuildMeshesLocked checks the
        // counts and the indices' hash; the geometry is the one UploadMeshLocked built.
        const UINT64 positionsSize = pending.positions.size() * sizeof(float);
        const UINT64 attributesSize = pending.attributes.size() * sizeof(float);

        BuildFrame::Build build;
        build.geometry = GeometryOf(mesh.positionsBuffer->GetGPUVirtualAddress() + mesh.positionsOffset, pending.vertexCount,
                                    mesh.indicesBuffer->GetGPUVirtualAddress() + mesh.indicesOffset,
                                    static_cast<UINT>(pending.indices.size()));
        D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_INPUTS& inputs = build.desc.Inputs;
        inputs.Type = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_TYPE_BOTTOM_LEVEL;
        inputs.Flags = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BUILD_FLAG_PREFER_FAST_TRACE
                       | D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BUILD_FLAG_ALLOW_UPDATE;
        inputs.NumDescs = 1;
        inputs.DescsLayout = D3D12_ELEMENTS_LAYOUT_ARRAY;
        inputs.pGeometryDescs = &build.geometry;

        // Everything made before anything is recorded. The first time the structure is
        // built once more, to be updatable; from then on it is updated in place.
        ID3D12Resource* upload = nullptr;
        UINT64 uploadOffset = 0;
        uint8_t* mapped = nullptr;
        RtStoredBuffer blas;
        ComPtr<ID3D12Resource> updateScratch;
        // In the upload as in the mesh's buffers: in a range of the geometry pool the
        // attributes, then the positions after them, in one copy.
        const bool inPool = mesh.geometry.Valid();
        const UINT64 positionsAt = inPool ? mesh.positionsOffset - mesh.attributesOffset : attributesSize;
        bool ok = UploadSpaceLocked(frame, slot, positionsAt + positionsSize, upload, uploadOffset, mapped);
        if (ok && !mesh.updatable)
        {
            D3D12_RAYTRACING_ACCELERATION_STRUCTURE_PREBUILD_INFO info = {};
            device->GetRaytracingAccelerationStructurePrebuildInfo(&inputs, &info);
            if (info.ResultDataMaxSizeInBytes == 0)
                return false;
            ok = MakeBlasLocked(info.ResultDataMaxSizeInBytes, frame.pooled, blas)
                 && ScratchLocked(frame, slot, info.ScratchDataSizeInBytes, build.desc.ScratchAccelerationStructureData)
                 && EnsureDefaultBuffer(updateScratch, std::max<UINT64>(info.UpdateScratchDataSizeInBytes, 256),
                                        D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS);
            if (!ok && blas.Valid())
                RetireBlasLocked(blas, slot);
        }
        if (!ok)
        {
            SetProblem("a mesh handed over again could not be updated; it is built anew");
            return false;
        }
        memcpy(mapped, pending.attributes.data(), static_cast<size_t>(attributesSize));
        memcpy(mapped + positionsAt, pending.positions.data(), static_cast<size_t>(positionsSize));

        // Buffers decay to COMMON once an ExecuteCommandLists is done on the GPU, so the
        // copies promote them to COPY_DEST again ("Using Resource Barriers to Synchronize
        // Resource States in Direct3D 12", State decay to common); the lists before this
        // one that read them are finished by then.
        ToCopyLocked(frame, mesh.positionsBuffer);
        ToCopyLocked(frame, mesh.attributesBuffer);
        if (inPool)
            frame.CopyInto(mesh.attributesBuffer, mesh.attributesOffset, upload, uploadOffset, positionsAt + positionsSize);
        else
        {
            frame.CopyInto(mesh.attributesBuffer, 0, upload, uploadOffset, attributesSize);
            frame.CopyInto(mesh.positionsBuffer, 0, upload, uploadOffset + positionsAt, positionsSize);
        }
        for (ID3D12Resource* buffer : { mesh.positionsBuffer, mesh.attributesBuffer, mesh.indicesBuffer })
            ToReadLocked(frame, buffer);

        if (mesh.updatable)
        {
            // "source can be the same as destination address to mean an in-place update".
            build.desc.Inputs.Flags |= D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BUILD_FLAG_PERFORM_UPDATE;
            build.desc.SourceAccelerationStructureData = mesh.blas.address;
            build.desc.DestAccelerationStructureData = mesh.blas.address;
            build.desc.ScratchAccelerationStructureData = mesh.updateScratch->GetGPUVirtualAddress();
        }
        else
        {
            build.desc.DestAccelerationStructureData = blas.address;
            RetireBlasLocked(mesh.blas, slot);
            mesh.blas = blas;
            mesh.blasSerial = blasMemory.nextSerial++;
            mesh.refine = kRefineNone;
            mesh.updateScratch = updateScratch;
            mesh.updatable = true;
        }
        frame.builds.push_back(build);
        frame.builds.back().desc.Inputs.pGeometryDescs = &frame.builds.back().geometry;
        return true;
    }

    bool Raytracing::UploadMaterialTexturesLocked(Slot& slot)
    {
        // Material textures: the array grows to hold the highest layer, then the new
        // layers are copied in.
        if (pendingTextures.empty() && materialTextures != nullptr)
            return true;
        UINT highest = 0;
        for (const auto& entry : pendingTextures)
            highest = std::max<UINT>(highest, static_cast<UINT>(entry.first) + 1);
        if (!EnsureMaterialTexturesLocked(std::max<UINT>(highest, 1), slot))
            return false;
        if (pendingTextures.empty())
            return true;
        const UINT64 layerBytes = static_cast<UINT64>(kRtMaterialTextureSize) * kRtMaterialTextureSize * 4;
        ComPtr<ID3D12Resource> upload;
        uint8_t* mapped = nullptr;
        const D3D12_RANGE none = { 0, 0 };
        if (!EnsureUploadBuffer(upload, layerBytes * pendingTextures.size())
            || FAILED(upload->Map(0, &none, reinterpret_cast<void**>(&mapped))))
        {
            SetProblem("the material textures' upload buffer could not be made");
            return true;
        }
        const D3D12_RESOURCE_BARRIER toCopy = TransitionBarrier(
            materialTextures.Get(), D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, D3D12_RESOURCE_STATE_COPY_DEST);
        list->ResourceBarrier(1, &toCopy);
        UINT64 offset = 0;
        for (const auto& entry : pendingTextures)
        {
            memcpy(mapped + offset, entry.second.data(), static_cast<size_t>(layerBytes));
            D3D12_TEXTURE_COPY_LOCATION source = {};
            source.pResource = upload.Get();
            source.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
            source.PlacedFootprint.Offset = offset;
            source.PlacedFootprint.Footprint.Format = DXGI_FORMAT_R8G8B8A8_UNORM_SRGB;
            source.PlacedFootprint.Footprint.Width = kRtMaterialTextureSize;
            source.PlacedFootprint.Footprint.Height = kRtMaterialTextureSize;
            source.PlacedFootprint.Footprint.Depth = 1;
            source.PlacedFootprint.Footprint.RowPitch = kRtMaterialTextureSize * 4;
            D3D12_TEXTURE_COPY_LOCATION destination = {};
            destination.pResource = materialTextures.Get();
            destination.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
            destination.SubresourceIndex = static_cast<UINT>(entry.first);
            list->CopyTextureRegion(&destination, 0, 0, 0, &source, nullptr);
            offset += layerBytes;
        }
        upload->Unmap(0, nullptr);
        const D3D12_RESOURCE_BARRIER toRead = TransitionBarrier(
            materialTextures.Get(), D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
        list->ResourceBarrier(1, &toRead);
        slot.held.push_back(upload);
        pendingTextures.clear();
        return true;
    }

    static_assert(kRtMaxMaterialTextures <= D3D12_REQ_TEXTURE2D_ARRAY_AXIS_DIMENSION,
                  "the material textures exceed a Direct3D 12 texture array");

    bool Raytracing::EnsureMaterialTexturesLocked(UINT layers, Slot& slot)
    {
        if (materialTextures != nullptr && materialTextureLayers >= layers)
            return true;
        UINT grown = std::max<UINT>(materialTextureLayers, 1);
        while (grown < layers)
            grown *= 2;
        grown = std::min<UINT>(grown, kRtMaxMaterialTextures);

        D3D12_HEAP_PROPERTIES heapProperties = {};
        heapProperties.Type = D3D12_HEAP_TYPE_DEFAULT;
        D3D12_RESOURCE_DESC desc = {};
        desc.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
        desc.Width = kRtMaterialTextureSize;
        desc.Height = kRtMaterialTextureSize;
        desc.DepthOrArraySize = static_cast<UINT16>(grown);
        desc.MipLevels = 1;
        desc.Format = DXGI_FORMAT_R8G8B8A8_UNORM_SRGB;
        desc.SampleDesc.Count = 1;
        ComPtr<ID3D12Resource> created;
        HRESULT hr = device->CreateCommittedResource(&heapProperties, D3D12_HEAP_FLAG_NONE, &desc,
                                                     D3D12_RESOURCE_STATE_COPY_DEST, nullptr, IID_PPV_ARGS(&created));
        if (FAILED(hr))
        {
            SetProblem("the material textures could not be made (" + Hr(hr) + ")");
            return false;
        }
        // The layers there are come along.
        if (materialTextures != nullptr)
        {
            const D3D12_RESOURCE_BARRIER toSource = TransitionBarrier(
                materialTextures.Get(), D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, D3D12_RESOURCE_STATE_COPY_SOURCE);
            list->ResourceBarrier(1, &toSource);
            for (UINT layer = 0; layer < materialTextureLayers; ++layer)
            {
                D3D12_TEXTURE_COPY_LOCATION source = {};
                source.pResource = materialTextures.Get();
                source.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
                source.SubresourceIndex = layer;
                D3D12_TEXTURE_COPY_LOCATION destination = {};
                destination.pResource = created.Get();
                destination.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
                destination.SubresourceIndex = layer;
                list->CopyTextureRegion(&destination, 0, 0, 0, &source, nullptr);
            }
            slot.held.push_back(materialTextures);
        }
        const D3D12_RESOURCE_BARRIER toRead =
            TransitionBarrier(created.Get(), D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
        list->ResourceBarrier(1, &toRead);
        materialTextures = created;
        materialTextureLayers = grown;
        return true;
    }

    void Raytracing::WriteMaterialViewLocked(UINT slotNumber)
    {
        if (materialTextures == nullptr)
            return;
        D3D12_SHADER_RESOURCE_VIEW_DESC srv = {};
        srv.Format = DXGI_FORMAT_R8G8B8A8_UNORM_SRGB;
        srv.ViewDimension = D3D12_SRV_DIMENSION_TEXTURE2DARRAY;
        srv.Shader4ComponentMapping = D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING;
        srv.Texture2DArray.MipLevels = 1;
        srv.Texture2DArray.ArraySize = materialTextureLayers;
        D3D12_CPU_DESCRIPTOR_HANDLE view = heap->GetCPUDescriptorHandleForHeapStart();
        view.ptr += static_cast<SIZE_T>(kMaterialViews + slotNumber) * increment;
        device->CreateShaderResourceView(materialTextures.Get(), &srv, view);
    }

    bool Raytracing::BuildSceneLocked(Slot& slot, UINT slotNumber, uint32_t frameIndex, Shared* gpuInstances,
                                      UINT& instanceCount)
    {
        // This frame's scene, or the newest where it sent none of its own.
        static const FrameScene none;
        const FrameScene* scene = frameScenes.empty() ? &none : &frameScenes.back();
        for (const FrameScene& candidate : frameScenes)
            if (candidate.frameIndex == frameIndex)
                scene = &candidate;
        const std::vector<RtInstance>& instances = scene->instances;

        // Materials: index 0 is a plain grey for instances whose material is unknown.
        std::unordered_map<int32_t, uint32_t> materialIndex;
        std::vector<MaterialData> materialList;
        MaterialData grey = {};
        grey.albedo[0] = grey.albedo[1] = grey.albedo[2] = 0.5f;
        grey.albedo[3] = 0.0f;
        grey.specular[0] = grey.specular[1] = grey.specular[2] = 0.04f;
        grey.smoothness = 0.2f;
        grey.texture = -1;
        materialList.push_back(grey);
        for (const auto& entry : materialData)
        {
            materialIndex[entry.first] = static_cast<uint32_t>(materialList.size());
            materialList.push_back(entry.second);
        }

        std::vector<D3D12_RAYTRACING_INSTANCE_DESC> descs;
        std::vector<InstanceData> data;
        descs.reserve(instances.size());
        data.reserve(instances.size());
        for (const RtInstance& instance : instances)
        {
            auto mesh = meshes.find(instance.mesh);
            if (mesh == meshes.end() || !mesh->second.built)
                continue;
            // InstanceID has 24 bits.
            if (descs.size() >= 0xFFFFFFu)
            {
                SetProblem("more instances than a scene's 16 777 215; the rest are left out");
                break;
            }
            D3D12_RAYTRACING_INSTANCE_DESC desc = {};
            memcpy(desc.Transform, instance.transform, sizeof(desc.Transform));
            desc.InstanceID = static_cast<UINT>(data.size()) & 0xFFFFFFu;
            desc.InstanceMask = instance.mask & 0xFFu;
            desc.InstanceContributionToHitGroupIndex = 0;
            // Terrain and cut-out surfaces go through the passes' candidate test.
            const bool cutout = IsCutoutLocked(instance.material);
            desc.Flags = D3D12_RAYTRACING_INSTANCE_FLAG_TRIANGLE_CULL_DISABLE
                         | ((instance.flags & kRtInstanceTerrain) != 0 || cutout ? D3D12_RAYTRACING_INSTANCE_FLAG_FORCE_NON_OPAQUE
                                                                               : 0);
            desc.AccelerationStructure = mesh->second.blas.address;
            descs.push_back(desc);

            InstanceData item = {};
            item.meshSlot = mesh->second.slot;
            auto material = materialIndex.find(instance.material);
            item.material = material != materialIndex.end() ? material->second : 0u;
            item.flags = instance.flags | (cutout ? kRtInstanceCutout : 0u);
            NormalMatrix(instance.transform, item.normalRows);
            data.push_back(item);
        }
        instanceCount = static_cast<UINT>(descs.size());

        const UINT capacity = std::max<UINT>(instanceCount, 1);
        if (slot.instanceCapacity < capacity)
        {
            UINT grown = std::max<UINT>(capacity, slot.instanceCapacity * 2);
            grown = std::max<UINT>(grown, 256);
            if (!EnsureUploadBuffer(slot.instanceDescs, static_cast<UINT64>(grown) * sizeof(D3D12_RAYTRACING_INSTANCE_DESC))
                || !EnsureUploadBuffer(slot.instanceData, static_cast<UINT64>(grown) * sizeof(InstanceData)))
            {
                SetProblem("the instance buffers could not be made");
                return false;
            }
            slot.instanceCapacity = grown;
        }
        const UINT materialCount = static_cast<UINT>(materialList.size());
        if (slot.materialCapacity < materialCount)
        {
            const UINT grown = std::max<UINT>(std::max<UINT>(materialCount, slot.materialCapacity * 2), 64);
            if (!EnsureUploadBuffer(slot.materials, static_cast<UINT64>(grown) * sizeof(MaterialData)))
            {
                SetProblem("the material buffer could not be made");
                return false;
            }
            slot.materialCapacity = grown;
        }

        const D3D12_RANGE nothing = { 0, 0 };
        void* mapped = nullptr;
        if (instanceCount > 0 && SUCCEEDED(slot.instanceDescs->Map(0, &nothing, &mapped)))
        {
            memcpy(mapped, descs.data(), descs.size() * sizeof(D3D12_RAYTRACING_INSTANCE_DESC));
            slot.instanceDescs->Unmap(0, nullptr);
        }
        if (instanceCount > 0 && SUCCEEDED(slot.instanceData->Map(0, &nothing, &mapped)))
        {
            memcpy(mapped, data.data(), data.size() * sizeof(InstanceData));
            slot.instanceData->Unmap(0, nullptr);
        }
        if (SUCCEEDED(slot.materials->Map(0, &nothing, &mapped)))
        {
            memcpy(mapped, materialList.data(), materialList.size() * sizeof(MaterialData));
            slot.materials->Unmap(0, nullptr);
        }

        // The GPU's instances after these, where the frame has any.
        D3D12_GPU_VIRTUAL_ADDRESS descsAddress = slot.instanceDescs->GetGPUVirtualAddress();
        slot.instanceDataAddress = slot.instanceData->GetGPUVirtualAddress();
        UINT total = instanceCount;
        if (!scene->groups.empty() && gpuInstances != nullptr
            && AddGpuInstancesLocked(slot, slotNumber, gpuInstances, scene->groups, materialIndex, instanceCount, total)
            && total > instanceCount)
        {
            descsAddress = slot.sceneDescs->GetGPUVirtualAddress();
            slot.instanceDataAddress = slot.sceneData->GetGPUVirtualAddress();
            instanceCount = total;
        }

        D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_INPUTS inputs = {};
        inputs.Type = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_TYPE_TOP_LEVEL;
        // rtTlasFastTrace: NVIDIA's "For TLAS, consider the PREFER_FAST_TRACE flag and
        // perform only rebuilds. Often, this results in best overall performance."
        inputs.Flags = Config().rtTlasFastTrace ? D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BUILD_FLAG_PREFER_FAST_TRACE
                                                : D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BUILD_FLAG_PREFER_FAST_BUILD;
        inputs.NumDescs = instanceCount;
        inputs.DescsLayout = D3D12_ELEMENTS_LAYOUT_ARRAY;
        inputs.InstanceDescs = descsAddress;
        D3D12_RAYTRACING_ACCELERATION_STRUCTURE_PREBUILD_INFO info = {};
        device->GetRaytracingAccelerationStructurePrebuildInfo(&inputs, &info);

        // The scene's structure is used by one list at a time on one queue, so one is
        // enough; it grows, and the one it replaces is held until its lists are done.
        if (tlas == nullptr || tlasSize < info.ResultDataMaxSizeInBytes)
        {
            if (tlas != nullptr)
                slot.held.push_back(tlas);
            tlas.Reset();
            tlasSize = std::max<UINT64>(info.ResultDataMaxSizeInBytes * 3 / 2, 4096);
            if (!EnsureDefaultBuffer(tlas, tlasSize, D3D12_RESOURCE_STATE_RAYTRACING_ACCELERATION_STRUCTURE,
                                     D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS))
            {
                SetProblem("the scene's acceleration structure could not be made");
                return false;
            }
        }
        if (tlasScratch == nullptr || tlasScratchSize < info.ScratchDataSizeInBytes)
        {
            if (tlasScratch != nullptr)
                slot.held.push_back(tlasScratch);
            tlasScratch.Reset();
            tlasScratchSize = std::max<UINT64>(info.ScratchDataSizeInBytes * 3 / 2, 4096);
            if (!EnsureDefaultBuffer(tlasScratch, tlasScratchSize, D3D12_RESOURCE_STATE_COMMON,
                                     D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS))
            {
                SetProblem("the scene's scratch buffer could not be made");
                return false;
            }
        }

        D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_DESC build = {};
        build.Inputs = inputs;
        build.DestAccelerationStructureData = tlas->GetGPUVirtualAddress();
        build.ScratchAccelerationStructureData = tlasScratch->GetGPUVirtualAddress();
        list->BuildRaytracingAccelerationStructure(&build, 0, nullptr);
        const D3D12_RESOURCE_BARRIER barrier = UavBarrier(tlas.Get());
        list->ResourceBarrier(1, &barrier);
        slot.held.push_back(tlas);
        return true;
    }

    // A group as the GPU pass reads it (RaytracingShaders/GpuInstances.hlsl).
    struct GpuGroup
    {
        uint32_t blasLow;
        uint32_t blasHigh;
        uint32_t meshSlot;
        uint32_t material;
        uint32_t first;
        uint32_t count;
        uint32_t start;
        uint32_t maskFlags;
    };

    bool Raytracing::AddGpuInstancesLocked(Slot& slot, UINT slotNumber, Shared* gpuInstances,
                                           const std::vector<RtGpuInstanceGroup>& given,
                                           const std::unordered_map<int32_t, uint32_t>& materialIndex, UINT cpuCount, UINT& total)
    {
        auto pipeline = denoisePipelines.find("RT_GpuInstances");
        if (pipeline == denoisePipelines.end())
            return false;
        // What each group is on the device; the ones whose mesh is not built are left out,
        // and the ones past InstanceID's 24 bits.
        const UINT capacityTexels = gpuInstances->texture.width / 3 * gpuInstances->texture.height;
        std::vector<GpuGroup> groups;
        UINT gpuCount = 0;
        bool full = false;
        for (const RtGpuInstanceGroup& group : given)
        {
            auto mesh = meshes.find(group.mesh);
            if (mesh == meshes.end() || !mesh->second.built || group.count == 0
                || static_cast<uint64_t>(group.first) + group.count > capacityTexels)
                continue;
            if (static_cast<uint64_t>(cpuCount) + gpuCount + group.count >= 0xFFFFFFu)
            {
                full = true;
                continue;
            }
            const D3D12_GPU_VIRTUAL_ADDRESS blas = mesh->second.blas.address;
            auto material = materialIndex.find(group.material);
            const bool cutout = IsCutoutLocked(group.material);
            GpuGroup item = {};
            item.blasLow = static_cast<uint32_t>(blas & 0xFFFFFFFFu);
            item.blasHigh = static_cast<uint32_t>(blas >> 32);
            item.meshSlot = mesh->second.slot;
            item.material = material != materialIndex.end() ? material->second : 0u;
            item.first = group.first;
            item.count = group.count;
            item.start = gpuCount;
            item.maskFlags = (group.mask & 0xFFu) | (((group.flags | (cutout ? kRtInstanceCutout : 0u)) & 0xFFFFFFu) << 8);
            groups.push_back(item);
            gpuCount += group.count;
        }
        if (full)
            SetProblem("more instances than a scene's 16 777 215; groups of the GPU's instances are left out");
        if (gpuCount == 0)
            return false;
        total = cpuCount + gpuCount;

        // The buffers, grown as needed; the ones they replace held until the slot is done.
        if (slot.sceneCapacity < total)
        {
            const UINT grown = std::max<UINT>(std::max<UINT>(total, slot.sceneCapacity * 2), 1024);
            if (slot.sceneDescs != nullptr)
                slot.held.push_back(slot.sceneDescs);
            if (slot.sceneData != nullptr)
                slot.held.push_back(slot.sceneData);
            slot.sceneDescs.Reset();
            slot.sceneData.Reset();
            if (!EnsureDefaultBuffer(slot.sceneDescs, static_cast<UINT64>(grown) * sizeof(D3D12_RAYTRACING_INSTANCE_DESC),
                                     D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS)
                || !EnsureDefaultBuffer(slot.sceneData, static_cast<UINT64>(grown) * sizeof(InstanceData),
                                        D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS))
            {
                SetProblem("the buffers for the GPU's instances could not be made");
                slot.sceneCapacity = 0;
                return false;
            }
            slot.sceneCapacity = grown;
            slot.sceneState = slot.sceneStateRecorded = D3D12_RESOURCE_STATE_COMMON;
        }
        const UINT groupCount = static_cast<UINT>(groups.size());
        if (slot.gpuGroupCapacity < groupCount)
        {
            const UINT grown = std::max<UINT>(std::max<UINT>(groupCount, slot.gpuGroupCapacity * 2), 64);
            if (!EnsureUploadBuffer(slot.gpuGroups, static_cast<UINT64>(grown) * sizeof(GpuGroup)))
            {
                SetProblem("the GPU instance groups' buffer could not be made");
                return false;
            }
            slot.gpuGroupCapacity = grown;
        }
        const D3D12_RANGE nothing = { 0, 0 };
        void* mapped = nullptr;
        if (FAILED(slot.gpuGroups->Map(0, &nothing, &mapped)))
            return false;
        memcpy(mapped, groups.data(), groups.size() * sizeof(GpuGroup));
        slot.gpuGroups->Unmap(0, nullptr);

        // The CPU's instances first, copied from their upload buffers.
        std::vector<D3D12_RESOURCE_BARRIER> barriers;
        const auto move = [&](ID3D12Resource* resource, D3D12_RESOURCE_STATES from, D3D12_RESOURCE_STATES to)
        {
            if (from != to)
                barriers.push_back(TransitionBarrier(resource, from, to));
        };
        move(slot.sceneDescs.Get(), slot.sceneState, D3D12_RESOURCE_STATE_COPY_DEST);
        move(slot.sceneData.Get(), slot.sceneState, D3D12_RESOURCE_STATE_COPY_DEST);
        move(gpuInstances->texture.d3d12.Get(), D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
        if (!barriers.empty())
            list->ResourceBarrier(static_cast<UINT>(barriers.size()), barriers.data());
        if (cpuCount > 0)
        {
            list->CopyBufferRegion(slot.sceneDescs.Get(), 0, slot.instanceDescs.Get(), 0,
                                   static_cast<UINT64>(cpuCount) * sizeof(D3D12_RAYTRACING_INSTANCE_DESC));
            list->CopyBufferRegion(slot.sceneData.Get(), 0, slot.instanceData.Get(), 0,
                                   static_cast<UINT64>(cpuCount) * sizeof(InstanceData));
        }
        barriers.clear();
        move(slot.sceneDescs.Get(), D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
        move(slot.sceneData.Get(), D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
        list->ResourceBarrier(static_cast<UINT>(barriers.size()), barriers.data());

        // The pass's views: the groups and the transforms to read, the two buffers to write.
        const UINT base = kSlotTables + slotNumber * kDenoiseDescriptors + kGpuInstancesTable;
        D3D12_CPU_DESCRIPTOR_HANDLE cpu = heap->GetCPUDescriptorHandleForHeapStart();
        cpu.ptr += static_cast<SIZE_T>(base) * increment;
        D3D12_GPU_DESCRIPTOR_HANDLE gpu = heap->GetGPUDescriptorHandleForHeapStart();
        gpu.ptr += static_cast<UINT64>(base) * increment;
        for (UINT i = 0; i < 8; ++i)
        {
            D3D12_SHADER_RESOURCE_VIEW_DESC srv = {};
            srv.Shader4ComponentMapping = D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING;
            if (i == 0)
            {
                srv.Format = DXGI_FORMAT_UNKNOWN;
                srv.ViewDimension = D3D12_SRV_DIMENSION_BUFFER;
                srv.Buffer.NumElements = groupCount;
                srv.Buffer.StructureByteStride = sizeof(GpuGroup);
                device->CreateShaderResourceView(slot.gpuGroups.Get(), &srv, cpu);
            }
            else
            {
                srv = SrvDesc(i == 1 ? gpuInstances->texture.format : DXGI_FORMAT_R32G32B32A32_FLOAT);
                device->CreateShaderResourceView(i == 1 ? gpuInstances->texture.d3d12.Get() : nullptr, &srv, cpu);
            }
            cpu.ptr += increment;
        }
        for (UINT i = 0; i < 4; ++i)
        {
            D3D12_UNORDERED_ACCESS_VIEW_DESC uav = {};
            if (i < 2)
            {
                uav.Format = DXGI_FORMAT_UNKNOWN;
                uav.ViewDimension = D3D12_UAV_DIMENSION_BUFFER;
                uav.Buffer.NumElements = slot.sceneCapacity;
                uav.Buffer.StructureByteStride = i == 0 ? sizeof(D3D12_RAYTRACING_INSTANCE_DESC) : sizeof(InstanceData);
                device->CreateUnorderedAccessView(i == 0 ? slot.sceneDescs.Get() : slot.sceneData.Get(), nullptr, &uav, cpu);
            }
            else
            {
                uav = UavDesc(DXGI_FORMAT_R16G16B16A16_FLOAT);
                device->CreateUnorderedAccessView(nullptr, nullptr, &uav, cpu);
            }
            cpu.ptr += increment;
        }

        ID3D12DescriptorHeap* heaps[] = { heap.Get() };
        list->SetDescriptorHeaps(1, heaps);
        list->SetComputeRootSignature(rootSignature.Get());
        BindTablesLocked(gpu);
        const UINT values[4] = { groupCount, gpuCount, cpuCount, 0 };
        list->SetComputeRoot32BitConstants(9, 4, values, 0);
        list->SetPipelineState(pipeline->second.Get());
        list->Dispatch((gpuCount + 63) / 64, 1, 1);

        barriers.clear();
        barriers.push_back(UavBarrier(nullptr));
        move(slot.sceneDescs.Get(), D3D12_RESOURCE_STATE_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
        move(slot.sceneData.Get(), D3D12_RESOURCE_STATE_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
        move(gpuInstances->texture.d3d12.Get(), D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, D3D12_RESOURCE_STATE_COMMON);
        list->ResourceBarrier(static_cast<UINT>(barriers.size()), barriers.data());
        slot.sceneStateRecorded = D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE;
        return true;
    }
}
