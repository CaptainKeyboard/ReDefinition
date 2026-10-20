#include "LowLatency.h"

#include "Config.h"
#include "Log.h"
#include "NvidiaGpu.h"
#include "WaitStats.h"

#include <algorithm>
#include <cstdio>
#include <cstring>
#include <vector>

namespace redefinition
{
    namespace
    {
        // Declared from NVIDIA's NVAPI headers (MIT; nvapi.h and nvapi_interface.h,
        // github.com/NVIDIA/nvapi): the functions' ids for nvapi_QueryInterface and
        // the two structures. NvBool is NvU8.
        constexpr uint32_t kSetSleepMode = 0xac1ca9e0;
        constexpr uint32_t kSleep = 0x852cd1d2;
        constexpr uint32_t kSetLatencyMarker = 0xd9984c05;
        constexpr uint32_t kGetLatency = 0x1a587f9c;

        struct SleepModeParams   // NV_SET_SLEEP_MODE_PARAMS_V1
        {
            uint32_t version;
            uint8_t lowLatencyMode;
            uint8_t lowLatencyBoost;
            uint32_t minimumIntervalUs;
            uint8_t useMarkersToOptimize;
            uint8_t useMinQueueTime;
            uint8_t reserved[30];
        };

        struct LatencyMarkerParams   // NV_LATENCY_MARKER_PARAMS_V1
        {
            uint32_t version;
            uint64_t frameId;
            int32_t markerType;   // NV_LATENCY_MARKER_TYPE
            uint64_t reserved0;
            uint8_t reserved[56];
        };

        struct FrameReport   // NV_LATENCY_RESULT_PARAMS_V1::FrameReport
        {
            uint64_t frameId;
            uint64_t inputSampleTime;
            uint64_t simStartTime;
            uint64_t simEndTime;
            uint64_t renderSubmitStartTime;
            uint64_t renderSubmitEndTime;
            uint64_t presentStartTime;
            uint64_t presentEndTime;
            uint64_t driverStartTime;
            uint64_t driverEndTime;
            uint64_t osRenderQueueStartTime;
            uint64_t osRenderQueueEndTime;
            uint64_t gpuRenderStartTime;
            uint64_t gpuRenderEndTime;
            uint32_t gpuActiveRenderTimeUs;
            uint32_t gpuFrameTimeUs;
            uint64_t cameraConstructedTime;
            uint32_t crossAdapterCopyTimeUs;
            uint32_t aiFrameTimeUs;
            uint8_t reserved[104];
        };

        struct LatencyResultParams   // NV_LATENCY_RESULT_PARAMS_V1
        {
            uint32_t version;
            FrameReport frameReport[64];
            uint8_t reserved[32];
        };
        static_assert(sizeof(FrameReport) == 240, "NV_LATENCY_RESULT_PARAMS_V1::FrameReport");

        // NV_LATENCY_MARKER_TYPE.
        constexpr int kSimulationStart = 0;
        constexpr int kSimulationEnd = 1;
        constexpr int kRenderSubmitStart = 2;
        constexpr int kRenderSubmitEnd = 3;
        constexpr int kPresentStart = 4;
        constexpr int kPresentEnd = 5;

        uint32_t Version(size_t size, uint32_t version)   // MAKE_NVAPI_VERSION
        {
            return static_cast<uint32_t>(size) | (version << 16);
        }

        using SetSleepModeFn = int (*)(IUnknown* device, SleepModeParams* params);
        using SleepFn = int (*)(IUnknown* device);
        using SetLatencyMarkerFn = int (*)(IUnknown* device, LatencyMarkerParams* params);
        using GetLatencyFn = int (*)(IUnknown* device, LatencyResultParams* params);

        SetSleepModeFn SetSleepMode()
        {
            static const auto function = reinterpret_cast<SetSleepModeFn>(NvapiInterface(kSetSleepMode));
            return function;
        }

        SleepFn SleepCall()
        {
            static const auto function = reinterpret_cast<SleepFn>(NvapiInterface(kSleep));
            return function;
        }

        GetLatencyFn GetLatency()
        {
            static const auto function = reinterpret_cast<GetLatencyFn>(NvapiInterface(kGetLatency));
            return function;
        }

        SetLatencyMarkerFn SetLatencyMarker()
        {
            static const auto function = reinterpret_cast<SetLatencyMarkerFn>(NvapiInterface(kSetLatencyMarker));
            return function;
        }
    }

    // Never destroyed, like the other singletons: at process exit the device may be
    // gone.
    LowLatency& LowLatency::Get()
    {
        static LowLatency* instance = new LowLatency();
        return *instance;
    }

