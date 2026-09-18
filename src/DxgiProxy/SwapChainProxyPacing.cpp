// The proxy's pacing and frame times (SwapChainProxy): FSR's frame pacing
// knobs, the monitor's refresh rate from a thread of its own, the half-refresh
// limit, and the frame time report.

#include "SwapChainProxy.h"

#include "Config.h"
#include "FidelityFx.h"
#include "FrameGeneration.h"
#include "LoadMonitor.h"
#include "Log.h"

#include <mmsystem.h>

#include <FidelityFX/framegeneration/include/dx12/ffx_api_framegeneration_dx12.h>
#include <FidelityFX/framegeneration/include/ffx_framegeneration_api_types.h>

#include <algorithm>
#include <cmath>
#include <cwchar>
#include <mutex>
#include <string>
#include <vector>

namespace redefinition
{
    // How evenly generated frames are spaced. FSR's swapchain owns two worker
    // threads for this, and these are the settings it exposes, at AMD's defaults.
    // Hybrid spin -- "Less precise, but power saving" in AMD's header -- is a
    // setting (fgPacingHybridSpin).
    void SwapChainProxy::ApplyPacingTuning()
    {
        if (fgSwapChainContext == nullptr || !FidelityFx::Get().Ready())
            return;

        const ProxyConfig& cfg = Config();

        FfxApiSwapchainFramePacingTuning tuning = {};
        tuning.safetyMarginInMs = cfg.fgPacingSafetyMarginMs;
        tuning.varianceFactor = cfg.fgPacingVarianceFactor;
        tuning.allowHybridSpin = cfg.fgPacingHybridSpin;
        tuning.hybridSpinTime = static_cast<uint32_t>(cfg.fgPacingHybridSpinTime);
        tuning.allowWaitForSingleObjectOnFence = cfg.fgPacingWaitOnFence;

        ffxConfigureDescFrameGenerationSwapChainKeyValueDX12 configure = {};
        configure.header.type = FFX_API_CONFIGURE_DESC_TYPE_FRAMEGENERATIONSWAPCHAIN_KEYVALUE_DX12;
        configure.key = FFX_API_CONFIGURE_FG_SWAPCHAIN_KEY_FRAMEPACINGTUNING;
        configure.ptr = &tuning;

        const ffxReturnCode_t code =
            FidelityFx::Get().Api().Configure(&fgSwapChainContext, &configure.header);

        LogLine(code == FFX_API_RETURN_OK
                    ? "Frame pacing configured"
                    : "Frame pacing configuration failed, code " + std::to_string(static_cast<int>(code)));
    }

