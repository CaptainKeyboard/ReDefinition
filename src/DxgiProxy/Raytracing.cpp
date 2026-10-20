// The raytracing service: its device attached, what the managed side hands over from the
// main thread, and a frame on the render thread, step by step (OnFrame).

#include "GpuProfiler.h"
#include "Raytracing.h"

#include "Config.h"
#include "D3d12Util.h"
#include "Log.h"
#include "RaytracingInternal.h"

#include <algorithm>
#include <cstring>

using Microsoft::WRL::ComPtr;

namespace redefinition
{
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
        // Read with the swapchain's own choice of Streamline (SwapChainProxy.cpp).
        rrPath = RayReconstructionPathAsked();
        {
            std::lock_guard<std::mutex> status(statusLock);
            statusDevice.Reset();
            debugMessages.Reset();
        }
        tier = 0;
        unavailable.clear();

        if (newDevice12 == nullptr || FAILED(newDevice12->QueryInterface(IID_PPV_ARGS(&device))))
            unavailable = "the Direct3D 12 device has no raytracing interface (ID3D12Device5)";
        else
        {
            {
                std::lock_guard<std::mutex> status(statusLock);
                statusDevice = device;
                device.As(&debugMessages);
            }
            D3D12_FEATURE_DATA_D3D12_OPTIONS5 options5 = {};
            if (SUCCEEDED(device->CheckFeatureSupport(D3D12_FEATURE_D3D12_OPTIONS5, &options5, sizeof(options5))))
                tier = static_cast<int>(options5.RaytracingTier);
            if (tier < D3D12_RAYTRACING_TIER_1_1)
                unavailable = "the GPU or its driver offers raytracing tier " + std::to_string(tier / 10) + "."
                              + std::to_string(tier % 10) + "; ray queries need 1.1";
            else if (!LoadShadersLocked())
                unavailable = "this build of the proxy has no raytracing shaders";
        }

        // A queue of its own (Config.h, rtOwnQueue): the fences below order it against
        // Direct3D 11 as they order the swapchain's.
        if (unavailable.empty() && device != nullptr && Config().rtOwnQueue)
        {
            D3D12_COMMAND_QUEUE_DESC desc = {};
            desc.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
            desc.Priority = D3D12_COMMAND_QUEUE_PRIORITY_HIGH;
            ComPtr<ID3D12CommandQueue> own;
            const HRESULT hr = device->CreateCommandQueue(&desc, IID_PPV_ARGS(&own));
            if (SUCCEEDED(hr))
            {
                own->SetName(L"ReDefinition raytracing");
                queue = own;
            }
            LogLine(SUCCEEDED(hr) ? "Raytracing: on a queue of its own"
                                  : "Raytracing: no queue of its own (" + Hr(hr) + "), on the swapchain's");
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
        {
            std::lock_guard<std::mutex> status(statusLock);
            statusDevice.Reset();
            debugMessages.Reset();
        }
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
        uint64_t hash = 14695981039346656037ull;
        for (uint32_t& index : pending.indices)
        {
            if (index >= desc->vertexCount)
            {
                SetProblem("mesh " + std::to_string(desc->mesh) + " rejected: an index past its vertices");
                return 0;
            }
            hash = (hash ^ index) * 1099511628211ull;
        }
        pending.indexHash = hash;

        std::lock_guard<std::mutex> lock(handOverLock);
        if (!HandOverCurrentLocked())
            return 0;
        handOver.releases.erase(std::remove(handOver.releases.begin(), handOver.releases.end(), desc->mesh),
                                handOver.releases.end());
        handOver.meshes[desc->mesh] = std::move(pending);
        return 1;
    }

    void Raytracing::ReleaseMesh(int32_t mesh)
    {
        std::lock_guard<std::mutex> lock(handOverLock);
        if (!HandOverCurrentLocked())
            return;
        handOver.meshes.erase(mesh);
        handOver.releases.push_back(mesh);
    }

    int Raytracing::SetMaterial(const RtMaterialDesc* desc)
    {
        if (desc == nullptr || desc->size != sizeof(RtMaterialDesc) || desc->magic != kRtMaterialMagic)
        {
            SetProblem("a material rejected: the mod and the proxy disagree on its layout");
            return 0;
        }
        if (desc->vertexColour <= 0.0f && (desc->texture < -1 || desc->texture >= static_cast<int32_t>(kRtMaxMaterialTextures)))
        {
            SetProblem("material " + std::to_string(desc->material) + " rejected: texture "
                       + std::to_string(desc->texture) + " past the material textures");
            return 0;
        }
        MaterialData data = {};
        memcpy(data.albedo, desc->albedo, sizeof(data.albedo));
        memcpy(data.specular, desc->specular, sizeof(data.specular));
        data.smoothness = desc->smoothness;
        memcpy(data.emission, desc->emission, sizeof(data.emission));
        data.texture = desc->vertexColour > 0.0f ? -1 : desc->texture;
        data.vertexColour[0] = desc->vertexColour;
        data.vertexColour[1] = static_cast<float>(desc->flags);
        data.vertexColour[2] = desc->glossDivisor;
        data.vertexColour[3] = desc->glossMean;
        data.look[0] = desc->diffuseShare > 0.0f ? desc->diffuseShare : 1.0f;
        memcpy(data.highlight, desc->highlight, sizeof(data.highlight));
        std::lock_guard<std::mutex> lock(handOverLock);
        if (!HandOverCurrentLocked())
            return 0;
        handOver.materials[desc->material] = { true, data };
        return 1;
    }

