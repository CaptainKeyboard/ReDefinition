// FSR's preparation for each Present (FrameGeneration): configured on or off for
// the frame, the prepare dispatch with this frame's inputs and packet, and the
// callback through which the swapchain asks for an interpolated frame.

#include "FrameGeneration.h"

#include "Config.h"
#include "FidelityFx.h"
#include "Log.h"

#include <FidelityFX/api/include/dx12/ffx_api_dx12.h>
#include <FidelityFX/framegeneration/include/ffx_framegeneration.h>
#include <FidelityFX/framegeneration/include/dx12/ffx_api_framegeneration_dx12.h>

#include <atomic>
#include <string>

namespace redefinition
{
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

    void FrameGeneration::CancelPreparedLocked(void* swapChain, const char* reason)
    {
        if (streamline)
        {
            if (streamlineOn)
                StreamlineOffLocked(reason);
            return;
        }
        if (contextCreated && configuredOn && swapChain != nullptr)
            ConfigureOffLocked(swapChain, reason);
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
        const bool inputs = wanted && thisFrame && S(depth).Valid() && S(motion).Valid();
        const bool generate = inputs && !rateTooLow && !slowFrame;

        // The context is created on first use, also while the frame rate holds frame
        // generation off: its provider sets the minimum (MinimumFpsLocked). Until
        // then there is nothing to configure, and the swapchain presents like a
        // plain one.
        if (inputs && !EnsureContextLocked())
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
            ConfigureOffLocked(swapChain, !wanted ? "switched off in the mod"
                                          : rateTooLow || slowFrame ? "the rendered frame rate is below its minimum (fgMinimumFps)"
                                          : "no inputs this frame");
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
        contextSwapChain = swapChain;
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
            // Configured on above for this frame: off again, or the Present would
            // interpolate without this frame's inputs.
            ConfigureOffLocked(swapChain, "its prepare dispatch failed");
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
