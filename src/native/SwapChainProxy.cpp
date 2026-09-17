// The proxy swapchain (SwapChainProxy): made in place of Unity's, through
// Streamline, FidelityFX or DXGI, with the shared backbuffer Unity renders into,
// resized with it and torn down in the order the runtimes ask for. The present
// path, the pacing and the forwarded calls are in SwapChainProxyPresent.cpp,
// SwapChainProxyPacing.cpp and SwapChainProxyForwarding.cpp.

#include "SwapChainProxy.h"

#include "AmdUpscaler.h"
#include "Config.h"
#include "D3d12Compute.h"
#include "FidelityFx.h"
#include "FrameGeneration.h"
#include "Log.h"
#include "Streamline.h"
#include "SwapChainProxyState.h"

#include <FidelityFX/framegeneration/include/dx12/ffx_api_framegeneration_dx12.h>
#include <FidelityFX/framegeneration/include/ffx_framegeneration_api_types.h>

#include <cstdio>
#include <mutex>
#include <string>

using Microsoft::WRL::ComPtr;

namespace ksp
{
    int FrameGenerationTechnique()
    {
        if (g_streamlineSwapChains.load() > 0)
            return 2;
        return g_generatingSwapChains.load() > 0 ? 1 : 0;
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

    namespace
    {
        std::string Describe(const DXGI_SWAP_CHAIN_DESC1& desc)
        {
            return std::to_string(desc.Width) + "x" + std::to_string(desc.Height)
                 + " format " + std::to_string(static_cast<int>(desc.Format))
                 + " buffers " + std::to_string(desc.BufferCount)
                 + " swapEffect " + std::to_string(static_cast<int>(desc.SwapEffect))
                 + " flags 0x" + [&] { char b[16]; sprintf_s(b, "%X", desc.Flags); return std::string(b); }()
                 + " samples " + std::to_string(desc.SampleDesc.Count);
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

    HRESULT SwapChainProxy::CreateMeasuring(IUnknown* device, IDXGISwapChain1* real,
                                            const DXGI_SWAP_CHAIN_DESC1& desc,
                                            IDXGISwapChain1** result)
    {
        *result = nullptr;

        SwapChainProxy* proxy = new SwapChainProxy();
        proxy->measureOnly = true;
        proxy->realSwapChain = real;
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
        // whose Initialise failed.
        AmdUpscaler::Get().Detach(this);
        D3d12Compute::Get().Detach(this);
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

        // Streamline's swapchain, queue and device proxies, then Streamline
        // itself -- all before the native device, a member released after this.
        // Its shutdown lets go of everything it holds, the tagged inputs among
        // them, which it referenced for itself when they were tagged.
        if (streamlineDevice != nullptr)
        {
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

        hr = D3D12CreateDevice(adapter.Get(), D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&d3d12Device));
        if (FAILED(hr))
        {
            LogLine("D3D12CreateDevice failed (" + Hr(hr) + ")");
            return hr;
        }

        // DLSS frame generation where it runs: set up now, because its queue has
        // to come from Streamline's device proxy ("Mandatory - ID3D12Device*
        // eID3D12Device_CreateCommandQueue", sl_hooks.h).
        const bool dlss = Config().frameGeneration && Config().dlssFrameGeneration && SetUpStreamline(adapter.Get());

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
                                       + ") -- FSR frame generation instead");
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
        hr = d3d12Device->CreateFence(0, D3D12_FENCE_FLAG_SHARED, IID_PPV_ARGS(&sharedFence12));
        if (FAILED(hr))
            return hr;

        HANDLE fenceHandle = nullptr;
        hr = d3d12Device->CreateSharedHandle(sharedFence12.Get(), nullptr, GENERIC_ALL,
                                             nullptr, &fenceHandle);
        if (FAILED(hr))
        {
            LogLine("CreateSharedHandle for the fence failed (" + Hr(hr) + ")");
            return hr;
        }

        hr = d3d11Device->OpenSharedFence(fenceHandle, IID_PPV_ARGS(&sharedFence11));
        CloseHandle(fenceHandle);
        if (FAILED(hr))
        {
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
        D3d12Compute::Get().Attach(this, d3d11Device.Get(), d3d11Context.Get(), d3d12Device.Get(), queue.Get());
        FrameGeneration::Get().Attach(this, d3d11Device.Get(), d3d11Context.Get(), d3d12Device.Get(),
                                      description.Width, description.Height, desc.Format);
        if (streamlineSwapChain)
            FrameGeneration::Get().UseStreamline(this);
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

    bool SwapChainProxy::SetUpStreamline(IDXGIAdapter* adapter)
    {
        Streamline& streamline = Streamline::Get();
        if (!streamline.Acquire(this))
            return false;

        DXGI_ADAPTER_DESC adapterDesc = {};
        const HRESULT described = adapter->GetDesc(&adapterDesc);
        if (FAILED(described))
            streamline.Note("the adapter has no description (" + Hr(described) + ") -- FSR frame generation instead");
        if (FAILED(described) || !streamline.FrameGenerationSupported(adapterDesc.AdapterLuid)
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
            streamline.Note("Streamline did not upgrade the D3D12 device -- FSR frame generation instead");
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
            Streamline::Get().Note("Streamline did not upgrade the DXGI factory -- FSR frame generation instead");
            return E_FAIL;
        }
        ComPtr<IDXGIFactory> factoryProxy;
        factoryProxy.Attach(proxy);

        ComPtr<IDXGIFactory2> factoryProxy2;
        HRESULT hr = factoryProxy.As(&factoryProxy2);
        if (FAILED(hr))
        {
            Streamline::Get().Note("Streamline's factory has no IDXGIFactory2 (" + Hr(hr)
                                   + ") -- FSR frame generation instead");
            return hr;
        }

        ComPtr<IDXGISwapChain1> created;
        hr = factoryProxy2->CreateSwapChainForHwnd(streamlineQueue.Get(), hwnd, &desc, fullscreenDesc,
                                                   restrictToOutput, &created);
        if (FAILED(hr))
        {
            Streamline::Get().Note("CreateSwapChainForHwnd through Streamline failed (" + Hr(hr)
                                   + ") -- FSR frame generation instead");
            return hr;
        }

        hr = created.As(&swapChain);
        if (FAILED(hr))
        {
            Streamline::Get().Note("Streamline's swapchain has no IDXGISwapChain3 (" + Hr(hr)
                                   + ") -- FSR frame generation instead");
            return hr;
        }

        streamlineSwapChain = true;
        g_generatingSwapChains.fetch_add(1);
        g_streamlineSwapChains.fetch_add(1);
        LogLine("Swapchain created through Streamline (DLSS frame generation)");
        return S_OK;
    }

    void SwapChainProxy::TearDownStreamline()
    {
        if (streamlineSwapChain)
        {
            swapChain.Reset();
            g_generatingSwapChains.fetch_sub(1);
            g_streamlineSwapChains.fetch_sub(1);
            streamlineSwapChain = false;
        }
        streamlineQueue.Reset();
        streamlineDevice.Reset();
        nextFrameToken = nullptr;
        for (auto& fence : inputsFence)
            fence.Reset();
        Streamline::Get().Release(this);
    }

    bool SwapChainProxy::HasSharedColour()
    {
        if (sharedColour12 != nullptr)
            return true;
        if (!loggedNoSharedColour)
        {
            loggedNoSharedColour = true;
            LogLine("No shared backbuffer since the last resize failed -- nothing is presented until a resize"
                    " succeeds");
        }
        return false;
    }

    // Both halves or neither: Unity is never handed a texture that is not
    // presented.
    HRESULT SwapChainProxy::CreateSharedColour(UINT width, UINT height, DXGI_FORMAT format)
    {
        // Created on the D3D11 device and opened on the D3D12 one. The pair of
        // MISC flags is what makes an NT handle possible; without
        // SHARED_NTHANDLE the handle cannot be opened by D3D12 at all.
        D3D11_TEXTURE2D_DESC texture = {};
        texture.Width = width;
        texture.Height = height;
        texture.MipLevels = 1;
        texture.ArraySize = 1;
        texture.Format = format;
        texture.SampleDesc.Count = 1;
        texture.Usage = D3D11_USAGE_DEFAULT;
        texture.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
        texture.MiscFlags = D3D11_RESOURCE_MISC_SHARED | D3D11_RESOURCE_MISC_SHARED_NTHANDLE;

        HRESULT hr = d3d11Device->CreateTexture2D(&texture, nullptr, &sharedColour);
        if (FAILED(hr))
        {
            LogLine("Shared backbuffer texture could not be created (" + Hr(hr)
                    + "), format " + std::to_string(static_cast<int>(format)));
            return hr;
        }

        ComPtr<IDXGIResource1> resource;
        hr = sharedColour.As(&resource);
        if (FAILED(hr))
        {
            LogLine("The shared backbuffer texture has no IDXGIResource1 (" + Hr(hr) + ")");
            ReleaseSharedColour();
            return hr;
        }

        HANDLE handle = nullptr;
        hr = resource->CreateSharedHandle(nullptr,
                                          DXGI_SHARED_RESOURCE_READ | DXGI_SHARED_RESOURCE_WRITE,
                                          nullptr, &handle);
        if (FAILED(hr))
        {
            LogLine("CreateSharedHandle for the backbuffer failed (" + Hr(hr) + ")");
            ReleaseSharedColour();
            return hr;
        }

        hr = d3d12Device->OpenSharedHandle(handle, IID_PPV_ARGS(&sharedColour12));
        CloseHandle(handle);
        if (FAILED(hr))
        {
            LogLine("OpenSharedHandle for the backbuffer failed (" + Hr(hr) + ")");
            ReleaseSharedColour();
            return hr;
        }

        // A later loss is said again.
        loggedNoSharedColour = false;
        return S_OK;
    }

    void SwapChainProxy::ReleaseSharedColour()
    {
        sharedColour12.Reset();
        sharedColour.Reset();
    }

    HRESULT STDMETHODCALLTYPE SwapChainProxy::GetBuffer(UINT buffer, REFIID riid, void** surface)
    {
        // The substitution itself: Unity believes it is being handed the
        // swapchain's backbuffer and is handed the shared texture instead.
        if (measureOnly)
            return realSwapChain->GetBuffer(buffer, riid, surface);

        if (buffer != 0)
            LogLine("GetBuffer for index " + std::to_string(buffer)
                    + ", returning the shared texture anyway");

        if (sharedColour == nullptr)
            return DXGI_ERROR_INVALID_CALL;

        return sharedColour->QueryInterface(riid, surface);
    }

    HRESULT STDMETHODCALLTYPE SwapChainProxy::ResizeBuffers(UINT bufferCount, UINT width, UINT height,
                                                            DXGI_FORMAT format, UINT flags)
    {
        LogLine("ResizeBuffers to " + std::to_string(width) + "x" + std::to_string(height));

        if (measureOnly)
            return realSwapChain->ResizeBuffers(bufferCount, width, height, format, flags);

        // Nothing may still be reading the old buffers. Frame generation lets go
        // of the backbuffer first -- or its reference would keep the old texture
        // alive and the snapshot would copy a buffer nobody draws into -- and of
        // its context, which was made for the old size.
        PresentWithStreamlineOff("the swapchain is resized");
        WaitForFrame(frameFenceValue);
        FrameGeneration::Get().ReleaseBackBuffer(this, fgSwapChainContext != nullptr ? swapChain.Get() : nullptr);
        ReleaseSharedColour();

        const DXGI_FORMAT effectiveFormat = format == DXGI_FORMAT_UNKNOWN ? description.Format : format;

        // A swapchain has to keep the flag it was created with; losing it here
        // would make every later tearing present fail.
        const UINT effectiveFlags = flags | (description.Flags & DXGI_SWAP_CHAIN_FLAG_ALLOW_TEARING);

        HRESULT hr = swapChain->ResizeBuffers(bufferCount, width, height, format, effectiveFlags);
        if (FAILED(hr))
        {
            // The swapchain keeps its buffers when the resize fails, so Unity's
            // backbuffer comes back at their size: without one, the next Present
            // would have nothing to copy.
            const HRESULT restored = CreateSharedColour(description.Width, description.Height, description.Format);
            if (SUCCEEDED(restored))
                FrameGeneration::Get().RegisterBackBuffer(this, sharedColour.Get());
            LogLine("D3D12 ResizeBuffers failed (" + Hr(hr) + "), the shared backbuffer "
                    + (SUCCEEDED(restored) ? "made again at the old size" : "not made again (" + Hr(restored) + ")"));
            return hr;
        }

        // The size the swapchain has now: DXGI takes 0 for the window's.
        DXGI_SWAP_CHAIN_DESC1 resized = {};
        if (SUCCEEDED(swapChain->GetDesc1(&resized)))
        {
            width = resized.Width;
            height = resized.Height;
        }

        description.Width = width;
        description.Height = height;
        description.Format = effectiveFormat;
        if (bufferCount != 0)
            description.BufferCount = bufferCount;
        description.Flags = effectiveFlags;

        // The window may have changed monitor or mode with it.
        {
            std::lock_guard<std::mutex> lock(refreshMutex);
            refreshNow = true;
        }
        refreshWake.notify_all();
        limitDue = 0;

        hr = CreateSharedColour(width, height, effectiveFormat);
        if (FAILED(hr))
            return hr;

        FrameGeneration::Get().RegisterBackBuffer(this, sharedColour.Get());
        ResolveTargetInterfaces();
        return S_OK;
    }

    void SwapChainProxy::ResolveTargetInterfaces()
    {
        target2.Reset();
        target3.Reset();
        target4.Reset();

        IDXGISwapChain1* target = Target();
        if (target == nullptr)
            return;

        target->QueryInterface(IID_PPV_ARGS(&target2));
        target->QueryInterface(IID_PPV_ARGS(&target3));
        target->QueryInterface(IID_PPV_ARGS(&target4));

        LogLine(std::string("Target interfaces: SwapChain2=") + (target2 ? "yes" : "no")
                + " SwapChain3=" + (target3 ? "yes" : "no")
                + " SwapChain4=" + (target4 ? "yes" : "no"));
    }
}
