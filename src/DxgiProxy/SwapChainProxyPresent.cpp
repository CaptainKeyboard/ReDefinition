// The proxy's present path (SwapChainProxy): Unity's frame copied into the
// D3D12 backbuffer and presented, frame generation prepared under the same
// lock -- FSR's, DLSS-G's or XeSS-FG's -- and the fences between D3D11, D3D12 and
// DLSS-G's queue.

#include "WaitStats.h"
#include "GpuProfiler.h"
#include "SwapChainProxy.h"

#include "Config.h"
#include "D3d12Util.h"
#include "FrameGeneration.h"
#include "Log.h"
#include "LowLatency.h"
#include "Streamline.h"
#include "SwapChainProxyState.h"
#include "XessFg.h"

#include <string>

using Microsoft::WRL::ComPtr;

namespace redefinition
{
    HRESULT SwapChainProxy::WaitForUnityWork()
    {
        // Unity's work has been recorded but not necessarily submitted. Flush
        // pushes it to the driver, and the fence signal that follows marks the
        // point after it. The queue waits for that point rather than the CPU
        // doing it, so the two APIs overlap instead of taking turns.
        d3d11Context->Flush();
        ++sharedFenceValue;
        HRESULT hr = d3d11Context->Signal(sharedFence11.Get(), sharedFenceValue);
        if (SUCCEEDED(hr))
            hr = queue->Wait(sharedFence12.Get(), sharedFenceValue);
        return hr;
    }

    UINT64 SwapChainProxy::SignalFrameDone()
    {
        ++frameFenceValue;
        queue->Signal(frameFence.Get(), frameFenceValue);
        frameFenceValues[allocatorIndex] = frameFenceValue;
        allocatorIndex = (allocatorIndex + 1) % kFrameCount;
        return frameFenceValue;
    }

    HRESULT SwapChainProxy::ResetCommandList()
    {
        // Only stall if this allocator's frame is still in flight. With three
        // allocators that is rare, and it keeps the frame rate comparable to the
        // D3D11 baseline instead of serialising it.
        WaitForFrame(frameFenceValues[allocatorIndex]);

        HRESULT hr = allocators[allocatorIndex]->Reset();
        if (SUCCEEDED(hr))
            hr = commandList->Reset(allocators[allocatorIndex].Get(), nullptr);
        return hr;
    }

    HRESULT SwapChainProxy::CopySharedColourAndExecute()
    {
        // Asked for now, not remembered from last time. With frame generation
        // the swapchain presents frames of its own in between, so a cached index
        // points at the wrong buffer -- the game keeps rendering and the picture
        // stops moving.
        ComPtr<ID3D12Resource> backBuffer;
        HRESULT hr = HasSharedColour()
                         ? swapChain->GetBuffer(swapChain->GetCurrentBackBufferIndex(), IID_PPV_ARGS(&backBuffer))
                         : DXGI_ERROR_INVALID_CALL;
        if (FAILED(hr))
        {
            // Closed as it is: an open list refuses its next Reset, and every later
            // Present would stop there.
            commandList->Close();
            return hr;
        }

        // A resource shared with another API has to be back in COMMON whenever
        // that API touches it, so the transition is taken and given back within
        // the same command list.
        const D3D12_RESOURCE_BARRIER before[] = {
            TransitionBarrier(backBuffer.Get(), D3D12_RESOURCE_STATE_PRESENT, D3D12_RESOURCE_STATE_COPY_DEST),
            TransitionBarrier(sharedColour12.Get(), D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_COPY_SOURCE),
        };
        commandList->ResourceBarrier(2, before);

        commandList->CopyResource(backBuffer.Get(), sharedColour12.Get());

        const D3D12_RESOURCE_BARRIER after[] = {
            TransitionBarrier(backBuffer.Get(), D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_STATE_PRESENT),
            TransitionBarrier(sharedColour12.Get(), D3D12_RESOURCE_STATE_COPY_SOURCE, D3D12_RESOURCE_STATE_COMMON),
        };
        commandList->ResourceBarrier(2, after);

        hr = commandList->Close();
        if (FAILED(hr))
            return hr;

        ID3D12CommandList* lists[] = { commandList.Get() };
        queue->ExecuteCommandLists(1, lists);
        return S_OK;
    }

