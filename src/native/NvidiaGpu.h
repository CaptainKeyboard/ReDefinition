#pragma once

#include <windows.h>

#include <cstdint>

namespace ksp
{
    struct NvidiaGpuInfo
    {
        // NV_GPU_ARCHITECTURE_ID and NV_GPU_ARCH_IMPLEMENTATION_ID (nvapi.h).
        uint32_t architecture = 0;
        uint32_t implementation = 0;
    };

    // The NVIDIA GPU architecture of an adapter, through NVAPI -- nvapi64.dll,
    // part of NVIDIA's driver -- as Streamline finds it itself (sl.common's
    // commonInterface.cpp): the physical GPUs, each one's LUID through its logical
    // GPU, and the architecture of the one with the adapter's LUID. False where the
    // adapter is not NVIDIA's, or NVAPI is not there or fails.
    //
    // What decides whether NVIDIA's DLLs are offered for download before they
    // exist, and so before Streamline or NGX could be asked (NvidiaFiles.cs).
    enum class NvidiaGpuAnswer
    {
        Found,
        // No NVIDIA driver, or none of NVIDIA's GPUs is the adapter: asking again
        // changes nothing.
        NotNvidia,
        // NVAPI is there but did not answer this time.
        NoAnswer,
        // NVAPI is there without a function asked for: a driver older than the
        // headers. Not asked again either.
        Incomplete,
    };
    NvidiaGpuAnswer NvidiaGpu(const LUID& adapter, NvidiaGpuInfo& info);

    // The adapter KSP creates its swapchain on, told by the factory hook whatever
    // the proxy does with the swapchain, and its NVIDIA GPU: asked of NVAPI there,
    // at the game's start, and -- only where NVAPI did not answer -- again from
    // KnownNvidiaGpu, at most every ten seconds and a few times.
    void NoteSwapChainAdapter(const LUID& adapter);
    bool KnownNvidiaGpu(NvidiaGpuInfo& info);
}
