// FrameGeneration's XeSS side: the same inputs and packet FSR and DLSS-G get,
// handed to Intel's XeSS frame generation through its swapchain (XessFg.h).
//
// What XeSS-FG reads, from xess_fg_developer_guide_english.md (Inputs): "Motion
// vectors ... from the current frame to the previous", "Can be in normalized device
// coordinates (NDC) or pixel space", "Prefer low-res (same as input to Super
// Sampling or TAAU)"; the depth, "Buffer size must be the same as the motion vectors
// buffer"; the frame constants, "View/Projection matrices required"; and, for its UI
// handling, the HUD-less colour, "same texture format and color space as the back
// buffer". The backbuffer it takes itself from its swapchain.

#include "FrameGeneration.h"

#include "Config.h"
#include "Log.h"
#include "XessFg.h"

#include <cstring>
#include <string>

namespace redefinition
{
    namespace
    {
        // How long XeSS-FG stays off after it switched itself off or refused to come on,
        // before it is asked again.
        constexpr ULONGLONG kRetryMs = 2000;
        // Its results logged, at most: one a frame would fill the log.
        constexpr int kResultsLogged = 20;

        std::string ResultText(xefg::Result result)
        {
            return "result " + std::to_string(result);
        }

        // The view matrix, world to view, for row vectors and row by row, as the
        // projection beside it is: "viewMatrix - scene view matrix in a row-major order
        // without applied jitter" (Frame Constants), the guide's example storing
        // DirectXMath's matrices, which are for row vectors. The view space the packet's
        // viewToClip takes -- down +z along forward, x right, y up (FrameGeneration.h,
        // FramePacket; StreamlineCamera.cs) -- with the camera at the packet's position.
        void ViewMatrix(const FramePacket& packet, float view[16])
        {
            const float* axes[3] = { packet.right, packet.up, packet.forward };
            for (int row = 0; row < 3; ++row)
            {
                for (int column = 0; column < 3; ++column)
                    view[4 * row + column] = axes[column][row];
                view[4 * row + 3] = 0.0f;
            }
            for (int column = 0; column < 3; ++column)
                view[12 + column] = -(packet.position[0] * axes[column][0] + packet.position[1] * axes[column][1]
                                      + packet.position[2] * axes[column][2]);
            view[15] = 1.0f;
        }
    }

    void FrameGeneration::UseXess(const void* caller, xefg::Handle newContext)
    {
        std::lock_guard<std::mutex> lock(mutex);
        if (caller != owner)
            return;
        xess = newContext != nullptr;
        xessContext = newContext;
        xessOn = false;
        xessComposition = -1;
        xessShowOnly = -1;
        xessResultKnown = false;
        xessResultsLogged = 0;
        xessRetryAfter = 0;
        xessTagFailures = 0;
        xessMultiplier = 0;
        loggedUnflipped = false;
    }