    void Raytracing::ReleaseMaterial(int32_t material)
    {
        std::lock_guard<std::mutex> lock(handOverLock);
        if (!HandOverCurrentLocked())
            return;
        handOver.materials[material] = { false, MaterialData{} };
    }

    namespace
    {
        // The scene for a frame among the last few, newest last, made where there is none:
        // the service's own (frameScenes) and the hand-over's alike.
        template <typename Scene>
        Scene& SceneFor(std::deque<Scene>& scenes, uint32_t frameIndex)
        {
            // A scene for every frame (kRtAnyFrame) takes the place of the frames' own.
            if (frameIndex == kRtAnyFrame && (scenes.empty() || scenes.back().frameIndex != kRtAnyFrame))
            {
                scenes.clear();
                scenes.emplace_back();
                return scenes.back();
            }
            for (Scene& scene : scenes)
                if (scene.frameIndex == frameIndex)
                    return scene;
            // The main thread runs a frame or two ahead of the render thread.
            while (scenes.size() >= 8)
                scenes.pop_front();
            scenes.emplace_back();
            scenes.back().frameIndex = frameIndex;
            if (scenes.size() > 1)
            {
                scenes.back().groups = scenes[scenes.size() - 2].groups;
                scenes.back().groupsSet = scenes[scenes.size() - 2].groupsSet;
            }
            return scenes.back();
        }
    }

    int Raytracing::SetInstances(const RtInstance* given, int count, uint32_t frameIndex)
    {
        if (count < 0 || (count > 0 && given == nullptr))
            return 0;
        std::vector<RtInstance> instances(given, given + count);
        std::lock_guard<std::mutex> lock(handOverLock);
        if (!HandOverCurrentLocked())
            return 0;
        FrameScene& scene = SceneFor(handOver.scenes, frameIndex);
        scene.instances = std::move(instances);
        scene.instancesSet = true;
        return 1;
    }

    int Raytracing::SetGpuGroups(const RtGpuInstanceGroup* given, int count, uint32_t frameIndex)
    {
        if (count < 0 || (count > 0 && given == nullptr))
            return 0;
        std::vector<RtGpuInstanceGroup> groups(given, given + count);
        std::lock_guard<std::mutex> lock(handOverLock);
        if (!HandOverCurrentLocked())
            return 0;
        FrameScene& scene = SceneFor(handOver.scenes, frameIndex);
        scene.groups = std::move(groups);
        scene.groupsSet = true;
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
        std::vector<uint8_t> copy(pixels, pixels + bytes);
        std::lock_guard<std::mutex> lock(handOverLock);
        if (!HandOverCurrentLocked())
            return 0;
        handOver.textures[layer] = std::move(copy);
        return 1;
    }

    // ---------------------------------------------------------------- render thread

    void Raytracing::HandOverFor(uint32_t managedGeneration)
    {
        std::lock_guard<std::mutex> lock(handOverLock);
        handOverGeneration = managedGeneration;
    }

    void Raytracing::TakeHandOverLocked()
    {
        {
            std::lock_guard<std::mutex> lock(handOverLock);
            std::swap(handOver, handOverTaken);
        }
        // As the main thread's calls would have left the pending state, had they written
        // it themselves: a mesh is in the hand-over's meshes or its releases, by its last
        // call, and the scenes are taken in order, each with what was set in it.
        HandOver& taken = handOverTaken;
        for (int32_t id : taken.releases)
        {
            pendingMeshes.erase(id);
            pendingReleases.push_back(id);
        }
        for (auto& entry : taken.meshes)
        {
            pendingReleases.erase(std::remove(pendingReleases.begin(), pendingReleases.end(), entry.first),
                                  pendingReleases.end());
            pendingMeshes[entry.first] = std::move(entry.second);
        }
        for (const auto& entry : taken.materials)
        {
            if (entry.second.first)
                materialData[entry.first] = entry.second.second;
            else
                materialData.erase(entry.first);
        }
        for (auto& entry : taken.textures)
            pendingTextures[entry.first] = std::move(entry.second);
        for (FrameScene& scene : taken.scenes)
        {
            FrameScene& into = SceneFor(frameScenes, scene.frameIndex);
            if (scene.instancesSet)
                into.instances = std::move(scene.instances);
            if (scene.groupsSet)
                into.groups = std::move(scene.groups);
        }
        taken.releases.clear();
        taken.meshes.clear();
        taken.materials.clear();
        taken.textures.clear();
        taken.scenes.clear();
    }

