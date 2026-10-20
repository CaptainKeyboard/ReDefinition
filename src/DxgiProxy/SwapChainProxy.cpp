// The proxy swapchain (SwapChainProxy): made in place of Unity's, through
// Streamline, FidelityFX or DXGI, and torn down in the order the runtimes ask
// for. The shared backbuffer and the resize are in SwapChainProxyBuffers.cpp,
// Streamline's side in SwapChainProxyStreamline.cpp, the present path, the
// pacing and the forwarded calls in SwapChainProxyPresent.cpp,
// SwapChainProxyPacing.cpp and SwapChainProxyForwarding.cpp.

#include "SwapChainProxy.h"

#include "AmdUpscaler.h"
#include "Config.h"
#include "D3d12Compute.h"
#include "D3d12Util.h"
#include "Raytracing.h"
#include "FidelityFx.h"
#include "FrameGeneration.h"
#include "GpuProfiler.h"
#include "Log.h"
#include "LowLatency.h"
#include "Streamline.h"
#include "SwapChainProxyState.h"
#include "XessUpscaler.h"

#include <FidelityFX/framegeneration/include/dx12/ffx_api_framegeneration_dx12.h>
#include <FidelityFX/framegeneration/include/ffx_framegeneration_api_types.h>

#include <cstring>
#include <mutex>
#include <string>

using Microsoft::WRL::ComPtr;

namespace redefinition
{
    int FrameGenerationTechnique()
    {
        if (g_streamlineSwapChains.load() > 0)
            return 2;
        if (g_xessSwapChains.load() > 0)
            return 3;
        return g_generatingSwapChains.load() > 0 ? 1 : 0;
    }

    int FrameGenerationTechniqueWanted()
    {
        return g_fgTechniqueWanted.load();
    }

    bool FrameCounters(uint32_t& rendered, uint32_t& presented)
    {
        // The flag first: seeing it set means the totals written before it
        // are visible too.
        const bool valid = g_countersValid.load();
        const uint64_t both = g_counters.load();
        rendered = static_cast<uint32_t>(both >> 32);
        presented = static_cast<uint32_t>(both & 0xFFFFFFFFu);
        return valid;
    }

    HRESULT DeviceRemovedReason()
    {
        return g_deviceRemoved.load();
    }

    bool FrameGenerationSwapChainPresent()
    {
        return g_generatingSwapChains.load() > 0;
    }

    HWND PresentingWindow()
    {
        return g_presentingWindow.load();
    }

    namespace
    {
        std::string Describe(const DXGI_SWAP_CHAIN_DESC1& desc)
        {
            return std::to_string(desc.Width) + "x" + std::to_string(desc.Height)
                 + " format " + std::to_string(static_cast<int>(desc.Format))
                 + " buffers " + std::to_string(desc.BufferCount)
                 + " swapEffect " + std::to_string(static_cast<int>(desc.SwapEffect))
                 + " flags 0x" + [&] { char b[16]; FormatTo(b, "%X", desc.Flags); return std::string(b); }()
                 + " samples " + std::to_string(desc.SampleDesc.Count)
                 + " usage 0x" + [&] { char b[16]; FormatTo(b, "%X", desc.BufferUsage); return std::string(b); }()
                 + " scaling " + std::to_string(static_cast<int>(desc.Scaling))
                 + " alpha " + std::to_string(static_cast<int>(desc.AlphaMode));
        }
    }

    // ---------------------------------------------------------------- creation

