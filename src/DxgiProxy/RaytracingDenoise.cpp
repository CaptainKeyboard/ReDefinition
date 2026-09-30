// ReDefinition's own denoiser for the raytraced signals (RaytracingShaders/Denoise.hlsl):
// its textures on the proxy's Direct3D 12 device, and the passes of one frame.

#include "Raytracing.h"

#include "D3d12Util.h"
#include "Log.h"

using Microsoft::WRL::ComPtr;

namespace redefinition
{
    namespace
    {
        // The signals the denoiser filters, by output slot: sun visibility and
        // occlusion as a scalar, diffuse light and reflections as a colour.
        struct Filtered
        {
            uint32_t signal;
            uint32_t output;
            uint32_t kind;
        };
        const Filtered kFiltered[] = {
            { kRtSignalSunVisibility, 0, 0 },
            { kRtSignalDiffuse, 1, 1 },
            { kRtSignalSpecular, 2, 1 },
            { kRtSignalAmbientOcclusion, 3, 0 },
        };

        constexpr UINT kAtrousSteps = 5;
    }

    bool Raytracing::DenoiseTarget::Create(ID3D12Device* device, UINT width, UINT height, DXGI_FORMAT newFormat)
    {
        D3D12_HEAP_PROPERTIES heapProperties = {};
        heapProperties.Type = D3D12_HEAP_TYPE_DEFAULT;
        D3D12_RESOURCE_DESC desc = {};
        desc.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
        desc.Width = width;
        desc.Height = height;
        desc.DepthOrArraySize = 1;
        desc.MipLevels = 1;
        desc.Format = newFormat;
        desc.SampleDesc.Count = 1;
        desc.Flags = D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS;
        resource.Reset();
        format = newFormat;
        state = D3D12_RESOURCE_STATE_UNORDERED_ACCESS;
        return SUCCEEDED(device->CreateCommittedResource(&heapProperties, D3D12_HEAP_FLAG_NONE, &desc, state, nullptr,
                                                         IID_PPV_ARGS(&resource)));
    }

    void Raytracing::UseLocked(DenoiseTarget& target, D3D12_RESOURCE_STATES wanted)
    {
        if (target.state == wanted)
        {
            if (wanted == D3D12_RESOURCE_STATE_UNORDERED_ACCESS)
            {
                D3D12_RESOURCE_BARRIER barrier = {};
                barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_UAV;
                barrier.UAV.pResource = target.resource.Get();
                list->ResourceBarrier(1, &barrier);
            }
            return;
        }
        const D3D12_RESOURCE_BARRIER barrier = TransitionBarrier(target.resource.Get(), target.state, wanted);
        list->ResourceBarrier(1, &barrier);
        target.state = wanted;
    }

    bool Raytracing::EnsureDenoiseTargetsLocked(UINT width, UINT height, Slot& slot)
    {
        if (denoise.width == width && denoise.height == height && denoise.work[0].resource != nullptr)
            return true;
        // The textures of the old size may still be in use by lists in flight.
        const auto hold = [&](DenoiseTarget& target)
        {
            if (target.resource != nullptr)
                slot.held.push_back(target.resource);
        };
        for (UINT s = 0; s < 4; ++s)
        {
            hold(denoise.noisy[s]);
            for (UINT p = 0; p < 2; ++p)
            {
                hold(denoise.history[s][p]);
                hold(denoise.moments[s][p]);
            }
        }
        for (UINT p = 0; p < 2; ++p)
        {
            hold(denoise.work[p]);
            hold(denoise.linearDepth[p]);
            hold(denoise.normal[p]);
        }
        denoise = DenoiseTargets{};
        // The history and the filter's work in full floats: a history of half floats,
        // blended with a small weight each frame, stops short of its value where the step
        // falls under half a half float's precision (1.6 % short at 1.0, measured).
        bool ok = true;
        const DXGI_FORMAT colour = DXGI_FORMAT_R16G16B16A16_FLOAT;
        const DXGI_FORMAT precise = DXGI_FORMAT_R32G32B32A32_FLOAT;
        for (UINT s = 0; s < 4 && ok; ++s)
        {
            ok = ok && denoise.noisy[s].Create(device.Get(), width, height, colour);
            for (UINT p = 0; p < 2 && ok; ++p)
                ok = ok && denoise.history[s][p].Create(device.Get(), width, height, precise)
                     && denoise.moments[s][p].Create(device.Get(), width, height, colour);
        }
        for (UINT p = 0; p < 2 && ok; ++p)
            ok = ok && denoise.work[p].Create(device.Get(), width, height, precise)
                 && denoise.linearDepth[p].Create(device.Get(), width, height, DXGI_FORMAT_R32_FLOAT)
                 && denoise.normal[p].Create(device.Get(), width, height, colour);
        if (!ok)
        {
            denoise = DenoiseTargets{};
            SetProblem("the denoiser's textures could not be made");
            return false;
        }
        denoise.width = width;
        denoise.height = height;
        denoise.fresh = true;
        return true;
    }

