#pragma once

#include <windows.h>

#include <d3d12.h>
#include <dxgi1_6.h>

#include <sl.h>
#include <sl_dlss_d.h>
#include <sl_dlss_g.h>
#include <sl_pcl.h>
#include <sl_reflex.h>

#include <atomic>
#include <mutex>
#include <shared_mutex>
#include <string>

namespace redefinition
{
    // NVIDIA's DLSS Frame Generation, through Streamline -- NVIDIA's DLLs next to
    // KSP_x64.exe (or in streamlineDirectory), which the player downloads from
    // NVIDIA's release (NvidiaDownloader). Only what the headers in
    // extern/Streamline were taken from runs:
    // Streamline 2.14.16, signed by NVIDIA. NVIDIA documents no compatibility
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
        // The one instance, process-wide like the DLLs.
        static Streamline& Get();

        // Loads sl.interposer.dll and calls slInit with manual hooking, for this
        // owner: DLSS-G, Reflex, PCL and Ray Reconstruction for frame generation,
        // Ray Reconstruction alone otherwise. Not from DllMain: "slInit must NOT be
        // called within DLLMain entry point because that can cause a deadlock"
        // (ProgrammingGuide.md 2.2.1). False while another owner holds it, and on a
        // failure, which is logged with its reason and shows in Describe.
        bool Acquire(const void* owner, bool forFrameGeneration);

        // What runs instead where Streamline does not: FSR's frame generation, or,
        // set up for Ray Reconstruction alone, no Ray Reconstruction. For the notes.
        std::string Instead() const;

        // slShutdown, if this owner holds it -- before the D3D12 device goes: "Call
        // slShutdown() before destroying DXGI, D3D12, or Vulkan instances, devices,
        // and other components" (ProgrammingGuideDLSS_G.md 2.0).
        void Release(const void* owner);

        // Why DLSS frame generation does not run, where the proxy found out itself.
        void Note(const std::string& why);

        // Whether Streamline is of use on the adapter with this LUID, for what Acquire
        // set it up for: DLSS-G, which needs Reflex with it, where frame generation
        // is wanted; DLSS Ray Reconstruction alone where it is not. Logs why not.
        bool Supported(const LUID& adapter);

        // Whether the device set runs DLSS frame generation, not Ray Reconstruction
        // alone.
        bool FrameGenerationAvailable() const { return frameGeneration.load(); }

        // The device every feature is initialised with. Only after it are the
        // feature functions reachable: "Must be called AFTER device is set"
        // (sl_core_api.h, slGetFeatureFunction).
        bool SetDevice(ID3D12Device* nativeDevice);

        // A native D3D12 device or DXGI factory replaced in place by Streamline's
        // proxy, and the native interface behind a proxy.
        bool UpgradeInterface(void** baseInterface, const char* what);
        // The native interface behind one of Streamline's proxies, into native; false (and null) without one.
        bool NativeInterface(void* proxy, void** native);

        // Per frame, by frame index: the constants and tags, the options, and the
        // Reflex and PCL calls. False on a failure, which is logged a limited
        // number of times. The token for an index is fetched right before each
        // call that takes it: Streamline hands out slots of a ring of
        // MAX_FRAMES_IN_FLIGHT (6, sl_consts.h), which other threads asking for
        // other indices move on.
        bool SetConstants(const sl::Constants& constants, uint32_t frameIndex);
        // Frame generation's tags for the frame, all valid until Present, so without a command list.
        bool SetTags(uint32_t frameIndex, const sl::ResourceTag* tags, uint32_t count);
        // DLSS-G's options on frame generation's viewport; false before its functions are fetched.
        bool SetOptions(const sl::DLSSGOptions& options);
        // DLSS-G's state on frame generation's viewport; false before its functions are fetched.
        bool GetState(sl::DLSSGState& frameGenerationState);
        // One PCL marker for the frame, under callMutex shared.
        void Marker(sl::PCLMarker marker, uint32_t frameIndex);
        // Reflex's sleep for the frame, under callMutex shared: returns once Reflex lets the frame begin.
        void ReflexSleep(uint32_t frameIndex);

        // Whether Streamline has its device, without which every call above fails.
        bool DeviceSet() const { return deviceSet.load(); }

        // Frame indices for every token: one count for the game's frames and the
        // presenting thread's own, so that they only rise, as NVAPI asks:
        // "Requires calling NvAPI_D3D_SetLatencyMarker with incrementing
        // frameID" (nvapi.h, NvAPI_D3D_GetLatency). Never 0.
        uint32_t NextFrameIndex();

        // The game's frames (ManagedBridge.cpp, KspReflex*): the main thread
        // begins one -- Reflex's sleep, then eSimulationStart under a new index,
        // which it returns, 0 without Streamline's device -- and ends its
        // simulation; Unity's render thread receives the index before the frame's
        // first Direct3D call, marks eRenderSubmitStart and notes it as the frame
        // in progress; the presenting thread takes it for the frame's Present.
        uint32_t BeginGameFrame();
        // eSimulationEnd for the game's frame, on its main thread; nothing for index 0.
        void EndGameSimulation(uint32_t frameIndex);
        void OnGameFrameRendered(uint32_t frameIndex);
        // The frame Unity's render thread last received, into frameIndex and cleared; false where none waits.
        bool TakeGameFrame(uint32_t& frameIndex);

        // Whether the swapchain presents the game's frames (TakeGameFrame) rather
        // than its own, for the status line. Set by the presenting swapchain.
        void NoteGameFramesPresented(bool game) { gameFramesPresented = game; }
        // What NoteGameFramesPresented last set, read for the status line.
        bool GameFramesPresented() const { return gameFramesPresented.load(); }

