// The proxy's XeSS side (SwapChainProxy): XeLL's context and XeSS-FG's swapchain
// made through Intel's DLLs (XessFg.h), torn down in Intel's order, and XeLL alone on
// an Intel GPU. The per-frame inputs are FrameGeneration's (FrameGenerationXess.cpp),
// the Present CopyAndPresent's.

#include "SwapChainProxy.h"

#include "Config.h"
#include "Log.h"
#include "SwapChainProxyState.h"
#include "XessFg.h"

#include <string>

using Microsoft::WRL::ComPtr;

namespace redefinition
{
    HRESULT SwapChainProxy::CreateXessSwapChain(IDXGIFactory2* factory, HWND hwnd, const DXGI_SWAP_CHAIN_DESC1& desc,
                                                const DXGI_SWAP_CHAIN_FULLSCREEN_DESC* fullscreenDesc)
    {
        XessFg& xess = XessFg::Get();
        if (!xess.LoadXell() || !xess.LoadFrameGeneration())
        {
            LogLine("XeSS frame generation does not run: " + xess.Why() + " -- FSR's instead");
            return E_FAIL;
        }

        // XeLL first: "XeSS-FG proxy swap chain will fail to initialize without XeLL
        // context", and "XeLL must be enabled before enabling frame generation"
        // (xess_fg_developer_guide_english.md, Connect with XeLL) -- set now, before
        // the GPU has any work: "application must ensure any GPU activity is finished
        // before xellSetSleepMode is called" (xell_developer_guide_english.md,
        // Integration Checklist). It stays on: "application must ensure that XeSS-FG is
        // enabled if running in cross-vendor mode", and latency reduction is what it
        // is there for.
        xellContext = xess.CreateXell(d3d12Device.Get());
        if (xellContext == nullptr || !xess.XellSetSleepMode(xellContext, true))
        {
            xess.DestroyXell(xellContext);
            xellContext = nullptr;
            LogLine("XeSS frame generation does not run: " + xess.Why() + " -- FSR's instead");
            return E_FAIL;
        }

        ComPtr<IDXGISwapChain3> created;
        HRESULT hr = xess.CreateSwapChain(d3d12Device.Get(), queue.Get(), factory, hwnd, desc, fullscreenDesc,
                                          xellContext, Config().fgDepthInverted, xessContext, created);

        // KSP asks for FRAME_LATENCY_WAITABLE_OBJECT and waits on its handle every
        // frame; a swapchain without one would stop Unity right after creation
        // (SwapChainProxyForwarding.cpp).
        if (SUCCEEDED(hr) && (desc.Flags & DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT) != 0)
        {
            ComPtr<IDXGISwapChain2> created2;
            if (FAILED(created.As(&created2)) || created2->GetFrameLatencyWaitableObject() == nullptr)
            {
                xess.Note("its swapchain has no frame latency waitable object, which KSP waits on");
                hr = E_NOINTERFACE;
            }
        }

        if (FAILED(hr))
        {
            created.Reset();
            xess.Destroy(xessContext);
            xessContext = nullptr;
            xess.DestroyXell(xellContext);
            xellContext = nullptr;
            LogLine("XeSS frame generation does not run -- FSR's instead");
            return hr;
        }

        swapChain = created;
        xessSwapChain = true;
        g_generatingSwapChains.fetch_add(1);
        g_xessSwapChains.fetch_add(1);
        LogLine("Swapchain created through XeSS-FG (XeSS frame generation, with XeLL)");
        return S_OK;
    }

    // "Release the proxy swap chain. ... xefgSwapChainDestroy(xefgContext);
    // xellDestroyContext(xellContext);" (xess_fg_developer_guide_english.md,
    // Shutdown), once every reference to it is gone: the proxy's own, and its
    // interfaces'. Nothing else keeps one -- the backbuffer is asked for and let go
    // within each Present.
    void SwapChainProxy::TearDownXess()
    {
        XessFg& xess = XessFg::Get();
        if (xessSwapChain)
        {
            swapChain.Reset();
            target2.Reset();
            target3.Reset();
            target4.Reset();
            g_generatingSwapChains.fetch_sub(1);
            g_xessSwapChains.fetch_sub(1);
            xessSwapChain = false;
        }
        xess.Destroy(xessContext);
        xessContext = nullptr;
        xess.DestroyXell(xellContext);
        xellContext = nullptr;
    }

    // XeLL on an Intel GPU without XeSS-FG: "Supported devices: Intel Arc A-Series or
    // later, any non-Intel GPU supporting XeSS-FG" -- on any other, "XeLL requires
    // XeSS-FG to be supported on the device in order to perform latency reduction"
    // (xell_developer_guide_english.md, Requirements, Cross-vendor support). Its mode
    // as lowLatency says, set before the first frame.
    void SwapChainProxy::SetUpIntelXell()
    {
        XessFg& xess = XessFg::Get();
        if (!xess.LoadXell())
        {
            LogLine("Low latency: no Intel XeLL on this Intel GPU -- " + xess.Why());
            return;
        }
        xellContext = xess.CreateXell(d3d12Device.Get());
        if (xellContext == nullptr)
            return;
        if (!xess.XellSetSleepMode(xellContext, Config().lowLatency))
        {
            xess.DestroyXell(xellContext);
            xellContext = nullptr;
        }
    }
}
