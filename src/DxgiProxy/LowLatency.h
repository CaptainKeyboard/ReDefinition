#pragma once

#include <d3d12.h>
#include <wrl/client.h>

#include <atomic>
#include <cstdint>
#include <mutex>
#include <string>

namespace redefinition
{
    // Lower latency for the game's frames where Streamline's Reflex does not run --
    // frame generation off, FSR's frame generation, DLSS Ray Reconstruction alone:
    // NVIDIA Reflex through NVAPI on the proxy's Direct3D 12 device, which presents
    // (nvapi.h, NvAPI_D3D_SetSleepMode, NvAPI_D3D_Sleep, NvAPI_D3D_SetLatencyMarker;
    // GeForce GTX 900 and later, driver R455). Streamline brings its own with DLSS
    // frame generation (Streamline.h), and this then stays out of the way.
    //
    // The frame's beginning, its simulation's end and its first Direct3D call come
    // through the same exports and render event as Streamline's (ManagedBridge.cpp,
    // KspReflexFrameStart; ReflexFrames.cs), its Present from the swapchain proxy.
    // As NVIDIA asks, Sleep and the markers go on every frame whether low latency is
    // on or not -- "It is recommended to call this function even when low latency
    // mode is disabled" (NvAPI_D3D_Sleep) -- and lowLatency in ReDefinitionProxy.ini
    // only sets the mode, re-read while the game runs.
    class LowLatency
    {
    public:
        static LowLatency& Get();

        // The swapchain proxy's device, once its presentation path is ready, and
        // whether Streamline's Reflex runs on it. Only an NVIDIA adapter is taken.
        void Attach(const void* owner, ID3D12Device* device, const LUID& adapter, bool streamlineReflex);
        void Detach(const void* owner);

        // On the game's main thread where its frame begins: Sleep, then
        // SIMULATION_START, under the given index -- Streamline's where it gives one
        // -- or one of its own. Returns the index, 0 where nothing was sent.
        uint32_t BeginGameFrame(uint32_t streamlineIndex);
        void EndGameSimulation(uint32_t frameIndex);
        // On Unity's render thread before the frame's first Direct3D call.
        void OnGameFrameRendered(uint32_t frameIndex);
        // On the presenting thread, around the swapchain's Present: the game's
        // frame rendered last, 0 for none.
        uint32_t TakeGameFrame();
        void BeforePresent(uint32_t frameIndex);
        void AfterPresent(uint32_t frameIndex);

        // The low latency mode as lowLatency asks, again after the ini changed.
        void ApplyConfig();

        // The latency brake (latencyBrake in ReDefinitionProxy.ini), on the game's
        // main thread before its frame begins: where the GPU holds the frames back,
        // Unity's render thread waits in the proxy -- for a raytracing slot, a frame's
        // allocator, the Present -- after the frame's simulation has already read its
        // input. The brake sleeps that long before the simulation instead, less a
        // millisecond: an integrating controller on the waits the last frame met,
        // so the GPU is still fed in time. Any GPU, Reflex or not; not with DLSS frame
        // generation, whose Reflex paces the frames.
        void Brake();

        // What runs, for the settings row and the log: 1 NVIDIA Reflex through
        // NVAPI, 2 Streamline's Reflex with DLSS frame generation, 0 none -- the
        // text says why.
        int Describe(char* buffer, int size) const;

    private:
        LowLatency() = default;

        bool AttachLocked(const void* owner, ID3D12Device* device, const LUID& adapter, bool streamlineReflex);
        bool ActiveLocked() const;
        void MarkerLocked(uint32_t frameIndex, int marker);
        void ReportLocked();
        // The id Reflex was given for an index the game's calls carry.
        uint32_t IdLocked(uint32_t frameIndex) const;
        void SetText(int kind, const std::string& text);

        mutable std::mutex mutex;
        const void* owner = nullptr;
        Microsoft::WRL::ComPtr<ID3D12Device> device;
        bool streamline = false;
        bool loggedFailure = false;
        int appliedMode = -1;
        uint32_t lastBegun = 0;
        // Streamline's index to Reflex's id, and whether the frame begun last came
        // with one.
        uint32_t offset = 0;
        bool mapped = false;
        std::atomic<uint32_t> rendered{ 0 };
        uint32_t presented = 0;
        unsigned long long lastReport = 0;

        // The brake's state, on the main thread.
        int64_t brakeWaitsBefore = -1;
        int64_t brakeFrameStart = 0;
        double brakeSleepMs = 0.0;
        double brakeFrameMs = 0.0;
        double brakeWaitMs = 0.0;
        unsigned long long brakeReport = 0;
        HANDLE brakeTimer = nullptr;
        // A swapchain attached or let go: the brake starts afresh.
        std::atomic<bool> brakeReseed{ true };

        mutable std::mutex textLock;
        int kind = 0;
        std::string text = "no swapchain through the proxy yet";
    };
}
