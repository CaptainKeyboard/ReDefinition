#include "Raytracing.h"

#include "D3d12Util.h"
#include "Log.h"

#include <algorithm>
#include <cmath>
#include <cstring>

using Microsoft::WRL::ComPtr;

namespace redefinition
{
    // Per vertex: normal (three floats), texture coordinate (two), colour (four bytes).
    constexpr size_t kAttributeFloats = 6;

    namespace
    {
        // A shared copy of a Unity texture not used for this long is released.
        constexpr ULONGLONG kUnusedMs = 10000;
        constexpr UINT64 kConstantsSize = 512;
        // Start, after the build, after each pass.
        constexpr UINT kTimestampsPerSlot = 2 + kRtPassTimings;

        // The passes, by the signal bit that asks for them, and the entry point
        // each is compiled from (CMakeLists.txt embeds them as RT_<entry>).
        struct PassEntry
        {
            uint32_t signal;
            const wchar_t* resource;
        };
        const PassEntry kPasses[] = {
            { kRtSignalSunVisibility, L"RT_SunVisibility" },
            { kRtSignalAmbientOcclusion, L"RT_AmbientOcclusion" },
            { kRtSignalDiffuse, L"RT_DiffuseLight" },
            { kRtSignalSpecular, L"RT_SpecularLight" },
            { kRtSignalForwardLight, L"RT_ForwardLight" },
            { kRtSignalPrimaryCheck, L"RT_PrimaryCheck" },
        };

        // The denoiser's entry points (RaytracingShaders/Denoise.hlsl).
        const wchar_t* const kDenoiseEntries[] = {
            L"RT_DenoisePrepare", L"RT_DenoiseTemporal", L"RT_DenoiseVariance", L"RT_DenoiseAtrous",
            L"RT_ReconstructGuides", L"RT_FrameGuides", L"RT_GpuInstances", L"RT_RegenerationGuides",
            L"RT_RegenerationAlbedo",
        };

        // The output slot of each signal the denoiser filters, and its place among the
        // denoiser's noisy textures.
        struct NoisySlot
        {
            uint32_t signal;
            uint32_t output;
            uint32_t index;
        };
        const NoisySlot kNoisy[] = {
            { kRtSignalSunVisibility, 0, 0 },
            { kRtSignalDiffuse, 1, 1 },
            { kRtSignalSpecular, 2, 2 },
            { kRtSignalAmbientOcclusion, 3, 3 },
        };

        UINT SignalIndex(uint32_t signal)
        {
            UINT index = 0;
            while (signal > 1u)
            {
                signal >>= 1;
                ++index;
            }
            return index;
        }

        DXGI_FORMAT ShareFormat(DXGI_FORMAT format)
        {
            const DXGI_FORMAT typed = TypedFormat(format);
            switch (typed)
            {
            case DXGI_FORMAT_R8G8B8A8_UNORM_SRGB: return DXGI_FORMAT_R8G8B8A8_UNORM;
            case DXGI_FORMAT_B8G8R8A8_UNORM_SRGB: return DXGI_FORMAT_B8G8R8A8_UNORM;
            default: return typed;
            }
        }

        D3D12_RESOURCE_DESC BufferDesc(UINT64 size, D3D12_RESOURCE_FLAGS flags)
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

        D3D12_RESOURCE_BARRIER UavBarrier(ID3D12Resource* resource)
        {
            D3D12_RESOURCE_BARRIER barrier = {};
            barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_UAV;
            barrier.UAV.pResource = resource;
            return barrier;
        }

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

        HRESULT SharedFence(ID3D11Device5* device11, ID3D12Device* device12, ComPtr<ID3D12Fence>& fence12,
                            ComPtr<ID3D11Fence>& fence11)
        {
            HRESULT hr = device12->CreateFence(0, D3D12_FENCE_FLAG_SHARED, IID_PPV_ARGS(&fence12));
            HANDLE handle = nullptr;
            if (SUCCEEDED(hr))
                hr = device12->CreateSharedHandle(fence12.Get(), nullptr, GENERIC_ALL, nullptr, &handle);
            if (SUCCEEDED(hr))
            {
                hr = device11->OpenSharedFence(handle, IID_PPV_ARGS(&fence11));
                CloseHandle(handle);
            }
            return hr;
        }