    HRESULT SwapChainProxy::Create(
        IDXGIFactory2* factory,
        IUnknown* device,
        HWND hwnd,
        const DXGI_SWAP_CHAIN_DESC1& desc,
        const DXGI_SWAP_CHAIN_FULLSCREEN_DESC* fullscreenDesc,
        IDXGIOutput* restrictToOutput,
        IDXGISwapChain1** result)
    {
        *result = nullptr;

        // Only a D3D11 device is taken over. Anything else -- a D3D12 command
        // queue, most obviously -- is passed straight through.
        ComPtr<ID3D11Device> d3d11;
        if (FAILED(device->QueryInterface(IID_PPV_ARGS(&d3d11))))
        {
            LogLine("CreateSwapChainForHwnd: device is not D3D11, passing through");
            return E_NOINTERFACE;
        }

        LogLine("CreateSwapChainForHwnd intercepted: " + Describe(desc));

        SwapChainProxy* proxy = new SwapChainProxy();
        const HRESULT hr = proxy->Initialise(factory, d3d11.Get(), hwnd, desc,
                                             fullscreenDesc, restrictToOutput);
        if (FAILED(hr))
        {
            LogLine("Proxy setup failed (" + Hr(hr) + "), falling back to the real swapchain");
            proxy->Release();
            return hr;
        }

        *result = proxy;
        LogLine("Proxy swapchain in place");
        return S_OK;
    }

    HRESULT SwapChainProxy::CreateMeasuring(IUnknown* device, HWND hwnd, IDXGISwapChain1* real,
                                            const DXGI_SWAP_CHAIN_DESC1& desc,
                                            IDXGISwapChain1** result)
    {
        *result = nullptr;

        SwapChainProxy* proxy = new SwapChainProxy();
        proxy->measureOnly = true;
        proxy->realSwapChain = real;
        proxy->window = hwnd;
        g_presentingWindow.store(hwnd);
        proxy->description = desc;
        proxy->swapChain = nullptr;

        // Only needed so GetDevice can answer; nothing else touches D3D11 here.
        ComPtr<ID3D11Device> d3d11;
        if (SUCCEEDED(device->QueryInterface(IID_PPV_ARGS(&d3d11))))
            d3d11.As(&proxy->d3d11Device);

        proxy->ResolveTargetInterfaces();
        QueryPerformanceFrequency(reinterpret_cast<LARGE_INTEGER*>(&proxy->ticksPerSecond));

        *result = proxy;
        LogLine("Measuring pass-through in place: " + Describe(desc));
        return S_OK;
    }

    SwapChainProxy::~SwapChainProxy()
    {
        HWND own = window;
        g_presentingWindow.compare_exchange_strong(own, nullptr);
        StopRefreshMonitor();

        // Everything must be off the GPU before the D3D12 objects go away, or
        // the driver removes the device on the way out.
        ReportFrameTimes();

        if (frameFence && frameEvent)
            WaitForFrame(frameFenceValue);

        if (frameEvent != nullptr)
        {
            CloseHandle(frameEvent);
            frameEvent = nullptr;
        }
        if (inputsEvent != nullptr)
        {
            CloseHandle(inputsEvent);
            inputsEvent = nullptr;
        }

        ReleaseLimitTimer();

        // Order matters on the way out: the frame generation context holds
        // resources shared with D3D11, so it goes before the swapchain context
        // that owns the device those resources came from -- and it is switched
        // off on the swapchain first, which is AMD's documented shutdown order.
        //
        // Ignored from a proxy that does not own frame generation, such as one
        // whose Initialise failed. Low latency first: its XeLL calls end there, and
        // the context they go to only after XeSS-FG's, below.
        LowLatency::Get().Detach(this);
        AmdUpscaler::Get().Detach(this);
        XessUpscaler::Get().Detach(this);
        D3d12Compute::Get().Detach(this);
        Raytracing::Get().Detach(this);
        GpuProfiler::Get().Detach(this);
        FrameGeneration::Get().Detach(this, fgSwapChainContext != nullptr ? swapChain.Get() : nullptr);

        // The swapchain belongs to FidelityFX in that case and has to be
        // released before its context, or the context destroys an object the
        // proxy still holds a reference to. Under the frame generation lock: the
        // swapchain destructor is on AMD's list of calls that must be
        // externally synchronised.
        if (fgSwapChainContext != nullptr)
        {
            std::lock_guard<std::mutex> fgLock(FrameGeneration::Get().ContextMutex());

            swapChain.Reset();
            target2.Reset();
            target3.Reset();
            target4.Reset();

            FidelityFx::Get().Api().DestroyContext(&fgSwapChainContext, nullptr);
            fgSwapChainContext = nullptr;
            g_generatingSwapChains.fetch_sub(1);
            LogLine("FidelityFX swapchain context destroyed");
        }

        // XeSS-FG's swapchain and context, then XeLL's: "XeSS-FG context depends on
        // the XeLL context and must be destroyed first" (xess_fg_developer_guide_english.md,
        // Shutdown). The GPU is idle since the wait above. Under the frame generation
        // lock, as FidelityFX's: xefgSwapChainDestroy is one of the calls that are not
        // thread-safe (Multithreading).
        if (xessContext != nullptr || xellContext != nullptr)
        {
            std::lock_guard<std::mutex> fgLock(FrameGeneration::Get().ContextMutex());
            TearDownXess();
        }

        // Streamline's swapchain, queue and device proxies, then Streamline
        // itself -- all before the native device, a member released after this.
        // Its shutdown lets go of everything it holds, the tagged inputs among
        // them, which it referenced for itself when they were tagged.
        if (streamlineDevice != nullptr)
        {
            streamlineNativeSwapChain.Reset();
            swapChain.Reset();
            target2.Reset();
            target3.Reset();
            target4.Reset();
            TearDownStreamline();
        }
    }

