#include "GpuCrashDump.h"

#include "FileUtil.h"
#include "GpuAddressBook.h"
#include "GpuProfiler.h"
#include "Log.h"

#include <windows.h>

#include <cstdint>
#include <cstdio>
#include <cstring>
#include <cwchar>
#include <string>
#include <vector>

#if defined(REDEFINITION_AFTERMATH)
#include <GFSDK_Aftermath.h>
#include <GFSDK_Aftermath_GpuCrashDump.h>
#include <GFSDK_Aftermath_GpuCrashDumpDecoding.h>
#endif

namespace redefinition
{
    GpuCrashDump& GpuCrashDump::Get()
    {
        // Never destroyed, as GpuProfiler::Get.
        static GpuCrashDump* instance = new GpuCrashDump();
        return *instance;
    }

#if !defined(REDEFINITION_AFTERMATH)

    void GpuCrashDump::EnableEarly()
    {
        std::lock_guard<std::recursive_mutex> lock(enableMutex);
        if (started)
            return;
        started = true;
        const wchar_t* line = GetCommandLineW();
        if (line != nullptr && wcsstr(line, L"-redefinitionGpuCrashDump") != nullptr)
            LogLine("GPU crash dumps: asked for, but this dxgi.dll was built without the Nsight Aftermath SDK");
    }

    void GpuCrashDump::Attach(ID3D11Device*, ID3D11DeviceContext*, ID3D12Device*) {}
    void GpuCrashDump::Marker(int) {}
    void GpuCrashDump::OnDump(const void*, unsigned) {}

#else

    namespace
    {
        // The SDK's entry points, read from its DLL (the proxy does not link it).
        struct Api
        {
            decltype(&GFSDK_Aftermath_EnableGpuCrashDumps) enableGpuCrashDumps = nullptr;
            decltype(&GFSDK_Aftermath_DX11_Initialize) dx11Initialize = nullptr;
            decltype(&GFSDK_Aftermath_DX12_Initialize) dx12Initialize = nullptr;
            decltype(&GFSDK_Aftermath_DX11_CreateContextHandle) dx11CreateContextHandle = nullptr;
            decltype(&GFSDK_Aftermath_SetEventMarker) setEventMarker = nullptr;
            decltype(&GFSDK_Aftermath_GpuCrashDump_CreateDecoder) createDecoder = nullptr;
            decltype(&GFSDK_Aftermath_GpuCrashDump_DestroyDecoder) destroyDecoder = nullptr;
            decltype(&GFSDK_Aftermath_GpuCrashDump_GetDeviceInfo) getDeviceInfo = nullptr;
            decltype(&GFSDK_Aftermath_GpuCrashDump_GetPageFaultInfo) getPageFaultInfo = nullptr;
            decltype(&GFSDK_Aftermath_GpuCrashDump_GetActiveShadersInfoCount) getActiveShadersInfoCount = nullptr;
            decltype(&GFSDK_Aftermath_GpuCrashDump_GetActiveShadersInfo) getActiveShadersInfo = nullptr;
            decltype(&GFSDK_Aftermath_GpuCrashDump_GetEventMarkersInfoCount) getEventMarkersInfoCount = nullptr;
            decltype(&GFSDK_Aftermath_GpuCrashDump_GetEventMarkersInfo) getEventMarkersInfo = nullptr;
            decltype(&GFSDK_Aftermath_GpuCrashDump_GenerateJSON) generateJson = nullptr;
            decltype(&GFSDK_Aftermath_GpuCrashDump_GetJSON) getJson = nullptr;
        };

        Api g_api;

        template <typename T>
        bool Load(HMODULE module, const char* name, T& out)
        {
            out = reinterpret_cast<T>(GetProcAddress(module, name));
            return out != nullptr;
        }

        std::string ResultText(GFSDK_Aftermath_Result result)
        {
            char text[32];
            FormatTo(text, "0x%08X", static_cast<unsigned>(result));
            return text;
        }

