#pragma once

#include <d3d11.h>
#include <d3d12.h>
#include <dxgi.h>
#include <wrl/client.h>

#include <XeSS/xell.h>

#include "BrakeController.h"

#include <atomic>
#include <condition_variable>
#include <cstdint>
#include <deque>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

namespace redefinition
{
    // Lower latency for the game's frames where Streamline's Reflex does not run --
    // frame generation off, FSR's, DLSS Ray Reconstruction alone -- and a measure of
    // it on every GPU.
    //
    // - NVIDIA Reflex through NVAPI on the proxy's Direct3D 12 device, which presents
    //   (nvapi.h, NvAPI_D3D_SetSleepMode, NvAPI_D3D_Sleep, NvAPI_D3D_SetLatencyMarker;
    //   GeForce GTX 900 and later, driver R455). lowLatency in the ini sets its mode.
    // - AMD Anti-Lag 2 (ffx_antilag2_dx12.h / ffx_antilag2_dx11.h, the Anti-Lag 2
    //   SDK; Radeon RX 5000 and later), on the proxy's Direct3D 12 device or Unity's
    //   Direct3D 11 device as antiLag2 asks (12, 11, 0 off). With it on, the brake is
    //   off: "Any other forms of latency reduction ... should be disabled".
    // - Intel XeLL (xell.h, XessFg.h) on the proxy's Direct3D 12 device: always with
    //   XeSS frame generation, which requires it -- "XeSS-FG requires a working XeLL
    //   integration on all GPUs" -- and, where xell asks for it, on an Intel GPU where
    //   no other frame generation's swapchain presents, its mode from lowLatency when
    //   the swapchain is made. Nothing else lowers the latency while its low latency
    //   mode is on: "Latency reduction solutions other than XeLL are not supported and
    //   may cause unexpected behavior" (xess_fg_developer_guide_english.md,
    //   Requirements), "application must disable other latency reduction solutions
    //   when running XeLL on Intel or non-Intel GPUs" (xell_developer_guide_english.md,
    //   Integration Checklist). On a GPU other than Intel's it lowers the latency only
    //   while XeSS-FG generates -- "application must ensure that XeSS-FG is enabled if
    //   running in cross-vendor mode", "If the condition is not fulfilled then XeLL
    //   will not perform Latency reduction" (Integration Checklist, Cross-vendor
    //   support) -- and while XeSS-FG is held off the brake takes over; Reflex and
    //   Anti-Lag 2 stay off under XeSS-FG's swapchain.
    // - The latency brake (Brake), on any GPU, where nothing above lowers the latency.
    // - The measure: from the frame's start on the main thread to the GPU's end of
    //   its Present on the proxy's queue, through a fence of its own, logged every
    //   reportSeconds -- the same on every GPU, so that what lowers it can be told
    //   apart where the vendors' own reports are not there.
    //
    // Streamline brings its own Reflex with DLSS frame generation (Streamline.h), and
    // this then stays out of the way. The frame's beginning, its simulation's end and
    // its first Direct3D call come through the same exports and render event as
    // Streamline's (ManagedBridge.cpp, KspReflexFrameStart; ReflexFrames.cs), its
    // Present from the swapchain proxy. As NVIDIA asks, Sleep and the markers go on
    // every frame whether low latency is on or not -- and as Intel does: "xellSleep
    // and xellAddMarkerData are always called". XeLL's markers are Reflex's, at the
    // same points and with the same values (XELL_SIMULATION_START 0 to
    // XELL_PRESENT_END 5, as NV_LATENCY_MARKER_TYPE).
    class LowLatency
    {
    public:
        static LowLatency& Get();

        // The XeLL context a swapchain proxy made -- null for none -- whether XeSS
        // frame generation's swapchain is the proxy's, whether XeLL's low latency mode
        // was set on (always with XeSS-FG), and whether the GPU is Intel's, where XeLL
        // lowers the latency without XeSS-FG generating.
        struct XellSetup
        {
            // In the order of the comment above.
            xell_context_handle_t context = nullptr;
            bool frameGeneration = false;
            bool lowLatency = false;
            bool intelGpu = false;
        };

        // The swapchain proxy's devices and queue, once its presentation path is
        // ready, whether DLSS frame generation's Reflex runs on it, and its XeLL. The
        // context stays the proxy's, which destroys it after Detach.
        void Attach(const void* owner, ID3D11Device* gameDevice, ID3D12Device* device, ID3D12CommandQueue* queue,
                    const LUID& adapter, bool streamlineReflex, const XellSetup& xell);
        // Lets go of the devices, XeLL's context and Anti-Lag 2's, where owner is the one attached; XeLL's
        // Sleep on the main thread is waited for first.
        void Detach(const void* owner);