    void Raytracing::OnFrame(const void* data)
    {
        const RtFramePacket* packet = static_cast<const RtFramePacket*>(data);
        if (packet == nullptr || packet->size != sizeof(RtFramePacket) || packet->magic != kRtFrameMagic)
        {
            SetProblem("a frame rejected: the mod and the proxy disagree on its layout");
            return;
        }

        std::lock_guard<std::mutex> lock(mutex);
        TakeHandOverLocked();
        if (!attached || !unavailable.empty())
            return;
        if (packet->width == 0 || packet->height == 0 || packet->inputs[kRtInputDepth] == nullptr)
        {
            SetProblem("a frame without a size or without depth");
            return;
        }
        if (!EnsureDeviceLocked())
            return;
        EvictTexturesLocked();

        FrameWork frame;
        frame.packet = packet;
        frame.denoising = (packet->flags & kRtFrameDenoise) != 0;
        frame.extra = (packet->flags & kRtFrameExtra) != 0;
        if (!TakeTexturesLocked(frame) || !BeginListLocked(frame))
            return;
        Slot& slot = *frame.slot;

        frame.built = BuildMeshesLocked(slot);
        list->EndQuery(timestamps.Get(), D3D12_QUERY_TYPE_TIMESTAMP, frame.timestampBase + kTimestampMeshesBuilt);
        // A frame that stops early is submitted all the same (SubmitLocked).
        if (!UploadMaterialTexturesLocked(slot)
            || (list->EndQuery(timestamps.Get(), D3D12_QUERY_TYPE_TIMESTAMP, frame.timestampBase + kTimestampMaterialsUploaded), false)
            || (!ReuseSceneLocked(frame)
                && !BuildSceneLocked(slot, frame.slotNumber, packet->frameIndex, frame.inputs[kRtInputInstances],
                                     frame.instanceCount)))
        {
            builtSceneSlot = nullptr;
            SubmitLocked(slot, frame.inputs, frame.outputs);
            return;
        }
        if (frame.sceneSlot == nullptr)
        {
            frame.sceneSlot = frame.slot;
            frame.sceneSlotNumber = frame.slotNumber;
            builtSceneSlot = frame.slot;
            builtSceneSlotNumber = frame.slotNumber;
            builtSceneFrame = packet->frameIndex;
            builtSceneInstances = frame.instanceCount;
        }
        list->EndQuery(timestamps.Get(), D3D12_QUERY_TYPE_TIMESTAMP, frame.timestampBase + 1);
        if (!TakeNoisyTargetsLocked(frame))
        {
            SubmitLocked(slot, frame.inputs, frame.outputs);
            return;
        }

        PrepareRadianceCacheLocked(frame);
        WriteFrameViewsLocked(frame);
        WriteConstantsLocked(frame);
        TransitionTexturesLocked(frame);
        WriteMaterialViewLocked(frame.slotNumber);
        BindFrameLocked(frame);
        slot.held.push_back(materialTextures);

        RunPassesLocked(frame);
        WriteFrameGuidesLocked(frame);
        FinishFrameLocked(frame);
    }

    void Raytracing::EvictTexturesLocked()
    {
        // Retire unused shared copies once a second.
        const ULONGLONG now = GetTickCount64();
        if (now < nextEviction)
            return;
        nextEviction = now + 1000;
        for (auto it = textures.begin(); it != textures.end();)
            it = now - it->second.lastUse > kUnusedMs ? textures.erase(it) : std::next(it);
    }