        const char* DeviceStatusText(GFSDK_Aftermath_Device_Status status)
        {
            switch (status)
            {
            case GFSDK_Aftermath_Device_Status_Active: return "active";
            case GFSDK_Aftermath_Device_Status_Timeout: return "timeout (the GPU did not finish its work in time: a hang)";
            case GFSDK_Aftermath_Device_Status_OutOfMemory: return "out of memory";
            case GFSDK_Aftermath_Device_Status_PageFault: return "page fault (a read or write outside any resource)";
            case GFSDK_Aftermath_Device_Status_Stopped: return "stopped";
            case GFSDK_Aftermath_Device_Status_Reset: return "reset";
            case GFSDK_Aftermath_Device_Status_DmaFault: return "DMA fault";
            case GFSDK_Aftermath_Device_Status_DeviceRemovedNoGpuFault: return "removed without a GPU fault";
            default: return "unknown";
            }
        }

        const char* ContextStatusText(GFSDK_Aftermath_Context_Status status)
        {
            switch (status)
            {
            case GFSDK_Aftermath_Context_Status_NotStarted: return "not started";
            case GFSDK_Aftermath_Context_Status_Executing: return "executing";
            case GFSDK_Aftermath_Context_Status_Finished: return "finished";
            default: return "invalid";
            }
        }

        const char* ShaderTypeText(GFSDK_Aftermath_ShaderType type)
        {
            switch (type)
            {
            case GFSDK_Aftermath_ShaderType_Vertex: return "vertex";
            case GFSDK_Aftermath_ShaderType_Hull: return "hull";
            case GFSDK_Aftermath_ShaderType_Domain: return "domain";
            case GFSDK_Aftermath_ShaderType_Geometry: return "geometry";
            case GFSDK_Aftermath_ShaderType_Pixel: return "pixel";
            case GFSDK_Aftermath_ShaderType_Compute: return "compute";
            default: return "other";
            }
        }

        // A marker's id travels as the marker's pointer value (markerDataSize 0, so the
        // driver copies nothing per marker); ids start at 0, so the value is id + 1.
        const void* MarkerPointer(int id)
        {
            return reinterpret_cast<const void*>(static_cast<uintptr_t>(id) + 1);
        }

        int MarkerId(const void* pointer)
        {
            return static_cast<int>(reinterpret_cast<uintptr_t>(pointer)) - 1;
        }

        // "id <n>: <label>", what a resolved marker carries into the dump.
        std::string MarkerText(int id)
        {
            return "id " + std::to_string(id) + ": " + Narrow(GpuProfiler::Get().Label(id));
        }

        // The id back out of a resolved marker's text, or -1.
        int IdOfText(const std::string& text)
        {
            int id = -1;
            if (sscanf_s(text.c_str(), "id %d:", &id) != 1)
                return -1;
            return id;
        }

        std::wstring StampedPath(const wchar_t* extension)
        {
            SYSTEMTIME now = {};
            GetLocalTime(&now);
            wchar_t name[96];
            swprintf_s(name, L"\\ReDefinitionProxy-gpucrash-%04u%02u%02u-%02u%02u%02u%s", now.wYear, now.wMonth, now.wDay,
                       now.wHour, now.wMinute, now.wSecond, extension);
            return ExecutableDirectory() + name;
        }

        bool WriteFile(const std::wstring& path, const void* data, size_t size)
        {
            FILE* file = nullptr;
            if (_wfopen_s(&file, path.c_str(), L"wb") != 0 || file == nullptr)
                return false;
            const bool ok = std::fwrite(data, 1, size, file) == size;
            std::fclose(file);
            return ok;
        }
    }

    // The SDK's callbacks: plain functions handing over to the instance.
    struct GpuCrashDumpCallbacks
    {
        static void GFSDK_AFTERMATH_CALL Dump(const void* data, const uint32_t size, void*)
        {
            GpuCrashDump::Get().OnDump(data, size);
        }

        static void GFSDK_AFTERMATH_CALL Description(PFN_GFSDK_Aftermath_AddGpuCrashDumpDescription add, void*)
        {
            add(GFSDK_Aftermath_GpuCrashDumpDescriptionKey_ApplicationName, "KSP with ReDefinition");
        }

        // A zero-sized marker gets its text (MarkerText) while the dump is made.
        static void GFSDK_AFTERMATH_CALL Resolve(const void* data, const uint32_t size, void*,
                                                 PFN_GFSDK_Aftermath_ResolveMarker resolve)
        {
            if (size != 0)
                return;
            const std::string text = MarkerText(MarkerId(data));
            resolve(text.c_str(), static_cast<uint32_t>(text.size() + 1));
        }
    };