    // ----------------------------------------------------------------- present

    void SwapChainProxy::WaitForFrame(UINT64 value)
    {
        if (frameFence->GetCompletedValue() >= value)
            return;

        if (SUCCEEDED(frameFence->SetEventOnCompletion(value, frameEvent)))
        {
            WaitTimer timer(WaitKind::PresentFrame);
            WaitForSingleObject(frameEvent, INFINITE);
        }
    }

    // Said once, with the reason: every Present after a removal reaches
    // nothing, and FSR's swapchain goes on returning success.
    void SwapChainProxy::NoteDeviceRemoved()
    {
        // Asked once; stored every time, since a newer proxy's Initialise
        // clears the state and this one's device stays removed.
        if (!loggedDeviceRemoved)
        {
            const HRESULT reason = d3d12Device->GetDeviceRemovedReason();
            removedReason = FAILED(reason) ? reason : DXGI_ERROR_DEVICE_REMOVED;
        }
        g_deviceRemoved.store(removedReason);
        if (loggedDeviceRemoved)
            return;

        loggedDeviceRemoved = true;
        LogLine("D3D12 device removed, reason " + Hr(removedReason)
                + " -- nothing reaches the screen any more; KSP has to be restarted");
    }

    // Every way out of the copy below leaves the frame unpresented: the display
    // keeps what it has, Unity is told the Present failed, and the picture is
    // black or frozen until something else puts it right. A frame that cannot be
    // copied is better shown as the one before it, so the swapchain is presented
    // as it stands and Unity is told the frame went out. The context lock is the
    // copy path's, because Present is where interpolation is requested.
    HRESULT SwapChainProxy::PresentUncopied(UINT syncInterval, UINT flags, const char* why)
    {
        ++uncopiedPresents;
        if (uncopiedPresents == 1 || uncopiedPresents % 600 == 0)
            LogLine(std::string("Frame not copied (") + why + "): presented as it stands, "
                    + std::to_string(uncopiedPresents) + " so far. The picture holds the frame before it.");

        HRESULT hr = S_OK;
        {
            std::lock_guard<std::mutex> fgLock(FrameGeneration::Get().ContextMutex());
            // XeSS-FG off for it: a Present without its present id and inputs would
            // interpolate from none -- "Failure to make this call will result in
            // visible artifacts or XeSS-FG skipping interpolation"
            // (xess_fg_developer_guide_english.md, Execution).
            if (xessSwapChain)
                FrameGeneration::Get().XessOffLocked(why);
            hr = swapChain->Present(syncInterval, flags & ~static_cast<UINT>(DXGI_PRESENT_ALLOW_TEARING));
        }
        // The game's frame went out, if as the one before it.
        CountPresent(0);
        return SUCCEEDED(hr) ? S_OK : hr;
    }

    HRESULT SwapChainProxy::PresentTest(UINT syncInterval, UINT flags)
    {
        if (streamlineNativeSwapChain != nullptr)
            return streamlineNativeSwapChain->Present(syncInterval, flags);
        // FidelityFX's Present is one of the calls its lock covers.
        std::lock_guard<std::mutex> fgLock(FrameGeneration::Get().ContextMutex());
        return swapChain->Present(syncInterval, flags);
    }

    // For the mod's window (KspFgCounters): one more frame rendered, and as
    // many presented as the swapchain counted since the last one -- FSR's
    // own count, generated frames included, trailing a little as its
    // presenter thread presents them. Outside the lock, on the thread that
    // presents.
    void SwapChainProxy::CountPresent(uint32_t framesPresented)
    {
        UINT presents = 0;
        const bool gotPresents = SUCCEEDED(swapChain->GetLastPresentCount(&presents));
        UINT step = gotPresents && haveCountedPresents && presents >= countedPresents
                        ? presents - countedPresents
                        : 1u;
        // DLSS-G's own count: "Number of frames presented since the last
        // 'slDLSSGGetState' call" (sl_dlss_g.h); XeSS-FG's, "Number of frames sent to
        // be presented during the last call" (xefg_swapchain.h), where its
        // GetLastPresentCount is the native swapchain's, which presents from a thread
        // of its own on GPUs other than Intel's.
        if (streamlineSwapChain || xessSwapChain)
            step = framesPresented != 0 ? framesPresented : 1u;
        countedPresents = presents;
        haveCountedPresents = gotPresents;
        // Compare and swap: one proxy presents at a time in practice, but two
        // overlapping ones must not lose each other's frames.
        uint64_t both = g_counters.load();
        uint64_t next = 0;
        do
        {
            const uint32_t rendered = static_cast<uint32_t>(both >> 32) + 1u;
            const uint32_t presentedTotal = static_cast<uint32_t>(both & 0xFFFFFFFFu) + step;
            next = (static_cast<uint64_t>(rendered) << 32) | presentedTotal;
        } while (!g_counters.compare_exchange_weak(both, next));
        g_countersValid.store(true);
    }