        // The viewport of frame generation: the scene.
        static constexpr uint32_t kViewport = 0;
        // DLSS Ray Reconstruction's own viewport (ProgrammingGuideDLSS_RR.md 8.0), so
        // that its constants and tags stay apart from frame generation's.
        static constexpr uint32_t kRayReconstructionViewport = 1;

        // DLSS Ray Reconstruction (sl.dlss_d.dll and nvngx_dlssd.dll beside the other
        // plugins): whether it runs on the device Streamline was given, and why not.
        bool RayReconstructionReady(std::string& why) const;

        // The frame the swapchain has begun, whose token DLSS Ray Reconstruction
        // evaluates under. Set by the presenting swapchain.
        void NoteFrameInProgress(uint32_t frameIndex);
        void ClearGameFrame();

        // The index of the frame in progress (NoteFrameInProgress); without one, a
        // new index each time, from the same count as every other (NextFrameIndex).
        uint32_t FrameInProgressIndex();

        // Ray Reconstruction for this frame, recorded into list: constants, options and
        // tags under its viewport, then the evaluation. The caller restores the list's
        // state afterwards, as NVIDIA's guide says (7.0).
        bool EvaluateRayReconstruction(const sl::Constants& constants, const sl::DLSSDOptions& options,
                                       const sl::ResourceTag* tags, uint32_t count, ID3D12GraphicsCommandList* list);

        // Ray Reconstruction's resources under its viewport freed, once no list that
        // evaluated it is pending on the GPU.
        bool FreeRayReconstruction();

        // What the last attempt came to, for the status line.
        std::string Describe() const;

    private:
        // Only Get makes it.
        Streamline() = default;

        // sl.interposer.dll loaded and slInit called with the features Acquire asked for; true once done.
        bool Initialise();
        // slShutdown with no Reflex or PCL call inside the plugins, and every feature function forgotten.
        void Shutdown();
        // The PC latency ping, picked up where the game's main thread takes its
        // messages (OnGetMessage in Streamline.cpp).
        void InstallPingHook();
        // The WH_GETMESSAGE hook on the game's main thread: a removed message that is PCL's ping becomes Ping.
        static LRESULT CALLBACK OnGetMessage(int code, WPARAM wParam, LPARAM lParam);
        void Ping();
        // Marker and ReflexSleep, for a caller that holds callMutex.
        void MarkerLocked(sl::PCLMarker marker, uint32_t frameIndex);
        void ReflexSleepLocked(uint32_t frameIndex);
        // The token for frameIndex handed to call, under tokenMutex: "This method
        // is NOT thread safe" (sl_core_api.h, slGetNewFrameToken).
        template <typename Call>
        bool WithToken(uint32_t frameIndex, const char* what, Call&& call);
        // sl.interposer.dll from streamlineDirectory or beside the executable, only as version 2.14.16 and
        // signed by NVIDIA, with every function this build calls.
        bool LoadModule();
        // What Describe reports, logged where log is set.
        void SetState(const std::string& text, bool log);
        // Whether the call succeeded; a failure is logged, the first 20 per start.
        bool Check(sl::Result result, const char* call);

        // An export of sl.interposer.dll into target; false where it lacks it.
        template <typename T>
        bool Import(T*& target, const char* name);
        // A feature's function from slGetFeatureFunction into target; false (and null) where it has none.
        template <typename T>
        bool ImportFeature(T*& target, sl::Feature feature, const char* name);

        // Streamline's log callback: its lines into the proxy's log, repeats counted, information capped.
        static void OnLogMessage(sl::LogType type, const char* message);

        // What Describe reports, under its own mutex.
        mutable std::mutex stateMutex;
        std::string state = "not tried";

        // sl.interposer.dll once loaded; below it the owner, whether slInit and the device ran, and the failures logged.
        HMODULE module = nullptr;
        // The folder sl.interposer.dll came from, where its plugins are looked for.
        std::wstring pluginFolder;
        const void* owner = nullptr;
        bool initialised = false;
        std::atomic<bool> deviceSet{ false };
        std::atomic<int> failedCallsLogged{ 0 };

        // Held shared by the Reflex and PCL calls, which come from the game's main
        // thread too, and exclusively by Shutdown, which unloads the plugins they
        // run in.
        std::shared_mutex callMutex;
        std::mutex tokenMutex;

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
        PFun_slFreeResources* slFreeResources = nullptr;

        PFun_slDLSSGSetOptions* slDLSSGSetOptions = nullptr;
        PFun_slDLSSGGetState* slDLSSGGetState = nullptr;
        PFun_slReflexSetOptions* slReflexSetOptions = nullptr;
        PFun_slReflexSleep* slReflexSleep = nullptr;
        PFun_slPCLSetMarker* slPCLSetMarker = nullptr;
        PFun_slPCLGetState* slPCLGetState = nullptr;
        PFun_slEvaluateFeature* slEvaluateFeature = nullptr;
        PFun_slDLSSDSetOptions* slDLSSDSetOptions = nullptr;
        std::atomic<bool> rayReconstruction{ false };
        std::atomic<bool> frameGeneration{ false };
        // What the owner set Streamline up for (Acquire).
        bool frameGenerationWanted = true;
        std::string rayReconstructionWhy = "Streamline not set up";
        std::atomic<int64_t> frameInProgress{ -1 };

        std::atomic<uint32_t> frameCounter{ 0 };
        // The game's frame on its main thread, and the last one Unity's render
        // thread received, until the presenting thread takes it.
        std::atomic<uint32_t> gameFrame{ 0 };
        std::atomic<uint32_t> renderedGameFrame{ 0 };
        std::atomic<bool> gameFramesPresented{ false };
        // PCLState::statsWindowMessage, 0 while unknown.
        std::atomic<uint32_t> pingMessage{ 0 };
        std::atomic<bool> pingSeen{ false };
    };
}
