// DLSS Ray Reconstruction through NGX on the proxy's Direct3D 12 device
// (DlssRayReconstruction.h).
//
// What NGX is handed for a frame follows Streamline's sl.dlss_d, which does the same
// for the same inputs (Streamline 2.14.16, source/plugins/sl.dlss_d/dlss_dEntry.cpp,
// MIT licence, Copyright (c) 2023 NVIDIA CORPORATION): the feature's creation
// parameters and flags, the optimal render size at creation, the mode as
// "SL DLSSDContext modes start with 'off' so subtract one", the motion vector scale
// in render pixels, the presets per mode, and the two matrices. Adapted to this
// proxy's own resources and barriers. OptiScaler's DLSSDFeature_Dx12.cpp is a
// shipped precedent of the same calls into the driver's NGX core.

#include "DlssRayReconstruction.h"

#include "Config.h"
#include "FileUtil.h"
#include "Log.h"
#include "ProjectIdentity.h"

#include <algorithm>
#include <cstring>
#include <vector>

using Microsoft::WRL::ComPtr;

namespace redefinition
{
    namespace
    {
        // NVSDK_NGX_Feature_RayReconstruction (nvsdk_ngx_defs.h).
        constexpr int kNgxFeatureRayReconstruction = 13;
        // nvsdk_ngx_defs_dlssd.h.
        constexpr int kNgxDenoiseModeDlUnified = 1;
        constexpr unsigned int kNgxRoughnessModePacked = 1;
        constexpr unsigned int kNgxDepthTypeHardware = 1;

        // The driver's D3D12 exports (NVSDK_NGX_D3D12_* in nvsdk_ngx.h), with
        // Init_ProjectID's arguments in the order of D3D11's (Dlss.cpp).
        using InitProjectIdFn = uint32_t (*)(const char* projectId, int engineType, const char* engineVersion,
                                             const wchar_t* dataPath, ID3D12Device* device, int sdkVersion,
                                             const NgxCommonInfo* info);
        using CreateFeatureFn = uint32_t (*)(ID3D12GraphicsCommandList* list, int feature, NgxParameters* parameters,
                                             void** handle);
        using EvaluateFeatureFn = uint32_t (*)(ID3D12GraphicsCommandList* list, const void* handle,
                                               const NgxParameters* parameters, void* progress);
        // NVSDK_NGX_D3D12_Shutdown, not Shutdown1: the driver's Shutdown1 export ends
        // the process with an access violation inside _nvngx.dll once a feature was
        // created and released on the device -- with the device or nullptr, measured
        // with driver 597.16 in a program of its own (init, CreateFeature for Ray
        // Reconstruction or Super Resolution, ReleaseFeature, Shutdown1); Shutdown
        // returns success there, and an NGX instance on a Direct3D 11 device keeps
        // running past it (its capabilities and a new feature after it, and Super
        // Resolution's live feature went on evaluating: driver 597.16, RaytracingHarness,
        // "Super Resolution running after Ray Reconstruction's NGX shut down"). "Legacy
        // Shutdown() call does not have device pointer - so it will shut down NGX on
        // ALL devices" (DLSS Programming Guide 5.7): on Direct3D 12 the only other
        // caller is Streamline's sl.common, which is not set up while this runs
        // (ChooseRoute, RaytracingReconstruct.cpp); a Streamline of a second swapchain
        // made while the first still lives would lose NGX with this one's.
        using ShutdownFn = uint32_t (*)();

        // The driver's D3D12 exports the proxy calls.
        struct NgxD3d12
        {
            // Init, the capability and parameter maps, the feature's creation, evaluation and release, shutdown.
            InitProjectIdFn initProjectId = nullptr;
            NgxGetParametersFn getCapabilityParameters = nullptr;
            NgxGetParametersFn getParameters = nullptr;
            NgxDestroyParametersFn destroyParameters = nullptr;
            CreateFeatureFn createFeature = nullptr;
            EvaluateFeatureFn evaluateFeature = nullptr;
            NgxReleaseFeatureFn releaseFeature = nullptr;
            ShutdownFn shutdown = nullptr;
        };

