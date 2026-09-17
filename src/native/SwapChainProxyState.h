#pragma once

#include <windows.h>

#include <atomic>
#include <cstdint>

namespace ksp
{
    // What the proxy's swapchains share process-wide, for the files that make up
    // SwapChainProxy.

    // Running totals for the mod's window: frames rendered in the high
    // half, frames presented in the low half. Written by the render thread
    // in CopyAndPresent, read by the game's main thread through
    // KspFgCounters -- one word, so a reading never mixes two frames;
    // process-wide, so a swapchain made anew carries on counting.
    inline std::atomic<uint64_t> g_counters{ 0 };
    inline std::atomic<bool> g_countersValid{ false };

    // Why the proxy's device was removed; S_OK while it was not, and again
    // once a new proxy has a device of its own.
    inline std::atomic<HRESULT> g_deviceRemoved{ S_OK };

    // Swapchains made through FidelityFX or Streamline, the ones that can
    // generate frames, and of those the ones made through Streamline.
    inline std::atomic<int> g_generatingSwapChains{ 0 };
    inline std::atomic<int> g_streamlineSwapChains{ 0 };
}
