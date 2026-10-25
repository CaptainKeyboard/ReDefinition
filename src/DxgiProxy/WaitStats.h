#pragma once

#include <windows.h>

#include <atomic>
#include <cstdint>

// How long the thread that calls in -- Unity's render thread -- is held up waiting for
// the GPU at the proxy's CPU waits, summed between two of the proxy's reports
// (SwapChainProxy::ReportFrameTimes): what makes Unity's main thread wait for it
// (Gfx.WaitForPresentOnGfxThread), told apart from the GPU's own frame time.
namespace redefinition
{
    // The waits told apart, each with its own sum; Count is their number.
    enum class WaitKind
    {
        PresentFrame,       // a frame's allocator still in flight (SwapChainProxy::WaitForFrame)
        RaytracingSlot,     // a raytracing slot's earlier list still on the GPU (Raytracing::WaitForSlotLocked)
        FrameGenerationInputs, // DLSS-G still reading the inputs D3D11 is about to write
        Count,
    };

    // Per kind: the ticks waited and the waits counted since the last report, which takes and clears them.
    inline std::atomic<int64_t> g_waitTicks[static_cast<int>(WaitKind::Count)];
    inline std::atomic<int64_t> g_waitCount[static_cast<int>(WaitKind::Count)];
    // The same waits summed since the start, never reset, and how long the
    // swapchain's Present held the presenting thread: what the latency brake takes
    // per frame (LowLatency::Brake).
    inline std::atomic<int64_t> g_waitTotalTicks{ 0 };
    inline std::atomic<int64_t> g_presentTotalTicks{ 0 };

    // Times the scope it is made in as a wait of its kind.
    class WaitTimer
    {
    public:
        // Starts timing the wait now.
        explicit WaitTimer(WaitKind kind) : kind(kind) { QueryPerformanceCounter(&start); }
        // Adds the time since it was made to its kind's sum and count and to the total.
        ~WaitTimer()
        {
            LARGE_INTEGER end;
            QueryPerformanceCounter(&end);
            g_waitTicks[static_cast<int>(kind)] += end.QuadPart - start.QuadPart;
            ++g_waitCount[static_cast<int>(kind)];
            g_waitTotalTicks += end.QuadPart - start.QuadPart;
        }

    private:
        // The kind it counts as, and the counter when it was made.
        WaitKind kind;
        LARGE_INTEGER start;
    };
}