    void GpuCrashDump::EnableEarly()
    {
        // Held until dumps are on, so that no other thread makes a device before; the same thread passes again.
        std::lock_guard<std::recursive_mutex> lock(enableMutex);
        if (started)
            return;
        started = true;
        const wchar_t* line = GetCommandLineW();
        if (line == nullptr || wcsstr(line, L"-redefinitionGpuCrashDump") == nullptr)
            return;

        const std::wstring path = ExecutableDirectory() + L"\\GFSDK_Aftermath_Lib.x64.dll";
        HMODULE module = LoadLibraryW(path.c_str());
        if (module == nullptr)
        {
            LogLine("GPU crash dumps: " + Narrow(path) + " could not be loaded (" + std::to_string(GetLastError()) + ")");
            return;
        }
        const bool loaded = Load(module, "GFSDK_Aftermath_EnableGpuCrashDumps", g_api.enableGpuCrashDumps)
                            && Load(module, "GFSDK_Aftermath_DX11_Initialize", g_api.dx11Initialize)
                            && Load(module, "GFSDK_Aftermath_DX12_Initialize", g_api.dx12Initialize)
                            && Load(module, "GFSDK_Aftermath_DX11_CreateContextHandle", g_api.dx11CreateContextHandle)
                            && Load(module, "GFSDK_Aftermath_SetEventMarker", g_api.setEventMarker)
                            && Load(module, "GFSDK_Aftermath_GpuCrashDump_CreateDecoder", g_api.createDecoder)
                            && Load(module, "GFSDK_Aftermath_GpuCrashDump_DestroyDecoder", g_api.destroyDecoder)
                            && Load(module, "GFSDK_Aftermath_GpuCrashDump_GetDeviceInfo", g_api.getDeviceInfo)
                            && Load(module, "GFSDK_Aftermath_GpuCrashDump_GetPageFaultInfo", g_api.getPageFaultInfo)
                            && Load(module, "GFSDK_Aftermath_GpuCrashDump_GetActiveShadersInfoCount", g_api.getActiveShadersInfoCount)
                            && Load(module, "GFSDK_Aftermath_GpuCrashDump_GetActiveShadersInfo", g_api.getActiveShadersInfo)
                            && Load(module, "GFSDK_Aftermath_GpuCrashDump_GetEventMarkersInfoCount", g_api.getEventMarkersInfoCount)
                            && Load(module, "GFSDK_Aftermath_GpuCrashDump_GetEventMarkersInfo", g_api.getEventMarkersInfo)
                            && Load(module, "GFSDK_Aftermath_GpuCrashDump_GenerateJSON", g_api.generateJson)
                            && Load(module, "GFSDK_Aftermath_GpuCrashDump_GetJSON", g_api.getJson);
        if (!loaded)
        {
            LogLine("GPU crash dumps: the Aftermath DLL lacks an entry point this proxy was built against");
            return;
        }

        const GFSDK_Aftermath_Result result = g_api.enableGpuCrashDumps(
            GFSDK_Aftermath_Version_API, GFSDK_Aftermath_GpuCrashDumpWatchedApiFlags_DX,
            GFSDK_Aftermath_GpuCrashDumpFeatureFlags_Default, &GpuCrashDumpCallbacks::Dump, nullptr,
            &GpuCrashDumpCallbacks::Description, &GpuCrashDumpCallbacks::Resolve, nullptr);
        if (!GFSDK_Aftermath_SUCCEED(result))
        {
            LogLine("GPU crash dumps: EnableGpuCrashDumps failed (" + ResultText(result) + ")");
            return;
        }
        active.store(true);
        LogLine("GPU crash dumps: on (Nsight Aftermath, " + FileVersion(path) + ")");
    }