    void LowLatency::SetText(int newKind, const std::string& newText)
    {
        bool changed = false;
        {
            std::lock_guard<std::mutex> lock(textLock);
            changed = kind != newKind || text != newText;
            kind = newKind;
            text = newText;
        }
        if (changed)
            LogLine("Low latency: " + newText);
    }

    void LowLatency::Attach(const void* newOwner, ID3D12Device* newDevice, const LUID& adapter, bool streamlineReflex)
    {
        if (AttachLocked(newOwner, newDevice, adapter, streamlineReflex))
            ApplyConfig();
    }

    bool LowLatency::AttachLocked(const void* newOwner, ID3D12Device* newDevice, const LUID& adapter,
                                  bool streamlineReflex)
    {
        std::lock_guard<std::mutex> lock(mutex);
        owner = newOwner;
        device.Reset();
        streamline = streamlineReflex;
        appliedMode = -1;
        loggedFailure = false;
        if (streamlineReflex)
        {
            SetText(2, "NVIDIA Reflex, Streamline's, with DLSS frame generation");
            return false;
        }
        NvidiaGpuInfo info;
        if (NvidiaGpu(adapter, info) != NvidiaGpuAnswer::Found)
        {
            SetText(0, "none on this GPU -- NVIDIA Reflex needs an NVIDIA GPU");
            return false;
        }
        if (SetSleepMode() == nullptr || SleepCall() == nullptr || SetLatencyMarker() == nullptr)
        {
            SetText(0, "none -- the NVIDIA driver's NVAPI lacks Reflex's functions (R455 and later)");
            return false;
        }
        device = newDevice;
        LogLine("Low latency: NVIDIA Reflex through NVAPI on the proxy's Direct3D 12 device");
        return true;
    }

    void LowLatency::Detach(const void* caller)
    {
        std::lock_guard<std::mutex> lock(mutex);
        if (caller != owner)
            return;
        owner = nullptr;
        device.Reset();
        streamline = false;
        rendered = 0;
        SetText(0, "no swapchain through the proxy");
    }

    bool LowLatency::ActiveLocked() const
    {
        return device != nullptr && !streamline;
    }

    void LowLatency::ApplyConfig()
    {
        std::lock_guard<std::mutex> lock(mutex);
        if (!ActiveLocked())
            return;
        const int wanted = Config().lowLatency ? 1 : 0;
        if (wanted == appliedMode)
            return;
        SleepModeParams params = {};
        params.version = Version(sizeof(params), 1);
        params.lowLatencyMode = static_cast<uint8_t>(wanted);
        const int status = SetSleepMode()(device.Get(), &params);
        if (status != 0)
        {
            appliedMode = wanted;
            SetText(0, "none -- NvAPI_D3D_SetSleepMode failed with status " + std::to_string(status));
            return;
        }
        appliedMode = wanted;
        SetText(wanted ? 1 : 0, wanted ? "NVIDIA Reflex, low latency mode"
                                       : "off (lowLatency=0 in ReDefinitionProxy.ini) -- Reflex's markers only");
    }

    void LowLatency::MarkerLocked(uint32_t frameIndex, int marker)
    {
        LatencyMarkerParams params = {};
        params.version = Version(sizeof(params), 1);
        params.frameId = frameIndex;
        params.markerType = marker;
        const int status = SetLatencyMarker()(device.Get(), &params);
        if (status != 0 && !loggedFailure)
        {
            loggedFailure = true;
            LogLine("Low latency: NvAPI_D3D_SetLatencyMarker failed with status " + std::to_string(status)
                    + " (marker " + std::to_string(marker) + ", frame " + std::to_string(frameIndex) + ")");
        }
    }

