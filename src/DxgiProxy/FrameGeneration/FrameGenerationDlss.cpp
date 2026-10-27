// FrameGeneration's DLSS side: the same inputs and packet FSR gets, handed to
// NVIDIA's DLSS frame generation through Streamline (Streamline.h) instead.
//
// What DLSS-G reads, from ProgrammingGuideDLSS_G.md: the backbuffer, which it
// takes itself from its swapchain; depth and motion vectors -- "the same set of
// requirements as DLSS-SR, and the same depth can be used for both" -- and the
// HUD-less colour, all tagged "eValidUntilPresent" first, as it asks; the common
// constants once per frame; and its options on the presenting thread.

#include "FrameGeneration.h"

#include "Config.h"
#include "Log.h"
#include "NvidiaGpu.h"
#include "Streamline.h"

#include <atomic>
#include <cstring>
#include <string>

namespace redefinition
{
    namespace
    {
        // DLSS-G's errors from its asynchronous Present: kept, and logged on the
        // presenting thread's next frame -- "STORE ERROR AND RETURN IMMEDIATELY TO
        // AVOID STALLING PRESENT THREAD" (ProgrammingGuideDLSS_G.md 6.7).
        std::atomic<uint32_t> g_apiErrors{ 0 };
        std::atomic<long> g_lastApiError{ 0 };
        // How many of the stored errors were logged already.
        uint32_t g_apiErrorsLogged = 0;

        // Streamline's callback for errors of its Present: stored only, never logged from there.
        void OnStreamlineApiError(const sl::APIError& error)
        {
            g_lastApiError.store(error.hres);
            g_apiErrors.fetch_add(1);
        }

        // The errors stored since the last frame, logged as one line with the last HRESULT.
        void LogStreamlineApiErrors()
        {
            const uint32_t errors = g_apiErrors.load();
            if (errors == g_apiErrorsLogged)
                return;
            LogLine("DLSS frame generation: " + std::to_string(errors - g_apiErrorsLogged)
                    + " error(s) from its Present since the last frame, the last " + Hr(g_lastApiError.load()));
            g_apiErrorsLogged = errors;
        }

        // A row-major matrix of the packet into Streamline's float4x4, row by row.
        void CopyMatrix(sl::float4x4& target, const float source[16])
        {
            for (int row = 0; row < 4; ++row)
                target[row] = sl::float4(source[4 * row], source[4 * row + 1], source[4 * row + 2], source[4 * row + 3]);
        }

        // The 4x4 identity, for the constants that have no transform of their own.
        sl::float4x4 Identity()
        {
            sl::float4x4 m;
            for (int row = 0; row < 4; ++row)
                m[row] = sl::float4(row == 0 ? 1.0f : 0.0f, row == 1 ? 1.0f : 0.0f, row == 2 ? 1.0f : 0.0f,
                                    row == 3 ? 1.0f : 0.0f);
            return m;
        }

        // The four tags of a frame -- depth, motion vectors, the HUD-less copy and the UI plane -- with the
        // resources and extents they point into. It stays where it is made, as the tags hold their addresses.
        struct StreamlineTags
        {
            // The shared textures were opened on D3D12 and sit in COMMON between
            // uses, where D3D11 finds them; Streamline transitions what it needs
            // and "resources will always be in the state they where left".
            static constexpr uint32_t kCommon = static_cast<uint32_t>(D3D12_RESOURCE_STATE_COMMON);

