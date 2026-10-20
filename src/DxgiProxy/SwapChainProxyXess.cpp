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
        xellIntervalUs = 0;
        xellLimitRefresh = 0.0;
        if (asked.fgHalfRefreshLimit)
        {
            const double refresh = QueryWindowRefreshRate();
            if (refresh > 0.0)
            {
                xellLimitFps = 0.98 * refresh;
                minimumIntervalUs = static_cast<uint32_t>(1000000.0 / xellLimitFps + 0.5);
                xellLimitRefresh = refresh;
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
        xellIntervalUs = minimumIntervalUs;
        xellBoost = asked.xellLowLatencyBoost;

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
        // (Microsoft, IDXGISwapChain2::GetFrameLatencyWaitableObject). Intel's own sample
        // waits on XeSS-FG's swapchain's handle every frame (basic_sample_frame_generation,
        // -waitable), which is what xessFgOwnWaitable=0 hands out. auto and 1 hand out a
        // semaphore of the proxy's instead (ReleaseOwnWaitable), as OptiScaler does for
        // Unity games with XeSS-FG: KSP with XeSS-FG's own handle stopped for good right
        // after the first ResizeBuffers (a run of the standing comparison, killed after
        // 699 s), with the semaphore it runs. 0 and a swapchain without a handle: see above.
        ownWaitable = nullptr;
        if (SUCCEEDED(hr) && (desc.Flags & DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT) != 0)
        {
            ComPtr<IDXGISwapChain2> created2;
            const HANDLE waitable = SUCCEEDED(created.As(&created2)) ? created2->GetFrameLatencyWaitableObject() : nullptr;
            if (waitable != nullptr)
                CloseHandle(waitable);
            if (asked.xessFgOwnWaitable != 0 || waitable == nullptr)
            {
                // Full, and a Present releases one more: a wait never blocks.
                ownWaitable = CreateSemaphoreW(nullptr, 16, 16, nullptr);
                if (ownWaitable == nullptr)
                    xess.Note(waitable == nullptr ? "its swapchain has no frame latency waitable object, which KSP waits on"
                                                  : "the proxy's frame latency waitable object could not be made");
                else
                    LogLine(waitable == nullptr
                                ? "XeSS frame generation: its swapchain has no frame latency waitable object -- the proxy's"
                                  " semaphore stands in for it"
                                : "XeSS frame generation: the frame latency waitable object is the proxy's semaphore"
                                  " (xessFgOwnWaitable=1)");
            }
            if (ownWaitable == nullptr && waitable == nullptr)
                hr = E_NOINTERFACE;
        }

        if (FAILED(hr))
        {
            created.Reset();
            if (ownWaitable != nullptr)
                CloseHandle(ownWaitable);
            ownWaitable = nullptr;
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
        if (ownWaitable != nullptr)
            CloseHandle(ownWaitable);
        ownWaitable = nullptr;
    }

    void SwapChainProxy::ReleaseOwnWaitable()
    {
        if (ownWaitable != nullptr)
            ReleaseSemaphore(ownWaitable, 1, nullptr);
    }

    // XeLL's frame limit (fgHalfRefreshLimit) as the settings and the monitor's refresh
    // rate have it, the presented frames slightly below the refresh rate, as
    // CreateXessSwapChain made it. "The function call needs to be done only once after
    // settings change" (xell_developer_guide_english.md, Enable Features): so only when
    // the interval differs from the one set, and then once "any GPU activity is finished
    // before xellSetSleepMode is called" (Integration Checklist) -- the proxy's queue,
    // which XeSS-FG's work is on, has passed the last frame's Present. A refresh rate not
    // yet known leaves the limit as it stands.
    void SwapChainProxy::FollowXellLimit(double refresh)
    {
        if (!xessSwapChain || xellContext == nullptr || !Config().xellLimitLive)
            return;
        uint32_t wanted = xellIntervalUs;
        double fps = xellLimitFps;
        double usedRefresh = xellLimitRefresh;
        if (!Config().fgHalfRefreshLimit)
        {
            wanted = 0;
            fps = 0.0;
            usedRefresh = 0.0;
        }
        else if (refresh > 0.0)
        {
            fps = 0.98 * refresh;
            wanted = static_cast<uint32_t>(1000000.0 / fps + 0.5);
            usedRefresh = refresh;
        }
        if (wanted == xellIntervalUs)
        {
            xellLimitFps = fps;
            xellLimitRefresh = usedRefresh;
            return;
        }

        WaitForFrame(frameFenceValue);
        if (XessFg::Get().XellSetSleepMode(xellContext, true, wanted, xellBoost))
        {
            char hertz[32];
            std::snprintf(hertz, sizeof(hertz), "%.2f Hz", usedRefresh);
            LogLine(std::string("XeLL's frame limit follows (fgHalfRefreshLimit ")
                    + (Config().fgHalfRefreshLimit ? std::string("on, ") + hertz : std::string("off")) + "): "
                    + (wanted == 0 ? std::string("none") : std::to_string(wanted) + " us a frame"));
            xellIntervalUs = wanted;
            xellLimitFps = fps;
            xellLimitRefresh = usedRefresh;
        }
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
