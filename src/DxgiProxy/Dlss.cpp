#include "Dlss.h"

#include "Config.h"
#include "FileUtil.h"
#include "Log.h"
#include "ProjectIdentity.h"

#include <atomic>
#include <cstdio>
#include <cstring>
#include <cwchar>

using Microsoft::WRL::ComPtr;

// Only as a pointer type in NGX's parameter map.
struct ID3D12Resource;

namespace redefinition
{
    namespace
    {
        // ---- NVIDIA NGX as the driver's _nvngx.dll exports it ----
        //
        // Declared here from NVIDIA's DLSS Programming Guide (310.6.0, 31 March
        // 2026) and the exports of the installed driver.

        constexpr uint32_t kNgxFeatureNotSupported = 0xBAD00001u;
        bool NgxFailed(uint32_t result) { return (result & 0xFFF00000u) == 0xBAD00000u; }

        constexpr int kNgxFeatureSuperSampling = 1;
        constexpr int kNgxVersionApi = 0x15;
        constexpr int kNgxEngineCustom = 0;
        constexpr int kNgxLoggingOn = 1;
        constexpr int kNgxQualityDlaa = 5;

        // NVSDK_NGX_DLSS_Feature_Flags.
        constexpr int kFlagIsHdr = 1 << 0;
        constexpr int kFlagMotionVectorsLowRes = 1 << 1;
        constexpr int kFlagDepthInverted = 1 << 3;
        constexpr int kFlagAutoExposure = 1 << 6;

        // NGX's parameter map. The driver implements it; what has to agree is
        // the order of the virtual functions, which follows NVIDIA's declaration.
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

        // The driver's exports. Init_ProjectID takes the SDK version before the
        // common info, unlike the SDK's own Init_with_ProjectID -- the order
        // OptiScaler's NGX proxy calls the driver with.
        using InitProjectIdFn = uint32_t (*)(const char* projectId, int engineType, const char* engineVersion,
                                             const wchar_t* dataPath, ID3D11Device* device, int sdkVersion,
                                             const NgxCommonInfo* info);
        using GetParametersFn = uint32_t (*)(NgxParameters** parameters);
        using DestroyParametersFn = uint32_t (*)(NgxParameters* parameters);
        using CreateFeatureFn = uint32_t (*)(ID3D11DeviceContext* context, int feature, NgxParameters* parameters,
                                             void** handle);
        using EvaluateFeatureFn = uint32_t (*)(ID3D11DeviceContext* context, const void* handle,
                                               const NgxParameters* parameters, void* progress);
        using ReleaseFeatureFn = uint32_t (*)(void* handle);
        using ShutdownFn = uint32_t (*)(ID3D11Device* device);
        using OptimalSettingsFn = uint32_t (*)(NgxParameters* parameters);

        struct NgxCore
        {
            InitProjectIdFn initProjectId = nullptr;
            GetParametersFn getCapabilityParameters = nullptr;
            GetParametersFn getParameters = nullptr;
            DestroyParametersFn destroyParameters = nullptr;
            CreateFeatureFn createFeature = nullptr;
            EvaluateFeatureFn evaluateFeature = nullptr;
            ReleaseFeatureFn releaseFeature = nullptr;
            ShutdownFn shutdown = nullptr;
        };

        NgxCore g_ngx;

        std::atomic<int> g_ngxLogLines{ 0 };

        void NgxLog(const char* message, int, int)
        {
            // Everything NGX says about initialisation and features, then quiet.
            const int line = ++g_ngxLogLines;
            if (line > 200 || message == nullptr)
                return;
            std::string text(message);
            while (!text.empty() && (text.back() == '\n' || text.back() == '\r'))
                text.pop_back();
            LogLine("NGX: " + text + (line == 200 ? "   (further NGX lines are not logged)" : ""));
        }