            // The HUD-less copy or the UI plane is given a null resource where it is not used: its tag is
            // then let go.
            StreamlineTags(const SharedTexture& depthTexture, const SharedTexture& motionTexture,
                           const SharedTexture& hudLessTexture, const SharedTexture& uiTexture, bool useHudLess,
                           bool useUi)
                : depth(sl::ResourceType::eTex2d, depthTexture.d3d12.Get(), kCommon),
                  motion(sl::ResourceType::eTex2d, motionTexture.d3d12.Get(), kCommon),
                  hudLess(sl::ResourceType::eTex2d, useHudLess ? hudLessTexture.d3d12.Get() : nullptr, kCommon),
                  ui(sl::ResourceType::eTex2d, useUi ? uiTexture.d3d12.Get() : nullptr, kCommon),
                  depthExtent{ 0, 0, depthTexture.width, depthTexture.height },
                  motionExtent{ 0, 0, motionTexture.width, motionTexture.height },
                  hudLessExtent{ 0, 0, hudLessTexture.width, hudLessTexture.height },
                  uiExtent{ 0, 0, uiTexture.width, uiTexture.height },
                  tags{ sl::ResourceTag(&depth, sl::kBufferTypeDepth, sl::ResourceLifecycle::eValidUntilPresent,
                                        &depthExtent),
                        sl::ResourceTag(&motion, sl::kBufferTypeMotionVectors, sl::ResourceLifecycle::eValidUntilPresent,
                                        &motionExtent),
                        useHudLess ? sl::ResourceTag(&hudLess, sl::kBufferTypeHUDLessColor,
                                                     sl::ResourceLifecycle::eValidUntilPresent, &hudLessExtent)
                                   : sl::ResourceTag(nullptr, sl::kBufferTypeHUDLessColor,
                                                     sl::ResourceLifecycle::eValidUntilPresent),
                        useUi ? sl::ResourceTag(&ui, sl::kBufferTypeUIColorAndAlpha,
                                                sl::ResourceLifecycle::eValidUntilPresent, &uiExtent)
                              : sl::ResourceTag(nullptr, sl::kBufferTypeUIColorAndAlpha,
                                                sl::ResourceLifecycle::eValidUntilPresent) }
            {
                depth.width = depthTexture.width;
                depth.height = depthTexture.height;
                depth.nativeFormat = static_cast<uint32_t>(TypedFormat(depthTexture.format));
                motion.width = motionTexture.width;
                motion.height = motionTexture.height;
                motion.nativeFormat = static_cast<uint32_t>(TypedFormat(motionTexture.format));
                hudLess.width = hudLessTexture.width;
                hudLess.height = hudLessTexture.height;
                hudLess.nativeFormat = static_cast<uint32_t>(TypedFormat(hudLessTexture.format));
                ui.width = uiTexture.width;
                ui.height = uiTexture.height;
                ui.nativeFormat = static_cast<uint32_t>(DXGI_FORMAT_R8G8B8A8_UNORM);
            }
            StreamlineTags(const StreamlineTags&) = delete;
            StreamlineTags& operator=(const StreamlineTags&) = delete;

            // The resources, the extents they are tagged with, and the tags: depth, motion vectors, HUD-less
            // colour, UI plane.
            sl::Resource depth, motion, hudLess, ui;
            sl::Extent depthExtent, motionExtent, hudLessExtent, uiExtent;
            sl::ResourceTag tags[4];
        };

