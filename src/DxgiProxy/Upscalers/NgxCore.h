#pragma once

#include <windows.h>

#include <atomic>
#include <cstdint>
#include <mutex>
#include <string>
#include <vector>

// Only as pointer types in NGX's parameter map.
struct ID3D11Resource;
struct ID3D12Resource;

namespace redefinition
{
    // NVIDIA's NGX as the driver's _nvngx.dll exports it, for DLSS Super Resolution
    // on Unity's Direct3D 11 device (Dlss.cpp) and DLSS Ray Reconstruction on the
    // proxy's Direct3D 12 device (DlssRayReconstruction.cpp). Declared here from
    // NVIDIA's DLSS Programming Guide (310.6.0, 31 March 2026), the DLSS SDK's
    // headers (nvsdk_ngx.h, nvsdk_ngx_defs.h) and the exports of the installed
    // driver.

    // NVSDK_NGX_Result_FAIL_FeatureNotSupported: the feature does not run on this GPU or driver.
    constexpr uint32_t kNgxFeatureNotSupported = 0xBAD00001u;
    // Whether an NGX result is a failure: NVSDK_NGX_FAILED, the 0xBAD00000 range.
    inline bool NgxFailed(uint32_t result) { return (result & 0xFFF00000u) == 0xBAD00000u; }

    // NVSDK_NGX_VERSION_API_MACRO, NVSDK_NGX_ENGINE_TYPE_CUSTOM, NVSDK_NGX_LOGGING_LEVEL_ON.
    constexpr int kNgxVersionApi = 0x15;
    constexpr int kNgxEngineCustom = 0;
    constexpr int kNgxLoggingOn = 1;

    // NVSDK_NGX_DLSS_Feature_Flags.
    constexpr int kNgxFlagIsHdr = 1 << 0;
    constexpr int kNgxFlagMotionVectorsLowRes = 1 << 1;
    constexpr int kNgxFlagDepthInverted = 1 << 3;
    constexpr int kNgxFlagAutoExposure = 1 << 6;

    // NGX's parameter map. The driver implements it; what has to agree is the
    // order of the virtual functions, which follows NVIDIA's declaration.
    struct NgxParameters
    {
        // A value under name, as an unsigned 64-bit integer.
        virtual void Set(const char* name, unsigned long long value) = 0;
        // A value under name, as a float.
        virtual void Set(const char* name, float value) = 0;
        // A value under name, as a double.
        virtual void Set(const char* name, double value) = 0;
        // A value under name, as an unsigned int.
        virtual void Set(const char* name, unsigned int value) = 0;
        // A value under name, as an int.
        virtual void Set(const char* name, int value) = 0;
        // A value under name, as a Direct3D 11 resource.
        virtual void Set(const char* name, ID3D11Resource* value) = 0;
        // A value under name, as a Direct3D 12 resource.
        virtual void Set(const char* name, ID3D12Resource* value) = 0;
        // A value under name, as an untyped pointer.
        virtual void Set(const char* name, void* value) = 0;
        // The value under name as an unsigned 64-bit integer; an NGX result, failed where it has none.
        virtual uint32_t Get(const char* name, unsigned long long* value) const = 0;
        // The value under name as a float; an NGX result.
        virtual uint32_t Get(const char* name, float* value) const = 0;
        // The value under name as a double; an NGX result.
        virtual uint32_t Get(const char* name, double* value) const = 0;
        // The value under name as an unsigned int; an NGX result.
        virtual uint32_t Get(const char* name, unsigned int* value) const = 0;
        // The value under name as an int; an NGX result.
        virtual uint32_t Get(const char* name, int* value) const = 0;
        // The value under name as a Direct3D 11 resource; an NGX result.
        virtual uint32_t Get(const char* name, ID3D11Resource** value) const = 0;
        // The value under name as a Direct3D 12 resource; an NGX result.
        virtual uint32_t Get(const char* name, ID3D12Resource** value) const = 0;
        // The value under name as an untyped pointer; an NGX result.
        virtual uint32_t Get(const char* name, void** value) const = 0;
        // Every value in the map cleared.
        virtual void Reset() = 0;
    };

