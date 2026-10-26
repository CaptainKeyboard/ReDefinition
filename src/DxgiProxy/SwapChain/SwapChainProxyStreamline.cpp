// The proxy's Streamline side (SwapChainProxy): Streamline set up on the device,
// its swapchain made and torn down, DLSS-G's per-frame tokens and frame limit,
// DLSS-G off before a resize, and the wait for its work on an input slot.

#include "WaitStats.h"
#include "SwapChainProxy.h"

#include "FrameGeneration.h"
#include "Log.h"
#include "Streamline.h"
#include "SwapChainProxyState.h"

#include <algorithm>
#include <cmath>
#include <mutex>
#include <string>

using Microsoft::WRL::ComPtr;

namespace redefinition
{
    bool SwapChainProxy::SetUpStreamline(IDXGIAdapter* adapter, bool frameGenerationWanted)
    {
        Streamline& streamline = Streamline::Get();
        if (!streamline.Acquire(this, frameGenerationWanted))
            return false;

        DXGI_ADAPTER_DESC adapterDesc = {};
        const HRESULT described = adapter->GetDesc(&adapterDesc);
        if (FAILED(described))
            streamline.Note("the adapter has no description (" + Hr(described) + ")" + Streamline::Get().Instead());
        if (FAILED(described) || !streamline.Supported(adapterDesc.AdapterLuid)
            || !streamline.SetDevice(d3d12Device.Get()))
        {
            streamline.Release(this);
            return false;
        }

        // Replaced in place by its proxy, which holds a reference of its own to
        // the native device (D3D12Device's constructor in Streamline's source)
        // and starts with one for the caller to release.
        ID3D12Device* proxy = d3d12Device.Get();
        if (!streamline.UpgradeInterface(reinterpret_cast<void**>(&proxy), "slUpgradeInterface(ID3D12Device)"))
        {
            streamline.Note("Streamline did not upgrade the D3D12 device" + Streamline::Get().Instead());
            streamline.Release(this);
            return false;
        }
        streamlineDevice.Attach(proxy);
        return true;
    }

    HRESULT SwapChainProxy::CreateStreamlineSwapChain(IDXGIFactory2* factory, HWND hwnd,
                                                      const DXGI_SWAP_CHAIN_DESC1& desc,
                                                      const DXGI_SWAP_CHAIN_FULLSCREEN_DESC* fullscreenDesc,
                                                      IDXGIOutput* restrictToOutput)
    {
        // "Any create swap chain API must be intercepted ... Therefore here we are
        // using proxy factory and not a native one" (ProgrammingGuideManualHooking.md
        // 4.1). The factory Unity created: the swapchain Streamline makes behind
        // its proxy reaches the proxy's CreateSwapChainForHwnd hook with a D3D12 queue,
        // which the hook passes through.
        // As the device: the proxy references the native factory itself.
        IDXGIFactory* proxy = factory;
        if (!Streamline::Get().UpgradeInterface(reinterpret_cast<void**>(&proxy), "slUpgradeInterface(IDXGIFactory)"))
        {
            Streamline::Get().Note("Streamline did not upgrade the DXGI factory" + Streamline::Get().Instead());
            return E_FAIL;
        }
        ComPtr<IDXGIFactory> factoryProxy;
        factoryProxy.Attach(proxy);

        ComPtr<IDXGIFactory2> factoryProxy2;
        HRESULT hr = factoryProxy.As(&factoryProxy2);
        if (FAILED(hr))
        {
            Streamline::Get().Note("Streamline's factory has no IDXGIFactory2 (" + Hr(hr)
                                   + ")" + Streamline::Get().Instead());
            return hr;
        }

        ComPtr<IDXGISwapChain1> created;
        hr = factoryProxy2->CreateSwapChainForHwnd(streamlineQueue.Get(), hwnd, &desc, fullscreenDesc,
                                                   restrictToOutput, &created);
        if (FAILED(hr))
        {
            Streamline::Get().Note("CreateSwapChainForHwnd through Streamline failed (" + Hr(hr)
                                   + ")" + Streamline::Get().Instead());
            return hr;
        }

        hr = created.As(&swapChain);
        if (FAILED(hr))
        {
            Streamline::Get().Note("Streamline's swapchain has no IDXGISwapChain3 (" + Hr(hr)
                                   + ")" + Streamline::Get().Instead());
            return hr;
        }

        // The native swapchain behind the proxy, for DXGI_PRESENT_TEST: a test asks
        // DXGI alone and is no frame for Streamline's plugins.
        void* native = nullptr;
        if (Streamline::Get().NativeInterface(swapChain.Get(), &native))
            streamlineNativeSwapChain.Attach(static_cast<IDXGISwapChain*>(native));

        streamlineSwapChain = true;
        streamlineGenerates = Streamline::Get().FrameGenerationAvailable();
        if (streamlineGenerates)
        {
            g_generatingSwapChains.fetch_add(1);
            g_streamlineSwapChains.fetch_add(1);
        }
        LogLine(streamlineGenerates ? "Swapchain created through Streamline (DLSS frame generation)"
                                    : "Swapchain created through Streamline (DLSS Ray Reconstruction, no frame generation)");
        return S_OK;
    }