        // From the present path under XeSS frame generation's swapchain, after each
        // Present: whether XeSS-FG generates, without which XeLL lowers no latency on
        // a GPU other than Intel's and the brake takes over.
        void NoteXessGenerating(bool generating) { xessGenerating.store(generating); }

        // On the game's main thread where its frame begins, before its input is read:
        // the brake or Anti-Lag 2's delay, Reflex's or XeLL's Sleep, then SIMULATION_START, under
        // the given index -- Streamline's where it gives one -- or one of its own.
        // Returns the index the game's other calls take, 0 where nothing is tracked.
        uint32_t BeginGameFrame(uint32_t streamlineIndex);
        // Before Streamline's Reflex sleep and everything else of the frame's start: the
        // brake's sleep, so that Reflex sleeps, and sends its markers, after it.
        void BrakeBeforeFrame();
        // On the game's main thread where its simulation ends: SIMULATION_END and the measure's stamp.
        void EndGameSimulation(uint32_t frameIndex);
        // On Unity's render thread before the frame's first Direct3D call.
        void OnGameFrameRendered(uint32_t frameIndex);
        // On the presenting thread, around the swapchain's Present: the game's frame
        // rendered last, 0 for none.
        uint32_t TakeGameFrame();
        // Before it: Anti-Lag 2's end of rendering and its data in the swapchain's private data, then
        // RENDERSUBMIT_END and PRESENT_START.
        void BeforePresent(uint32_t frameIndex, IDXGISwapChain* swapChain);
        // After it: PRESENT_END, the measure's fence signalled on the queue for the meter thread, and the
        // report when it is due.
        void AfterPresent(uint32_t frameIndex);

        // The modes as the ini asks, again after it changed.
        void ApplyConfig();

        // What runs, for the settings row and the log: 1 NVIDIA Reflex through NVAPI,
        // 2 Streamline's Reflex with DLSS frame generation, 3 AMD Anti-Lag 2, 4 Intel
        // XeLL, 0 none -- the text says why.
        int Describe(char* buffer, int size) const;

        // Whether XeLL's low latency mode is on, for the present path, which presents
        // with a sync interval of 1 at most then -- "when VSync is enabled, sync
        // interval for IDXGISwapChain::Present must always be 1 when Low Latency is
        // active" -- and for the mod's V-Sync row.
        bool XellActive() const { return xellActive.load(); }

        // The measure's last report: its median and 90th percentile in milliseconds;
        // the frames it took, 0 before one.
        int Measure(double* median, double* percentile90) const;

    private:
        // Only Get makes it.
        LowLatency() = default;

        // A game frame's times on the CPU, in performance counter ticks: its start on the
        // main thread, its simulation's end, the render thread's first call, the
        // Present's start and end.
        struct Stamps
        {
            // In the order of the comment above.
            int64_t start = 0;
            int64_t simulationEnd = 0;
            int64_t rendered = 0;
            int64_t presentStart = 0;
            int64_t presentEnd = 0;
        };

        // A Present the meter thread waits for on the GPU.
        struct Pending
        {
            // The measure's fence value after it, the frame's start, and its stamps for the stages.
            uint64_t fenceValue;
            int64_t start;
            Stamps stamps;
        };

        bool TrackingLocked() const;
        // Whether XeLL's context is attached, which then takes the markers instead of Reflex.
        bool XellLocked() const;
        // Whether XeLL lowers the latency now: its low latency mode on, and on a GPU
        // other than Intel's XeSS-FG generating.
        bool XellReducingLocked() const;
        // Whether Reflex through NVAPI runs on the attached device.
        bool ReflexLocked() const;
        // Whether Anti-Lag 2 is on, on the device the ini asked for and it was initialised on.
        bool AntiLagLocked() const;
        // One latency marker (NV_LATENCY_MARKER_TYPE) for the frame: XeLL's where it is attached -- only for an
        // id its sleep was called for -- otherwise Reflex's; the first failure logged.
        void MarkerLocked(uint32_t frameIndex, int marker);
        void ReportLocked();
        // The id the frame's markers take: Streamline's index moved by the offset where the frame came with one.
        uint32_t IdLocked(uint32_t frameIndex) const;
        void Brake();
        // The hand-over between XeLL and the brake as XeSS-FG generates or is held
        // off on a GPU other than Intel's, in the log at most every kHandOverLogMs.
        void NoteHandOverLocked(bool xellReduces);
        // What Describe reports: the kind and its text, logged where it changes and log is set.
        void SetText(int kind, const std::string& text, bool log = true);
        // The measure's thread: waits for each pending Present's fence and takes the frame's latency and stages.
        void MeterThread();