    void GpuCrashDump::Attach(ID3D11Device* device, ID3D11DeviceContext* context, ID3D12Device* d3d12Device)
    {
        if (!active.load())
            return;
        // Markers, the resources a page fault lands in and the shader errors the GPU can report (no cost); call stacks only with -redefinitionGpuCrashDumpStacks (their CPU
        // cost is in every draw: a marker with the CPU's call stack at each draw and dispatch, which names the call that faulted).
        const wchar_t* line = GetCommandLineW();
        const bool stacks = line != nullptr && wcsstr(line, L"-redefinitionGpuCrashDumpStacks") != nullptr;
        const uint32_t flags = GFSDK_Aftermath_FeatureFlags_EnableMarkers | GFSDK_Aftermath_FeatureFlags_EnableResourceTracking
                               | GFSDK_Aftermath_FeatureFlags_EnableShaderErrorReporting | (stacks ? GFSDK_Aftermath_FeatureFlags_CallStackCapturing : 0u);
        if (device != nullptr)
        {
            const GFSDK_Aftermath_Result result = g_api.dx11Initialize(GFSDK_Aftermath_Version_API, flags, device);
            if (GFSDK_Aftermath_SUCCEED(result) || result == GFSDK_Aftermath_Result_FAIL_AlreadyInitialized)
            {
                GFSDK_Aftermath_ContextHandle handle = nullptr;
                const GFSDK_Aftermath_Result made = context != nullptr ? g_api.dx11CreateContextHandle(context, &handle)
                                                                       : GFSDK_Aftermath_Result_Fail;
                if (GFSDK_Aftermath_SUCCEED(made) && handle != nullptr)
                {
                    contextHandle.store(handle);
                    markersOn.store(true);
                }
                LogLine("GPU crash dumps: Direct3D 11 device initialised (" + ResultText(result) + "), markers "
                        + (markersOn.load() ? "on" : "off (" + ResultText(made) + ")") + (stacks ? ", call stacks on" : ""));
            }
            else
                LogLine("GPU crash dumps: DX11_Initialize failed (" + ResultText(result) + ")");
        }
        if (d3d12Device != nullptr)
        {
            const GFSDK_Aftermath_Result result = g_api.dx12Initialize(GFSDK_Aftermath_Version_API,
                                                                       GFSDK_Aftermath_FeatureFlags_EnableResourceTracking, d3d12Device);
            LogLine("GPU crash dumps: Direct3D 12 device " + std::string(GFSDK_Aftermath_SUCCEED(result) ? "initialised" : "not initialised")
                    + " (" + ResultText(result) + ")");
        }
    }

    void GpuCrashDump::Marker(int id)
    {
        if (!markersOn.load())
            return;
        const GFSDK_Aftermath_Result result =
            g_api.setEventMarker(static_cast<GFSDK_Aftermath_ContextHandle>(contextHandle.load()), MarkerPointer(id), 0);
        // The first marker's answer and the first failure, once each: a dump without markers is otherwise silent on why.
        static std::atomic<bool> firstSaid{ false };
        static std::atomic<bool> failureSaid{ false };
        if (!firstSaid.exchange(true))
            LogLine("GPU crash dumps: the first event marker (id " + std::to_string(id) + ") answered " + ResultText(result));
        if (!GFSDK_Aftermath_SUCCEED(result) && !failureSaid.exchange(true))
            LogLine("GPU crash dumps: an event marker failed (" + ResultText(result) + ", id " + std::to_string(id) + ")");
    }