        HMODULE OwnModule()
        {
            HMODULE module = nullptr;
            GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                               reinterpret_cast<LPCWSTR>(&OwnModule), &module);
            return module;
        }
    }

    // Never destroyed, like D3d12Compute: at process exit the devices it holds may be
    // gone.
    Raytracing& Raytracing::Get()
    {
        static Raytracing* instance = new Raytracing();
        return *instance;
    }

    void Raytracing::Attach(const void* newOwner, ID3D11Device5* newDevice11, ID3D11DeviceContext4* newContext11,
                            ID3D12Device* newDevice12, ID3D12CommandQueue* newQueue)
    {
        std::lock_guard<std::mutex> lock(mutex);
        ReleaseLocked();
        owner = newOwner;
        device11 = newDevice11;
        context11 = newContext11;
        queue = newQueue;
        device.Reset();
        tier = 0;
        unavailable.clear();

        if (newDevice12 == nullptr || FAILED(newDevice12->QueryInterface(IID_PPV_ARGS(&device))))
            unavailable = "the Direct3D 12 device has no raytracing interface (ID3D12Device5)";
        else
        {
            D3D12_FEATURE_DATA_D3D12_OPTIONS5 options5 = {};
            if (SUCCEEDED(device->CheckFeatureSupport(D3D12_FEATURE_D3D12_OPTIONS5, &options5, sizeof(options5))))
                tier = static_cast<int>(options5.RaytracingTier);
            if (tier < D3D12_RAYTRACING_TIER_1_1)
                unavailable = "the GPU or its driver offers raytracing tier " + std::to_string(tier / 10) + "."
                              + std::to_string(tier % 10) + "; ray queries need 1.1";
            else if (!LoadShadersLocked())
                unavailable = "this build of the proxy has no raytracing shaders";
        }

        attached = device11 != nullptr && context11 != nullptr && device != nullptr && queue != nullptr;
        if (attached)
            LogLine(unavailable.empty() ? "Raytracing: ready, tier " + std::to_string(tier / 10) + "."
                                              + std::to_string(tier % 10)
                                        : "Raytracing: not available -- " + unavailable);
    }

    void Raytracing::Detach(const void* detaching)
    {
        std::lock_guard<std::mutex> lock(mutex);
        if (detaching != owner)
            return;
        ReleaseLocked();
        queue.Reset();
        device.Reset();
        context11.Reset();
        device11.Reset();
        owner = nullptr;
        attached = false;
    }

    int Raytracing::Available(char* buffer, int size) const
    {
        std::lock_guard<std::mutex> lock(mutex);
        const std::string text = !attached ? std::string("no Direct3D 12 device yet") : unavailable.empty() ? "ready" : unavailable;
        if (buffer != nullptr && size > 0)
            strncpy_s(buffer, static_cast<size_t>(size), text.c_str(), _TRUNCATE);
        if (!attached)
            return 0;
        return unavailable.empty() ? 1 : -1;
    }

    bool Raytracing::LoadShadersLocked()
    {
        if (!shaders.empty())
            return true;
        const HMODULE module = OwnModule();
        for (const PassEntry& pass : kPasses)
        {
            const HRSRC found = FindResourceW(module, pass.resource, MAKEINTRESOURCEW(10));   // RT_RCDATA
            const HGLOBAL loaded = found != nullptr ? LoadResource(module, found) : nullptr;
            const void* bytes = loaded != nullptr ? LockResource(loaded) : nullptr;
            const DWORD length = found != nullptr ? SizeofResource(module, found) : 0;
            if (bytes == nullptr || length == 0)
            {
                shaders.clear();
                return false;
            }
            std::vector<uint8_t> code(static_cast<const uint8_t*>(bytes), static_cast<const uint8_t*>(bytes) + length);
            char name[64] = {};
            WideCharToMultiByte(CP_UTF8, 0, pass.resource, -1, name, sizeof(name), nullptr, nullptr);
            shaders[name] = std::move(code);
        }
        for (const wchar_t* entry : kDenoiseEntries)
        {
            const HRSRC found = FindResourceW(module, entry, MAKEINTRESOURCEW(10));
            const HGLOBAL loaded = found != nullptr ? LoadResource(module, found) : nullptr;
            const void* bytes = loaded != nullptr ? LockResource(loaded) : nullptr;
            const DWORD length = found != nullptr ? SizeofResource(module, found) : 0;
            if (bytes == nullptr || length == 0)
            {
                shaders.clear();
                return false;
            }
            char name[64] = {};
            WideCharToMultiByte(CP_UTF8, 0, entry, -1, name, sizeof(name), nullptr, nullptr);
            shaders[name].assign(static_cast<const uint8_t*>(bytes), static_cast<const uint8_t*>(bytes) + length);
        }
        return true;
    }

    // ---------------------------------------------------------------- main thread

    int Raytracing::SetMesh(const RtMeshDesc* desc)
    {
        if (desc == nullptr || desc->size != sizeof(RtMeshDesc) || desc->magic != kRtMeshMagic)
        {
            SetProblem("a mesh rejected: the mod and the proxy disagree on its layout");
            return 0;
        }
        if (desc->positions == nullptr || desc->indices == nullptr || desc->vertexCount == 0 || desc->indexCount < 3
            || desc->indexCount % 3 != 0)
        {
            SetProblem("mesh " + std::to_string(desc->mesh) + " rejected: no positions or no whole triangles");
            return 0;
        }
        PendingMesh pending;
        pending.vertexCount = desc->vertexCount;
        pending.positions.assign(desc->positions, desc->positions + static_cast<size_t>(desc->vertexCount) * 3);
        pending.attributes.resize(static_cast<size_t>(desc->vertexCount) * kAttributeFloats, 0.0f);
        const uint32_t white = 0xFFFFFFFFu;
        for (uint32_t v = 0; v < desc->vertexCount; ++v)
        {
            float* attribute = &pending.attributes[static_cast<size_t>(v) * kAttributeFloats];
            // The colour's four bytes as the shader's uint (VertexAttributes.color).
            memcpy(attribute + 5, desc->colors != nullptr ? desc->colors + v : &white, sizeof(uint32_t));
            if (desc->normals != nullptr)
                memcpy(attribute, desc->normals + static_cast<size_t>(v) * 3, 3 * sizeof(float));
            else
                attribute[1] = 1.0f;
            if (desc->uvs != nullptr)
                memcpy(attribute + 3, desc->uvs + static_cast<size_t>(v) * 2, 2 * sizeof(float));
        }
        pending.indices.assign(desc->indices, desc->indices + desc->indexCount);
        for (uint32_t& index : pending.indices)
            if (index >= desc->vertexCount)
            {
                SetProblem("mesh " + std::to_string(desc->mesh) + " rejected: an index past its vertices");
                return 0;
            }

        std::lock_guard<std::mutex> lock(mutex);
        pendingReleases.erase(std::remove(pendingReleases.begin(), pendingReleases.end(), desc->mesh),
                              pendingReleases.end());
        pendingMeshes[desc->mesh] = std::move(pending);
        return 1;
    }

    void Raytracing::ReleaseMesh(int32_t mesh)
    {
        std::lock_guard<std::mutex> lock(mutex);
        pendingMeshes.erase(mesh);
        pendingReleases.push_back(mesh);
    }

    int Raytracing::SetMaterial(const RtMaterialDesc* desc)
    {
        if (desc == nullptr || desc->size != sizeof(RtMaterialDesc) || desc->magic != kRtMaterialMagic)
        {
            SetProblem("a material rejected: the mod and the proxy disagree on its layout");
            return 0;
        }
        MaterialData data = {};
        memcpy(data.albedo, desc->albedo, sizeof(data.albedo));
        memcpy(data.specular, desc->specular, sizeof(data.specular));
        data.smoothness = desc->smoothness;
        memcpy(data.emission, desc->emission, sizeof(data.emission));
        data.texture = desc->vertexColour > 0.0f ? -1 : desc->texture;
        data.vertexColour[0] = desc->vertexColour;
        std::lock_guard<std::mutex> lock(mutex);
        materialData[desc->material] = data;
        return 1;
    }

    void Raytracing::ReleaseMaterial(int32_t material)
    {
        std::lock_guard<std::mutex> lock(mutex);
        materialData.erase(material);
    }

    Raytracing::FrameScene& Raytracing::FrameSceneLocked(uint32_t frameIndex)
    {
        // A scene for every frame (kRtAnyFrame) takes the place of the frames' own.
        if (frameIndex == kRtAnyFrame && (frameScenes.empty() || frameScenes.back().frameIndex != kRtAnyFrame))
        {
            frameScenes.clear();
            frameScenes.emplace_back();
            return frameScenes.back();
        }
        for (FrameScene& scene : frameScenes)
            if (scene.frameIndex == frameIndex)
                return scene;
        // The main thread runs a frame or two ahead of the render thread.
        while (frameScenes.size() >= 8)
            frameScenes.pop_front();
        frameScenes.emplace_back();
        frameScenes.back().frameIndex = frameIndex;
        if (frameScenes.size() > 1)
            frameScenes.back().groups = frameScenes[frameScenes.size() - 2].groups;
        return frameScenes.back();
    }

    int Raytracing::SetInstances(const RtInstance* given, int count, uint32_t frameIndex)
    {
        if (count < 0 || (count > 0 && given == nullptr))
            return 0;
        std::lock_guard<std::mutex> lock(mutex);
        FrameSceneLocked(frameIndex).instances.assign(given, given + count);
        return 1;
    }

    int Raytracing::SetGpuGroups(const RtGpuInstanceGroup* given, int count, uint32_t frameIndex)
    {
        if (count < 0 || (count > 0 && given == nullptr))
            return 0;
        std::lock_guard<std::mutex> lock(mutex);
        FrameSceneLocked(frameIndex).groups.assign(given, given + count);
        return 1;
    }

    int Raytracing::SetMaterialTexture(int layer, const uint8_t* pixels, int size)
    {
        if (layer < 0 || layer >= static_cast<int>(kRtMaxMaterialTextures) || pixels == nullptr
            || size != static_cast<int>(kRtMaterialTextureSize))
        {
            SetProblem("a material texture rejected: layer " + std::to_string(layer) + ", size " + std::to_string(size));
            return 0;
        }
        const size_t bytes = static_cast<size_t>(size) * size * 4;
        std::lock_guard<std::mutex> lock(mutex);
        pendingTextures[layer].assign(pixels, pixels + bytes);
        return 1;
    }

    // ---------------------------------------------------------------- render thread

    void Raytracing::OnFrame(const void* data)
    {
        const RtFramePacket* packet = static_cast<const RtFramePacket*>(data);
        if (packet == nullptr || packet->size != sizeof(RtFramePacket) || packet->magic != kRtFrameMagic)
        {
            SetProblem("a frame rejected: the mod and the proxy disagree on its layout");
            return;
        }

        std::lock_guard<std::mutex> lock(mutex);
        if (!attached || !unavailable.empty())
            return;
        if (packet->width == 0 || packet->height == 0 || packet->inputs[kRtInputDepth] == nullptr)
        {
            SetProblem("a frame without a size or without depth");
            return;
        }
        if (!EnsureDeviceLocked())
            return;

        // Retire unused shared copies once a second.
        const ULONGLONG now = GetTickCount64();
        if (now >= nextEviction)
        {
            nextEviction = now + 1000;
            for (auto it = textures.begin(); it != textures.end();)
                it = now - it->second.lastUse > kUnusedMs ? textures.erase(it) : std::next(it);
        }

        Shared* inputs[kRtMaxTextures] = {};
        Shared* outputs[kRtMaxTextures] = {};
        std::string problem;
        for (uint32_t i = 0; i < kRtMaxTextures; ++i)
        {
            if (packet->inputs[i] != nullptr && (inputs[i] = SharedForLocked(packet->inputs[i], problem)) == nullptr)
            {
                SetProblem("input " + std::to_string(i) + ": " + problem);
                return;
            }
            if (packet->outputs[i] != nullptr && (outputs[i] = SharedForLocked(packet->outputs[i], problem)) == nullptr)
            {
                SetProblem("output " + std::to_string(i) + ": " + problem);
                return;
            }
        }

        // Direct3D 11: this frame's inputs into the shared copies, once the queue is past
        // every list that used them.
        UINT64 usedUntil = 0;
        for (uint32_t i = 0; i < kRtMaxTextures; ++i)
        {
            if (inputs[i] != nullptr)
                usedUntil = std::max(usedUntil, inputs[i]->lastFence);
            if (outputs[i] != nullptr)
                usedUntil = std::max(usedUntil, outputs[i]->lastFence);
        }
        if (usedUntil != 0)
            context11->Wait(fence11.Get(), usedUntil);
        for (uint32_t i = 0; i < kRtMaxTextures; ++i)
            if (inputs[i] != nullptr)
                context11->CopyResource(inputs[i]->texture.d3d11.Get(), static_cast<ID3D11Resource*>(packet->inputs[i]));
        const UINT64 inputsCopied = ++copiedValue;
        context11->Signal(copied11.Get(), inputsCopied);
        context11->Flush();
        queue->Wait(copied12.Get(), inputsCopied);

        const UINT slotNumber = slotIndex;
        Slot& slot = slots[slotIndex];
        slotIndex = (slotIndex + 1) % kSlots;
        WaitForSlotLocked(slot);
        slot.held.clear();
        HRESULT hr = slot.allocator->Reset();
        if (SUCCEEDED(hr))
            hr = list->Reset(slot.allocator.Get(), nullptr);
        if (FAILED(hr))
        {
            SetProblem("the command list could not be reset (" + Hr(hr) + ")");
            return;
        }

        const UINT timestampBase = slotNumber * kTimestampsPerSlot;
        list->EndQuery(timestamps.Get(), D3D12_QUERY_TYPE_TIMESTAMP, timestampBase);

        // Meshes: releases first, then what is new, each into its own acceleration
        // structure.
        for (int32_t id : pendingReleases)
        {
            auto found = meshes.find(id);
            if (found == meshes.end())
                continue;
            Mesh& mesh = found->second;
            slot.held.push_back(mesh.positions);
            slot.held.push_back(mesh.attributes);
            slot.held.push_back(mesh.indices);
            slot.held.push_back(mesh.blas);
            retiredSlots.emplace_back(mesh.slot, fenceValue + 1);
            meshes.erase(found);
        }
        pendingReleases.clear();
        // SetMesh told the managed side the mesh is taken, and it does not hand it over
        // again: a mesh leaves the pending ones once it is built. Where the descriptor
        // slots run out, it and the rest wait for a later frame; one whose buffers
        // cannot be made is tried again in later frames, after the others, and
        // dropped after kMeshAttempts.
        uint32_t built = 0;
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
                slot.held.push_back(mesh.positions);
                slot.held.push_back(mesh.attributes);
                slot.held.push_back(mesh.indices);
                slot.held.push_back(mesh.blas);
                retiredSlots.emplace_back(mesh.slot, fenceValue + 1);
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
            {
                Mesh& old = existing->second;
                slot.held.push_back(old.positions);
                slot.held.push_back(old.attributes);
                slot.held.push_back(old.indices);
                slot.held.push_back(old.blas);
                retiredSlots.emplace_back(old.slot, fenceValue + 1);
            }
            meshes[entry->first] = std::move(mesh);
            entry = pendingMeshes.erase(entry);
            ++built;
        }
        if (built > 0)
        {
            D3D12_RESOURCE_BARRIER barrier = UavBarrier(nullptr);
            list->ResourceBarrier(1, &barrier);
        }

        // Material textures: the array grows to hold the highest layer, then the new
        // layers are copied in.
        if (!pendingTextures.empty() || materialTextures == nullptr)
        {
            UINT highest = 0;
            for (const auto& entry : pendingTextures)
                highest = std::max<UINT>(highest, static_cast<UINT>(entry.first) + 1);
            if (!EnsureMaterialTexturesLocked(std::max<UINT>(highest, 1), slot))
            {
                SubmitLocked(slot, inputs, outputs);
                return;
            }
            if (!pendingTextures.empty())
            {
                const UINT64 layerBytes = static_cast<UINT64>(kRtMaterialTextureSize) * kRtMaterialTextureSize * 4;
                ComPtr<ID3D12Resource> upload;
                if (EnsureUploadBuffer(upload, layerBytes * pendingTextures.size()))
                {
                    uint8_t* mapped = nullptr;
                    const D3D12_RANGE none = { 0, 0 };
                    upload->Map(0, &none, reinterpret_cast<void**>(&mapped));
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
                }
                else
                    SetProblem("the material textures' upload buffer could not be made");
            }
        }

        UINT instanceCount = 0;
        if (!BuildSceneLocked(slot, slotNumber, packet->frameIndex, inputs[kRtInputInstances], instanceCount))
        {
            SubmitLocked(slot, inputs, outputs);
            return;
        }
        list->EndQuery(timestamps.Get(), D3D12_QUERY_TYPE_TIMESTAMP, timestampBase + 1);

        // With the denoiser, the passes write its noisy textures, and it writes Unity's.
        const bool denoising = (packet->flags & kRtFrameDenoise) != 0;
        if (denoising && !EnsureDenoiseTargetsLocked(packet->width, packet->height, slot))
        {
            SubmitLocked(slot, inputs, outputs);
            return;
        }
        DenoiseTarget* noisyFor[kRtMaxTextures] = {};
        if (denoising)
            for (const NoisySlot& noisy : kNoisy)
                if ((packet->signals & noisy.signal) != 0 && outputs[noisy.output] != nullptr)
                {
                    noisyFor[noisy.output] = &denoise.noisy[noisy.index];
                    UseLocked(denoise.noisy[noisy.index], D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
                }

        // Descriptors of this frame: t1-t8, then u0-u7, in the slot's part of the heap.
        const D3D12_CPU_DESCRIPTOR_HANDLE heapCpu = heap->GetCPUDescriptorHandleForHeapStart();
        const D3D12_GPU_DESCRIPTOR_HANDLE heapGpu = heap->GetGPUDescriptorHandleForHeapStart();
        D3D12_CPU_DESCRIPTOR_HANDLE cpu = heapCpu;
        cpu.ptr += static_cast<SIZE_T>(slotNumber) * kFrameDescriptors * increment;
        D3D12_GPU_DESCRIPTOR_HANDLE frameTable = heapGpu;
        frameTable.ptr += static_cast<UINT64>(slotNumber) * kFrameDescriptors * increment;
        for (uint32_t i = 0; i < kRtMaxTextures; ++i)
        {
            D3D12_SHADER_RESOURCE_VIEW_DESC srv = {};
            srv.Format = inputs[i] != nullptr ? inputs[i]->texture.format : DXGI_FORMAT_R8G8B8A8_UNORM;
            srv.ViewDimension = D3D12_SRV_DIMENSION_TEXTURE2D;
            srv.Shader4ComponentMapping = D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING;
            srv.Texture2D.MipLevels = 1;
            device->CreateShaderResourceView(inputs[i] != nullptr ? inputs[i]->texture.d3d12.Get() : nullptr, &srv, cpu);
            cpu.ptr += increment;
        }
        for (uint32_t i = 0; i < kRtMaxTextures; ++i)
        {
            D3D12_UNORDERED_ACCESS_VIEW_DESC uav = {};
            uav.ViewDimension = D3D12_UAV_DIMENSION_TEXTURE2D;
            if (noisyFor[i] != nullptr)
            {
                uav.Format = noisyFor[i]->format;
                device->CreateUnorderedAccessView(noisyFor[i]->resource.Get(), nullptr, &uav, cpu);
            }
            else
            {
                uav.Format = outputs[i] != nullptr ? outputs[i]->texture.format : DXGI_FORMAT_R16G16B16A16_FLOAT;
                device->CreateUnorderedAccessView(outputs[i] != nullptr ? outputs[i]->texture.d3d12.Get() : nullptr, nullptr,
                                                  &uav, cpu);
            }
            cpu.ptr += increment;
        }

        // Constants: the packet's matrices and vectors, then the size, frame and flags.
        uint8_t* constants = slot.constantsMapped;
        memset(constants, 0, static_cast<size_t>(kConstantsSize));
        memcpy(constants, packet->clipToWorld, 3 * 64);
        memcpy(constants + 192, packet->sunDirection, 16);
        memcpy(constants + 208, packet->sunColor, 16);
        memcpy(constants + 224, packet->skyColor, 16);
        memcpy(constants + 240, packet->params, 32);
        memcpy(constants + 272, packet->bodySphere, 16);
        memcpy(constants + 288, packet->ambient, 48);
        const uint32_t tail[4] = { packet->width, packet->height, packet->frameIndex, packet->flags };
        memcpy(constants + 336, tail, sizeof(tail));
        memcpy(constants + 352, packet->lights, 64);
        memcpy(constants + 416, packet->lightParams, 16);
        memcpy(constants + 432, packet->nightLight, 16);

        std::vector<D3D12_RESOURCE_BARRIER> into;
        std::vector<D3D12_RESOURCE_BARRIER> back;
        for (uint32_t i = 0; i < kRtMaxTextures; ++i)
        {
            if (inputs[i] != nullptr)
            {
                bool before = false;
                for (uint32_t j = 0; j < i; ++j)
                    before = before || inputs[j] == inputs[i];
                if (!before)
                {
                    into.push_back(TransitionBarrier(inputs[i]->texture.d3d12.Get(), D3D12_RESOURCE_STATE_COMMON,
                                                     D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE));
                    back.push_back(TransitionBarrier(inputs[i]->texture.d3d12.Get(),
                                                     D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE,
                                                     D3D12_RESOURCE_STATE_COMMON));
                    slot.held.push_back(inputs[i]->texture.d3d12);
                }
            }
            if (outputs[i] != nullptr)
            {
                into.push_back(TransitionBarrier(outputs[i]->texture.d3d12.Get(), D3D12_RESOURCE_STATE_COMMON,
                                                 D3D12_RESOURCE_STATE_UNORDERED_ACCESS));
                back.push_back(TransitionBarrier(outputs[i]->texture.d3d12.Get(), D3D12_RESOURCE_STATE_UNORDERED_ACCESS,
                                                 D3D12_RESOURCE_STATE_COMMON));
                slot.held.push_back(outputs[i]->texture.d3d12);
            }
        }
        if (!into.empty())
            list->ResourceBarrier(static_cast<UINT>(into.size()), into.data());

        // The frame's bindings: set now, and again after anything that sets its own.
        const auto bindFrame = [&]()
        {
            ID3D12DescriptorHeap* heaps[] = { heap.Get() };
            list->SetDescriptorHeaps(1, heaps);
            list->SetComputeRootSignature(rootSignature.Get());
            list->SetComputeRootConstantBufferView(0, slot.constants->GetGPUVirtualAddress());
            list->SetComputeRootShaderResourceView(1, tlas->GetGPUVirtualAddress());
            list->SetComputeRootDescriptorTable(2, frameTable);
            D3D12_GPU_DESCRIPTOR_HANDLE outputTable = frameTable;
            outputTable.ptr += static_cast<UINT64>(kRtMaxTextures) * increment;
            list->SetComputeRootDescriptorTable(3, outputTable);
            list->SetComputeRootShaderResourceView(4, slot.instanceDataAddress);
            list->SetComputeRootShaderResourceView(5, slot.materials->GetGPUVirtualAddress());
            D3D12_GPU_DESCRIPTOR_HANDLE meshTable = heapGpu;
            meshTable.ptr += static_cast<UINT64>(kSlots) * kFrameDescriptors * increment;
            list->SetComputeRootDescriptorTable(6, meshTable);
            meshTable.ptr += static_cast<UINT64>(kMaxMeshes) * increment;
            list->SetComputeRootDescriptorTable(7, meshTable);
            meshTable.ptr += static_cast<UINT64>(kMaxMeshes) * increment;
            list->SetComputeRootDescriptorTable(8, meshTable);
        };
        bindFrame();
        slot.held.push_back(materialTextures);

        const UINT groupsX = (packet->width + 7) / 8;
        const UINT groupsY = (packet->height + 7) / 8;
        // Each pass's end is stamped; a pass that does not run stamps where the one
        // before it ended, so it counts nothing. The signals first, then the denoiser
        // (its time as pass 6), then what reads the denoised signals.
        const UINT order[kRtPassTimings] = { 0, 1, 2, 3, 6, 4, 5, 7 };
        for (UINT position = 0; position < kRtPassTimings; ++position)
        {
            const UINT index = order[position];
            const uint32_t signal = 1u << index;
            const PassEntry* pass = nullptr;
            for (const PassEntry& entry : kPasses)
                if (entry.signal == signal)
                    pass = &entry;
            auto pipeline = pass != nullptr ? pipelines.find(pass->signal) : pipelines.end();
            if ((packet->signals & signal) != 0 && pipeline != pipelines.end())
            {
                // What an earlier pass or the denoiser wrote, finished before this one reads it.
                const D3D12_RESOURCE_BARRIER written = UavBarrier(nullptr);
                list->ResourceBarrier(1, &written);
                // The forward light reads the finished signals, Unity's outputs, as textures.
                std::vector<D3D12_RESOURCE_BARRIER> toRead, toWrite;
                if (signal == kRtSignalForwardLight)
                {
                    const uint32_t finished[2] = { 1, 2 };
                    const uint32_t traced[2] = { kRtSignalDiffuse, kRtSignalSpecular };
                    View reads[8] = {};
                    for (UINT k = 0; k < 2; ++k)
                    {
                        // A signal not traced this frame reads as none, whatever its texture holds.
                        Shared* output = (packet->signals & traced[k]) != 0 ? outputs[finished[k]] : nullptr;
                        if (output == nullptr)
                            continue;
                        reads[k] = Of(output);
                        toRead.push_back(TransitionBarrier(output->texture.d3d12.Get(), D3D12_RESOURCE_STATE_UNORDERED_ACCESS,
                                                           D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE));
                        toWrite.push_back(TransitionBarrier(output->texture.d3d12.Get(),
                                                            D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE,
                                                            D3D12_RESOURCE_STATE_UNORDERED_ACCESS));
                    }
                    if (!toRead.empty())
                        list->ResourceBarrier(static_cast<UINT>(toRead.size()), toRead.data());
                    UINT used = kDenoiseDescriptors - kTableSize;
                    const View writes[4] = {};
                    list->SetComputeRootDescriptorTable(10, TableLocked(slotNumber, used, reads, writes));
                }
                list->SetPipelineState(pipeline->second.Get());
                list->Dispatch(groupsX, groupsY, 1);
                if (!toWrite.empty())
                    list->ResourceBarrier(static_cast<UINT>(toWrite.size()), toWrite.data());
            }
            if (index == 6 && denoising)
            {
                // AMD FSR Ray Regeneration for the diffuse light and the reflections where it
                // runs, ReDefinition's denoiser for the rest; for all where it does not.
                bool amdDenoised = false;
                if (RegenerationReadyLocked(*packet))
                {
                    amdDenoised = RunRegenerationLocked(*packet, slotNumber, outputs, inputs[kRtInputMotion], slot);
                    // AMD's denoiser sets its own heap and root signature on the list.
                    bindFrame();
                }
                RtFramePacket rest = *packet;
                if (amdDenoised)
                    rest.signals &= ~(kRtSignalDiffuse | kRtSignalSpecular);
                // ReDefinition's history stood still while AMD's denoiser had the frames.
                else if (regeneration.previousWasAmd)
                    rest.flags |= kRtFrameReset;
                regeneration.previousWasAmd = amdDenoised;
                RunDenoiserLocked(rest, slotNumber, outputs, inputs[kRtInputMotion]);
            }
            list->EndQuery(timestamps.Get(), D3D12_QUERY_TYPE_TIMESTAMP, timestampBase + 2 + position);
        }

        // DLSS Ray Reconstruction's guides from this frame's surfaces, which the
        // reconstruction of the same frame takes (RaytracingReconstruct.cpp).
        guides.fromFrame = false;
        if ((packet->flags & kRtFrameGuides) != 0)
        {
            auto pipeline = denoisePipelines.find("RT_FrameGuides");
            if (pipeline != denoisePipelines.end() && EnsureGuidesLocked(slot.held, packet->width, packet->height))
            {
                const D3D12_RESOURCE_BARRIER written = UavBarrier(nullptr);
                list->ResourceBarrier(1, &written);
                for (DenoiseTarget* target : { &guides.albedo, &guides.specularAlbedo, &guides.normalRoughness, &guides.hitDistance })
                    UseLocked(*target, D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
                UINT used = kDenoiseDescriptors - 2 * kTableSize;
                const View reads[8] = {};
                const View writes[4] = { Of(&guides.albedo), Of(&guides.specularAlbedo), Of(&guides.normalRoughness),
                                         Of(&guides.hitDistance) };
                const D3D12_GPU_DESCRIPTOR_HANDLE guideTable = TableLocked(slotNumber, used, reads, writes);
                D3D12_GPU_DESCRIPTOR_HANDLE writeTable = guideTable;
                writeTable.ptr += static_cast<UINT64>(8) * increment;
                list->SetComputeRootDescriptorTable(10, guideTable);
                list->SetComputeRootDescriptorTable(11, writeTable);
                list->SetPipelineState(pipeline->second.Get());
                list->Dispatch(groupsX, groupsY, 1);
                // Streamline takes them in the state it is given.
                for (DenoiseTarget* target : { &guides.albedo, &guides.specularAlbedo, &guides.normalRoughness, &guides.hitDistance })
                    UseLocked(*target, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
                guides.fromFrame = true;
                guides.frame = packet->frameIndex;
            }
        }

        if (!back.empty())
            list->ResourceBarrier(static_cast<UINT>(back.size()), back.data());
        list->ResolveQueryData(timestamps.Get(), D3D12_QUERY_TYPE_TIMESTAMP, timestampBase, kTimestampsPerSlot,
                               timestampReadback.Get(), static_cast<UINT64>(timestampBase) * sizeof(UINT64));
        const UINT64 finished = SubmitLocked(slot, inputs, outputs);
        if (finished == 0)
            return;

        // The results back into Unity's textures before the event returns.
        context11->Wait(fence11.Get(), finished);
        for (uint32_t i = 0; i < kRtMaxTextures; ++i)
            if (outputs[i] != nullptr)
                context11->CopyResource(static_cast<ID3D11Resource*>(packet->outputs[i]), outputs[i]->texture.d3d11.Get());

        std::lock_guard<std::mutex> status(statusLock);
        ++frames;
        liveMeshes = static_cast<uint32_t>(meshes.size());
        lastInstances = instanceCount;
        lastBuilt = built;
    }

    UINT64 Raytracing::SubmitLocked(Slot& slot, Shared* const* inputs, Shared* const* outputs)
    {
        // A frame that stops early is submitted too: its list holds the meshes' uploads
        // and builds, which the meshes count as built, the material textures' copies,
        // which leave the pending ones, and barriers whose end states UseLocked and
        // sceneStateRecorded already took.
        const HRESULT hr = list->Close();
        if (FAILED(hr))
        {
            // None of it runs: everything made on the device goes, and the managed side,
            // seeing the generation change, hands its meshes and textures over again.
            SetProblem("the command list could not be closed (" + Hr(hr) + ")");
            ReleaseLocked();
            return 0;
        }
        ID3D12CommandList* lists[] = { list.Get() };
        queue->ExecuteCommandLists(1, lists);
        slot.sceneState = slot.sceneStateRecorded;
        const UINT64 finished = ++fenceValue;
        queue->Signal(fence12.Get(), finished);
        slot.done = finished;
        for (uint32_t i = 0; i < kRtMaxTextures; ++i)
        {
            if (inputs[i] != nullptr)
                inputs[i]->lastFence = finished;
            if (outputs[i] != nullptr)
                outputs[i]->lastFence = finished;
        }
        return finished;
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

    bool Raytracing::UploadMeshLocked(Mesh& mesh, const PendingMesh& pending, Slot& slot)
    {
        const UINT64 positionsSize = pending.positions.size() * sizeof(float);
        const UINT64 attributesSize = pending.attributes.size() * sizeof(float);
        const UINT64 indicesSize = pending.indices.size() * sizeof(uint32_t);

        D3D12_HEAP_PROPERTIES defaultHeap = {};
        defaultHeap.Type = D3D12_HEAP_TYPE_DEFAULT;
        D3D12_HEAP_PROPERTIES uploadHeap = {};
        uploadHeap.Type = D3D12_HEAP_TYPE_UPLOAD;

        const D3D12_RESOURCE_DESC positionsDesc = BufferDesc(positionsSize, D3D12_RESOURCE_FLAG_NONE);
        const D3D12_RESOURCE_DESC attributesDesc = BufferDesc(attributesSize, D3D12_RESOURCE_FLAG_NONE);
        const D3D12_RESOURCE_DESC indicesDesc = BufferDesc(indicesSize, D3D12_RESOURCE_FLAG_NONE);
        const UINT64 uploadSize = positionsSize + attributesSize + indicesSize;
        const D3D12_RESOURCE_DESC uploadDesc = BufferDesc(uploadSize, D3D12_RESOURCE_FLAG_NONE);

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
            BufferDesc(info.ResultDataMaxSizeInBytes, D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS);
        const D3D12_RESOURCE_DESC scratchDesc =
            BufferDesc(info.ScratchDataSizeInBytes, D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS);
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

        mesh.vertexCount = pending.vertexCount;
        mesh.indexCount = static_cast<uint32_t>(pending.indices.size());
        mesh.built = true;
        return true;
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
            D3D12_RAYTRACING_INSTANCE_DESC desc = {};
            memcpy(desc.Transform, instance.transform, sizeof(desc.Transform));
            desc.InstanceID = static_cast<UINT>(data.size()) & 0xFFFFFFu;
            desc.InstanceMask = instance.mask & 0xFFu;
            desc.InstanceContributionToHitGroupIndex = 0;
            // Terrain and cut-out surfaces go through the passes' candidate test.
            auto materialOf = materialData.find(instance.material);
            const bool cutout = materialOf != materialData.end() && materialOf->second.albedo[3] > 0.0f;
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
            if (descs.size() >= 0xFFFFFFu)
                break;
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
        // What each group is on the device; the ones whose mesh is not built are left out.
        const UINT capacityTexels = gpuInstances->texture.width / 3 * gpuInstances->texture.height;
        std::vector<GpuGroup> groups;
        UINT gpuCount = 0;
        for (const RtGpuInstanceGroup& group : given)
        {
            auto mesh = meshes.find(group.mesh);
            if (mesh == meshes.end() || !mesh->second.built || group.count == 0
                || static_cast<uint64_t>(group.first) + group.count > capacityTexels)
                continue;
            const D3D12_GPU_VIRTUAL_ADDRESS blas = mesh->second.blas->GetGPUVirtualAddress();
            auto material = materialIndex.find(group.material);
            auto materialOf = materialData.find(group.material);
            const bool cutout = materialOf != materialData.end() && materialOf->second.albedo[3] > 0.0f;
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
        if (gpuCount == 0 || cpuCount + static_cast<uint64_t>(gpuCount) >= 0xFFFFFFu)
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
        const UINT base = kSlots * kFrameDescriptors + 2 * kMaxMeshes + 1 + slotNumber * kDenoiseDescriptors
                          + kDenoiseDescriptors - 3 * kTableSize;
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
                srv.Format = i == 1 ? gpuInstances->texture.format : DXGI_FORMAT_R32G32B32A32_FLOAT;
                srv.ViewDimension = D3D12_SRV_DIMENSION_TEXTURE2D;
                srv.Texture2D.MipLevels = 1;
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
                uav.Format = DXGI_FORMAT_R16G16B16A16_FLOAT;
                uav.ViewDimension = D3D12_UAV_DIMENSION_TEXTURE2D;
                device->CreateUnorderedAccessView(nullptr, nullptr, &uav, cpu);
            }
            cpu.ptr += increment;
        }

        ID3D12DescriptorHeap* heaps[] = { heap.Get() };
        list->SetDescriptorHeaps(1, heaps);
        list->SetComputeRootSignature(rootSignature.Get());
        list->SetComputeRootDescriptorTable(10, gpu);
        D3D12_GPU_DESCRIPTOR_HANDLE writes = gpu;
        writes.ptr += static_cast<UINT64>(8) * increment;
        list->SetComputeRootDescriptorTable(11, writes);
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

    bool Raytracing::EnsureUploadBuffer(ComPtr<ID3D12Resource>& buffer, UINT64 size)
    {
        D3D12_HEAP_PROPERTIES upload = {};
        upload.Type = D3D12_HEAP_TYPE_UPLOAD;
        const D3D12_RESOURCE_DESC desc = BufferDesc(size, D3D12_RESOURCE_FLAG_NONE);
        buffer.Reset();
        return SUCCEEDED(device->CreateCommittedResource(&upload, D3D12_HEAP_FLAG_NONE, &desc,
                                                         D3D12_RESOURCE_STATE_GENERIC_READ, nullptr,
                                                         IID_PPV_ARGS(&buffer)));
    }

    bool Raytracing::EnsureDefaultBuffer(ComPtr<ID3D12Resource>& buffer, UINT64 size, D3D12_RESOURCE_STATES state,
                                         D3D12_RESOURCE_FLAGS flags)
    {
        D3D12_HEAP_PROPERTIES heapProperties = {};
        heapProperties.Type = D3D12_HEAP_TYPE_DEFAULT;
        const D3D12_RESOURCE_DESC desc = BufferDesc(size, flags);
        buffer.Reset();
        return SUCCEEDED(device->CreateCommittedResource(&heapProperties, D3D12_HEAP_FLAG_NONE, &desc, state, nullptr,
                                                         IID_PPV_ARGS(&buffer)));
    }

    Raytracing::Shared* Raytracing::SharedForLocked(void* unityTexture, std::string& problem)
    {
        ComPtr<ID3D11Texture2D> texture2d;
        if (FAILED(static_cast<ID3D11Resource*>(unityTexture)->QueryInterface(IID_PPV_ARGS(&texture2d))))
        {
            problem = "not a 2D texture";
            return nullptr;
        }
        D3D11_TEXTURE2D_DESC desc = {};
        texture2d->GetDesc(&desc);
        if (desc.MipLevels != 1 || desc.ArraySize != 1 || desc.SampleDesc.Count != 1)
        {
            problem = "a texture with mipmaps, an array or multisampling";
            return nullptr;
        }
        const DXGI_FORMAT format = ShareFormat(desc.Format);
        Shared& shared = textures[unityTexture];
        shared.lastUse = GetTickCount64();
        if (shared.texture.Valid() && shared.texture.width == desc.Width && shared.texture.height == desc.Height
            && shared.texture.format == format)
            return &shared;
        if (!shared.texture.Create(device11.Get(), device.Get(), desc.Width, desc.Height, format))
        {
            textures.erase(unityTexture);
            problem = "format " + std::to_string(static_cast<int>(desc.Format)) + " cannot be shared";
            return nullptr;
        }
        return &shared;
    }

    bool Raytracing::EnsureDeviceLocked()
    {
        if (list != nullptr)
            return true;

        HRESULT hr = SharedFence(device11.Get(), device.Get(), copied12, copied11);
        if (SUCCEEDED(hr))
            hr = SharedFence(device11.Get(), device.Get(), fence12, fence11);

        D3D12_HEAP_PROPERTIES upload = {};
        upload.Type = D3D12_HEAP_TYPE_UPLOAD;
        const D3D12_RESOURCE_DESC constantsDesc = BufferDesc(kConstantsSize, D3D12_RESOURCE_FLAG_NONE);
        for (UINT i = 0; SUCCEEDED(hr) && i < kSlots; ++i)
        {
            Slot& slot = slots[i];
            hr = device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&slot.allocator));
            if (SUCCEEDED(hr))
                hr = device->CreateCommittedResource(&upload, D3D12_HEAP_FLAG_NONE, &constantsDesc,
                                                     D3D12_RESOURCE_STATE_GENERIC_READ, nullptr,
                                                     IID_PPV_ARGS(&slot.constants));
            if (SUCCEEDED(hr))
            {
                const D3D12_RANGE none = { 0, 0 };
                void* mapped = nullptr;
                hr = slot.constants->Map(0, &none, &mapped);
                slot.constantsMapped = static_cast<uint8_t*>(mapped);
            }
            if (SUCCEEDED(hr) && !EnsureUploadBuffer(slot.materials, 64 * sizeof(MaterialData)))
                hr = E_OUTOFMEMORY;
            slot.materialCapacity = 64;
            if (SUCCEEDED(hr) && (!EnsureUploadBuffer(slot.instanceDescs, 256 * sizeof(D3D12_RAYTRACING_INSTANCE_DESC))
                                  || !EnsureUploadBuffer(slot.instanceData, 256 * sizeof(InstanceData))))
                hr = E_OUTOFMEMORY;
            slot.instanceCapacity = 256;
            slot.done = 0;
        }

        if (SUCCEEDED(hr))
        {
            D3D12_DESCRIPTOR_HEAP_DESC heapDesc = {};
            heapDesc.Type = D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV;
            heapDesc.NumDescriptors = kSlots * kFrameDescriptors + 2 * kMaxMeshes + 1 + kSlots * kDenoiseDescriptors;
            heapDesc.Flags = D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE;
            hr = device->CreateDescriptorHeap(&heapDesc, IID_PPV_ARGS(&heap));
            increment = device->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV);
        }
        if (SUCCEEDED(hr))
        {
            ComPtr<ID3D12GraphicsCommandList> created;
            hr = device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, slots[0].allocator.Get(), nullptr,
                                           IID_PPV_ARGS(&created));
            if (SUCCEEDED(hr))
                hr = created.As(&list);
            if (SUCCEEDED(hr))
                list->Close();
        }
        if (SUCCEEDED(hr))
        {
            D3D12_QUERY_HEAP_DESC queryDesc = {};
            queryDesc.Type = D3D12_QUERY_HEAP_TYPE_TIMESTAMP;
            queryDesc.Count = kSlots * kTimestampsPerSlot;
            hr = device->CreateQueryHeap(&queryDesc, IID_PPV_ARGS(&timestamps));
        }
        if (SUCCEEDED(hr))
        {
            D3D12_HEAP_PROPERTIES readback = {};
            readback.Type = D3D12_HEAP_TYPE_READBACK;
            const D3D12_RESOURCE_DESC readbackDesc =
                BufferDesc(static_cast<UINT64>(kSlots) * kTimestampsPerSlot * sizeof(UINT64), D3D12_RESOURCE_FLAG_NONE);
            hr = device->CreateCommittedResource(&readback, D3D12_HEAP_FLAG_NONE, &readbackDesc,
                                                 D3D12_RESOURCE_STATE_COPY_DEST, nullptr, IID_PPV_ARGS(&timestampReadback));
        }
        if (SUCCEEDED(hr))
            hr = queue->GetTimestampFrequency(&timestampFrequency);
        if (FAILED(hr) || !BuildPipelinesLocked())
        {
            if (FAILED(hr))
                SetProblem("the fences, lists or heaps could not be made (" + Hr(hr) + ")");
            ReleaseLocked();
            unavailable = lastProblem;
            return false;
        }

        freeSlots.clear();
        for (uint32_t i = kMaxMeshes; i > 0; --i)
            freeSlots.push_back(i - 1);
        retiredSlots.clear();
        if (fenceEvent == nullptr)
            fenceEvent = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        fenceValue = 0;
        copiedValue = 0;
        slotIndex = 0;
        return true;
    }

    bool Raytracing::BuildPipelinesLocked()
    {
        D3D12_DESCRIPTOR_RANGE ranges[7] = {};
        ranges[0].RangeType = D3D12_DESCRIPTOR_RANGE_TYPE_SRV;   // t1-t8
        ranges[0].NumDescriptors = kRtMaxTextures;
        ranges[0].BaseShaderRegister = 1;
        ranges[1].RangeType = D3D12_DESCRIPTOR_RANGE_TYPE_UAV;   // u0-u7
        ranges[1].NumDescriptors = kRtMaxTextures;
        ranges[1].BaseShaderRegister = 0;
        ranges[2].RangeType = D3D12_DESCRIPTOR_RANGE_TYPE_SRV;   // t0, space1 onwards: index buffers
        ranges[2].NumDescriptors = kMaxMeshes;
        ranges[2].BaseShaderRegister = 0;
        ranges[2].RegisterSpace = 1;
        ranges[3].RangeType = D3D12_DESCRIPTOR_RANGE_TYPE_SRV;   // t0, space2 onwards: attributes
        ranges[3].NumDescriptors = kMaxMeshes;
        ranges[3].BaseShaderRegister = 0;
        ranges[3].RegisterSpace = 2;
        ranges[4].RangeType = D3D12_DESCRIPTOR_RANGE_TYPE_SRV;   // t11: the material textures
        ranges[4].NumDescriptors = 1;
        ranges[4].BaseShaderRegister = 11;
        ranges[5].RangeType = D3D12_DESCRIPTOR_RANGE_TYPE_SRV;   // t0-t7, space3: the denoiser's reads
        ranges[5].NumDescriptors = 8;
        ranges[5].RegisterSpace = 3;
        ranges[6].RangeType = D3D12_DESCRIPTOR_RANGE_TYPE_UAV;   // u0-u3, space3: the denoiser's writes
        ranges[6].NumDescriptors = 4;
        ranges[6].RegisterSpace = 3;

        D3D12_ROOT_PARAMETER parameters[12] = {};
        parameters[0].ParameterType = D3D12_ROOT_PARAMETER_TYPE_CBV;
        parameters[0].Descriptor.ShaderRegister = 0;
        parameters[1].ParameterType = D3D12_ROOT_PARAMETER_TYPE_SRV;
        parameters[1].Descriptor.ShaderRegister = 0;
        for (UINT i = 0; i < 2; ++i)
        {
            parameters[2 + i].ParameterType = D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE;
            parameters[2 + i].DescriptorTable.NumDescriptorRanges = 1;
            parameters[2 + i].DescriptorTable.pDescriptorRanges = &ranges[i];
        }
        parameters[4].ParameterType = D3D12_ROOT_PARAMETER_TYPE_SRV;
        parameters[4].Descriptor.ShaderRegister = 9;
        parameters[5].ParameterType = D3D12_ROOT_PARAMETER_TYPE_SRV;
        parameters[5].Descriptor.ShaderRegister = 10;
        for (UINT i = 0; i < 2; ++i)
        {
            parameters[6 + i].ParameterType = D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE;
            parameters[6 + i].DescriptorTable.NumDescriptorRanges = 1;
            parameters[6 + i].DescriptorTable.pDescriptorRanges = &ranges[2 + i];
        }
        parameters[8].ParameterType = D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE;
        parameters[8].DescriptorTable.NumDescriptorRanges = 1;
        parameters[8].DescriptorTable.pDescriptorRanges = &ranges[4];
        parameters[9].ParameterType = D3D12_ROOT_PARAMETER_TYPE_32BIT_CONSTANTS;   // b1: the denoiser's step
        parameters[9].Constants.ShaderRegister = 1;
        parameters[9].Constants.Num32BitValues = 4;
        for (UINT i = 0; i < 2; ++i)
        {
            parameters[10 + i].ParameterType = D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE;
            parameters[10 + i].DescriptorTable.NumDescriptorRanges = 1;
            parameters[10 + i].DescriptorTable.pDescriptorRanges = &ranges[5 + i];
        }
        for (D3D12_ROOT_PARAMETER& parameter : parameters)
            parameter.ShaderVisibility = D3D12_SHADER_VISIBILITY_ALL;

        D3D12_STATIC_SAMPLER_DESC samplers[3] = {};
        for (UINT i = 0; i < 3; ++i)
        {
            samplers[i].Filter = i == 0 ? D3D12_FILTER_MIN_MAG_MIP_POINT : D3D12_FILTER_MIN_MAG_MIP_LINEAR;
            samplers[i].AddressU = samplers[i].AddressV = samplers[i].AddressW =
                i == 2 ? D3D12_TEXTURE_ADDRESS_MODE_WRAP : D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
            samplers[i].MaxLOD = D3D12_FLOAT32_MAX;
            samplers[i].ShaderRegister = i;
            samplers[i].ShaderVisibility = D3D12_SHADER_VISIBILITY_ALL;
        }

        D3D12_ROOT_SIGNATURE_DESC desc = {};
        desc.NumParameters = 12;
        desc.pParameters = parameters;
        desc.NumStaticSamplers = 3;
        desc.pStaticSamplers = samplers;
        ComPtr<ID3DBlob> serialized;
        ComPtr<ID3DBlob> errors;
        HRESULT hr = D3D12SerializeRootSignature(&desc, D3D_ROOT_SIGNATURE_VERSION_1, &serialized, &errors);
        if (SUCCEEDED(hr))
            hr = device->CreateRootSignature(0, serialized->GetBufferPointer(), serialized->GetBufferSize(),
                                             IID_PPV_ARGS(&rootSignature));
        if (FAILED(hr))
        {
            std::string text = "the raytracing root signature could not be made (" + Hr(hr) + ")";
            if (errors != nullptr)
                text += ": " + std::string(static_cast<const char*>(errors->GetBufferPointer()), errors->GetBufferSize());
            SetProblem(text);
            return false;
        }

        for (const PassEntry& pass : kPasses)
        {
            char name[64] = {};
            WideCharToMultiByte(CP_UTF8, 0, pass.resource, -1, name, sizeof(name), nullptr, nullptr);
            const auto code = shaders.find(name);
            if (code == shaders.end())
                continue;
            D3D12_COMPUTE_PIPELINE_STATE_DESC pipelineDesc = {};
            pipelineDesc.pRootSignature = rootSignature.Get();
            pipelineDesc.CS.pShaderBytecode = code->second.data();
            pipelineDesc.CS.BytecodeLength = code->second.size();
            ComPtr<ID3D12PipelineState> pipeline;
            hr = device->CreateComputePipelineState(&pipelineDesc, IID_PPV_ARGS(&pipeline));
            if (FAILED(hr))
            {
                SetProblem(std::string("the raytracing pass ") + name + " could not be built (" + Hr(hr) + ")");
                return false;
            }
            pipelines[pass.signal] = pipeline;
        }
        for (const wchar_t* entry : kDenoiseEntries)
        {
            char name[64] = {};
            WideCharToMultiByte(CP_UTF8, 0, entry, -1, name, sizeof(name), nullptr, nullptr);
            const auto code = shaders.find(name);
            if (code == shaders.end())
                continue;
            D3D12_COMPUTE_PIPELINE_STATE_DESC pipelineDesc = {};
            pipelineDesc.pRootSignature = rootSignature.Get();
            pipelineDesc.CS.pShaderBytecode = code->second.data();
            pipelineDesc.CS.BytecodeLength = code->second.size();
            ComPtr<ID3D12PipelineState> pipeline;
            hr = device->CreateComputePipelineState(&pipelineDesc, IID_PPV_ARGS(&pipeline));
            if (FAILED(hr))
            {
                SetProblem(std::string("the denoiser's pass ") + name + " could not be built (" + Hr(hr) + ")");
                return false;
            }
            denoisePipelines[name] = pipeline;
        }
        return true;
    }

    void Raytracing::WaitForSlotLocked(Slot& slot)
    {
        if (slot.done == 0 || fence12 == nullptr || fenceEvent == nullptr)
            return;
        if (fence12->GetCompletedValue() < slot.done && SUCCEEDED(fence12->SetEventOnCompletion(slot.done, fenceEvent)))
            WaitForSingleObject(fenceEvent, INFINITE);

        // The slot's list is done: its timestamps are there to read.
        const UINT slotNumber = static_cast<UINT>(&slot - slots);
        const D3D12_RANGE range = { slotNumber * kTimestampsPerSlot * sizeof(UINT64),
                                    (slotNumber + 1) * kTimestampsPerSlot * sizeof(UINT64) };
        void* mapped = nullptr;
        if (timestampFrequency != 0 && SUCCEEDED(timestampReadback->Map(0, &range, &mapped)))
        {
            const UINT64* ticks = reinterpret_cast<const UINT64*>(static_cast<const uint8_t*>(mapped) + range.Begin);
            const double toMs = 1000.0 / static_cast<double>(timestampFrequency);
            std::lock_guard<std::mutex> status(statusLock);
            buildMs = static_cast<float>(static_cast<double>(ticks[1] - ticks[0]) * toMs);
            traceMs = static_cast<float>(static_cast<double>(ticks[kTimestampsPerSlot - 1] - ticks[1]) * toMs);
            const UINT order[kRtPassTimings] = { 0, 1, 2, 3, 6, 4, 5, 7 };
            for (UINT position = 0; position < kRtPassTimings; ++position)
                passMs[order[position]] =
                    static_cast<float>(static_cast<double>(ticks[2 + position] - ticks[1 + position]) * toMs);
            const D3D12_RANGE none = { 0, 0 };
            timestampReadback->Unmap(0, &none);
        }
    }

    void Raytracing::WaitForAllLocked()
    {
        if (fence12 == nullptr || fenceEvent == nullptr || fenceValue == 0)
            return;
        if (fence12->GetCompletedValue() < fenceValue && SUCCEEDED(fence12->SetEventOnCompletion(fenceValue, fenceEvent)))
            WaitForSingleObject(fenceEvent, INFINITE);
    }

    // The device is going or changing: everything made on it goes. The meshes the
    // managed side handed over go too; it hands them over again when it sees the
    // service ready on a new device (Status).
    void Raytracing::ReleaseLocked()
    {
        WaitForAllLocked();
        ReleaseRegenerationLocked();
        meshes.clear();
        meshSlotsFull = false;
        textures.clear();
        pipelines.clear();
        denoisePipelines.clear();
        denoise = DenoiseTargets{};
        guides = GuideTargets{};
        rootSignature.Reset();
        list.Reset();
        for (Slot& slot : slots)
        {
            if (slot.constants != nullptr && slot.constantsMapped != nullptr)
                slot.constants->Unmap(0, nullptr);
            slot = Slot{};
        }
        heap.Reset();
        materialTextures.Reset();
        materialTextureLayers = 0;
        tlas.Reset();
        tlasScratch.Reset();
        tlasSize = tlasScratchSize = 0;
        timestamps.Reset();
        timestampReadback.Reset();
        freeSlots.clear();
        retiredSlots.clear();
        fence11.Reset();
        fence12.Reset();
        copied11.Reset();
        copied12.Reset();
        fenceValue = 0;
        copiedValue = 0;
        ++generation;
    }

    int Raytracing::Status(char* buffer, int size) const
    {
        // A device the driver has taken away runs nothing more; its reason goes first.
        HRESULT removed = S_OK;
        {
            std::lock_guard<std::mutex> lock(mutex);
            if (device != nullptr)
            {
                removed = device->GetDeviceRemovedReason();
                // The debug layer's messages, where it is on, into the log once.
                ComPtr<ID3D12InfoQueue> messages;
                if (SUCCEEDED(device.As(&messages)))
                {
                    const UINT64 count = messages->GetNumStoredMessages();
                    for (UINT64 i = 0; i < count && i < 20; ++i)
                    {
                        SIZE_T length = 0;
                        messages->GetMessage(i, nullptr, &length);
                        std::vector<uint8_t> bytes(length);
                        D3D12_MESSAGE* message = reinterpret_cast<D3D12_MESSAGE*>(bytes.data());
                        if (SUCCEEDED(messages->GetMessage(i, message, &length)))
                            LogLine(std::string("Raytracing, Direct3D 12 debug layer: ") + message->pDescription);
                    }
                    messages->ClearStoredMessages();
                }
            }
        }
        std::lock_guard<std::mutex> status(statusLock);
        if (removed != S_OK && buffer != nullptr && size > 0)
        {
            _snprintf_s(buffer, static_cast<size_t>(size), _TRUNCATE, "device removed (%s) ", Hr(removed).c_str());
            const size_t used = strnlen(buffer, static_cast<size_t>(size));
            buffer += used;
            size -= static_cast<int>(used);
        }
        if (buffer != nullptr && size > 0)
            _snprintf_s(buffer, static_cast<size_t>(size), _TRUNCATE,
                        "generation=%u frames=%llu reconstructed=%llu regenerated=%llu meshes=%u built=%u instances=%u"
                        " build=%.3fms trace=%.3fms%s%s",
                        generation.load(), static_cast<unsigned long long>(frames),
                        static_cast<unsigned long long>(reconstructed), static_cast<unsigned long long>(regenerated), liveMeshes,
                        lastBuilt, lastInstances,
                        buildMs, traceMs, lastProblem.empty() ? "" : " last problem: ", lastProblem.c_str());
        return static_cast<int>(generation.load());
    }

    void Raytracing::Timings(float& build, float& trace) const
    {
        std::lock_guard<std::mutex> status(statusLock);
        build = buildMs;
        trace = traceMs;
    }

    void Raytracing::PassTimings(float* out, int count) const
    {
        std::lock_guard<std::mutex> status(statusLock);
        for (int i = 0; i < count && i < static_cast<int>(kRtPassTimings); ++i)
            out[i] = passMs[i];
    }

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

        D3D12_SHADER_RESOURCE_VIEW_DESC srv = {};
        srv.Format = DXGI_FORMAT_R8G8B8A8_UNORM_SRGB;
        srv.ViewDimension = D3D12_SRV_DIMENSION_TEXTURE2DARRAY;
        srv.Shader4ComponentMapping = D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING;
        srv.Texture2DArray.MipLevels = 1;
        srv.Texture2DArray.ArraySize = grown;
        D3D12_CPU_DESCRIPTOR_HANDLE view = heap->GetCPUDescriptorHandleForHeapStart();
        view.ptr += static_cast<SIZE_T>(kSlots * kFrameDescriptors + 2 * kMaxMeshes) * increment;
        device->CreateShaderResourceView(materialTextures.Get(), &srv, view);
        return true;
    }

    void Raytracing::SetProblem(const std::string& text)
    {
        bool changed = false;
        {
            std::lock_guard<std::mutex> status(statusLock);
            changed = lastProblem != text;
            lastProblem = text;
        }
        if (changed)
            LogLine("Raytracing: " + text);
    }
}