    HRESULT SwapChainProxy::CopyAndPresent(UINT syncInterval, UINT flags)
    {
        // A removed device first: every call below would fail and return early
        // before any later check was reached. The fence says so for nothing --
        // "If the device has been removed, the return value will be
        // UINT64_MAX" (Microsoft, ID3D12Fence::GetCompletedValue). Read before
        // the frame wait below, it also tells how many frames are on the GPU.
        const UINT64 completedAtStart = frameFence->GetCompletedValue();
        if (completedAtStart == UINT64_MAX)
            NoteDeviceRemoved();

        // Without Unity's backbuffer nothing of the frame is begun: no inputs
        // copied, no frame generation prepared for a Present that does not come.
        if (!HasSharedColour())
            return PresentUncopied(syncInterval, flags, "Unity's backbuffer is not shared");

        // Before the flush, so the copies go out with this frame's work. Unity
        // calls Present once everything it drew this frame is submitted, which
        // makes this the one moment a copy of its textures is of this frame.
        //
        // Into the slot FSR last read two Presents ago. D3D11 waits for that
        // Present first -- long finished by now, so the wait costs nothing --
        // rather than for the previous frame's interpolation, which would serialise
        // rendering and interpolation on the GPU.
        const UINT slot = static_cast<UINT>(presentCount % 2);
        WaitForStreamlineInputs(slot);
        d3d11Context->Wait(sharedFence11.Get(), afterPresentFence[slot]);
        FrameGeneration::Get().CaptureForPresent(slot);

        HRESULT hr = WaitForUnityWork();
        if (FAILED(hr))
            return PresentUncopied(syncInterval, flags, "waiting for Unity's work failed");

        hr = ResetCommandList();
        if (FAILED(hr))
            return PresentUncopied(syncInterval, flags, "the command list could not be reset");

        // The API names the calls that must be externally synchronised, and
        // the swapchain's Present is one of them once a frame generation
        // callback is configured -- which the proxy's is, because Present is where the
        // interpolation work is requested. The lock therefore has to span both,
        // not just the dispatch. Documented symptoms of getting this wrong are
        // crashes, visual artefacts and infinite waits.
        std::unique_lock<std::mutex> fgLock(FrameGeneration::Get().ContextMutex());

        // Inputs replaced since they were last read go once the GPU is done
        // with every frame that can read them.
        FrameGeneration::Get().ReleaseRetiredLocked(frameFenceValue, completedAtStart,
                                                    frameFence->GetCompletedValue(),
                                                    fgSwapChainContext != nullptr ? &fgSwapChainContext : nullptr);

        // The ini's live settings: read on every change, as it says. The context is
        // made anew only for the settings it was built with; the pacing goes to
        // FSR's swapchain every time, a Configure call.
        bool rebuildContext = false;
        if (ReloadConfigIfChanged(rebuildContext) && Config().frameGeneration)
        {
            if (rebuildContext)
                FrameGeneration::Get().RebuildContextLocked(fgSwapChainContext != nullptr ? swapChain.Get() : nullptr);
            ApplyPacingTuning();
        }

        // Before the proxy's own copy, into the same command list: FSR records its
        // preparation here and interpolates during the Present below. DLSS-G
        // takes its constants and tags under this frame's token instead and does
        // its work in the Present; the same index marks the Present for Reflex,
        // as it asks: "Ensure the frame index provided with the common constants
        // matches the presented frame" (ProgrammingGuideDLSS_G.md 0.0).
        uint32_t frameIndex = 0;
        bool streamlineGenerating = false;
        presentingThread.store(GetCurrentThreadId());
        // The game's frame rendered last, for the low latency markers (LowLatency.h)
        // and as XeSS-FG's present id: "The application must use the same frame ID
        // value for XeLL markers and the XeSS-FG present ID"
        // (xess_fg_developer_guide_english.md, Connect with XeLL).
        const uint32_t lowLatencyFrame = LowLatency::Get().TakeGameFrame();
        // XeLL's sync interval: "when VSync is enabled, sync interval for
        // IDXGISwapChain::Present must always be 1 when Low Latency is active"
        // (xell_developer_guide_english.md, Integration Checklist).
        const bool xell = LowLatency::Get().XellActive();
        const UINT xellSync = xell && syncInterval > 1 ? 1u : syncInterval;
        // The sync interval DLSS-G presents with while it generates, and its frame
        // limit with it: every refresh at most -- "SyncInterval > 1: Not supported.
        // Will be clamped to 1 with a warning" (ProgrammingGuideDLSS_G.md 22.2) --
        // and none where its build does not support V-Sync (22.1).
        UINT streamlineSync = syncInterval;
        if (streamlineSwapChain && syncInterval > 0)
            streamlineSync = FrameGeneration::Get().StreamlineVsyncSupported() ? 1u : 0u;
        // The cap V-Sync sets on the frames shown, for frame generation's minimum
        // (SetPresentCapLocked): DLSS-G shows its limit's frames and the rendered one,
        // FSR one generated and the rendered one.
        {
            const UINT capSync = streamlineSwapChain ? streamlineSync
                                 : xessSwapChain ? xellSync
                                 : syncInterval > 0 ? syncInterval : Config().fgVSync ? 1u : 0u;
            const ProxyConfig& cfg = Config();
            double capFps = 0.0;
            if (capSync > 0)
            {
                const double refresh = MonitorRefreshRate();
                if (refresh > 0.0)
                    capFps = refresh / static_cast<double>(capSync);
            }
            else if (!streamlineSwapChain && (xessSwapChain || !(cfg.fgHalfRefreshLimit && !cfg.fgVSync && syncInterval == 0)))
                PauseRefreshMonitor();
            const uint32_t shown = streamlineSwapChain ? StreamlineFrameLimit(streamlineSync) + 1u : 2u;
            FrameGeneration::Get().SetPresentCapLocked(capFps, shown);
        }
        if (streamlineSwapChain)
        {
            frameIndex = TakeFrameIndex(true);
            streamlineGenerating = FrameGeneration::Get().PrepareStreamlineLocked(
                frameIndex, [this, streamlineSync]() { return StreamlineFrameLimit(streamlineSync); },
                streamlineOffRequested.exchange(false) ? "a window change asked for it" : nullptr);
            // The refresh rate is asked for while DLSS-G generates with VSync, and
            // let go only after long without: frames that alternate would
            // otherwise start a query on every other Present. Under V-Sync it stays
            // for frame generation's cap (SetPresentCapLocked, above), which asks for
            // it on every Present: paused there, every Present would start a query.
            if (streamlineGenerating)
                streamlineIdlePresents = 0;
            else if (++streamlineIdlePresents > 120 && streamlineSync == 0)
                PauseRefreshMonitor();
        }
        else if (xessSwapChain)
        {
            // XeSS-FG takes its resources and constants under the game's frame; its
            // work is submitted in the Present, on the queue it was given.
            if (lowLatencyFrame == 0 && !loggedXessNoFrame)
            {
                loggedXessNoFrame = true;
                LogLine("XeSS frame generation: a Present without a frame of the game's -- presented without"
                        " generated frames (said once)");
            }
            FrameGeneration::Get().PrepareXessLocked(lowLatencyFrame, nullptr);
        }
        else
            // FSR's own swapchain only: a plain one, made where FidelityFX could
            // not make its own, has nothing to generate with.
            FrameGeneration::Get().PrepareForPresentLocked(fgSwapChainContext != nullptr ? swapChain.Get() : nullptr,
                                                           commandList.Get());
        const bool pacedByVSync = !streamlineSwapChain && !xessSwapChain && Config().fgVSync
                                  && FrameGeneration::Get().PacedByVSyncLocked();

        hr = CopySharedColourAndExecute();
        if (FAILED(hr))
        {
            FrameGeneration::Get().CancelPreparedLocked(fgSwapChainContext != nullptr ? swapChain.Get() : nullptr,
                                                        "the frame could not be copied");
            fgLock.unlock();
            return PresentUncopied(syncInterval, flags, "the frame could not be copied");
        }

        // Unity must not draw the next frame into the shared backbuffer until
        // the copy above has read it. That copy is done long before the
        // interpolation FSR records in the Present below, so the wait is on
        // the copy alone.
        ++sharedFenceValue;
        queue->Signal(sharedFence12.Get(), sharedFenceValue);
        d3d11Context->Wait(sharedFence11.Get(), sharedFenceValue);

        // While frame generation is on, FSR's swapchain paces by the sync
        // interval it is handed: with VSync every real and generated frame is
        // shown for one refresh. See fgVSync in Config.h, and
        // PacedByVSyncLocked for why this holds on frames without inputs too.
        UINT effectiveSync = streamlineGenerating ? streamlineSync : xell ? xellSync : syncInterval;
        if (xell && syncInterval > 1 && !loggedXessSyncClamp)
        {
            LogLine("XeLL presents every refresh: KSP's sync interval " + std::to_string(syncInterval)
                    + " is not allowed with it");
            loggedXessSyncClamp = true;
        }
        if (streamlineGenerating && syncInterval > 1 && streamlineSync == 1 && !loggedStreamlineSyncClamp)
        {
            LogLine("DLSS frame generation presents every refresh while it generates: KSP's sync interval "
                    + std::to_string(syncInterval) + " is not supported with it");
            loggedStreamlineSyncClamp = true;
        }
        if (pacedByVSync && effectiveSync == 0)
        {
            effectiveSync = 1;
            if (!loggedVSyncPacing)
            {
                LogLine("Frame generation paced by VSync: presenting with sync interval 1 while generating");
                loggedVSyncPacing = true;
            }
        }
        lastSyncInterval = effectiveSync;

        // Tearing and a sync interval are mutually exclusive; DXGI rejects the
        // combination outright.
        UINT presentFlags = flags;
        if (effectiveSync == 0 && (description.Flags & DXGI_SWAP_CHAIN_FLAG_ALLOW_TEARING) != 0)
            presentFlags |= DXGI_PRESENT_ALLOW_TEARING;
        else
            presentFlags &= ~static_cast<UINT>(DXGI_PRESENT_ALLOW_TEARING);

        // "RENDERSUBMIT_END should come before calling Present" (nvapi.h,
        // NvAPI_D3D_SetLatencyMarker), for the game's frame whose
        // eRenderSubmitStart Unity's render thread marked.
        if (freshGameFrame)
            Streamline::Get().Marker(sl::PCLMarker::eRenderSubmitEnd, frameIndex);
        if (frameIndex != 0)
            Streamline::Get().Marker(sl::PCLMarker::ePresentStart, frameIndex);
        // Reflex where Streamline's does not run, or XeLL (LowLatency.h), for the
        // game's frame rendered last. Then XeSS-FG's present id, between XeLL's
        // PRESENT_START and the Present, as Intel's example has it: "Call
        // xefgSwapChainSetPresentId before Present/Present1" (Quick Start Guide).
        LowLatency::Get().BeforePresent(lowLatencyFrame, swapChain.Get());
        if (xessSwapChain && lowLatencyFrame != 0)
            XessFg::Get().SetPresentId(xessContext, lowLatencyFrame);

        LARGE_INTEGER presentStart;
        QueryPerformanceCounter(&presentStart);
        hr = swapChain->Present(effectiveSync, presentFlags);
        LARGE_INTEGER presentEnd;
        QueryPerformanceCounter(&presentEnd);
        g_presentTotalTicks += presentEnd.QuadPart - presentStart.QuadPart;
        if (FAILED(hr))
            LogLine("Present failed (" + Hr(hr) + ")");
        LowLatency::Get().AfterPresent(lowLatencyFrame);

        // DLSS-G's frames presented since the last Present, its status, how many
        // it may generate from the next, and the fence its work on this Present's
        // inputs completes at.
        FrameGeneration::StreamlinePresent streamlinePresent;
        if (frameIndex != 0)
        {
            Streamline::Get().Marker(sl::PCLMarker::ePresentEnd, frameIndex);
            streamlinePresent = FrameGeneration::Get().ReadStreamlineStateLocked();
        }
        const uint32_t xessPresented = xessSwapChain ? FrameGeneration::Get().ReadXessStateLocked() : 0u;

        fgLock.unlock();

        CountPresent(streamlineSwapChain ? streamlinePresent.presented : xessPresented);

        // The interpolation FSR recorded in Present reads this slot's depth,
        // motion vectors and HUD-less copy. D3D11 writes from another API,
        // where only a fence gives the order; AMD's "the app can safely modify
        // the HUDLess texture in the next frame" assumes writes on the same
        // queue. The wait is taken two Presents later, before the slot is
        // written again -- see the top of this function.
        //
        // Signalled before the frame fence, so whatever waits for the frame --
        // a resize, the destructor -- knows this signal has landed too, and
        // D3D11 is never left waiting on a queue that is gone.
        //
        // DLSS-G does its work on these inputs on a queue of its own, after the
        // Present has returned: its fence for them is kept for this slot, and
        // waited for before the slot is written again (WaitForStreamlineInputs) --
        // after a Present it generated for only, the only one it read them for.
        if (streamlinePresent.statusOk)
            inputsWaitRanOut = false;
        inputsFence[slot] = streamlineGenerating ? streamlinePresent.inputsFence : nullptr;
        inputsFenceValue[slot] = streamlinePresent.inputsFenceValue;
        inputsStatusOk[slot] = streamlinePresent.statusOk;
        ++sharedFenceValue;
        queue->Signal(sharedFence12.Get(), sharedFenceValue);
        afterPresentFence[slot] = sharedFenceValue;

        SignalFrameDone();

        ++presentCount;
        if (presentCount == 1)
            LogLine("First frame presented through the proxy");

        // AMD's advice for FSR's pacing; Reflex paces DLSS-G, last, once this
        // frame's signals are out: where the next frame begins, as far as the
        // thread that presents goes. XeLL paces XeSS-FG, and with it no frame limit:
        // "Any other forms of latency reduction or wait inducing technologies like
        // frame limiters should be disabled when XeLL is in use"
        // (xell_developer_guide_english.md, Requirements).
        if (SUCCEEDED(hr) && !streamlineSwapChain && !xessSwapChain && !xell)
            LimitToHalfRefresh(syncInterval);
        if (streamlineSwapChain)
            BeginStreamlineFrame();

        return hr;
    }