    bool FrameGeneration::PrepareXessLocked(uint32_t presentId, const char* holdOff)
    {
        // As PrepareForPresentLocked: taken and cleared first, so neither a packet
        // nor a set of inputs carries over into a later frame.
        const bool thisFrame = packetFresh && inputsCopied;
        packetFresh = false;
        inputsCopied = false;
        generatedLastPresent = false;

        if (!xess)
            return false;

        XessFg& api = XessFg::Get();
        const ProxyConfig& cfg = Config();

        // The live settings: "You can enable or disable UI composition at any time";
        // debug features "can be called at any time between context creation and
        // destruction" (UI Handling, Debug Features).
        const int composition = cfg.xessFgUiComposition ? 1 : 0;
        if (composition != xessComposition)
        {
            const xefg::Result result = api.SetUiComposition(xessContext, composition != 0);
            if (result < 0 || xessComposition != -1 || composition != 0)
                LogLine(std::string("XeSS frame generation: UI composition ") + (composition != 0 ? "on" : "off")
                        + (result < 0 ? " refused (" + ResultText(result) + ")" : ""));
            xessComposition = composition;
        }
        const int showOnly = cfg.fgShowOnlyGenerated ? 1 : 0;
        if (showOnly != xessShowOnly)
        {
            if (showOnly != 0 || xessShowOnly == 1)
            {
                const xefg::Result result = api.ShowOnlyInterpolated(xessContext, showOnly != 0);
                LogLine(std::string("XeSS frame generation: only the generated frames shown ")
                        + (showOnly != 0 ? "on" : "off") + (result < 0 ? " refused (" + ResultText(result) + ")" : ""));
            }
            xessShowOnly = showOnly;
        }

        const bool inputs = wanted && !rateTooLow && !slowFrame && thisFrame && S(depth).Valid() && S(motion).Valid();

        // As for DLSS-G: depth and motion vectors upside down against the backbuffer
        // and the HUD-less colour, and against the projection, which is the screen's
        // way up.
        const bool unflipped = inputs && !flippedLastCopy;
        if (unflipped && !loggedUnflipped)
        {
            loggedUnflipped = true;
            LogLine("XeSS frame generation stays off: depth and motion vectors are not flipped to the screen's"
                    " orientation (fgFlipInputs, or no flip for them)");
        }

        const bool retryWaiting = !xessOn && GetTickCount64() < xessRetryAfter;
        if (holdOff != nullptr || !inputs || unflipped || presentId == 0 || retryWaiting)
        {
            XessOffLocked(holdOff != nullptr ? holdOff
                          : !wanted ? "switched off in the mod"
                          : rateTooLow || slowFrame ? "the rendered frame rate is below its minimum (fgMinimumFps)"
                          : !inputs ? "no inputs this frame"
                          : unflipped ? "its inputs are not flipped"
                          : presentId == 0 ? "no frame of the game's for this Present"
                          : nullptr);
            return false;
        }

        // On first, then this frame's inputs: "Each call to disable will mark input
        // history for cleanup next time frame generation is enabled", so a frame that
        // comes back starts afresh by itself (Enabling Frame Generation).
        const bool resumed = !xessOn;
        if (resumed)
        {
            const xefg::Result result = api.SetEnabled(xessContext, true);
            if (result < 0)
            {
                xessRetryAfter = GetTickCount64() + kRetryMs;
                if (xessResultsLogged < kResultsLogged)
                {
                    ++xessResultsLogged;
                    LogLine("XeSS frame generation could not be switched on (" + ResultText(result)
                            + ") -- tried again every " + std::to_string(kRetryMs / 1000) + " s");
                }
                return false;
            }
        }
        xessOn = true;

        // The shared textures were opened on D3D12 and sit in COMMON between uses,
        // where D3D11 finds them; XeSS-FG "will add appropriate state transition
        // barriers from/to the provided state". Valid until the next Present: the
        // proxy writes a slot again only after the Present two frames later has
        // finished on the queue XeSS-FG works on -- "XeSS-FG stores a reference to the
        // command queue provided during initialization and executes the interpolation
        // work using this queue" (Initialization) -- so no copy is needed, and none is
        // recorded: "Resources tagged as XEFG_SWAPCHAIN_RV_UNTIL_NEXT_PRESENT can only
        // be reused after the Present/Present1 call with the matching frame ID"
        // (Resource Tagging).
        auto tag = [&](int32_t type, const SharedTexture& texture) {
            xefg::D3d12ResourceData data = {};
            data.type = type;
            data.validity = xefg::kValidUntilNextPresent;
            data.resourceBase = { 0, 0 };
            data.resourceSize = { texture.width, texture.height };
            data.pResource = texture.d3d12.Get();
            data.incomingState = D3D12_RESOURCE_STATE_COMMON;
            return api.TagResource(xessContext, presentId, data);
        };

        const bool useHudLess = cfg.hudLessColour && S(hudLess).Valid();
        xefg::Result result = tag(xefg::kResourceDepth, S(depth));
        if (result >= 0)
            result = tag(xefg::kResourceMotionVectors, S(motion));
        if (result >= 0 && useHudLess)
            result = tag(xefg::kResourceHudLessColour, S(hudLess));

        // The upscaler's conventions carry over, as for FSR (PrepareForPresentLocked),
        // whose are XeSS's: "Motion vectors specify the screen-space motion in pixels
        // from the current frame to the previous frame" (Motion Vectors), the packet's
        // scale turning Unity's into those, and the flip of the textures -- always,
        // above -- turning Y round with it and the jitter. Unity's vectors do not hold
        // the jitter, so XEFG_SWAPCHAIN_INIT_FLAG_JITTERED_MV is not set; the jitter
        // goes with them as FSR has it, in pixels. The projection is the packet's, the
        // one DLSS-G gets: row-major, without jitter, Direct3D's clip space with
        // Unity's reversed depth, the top of the screen up (StreamlineCamera.cs).
        const float negate = cfg.fgNegateMotionScale ? -1.0f : 1.0f;
        const float flipY = -1.0f;
        xefg::FrameConstants constants = {};
        ViewMatrix(packet, constants.viewMatrix);
        std::memcpy(constants.projectionMatrix, packet.viewToClip, sizeof(constants.projectionMatrix));
        constants.jitterOffsetX = packet.jitterX;
        constants.jitterOffsetY = packet.jitterY * flipY;
        constants.motionVectorScaleX = packet.motionVectorScaleX * negate;
        constants.motionVectorScaleY = packet.motionVectorScaleY * negate * flipY;
        // "The application should set resetHistory flag during scene cuts and camera
        // teleports" (Frame Constants).
        constants.resetHistory = packet.reset != 0 ? 1u : 0u;
        constants.frameRenderTime = packet.frameTimeDeltaMs;
        if (result >= 0)
            result = api.TagConstants(xessContext, presentId, constants);

        if (result < 0)
        {
            if (xessTagFailures++ < kResultsLogged)
                LogLine("XeSS frame generation: this frame's inputs were not accepted (" + ResultText(result) + ")");
            XessOffLocked("its inputs were not accepted");
            xessRetryAfter = GetTickCount64() + kRetryMs;
            return false;
        }

        if (resumed)
            LogLine(std::string("XeSS frame generation on, 1 generated frame per rendered")
                    + (useHudLess ? ", with the HUD-less copy" : ", without a HUD-less copy")
                    + (composition != 0 ? ", the UI composed" : ", the UI interpolated"));
        generatedLastPresent = true;
        return true;
    }