    // "It is recommended to call this function at the very start of each frame
    // (e.g. before input sampling) ... it must be called exactly once on each frame",
    // then "SIMULATION_START must be the first marker sent in a frame, after the
    // previous frame's Sleep call" (nvapi.h). The sleep is taken outside the lock,
    // on a reference of its own to the device: it lasts until Reflex lets the frame
    // begin.
    void LowLatency::Brake()
    {
        {
            std::lock_guard<std::mutex> lock(mutex);
            if (owner == nullptr || streamline)
                return;
        }
        LARGE_INTEGER now;
        QueryPerformanceCounter(&now);
        static LARGE_INTEGER frequency = []() { LARGE_INTEGER f; QueryPerformanceFrequency(&f); return f; }();
        const int64_t waits = g_waitTotalTicks.load() + g_presentTotalTicks.load();
        if (brakeWaitsBefore < 0 || !Config().latencyBrake)
        {
            brakeWaitsBefore = waits;
            brakeFrameStart = now.QuadPart;
            brakeSleepMs = 0.0;
            return;
        }
        const double waitMs = static_cast<double>(waits - brakeWaitsBefore) * 1000.0 / frequency.QuadPart;
        const double frameMs = static_cast<double>(now.QuadPart - brakeFrameStart) * 1000.0 / frequency.QuadPart;
        brakeWaitsBefore = waits;
        brakeFrameStart = now.QuadPart;
        brakeFrameMs = brakeFrameMs == 0.0 ? frameMs : brakeFrameMs * 0.9 + frameMs * 0.1;
        brakeWaitMs = brakeWaitMs * 0.9 + waitMs * 0.1;

        // Towards a millisecond of waiting left, a fifth of the difference a frame; at
        // most half a frame, so a sudden change cannot stall the game.
        const double kTargetMs = Config().latencyBrakeTargetMs;
        constexpr double kGain = 0.2;
        brakeSleepMs += kGain * (waitMs - kTargetMs);
        if (brakeSleepMs < 0.0)
            brakeSleepMs = 0.0;
        if (brakeSleepMs > brakeFrameMs * 0.5)
            brakeSleepMs = brakeFrameMs * 0.5;

        if (brakeSleepMs >= 0.1)
        {
            if (brakeTimer == nullptr)
                brakeTimer = CreateWaitableTimerExW(nullptr, nullptr, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION,
                                                    TIMER_ALL_ACCESS);
            LARGE_INTEGER due;
            due.QuadPart = -static_cast<LONGLONG>(brakeSleepMs * 10000.0);
            if (brakeTimer != nullptr && SetWaitableTimer(brakeTimer, &due, 0, nullptr, nullptr, FALSE))
                WaitForSingleObject(brakeTimer, 100);
            else
                Sleep(static_cast<DWORD>(brakeSleepMs));
        }

        const ULONGLONG tick = GetTickCount64();
        const ULONGLONG every = static_cast<ULONGLONG>(Config().reportSeconds > 0 ? Config().reportSeconds : 10) * 1000;
        if (tick - brakeReport >= every)
        {
            brakeReport = tick;
            char line[160];
            sprintf_s(line, "Latency brake: sleeps %.1f ms before the frame, %.1f ms of waits in the proxy left a frame"
                            " of %.1f ms", brakeSleepMs, brakeWaitMs, brakeFrameMs);
            LogLine(line);
        }
    }

    uint32_t LowLatency::BeginGameFrame(uint32_t streamlineIndex)
    {
        Brake();
        Microsoft::WRL::ComPtr<ID3D12Device> held;
        ApplyConfig();
        {
            std::lock_guard<std::mutex> lock(mutex);
            if (!ActiveLocked())
                return 0;
            held = device;
        }
        SleepCall()(held.Get());

        std::lock_guard<std::mutex> lock(mutex);
        if (!ActiveLocked())
            return 0;
        // "The frameID ... must strictly increment from that point forward".
        uint32_t index = streamlineIndex != 0 ? streamlineIndex : ++counter;
        if (index <= lastBegun)
            index = lastBegun + 1;
        counter = index;
        lastBegun = index;
        MarkerLocked(index, kSimulationStart);
        return index;
    }

    void LowLatency::EndGameSimulation(uint32_t frameIndex)
    {
        std::lock_guard<std::mutex> lock(mutex);
        if (frameIndex != 0 && ActiveLocked())
            MarkerLocked(frameIndex, kSimulationEnd);
    }

    // "RENDERSUBMIT_START should come before any Direct3D calls are made for the
    // given frame" (nvapi.h).
    void LowLatency::OnGameFrameRendered(uint32_t frameIndex)
    {
        std::lock_guard<std::mutex> lock(mutex);
        if (frameIndex == 0 || !ActiveLocked())
            return;
        MarkerLocked(frameIndex, kRenderSubmitStart);
        rendered = frameIndex;
    }

    uint32_t LowLatency::TakeGameFrame()
    {
        const uint32_t frame = rendered.exchange(0);
        std::lock_guard<std::mutex> lock(mutex);
        if (frame == 0 || frame <= presented || !ActiveLocked())
            return 0;
        presented = frame;
        return frame;
    }

    // "RENDERSUBMIT_END should come before calling Present. PRESENT_START and END
    // should wrap the Present call" (nvapi.h).
    void LowLatency::BeforePresent(uint32_t frameIndex)
    {
        std::lock_guard<std::mutex> lock(mutex);
        if (frameIndex == 0 || !ActiveLocked())
            return;
        MarkerLocked(frameIndex, kRenderSubmitEnd);
        MarkerLocked(frameIndex, kPresentStart);
    }

    void LowLatency::AfterPresent(uint32_t frameIndex)
    {
        std::lock_guard<std::mutex> lock(mutex);
        if (frameIndex == 0 || !ActiveLocked())
            return;
        MarkerLocked(frameIndex, kPresentEnd);
        ReportLocked();
    }