    bool Raytracing::TakeTexturesLocked(FrameWork& frame)
    {
        const RtFramePacket& packet = *frame.packet;
        std::string problem;
        for (uint32_t i = 0; i < kRtMaxTextures; ++i)
        {
            if (packet.inputs[i] != nullptr && (frame.inputs[i] = SharedForLocked(packet.inputs[i], problem)) == nullptr)
            {
                SetProblem("input " + std::to_string(i) + ": " + problem);
                return false;
            }
            if (packet.outputs[i] != nullptr && (frame.outputs[i] = SharedForLocked(packet.outputs[i], problem)) == nullptr)
            {
                SetProblem("output " + std::to_string(i) + ": " + problem);
                return false;
            }
        }

        // Direct3D 11: this frame's inputs into the shared copies, once the queue is past
        // every list that used them.
        UINT64 usedUntil = 0;
        for (uint32_t i = 0; i < kRtMaxTextures; ++i)
        {
            if (frame.inputs[i] != nullptr)
                usedUntil = std::max(usedUntil, frame.inputs[i]->lastFence);
            if (frame.outputs[i] != nullptr)
                usedUntil = std::max(usedUntil, frame.outputs[i]->lastFence);
        }
        if (usedUntil != 0)
            context11->Wait(fence11.Get(), usedUntil);
        GpuProfiler::Get().OnMarker(kProfRtWaitShared + (frame.extra ? kProfRtExtraOffset : 0));
        for (uint32_t i = 0; i < kRtMaxTextures; ++i)
            if (frame.inputs[i] != nullptr)
                context11->CopyResource(frame.inputs[i]->texture.d3d11.Get(), static_cast<ID3D11Resource*>(packet.inputs[i]));
        GpuProfiler::Get().OnMarker(kProfRtInputs + (frame.extra ? kProfRtExtraOffset : 0));
        const UINT64 inputsCopied = ++copiedValue;
        context11->Signal(copied11.Get(), inputsCopied);
        context11->Flush();
        queue->Wait(copied12.Get(), inputsCopied);
        return true;
    }

    bool Raytracing::BeginListLocked(FrameWork& frame)
    {
        frame.slotNumber = slotIndex;
        frame.slot = &slots[slotIndex];
        slotIndex = (slotIndex + 1) % kSlots;
        Slot& slot = *frame.slot;
        WaitForSlotLocked(slot);
        slot.held.clear();
        HRESULT hr = slot.allocator->Reset();
        if (SUCCEEDED(hr))
            hr = list->Reset(slot.allocator.Get(), nullptr);
        if (FAILED(hr))
        {
            SetProblem("the command list could not be reset (" + Hr(hr) + ")");
            return false;
        }
        frame.timestampBase = frame.slotNumber * kTimestampsPerSlot;
        list->EndQuery(timestamps.Get(), D3D12_QUERY_TYPE_TIMESTAMP, frame.timestampBase);
        return true;
    }