    D3D12_GPU_DESCRIPTOR_HANDLE Raytracing::TableLocked(UINT slotNumber, UINT& used, const View (&reads)[8],
                                                         const View (&writes)[4])
    {
        const UINT base = kSlots * kFrameDescriptors + 2 * kMaxMeshes + 1 + slotNumber * kDenoiseDescriptors + used;
        used += kTableSize;
        D3D12_CPU_DESCRIPTOR_HANDLE cpu = heap->GetCPUDescriptorHandleForHeapStart();
        cpu.ptr += static_cast<SIZE_T>(base) * increment;
        D3D12_GPU_DESCRIPTOR_HANDLE gpu = heap->GetGPUDescriptorHandleForHeapStart();
        gpu.ptr += static_cast<UINT64>(base) * increment;
        for (const View& read : reads)
        {
            D3D12_SHADER_RESOURCE_VIEW_DESC srv = {};
            srv.Format = read.format;
            srv.ViewDimension = D3D12_SRV_DIMENSION_TEXTURE2D;
            srv.Shader4ComponentMapping = D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING;
            srv.Texture2D.MipLevels = 1;
            device->CreateShaderResourceView(read.resource, &srv, cpu);
            cpu.ptr += increment;
        }
        for (const View& write : writes)
        {
            D3D12_UNORDERED_ACCESS_VIEW_DESC uav = {};
            uav.Format = write.format;
            uav.ViewDimension = D3D12_UAV_DIMENSION_TEXTURE2D;
            device->CreateUnorderedAccessView(write.resource, nullptr, &uav, cpu);
            cpu.ptr += increment;
        }
        return gpu;
    }

