#include "XessFg.h"

#include "Config.h"
#include "FileUtil.h"
#include "Log.h"

#include <XeSS/xell_d3d12.h>

#include <atomic>
#include <string>

using Microsoft::WRL::ComPtr;

namespace redefinition
{
    namespace
    {
        constexpr const wchar_t* kXellName = L"libxell.dll";
        constexpr const wchar_t* kFgName = L"libxess_fg.dll";

        // xessFgDirectory, or next to the executable.
        std::wstring RuntimeFolder()
        {
            const std::wstring directory = XessFgDirectory();
            return directory.empty() ? ExecutableDirectory() : directory;
        }

        // Their messages into the log, "called from other threads" too, at most
        // kLogLines of them: a warning a frame would fill it.
        constexpr int kLogLines = 200;
        std::atomic<int> g_logLines{ 0 };

        bool TakeLogLine()
        {
            const int line = g_logLines.fetch_add(1);
            if (line == kLogLines)
                LogLine("XeSS: further messages from libxess_fg.dll and libxell.dll are not logged");
            return line < kLogLines;
        }

        const char* LevelName(int level)
        {
            return level <= 0 ? "debug" : level == 1 ? "info" : level == 2 ? "warning" : "error";
        }

        void OnFgMessage(const char* message, int32_t level, void*)
        {
            if (message != nullptr && TakeLogLine())
                LogLine(std::string("XeSS-FG ") + LevelName(level) + ": " + message);
        }

        void OnXellMessage(const char* message, xell_logging_level_t level)
        {
            if (message != nullptr && TakeLogLine())
                LogLine(std::string("XeLL ") + LevelName(static_cast<int>(level)) + ": " + message);
        }

        template <typename T>
        bool Resolve(HMODULE module, const char* name, T& function)
        {
            function = reinterpret_cast<T>(GetProcAddress(module, name));
            return function != nullptr;
        }

        std::string ResultText(int result)
        {
            return "result " + std::to_string(result);
        }
    }

    XessFg& XessFg::Get()
    {
        static XessFg* instance = new XessFg();
        return *instance;
    }

    void XessFg::NoteLocked(const std::string& text)
    {
        why = text;
        LogLine("XeSS frame generation: " + text);
    }

    void XessFg::Note(const std::string& text)
    {
        std::lock_guard<std::mutex> lock(mutex);
        NoteLocked(text);
    }

    std::string XessFg::Why() const
    {
        std::lock_guard<std::mutex> lock(mutex);
        return why;
    }

    std::string XessFg::Versions() const
    {
        std::lock_guard<std::mutex> lock(mutex);
        return versions;
    }