        // DLSS-G's common constants for the frame from the packet, the settings and the motion vectors' size.
        // resumed is set when DLSS-G comes back from off, which is a discontinuity as it is for FSR.
        sl::Constants BuildStreamlineConstants(const FramePacket& packet, const ProxyConfig& cfg,
                                               const SharedTexture& motion, uint32_t displayWidth,
                                               uint32_t displayHeight, bool resumed)
        {
            // The upscaler's conventions carry over, as for FSR (PrepareForPresentLocked):
            // the motion vector scale turned into Streamline's "scale factors used to
            // normalize motion vectors" by the texture's size -- NVIDIA's own example
            // for vectors in pixels is {1/renderWidth, 1/renderHeight} -- and a flip of
            // the textures -- always, above -- turning Y round with it. The packet's
            // matrices are Direct3D's with the top of the screen up
            // (StreamlineCamera), as the flipped textures are.
            const float negate = cfg.fgNegateMotionScale ? -1.0f : 1.0f;
            const float flipY = -1.0f;

            sl::Constants constants{};
            CopyMatrix(constants.cameraViewToClip, packet.viewToClip);
            CopyMatrix(constants.clipToCameraView, packet.clipToView);
            constants.clipToLensClip = Identity();
            CopyMatrix(constants.clipToPrevClip, packet.clipToPrevClip);
            CopyMatrix(constants.prevClipToClip, packet.prevClipToClip);
            constants.jitterOffset = sl::float2(packet.jitterX, packet.jitterY * flipY);
            constants.mvecScale = sl::float2(packet.motionVectorScaleX * negate / static_cast<float>(motion.width),
                                             packet.motionVectorScaleY * negate * flipY
                                                 / static_cast<float>(motion.height));
            constants.cameraPinholeOffset = sl::float2(0.0f, 0.0f);
            constants.cameraPos = sl::float3(packet.position[0], packet.position[1], packet.position[2]);
            constants.cameraUp = sl::float3(packet.up[0], packet.up[1], packet.up[2]);
            constants.cameraRight = sl::float3(packet.right[0], packet.right[1], packet.right[2]);
            constants.cameraFwd = sl::float3(packet.forward[0], packet.forward[1], packet.forward[2]);
            constants.cameraNear = packet.nearPlane;
            constants.cameraFar = packet.farPlane;
            constants.cameraFOV = packet.verticalFovRadians;
            const uint32_t renderWidth = packet.renderWidth != 0 ? packet.renderWidth : displayWidth;
            const uint32_t renderHeight = packet.renderHeight != 0 ? packet.renderHeight : displayHeight;
            constants.cameraAspectRatio = renderHeight != 0 ? static_cast<float>(renderWidth) / static_cast<float>(renderHeight)
                                                            : 1.0f;
            constants.depthInverted = cfg.fgDepthInverted ? sl::Boolean::eTrue : sl::Boolean::eFalse;
            // Unity's vectors: camera and object motion together, 2D, at render size,
            // neither dilated nor jittered.
            constants.cameraMotionIncluded = sl::Boolean::eTrue;
            constants.motionVectors3D = sl::Boolean::eFalse;
            constants.motionVectorsDilated = sl::Boolean::eFalse;
            constants.motionVectorsJittered = sl::Boolean::eFalse;
            constants.orthographicProjection = sl::Boolean::eFalse;
            // Streamline's "minimum depth difference between two objects in
            // screen-space", in linear depth: "if depthInverted is true: lin_depth =
            // 1 / depth", which is about the distance over the near plane (sl_consts.h).
            // Its default of 40 near planes is 8.4 m at KSP's 0.21 m in flight: an
            // aircraft and the runway under it would count as one object. The header:
            // "smaller thresholds are useful when depth units are unusually compressed".
            if (cfg.fgObjectSeparationMetres > 0.0f && packet.nearPlane > 0.0f)
                constants.minRelativeLinearDepthObjectSeparation = cfg.fgObjectSeparationMetres / packet.nearPlane;
            // Coming back from off is a discontinuity, as for FSR.
            constants.reset = packet.reset != 0 || resumed ? sl::Boolean::eTrue : sl::Boolean::eFalse;
            return constants;
        }

        // DLSS-G's status bits in words for the log, "ok" for none; bits the header does not name in hex.
        std::string StatusText(uint32_t status)
        {
            if (status == 0)
                return "ok";
            std::string text;
            uint32_t named = 0;
            auto add = [&](sl::DLSSGStatus flag, const char* what) {
                named |= static_cast<uint32_t>(flag);
                if ((status & static_cast<uint32_t>(flag)) != 0)
                    text += (text.empty() ? "" : ", ") + std::string(what);
            };
            add(sl::DLSSGStatus::eFailResolutionTooLow, "the output resolution is too low");
            add(sl::DLSSGStatus::eFailReflexNotDetectedAtRuntime, "Reflex is not active");
            add(sl::DLSSGStatus::eFailHDRFormatNotSupported, "the backbuffer format is not supported");
            add(sl::DLSSGStatus::eFailCommonConstantsInvalid, "some constants are invalid");
            add(sl::DLSSGStatus::eFailGetCurrentBackBufferIndexNotCalled, "GetCurrentBackBufferIndex was not called");
            // Bits sl_dlss_g.h 2.14.16 does not name, in hex as the header writes them.
            const uint32_t rest = status & ~named;
            if (rest != 0)
            {
                char bits[32] = {};
                FormatTo(bits, "status bits 0x%X", rest);
                text += (text.empty() ? "" : ", ") + std::string(bits);
            }
            return text;
        }

