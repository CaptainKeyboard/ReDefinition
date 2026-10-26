#pragma once

#include <windows.h>

#include <cstdint>

namespace redefinition
{
    // An NVIDIA GPU as NVAPI names it.
    struct NvidiaGpuInfo
    {
        // NV_GPU_ARCHITECTURE_ID and NV_GPU_ARCH_IMPLEMENTATION_ID (nvapi.h).
        uint32_t architecture = 0;
        uint32_t implementation = 0;
    };

    // The NVIDIA GPU architecture of an adapter, through NVAPI (nvapi64.dll, part of NVIDIA's
    // driver). It finds it as Streamline does itself (sl.common's commonInterface.cpp). It takes the
    // physical GPUs and each one's LUID through its logical GPU. It returns the architecture of the
    // one with the adapter's LUID. The answer is not Found where the adapter is not NVIDIA's, or
    // NVAPI is not there or fails.
    //
    // What decides whether NVIDIA's DLLs are offered for download before they
    // exist, and so before Streamline or NGX could be asked (NvidiaFiles.cs).
    enum class NvidiaGpuAnswer
    {
        // The adapter is an NVIDIA GPU, its architecture known.
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
    // The adapter's architecture into info where the answer is Found.
    NvidiaGpuAnswer NvidiaGpu(const LUID& adapter, NvidiaGpuInfo& info);

    // Records the adapter KSP creates its swapchain on, told by the factory hook whatever the proxy
    // does with the swapchain. It also asks NVAPI for the adapter's NVIDIA GPU there, at the game's
    // start. Only where NVAPI did not answer is it asked again from KnownNvidiaGpu, at most every
    // ten seconds and a few times.
    void NoteSwapChainAdapter(const LUID& adapter);
    // That adapter's NVIDIA GPU into info; false where it is none or NVAPI has not answered yet.
    bool KnownNvidiaGpu(NvidiaGpuInfo& info);

    // A function of NVAPI by its id (nvapi_interface.h), NVAPI initialised; null
    // without NVIDIA's driver or where the driver lacks it.
    void* NvapiInterface(uint32_t id);

    // NVIDIA's display driver version, through NVAPI, as NVIDIA numbers it: 59716 for
    // 597.16; 0 without NVIDIA's driver or where NVAPI does not answer.
    uint32_t NvidiaDriverVersion();

    // That adapter's LUID; false before a swapchain was made.
    bool SwapChainAdapter(LUID& adapter);
}
