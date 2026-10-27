// Frame generation's frame rate gate (FrameGeneration): the rendered frame rate
// against fgMinimumFps, for FSR, DLSS-G and XeSS-FG alike.

#include "FrameGeneration.h"

#include "Config.h"
#include "Log.h"

#include <cstdio>
#include <string>

namespace redefinition
{
    float FrameGeneration::MinimumFpsLocked() const
    {
        const float configured = Config().fgMinimumFps;
        if (configured >= 0.0f)
            return configured;
        // XeSS-FG: "40 FPS minimum (XeSS-SR or native input specification): This
        // provides adequate reconstruction for fluid gameplay"
        // (xess_fg_developer_guide_english.md, Introduction).
        if (xess)
            return 40.0f;
        return streamline ? 30.0f : static_cast<float>(designedForFps);
    }

    void FrameGeneration::SetPresentCapLocked(double capFps, uint32_t shownPerRendered)
    {
        presentCapFps = capFps;
        presentCapFactor = shownPerRendered > 0 ? shownPerRendered : 1u;
    }

    const char* FrameGeneration::GateReasonLocked() const
    {
        return !wanted ? "switched off in the mod"
               : rateTooLow || slowFrame ? "the rendered frame rate is below its minimum (fgMinimumFps)"
               : nullptr;
    }

    bool FrameGeneration::TakeFrameInputsLocked()
    {
        const bool thisFrame = packetFresh && inputsCopied;
        packetFresh = false;
        inputsCopied = false;
        generatedLastPresent = false;
        return thisFrame;
    }

    // Each rendered frame's time from its packet; every two seconds the average decides.
    void FrameGeneration::NoteFrameRateLocked(float frameTimeMs)
    {
        const float minimumFps = MinimumFpsLocked();
        // Capped below the minimum while generating: held off as long as the cap stands.
        const double cappedRate = presentCapFps > 0.0 ? presentCapFps / static_cast<double>(presentCapFactor) : 0.0;
        const bool capped = minimumFps > 0.0f && cappedRate > 0.0 && cappedRate < minimumFps * 0.9;
        if (capped != heldByCap)
        {
            heldByCap = capped;
            rateWindowMs = 0.0;
            rateWindowFrames = 0;
            slowFrame = false;
            rateTooLow = capped;
            char line[220];
            if (capped)
                std::snprintf(line, sizeof(line), "Frame generation held off: V-Sync shows at most %.0f frames a second, and"
                              " with frame generation the game would render %.0f, below the minimum %.0f (fgMinimumFps)",
                              presentCapFps, cappedRate, minimumFps);
            else
                std::snprintf(line, sizeof(line), "Frame generation allowed again: the frame rate is no longer capped below"
                              " its minimum");
            LogLine(line);
        }
        if (capped)
            return;
        const bool slow = minimumFps > 0.0f && frameTimeMs > 1500.0f / minimumFps;
        if (slow && !slowFrame && slowFrames++ == 0)
        {
            char note[160];
            std::snprintf(note, sizeof(note), "Frame generation: a frame of %.0f ms, above %.0f ms, is presented without"
                          " generated frames (and so is each such frame after it)", frameTimeMs, 1500.0f / minimumFps);
            LogLine(note);
        }
        slowFrame = slow;
        if (frameTimeMs <= 0.0f || frameTimeMs > 1000.0f)
            return;
        rateWindowMs += frameTimeMs;
        ++rateWindowFrames;
        if (rateWindowMs < 2000.0)
            return;
        const double fps = 1000.0 * rateWindowFrames / rateWindowMs;
        rateWindowMs = 0.0;
        rateWindowFrames = 0;
        // Off below nine tenths of the minimum, on again above nineteen twentieths: a
        // game held at 60 by V-Sync averages a little under it, and turning frame
        // generation off gives back only its own cost, which AMD says a decision should
        // not flip on.
        const bool tooLow = minimumFps > 0.0f && (rateTooLow ? fps < minimumFps * 0.95 : fps < minimumFps * 0.9);
        if (tooLow == rateTooLow)
            return;
        rateTooLow = tooLow;
        char line[200];
        std::snprintf(line, sizeof(line), "Frame generation %s: %.1f fps rendered over two seconds, minimum %.0f%s",
                      tooLow ? "held off" : "allowed again", fps, minimumFps,
                      tooLow ? " (fgMinimumFps), off below nine tenths of it" : ", back above nineteen twentieths of it");
        LogLine(line);
    }
}
