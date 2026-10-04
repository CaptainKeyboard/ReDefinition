#pragma once

// What the raytracing service's sources share among themselves (Raytracing*.cpp): the
// passes and their shaders, the timestamps, the layout of a mesh's attributes.

#include "Raytracing.h"

#include <algorithm>

namespace redefinition
{
    // Per vertex: normal (three floats), texture coordinate (two), colour (four bytes).
    inline constexpr size_t kAttributeFloats = 6;

    // A shared copy of a Unity texture not used for this long is released.
    inline constexpr ULONGLONG kUnusedMs = 10000;
    // Start, after the build, after each pass.
    inline constexpr UINT kTimestampsPerSlot = 2 + kRtPassTimings;

    // The passes, by the signal bit that asks for them, and the entry point each is
    // compiled from (CMakeLists.txt embeds them as RT_<entry>).
    struct RtPassEntry
    {
        uint32_t signal;
        const wchar_t* resource;
    };
    inline const RtPassEntry kRtPasses[] = {
        { kRtSignalSunVisibility, L"RT_SunVisibility" },
        { kRtSignalAmbientOcclusion, L"RT_AmbientOcclusion" },
        { kRtSignalDiffuse, L"RT_DiffuseLight" },
        { kRtSignalSpecular, L"RT_SpecularLight" },
        { kRtSignalForwardLight, L"RT_ForwardLight" },
        { kRtSignalPrimaryCheck, L"RT_PrimaryCheck" },
    };

    // The passes in the order they run, each by its place in PassTimings: the signals
    // first, then the denoiser, then what reads the denoised signals.
    inline constexpr UINT kRtPassOrder[kRtPassTimings] = { 0, 1, 2, 3, kRtPassDenoiser, 4, 5, 7 };

    // The entry points of the passes after the trace (RaytracingShaders/Denoise.hlsl and
    // the others beside it).
    inline const wchar_t* const kRtDenoiseEntries[] = {
        L"RT_DenoisePrepare", L"RT_DenoiseTemporal", L"RT_DenoiseVariance", L"RT_DenoiseAtrous",
        L"RT_ReconstructGuides", L"RT_FrameGuides", L"RT_GpuInstances", L"RT_RegenerationGuides",
        L"RT_RegenerationAlbedo",
    };

    // A buffer's description: at least 256 bytes, row major.
    inline D3D12_RESOURCE_DESC RtBufferDesc(UINT64 size, D3D12_RESOURCE_FLAGS flags)
    {
        D3D12_RESOURCE_DESC desc = {};
        desc.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
        desc.Width = std::max<UINT64>(size, 256);
        desc.Height = 1;
        desc.DepthOrArraySize = 1;
        desc.MipLevels = 1;
        desc.SampleDesc.Count = 1;
        desc.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
        desc.Flags = flags;
        return desc;
    }
}