    HRESULT SwapChainProxy::Initialise(IDXGIFactory2* factory, ID3D11Device* device, HWND hwnd,
                                       const DXGI_SWAP_CHAIN_DESC1& desc,
                                       const DXGI_SWAP_CHAIN_FULLSCREEN_DESC* fullscreenDesc,
                                       IDXGIOutput* restrictToOutput)
    {
        window = hwnd;
        g_presentingWindow.store(hwnd);
        description = desc;

        // ID3D11Device5 and ID3D11DeviceContext4 exist from Windows 10 1703 and
        // are what makes a shared fence possible at all. Without them the two
        // APIs could only be synchronised by stalling the CPU every frame.
        HRESULT hr = device->QueryInterface(IID_PPV_ARGS(&d3d11Device));
        if (FAILED(hr))
        {
            LogLine("No ID3D11Device5 (" + Hr(hr) + ")");
            return hr;
        }

        ComPtr<ID3D11DeviceContext> context;
        device->GetImmediateContext(&context);
        hr = context.As(&d3d11Context);
        if (FAILED(hr))
        {
            LogLine("No ID3D11DeviceContext4 (" + Hr(hr) + ")");
            return hr;
        }

        // The D3D12 device has to sit on the same adapter, otherwise sharing a
        // texture between the two is cross-adapter and a different problem.
        ComPtr<IDXGIDevice> dxgiDevice;
        hr = device->QueryInterface(IID_PPV_ARGS(&dxgiDevice));
        if (FAILED(hr))
            return hr;

        ComPtr<IDXGIAdapter> adapter;
        hr = dxgiDevice->GetAdapter(&adapter);
        if (FAILED(hr))
            return hr;

        // Intel's: where auto asks for XeSS-FG, and XeLL runs without it.
        {
            DXGI_ADAPTER_DESC adapterDesc = {};
            intelAdapter = SUCCEEDED(adapter->GetDesc(&adapterDesc)) && adapterDesc.VendorId == 0x8086;
        }

        // Direct3D 12's debug layer for the harnesses, where REDEFINITION_D3D12_DEBUG is set:
        // its messages go into the log (Raytracing::Status). Never in a player's game.
        {
            wchar_t value[8] = {};
            if (GetEnvironmentVariableW(L"REDEFINITION_D3D12_DEBUG", value, 8) > 0 && value[0] == L'1')
            {
                ComPtr<ID3D12Debug> debug;
                if (SUCCEEDED(D3D12GetDebugInterface(IID_PPV_ARGS(&debug))))
                {
                    debug->EnableDebugLayer();
                    LogLine("Direct3D 12's debug layer on (REDEFINITION_D3D12_DEBUG)");
                }
            }
        }
        hr = D3D12CreateDevice(adapter.Get(), D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&d3d12Device));
        if (FAILED(hr))
        {
            LogLine("D3D12CreateDevice failed (" + Hr(hr) + ")");
            return hr;
        }

