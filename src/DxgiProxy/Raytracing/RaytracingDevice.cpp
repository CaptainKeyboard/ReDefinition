// The raytracing service's Direct3D 12 objects: its shaders and pipelines, the fences,
// lists and heaps of its slots, the shared copies of Unity's textures, buffers, and the
// submission of a slot's list.

#include "WaitStats.h"
#include "Raytracing.h"

#include "D3d12Util.h"
#include "DlssRayReconstruction.h"
#include "FileUtil.h"
#include "Log.h"
#include "RaytracingInternal.h"

#include <algorithm>

using Microsoft::WRL::ComPtr;

namespace redefinition
{
    namespace
    {
        // The proxy's own DLL, whose resources hold the shaders.
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
        // One shader by its resource name, kept under the name as text; false where the DLL
        // has none, with every shader let go of.
        const auto load = [&](const wchar_t* resource)
        {
            const HRSRC found = FindResourceW(module, resource, MAKEINTRESOURCEW(10));   // RT_RCDATA
            const HGLOBAL loaded = found != nullptr ? LoadResource(module, found) : nullptr;
            const void* bytes = loaded != nullptr ? LockResource(loaded) : nullptr;
            const DWORD length = found != nullptr ? SizeofResource(module, found) : 0;
            if (bytes == nullptr || length == 0)
            {
                shaders.clear();
                return false;
            }
            shaders[Narrow(resource)].assign(static_cast<const uint8_t*>(bytes), static_cast<const uint8_t*>(bytes) + length);
            return true;
        };
        for (const RtPassEntry& pass : kRtPasses)
            if (!load(pass.resource))
                return false;
        for (const wchar_t* entry : kRtDenoiseEntries)
            if (!load(entry))
                return false;
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

