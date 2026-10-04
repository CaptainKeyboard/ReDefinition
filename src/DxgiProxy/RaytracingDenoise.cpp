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
        constexpr UINT kAtrousSteps = 5;
        // The tables of one frame: the preparation, two per signal, one per à-trous step.
        constexpr UINT kDenoiserTables = 1 + 2 * kRtDenoisedSignals + kAtrousSteps;
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
                const D3D12_RESOURCE_BARRIER barrier = UavBarrier(target.resource.Get());
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
        if (denoise.width == width && denoise.height == height && denoise.work.resource != nullptr)
            return true;
        // The textures of the old size may still be in use by lists in flight.
        const auto hold = [&](DenoiseTarget& target)
        {
            if (target.resource != nullptr)
                slot.held.push_back(target.resource);
        };
        for (UINT s = 0; s < kRtDenoisedSignals; ++s)
        {
            hold(denoise.noisy[s]);
            hold(denoise.workHalf[s]);
            for (UINT p = 0; p < 2; ++p)
            {
                hold(denoise.history[s][p]);
                hold(denoise.moments[s][p]);
            }
        }
        for (UINT p = 0; p < 2; ++p)
        {
            hold(denoise.linearDepth[p]);
            hold(denoise.normal[p]);
        }
        hold(denoise.work);
        denoise = DenoiseTargets{};
        // The history and the filter's work up to it in full floats: a history of half
        // floats, blended with a small weight each frame, stops short of its value where the
        // step falls under half a half float's precision (1.6 % short at 1.0, measured). The
        // à-trous steps after the first, which no history takes, in half floats.
        bool ok = true;
        const DXGI_FORMAT colour = DXGI_FORMAT_R16G16B16A16_FLOAT;
        const DXGI_FORMAT precise = DXGI_FORMAT_R32G32B32A32_FLOAT;
        for (UINT s = 0; s < kRtDenoisedSignals && ok; ++s)
        {
            ok = ok && denoise.noisy[s].Create(device.Get(), width, height, colour)
                 && denoise.workHalf[s].Create(device.Get(), width, height, colour);
            for (UINT p = 0; p < 2 && ok; ++p)
                ok = ok && denoise.history[s][p].Create(device.Get(), width, height, precise)
                     && denoise.moments[s][p].Create(device.Get(), width, height, colour);
        }
        ok = ok && denoise.work.Create(device.Get(), width, height, precise);
        for (UINT p = 0; p < 2 && ok; ++p)
            ok = ok && denoise.linearDepth[p].Create(device.Get(), width, height, DXGI_FORMAT_R32_FLOAT)
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
        // Tables taken from the start stay below the fixed ones.
        if (used < kFixedTables && used + kTableSize > kFixedTables)
            SetProblem("the denoiser's descriptor tables reach the fixed ones");
        const UINT base = kSlotTables + slotNumber * kDenoiseDescriptors + used;
        used += kTableSize;
        D3D12_CPU_DESCRIPTOR_HANDLE cpu = heap->GetCPUDescriptorHandleForHeapStart();
        cpu.ptr += static_cast<SIZE_T>(base) * increment;
        D3D12_GPU_DESCRIPTOR_HANDLE gpu = heap->GetGPUDescriptorHandleForHeapStart();
        gpu.ptr += static_cast<UINT64>(base) * increment;
        for (const View& read : reads)
        {
            const D3D12_SHADER_RESOURCE_VIEW_DESC srv = SrvDesc(read.format);
            device->CreateShaderResourceView(read.resource, &srv, cpu);
            cpu.ptr += increment;
        }
        for (const View& write : writes)
        {
            const D3D12_UNORDERED_ACCESS_VIEW_DESC uav = UavDesc(write.format);
            device->CreateUnorderedAccessView(write.resource, nullptr, &uav, cpu);
            cpu.ptr += increment;
        }
        return gpu;
    }

    void Raytracing::BindTablesLocked(D3D12_GPU_DESCRIPTOR_HANDLE table)
    {
        list->SetComputeRootDescriptorTable(10, table);
        D3D12_GPU_DESCRIPTOR_HANDLE writeTable = table;
        writeTable.ptr += static_cast<UINT64>(8) * increment;
        list->SetComputeRootDescriptorTable(11, writeTable);
    }

    void Raytracing::RunDenoiserLocked(const RtFramePacket& packet, UINT slotNumber, Shared* const* outputs,
                                       Shared* motionInput)
    {
        static_assert(kDenoiserTables * kTableSize <= kFixedTables, "the denoiser's tables reach the fixed ones");
        const auto pipeline = [&](const char* name) -> ID3D12PipelineState*
        {
            auto found = denoisePipelines.find(name);
            return found != denoisePipelines.end() ? found->second.Get() : nullptr;
        };
        ID3D12PipelineState* prepare = pipeline("RT_DenoisePrepare");
        ID3D12PipelineState* temporal = pipeline("RT_DenoiseTemporal");
        ID3D12PipelineState* variance = pipeline("RT_DenoiseVariance");
        ID3D12PipelineState* atrous = pipeline("RT_DenoiseAtrous");
        if (prepare == nullptr || temporal == nullptr || variance == nullptr || atrous == nullptr)
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
            BindTablesLocked(table);
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

        // Temporal and variance for each signal: the noisy signal and its history into the
        // work texture, the moments into this frame's; then into the history of the frame
        // before, which the temporal pass has read by then, as the first à-trous step's input.
        // A signal the last run did not filter starts afresh: its history is older.
        uint32_t present = 0;
        for (UINT s = 0; s < kRtDenoisedSignals; ++s)
        {
            const RtDenoisedSignal& filtered = kRtDenoised[s];
            if ((packet.signals & filtered.signal) == 0 || outputs[filtered.output] == nullptr)
                continue;
            present |= 1u << s;
            DenoiseTarget& noisy = denoise.noisy[s];
            DenoiseTarget& historyBefore = denoise.history[s][before];
            DenoiseTarget& moments = denoise.moments[s][now];
            DenoiseTarget& momentsBefore = denoise.moments[s][before];

            UseLocked(noisy, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
            UseLocked(historyBefore, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
            UseLocked(momentsBefore, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
            UseLocked(denoise.work, D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
            UseLocked(moments, D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
            {
                const View reads[8] = { Of(&noisy), Of(&historyBefore), Of(&momentsBefore), Of(&denoise.linearDepth[now]),
                                        Of(&denoise.normal[now]), Of(&denoise.linearDepth[before]),
                                        Of(&denoise.normal[before]), motion ? Of(motionInput) : View{} };
                const View writes[4] = { Of(&denoise.work), Of(&moments) };
                const bool afresh = reset || (denoise.lastPresent & (1u << s)) == 0;
                constants(1, filtered.kind, (afresh ? 1u : 0u) | (motion ? 2u : 0u), 0);
                dispatch(temporal, TableLocked(slotNumber, used, reads, writes));
            }

            UseLocked(denoise.work, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
            UseLocked(moments, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
            UseLocked(historyBefore, D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
            {
                const View reads[8] = { Of(&denoise.work), Of(&moments), View{}, Of(&denoise.linearDepth[now]),
                                        Of(&denoise.normal[now]) };
                const View writes[4] = { Of(&historyBefore) };
                constants(1, filtered.kind, 0, 0);
                dispatch(variance, TableLocked(slotNumber, used, reads, writes));
            }
        }

        // À-trous, all signals in each step: the first step into this frame's history; the
        // steps after it between the noisy texture, read by now, and the half-float work
        // texture; the last into Unity's texture.
        DenoiseTarget* from[kRtDenoisedSignals] = {};
        for (UINT s = 0; s < kRtDenoisedSignals; ++s)
            from[s] = &denoise.history[s][before];
        for (UINT step = 0; present != 0 && step < kAtrousSteps; ++step)
        {
            const bool last = step + 1 == kAtrousSteps;
            View reads[8] = {};
            View writes[4] = {};
            DenoiseTarget* to[kRtDenoisedSignals] = {};
            for (UINT s = 0; s < kRtDenoisedSignals; ++s)
            {
                if ((present & (1u << s)) == 0)
                    continue;
                to[s] = step == 0 ? &denoise.history[s][now] : from[s] == &denoise.noisy[s] ? &denoise.workHalf[s]
                                                                                          : &denoise.noisy[s];
                UseLocked(*from[s], D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
                if (!last)
                    UseLocked(*to[s], D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
                // The signals in t0, t1, t2 and t5, beside the depth and normal in t3 and t4.
                reads[s == 3 ? 5 : s] = Of(from[s]);
                writes[s] = last ? Of(outputs[kRtDenoised[s].output]) : Of(to[s]);
            }
            reads[3] = Of(&denoise.linearDepth[now]);
            reads[4] = Of(&denoise.normal[now]);
            constants(1u << step, present, last ? 4u : 0u, step);
            dispatch(atrous, TableLocked(slotNumber, used, reads, writes));
            for (UINT s = 0; s < kRtDenoisedSignals; ++s)
                from[s] = to[s];
        }

        denoise.parity = before;
        denoise.fresh = false;
        denoise.lastPresent = present;
    }
}
