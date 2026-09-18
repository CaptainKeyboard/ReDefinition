// The proxy's present path (SwapChainProxy): Unity's frame copied into the
// D3D12 backbuffer and presented, frame generation prepared under the same
// lock, the fences between D3D11, D3D12 and DLSS-G's queue, and DLSS-G's
// per-frame tokens.

#include "SwapChainProxy.h"

#include "Config.h"
#include "D3d12Util.h"
#include "FrameGeneration.h"
#include "Log.h"
#include "Streamline.h"
#include "SwapChainProxyState.h"

#include <string>

using Microsoft::WRL::ComPtr;

namespace redefinition
{
    // The most frames DLSS-G generates per rendered frame for this Present, 0 for
    // no limit beyond the GPU's. Without VSync none. With it, NVIDIA's own table:
    // "60Hz ... 4x", "75Hz ... 5x", above which "frames are generated faster than the
    // display can present them, causing frame queue backup" and latency grows
    // (ProgrammingGuideDLSS_G.md 22.7) -- a multiplier of a fifteenth of the rate
    // frames are shown at: the refresh rate over the sync interval, down to the
    // table's row, with a hundredth of a row for rates such as 59.94 Hz -- 72 Hz
    // stays at 4x. A rate not known yet allows one generated frame, not the GPU's
    // most.
    uint32_t SwapChainProxy::StreamlineFrameLimit(UINT syncInterval)
    {
        if (syncInterval == 0)
        {
            PauseRefreshMonitor();
            return 0;
        }
        const double refresh = MonitorRefreshRate();
        if (refresh <= 0.0)
            return 1;
        const double multiplier = std::floor(refresh / static_cast<double>(syncInterval) / 15.0 + 0.01);
        return multiplier >= 2.0 ? static_cast<uint32_t>(multiplier) - 1u : 1u;
    }

    const sl::FrameToken* SwapChainProxy::TakeFrameToken()
    {
        const sl::FrameToken* token = nextFrameToken;
        nextFrameToken = nullptr;
        return token != nullptr ? token : Streamline::Get().NewFrameToken(streamlineFrameIndex++);
    }

    // The next frame's token, fetched where the presenting thread begins it, and
    // Reflex's sleep under it: "Starting new frame, grab handle from SL" and then
    // slReflexSleep(*currentFrame) (ProgrammingGuideReflex.md 5.0) -- its wait
    // "regardless of Reflex Low Latency mode state" (8.0). The frame's constants,
    // tags and markers go under the same token when it is presented.
    void SwapChainProxy::BeginStreamlineFrame()
    {
        nextFrameToken = Streamline::Get().NewFrameToken(streamlineFrameIndex++);
        if (nextFrameToken != nullptr)
            Streamline::Get().ReflexSleep(*nextFrameToken);
    }