        // The exports, resolved once from the driver's core.
        NgxD3d12 g_ngx;

        // The folder without trailing separators.
        std::wstring Folder(std::wstring folder)
        {
            while (!folder.empty() && (folder.back() == L'\\' || folder.back() == L'/'))
                folder.pop_back();
            return folder;
        }

        // Where NGX looks for nvngx_dlssd.dll: where "NVIDIA DLSS files" puts it with
        // Streamline's DLLs, where nvngx_dlss.dll lies, and next to KSP_x64.exe.
        std::vector<std::wstring> SearchFolders()
        {
            return { Folder(StreamlineDirectory()), Folder(DlssDirectory()), ExecutableDirectory() };
        }

        // Whether the frame asks for these sizes, quality and preset.
        bool Same(const NgxRrFrame& frame, uint32_t renderWidth, uint32_t renderHeight, uint32_t outputWidth,
                  uint32_t outputHeight, int32_t quality, uint32_t preset)
        {
            return frame.renderWidth == renderWidth && frame.renderHeight == renderHeight
                   && frame.outputWidth == outputWidth && frame.outputHeight == outputHeight
                   && frame.quality == quality && frame.preset == preset;
        }
    }

    // Never destroyed, like Dlss: at process exit a static destructor would call into
    // NGX after the driver's core may already be gone.
    DlssRayReconstruction& DlssRayReconstruction::Get()
    {
        static DlssRayReconstruction* instance = new DlssRayReconstruction();
        return *instance;
    }

    std::wstring DlssRayReconstruction::SearchSignature() const
    {
        std::wstring signature;
        for (const std::wstring& folder : SearchFolders())
            if (!folder.empty())
                signature += folder + L"|" + FileStamp(folder + L"\\nvngx_dlssd.dll") + L";";
        return signature;
    }

    void DlssRayReconstruction::FailStable(const std::string& why)
    {
        const std::wstring signature = SearchSignature();
        std::lock_guard<std::mutex> lock(stateMutex);
        if (stableWhy != why)
            LogLine("DLSS Ray Reconstruction through NGX: " + why);
        stableWhy = why;
        stableSearch = signature;
    }

    bool DlssRayReconstruction::Ready(std::string& why)
    {
        if (!NgxCoreRegistered())
        {
            why = "no NVIDIA NGX in the driver: Ray Reconstruction needs an NVIDIA RTX GPU and its driver";
            return false;
        }
        std::lock_guard<std::mutex> lock(stateMutex);
        if (!stableWhy.empty())
        {
            // Tried again once the player has fetched nvngx_dlssd.dll or set a
            // folder; the files are asked at most once a second.
            const ULONGLONG now = GetTickCount64();
            if (now < nextSearchCheck || SearchSignature() == stableSearch)
            {
                nextSearchCheck = std::max(nextSearchCheck, now + 1000);
                why = stableWhy;
                return false;
            }
            stableWhy.clear();
        }
        why = "ready through NGX on the proxy's Direct3D 12 device";
        return true;
    }