        // The GPU a device renders on, for messages.
        std::string AdapterName(ID3D11Device* device)
        {
            ComPtr<IDXGIDevice> dxgiDevice;
            ComPtr<IDXGIAdapter> adapter;
            DXGI_ADAPTER_DESC desc = {};
            if (FAILED(device->QueryInterface(IID_PPV_ARGS(&dxgiDevice))) || FAILED(dxgiDevice->GetAdapter(&adapter))
                || FAILED(adapter->GetDesc(&desc)))
                return "this GPU";
            return Narrow(desc.Description);
        }

        std::string Hex(uint32_t value)
        {
            char text[16] = {};
            FormatTo(text, "0x%08X", value);
            return text;
        }

        // Where the driver registers its NGX core: the folder in NGXPath
        // (HKLM\SYSTEM\CurrentControlSet\Services\nvlddmkm\NGXCore), which holds
        // _nvngx.dll.
        std::wstring NgxCorePath()
        {
            HKEY key = nullptr;
            if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, L"SYSTEM\\CurrentControlSet\\Services\\nvlddmkm\\NGXCore", 0,
                              KEY_READ, &key) != ERROR_SUCCESS)
                return std::wstring();

            wchar_t buffer[MAX_PATH] = {};
            DWORD bytes = sizeof(buffer) - sizeof(wchar_t);
            DWORD type = 0;
            const LSTATUS status = RegQueryValueExW(key, L"NGXPath", nullptr, &type,
                                                    reinterpret_cast<BYTE*>(buffer), &bytes);
            RegCloseKey(key);
            if (status != ERROR_SUCCESS || (type != REG_SZ && type != REG_EXPAND_SZ) || buffer[0] == L'\0')
                return std::wstring();

