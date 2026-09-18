#include "Streamline.h"

#include "Config.h"
#include "FileUtil.h"
#include "Log.h"
#include "ProjectIdentity.h"

// NVIDIA's signature check (MIT, extern/Streamline). Its functions are defined
// in the header without inline, so this is the one file that includes it.
#include <sl_security.h>

#include <cstring>
#include <string>

namespace redefinition
{
    namespace
    {
        // The version the headers in extern/Streamline declare (sl_version.h),
        // as the DLL's version resource reads.
        constexpr const char* kFileVersionPrefix = "2.14.1.";

        // Streamline's own lines, into the proxy's log up to this many per start:
        // its warnings and errors, and fewer of its information lines, which at
        // start-up are mostly NGX listing its configuration.
        constexpr int kLogLines = 300;
        constexpr int kInfoLines = 120;
        std::atomic<int> g_logLines{ 0 };
        std::atomic<int> g_infoLines{ 0 };

        std::string ResultText(sl::Result result)
        {
            switch (result)
            {
            case sl::Result::eOk: return "ok";
            case sl::Result::eErrorIO: return "eErrorIO";
            case sl::Result::eErrorDriverOutOfDate: return "the NVIDIA driver is too old (eErrorDriverOutOfDate)";
            case sl::Result::eErrorOSOutOfDate: return "Windows is too old (eErrorOSOutOfDate)";
            case sl::Result::eErrorOSDisabledHWS:
                return "hardware-accelerated GPU scheduling is off in Windows' graphics settings (eErrorOSDisabledHWS)";
            case sl::Result::eErrorDeviceNotCreated: return "eErrorDeviceNotCreated";
            case sl::Result::eErrorNoSupportedAdapterFound:
                return "no GPU in this system supports it (eErrorNoSupportedAdapterFound)";
            case sl::Result::eErrorAdapterNotSupported: return "this GPU does not support it (eErrorAdapterNotSupported)";
            case sl::Result::eErrorNoPlugins: return "its plugins were not found (eErrorNoPlugins)";
            case sl::Result::eErrorVulkanAPI: return "eErrorVulkanAPI";
            case sl::Result::eErrorDXGIAPI: return "eErrorDXGIAPI";
            case sl::Result::eErrorD3DAPI: return "eErrorD3DAPI";
            case sl::Result::eErrorNRDAPI: return "eErrorNRDAPI";
            case sl::Result::eErrorNVAPI: return "eErrorNVAPI";
            case sl::Result::eErrorReflexAPI: return "eErrorReflexAPI";
            case sl::Result::eErrorNGXFailed: return "NGX failed, nvngx_dlssg.dll missing or unusable (eErrorNGXFailed)";
            case sl::Result::eErrorJSONParsing: return "eErrorJSONParsing";
            case sl::Result::eErrorMissingProxy: return "eErrorMissingProxy";
            case sl::Result::eErrorMissingResourceState: return "eErrorMissingResourceState";
            case sl::Result::eErrorInvalidIntegration: return "eErrorInvalidIntegration";
            case sl::Result::eErrorMissingInputParameter: return "eErrorMissingInputParameter";
            case sl::Result::eErrorNotInitialized: return "eErrorNotInitialized";
            case sl::Result::eErrorComputeFailed: return "eErrorComputeFailed";
            case sl::Result::eErrorInitNotCalled: return "eErrorInitNotCalled";
            case sl::Result::eErrorExceptionHandler: return "eErrorExceptionHandler";
            case sl::Result::eErrorInvalidParameter: return "eErrorInvalidParameter";
            case sl::Result::eErrorMissingConstants: return "eErrorMissingConstants";
            case sl::Result::eErrorDuplicatedConstants: return "eErrorDuplicatedConstants";
            case sl::Result::eErrorMissingOrInvalidAPI: return "eErrorMissingOrInvalidAPI";
            case sl::Result::eErrorCommonConstantsMissing: return "eErrorCommonConstantsMissing";
            case sl::Result::eErrorUnsupportedInterface: return "eErrorUnsupportedInterface";
            case sl::Result::eErrorFeatureMissing: return "its plugin was not found (eErrorFeatureMissing)";
            case sl::Result::eErrorFeatureNotSupported: return "not supported here (eErrorFeatureNotSupported)";
            case sl::Result::eErrorFeatureMissingHooks: return "eErrorFeatureMissingHooks";
            case sl::Result::eErrorFeatureFailedToLoad: return "its plugin failed to load (eErrorFeatureFailedToLoad)";
            case sl::Result::eErrorFeatureWrongPriority: return "eErrorFeatureWrongPriority";
            case sl::Result::eErrorFeatureMissingDependency: return "eErrorFeatureMissingDependency";
            case sl::Result::eErrorFeatureManagerInvalidState: return "eErrorFeatureManagerInvalidState";
            case sl::Result::eErrorInvalidState: return "eErrorInvalidState";
            case sl::Result::eWarnOutOfVRAM: return "out of video memory (eWarnOutOfVRAM)";
            default: return "result " + std::to_string(static_cast<int>(result));
            }
        }
    }