    bool DlssRayReconstruction::Evaluate(ID3D12Device* target, ID3D12GraphicsCommandList* list, const NgxRrFrame& frame,
                                         const std::function<void()>& idle, std::string& problem)
    {
        if (target == nullptr || list == nullptr)
        {
            problem = "no Direct3D 12 device or command list";
            return false;
        }
        std::lock_guard<std::mutex> ngx(NgxMutex());
        {
            std::lock_guard<std::mutex> lock(stateMutex);
            if (!stableWhy.empty())
            {
                problem = stableWhy;
                return false;
            }
        }
        if (!EnsureInitialisedLocked(target, problem) || !EnsureFeatureLocked(list, frame, idle, problem))
            return false;

        NgxParameters* map = parameters;
        map->Set("Color", frame.colour);
        map->Set("Output", frame.output);
        map->Set("Depth", frame.depth);
        map->Set("MotionVectors", frame.motion);
        map->Set("DLSS.Input.DiffuseAlbedo", frame.albedo);
        map->Set("DLSS.Input.SpecularAlbedo", frame.specularAlbedo);
        map->Set("GBuffer.Normals", frame.normalRoughness);
        // Packed into the normals' w (DLSS.Roughness.Mode), as sl.dlss_d leaves it.
        map->Set("GBuffer.Roughness", static_cast<ID3D12Resource*>(nullptr));
        map->Set("DLSSD.SpecularHitDistance", frame.specularHitDistance);
        map->Set("Jitter.Offset.X", frame.jitterX);
        map->Set("Jitter.Offset.Y", frame.jitterY);
        map->Set("MV.Scale.X", frame.motionScaleX);
        map->Set("MV.Scale.Y", frame.motionScaleY);
        // As sl.dlss_d forces it for a feature just made (dlss_dEntry.cpp sets
        // commonConsts->reset where the viewport has no handle or the mode or size changed):
        // its history is empty, whatever the caller says.
        map->Set("Reset", frame.reset || resetFeature ? 1 : 0);
        map->Set("DLSS.Render.Subrect.Dimensions.Width", static_cast<unsigned int>(frame.renderWidth));
        map->Set("DLSS.Render.Subrect.Dimensions.Height", static_cast<unsigned int>(frame.renderHeight));
        map->Set("DLSS.Pre.Exposure", 1.0f);
        map->Set("DLSS.Exposure.Scale", 1.0f);
        map->Set("Sharpness", 0.0f);
        // "The application needs to provide its World To View Matrix and View To Clip
        // Space Matrix" with the specular hit distance (DLSS-RR Integration Guide
        // 3.4.9), handed over as pointers.
        memcpy(worldToView, frame.worldToView, sizeof(worldToView));
        memcpy(viewToClip, frame.viewToClip, sizeof(viewToClip));
        map->Set("WorldToViewMatrix", static_cast<void*>(worldToView));
        map->Set("ViewToClipMatrix", static_cast<void*>(viewToClip));

        const uint32_t result = g_ngx.evaluateFeature(list, feature, map, nullptr);
        if (!NgxFailed(result))
            resetFeature = false;
        else
        {
            problem = "Ray Reconstruction's evaluation through NGX failed (" + NgxHex(result) + ")";
            if (!loggedEvaluateFailure)
                LogLine("DLSS Ray Reconstruction through NGX: evaluation failed (" + NgxHex(result) + ")");
            loggedEvaluateFailure = true;
            return false;
        }
        if (++evaluated == 1 || (evaluated & 4095) == 0)
            LogLine("DLSS Ray Reconstruction through NGX: "
                    + (evaluated == 1 ? std::string("the first frame") : std::to_string(evaluated) + " frames")
                    + " evaluated");
        return true;
    }