    namespace
    {
        // The refresh rate of the display a GDI device name belongs to, as
        // Windows keeps it: a fraction, so 59.94 and 143.98 Hz stay what they
        // are. 0 where it is not found.
        double ExactRefreshRate(const wchar_t* gdiDeviceName)
        {
            UINT32 pathCount = 0;
            UINT32 modeCount = 0;
            if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, &pathCount, &modeCount) != ERROR_SUCCESS)
                return 0.0;
            std::vector<DISPLAYCONFIG_PATH_INFO> paths(pathCount);
            std::vector<DISPLAYCONFIG_MODE_INFO> modes(modeCount);
            if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, &pathCount, paths.data(), &modeCount, modes.data(),
                                   nullptr) != ERROR_SUCCESS)
                return 0.0;

            for (UINT32 i = 0; i < pathCount; ++i)
            {
                DISPLAYCONFIG_SOURCE_DEVICE_NAME source = {};
                source.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
                source.header.size = sizeof(source);
                source.header.adapterId = paths[i].sourceInfo.adapterId;
                source.header.id = paths[i].sourceInfo.id;
                if (DisplayConfigGetDeviceInfo(&source.header) != ERROR_SUCCESS
                    || wcscmp(source.viewGdiDeviceName, gdiDeviceName) != 0)
                    continue;

                const DISPLAYCONFIG_RATIONAL& rate = paths[i].targetInfo.refreshRate;
                if (rate.Numerator != 0 && rate.Denominator != 0)
                    return static_cast<double>(rate.Numerator) / static_cast<double>(rate.Denominator);
            }
            return 0.0;
        }
    }

    namespace
    {
        double QueryRefreshRate(HWND window)
        {
            const HMONITOR monitor = MonitorFromWindow(window, MONITOR_DEFAULTTONEAREST);
            MONITORINFOEXW info = {};
            info.cbSize = sizeof(info);
            if (monitor == nullptr || !GetMonitorInfoW(monitor, &info))
                return 0.0;

            double rate = ExactRefreshRate(info.szDevice);
            if (rate <= 0.0)
            {
                // DEVMODE's frequency is whole hertz; 0 and 1 stand for the
                // hardware's default and are no rate.
                DEVMODEW mode = {};
                mode.dmSize = sizeof(mode);
                if (EnumDisplaySettingsW(info.szDevice, ENUM_CURRENT_SETTINGS, &mode) && mode.dmDisplayFrequency > 1)
                    rate = static_cast<double>(mode.dmDisplayFrequency);
            }
            return rate;
        }
    }

    // The refresh rate the window's monitor runs at, as a thread of its own last
    // found it: every two seconds and after a resize, so a window moved to another
    // monitor or a mode changed in Windows follows -- and never on the present
    // path, where the query, a round trip to the display driver, would land in a
    // frame. Only while the limit asks for it; paused otherwise
    // (PauseRefreshMonitor). 0 until the first answer; after a pause the last
    // one, until the query that resuming asks for returns.
    double SwapChainProxy::MonitorRefreshRate()
    {
        if (!refreshThread.joinable())
        {
            refreshThread = std::thread([this]()
            {
                std::unique_lock<std::mutex> lock(refreshMutex);
                while (!refreshStop)
                {
                    if (!refreshActive.load())
                    {
                        refreshWake.wait(lock, [this]() { return refreshStop || refreshActive.load(); });
                        continue;
                    }
                    refreshNow = false;
                    lock.unlock();
                    refreshRate.store(QueryRefreshRate(window));
                    lock.lock();
                    refreshWake.wait_for(lock, std::chrono::seconds(2),
                                         [this]() { return refreshStop || refreshNow || !refreshActive.load(); });
                }
            });
        }
        if (!refreshActive.exchange(true))
        {
            {
                std::lock_guard<std::mutex> lock(refreshMutex);
                refreshNow = true;
            }
            refreshWake.notify_all();
        }
        return refreshRate.load();
    }

    // fgHalfRefreshLimit, after a Present whose frame frame generation
    // generated from and which went out without VSync. The wait comes before
    // the game starts on its next frame, so it delays when input is read rather
    // than a finished frame on its way to the GPU.
    //
    // Slightly below half the refresh rate -- AMD: "The application should
    // ensure that the rendered frame rate is slightly below half the desired
    // output frame rate"; 2 % below -- which keeps real and generated
    // frames inside a VRR window too. A frame more than half a millisecond late
    // starts the schedule anew from now: catching up would bunch real frames
    // together, and FSR paces the generated ones from their spacing.
    void SwapChainProxy::LimitToHalfRefresh(UINT syncInterval)
    {
        const ProxyConfig& cfg = Config();
        if (!cfg.fgHalfRefreshLimit || cfg.fgVSync || syncInterval != 0 || ticksPerSecond == 0)
        {
            limitDue = 0;
            ReleaseLimitTimer();
            return;
        }
        // A frame without a generated one is not held but keeps the schedule:
        // frames that alternate are still held, and after a longer pause the
        // next held frame starts the schedule anew (below).
        if (!FrameGeneration::Get().GeneratedLastPresent())
        {
            // Long without one -- frame generation switched off in the game --:
            // the timer and the raised timer resolution go until it is back.
            if (++limitIdlePresents > 120)
                ReleaseLimitTimer();
            return;
        }
        limitIdlePresents = 0;

        const double refresh = MonitorRefreshRate();
        if (refresh <= 0.0)
        {
            limitDue = 0;
            return;
        }
        const double rate = 0.49 * refresh;
        const int64_t interval = static_cast<int64_t>(static_cast<double>(ticksPerSecond) / rate);
        if (!loggedLimit)
        {
            LogLine("Frame limit: " + std::to_string(rate) + " rendered frames a second at "
                    + std::to_string(refresh) + " Hz while generating");
            loggedLimit = true;
        }

        LARGE_INTEGER now = {};
        QueryPerformanceCounter(&now);
        if (limitDue == 0)
        {
            limitDue = now.QuadPart + interval;
            return;
        }

        // A high-resolution waitable timer, accurate to well below a
        // millisecond, for all but the last one. Without it (Windows before 10
        // 1803) an ordinary timer at the finest system timer resolution, and
        // two milliseconds left to the performance counter.
        if (!limitTimerTried)
        {
            limitTimerTried = true;
            limitTimer = CreateWaitableTimerExW(nullptr, nullptr, 0x00000002 /* CREATE_WAITABLE_TIMER_HIGH_RESOLUTION */,
                                                TIMER_ALL_ACCESS);
            limitTimerHighResolution = limitTimer != nullptr;
            if (limitTimer == nullptr)
            {
                limitTimer = CreateWaitableTimerExW(nullptr, nullptr, 0, TIMER_ALL_ACCESS);
                if (limitTimer != nullptr)
                    limitTimePeriod = timeBeginPeriod(1) == TIMERR_NOERROR;
                LogLine(limitTimer != nullptr
                            ? "Frame limit: no high-resolution timer, an ordinary one at 1 ms timer resolution"
                            : "Frame limit: no waitable timer, the wait spins");
            }
        }

        const int64_t margin = ticksPerSecond / (limitTimerHighResolution ? 1000 : 500);
        const int64_t remaining = limitDue - now.QuadPart;
        if (limitTimer != nullptr && remaining > margin)
        {
            LARGE_INTEGER dueTime = {};
            // Relative, in 100 ns.
            dueTime.QuadPart = -((remaining - margin) * 10000000 / ticksPerSecond);
            if (SetWaitableTimerEx(limitTimer, &dueTime, 0, nullptr, nullptr, nullptr, 0))
                WaitForSingleObject(limitTimer, 1000);
        }
        while (true)
        {
            QueryPerformanceCounter(&now);
            if (now.QuadPart >= limitDue)
                break;
            if (limitDue - now.QuadPart > margin)
                SwitchToThread();
            else
                YieldProcessor();
        }

        const int64_t slack = ticksPerSecond / 2000;
        limitDue = (now.QuadPart - limitDue > slack ? now.QuadPart : limitDue) + interval;
    }

    // The timer, the raised system timer resolution and the refresh rate's
    // queries, once the limit is off.
    void SwapChainProxy::ReleaseLimitTimer()
    {
        if (limitTimer != nullptr)
        {
            CloseHandle(limitTimer);
            limitTimer = nullptr;
        }
        if (limitTimePeriod)
        {
            timeEndPeriod(1);
            limitTimePeriod = false;
        }
        limitTimerTried = false;
        limitTimerHighResolution = false;
        PauseRefreshMonitor();
    }

    // From the present path: the thread stops asking, and nothing here waits for
    // it -- it may be inside a query.
    void SwapChainProxy::PauseRefreshMonitor()
    {
        if (refreshActive.exchange(false))
            refreshWake.notify_all();
    }

    // On the way out: wakes the thread and waits for it, at most the one query
    // it may be in.
    void SwapChainProxy::StopRefreshMonitor()
    {
        if (!refreshThread.joinable())
            return;
        {
            std::lock_guard<std::mutex> lock(refreshMutex);
            refreshStop = true;
        }
        refreshWake.notify_all();
        refreshThread.join();
    }

    void SwapChainProxy::RecordFrameTime()
    {
        LoadMonitor::Get().NoteRenderThread(Config().reportSeconds);

        int64_t now = 0;
        QueryPerformanceCounter(reinterpret_cast<LARGE_INTEGER*>(&now));

        if (lastTicks != 0 && ticksPerSecond != 0)
        {
            const double ms = 1000.0 * static_cast<double>(now - lastTicks)
                            / static_cast<double>(ticksPerSecond);
            frameTimesMs.push_back(ms);
        }

        lastTicks = now;

        if (lastReportTicks == 0)
            lastReportTicks = now;

        const int64_t interval = static_cast<int64_t>(Config().reportSeconds) * ticksPerSecond;
        if (interval > 0 && now - lastReportTicks >= interval)
        {
            ReportFrameTimes();
            lastReportTicks = now;
        }
    }

    void SwapChainProxy::ReportFrameTimes()
    {
        if (frameTimesMs.size() < 2)
            return;

        std::vector<double> sorted = frameTimesMs;
        std::sort(sorted.begin(), sorted.end());

        double sum = 0.0;
        for (const double value : sorted)
            sum += value;

        const size_t count = sorted.size();
        const double mean = sum / static_cast<double>(count);

        // From Present on the render thread, the one that presents, or from
        // the destructor: nothing to lock against -- FSR's presenter thread
        // does not take the proxy's lock.
        UINT presented = 0;
        DXGI_FRAME_STATISTICS stats = {};
        const bool gotPresented = Target() != nullptr && SUCCEEDED(Target()->GetLastPresentCount(&presented));
        const bool gotStats = Target() != nullptr && SUCCEEDED(Target()->GetFrameStatistics(&stats));

        // The proxy's counter sees only the frames the game renders; FSR presents
        // the generated ones itself. The swapchain's own present count shows the
        // difference: a ratio near 2 means every second frame is generated.
        double ratio = 0.0;
        if (gotPresented)
        {
            if (lastPresentCount != 0 && presented > lastPresentCount)
                ratio = static_cast<double>(presented - lastPresentCount) / static_cast<double>(count);

            lastPresentCount = presented;
        }

        // What the monitor got, from DXGI's own statistics: PresentCount is
        // the running count of images presented to the monitor, and
        // SyncRefreshCount the running count of its refreshes. Over the
        // interval between two reports they give displayed frames per second
        // and the refresh rate -- the figures that say whether generated
        // frames reach the screen, and how many the monitor can take.
        double displayedPerSecond = 0.0;
        double refreshHz = 0.0;
        if (gotStats)
        {
            if (lastStats.SyncQPCTime.QuadPart != 0 && ticksPerSecond != 0
                && stats.SyncQPCTime.QuadPart > lastStats.SyncQPCTime.QuadPart)
            {
                const double seconds = static_cast<double>(stats.SyncQPCTime.QuadPart - lastStats.SyncQPCTime.QuadPart)
                                     / static_cast<double>(ticksPerSecond);
                displayedPerSecond = static_cast<double>(stats.PresentCount - lastStats.PresentCount) / seconds;
                refreshHz = static_cast<double>(stats.SyncRefreshCount - lastStats.SyncRefreshCount) / seconds;
            }
            lastStats = stats;
        }

        char line[400] = {};
        FormatTo(line,
                  "%s: %zu frames, mean %.2f ms (%.1f fps), p50 %.2f, p95 %.2f, p99 %.2f, max %.2f"
                  ", presented/rendered %.2f, displayed %.0f/s at %.0f Hz, sync %u",
                  measureOnly ? "Baseline" : "D3D12 proxy",
                  count, mean, mean > 0.0 ? 1000.0 / mean : 0.0,
                  sorted[count / 2],
                  sorted[static_cast<size_t>(static_cast<double>(count) * 0.95)],
                  sorted[static_cast<size_t>(static_cast<double>(count) * 0.99)],
                  sorted.back(), ratio, displayedPerSecond, refreshHz, lastSyncInterval);

        LogLine(line);
        frameTimesMs.clear();
    }
}