    Streamline& Streamline::Get()
    {
        static Streamline instance;
        return instance;
    }

    void Streamline::SetState(const std::string& text, bool log)
    {
        {
            std::lock_guard<std::mutex> lock(stateMutex);
            state = text;
        }
        if (log)
            LogLine("DLSS frame generation: " + text);
    }

    std::string Streamline::Describe() const
    {
        std::lock_guard<std::mutex> lock(stateMutex);
        return state;
    }

    bool Streamline::Check(sl::Result result, const char* call)
    {
        if (result == sl::Result::eOk)
            return true;

        constexpr int kLogged = 20;
        if (failedCallsLogged < kLogged)
        {
            ++failedCallsLogged;
            LogLine(std::string("Streamline: ") + call + " failed, " + ResultText(result)
                    + (failedCallsLogged == kLogged ? "   (further failed calls are not logged)" : ""));
        }
        return false;
    }

    template <typename T>
    bool Streamline::Import(T*& target, const char* name)
    {
        target = reinterpret_cast<T*>(GetProcAddress(module, name));
        return target != nullptr;
    }

    template <typename T>
    bool Streamline::ImportFeature(T*& target, sl::Feature feature, const char* name)
    {
        void* function = nullptr;
        target = nullptr;
        if (!Check(slGetFeatureFunction(feature, name, function), name) || function == nullptr)
            return false;
        target = reinterpret_cast<T*>(function);
        return true;
    }

    void Streamline::OnLogMessage(sl::LogType type, const char* message)
    {
        if (message == nullptr)
            return;
        if (type == sl::LogType::eInfo
            && (strstr(message, "NGXLoadConfig") != nullptr || strstr(message, "mapPluginCallbacks") != nullptr
                || strstr(message, "processPluginHooks") != nullptr || strstr(message, "isAppDenylisted") != nullptr
                || ++g_infoLines > kInfoLines))
            return;
        const int line = ++g_logLines;
        if (line > kLogLines)
            return;

        std::string text(message);
        while (!text.empty() && (text.back() == '\n' || text.back() == '\r'))
            text.pop_back();

        const char* kind = type == sl::LogType::eError ? "error: " : type == sl::LogType::eWarn ? "warning: " : "";
        LogLine(std::string("Streamline ") + kind + text
                + (line == kLogLines ? "   (further Streamline lines are not logged)" : ""));
    }