        // How long a failing status keeps DLSS-G off before it is tried again. The grace period
        // after it comes on is a number of generating Presents in which a failing status is not
        // yet held against it. Reflex needs frames to be detected, and a status read straight after
        // a Present may still be the one before.
        constexpr ULONGLONG kStatusRetryMs = 2000;
        constexpr uint32_t kStatusGracePresents = 8;
    }

    void FrameGeneration::UseStreamline(const void* caller)
    {
        std::lock_guard<std::mutex> lock(mutex);
        if (caller != owner)
            return;
        streamline = true;
        streamlineOn = false;
        streamlineFrames = 1;
        streamlineFramesSet = 1;
        streamlineStatusKnown = false;
        streamlineVsyncKnown = false;
        streamlineVsyncSupported = true;
        streamlineDynamicKnown = false;
        streamlineEverOn = false;
        streamlineDynamicSupported = false;
        loggedDynamicFallback = false;
        streamlineModeSet = -1;
        streamlineRetryAfter = 0;
        streamlineGrace = 0;
        streamlineOffStatus = 0;
        streamlineMultiplier = 0;
        streamlineFramesNow = 0;
        streamlineDynamicNow = -1;
        streamlineDynamicActive = false;
        loggedUnflipped = false;
        streamlineUiSet = false;
    }

    FrameGeneration::StreamlineFramePlan FrameGeneration::ChooseStreamlineFramesLocked(
        const std::function<uint32_t()>& maxFrames)
    {
        const ProxyConfig& cfg = Config();
        // On, with as many frames as the GPU reports it can generate, but no more
        // than the proxy allows for the display (maxFrames). Options take effect
        // "in the next Present() call that executes after it", and this is the
        // presenting thread. Resources are kept while off, "strongly recommended"
        // to avoid a stutter when it comes back.
        const uint32_t limit = maxFrames ? maxFrames() : 0;
        // The multiplier the mod asked for (SetMultiplier), else dlssgFrames, else as many as the
        // GPU and the package offer; never more than they offer.
        const uint32_t askedNow = askedPacked.load();
        const int multiplierAsked = static_cast<int>(askedNow & 0xFFFFu);
        const bool dynamicByMod = (askedNow >> 16) != 0;
        const uint32_t askedFrames = multiplierAsked >= 2 ? static_cast<uint32_t>(multiplierAsked - 1)
                                     // Dynamic asked for by the mod: the fixed multiplier it falls back to where
                                     // Streamline does not support it is 2x, not as many as the GPU offers.
                                     : multiplierAsked == 0 && dynamicByMod ? 1u
                                     : multiplierAsked == 0 && cfg.dlssgFrames > 0 ? static_cast<uint32_t>(cfg.dlssgFrames)
                                                                                    : streamlineFrames;
        const uint32_t wantedFrames = askedFrames < streamlineFrames ? askedFrames : streamlineFrames;
        uint32_t frames = limit != 0 && limit < wantedFrames ? limit : wantedFrames;
        LogStreamlineApiErrors();

        // Dynamic Multi Frame Generation where dlssgMode asks for it and the state of this
        // swapchain's first Present reported it supported (ReadStreamlineStateLocked); the
        // fixed multiplier until that is known, and where it is not. Streamline ignores
        // numFramesToGenerate under it, and the latency brake runs as under any
        // Streamline Reflex (LowLatency::Describe).
        // Asked for by the mod, or by dlssgMode where the mod has asked for nothing.
        const bool dynamicAsked = multiplierAsked != 0 || dynamicByMod ? dynamicByMod
                                                                          : cfg.dlssgMode == kDlssgModeDynamic;
        const bool dynamic = dynamicAsked && streamlineDynamicKnown && streamlineDynamicSupported;
        // Under it the frames are the most Streamline may choose from, not the fixed fallback.
        if (dynamic)
            frames = limit != 0 && limit < streamlineFrames ? limit : streamlineFrames;
        if (dynamicAsked && streamlineDynamicKnown && !streamlineDynamicSupported && !loggedDynamicFallback)
        {
            loggedDynamicFallback = true;
            LogLine(std::string("DLSS frame generation: dynamic multi frame generation is asked for (")
                    + (dynamicByMod ? "in the mod" : "dlssgMode=dynamic")
                    + "), but Streamline does not report it supported on this system -- the fixed multiplier is used");
        }

        StreamlineFramePlan plan;
        plan.wantedFrames = wantedFrames;
        plan.frames = frames;
        plan.dynamic = dynamic;
        return plan;
    }

