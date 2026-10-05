// FSR's frame generation context (FrameGeneration): made on first use, retried
// after a failure, configured off and destroyed in AMD's order.

#include "FrameGeneration.h"

#include "Config.h"
#include "FidelityFx.h"
#include "Log.h"

#include <FidelityFX/api/include/dx12/ffx_api_dx12.h>
#include <FidelityFX/framegeneration/include/ffx_framegeneration.h>
#include <FidelityFX/framegeneration/include/dx12/ffx_api_framegeneration_dx12.h>

#include <cstdlib>
#include <string>

namespace redefinition
{
    // ---------------------------------------------------------------- context

    void FrameGeneration::RebuildContextLocked(void* swapChain)
    {
        DestroyContextLocked(swapChain);
        ForgetContextFailuresLocked();
    }

    void FrameGeneration::ForgetContextFailuresLocked()
    {
        contextFailures = 0;
        contextFailing = kContextFine;
        contextEscalated = false;
    }

    bool FrameGeneration::AttemptWaitingLocked(ULONGLONG now) const
    {
        return contextFailing == kContextStands || (!contextCreated && now < contextRetryAfter);
    }

    void FrameGeneration::RetryContext()
    {
        std::lock_guard<std::mutex> lock(mutex);
        ForgetContextFailuresLocked();
        contextRetryAfter = 0;
    }

    bool FrameGeneration::EnsureContextLocked()
    {
        if (contextCreated)
            return true;

        if (!FidelityFx::Get().Ready() || device12 == nullptr)
            return false;

        // After a failure not with every frame: once a second, and not at all
        // while one stands.
        const ULONGLONG now = GetTickCount64();
        if (AttemptWaitingLocked(now))
            return false;

        ffxCreateContextDescFrameGeneration create = {};
        create.header.type = FFX_API_CREATE_CONTEXT_DESC_TYPE_FRAMEGENERATION;
        create.displaySize = { displayWidth, displayHeight };
        create.maxRenderSize = { displayWidth, displayHeight };
        create.backBufferFormat = ffxApiGetSurfaceFormatDX12(backBufferFormat);

        // No HIGH_DYNAMIC_RANGE: the flag describes the backbuffer, which is
        // R8G8B8A8_UNORM.
        //
        // DEPTH_INVERTED for Unity's reversed Z buffer, as the bundled upscaler
        // sets it for itself. It is the only thing that tells FSR which way the
        // depth runs: its source takes min and max of near and far, so the
        // order the planes are passed in carries no information. See Config.h.
        create.flags = Config().fgDepthInverted ? FFX_FRAMEGENERATION_ENABLE_DEPTH_INVERTED : 0u;

        // Interpolation on the swapchain's compute queue, when asked for: its
        // "One asynchronous compute queue - only used when
        // FFX_FRAMEGENERATION_ENABLE_ASYNC_WORKLOAD_SUPPORT is set on FSR3
        // context creation and allowAsyncWorkloads is true"
        // (frame-interpolation-swap-chain.md).
        const bool async = Config().fgAsyncWorkloads;
        if (async)
            create.flags |= FFX_FRAMEGENERATION_ENABLE_ASYNC_WORKLOAD_SUPPORT;
        // AMD's checks of what the proxy hands it: "will output textual warnings ...
        // Calling ffxConfigure with fpMessage within a ffxConfigureDescGlobalDebug1
        // structure ... allows the application to receive the debug messages issued"
        // (frame-interpolation-api.md, Debug Checker).
        const bool checked = Config().vendorDiagnostics;
        if (checked)
            create.flags |= FFX_FRAMEGENERATION_ENABLE_DEBUG_CHECKING;

        ffxCreateBackendDX12Desc backend = {};
        backend.header.type = FFX_API_CREATE_CONTEXT_DESC_TYPE_BACKEND_DX12;
        backend.device = device12.Get();

        // Required since SDK 2.1; without it the call fails at run time.
        ffxCreateContextDescFrameGenerationVersion version = {};
        version.header.type = FFX_API_CREATE_CONTEXT_DESC_TYPE_FRAMEGENERATION_VERSION;
        version.version = FFX_FRAMEGENERATION_VERSION;

        // The descriptors form a linked list.
        create.header.pNext = &backend.header;
        backend.header.pNext = &version.header;
        version.header.pNext = nullptr;

        const ffxReturnCode_t code =
            FidelityFx::Get().Api().CreateContext(&context, &create.header, nullptr);

        if (code != FFX_API_RETURN_OK)
        {
            context = nullptr;
            contextRetryAfter = now + 1000;
            ++contextFailures;
            // What ffx_api.h calls "likely a programming error" or "fixed with
            // driver upgrade or effect DLL upgrade", and a rejected parameter,
            // stand. An unspecified, runtime or memory error may pass; ten of them
            // in a row are taken to stand too, since which code a lasting failure
            // returns is the provider's to choose.
            const bool standsByCode = code == FFX_API_RETURN_ERROR_UNKNOWN_DESCTYPE
                                      || code == FFX_API_RETURN_NO_PROVIDER || code == FFX_API_RETURN_ERROR_PARAMETER
                                      || code == FFX_API_RETURN_PROVIDER_NO_SUPPORT_NEW_DESCTYPE;
            const bool stands = standsByCode || contextFailures >= kAttemptsBeforeStanding;
            contextFailing = stands ? kContextStands : kContextRetrying;
            contextEscalated = stands && !standsByCode;
            const std::string failed = "Frame generation context creation failed, code " + std::to_string(static_cast<int>(code));
            if (standsByCode)
                LogLine(failed + " (not tried again before a new swapchain, a new scene, an ini change or frame"
                                 " generation switched on again)");
            else if (stands)
                LogLine(failed + ", " + std::to_string(contextFailures) + " attempts in a row (not tried again before"
                                 " a new swapchain or size, a new scene, an ini change or frame generation switched"
                                 " on again)");
            else if (contextFailures <= 3)
                LogLine(failed + " (attempt " + std::to_string(contextFailures) + ", tried again once a second)");
            return false;
        }

        contextCreated = true;
        contextRetryAfter = 0;
        ForgetContextFailuresLocked();
        if (checked)
        {
            ffxConfigureDescGlobalDebug1 debug = {};
            debug.header.type = FFX_API_CONFIGURE_DESC_TYPE_GLOBALDEBUG1;
            debug.fpMessage = &FfxDebugMessage;
            debug.debugLevel = FFX_API_CONFIGURE_GLOBALDEBUG_LEVEL_VERBOSE;
            const ffxReturnCode_t configured = FidelityFx::Get().Api().Configure(&context, &debug.header);
            LogLine("Frame generation: AMD's debug checker on (" + std::string(configured == FFX_API_RETURN_OK
                                                                                  ? "its messages go into this log)"
                                                                                  : "its messages refused, code "
                                                                                        + std::to_string(static_cast<int>(configured)) + ")"));
        }
        contextAsync = async;
        contextWidth = displayWidth;
        contextHeight = displayHeight;
        loggedFirstDispatch = false;

        // Which frame generation runs, and the input frame rate AMD designed it
        // for: "AMD FidelityFX Super Resolution Frame Generation 3.1.6 has been
        // designed for an input framerate of at least 60 FPS", "AMD FSR Frame
        // Generation 4.0.1 has been designed for an input framerate of at least
        // 30 FPS" (frame-interpolation-swap-chain.md, Minimum Framerate). A name
        // that does not start with a major version of 4 or more counts as the
        // stricter one.
        std::string provider = "unknown";
        ffxQueryGetProviderVersion providerVersion = {};
        providerVersion.header.type = FFX_API_QUERY_DESC_TYPE_GET_PROVIDER_VERSION;
        if (FidelityFx::Get().Api().Query(&context, &providerVersion.header) == FFX_API_RETURN_OK
            && providerVersion.versionName != nullptr)
            provider = providerVersion.versionName;
        const int designedFor = std::atoi(provider.c_str()) >= 4 ? 30 : 60;
        designedForFps = designedFor;

        LogLine("Frame generation context created for " + std::to_string(displayWidth) + "x"
                + std::to_string(displayHeight) + ", depthInverted="
                + (Config().fgDepthInverted ? "1" : "0") + ", asyncWorkloads=" + (async ? "1" : "0")
                + ", provider " + provider + " (designed for " + std::to_string(designedFor)
                + " fps input and more)");
        return true;
    }