    bool FrameGeneration::XessOffLocked(const char* reason)
    {
        if (!xess)
            return false;
        const bool wasOn = xessOn;
        if (wasOn)
        {
            const xefg::Result result = XessFg::Get().SetEnabled(xessContext, false);
            if (reason != nullptr)
                LogLine(std::string("XeSS frame generation off: ") + reason
                        + (result < 0 ? " (refused, " + ResultText(result) + ")" : ""));
        }
        xessOn = false;
        xessMultiplier = 0;
        return wasOn;
    }

    // "It populates the provided xefg_swapchain_present_status_t structure with the
    // last interpolation result, number of frames queued for the presentation, and an
    // indicator of whether XeSS-FG was enabled" (Present Status).
    uint32_t FrameGeneration::ReadXessStateLocked()
    {
        if (!xess)
            return 0;
        xefg::PresentStatus status = {};
        if (XessFg::Get().LastPresentStatus(xessContext, status) < 0)
        {
            xessMultiplier = 0;
            return 0;
        }

        // Each result once where it changes, while on: "It is expected for
        // xefgSwapChainGetLastPresentStatus to report warnings after history resets or
        // the swap chain resolution changes, all other warnings deserve a second look"
        // (Error Handling).
        if (xessOn && (!xessResultKnown || status.frameGenResult != xessResult))
        {
            if (xessResultsLogged < kResultsLogged)
            {
                ++xessResultsLogged;
                LogLine("XeSS frame generation: its interpolation reports "
                        + (status.frameGenResult == xefg::kSuccess ? std::string("success")
                           : status.frameGenResult == xefg::kErrorMismatchInputResources
                               ? "that the inputs do not suit its UI mode (" + ResultText(status.frameGenResult) + ")"
                               : ResultText(status.frameGenResult)));
            }
            xessResult = status.frameGenResult;
            xessResultKnown = true;
        }

        // Switched off by XeSS-FG itself, as a change to exclusive full screen does:
        // "After leaving fullscreen exclusive mode, xefgSwapChainSetEnabled must be called
        // again to enable frame generation" (Enabling Frame Generation). Asked again
        // after a while.
        if (xessOn && status.isFrameGenEnabled == 0)
        {
            if (xessResultsLogged < kResultsLogged)
            {
                ++xessResultsLogged;
                LogLine("XeSS frame generation was switched off by its swapchain -- switched on again in "
                        + std::to_string(kRetryMs / 1000) + " s");
            }
            xessOn = false;
            xessRetryAfter = GetTickCount64() + kRetryMs;
        }

        xessMultiplier = xessOn && status.isFrameGenEnabled != 0 && status.frameGenResult >= 0 ? 2 : 0;
        return status.framesPresented;
    }
}
