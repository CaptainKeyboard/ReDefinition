// The proxy's XeSS side (SwapChainProxy): XeLL's context and XeSS-FG's swapchain
// made through Intel's DLLs (XessFg.h), torn down in Intel's order, and XeLL alone on
// an Intel GPU. The per-frame inputs are FrameGeneration's (FrameGenerationXess.cpp),
// the Present CopyAndPresent's.

#include "SwapChainProxy.h"

#include "Config.h"
#include "Log.h"
#include "SwapChainProxyState.h"
#include "XessFg.h"

#include <cstdio>
#include <string>

using Microsoft::WRL::ComPtr;

namespace redefinition
{
    HRESULT SwapChainProxy::CreateXessSwapChain(IDXGIFactory2* factory, HWND hwnd, const DXGI_SWAP_CHAIN_DESC1& desc,
                                                const DXGI_SWAP_CHAIN_FULLSCREEN_DESC* fullscreenDesc)
    {
        // "Currently XeSS-FG does not support multiple proxy swap chains operating at
        // the same time" (xess_fg_developer_guide_english.md, Initialization): a
        // swapchain made while an earlier one's XeSS-FG lives presents with FSR for as
        // long as it lives, as one made while Streamline belongs to another
        // (Streamline::Acquire). Logged only: the state stays the earlier one's, whose
        // XeSS-FG still runs.
        if (g_xessSwapChains.load() > 0)
        {
            LogLine("XeSS frame generation does not run on this swapchain: an earlier swapchain's XeSS-FG still lives,"
                    " and \"Currently XeSS-FG does not support multiple proxy swap chains operating at the same time\""
                    " -- FSR's instead");
            return E_FAIL;
        }

        XessFg& xess = XessFg::Get();
        if (!xess.LoadXell() || !xess.LoadFrameGeneration())
        {
            LogLine("XeSS frame generation does not run: " + xess.Why() + " -- FSR's instead");
            return E_FAIL;
        }

        // What the swapchain is made with, as the file has it now (Config.h).
        const ProxyConfig asked = ConfigAsked();

        // XeLL's frame limit for fgHalfRefreshLimit, the presented frames slightly below
        // the refresh rate, as FSR's rendered ones are kept slightly below half of it:
        // "XeLL will limit the frame rate to half of the target ensuring that the total
        // count of presented frames (both generated and rendered) matches the requested
        // FPS" (xell_developer_guide_english.md, XeSS-FG Support). The proxy's own limit
        // stays off with it: "FPS Cap is controlled via xellSetSleepMode API when Low
        // Latency mode is active" (Integration Checklist).
        uint32_t minimumIntervalUs = 0;
        xellLimitFps = 0.0;
        if (asked.fgHalfRefreshLimit)
        {
            const double refresh = QueryWindowRefreshRate();
            if (refresh > 0.0)
            {
                xellLimitFps = 0.98 * refresh;
                minimumIntervalUs = static_cast<uint32_t>(1000000.0 / xellLimitFps + 0.5);
                char line[160];
                std::snprintf(line, sizeof(line), "XeLL's frame limit (fgHalfRefreshLimit): %.1f frames presented a"
                              " second at %.2f Hz", xellLimitFps, refresh);
                LogLine(line);
            }
            else
                LogLine("XeLL's frame limit (fgHalfRefreshLimit): the monitor's refresh rate is not known -- no limit");
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
        if (xellContext == nullptr
            || !xess.XellSetSleepMode(xellContext, true, minimumIntervalUs, asked.xellLowLatencyBoost))
        {
            xess.DestroyXell(xellContext);
            xellContext = nullptr;
            xellLimitFps = 0.0;
            LogLine("XeSS frame generation does not run: " + xess.Why() + " -- FSR's instead");
            return E_FAIL;
        }

        XessFg::SwapChainOptions options;
        options.invertedDepth = Config().fgDepthInverted;
        options.frames = static_cast<uint32_t>(asked.xessFgFrames);
        options.uiMode = asked.xessFgUiMode;
        options.intelGpu = intelAdapter;
        ComPtr<IDXGISwapChain3> created;
        HRESULT hr = xess.CreateSwapChain(d3d12Device.Get(), queue.Get(), factory, hwnd, desc, fullscreenDesc,
                                          xellContext, options, xessContext, created, xessFramesMost);

        // KSP asks for FRAME_LATENCY_WAITABLE_OBJECT and waits on its handle every
        // frame; a swapchain without one would stop Unity right after creation
        // (SwapChainProxyForwarding.cpp). The handle asked for here is closed again:
        // "When you are done with the handle, use CloseHandle to close the handle"
        // (Microsoft, IDXGISwapChain2::GetFrameLatencyWaitableObject).
        if (SUCCEEDED(hr) && (desc.Flags & DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT) != 0)
        {
            ComPtr<IDXGISwapChain2> created2;
            const HANDLE waitable = SUCCEEDED(created.As(&created2)) ? created2->GetFrameLatencyWaitableObject() : nullptr;
            if (waitable == nullptr)
            {
                xess.Note("its swapchain has no frame latency waitable object, which KSP waits on");
                hr = E_NOINTERFACE;
            }
            else
                CloseHandle(waitable);
        }

        if (FAILED(hr))
        {
            created.Reset();
            xess.Destroy(xessContext);
            xessContext = nullptr;
            xessFramesMost = 0;
            xess.DestroyXell(xellContext);
            xellContext = nullptr;
            xellLimitFps = 0.0;
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
    // (xell_developer_guide_english.md, Requirements, Cross-vendor support). Only where
    // xell asks for it: "The application can disable XeLL when frame generation is
    // disabled" (xess_fg_developer_guide_english.md, Connect with XeLL). Its mode as
    // lowLatency says, set before the first frame; no frame limit, which
    // fgHalfRefreshLimit sets only while frames are generated.
    bool SwapChainProxy::SetUpIntelXell()
    {
        const ProxyConfig asked = ConfigAsked();
        if (!asked.xell)
        {
            LogLine("Low latency: Intel XeLL not made (xell=0 in ReDefinitionProxy.ini)");
            return false;
        }
        XessFg& xess = XessFg::Get();
        if (!xess.LoadXell())
        {
            LogLine("Low latency: no Intel XeLL on this Intel GPU -- " + xess.Why());
            return false;
        }
        xellContext = xess.CreateXell(d3d12Device.Get());
        if (xellContext == nullptr)
            return false;
        if (!xess.XellSetSleepMode(xellContext, asked.lowLatency, 0, asked.xellLowLatencyBoost))
        {
            xess.DestroyXell(xellContext);
            xellContext = nullptr;
            return false;
        }
        return asked.lowLatency;
    }
}
