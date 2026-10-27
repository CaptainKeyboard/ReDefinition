// DLSS Ray Reconstruction for the raytraced frame (docs/development/flow-raytracing.md,
// "Denoising"): the noisy frame and its guides into NVIDIA's denoiser and upscaler,
// on the proxy's Direct3D 12 device, in place of DLSS Super Resolution -- through
// Streamline where its swapchain presents, through NGX directly beside FSR's or
// XeSS's frame generation (rrPath, Config.h).

#include "GpuProfiler.h"
#include "Raytracing.h"
#include "RaytracingInternal.h"

#include "Config.h"
#include "D3d12Util.h"
#include "DlssRayReconstruction.h"
#include "Log.h"
#include "Streamline.h"

#include <cstring>

using Microsoft::WRL::ComPtr;

namespace redefinition
{
    namespace
    {
        // A packet's matrix, rows first, as Streamline's float4x4, copied as it is.
        sl::float4x4 Matrix(const float* rows)
        {
            sl::float4x4 m;
            memcpy(&m, rows, sizeof(m));
            return m;
        }

        // How a frame reaches DLSS Ray Reconstruction (ChooseRoute).
        enum class Route
        {
            None,         // not at all: the reason goes to the status
            Streamline,   // Streamline's sl.dlss_d
            Ngx,          // NGX directly (DlssRayReconstruction.h)
        };

        // Chooses Streamline's sl.dlss_d where Streamline has the device, which it has where
        // its swapchain presents, and NGX directly where Streamline is not set up on it. One
        // of the two starts NGX on a device (DlssRayReconstruction.h). rrPath narrows the
        // choice to one of them.
        Route ChooseRoute(int path, std::string& why)
        {
            Streamline& streamline = Streamline::Get();
            if (streamline.DeviceSet())
            {
                if (path == kRrPathNgx)
                {
                    why = "rrPath=ngx, but Streamline runs on this device for DLSS frame generation and holds NGX on it";
                    return Route::None;
                }
                if (!streamline.RayReconstructionReady(why))
                    return Route::None;
                why = "ready through Streamline";
                return Route::Streamline;
            }
            if (path == kRrPathStreamline)
            {
                streamline.RayReconstructionReady(why);
                why += " (rrPath=streamline)";
                return Route::None;
            }
            return DlssRayReconstruction::Get().Ready(why) ? Route::Ngx : Route::None;
        }
    }

    int Raytracing::ReconstructionAvailable(char* buffer, int size) const
    {
        std::string why;
        const bool ready = ChooseRoute(rrPath.load(), why) != Route::None;
        if (buffer != nullptr && size > 0)
            strncpy_s(buffer, static_cast<size_t>(size), why.c_str(), _TRUNCATE);
        return ready ? 1 : 0;
    }

    uint64_t Raytracing::ReconstructedFrames()
    {
        std::lock_guard<std::mutex> status(statusLock);
        return reconstructed;
    }

