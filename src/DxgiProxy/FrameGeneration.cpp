// Frame generation (FrameGeneration): who owns it, FSR's context -- made, retried,
// configured off and destroyed in AMD's order -- and FSR's preparation for each
// Present. The shared inputs are in FrameGenerationInputs.cpp, the HUD-less and
// motion vector checks in FrameGenerationCheck.cpp, DLSS-G in
// FrameGenerationDlss.cpp.

#include "FrameGeneration.h"

#include "Config.h"
#include "FidelityFx.h"
#include "Log.h"

#include <FidelityFX/api/include/dx12/ffx_api_dx12.h>
#include <FidelityFX/framegeneration/include/ffx_framegeneration.h>
#include <FidelityFX/framegeneration/include/dx12/ffx_api_framegeneration_dx12.h>

#include <atomic>
#include <cstdio>
#include <cstdlib>
#include <string>

using Microsoft::WRL::ComPtr;

namespace redefinition
{
    DXGI_FORMAT TypedFormat(DXGI_FORMAT format)
    {
        switch (format)
        {
        case DXGI_FORMAT_R32_TYPELESS: return DXGI_FORMAT_R32_FLOAT;
        case DXGI_FORMAT_R16G16_TYPELESS: return DXGI_FORMAT_R16G16_FLOAT;
        case DXGI_FORMAT_R16G16B16A16_TYPELESS: return DXGI_FORMAT_R16G16B16A16_FLOAT;
        case DXGI_FORMAT_R8G8B8A8_TYPELESS: return DXGI_FORMAT_R8G8B8A8_UNORM;
        case DXGI_FORMAT_B8G8R8A8_TYPELESS: return DXGI_FORMAT_B8G8R8A8_UNORM;
        default: return format;
        }
    }

    namespace
    {
        // The swapchain calls this when it wants an interpolated frame, and it
        // is forwarded to the frame generation context. Without it the context
        // exists and the prepare pass runs, but nothing asks for the work, and
        // no frame is generated.
        ffxReturnCode_t FrameGenerationDispatch(ffxDispatchDescFrameGeneration* params, void* userContext)
        {
            if (params == nullptr || userContext == nullptr)
                return FFX_API_RETURN_ERROR_PARAMETER;

            // The frame ID: "Must increment by exactly one (1) for each frame"
            // (ffx_framegeneration.h). Asked twice for the same frame, nothing is
            // generated the second time rather than a frame from inputs already
            // spent. The swapchain shows a generated frame whenever
            // numGeneratedFrames is above zero, whatever this returns ("if
            // (desc.numGeneratedFrames > 0)", FrameInterpolationSwapchainDX12.cpp),
            // so it is zeroed on every way out without one; an error return has
            // the swapchain drop its command list instead of submitting it empty.
            static std::atomic<uint64_t> lastFrameId{ UINT64_MAX };
            if (lastFrameId.exchange(params->frameID) == params->frameID)
            {
                params->numGeneratedFrames = 0;
                return FFX_API_RETURN_ERROR;
            }

            const ffxReturnCode_t code = FidelityFx::Get().Api().Dispatch(
                reinterpret_cast<ffxContext*>(userContext), &params->header);
            if (code != FFX_API_RETURN_OK)
                params->numGeneratedFrames = 0;
            return code;
        }
    }

    // ---------------------------------------------------------- FrameGeneration

    FrameGeneration& FrameGeneration::Get()
    {
        static FrameGeneration instance;
        return instance;
    }

    void FrameGeneration::Attach(const void* newOwner, ID3D11Device* newDevice11, ID3D11DeviceContext* newContext11,
                                 ID3D12Device* newDevice12, UINT width, UINT height,
                                 DXGI_FORMAT format)
    {
        std::lock_guard<std::mutex> lock(mutex);

        owner = newOwner;
        device11 = newDevice11;
        context11 = newContext11;
        device12 = newDevice12;
        displayWidth = width;
        displayHeight = height;
        backBufferFormat = format;
        ForgetContextFailuresLocked();

        // FSR's until the owner says otherwise (UseStreamline). DLSS-G off first on
        // the swapchain of an earlier owner that still lives: nothing turns it off
        // there once this one owns the inputs -- Streamline stays that swapchain's
        // (Streamline::Acquire), so the options still reach it.
        StreamlineOffLocked("frame generation belongs to a swapchain made after this one");
        streamline = false;
        streamlineOn = false;
        streamlineMultiplier = 0;
    }

