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
    enum class WaitKind
    {
        PresentFrame,       // a frame's allocator still in flight (SwapChainProxy::WaitForFrame)
        RaytracingSlot,     // a raytracing slot's earlier list still on the GPU (Raytracing::WaitForSlotLocked)
        FrameGenerationInputs, // DLSS-G still reading the inputs D3D11 is about to write
        Count,
    };

    inline std::atomic<int64_t> g_waitTicks[static_cast<int>(WaitKind::Count)];
    inline std::atomic<int64_t> g_waitCount[static_cast<int>(WaitKind::Count)];

    // Times the scope it is made in as a wait of its kind.
    class WaitTimer
    {
    public:
        explicit WaitTimer(WaitKind kind) : kind(kind) { QueryPerformanceCounter(&start); }
        ~WaitTimer()
        {
            LARGE_INTEGER end;
            QueryPerformanceCounter(&end);
            g_waitTicks[static_cast<int>(kind)] += end.QuadPart - start.QuadPart;
            ++g_waitCount[static_cast<int>(kind)];
        }

    private:
        WaitKind kind;
        LARGE_INTEGER start;
    };
}