        // Guards the state below, except what antiLagMutex, xellMutex and textLock guard.
        mutable std::mutex mutex;
        // Anti-Lag 2's contexts, under a lock of their own: their Update sleeps, which
        // must not hold the others up; Attach and Detach take it inside `mutex`.
        std::mutex antiLagMutex;
        // The device Anti-Lag 2 is on, 12 or 11, 0 off, for the main thread's Update.
        std::atomic<int> antiLagOn{ 0 };
        // XeLL's context, for the main thread's Sleep under a lock of its own as Anti-Lag
        // 2's: the Sleep waits, and Detach takes it so that the context is not let go
        // under a call. xell is the same under `mutex`, for the markers.
        std::mutex xellMutex;
        xell_context_handle_t xellSleeping = nullptr;
        xell_context_handle_t xell = nullptr;
        bool xellFrameGeneration = false;
        bool xellLowLatency = false;
        bool xellIntel = false;
        std::atomic<bool> xellActive{ false };
        std::atomic<bool> xessGenerating{ false };
        // The hand-over last seen and last logged (-1 none yet), when, and the changes
        // since.
        int handOverSeen = -1;
        int handOverLogged = -1;
        ULONGLONG handOverLoggedAt = 0;
        unsigned int handOversUnlogged = 0;
        static constexpr ULONGLONG kHandOverLogMs = 10000;
        // Said once; the main thread's, under xellMutex.
        bool xellSleepFailed = false;
        // The frame ids XeLL's sleep was called for, by id modulo 64: "xellSleep must be
        // the first XeLL API function called with a newly assigned frame_id value", so a
        // marker for an id without one -- the frames in flight when the context was made,
        // or after a sleep that failed -- is not sent (MarkerLocked). Said once.
        std::atomic<uint32_t> xellSlept[64] = {};
        bool xellMarkerDropLogged = false;
        const void* owner = nullptr;
        Microsoft::WRL::ComPtr<ID3D12Device> device;
        Microsoft::WRL::ComPtr<ID3D11Device> gameDevice;
        Microsoft::WRL::ComPtr<ID3D12CommandQueue> queue;
        bool streamline = false;
        bool reflex = false;
        bool amd = false;
        bool loggedFailure = false;
        int appliedMode = -1;
        int appliedAntiLag = -1;
        uint32_t lastBegun = 0;
        // Streamline's index to the id given, and whether the frame begun last came
        // with one.
        uint32_t offset = 0;
        bool mapped = false;
        std::atomic<uint32_t> rendered{ 0 };
        uint32_t presented = 0;
        unsigned long long lastReport = 0;
        // Each frame's start, by id, for the measure.
        Stamps stamps[256] = {};
        // The measure's stages of the frames it took, in milliseconds, since the last
        // report: the simulation, from its end to the Present's start, the Present, the
        // Present's end to the GPU's, and the render thread's first call to the Present.
        static constexpr int kLatencyStages = 5;
        std::vector<double> cpuStages[kLatencyStages];

        // The measure's fence, its waiting thread, and what it measured.
        Microsoft::WRL::ComPtr<ID3D12Fence> fence;
        uint64_t fenceValue = 0;
        std::deque<Pending> pending;
        std::vector<double> measured;
        double lastMedian = 0.0;
        double lastPercentile90 = 0.0;
        int lastCount = 0;
        std::condition_variable wake;
        std::thread meter;
        bool meterStarted = false;
        HANDLE meterEvent = nullptr;

        // The brake's state, on the main thread.
        int64_t brakeWaitsBefore = -1;
        int64_t brakeFrameStart = 0;
        BrakeController brake;
        // The waits it counts, as latencyBrakeWaits had them.
        int brakeWaitsKind = 0;
        unsigned long long brakeReport = 0;
        HANDLE brakeTimer = nullptr;
        // A swapchain attached or let go: the brake starts afresh.
        std::atomic<bool> brakeReseed{ true };

        mutable std::mutex textLock;
        int kind = 0;
        std::string text = "no swapchain through the proxy yet";
    };
}