    void FrameGeneration::Detach(const void* caller, void* swapChain)
    {
        std::lock_guard<std::mutex> lock(mutex);

        // A proxy that never attached -- measuring pass-through, a failed
        // Initialise -- has nothing to detach; one that lost ownership is
        // worth a line.
        if (caller != owner)
        {
            if (owner != nullptr)
                LogLine("Detach from a swapchain that does not own frame generation -- ignored");
            return;
        }

        DestroyContextLocked(swapChain);
        ForgetContextFailuresLocked();

        for (Input* input : { &depth, &motion, &hudLess })
        {
            input->buffers[0].Reset();
            input->buffers[1].Reset();
            input->unityView.Reset();
            input->unity.Reset();
        }

        // The proxy's destructor waited for the GPU before this, so nothing
        // reads them any more.
        retired.clear();

        for (auto& shader : flipShaders)
            shader.Reset();
        flipReady = false;

        ReleaseCheckLocked();
        backBuffer11.Reset();
        packetFresh = false;
        inputsCopied = false;

        streamline = false;
        streamlineOn = false;
        streamlineFrames = 1;
        streamlineFramesSet = 1;
        streamlineStatusKnown = false;
        streamlineRetryAfter = 0;
        streamlineGrace = 0;
        streamlineOffStatus = 0;
        streamlineMultiplier = 0;
        loggedUnflipped = false;
        loggedFlipMissing = false;

        context11.Reset();
        device11.Reset();
        device12.Reset();
        owner = nullptr;
    }

    void FrameGeneration::SetEnabled(bool enabled)
    {
        if (wanted.load() == enabled)
            return;
        std::lock_guard<std::mutex> lock(mutex);
        if (wanted == enabled)
            return;

        wanted = enabled;
        LogLine(std::string("Frame generation requested: ") + (enabled ? "on" : "off"));
    }

    bool FrameGeneration::GeneratedLastPresent() const
    {
        std::lock_guard<std::mutex> lock(mutex);
        return generatedLastPresent;
    }

    bool FrameGeneration::Enabled() const
    {
        std::lock_guard<std::mutex> lock(mutex);
        return wanted;
    }

    bool FrameGeneration::Ready() const
    {
        std::lock_guard<std::mutex> lock(mutex);
        return contextCreated;
    }

    bool FrameGeneration::ContextSize(UINT& width, UINT& height) const
    {
        std::lock_guard<std::mutex> lock(mutex);
        width = contextWidth;
        height = contextHeight;
        return contextCreated;
    }

    bool FrameGeneration::ContextAsync() const
    {
        std::lock_guard<std::mutex> lock(mutex);
        return contextCreated && contextAsync;
    }

    bool FrameGeneration::HasInputs() const
    {
        std::lock_guard<std::mutex> lock(mutex);
        return S(depth).Valid() && S(motion).Valid();
    }

    bool FrameGeneration::HasHudLess() const
    {
        std::lock_guard<std::mutex> lock(mutex);
        return S(hudLess).Valid();
    }

    void FrameGeneration::LastHudLessCheck(float& direct, float& mirrored) const
    {
        std::lock_guard<std::mutex> lock(mutex);
        direct = lastDirect;
        mirrored = lastMirrored;
    }

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

    // Everything handed to FSR, in one line.
    void FrameGeneration::LogInputsLocked() const
    {
        char line[512] = {};
        FormatTo(line,
                  "FG inputs: frame %u  render %ux%u  jitter %.4f/%.4f  mvScale %.1f/%.1f  "
                  "near %.3f far %.1f fovY %.4f  dt %.2f ms  reset %u  flipped %d  "
                  "pos %.1f/%.1f/%.1f  fwd %.3f/%.3f/%.3f  up %.3f/%.3f/%.3f  "
                  "hudless captures %llu",
                  packet.frameIndex, packet.renderWidth, packet.renderHeight,
                  packet.jitterX, packet.jitterY,
                  packet.motionVectorScaleX, packet.motionVectorScaleY,
                  packet.nearPlane, packet.farPlane, packet.verticalFovRadians,
                  packet.frameTimeDeltaMs, packet.reset, flippedLastCopy ? 1 : 0,
                  packet.position[0], packet.position[1], packet.position[2],
                  packet.forward[0], packet.forward[1], packet.forward[2],
                  packet.up[0], packet.up[1], packet.up[2],
                  static_cast<unsigned long long>(hudLessCaptures));

        LogLine(line);
    }