    void FrameGeneration::LogStreamlineOnLocked(const StreamlineFramePlan& plan, bool useHudLess)
    {
        const ProxyConfig& cfg = Config();
        const uint32_t frames = plan.frames;
        const uint32_t wantedFrames = plan.wantedFrames;
        const bool dynamic = plan.dynamic;
        if (dynamic)
        {
            char target[48] = "the display's refresh rate";
            if (cfg.dlssgDynamicTargetFps > 0.0f)
                FormatTo(target, "%.0f fps", cfg.dlssgDynamicTargetFps);
            LogLine(std::string("DLSS frame generation on, dynamic multi frame generation toward ") + target
                    + ", up to " + std::to_string(streamlineFrames) + " generated frame(s) per rendered"
                    + (useHudLess ? ", with the HUD-less copy" : ", without a HUD-less copy"));
        }
        else
            LogLine("DLSS frame generation on, " + std::to_string(frames) + " generated frame(s) per rendered"
                    + (frames < streamlineFrames ? " (the GPU and the package offer " + std::to_string(streamlineFrames)
                                                         + (wantedFrames < streamlineFrames ? ", fewer asked for" : "") + ")"
                                                 : "")
                    + (useHudLess ? ", with the HUD-less copy" : ", without a HUD-less copy"));
    }