        // Streamline where DLSS frame generation runs, and, with frame generation
        // off, for DLSS Ray Reconstruction alone, which needs Streamline's swapchain
        // too: "you must ensure the common plugin's presentCommon() function is
        // called every frame" (ProgrammingGuideManualHooking.md 2.0). With frame
        // generation on and no DLSS-G, FSR's swapchain takes the place. Set up now,
        // because the queue has to come from Streamline's device proxy ("Mandatory -
        // ID3D12Device* eID3D12Device_CreateCommandQueue", sl_hooks.h).
        //
        // So Ray Reconstruction and FSR's or XeSS's frame generation exclude each
        // other: each needs the presenting swapchain to be its own, Streamline's,
        // FidelityFX's or XeSS-FG's, and there is one. With frame generation on and
        // DLSS-G not running, raytracing goes without Ray Reconstruction.
        const int technique = FrameGenerationTechniqueAsked();
        const bool dlssFrameGeneration = Config().frameGeneration
                                         && (technique == kFgTechniqueAuto || technique == kFgTechniqueDlss);
        xessWanted = Config().frameGeneration
                     && (technique == kFgTechniqueXess || (technique == kFgTechniqueAuto && intelAdapter));
        // For the status line: whose reason it gives where the wanted one does not run.
        g_fgTechniqueWanted.store(!Config().frameGeneration ? -1
                                  : xessWanted ? kFgTechniqueXess
                                  : dlssFrameGeneration ? kFgTechniqueDlss : kFgTechniqueFsr);
        const bool dlss = (dlssFrameGeneration || !Config().frameGeneration)
                          && SetUpStreamline(adapter.Get(), dlssFrameGeneration);

        D3D12_COMMAND_QUEUE_DESC queueDesc = {};
        queueDesc.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
        queueDesc.Flags = D3D12_COMMAND_QUEUE_FLAG_NONE;
        if (dlss)
        {
            // The proxy goes to the swapchain; the native queue behind it does
            // everything else here, as manual hooking asks: "use native
            // interfaces EVERYWHERE in the host application EXCEPT for the APIs
            // which are hooked by SL".
            void* native = nullptr;
            hr = streamlineDevice->CreateCommandQueue(&queueDesc, IID_PPV_ARGS(&streamlineQueue));
            if (SUCCEEDED(hr) && Streamline::Get().NativeInterface(streamlineQueue.Get(), &native))
                queue.Attach(static_cast<ID3D12CommandQueue*>(native));
            else
            {
                Streamline::Get().Note("no command queue through Streamline (" + Hr(hr)
                                       + ")" + Streamline::Get().Instead());
                TearDownStreamline();
            }
        }
        if (queue == nullptr)
        {
            hr = d3d12Device->CreateCommandQueue(&queueDesc, IID_PPV_ARGS(&queue));
            if (FAILED(hr))
                return hr;
        }

        // The swapchain Unity asked for, but driven by the D3D12 queue. Its
        // description already suits D3D12: Unity asks for FLIP_DISCARD, three
        // buffers, one sample and ALLOW_TEARING, which is exactly what the flip
        // model requires. Nothing has to be converted; the flags only must not
        // be thrown away.
        DXGI_SWAP_CHAIN_DESC1 swapDesc = desc;
        if (Config().allowTearing)
            swapDesc.Flags |= DXGI_SWAP_CHAIN_FLAG_ALLOW_TEARING;

        hr = CreatePresentationSwapChain(factory, hwnd, swapDesc, fullscreenDesc, restrictToOutput);
        if (FAILED(hr))
            return hr;

        // The flags the swapchain was created with, not the ones that were asked for:
        // CopyAndPresent reads these back to decide whether a tearing present is
        // allowed, and a mismatch there means DXGI rejects the present.
        description.Flags = swapDesc.Flags;
        ResolveTargetInterfaces();