    bool FrameGeneration::PrepareForPresentLocked(void* swapChain, ID3D12GraphicsCommandList* commandList)
    {
        // Taken and cleared before anything can return early, so neither a
        // packet nor a set of inputs ever carries over into a later frame.
        const bool thisFrame = packetFresh && inputsCopied;
        packetFresh = false;
        inputsCopied = false;
        generatedLastPresent = false;

        if (swapChain == nullptr || commandList == nullptr)
            return false;

        // Only frames whose own depth and motion vectors arrived. Switched off
        // in the mod, or no upscaler dispatch this frame -- a scene change, a
        // menu -- and the last inputs would be stale.
        const bool generate = wanted && thisFrame && S(depth).Valid() && S(motion).Valid();

        // The context is created on first use. Until then there is nothing to
        // configure, and the swapchain presents like a plain one.
        if (generate && !EnsureContextLocked())
            return false;
        if (!contextCreated)
            return false;

        // From here on every frame is configured, on or off, with an ID exactly
        // one higher than the last: "This must be called once per frame. The
        // frame ID must increment by exactly 1 each frame. Any other difference
        // between consecutive frames will reset frame generation logic."
        ++frameId;

        if (!generate)
        {
            ConfigureOffLocked(swapChain, !wanted ? "switched off in the mod" : "no inputs this frame");
            return false;
        }

        const ProxyConfig& cfg = Config();

        ffxConfigureDescFrameGeneration config = {};
        config.header.type = FFX_API_CONFIGURE_DESC_TYPE_FRAMEGENERATION;
        config.swapChain = swapChain;
        config.frameGenerationEnabled = true;
        config.allowAsyncWorkloads = contextAsync;
        config.frameGenerationCallback = &FrameGenerationDispatch;
        config.frameGenerationCallbackUserContext = &context;
        config.frameID = frameId;
        config.flags = 0;

        // The whole backbuffer. Left zeroed, FSR is told the region to generate
        // into is empty; the documented use of a smaller rect is letterboxing,
        // which KSP does not use.
        config.generationRect = { 0, 0, static_cast<int32_t>(displayWidth),
                                  static_cast<int32_t>(displayHeight) };

        // This frame's HUD-less copy, or nothing at all.
        //
        // AMD offers three ways to keep the UI out of interpolation. This is the
        // one its documentation says was added "for compatibility with engines
        // that can not apply either of the other two options" -- a present
        // callback that draws the UI again, or the UI in a texture of its own --
        // which is KSP: its UI and every mod's OnGUI window go straight onto the
        // backbuffer, and nothing here can draw them a second time.
        //
        // COMPUTE_READ, as in AMD's sample. Two copies in turn, like depth and
        // motion vectors (CaptureForPresent): with async workloads "the HUDLess
        // texture needs to be double buffered by the application"; without them
        // FSR reads it on the game queue, and the proxy's fence after Present
        // keeps D3D11 from overwriting it early.
        const bool useHudLess = cfg.hudLessColour && S(hudLess).Valid();
        if (useHudLess)
            config.HUDLessColor = S(hudLess).AsFfx(FFX_API_RESOURCE_USAGE_READ_ONLY,
                                                       FFX_API_RESOURCE_STATE_COMPUTE_READ);
        else
            config.HUDLessColor = FfxApiResource{};

        if (useHudLess != lastUsedHudLess || hudLessStatesLogged == 0)
        {
            if (hudLessStatesLogged < 20)
            {
                if (useHudLess)
                    LogLine("HUD-less copy in use, " + std::to_string(hudLessCaptures) + " taken so far");
                else if (!cfg.hudLessColour)
                    LogLine("HUD-less off: the UI is interpolated with the scene");
                else
                    LogLine("HUD-less wanted, but no HUD-less texture is registered -- the UI is interpolated");

                if (++hudLessStatesLogged == 20)
                    LogLine("(further HUD-less state changes are not logged)");
            }

            lastUsedHudLess = useHudLess;
        }

        ffxReturnCode_t code = FidelityFx::Get().Api().Configure(&context, &config.header);
        if (code != FFX_API_RETURN_OK)
        {
            LogLine("Frame generation configure failed, code " + std::to_string(static_cast<int>(code)));
            return false;
        }

        // Coming back from off is a discontinuity, like a camera cut: the last
        // frame FSR saw may be seconds old and from another scene.
        const bool resumed = !configuredOn;
        configuredOn = true;

        // The upscaler's scale carries over unchanged ("the same input
        // requirements and recommendations apply"), except that a vertical
        // flip of the texture turns its Y direction round with it.
        const float negate = cfg.fgNegateMotionScale ? -1.0f : 1.0f;
        const float flipY = flippedLastCopy ? -1.0f : 1.0f;

        ffxDispatchDescFrameGenerationPrepareV2 prepare = {};
        prepare.header.type = FFX_API_DISPATCH_DESC_TYPE_FRAMEGENERATION_PREPARE_V2;
        prepare.frameID = frameId;
        prepare.commandList = commandList;
        prepare.renderSize = { packet.renderWidth != 0 ? packet.renderWidth : displayWidth,
                               packet.renderHeight != 0 ? packet.renderHeight : displayHeight };
        prepare.jitterOffset = { packet.jitterX, packet.jitterY * flipY };
        prepare.motionVectorScale = { packet.motionVectorScaleX * negate,
                                      packet.motionVectorScaleY * negate * flipY };
        prepare.frameTimeDelta = packet.frameTimeDeltaMs;
        prepare.reset = packet.reset != 0 || resumed;
        // The camera's own planes. FSR takes min and max of them itself and
        // lets the DEPTH_INVERTED flag decide the direction, so there is
        // nothing to swap here.
        prepare.cameraNear = packet.nearPlane;
        prepare.cameraFar = packet.farPlane;
        prepare.cameraFovAngleVertical = packet.verticalFovRadians;
        prepare.viewSpaceToMetersFactor = 1.0f;   // in KSP one unit is one metre
        prepare.depth = S(depth).AsFfx(FFX_API_RESOURCE_USAGE_READ_ONLY,
                                           FFX_API_RESOURCE_STATE_COMPUTE_READ);
        prepare.motionVectors = S(motion).AsFfx(FFX_API_RESOURCE_USAGE_READ_ONLY,
                                                    FFX_API_RESOURCE_STATE_COMPUTE_READ);

        // Documented as required. Leaving them zero breaks reconstruction in
        // proportion to parallax.
        if (cfg.fgCameraBasis)
        {
            for (int i = 0; i < 3; ++i)
            {
                prepare.cameraPosition[i] = packet.position[i];
                prepare.cameraUp[i] = packet.up[i];
                prepare.cameraRight[i] = packet.right[i];
                prepare.cameraForward[i] = packet.forward[i];
            }
        }

        code = FidelityFx::Get().Api().Dispatch(&context, &prepare.header);
        if (code != FFX_API_RETURN_OK)
        {
            LogLine("Frame generation prepare failed, code " + std::to_string(static_cast<int>(code)));
            return false;
        }

        if (!loggedFirstDispatch)
        {
            loggedFirstDispatch = true;
            LogLine("Frame generation dispatched for the first time, "
                    + std::to_string(capturedFrames) + " input captures so far");
            LogInputsLocked();
        }

        if (cfg.logInputsSeconds > 0)
        {
            const ULONGLONG now = GetTickCount64();
            if (now - lastInputLogTicks >= static_cast<ULONGLONG>(cfg.logInputsSeconds) * 1000ull)
            {
                lastInputLogTicks = now;
                LogInputsLocked();
            }
        }

        generatedLastPresent = true;
        return true;
    }
}