    void SwapChainProxy::TearDownStreamline()
    {
        if (streamlineSwapChain)
        {
            streamlineNativeSwapChain.Reset();
            swapChain.Reset();
            if (streamlineGenerates)
            {
                g_generatingSwapChains.fetch_sub(1);
                g_streamlineSwapChains.fetch_sub(1);
            }
            streamlineSwapChain = false;
            streamlineGenerates = false;
        }
        streamlineQueue.Reset();
        streamlineDevice.Reset();
        nextFrameIndex = 0;
        presentingGameFrames = false;
        Streamline::Get().NoteGameFramesPresented(false);
        for (auto& fence : inputsFence)
            fence.Reset();
        Streamline::Get().Release(this);
    }

    // The most frames DLSS-G generates per rendered frame for this Present, 0 for no limit beyond
    // the GPU's. Without VSync it is 0. With VSync it follows NVIDIA's own table: "60Hz ... 4x",
    // "75Hz ... 5x". Above that "frames are generated faster than the display can present them,
    // causing frame queue backup" and latency grows (ProgrammingGuideDLSS_G.md 22.7). It is a
    // fifteenth of the refresh rate over the sync interval, down to the table's row with a
    // hundredth of a row for rates such as 59.94 Hz, so 72 Hz stays at 4x. An unknown rate allows
    // one generated frame, not the GPU's most.
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
        uint32_t frames = multiplier >= 2.0 ? static_cast<uint32_t>(multiplier) - 1u : 1u;
        // No more than keep the game at its minimum rendered rate: each generated frame
        // takes a refresh from the rendered ones under V-Sync.
        const float minimum = FrameGeneration::Get().MinimumFpsLocked();
        if (minimum > 0.0f)
        {
            const double most = std::floor(refresh / static_cast<double>(syncInterval) / minimum + 0.01) - 1.0;
            frames = std::min(frames, static_cast<uint32_t>(std::max(1.0, most)));
        }
        return frames;
    }

    // DLSS-G generates nothing while the game's window is not in the foreground. Streamline says
    // "DLSS-G disabled: window not focused" (sl.dlss_g, shouldInterpolate) and presents the rendered
    // frames alone, with its status still fine. A benchmark started from another program's window
    // found its frame generation rows at 1.00 presented per rendered frame that way. The note is
    // said once per change, from the Present that asked it to generate. The foreground window of
    // this process counts as the game's.
    void SwapChainProxy::NoteStreamlineFocus()
    {
        const HWND foreground = GetForegroundWindow();
        DWORD process = 0;
        if (foreground != nullptr)
            GetWindowThreadProcessId(foreground, &process);
        const int focused = foreground != nullptr && (foreground == window || process == GetCurrentProcessId()) ? 1 : 0;
        if (focused == streamlineFocused)
            return;
        if (focused == 0 || streamlineFocused == 0)
            LogLine(focused == 0
                        ? "DLSS frame generation: the game's window is not in the foreground -- Streamline's DLSS-G generates"
                          " no frames while it is not (\"DLSS-G disabled: window not focused\")"
                        : "DLSS frame generation: the game's window is in the foreground again");
        streamlineFocused = focused;
    }

    // The guide says "Ensure the frame index provided with the common constants matches the
    // presented frame (i.e., the frame index provided with Reflex markers ReflexMarker::ePresentStart
    // and ReflexMarker::ePresentEnd)" (ProgrammingGuideDLSS_G.md 17.0). So the game's frame goes from
    // its main thread to this Present, as "Store a copy of the handle and use that in any async tasks
    // ... asyncPresentFrame(currentFrame)" (ProgrammingGuide.md 2.10).
    uint32_t SwapChainProxy::TakeFrameIndex(bool takeGameFrame)
    {
        freshGameFrame = false;
        Streamline& streamline = Streamline::Get();
        if (!streamline.DeviceSet())
            return 0;

        uint32_t game = 0;
        if (takeGameFrame && streamline.TakeGameFrame(game))
        {
            if (game > presentedFrameIndex)
            {
                if (!presentingGameFrames)
                    LogLine("Reflex: the game's frames are presented, from frame " + std::to_string(game)
                            + " -- its main thread sleeps and marks the simulation");
                presentingGameFrames = true;
                streamline.NoteGameFramesPresented(true);
                presentsWithoutGameFrame = 0;
                nextFrameIndex = 0;
                freshGameFrame = true;
                presentedFrameIndex = game;
                return game;
            }
            if (!loggedLateGameFrame)
            {
                loggedLateGameFrame = true;
                LogLine("Reflex: the game's frame " + std::to_string(game) + " arrived after frame "
                        + std::to_string(presentedFrameIndex) + " was presented, and goes without its Present");
            }
        }

        if (presentingGameFrames)
        {
            if (!takeGameFrame || ++presentsWithoutGameFrame < kPresentsWithoutGameFrame)
            {
                if (!loggedPresentWithoutGameFrame)
                {
                    loggedPresentWithoutGameFrame = true;
                    LogLine(std::string("Reflex: a Present ")
                            + (takeGameFrame ? "without a frame from the game" : "that is no frame of the game's")
                            + " after frame " + std::to_string(presentedFrameIndex)
                            + " -- presented without DLSS frame generation and its markers");
                }
                // A Present that is no frame of the game's comes between two of its
                // frames: the one in progress stays.
                if (takeGameFrame)
                    streamline.ClearGameFrame();
                return 0;
            }
            presentingGameFrames = false;
            streamline.ClearGameFrame();
            streamline.NoteGameFramesPresented(false);
            LogLine("Reflex: no frame from the game for " + std::to_string(kPresentsWithoutGameFrame)
                    + " Presents -- the presenting thread begins the frames again");
        }

        const uint32_t index = nextFrameIndex != 0 ? nextFrameIndex : streamline.NextFrameIndex();
        nextFrameIndex = 0;
        presentedFrameIndex = index;
        return index;
    }

    // Without the game's frames, the presenting thread begins the next frame. It takes the index and
    // sleeps under it with Reflex: "Starting new frame, grab handle from SL" and then
    // slReflexSleep(*currentFrame) (ProgrammingGuideReflex.md 5.0). Its wait comes "regardless of
    // Reflex Low Latency mode state" (8.0). The frame's constants, tags and markers go under the
    // same index when it is presented. With the game's frames, the game's main thread sleeps
    // instead (Streamline::BeginGameFrame).
    void SwapChainProxy::BeginStreamlineFrame()
    {
        if (presentingGameFrames)
            return;
        Streamline& streamline = Streamline::Get();
        if (!streamline.DeviceSet())
            return;
        nextFrameIndex = streamline.NextFrameIndex();
        streamline.NoteFrameInProgress(nextFrameIndex);
        streamline.ReflexSleep(nextFrameIndex);
    }

    // DLSS-G off before the swapchain's size or full screen state changes: "Turn DLSS-G off ...
    // before any window manipulation (resize, maximize/minimize, full-screen transition, etc.) to
    // avoid potential deadlocks or instability" (ProgrammingGuideDLSS_G.md 17.0). Options take effect
    // "in the next Present() call that executes after it" (6.0). So the shared colour is presented
    // once more with generation off: the last frame, or whatever Unity drew into it since, flushed
    // first. This runs on the presenting thread only, outside a Present. Another thread only
    // requests the next Present, because the lock the Present is taken under could hold both
    // threads up. The log says so.
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

        const uint32_t frameIndex = TakeFrameIndex(false);
        if (frameIndex != 0)
            Streamline::Get().Marker(sl::PCLMarker::ePresentStart, frameIndex);
        // VSync: a tearing present is refused in exclusive full screen, and one
        // refresh does not matter here.
        hr = swapChain->Present(1, 0);
        if (frameIndex != 0)
            Streamline::Get().Marker(sl::PCLMarker::ePresentEnd, frameIndex);
        LogLine("DLSS frame generation off before " + std::string(reason) + ": last frame presented again ("
                + Hr(hr) + ")");

        WaitForFrame(SignalFrameDone());
        BeginStreamlineFrame();
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
            WaitTimer timer(WaitKind::FrameGenerationInputs);
            WaitForSingleObject(inputsEvent, static_cast<DWORD>(deadline - now));
        }
    }
}
