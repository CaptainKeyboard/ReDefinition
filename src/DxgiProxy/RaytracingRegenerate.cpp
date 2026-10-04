// AMD FSR Ray Regeneration 1.2 for the raytraced frame (docs/development/raytracing.md,
// "Denoising"): the diffuse light and the reflections through AMD's denoiser where its
// DLL runs, a Radeon RX 9000 or newer (denoising.md, "Requirements"); the sun's
// visibility stays with ReDefinition's own denoiser, whose output keeps the view depth
// the next frame finds its pixels by. Where AMD's denoiser does not run, ReDefinition's
// denoises everything, as without it.
//
// AMD's API as its header gives it (extern/FidelityFX/denoisers/include/ffx_denoiser.h,
// FidelityFX SDK 2.3.0); the guides as its documentation asks for them (denoising.md,
// 5.2-5.5): signed linear depth, motion as previous minus current UV with the linear
// depth's change, octahedral normals with linear roughness and material type 0, the
// albedos square-root encoded.

#include "Raytracing.h"

#include "Config.h"
#include "D3d12Util.h"
#include "FileUtil.h"
#include "Log.h"

#include <FidelityFX/api/include/dx12/ffx_api_dx12.h>
#include <FidelityFX/api/include/ffx_api_loader.h>
#include <FidelityFX/denoisers/include/ffx_denoiser.h>

#include <cstring>

using Microsoft::WRL::ComPtr;

namespace redefinition
{
    namespace
    {
        constexpr const wchar_t* kRuntimeName = L"amd_fidelityfx_denoiser_dx12.dll";

        // Next to the executable, where the package puts it; else in amdUpscalerDirectory,
        // the folder of the player's own AMD DLLs.
        std::wstring RuntimePath()
        {
            const std::wstring beside = ExecutableDirectory() + L"\\" + kRuntimeName;
            if (GetFileAttributesW(beside.c_str()) != INVALID_FILE_ATTRIBUTES)
                return beside;
            const std::wstring directory = AmdUpscalerDirectory();
            return directory.empty() ? beside : directory + L"\\" + kRuntimeName;
        }

        FfxApiMatrix4x4 Matrix(const float* rows)
        {
            FfxApiMatrix4x4 m;
            memcpy(&m, rows, sizeof(m));
            return m;
        }

        uint32_t AmdSignals(uint32_t signals)
        {
            return ((signals & kRtSignalDiffuse) != 0 ? static_cast<uint32_t>(FFX_DENOISER_SIGNAL_INDIRECT_DIFFUSE) : 0u)
                 | ((signals & kRtSignalSpecular) != 0 ? static_cast<uint32_t>(FFX_DENOISER_SIGNAL_INDIRECT_SPECULAR) : 0u);
        }
    }