    bool DlssRayReconstruction::EnsureInitialisedLocked(ID3D12Device* target, std::string& problem)
    {
        if (device.Get() == target && parameters != nullptr)
            return true;
        if (initFailed == target)
        {
            problem = initFailedWhy;
            return false;
        }
        ShutdownLocked();

        const auto fail = [&](const std::string& why)
        {
            initFailed = target;
            initFailedWhy = why;
            problem = why;
            LogLine("DLSS Ray Reconstruction through NGX: " + why);
            return false;
        };
        const auto failStable = [&](const std::string& why)
        {
            FailStable(why);
            problem = why;
            return false;
        };

        std::string error;
        bool stable = false;
        const HMODULE core = NgxCoreModule(error, stable);
        if (core == nullptr)
            return stable ? failStable(error) : fail(error);
        if (g_ngx.initProjectId == nullptr)
        {
            g_ngx.initProjectId = reinterpret_cast<InitProjectIdFn>(GetProcAddress(core, "NVSDK_NGX_D3D12_Init_ProjectID"));
            g_ngx.getCapabilityParameters = reinterpret_cast<NgxGetParametersFn>(
                GetProcAddress(core, "NVSDK_NGX_D3D12_GetCapabilityParameters"));
            g_ngx.getParameters = reinterpret_cast<NgxGetParametersFn>(GetProcAddress(core, "NVSDK_NGX_D3D12_GetParameters"));
            g_ngx.destroyParameters = reinterpret_cast<NgxDestroyParametersFn>(
                GetProcAddress(core, "NVSDK_NGX_D3D12_DestroyParameters"));
            g_ngx.createFeature = reinterpret_cast<CreateFeatureFn>(GetProcAddress(core, "NVSDK_NGX_D3D12_CreateFeature"));
            g_ngx.evaluateFeature = reinterpret_cast<EvaluateFeatureFn>(
                GetProcAddress(core, "NVSDK_NGX_D3D12_EvaluateFeature"));
            g_ngx.releaseFeature = reinterpret_cast<NgxReleaseFeatureFn>(
                GetProcAddress(core, "NVSDK_NGX_D3D12_ReleaseFeature"));
            g_ngx.shutdown = reinterpret_cast<ShutdownFn>(GetProcAddress(core, "NVSDK_NGX_D3D12_Shutdown"));
        }
        if (g_ngx.initProjectId == nullptr || g_ngx.getCapabilityParameters == nullptr
            || g_ngx.destroyParameters == nullptr || g_ngx.createFeature == nullptr || g_ngx.evaluateFeature == nullptr
            || g_ngx.releaseFeature == nullptr || g_ngx.shutdown == nullptr)
            return failStable("the driver's NGX core lacks a D3D12 function Ray Reconstruction needs -- driver too old?");

        search.Set(SearchFolders());
        commonInfo = search.Info();
        ResetNgxLog();
        dataPath = ExecutableDirectory();
        uint32_t result = g_ngx.initProjectId(kNvidiaProjectId, kNgxEngineCustom, kEngineVersion, dataPath.c_str(), target,
                                              kNgxVersionApi, &commonInfo);
        if (result == kNgxFeatureNotSupported)
            return failStable("NGX reports this GPU as unsupported -- Ray Reconstruction needs an NVIDIA RTX GPU");
        if (NgxFailed(result))
            return fail("NGX could not be initialised on the proxy's Direct3D 12 device (" + NgxHex(result) + ")");

        // The capability map, which the queries below read and the feature takes; an
        // older driver has only the one NGX manages itself (DLSS Programming Guide
        // 5.2.6).
        NgxParameters* map = nullptr;
        result = g_ngx.getCapabilityParameters(&map);
        parametersOwned = !NgxFailed(result) && map != nullptr;
        if (!parametersOwned && g_ngx.getParameters != nullptr)
            result = g_ngx.getParameters(&map);
        if (NgxFailed(result) || map == nullptr)
        {
            g_ngx.shutdown();
            return fail("NGX gave no parameter map (" + NgxHex(result) + ")");
        }
        parameters = map;
        device = target;

        // "Querying for DLSS-RR feature support ... is the same as those DLSS
        // sections" (DLSS-RR Integration Guide 2.1), under SuperSamplingDenoising's
        // keys (nvsdk_ngx_defs_dlssd.h).
        int needsUpdatedDriver = 0;
        if (!NgxFailed(map->Get("SuperSamplingDenoising.NeedsUpdatedDriver", &needsUpdatedDriver))
            && needsUpdatedDriver != 0)
        {
            unsigned int major = 0, minor = 0;
            map->Get("SuperSamplingDenoising.MinDriverVersionMajor", &major);
            map->Get("SuperSamplingDenoising.MinDriverVersionMinor", &minor);
            ShutdownLocked();
            return failStable("Ray Reconstruction needs a newer NVIDIA driver, at least " + std::to_string(major) + "."
                              + std::to_string(minor));
        }
        int available = 0;
        if (NgxFailed(map->Get("SuperSamplingDenoising.Available", &available)) || available == 0)
        {
            const std::string where = search.Where();
            ShutdownLocked();
            return failStable("Ray Reconstruction is not available: no usable nvngx_dlssd.dll in " + where
                              + ", or no RTX GPU -- NVIDIA DLSS files in ReDefinition's settings window downloads it");
        }
        int initResult = 0;
        if (!NgxFailed(map->Get("SuperSamplingDenoising.FeatureInitResult", &initResult))
            && NgxFailed(static_cast<uint32_t>(initResult)))
        {
            ShutdownLocked();
            return failStable("NGX denies Ray Reconstruction for this application ("
                              + NgxHex(static_cast<uint32_t>(initResult)) + ")");
        }

        initFailed = nullptr;
        LogLine("DLSS Ray Reconstruction: NGX initialised on the proxy's Direct3D 12 device");
        return true;
    }

