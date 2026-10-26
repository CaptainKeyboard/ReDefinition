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

#include <cmath>
#include <cstdio>
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

        // Whether a value is finite and above 0.
        bool Positive(float value)
        {
            return std::isfinite(value) && value > 0.0f;
        }

        // Whether a motion vector scale is finite and not 0.
        bool Scale(float value)
        {
            return std::isfinite(value) && value != 0.0f;
        }

        // Why XeSS-FG cannot use the packet's constants, nullptr where it can. A motion vector scale
        // of 0 gives XEFG_SWAPCHAIN_RESULT_ERROR_INVALID_ARGUMENT in OptiScaler's fork ("so 0.0f
        // inputs safely default to 1.0f"). Intel's header names no range, and libxess_fg 1.3.1 took
        // it in the harness. But it turns every vector to none. A projection from no near plane,
        // far plane or field of view is none: "View/Projection matrices required"
        // (xess_fg_developer_guide_english.md, Inputs). The scale is left out where it is made 1
        // instead.
        const char* RefusedConstants(const FramePacket& packet, bool scaleFixed)
        {
            if (!scaleFixed && (!Scale(packet.motionVectorScaleX) || !Scale(packet.motionVectorScaleY)))
                return "a motion vector scale of 0 or not a number";
            if (!Positive(packet.nearPlane) || !Positive(packet.farPlane) || !Positive(packet.verticalFovRadians)
                || packet.verticalFovRadians >= 3.14159265f)
                return "a near plane, far plane or field of view that is not a positive number";
            for (float value : packet.viewToClip)
                if (!std::isfinite(value))
                    return "a projection that is not a number";
            if (packet.viewToClip[0] == 0.0f || packet.viewToClip[5] == 0.0f)
                return "a projection without a field of view or aspect ratio";
            for (const float* vector : { packet.position, packet.up, packet.right, packet.forward })
                for (int i = 0; i < 3; ++i)
                    if (!std::isfinite(vector[i]))
                        return "a camera position or basis that is not a number";
            return nullptr;
        }

        // The view matrix, world to view, for row vectors and row by row, as the projection beside
        // it is. The guide says "viewMatrix - scene view matrix in a row-major order without applied
        // jitter" (Frame Constants), and its example stores DirectXMath's matrices, which are for
        // row vectors. The view space is the one the packet's viewToClip takes: down +z along
        // forward, x right, y up (FrameGeneration.h, FramePacket; StreamlineCamera.cs). The camera
        // is at the packet's position.
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

    void FrameGeneration::UseXess(const void* caller, xefg::Handle newContext, uint32_t framesMost)
    {
        std::lock_guard<std::mutex> lock(mutex);
        if (caller != owner)
            return;
        xess = newContext != nullptr;
        xessContext = newContext;
        xessOn = false;
        xessComposition = -1;
        xessShowOnly = -1;
        xessTagFrames = -1;
        xessPresentFailed = -1;
        // The SDK's: "Default value is 0.7" (xefgSwapChainSetSceneChangeThreshold);
        // and "By default, XeSS-FG will produce the maximum configured number of
        // interpolated frames" (Controlling the Number of Generated Frames).
        xessThreshold = 0.7f;
        xessFramesMost = framesMost > 0 ? framesMost : 1u;
        xessFrames = xessFramesMost;
        // None seen yet: the first Present sets the frames asked for, the mod's or the ini's.
        xessFramesSeen = -1;
        xessFramesNow = xess ? static_cast<int>(xessFrames) : 0;
        xessFramesMostNow = xess ? static_cast<int>(xessFramesMost) : 0;
        xessResultKnown = false;
        xessResultsLogged = 0;
        xessRetryAfter = 0;
        xessTagFailures = 0;
        xessMultiplier = 0;
        xessMissing = 0;
        xessSwitchedOff = 0;
        xessSwitchedOn = 0;
        xessRefused = 0;
        xessSyncSeen = ~0u;
        xessPresented = 0;
        xessResetPresented = 0;
        xessLoggedFullscreen = false;
        xessLoggedHudLessSize = false;
        xessLoggedDepthSize = false;
        xessBadLogged = 0;
        loggedUnflipped = false;
    }

    bool FrameGeneration::PrepareXessLocked(uint32_t presentId, const char* holdOff, IDXGISwapChain* chain,
                                            UINT presentSync)
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
        //
        // A change of the UI composition, the generated frames or the sync interval while
        // it generates is one the next Present's switching off and on takes again with
        // xessFgResetOnChange.
        bool changed = false;
        const int composition = cfg.xessFgUiComposition ? 1 : 0;
        if (composition != xessComposition)
        {
            const xefg::Result result = api.SetUiComposition(xessContext, composition != 0);
            if (result < 0 || xessComposition != -1 || composition != 0)
                LogLine(std::string("XeSS frame generation: UI composition ") + (composition != 0 ? "on" : "off")
                        + (result < 0 ? " refused (" + ResultText(result) + ")" : ""));
            changed = xessComposition != -1;
            xessComposition = composition;
        }
        if (presentSync != xessSyncSeen)
        {
            changed = changed || xessSyncSeen != ~0u;
            xessSyncSeen = presentSync;
        }
        auto debugFeature = [&](int32_t feature, bool wantedOn, int& applied, const char* name) {
            const int on = wantedOn ? 1 : 0;
            if (on == applied)
                return;
            if (on != 0 || applied == 1)
            {
                const xefg::Result result = api.DebugFeature(xessContext, feature, on != 0);
                LogLine(std::string("XeSS frame generation: ") + name + " " + (on != 0 ? "on" : "off")
                        + (result < 0 ? " refused (" + ResultText(result) + ")" : ""));
            }
            applied = on;
        };
        debugFeature(xefg::kDebugShowOnlyInterpolation, cfg.fgShowOnlyGenerated, xessShowOnly,
                     "only the generated frames shown");
        debugFeature(xefg::kDebugTagInterpolatedFrames, cfg.xessFgTagInterpolatedFrames, xessTagFrames,
                     "generated frames tagged (xessFgTagInterpolatedFrames)");
        debugFeature(xefg::kDebugPresentFailedInterpolation, cfg.xessFgPresentFailedInterpolation, xessPresentFailed,
                     "failed interpolations shown black (xessFgPresentFailedInterpolation)");

        // Live, within the swapchain's most: "you can ask XeSS-FG to generate any
        // number of frames between one and the maximum set during initialization",
        // "only as a response to a user changing the graphics settings" -- so only
        // when xessFgFrames changes (Controlling the Number of Generated Frames).
        // The mod's multiplier (SetMultiplier) where it has asked for one, else xessFgFrames.
        const int multiplierAsked = AskedMultiplier();
        const int askedNow = multiplierAsked >= 2 ? multiplierAsked - 1 : cfg.xessFgFrames;
        if (askedNow != xessFramesSeen)
        {
            xessFramesSeen = askedNow;
            const uint32_t asked = static_cast<uint32_t>(askedNow < 1 ? 1 : askedNow);
            const uint32_t frames = asked > xessFramesMost ? xessFramesMost : asked;
            if (asked > xessFramesMost)
                LogLine("XeSS frame generation: " + std::to_string(asked) + " generated frames asked for, but its swapchain was made"
                        " for at most " + std::to_string(xessFramesMost) + " -- " + std::to_string(frames)
                        + "; more takes effect with the next swapchain where the GPU generates more");
            if (frames != xessFrames)
            {
                const xefg::Result result = api.SetFrames(xessContext, frames);
                LogLine("XeSS frame generation: " + std::to_string(frames) + " generated frame(s) per rendered"
                        + (result < 0 ? " refused (" + ResultText(result) + "), " + std::to_string(xessFrames)
                                            + " as before"
                                      : std::string()));
                if (result >= 0)
                {
                    xessFrames = frames;
                    changed = true;
                }
                xessFramesNow = static_cast<int>(xessFrames);
            }
        }

        // "Sets scene change detection threshold", live where the DLL has it.
        if (cfg.xessFgSceneChangeThreshold != xessThreshold)
        {
            const xefg::Result result = api.SetSceneChangeThreshold(xessContext, cfg.xessFgSceneChangeThreshold);
            char line[160];
            std::snprintf(line, sizeof(line), "XeSS frame generation: scene change threshold %.2f%s",
                          cfg.xessFgSceneChangeThreshold, result < 0 ? " refused" : "");
            LogLine(std::string(line) + (result < 0 ? " (" + ResultText(result) + ")" : ""));
            xessThreshold = cfg.xessFgSceneChangeThreshold;
        }

        // A change that a switch off and on takes again (xessFgResetOnChange): this
        // Present goes out without generated frames, the next one switches it on.
        if (changed && cfg.xessFgResetOnChange && xessOn && holdOff == nullptr)
        {
            XessOffLocked("a setting changed (xessFgResetOnChange)");
            xessRetryAfter = 0;
            return false;
        }

        // Wanted, at a rate it runs at, and the inputs of this frame in the shared
        // textures; and what it refuses of the constants (xessFgBadConstants).
        const bool allowed = wanted && !rateTooLow && !slowFrame;
        const bool haveInputs = allowed && thisFrame && S(depth).Valid() && S(motion).Valid();
        const int badMode = cfg.xessFgBadConstants;
        const char* refused = haveInputs && badMode != kXessBadPass ? RefusedConstants(packet, badMode == kXessBadFix)
                                                                    : nullptr;
        if (refused != nullptr && xessBadLogged < kResultsLogged)
        {
            ++xessBadLogged;
            LogLine(std::string("XeSS frame generation: this frame's constants have ") + refused + " -- "
                    + (badMode == kXessBadFix ? "a projection cannot be made good, no inputs for it"
                                              : "no inputs for it, xessFgBadConstants=pass hands them over"));
        }
        const bool inputs = haveInputs && refused == nullptr;

        // Depth and motion vectors of one size: "motion vectors and depth have the same
        // resourceSize" (Resource Tagging); two sizes are refused with
        // XEFG_SWAPCHAIN_RESULT_ERROR_MISMATCH_INPUT_RESOURCES.
        const bool depthSized = !inputs || (S(depth).width == S(motion).width && S(depth).height == S(motion).height);
        if (!depthSized && !xessLoggedDepthSize)
        {
            xessLoggedDepthSize = true;
            LogLine("XeSS frame generation stays off: the depth is " + std::to_string(S(depth).width) + "x"
                    + std::to_string(S(depth).height) + " and the motion vectors "
                    + std::to_string(S(motion).width) + "x" + std::to_string(S(motion).height)
                    + ", which XeSS-FG requires equal");
        }

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

        // A frame without inputs keeps it on for xessFgGraceFrames in a row: switched off
        // and on, its history is cleared and the frames after it come back with Intel's
        // "too few frames" warning, an extra frame to activate (Frame Constants); a
        // frame without tags is presented without generated ones (Execution). Only
        // for what comes and goes with the game -- a packet or inputs that did not
        // arrive, constants it would refuse -- and with a present id; the mod's switch,
        // the frame rate and the other reasons switch it off at once.
        const bool graceable = holdOff == nullptr && presentId != 0 && allowed && !inputs;
        if (graceable && xessOn && xessMissing < cfg.xessFgGraceFrames)
        {
            ++xessMissing;
            return false;
        }

        const bool retryWaiting = !xessOn && GetTickCount64() < xessRetryAfter;
        if (holdOff != nullptr || !inputs || !depthSized || unflipped || presentId == 0 || retryWaiting)
        {
            XessOffLocked(holdOff != nullptr ? holdOff
                          : !wanted ? "switched off in the mod"
                          : rateTooLow || slowFrame ? "the rendered frame rate is below its minimum (fgMinimumFps)"
                          : refused != nullptr ? "the frame's constants are refused (xessFgBadConstants)"
                          : !inputs ? "no inputs this frame"
                          : !depthSized ? "the depth and the motion vectors differ in size"
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
                NoteExclusiveFullScreen(chain);
                return false;
            }
        }
        if (resumed)
            ++xessSwitchedOn;
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

        // "HUD-less, UI, and back buffer have the same resourceSize" (Resource Tagging),
        // "identical in color space and size to the back buffer" (HUD-less Color Texture),
        // else the interpolation reports XEFG_SWAPCHAIN_RESULT_ERROR_MISMATCH_INPUT_RESOURCES
        // and generates none: a HUD-less copy of another size than the display is not tagged.
        bool useHudLess = cfg.hudLessColour && S(hudLess).Valid();
        if (useHudLess && displayWidth != 0 && (S(hudLess).width != displayWidth || S(hudLess).height != displayHeight))
        {
            useHudLess = false;
            if (!xessLoggedHudLessSize)
            {
                xessLoggedHudLessSize = true;
                LogLine("XeSS frame generation: the HUD-less copy is " + std::to_string(S(hudLess).width) + "x"
                        + std::to_string(S(hudLess).height) + " but the display is " + std::to_string(displayWidth)
                        + "x" + std::to_string(displayHeight) + ", which XeSS-FG requires equal -- not handed over");
            }
        }
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
        // xessFgBadConstants=fix: a scale that is 0 or not a number is 1.
        const float scaleX = badMode == kXessBadFix && !Scale(packet.motionVectorScaleX) ? 1.0f : packet.motionVectorScaleX;
        const float scaleY = badMode == kXessBadFix && !Scale(packet.motionVectorScaleY) ? 1.0f : packet.motionVectorScaleY;
        constants.motionVectorScaleX = scaleX * negate;
        constants.motionVectorScaleY = scaleY * negate * flipY;
        // "The application should set resetHistory flag during scene cuts and camera
        // teleports" (Frame Constants). xessFgResetDebounce drops one that follows the last
        // handed over by fewer Presents: a reset skips the frame it is set for, and
        // resets in a row would skip them all.
        ++xessPresented;
        bool reset = packet.reset != 0;
        if (reset && cfg.xessFgResetDebounce > 0 && xessResetPresented != 0
            && xessPresented - xessResetPresented < static_cast<uint64_t>(cfg.xessFgResetDebounce))
            reset = false;
        if (reset)
            xessResetPresented = xessPresented;
        constants.resetHistory = reset ? 1u : 0u;
        constants.frameRenderTime = packet.frameTimeDeltaMs;
        if (result >= 0)
            result = api.TagConstants(xessContext, presentId, constants);

        if (result < 0)
        {
            ++xessRefused;
            if (xessTagFailures++ < kResultsLogged)
                LogLine("XeSS frame generation: this frame's inputs were not accepted (" + ResultText(result) + ")");
            XessOffLocked("its inputs were not accepted");
            xessRetryAfter = GetTickCount64() + kRetryMs;
            return false;
        }

        if (resumed)
            LogLine("XeSS frame generation on, " + std::to_string(xessFrames) + " generated frame(s) per rendered"
                    + (useHudLess ? ", with the HUD-less copy" : ", without a HUD-less copy")
                    + (composition != 0 ? ", the UI composed" : ", the UI interpolated"));
        xessMissing = 0;
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
            ++xessSwitchedOff;
            if (reason != nullptr)
                LogLine(std::string("XeSS frame generation off: ") + reason
                        + (result < 0 ? " (refused, " + ResultText(result) + ")" : ""));
        }
        xessOn = false;
        xessMultiplier = 0;
        xessMissing = 0;
        xessResetPresented = 0;
        return wasOn;
    }

    // Said once: "Frame generation cannot be enabled while the application is in the
    // fullscreen exclusive mode. If the application enters fullscreen exclusive mode
    // while XeSS-FG is enabled, frame generation will be disabled and the swap chain
    // will switch to a passthrough mode" (Enabling Frame Generation). Asked of the
    // swapchain only where switching on failed or it switched itself off.
    void FrameGeneration::NoteExclusiveFullScreen(IDXGISwapChain* chain)
    {
        if (xessLoggedFullscreen || chain == nullptr)
            return;
        BOOL fullscreen = FALSE;
        if (FAILED(chain->GetFullscreenState(&fullscreen, nullptr)) || !fullscreen)
            return;
        xessLoggedFullscreen = true;
        LogLine("XeSS frame generation cannot run in exclusive full screen (\"Frame generation cannot be enabled while"
                " the application is in the fullscreen exclusive mode\", xess_fg_developer_guide_english.md): KSP's"
                " screen mode is full screen -- use a window or borderless full screen for it; asked again every "
                + std::to_string(kRetryMs / 1000) + " s");
    }

    // "It populates the provided xefg_swapchain_present_status_t structure with the
    // last interpolation result, number of frames queued for the presentation, and an
    // indicator of whether XeSS-FG was enabled" (Present Status).
    uint32_t FrameGeneration::ReadXessStateLocked(IDXGISwapChain* chain)
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
            NoteExclusiveFullScreen(chain);
        }

        xessMultiplier = xessOn && status.isFrameGenEnabled != 0 && status.frameGenResult >= 0
                             ? static_cast<int>(xessFrames) + 1
                             : 0;
        return status.framesPresented;
    }
}