    bool Raytracing::RegenerationReadyLocked(const RtFramePacket& packet)
    {
        const uint32_t wanted = AmdSignals(packet.signals);
        if (wanted == 0)
            return false;
        Regeneration& r = regeneration;

        if (r.api.CreateContext == nullptr)
        {
            if (r.tried)
                return false;
            r.tried = true;
            const std::wstring path = RuntimePath();
            r.module = LoadLibraryExW(path.c_str(), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
            if (r.module == nullptr)
            {
                regenerationWhy = "no " + Narrow(path);
                LogLine("Raytracing: AMD FSR Ray Regeneration not there (" + regenerationWhy + "); ReDefinition's denoiser");
                return false;
            }
            ffxLoadFunctions(&r.api, r.module);
            if (r.api.CreateContext == nullptr || r.api.DestroyContext == nullptr || r.api.Dispatch == nullptr
                || r.api.Query == nullptr)
            {
                r.api = ffxFunctions{};
                FreeLibrary(r.module);
                r.module = nullptr;
                regenerationWhy = Narrow(path) + " is not AMD's FidelityFX API";
                LogLine("Raytracing: " + regenerationWhy + "; ReDefinition's denoiser");
                return false;
            }
            LogLine("Raytracing: AMD FSR Ray Regeneration from " + Narrow(path) + " " + FileVersion(path));
        }

        if (r.unsupported)
            return false;
        if (r.context != nullptr && r.width == packet.width && r.height == packet.height && r.signals == wanted)
            return true;

        // A context for this size and these signals; the one before once the queue is past it.
        if (r.context != nullptr)
        {
            WaitForAllLocked();
            r.api.DestroyContext(&r.context, nullptr);
            r.context = nullptr;
        }
        ffxCreateContextDescDenoiser create = {};
        create.header.type = FFX_API_CREATE_CONTEXT_DESC_TYPE_DENOISER;
        create.version = FFX_DENOISER_VERSION;
        create.maxRenderSize = { packet.width, packet.height };
        create.signalFlags = wanted;
        create.checkerboardSignalFlags = FFX_DENOISER_SIGNAL_NONE;
        create.flags = 0;
        ffxCreateBackendDX12Desc backend = {};
        backend.header.type = FFX_API_CREATE_CONTEXT_DESC_TYPE_BACKEND_DX12;
        backend.device = device.Get();
        create.header.pNext = &backend.header;
        backend.header.pNext = nullptr;
        const ffxReturnCode_t code = r.api.CreateContext(&r.context, &create.header, nullptr);
        if (code != FFX_API_RETURN_OK || r.context == nullptr)
        {
            r.context = nullptr;
            r.unsupported = true;
            regenerationWhy = "AMD FSR Ray Regeneration could not start on this GPU, code " + std::to_string(static_cast<int>(code))
                              + " (it needs a Radeon RX 9000 or newer)";
            LogLine("Raytracing: " + regenerationWhy + "; ReDefinition's denoiser instead");
            return false;
        }
        std::string provider = "AMD FSR Ray Regeneration";
        ffxQueryGetProviderVersion version = {};
        version.header.type = FFX_API_QUERY_DESC_TYPE_GET_PROVIDER_VERSION;
        if (r.api.Query(&r.context, &version.header) == FFX_API_RETURN_OK && version.versionName != nullptr)
            provider += std::string(" ") + version.versionName;
        r.width = packet.width;
        r.height = packet.height;
        r.signals = wanted;
        r.reset = true;
        regenerationWhy.clear();
        LogLine("Raytracing: " + provider + " denoises the diffuse light and the reflections, "
                + std::to_string(packet.width) + "x" + std::to_string(packet.height));
        return true;
    }

    bool Raytracing::RunRegenerationLocked(const RtFramePacket& packet, UINT slotNumber, Shared* const* outputs,
                                           Shared* motionInput, Slot& slot)
    {
        Regeneration& r = regeneration;
        auto guidePass = denoisePipelines.find("RT_RegenerationGuides");
        auto albedo = denoisePipelines.find("RT_RegenerationAlbedo");
        if (guidePass == denoisePipelines.end() || albedo == denoisePipelines.end())
            return false;

        // The guides at this size.
        if (r.linearDepth.resource == nullptr || r.width != r.linearDepth.resource->GetDesc().Width
            || r.height != r.linearDepth.resource->GetDesc().Height)
        {
            for (DenoiseTarget* target : { &r.linearDepth, &r.motion, &r.normals, &r.specularAlbedo, &r.diffuseAlbedo })
            {
                if (target->resource != nullptr)
                    slot.held.push_back(target->resource);
                *target = DenoiseTarget{};
            }
            const DXGI_FORMAT half4 = DXGI_FORMAT_R16G16B16A16_FLOAT;
            if (!r.linearDepth.Create(device.Get(), packet.width, packet.height, DXGI_FORMAT_R32_FLOAT)
                || !r.motion.Create(device.Get(), packet.width, packet.height, half4)
                || !r.normals.Create(device.Get(), packet.width, packet.height, half4)
                || !r.specularAlbedo.Create(device.Get(), packet.width, packet.height, half4)
                || !r.diffuseAlbedo.Create(device.Get(), packet.width, packet.height, half4))
            {
                SetProblem("AMD FSR Ray Regeneration's guides could not be made");
                return false;
            }
        }

        const UINT groupsX = (packet.width + 7) / 8;
        const UINT groupsY = (packet.height + 7) / 8;
        const bool motion = motionInput != nullptr && (packet.flags & kRtFrameMotionVectors) != 0;
        const uint32_t values[4] = { motion ? 2u : 0u, 0u, 0u, 0u };
        list->SetComputeRoot32BitConstants(9, 4, values, 0);
        for (DenoiseTarget* target : { &r.linearDepth, &r.motion, &r.normals, &r.specularAlbedo, &r.diffuseAlbedo })
            UseLocked(*target, D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
        {
            UINT used = kRegenerationGuidesTable;
            const View reads[8] = {};
            const View writes[4] = { Of(&r.linearDepth), Of(&r.motion), Of(&r.normals), Of(&r.specularAlbedo) };
            BindTablesLocked(TableLocked(slotNumber, used, reads, writes));
            list->SetPipelineState(guidePass->second.Get());
            list->Dispatch(groupsX, groupsY, 1);
        }
        {
            UINT used = kRegenerationAlbedoTable;
            const View reads[8] = {};
            const View writes[4] = { Of(&r.diffuseAlbedo) };
            BindTablesLocked(TableLocked(slotNumber, used, reads, writes));
            list->SetPipelineState(albedo->second.Get());
            list->Dispatch(groupsX, groupsY, 1);
        }
        for (DenoiseTarget* target : { &r.linearDepth, &r.motion, &r.normals, &r.specularAlbedo, &r.diffuseAlbedo })
            UseLocked(*target, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);

        // The noisy signals the passes wrote to be read, Unity's outputs, in UAV state
        // through the frame, to be written; AMD's denoiser leaves each in the state it
        // is given.
        const uint32_t read = FFX_API_RESOURCE_STATE_COMPUTE_READ;
        const uint32_t write = FFX_API_RESOURCE_STATE_UNORDERED_ACCESS;
        ffxDispatchDescDenoiser dispatch = {};
        dispatch.header.type = FFX_API_DISPATCH_DESC_TYPE_DENOISER;
        dispatch.commandList = list.Get();
        dispatch.linearDepth = ffxApiGetResourceDX12(r.linearDepth.resource.Get(), read);
        dispatch.motionVectors = ffxApiGetResourceDX12(r.motion.resource.Get(), read);
        dispatch.normals = ffxApiGetResourceDX12(r.normals.resource.Get(), read);
        dispatch.specularAlbedo = ffxApiGetResourceDX12(r.specularAlbedo.resource.Get(), read);
        dispatch.diffuseAlbedo = ffxApiGetResourceDX12(r.diffuseAlbedo.resource.Get(), read);
        dispatch.motionVectorScale = { 1.0f, 1.0f, 1.0f };
        dispatch.jitterOffsets = { packet.jitter[0], packet.jitter[1] };
        dispatch.cameraPositionDelta = { packet.cameraDelta[0], packet.cameraDelta[1], packet.cameraDelta[2] };
        dispatch.view = Matrix(packet.worldToView);
        dispatch.projection = Matrix(packet.viewToClip);
        dispatch.linearDepthBounds = { packet.depthRange[0], packet.depthRange[1] };
        dispatch.renderSize = { packet.width, packet.height };
        dispatch.frameIndex = packet.frameIndex;
        // Afresh where the frame before was not AMD's: its history is older than that.
        const bool continued = r.hadFrame && packet.frameIndex == r.lastFrame + 1;
        dispatch.flags = r.reset || !continued || (packet.flags & kRtFrameReset) != 0 ? FFX_DENOISER_DISPATCH_RESET : 0u;

        ffxDispatchDescDenoiserIndirectDiffuse diffuse = {};
        diffuse.header.type = FFX_API_DISPATCH_DESC_TYPE_DENOISER_INDIRECT_DIFFUSE;
        ffxDispatchDescDenoiserIndirectSpecular specular = {};
        specular.header.type = FFX_API_DISPATCH_DESC_TYPE_DENOISER_INDIRECT_SPECULAR;
        ffxApiHeader* last = &dispatch.header;
        const struct
        {
            const RtDenoisedSignal& denoised;
            FfxApiDenoiserSignal* target;
            ffxApiHeader* header;
        } signals[2] = {
            { RtDenoisedFor(kRtSignalDiffuse), &diffuse.signal, &diffuse.header },
            { RtDenoisedFor(kRtSignalSpecular), &specular.signal, &specular.header },
        };
        for (const auto& signal : signals)
        {
            if ((r.signals & AmdSignals(signal.denoised.signal)) == 0)
                continue;
            DenoiseTarget& noisy = denoise.noisy[signal.denoised.index];
            if (outputs[signal.denoised.output] == nullptr || noisy.resource == nullptr)
                return false;
            UseLocked(noisy, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
            signal.target->input = ffxApiGetResourceDX12(noisy.resource.Get(), read);
            signal.target->output = ffxApiGetResourceDX12(outputs[signal.denoised.output]->texture.d3d12.Get(), write);
            signal.target->checkerboardOrigin = 0;
            last->pNext = signal.header;
            last = signal.header;
        }
        last->pNext = nullptr;

        const ffxReturnCode_t code = r.api.Dispatch(&r.context, &dispatch.header);
        if (code != FFX_API_RETURN_OK)
        {
            // Not tried again until the device goes; the context stays until then.
            regenerationWhy = "AMD FSR Ray Regeneration did not denoise, code " + std::to_string(static_cast<int>(code));
            SetProblem(regenerationWhy + "; ReDefinition's denoiser instead");
            r.unsupported = true;
            return false;
        }
        r.reset = false;
        r.lastFrame = packet.frameIndex;
        r.hadFrame = true;
        std::lock_guard<std::mutex> status(statusLock);
        ++regenerated;
        return true;
    }

    void Raytracing::ReleaseRegenerationLocked()
    {
        Regeneration& r = regeneration;
        if (r.context != nullptr)
            r.api.DestroyContext(&r.context, nullptr);
        r.context = nullptr;
        for (DenoiseTarget* target : { &r.linearDepth, &r.motion, &r.normals, &r.specularAlbedo, &r.diffuseAlbedo })
            *target = DenoiseTarget{};
        r.width = r.height = 0;
        r.signals = 0;
        r.unsupported = false;
        r.hadFrame = false;
        r.previousWasAmd = false;
    }
}
