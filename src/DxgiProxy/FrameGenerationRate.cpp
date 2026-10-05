// Frame generation's frame rate gate (FrameGeneration): the rendered frame rate
// against fgMinimumFps, for FSR and DLSS-G alike.

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
        return streamline ? 30.0f : static_cast<float>(designedForFps);
    }

    // Each rendered frame's time from its packet; every two seconds the average decides.
    void FrameGeneration::NoteFrameRateLocked(float frameTimeMs)
    {
        const float minimumFps = MinimumFpsLocked();
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
        const float minimum = MinimumFpsLocked();
        // Off below nine tenths of the minimum, on again above nineteen twentieths: a
        // game held at 60 by V-Sync averages a little under it, and turning frame
        // generation off gives back only its own cost, which AMD says a decision should
        // not flip on.
        const bool tooLow = minimum > 0.0f && (rateTooLow ? fps < minimum * 0.95 : fps < minimum * 0.9);
        if (tooLow == rateTooLow)
            return;
        rateTooLow = tooLow;
        char line[200];
        std::snprintf(line, sizeof(line), "Frame generation %s: %.1f fps rendered over two seconds, minimum %.0f%s",
                      tooLow ? "held off" : "allowed again", fps, minimum,
                      tooLow ? " (fgMinimumFps), off below nine tenths of it" : ", back above nineteen twentieths of it");
        LogLine(line);
    }
}