    bool DlssRayReconstruction::EnsureFeatureLocked(ID3D12GraphicsCommandList* list, const NgxRrFrame& frame,
                                                    const std::function<void()>& idle, std::string& problem)
    {
        if (feature != nullptr
            && Same(frame, created.renderWidth, created.renderHeight, created.outputWidth, created.outputHeight,
                    created.quality, created.preset))
            return true;
        if (hasFailed
            && Same(frame, failed.renderWidth, failed.renderHeight, failed.outputWidth, failed.outputHeight,
                    failed.quality, failed.preset))
        {
            problem = failedWhy;
            return false;
        }
        // The lists in flight still evaluate the old one: sl.dlss_d delays its release
        // by three frames "to avoid race conditions"; here they finish first.
        if (feature != nullptr)
        {
            idle();
            ReleaseFeatureLocked();
        }

        const auto fail = [&](const std::string& why)
        {
            failed = Settings{ frame.renderWidth, frame.renderHeight, frame.outputWidth, frame.outputHeight, frame.quality,
                               frame.preset };
            hasFailed = true;
            failedWhy = why;
            problem = why;
            LogLine("DLSS Ray Reconstruction through NGX: " + why);
            return false;
        };
        if (frame.quality < 0 || frame.quality > 5)
            return fail("no NGX quality mode " + std::to_string(frame.quality));

        // The render size the feature is made for, from NGX_DLSSD_GET_OPTIMAL_SETTINGS
        // (nvsdk_ngx_helpers_dlssd.h) as sl.dlss_d asks it: output size and mode in.
        // "Dynamic Resolution Support is not currently supported by DLSS Ray
        // Reconstruction" (DLSS-RR Integration Guide 3.3); the frames come at the size
        // the rig renders, which the log line names beside it.
        NgxParameters* map = parameters;
        void* callback = nullptr;
        map->Get("DLSSDOptimalSettingsCallback", &callback);
        if (callback == nullptr)
            return fail("this nvngx_dlssd.dll gives no optimal settings");
        map->Set("Width", static_cast<unsigned int>(frame.outputWidth));
        map->Set("Height", static_cast<unsigned int>(frame.outputHeight));
        map->Set("PerfQualityValue", static_cast<int>(frame.quality));
        map->Set("RTXValue", 0);
        uint32_t result = static_cast<NgxOptimalSettingsFn>(callback)(map);
        if (NgxFailed(result))
            return fail("Ray Reconstruction's optimal settings query failed (" + NgxHex(result) + ")");
        unsigned int optimalWidth = 0, optimalHeight = 0;
        map->Get("OutWidth", &optimalWidth);
        map->Get("OutHeight", &optimalHeight);
        if (optimalWidth == 0 || optimalHeight == 0)
            return fail("Ray Reconstruction offers quality mode " + std::to_string(frame.quality) + " not at "
                        + NgxSize(frame.outputWidth, frame.outputHeight));

        for (const char* mode : { "RayReconstruction.Hint.Render.Preset.DLAA", "RayReconstruction.Hint.Render.Preset.Quality",
                                  "RayReconstruction.Hint.Render.Preset.Balanced",
                                  "RayReconstruction.Hint.Render.Preset.Performance",
                                  "RayReconstruction.Hint.Render.Preset.UltraPerformance",
                                  "RayReconstruction.Hint.Render.Preset.UltraQuality" })
            map->Set(mode, static_cast<unsigned int>(frame.preset));

        // Motion vectors at the render size and without jitter, HDR colour, depth
        // reversed: the flags sl.dlss_d sets for the constants Streamline is given.
        const int flags = kNgxFlagMotionVectorsLowRes | kNgxFlagIsHdr | kNgxFlagDepthInverted;
        map->Set("CreationNodeMask", 1u);
        map->Set("VisibilityNodeMask", 1u);
        map->Set("Width", optimalWidth);
        map->Set("Height", optimalHeight);
        map->Set("OutWidth", static_cast<unsigned int>(frame.outputWidth));
        map->Set("OutHeight", static_cast<unsigned int>(frame.outputHeight));
        map->Set("PerfQualityValue", static_cast<int>(frame.quality));
        map->Set("DLSS.Feature.Create.Flags", flags);
        map->Set("DLSS.Enable.Output.Subrects", 0);
        // "force memory release on each ReleaseFeature() call" (DLSS Programming
        // Guide 3.14), as sl.dlss_d asks.
        map->Set("FreeMemOnReleaseFeature", 1);
        map->Set("DLSS.Denoise.Mode", kNgxDenoiseModeDlUnified);
        map->Set("DLSS.Roughness.Mode", kNgxRoughnessModePacked);
        map->Set("DLSS.Use.HW.Depth", kNgxDepthTypeHardware);

        ResetNgxLog();
        void* handle = nullptr;
        result = g_ngx.createFeature(list, kNgxFeatureRayReconstruction, map, &handle);
        if (NgxFailed(result) || handle == nullptr)
            return fail("the Ray Reconstruction feature could not be created (" + NgxHex(result) + ")");

        feature = handle;
        created = Settings{ frame.renderWidth, frame.renderHeight, frame.outputWidth, frame.outputHeight, frame.quality,
                            frame.preset };
        hasFailed = false;
        loggedEvaluateFailure = false;
        evaluated = 0;
        resetFeature = true;
        LogLine("DLSS Ray Reconstruction: " + LoadedNgxModule(L"nvngx_dlssd.dll") + " through NGX, created for "
                + NgxSize(optimalWidth, optimalHeight) + " to " + NgxSize(frame.outputWidth, frame.outputHeight)
                + ", rendered at " + NgxSize(frame.renderWidth, frame.renderHeight) + ", quality "
                + std::to_string(frame.quality) + ", preset " + std::to_string(frame.preset) + ", flags "
                + NgxHex(static_cast<uint32_t>(flags)));
        return true;
    }

