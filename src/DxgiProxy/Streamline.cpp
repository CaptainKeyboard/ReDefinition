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
        const int logged = ++failedCallsLogged;
        if (logged <= kLogged)
            LogLine(std::string("Streamline: ") + call + " failed, " + ResultText(result)
                    + (logged == kLogged ? "   (further failed calls are not logged)" : ""));
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
            SetState("no sl.interposer.dll in " + Narrow(folder) + Instead(), true);
            return false;
        }

        const std::string version = FileVersion(path);
        if (version.rfind(kFileVersionPrefix, 0) != 0)
        {
            SetState("sl.interposer.dll is version " + (version.empty() ? std::string("unknown") : version)
                         + ", this build runs Streamline 2.14.1 only" + Instead(),
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
                         + " is not signed by NVIDIA and was not loaded" + Instead(),
                     true);
            return false;
        }

        module = LoadLibraryExW(path.c_str(), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
        if (module == nullptr)
        {
            SetState("sl.interposer.dll could not be loaded (" + Hr(HRESULT_FROM_WIN32(GetLastError()))
                         + ")" + Instead(),
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
                              && Import(slGetFeatureVersion, "slGetFeatureVersion")
                              && Import(slFreeResources, "slFreeResources");
        if (!complete)
        {
            FreeLibrary(module);
            module = nullptr;
            SetState("sl.interposer.dll lacks functions this build calls" + Instead(), true);
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
        // DLSS Ray Reconstruction loads where its plugin is there; without it, frame
        // generation runs as before. For Ray Reconstruction alone, its plugin only:
        // sl.dlss_g would take over the swapchain's pacing "whenever the plugin is
        // loaded, even while DLSS-G is turned off" (ProgrammingGuideDLSS_G.md 12.1).
        const sl::Feature allFeatures[] = { sl::kFeatureDLSS_G, sl::kFeatureReflex, sl::kFeaturePCL,
                                            sl::kFeatureDLSS_RR };
        const sl::Feature reconstructionOnly[] = { sl::kFeatureDLSS_RR };
        const sl::Feature* features = frameGenerationWanted ? allFeatures : reconstructionOnly;
        const uint32_t featureCount = frameGenerationWanted ? 4u : 1u;

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
        preferences.numFeaturesToLoad = featureCount;
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
            SetState("slInit failed, " + ResultText(result) + Instead(), true);
            return false;
        }

        initialised = true;
        SetState("initialised", false);
        LogLine("DLSS frame generation: Streamline initialised");
        return true;
    }

    std::string Streamline::Instead() const
    {
        return frameGenerationWanted ? " -- FSR frame generation instead" : " -- no DLSS Ray Reconstruction";
    }

    bool Streamline::Acquire(const void* newOwner, bool forFrameGeneration)
    {
        // A swapchain made while an earlier one lives presents with FSR for as long
        // as it lives: its swapchain is made now, through Streamline or not.
        if (owner != nullptr && owner != newOwner)
        {
            SetState("Streamline belonged to an earlier swapchain still when this one was made" + Instead(), true);
            return false;
        }
        frameGenerationWanted = forFrameGeneration;
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

        // No call from the game's main thread is inside Streamline's plugins
        // while they are unloaded.
        std::unique_lock<std::shared_mutex> calls(callMutex);
        Check(slShutdown(), "slShutdown");
        initialised = false;
        deviceSet = false;
        slDLSSGSetOptions = nullptr;
        slDLSSGGetState = nullptr;
        slReflexSetOptions = nullptr;
        slReflexSleep = nullptr;
        slPCLSetMarker = nullptr;
        slPCLGetState = nullptr;
        pingMessage = 0;
        slEvaluateFeature = nullptr;
        slDLSSDSetOptions = nullptr;
        slDLSSDGetOptimalSettings = nullptr;
        rayReconstruction = false;
        frameInProgress = -1;
        // A reason already stated stays; "ready" does not outlast the shutdown.
        {
            std::lock_guard<std::mutex> lock(stateMutex);
            rayReconstructionWhy = "Streamline shut down with its swapchain";
            if (state == "ready" || state == "initialised")
                state = "shut down with its swapchain";
        }
        LogLine("DLSS frame generation: Streamline shut down");
    }

    bool Streamline::Supported(const LUID& adapter)
    {
        frameGeneration = false;
        if (!initialised)
            return false;

        LUID luid = adapter;
        sl::AdapterInfo info{};
        info.deviceLUID = reinterpret_cast<uint8_t*>(&luid);
        info.deviceLUIDSizeInBytes = sizeof(LUID);

        if (!frameGenerationWanted)
        {
            const sl::Result reconstruction = slIsFeatureSupported(sl::kFeatureDLSS_RR, info);
            if (reconstruction != sl::Result::eOk)
            {
                SetState("frame generation is off, and DLSS Ray Reconstruction does not run here: "
                         + ResultText(reconstruction), true);
                return false;
            }
            return true;
        }

        const sl::Result dlssg = slIsFeatureSupported(sl::kFeatureDLSS_G, info);
        if (dlssg != sl::Result::eOk)
        {
            SetState("does not run here: " + ResultText(dlssg) + Instead(), true);
            return false;
        }

        // Required with it: "It is required for sl.reflex to be integrated"
        // (ProgrammingGuideDLSS_G.md 8.0).
        const sl::Result reflex = slIsFeatureSupported(sl::kFeatureReflex, info);
        if (reflex != sl::Result::eOk)
        {
            SetState("Reflex does not run here: " + ResultText(reflex) + Instead(), true);
            return false;
        }

        frameGeneration = true;
        return true;
    }

    bool Streamline::SetDevice(ID3D12Device* nativeDevice)
    {
        if (!initialised)
            return false;
        if (!Check(slSetD3DDevice(nativeDevice), "slSetD3DDevice"))
        {
            SetState("Streamline did not take the D3D12 device" + Instead(), true);
            return false;
        }

        if (frameGeneration)
        {
            const bool functions = ImportFeature(slDLSSGSetOptions, sl::kFeatureDLSS_G, "slDLSSGSetOptions")
                                   && ImportFeature(slDLSSGGetState, sl::kFeatureDLSS_G, "slDLSSGGetState")
                                   && ImportFeature(slReflexSetOptions, sl::kFeatureReflex, "slReflexSetOptions")
                                   && ImportFeature(slReflexSleep, sl::kFeatureReflex, "slReflexSleep")
                                   && ImportFeature(slPCLSetMarker, sl::kFeaturePCL, "slPCLSetMarker");
            if (!functions)
            {
                SetState("its feature functions are missing" + Instead(), true);
                return false;
            }

            // Reflex on: "Reflex is not active while DLSS-G is running, Reflex must
            // be turned on when DLSS-G is on" (sl_dlss_g.h, DLSSGStatus).
            sl::ReflexOptions reflex{};
            reflex.mode = sl::ReflexMode::eLowLatency;
            Check(slReflexSetOptions(reflex), "slReflexSetOptions");
        }

        // The message PCL Stats pings the game's window with: "On seeing the ping
        // message, the application must either call slPclSetMarker or
        // sl::PclMarker::ePCLatencyPing to send the ping marker right away"
        // (ProgrammingGuidePCL.md). Without it, only that ping is missing.
        sl::PCLState pcl{};
        if (ImportFeature(slPCLGetState, sl::kFeaturePCL, "slPCLGetState")
            && Check(slPCLGetState(pcl), "slPCLGetState"))
            pingMessage = pcl.statsWindowMessage;

        // Ray Reconstruction on the same device, where its plugin loaded and the GPU
        // runs it; its absence changes nothing for frame generation.
        {
            rayReconstruction = false;
            std::string why;
            LUID luid = nativeDevice->GetAdapterLuid();
            sl::AdapterInfo info{};
            info.deviceLUID = reinterpret_cast<uint8_t*>(&luid);
            info.deviceLUIDSizeInBytes = sizeof(LUID);
            const sl::Result supported = slIsFeatureSupported(sl::kFeatureDLSS_RR, info);
            void* evaluate = nullptr;
            if (supported != sl::Result::eOk)
                why = "DLSS Ray Reconstruction does not run here: " + ResultText(supported);
            else if (module == nullptr
                     || (evaluate = reinterpret_cast<void*>(GetProcAddress(module, "slEvaluateFeature"))) == nullptr)
                why = "sl.interposer.dll has no slEvaluateFeature";
            else
            {
                slEvaluateFeature = reinterpret_cast<PFun_slEvaluateFeature*>(evaluate);
                void* setOptions = nullptr;
                void* optimal = nullptr;
                if (slGetFeatureFunction(sl::kFeatureDLSS_RR, "slDLSSDSetOptions", setOptions) != sl::Result::eOk
                    || setOptions == nullptr
                    || slGetFeatureFunction(sl::kFeatureDLSS_RR, "slDLSSDGetOptimalSettings", optimal) != sl::Result::eOk
                    || optimal == nullptr)
                    why = "sl.dlss_d.dll lacks its functions";
                else
                {
                    slDLSSDSetOptions = reinterpret_cast<PFun_slDLSSDSetOptions*>(setOptions);
                    slDLSSDGetOptimalSettings = reinterpret_cast<PFun_slDLSSDGetOptimalSettings*>(optimal);
                    rayReconstruction = true;
                    why = "ready";
                }
            }
            sl::FeatureVersion rrVersion{};
            if (rayReconstruction && slGetFeatureVersion(sl::kFeatureDLSS_RR, rrVersion) == sl::Result::eOk)
                LogLine("DLSS Ray Reconstruction: Streamline " + rrVersion.versionSL.toStr() + ", nvngx_dlssd "
                        + rrVersion.versionNGX.toStr());
            else
                LogLine(why);
            std::lock_guard<std::mutex> lock(stateMutex);
            rayReconstructionWhy = why;
        }

        sl::FeatureVersion version{};
        if (frameGeneration && slGetFeatureVersion(sl::kFeatureDLSS_G, version) == sl::Result::eOk)
            LogLine("DLSS frame generation: Streamline " + version.versionSL.toStr() + ", nvngx_dlssg "
                    + version.versionNGX.toStr());

        // Last: the game's main thread calls in once it is set (BeginGameFrame), and
        // finds the functions imported and Reflex's options given.
        deviceSet = true;
        SetState("ready", false);
        return true;
    }

    // The game's frame no longer in progress: a Present without it, or the
    // presenting thread beginning the frames again. Ray Reconstruction and the
    // ping then take no index already presented.
    void Streamline::ClearGameFrame()
    {
        frameInProgress = -1;
        gameFrame = 0;
    }

    bool Streamline::RayReconstructionReady(std::string& why) const
    {
        std::lock_guard<std::mutex> lock(stateMutex);
        why = deviceSet ? rayReconstructionWhy : std::string("Streamline has no device: it runs with DLSS frame generation, or with frame generation off in ReDefinitionProxy.ini");
        return deviceSet && rayReconstruction.load();
    }

    void Streamline::NoteFrameInProgress(uint32_t frameIndex)
    {
        frameInProgress = static_cast<int64_t>(frameIndex);
    }

    bool Streamline::EvaluateRayReconstruction(const sl::Constants& constants, const sl::DLSSDOptions& options,
                                               const sl::ResourceTag* tags, uint32_t count,
                                               ID3D12GraphicsCommandList* list)
    {
        if (!deviceSet || !rayReconstruction || list == nullptr)
            return false;
        // The swapchain's frame in progress, whose token its present takes.
        const uint32_t frame = FrameInProgressIndex();
        const sl::ViewportHandle viewport(kRayReconstructionViewport);
        if (!WithToken(frame, "slSetConstants (Ray Reconstruction)",
                       [&](const sl::FrameToken& token) { return slSetConstants(constants, token, viewport); })
            || !Check(slDLSSDSetOptions(viewport, options), "slDLSSDSetOptions")
            || !WithToken(frame, "slSetTagForFrame (Ray Reconstruction)",
                          [&](const sl::FrameToken& token)
                          { return slSetTagForFrame(token, viewport, tags, count, list); }))
            return false;
        const sl::BaseStructure* inputs[] = { &viewport };
        return WithToken(frame, "slEvaluateFeature (Ray Reconstruction)", [&](const sl::FrameToken& token)
                         { return slEvaluateFeature(sl::kFeatureDLSS_RR, token, inputs, 1, list); });
    }

    // "To turn off DLSS-RR set sl::DLSSDOptions.mode to sl::DLSSDMode::eOff, note
    // that this does NOT release any resources, for that please use slFreeResources"
    // (ProgrammingGuideDLSS_RR.md 5.0); "If slEvaluateFeature is pending on a command
    // list, that command list must be flushed before calling this method"
    // (sl_core_api.h) -- the caller's to see to.
    bool Streamline::FreeRayReconstruction()
    {
        if (!deviceSet || !rayReconstruction || slFreeResources == nullptr)
            return false;
        return Check(slFreeResources(sl::kFeatureDLSS_RR, sl::ViewportHandle(kRayReconstructionViewport)),
                     "slFreeResources (Ray Reconstruction)");
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

    uint32_t Streamline::FrameInProgressIndex()
    {
        const int64_t current = frameInProgress.load();
        return current >= 0 ? static_cast<uint32_t>(current) : ownFrameIndex++;
    }

    template <typename Call>
    bool Streamline::WithToken(uint32_t frameIndex, const char* what, Call&& call)
    {
        if (!deviceSet)
            return false;
        std::lock_guard<std::mutex> lock(tokenMutex);
        sl::FrameToken* token = nullptr;
        const uint32_t index = frameIndex;
        return Check(slGetNewFrameToken(token, &index), "slGetNewFrameToken") && Check(call(*token), what);
    }

    bool Streamline::SetConstants(const sl::Constants& constants, uint32_t frameIndex)
    {
        return WithToken(frameIndex, "slSetConstants", [&](const sl::FrameToken& token)
                         { return slSetConstants(constants, token, sl::ViewportHandle(kViewport)); });
    }

    bool Streamline::SetTags(uint32_t frameIndex, const sl::ResourceTag* tags, uint32_t count)
    {
        // No command list: every tag lasts until Present, which the API allows
        // without one ("can be null if ALL tags are null or have
        // eValidUntilPresent life-cycle").
        return WithToken(frameIndex, "slSetTagForFrame", [&](const sl::FrameToken& token)
                         { return slSetTagForFrame(token, sl::ViewportHandle(kViewport), tags, count, nullptr); });
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

    void Streamline::Marker(sl::PCLMarker marker, uint32_t frameIndex)
    {
        std::shared_lock<std::shared_mutex> calls(callMutex);
        MarkerLocked(marker, frameIndex);
    }

    void Streamline::ReflexSleep(uint32_t frameIndex)
    {
        std::shared_lock<std::shared_mutex> calls(callMutex);
        ReflexSleepLocked(frameIndex);
    }

    // "This method is thread safe" (sl_pcl.h, slPCLSetMarker).
    void Streamline::MarkerLocked(sl::PCLMarker marker, uint32_t frameIndex)
    {
        if (slPCLSetMarker != nullptr)
            WithToken(frameIndex, "slPCLSetMarker",
                      [&](const sl::FrameToken& token) { return slPCLSetMarker(marker, token); });
    }

    // "This method is thread safe" (sl_reflex.h, slReflexSleep). The token is
    // fetched under tokenMutex, the sleep is not: it lasts until Reflex lets the
    // frame begin.
    void Streamline::ReflexSleepLocked(uint32_t frameIndex)
    {
        if (slReflexSleep == nullptr || !deviceSet)
            return;
        sl::FrameToken* token = nullptr;
        {
            std::lock_guard<std::mutex> lock(tokenMutex);
            const uint32_t index = frameIndex;
            if (!Check(slGetNewFrameToken(token, &index), "slGetNewFrameToken"))
                return;
        }
        Check(slReflexSleep(*token), "slReflexSleep");
    }

    uint32_t Streamline::NextFrameIndex()
    {
        uint32_t index = ++frameCounter;
        if (index == 0)
            index = ++frameCounter;
        return index;
    }

    // Where the game's frame begins, on its main thread, before its messages are
    // taken: "Starting new frame, grab handle from SL", then slReflexSleep
    // (ProgrammingGuideReflex.md 5.0); "SIMULATION_START must be the first marker
    // sent in a frame, after the previous frame's Sleep call (if used)" (nvapi.h,
    // NvAPI_D3D_SetLatencyMarker).
    uint32_t Streamline::BeginGameFrame()
    {
        std::shared_lock<std::shared_mutex> calls(callMutex);
        if (!deviceSet)
            return 0;
        InstallPingHook();
        const uint32_t index = NextFrameIndex();
        ReflexSleepLocked(index);
        gameFrame = index;
        MarkerLocked(sl::PCLMarker::eSimulationStart, index);
        return index;
    }

    void Streamline::EndGameSimulation(uint32_t frameIndex)
    {
        std::shared_lock<std::shared_mutex> calls(callMutex);
        if (frameIndex != 0)
            MarkerLocked(sl::PCLMarker::eSimulationEnd, frameIndex);
    }

    // "RENDERSUBMIT_START should come before any Direct3D calls are made for the
    // given frame" (nvapi.h, NvAPI_D3D_SetLatencyMarker): Unity's render thread
    // runs this before the frame's first one.
    void Streamline::OnGameFrameRendered(uint32_t frameIndex)
    {
        if (frameIndex == 0)
            return;
        NoteFrameInProgress(frameIndex);
        Marker(sl::PCLMarker::eRenderSubmitStart, frameIndex);
        renderedGameFrame = frameIndex;
    }

    bool Streamline::TakeGameFrame(uint32_t& frameIndex)
    {
        frameIndex = renderedGameFrame.exchange(0);
        return frameIndex != 0;
    }

    // Installed once, on the thread that begins the game's frames, whose queue
    // the game window's messages arrive in -- the ping among them, which PCL
    // Stats posts to the foreground window (PCLStatsPingThreadProc, pclstats.h).
    // "The hMod parameter must be set to NULL if the dwThreadId parameter
    // specifies a thread created by the current process and the hook procedure
    // is within the code associated with the current process" (Microsoft,
    // SetWindowsHookExW).
    void Streamline::InstallPingHook()
    {
        static std::atomic<DWORD> hookedThread{ 0 };
        const DWORD thread = GetCurrentThreadId();
        DWORD none = 0;
        if (pingMessage == 0 || !hookedThread.compare_exchange_strong(none, thread))
            return;
        if (SetWindowsHookExW(WH_GETMESSAGE, &Streamline::OnGetMessage, nullptr, thread) == nullptr)
            LogLine("Reflex: no hook for PC latency's ping on the game's main thread ("
                    + Hr(HRESULT_FROM_WIN32(GetLastError())) + ") -- its input latency goes unmeasured");
    }

    LRESULT CALLBACK Streamline::OnGetMessage(int code, WPARAM wParam, LPARAM lParam)
    {
        if (code == HC_ACTION && wParam == PM_REMOVE)
        {
            const MSG* message = reinterpret_cast<const MSG*>(lParam);
            const uint32_t ping = Get().pingMessage.load();
            if (ping != 0 && message->message == ping)
                Get().Ping();
        }
        return CallNextHookEx(nullptr, code, wParam, lParam);
    }

    // Taken while the game's frame runs, after its eSimulationStart: "First
    // scenario, using current frame" (ProgrammingGuidePCL.md 3.0).
    void Streamline::Ping()
    {
        const uint32_t frame = gameFrame.load();
        if (frame == 0)
            return;
        Marker(sl::PCLMarker::ePCLatencyPing, frame);
        if (!pingSeen.exchange(true))
            LogLine("Reflex: PC latency's ping picked up on the game's main thread, frame "
                    + std::to_string(frame));
    }
}