    void GpuCrashDump::OnDump(const void* data, unsigned size)
    {
        const std::wstring dumpPath = StampedPath(L".nv-gpudmp");
        LogLine("GPU crash dumps: a GPU crash, " + std::to_string(size) + " bytes, "
                + (WriteFile(dumpPath, data, size) ? "written to " + Narrow(dumpPath) : std::string("could NOT be written")));

        GFSDK_Aftermath_GpuCrashDump_Decoder decoder = nullptr;
        if (!GFSDK_Aftermath_SUCCEED(g_api.createDecoder(GFSDK_Aftermath_Version_API, data, size, &decoder)))
        {
            LogLine("GPU crash dumps: the dump could not be decoded");
            return;
        }

        GFSDK_Aftermath_GpuCrashDump_DeviceInfo device = {};
        if (GFSDK_Aftermath_SUCCEED(g_api.getDeviceInfo(decoder, &device)))
            LogLine(std::string("GPU crash dumps: device status ") + DeviceStatusText(device.status));

        GFSDK_Aftermath_GpuCrashDump_PageFaultInfo fault = {};
        if (GFSDK_Aftermath_SUCCEED(g_api.getPageFaultInfo(decoder, &fault)))
        {
            char text[128];
            FormatTo(text, "GPU crash dumps: page fault at GPU address 0x%016llX, %u resource(s) there",
                     static_cast<unsigned long long>(fault.faultingGpuVA), fault.resourceInfoCount);
            LogLine(text);
            GpuAddressBook::Explain(fault.faultingGpuVA);
        }

        // The last marker the GPU passed on each context, and the profiler's next one after it in the last
        // complete frame: the work between the two is what the GPU was on.
        uint32_t markerCount = 0;
        const GFSDK_Aftermath_Result countResult = g_api.getEventMarkersInfoCount(decoder, &markerCount);
        LogLine("GPU crash dumps: event markers in the dump: " + std::to_string(markerCount) + " (" + ResultText(countResult) + ")");
        if (GFSDK_Aftermath_SUCCEED(countResult) && markerCount > 0)
        {
            std::vector<GFSDK_Aftermath_GpuCrashDump_EventMarkerInfo> markers(markerCount);
            if (GFSDK_Aftermath_SUCCEED(g_api.getEventMarkersInfo(decoder, markerCount, markers.data())))
            {
                for (const GFSDK_Aftermath_GpuCrashDump_EventMarkerInfo& marker : markers)
                {
                    std::string text;
                    if (marker.markerDataSize > 0 && marker.markerData != nullptr)
                        text.assign(static_cast<const char*>(marker.markerData),
                                    strnlen(static_cast<const char*>(marker.markerData), marker.markerDataSize));
                    else
                        text = MarkerText(MarkerId(marker.markerData));
                    const int id = IdOfText(text);
                    const int next = id >= 0 ? GpuProfiler::Get().NextAfter(id) : -1;
                    LogLine("GPU crash dumps: context " + std::to_string(marker.contextId) + " " + ContextStatusText(marker.contextStatus)
                            + ", last marker passed: " + text
                            + (next >= 0 ? "; the work after it ends at the marker " + MarkerText(next) : std::string("; no marker after it in the last frame")));
                }
            }
        }
        else
            LogLine("GPU crash dumps: no event markers in the dump (on Direct3D 11 the driver records them only while the Nsight Aftermath GPU Crash Dump Monitor runs)");

        uint32_t shaderCount = 0;
        if (GFSDK_Aftermath_SUCCEED(g_api.getActiveShadersInfoCount(decoder, &shaderCount)) && shaderCount > 0)
        {
            std::vector<GFSDK_Aftermath_GpuCrashDump_ShaderInfo> shaders(shaderCount);
            if (GFSDK_Aftermath_SUCCEED(g_api.getActiveShadersInfo(decoder, shaderCount, shaders.data())))
            {
                for (const GFSDK_Aftermath_GpuCrashDump_ShaderInfo& shader : shaders)
                {
                    char text[160];
                    FormatTo(text, "GPU crash dumps: active %s shader, hash 0x%016llX%s", ShaderTypeText(shader.shaderType),
                             static_cast<unsigned long long>(shader.shaderHash), shader.isInternal ? " (the driver's own)" : "");
                    LogLine(text);
                }
            }
        }

        uint32_t jsonSize = 0;
        if (GFSDK_Aftermath_SUCCEED(g_api.generateJson(decoder, GFSDK_Aftermath_GpuCrashDumpDecoderFlags_ALL_INFO,
                                                       GFSDK_Aftermath_GpuCrashDumpFormatterFlags_UTF8_OUTPUT, nullptr, nullptr,
                                                       nullptr, nullptr, &jsonSize))
            && jsonSize > 0)
        {
            std::vector<char> json(jsonSize);
            if (GFSDK_Aftermath_SUCCEED(g_api.getJson(decoder, jsonSize, json.data())))
            {
                const std::wstring jsonPath = StampedPath(L".json");
                const size_t length = strnlen(json.data(), json.size());
                LogLine("GPU crash dumps: the decoded dump " + (WriteFile(jsonPath, json.data(), length)
                                                                   ? "written to " + Narrow(jsonPath)
                                                                   : std::string("could NOT be written")));
            }
        }
        g_api.destroyDecoder(decoder);
    }

#endif
}