    bool Raytracing::TakeNoisyTargetsLocked(FrameWork& frame)
    {
        // With the denoiser, the passes write its noisy textures, and it writes Unity's.
        // Frames without it for a while let its textures go; the further traces (the
        // ocean's) never take it.
        if (!frame.denoising)
        {
            if (!frame.extra && denoise.width != 0 && ++denoise.framesWithout >= kDenoiseUnusedFrames)
                DropDenoiseTargetsLocked(*frame.slot);
            return true;
        }
        uint32_t noisySignals = 0;
        for (const RtDenoisedSignal& noisy : kRtDenoised)
            if ((frame.packet->signals & noisy.signal) != 0 && frame.outputs[noisy.output] != nullptr
                && !(noisy.signal == kRtSignalSunVisibility && Config().rtSunUnfiltered))
                noisySignals |= 1u << noisy.index;
        if (!EnsureDenoiseTargetsLocked(frame.packet->width, frame.packet->height, noisySignals, *frame.slot))
            return false;
        for (const RtDenoisedSignal& noisy : kRtDenoised)
            if ((noisySignals & (1u << noisy.index)) != 0)
            {
                frame.noisyFor[noisy.output] = &denoise.noisy[noisy.index];
                UseLocked(denoise.noisy[noisy.index], D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
            }
        return true;
    }

    void Raytracing::WriteFrameViewsLocked(FrameWork& frame)
    {
        // Descriptors of this frame: t1-t8, then u0-u7, in the slot's part of the heap.
        D3D12_CPU_DESCRIPTOR_HANDLE cpu = heap->GetCPUDescriptorHandleForHeapStart();
        cpu.ptr += static_cast<SIZE_T>(frame.slotNumber) * kFrameDescriptors * increment;
        frame.frameTable = heap->GetGPUDescriptorHandleForHeapStart();
        frame.frameTable.ptr += static_cast<UINT64>(frame.slotNumber) * kFrameDescriptors * increment;
        for (uint32_t i = 0; i < kRtMaxTextures; ++i)
        {
            Shared* input = frame.inputs[i];
            const D3D12_SHADER_RESOURCE_VIEW_DESC srv = SrvDesc(input != nullptr ? input->texture.format : DXGI_FORMAT_R8G8B8A8_UNORM);
            device->CreateShaderResourceView(input != nullptr ? input->texture.d3d12.Get() : nullptr, &srv, cpu);
            cpu.ptr += increment;
        }
        for (uint32_t i = 0; i < kRtMaxTextures; ++i)
        {
            if (frame.noisyFor[i] != nullptr)
            {
                const D3D12_UNORDERED_ACCESS_VIEW_DESC uav = UavDesc(frame.noisyFor[i]->format);
                device->CreateUnorderedAccessView(frame.noisyFor[i]->resource.Get(), nullptr, &uav, cpu);
            }
            else
            {
                Shared* output = frame.outputs[i];
                const D3D12_UNORDERED_ACCESS_VIEW_DESC uav =
                    UavDesc(output != nullptr ? output->texture.format : DXGI_FORMAT_R16G16B16A16_FLOAT);
                device->CreateUnorderedAccessView(output != nullptr ? output->texture.d3d12.Get() : nullptr, nullptr, &uav,
                                                  cpu);
            }
            cpu.ptr += increment;
        }
    }

    void Raytracing::WriteConstantsLocked(const FrameWork& frame)
    {
        // Constants: the packet's matrices and vectors, the size, frame and flags.
        const RtFramePacket& packet = *frame.packet;
        memset(frame.slot->constantsMapped, 0, static_cast<size_t>(kRtConstantsSize));
        RtFrameConstants& constants = *reinterpret_cast<RtFrameConstants*>(frame.slot->constantsMapped);
        memcpy(constants.clipToWorld, packet.clipToWorld, sizeof(constants.clipToWorld));
        memcpy(constants.worldToClip, packet.worldToClip, sizeof(constants.worldToClip));
        memcpy(constants.prevWorldToClip, packet.prevWorldToClip, sizeof(constants.prevWorldToClip));
        memcpy(constants.prevWorldToClipFar, packet.prevWorldToClipFar, sizeof(constants.prevWorldToClipFar));
        memcpy(constants.shadowFade, packet.shadowFade, sizeof(constants.shadowFade));
        memcpy(constants.sunDirection, packet.sunDirection, sizeof(constants.sunDirection));
        memcpy(constants.sunColor, packet.sunColor, sizeof(constants.sunColor));
        memcpy(constants.skyColor, packet.skyColor, sizeof(constants.skyColor));
        memcpy(constants.params, packet.params, sizeof(constants.params));
        memcpy(constants.bodySphere, packet.bodySphere, sizeof(constants.bodySphere));
        memcpy(constants.ambient, packet.ambient, sizeof(constants.ambient));
        constants.frameSize[0] = packet.width;
        constants.frameSize[1] = packet.height;
        constants.frameIndex = packet.frameIndex;
        constants.frameFlags = packet.flags | (Config().rtCutoutTest == 1 ? RT_FLAG_CUTOUT_PLAIN : 0u)
                               | (Config().rtCutoutTest == 2 ? RT_FLAG_CUTOUT_OPAQUE : 0u)
                               | (static_cast<uint32_t>(std::clamp(Config().rtFoliageTransmission, 0.0f, 1.0f) * 255.0f + 0.5f)
                                  << RT_FLAG_FOLIAGE_SHIFT)
                               | (cache.active ? RT_FLAG_RADIANCE_CACHE : 0u) | (cache.update ? RT_FLAG_RADIANCE_CACHE_UPDATE : 0u);
        WriteRadianceCacheConstantsLocked(constants);
        memcpy(constants.lights, packet.lights, sizeof(constants.lights));
        memcpy(constants.lightParams, packet.lightParams, sizeof(constants.lightParams));
        memcpy(constants.nightLight, packet.nightLight, sizeof(constants.nightLight));
        memcpy(constants.planet, packet.planet, sizeof(constants.planet));
        memcpy(constants.lightSurface, packet.lightSurface, sizeof(constants.lightSurface));
        memcpy(constants.sea, packet.sea, sizeof(constants.sea));
        memcpy(constants.clouds, packet.clouds, sizeof(constants.clouds));
    }

    void Raytracing::TransitionTexturesLocked(FrameWork& frame)
    {
        // Unity's textures from COMMON into the states the passes use them in, each once,
        // and back once the passes are done (FinishFrameLocked).
        std::vector<D3D12_RESOURCE_BARRIER> into;
        for (uint32_t i = 0; i < kRtMaxTextures; ++i)
        {
            Shared* input = frame.inputs[i];
            bool inputBefore = false;
            for (uint32_t j = 0; j < i; ++j)
                inputBefore = inputBefore || frame.inputs[j] == input;
            if (input != nullptr && !inputBefore)
            {
                into.push_back(TransitionBarrier(input->texture.d3d12.Get(), D3D12_RESOURCE_STATE_COMMON,
                                                 D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE));
                frame.back.push_back(TransitionBarrier(input->texture.d3d12.Get(), D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE,
                                                       D3D12_RESOURCE_STATE_COMMON));
                frame.slot->held.push_back(input->texture.d3d12);
            }
            Shared* output = frame.outputs[i];
            bool outputBefore = false;
            for (uint32_t j = 0; j < i; ++j)
                outputBefore = outputBefore || frame.outputs[j] == output;
            if (output != nullptr && !outputBefore)
            {
                into.push_back(TransitionBarrier(output->texture.d3d12.Get(), D3D12_RESOURCE_STATE_COMMON,
                                                 D3D12_RESOURCE_STATE_UNORDERED_ACCESS));
                frame.back.push_back(TransitionBarrier(output->texture.d3d12.Get(), D3D12_RESOURCE_STATE_UNORDERED_ACCESS,
                                                       D3D12_RESOURCE_STATE_COMMON));
                frame.slot->held.push_back(output->texture.d3d12);
            }
        }
        if (!into.empty())
            list->ResourceBarrier(static_cast<UINT>(into.size()), into.data());
    }

    bool Raytracing::ReuseSceneLocked(FrameWork& frame)
    {
        // The ocean's trace comes after the frame's own in the same frame, on the same
        // queue: the scene's structure and the instances and materials it was built from
        // are still that frame's. The frame's slot is marked as read by this trace
        // (FinishFrameLocked), so that whatever takes it next, the next frame or Ray
        // Reconstruction, waits for this list before it writes the slot's buffers again.
        // Building it again cost 0.30 ms of the GPU's time a frame (the extra trace's
        // timestamps).
        const RtFramePacket& packet = *frame.packet;
        if (!frame.extra || (packet.flags & kRtFrameSameFrame) == 0 || frame.built != 0 || builtSceneSlot == nullptr
            || builtSceneSlot == frame.slot || builtSceneFrame != packet.frameIndex)
            return false;
        frame.sceneSlot = builtSceneSlot;
        frame.sceneSlotNumber = builtSceneSlotNumber;
        frame.instanceCount = builtSceneInstances;
        return true;
    }

    void Raytracing::BindFrameLocked(const FrameWork& frame)
    {
        // The frame's bindings: set before the passes, and again after anything that sets
        // its own.
        const D3D12_GPU_DESCRIPTOR_HANDLE heapGpu = heap->GetGPUDescriptorHandleForHeapStart();
        ID3D12DescriptorHeap* heaps[] = { heap.Get() };
        list->SetDescriptorHeaps(1, heaps);
        list->SetComputeRootSignature(rootSignature.Get());
        list->SetComputeRootConstantBufferView(0, frame.slot->constants->GetGPUVirtualAddress());
        list->SetComputeRootShaderResourceView(1, tlas->GetGPUVirtualAddress());
        list->SetComputeRootDescriptorTable(2, frame.frameTable);
        D3D12_GPU_DESCRIPTOR_HANDLE outputTable = frame.frameTable;
        outputTable.ptr += static_cast<UINT64>(kRtMaxTextures) * increment;
        list->SetComputeRootDescriptorTable(3, outputTable);
        const Slot& scene = frame.sceneSlot != nullptr ? *frame.sceneSlot : *frame.slot;
        const UINT sceneNumber = frame.sceneSlot != nullptr ? frame.sceneSlotNumber : frame.slotNumber;
        list->SetComputeRootShaderResourceView(4, scene.instanceDataAddress);
        list->SetComputeRootShaderResourceView(5, scene.materials->GetGPUVirtualAddress());
        D3D12_GPU_DESCRIPTOR_HANDLE meshTable = heapGpu;
        meshTable.ptr += static_cast<UINT64>(kSlots) * kFrameDescriptors * increment;
        list->SetComputeRootDescriptorTable(6, meshTable);
        meshTable.ptr += static_cast<UINT64>(kMaxMeshes) * increment;
        list->SetComputeRootDescriptorTable(7, meshTable);
        meshTable.ptr += static_cast<UINT64>(kMaxMeshes + sceneNumber) * increment;
        list->SetComputeRootDescriptorTable(8, meshTable);
        BindRadianceCacheLocked();
    }

    void Raytracing::RunPassesLocked(FrameWork& frame)
    {
        const RtFramePacket& packet = *frame.packet;
        // Each pass's end is stamped, in kRtPassOrder; a pass that does not run stamps where
        // the one before it ended, so it counts nothing. The radiance cache cleared or its
        // counts zeroed first, its resolve in its place among them (kRtPassCache).
        BeginRadianceCacheLocked(frame);
        for (UINT position = 0; position < kRtPassTimings; ++position)
        {
            const UINT index = kRtPassOrder[position];
            const uint32_t signal = 1u << index;
            const RtPassEntry* pass = nullptr;
            for (const RtPassEntry& entry : kRtPasses)
                if (entry.signal == signal)
                    pass = &entry;
            auto pipeline = pass != nullptr ? pipelines.find(pass->signal) : pipelines.end();
            if ((packet.signals & signal) != 0 && pipeline != pipelines.end())
                DispatchPassLocked(frame, signal, CachedPipelineLocked(signal, pipeline->second.Get()));
            if (index == kRtPassDenoiser && frame.denoising)
                RunDenoisersLocked(frame);
            if (index == kRtPassCache)
                ResolveRadianceCacheLocked(frame);
            list->EndQuery(timestamps.Get(), D3D12_QUERY_TYPE_TIMESTAMP, frame.timestampBase + 2 + position);
        }
    }

    void Raytracing::DispatchPassLocked(FrameWork& frame, uint32_t signal, ID3D12PipelineState* pipeline)
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
                Shared* output = (frame.packet->signals & traced[k]) != 0 ? frame.outputs[finished[k]] : nullptr;
                if (output == nullptr)
                    continue;
                reads[k] = Of(output);
                toRead.push_back(TransitionBarrier(output->texture.d3d12.Get(), D3D12_RESOURCE_STATE_UNORDERED_ACCESS,
                                                   D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE));
                toWrite.push_back(TransitionBarrier(output->texture.d3d12.Get(), D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE,
                                                    D3D12_RESOURCE_STATE_UNORDERED_ACCESS));
            }
            if (!toRead.empty())
                list->ResourceBarrier(static_cast<UINT>(toRead.size()), toRead.data());
            UINT used = kForwardLightTable;
            const View writes[4] = {};
            list->SetComputeRootDescriptorTable(10, TableLocked(frame.slotNumber, used, reads, writes));
        }
        list->SetPipelineState(pipeline);
        list->Dispatch((frame.packet->width + 7) / 8, (frame.packet->height + 7) / 8, 1);
        if (!toWrite.empty())
            list->ResourceBarrier(static_cast<UINT>(toWrite.size()), toWrite.data());
    }

    void Raytracing::RunDenoisersLocked(FrameWork& frame)
    {
        // AMD FSR Ray Regeneration for the diffuse light and the reflections where it
        // runs, ReDefinition's denoiser for the rest; for all where it does not.
        const RtFramePacket& packet = *frame.packet;
        bool amdDenoised = false;
        if (RegenerationReadyLocked(packet))
        {
            amdDenoised = RunRegenerationLocked(packet, frame.slotNumber, frame.outputs, frame.inputs[kRtInputMotion], *frame.slot);
            // AMD's denoiser sets its own heap and root signature on the list.
            BindFrameLocked(frame);
        }
        RtFramePacket rest = packet;
        if (Config().rtSunUnfiltered)
            rest.signals &= ~kRtSignalSunVisibility;
        if (amdDenoised)
            rest.signals &= ~(kRtSignalDiffuse | kRtSignalSpecular);
        // ReDefinition's history stood still while AMD's denoiser had the frames.
        else if (regeneration.previousWasAmd)
            rest.flags |= kRtFrameReset;
        regeneration.previousWasAmd = amdDenoised;
        RunDenoiserLocked(rest, frame.slotNumber, frame.outputs, frame.inputs[kRtInputMotion]);
    }

    void Raytracing::WriteFrameGuidesLocked(FrameWork& frame)
    {
        // DLSS Ray Reconstruction's guides from this frame's surfaces, which the
        // reconstruction of the same frame takes (RaytracingReconstruct.cpp). Not with the
        // denoiser: the guides read the reflections' hit distance from u2, which the
        // denoiser's steps have overwritten by then, and Ray Reconstruction takes the
        // noisy signals.
        const RtFramePacket& packet = *frame.packet;
        if (frame.extra)
            return;
        guides.fromFrame = false;
        if (frame.denoising || (packet.flags & kRtFrameGuides) == 0)
            return;
        auto pipeline = denoisePipelines.find("RT_FrameGuides");
        if (pipeline == denoisePipelines.end() || !EnsureGuidesLocked(frame.slot->held, packet.width, packet.height))
            return;
        const D3D12_RESOURCE_BARRIER written = UavBarrier(nullptr);
        list->ResourceBarrier(1, &written);
        for (DenoiseTarget* target : { &guides.albedo, &guides.specularAlbedo, &guides.normalRoughness, &guides.hitDistance })
            UseLocked(*target, D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
        UINT used = kFrameGuidesTable;
        const View reads[8] = {};
        const View writes[4] = { Of(&guides.albedo), Of(&guides.specularAlbedo), Of(&guides.normalRoughness),
                                 Of(&guides.hitDistance) };
        BindTablesLocked(TableLocked(frame.slotNumber, used, reads, writes));
        list->SetPipelineState(pipeline->second.Get());
        list->Dispatch((packet.width + 7) / 8, (packet.height + 7) / 8, 1);
        // Streamline takes them in the state it is given.
        for (DenoiseTarget* target : { &guides.albedo, &guides.specularAlbedo, &guides.normalRoughness, &guides.hitDistance })
            UseLocked(*target, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
        guides.fromFrame = true;
        guides.frame = packet.frameIndex;
    }

    void Raytracing::FinishFrameLocked(FrameWork& frame)
    {
        if (!frame.back.empty())
            list->ResourceBarrier(static_cast<UINT>(frame.back.size()), frame.back.data());
        // The timings are the frame's; a further trace of it (the ocean's) is timed apart,
        // its scene build and the rest, its end marked here as its passes do not.
        if (frame.extra)
            list->EndQuery(timestamps.Get(), D3D12_QUERY_TYPE_TIMESTAMP, frame.timestampBase + kTimestampTraceEnd);
        list->ResolveQueryData(timestamps.Get(), D3D12_QUERY_TYPE_TIMESTAMP, frame.timestampBase, kTimestampsPerSlot,
                               timestampReadback.Get(), static_cast<UINT64>(frame.timestampBase) * sizeof(UINT64));
        frame.slot->timed = true;
        frame.slot->extra = frame.extra;
        const UINT64 finished = SubmitLocked(*frame.slot, frame.inputs, frame.outputs);
        if (finished == 0)
            return;
        if (frame.sceneSlot != nullptr && frame.sceneSlot != frame.slot)
            frame.sceneSlot->sceneReadUntil = finished;

        // The results back into Unity's textures before the event returns.
        context11->Wait(fence11.Get(), finished);
        GpuProfiler::Get().OnMarker(kProfRtTrace + (frame.extra ? kProfRtExtraOffset : 0));
        for (uint32_t i = 0; i < kRtMaxTextures; ++i)
            if (frame.outputs[i] != nullptr)
                context11->CopyResource(static_cast<ID3D11Resource*>(frame.packet->outputs[i]), frame.outputs[i]->texture.d3d11.Get());
        GpuProfiler::Get().OnMarker(kProfRtOutputs + (frame.extra ? kProfRtExtraOffset : 0));

        std::lock_guard<std::mutex> status(statusLock);
        if (frame.extra)
            return;
        ++frames;
        liveMeshes = static_cast<uint32_t>(meshes.size());
        lastInstances = frame.instanceCount;
        lastBuilt = frame.built;
    }

    int Raytracing::Status(char* buffer, int size) const
    {
        // A device the driver has taken away runs nothing more; its reason goes first.
        HRESULT removed = S_OK;
        {
            // Not the service's mutex: the main thread asks every 30 frames, and the
            // render thread holds the mutex through its waits for the GPU.
            ComPtr<ID3D12Device5> statusOf;
            ComPtr<ID3D12InfoQueue> debugOf;
            {
                std::lock_guard<std::mutex> status(statusLock);
                statusOf = statusDevice;
                debugOf = debugMessages;
            }
            if (statusOf != nullptr)
            {
                removed = statusOf->GetDeviceRemovedReason();
                // The debug layer's messages, where it is on, into the log once.
                if (debugOf != nullptr)
                {
                    ID3D12InfoQueue* messages = debugOf.Get();
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
        // The radiance cache's counts of the latest frame read, where it runs (RaytracingCache.cpp).
        char cacheText[160] = {};
        if (cache.reported)
            _snprintf_s(cacheText, sizeof(cacheText), _TRUNCATE,
                        " cache live=%u inserted=%u full=%u evicted=%u samples=%u queries=%u warm=%u",
                        cache.stats[4], cache.stats[0], cache.stats[1], cache.stats[5], cache.stats[6], cache.stats[2],
                        cache.stats[3]);
        if (buffer != nullptr && size > 0)
            _snprintf_s(buffer, static_cast<size_t>(size), _TRUNCATE,
                        "generation=%u frames=%llu reconstructed=%llu regenerated=%llu meshes=%u built=%u instances=%u"
                        " build=%.3fms trace=%.3fms extra build=%.3fms extra trace=%.3fms meshBuild=%u/%.3fms/%.3fms/%u"
                        " blas=%.2fMB geometry=%.2fMB reserved=%.2fMB%s%s%s",
                        generation.load(), static_cast<unsigned long long>(frames),
                        static_cast<unsigned long long>(reconstructed), static_cast<unsigned long long>(regenerated), liveMeshes,
                        lastBuilt, lastInstances,
                        buildMs, traceMs, extraBuildMs, extraTraceMs, meshBuildCount, meshBuildCpuMs, meshBuildGpuMs,
                        meshBuildsTimed, static_cast<double>(blasBytes) / 1e6, static_cast<double>(geometryBytes) / 1e6,
                        static_cast<double>(buildReservedBytes) / 1e6, cacheText, lastProblem.empty() ? "" : " last problem: ",
                        lastProblem.c_str());
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
