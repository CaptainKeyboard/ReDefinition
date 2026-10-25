#include "LowLatency.h"

#include "Config.h"
#include "Log.h"
#include "NvidiaGpu.h"
#include "WaitStats.h"
#include "XessFg.h"

#include <AntiLag2/ffx_antilag2_dx11.h>
#include <AntiLag2/ffx_antilag2_dx12.h>

#include <dxgi.h>

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
        // the structures. NvBool is NvU8.
        constexpr uint32_t kSetSleepMode = 0xac1ca9e0;
        constexpr uint32_t kSleep = 0x852cd1d2;
        constexpr uint32_t kSetLatencyMarker = 0xd9984c05;
        constexpr uint32_t kGetLatency = 0x1a587f9c;

        // Reflex's mode, for NvAPI_D3D_SetSleepMode.
        struct SleepModeParams   // NV_SET_SLEEP_MODE_PARAMS_V1
        {
            // The version, low latency mode and boost, the frame limit as the shortest frame in microseconds (0
            // none), and options the proxy leaves at 0.
            uint32_t version;
            uint8_t lowLatencyMode;
            uint8_t lowLatencyBoost;
            uint32_t minimumIntervalUs;
            uint8_t useMarkersToOptimize;
            uint8_t useMinQueueTime;
            uint8_t reserved[30];
        };

        // A latency marker for a frame, for NvAPI_D3D_SetLatencyMarker.
        struct LatencyMarkerParams   // NV_LATENCY_MARKER_PARAMS_V1
        {
            // The version, and the frame id the marker is for.
            uint32_t version;
            uint64_t frameId;
            int32_t markerType;   // NV_LATENCY_MARKER_TYPE
            uint64_t reserved0;
            uint8_t reserved[56];
        };

        // One frame in NVIDIA's latency report.
        struct FrameReport   // NV_LATENCY_RESULT_PARAMS_V1::FrameReport
        {
            // The frame's id, then its stages' times in the driver's units -- which ReportLocked derives from
            // gpuFrameTimeUs -- and the GPU's times in microseconds.
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

        // What NvAPI_D3D_GetLatency fills: the reports of the last 64 frames.
        struct LatencyResultParams   // NV_LATENCY_RESULT_PARAMS_V1
        {
            // The version, then the last 64 frames' reports.
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

        // A structure's version as NVAPI asks for it: its size, and the version above bit 16.
        uint32_t Version(size_t size, uint32_t version)   // MAKE_NVAPI_VERSION
        {
            return static_cast<uint32_t>(size) | (version << 16);
        }

        // The four functions' signatures; each returns an NvAPI_Status, 0 for NVAPI_OK.
        using SetSleepModeFn = int (*)(IUnknown* device, SleepModeParams* params);
        using SleepFn = int (*)(IUnknown* device);
        using SetLatencyMarkerFn = int (*)(IUnknown* device, LatencyMarkerParams* params);
        using GetLatencyFn = int (*)(IUnknown* device, LatencyResultParams* params);

        // NvAPI_D3D_SetSleepMode, looked up once; null where NVAPI lacks it.
        SetSleepModeFn SetSleepMode()
        {
            static const auto function = reinterpret_cast<SetSleepModeFn>(NvapiInterface(kSetSleepMode));
            return function;
        }

        // NvAPI_D3D_Sleep, looked up once; null where NVAPI lacks it.
        SleepFn SleepCall()
        {
            static const auto function = reinterpret_cast<SleepFn>(NvapiInterface(kSleep));
            return function;
        }

        // NvAPI_D3D_GetLatency, looked up once; null where NVAPI lacks it.
        GetLatencyFn GetLatency()
        {
            static const auto function = reinterpret_cast<GetLatencyFn>(NvapiInterface(kGetLatency));
            return function;
        }

        // NvAPI_D3D_SetLatencyMarker, looked up once; null where NVAPI lacks it.
        SetLatencyMarkerFn SetLatencyMarker()
        {
            static const auto function = reinterpret_cast<SetLatencyMarkerFn>(NvapiInterface(kSetLatencyMarker));
            return function;
        }

        // Anti-Lag 2's contexts, "persistent ... initialized with = {}", one per device
        // it can be given; under LowLatency's antiLagMutex.
        AMD::AntiLag2DX12::Context g_antiLag12 = {};
        AMD::AntiLag2DX11::Context g_antiLag11 = {};
        bool g_antiLag12Ready = false;
        bool g_antiLag11Ready = false;

        // "you will need to poke some data into the DXGI swapchain's private data each
        // frame using a specific struct format using a specific GUID" (the SDK's
        // README, FSR 3 Frame Generation Support): FSR 3.1.1 and later then tell
        // Anti-Lag 2 which presented frames are interpolated.
        // {5083ae5b-8070-4fca-8ee5-3582dd367d13}
        const GUID kAntiLag2Data = { 0x5083ae5b, 0x8070, 0x4fca, { 0x8e, 0xe5, 0x35, 0x82, 0xdd, 0x36, 0x7d, 0x13 } };
        // The private data's layout under kAntiLag2Data.
        struct AntiLag2Data
        {
            // Anti-Lag 2's Direct3D 12 context, and whether it is on there.
            AMD::AntiLag2DX12::Context* context;
            bool enabled;
        };

        // The performance counter's ticks per second, asked once.
        double Frequency()
        {
            static const double frequency = []()
            {
                LARGE_INTEGER f;
                QueryPerformanceFrequency(&f);
                return static_cast<double>(f.QuadPart);
            }();
            return frequency;
        }

        // The performance counter now, in its ticks.
        int64_t Now()
        {
            LARGE_INTEGER now;
            QueryPerformanceCounter(&now);
            return now.QuadPart;
        }
    }

    // Never destroyed, like the other singletons: at process exit the device may be
    // gone.
    LowLatency& LowLatency::Get()
    {
        static LowLatency* instance = new LowLatency();
        return *instance;
    }

    // What Describe reports, under textLock; logged where it changed and log is set.
    void LowLatency::SetText(int newKind, const std::string& newText, bool log)
    {
        bool changed = false;
        {
            std::lock_guard<std::mutex> lock(textLock);
            changed = kind != newKind || text != newText;
            kind = newKind;
            text = newText;
        }
        if (changed && log)
            LogLine("Low latency: " + newText);
    }

    void LowLatency::Attach(const void* newOwner, ID3D11Device* newGameDevice, ID3D12Device* newDevice,
                            ID3D12CommandQueue* newQueue, const LUID& adapter, bool streamlineReflex,
                            const XellSetup& setup)
    {
        const xell_context_handle_t newXell = setup.context;
        {
            std::lock_guard<std::mutex> lock(mutex);
            owner = newOwner;
            device = newDevice;
            gameDevice = newGameDevice;
            queue = newQueue;
            streamline = streamlineReflex;
            brakeReseed = true;
            appliedMode = -1;
            appliedAntiLag = -1;
            loggedFailure = false;
            pending.clear();
            measured.clear();
            for (std::vector<double>& stage : cpuStages)
                stage.clear();
            fence.Reset();
            fenceValue = 0;

            // XeLL where the proxy made it, and then nothing else.
            {
                std::lock_guard<std::mutex> sleeping(xellMutex);
                xell = newXell;
                xellSleeping = newXell;
                for (std::atomic<uint32_t>& slept : xellSlept)
                    slept.store(0);
                xellMarkerDropLogged = false;
            }
            xellFrameGeneration = newXell != nullptr && setup.frameGeneration;
            xellLowLatency = newXell != nullptr && (xellFrameGeneration || setup.lowLatency);
            xellIntel = setup.intelGpu;
            xellActive = xellLowLatency;
            xessGenerating = false;
            handOverSeen = -1;
            handOverLogged = -1;
            handOversUnlogged = 0;
            // Reflex and Anti-Lag 2 only where XeLL's low latency mode is off: with it on
            // -- always under XeSS-FG's swapchain -- "other latency reduction solutions"
            // are excluded (Integration Checklist).
            const bool others = !streamlineReflex && !xellLowLatency;

            NvidiaGpuInfo info;
            reflex = others && NvidiaGpu(adapter, info) == NvidiaGpuAnswer::Found && SetSleepMode() != nullptr
                     && SleepCall() != nullptr && SetLatencyMarker() != nullptr;

            // Anti-Lag 2 where AMD's driver behind the device has it: "If this function
            // returns S_OK, then Anti-Lag 2 is present".
            std::lock_guard<std::mutex> antiLag(antiLagMutex);
            antiLagOn = 0;
            AMD::AntiLag2DX12::DeInitialize(&g_antiLag12);
            AMD::AntiLag2DX11::DeInitialize(&g_antiLag11);
            g_antiLag12 = {};
            g_antiLag11 = {};
            g_antiLag12Ready = others && AMD::AntiLag2DX12::Initialize(&g_antiLag12, newDevice) == S_OK;
            g_antiLag11Ready = others && newGameDevice != nullptr
                               && AMD::AntiLag2DX11::Initialize(&g_antiLag11) == S_OK;
            amd = g_antiLag12Ready || g_antiLag11Ready;
            if (amd)
                LogLine(std::string("Low latency: AMD Anti-Lag 2 available on ")
                        + (g_antiLag12Ready && g_antiLag11Ready ? "both devices"
                           : g_antiLag12Ready ? "the proxy's Direct3D 12 device" : "the game's Direct3D 11 device"));
            if (reflex)
                LogLine("Low latency: NVIDIA Reflex through NVAPI on the proxy's Direct3D 12 device");
            if (newXell != nullptr)
                LogLine(std::string("Low latency: Intel XeLL on the proxy's Direct3D 12 device")
                        + (xellFrameGeneration ? ", with XeSS frame generation" : "")
                        + (xellFrameGeneration && !xellIntel
                               ? " -- on this GPU it lowers the latency only while XeSS frame generation generates,"
                                 " the latency brake while it is held off"
                               : ""));

            // The measure, on every GPU and with DLSS frame generation as well.
            if (newDevice != nullptr && newQueue != nullptr)
            {
                if (FAILED(newDevice->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&fence))))
                    fence.Reset();
                if (meterEvent == nullptr)
                    meterEvent = CreateEventW(nullptr, FALSE, FALSE, nullptr);
                if (!meterStarted && meterEvent != nullptr)
                {
                    meterStarted = true;
                    meter = std::thread([this]() { MeterThread(); });
                    meter.detach();
                }
            }
        }
        ApplyConfig();
    }

    // Everything let go, the main thread's XeLL Sleep waited out first.
    void LowLatency::Detach(const void* caller)
    {
        std::lock_guard<std::mutex> lock(mutex);
        if (caller != owner)
            return;
        {
            // The main thread's Sleep is done before the context goes.
            std::lock_guard<std::mutex> sleeping(xellMutex);
            xellSleeping = nullptr;
            for (std::atomic<uint32_t>& slept : xellSlept)
                slept.store(0);
        }
        xell = nullptr;
        xellFrameGeneration = false;
        xellLowLatency = false;
        xellIntel = false;
        xellActive = false;
        xessGenerating = false;
        {
            std::lock_guard<std::mutex> antiLag(antiLagMutex);
            antiLagOn = 0;
            if (g_antiLag12Ready)
                AMD::AntiLag2DX12::DeInitialize(&g_antiLag12);
            if (g_antiLag11Ready)
                AMD::AntiLag2DX11::DeInitialize(&g_antiLag11);
            g_antiLag12Ready = false;
            g_antiLag11Ready = false;
        }
        owner = nullptr;
        device.Reset();
        gameDevice.Reset();
        queue.Reset();
        fence.Reset();
        pending.clear();
        streamline = false;
        reflex = false;
        amd = false;
        rendered = 0;
        brakeReseed = true;
        SetText(0, "no swapchain through the proxy");
    }

    // The game's frames are followed, and measured, on every swapchain through the
    // proxy, DLSS frame generation's included; what lowers the latency stays out of
    // the way of Streamline's Reflex.
    bool LowLatency::TrackingLocked() const
    {
        return owner != nullptr;
    }

    // Whether XeLL's context is attached, which then takes the markers instead of Reflex.
    bool LowLatency::XellLocked() const
    {
        return TrackingLocked() && xell != nullptr;
    }

    // "XeLL requires XeSS-FG to be supported on the device in order to perform latency
    // reduction. Application must ensure that on non-Intel GPUs XeSS-FG context is
    // created and interpolation is enabled. If the condition is not fulfilled then XeLL
    // will not perform Latency reduction" (xell_developer_guide_english.md,
    // Cross-vendor support).
    bool LowLatency::XellReducingLocked() const
    {
        return XellLocked() && xellLowLatency && (xellIntel || !xellFrameGeneration || xessGenerating.load());
    }

    void LowLatency::NoteHandOverLocked(bool xellReduces)
    {
        const int state = xellReduces ? 1 : 0;
        if (state != handOverSeen)
        {
            handOverSeen = state;
            ++handOversUnlogged;
        }
        // Back where the last line left it, or that line too recent: counted, and the
        // state that stands said once the interval is over.
        if (state == handOverLogged)
            return;
        const ULONGLONG now = GetTickCount64();
        if (handOverLogged != -1 && now - handOverLoggedAt < kHandOverLogMs)
            return;
        const std::string since = handOversUnlogged > 1
                                      ? " (" + std::to_string(handOversUnlogged) + " changes since the last line)"
                                      : std::string();
        if (xellReduces)
            LogLine("Low latency: XeSS frame generation generates -- XeLL lowers the latency, the latency brake steps"
                    " back" + since);
        else
            LogLine(std::string("Low latency: XeSS frame generation is held off, and on this GPU XeLL lowers no latency"
                                " without it -- ")
                    + (Config().latencyBrake ? "the latency brake takes over" : "nothing instead (latencyBrake=0)")
                    + since);
        handOverLogged = state;
        handOverLoggedAt = now;
        handOversUnlogged = 0;
    }

    // Whether Reflex through NVAPI runs on the attached device.
    bool LowLatency::ReflexLocked() const
    {
        return TrackingLocked() && reflex && device != nullptr;
    }

    // Whether Anti-Lag 2 is on, on the device it was initialised on.
    bool LowLatency::AntiLagLocked() const
    {
        return TrackingLocked() && (appliedAntiLag == 12 ? g_antiLag12Ready : appliedAntiLag == 11 && g_antiLag11Ready);
    }

    void LowLatency::ApplyConfig()
    {
        std::lock_guard<std::mutex> lock(mutex);
        if (!TrackingLocked())
            return;
        if (streamline)
        {
            SetText(2, "NVIDIA Reflex, Streamline's, with DLSS frame generation");
            return;
        }
        // Its mode as set when the swapchain was made (XessFg::XellSetSleepMode).
        if (xell != nullptr)
        {
            if (xellFrameGeneration && !xellIntel)
            {
                // Said in the log by the hand-over's own lines, which hold back when it
                // goes back and forth.
                const bool reduces = XellReducingLocked();
                NoteHandOverLocked(reduces);
                if (reduces)
                    SetText(4, "Intel XeLL, with XeSS frame generation", false);
                else
                    SetText(0, "Intel XeLL's markers only while XeSS frame generation is held off -- on this GPU XeLL"
                               " lowers the latency only while it generates", false);
            }
            else if (xellFrameGeneration)
                SetText(4, "Intel XeLL, with XeSS frame generation");
            else if (xellLowLatency)
                SetText(4, Config().lowLatency ? "Intel XeLL, low latency mode"
                                               : "Intel XeLL, low latency mode (lowLatency=0 takes effect with the"
                                                 " next swapchain)");
            else
                SetText(0, Config().lowLatency ? "off (lowLatency=0 when the swapchain was made) -- XeLL's markers only;"
                                                 " lowLatency=1 takes effect with the next swapchain"
                                               : "off (lowLatency=0 in ReDefinitionProxy.ini) -- XeLL's markers only");
            return;
        }
        const int wantedMode = reflex && Config().lowLatency ? 1 : 0;
        int wantedAntiLag = Config().antiLag2;
        if (wantedAntiLag == 12 && !g_antiLag12Ready)
            wantedAntiLag = 0;
        if (wantedAntiLag == 11 && !g_antiLag11Ready)
            wantedAntiLag = 0;
        if (wantedAntiLag != 11 && wantedAntiLag != 12)
            wantedAntiLag = 0;
        if (wantedMode == appliedMode && wantedAntiLag == appliedAntiLag)
            return;

        std::string status;
        if (reflex && wantedMode != appliedMode)
        {
            SleepModeParams params = {};
            params.version = Version(sizeof(params), 1);
            params.lowLatencyMode = static_cast<uint8_t>(wantedMode);
            const int result = SetSleepMode()(device.Get(), &params);
            if (result != 0)
                status = "NvAPI_D3D_SetSleepMode failed with status " + std::to_string(result);
        }
        appliedMode = wantedMode;

        // Set on the device taken and off on the other by the next frame's Update on the
        // main thread, which also takes the delay: never under this lock.
        appliedAntiLag = wantedAntiLag;
        antiLagOn = wantedAntiLag;

        if (appliedAntiLag != 0)
            SetText(3, std::string("AMD Anti-Lag 2 on the ")
                           + (appliedAntiLag == 12 ? "proxy's Direct3D 12 device" : "game's Direct3D 11 device"));
        else if (!status.empty())
            SetText(0, "none -- " + status);
        else if (reflex)
            SetText(appliedMode == 1 ? 1 : 0, appliedMode == 1
                ? "NVIDIA Reflex, low latency mode"
                : "off (lowLatency=0 in ReDefinitionProxy.ini) -- Reflex's markers only");
        else if (amd)
            SetText(0, "AMD Anti-Lag 2 off (antiLag2=0 in ReDefinitionProxy.ini)");
        else
            SetText(0, "neither NVIDIA Reflex nor AMD Anti-Lag 2 on this GPU");
    }

    // XeLL's marker where it is attached, otherwise Reflex's; the first failure logged.
    void LowLatency::MarkerLocked(uint32_t frameIndex, int marker)
    {
        if (XellLocked())
        {
            // Only for an id xellSleep was called for first ("xellSleep must be the first
            // XeLL API function called with a newly assigned frame_id value",
            // xell_developer_guide_english.md, Sleep).
            if (xellSlept[frameIndex % 64].load() != frameIndex)
            {
                if (!xellMarkerDropLogged)
                {
                    xellMarkerDropLogged = true;
                    LogLine("Low latency: XeLL's markers for frames its sleep was not called for first are not sent (frame "
                            + std::to_string(frameIndex) + "), as Intel asks");
                }
                return;
            }
            const xell_result_t result =
                XessFg::Get().XellMarker(xell, frameIndex, static_cast<xell_latency_marker_type_t>(marker));
            if (result != XELL_RESULT_SUCCESS && !loggedFailure)
            {
                loggedFailure = true;
                LogLine("Low latency: xellAddMarkerData failed with result " + std::to_string(static_cast<int>(result))
                        + " (marker " + std::to_string(marker) + ", frame " + std::to_string(frameIndex) + ")");
            }
            return;
        }
        if (!ReflexLocked())
            return;
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

    // The latency brake: where the GPU holds the frames back, Unity's render thread
    // waits in the proxy -- for a raytracing slot, a frame's allocator, and with
    // latencyBrakeWaits=1 the Present -- after the frame's simulation has already read
    // its input. The brake sleeps that long before the simulation instead, less
    // latencyBrakeTargetMs: an integrating controller on the waits the last frame met,
    // so the GPU is still fed in time.
    void LowLatency::Brake()
    {
        {
            std::lock_guard<std::mutex> lock(mutex);
            // Not beside another latency reduction: Anti-Lag 2 -- "Any other forms of
            // latency reduction ... should be disabled" -- or XeLL while it lowers the
            // latency, the same: "Any other forms of latency reduction or wait inducing
            // technologies like frame limiters should be disabled when XeLL is in use"
            // (xell_developer_guide_english.md, Requirements). Its markers alone, or
            // XeLL on a GPU other than Intel's while XeSS-FG is held off, lower none.
            // Streamline's Reflex runs beside it: the brake sleeps before slReflexSleep
            // (KspReflexFrameStart), where the waits of the frame before have been
            // spent, as a frame limiter does, and Reflex's sleep follows with its
            // markers unchanged; latencyBrakeWithDlssg=0 leaves it to Reflex alone.
            if (!TrackingLocked() || (streamline && !Config().latencyBrakeWithDlssg) || AntiLagLocked()
                || XellReducingLocked())
            {
                brakeWaitsBefore = -1;
                return;
            }
        }
        if (brakeReseed.exchange(false))
            brakeWaitsBefore = -1;
        const int64_t now = Now();
        const int64_t waits = g_waitTotalTicks.load() + (Config().latencyBrakeWaits == 1 ? g_presentTotalTicks.load() : 0);
        if (brakeWaitsBefore < 0 || !Config().latencyBrake || Config().latencyBrakeWaits != brakeWaitsKind)
        {
            brakeWaitsKind = Config().latencyBrakeWaits;
            brakeWaitsBefore = waits;
            brakeFrameStart = now;
            brake.sleepMs = 0.0;
            return;
        }
        const double waitMs = static_cast<double>(waits - brakeWaitsBefore) * 1000.0 / Frequency();
        const double frameMs = static_cast<double>(now - brakeFrameStart) * 1000.0 / Frequency();
        brakeWaitsBefore = waits;
        brakeFrameStart = now;
        // Towards the target's waiting left, a fifth of the difference a frame. A hitch --
        // one long wait or frame -- counts as a frame's worth at most, measured against the
        // frame time from before it, and the sleep is at most half a frame's work, so it
        // cannot hold the game back after it (BrakeController.h).
        const double sleepMs = brake.Update(frameMs, waitMs, Config().latencyBrakeTargetMs);

        if (sleepMs >= 0.1)
        {
            if (brakeTimer == nullptr)
                brakeTimer = CreateWaitableTimerExW(nullptr, nullptr, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION,
                                                    TIMER_ALL_ACCESS);
            LARGE_INTEGER due;
            due.QuadPart = -static_cast<LONGLONG>(sleepMs * 10000.0);
            if (brakeTimer != nullptr && SetWaitableTimer(brakeTimer, &due, 0, nullptr, nullptr, FALSE))
                WaitForSingleObject(brakeTimer, 100);
            else
                Sleep(static_cast<DWORD>(sleepMs));
        }

        const ULONGLONG tick = GetTickCount64();
        const ULONGLONG every = static_cast<ULONGLONG>(Config().reportSeconds > 0 ? Config().reportSeconds : 10) * 1000;
        if (tick - brakeReport >= every)
        {
            brakeReport = tick;
            char line[192];
            sprintf_s(line, "Latency brake: sleeps %.1f ms before the frame, %.1f ms of waits in the proxy%s left a"
                            " frame of %.1f ms work",
                      brake.sleepMs, brake.waitMs, brakeWaitsKind == 1 ? " with the Present" : "", brake.frameMs);
            LogLine(line);
        }
    }

    // "It is recommended to call this function at the very start of each frame
    // (e.g. before input sampling) ... it must be called exactly once on each frame",
    // then "SIMULATION_START must be the first marker sent in a frame, after the
    // previous frame's Sleep call" (nvapi.h); Anti-Lag 2's Update "just before the
    // game polls for input" (its README). The sleeps are taken outside the lock, on
    // references of their own.
    void LowLatency::BrakeBeforeFrame()
    {
        ApplyConfig();
        Brake();
    }

    uint32_t LowLatency::BeginGameFrame(uint32_t streamlineIndex)
    {
        ApplyConfig();
        Microsoft::WRL::ComPtr<ID3D12Device> held;
        uint32_t index = 0;
        {
            std::lock_guard<std::mutex> lock(mutex);
            if (!TrackingLocked())
                return 0;
            if (ReflexLocked())
                held = device;
            // "The frameID ... must strictly increment from that point forward". Where
            // Streamline gives the frame's index, the game's other calls come under it,
            // so the id is that index moved by an offset past every id given before.
            // Given before the sleeps: XeLL's takes it -- "xellSleep must be the first
            // XeLL API function called with a newly assigned frame_id value"
            // (xell_developer_guide_english.md, Sleep).
            if (streamlineIndex != 0)
            {
                if (streamlineIndex + offset <= lastBegun)
                    offset = lastBegun + 1 - streamlineIndex;
                index = streamlineIndex + offset;
                mapped = true;
            }
            else
            {
                index = lastBegun + 1;
                mapped = false;
            }
            lastBegun = index;
        }
        if (held != nullptr)
            SleepCall()(held.Get());
        {
            std::lock_guard<std::mutex> sleeping(xellMutex);
            if (xellSleeping != nullptr)
            {
                const xell_result_t result = XessFg::Get().XellSleep(xellSleeping, index);
                if (result == XELL_RESULT_SUCCESS)
                    xellSlept[index % 64].store(index);
                else if (!xellSleepFailed)
                {
                    xellSleepFailed = true;
                    LogLine("Low latency: xellSleep failed with result " + std::to_string(static_cast<int>(result))
                            + " (frame " + std::to_string(index) + ")");
                }
            }
        }
        // Every frame on each device it was set up on, enabled on the one taken: off it
        // inserts nothing. Under the contexts' lock, so that a swapchain made anew
        // meanwhile cannot let them go under the call.
        {
            std::lock_guard<std::mutex> antiLag(antiLagMutex);
            const int on = antiLagOn.load();
            if (g_antiLag12Ready)
                AMD::AntiLag2DX12::Update(&g_antiLag12, on == 12, 0);
            if (g_antiLag11Ready)
                AMD::AntiLag2DX11::Update(&g_antiLag11, on == 11, 0);
        }

        std::lock_guard<std::mutex> lock(mutex);
        if (!TrackingLocked())
            return 0;
        stamps[index % 256] = Stamps();
        stamps[index % 256].start = Now();
        MarkerLocked(index, kSimulationStart);
        return mapped ? streamlineIndex : index;
    }

    // Streamline's index moved by the offset where the frame came with one.
    uint32_t LowLatency::IdLocked(uint32_t frameIndex) const
    {
        return frameIndex != 0 && mapped ? frameIndex + offset : frameIndex;
    }

    // SIMULATION_END and the measure's stamp for the frame.
    void LowLatency::EndGameSimulation(uint32_t frameIndex)
    {
        std::lock_guard<std::mutex> lock(mutex);
        if (frameIndex == 0 || !TrackingLocked())
            return;
        const uint32_t id = IdLocked(frameIndex);
        stamps[id % 256].simulationEnd = Now();
        MarkerLocked(id, kSimulationEnd);
    }

    // "RENDERSUBMIT_START should come before any Direct3D calls are made for the
    // given frame" (nvapi.h).
    void LowLatency::OnGameFrameRendered(uint32_t frameIndex)
    {
        std::lock_guard<std::mutex> lock(mutex);
        if (frameIndex == 0 || !TrackingLocked())
            return;
        const uint32_t id = IdLocked(frameIndex);
        stamps[id % 256].rendered = Now();
        MarkerLocked(id, kRenderSubmitStart);
        rendered = id;
    }

    uint32_t LowLatency::TakeGameFrame()
    {
        const uint32_t frame = rendered.exchange(0);
        std::lock_guard<std::mutex> lock(mutex);
        if (frame == 0 || frame <= presented || !TrackingLocked())
            return 0;
        presented = frame;
        return frame;
    }

    // "RENDERSUBMIT_END should come before calling Present. PRESENT_START and END
    // should wrap the Present call" (nvapi.h); Anti-Lag 2's MarkEndOfFrameRendering
    // "once the game's main rendering workload has been submitted ... before the
    // FSR 3 Present call", and its data in the swapchain's private data.
    void LowLatency::BeforePresent(uint32_t frameIndex, IDXGISwapChain* swapChain)
    {
        std::lock_guard<std::mutex> lock(mutex);
        if (!TrackingLocked())
            return;
        std::lock_guard<std::mutex> antiLag(antiLagMutex);
        if (g_antiLag12Ready)
        {
            if (frameIndex != 0 && appliedAntiLag == 12)
                AMD::AntiLag2DX12::MarkEndOfFrameRendering(&g_antiLag12);
            if (swapChain != nullptr)
            {
                AntiLag2Data data = { &g_antiLag12, appliedAntiLag == 12 };
                swapChain->SetPrivateData(kAntiLag2Data, sizeof(data), &data);
            }
        }
        if (frameIndex == 0)
            return;
        stamps[frameIndex % 256].presentStart = Now();
        MarkerLocked(frameIndex, kRenderSubmitEnd);
        MarkerLocked(frameIndex, kPresentStart);
    }

    // PRESENT_END, then the measure's fence for the meter thread and the report when it is due.
    void LowLatency::AfterPresent(uint32_t frameIndex)
    {
        std::lock_guard<std::mutex> lock(mutex);
        if (frameIndex == 0 || !TrackingLocked())
            return;
        stamps[frameIndex % 256].presentEnd = Now();
        MarkerLocked(frameIndex, kPresentEnd);
        // The measure: the GPU's end of this Present's work on the queue, waited for
        // by the meter's thread.
        if (fence != nullptr && queue != nullptr && SUCCEEDED(queue->Signal(fence.Get(), fenceValue + 1)))
        {
            ++fenceValue;
            if (pending.size() < 64)
                pending.push_back({ fenceValue, stamps[frameIndex % 256].start, stamps[frameIndex % 256] });
            wake.notify_one();
        }
        ReportLocked();
    }

    // Waits, outside the lock, for each pending Present's fence and takes its latency and stages.
    void LowLatency::MeterThread()
    {
        std::unique_lock<std::mutex> lock(mutex);
        for (;;)
        {
            wake.wait(lock, [this]() { return !pending.empty() && fence != nullptr; });
            const Pending next = pending.front();
            Microsoft::WRL::ComPtr<ID3D12Fence> watched = fence;
            lock.unlock();
            if (watched->GetCompletedValue() < next.fenceValue
                && SUCCEEDED(watched->SetEventOnCompletion(next.fenceValue, meterEvent)))
                WaitForSingleObject(meterEvent, 1000);
            const int64_t done = Now();
            // A wait that timed out leaves the frame waiting, and the event set later
            // ends the next wait early: a frame counts only once the GPU is past it.
            const bool reached = watched->GetCompletedValue() >= next.fenceValue;
            lock.lock();
            if (reached && !pending.empty() && pending.front().fenceValue == next.fenceValue && fence == watched)
            {
                pending.pop_front();
                if (next.start != 0 && done > next.start && measured.size() < 1024)
                {
                    const double toMs = 1000.0 / Frequency();
                    measured.push_back(static_cast<double>(done - next.start) * toMs);
                    // The stages, where each of the frame's times was taken: a frame
                    // whose simulation or render thread came without a mark -- the
                    // first after a swapchain -- counts none.
                    const Stamps& s = next.stamps;
                    if (s.simulationEnd >= s.start && s.rendered != 0 && s.presentStart >= s.rendered
                        && s.presentEnd >= s.presentStart && done >= s.presentEnd)
                    {
                        cpuStages[0].push_back(static_cast<double>(s.simulationEnd - s.start) * toMs);
                        cpuStages[1].push_back(static_cast<double>(s.presentStart - s.simulationEnd) * toMs);
                        cpuStages[4].push_back(static_cast<double>(s.presentStart - s.rendered) * toMs);
                        cpuStages[2].push_back(static_cast<double>(s.presentEnd - s.presentStart) * toMs);
                        cpuStages[3].push_back(static_cast<double>(done - s.presentEnd) * toMs);
                    }
                }
            }
        }
    }

    // Every reportSeconds: the measure, on every GPU, and with Reflex the driver's own
    // report of the last 64 frames (NvAPI_D3D_GetLatency) with the frame's stages. The
    // driver's timestamps' unit is not stated in nvapi.h; it is taken from the same
    // report, where the GPU's frame time is given in microseconds as well.
    void LowLatency::ReportLocked()
    {
        const ULONGLONG now = GetTickCount64();
        const ULONGLONG every = static_cast<ULONGLONG>(Config().reportSeconds > 0 ? Config().reportSeconds : 10) * 1000;
        if (now - lastReport < every)
            return;
        lastReport = now;

        if (measured.size() >= 16)
        {
            std::vector<double> sorted(measured);
            std::sort(sorted.begin(), sorted.end());
            const char* running = streamline ? (Config().latencyBrake && Config().latencyBrakeWithDlssg
                                                    ? "Streamline's Reflex and the brake" : "Streamline's Reflex")
                                  : XellReducingLocked() ? "XeLL"
                                  : AntiLagLocked() ? (appliedAntiLag == 12 ? "Anti-Lag 2 (Direct3D 12)"
                                                                            : "Anti-Lag 2 (Direct3D 11)")
                                  : Config().latencyBrake ? (appliedMode == 1 ? "Reflex and the brake" : "the brake")
                                  : appliedMode == 1 ? "Reflex"
                                  : xell != nullptr ? "nothing (XeLL's markers only)" : "nothing";
            char line[192];
            sprintf_s(line, "Latency: from the frame's start to the GPU's end of its Present %.1f ms (median of %u"
                            " frames, 90th percentile %.1f), lowered by %s",
                      sorted[sorted.size() / 2], static_cast<unsigned>(sorted.size()),
                      sorted[sorted.size() * 9 / 10], running);
            LogLine(line);
            lastMedian = sorted[sorted.size() / 2];
            lastPercentile90 = sorted[sorted.size() * 9 / 10];
            lastCount = static_cast<int>(sorted.size());

            // Where the time went, each stage's median: the main thread's simulation, from
            // its end to the Present's start -- the main thread's rendering, the render
            // thread's work and its waits in the proxy -- the Present, and from its end to
            // the GPU's, where the frame's work still queued runs; last the render
            // thread's first call to the Present.
            static const char* const names[kLatencyStages] = { "simulation", "simulation's end to the Present",
                                                               "Present", "to the GPU's end",
                                                               "the render thread's first call to the Present" };
            if (cpuStages[0].size() >= 16)
            {
                std::string breakdown = "Latency: the frame's stages, medians in ms --";
                for (int s = 0; s < kLatencyStages; ++s)
                {
                    std::sort(cpuStages[s].begin(), cpuStages[s].end());
                    char part[64];
                    sprintf_s(part, " %s %.1f%s", names[s], cpuStages[s][cpuStages[s].size() / 2],
                              s + 1 < kLatencyStages ? "," : "");
                    breakdown += part;
                }
                LogLine(breakdown);
            }
        }
        measured.clear();
        for (std::vector<double>& stage : cpuStages)
            stage.clear();

        if (!ReflexLocked() || GetLatency() == nullptr)
            return;
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
        sprintf_s(line, "Low latency: NVIDIA's report, from the simulation's start to the GPU's end of the frame %.1f ms"
                        " (median of %u frames), the GPU's frame %.1f ms; low latency mode %s",
                  latencyMs, static_cast<unsigned>(latencies.size()), frameMs, appliedMode == 1 ? "on" : "off");
        LogLine(line);
        const char* names[kStages] = { "simulation", "to the render thread", "render submission", "to Present",
                                       "Present", "to the GPU", "GPU" };
        std::string breakdown = "Low latency: the frame's stages, medians in ms --";
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

    int LowLatency::Measure(double* median, double* percentile90) const
    {
        std::lock_guard<std::mutex> lock(mutex);
        if (median != nullptr)
            *median = lastMedian;
        if (percentile90 != nullptr)
            *percentile90 = lastPercentile90;
        return lastCount;
    }

    int LowLatency::Describe(char* buffer, int size) const
    {
        bool braking = false;
        {
            std::lock_guard<std::mutex> lock(mutex);
            braking = TrackingLocked() && (!streamline || Config().latencyBrakeWithDlssg) && !AntiLagLocked()
                      && !XellReducingLocked() && Config().latencyBrake;
        }
        std::lock_guard<std::mutex> lock(textLock);
        const std::string shown = braking ? text + "; latency brake on" : text;
        if (buffer != nullptr && size > 0)
            strncpy_s(buffer, static_cast<size_t>(size), shown.c_str(), _TRUNCATE);
        return kind;
    }
}