    bool Raytracing::EnsureGuidesLocked(std::vector<ComPtr<ID3D12Resource>>& held, UINT width, UINT height)
    {
        if (guides.width == width && guides.height == height && guides.albedo.resource != nullptr)
            return true;
        for (DenoiseTarget* target : { &guides.albedo, &guides.specularAlbedo, &guides.normalRoughness, &guides.hitDistance })
            if (target->resource != nullptr)
                held.push_back(target->resource);
        guides = GuideTargets{};
        const DXGI_FORMAT half4 = DXGI_FORMAT_R16G16B16A16_FLOAT;
        if (!guides.albedo.Create(device.Get(), width, height, half4)
            || !guides.specularAlbedo.Create(device.Get(), width, height, half4)
            || !guides.normalRoughness.Create(device.Get(), width, height, half4)
            || !guides.hitDistance.Create(device.Get(), width, height, DXGI_FORMAT_R16_FLOAT))
        {
            guides = GuideTargets{};
            SetProblem("Ray Reconstruction's guides could not be made");
            return false;
        }
        guides.width = width;
        guides.height = height;
        return true;
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
        const Route route = ChooseRoute(rrPath.load(), why);
        if (route == Route::None)
        {
            SetProblem("Ray Reconstruction: " + why);
            return;
        }

        std::lock_guard<std::mutex> lock(mutex);
        if (!attached || !unavailable.empty() || !EnsureDeviceLocked())
            return;
        void* const given[] = { packet->colour, packet->depth, packet->motion, packet->gbuffer0, packet->gbuffer1,
                                packet->gbuffer2, packet->specular, packet->output };
        // The guides of the raytraced frame of the same index, where it made them; the
        // G-buffer and the reflections are then not needed.
        const UINT width = packet->renderWidth, height = packet->renderHeight;
        const bool prepared = guides.fromFrame && guides.frame == packet->frameIndex && guides.width == width
                              && guides.height == height;
        for (int i = 0; i < 8; ++i)
            if (given[i] == nullptr && (!prepared || i < 3 || i == 7))
            {
                SetProblem(prepared || i < 3 || i == 7
                               ? "a Ray Reconstruction frame without its colour, depth, motion or output"
                               : "a Ray Reconstruction frame without the G-buffer, and no guides from its raytraced frame");
                return;
            }

        Shared* shared[8] = {};
        std::string problem;
        for (int i = 0; i < 8; ++i)
            if (given[i] != nullptr && (shared[i] = SharedForLocked(given[i], problem)) == nullptr)
            {
                SetProblem("Ray Reconstruction, texture " + std::to_string(i) + ": " + problem);
                return;
            }
        ReconstructFrame frame;
        frame.packet = packet;
        frame.colour = shared[0];
        frame.depth = shared[1];
        frame.motion = shared[2];
        frame.output = shared[7];
        frame.width = width;
        frame.height = height;
        Shared* const specular = shared[6];

        UINT64 usedUntil = 0;
        for (Shared* texture : shared)
            if (texture != nullptr)
                usedUntil = std::max(usedUntil, texture->lastFence);
        CopyInputsLocked(usedUntil, shared, given, 7, kProfRrWaitShared, kProfRrInputs);

        UINT slotNumber = 0;
        Slot* const taken = BeginSlotLocked(slotNumber);
        if (taken == nullptr)
            return;
        Slot& slot = *taken;

        const UINT timestampBase = slotNumber * kTimestampsPerSlot;
        list->EndQuery(timestamps.Get(), D3D12_QUERY_TYPE_TIMESTAMP, timestampBase + kTimestampRrStart);

        // The guides, at the render size.
        if (!prepared && !EnsureGuidesLocked(slot.held, width, height))
        {
            list->Close();
            return;
        }

        // Every shared texture into the state the guide pass reads it in.
        std::vector<D3D12_RESOURCE_BARRIER> into;
        std::vector<D3D12_RESOURCE_BARRIER> back;
        for (int i = 0; i < 7; ++i)
        {
            if (shared[i] == nullptr)
                continue;
            into.push_back(TransitionBarrier(shared[i]->texture.d3d12.Get(), D3D12_RESOURCE_STATE_COMMON,
                                             D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE));
            back.push_back(TransitionBarrier(shared[i]->texture.d3d12.Get(), D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE,
                                             D3D12_RESOURCE_STATE_COMMON));
            slot.held.push_back(shared[i]->texture.d3d12);
        }
        slot.held.push_back(frame.output->texture.d3d12);
        list->ResourceBarrier(static_cast<UINT>(into.size()), into.data());

        // The frame table as the raytracing passes have it: t1 depth, t2-t4 the G-buffer,
        // t5 the motion vectors, and nothing to write.
        FrameWork views;
        views.slotNumber = slotNumber;
        views.inputs[0] = frame.depth;
        views.inputs[1] = shared[3];
        views.inputs[2] = shared[4];
        views.inputs[3] = shared[5];
        views.inputs[4] = frame.motion;
        WriteFrameViewsLocked(views);
        const D3D12_GPU_DESCRIPTOR_HANDLE frameTable = views.frameTable;
        memset(slot.constantsMapped, 0, static_cast<size_t>(kRtConstantsSize));
        RtFrameConstants& constants = *reinterpret_cast<RtFrameConstants*>(slot.constantsMapped);
        memcpy(constants.clipToWorld, packet->clipToWorld, sizeof(constants.clipToWorld));
        constants.frameSize[0] = width;
        constants.frameSize[1] = height;
        constants.frameIndex = packet->frameIndex;
        constants.frameFlags = packet->flags & kRtFrameLegacySmoothness;

        ID3D12DescriptorHeap* heaps[] = { heap.Get() };
        list->SetDescriptorHeaps(1, heaps);
        list->SetComputeRootSignature(rootSignature.Get());
        list->SetComputeRootConstantBufferView(0, slot.constants->GetGPUVirtualAddress());
        list->SetComputeRootDescriptorTable(2, frameTable);
        if (!prepared)
        {
            // The pipeline first: the guides' states are recorded only for a list that runs.
            ID3D12PipelineState* pipeline = AuxPipeline(RtAux::ReconstructGuides);
            if (pipeline == nullptr)
            {
                SetProblem("this build has no Ray Reconstruction guide pass");
                list->Close();
                return;
            }
            UseLocked({ &guides.albedo, &guides.specularAlbedo, &guides.normalRoughness, &guides.hitDistance },
                      D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
            UINT used = 0;
            const View reads[8] = { Of(specular) };
            const View writes[4] = { Of(&guides.albedo), Of(&guides.specularAlbedo), Of(&guides.normalRoughness),
                                     Of(&guides.hitDistance) };
            const D3D12_GPU_DESCRIPTOR_HANDLE guideTable = TableLocked(slotNumber, used, reads, writes);
            list->SetPipelineState(pipeline);
            BindTablesLocked(guideTable);
            list->Dispatch((width + 7) / 8, (height + 7) / 8, 1);
            // Streamline takes every resource in the state it is given and leaves it so;
            // NGX takes its inputs in this one.
            UseLocked({ &guides.albedo, &guides.specularAlbedo, &guides.normalRoughness, &guides.hitDistance },
                      D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
        }

        list->EndQuery(timestamps.Get(), D3D12_QUERY_TYPE_TIMESTAMP, timestampBase + kTimestampRrGuides);
        reconstructionEvaluated = true;
        std::string notEvaluated = "Ray Reconstruction did not evaluate (the proxy's log has Streamline's reason)";
        const bool evaluated = route == Route::Ngx ? EvaluateReconstructNgxLocked(frame, back, notEvaluated)
                                                   : EvaluateReconstructStreamlineLocked(frame, back);
        // Timed where it evaluated; WaitForSlotLocked reads the three stamps once the slot's
        // list is done.
        list->EndQuery(timestamps.Get(), D3D12_QUERY_TYPE_TIMESTAMP, timestampBase + kTimestampRrEnd);
        list->ResolveQueryData(timestamps.Get(), D3D12_QUERY_TYPE_TIMESTAMP, timestampBase, kTimestampsPerSlot,
                               timestampReadback.Get(), static_cast<UINT64>(timestampBase) * sizeof(UINT64));
        slot.timed = evaluated;
        slot.extra = false;
        slot.reconstruction = evaluated;
        const HRESULT hr = list->Close();
        if (FAILED(hr))
        {
            // None of it runs, its recorded states neither: everything made goes, as
            // SubmitLocked does.
            SetProblem("the command list could not be closed (" + Hr(hr) + ")");
            ReleaseLocked();
            return;
        }
        ID3D12CommandList* lists[] = { list.Get() };
        queue->ExecuteCommandLists(1, lists);
        const UINT64 finished = ++fenceValue;
        queue->Signal(fence12.Get(), finished);
        slot.done = finished;
        for (Shared* texture : shared)
            if (texture != nullptr)
                texture->lastFence = finished;
        if (!evaluated)
        {
            SetProblem(notEvaluated);
            return;
        }
        context11->Wait(fence11.Get(), finished);
        GpuProfiler::Get().OnMarker(kProfRrEvaluate);
        context11->CopyResource(static_cast<ID3D11Resource*>(packet->output), frame.output->texture.d3d11.Get());
        GpuProfiler::Get().OnMarker(kProfRrOutput);

        std::lock_guard<std::mutex> status(statusLock);
        ++reconstructed;
    }

    bool Raytracing::EvaluateReconstructNgxLocked(const ReconstructFrame& frame, std::vector<D3D12_RESOURCE_BARRIER>& back,
                                                  std::string& notEvaluated)
    {
        const RtReconstructPacket* packet = frame.packet;
        const UINT width = frame.width, height = frame.height;
        reconstructionThroughNgx = true;
        // "Input buffers ... For D3D12 - the state must be
        // D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE", "The Output buffer be in UAV
        // state", and "After the evaluate call, DLSS ... always transitions buffers
        // back to these known states" (DLSS Programming Guide 3.4): the shared inputs
        // stay where the guide pass read them, the output goes to UAV and back, and
        // all of them to COMMON after it, for Direct3D 11.
        const D3D12_RESOURCE_BARRIER written =
            TransitionBarrier(frame.output->texture.d3d12.Get(), D3D12_RESOURCE_STATE_COMMON,
                              D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
        list->ResourceBarrier(1, &written);
        back.push_back(TransitionBarrier(frame.output->texture.d3d12.Get(), D3D12_RESOURCE_STATE_UNORDERED_ACCESS,
                                         D3D12_RESOURCE_STATE_COMMON));

        NgxRrFrame ngx = {};
        ngx.colour = frame.colour->texture.d3d12.Get();
        ngx.output = frame.output->texture.d3d12.Get();
        ngx.depth = frame.depth->texture.d3d12.Get();
        ngx.motion = frame.motion->texture.d3d12.Get();
        ngx.albedo = guides.albedo.resource.Get();
        ngx.specularAlbedo = guides.specularAlbedo.resource.Get();
        ngx.normalRoughness = guides.normalRoughness.resource.Get();
        ngx.specularHitDistance = guides.hitDistance.resource.Get();
        ngx.renderWidth = width;
        ngx.renderHeight = height;
        ngx.outputWidth = packet->outputWidth;
        ngx.outputHeight = packet->outputHeight;
        // "SL DLSSDContext modes start with 'off' so subtract one, the rest is mapped
        // 1:1" (sl.dlss_d); its presets are NGX's values.
        ngx.quality = static_cast<int32_t>(packet->mode) - 1;
        ngx.preset = packet->preset;
        ngx.jitterX = packet->jitter[0];
        ngx.jitterY = packet->jitter[1];
        // Streamline's mvecScale times the render size, as sl.dlss_d hands it to NGX.
        ngx.motionScaleX = packet->motionScale[0] * static_cast<float>(width);
        ngx.motionScaleY = packet->motionScale[1] * static_cast<float>(height);
        ngx.reset = (packet->flags & kRtFrameReset) != 0;
        memcpy(ngx.worldToView, packet->worldToView, sizeof(ngx.worldToView));
        memcpy(ngx.viewToClip, packet->viewToClip, sizeof(ngx.viewToClip));
        std::string ngxProblem;
        const bool evaluated = DlssRayReconstruction::Get().Evaluate(device.Get(), list.Get(), ngx,
                                                                     [this]() { WaitForAllLocked(); }, ngxProblem);
        if (!evaluated)
            notEvaluated = "Ray Reconstruction through NGX: " + ngxProblem;
        // "NGX modifies the Vulkan and D3D12 command list states" (DLSS Programming
        // Guide 5.4): only barriers follow, which need none of it.
        list->ResourceBarrier(static_cast<UINT>(back.size()), back.data());
        return evaluated;
    }

    // The frame handed to Streamline's sl.dlss_d with its resources tagged (the NGX route above does the
    // same through NGX's own call).
    bool Raytracing::EvaluateReconstructStreamlineLocked(const ReconstructFrame& frame,
                                                         std::vector<D3D12_RESOURCE_BARRIER>& back)
    {
        const RtReconstructPacket* packet = frame.packet;
        const UINT width = frame.width, height = frame.height;
        Shared* const colour = frame.colour;
        Shared* const depth = frame.depth;
        Shared* const motion = frame.motion;
        Shared* const output = frame.output;
        reconstructionThroughNgx = false;
        if (!back.empty())
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
        // "DLSS-RR only support HDR inputs" (ProgrammingGuideDLSS_RR.md): the mod sends
        // HDR frames only (UpscalerRig.SubmitReconstruction).
        options.colorBuffersHDR = sl::Boolean::eTrue;
        options.normalRoughnessMode = sl::DLSSDNormalRoughnessMode::ePacked;
        options.worldToCameraView = Matrix(packet->worldToView);
        options.cameraViewToWorld = Matrix(packet->viewToWorld);
        const sl::DLSSDPreset preset = static_cast<sl::DLSSDPreset>(packet->preset);
        options.dlaaPreset = options.qualityPreset = options.balancedPreset = options.performancePreset =
            options.ultraPerformancePreset = options.ultraQualityPreset = preset;

        return Streamline::Get().EvaluateRayReconstruction(slConstants, options, tags,
                                                           static_cast<uint32_t>(sizeof(tags) / sizeof(tags[0])), list.Get());
    }

    // The mod stopped sending Ray Reconstruction frames (UpscalerRig.Reconstruction).
    // The lists that evaluated it finish first, as slFreeResources asks for pending
    // evaluations (Streamline::FreeRayReconstruction) and as a handle NGX releases is
    // gone ("after this call, it is invalid to use the provided handle", nvsdk_ngx.h,
    // NVSDK_NGX_ReleaseFeature). It runs on the render thread, after the last of its frames.
    void Raytracing::OnReconstructRelease()
    {
        std::lock_guard<std::mutex> lock(mutex);
        if (!reconstructionEvaluated)
            return;
        reconstructionEvaluated = false;
        if (fence12 != nullptr && fenceEvent != nullptr && fence12->GetCompletedValue() < fenceValue
            && SUCCEEDED(fence12->SetEventOnCompletion(fenceValue, fenceEvent)))
            WaitForSingleObject(fenceEvent, INFINITE);
        const bool freed = reconstructionThroughNgx ? DlssRayReconstruction::Get().ReleaseFeature()
                                                    : Streamline::Get().FreeRayReconstruction();
        if (freed)
            LogLine("DLSS Ray Reconstruction: no longer used, its resources freed");
    }
}
