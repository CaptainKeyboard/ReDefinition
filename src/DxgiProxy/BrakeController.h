#pragma once

#include <algorithm>

namespace redefinition
{
    // The latency brake's controller (LowLatency::Brake), free of the GPU and of the clock so
    // that the harness can run it (ProxyHarness.cpp, "brake controller").
    //
    // Per frame it takes the frame's wall time, the waits the frame met in the proxy and the
    // target for them, and gives the sleep before the next frame's simulation: an integrating
    // controller, a fifth of the difference a frame. The frame's own work is the wall time less
    // the sleep taken, so that the clamp below does not grow with the sleep.
    //
    // A hitch -- one frame far longer than the others, a level load, a shader compile -- must
    // count as a frame's worth at most. So the frame time that bounds a sample, and the waits
    // counted, is the smoothed one from before the sample: the frame time smoothed with the
    // hitch in it would lift the bound with the hitch (a 500 ms frame lifts a 16 ms average to
    // 64 ms), and a sample is taken into the average at no more than twice that average.
    struct BrakeController
    {
        // The sleep before the next frame, the smoothed work and waits of a frame, in ms.
        double sleepMs = 0.0;
        double frameMs = 0.0;
        double waitMs = 0.0;

        static constexpr double kGain = 0.2;
        static constexpr double kSmoothing = 0.1;
        // A sample of a frame's work enters the average at twice the average at most.
        static constexpr double kSampleCeiling = 2.0;
        // The sleep is at most this share of a frame's work.
        static constexpr double kSleepShare = 0.5;

        // Back to the start: no sleep, nothing smoothed yet.
        void Reset()
        {
            *this = BrakeController();
        }

        // wallMs: the time since the last frame's start; waitedMs: the waits in the proxy in
        // that time; targetMs: the waiting left. Returns the sleep for the next frame.
        double Update(double wallMs, double waitedMs, double targetMs)
        {
            const double work = wallMs > sleepMs ? wallMs - sleepMs : wallMs;
            // The average from before this frame: what a hitch is measured against.
            const double before = frameMs;
            const double sample = before == 0.0 ? work : std::min(work, before * kSampleCeiling);
            frameMs = before == 0.0 ? sample : before * (1.0 - kSmoothing) + sample * kSmoothing;
            waitMs = waitMs * (1.0 - kSmoothing) + waitedMs * kSmoothing;

            const double target = targetMs > 0.5 ? targetMs : 0.5;
            const double counted = std::min(waitedMs, before == 0.0 ? sample : before);
            sleepMs += kGain * (counted - target);
            if (sleepMs < 0.0)
                sleepMs = 0.0;
            if (sleepMs > frameMs * kSleepShare)
                sleepMs = frameMs * kSleepShare;
            return sleepMs;
        }
    };
}
