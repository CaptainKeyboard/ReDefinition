// ReDefinition's own denoiser for the raytraced signals (Raytracing/Shaders/Denoise.hlsl):
// its textures on the proxy's Direct3D 12 device, and the passes of one frame.

#include "Raytracing.h"

#include <algorithm>

#include "Config.h"
#include "D3d12Util.h"
#include "Log.h"

using Microsoft::WRL::ComPtr;

namespace redefinition
{
    namespace
    {
        // The à-trous filter's steps a frame, each at twice the spacing of the one before.
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

    void Raytracing::DropDenoiseTargetLocked(DenoiseTarget& target, Slot& slot)
    {
        // A texture may still be in use by lists in flight: held by the slot until it is
        // done.
        if (target.resource != nullptr)
            slot.held.push_back(target.resource);
        target = DenoiseTarget{};
    }

    void Raytracing::DropDenoiseTargetsLocked(Slot& slot)
    {
        for (UINT s = 0; s < kRtDenoisedSignals; ++s)
        {
            DropDenoiseTargetLocked(denoise.noisy[s], slot);
            DropDenoiseTargetLocked(denoise.workHalf[s], slot);
            for (UINT p = 0; p < 2; ++p)
            {
                DropDenoiseTargetLocked(denoise.history[s][p], slot);
                DropDenoiseTargetLocked(denoise.moments[s][p], slot);
            }
        }
        for (UINT p = 0; p < 2; ++p)
        {
            DropDenoiseTargetLocked(denoise.linearDepth[p], slot);
            DropDenoiseTargetLocked(denoise.normal[p], slot);
        }
        DropDenoiseTargetLocked(denoise.work, slot);
        denoise = DenoiseTargets{};
    }

    bool Raytracing::EnsureDenoiseTargetsLocked(UINT width, UINT height, uint32_t noisy, Slot& slot)
    {
        // The noisy textures of the signals the passes trace for a denoiser (noisy, by
        // their place in kRtDenoised), the ones of the signals no longer traced for one
        // let go; the filter's own come with the run that takes them
        // (EnsureFilterTargetsLocked). Another size lets all of them go.
        denoise.framesWithout = 0;
        if (denoise.width != width || denoise.height != height)
        {
            DropDenoiseTargetsLocked(slot);
            denoise.width = width;
            denoise.height = height;
        }
        bool ok = true;
        for (UINT s = 0; s < kRtDenoisedSignals && ok; ++s)
        {
            DenoiseTarget& target = denoise.noisy[s];
            if ((noisy & (1u << s)) == 0)
                DropDenoiseTargetLocked(target, slot);
            else if (target.resource == nullptr)
                ok = target.Create(device.Get(), width, height, DXGI_FORMAT_R16G16B16A16_FLOAT);
        }
        if (!ok)
        {
            DropDenoiseTargetsLocked(slot);
            SetProblem("the denoiser's textures could not be made");
            return false;
        }
        return true;
    }

    bool Raytracing::EnsureFilterTargetsLocked(uint32_t present, Slot& slot)
    {
        // Per signal ReDefinition's denoiser filters (present, by its place in kRtDenoised):
        // its history and moments for this frame and the one before, and its texture in
        // half floats; for any of them the work texture and the view distance and normal
        // for this frame and the one before. The ones it no longer filters go. A signal
        // whose history is new starts afresh (lastPresent), and new view distances and
        // normals start them all afresh (fresh).
        //
        // The history and the filter's work up to it in full floats: a history of half
        // floats, blended with a small weight each frame, stops short of its value where the
        // step falls under half a half float's precision (1.6 % short at 1.0, measured). The
        // à-trous steps after the first, which no history takes, in half floats.
        const DXGI_FORMAT colour = DXGI_FORMAT_R16G16B16A16_FLOAT;
        const DXGI_FORMAT precise = DXGI_FORMAT_R32G32B32A32_FLOAT;
        const auto ensure = [&](DenoiseTarget& target, bool wanted, DXGI_FORMAT format, bool& made)
        {
            if (!wanted)
            {
                DropDenoiseTargetLocked(target, slot);
                return true;
            }
            if (target.resource != nullptr)
                return true;
            made = true;
            return target.Create(device.Get(), denoise.width, denoise.height, format);
        };
        bool ok = true;
        for (UINT s = 0; s < kRtDenoisedSignals && ok; ++s)
        {
            const bool wanted = (present & (1u << s)) != 0;
            bool made = false;
            ok = ensure(denoise.workHalf[s], wanted, colour, made);
            for (UINT p = 0; p < 2 && ok; ++p)
                ok = ensure(denoise.history[s][p], wanted, precise, made) && ensure(denoise.moments[s][p], wanted, colour, made);
            if (made)
                denoise.lastPresent &= ~(1u << s);
        }
        bool madeShared = false;
        ok = ok && ensure(denoise.work, present != 0, precise, madeShared);
        for (UINT p = 0; p < 2 && ok; ++p)
            ok = ensure(denoise.linearDepth[p], present != 0, DXGI_FORMAT_R32_FLOAT, madeShared)
                 && ensure(denoise.normal[p], present != 0, colour, madeShared);
        if (madeShared)
            denoise.fresh = true;
        if (!ok)
        {
            DropDenoiseTargetsLocked(slot);
            SetProblem("the denoiser's textures could not be made");
            return false;
        }
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

        // The signals it filters, each with its noisy texture from this frame's passes
        // (TakeNoisyTargetsLocked), and their textures; none, and nothing runs.
        uint32_t present = 0;
        for (UINT s = 0; s < kRtDenoisedSignals; ++s)
            if ((packet.signals & kRtDenoised[s].signal) != 0 && outputs[kRtDenoised[s].output] != nullptr
                && denoise.noisy[s].resource != nullptr)
                present |= 1u << s;
        if (!EnsureFilterTargetsLocked(present, slots[slotNumber]) || present == 0)
        {
            denoise.lastPresent = 0;
            return;
        }

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
        for (UINT s = 0; s < kRtDenoisedSignals; ++s)
        {
            const RtDenoisedSignal& filtered = kRtDenoised[s];
            if ((present & (1u << s)) == 0)
                continue;
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
                // Bits 8 to 13, for the sun's visibility: its longest history (rtSunHistory).
                const uint32_t history = filtered.signal == kRtSignalSunVisibility
                                             ? static_cast<uint32_t>(std::clamp(Config().rtSunHistory, 1, 32)) << 8
                                             : 0u;
                constants(1, filtered.kind, (afresh ? 1u : 0u) | (motion ? 2u : 0u) | history, 0);
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
            // Bits 8 to 11: how many steps filter the sun's visibility (rtSunAtrousSteps).
            const uint32_t sunSteps = static_cast<uint32_t>(std::clamp(Config().rtSunAtrousSteps, 0, 15)) << 8;
            constants(1u << step, present, (last ? 4u : 0u) | sunSteps, step);
            dispatch(atrous, TableLocked(slotNumber, used, reads, writes));
            for (UINT s = 0; s < kRtDenoisedSignals; ++s)
                from[s] = to[s];
        }

        denoise.parity = before;
        denoise.fresh = false;
        denoise.lastPresent = present;
    }
}