    // AMD's shutdown order: "Disable frame generation and UI composition on the
    // proxy swap chain via ffx::Configure. This configure call waits for the
    // proxy swap chain to complete GPU work ... that references Frame
    // Generation resources." Only then does the context go.
    void FrameGeneration::DestroyContextLocked(void* swapChain)
    {
        if (!contextCreated)
            return;

        if (swapChain != nullptr)
            ConfigureOffLocked(swapChain, "the context is being destroyed");

        FidelityFx::Get().Api().DestroyContext(&context, nullptr);
        context = nullptr;
        contextCreated = false;
        contextSwapChain = nullptr;
        contextAsync = false;
        configuredOn = false;
        LogLine("Frame generation context destroyed");
    }

    // A frame without generation: switched off in the mod, no inputs this
    // frame, or on the way out. Configure runs all the same -- "This must be
    // called once per frame" -- with frameGenerationEnabled false, as AMD's
    // sample sets it every frame, and without the callback. Logged when the
    // state changes, not every frame.
    void FrameGeneration::ConfigureOffLocked(void* swapChain, const char* reason)
    {
        ffxConfigureDescFrameGeneration config = {};
        config.header.type = FFX_API_CONFIGURE_DESC_TYPE_FRAMEGENERATION;
        config.swapChain = swapChain;
        config.frameGenerationEnabled = false;
        // As when on: the swapchain switches its interpolation queue whenever
        // this differs from the last configure -- waiting for its presents,
        // dropping outstanding command lists, resetting its fence
        // (FrameInterpolationSwapchainDX12.cpp, setFrameGenerationConfig).
        config.allowAsyncWorkloads = contextAsync;
        config.frameGenerationCallback = nullptr;
        config.frameGenerationCallbackUserContext = nullptr;
        config.HUDLessColor = FfxApiResource{};
        config.frameID = frameId;
        config.flags = 0;
        config.generationRect = { 0, 0, static_cast<int32_t>(displayWidth),
                                  static_cast<int32_t>(displayHeight) };

        const ffxReturnCode_t code = FidelityFx::Get().Api().Configure(&context, &config.header);
        contextSwapChain = swapChain;

        if (code != FFX_API_RETURN_OK)
        {
            if (!loggedOffFailure)
                LogLine("Frame generation could not be configured off, code "
                        + std::to_string(static_cast<int>(code)));
            loggedOffFailure = true;
        }
        else if (configuredOn)
        {
            LogLine(std::string("Frame generation off: ") + reason);
        }

        configuredOn = false;
    }
}