        // The size the swapchain has: DXGI takes 0 for the window's.
        DXGI_SWAP_CHAIN_DESC1 created = {};
        if (SUCCEEDED(swapChain->GetDesc1(&created)))
        {
            description.Width = created.Width;
            description.Height = created.Height;
        }

        hr = CreateSharedColour(description.Width, description.Height, desc.Format);
        if (FAILED(hr))
            return hr;

        // Created on the D3D12 side and opened on the D3D11 side, so the queue
        // can wait on exactly the point in Unity's command stream where the
        // frame is finished.
        const char* failedCall = nullptr;
        hr = SharedFence(d3d11Device.Get(), d3d12Device.Get(), sharedFence12, sharedFence11, &failedCall);
        if (FAILED(hr))
        {
            if (strcmp(failedCall, "CreateSharedHandle") == 0)
                LogLine("CreateSharedHandle for the fence failed (" + Hr(hr) + ")");
            else if (strcmp(failedCall, "OpenSharedFence") == 0)
                LogLine("OpenSharedFence failed (" + Hr(hr) + ")");
            return hr;
        }

        for (UINT i = 0; i < kFrameCount; ++i)
        {
            hr = d3d12Device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT,
                                                     IID_PPV_ARGS(&allocators[i]));
            if (FAILED(hr))
                return hr;
        }

        hr = d3d12Device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT,
                                            allocators[0].Get(), nullptr,
                                            IID_PPV_ARGS(&commandList));
        if (FAILED(hr))
            return hr;
        commandList->Close();

        hr = d3d12Device->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&frameFence));
        if (FAILED(hr))
            return hr;

        frameEvent = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        if (frameEvent == nullptr)
            return HRESULT_FROM_WIN32(GetLastError());

        QueryPerformanceFrequency(reinterpret_cast<LARGE_INTEGER*>(&ticksPerSecond));

        // The managed side registers its textures whenever it likes; this is the
        // moment both devices exist for them to be shared against.
        AmdUpscaler::Get().Attach(this, d3d11Device.Get(), d3d11Context.Get(), d3d12Device.Get(), queue.Get());
        XessUpscaler::Get().Attach(this, d3d11Device.Get(), d3d11Context.Get(), d3d12Device.Get(), queue.Get());
        D3d12Compute::Get().Attach(this, d3d11Device.Get(), d3d11Context.Get(), d3d12Device.Get(), queue.Get());
        Raytracing::Get().Attach(this, d3d11Device.Get(), d3d11Context.Get(), d3d12Device.Get(), queue.Get());
        GpuProfiler::Get().Attach(this, d3d11Device.Get(), d3d11Context.Get());
        FrameGeneration::Get().Attach(this, d3d11Device.Get(), d3d11Context.Get(), d3d12Device.Get(),
                                      description.Width, description.Height, desc.Format);
        if (streamlineSwapChain && Streamline::Get().FrameGenerationAvailable())
            FrameGeneration::Get().UseStreamline(this);
        if (xessSwapChain)
            FrameGeneration::Get().UseXess(this, xessContext, xessFramesMost);
        // XeLL on an Intel GPU where neither Streamline's nor FidelityFX's swapchain
        // presents, as xell asks.
        bool xellLowLatency = xessSwapChain;
        if (xellContext == nullptr && intelAdapter && !streamlineSwapChain && fgSwapChainContext == nullptr)
            xellLowLatency = SetUpIntelXell();
        // Reflex where Streamline's does not run on this swapchain, or XeLL where it
        // was made.
        {
            LowLatency::XellSetup xell;
            xell.context = xellContext;
            xell.frameGeneration = xessSwapChain;
            xell.lowLatency = xellLowLatency;
            xell.intelGpu = intelAdapter;
            LowLatency::Get().Attach(this, d3d11Device.Get(), d3d12Device.Get(), queue.Get(),
                                     d3d12Device->GetAdapterLuid(),
                                     streamlineSwapChain && Streamline::Get().FrameGenerationAvailable(), xell);
        }
        g_deviceRemoved.store(S_OK);

        // The texture Unity believes is its backbuffer, and therefore the one
        // the HUD-less snapshot is copied from.
        FrameGeneration::Get().RegisterBackBuffer(this, sharedColour.Get());

        LogLine("D3D12 presentation path ready");
        return S_OK;
    }

    HRESULT SwapChainProxy::CreatePresentationSwapChain(
        IDXGIFactory2* factory, HWND hwnd, const DXGI_SWAP_CHAIN_DESC1& desc,
        const DXGI_SWAP_CHAIN_FULLSCREEN_DESC* fullscreenDesc,
        IDXGIOutput* restrictToOutput)
    {
        if (streamlineDevice != nullptr)
        {
            if (SUCCEEDED(CreateStreamlineSwapChain(factory, hwnd, desc, fullscreenDesc, restrictToOutput)))
                return S_OK;

            // Presenting has to work without it. The queue stays: the native one
            // behind Streamline's proxy is an ordinary queue.
            TearDownStreamline();
        }

        // XeSS-FG's route, FSR's below where it does not run.
        if (xessWanted && streamlineDevice == nullptr
            && SUCCEEDED(CreateXessSwapChain(factory, hwnd, desc, fullscreenDesc)))
            return S_OK;

        // The FidelityFX route. Its ForHwnd descriptor takes exactly the
        // arguments the proxy already holds, and hands back an ordinary IDXGISwapChain4
        // that presents normally until interpolation is configured on
        // (FrameGeneration).
        if (Config().frameGeneration && FidelityFx::Get().Ready())
        {
            DXGI_SWAP_CHAIN_DESC1 fgDesc = desc;
            DXGI_SWAP_CHAIN_FULLSCREEN_DESC fgFullscreen = {};
            if (fullscreenDesc != nullptr)
                fgFullscreen = *fullscreenDesc;

            IDXGISwapChain4* produced = nullptr;

            ffxCreateContextDescFrameGenerationSwapChainForHwndDX12 create = {};
            create.header.type = FFX_API_CREATE_CONTEXT_DESC_TYPE_FRAMEGENERATIONSWAPCHAIN_FOR_HWND_DX12;
            create.header.pNext = nullptr;
            create.swapchain = &produced;
            create.hwnd = hwnd;
            create.desc = &fgDesc;
            create.fullscreenDesc = fullscreenDesc != nullptr ? &fgFullscreen : nullptr;
            create.dxgiFactory = factory;
            create.gameQueue = queue.Get();

            const ffxReturnCode_t code = FidelityFx::Get().Api().CreateContext(
                &fgSwapChainContext, &create.header, nullptr);

            if (code == FFX_API_RETURN_OK && produced != nullptr)
            {
                swapChain.Attach(nullptr);
                const HRESULT hr = produced->QueryInterface(IID_PPV_ARGS(&swapChain));
                produced->Release();

                if (SUCCEEDED(hr))
                {
                    LogLine("Swapchain created through FidelityFX");
                    g_generatingSwapChains.fetch_add(1);
                    ApplyPacingTuning();
                    return S_OK;
                }

                LogLine("FidelityFX swapchain has no IDXGISwapChain3 (" + Hr(hr) + ")");
            }
            else
            {
                LogLine("FidelityFX swapchain creation failed, code "
                        + std::to_string(static_cast<int>(code)));
            }

            // Falling back rather than failing: an unavailable interpolation
            // path must still leave a game that renders.
            if (fgSwapChainContext != nullptr)
            {
                FidelityFx::Get().Api().DestroyContext(&fgSwapChainContext, nullptr);
                fgSwapChainContext = nullptr;
            }
        }

        ComPtr<IDXGISwapChain1> created;
        HRESULT hr = factory->CreateSwapChainForHwnd(queue.Get(), hwnd, &desc,
                                                     fullscreenDesc, restrictToOutput, &created);
        if (FAILED(hr))
        {
            LogLine("D3D12 CreateSwapChainForHwnd failed (" + Hr(hr) + ")");
            return hr;
        }

        return created.As(&swapChain);
    }
}
