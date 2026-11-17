#pragma once

#include <d3d11.h>
#include <d3d12.h>

#include <atomic>
#include <mutex>
#include <string>

// NVIDIA Nsight Aftermath's GPU crash dumps, for test runs: which work the GPU was
// on when the driver reset it. Only with the game's command line switch
// -redefinitionGpuCrashDump, and only in a proxy built with the SDK
// (REDEFINITION_AFTERMATH_SDK in CMakeLists.txt); the SDK's DLL
// (GFSDK_Aftermath_Lib.x64.dll) is loaded from the game's folder at run time.
//
// The GPU profiler's markers (GpuProfiler::OnMarker) are Aftermath event markers on
// Unity's immediate context as well, so the dump names the last marker the GPU
// passed. On Direct3D 11 the driver records them only while NVIDIA's "Nsight
// Aftermath GPU Crash Dump Monitor" runs on the machine (GFSDK_Aftermath.h,
// FeatureFlags_EnableMarkers). A dump is written next to the game as
// ReDefinitionProxy-gpucrash-<time>.nv-gpudmp with its decoded .json, and the
// proxy's log says the device's status, the markers and the active shaders.
namespace redefinition
{
    class GpuCrashDump
    {
    public:
        // The one instance, never destroyed.
        static GpuCrashDump& Get();

        // From the first CreateDXGIFactory*, before Unity makes its device: the switch
        // read, the DLL loaded and crash dumps enabled. Nothing after the first call.
        void EnableEarly();

        // Unity's device and immediate context, and the proxy's own Direct3D 12 device:
        // Aftermath initialised on both, the context's marker handle made.
        void Attach(ID3D11Device* device, ID3D11DeviceContext* context, ID3D12Device* d3d12Device);

        // On Unity's render thread, from the GPU profiler's marker id.
        void Marker(int id);

        // Whether dumps are enabled in this process.
        bool Active() const { return active.load(); }

    private:
        GpuCrashDump() = default;

        // The dump's callback, on Aftermath's thread: the file written and decoded into the log.
        void OnDump(const void* data, unsigned size);

        // EnableEarly's guard: another thread waits until dumps are on before it goes on to make a device;
        // recursive, as the SDK may call CreateDXGIFactory* on the same thread while it is being enabled.
        std::recursive_mutex enableMutex;
        bool started = false;
        std::atomic<bool> active{ false };
        std::atomic<bool> markersOn{ false };
        // GFSDK_Aftermath_ContextHandle of Unity's immediate context, as the SDK's opaque pointer.
        std::atomic<void*> contextHandle{ nullptr };

        friend struct GpuCrashDumpCallbacks;
    };
}