    bool Streamline::LoadModule()
    {
        if (module != nullptr)
            return true;

        std::wstring folder = StreamlineDirectory();
        if (folder.empty())
            folder = ExecutableDirectory();
        while (!folder.empty() && (folder.back() == L'\\' || folder.back() == L'/'))
            folder.pop_back();
        const std::wstring path = folder + L"\\sl.interposer.dll";

        if (GetFileAttributesW(path.c_str()) == INVALID_FILE_ATTRIBUTES)
        {
            SetState("no sl.interposer.dll in " + Narrow(folder) + " -- FSR frame generation instead", true);
            return false;
        }

        const std::string version = FileVersion(path);
        if (version.rfind(kFileVersionPrefix, 0) != 0)
        {
            SetState("sl.interposer.dll is version " + (version.empty() ? std::string("unknown") : version)
                         + ", this build runs Streamline 2.14.1 only -- FSR frame generation instead",
                     true);
            return false;
        }

        // "Validate the digital signature on sl.interposer.dll using the
        // WinVerifyTrust Win32 API" and "Validate the public key for the NVIDIA
        // custom digital certificate on sl.interposer.dll" (ProgrammingGuide.md
        // 2.1.1). The interposer checks every plugin it loads the same way.
        if (!sl::security::verifyEmbeddedSignature(path.c_str()))
        {
            SetState("sl.interposer.dll in " + Narrow(folder)
                         + " is not signed by NVIDIA and was not loaded -- FSR frame generation instead",
                     true);
            return false;
        }

        module = LoadLibraryExW(path.c_str(), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
        if (module == nullptr)
        {
            SetState("sl.interposer.dll could not be loaded (" + Hr(HRESULT_FROM_WIN32(GetLastError()))
                         + ") -- FSR frame generation instead",
                     true);
            return false;
        }

        const bool complete = Import(slInit, "slInit") && Import(slShutdown, "slShutdown")
                              && Import(slIsFeatureSupported, "slIsFeatureSupported")
                              && Import(slSetD3DDevice, "slSetD3DDevice")
                              && Import(slUpgradeInterface, "slUpgradeInterface")
                              && Import(slGetNativeInterface, "slGetNativeInterface")
                              && Import(slGetFeatureFunction, "slGetFeatureFunction")
                              && Import(slGetNewFrameToken, "slGetNewFrameToken")
                              && Import(slSetConstants, "slSetConstants")
                              && Import(slSetTagForFrame, "slSetTagForFrame")
                              && Import(slGetFeatureVersion, "slGetFeatureVersion");
        if (!complete)
        {
            FreeLibrary(module);
            module = nullptr;
            SetState("sl.interposer.dll lacks functions this build calls -- FSR frame generation instead", true);
            return false;
        }

        pluginFolder = folder;
        LogLine("DLSS frame generation: sl.interposer.dll " + version + ", signed by NVIDIA, loaded from "
                + Narrow(folder));
        return true;
    }

    bool Streamline::Initialise()
    {
        if (initialised)
            return true;
        if (!LoadModule())
            return false;

        const wchar_t* paths[] = { pluginFolder.c_str() };
        const sl::Feature features[] = { sl::kFeatureDLSS_G, sl::kFeatureReflex, sl::kFeaturePCL };

        sl::Preferences preferences{};
        preferences.showConsole = false;
        preferences.logLevel = sl::LogLevel::eDefault;
        // Its plugins and nvngx_dlssg.dll where sl.interposer.dll is: "Plugins
        // will be loaded from the first path where they can be found".
        preferences.pathsToPlugins = paths;
        preferences.numPathsToPlugins = 1;
        // No log files of its own: its lines go into this proxy's log.
        preferences.pathToLogsAndData = nullptr;
        preferences.logMessageCallback = &OnLogMessage;
        // Manual hooking, tags per frame token, and a factory proxy rather than
        // a patched vtable -- this proxy patches the factory's vtable itself
        // (DxgiExports.cpp). No over-the-air updates: what runs is the version
        // checked in LoadModule, not one NVIDIA downloads meanwhile.
        preferences.flags = sl::PreferenceFlags::eDisableCLStateTracking | sl::PreferenceFlags::eUseManualHooking
                            | sl::PreferenceFlags::eUseFrameBasedResourceTagging
                            | sl::PreferenceFlags::eUseDXGIFactoryProxy;
        preferences.featuresToLoad = features;
        preferences.numFeaturesToLoad = static_cast<uint32_t>(sizeof(features) / sizeof(features[0]));
        // "if not specified then engine type and version are required": the
        // same identity DLSS gives NGX.
        preferences.engine = sl::EngineType::eCustom;
        preferences.engineVersion = kEngineVersion;
        preferences.projectId = kNvidiaProjectId;
        preferences.renderAPI = sl::RenderAPI::eD3D12;

        g_logLines = 0;
        g_infoLines = 0;
        failedCallsLogged = 0;
        const sl::Result result = slInit(preferences, sl::kSDKVersion);
        if (result != sl::Result::eOk)
        {
            SetState("slInit failed, " + ResultText(result) + " -- FSR frame generation instead", true);
            return false;
        }

        initialised = true;
        SetState("initialised", false);
        LogLine("DLSS frame generation: Streamline initialised");
        return true;
    }

    bool Streamline::Acquire(const void* newOwner)
    {
        // A swapchain made while an earlier one lives presents with FSR for as long
        // as it lives: its swapchain is made now, through Streamline or not.
        if (owner != nullptr && owner != newOwner)
        {
            SetState("Streamline belonged to an earlier swapchain still when this one was made -- FSR frame"
                         " generation instead",
                     true);
            return false;
        }
        if (!Initialise())
            return false;
        owner = newOwner;
        return true;
    }

    void Streamline::Release(const void* caller)
    {
        if (owner != caller)
            return;
        Shutdown();
        owner = nullptr;
    }

    void Streamline::Note(const std::string& why)
    {
        SetState(why, true);
    }

    void Streamline::Shutdown()
    {
        if (!initialised)
            return;

        Check(slShutdown(), "slShutdown");
        initialised = false;
        deviceSet = false;
        slDLSSGSetOptions = nullptr;
        slDLSSGGetState = nullptr;
        slReflexSetOptions = nullptr;
        slReflexSleep = nullptr;
        slPCLSetMarker = nullptr;
        // A reason already stated stays; "ready" does not outlast the shutdown.
        {
            std::lock_guard<std::mutex> lock(stateMutex);
            if (state == "ready" || state == "initialised")
                state = "shut down with its swapchain";
        }
        LogLine("DLSS frame generation: Streamline shut down");
    }

    bool Streamline::FrameGenerationSupported(const LUID& adapter)
    {
        if (!initialised)
            return false;

        LUID luid = adapter;
        sl::AdapterInfo info{};
        info.deviceLUID = reinterpret_cast<uint8_t*>(&luid);
        info.deviceLUIDSizeInBytes = sizeof(LUID);

        const sl::Result frameGeneration = slIsFeatureSupported(sl::kFeatureDLSS_G, info);
        if (frameGeneration != sl::Result::eOk)
        {
            SetState("does not run here: " + ResultText(frameGeneration) + " -- FSR frame generation instead", true);
            return false;
        }

        // Required with it: "It is required for sl.reflex to be integrated"
        // (ProgrammingGuideDLSS_G.md 8.0).
        const sl::Result reflex = slIsFeatureSupported(sl::kFeatureReflex, info);
        if (reflex != sl::Result::eOk)
        {
            SetState("Reflex does not run here: " + ResultText(reflex) + " -- FSR frame generation instead", true);
            return false;
        }

        return true;
    }

    bool Streamline::SetDevice(ID3D12Device* nativeDevice)
    {
        if (!initialised)
            return false;
        if (!Check(slSetD3DDevice(nativeDevice), "slSetD3DDevice"))
        {
            SetState("Streamline did not take the D3D12 device -- FSR frame generation instead", true);
            return false;
        }
        deviceSet = true;

        const bool functions = ImportFeature(slDLSSGSetOptions, sl::kFeatureDLSS_G, "slDLSSGSetOptions")
                               && ImportFeature(slDLSSGGetState, sl::kFeatureDLSS_G, "slDLSSGGetState")
                               && ImportFeature(slReflexSetOptions, sl::kFeatureReflex, "slReflexSetOptions")
                               && ImportFeature(slReflexSleep, sl::kFeatureReflex, "slReflexSleep")
                               && ImportFeature(slPCLSetMarker, sl::kFeaturePCL, "slPCLSetMarker");
        if (!functions)
        {
            SetState("its feature functions are missing -- FSR frame generation instead", true);
            return false;
        }

        // Reflex on: "Reflex is not active while DLSS-G is running, Reflex must
        // be turned on when DLSS-G is on" (sl_dlss_g.h, DLSSGStatus).
        sl::ReflexOptions reflex{};
        reflex.mode = sl::ReflexMode::eLowLatency;
        Check(slReflexSetOptions(reflex), "slReflexSetOptions");

        sl::FeatureVersion version{};
        if (slGetFeatureVersion(sl::kFeatureDLSS_G, version) == sl::Result::eOk)
            LogLine("DLSS frame generation: Streamline " + version.versionSL.toStr() + ", nvngx_dlssg "
                    + version.versionNGX.toStr());

        SetState("ready", false);
        return true;
    }

    bool Streamline::UpgradeInterface(void** baseInterface, const char* what)
    {
        return initialised && Check(slUpgradeInterface(baseInterface), what);
    }

    bool Streamline::NativeInterface(void* proxy, void** native)
    {
        *native = nullptr;
        return initialised && slGetNativeInterface(proxy, native) == sl::Result::eOk && *native != nullptr;
    }

    sl::FrameToken* Streamline::NewFrameToken(uint32_t frameIndex)
    {
        if (!deviceSet)
            return nullptr;
        sl::FrameToken* token = nullptr;
        const uint32_t index = frameIndex;
        if (!Check(slGetNewFrameToken(token, &index), "slGetNewFrameToken"))
            return nullptr;
        return token;
    }

    bool Streamline::SetConstants(const sl::Constants& constants, const sl::FrameToken& frame)
    {
        return deviceSet && Check(slSetConstants(constants, frame, sl::ViewportHandle(kViewport)), "slSetConstants");
    }

    bool Streamline::SetTags(const sl::FrameToken& frame, const sl::ResourceTag* tags, uint32_t count)
    {
        // No command list: every tag lasts until Present, which the API allows
        // without one ("can be null if ALL tags are null or have
        // eValidUntilPresent life-cycle").
        return deviceSet
               && Check(slSetTagForFrame(frame, sl::ViewportHandle(kViewport), tags, count, nullptr),
                        "slSetTagForFrame");
    }

    bool Streamline::SetOptions(const sl::DLSSGOptions& options)
    {
        return slDLSSGSetOptions != nullptr
               && Check(slDLSSGSetOptions(sl::ViewportHandle(kViewport), options), "slDLSSGSetOptions");
    }

    bool Streamline::GetState(sl::DLSSGState& frameGenerationState)
    {
        return slDLSSGGetState != nullptr
               && Check(slDLSSGGetState(sl::ViewportHandle(kViewport), frameGenerationState, nullptr),
                        "slDLSSGGetState");
    }

    void Streamline::Marker(sl::PCLMarker marker, const sl::FrameToken& frame)
    {
        if (slPCLSetMarker != nullptr)
            Check(slPCLSetMarker(marker, frame), "slPCLSetMarker");
    }

    void Streamline::ReflexSleep(const sl::FrameToken& frame)
    {
        if (slReflexSleep != nullptr)
            Check(slReflexSleep(frame), "slReflexSleep");
    }
}
