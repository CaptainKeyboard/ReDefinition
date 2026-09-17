#pragma once

#include <windows.h>

#include <d3d12.h>
#include <dxgi1_6.h>

#include <sl.h>
#include <sl_dlss_g.h>
#include <sl_pcl.h>
#include <sl_reflex.h>

#include <atomic>
#include <mutex>
#include <string>

namespace ksp
{
    // NVIDIA's DLSS Frame Generation, through Streamline -- NVIDIA's DLLs next to
    // KSP_x64.exe (or in streamlineDirectory), which the player downloads from
    // NVIDIA's release (NvidiaDownloader). Only what the headers in
    // extern/Streamline were taken from runs:
    // Streamline 2.14.1, signed by NVIDIA. NVIDIA documents no compatibility
    // between versions, and slInit is handed the version these headers declare.
    //
    // "Manual hooking" (ProgrammingGuideManualHooking.md): nothing in the process
    // is interposed. The proxy upgrades its own D3D12 device and DXGI factory to
    // Streamline's proxies and creates the command queue and the swapchain through
    // them; everything else keeps the native device. DLSS-G does not run on
    // D3D11, which is why this lives on the proxy's D3D12 side.
    //
    // Process-wide, like the DLLs, and one swapchain's at a time: the swapchain
    // that sets it up owns it until it goes, and the next one sets it up again. A
    // swapchain made while another owns it presents without it -- Streamline takes
    // one device ("Plugins already initialized" otherwise, sl.api), and its
    // shutdown would unload DLSS-G under the owner.
    class Streamline
    {
    public:
        static Streamline& Get();

        // Loads sl.interposer.dll and calls slInit with manual hooking, DLSS-G,
        // Reflex and PCL, for this owner. Not from DllMain: "slInit must NOT be
        // called within DLLMain entry point because that can cause a deadlock"
        // (ProgrammingGuide.md 2.2.1). False while another owner holds it, and on a
        // failure, which is logged with its reason and shows in Describe.
        bool Acquire(const void* owner);

        // slShutdown, if this owner holds it -- before the D3D12 device goes: "Call
        // slShutdown() before destroying DXGI, D3D12, or Vulkan instances, devices,
        // and other components" (ProgrammingGuideDLSS_G.md 2.0).
        void Release(const void* owner);

        // Why DLSS frame generation does not run, where the proxy found out itself.
        void Note(const std::string& why);

        // Whether DLSS-G and Reflex run on the adapter with this LUID. Logs why
        // not.
        bool FrameGenerationSupported(const LUID& adapter);

        // The device every feature is initialised with. Only after it are the
        // feature functions reachable: "Must be called AFTER device is set"
        // (sl_core_api.h, slGetFeatureFunction).
        bool SetDevice(ID3D12Device* nativeDevice);

        // A native D3D12 device or DXGI factory replaced in place by Streamline's
        // proxy, and the native interface behind a proxy.
        bool UpgradeInterface(void** baseInterface, const char* what);
        bool NativeInterface(void* proxy, void** native);

        // Per frame: the token, the constants and tags for it, the options, and
        // the Reflex and PCL calls around Present. Null or false on a failure,
        // which is logged a limited number of times.
        sl::FrameToken* NewFrameToken(uint32_t frameIndex);
        bool SetConstants(const sl::Constants& constants, const sl::FrameToken& frame);
        bool SetTags(const sl::FrameToken& frame, const sl::ResourceTag* tags, uint32_t count);
        bool SetOptions(const sl::DLSSGOptions& options);
        bool GetState(sl::DLSSGState& frameGenerationState);
        void Marker(sl::PCLMarker marker, const sl::FrameToken& frame);
        void ReflexSleep(const sl::FrameToken& frame);

        // The one viewport every call uses: the scene.
        static constexpr uint32_t kViewport = 0;

        // What the last attempt came to, for the status line.
        std::string Describe() const;

    private:
        Streamline() = default;

        bool Initialise();
        void Shutdown();
        bool LoadModule();
        void SetState(const std::string& text, bool log);
        bool Check(sl::Result result, const char* call);

        template <typename T>
        bool Import(T*& target, const char* name);
        template <typename T>
        bool ImportFeature(T*& target, sl::Feature feature, const char* name);

        static void OnLogMessage(sl::LogType type, const char* message);

        mutable std::mutex stateMutex;
        std::string state = "not tried";

        HMODULE module = nullptr;
        // The folder sl.interposer.dll came from, where its plugins are looked for.
        std::wstring pluginFolder;
        const void* owner = nullptr;
        bool initialised = false;
        bool deviceSet = false;
        int failedCallsLogged = 0;

        PFun_slInit* slInit = nullptr;
        PFun_slShutdown* slShutdown = nullptr;
        PFun_slIsFeatureSupported* slIsFeatureSupported = nullptr;
        PFun_slSetD3DDevice* slSetD3DDevice = nullptr;
        PFun_slUpgradeInterface* slUpgradeInterface = nullptr;
        PFun_slGetNativeInterface* slGetNativeInterface = nullptr;
        PFun_slGetFeatureFunction* slGetFeatureFunction = nullptr;
        PFun_slGetNewFrameToken* slGetNewFrameToken = nullptr;
        PFun_slSetConstants* slSetConstants = nullptr;
        PFun_slSetTagForFrame* slSetTagForFrame = nullptr;
        PFun_slGetFeatureVersion* slGetFeatureVersion = nullptr;

        PFun_slDLSSGSetOptions* slDLSSGSetOptions = nullptr;
        PFun_slDLSSGGetState* slDLSSGGetState = nullptr;
        PFun_slReflexSetOptions* slReflexSetOptions = nullptr;
        PFun_slReflexSleep* slReflexSleep = nullptr;
        PFun_slPCLSetMarker* slPCLSetMarker = nullptr;
    };
}