    void Raytracing::RunDenoiserLocked(const RtFramePacket& packet, UINT slotNumber, Shared* const* outputs,
                                       Shared* motionInput)
    {
        const auto pipeline = [&](const char* name) -> ID3D12PipelineState*
        {
            auto found = denoisePipelines.find(name);
            return found != denoisePipelines.end() ? found->second.Get() : nullptr;
        };
        ID3D12PipelineState* prepare = pipeline("RT_DenoisePrepare");
        ID3D12PipelineState* temporal = pipeline("RT_DenoiseTemporal");
        ID3D12PipelineState* variance = pipeline("RT_DenoiseVariance");
        ID3D12PipelineState* atrous = pipeline("RT_DenoiseAtrous");
        ID3D12PipelineState* resolve = pipeline("RT_DenoiseResolve");
        if (prepare == nullptr || temporal == nullptr || variance == nullptr || atrous == nullptr || resolve == nullptr)
            return;

        const UINT groupsX = (packet.width + 7) / 8;
        const UINT groupsY = (packet.height + 7) / 8;
        const UINT now = denoise.parity;
        const UINT before = 1 - now;
        const bool reset = denoise.fresh || (packet.flags & kRtFrameReset) != 0;
        const bool motion = motionInput != nullptr && (packet.flags & kRtFrameMotionVectors) != 0;
        UINT used = 0;
        const auto constants = [&](uint32_t step, uint32_t kind, uint32_t flags, uint32_t iteration)
        {
            const uint32_t values[4] = { step, kind, flags, iteration };
            list->SetComputeRoot32BitConstants(9, 4, values, 0);
        };
        const auto dispatch = [&](ID3D12PipelineState* state, D3D12_GPU_DESCRIPTOR_HANDLE table)
        {
            list->SetPipelineState(state);
            list->SetComputeRootDescriptorTable(10, table);
            D3D12_GPU_DESCRIPTOR_HANDLE writeTable = table;
            writeTable.ptr += static_cast<UINT64>(8) * increment;
            list->SetComputeRootDescriptorTable(11, writeTable);
            list->Dispatch(groupsX, groupsY, 1);
        };

        // This frame's view distance and normal, for this frame and the next.
        UseLocked(denoise.linearDepth[now], D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
        UseLocked(denoise.normal[now], D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
        {
            const View reads[8] = {};
            const View writes[4] = { Of(&denoise.linearDepth[now]), Of(&denoise.normal[now]) };
            constants(1, 0, 0, 0);
            dispatch(prepare, TableLocked(slotNumber, used, reads, writes));
        }
        UseLocked(denoise.linearDepth[now], D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
        UseLocked(denoise.normal[now], D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
        UseLocked(denoise.linearDepth[before], D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
        UseLocked(denoise.normal[before], D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);

        for (UINT s = 0; s < 4; ++s)
        {
            const Filtered& filtered = kFiltered[s];
            Shared* output = outputs[filtered.output];
            if ((packet.signals & filtered.signal) == 0 || output == nullptr)
                continue;
            DenoiseTarget& noisy = denoise.noisy[s];
            DenoiseTarget& history = denoise.history[s][now];
            DenoiseTarget& historyBefore = denoise.history[s][before];
            DenoiseTarget& moments = denoise.moments[s][now];
            DenoiseTarget& momentsBefore = denoise.moments[s][before];
            DenoiseTarget* work[2] = { &denoise.work[0], &denoise.work[1] };

            // Temporal: noisy and the history into work 0, the moments into this frame's.
            UseLocked(noisy, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
            UseLocked(historyBefore, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
            UseLocked(momentsBefore, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
            UseLocked(*work[0], D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
            UseLocked(moments, D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
            {
                const View reads[8] = { Of(&noisy), Of(&historyBefore), Of(&momentsBefore), Of(&denoise.linearDepth[now]),
                                        Of(&denoise.normal[now]), Of(&denoise.linearDepth[before]),
                                        Of(&denoise.normal[before]), motion ? Of(motionInput) : View{} };
                const View writes[4] = { Of(work[0]), Of(&moments) };
                constants(1, filtered.kind, (reset ? 1u : 0u) | (motion ? 2u : 0u), 0);
                dispatch(temporal, TableLocked(slotNumber, used, reads, writes));
            }

            // Variance: work 0 and the moments into work 1.
            UseLocked(*work[0], D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
            UseLocked(moments, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
            UseLocked(*work[1], D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
            {
                const View reads[8] = { Of(work[0]), Of(&moments), View{}, Of(&denoise.linearDepth[now]),
                                        Of(&denoise.normal[now]) };
                const View writes[4] = { Of(work[1]) };
                constants(1, filtered.kind, 0, 0);
                dispatch(variance, TableLocked(slotNumber, used, reads, writes));
            }

            // À-trous, work 1 and 0 in turn; the first step's result is the history.
            UINT current = 1;
            for (UINT step = 0; step < kAtrousSteps; ++step)
            {
                DenoiseTarget& from = *work[current];
                DenoiseTarget& to = *work[1 - current];
                UseLocked(from, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
                UseLocked(to, D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
                const View reads[8] = { Of(&from), View{}, View{}, Of(&denoise.linearDepth[now]), Of(&denoise.normal[now]) };
                const View writes[4] = { Of(&to) };
                constants(1u << step, filtered.kind, 0, step);
                dispatch(atrous, TableLocked(slotNumber, used, reads, writes));
                if (step == 0)
                {
                    UseLocked(to, D3D12_RESOURCE_STATE_COPY_SOURCE);
                    UseLocked(history, D3D12_RESOURCE_STATE_COPY_DEST);
                    list->CopyResource(history.resource.Get(), to.resource.Get());
                }
                current = 1 - current;
            }

            // Resolve into Unity's texture.
            DenoiseTarget& last = *work[current];
            UseLocked(last, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
            {
                const View reads[8] = { Of(&last), View{}, View{}, Of(&denoise.linearDepth[now]) };
                const View writes[4] = { Of(output) };
                constants(1, filtered.kind, 0, 0);
                dispatch(resolve, TableLocked(slotNumber, used, reads, writes));
            }
        }

        denoise.parity = before;
        denoise.fresh = false;
    }
}