    bool FrameGeneration::PrepareStreamlineLocked(uint32_t frameIndex, const std::function<uint32_t()>& maxFrames,
                                                  const char* holdOff)
    {
        // As PrepareForPresentLocked: taken and cleared first, so neither a packet
        // nor a set of inputs carries over into a later frame.
        const bool thisFrame = TakeFrameInputsLocked();

        if (!streamline)
            return false;

        const bool inputs = wanted && !rateTooLow && !slowFrame && thisFrame && S(depth).Valid() && S(motion).Valid();

        // Depth and motion vectors upside down against the backbuffer and the
        // HUD-less colour DLSS-G warps -- fgFlipInputs off, or no flip for them
        // (CaptureForPresent) -- would move every pixel by the row mirrored to it;
        // no constant turns that round.
        const bool unflipped = inputs && !flippedLastCopy;
        if (unflipped && !loggedUnflipped)
        {
            loggedUnflipped = true;
            LogLine("DLSS frame generation stays off: depth and motion vectors are not flipped to the screen's"
                    " orientation (fgFlipInputs, or no flip for them)");
        }

        // A status failing past its grace keeps DLSS-G off until the retry; what
        // it was is said once per status (ReadStreamlineStateLocked), so a retry
        // goes by without lines of its own.
        const bool statusFailing = streamlineStatusKnown && streamlineStatus != 0;
        const bool statusWaiting = statusFailing && streamlineGrace == 0 && GetTickCount64() < streamlineRetryAfter;
        if (holdOff != nullptr || !inputs || unflipped || frameIndex == 0 || statusWaiting)
        {
            const char* gate = GateReasonLocked();
            StreamlineOffLocked(holdOff != nullptr ? holdOff
                                : gate != nullptr ? gate
                                : !inputs ? "no inputs this frame"
                                : unflipped ? "its inputs are not flipped"
                                : frameIndex == 0 ? "no frame token"
                                : nullptr);
            return false;
        }

        Streamline& streamlineApi = Streamline::Get();
        const ProxyConfig& cfg = Config();

        // The HUD-less copy, or its tag let go: "Hudless ... Should contain the
        // full viewable scene, without any HUD/UI elements in it", at the
        // backbuffer's size.
        const bool useHudLess = cfg.hudLessColour && S(hudLess).Valid();

        // The UI plane, where the proxy made one for this frame and the options ask for recomposition
        // (FrameGenerationUi.cpp); otherwise its tag let go. "Needs the same dimensions as the colour backbuffer".
        const bool useUi = cfg.dlssgUiRecomposition && useHudLess && uiPlaneFresh && uiPlane[slot].Valid();
        StreamlineTags tagged(S(depth), S(motion), S(hudLess), uiPlane[slot], useHudLess, useUi);

        // Coming back from off is a discontinuity, as for FSR.
        const sl::Constants constants =
            BuildStreamlineConstants(packet, cfg, S(motion), displayWidth, displayHeight, !streamlineOn);

        streamlineTagged = true;
        if (!streamlineApi.SetConstants(constants, frameIndex) || !streamlineApi.SetTags(frameIndex, tagged.tags, 4))
        {
            StreamlineOffLocked("its inputs were not accepted");
            return false;
        }

        const StreamlineFramePlan plan = ChooseStreamlineFramesLocked(maxFrames);
        const uint32_t frames = plan.frames;
        const bool dynamic = plan.dynamic;

        sl::DLSSGOptions options{};
        options.mode = dynamic ? sl::DLSSGMode::eDynamic : sl::DLSSGMode::eOn;
        options.numFramesToGenerate = frames;
        // 0 targets the monitor's refresh rate; Streamline ignores the target under V-Sync.
        options.dynamicTargetFrameRate = dynamic ? cfg.dlssgDynamicTargetFps : 0.0f;
        options.flags = sl::DLSSGFlags::eRetainResourcesWhenOff;
        // "When both Hudless and a UI buffer are tagged, User Interface Recomposition can be enabled"
        // (ProgrammingGuideDLSS_G.md 6.6): the scene interpolated without the UI, which is laid over it after.
        options.enableUserInterfaceRecomposition = useUi ? sl::Boolean::eTrue : sl::Boolean::eFalse;
        options.uiBufferFormat = useUi ? static_cast<uint32_t>(DXGI_FORMAT_R8G8B8A8_UNORM) : 0;
        if (useUi != streamlineUiSet)
        {
            streamlineUiSet = useUi;
            LogLine(useUi ? std::string("DLSS frame generation: user interface recomposition on, with a UI plane made from the"
                                        " HUD-less copy and the presented frame (dlssgUiAlpha=")
                                + DlssgUiAlphaName(cfg.dlssgUiAlpha) + ", dlssgUiThreshold="
                                + std::to_string(static_cast<int>(cfg.dlssgUiThreshold)) + ")"
                          : std::string("DLSS frame generation: user interface recomposition off"));
        }
        // Only the frames DLSS-G generates on screen, to see them apart (Config.h).
        if (Config().fgShowOnlyGenerated)
            options.flags |= sl::DLSSGFlags::eShowOnlyInterpolatedFrame;
        // "To obtain per-call API errors, you must provide an API error callback"
        // (ProgrammingGuideDLSS_G.md 6.7): Present runs asynchronously in DLSS-G.
        options.onErrorCallback = &OnStreamlineApiError;
        if (!streamlineApi.SetOptions(options))
        {
            StreamlineOffLocked("its options were not accepted");
            return false;
        }

        // The grace comes with DLSS-G coming on while its status is not known to
        // fail, and with a retry once its wait is over -- not when it only comes
        // back after a Present without inputs while the status still fails: that
        // grace would begin anew every time and never run out.
        if (!streamlineOn && (!statusFailing || streamlineRetryAfter != 0))
        {
            streamlineGrace = kStatusGracePresents;
            streamlineRetryAfter = 0;
        }
        const int modeNow = dynamic ? kDlssgModeDynamic : kDlssgModeOn;
        if ((!streamlineOn && !statusFailing) || frames != streamlineFramesSet
            || (streamlineModeSet >= 0 && modeNow != streamlineModeSet))
            LogStreamlineOnLocked(plan, useHudLess);
        streamlineModeSet = modeNow;
        streamlineDynamicActive = dynamic;
        streamlineOn = true;
        streamlineEverOn = true;
        streamlineFramesSet = frames;
        generatedLastPresent = true;
        return true;
    }

