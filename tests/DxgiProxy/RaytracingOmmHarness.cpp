// The harness's checks of opacity micromaps against the driver (RaytracingHarness.cpp): on a
// Direct3D 12 device of its own, through NVAPI as the proxy goes (RaytracingOmm.h), the order
// of the micro-triangles along the curve (RaytracingOmmBake.h), their packing, the special
// indices, the unknown states and the instance flags. Rays at the centre of each
// micro-triangle of each of a grid of triangles, every triangle with an OMM of its own
// whose one micro-triangle is opaque (OmmProbe.hlsl).

#include "RaytracingOmmBake.h"
#include "RaytracingOmm.h"

#include <windows.h>

#include <d3d12.h>
#include <dxgi1_6.h>
#include <wrl/client.h>

#include <algorithm>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

using Microsoft::WRL::ComPtr;
// NVAPI's opacity micromap declarations (RaytracingOmm.h).
namespace nv = redefinition::nvomm;
// The proxy's baker (RaytracingOmmBake.h).
namespace bake = redefinition::omm;

namespace
{
    // The checks failed so far; RunOmmDriverChecks returns how many.
    int failures = 0;

    // A check's line, PASS or FAIL, with what it checks and what it found; a failure counted.
    void Check(bool passed, const char* what, const std::string& detail)
    {
        printf("%s  %s -- %s\n", passed ? "PASS" : "FAIL", what, detail.c_str());
        if (!passed)
            ++failures;
    }

    // NVAPI's functions for opacity micromaps, from the driver's nvapi64.dll.
    struct NvApi
    {
        // The raytracing caps, the pipeline options, a micromap array's sizes and build, a
        // structure's sizes and build with micromaps.
        nv::GetRaytracingCapsFn caps = nullptr;
        nv::SetCreatePipelineStateOptionsFn options = nullptr;
        nv::GetArrayPrebuildInfoFn arrayPrebuild = nullptr;
        nv::BuildArrayFn buildArray = nullptr;
        nv::GetPrebuildInfoExFn prebuild = nullptr;
        nv::BuildStructureExFn build = nullptr;

        // NVAPI loaded from System32 and initialised, the functions looked up by their ids;
        // false where any is missing.
        bool Load()
        {
            using Query = void* (*)(uint32_t);
            const HMODULE module = LoadLibraryExW(L"nvapi64.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
            if (module == nullptr)
                return false;
            const auto query = reinterpret_cast<Query>(GetProcAddress(module, "nvapi_QueryInterface"));
            if (query == nullptr)
                return false;
            const auto initialize = reinterpret_cast<int (*)()>(query(0x0150e828));
            if (initialize == nullptr || initialize() != 0)
                return false;
            caps = reinterpret_cast<nv::GetRaytracingCapsFn>(query(nv::kGetRaytracingCaps));
            options = reinterpret_cast<nv::SetCreatePipelineStateOptionsFn>(query(nv::kSetCreatePipelineStateOptions));
            arrayPrebuild = reinterpret_cast<nv::GetArrayPrebuildInfoFn>(query(nv::kGetArrayPrebuildInfo));
            buildArray = reinterpret_cast<nv::BuildArrayFn>(query(nv::kBuildArray));
            prebuild = reinterpret_cast<nv::GetPrebuildInfoExFn>(query(nv::kGetPrebuildInfoEx));
            build = reinterpret_cast<nv::BuildStructureExFn>(query(nv::kBuildStructureEx));
            return caps != nullptr && options != nullptr && arrayPrebuild != nullptr && buildArray != nullptr
                   && prebuild != nullptr && build != nullptr;
        }
    };

    // A buffer's description: at least 256 bytes, row major.
    D3D12_RESOURCE_DESC BufferDesc(UINT64 size, D3D12_RESOURCE_FLAGS flags = D3D12_RESOURCE_FLAG_NONE)
    {
        D3D12_RESOURCE_DESC desc = {};
        desc.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
        desc.Width = std::max<UINT64>(size, 256);
        desc.Height = 1;
        desc.DepthOrArraySize = 1;
        desc.MipLevels = 1;
        desc.SampleDesc.Count = 1;
        desc.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
        desc.Flags = flags;
        return desc;
    }

    // The checks' own Direct3D 12 device and what a probe records and waits on.
    struct Gpu
    {
        // The device, its queue, allocator and list; the fence, its last value and the event
        // waits take; the probe shader's root signature and pipeline (OmmProbe.hlsl); NVAPI.
        ComPtr<ID3D12Device5> device;
        ComPtr<ID3D12CommandQueue> queue;
        ComPtr<ID3D12CommandAllocator> allocator;
        ComPtr<ID3D12GraphicsCommandList4> list;
        ComPtr<ID3D12Fence> fence;
        UINT64 fenceValue = 0;
        HANDLE event = nullptr;
        ComPtr<ID3D12RootSignature> root;
        ComPtr<ID3D12PipelineState> pipeline;
        NvApi nvapi;

        // A committed buffer on that heap: upload buffers in GENERIC_READ, readback ones in
        // COPY_DEST, the rest in the state given; null where it could not be made.
        ComPtr<ID3D12Resource> Buffer(UINT64 size, D3D12_HEAP_TYPE heap, D3D12_RESOURCE_FLAGS flags, D3D12_RESOURCE_STATES state)
        {
            D3D12_HEAP_PROPERTIES properties = {};
            properties.Type = heap;
            const D3D12_RESOURCE_DESC desc = BufferDesc(size, flags);
            ComPtr<ID3D12Resource> buffer;
            if (FAILED(device->CreateCommittedResource(&properties, D3D12_HEAP_FLAG_NONE, &desc,
                                                       heap == D3D12_HEAP_TYPE_UPLOAD ? D3D12_RESOURCE_STATE_GENERIC_READ
                                                       : heap == D3D12_HEAP_TYPE_READBACK ? D3D12_RESOURCE_STATE_COPY_DEST
                                                                                         : state,
                                                       nullptr, IID_PPV_ARGS(&buffer))))
                return nullptr;
            return buffer;
        }

        // An upload buffer holding a copy of the bytes; null where it could not be made.
        ComPtr<ID3D12Resource> Upload(const void* bytes, size_t size)
        {
            ComPtr<ID3D12Resource> buffer = Buffer(size, D3D12_HEAP_TYPE_UPLOAD, D3D12_RESOURCE_FLAG_NONE, D3D12_RESOURCE_STATE_COMMON);
            if (buffer == nullptr)
                return nullptr;
            void* mapped = nullptr;
            const D3D12_RANGE none = { 0, 0 };
            if (FAILED(buffer->Map(0, &none, &mapped)))
                return nullptr;
            if (size > 0)
                memcpy(mapped, bytes, size);
            buffer->Unmap(0, nullptr);
            return buffer;
        }