    HRESULT STDMETHODCALLTYPE SwapChainProxy::Present(UINT syncInterval, UINT flags)
    {
        if ((flags & DXGI_PRESENT_TEST) != 0)
            return measureOnly ? realSwapChain->Present(syncInterval, flags) : PresentTest(syncInterval, flags);

        RecordFrameTime();
        GpuProfiler::Get().OnPresent();

        if (measureOnly)
        {
            lastSyncInterval = syncInterval;
            return realSwapChain->Present(syncInterval, flags);
        }

        return CopyAndPresent(syncInterval, flags);
    }

    HRESULT STDMETHODCALLTYPE SwapChainProxy::Present1(UINT syncInterval, UINT flags,
                                                       const DXGI_PRESENT_PARAMETERS* parameters)
    {
        if ((flags & DXGI_PRESENT_TEST) != 0)
            return measureOnly ? realSwapChain->Present1(syncInterval, flags, parameters)
                               : PresentTest(syncInterval, flags);

        RecordFrameTime();
        GpuProfiler::Get().OnPresent();

        if (measureOnly)
        {
            lastSyncInterval = syncInterval;
            return realSwapChain->Present1(syncInterval, flags, parameters);
        }

        // Dirty rectangles are meaningless once the frame goes through a full
        // copy, so the parameters are dropped rather than half honoured.
        return CopyAndPresent(syncInterval, flags);
    }
}