    bool FrameGeneration::StreamlineOffLocked(const char* reason)
    {
        if (!streamline)
            return false;

        const bool wasOn = streamlineOn;
        if (wasOn)
        {
            sl::DLSSGOptions options{};
            options.mode = sl::DLSSGMode::eOff;
            options.flags = sl::DLSSGFlags::eRetainResourcesWhenOff;
            Streamline::Get().SetOptions(options);
            if (reason != nullptr)
                LogLine(std::string("DLSS frame generation off: ") + reason);
        }
        streamlineOn = false;
        streamlineMultiplier = 0;
        streamlineDynamicActive = false;

        // The inputs let go while DLSS-G is off: "SL holds a reference to all
        // sl::ResourceLifecycle::eValidUntilPresent resources until a null tag is
        // set" (ProgrammingGuideDLSS_G.md 5.2), and "When resource tag of type
        // sl::ResourceLifecycle::eValidUntilPresent is no longer needed (e.g.
        // resource is about to be destroyed) host app must set the tag to null in
        // order to release the reference held by SL" (ProgrammingGuide.md). Shared
        // textures replaced while it is off (RetireLocked) would otherwise live on
        // in Streamline. Tagged again when it comes back on.
        if (streamlineTagged)
        {
            Streamline& streamlineApi = Streamline::Get();
            if (streamlineApi.DeviceSet())
            {
                const sl::ResourceTag none[] = {
                    sl::ResourceTag(nullptr, sl::kBufferTypeDepth, sl::ResourceLifecycle::eValidUntilPresent),
                    sl::ResourceTag(nullptr, sl::kBufferTypeMotionVectors, sl::ResourceLifecycle::eValidUntilPresent),
                    sl::ResourceTag(nullptr, sl::kBufferTypeHUDLessColor, sl::ResourceLifecycle::eValidUntilPresent),
                    sl::ResourceTag(nullptr, sl::kBufferTypeUIColorAndAlpha, sl::ResourceLifecycle::eValidUntilPresent),
                };
                if (streamlineApi.SetTags(streamlineApi.FrameInProgressIndex(), none, 4))
                    streamlineTagged = false;
            }
        }
        return wasOn;
    }

    void FrameGeneration::NoteDynamicSupportLocked(bool supported, uint32_t framesMax)
    {
        streamlineDynamicKnown = true;
        streamlineDynamicSupported = supported;
        streamlineDynamicNow = supported ? 1 : 0;

        // "The most likely reasons for lack of support are: Multi-frame generation is not
        // supported; The NVIDIA display driver is below version 595.41; The application is
        // using Vulkan" (ProgrammingGuideDLSS_G.md 6.3) -- the state gives no reason of
        // its own, so what the proxy knows of each is said next to it.
        const uint32_t driver = NvidiaDriverVersion();
        char driverText[32] = "not known";
        if (driver != 0)
            FormatTo(driverText, "%u.%02u", driver / 100, driver % 100);
        if (supported)
        {
            LogLine(std::string("DLSS frame generation: dynamic multi frame generation is supported (NVIDIA driver ")
                    + driverText + ", up to " + std::to_string(framesMax) + " generated frame(s)); dlssgMode=dynamic"
                    + (Config().dlssgMode == kDlssgModeDynamic ? " is set" : " selects it"));
            return;
        }
        LogLine(std::string("DLSS frame generation: dynamic multi frame generation is not supported here"
                            " (bIsDynamicMFGSupported is false) -- the reasons Streamline's guide gives: multi frame"
                            " generation not supported (this GPU: up to ")
                + std::to_string(framesMax) + " generated frame(s)), an NVIDIA driver below 595.41 (this one: "
                + driverText + "), Vulkan (this is Direct3D 12)");
    }

