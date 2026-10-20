// The raytracing service's Direct3D 12 objects: its shaders and pipelines, the fences,
// lists and heaps of its slots, the shared copies of Unity's textures, buffers, and the
// submission of a slot's list.

#include "WaitStats.h"
#include "Raytracing.h"

#include "D3d12Util.h"
#include "DlssRayReconstruction.h"
#include "Log.h"
#include "RaytracingInternal.h"

#include <algorithm>

using Microsoft::WRL::ComPtr;

namespace redefinition
{
    namespace
    {
        HMODULE OwnModule()
        {
            HMODULE module = nullptr;
            GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                               reinterpret_cast<LPCWSTR>(&OwnModule), &module);
            return module;
        }
    }

    bool Raytracing::LoadShadersLocked()
    {
        if (!shaders.empty())
            return true;
        const HMODULE module = OwnModule();
        for (const RtPassEntry& pass : kRtPasses)
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
        for (const wchar_t* entry : kRtDenoiseEntries)
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

    bool Raytracing::EnsureDeviceLocked()
    {
        if (list != nullptr)
            return true;

        HRESULT hr = SharedFence(device11.Get(), device.Get(), copied12, copied11);
        if (SUCCEEDED(hr))
            hr = SharedFence(device11.Get(), device.Get(), fence12, fence11);

        D3D12_HEAP_PROPERTIES upload = {};
        upload.Type = D3D12_HEAP_TYPE_UPLOAD;
        const D3D12_RESOURCE_DESC constantsDesc = RtBufferDesc(kRtConstantsSize, D3D12_RESOURCE_FLAG_NONE);
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
            heapDesc.NumDescriptors = kHeapDescriptors;
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
                RtBufferDesc(static_cast<UINT64>(kSlots) * kTimestampsPerSlot * sizeof(UINT64), D3D12_RESOURCE_FLAG_NONE);
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
        builtSceneSlot = nullptr;
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

        for (const RtPassEntry& pass : kRtPasses)
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
        for (const wchar_t* entry : kRtDenoiseEntries)
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

    bool Raytracing::EnsureUploadBuffer(ComPtr<ID3D12Resource>& buffer, UINT64 size)
    {
        D3D12_HEAP_PROPERTIES upload = {};
        upload.Type = D3D12_HEAP_TYPE_UPLOAD;
        const D3D12_RESOURCE_DESC desc = RtBufferDesc(size, D3D12_RESOURCE_FLAG_NONE);
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
        const D3D12_RESOURCE_DESC desc = RtBufferDesc(size, flags);
        buffer.Reset();
        return SUCCEEDED(device->CreateCommittedResource(&heapProperties, D3D12_HEAP_FLAG_NONE, &desc, state, nullptr,
                                                         IID_PPV_ARGS(&buffer)));
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

    void Raytracing::WaitForSlotLocked(Slot& slot)
    {
        if (slot.done == 0 || fence12 == nullptr || fenceEvent == nullptr)
            return;
        // The slot's own list, and a later one that read its scene.
        const UINT64 until = std::max(slot.done, slot.sceneReadUntil);
        if (fence12->GetCompletedValue() < until && SUCCEEDED(fence12->SetEventOnCompletion(until, fenceEvent)))
        {
            WaitTimer timer(WaitKind::RaytracingSlot);
            WaitForSingleObject(fenceEvent, INFINITE);
        }

        // The slot's list is done: its timestamps are there to read where it resolved them.
        const bool timed = slot.timed;
        slot.timed = false;
        if (!timed)
            return;
        const UINT slotNumber = static_cast<UINT>(&slot - slots);
        const D3D12_RANGE range = { slotNumber * kTimestampsPerSlot * sizeof(UINT64),
                                    (slotNumber + 1) * kTimestampsPerSlot * sizeof(UINT64) };
        void* mapped = nullptr;
        if (timestampFrequency != 0 && SUCCEEDED(timestampReadback->Map(0, &range, &mapped)))
        {
            const UINT64* ticks = reinterpret_cast<const UINT64*>(static_cast<const uint8_t*>(mapped) + range.Begin);
            const double toMs = 1000.0 / static_cast<double>(timestampFrequency);
            std::lock_guard<std::mutex> status(statusLock);
            if (slot.extra)
            {
                extraBuildMs = static_cast<float>(static_cast<double>(ticks[1] - ticks[0]) * toMs);
                extraTraceMs = static_cast<float>(static_cast<double>(ticks[kTimestampTraceEnd] - ticks[1]) * toMs);
                const D3D12_RANGE untouched = { 0, 0 };
                timestampReadback->Unmap(0, &untouched);
                return;
            }
            buildMs = static_cast<float>(static_cast<double>(ticks[1] - ticks[0]) * toMs);
            traceMs = static_cast<float>(static_cast<double>(ticks[kTimestampTraceEnd] - ticks[1]) * toMs);
            for (UINT position = 0; position < kRtPassTimings; ++position)
                passMs[kRtPassOrder[position]] =
                    static_cast<float>(static_cast<double>(ticks[2 + position] - ticks[1 + position]) * toMs);
            // A slow frame says where its time went, a few times a session: the scene's
            // build, then each pass in the order they ran.
            if (buildMs + traceMs > 15.0f && slowFramesLogged < 30)
            {
                ++slowFramesLogged;
                std::string line = "Raytracing: a slow frame, build " + std::to_string(buildMs) + " ms (meshes "
                                   + std::to_string(static_cast<double>(ticks[kTimestampMeshesBuilt] - ticks[0]) * toMs)
                                   + ", material textures "
                                   + std::to_string(static_cast<double>(ticks[kTimestampMaterialsUploaded] - ticks[kTimestampMeshesBuilt]) * toMs)
                                   + ", the scene " + std::to_string(static_cast<double>(ticks[1] - ticks[kTimestampMaterialsUploaded]) * toMs)
                                   + "), then";
                for (UINT position = 0; position < kRtPassTimings; ++position)
                    line += " " + std::to_string(static_cast<double>(ticks[2 + position] - ticks[1 + position]) * toMs);
                line += " ms (passes in the order they ran: sun, occlusion, diffuse, reflections, denoiser, 4, 5, 7);"
                        " the latest frame built " + std::to_string(lastBuilt) + " meshes, "
                        + std::to_string(lastInstances) + " instances";
                LogLine(line);
            }
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

    void Raytracing::OnRelease()
    {
        // Raytracing turned off: frames stop coming, and with them the steps that let
        // things go (EvictTexturesLocked, BuildMeshesLocked's releases, the denoiser's
        // frames without it). What the next frame makes again goes now, once the queue
        // is past every list that used it: the denoiser's textures, Ray Reconstruction's
        // guides, AMD's context and its guides, the shared copies of Unity's textures,
        // and the meshes the mod released. The device, its lists and the meshes still
        // held stay.
        std::lock_guard<std::mutex> lock(mutex);
        TakeHandOverLocked();
        if (list == nullptr)
            return;
        WaitForAllLocked();
        for (Slot& slot : slots)
            slot.held.clear();
        denoise = DenoiseTargets{};
        guides = GuideTargets{};
        // Where AMD's denoiser did not run, it is not tried again until the device goes.
        const bool unsupported = regeneration.unsupported;
        ReleaseRegenerationLocked();
        regeneration.unsupported = unsupported;
        textures.clear();
        size_t released = 0;
        for (int32_t id : pendingReleases)
        {
            auto found = meshes.find(id);
            if (found == meshes.end())
                continue;
            freeSlots.push_back(found->second.slot);
            meshes.erase(found);
            ++released;
        }
        pendingReleases.clear();
        LogLine("Raytracing: no longer used; its textures and " + std::to_string(released) + " released meshes freed, "
                + std::to_string(meshes.size()) + " meshes kept");
    }

    // The device is going or changing: everything made on it goes. The meshes the
    // managed side handed over go too; it hands them over again when it sees the
    // service ready on a new device (Status). What it handed over and no frame took
    // yet goes as well: the managed side forgets it with the generation (RtScene.Update)
    // and gives meshes and materials new ids, so a mesh still pending would be built on
    // the new device under an id nobody releases, a material kept under one nobody
    // uses, and a layer's texture copied over the layer handed over anew.
    void Raytracing::ReleaseLocked()
    {
        WaitForAllLocked();
        // Ray Reconstruction through NGX on the device that goes, past every list that
        // evaluated it.
        if (device != nullptr)
            DlssRayReconstruction::Get().Shutdown(device.Get());
        ReleaseRegenerationLocked();
        pendingMeshes.clear();
        pendingReleases.clear();
        materialData.clear();
        pendingTextures.clear();
        {
            // The same of what the main thread handed over since; its scenes stay, as the
            // service's own do.
            std::lock_guard<std::mutex> handOverHeld(handOverLock);
            handOver.meshes.clear();
            handOver.releases.clear();
            handOver.materials.clear();
            handOver.textures.clear();
            // The new generation under the same lock: what the main thread hands over
            // from here on is for it, or dropped (HandOverCurrentLocked), and nothing
            // for the old one slips in between the clearing and the count.
            ++generation;
        }
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
        builtSceneSlot = nullptr;
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

    }
}