    // DLSS-G off before the swapchain's size or full screen state changes: "Turn
    // DLSS-G off ... before any window manipulation (resize, maximize/minimize,
    // full-screen transition, etc.) to avoid potential deadlocks or instability"
    // (ProgrammingGuideDLSS_G.md 17.0) -- and options take effect "in the next
    // Present() call that executes after it" (6.0). So the shared colour is
    // presented once more with generation off: the last frame, or whatever Unity
    // drew into it since, flushed first. On the presenting thread
    // only, which then is not inside a Present. From any other thread, where the
    // lock the Present is taken under could hold both threads up, only a request
    // for the next Present is left, and the log says so.
    void SwapChainProxy::PresentWithStreamlineOff(const char* reason)
    {
        if (!streamlineSwapChain || presentCount == 0)
            return;
        if (GetCurrentThreadId() != presentingThread.load())
        {
            if (!streamlineOffRequested.exchange(true))
                LogLine("DLSS frame generation: " + std::string(reason)
                        + " from a thread that does not present -- off from the next Present on");
            return;
        }
        // Handled here: a request from another thread before this is answered too.
        streamlineOffRequested.store(false);

        std::lock_guard<std::mutex> fgLock(FrameGeneration::Get().ContextMutex());
        if (!FrameGeneration::Get().StreamlineOffLocked(reason))
            return;

        // The options are sent; without the Present below they take effect at the
        // next one, after the change, and the log says so.
        const auto notPresented = [&](const char* step, HRESULT failure) {
            LogLine("DLSS frame generation off before " + std::string(reason)
                    + ", but the last frame was not presented again: " + step + " failed (" + Hr(failure) + ")");
        };

        // What Unity may have drawn since its last Present lands before the copy
        // reads the shared colour, as in CopyAndPresent.
        HRESULT hr = WaitForUnityWork();
        if (FAILED(hr))
        {
            notPresented("the wait for D3D11", hr);
            return;
        }

        hr = ResetCommandList();
        if (FAILED(hr))
        {
            notPresented("the command list's reset", hr);
            return;
        }
        hr = CopySharedColourAndExecute();
        if (FAILED(hr))
        {
            notPresented("the copy into the backbuffer", hr);
            return;
        }

        const sl::FrameToken* token = TakeFrameToken();
        if (token != nullptr)
            Streamline::Get().Marker(sl::PCLMarker::ePresentStart, *token);
        // VSync: a tearing present is refused in exclusive full screen, and one
        // refresh does not matter here.
        hr = swapChain->Present(1, 0);
        if (token != nullptr)
            Streamline::Get().Marker(sl::PCLMarker::ePresentEnd, *token);
        LogLine("DLSS frame generation off before " + std::string(reason) + ": last frame presented again ("
                + Hr(hr) + ")");

        WaitForFrame(SignalFrameDone());
        BeginStreamlineFrame();
    }

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

    void SwapChainProxy::WaitForStreamlineInputs(UINT slot)
    {
        constexpr ULONGLONG kInputsWaitMs = 1000;

        const ComPtr<ID3D12Fence> fence = inputsFence[slot];
        inputsFence[slot].Reset();
        if (fence == nullptr || (inputsWaitRanOut && !inputsStatusOk[slot]))
            return;

        // Done long before, as a rule: this is two Presents after the one that read
        // the slot. A value that does not complete -- DLSS-G skipping its work while
        // its status fails -- costs the wait's limit once: fences stored while the
        // status failed are not waited for again until it is fine, and it is said
        // once, rather than holding the game. One from a Present whose status was
        // fine is waited for, a GPU stall's worth at most.
        const UINT64 value = inputsFenceValue[slot];
        if (fence->GetCompletedValue() >= value)
            return;
        if (inputsEvent == nullptr)
            inputsEvent = CreateEventW(nullptr, FALSE, FALSE, nullptr);

        const ULONGLONG deadline = GetTickCount64() + kInputsWaitMs;
        while (fence->GetCompletedValue() < value)
        {
            const ULONGLONG now = GetTickCount64();
            if (now >= deadline || inputsEvent == nullptr || FAILED(fence->SetEventOnCompletion(value, inputsEvent)))
            {
                inputsWaitRanOut = true;
                if (!loggedInputsWait)
                {
                    loggedInputsWait = true;
                    LogLine("DLSS frame generation: its work on an input slot had not completed after "
                            + std::to_string(kInputsWaitMs) + " ms -- written again regardless (said once)");
                }
                return;
            }
            WaitForSingleObject(inputsEvent, static_cast<DWORD>(deadline - now));
        }
    }

    // ----------------------------------------------------------------- present