    bool DlssRayReconstruction::ReleaseFeature()
    {
        std::lock_guard<std::mutex> ngx(NgxMutex());
        hasFailed = false;
        initFailed = nullptr;
        return ReleaseFeatureLocked();
    }

    bool DlssRayReconstruction::ReleaseFeatureLocked()
    {
        if (feature == nullptr)
            return false;
        g_ngx.releaseFeature(feature);
        feature = nullptr;
        created = Settings{};
        LogLine("DLSS Ray Reconstruction through NGX: feature released");
        return true;
    }

    void DlssRayReconstruction::Shutdown(ID3D12Device* going)
    {
        std::lock_guard<std::mutex> ngx(NgxMutex());
        if (initFailed == going)
            initFailed = nullptr;
        {
            // What stood on this device is asked again on the next.
            std::lock_guard<std::mutex> lock(stateMutex);
            stableWhy.clear();
        }
        if (going == nullptr || device.Get() != going)
            return;
        ShutdownLocked();
        LogLine("DLSS Ray Reconstruction: NGX shut down on the proxy's Direct3D 12 device");
    }

    void DlssRayReconstruction::ShutdownLocked()
    {
        ReleaseFeatureLocked();
        if (parameters != nullptr && parametersOwned)
            g_ngx.destroyParameters(parameters);
        parameters = nullptr;
        parametersOwned = false;
        if (device != nullptr)
            g_ngx.shutdown();
        device.Reset();
    }
}