        // Until the queue is past everything submitted so far.
        void Wait()
        {
            queue->Signal(fence.Get(), ++fenceValue);
            if (fence->GetCompletedValue() < fenceValue)
            {
                fence->SetEventOnCompletion(fenceValue, event);
                WaitForSingleObject(event, INFINITE);
            }
        }
    };

    // What one probe asks for. The triangles all have the same micro-triangles, `level`
    // times subdivided.
    struct Probe
    {
        // The subdivision level, and the OMMs' format: 2 or 4 states.
        uint32_t level = 0;
        uint16_t format = nv::kFormat4State;
        // 0: OMM k has micro-triangle k opaque, the rest transparent;
        // 1: the rest unknown transparent; 2: the rest unknown opaque.
        int rest = 0;
        // Special indices in place of OMMs: the triangle t takes special[t % 4].
        bool special = false;
        int32_t specialIndex[4] = { nv::kIndexFullyTransparent, nv::kIndexFullyOpaque, nv::kIndexFullyUnknownTransparent,
                                     nv::kIndexFullyUnknownOpaque };
        // How many triangles, where special.
        uint32_t triangles = 0;
        uint32_t geometryFlags = 0;
        uint32_t instanceFlags = D3D12_RAYTRACING_INSTANCE_FLAG_TRIANGLE_CULL_DISABLE;
        uint32_t blasFlags = nv::kBuildPreferFastTrace | nv::kBuildAllowDisableOmms;
        uint32_t rayFlags = 0;
        bool writeTable = false;
        // The shader's expectation (OmmProbe.hlsl).
        uint32_t expectation = 0;
    };

    // What a probe found.
    struct Outcome
    {
        // Whether it ran. Then the shader's 64 counts (misses, hits, rays that met a candidate, rays
        // not as expected, then the first 60 of those). Then per ray its result where writeTable
        // asked, and the driver's sizes of the micromap array and the structure.
        bool ok = false;
        std::vector<uint32_t> counts;
        std::vector<uint32_t> table;
        UINT64 ommBytes = 0;
        UINT64 blasBytes = 0;
    };