    void SwapChainProxy::WaitForFrame(UINT64 value)
    {
        if (frameFence->GetCompletedValue() >= value)
            return;

        if (SUCCEEDED(frameFence->SetEventOnCompletion(value, frameEvent)))
            WaitForSingleObject(frameEvent, INFINITE);
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
            return DXGI_ERROR_INVALID_CALL;

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
            return hr;

        hr = ResetCommandList();
        if (FAILED(hr))
            return hr;

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
        // its work in the Present; the same token marks the Present for Reflex,
        // as it asks: "Ensure the frame index provided with the common constants
        // matches the presented frame" (ProgrammingGuideDLSS_G.md 0.0).
        const sl::FrameToken* frameToken = nullptr;
        bool streamlineGenerating = false;
        presentingThread.store(GetCurrentThreadId());
        // The sync interval DLSS-G presents with while it generates, and its frame
        // limit with it: every refresh at most -- "SyncInterval > 1: Not supported.
        // Will be clamped to 1 with a warning" (ProgrammingGuideDLSS_G.md 22.2) --
        // and none where its build does not support V-Sync (22.1).
        UINT streamlineSync = syncInterval;
        if (streamlineSwapChain && syncInterval > 0)
            streamlineSync = FrameGeneration::Get().StreamlineVsyncSupported() ? 1u : 0u;
        if (streamlineSwapChain)
        {
            frameToken = TakeFrameToken();
            streamlineGenerating = FrameGeneration::Get().PrepareStreamlineLocked(
                frameToken, [this, streamlineSync]() { return StreamlineFrameLimit(streamlineSync); },
                streamlineOffRequested.exchange(false) ? "a window change asked for it" : nullptr);
            // The refresh rate is asked for while DLSS-G generates with VSync, and
            // let go only after long without: frames that alternate would
            // otherwise start a query on every other Present.
            if (streamlineGenerating)
                streamlineIdlePresents = 0;
            else if (++streamlineIdlePresents > 120)
                PauseRefreshMonitor();
        }
        else
            // FSR's own swapchain only: a plain one, made where FidelityFX could
            // not make its own, has nothing to generate with.
            FrameGeneration::Get().PrepareForPresentLocked(fgSwapChainContext != nullptr ? swapChain.Get() : nullptr,
                                                           commandList.Get());
        const bool pacedByVSync =
            !streamlineSwapChain && Config().fgVSync && FrameGeneration::Get().PacedByVSyncLocked();

        hr = CopySharedColourAndExecute();
        if (FAILED(hr))
            return hr;

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
        UINT effectiveSync = streamlineGenerating ? streamlineSync : syncInterval;
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

        if (frameToken != nullptr)
            Streamline::Get().Marker(sl::PCLMarker::ePresentStart, *frameToken);

        hr = swapChain->Present(effectiveSync, presentFlags);
        if (FAILED(hr))
            LogLine("Present failed (" + Hr(hr) + ")");

        // DLSS-G's frames presented since the last Present, its status, how many
        // it may generate from the next, and the fence its work on this Present's
        // inputs completes at.
        FrameGeneration::StreamlinePresent streamlinePresent;
        if (frameToken != nullptr)
        {
            Streamline::Get().Marker(sl::PCLMarker::ePresentEnd, *frameToken);
            streamlinePresent = FrameGeneration::Get().ReadStreamlineStateLocked();
        }

        fgLock.unlock();

        // For the mod's window (KspFgCounters): one more frame rendered, and as
        // many presented as the swapchain counted since the last one -- FSR's
        // own count, generated frames included, trailing a little as its
        // presenter thread presents them. Outside the lock, on the thread that
        // presents.
        UINT presents = 0;
        const bool gotPresents = SUCCEEDED(swapChain->GetLastPresentCount(&presents));
        UINT step = gotPresents && haveCountedPresents && presents >= countedPresents
                        ? presents - countedPresents
                        : 1u;
        // DLSS-G's own count: "Number of frames presented since the last
        // 'slDLSSGGetState' call" (sl_dlss_g.h).
        if (streamlineSwapChain)
            step = streamlinePresent.presented != 0 ? streamlinePresent.presented : 1u;
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
        // thread that presents goes.
        if (SUCCEEDED(hr) && !streamlineSwapChain)
            LimitToHalfRefresh(syncInterval);
        if (streamlineSwapChain)
            BeginStreamlineFrame();

        return hr;
    }

    HRESULT STDMETHODCALLTYPE SwapChainProxy::Present(UINT syncInterval, UINT flags)
    {
        if ((flags & DXGI_PRESENT_TEST) != 0)
            return measureOnly ? realSwapChain->Present(syncInterval, flags) : S_OK;

        RecordFrameTime();

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
            return measureOnly ? realSwapChain->Present1(syncInterval, flags, parameters) : S_OK;

        RecordFrameTime();

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