    // NVSDK_NGX_PathListInfo: the folders NGX searches for a feature's DLL.
    struct NgxPathList
    {
        // The folders, as wide strings, and how many there are.
        wchar_t** path;
        unsigned int length;
    };

    // NGX's logging callback: the message, its level and the component that sent it.
    using NgxLogCallback = void (*)(const char* message, int level, int component);

    // NVSDK_NGX_FeatureCommonInfo's logging: where NGX's log lines go.
    struct NgxLoggingInfo
    {
        // The callback, the least level it is called for, and whether NGX's own sinks (nvngx.log, the console)
        // are switched off.
        NgxLogCallback callback;
        int minimumLevel;
        bool disableOtherSinks;
    };

    // NVSDK_NGX_FeatureCommonInfo, handed to NGX's init.
    struct NgxCommonInfo
    {
        // The search folders, a pointer NGX reserves (null), and the logging.
        NgxPathList pathList;
        void* internalData;
        NgxLoggingInfo logging;
    };

    // NVSDK_NGX_*_GetCapabilityParameters: a parameter map the driver allocates.
    using NgxGetParametersFn = uint32_t (*)(NgxParameters** parameters);
    // NVSDK_NGX_*_DestroyParameters: a map from GetCapabilityParameters let go.
    using NgxDestroyParametersFn = uint32_t (*)(NgxParameters* parameters);
    // NVSDK_NGX_*_ReleaseFeature: a feature handle let go.
    using NgxReleaseFeatureFn = uint32_t (*)(void* handle);
    // The function a map holds under DLSSOptimalSettingsCallback: the render sizes for the output size and
    // quality.
    using NgxOptimalSettingsFn = uint32_t (*)(NgxParameters* parameters);

    // "The NGX API is not thread safe. The client application must ensure that
    // thread safety is enforced as needed" (DLSS Programming Guide 5.2.4). One lock
    // for every call into NGX, whichever device: Super Resolution's on Unity's
    // render thread, Ray Reconstruction's under the raytracing's lock.
    std::mutex& NgxMutex();

    // The driver's NGX core, loaded once for the process: the folder in NGXPath
    // (HKLM\SYSTEM\CurrentControlSet\Services\nvlddmkm\NGXCore), which holds
    // _nvngx.dll. Null with the reason in error; stable is set where that stands
    // until the driver changes (no NGX registered at all).
    HMODULE NgxCoreModule(std::string& error, bool& stable);

    // Whether the driver registers an NGX core at all, without loading it; asked of
    // the registry once.
    bool NgxCoreRegistered();

    // NGX's log lines into the proxy's log: everything NGX says about a start and
    // its features, then quiet until the next ResetNgxLog.
    void NgxLog(const char* message, int level, int component);
        // NgxLog's line count back to 0: the next start is logged in full.
    void ResetNgxLog();

        // An NGX result as eight hexadecimal digits, for messages.
    std::string NgxHex(uint32_t value);
        // A size as "1920x1080", for messages.
    std::string NgxSize(unsigned int width, unsigned int height);

    // The folders NGX searches for a feature's DLL, and the common info that hands
    // them over with the logging. Kept alive for as long as NGX runs on the device.
    struct NgxSearch
    {
        // The folders, and the pointers to their characters NgxPathList hands NGX.
        std::vector<std::wstring> paths;
        std::vector<wchar_t*> pointers;

        // The folders taken, without empty ones and those already there (case ignored), and pointers to them.
        void Set(const std::vector<std::wstring>& folders);
        // The common info NGX's init takes: these folders, and its log into the proxy's alone.
        NgxCommonInfo Info();
        // "a or b", for messages.
        std::string Where() const;
    };

    // The version and path of a feature DLL the core loaded, by its file name, for
    // the log.
    std::string LoadedNgxModule(const wchar_t* name);
}
