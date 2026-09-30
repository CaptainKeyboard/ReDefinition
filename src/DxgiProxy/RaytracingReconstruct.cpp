// DLSS Ray Reconstruction for the raytraced frame (docs/development/raytracing.md,
// "Denoising"): the noisy frame and its guides into NVIDIA's denoiser and upscaler,
// through Streamline, on the proxy's Direct3D 12 device, in place of DLSS Super
// Resolution.

#include "Raytracing.h"

#include "D3d12Util.h"
#include "Log.h"
#include "Streamline.h"

#include <cstring>

using Microsoft::WRL::ComPtr;

namespace redefinition
{
    namespace
    {
        sl::float4x4 Matrix(const float* rows)
        {
            sl::float4x4 m;
            memcpy(&m, rows, sizeof(m));
            return m;
        }
    }

    int Raytracing::ReconstructionAvailable(char* buffer, int size) const
    {
        std::string why;
        const bool ready = Streamline::Get().RayReconstructionReady(why);
        if (buffer != nullptr && size > 0)
            strncpy_s(buffer, static_cast<size_t>(size), why.c_str(), _TRUNCATE);
        return ready ? 1 : 0;
    }

    void Raytracing::OnReconstruct(const void* data)
    {
        const RtReconstructPacket* packet = static_cast<const RtReconstructPacket*>(data);
        if (packet == nullptr || packet->size != sizeof(RtReconstructPacket) || packet->magic != kRtReconstructMagic)
        {
            SetProblem("a Ray Reconstruction frame rejected: the mod and the proxy disagree on its layout");
            return;
        }
        std::string why;
        if (!Streamline::Get().RayReconstructionReady(why))
        {
            SetProblem("Ray Reconstruction: " + why);
            return;
        }

        std::lock_guard<std::mutex> lock(mutex);
        if (!attached || !unavailable.empty() || !EnsureDeviceLocked())
            return;
        void* const given[] = { packet->colour, packet->depth, packet->motion, packet->gbuffer0, packet->gbuffer1,
                                packet->gbuffer2, packet->specular, packet->output };
        for (void* texture : given)
            if (texture == nullptr)
            {
                SetProblem("a Ray Reconstruction frame without all its textures");
                return;
            }
        CompletePendingLocked();

        Shared* shared[8] = {};
        std::string problem;
        for (int i = 0; i < 8; ++i)
            if ((shared[i] = SharedForLocked(given[i], problem)) == nullptr)
            {
                SetProblem("Ray Reconstruction, texture " + std::to_string(i) + ": " + problem);
                return;
            }
        Shared* const colour = shared[0];
        Shared* const depth = shared[1];
        Shared* const motion = shared[2];
        Shared* const specular = shared[6];
        Shared* const output = shared[7];

        UINT64 usedUntil = 0;
        for (Shared* texture : shared)
            usedUntil = std::max(usedUntil, texture->lastFence);
        if (usedUntil != 0)
            context11->Wait(fence11.Get(), usedUntil);
        for (int i = 0; i < 7; ++i)
            context11->CopyResource(shared[i]->texture.d3d11.Get(), static_cast<ID3D11Resource*>(given[i]));
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

        // The guides, at the render size.
        const UINT width = packet->renderWidth, height = packet->renderHeight;
        if (guides.width != width || guides.height != height || guides.albedo.resource == nullptr)
        {
            for (DenoiseTarget* target : { &guides.albedo, &guides.specularAlbedo, &guides.normalRoughness, &guides.hitDistance })
                if (target->resource != nullptr)
                    slot.held.push_back(target->resource);
            guides = GuideTargets{};
            const DXGI_FORMAT half4 = DXGI_FORMAT_R16G16B16A16_FLOAT;
            if (!guides.albedo.Create(device.Get(), width, height, half4)
                || !guides.specularAlbedo.Create(device.Get(), width, height, half4)
                || !guides.normalRoughness.Create(device.Get(), width, height, half4)
                || !guides.hitDistance.Create(device.Get(), width, height, DXGI_FORMAT_R16_FLOAT))
            {
                guides = GuideTargets{};
                SetProblem("Ray Reconstruction's guides could not be made");
                list->Close();
                return;
            }
            guides.width = width;
            guides.height = height;
        }

        // Every shared texture into the state the guide pass reads it in.
        std::vector<D3D12_RESOURCE_BARRIER> into;
        std::vector<D3D12_RESOURCE_BARRIER> back;
        for (int i = 0; i < 7; ++i)
        {
            into.push_back(TransitionBarrier(shared[i]->texture.d3d12.Get(), D3D12_RESOURCE_STATE_COMMON,
                                             D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE));
            back.push_back(TransitionBarrier(shared[i]->texture.d3d12.Get(), D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE,
                                             D3D12_RESOURCE_STATE_COMMON));
            slot.held.push_back(shared[i]->texture.d3d12);
        }
        slot.held.push_back(output->texture.d3d12);
        list->ResourceBarrier(static_cast<UINT>(into.size()), into.data());

        // The frame table as the raytracing passes have it: t1 depth, t2-t4 the G-buffer.
        D3D12_CPU_DESCRIPTOR_HANDLE cpu = heap->GetCPUDescriptorHandleForHeapStart();
        cpu.ptr += static_cast<SIZE_T>(slotNumber) * kFrameDescriptors * increment;
        D3D12_GPU_DESCRIPTOR_HANDLE frameTable = heap->GetGPUDescriptorHandleForHeapStart();
        frameTable.ptr += static_cast<UINT64>(slotNumber) * kFrameDescriptors * increment;
        Shared* const table[8] = { depth, shared[3], shared[4], shared[5], motion, nullptr, nullptr, nullptr };
        for (UINT i = 0; i < kFrameDescriptors; ++i)
        {
            if (i < kRtMaxTextures)
            {
                D3D12_SHADER_RESOURCE_VIEW_DESC srv = {};
                srv.Format = table[i] != nullptr ? table[i]->texture.format : DXGI_FORMAT_R8G8B8A8_UNORM;
                srv.ViewDimension = D3D12_SRV_DIMENSION_TEXTURE2D;
                srv.Shader4ComponentMapping = D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING;
                srv.Texture2D.MipLevels = 1;
                device->CreateShaderResourceView(table[i] != nullptr ? table[i]->texture.d3d12.Get() : nullptr, &srv, cpu);
            }
            else
            {
                D3D12_UNORDERED_ACCESS_VIEW_DESC uav = {};
                uav.Format = DXGI_FORMAT_R16G16B16A16_FLOAT;
                uav.ViewDimension = D3D12_UAV_DIMENSION_TEXTURE2D;
                device->CreateUnorderedAccessView(nullptr, nullptr, &uav, cpu);
            }
            cpu.ptr += increment;
        }
        uint8_t* constants = slot.constantsMapped;
        memset(constants, 0, 512);
        memcpy(constants, packet->clipToWorld, 64);
        const uint32_t tail[4] = { width, height, packet->frameIndex, 0 };
        memcpy(constants + 336, tail, sizeof(tail));

        ID3D12DescriptorHeap* heaps[] = { heap.Get() };
        list->SetDescriptorHeaps(1, heaps);
        list->SetComputeRootSignature(rootSignature.Get());
        list->SetComputeRootConstantBufferView(0, slot.constants->GetGPUVirtualAddress());
        list->SetComputeRootDescriptorTable(2, frameTable);
        for (DenoiseTarget* target : { &guides.albedo, &guides.specularAlbedo, &guides.normalRoughness, &guides.hitDistance })
            UseLocked(*target, D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
        {
            UINT used = 0;
            const View reads[8] = { Of(specular) };
            const View writes[4] = { Of(&guides.albedo), Of(&guides.specularAlbedo), Of(&guides.normalRoughness),
                                     Of(&guides.hitDistance) };
            const D3D12_GPU_DESCRIPTOR_HANDLE guideTable = TableLocked(slotNumber, used, reads, writes);
            auto pipeline = denoisePipelines.find("RT_ReconstructGuides");
            if (pipeline == denoisePipelines.end())
            {
                SetProblem("this build has no Ray Reconstruction guide pass");
                list->Close();
                return;
            }
            list->SetPipelineState(pipeline->second.Get());
            list->SetComputeRootDescriptorTable(10, guideTable);
            D3D12_GPU_DESCRIPTOR_HANDLE writeTable = guideTable;
            writeTable.ptr += static_cast<UINT64>(8) * increment;
            list->SetComputeRootDescriptorTable(11, writeTable);
            list->Dispatch((width + 7) / 8, (height + 7) / 8, 1);
        }
        // Streamline takes every resource in the state it is given and leaves it so.
        for (DenoiseTarget* target : { &guides.albedo, &guides.specularAlbedo, &guides.normalRoughness, &guides.hitDistance })
            UseLocked(*target, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
        list->ResourceBarrier(static_cast<UINT>(back.size()), back.data());

        constexpr uint32_t kCommon = static_cast<uint32_t>(D3D12_RESOURCE_STATE_COMMON);
        constexpr uint32_t kRead = static_cast<uint32_t>(D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
        const auto resource = [](ID3D12Resource* native, uint32_t state, UINT w, UINT h, DXGI_FORMAT format)
        {
            sl::Resource r(sl::ResourceType::eTex2d, native, state);
            r.width = w;
            r.height = h;
            r.nativeFormat = static_cast<uint32_t>(format);
            return r;
        };
        sl::Resource colourResource = resource(colour->texture.d3d12.Get(), kCommon, colour->texture.width,
                                               colour->texture.height, colour->texture.format);
        sl::Resource outputResource = resource(output->texture.d3d12.Get(), kCommon, output->texture.width,
                                               output->texture.height, output->texture.format);
        sl::Resource depthResource = resource(depth->texture.d3d12.Get(), kCommon, depth->texture.width,
                                              depth->texture.height, depth->texture.format);
        sl::Resource motionResource = resource(motion->texture.d3d12.Get(), kCommon, motion->texture.width,
                                               motion->texture.height, motion->texture.format);
        sl::Resource albedoResource = resource(guides.albedo.resource.Get(), kRead, width, height, guides.albedo.format);
        sl::Resource specularResource =
            resource(guides.specularAlbedo.resource.Get(), kRead, width, height, guides.specularAlbedo.format);
        sl::Resource normalResource =
            resource(guides.normalRoughness.resource.Get(), kRead, width, height, guides.normalRoughness.format);
        sl::Resource hitResource = resource(guides.hitDistance.resource.Get(), kRead, width, height, guides.hitDistance.format);
        const sl::Extent input{ 0, 0, width, height };
        const sl::Extent outputExtent{ 0, 0, packet->outputWidth, packet->outputHeight };
        const sl::ResourceLifecycle now = sl::ResourceLifecycle::eOnlyValidNow;
        const sl::ResourceTag tags[] = {
            sl::ResourceTag(&colourResource, sl::kBufferTypeScalingInputColor, now, &input),
            sl::ResourceTag(&outputResource, sl::kBufferTypeScalingOutputColor, now, &outputExtent),
            sl::ResourceTag(&depthResource, sl::kBufferTypeDepth, now, &input),
            sl::ResourceTag(&motionResource, sl::kBufferTypeMotionVectors, now, &input),
            sl::ResourceTag(&albedoResource, sl::kBufferTypeAlbedo, now, &input),
            sl::ResourceTag(&specularResource, sl::kBufferTypeSpecularAlbedo, now, &input),
            sl::ResourceTag(&normalResource, sl::kBufferTypeNormalRoughness, now, &input),
            sl::ResourceTag(&hitResource, sl::kBufferTypeSpecularHitDistance, now, &input),
        };

        sl::Constants slConstants{};
        slConstants.cameraViewToClip = Matrix(packet->viewToClip);
        slConstants.clipToCameraView = Matrix(packet->clipToView);
        sl::float4x4 identity;
        memset(&identity, 0, sizeof(identity));
        for (int i = 0; i < 4; ++i)
            reinterpret_cast<float*>(&identity)[i * 5] = 1.0f;
        slConstants.clipToLensClip = identity;
        slConstants.clipToPrevClip = Matrix(packet->clipToPrevClip);
        slConstants.prevClipToClip = Matrix(packet->prevClipToClip);
        slConstants.jitterOffset = sl::float2(packet->jitter[0], packet->jitter[1]);
        slConstants.mvecScale = sl::float2(packet->motionScale[0], packet->motionScale[1]);
        slConstants.cameraPinholeOffset = sl::float2(0.0f, 0.0f);
        slConstants.cameraPos = sl::float3(0.0f, 0.0f, 0.0f);
        slConstants.cameraUp = sl::float3(packet->up[0], packet->up[1], packet->up[2]);
        slConstants.cameraRight = sl::float3(packet->right[0], packet->right[1], packet->right[2]);
        slConstants.cameraFwd = sl::float3(packet->forward[0], packet->forward[1], packet->forward[2]);
        slConstants.cameraNear = packet->nearPlane;
        slConstants.cameraFar = packet->farPlane;
        slConstants.cameraFOV = packet->verticalFov;
        slConstants.cameraAspectRatio = height != 0 ? static_cast<float>(width) / static_cast<float>(height) : 1.0f;
        slConstants.depthInverted = sl::Boolean::eTrue;
        slConstants.cameraMotionIncluded = sl::Boolean::eTrue;
        slConstants.motionVectors3D = sl::Boolean::eFalse;
        slConstants.motionVectorsDilated = sl::Boolean::eFalse;
        slConstants.motionVectorsJittered = sl::Boolean::eFalse;
        slConstants.orthographicProjection = sl::Boolean::eFalse;
        slConstants.reset = (packet->flags & kRtFrameReset) != 0 ? sl::Boolean::eTrue : sl::Boolean::eFalse;

        sl::DLSSDOptions options{};
        options.mode = static_cast<sl::DLSSMode>(packet->mode);
        options.outputWidth = packet->outputWidth;
        options.outputHeight = packet->outputHeight;
        options.colorBuffersHDR = sl::Boolean::eTrue;
        options.normalRoughnessMode = sl::DLSSDNormalRoughnessMode::ePacked;
        options.worldToCameraView = Matrix(packet->worldToView);
        options.cameraViewToWorld = Matrix(packet->viewToWorld);
        const sl::DLSSDPreset preset = static_cast<sl::DLSSDPreset>(packet->preset);
        options.dlaaPreset = options.qualityPreset = options.balancedPreset = options.performancePreset =
            options.ultraPerformancePreset = options.ultraQualityPreset = preset;

        const bool evaluated = Streamline::Get().EvaluateRayReconstruction(slConstants, options, tags,
                                                                           static_cast<uint32_t>(sizeof(tags) / sizeof(tags[0])),
                                                                           list.Get());
        hr = list->Close();
        if (FAILED(hr))
        {
            SetProblem("the command list could not be closed (" + Hr(hr) + ")");
            return;
        }
        ID3D12CommandList* lists[] = { list.Get() };
        queue->ExecuteCommandLists(1, lists);
        const UINT64 finished = ++fenceValue;
        queue->Signal(fence12.Get(), finished);
        slot.done = finished;
        for (Shared* texture : shared)
            texture->lastFence = finished;
        if (!evaluated)
        {
            SetProblem("Ray Reconstruction did not evaluate (the proxy's log has Streamline's reason)");
            return;
        }
        context11->Wait(fence11.Get(), finished);
        context11->CopyResource(static_cast<ID3D11Resource*>(packet->output), output->texture.d3d11.Get());

        std::lock_guard<std::mutex> status(statusLock);
        ++reconstructed;
    }
}