    // Every reportSeconds, the driver's own report of the last 64 frames
    // (NvAPI_D3D_GetLatency): from the simulation's start to the GPU's end of the
    // frame, the median, for what the low latency mode does. The timestamps' unit is
    // not stated in nvapi.h; it is taken from the same report, where the GPU's frame
    // time is given in microseconds as well (gpuFrameTimeUs).
    void LowLatency::ReportLocked()
    {
        const ULONGLONG now = GetTickCount64();
        const ULONGLONG every = static_cast<ULONGLONG>(Config().reportSeconds > 0 ? Config().reportSeconds : 10) * 1000;
        if (GetLatency() == nullptr || now - lastReport < every)
            return;
        lastReport = now;
        static LatencyResultParams result;
        std::memset(&result, 0, sizeof(result));
        result.version = Version(sizeof(result), 1);
        if (GetLatency()(device.Get(), &result) != 0)
            return;
        double unitsPerUs = 0.0;
        std::vector<double> latencies;
        std::vector<double> frames;
        // Where the time goes, each stage's median: the simulation, from its end to
        // the render thread's first call, the render thread's submission, from its end
        // to the Present's start, the Present, from the Present's end to the GPU's
        // start on this device, and the GPU's work there.
        constexpr int kStages = 7;
        std::vector<double> stages[kStages];
        for (int i = 1; i < 64; ++i)
        {
            const FrameReport& before = result.frameReport[i - 1];
            const FrameReport& frame = result.frameReport[i];
            if (frame.simStartTime == 0 || frame.gpuRenderEndTime <= frame.simStartTime)
                continue;
            if (unitsPerUs == 0.0 && frame.gpuFrameTimeUs > 0 && before.gpuRenderEndTime != 0
                && frame.gpuRenderEndTime > before.gpuRenderEndTime)
                unitsPerUs = static_cast<double>(frame.gpuRenderEndTime - before.gpuRenderEndTime) / frame.gpuFrameTimeUs;
            latencies.push_back(static_cast<double>(frame.gpuRenderEndTime - frame.simStartTime));
            const uint64_t points[kStages + 1] = { frame.simStartTime, frame.simEndTime, frame.renderSubmitStartTime,
                                                   frame.renderSubmitEndTime, frame.presentStartTime,
                                                   frame.presentEndTime, frame.gpuRenderStartTime,
                                                   frame.gpuRenderEndTime };
            for (int s = 0; s < kStages; ++s)
                stages[s].push_back(points[s + 1] >= points[s] && points[s] != 0
                                        ? static_cast<double>(points[s + 1] - points[s])
                                        : -static_cast<double>(points[s] - points[s + 1]));
            if (frame.gpuFrameTimeUs > 0)
                frames.push_back(frame.gpuFrameTimeUs / 1000.0);
        }
        if (latencies.size() < 16 || unitsPerUs <= 0.0)
            return;
        std::sort(latencies.begin(), latencies.end());
        std::sort(frames.begin(), frames.end());
        const double latencyMs = latencies[latencies.size() / 2] / unitsPerUs / 1000.0;
        const double frameMs = frames.empty() ? 0.0 : frames[frames.size() / 2];
        char line[192];
        sprintf_s(line, "Low latency: from the simulation's start to the GPU's end of the frame %.1f ms (median of %u"
                        " frames), the GPU's frame %.1f ms; low latency mode %s",
                  latencyMs, static_cast<unsigned>(latencies.size()), frameMs, appliedMode == 1 ? "on" : "off");
        LogLine(line);
        std::string breakdown;
        const char* names[kStages] = { "simulation", "to the render thread", "render submission", "to Present",
                                       "Present", "to the GPU", "GPU" };
        breakdown = "Low latency: the frame's stages, medians in ms --";
        for (int s = 0; s < kStages; ++s)
        {
            std::sort(stages[s].begin(), stages[s].end());
            char part[64];
            sprintf_s(part, " %s %.1f%s", names[s], stages[s][stages[s].size() / 2] / unitsPerUs / 1000.0,
                      s + 1 < kStages ? "," : "");
            breakdown += part;
        }
        LogLine(breakdown);
    }

    int LowLatency::Describe(char* buffer, int size) const
    {
        bool braking = false;
        {
            std::lock_guard<std::mutex> lock(mutex);
            braking = owner != nullptr && !streamline && Config().latencyBrake;
        }
        std::lock_guard<std::mutex> lock(textLock);
        const std::string shown = braking ? text + "; latency brake on" : text;
        if (buffer != nullptr && size > 0)
            strncpy_s(buffer, static_cast<size_t>(size), shown.c_str(), _TRUNCATE);
        return kind;
    }
}