    FrameGeneration::StreamlinePresent FrameGeneration::ReadStreamlineStateLocked()
    {
        StreamlinePresent result;
        if (!streamline)
            return result;

        sl::DLSSGState state{};
        if (!Streamline::Get().GetState(state))
            return result;

        // "The value of numFramesToGenerate must be between 1 (single frame
        // generation) and the maximum defined by DLSSGState::numFramesToGenerateMax".
        const uint32_t frames = state.numFramesToGenerateMax >= 1 ? state.numFramesToGenerateMax : 1;
        if (frames != streamlineFrames)
        {
            LogLine("DLSS frame generation: this GPU generates up to " + std::to_string(frames)
                    + " frame(s) per rendered frame");
            streamlineFrames = frames;
        }
        streamlineFramesNow = static_cast<int>(frames);

        // Read again on every Present until a verdict is taken: a "not supported" from before the
        // first options is not one, and the session would be told Dynamic is unsupported although
        // it works once DLSS-G is on.
        if (!streamlineDynamicKnown)
        {
            const bool dynamicSupported = state.bIsDynamicMFGSupported == sl::Boolean::eTrue;
            if (dynamicSupported || streamlineEverOn)
                NoteDynamicSupportLocked(dynamicSupported, state.numFramesToGenerateMax);
        }

        // "Applications should check sl::DLSSGState::bIsVsyncSupportAvailable to
        // determine if VSync is supported with the current DLSS-G build"
        // (ProgrammingGuideDLSS_G.md 22.1). Said once, and again where it changes.
        const bool vsync = state.bIsVsyncSupportAvailable == sl::Boolean::eTrue;
        if (!streamlineVsyncKnown || vsync != streamlineVsyncSupported.load())
        {
            LogLine(std::string("DLSS frame generation: ")
                    + (vsync ? "this build presents with V-Sync where KSP asks for it"
                             : "this build does not support V-Sync -- presented without it while it generates"));
            streamlineVsyncSupported = vsync;
            streamlineVsyncKnown = true;
        }

        // The status of a Present DLSS-G generated for, once its grace is over:
        // a failure then keeps it off, and the retry waits from the last Present
        // it failed on. Each status once, and the switch off once per status.
        const uint32_t status = static_cast<uint32_t>(state.status);
        if (streamlineOn)
        {
            if (!streamlineStatusKnown || status != streamlineStatus)
            {
                LogLine("DLSS frame generation status: " + StatusText(status));
                streamlineStatus = status;
                streamlineStatusKnown = true;
            }
            if (status == 0)
            {
                streamlineGrace = 0;
                streamlineOffStatus = 0;
            }
            else if (streamlineGrace > 0)
                --streamlineGrace;
            else
            {
                streamlineRetryAfter = GetTickCount64() + kStatusRetryMs;
                if (status != streamlineOffStatus)
                {
                    LogLine("DLSS frame generation off while its status reports " + StatusText(status)
                            + " -- tried again every " + std::to_string(kStatusRetryMs / 1000) + " s");
                    streamlineOffStatus = status;
                }
            }
        }

        // What this Present was set to, not what the next may be; under Dynamic Multi
        // Frame Generation the frames Streamline presented since the last state, which
        // it chose.
        const bool dynamicSet = streamlineModeSet == kDlssgModeDynamic;
        streamlineMultiplier = streamlineOn && status == 0
                                   ? (dynamicSet && state.numFramesActuallyPresented >= 1
                                          ? static_cast<int>(state.numFramesActuallyPresented)
                                          : static_cast<int>(streamlineFramesSet) + 1)
                                   : 0;

        result.presented = state.numFramesActuallyPresented;
        result.statusOk = streamlineOn && status == 0;
        // Whatever the status: "SL client must wait on SL DLSS-G plugin-internal
        // fence and associated value, before it can modify or destroy the tagged
        // resources input to DLSS-G [...] on a non-presenting queue" (sl_dlss_g.h,
        // DLSSGState) -- D3D11's writes are on one. Whether it completes while the
        // status fails is not said; the proxy stops waiting for it then after one
        // wait runs out (SwapChainProxy::WaitForStreamlineInputs).
        if (state.inputsProcessingCompletionFence != nullptr)
        {
            static_cast<IUnknown*>(state.inputsProcessingCompletionFence)->QueryInterface(IID_PPV_ARGS(&result.inputsFence));
            result.inputsFenceValue = state.lastPresentInputsProcessingCompletionFenceValue;
        }
        // Said of a Present DLSS-G generated for with its status fine, once.
        if (!loggedFence && result.statusOk)
        {
            loggedFence = true;
            LogLine(std::string("DLSS frame generation: its inputs' completion fence is ")
                    + (result.inputsFence ? "given -- the proxy waits for it before D3D11 writes them again"
                                          : "not given -- D3D11 waits for the proxy's own queue only"));
        }
        return result;
    }
}