    // Loaded by path from the folder, as the XeSS upscaler's libxess.dll is
    // (XessUpscaler.cpp): libxess_fg.dll looks for XeLL's functions itself
    // ("XeLL library does not contains required functions",
    // XEFG_SWAPCHAIN_RESULT_ERROR_LATENCY_REDUCTION_FUNCTION_MISSING), and a module
    // already loaded under its name is the one it finds.
    bool XessFg::LoadXell()
    {
        std::lock_guard<std::mutex> lock(mutex);
        if (xellModule != nullptr)
            return true;

        const std::wstring folder = RuntimeFolder();
        const std::wstring path = folder + L"\\" + kXellName;
        HMODULE module = LoadLibraryExW(path.c_str(), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
        if (module == nullptr)
        {
            const DWORD error = GetLastError();
            NoteLocked(GetFileAttributesW(path.c_str()) == INVALID_FILE_ATTRIBUTES
                           ? "no libxell.dll in " + Narrow(folder) + " -- Intel's XeSS SDK has it"
                           : Narrow(path) + " could not be loaded (" + Hr(HRESULT_FROM_WIN32(error)) + ")");
            return false;
        }

        const bool complete = Resolve(module, "xellD3D12CreateContext", xellCreate)
                              && Resolve(module, "xellDestroyContext", xellDestroy)
                              && Resolve(module, "xellSetSleepMode", xellSetSleepMode)
                              && Resolve(module, "xellSleep", xellSleep)
                              && Resolve(module, "xellAddMarkerData", xellAddMarkerData)
                              && Resolve(module, "xellGetVersion", xellGetVersion);
        Resolve(module, "xellSetLoggingCallback", xellSetLoggingCallback);
        if (!complete)
        {
            FreeLibrary(module);
            xellCreate = nullptr;
            NoteLocked(Narrow(path) + " is not Intel's XeLL for Direct3D 12: a function is missing");
            return false;
        }
        xellModule = module;

        xell_version_t version = {};
        const std::string name = xellGetVersion(&version) == XELL_RESULT_SUCCESS
                                     ? "XeLL " + std::to_string(version.major) + "." + std::to_string(version.minor)
                                           + "." + std::to_string(version.patch)
                                     : std::string("XeLL");
        versions = versions.empty() ? name : versions + ", " + name;
        LogLine("XeSS: " + Narrow(path) + " " + FileVersion(path) + ", " + name);
        return true;
    }

    bool XessFg::LoadFrameGeneration()
    {
        std::lock_guard<std::mutex> lock(mutex);
        if (fgModule != nullptr)
            return true;
        if (xellModule == nullptr)
        {
            NoteLocked("libxell.dll is not loaded, which XeSS frame generation requires");
            return false;
        }

        const std::wstring folder = RuntimeFolder();
        const std::wstring path = folder + L"\\" + kFgName;
        HMODULE module = LoadLibraryExW(path.c_str(), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
        if (module == nullptr)
        {
            const DWORD error = GetLastError();
            NoteLocked(GetFileAttributesW(path.c_str()) == INVALID_FILE_ATTRIBUTES
                           ? "no libxess_fg.dll in " + Narrow(folder) + " -- Intel's XeSS SDK has it"
                           : Narrow(path) + " could not be loaded (" + Hr(HRESULT_FROM_WIN32(error)) + ")");
            return false;
        }

        const bool complete = Resolve(module, "xefgSwapChainGetVersion", fgGetVersion)
                              && Resolve(module, "xefgSwapChainD3D12CreateContext", fgCreateContext)
                              && Resolve(module, "xefgSwapChainSetLatencyReduction", fgSetLatencyReduction)
                              && Resolve(module, "xefgSwapChainD3D12InitFromSwapChainDesc", fgInitFromSwapChainDesc)
                              && Resolve(module, "xefgSwapChainD3D12GetSwapChainPtr", fgGetSwapChainPtr)
                              && Resolve(module, "xefgSwapChainDestroy", fgDestroy)
                              && Resolve(module, "xefgSwapChainSetEnabled", fgSetEnabled)
                              && Resolve(module, "xefgSwapChainSetPresentId", fgSetPresentId)
                              && Resolve(module, "xefgSwapChainD3D12TagFrameResource", fgTagFrameResource)
                              && Resolve(module, "xefgSwapChainTagFrameConstants", fgTagFrameConstants)
                              && Resolve(module, "xefgSwapChainGetLastPresentStatus", fgGetLastPresentStatus);
        Resolve(module, "xefgSwapChainSetLoggingCallback", fgSetLoggingCallback);
        Resolve(module, "xefgSwapChainD3D12GetInitializationParameters", fgGetInitializationParameters);
        Resolve(module, "xefgSwapChainSetUiCompositionState", fgSetUiCompositionState);
        Resolve(module, "xefgSwapChainEnableDebugFeature", fgEnableDebugFeature);
        if (!complete)
        {
            FreeLibrary(module);
            fgCreateContext = nullptr;
            NoteLocked(Narrow(path) + " is not Intel's XeSS frame generation for Direct3D 12: a function is missing");
            return false;
        }
        fgModule = module;

        xefg::Version version = {};
        const std::string name = fgGetVersion(&version) == xefg::kSuccess
                                     ? "XeSS-FG " + std::to_string(version.major) + "." + std::to_string(version.minor)
                                           + "." + std::to_string(version.patch)
                                     : std::string("XeSS-FG");
        versions = name + (versions.empty() ? "" : ", " + versions);
        LogLine("XeSS: " + Narrow(path) + " " + FileVersion(path) + ", " + name);
        return true;
    }

    // "The function will return XELL_RESULT_ERROR_UNSUPPORTED_DEVICE if the device is
    // not supported or XELL_RESULT_ERROR_UNSUPPORTED_DRIVER if the installed driver is
    // not supported. No context will get created in such cases. The application should
    // validate if the context is not a null pointer and if the return value is
    // XELL_RESULT_SUCCESS" (xell_developer_guide_english.md, Initialization).
    xell_context_handle_t XessFg::CreateXell(ID3D12Device* device)
    {
        std::lock_guard<std::mutex> lock(mutex);
        if (xellCreate == nullptr || device == nullptr)
            return nullptr;
        xell_context_handle_t context = nullptr;
        const xell_result_t result = xellCreate(device, &context);
        if (result != XELL_RESULT_SUCCESS || context == nullptr)
        {
            NoteLocked("XeLL refused the device (" + ResultText(static_cast<int>(result)) + ")");
            return nullptr;
        }
        // "We recommend using error for production and warning or debug during
        // development."
        if (xellSetLoggingCallback != nullptr)
            xellSetLoggingCallback(context, Config().vendorDiagnostics ? XELL_LOGGING_LEVEL_DEBUG : XELL_LOGGING_LEVEL_WARNING,
                                   &OnXellMessage);
        LogLine("XeLL: context made on the proxy's Direct3D 12 device");
        return context;
    }

    void XessFg::DestroyXell(xell_context_handle_t context)
    {
        if (context == nullptr)
            return;
        std::lock_guard<std::mutex> lock(mutex);
        if (xellDestroy == nullptr)
            return;
        const xell_result_t result = xellDestroy(context);
        LogLine("XeLL: context destroyed (" + ResultText(static_cast<int>(result)) + ")");
    }

    // "The function call needs to be done only once after settings change", and
    // "application must ensure any GPU activity is finished before xellSetSleepMode
    // is called" (xell_developer_guide_english.md, Enable Features, Integration
    // Checklist). No frame limit: "Having both, application frame limiter and XeLL
    // active at the same time is discouraged", and the proxy limits none with it.
    bool XessFg::XellSetSleepMode(xell_context_handle_t context, bool lowLatency)
    {
        if (context == nullptr || xellSetSleepMode == nullptr)
            return false;
        xell_sleep_params_t params = {};
        params.bLowLatencyMode = lowLatency ? 1u : 0u;
        params.minimumIntervalUs = 0;
        const xell_result_t result = xellSetSleepMode(context, &params);
        if (result != XELL_RESULT_SUCCESS)
            LogLine("XeLL: xellSetSleepMode failed (" + ResultText(static_cast<int>(result)) + ")");
        return result == XELL_RESULT_SUCCESS;
    }

    xell_result_t XessFg::XellSleep(xell_context_handle_t context, uint32_t frameId)
    {
        return context != nullptr && xellSleep != nullptr ? xellSleep(context, frameId) : XELL_RESULT_ERROR_UNINITIALIZED;
    }

    xell_result_t XessFg::XellMarker(xell_context_handle_t context, uint32_t frameId, xell_latency_marker_type_t marker)
    {
        return context != nullptr && xellAddMarkerData != nullptr ? xellAddMarkerData(context, frameId, marker)
                                                                   : XELL_RESULT_ERROR_UNINITIALIZED;
    }

    HRESULT XessFg::CreateSwapChain(ID3D12Device* device, ID3D12CommandQueue* queue, IDXGIFactory2* factory,
                                    HWND window, const DXGI_SWAP_CHAIN_DESC1& desc,
                                    const DXGI_SWAP_CHAIN_FULLSCREEN_DESC* fullscreen, xell_context_handle_t xell,
                                    bool invertedDepth, xefg::Handle& context, ComPtr<IDXGISwapChain3>& swapChain)
    {
        context = nullptr;
        swapChain.Reset();
        std::lock_guard<std::mutex> lock(mutex);
        if (fgCreateContext == nullptr)
            return E_FAIL;

        // "The function xefgSwapChainD3D12CreateContext will check if the device has
        // the necessary feature capabilities to support XeSS-FG" (Context Creation).
        xefg::Handle created = nullptr;
        xefg::Result result = fgCreateContext(device, &created);
        if (result < 0 || created == nullptr)
        {
            NoteLocked("this GPU or driver does not run it (xefgSwapChainD3D12CreateContext, "
                       + ResultText(result) + ")");
            return E_FAIL;
        }
        if (fgSetLoggingCallback != nullptr)
            fgSetLoggingCallback(created, Config().vendorDiagnostics ? xefg::kLogDebug : xefg::kLogWarning, &OnFgMessage,
                                 nullptr);

        // "XeSS-FG proxy swap chain will fail to initialize without XeLL context."
        result = fgSetLatencyReduction(created, xell);
        if (result < 0)
        {
            fgDestroy(created);
            NoteLocked("XeLL was not accepted (xefgSwapChainSetLatencyReduction, " + ResultText(result) + ")");
            return E_FAIL;
        }

        // One generated frame per rendered: "On non-Intel GPUs the maximum number of
        // generated frames is one" (Requirements), and the proxy's frame rate gate
        // and V-Sync cap count with two frames shown per rendered (FrameGeneration.h,
        // SetPresentCapLocked). UI interpolation, the default (UI Handling); its
        // composition, AUTO's when switched on (xessFgUiComposition).
        xefg::D3d12InitParams params = {};
        params.pApplicationSwapChain = nullptr;
        params.initFlags = invertedDepth ? xefg::kInitInvertedDepth : 0u;
        params.maxInterpolatedFrames = 1;
        params.uiMode = xefg::kUiModeAuto;
        result = fgInitFromSwapChainDesc(created, window, &desc, fullscreen, queue, factory, &params);
        if (result < 0)
        {
            // "initialization failed, destroy the context to start from a clean slate"
            // (Error Handling).
            fgDestroy(created);
            NoteLocked("its swapchain could not be made (xefgSwapChainD3D12InitFromSwapChainDesc, " + ResultText(result)
                       + ")");
            return E_FAIL;
        }

        result = fgGetSwapChainPtr(created, IID_PPV_ARGS(&swapChain));
        if (result < 0 || swapChain == nullptr)
        {
            swapChain.Reset();
            fgDestroy(created);
            NoteLocked("its swapchain has no IDXGISwapChain3 (xefgSwapChainD3D12GetSwapChainPtr, " + ResultText(result)
                       + ")");
            return E_FAIL;
        }

        uint32_t frames = params.maxInterpolatedFrames;
        if (fgGetInitializationParameters != nullptr)
        {
            xefg::D3d12InitParams applied = {};
            if (fgGetInitializationParameters(created, &applied) >= 0)
                frames = applied.maxInterpolatedFrames;
        }
        why = "runs";
        LogLine("XeSS frame generation: swapchain made, " + std::to_string(frames) + " generated frame(s) per rendered at"
                + " most, depth " + (invertedDepth ? "inverted" : "not inverted") + " (" + versions + ")");
        context = created;
        return S_OK;
    }

    // "Calling xefgSwapChainDestroy will release all resources owned by the frame
    // generation context. The function will return an error if the application has
    // any outstanding references to the proxy swap chain object" (Shutdown).
    void XessFg::Destroy(xefg::Handle context)
    {
        if (context == nullptr)
            return;
        std::lock_guard<std::mutex> lock(mutex);
        const xefg::Result result = fgDestroy(context);
        LogLine(result == xefg::kErrorPointerStillInUse
                    ? "XeSS frame generation: its context was destroyed while its swapchain was still referenced"
                    : "XeSS frame generation: context destroyed (" + ResultText(result) + ")");
    }

    xefg::Result XessFg::SetEnabled(xefg::Handle context, bool enabled)
    {
        return fgSetEnabled(context, enabled ? 1u : 0u);
    }

    xefg::Result XessFg::SetPresentId(xefg::Handle context, uint32_t presentId)
    {
        return fgSetPresentId(context, presentId);
    }

    xefg::Result XessFg::TagResource(xefg::Handle context, uint32_t presentId, const xefg::D3d12ResourceData& data)
    {
        // "It is only required if the resource is not valid until the next present
        // call": no command list for XEFG_SWAPCHAIN_RV_UNTIL_NEXT_PRESENT.
        return fgTagFrameResource(context, nullptr, presentId, &data);
    }

    xefg::Result XessFg::TagConstants(xefg::Handle context, uint32_t presentId, const xefg::FrameConstants& constants)
    {
        return fgTagFrameConstants(context, presentId, &constants);
    }

    xefg::Result XessFg::LastPresentStatus(xefg::Handle context, xefg::PresentStatus& status)
    {
        status = {};
        return fgGetLastPresentStatus(context, &status);
    }

    xefg::Result XessFg::SetUiComposition(xefg::Handle context, bool enabled)
    {
        if (fgSetUiCompositionState == nullptr)
            return -7;   // XEFG_SWAPCHAIN_RESULT_ERROR_NOT_IMPLEMENTED
        return fgSetUiCompositionState(context, enabled ? xefg::kUiCompositionEnabled : xefg::kUiCompositionDisabled);
    }

    xefg::Result XessFg::ShowOnlyInterpolated(xefg::Handle context, bool enabled)
    {
        if (fgEnableDebugFeature == nullptr)
            return -7;
        return fgEnableDebugFeature(context, xefg::kDebugShowOnlyInterpolation, enabled ? 1u : 0u, nullptr);
    }
}
