#pragma once

#include <d3d11.h>
#include <d3d12.h>
#include <dxgi.h>
#include <wrl/client.h>

#include <XeSS/xell.h>

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
    //   integration on all GPUs" -- and on an Intel GPU where no other frame
    //   generation's swapchain presents, its mode from lowLatency when the swapchain
    //   is made. Nothing else lowers the latency with it: "Latency reduction solutions
    //   other than XeLL are not supported and may cause unexpected behavior"
    //   (xess_fg_developer_guide_english.md, Requirements), "application must disable
    //   other latency reduction solutions when running XeLL on Intel or non-Intel
    //   GPUs" (xell_developer_guide_english.md, Integration Checklist).
    // - The latency brake (Brake), on any GPU.
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

        // The swapchain proxy's devices and queue, once its presentation path is
        // ready, whether DLSS frame generation's Reflex runs on it, and the XeLL
        // context it made, with XeSS frame generation or without -- null for none. The
        // context stays the proxy's, which destroys it after Detach.
        void Attach(const void* owner, ID3D11Device* gameDevice, ID3D12Device* device, ID3D12CommandQueue* queue,
                    const LUID& adapter, bool streamlineReflex, xell_context_handle_t xell, bool xessFrameGeneration);
        void Detach(const void* owner);

        // On the game's main thread where its frame begins, before its input is read:
        // the brake or Anti-Lag 2's delay, Reflex's or XeLL's Sleep, then SIMULATION_START, under
        // the given index -- Streamline's where it gives one -- or one of its own.
        // Returns the index the game's other calls take, 0 where nothing is tracked.
        uint32_t BeginGameFrame(uint32_t streamlineIndex);
        void EndGameSimulation(uint32_t frameIndex);
        // On Unity's render thread before the frame's first Direct3D call.
        void OnGameFrameRendered(uint32_t frameIndex);
        // On the presenting thread, around the swapchain's Present: the game's frame
        // rendered last, 0 for none.
        uint32_t TakeGameFrame();
        void BeforePresent(uint32_t frameIndex, IDXGISwapChain* swapChain);
        void AfterPresent(uint32_t frameIndex);

        // The modes as the ini asks, again after it changed.
        void ApplyConfig();

        // What runs, for the settings row and the log: 1 NVIDIA Reflex through NVAPI,
        // 2 Streamline's Reflex with DLSS frame generation, 3 AMD Anti-Lag 2, 4 Intel
        // XeLL, 0 none -- the text says why.
        int Describe(char* buffer, int size) const;

        // Whether XeLL runs, for the present path, which presents with a sync interval
        // of 1 at most then.
        bool XellActive() const { return xellActive.load(); }

        // The measure's last report: its median and 90th percentile in milliseconds;
        // the frames it took, 0 before one.
        int Measure(double* median, double* percentile90) const;

    private:
        LowLatency() = default;

        struct Pending
        {
            uint64_t fenceValue;
            int64_t start;
        };

        bool TrackingLocked() const;
        bool XellLocked() const;
        bool ReflexLocked() const;
        bool AntiLagLocked() const;
        void MarkerLocked(uint32_t frameIndex, int marker);
        void ReportLocked();
        uint32_t IdLocked(uint32_t frameIndex) const;
        void Brake();
        void SetText(int kind, const std::string& text);
        void MeterThread();

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
        std::atomic<bool> xellActive{ false };
        // Said once; the main thread's, under xellMutex.
        bool xellSleepFailed = false;
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
        int64_t starts[256] = {};

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
        double brakeSleepMs = 0.0;
        double brakeFrameMs = 0.0;
        double brakeWaitMs = 0.0;
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