        // 12-16: the radiance cache's buffers, u0-u4 in space4 (RadianceCache.hlsli), bound
        // only where it runs (BindRadianceCacheLocked).
        D3D12_ROOT_PARAMETER parameters[17] = {};
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
        for (UINT i = 0; i < 5; ++i)
        {
            parameters[12 + i].ParameterType = D3D12_ROOT_PARAMETER_TYPE_UAV;
            parameters[12 + i].Descriptor.ShaderRegister = i;
            parameters[12 + i].Descriptor.RegisterSpace = 4;
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
        desc.NumParameters = 17;
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

        // Opacity micromaps (RaytracingOmm.cpp): "NvAPI_D3D12_SetCreatePipelineStateOptions ...
        // affects all pipelines created after this call, and until this function is called
        // again", so the passes' pipelines are made with the support where it is wanted, and the
        // option is put back as these are done.
        const auto setOmmOptions = [&](uint32_t flags)
        {
            const nvomm::PipelineOptionsParams options = { nvomm::Version(sizeof(nvomm::PipelineOptionsParams), 1), flags };
            return reinterpret_cast<nvomm::SetCreatePipelineStateOptionsFn>(omm.setOptions)(device.Get(), &options);
        };
        // Puts the option back as this function returns, where it was set: by a lambda that
        // lives as long as the guard does.
        struct OptionsReset
        {
            const decltype(setOmmOptions)* set = nullptr;
            ~OptionsReset()
            {
                if (set != nullptr)
                    (*set)(0);
            }
        } optionsReset;
        omm.pipelines = false;
        if (omm.wanted && omm.caps == 1 && omm.setOptions != nullptr)
        {
            const int status = setOmmOptions(nvomm::kPipelineEnableOmm);
            if (status == 0)
            {
                omm.pipelines = true;
                optionsReset.set = &setOmmOptions;
            }
            else
            {
                omm.wanted = false;
                omm.caps = 0;
                omm.capsWhy = "NvAPI_D3D12_SetCreatePipelineStateOptions returned " + std::to_string(status);
            }
        }

        // One pass's pipeline from the shader of its resource name, made with the option set above;
        // null where there is no such shader, and failed set where the driver refused it.
        bool failed = false;
        const auto make = [&](const wchar_t* resource, const char* kind) -> ComPtr<ID3D12PipelineState>
        {
            const std::string name = Narrow(resource);
            const auto code = shaders.find(name);
            if (code == shaders.end())
                return nullptr;
            D3D12_COMPUTE_PIPELINE_STATE_DESC pipelineDesc = {};
            pipelineDesc.pRootSignature = rootSignature.Get();
            pipelineDesc.CS.pShaderBytecode = code->second.data();
            pipelineDesc.CS.BytecodeLength = code->second.size();
            ComPtr<ID3D12PipelineState> pipeline;
            const HRESULT made = device->CreateComputePipelineState(&pipelineDesc, IID_PPV_ARGS(&pipeline));
            if (FAILED(made))
            {
                SetProblem(std::string(kind) + name + " could not be built (" + Hr(made) + ")");
                failed = true;
                return nullptr;
            }
            return pipeline;
        };
        for (const RtPassEntry& pass : kRtPasses)
        {
            ComPtr<ID3D12PipelineState> pipeline = make(pass.resource, "the raytracing pass ");
            if (failed)
                return false;
            if (pipeline != nullptr)
                pipelines[pass.signal] = pipeline;
        }
        for (size_t entryIndex = 0; entryIndex < kRtAuxCount; ++entryIndex)
        {
            ComPtr<ID3D12PipelineState> pipeline = make(kRtDenoiseEntries[entryIndex], "the denoiser's pass ");
            if (failed)
                return false;
            if (pipeline != nullptr)
                denoisePipelines[entryIndex] = pipeline;
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

        // The slot's list is done: its timestamps are there to read where it resolved them,
        // and the radiance cache's counts where it copied them.
        ReadRadianceCacheStatsLocked(static_cast<UINT>(&slot - slots));
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
            if (slot.reconstruction)
            {
                // Ray Reconstruction's list: its guide pass and its evaluation, said every
                // 120 frames.
                slot.reconstruction = false;
                reconstructGuidesMs += static_cast<double>(ticks[kTimestampRrGuides] - ticks[kTimestampRrStart]) * toMs;
                reconstructEvaluateMs += static_cast<double>(ticks[kTimestampRrEnd] - ticks[kTimestampRrGuides]) * toMs;
                if (++reconstructTimed >= 120)
                {
                    LogLine("Raytracing: Ray Reconstruction on the GPU, over " + std::to_string(reconstructTimed) + " frames: guides "
                            + std::to_string(reconstructGuidesMs / reconstructTimed) + " ms, evaluation "
                            + std::to_string(reconstructEvaluateMs / reconstructTimed) + " ms ("
                            + (reconstructionThroughNgx ? "NGX" : "Streamline") + ")");
                    reconstructGuidesMs = reconstructEvaluateMs = 0.0;
                    reconstructTimed = 0;
                }
                const D3D12_RANGE untouched = { 0, 0 };
                timestampReadback->Unmap(0, &untouched);
                return;
            }
            if (slot.extra)
            {
                extraBuildMs = static_cast<float>(static_cast<double>(ticks[1] - ticks[0]) * toMs);
                extraTraceMs = static_cast<float>(static_cast<double>(ticks[kTimestampTraceEnd] - ticks[1]) * toMs);
                const D3D12_RANGE untouched = { 0, 0 };
                timestampReadback->Unmap(0, &untouched);
                return;
            }
            buildMs = static_cast<float>(static_cast<double>(ticks[1] - ticks[0]) * toMs);
            NoteMeshBuildTimeLocked(slotNumber, static_cast<double>(ticks[kTimestampMeshesBuilt] - ticks[0]) * toMs);
            traceMs = static_cast<float>(static_cast<double>(ticks[kTimestampTraceEnd] - ticks[1]) * toMs);
            if (slot.guided)
            {
                frameGuidesMs += static_cast<double>(ticks[kTimestampGuidesEnd] - ticks[kTimestampTraceEnd]) * toMs;
                if (++frameGuidesTimed >= 120)
                {
                    LogLine("Raytracing: the frame's guides for Ray Reconstruction on the GPU, over " + std::to_string(frameGuidesTimed)
                            + " frames: " + std::to_string(frameGuidesMs / frameGuidesTimed) + " ms");
                    frameGuidesMs = 0.0;
                    frameGuidesTimed = 0;
                }
            }
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
                line += " ms (passes in the order they ran: sun, occlusion, diffuse, reflections, denoiser, 4,"
                        " the radiance cache's resolve, 7);"
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
        for (BlasMemory::PerSlot& perSlot : blasMemory.slots)
        {
            perSlot.upload.Reset();
            perSlot.scratch.Reset();
        }
        denoise = DenoiseTargets{};
        guides = GuideTargets{};
        ReleaseRadianceCacheLocked(nullptr);
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
            FreeMeshNowLocked(found->second);
            meshes.erase(found);
            OmmForgetMeshLocked(id);
            ++released;
        }
        pendingReleases.clear();
        LogLine("Raytracing: no longer used; its textures and " + std::to_string(released) + " released meshes freed, "
                + std::to_string(meshes.size()) + " meshes kept");
    }

    // The device is going or changing: everything made on it goes. The meshes the
    // managed side handed over go too; it hands them over again when it sees the
    // service ready on a new device (Status). What it handed over and no frame took
    // yet goes as well. The managed side forgets it with the generation (RtScene.Update)
    // and gives meshes and materials new ids. So a mesh still pending would be built on
    // the new device under an id nobody releases. A material would be kept under one
    // nobody uses, and a layer's texture copied over the layer handed over anew.
    void Raytracing::ReleaseLocked()
    {
        WaitForAllLocked();
        // Ray Reconstruction through NGX on the device that goes, past every list that
        // evaluated it.
        if (device != nullptr)
            DlssRayReconstruction::Get().Shutdown(device.Get());
        ReleaseRegenerationLocked();
        ReleaseRadianceCacheLocked(nullptr);
        pendingMeshes.clear();
        pendingReleases.clear();
        materialData.clear();
        ++materialVersion;
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
        blasMemory.Reset();
        omm.Reset();
        {
            std::lock_guard<std::mutex> status(statusLock);
            ommMeshes = 0;
            ommBytes = 0;
        }
        meshSlotsFull = false;
        textures.clear();
        pipelines.clear();
        ResetAuxPipelines();
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