            std::wstring path(buffer);
            if (path.back() != L'\\')
                path += L'\\';
            return path + L"_nvngx.dll";
        }

        // The nvngx_dlss.dll the core loaded, with its version.
        std::string LoadedDlss()
        {
            const HMODULE module = GetModuleHandleW(L"nvngx_dlss.dll");
            if (module == nullptr)
                return "nvngx_dlss.dll";
            wchar_t buffer[MAX_PATH] = {};
            const DWORD length = GetModuleFileNameW(module, buffer, MAX_PATH);
            const std::wstring path(buffer, length);
            return "nvngx_dlss.dll " + FileVersion(path) + " (" + Narrow(path) + ")";
        }

        std::string Size(unsigned int width, unsigned int height)
        {
            return std::to_string(width) + "x" + std::to_string(height);
        }

        // What NGX's search for nvngx_dlss.dll comes down to: the folders it is
        // given, and the file in each.
        std::wstring SearchSignature()
        {
            std::wstring signature;
            for (const std::wstring& folder : { DlssDirectory(), ExecutableDirectory() })
            {
                if (!folder.empty())
                    signature += folder + L"|" + FileStamp(folder + L"\\nvngx_dlss.dll") + L";";
            }
            return signature;
        }

        bool SameFeature(const DlssPacket& a, const DlssPacket& b)
        {
            const uint32_t creationFlags = kDlssHdr | kDlssDepthInverted | kDlssAutoExposure;
            return a.renderWidth == b.renderWidth && a.renderHeight == b.renderHeight
                && a.outputWidth == b.outputWidth && a.outputHeight == b.outputHeight
                && a.quality == b.quality && a.preset == b.preset
                && (a.flags & creationFlags) == (b.flags & creationFlags);
        }

        NgxParameters* Map(void* parameters)
        {
            return static_cast<NgxParameters*>(parameters);
        }
    }

    // Never destroyed: at process exit a static destructor would release Unity's
    // D3D11 device after d3d11.dll may already be gone.
    Dlss& Dlss::Get()
    {
        static Dlss* instance = new Dlss();
        return *instance;
    }

    void Dlss::SetStatus(int newState, const std::string& text)
    {
        renderState = newState;
        bool changed = false;
        {
            std::lock_guard<std::mutex> lock(mutex);
            changed = state != newState || statusText != text;
            state = newState;
            statusText = text;
            stableSearch = newState == -2 ? failedSearch : std::wstring();
        }
        if (changed && newState < 0)
            LogLine("DLSS: " + text);
    }

    int Dlss::Status(char* buffer, int size) const
    {
        int reported = 0;
        std::wstring stood;
        {
            std::lock_guard<std::mutex> lock(mutex);
            // Cut to the buffer: sprintf_s would end the process on a longer text.
            if (buffer != nullptr && size > 0)
                strncpy_s(buffer, static_cast<size_t>(size), statusText.c_str(), _TRUNCATE);
            reported = state;
            if (reported == -2)
                stood = stableSearch;
        }
        // A failure that stands stands only while NGX's search is as it was: once
        // the player has copied nvngx_dlss.dll or set dlssDirectory, the next rig
        // tries again (EnsureInitialised). The files are asked outside the lock
        // the render thread takes.
        if (reported == -2 && SearchSignature() != stood)
            reported = -1;
        return reported;
    }

    bool Dlss::RenderSizes(uint32_t outputWidth, uint32_t outputHeight, int32_t quality, uint32_t optimal[2],
                           uint32_t minimum[2], uint32_t maximum[2]) const
    {
        std::lock_guard<std::mutex> lock(mutex);
        for (const RenderSizeEntry& entry : sizes)
        {
            if (entry.optimal[0] == 0 || entry.outputWidth != outputWidth || entry.outputHeight != outputHeight
                || entry.quality != quality)
                continue;
            for (int i = 0; i < 2; ++i)
            {
                optimal[i] = entry.optimal[i];
                minimum[i] = entry.minimum[i];
                maximum[i] = entry.maximum[i];
            }
            return true;
        }
        return false;
    }

    void Dlss::OnPacket(const void* data)
    {
        const DlssPacket* packet = static_cast<const DlssPacket*>(data);
        if (packet == nullptr || packet->size != sizeof(DlssPacket) || packet->magic != kDlssPacketMagic)
        {
            SetStatus(-1, "DLSS frame packet rejected: the mod and the proxy disagree on its layout");
            return;
        }

        const ID3D11Resource* colour = static_cast<ID3D11Resource*>(packet->colour);
        if (colour == nullptr || packet->output == nullptr || packet->depth == nullptr
            || packet->motionVectors == nullptr)
        {
            SetStatus(-1, "DLSS frame without all four textures");
            return;
        }

        ComPtr<ID3D11Device> textureDevice;
        static_cast<ID3D11Resource*>(packet->colour)->GetDevice(&textureDevice);
        if (!EnsureInitialised(textureDevice.Get()))
            return;

        ComPtr<ID3D11DeviceContext> context;
        textureDevice->GetImmediateContext(&context);
        if (!EnsureFeature(context.Get(), *packet))
            return;

        Evaluate(context.Get(), *packet);
    }

    void Dlss::OnRelease()
    {
        ReleaseFeature();
        hasFailed = false;
        // NGX's start is tried again by a new rig, except after a failure that
        // stands -- no RTX GPU, a driver too old, no usable nvngx_dlss.dll --
        // which keeps its status; EnsureInitialised tries that again once what
        // NGX searches has changed.
        if (failedDevice != nullptr && failedStable)
            return;
        failedDevice = nullptr;
        SetStatus(0, "DLSS feature released, no DLSS frame since");
    }

    bool Dlss::EnsureInitialised(ID3D11Device* textureDevice)
    {
        if (device.Get() == textureDevice && parameters != nullptr)
            return true;
        // Not again on the device it failed on -- except a failure that stood,
        // once what NGX searches has changed: the player copied nvngx_dlss.dll or
        // set dlssDirectory, and no release has to come first. The files are
        // asked at most once a second, not with every packet.
        const bool retrying = failedDevice == textureDevice;
        if (retrying)
        {
            if (!failedStable)
                return false;
            const ULONGLONG now = GetTickCount64();
            if (now < nextSearchCheck)
                return false;
            nextSearchCheck = now + 1000;
        }
        const std::wstring search = SearchSignature();
        if (retrying && search == failedSearch)
            return false;

        Shutdown();
        failedDevice = textureDevice;
        failedSearch = search;
        failedStable = false;

        if (core == nullptr)
        {
            const std::wstring corePath = NgxCorePath();
            if (corePath.empty())
            {
                SetStatus(-2, "no NVIDIA NGX in the driver: DLSS needs an NVIDIA RTX GPU and its driver");
                failedStable = true;
                return false;
            }
            core = LoadLibraryExW(corePath.c_str(), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
            if (core == nullptr)
            {
                SetStatus(-1, "the driver's NGX core could not be loaded (" + Narrow(corePath) + ", error "
                                  + std::to_string(GetLastError()) + ")");
                return false;
            }

            g_ngx.initProjectId = reinterpret_cast<InitProjectIdFn>(GetProcAddress(core, "NVSDK_NGX_D3D11_Init_ProjectID"));
            g_ngx.getCapabilityParameters = reinterpret_cast<GetParametersFn>(
                GetProcAddress(core, "NVSDK_NGX_D3D11_GetCapabilityParameters"));
            g_ngx.getParameters = reinterpret_cast<GetParametersFn>(GetProcAddress(core, "NVSDK_NGX_D3D11_GetParameters"));
            g_ngx.destroyParameters = reinterpret_cast<DestroyParametersFn>(
                GetProcAddress(core, "NVSDK_NGX_D3D11_DestroyParameters"));
            g_ngx.createFeature = reinterpret_cast<CreateFeatureFn>(GetProcAddress(core, "NVSDK_NGX_D3D11_CreateFeature"));
            g_ngx.evaluateFeature = reinterpret_cast<EvaluateFeatureFn>(
                GetProcAddress(core, "NVSDK_NGX_D3D11_EvaluateFeature"));
            g_ngx.releaseFeature = reinterpret_cast<ReleaseFeatureFn>(GetProcAddress(core, "NVSDK_NGX_D3D11_ReleaseFeature"));
            g_ngx.shutdown = reinterpret_cast<ShutdownFn>(GetProcAddress(core, "NVSDK_NGX_D3D11_Shutdown1"));
        }

        // On every start, not only when the core is loaded: a core without them
        // stays loaded.
        if (g_ngx.initProjectId == nullptr || g_ngx.getCapabilityParameters == nullptr
            || g_ngx.destroyParameters == nullptr || g_ngx.createFeature == nullptr
            || g_ngx.evaluateFeature == nullptr || g_ngx.releaseFeature == nullptr || g_ngx.shutdown == nullptr)
        {
            SetStatus(-2, "the driver's NGX core lacks a D3D11 function DLSS needs -- driver too old?");
            failedStable = true;
            return false;
        }

        // NGX looks next to the executable itself; dlssDirectory comes first.
        searchPaths.clear();
        const std::wstring directory = DlssDirectory();
        if (!directory.empty())
            searchPaths.push_back(directory);
        searchPaths.push_back(ExecutableDirectory());
        searchPathPointers.clear();
        for (std::wstring& path : searchPaths)
            searchPathPointers.push_back(&path[0]);

        NgxCommonInfo info = {};
        info.pathList.path = searchPathPointers.data();
        info.pathList.length = static_cast<unsigned int>(searchPathPointers.size());
        info.logging.callback = &NgxLog;
        info.logging.minimumLevel = kNgxLoggingOn;
        // Into the proxy's log only: otherwise NGX also writes nvngx.log next to
        // the game and to the console.
        info.logging.disableOtherSinks = true;

        // NGX's lines for this start, whatever earlier ones used up.
        g_ngxLogLines = 0;
        const std::wstring dataPath = ExecutableDirectory();
        uint32_t result = g_ngx.initProjectId(kNvidiaProjectId, kNgxEngineCustom, kEngineVersion, dataPath.c_str(),
                                              textureDevice, kNgxVersionApi, &info);
        if (result == kNgxFeatureNotSupported)
        {
            SetStatus(-2, "DLSS does not run on " + AdapterName(textureDevice)
                              + ", the GPU KSP renders on: NGX reports it as unsupported -- DLSS needs an NVIDIA RTX GPU");
            failedStable = true;
            return false;
        }
        if (NgxFailed(result))
        {
            SetStatus(-1, "NGX could not be initialised on " + AdapterName(textureDevice) + " (" + Hex(result) + ")");
            return false;
        }

        // The capability map, which the DLSS queries read and the feature takes;
        // an older driver has only the one NGX manages itself (guide 5.2.6).
        NgxParameters* map = nullptr;
        result = g_ngx.getCapabilityParameters(&map);
        parametersOwned = !NgxFailed(result) && map != nullptr;
        if (!parametersOwned && g_ngx.getParameters != nullptr)
            result = g_ngx.getParameters(&map);
        if (NgxFailed(result) || map == nullptr)
        {
            g_ngx.shutdown(textureDevice);
            SetStatus(-1, "NGX gave no parameter map (" + Hex(result) + ")");
            return false;
        }
        parameters = map;
        device = textureDevice;

        int needsUpdatedDriver = 0;
        unsigned int minimumMajor = 0;
        unsigned int minimumMinor = 0;
        if (!NgxFailed(map->Get("SuperSampling.NeedsUpdatedDriver", &needsUpdatedDriver)) && needsUpdatedDriver != 0)
        {
            map->Get("SuperSampling.MinDriverVersionMajor", &minimumMajor);
            map->Get("SuperSampling.MinDriverVersionMinor", &minimumMinor);
            SetStatus(-2, "DLSS needs a newer NVIDIA driver, at least " + std::to_string(minimumMajor) + "."
                              + std::to_string(minimumMinor));
            Shutdown();
            failedStable = true;
            return false;
        }

        int available = 0;
        if (NgxFailed(map->Get("SuperSampling.Available", &available)) || available == 0)
        {
            std::string where;
            for (const std::wstring& path : searchPaths)
                where += (where.empty() ? "" : " or ") + Narrow(path);
            SetStatus(-2, "DLSS is not available: no usable nvngx_dlss.dll in " + where
                              + ", or no RTX GPU -- NVIDIA DLSS files in ReDefinition's settings window downloads it");
            Shutdown();
            failedStable = true;
            return false;
        }

        int initResult = 0;
        if (!NgxFailed(map->Get("SuperSampling.FeatureInitResult", &initResult)) && NgxFailed(static_cast<uint32_t>(initResult)))
        {
            SetStatus(-2, "NGX denies DLSS for this application (" + Hex(static_cast<uint32_t>(initResult)) + ")");
            Shutdown();
            failedStable = true;
            return false;
        }

        failedDevice = nullptr;
        LogLine("DLSS: NGX initialised on Unity's D3D11 device");
        SetStatus(0, "NGX initialised, no DLSS feature yet");
        return true;
    }

    // Event 6: the render sizes of every mode at an output size, before the rig
    // that renders at one of them is built. NGX is started on the device of the
    // texture the query carries, as a frame would start it.
    void Dlss::OnSizeQuery(const void* data)
    {
        const DlssSizeQuery* query = static_cast<const DlssSizeQuery*>(data);
        if (query == nullptr || query->size != sizeof(DlssSizeQuery) || query->magic != kDlssSizeQueryMagic
            || query->texture == nullptr)
            return;

        ComPtr<ID3D11Device> textureDevice;
        static_cast<ID3D11Resource*>(query->texture)->GetDevice(&textureDevice);
        // A failure that need not stand is not kept for the rig's first frame to
        // meet: no release runs between a query and the rig after it.
        if (!EnsureInitialised(textureDevice.Get()))
        {
            if (!failedStable)
                failedDevice = nullptr;
            return;
        }

        for (const int quality : { 0, 1, 2, 3, kNgxQualityDlaa })
        {
            uint32_t optimal[2] = {};
            uint32_t minimum[2] = {};
            uint32_t maximum[2] = {};
            if (RenderSizes(query->outputWidth, query->outputHeight, quality, optimal, minimum, maximum))
                continue;

            RenderSizeEntry entry = {};
            std::string error;
            if (QueryRenderSize(query->outputWidth, query->outputHeight, quality, entry, error))
                StoreRenderSize(entry);
            else
                LogLine("DLSS: no render size for quality " + std::to_string(quality) + " at "
                        + Size(query->outputWidth, query->outputHeight) + ": " + error);
        }
    }

    // The render size DLSS wants for an output size and quality value, and the
    // range it accepts (guide 5.2.8): output size and quality in, optimal render
    // size out; the range in the dynamic keys, the optimal size where a library
    // has none. DLAA renders at the output size.
    bool Dlss::QueryRenderSize(uint32_t outputWidth, uint32_t outputHeight, int32_t quality, RenderSizeEntry& entry,
                               std::string& error)
    {
        NgxParameters* map = Map(parameters);
        unsigned int optimalWidth = outputWidth;
        unsigned int optimalHeight = outputHeight;
        unsigned int minWidth = optimalWidth, minHeight = optimalHeight;
        unsigned int maxWidth = optimalWidth, maxHeight = optimalHeight;
        if (quality != kNgxQualityDlaa)
        {
            void* callback = nullptr;
            map->Get("DLSSOptimalSettingsCallback", &callback);
            if (callback == nullptr)
            {
                error = "this DLSS library gives no optimal settings";
                return false;
            }
            map->Set("Width", outputWidth);
            map->Set("Height", outputHeight);
            map->Set("PerfQualityValue", static_cast<int>(quality));
            map->Set("RTXValue", 0);
            const uint32_t result = static_cast<OptimalSettingsFn>(callback)(map);
            if (NgxFailed(result))
            {
                error = "DLSS optimal settings query failed (" + Hex(result) + ")";
                return false;
            }
            map->Get("OutWidth", &optimalWidth);
            map->Get("OutHeight", &optimalHeight);
            minWidth = maxWidth = optimalWidth;
            minHeight = maxHeight = optimalHeight;
            map->Get("DLSS.Get.Dynamic.Min.Render.Width", &minWidth);
            map->Get("DLSS.Get.Dynamic.Min.Render.Height", &minHeight);
            map->Get("DLSS.Get.Dynamic.Max.Render.Width", &maxWidth);
            map->Get("DLSS.Get.Dynamic.Max.Render.Height", &maxHeight);
        }

        if (optimalWidth == 0 || optimalHeight == 0)
        {
            error = "DLSS offers this quality mode not at " + Size(outputWidth, outputHeight);
            return false;
        }
        if (minWidth == 0 || minHeight == 0)
        {
            minWidth = optimalWidth;
            minHeight = optimalHeight;
        }
        if (maxWidth == 0 || maxHeight == 0)
        {
            maxWidth = optimalWidth;
            maxHeight = optimalHeight;
        }

        entry = RenderSizeEntry{ outputWidth, outputHeight, quality, { optimalWidth, optimalHeight },
                                 { minWidth, minHeight }, { maxWidth, maxHeight } };
        return true;
    }

    // Replaces the entry for the same output size and quality, or the oldest.
    void Dlss::StoreRenderSize(const RenderSizeEntry& entry)
    {
        std::lock_guard<std::mutex> lock(mutex);
        size_t at = nextSize;
        bool known = false;
        for (size_t i = 0; i < kRenderSizeEntries && !known; ++i)
        {
            if (sizes[i].optimal[0] != 0 && sizes[i].outputWidth == entry.outputWidth
                && sizes[i].outputHeight == entry.outputHeight && sizes[i].quality == entry.quality)
            {
                at = i;
                known = true;
            }
        }
        if (!known)
            nextSize = (nextSize + 1) % kRenderSizeEntries;
        sizes[at] = entry;
    }

    bool Dlss::EnsureFeature(ID3D11DeviceContext* context, const DlssPacket& packet)
    {
        if (feature != nullptr && SameFeature(created, packet))
            return true;
        if (hasFailed && SameFeature(failed, packet))
            return false;

        ReleaseFeature();
        NgxParameters* map = Map(parameters);

        RenderSizeEntry wanted = {};
        std::string error;
        if (!QueryRenderSize(packet.outputWidth, packet.outputHeight, packet.quality, wanted, error))
        {
            SetStatus(-1, error);
            failed = packet;
            hasFailed = true;
            return false;
        }
        StoreRenderSize(wanted);
        const unsigned int optimalWidth = wanted.optimal[0];
        const unsigned int optimalHeight = wanted.optimal[1];
        const unsigned int minWidth = wanted.minimum[0];
        const unsigned int minHeight = wanted.minimum[1];
        const unsigned int maxWidth = wanted.maximum[0];
        const unsigned int maxHeight = wanted.maximum[1];

        // Created at the render size DLSS asked for -- the create parameters'
        // InWidth and InHeight, "Obtained via NGX_DLSS_GET_OPTIMAL_SETTINGS" --
        // and evaluated at the size the rig renders, which has to lie in the range
        // the same query returned: otherwise "the call to DLSS Evaluate fails with
        // an NVSDK_NGX_Result_FAIL_InvalidParameter error code" (guide 3.2.2). The
        // rig reads the optimal size back and renders at it (KspDlssRenderSizes2);
        // with presets L and M, a frame larger than at creation "triggers an
        // expensive internal buffer reallocation" (3.2.2.1).
        if (packet.renderWidth < minWidth || packet.renderWidth > maxWidth || packet.renderHeight < minHeight
            || packet.renderHeight > maxHeight)
        {
            SetStatus(-1, "DLSS takes this mode at " + Size(minWidth, minHeight) + " to " + Size(maxWidth, maxHeight)
                              + ", not at " + Size(packet.renderWidth, packet.renderHeight) + "; it asks for "
                              + Size(optimalWidth, optimalHeight));
            failed = packet;
            hasFailed = true;
            return false;
        }

        // The preset for every mode: the feature is created for one of them.
        for (const char* mode : { "DLSS.Hint.Render.Preset.DLAA", "DLSS.Hint.Render.Preset.Quality",
                                  "DLSS.Hint.Render.Preset.Balanced", "DLSS.Hint.Render.Preset.Performance",
                                  "DLSS.Hint.Render.Preset.UltraPerformance", "DLSS.Hint.Render.Preset.UltraQuality" })
            map->Set(mode, static_cast<unsigned int>(packet.preset));

        // Motion vectors at render resolution ("MVLowRes") and without jitter:
        // the rig's are Unity's own, rendered unjittered.
        int flags = kFlagMotionVectorsLowRes;
        if ((packet.flags & kDlssHdr) != 0)
            flags |= kFlagIsHdr;
        if ((packet.flags & kDlssDepthInverted) != 0)
            flags |= kFlagDepthInverted;
        if ((packet.flags & kDlssAutoExposure) != 0)
            flags |= kFlagAutoExposure;

        map->Set("CreationNodeMask", 1u);
        map->Set("VisibilityNodeMask", 1u);
        map->Set("Width", optimalWidth);
        map->Set("Height", optimalHeight);
        map->Set("OutWidth", static_cast<unsigned int>(packet.outputWidth));
        map->Set("OutHeight", static_cast<unsigned int>(packet.outputHeight));
        map->Set("PerfQualityValue", static_cast<int>(packet.quality));
        map->Set("DLSS.Feature.Create.Flags", flags);
        map->Set("DLSS.Enable.Output.Subrects", 0);

        g_ngxLogLines = 0;
        void* handle = nullptr;
        const uint32_t result = g_ngx.createFeature(context, kNgxFeatureSuperSampling, map, &handle);
        if (NgxFailed(result) || handle == nullptr)
        {
            SetStatus(-1, "the DLSS feature could not be created (" + Hex(result) + ")");
            failed = packet;
            hasFailed = true;
            return false;
        }

        feature = handle;
        created = packet;
        hasFailed = false;
        loggedEvaluateFailure = false;
        evaluated = 0;
        // The status names the new feature with its first evaluated frame.
        renderState = 0;
        LogLine("DLSS: " + LoadedDlss() + ", created for " + Size(optimalWidth, optimalHeight) + " (range "
                + Size(minWidth, minHeight) + " to " + Size(maxWidth, maxHeight) + ") to "
                + Size(packet.outputWidth, packet.outputHeight) + ", rendered at "
                + Size(packet.renderWidth, packet.renderHeight) + ", quality " + std::to_string(packet.quality)
                + ", preset " + std::to_string(packet.preset) + ", flags " + Hex(static_cast<uint32_t>(flags)));
        return true;
    }

    bool Dlss::Evaluate(ID3D11DeviceContext* context, const DlssPacket& packet)
    {
        NgxParameters* map = Map(parameters);
        map->Set("Color", static_cast<ID3D11Resource*>(packet.colour));
        map->Set("Output", static_cast<ID3D11Resource*>(packet.output));
        map->Set("Depth", static_cast<ID3D11Resource*>(packet.depth));
        map->Set("MotionVectors", static_cast<ID3D11Resource*>(packet.motionVectors));
        map->Set("Jitter.Offset.X", packet.jitterX);
        map->Set("Jitter.Offset.Y", packet.jitterY);
        map->Set("MV.Scale.X", packet.motionVectorScaleX);
        map->Set("MV.Scale.Y", packet.motionVectorScaleY);
        map->Set("Reset", (packet.flags & kDlssReset) != 0 ? 1 : 0);
        map->Set("DLSS.Render.Subrect.Dimensions.Width", static_cast<unsigned int>(packet.renderWidth));
        map->Set("DLSS.Render.Subrect.Dimensions.Height", static_cast<unsigned int>(packet.renderHeight));
        map->Set("DLSS.Pre.Exposure", 1.0f);
        map->Set("DLSS.Exposure.Scale", 1.0f);
        // Deprecated (guide 3.11); zero so no older library sharpens.
        map->Set("Sharpness", 0.0f);

        const uint32_t result = g_ngx.evaluateFeature(context, feature, map, nullptr);
        if (NgxFailed(result))
        {
            if (!loggedEvaluateFailure)
                LogLine("DLSS: evaluation failed (" + Hex(result) + ")");
            loggedEvaluateFailure = true;
            SetStatus(-1, "DLSS evaluation failed (" + Hex(result) + ")");
            return false;
        }

        ++evaluated;
        // Straight back to running after a failure, and every 1024 frames the count.
        if (renderState != 1 || (evaluated & 1023) == 0)
            SetStatus(1, "DLSS running, " + std::to_string(created.renderWidth) + "x"
                             + std::to_string(created.renderHeight) + " to " + std::to_string(created.outputWidth)
                             + "x" + std::to_string(created.outputHeight) + ", " + std::to_string(evaluated)
                             + " frames");
        return true;
    }

    void Dlss::ReleaseFeature()
    {
        if (feature == nullptr)
            return;
        g_ngx.releaseFeature(feature);
        feature = nullptr;
        created = DlssPacket{};
        LogLine("DLSS: feature released");
    }

    void Dlss::Shutdown()
    {
        ReleaseFeature();
        if (parameters != nullptr && parametersOwned)
            g_ngx.destroyParameters(Map(parameters));
        parameters = nullptr;
        parametersOwned = false;
        if (device != nullptr)
            g_ngx.shutdown(device.Get());
        device.Reset();
    }
}
