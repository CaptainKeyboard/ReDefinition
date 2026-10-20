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

    constexpr uint32_t kNgxFeatureNotSupported = 0xBAD00001u;
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
        virtual void Set(const char* name, unsigned long long value) = 0;
        virtual void Set(const char* name, float value) = 0;
        virtual void Set(const char* name, double value) = 0;
        virtual void Set(const char* name, unsigned int value) = 0;
        virtual void Set(const char* name, int value) = 0;
        virtual void Set(const char* name, ID3D11Resource* value) = 0;
        virtual void Set(const char* name, ID3D12Resource* value) = 0;
        virtual void Set(const char* name, void* value) = 0;
        virtual uint32_t Get(const char* name, unsigned long long* value) const = 0;
        virtual uint32_t Get(const char* name, float* value) const = 0;
        virtual uint32_t Get(const char* name, double* value) const = 0;
        virtual uint32_t Get(const char* name, unsigned int* value) const = 0;
        virtual uint32_t Get(const char* name, int* value) const = 0;
        virtual uint32_t Get(const char* name, ID3D11Resource** value) const = 0;
        virtual uint32_t Get(const char* name, ID3D12Resource** value) const = 0;
        virtual uint32_t Get(const char* name, void** value) const = 0;
        virtual void Reset() = 0;
    };

    struct NgxPathList
    {
        wchar_t** path;
        unsigned int length;
    };

    using NgxLogCallback = void (*)(const char* message, int level, int component);

    struct NgxLoggingInfo
    {
        NgxLogCallback callback;
        int minimumLevel;
        bool disableOtherSinks;
    };

    struct NgxCommonInfo
    {
        NgxPathList pathList;
        void* internalData;
        NgxLoggingInfo logging;
    };

    using NgxGetParametersFn = uint32_t (*)(NgxParameters** parameters);
    using NgxDestroyParametersFn = uint32_t (*)(NgxParameters* parameters);
    using NgxReleaseFeatureFn = uint32_t (*)(void* handle);
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
    void ResetNgxLog();

    std::string NgxHex(uint32_t value);
    std::string NgxSize(unsigned int width, unsigned int height);

    // The folders NGX searches for a feature's DLL, and the common info that hands
    // them over with the logging. Kept alive for as long as NGX runs on the device.
    struct NgxSearch
    {
        std::vector<std::wstring> paths;
        std::vector<wchar_t*> pointers;

        void Set(const std::vector<std::wstring>& folders);
        NgxCommonInfo Info();
        // "a or b", for messages.
        std::string Where() const;
    };

    // The version and path of a feature DLL the core loaded, by its file name, for
    // the log.
    std::string LoadedNgxModule(const wchar_t* name);
}
