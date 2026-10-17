// The raytracing service's scene: the meshes and their acceleration structures, the
// material textures, and each frame's instances, the CPU's and the GPU's, in the scene's
// acceleration structure.

#include "Raytracing.h"

#include "D3d12Util.h"
#include "Log.h"
#include "RaytracingInternal.h"

#include <algorithm>
#include <cmath>
#include <cstring>

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

    void Raytracing::RetireMeshLocked(Mesh& mesh, Slot& slot)
    {
        slot.held.push_back(mesh.positions);
        slot.held.push_back(mesh.attributes);
        slot.held.push_back(mesh.indices);
        slot.held.push_back(mesh.blas);
        retiredSlots.emplace_back(mesh.slot, fenceValue + 1);
    }

    bool Raytracing::IsCutoutLocked(int32_t material) const
    {
        const auto found = materialData.find(material);
        return found != materialData.end() && found->second.albedo[3] > 0.0f;
    }

    uint32_t Raytracing::BuildMeshesLocked(Slot& slot)
    {
        // Meshes: releases first, then what is new, each into its own acceleration
        // structure.
        for (int32_t id : pendingReleases)
        {
            auto found = meshes.find(id);
            if (found == meshes.end())
                continue;
            RetireMeshLocked(found->second, slot);
            meshes.erase(found);
        }
        pendingReleases.clear();
        // SetMesh told the managed side the mesh is taken, and it does not hand it over
        // again: a mesh leaves the pending ones once it is built. Where the descriptor
        // slots run out, it and the rest wait for a later frame; one whose buffers
        // cannot be made is tried again in later frames, after the others, and
        // dropped after kMeshAttempts.
        uint32_t built = 0;
        uint64_t builtIndices = 0;
        uint64_t builtVertices = 0;
        uint64_t largestIndices = 0;
        for (auto entry = pendingMeshes.begin(); entry != pendingMeshes.end();)
        {
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
            if (!UploadMeshLocked(mesh, entry->second, slot))
            {
                // The list may already copy into the buffers made before the failure.
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
            auto existing = meshes.find(entry->first);
            if (existing != meshes.end())
                RetireMeshLocked(existing->second, slot);
            meshes[entry->first] = std::move(mesh);
            builtIndices += entry->second.indices.size();
            builtVertices += entry->second.vertexCount;
            largestIndices = std::max<uint64_t>(largestIndices, entry->second.indices.size());
            entry = pendingMeshes.erase(entry);
            ++built;
        }
        // Many at once, a few times a session: what they are, for the frame's time
        // (the slow frame's line, RaytracingDevice).
        if (built >= 50 && bigBuildsLogged < 10)
        {
            ++bigBuildsLogged;
            LogLine("Raytracing: " + std::to_string(built) + " meshes built in one frame, " + std::to_string(builtIndices / 3)
                    + " triangles, " + std::to_string(builtVertices) + " vertices, the largest "
                    + std::to_string(largestIndices / 3) + " triangles");
        }
        if (built > 0)
        {
            D3D12_RESOURCE_BARRIER barrier = UavBarrier(nullptr);
            list->ResourceBarrier(1, &barrier);
        }
        return built;
    }

    bool Raytracing::UploadMeshLocked(Mesh& mesh, const PendingMesh& pending, Slot& slot)
    {
        const UINT64 positionsSize = pending.positions.size() * sizeof(float);
        const UINT64 attributesSize = pending.attributes.size() * sizeof(float);
        const UINT64 indicesSize = pending.indices.size() * sizeof(uint32_t);

        D3D12_HEAP_PROPERTIES defaultHeap = {};
        defaultHeap.Type = D3D12_HEAP_TYPE_DEFAULT;
        D3D12_HEAP_PROPERTIES uploadHeap = {};
        uploadHeap.Type = D3D12_HEAP_TYPE_UPLOAD;

        const D3D12_RESOURCE_DESC positionsDesc = RtBufferDesc(positionsSize, D3D12_RESOURCE_FLAG_NONE);
        const D3D12_RESOURCE_DESC attributesDesc = RtBufferDesc(attributesSize, D3D12_RESOURCE_FLAG_NONE);
        const D3D12_RESOURCE_DESC indicesDesc = RtBufferDesc(indicesSize, D3D12_RESOURCE_FLAG_NONE);
        const UINT64 uploadSize = positionsSize + attributesSize + indicesSize;
        const D3D12_RESOURCE_DESC uploadDesc = RtBufferDesc(uploadSize, D3D12_RESOURCE_FLAG_NONE);

        ComPtr<ID3D12Resource> upload;
        // Buffers start in COMMON whatever is asked for, and the copy promotes them
        // to COPY_DEST.
        HRESULT hr = device->CreateCommittedResource(&defaultHeap, D3D12_HEAP_FLAG_NONE, &positionsDesc,
                                                     D3D12_RESOURCE_STATE_COMMON, nullptr, IID_PPV_ARGS(&mesh.positions));
        if (SUCCEEDED(hr))
            hr = device->CreateCommittedResource(&defaultHeap, D3D12_HEAP_FLAG_NONE, &attributesDesc,
                                                 D3D12_RESOURCE_STATE_COMMON, nullptr, IID_PPV_ARGS(&mesh.attributes));
        if (SUCCEEDED(hr))
            hr = device->CreateCommittedResource(&defaultHeap, D3D12_HEAP_FLAG_NONE, &indicesDesc,
                                                 D3D12_RESOURCE_STATE_COMMON, nullptr, IID_PPV_ARGS(&mesh.indices));
        if (SUCCEEDED(hr))
            hr = device->CreateCommittedResource(&uploadHeap, D3D12_HEAP_FLAG_NONE, &uploadDesc,
                                                 D3D12_RESOURCE_STATE_GENERIC_READ, nullptr, IID_PPV_ARGS(&upload));
        uint8_t* mapped = nullptr;
        if (SUCCEEDED(hr))
        {
            const D3D12_RANGE none = { 0, 0 };
            hr = upload->Map(0, &none, reinterpret_cast<void**>(&mapped));
        }
        if (FAILED(hr))
        {
            SetProblem("a mesh's buffers could not be made (" + Hr(hr) + ")");
            return false;
        }
        memcpy(mapped, pending.positions.data(), static_cast<size_t>(positionsSize));
        memcpy(mapped + positionsSize, pending.attributes.data(), static_cast<size_t>(attributesSize));
        memcpy(mapped + positionsSize + attributesSize, pending.indices.data(), static_cast<size_t>(indicesSize));
        upload->Unmap(0, nullptr);

        list->CopyBufferRegion(mesh.positions.Get(), 0, upload.Get(), 0, positionsSize);
        list->CopyBufferRegion(mesh.attributes.Get(), 0, upload.Get(), positionsSize, attributesSize);
        list->CopyBufferRegion(mesh.indices.Get(), 0, upload.Get(), positionsSize + attributesSize, indicesSize);
        const D3D12_RESOURCE_BARRIER readable[3] = {
            TransitionBarrier(mesh.positions.Get(), D3D12_RESOURCE_STATE_COPY_DEST,
                              D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE),
            TransitionBarrier(mesh.attributes.Get(), D3D12_RESOURCE_STATE_COPY_DEST,
                              D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE),
            TransitionBarrier(mesh.indices.Get(), D3D12_RESOURCE_STATE_COPY_DEST,
                              D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE),
        };
        list->ResourceBarrier(3, readable);
        slot.held.push_back(upload);

        D3D12_RAYTRACING_GEOMETRY_DESC geometry = {};
        geometry.Type = D3D12_RAYTRACING_GEOMETRY_TYPE_TRIANGLES;
        geometry.Flags = D3D12_RAYTRACING_GEOMETRY_FLAG_OPAQUE;
        geometry.Triangles.VertexBuffer.StartAddress = mesh.positions->GetGPUVirtualAddress();
        geometry.Triangles.VertexBuffer.StrideInBytes = 3 * sizeof(float);
        geometry.Triangles.VertexFormat = DXGI_FORMAT_R32G32B32_FLOAT;
        geometry.Triangles.VertexCount = pending.vertexCount;
        geometry.Triangles.IndexBuffer = mesh.indices->GetGPUVirtualAddress();
        geometry.Triangles.IndexFormat = DXGI_FORMAT_R32_UINT;
        geometry.Triangles.IndexCount = static_cast<UINT>(pending.indices.size());

        D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_INPUTS inputs = {};
        inputs.Type = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_TYPE_BOTTOM_LEVEL;
        inputs.Flags = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BUILD_FLAG_PREFER_FAST_TRACE;
        inputs.NumDescs = 1;
        inputs.DescsLayout = D3D12_ELEMENTS_LAYOUT_ARRAY;
        inputs.pGeometryDescs = &geometry;
        D3D12_RAYTRACING_ACCELERATION_STRUCTURE_PREBUILD_INFO info = {};
        device->GetRaytracingAccelerationStructurePrebuildInfo(&inputs, &info);
        if (info.ResultDataMaxSizeInBytes == 0)
        {
            SetProblem("a mesh's acceleration structure has no size");
            return false;
        }

        ComPtr<ID3D12Resource> scratch;
        const D3D12_RESOURCE_DESC blasDesc =
            RtBufferDesc(info.ResultDataMaxSizeInBytes, D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS);
        const D3D12_RESOURCE_DESC scratchDesc =
            RtBufferDesc(info.ScratchDataSizeInBytes, D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS);
        hr = device->CreateCommittedResource(&defaultHeap, D3D12_HEAP_FLAG_NONE, &blasDesc,
                                             D3D12_RESOURCE_STATE_RAYTRACING_ACCELERATION_STRUCTURE, nullptr,
                                             IID_PPV_ARGS(&mesh.blas));
        if (SUCCEEDED(hr))
            hr = device->CreateCommittedResource(&defaultHeap, D3D12_HEAP_FLAG_NONE, &scratchDesc,
                                                 D3D12_RESOURCE_STATE_COMMON, nullptr, IID_PPV_ARGS(&scratch));
        if (FAILED(hr))
        {
            SetProblem("a mesh's acceleration structure could not be made (" + Hr(hr) + ")");
            return false;
        }

        D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_DESC build = {};
        build.Inputs = inputs;
        build.DestAccelerationStructureData = mesh.blas->GetGPUVirtualAddress();
        build.ScratchAccelerationStructureData = scratch->GetGPUVirtualAddress();
        list->BuildRaytracingAccelerationStructure(&build, 0, nullptr);
        slot.held.push_back(scratch);

        // The index and attribute views the passes read at a hit.
        const D3D12_CPU_DESCRIPTOR_HANDLE heapCpu = heap->GetCPUDescriptorHandleForHeapStart();
        D3D12_CPU_DESCRIPTOR_HANDLE indexView = heapCpu;
        indexView.ptr += static_cast<SIZE_T>(kSlots * kFrameDescriptors + mesh.slot) * increment;
        D3D12_SHADER_RESOURCE_VIEW_DESC raw = {};
        raw.Format = DXGI_FORMAT_R32_TYPELESS;
        raw.ViewDimension = D3D12_SRV_DIMENSION_BUFFER;
        raw.Shader4ComponentMapping = D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING;
        raw.Buffer.NumElements = static_cast<UINT>(pending.indices.size());
        raw.Buffer.Flags = D3D12_BUFFER_SRV_FLAG_RAW;
        device->CreateShaderResourceView(mesh.indices.Get(), &raw, indexView);
        D3D12_CPU_DESCRIPTOR_HANDLE attributeView = heapCpu;
        attributeView.ptr += static_cast<SIZE_T>(kSlots * kFrameDescriptors + kMaxMeshes + mesh.slot) * increment;
        D3D12_SHADER_RESOURCE_VIEW_DESC structured = {};
        structured.Format = DXGI_FORMAT_UNKNOWN;
        structured.ViewDimension = D3D12_SRV_DIMENSION_BUFFER;
        structured.Shader4ComponentMapping = D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING;
        structured.Buffer.NumElements = pending.vertexCount;
        structured.Buffer.StructureByteStride = kAttributeFloats * sizeof(float);
        device->CreateShaderResourceView(mesh.attributes.Get(), &structured, attributeView);

        mesh.built = true;
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
            desc.AccelerationStructure = mesh->second.blas->GetGPUVirtualAddress();
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
        inputs.Flags = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BUILD_FLAG_PREFER_FAST_BUILD;
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
            const D3D12_GPU_VIRTUAL_ADDRESS blas = mesh->second.blas->GetGPUVirtualAddress();
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