    // One probe on the driver: its OMMs and triangles built into an array and a structure, a
    // ray at the centre of every micro-triangle traced, and what the rays met read back.
    Outcome Run(Gpu& gpu, const Probe& probe)
    {
        Outcome outcome;
        const uint32_t cells = bake::MicroTriangleCount(probe.level);
        const uint32_t triangleCount = probe.special ? probe.triangles : cells;
        const uint32_t columns = std::max<uint32_t>(1, static_cast<uint32_t>(std::ceil(std::sqrt(double(triangleCount)))));

        // The OMMs: one for each triangle, micro-triangle k of OMM k opaque.
        const bool twoBits = probe.format == nv::kFormat4State;
        const uint32_t bitsPerMicro = twoBits ? 2 : 1;
        const uint32_t bytesPerOmm = std::max<uint32_t>(1, (cells * bitsPerMicro + 7) / 8);
        const uint32_t stride = (bytesPerOmm + 3) & ~3u;
        std::vector<uint8_t> data(static_cast<size_t>(stride) * cells, 0);
        std::vector<nv::Desc> descs(cells);
        for (uint32_t i = 0; i < cells; ++i)
        {
            uint8_t* block = &data[static_cast<size_t>(i) * stride];
            for (uint32_t k = 0; k < cells; ++k)
            {
                uint32_t state = k == i ? 1u : probe.rest == 1 ? 2u : probe.rest == 2 ? 3u : 0u;
                if (!twoBits)
                    state &= 1u;
                const uint32_t bit = k * bitsPerMicro;
                block[bit / 8] = static_cast<uint8_t>(block[bit / 8] | (state << (bit % 8)));
            }
            descs[i].byteOffset = i * stride;
            descs[i].subdivisionLevel = static_cast<uint16_t>(probe.level);
            descs[i].format = probe.format;
        }

        // The grid of triangles: vertex 0 at the grid point, 1 a unit to the right, 2 a unit up.
        std::vector<float> vertices;
        std::vector<uint32_t> indices;
        std::vector<uint32_t> ommIndices;
        for (uint32_t t = 0; t < triangleCount; ++t)
        {
            const float x = float(t % columns) * 2.0f, y = float(t / columns) * 2.0f;
            vertices.insert(vertices.end(), { x, y, 0.0f, x + 1.0f, y, 0.0f, x, y + 1.0f, 0.0f });
            for (uint32_t k = 0; k < 3; ++k)
                indices.push_back(t * 3 + k);
            ommIndices.push_back(probe.special ? static_cast<uint32_t>(probe.specialIndex[t % 4]) : t);
        }

        ComPtr<ID3D12Resource> dataBuffer = gpu.Upload(data.data(), data.size());
        ComPtr<ID3D12Resource> descBuffer = gpu.Upload(descs.data(), descs.size() * sizeof(nv::Desc));
        ComPtr<ID3D12Resource> vertexBuffer = gpu.Upload(vertices.data(), vertices.size() * sizeof(float));
        ComPtr<ID3D12Resource> indexBuffer = gpu.Upload(indices.data(), indices.size() * sizeof(uint32_t));
        ComPtr<ID3D12Resource> ommIndexBuffer = gpu.Upload(ommIndices.data(), ommIndices.size() * sizeof(uint32_t));
        if (dataBuffer == nullptr || descBuffer == nullptr || vertexBuffer == nullptr || indexBuffer == nullptr
            || ommIndexBuffer == nullptr)
            return outcome;

        std::vector<float> centres(static_cast<size_t>(cells) * 2);
        for (uint32_t k = 0; k < cells; ++k)
        {
            const bake::Corners& c = bake::MicroTriangle(probe.level, k);
            const double side = double(1u << probe.level);
            centres[k * 2] = float((c.u[0] + c.u[1] + c.u[2]) / (3.0 * side));
            centres[k * 2 + 1] = float((c.v[0] + c.v[1] + c.v[2]) / (3.0 * side));
        }
        ComPtr<ID3D12Resource> centreBuffer = gpu.Upload(centres.data(), centres.size() * sizeof(float));

        // The OMM array.
        nv::ArrayInputs arrayInputs = {};
        arrayInputs.flags = nv::kArrayPreferFastTrace;
        arrayInputs.numOMMUsageCounts = 1;
        const nv::UsageCount arrayUsage = { cells, probe.level, probe.format };
        arrayInputs.pOMMUsageCounts = &arrayUsage;
        arrayInputs.inputBuffer = dataBuffer->GetGPUVirtualAddress();
        arrayInputs.perOMMDescs = { descBuffer->GetGPUVirtualAddress(), sizeof(nv::Desc) };
        nv::ArrayPrebuildInfo arrayInfo = {};
        nv::ArrayPrebuildParams arrayPrebuild = { nv::Version(sizeof(nv::ArrayPrebuildParams), 1), &arrayInputs, &arrayInfo };
        if (gpu.nvapi.arrayPrebuild(gpu.device.Get(), &arrayPrebuild) != 0)
            return outcome;
        ComPtr<ID3D12Resource> array = gpu.Buffer(arrayInfo.resultDataMaxSizeInBytes, D3D12_HEAP_TYPE_DEFAULT,
                                                    D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS,
                                                    D3D12_RESOURCE_STATE_RAYTRACING_ACCELERATION_STRUCTURE);
        ComPtr<ID3D12Resource> arrayScratch = gpu.Buffer(arrayInfo.scratchDataSizeInBytes, D3D12_HEAP_TYPE_DEFAULT,
                                                          D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS,
                                                          D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
        if (array == nullptr || arrayScratch == nullptr)
            return outcome;
        outcome.ommBytes = arrayInfo.resultDataMaxSizeInBytes;

        // The BLAS, with the OMMs attached.
        nv::GeometryDescEx geometry = {};
        geometry.type = nv::kGeometryTypeOmmTriangles;
        geometry.flags = probe.geometryFlags;
        nv::OmmTriangles& omm = geometry.ommTriangles;
        omm.triangles.VertexBuffer = { vertexBuffer->GetGPUVirtualAddress(), 3 * sizeof(float) };
        omm.triangles.VertexFormat = DXGI_FORMAT_R32G32B32_FLOAT;
        omm.triangles.VertexCount = triangleCount * 3;
        omm.triangles.IndexBuffer = indexBuffer->GetGPUVirtualAddress();
        omm.triangles.IndexFormat = DXGI_FORMAT_R32_UINT;
        omm.triangles.IndexCount = triangleCount * 3;
        omm.ommAttachment.opacityMicromapIndexBuffer = { ommIndexBuffer->GetGPUVirtualAddress(), sizeof(uint32_t) };
        omm.ommAttachment.opacityMicromapIndexFormat = DXGI_FORMAT_R32_UINT;
        omm.ommAttachment.opacityMicromapBaseLocation = 0;
        omm.ommAttachment.opacityMicromapArray = probe.special ? 0 : array->GetGPUVirtualAddress();
        const nv::UsageCount attachUsage = { triangleCount, probe.level, probe.format };
        omm.ommAttachment.numOMMUsageCounts = probe.special ? 0 : 1;
        omm.ommAttachment.pOMMUsageCounts = probe.special ? nullptr : &attachUsage;
        nv::InputsEx blasInputs = {};
        blasInputs.type = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_TYPE_BOTTOM_LEVEL;
        blasInputs.flags = probe.blasFlags;
        blasInputs.numDescs = 1;
        blasInputs.descsLayout = D3D12_ELEMENTS_LAYOUT_ARRAY;
        blasInputs.geometryDescStrideInBytes = sizeof(nv::GeometryDescEx);
        blasInputs.pGeometryDescs = &geometry;
        D3D12_RAYTRACING_ACCELERATION_STRUCTURE_PREBUILD_INFO blasInfo = {};
        nv::PrebuildExParams blasPrebuild = { nv::Version(sizeof(nv::PrebuildExParams), 1), &blasInputs, &blasInfo };
        if (gpu.nvapi.prebuild(gpu.device.Get(), &blasPrebuild) != 0 || blasInfo.ResultDataMaxSizeInBytes == 0)
            return outcome;
        ComPtr<ID3D12Resource> blas = gpu.Buffer(blasInfo.ResultDataMaxSizeInBytes, D3D12_HEAP_TYPE_DEFAULT,
                                                  D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS,
                                                  D3D12_RESOURCE_STATE_RAYTRACING_ACCELERATION_STRUCTURE);
        ComPtr<ID3D12Resource> blasScratch = gpu.Buffer(blasInfo.ScratchDataSizeInBytes, D3D12_HEAP_TYPE_DEFAULT,
                                                         D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS,
                                                         D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
        if (blas == nullptr || blasScratch == nullptr)
            return outcome;
        outcome.blasBytes = blasInfo.ResultDataMaxSizeInBytes;

        // The scene: one instance.
        D3D12_RAYTRACING_INSTANCE_DESC instance = {};
        instance.Transform[0][0] = instance.Transform[1][1] = instance.Transform[2][2] = 1.0f;
        instance.InstanceMask = 0xFF;
        instance.Flags = probe.instanceFlags;
        instance.AccelerationStructure = blas->GetGPUVirtualAddress();
        ComPtr<ID3D12Resource> instanceBuffer = gpu.Upload(&instance, sizeof(instance));
        D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_INPUTS tlasInputs = {};
        tlasInputs.Type = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_TYPE_TOP_LEVEL;
        tlasInputs.Flags = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BUILD_FLAG_PREFER_FAST_TRACE;
        tlasInputs.NumDescs = 1;
        tlasInputs.DescsLayout = D3D12_ELEMENTS_LAYOUT_ARRAY;
        tlasInputs.InstanceDescs = instanceBuffer->GetGPUVirtualAddress();
        D3D12_RAYTRACING_ACCELERATION_STRUCTURE_PREBUILD_INFO tlasInfo = {};
        gpu.device->GetRaytracingAccelerationStructurePrebuildInfo(&tlasInputs, &tlasInfo);
        ComPtr<ID3D12Resource> tlas = gpu.Buffer(tlasInfo.ResultDataMaxSizeInBytes, D3D12_HEAP_TYPE_DEFAULT,
                                                  D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS,
                                                  D3D12_RESOURCE_STATE_RAYTRACING_ACCELERATION_STRUCTURE);
        ComPtr<ID3D12Resource> tlasScratch = gpu.Buffer(tlasInfo.ScratchDataSizeInBytes, D3D12_HEAP_TYPE_DEFAULT,
                                                         D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS,
                                                         D3D12_RESOURCE_STATE_UNORDERED_ACCESS);

        const uint64_t rays = static_cast<uint64_t>(triangleCount) * cells;
        const UINT64 tableBytes = probe.writeTable ? rays * sizeof(uint32_t) : sizeof(uint32_t);
        ComPtr<ID3D12Resource> table = gpu.Buffer(tableBytes, D3D12_HEAP_TYPE_DEFAULT, D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS,
                                                   D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
        ComPtr<ID3D12Resource> counts = gpu.Buffer(64 * sizeof(uint32_t), D3D12_HEAP_TYPE_DEFAULT,
                                                    D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
        ComPtr<ID3D12Resource> tableBack = gpu.Buffer(tableBytes, D3D12_HEAP_TYPE_READBACK, D3D12_RESOURCE_FLAG_NONE,
                                                       D3D12_RESOURCE_STATE_COPY_DEST);
        ComPtr<ID3D12Resource> countsBack = gpu.Buffer(64 * sizeof(uint32_t), D3D12_HEAP_TYPE_READBACK,
                                                        D3D12_RESOURCE_FLAG_NONE, D3D12_RESOURCE_STATE_COPY_DEST);
        if (tlas == nullptr || tlasScratch == nullptr || table == nullptr || counts == nullptr || tableBack == nullptr
            || countsBack == nullptr || centreBuffer == nullptr)
            return outcome;

        gpu.allocator->Reset();
        gpu.list->Reset(gpu.allocator.Get(), nullptr);
        // Zeroed counts: cleared by a copy from a zeroed upload.
        std::vector<uint32_t> zeros(64, 0);
        ComPtr<ID3D12Resource> zeroed = gpu.Upload(zeros.data(), zeros.size() * sizeof(uint32_t));
        D3D12_RESOURCE_BARRIER toCopy = {};
        toCopy.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
        toCopy.Transition.pResource = counts.Get();
        toCopy.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
        toCopy.Transition.StateBefore = D3D12_RESOURCE_STATE_UNORDERED_ACCESS;
        toCopy.Transition.StateAfter = D3D12_RESOURCE_STATE_COPY_DEST;
        gpu.list->ResourceBarrier(1, &toCopy);
        gpu.list->CopyBufferRegion(counts.Get(), 0, zeroed.Get(), 0, 64 * sizeof(uint32_t));
        std::swap(toCopy.Transition.StateBefore, toCopy.Transition.StateAfter);
        gpu.list->ResourceBarrier(1, &toCopy);

        if (!probe.special)
        {
            const nv::ArrayBuildDesc arrayBuild = { array->GetGPUVirtualAddress(), arrayInputs,
                                                    arrayScratch->GetGPUVirtualAddress() };
            nv::ArrayBuildParams arrayParams = { nv::Version(sizeof(nv::ArrayBuildParams), 1), &arrayBuild, 0, nullptr };
            if (gpu.nvapi.buildArray(gpu.list.Get(), &arrayParams) != 0)
            {
                gpu.list->Close();
                return outcome;
            }
            D3D12_RESOURCE_BARRIER uav = {};
            uav.Type = D3D12_RESOURCE_BARRIER_TYPE_UAV;
            gpu.list->ResourceBarrier(1, &uav);
        }
        nv::BuildDescEx blasBuild = {};
        blasBuild.destAccelerationStructureData = blas->GetGPUVirtualAddress();
        blasBuild.inputs = blasInputs;
        blasBuild.scratchAccelerationStructureData = blasScratch->GetGPUVirtualAddress();
        const nv::BuildExParams blasParams = { nv::Version(sizeof(nv::BuildExParams), 1), &blasBuild, 0, nullptr };
        if (gpu.nvapi.build(gpu.list.Get(), &blasParams) != 0)
        {
            gpu.list->Close();
            return outcome;
        }
        D3D12_RESOURCE_BARRIER uav = {};
        uav.Type = D3D12_RESOURCE_BARRIER_TYPE_UAV;
        gpu.list->ResourceBarrier(1, &uav);
        D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_DESC tlasBuild = {};
        tlasBuild.Inputs = tlasInputs;
        tlasBuild.DestAccelerationStructureData = tlas->GetGPUVirtualAddress();
        tlasBuild.ScratchAccelerationStructureData = tlasScratch->GetGPUVirtualAddress();
        gpu.list->BuildRaytracingAccelerationStructure(&tlasBuild, 0, nullptr);
        gpu.list->ResourceBarrier(1, &uav);

        gpu.list->SetComputeRootSignature(gpu.root.Get());
        gpu.list->SetPipelineState(gpu.pipeline.Get());
        gpu.list->SetComputeRootShaderResourceView(0, tlas->GetGPUVirtualAddress());
        gpu.list->SetComputeRootShaderResourceView(1, centreBuffer->GetGPUVirtualAddress());
        gpu.list->SetComputeRootUnorderedAccessView(2, table->GetGPUVirtualAddress());
        gpu.list->SetComputeRootUnorderedAccessView(3, counts->GetGPUVirtualAddress());
        const uint32_t constants[6] = { cells, columns, static_cast<uint32_t>(rays), probe.expectation,
                                        probe.writeTable ? 1u : 0u, probe.rayFlags };
        gpu.list->SetComputeRoot32BitConstants(4, 6, constants, 0);
        const uint64_t groups = (rays + 255) / 256;
        gpu.list->Dispatch(static_cast<UINT>(std::min<uint64_t>(groups, 65535)),
                           static_cast<UINT>((groups + 65534) / 65535), 1);
        D3D12_RESOURCE_BARRIER toRead[2] = {};
        for (int i = 0; i < 2; ++i)
        {
            toRead[i].Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
            toRead[i].Transition.pResource = i == 0 ? table.Get() : counts.Get();
            toRead[i].Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
            toRead[i].Transition.StateBefore = D3D12_RESOURCE_STATE_UNORDERED_ACCESS;
            toRead[i].Transition.StateAfter = D3D12_RESOURCE_STATE_COPY_SOURCE;
        }
        gpu.list->ResourceBarrier(2, toRead);
        gpu.list->CopyBufferRegion(tableBack.Get(), 0, table.Get(), 0, tableBytes);
        gpu.list->CopyBufferRegion(countsBack.Get(), 0, counts.Get(), 0, 64 * sizeof(uint32_t));
        gpu.list->Close();
        ID3D12CommandList* lists[] = { gpu.list.Get() };
        gpu.queue->ExecuteCommandLists(1, lists);
        gpu.Wait();
        if (gpu.device->GetDeviceRemovedReason() != S_OK)
            return outcome;

        outcome.counts.resize(64);
        void* mapped = nullptr;
        if (FAILED(countsBack->Map(0, nullptr, &mapped)))
            return outcome;
        memcpy(outcome.counts.data(), mapped, 64 * sizeof(uint32_t));
        countsBack->Unmap(0, nullptr);
        if (probe.writeTable && SUCCEEDED(tableBack->Map(0, nullptr, &mapped)))
        {
            outcome.table.resize(static_cast<size_t>(rays));
            memcpy(outcome.table.data(), mapped, static_cast<size_t>(rays) * sizeof(uint32_t));
            tableBack->Unmap(0, nullptr);
        }
        outcome.ok = true;
        return outcome;
    }
}

namespace
{
    // A texture of the alpha channel alone: f(x, y) in texel coordinates.
    template <typename F>
    std::vector<uint8_t> MakeAlpha(F f)
    {
        std::vector<uint8_t> alpha(bake::kTextureSize * bake::kTextureSize);
        for (uint32_t y = 0; y < bake::kTextureSize; ++y)
            for (uint32_t x = 0; x < bake::kTextureSize; ++x)
                alpha[y * bake::kTextureSize + x] = f(x, y);
        return alpha;
    }

    // What the baker is held to for a mesh of triangles. A micro-triangle it calls transparent
    // has coverage 0 at every point sampled in it, and one it calls opaque has coverage 1. The
    // check also counts how many it leaves unknown that the samples all found uniform.
    struct Conservative
    {
        // The micro-triangles checked; those whose state the samples contradict; those left
        // unknown that the samples found uniform; those left unknown.
        uint64_t microTriangles = 0, wrong = 0, lost = 0, unknown = 0;
    };

    // The baked micromaps of a mesh's triangles against the coverage sampled at sixteen points
    // in each micro-triangle.
    Conservative Verify(const bake::TriangleUv* triangles, size_t count, const uint8_t* alpha, const bake::Baked& baked)
    {
        Conservative result;
        // Points inside a micro-triangle: its corners, its centre, edge points and a few
        // interior ones, from a fixed sequence.
        const double weights[][3] = { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 }, { 1 / 3.0, 1 / 3.0, 1 / 3.0 },
                                      { 0.5, 0.5, 0 }, { 0, 0.5, 0.5 }, { 0.5, 0, 0.5 }, { 0.7, 0.2, 0.1 },
                                      { 0.1, 0.7, 0.2 }, { 0.2, 0.1, 0.7 }, { 0.45, 0.45, 0.1 }, { 0.05, 0.05, 0.9 },
                                      { 0.9, 0.05, 0.05 }, { 0.05, 0.9, 0.05 }, { 0.25, 0.25, 0.5 }, { 0.6, 0.3, 0.1 } };
        for (size_t t = 0; t < count; ++t)
        {
            const int32_t at = baked.triangleIndex[t];
            const uint32_t level = at < 0 ? 0u : baked.entries[static_cast<size_t>(at)].level;
            const uint32_t cells = bake::MicroTriangleCount(level);
            const double side = double(1u << level);
            for (uint32_t k = 0; k < cells; ++k)
            {
                const bake::Corners& c = bake::MicroTriangle(level, k);
                double least = 1.0, greatest = 0.0;
                for (const auto& w : weights)
                {
                    // The point on the base triangle, in barycentrics (u, v), and its coverage.
                    const double u = (c.u[0] * w[0] + c.u[1] * w[1] + c.u[2] * w[2]) / side;
                    const double v = (c.v[0] * w[0] + c.v[1] * w[1] + c.v[2] * w[2]) / side;
                    const double x = triangles[t].uv[0][0] + (triangles[t].uv[1][0] - triangles[t].uv[0][0]) * u
                                     + (triangles[t].uv[2][0] - triangles[t].uv[0][0]) * v;
                    const double y = triangles[t].uv[0][1] + (triangles[t].uv[1][1] - triangles[t].uv[0][1]) * u
                                     + (triangles[t].uv[2][1] - triangles[t].uv[0][1]) * v;
                    const double coverage = bake::Coverage(alpha, x, y);
                    least = std::min(least, coverage);
                    greatest = std::max(greatest, coverage);
                }
                const bake::State state = bake::StateOf(baked, t, k);
                ++result.microTriangles;
                if ((state == bake::kTransparent && greatest > 0.0) || (state == bake::kOpaque && least < 1.0 - 1e-9))
                    ++result.wrong;
                if (state == bake::kUnknownTransparent || state == bake::kUnknownOpaque)
                {
                    ++result.unknown;
                    if (greatest == 0.0 || least >= 1.0 - 1e-9)
                        ++result.lost;
                }
            }
        }
        return result;
    }

    // Random texture coordinates, a fixed sequence, over a few tiles, and the triangles' size
    // from a texel to the whole tile.
    std::vector<bake::TriangleUv> RandomTriangles(size_t count, float spread)
    {
        std::vector<bake::TriangleUv> triangles(count);
        uint32_t state = 12345u;
        const auto next = [&]()
        {
            state = state * 1664525u + 1013904223u;
            return float(state >> 8) / 16777216.0f;
        };
        for (bake::TriangleUv& t : triangles)
        {
            const float u = next() * 5.0f - 2.0f, v = next() * 5.0f - 2.0f;
            const float size = spread * std::pow(next(), 2.0f);
            for (int k = 0; k < 3; ++k)
            {
                t.uv[k][0] = u + (next() - 0.5f) * size;
                t.uv[k][1] = v + (next() - 0.5f) * size;
            }
        }
        return triangles;
    }
}

// The order of the micro-triangles and the baker, on the CPU.
int RunOmmBakerChecks()
{
    // The curve: every micro-triangle of a level once, filling the base triangle, each touching
    // the one before at a corner at least, and the inverse finds each by its centre.
    {
        bool filled = true, joined = true, inverse = true;
        char detail[200];
        uint32_t checked = 0;
        for (uint32_t level = 0; level <= 8; ++level)
        {
            const uint32_t cells = bake::MicroTriangleCount(level);
            const int64_t side = int64_t(1) << level;
            int64_t twiceArea = 0;
            for (uint32_t k = 0; k < cells; ++k)
            {
                const bake::Corners& c = bake::MicroTriangle(level, k);
                twiceArea += std::llabs(int64_t(c.u[1] - c.u[0]) * (c.v[2] - c.v[0]) - int64_t(c.u[2] - c.u[0]) * (c.v[1] - c.v[0]));
                if (k > 0)
                {
                    const bake::Corners& before = bake::MicroTriangle(level, k - 1);
                    int shared = 0;
                    for (int a = 0; a < 3; ++a)
                        for (int b = 0; b < 3; ++b)
                            shared += c.u[a] == before.u[b] && c.v[a] == before.v[b] ? 1 : 0;
                    joined = joined && shared >= 1;
                }
                const double centreU = (c.u[0] + c.u[1] + c.u[2]) / (3.0 * double(side));
                const double centreV = (c.v[0] + c.v[1] + c.v[2]) / (3.0 * double(side));
                inverse = inverse && bake::MicroTriangleAt(level, centreU, centreV) == k;
                ++checked;
            }
            // Twice the area of each micro-triangle is 1 in the grid's units, side^2 of them to a
            // base triangle of twice the area side^2.
            filled = filled && twiceArea == side * side;
        }
        sprintf_s(detail, "levels 0 to 8, %u micro-triangles: they fill the base triangle %s, each touches the one before %s, the inverse finds each by its centre %s",
                  checked, filled ? "exactly" : "NOT", joined ? "" : "NOT", inverse ? "" : "NOT");
        Check(filled && joined && inverse, "the micro-triangles' curve", detail);
    }

    const uint8_t* none = nullptr;
    (void)none;
    bake::Settings settings;
    const std::vector<bake::TriangleUv> random = RandomTriangles(3000, 3.0f);
    struct Texture
    {
        const char* name;
        std::vector<uint8_t> alpha;
        // How the baker is to come out on the random triangles: all uniform, or mixed.
        int expect;   // 0 all transparent, 1 all opaque, 2 mixed
    };
    const Texture textures[] = {
        { "all opaque", MakeAlpha([](uint32_t, uint32_t) { return uint8_t(255); }), 1 },
        { "all transparent", MakeAlpha([](uint32_t, uint32_t) { return uint8_t(0); }), 0 },
        { "left half opaque", MakeAlpha([](uint32_t x, uint32_t) { return uint8_t(x < 32 ? 255 : 0); }), 2 },
        { "a checker of 8 texels", MakeAlpha([](uint32_t x, uint32_t y) { return uint8_t(((x / 8 + y / 8) & 1) ? 255 : 0); }), 2 },
        { "a ramp", MakeAlpha([](uint32_t x, uint32_t) { return uint8_t(x * 4); }), 2 },
        { "a blade: a diagonal stripe of 6 texels", MakeAlpha([](uint32_t x, uint32_t y) {
              const int d = int(x) - int(y);
              return uint8_t(d > -4 && d < 4 ? 255 : (d > -6 && d < 6 ? 128 : 0));
          }), 2 },
    };
    for (const Texture& texture : textures)
    {
        bake::Baked baked;
        const auto begin = GetTickCount64();
        bake::Bake(random.data(), random.size(), texture.alpha.data(), settings, baked);
        const auto took = GetTickCount64() - begin;
        const Conservative verified = Verify(random.data(), random.size(), texture.alpha.data(), baked);
        bool shapeOk = true;
        if (texture.expect == 1)
            shapeOk = baked.uniform[1] == random.size();
        else if (texture.expect == 0)
            shapeOk = baked.uniform[0] == random.size();
        char detail[400];
        sprintf_s(detail, "%zu random triangles: %u transparent, %u opaque, %u unknown, %u with an OMM of their own (%zu OMMs, %zu bytes);"
                          " %llu micro-triangles, %llu called wrong, %llu unknown of which %llu the samples found uniform; %llu ms",
                  random.size(), baked.uniform[0], baked.uniform[1], baked.uniform[2] + baked.uniform[3], baked.withOmm,
                  baked.entries.size(), baked.data.size(), static_cast<unsigned long long>(verified.microTriangles),
                  static_cast<unsigned long long>(verified.wrong), static_cast<unsigned long long>(verified.unknown),
                  static_cast<unsigned long long>(verified.lost), static_cast<unsigned long long>(took));
        std::string what = std::string("the baker, ") + texture.name;
        Check(shapeOk && verified.wrong == 0, what.c_str(), detail);
    }

    // A quad's two triangles over a known texture, the micro-triangles by their places: the
    // half texture's left half opaque, so a triangle on the left only is opaque, one on the
    // right transparent, one across a mix whose opaque micro-triangles are the left of it.
    {
        const std::vector<uint8_t> half = MakeAlpha([](uint32_t x, uint32_t) { return uint8_t(x < 32 ? 255 : 0); });
        std::vector<bake::TriangleUv> triangles(3);
        const float left[3][2] = { { 0.05f, 0.05f }, { 0.4f, 0.05f }, { 0.05f, 0.4f } };
        const float right[3][2] = { { 0.6f, 0.1f }, { 0.95f, 0.1f }, { 0.6f, 0.4f } };
        const float across[3][2] = { { 0.1f, 0.5f }, { 0.9f, 0.5f }, { 0.1f, 0.9f } };
        memcpy(triangles[0].uv, left, sizeof(left));
        memcpy(triangles[1].uv, right, sizeof(right));
        memcpy(triangles[2].uv, across, sizeof(across));
        bake::Baked baked;
        bake::Bake(triangles.data(), triangles.size(), half.data(), bake::Settings(), baked);
        // In the third, micro-triangle k is opaque exactly where its centre's x is under 0.5.
        bool mixed = baked.triangleIndex[2] >= 0;
        uint32_t opaque = 0, transparent = 0, unknown = 0, wrongSide = 0;
        if (mixed)
        {
            const uint32_t level = baked.entries[static_cast<size_t>(baked.triangleIndex[2])].level;
            for (uint32_t k = 0; k < bake::MicroTriangleCount(level); ++k)
            {
                const bake::Corners& c = bake::MicroTriangle(level, k);
                const double side = double(1u << level);
                const double u = (c.u[0] + c.u[1] + c.u[2]) / (3.0 * side), v = (c.v[0] + c.v[1] + c.v[2]) / (3.0 * side);
                const double x = 0.1 + 0.8 * u;
                (void)v;
                const bake::State s = bake::StateOf(baked, 2, k);
                opaque += s == bake::kOpaque;
                transparent += s == bake::kTransparent;
                unknown += s == bake::kUnknownOpaque || s == bake::kUnknownTransparent;
                wrongSide += (s == bake::kOpaque && x > 0.5 + 1e-9) || (s == bake::kTransparent && x < 0.5 - 1e-9);
            }
        }
        char detail[300];
        sprintf_s(detail, "the triangle on the left %d (-2 is opaque), the one on the right %d (-1 transparent), the one across has an OMM"
                          " of level %u with %u opaque, %u transparent, %u unknown micro-triangles, %u on the wrong side",
                  baked.triangleIndex[0], baked.triangleIndex[1],
                  mixed ? unsigned(baked.entries[static_cast<size_t>(baked.triangleIndex[2])].level) : 0u, opaque, transparent,
                  unknown, wrongSide);
        Check(baked.triangleIndex[0] == nv::kIndexFullyOpaque && baked.triangleIndex[1] == nv::kIndexFullyTransparent && mixed
                  && opaque > 0 && transparent > 0 && wrongSide == 0,
              "the baker, a half texture", detail);
    }

    // Triangles with the same coordinates share an OMM, and so do OMMs with the same states.
    {
        const std::vector<uint8_t> checker = MakeAlpha([](uint32_t x, uint32_t y) { return uint8_t(((x / 8 + y / 8) & 1) ? 255 : 0); });
        std::vector<bake::TriangleUv> triangles(200);
        for (size_t i = 0; i < triangles.size(); ++i)
        {
            const float shift = float(i % 4) * 0.25f;   // a checker's square is 8 texels = 0.125; 0.25 is two squares: the same states
            const float uv[3][2] = { { 0.1f + shift, 0.1f }, { 0.4f + shift, 0.1f }, { 0.1f + shift, 0.4f } };
            memcpy(triangles[i].uv, uv, sizeof(uv));
        }
        bake::Baked baked;
        bake::Bake(triangles.data(), triangles.size(), checker.data(), bake::Settings(), baked);
        char detail[200];
        sprintf_s(detail, "%zu triangles of 4 kinds (two squares apart) share %zu OMM(s), %zu bytes", triangles.size(),
                  baked.entries.size(), baked.data.size());
        Check(!baked.entries.empty() && baked.entries.size() <= 4, "the baker, shared OMMs", detail);
    }
    return failures;
}

// Runs the checks on this adapter; the number of those that failed.
int RunOmmDriverChecks(IDXGIAdapter1* adapter, const wchar_t* shaderPath, bool dumpTable)
{
    RunOmmBakerChecks();
    Gpu gpu;
    ComPtr<ID3D12Device> device;
    if (FAILED(D3D12CreateDevice(adapter, D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&device))) || FAILED(device.As(&gpu.device)))
    {
        Check(false, "opacity micromaps on the driver", "no Direct3D 12 device with raytracing");
        return failures;
    }
    D3D12_FEATURE_DATA_D3D12_OPTIONS5 options5 = {};
    gpu.device->CheckFeatureSupport(D3D12_FEATURE_D3D12_OPTIONS5, &options5, sizeof(options5));
    DXGI_ADAPTER_DESC1 adapterDesc = {};
    adapter->GetDesc1(&adapterDesc);
    if (adapterDesc.VendorId != 0x10DE || !gpu.nvapi.Load())
    {
        printf("      opacity micromaps: not an NVIDIA GPU with NVAPI, the driver checks are left out\n");
        return 0;
    }
    uint32_t capsValue = 0;
    const int capsStatus = gpu.nvapi.caps(gpu.device.Get(), nv::kCapsOpacityMicromap, &capsValue, sizeof(capsValue));
    printf("      opacity micromaps: NvAPI_D3D12_GetRaytracingCaps(OPACITY_MICROMAP) status %d caps %u (1 standard)\n", capsStatus,
           capsValue);
    if (capsStatus != 0 || (capsValue & nv::kCapStandard) == 0)
    {
        printf("      the driver offers none; the driver checks are left out\n");
        return 0;
    }

    D3D12_COMMAND_QUEUE_DESC queueDesc = {};
    queueDesc.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
    if (FAILED(gpu.device->CreateCommandQueue(&queueDesc, IID_PPV_ARGS(&gpu.queue)))
        || FAILED(gpu.device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&gpu.allocator)))
        || FAILED(gpu.device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, gpu.allocator.Get(), nullptr,
                                                IID_PPV_ARGS(&gpu.list)))
        || FAILED(gpu.device->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&gpu.fence))))
    {
        Check(false, "opacity micromaps on the driver", "the queue could not be made");
        return failures;
    }
    gpu.list->Close();
    gpu.event = CreateEventW(nullptr, FALSE, FALSE, nullptr);

    // The pipeline, created after the driver was told micromaps are in use (RaytracingOmm.cpp).
    FILE* file = nullptr;
    std::vector<char> code;
    if (_wfopen_s(&file, shaderPath, L"rb") == 0 && file != nullptr)
    {
        fseek(file, 0, SEEK_END);
        code.resize(static_cast<size_t>(ftell(file)));
        fseek(file, 0, SEEK_SET);
        fread(code.data(), 1, code.size(), file);
        fclose(file);
    }
    if (code.empty())
    {
        Check(false, "opacity micromaps on the driver", "OmmProbe.cso is not next to the harness");
        return failures;
    }
    D3D12_ROOT_PARAMETER parameters[5] = {};
    parameters[0].ParameterType = D3D12_ROOT_PARAMETER_TYPE_SRV;
    parameters[0].Descriptor.ShaderRegister = 0;
    parameters[1].ParameterType = D3D12_ROOT_PARAMETER_TYPE_SRV;
    parameters[1].Descriptor.ShaderRegister = 1;
    parameters[2].ParameterType = D3D12_ROOT_PARAMETER_TYPE_UAV;
    parameters[2].Descriptor.ShaderRegister = 0;
    parameters[3].ParameterType = D3D12_ROOT_PARAMETER_TYPE_UAV;
    parameters[3].Descriptor.ShaderRegister = 1;
    parameters[4].ParameterType = D3D12_ROOT_PARAMETER_TYPE_32BIT_CONSTANTS;
    parameters[4].Constants.ShaderRegister = 0;
    parameters[4].Constants.Num32BitValues = 6;
    D3D12_ROOT_SIGNATURE_DESC rootDesc = {};
    rootDesc.NumParameters = 5;
    rootDesc.pParameters = parameters;
    ComPtr<ID3DBlob> serialized, errors;
    if (FAILED(D3D12SerializeRootSignature(&rootDesc, D3D_ROOT_SIGNATURE_VERSION_1, &serialized, &errors))
        || FAILED(gpu.device->CreateRootSignature(0, serialized->GetBufferPointer(), serialized->GetBufferSize(),
                                                  IID_PPV_ARGS(&gpu.root))))
    {
        Check(false, "opacity micromaps on the driver", "the root signature could not be made");
        return failures;
    }
    const nv::PipelineOptionsParams enable = { nv::Version(sizeof(nv::PipelineOptionsParams), 1), nv::kPipelineEnableOmm };
    const int enabled = gpu.nvapi.options(gpu.device.Get(), &enable);
    D3D12_COMPUTE_PIPELINE_STATE_DESC pipelineDesc = {};
    pipelineDesc.pRootSignature = gpu.root.Get();
    pipelineDesc.CS = { code.data(), code.size() };
    const HRESULT made = gpu.device->CreateComputePipelineState(&pipelineDesc, IID_PPV_ARGS(&gpu.pipeline));
    const nv::PipelineOptionsParams disable = { nv::Version(sizeof(nv::PipelineOptionsParams), 1), 0 };
    gpu.nvapi.options(gpu.device.Get(), &disable);
    if (enabled != 0 || FAILED(made))
    {
        char detail[100];
        sprintf_s(detail, "SetCreatePipelineStateOptions %d, CreateComputePipelineState 0x%08lX", enabled,
                  static_cast<unsigned long>(made));
        Check(false, "opacity micromaps on the driver", detail);
        return failures;
    }

    // The micro-triangles' order and packing, level by level: the ray at the centre of
    // micro-triangle j meets an opaque micro-triangle in the triangle whose OMM has k = j opaque.
    for (uint32_t level = 0; level <= 6; ++level)
    {
        Probe probe;
        probe.level = level;
        probe.writeTable = level <= 4;
        const Outcome outcome = Run(gpu, probe);
        char detail[300];
        if (!outcome.ok)
        {
            Check(false, "the micro-triangles' order, 4-state", "the probe could not be run");
            continue;
        }
        const uint64_t cells = bake::MicroTriangleCount(level);
        sprintf_s(detail, "level %u: %llu rays at the micro-triangles' centres, %u of them unlike the order (hits %u, candidates %u);"
                          " OMMs %llu bytes, BLAS %llu bytes",
                  level, static_cast<unsigned long long>(cells * cells), outcome.counts[3], outcome.counts[1], outcome.counts[2],
                  static_cast<unsigned long long>(outcome.ommBytes), static_cast<unsigned long long>(outcome.blasBytes));
        Check(outcome.counts[3] == 0 && outcome.counts[1] == cells && outcome.counts[2] == 0, "the micro-triangles' order, 4-state",
              detail);
        if ((outcome.counts[3] != 0 || dumpTable) && level >= 1 && level <= 4)
        {
            // Which micro-triangle was opaque where, as the driver has it: for OMM i the
            // micro-triangles j whose centre ray hit.
            printf("        index -> the micro-triangles hit (the driver's order), by the OMM's opaque index:\n");
            for (uint32_t i = 0; i < cells; ++i)
            {
                printf("          %3u:", i);
                for (uint32_t j = 0; j < cells; ++j)
                    if ((outcome.table[static_cast<size_t>(i) * cells + j] & 1u) != 0u)
                        printf(" %u", j);
                printf("\n");
            }
        }
    }

    // The same through the other ways a micromap is read.
    struct Case
    {
        const char* what;
        Probe probe;
        // The counts the driver is to give: rays that hit, that did not, that met a candidate.
        uint32_t hits, misses, candidates;
        // Hits the shader's expectation (OmmProbe.hlsl) counts as wrong, to hold the others.
        uint32_t mismatches;
    };
    std::vector<Case> cases;
    const uint32_t cells3 = bake::MicroTriangleCount(3);
    {
        Case c = { "a 2-state micromap", Probe(), cells3, cells3 * cells3 - cells3, 0, 0 };
        c.probe.level = 3;
        c.probe.format = nv::kFormat2State;
        cases.push_back(c);
    }
    {
        // Every micro-triangle but k unknown transparent: a candidate the shader commits.
        Case c = { "4-state, the rest unknown transparent", Probe(), cells3 * cells3, 0, cells3 * cells3 - cells3,
                   0 };
        c.probe.level = 3;
        c.probe.rest = 1;
        c.probe.expectation = 1;
        cases.push_back(c);
    }
    {
        Case c = { "4-state, the rest unknown opaque", Probe(), cells3 * cells3, 0, cells3 * cells3 - cells3, 0 };
        c.probe.level = 3;
        c.probe.rest = 2;
        c.probe.expectation = 1;
        cases.push_back(c);
    }
    {
        // FORCE_OMM_2_STATE: the unknown states count as their known ones, no candidate.
        Case c = { "4-state, the rest unknown transparent, FORCE_OMM_2_STATE", Probe(), cells3, cells3 * cells3 - cells3, 0, 0 };
        c.probe.level = 3;
        c.probe.rest = 1;
        c.probe.instanceFlags |= nv::kInstanceForceOmm2State;
        cases.push_back(c);
    }
    {
        Case c = { "4-state, the rest unknown opaque, FORCE_OMM_2_STATE", Probe(), cells3 * cells3, 0, 0, 0 };
        c.probe.level = 3;
        c.probe.rest = 2;
        c.probe.expectation = 1;
        c.probe.instanceFlags |= nv::kInstanceForceOmm2State;
        cases.push_back(c);
    }
    {
        // The special indices, two triangles each: transparent, opaque, unknown transparent,
        // unknown opaque.
        Case c = { "the special indices", Probe(), 6, 2, 4, 2 };
        c.probe.special = true;
        c.probe.triangles = 8;
        c.probe.expectation = 1;
        cases.push_back(c);
    }
    {
        // "Ray flags and instance flags may still alter the state of opaque or unknown hits,
        // but ... there is no way to turn a transparent micro-triangle miss into a hit": an
        // instance FORCE_NON_OPAQUE makes the opaque micro-triangles candidates (the shader
        // commits them) and leaves the transparent ones skipped.
        Case c = { "level 3, FORCE_NON_OPAQUE", Probe(), cells3, cells3 * cells3 - cells3, cells3, 0 };
        c.probe.level = 3;
        c.probe.instanceFlags |= D3D12_RAYTRACING_INSTANCE_FLAG_FORCE_NON_OPAQUE;
        cases.push_back(c);
    }
    {
        Case c = { "level 3, ray FORCE_OPAQUE", Probe(), cells3, cells3 * cells3 - cells3, 0, 0 };
        c.probe.level = 3;
        c.probe.rayFlags = 0x1;
        cases.push_back(c);
    }
    {
        // All transparent through the special index, the instance DISABLE_OMMS: the geometry's
        // own state (non-opaque here), so every ray meets a candidate; without it, none.
        Case c = { "transparent, OMMs on", Probe(), 0, 4, 0, 0 };
        c.probe.special = true;
        c.probe.triangles = 4;
        c.probe.specialIndex[0] = c.probe.specialIndex[1] = c.probe.specialIndex[2] = c.probe.specialIndex[3] =
            nv::kIndexFullyTransparent;
        c.probe.expectation = 0;
        cases.push_back(c);
        Case off = { "transparent, instance DISABLE_OMMS, non-opaque geometry", c.probe, 4, 0, 4, 4 };
        off.probe.instanceFlags |= nv::kInstanceDisableOmms;
        cases.push_back(off);
        Case opaque = { "transparent, instance DISABLE_OMMS, opaque geometry", c.probe, 4, 0, 0, 4 };
        opaque.probe.instanceFlags |= nv::kInstanceDisableOmms;
        opaque.probe.geometryFlags = D3D12_RAYTRACING_GEOMETRY_FLAG_OPAQUE;
        cases.push_back(opaque);
    }
    for (Case& c : cases)
    {
        const Outcome o = Run(gpu, c.probe);
        char detail[300];
        if (!o.ok)
        {
            Check(false, c.what, "the probe could not be run");
            continue;
        }
        sprintf_s(detail, "hits %u of %u expected, misses %u of %u, rays that met a candidate %u of %u", o.counts[1], c.hits,
                  o.counts[0], c.misses, o.counts[2], c.candidates);
        Check(o.counts[1] == c.hits && o.counts[0] == c.misses && o.counts[2] == c.candidates, c.what, detail);
    }
    return failures;
}
