#include "LoadMonitor.h"

#include "Log.h"

#include <dxgi1_4.h>
#include <pdh.h>
#include <pdhmsg.h>
#include <psapi.h>
#include <wrl/client.h>

#include <algorithm>
#include <cstdint>
#include <cstdio>
#include <map>
#include <string>
#include <utility>
#include <vector>

namespace redefinition
{
    namespace
    {
        uint64_t ToTicks(const FILETIME& time)
        {
            return (static_cast<uint64_t>(time.dwHighDateTime) << 32) | time.dwLowDateTime;
        }

        // The CPU time a thread has used, kernel and user, in 100 ns units.
        // Windows charges it by clock tick, so one second is a few percent
        // coarse and a thread that runs in bursts locked to the tick can be
        // off by more; the ten-second log line is the figure to go by.
        bool CpuTime(HANDLE thread, uint64_t& ticks)
        {
            FILETIME creation = {};
            FILETIME exit = {};
            FILETIME kernel = {};
            FILETIME user = {};
            if (thread == nullptr || !GetThreadTimes(thread, &creation, &exit, &kernel, &user))
                return false;

            ticks = ToTicks(kernel) + ToTicks(user);
            return true;
        }

        // One thread's share of a core since its last sample; -1 on the first
        // sample of a handle, or when it could not be read.
        struct ThreadClock
        {
            HANDLE sampled = nullptr;
            uint64_t last = 0;

            float Sample(HANDLE thread, bool read, uint64_t now, double seconds)
            {
                if (!read)
                {
                    sampled = nullptr;
                    return -1.0f;
                }

                if (thread != sampled || seconds <= 0.0 || now < last)
                {
                    sampled = thread;
                    last = now;
                    return -1.0f;
                }

                const double busy = static_cast<double>(now - last) / (seconds * 1.0e7) * 100.0;
                last = now;
                return static_cast<float>(std::min(busy, 100.0));
            }
        };

        // Windows' performance counters, loaded by hand like every optional
        // system library here: a proxy that cannot find one still has to let
        // the game start.
        struct Pdh
        {
            using OpenQueryFn = PDH_STATUS(WINAPI*)(LPCWSTR, DWORD_PTR, PDH_HQUERY*);
            using AddEnglishCounterFn = PDH_STATUS(WINAPI*)(PDH_HQUERY, LPCWSTR, DWORD_PTR, PDH_HCOUNTER*);
            using CollectFn = PDH_STATUS(WINAPI*)(PDH_HQUERY);
            using GetArrayFn = PDH_STATUS(WINAPI*)(PDH_HCOUNTER, DWORD, LPDWORD, LPDWORD,
                                                   PPDH_FMT_COUNTERVALUE_ITEM_W);

            OpenQueryFn openQuery = nullptr;
            AddEnglishCounterFn addEnglishCounter = nullptr;
            CollectFn collect = nullptr;
            GetArrayFn getArray = nullptr;

            bool Load()
            {
                const HMODULE module = LoadLibraryExW(L"pdh.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
                if (module == nullptr)
                    return false;

                openQuery = reinterpret_cast<OpenQueryFn>(GetProcAddress(module, "PdhOpenQueryW"));
                addEnglishCounter = reinterpret_cast<AddEnglishCounterFn>(GetProcAddress(module, "PdhAddEnglishCounterW"));
                collect = reinterpret_cast<CollectFn>(GetProcAddress(module, "PdhCollectQueryData"));
                getArray = reinterpret_cast<GetArrayFn>(GetProcAddress(module, "PdhGetFormattedCounterArrayW"));
                return openQuery && addEnglishCounter && collect && getArray;
            }
        };

        struct Engine
        {
            double total = 0.0;
            double own = 0.0;
        };

        // "luid_<adapter>_phys_<n>_eng_<n>_engtype_3D": the adapter is the part
        // before "_phys".
        std::wstring AdapterOf(const std::wstring& engine)
        {
            const size_t phys = engine.find(L"_phys");
            return phys == std::wstring::npos ? engine : engine.substr(0, phys);
        }

        // One instance per process and engine, named
        // "pid_<pid>_luid_<adapter>_phys_<n>_eng_<n>_engtype_3D". Summed over
        // processes per engine -- the name from "luid_" on -- each engine's
        // load. The figure is the busiest engine of the adapter this process
        // uses most -- on a machine with two GPUs not another adapter's -- and
        // this process's share of that engine. Engines are not added together:
        // two at half load are not one at full.
        bool ReadGpu(const Pdh& pdh, PDH_HCOUNTER counter, const std::wstring& ownPrefix,
                     float& busiest, float& own)
        {
            DWORD size = 0;
            DWORD count = 0;
            if (static_cast<DWORD>(pdh.getArray(counter, PDH_FMT_DOUBLE, &size, &count, nullptr)) != PDH_MORE_DATA)
                return false;

            // Instances come and go; if more arrived since the size was asked
            // for, this reading is skipped.
            std::vector<unsigned char> buffer(size);
            auto* items = reinterpret_cast<PDH_FMT_COUNTERVALUE_ITEM_W*>(buffer.data());
            if (pdh.getArray(counter, PDH_FMT_DOUBLE, &size, &count, items) != ERROR_SUCCESS)
                return false;

            std::map<std::wstring, Engine> engines;
            for (DWORD i = 0; i < count; ++i)
            {
                const PDH_FMT_COUNTERVALUE& value = items[i].FmtValue;
                if (value.CStatus != PDH_CSTATUS_VALID_DATA && value.CStatus != PDH_CSTATUS_NEW_DATA)
                    continue;

                const std::wstring name = items[i].szName;
                const size_t luid = name.find(L"luid_");
                if (luid == std::wstring::npos)
                    continue;

                Engine& engine = engines[name.substr(luid)];
                engine.total += value.doubleValue;
                if (name.compare(0, ownPrefix.size(), ownPrefix) == 0)
                    engine.own += value.doubleValue;
            }

            std::map<std::wstring, double> ownByAdapter;
            for (const auto& engine : engines)
                ownByAdapter[AdapterOf(engine.first)] += engine.second.own;

            std::wstring adapter;
            double most = 0.0;
            for (const auto& entry : ownByAdapter)
            {
                if (entry.second > most)
                {
                    most = entry.second;
                    adapter = entry.first;
                }
            }

            // Nothing of this process on any engine -- idle, or not rendering
            // yet: then the busiest engine anywhere.
            const Engine* top = nullptr;
            for (const auto& engine : engines)
            {
                if (!adapter.empty() && AdapterOf(engine.first) != adapter)
                    continue;
                if (top == nullptr || engine.second.total > top->total)
                    top = &engine.second;
            }

            if (top == nullptr)
                return false;

            busiest = static_cast<float>(std::min(top->total, 100.0));
            own = static_cast<float>(std::min(top->own, 100.0));
            return true;
        }

        // The seconds behind one figure of the log line.
        struct Average
        {
            double sum = 0.0;
            double seconds = 0.0;

            void Add(float value, double duration)
            {
                if (value < 0.0f || duration <= 0.0)
                    return;
                sum += static_cast<double>(value) * duration;
                seconds += duration;
            }

            std::string Text() const
            {
                if (seconds <= 0.0)
                    return "n/a";
                char buffer[16] = {};
                FormatTo(buffer, "%.0f %%", sum / seconds);
                return buffer;
            }
        };
    }

    LoadMonitor& LoadMonitor::Get()
    {
        static LoadMonitor instance;
        return instance;
    }

    void LoadMonitor::SetAllowed(bool value)
    {
        allowed.store(value);
    }

    // On failure the old handle goes as well, and the failure is logged once.
    // The caller holds mutex, the lock the sampler reads the thread times
    // under, so the handle it closes is not in use.
    bool LoadMonitor::SwapHandleLocked(HANDLE& handle, DWORD id, bool& failureLogged, const char* what)
    {
        const HANDLE opened = OpenThread(THREAD_QUERY_LIMITED_INFORMATION, FALSE, id);
        if (opened == nullptr)
        {
            if (!failureLogged)
                LogLine(std::string("Load: the ") + what + " could not be opened ("
                        + Hr(HRESULT_FROM_WIN32(GetLastError())) + ") -- its load is not measured");
            failureLogged = true;

            // The old one is another thread's -- for the render thread one
            // quiet for two seconds -- and would pass its near-zero load off
            // as this one's, so the load is unknown.
            if (handle != nullptr)
                CloseHandle(handle);
            handle = nullptr;
            return false;
        }

        if (handle != nullptr)
            CloseHandle(handle);
        handle = opened;
        return true;
    }

    void LoadMonitor::RegisterMainThread()
    {
        if (!allowed.load())
            return;

        // Reached through P/Invoke: nothing may leave it.
        try
        {
            const DWORD id = GetCurrentThreadId();
            {
                std::lock_guard<std::mutex> lock(mutex);
                if (mainThreadId == id)
                    return;
                // Not remembered as registered on failure: a later call may
                // succeed.
                if (!SwapHandleLocked(mainThread, id, mainThreadFailed, "main thread"))
                    return;
                mainThreadId = id;
            }
            EnsureStarted();
        }
        catch (...)
        {
        }
    }

    void LoadMonitor::NoteRenderThread(int seconds)
    {
        if (!allowed.load(std::memory_order_relaxed))
            return;

        reportSeconds.store(seconds, std::memory_order_relaxed);

        const DWORD id = GetCurrentThreadId();
        const ULONGLONG now = GetTickCount64();
        const DWORD current = renderThreadId.load(std::memory_order_relaxed);
        if (current == id)
        {
            renderThreadTicks.store(now, std::memory_order_relaxed);
            return;
        }

        // Another thread presents. It takes over only once the current one
        // has been quiet for two seconds, and after a failed open only five
        // seconds later: two threads presenting in turn, or a handle Windows
        // refuses, must not cost an OpenThread every frame.
        if (current != 0 && now - renderThreadTicks.load(std::memory_order_relaxed) < 2000)
            return;
        if (now < renderRetryTicks.load(std::memory_order_relaxed))
            return;

        // Called from Present: nothing may leave it.
        try
        {
            {
                std::lock_guard<std::mutex> lock(mutex);
                if (!SwapHandleLocked(renderThread, id, renderThreadFailed, "render thread"))
                {
                    renderRetryTicks.store(now + 5000, std::memory_order_relaxed);
                    return;
                }
                renderThreadId.store(id, std::memory_order_relaxed);
                renderThreadTicks.store(now, std::memory_order_relaxed);
            }
            EnsureStarted();
        }
        catch (...)
        {
        }
    }

    bool LoadMonitor::Latest(LoadSample& sample) const
    {
        lastReadTicks.store(GetTickCount64(), std::memory_order_relaxed);
        std::lock_guard<std::mutex> lock(mutex);
        sample = latest;
        return haveLatest;
    }

    void LoadMonitor::EnsureStarted()
    {
        // Never from DllMain: both callers run on the game's own threads. And
        // CreateThread rather than std::thread: nothing in here may throw into
        // Unity or Mono -- without the thread there is simply no load line.
        try
        {
            std::call_once(started, [this]
            {
                const HANDLE thread = CreateThread(nullptr, 0, &LoadMonitor::ThreadProc, this, 0, nullptr);
                if (thread != nullptr)
                    CloseHandle(thread);
                else
                    LogLine("Load: the measuring thread could not be started");
            });
        }
        catch (...)
        {
        }
    }

    DWORD WINAPI LoadMonitor::ThreadProc(LPVOID self)
    {
        // Nothing may leave a thread made with CreateThread: it would end KSP.
        try
        {
            static_cast<LoadMonitor*>(self)->Run();
        }
        catch (...)
        {
        }
        return 0;
    }

    void LoadMonitor::Run()
    {
        Pdh pdh;
        PDH_HQUERY query = nullptr;
        PDH_HCOUNTER counter = nullptr;

        // PdhAddEnglishCounter, because Windows names its counters in the
        // system's language.
        const bool gpuCounters = pdh.Load()
            && pdh.openQuery(nullptr, 0, &query) == ERROR_SUCCESS
            && pdh.addEnglishCounter(query, L"\\GPU Engine(*engtype_3D)\\Utilization Percentage", 0, &counter)
                   == ERROR_SUCCESS
            && pdh.collect(query) == ERROR_SUCCESS;
        if (!gpuCounters)
            LogLine("Load: Windows' GPU engine counters could not be opened -- GPU load is not measured");

        const std::wstring ownPrefix = L"pid_" + std::to_wstring(GetCurrentProcessId()) + L"_";

        LARGE_INTEGER frequency = {};
        QueryPerformanceFrequency(&frequency);
        LARGE_INTEGER last = {};
        QueryPerformanceCounter(&last);

        ThreadClock mainClock;
        ThreadClock renderClock;
        Average mainAverage;
        Average renderAverage;
        Average gpuAverage;
        Average ownAverage;
        double windowSeconds = 0.0;
        double sinceCollect = 0.0;

        for (;;)
        {
            Sleep(1000);

            // A failure in here must not take the game with it: the second is
            // lost, the monitor goes on.
            try
            {
                LARGE_INTEGER now = {};
                QueryPerformanceCounter(&now);
                const double seconds = static_cast<double>(now.QuadPart - last.QuadPart)
                                     / static_cast<double>(frequency.QuadPart);
                last = now;

                HANDLE main = nullptr;
                HANDLE render = nullptr;
                uint64_t mainTicks = 0;
                uint64_t renderTicks = 0;
                bool mainRead = false;
                bool renderRead = false;
                {
                    std::lock_guard<std::mutex> lock(mutex);
                    main = mainThread;
                    render = renderThread;
                    mainRead = CpuTime(mainThread, mainTicks);
                    renderRead = CpuTime(renderThread, renderTicks);
                }

                LoadSample sample;
                sample.mainThread = mainClock.Sample(main, mainRead, mainTicks, seconds);
                sample.renderThread = renderClock.Sample(render, renderRead, renderTicks, seconds);

                // The GPU counters cost a walk over every process's engines, so
                // they are collected while the figures are being read -- the
                // mod's Debug tab, the harness -- and at the end of each log
                // window; a reading averages over the time since the last one.
                // A second without a reading has no GPU figure rather than an
                // old one.
                const double report = static_cast<double>(std::max(1, reportSeconds.load(std::memory_order_relaxed)));
                const bool windowEnds = windowSeconds + seconds >= report;
                const bool readRecently = GetTickCount64() - lastReadTicks.load(std::memory_order_relaxed) < 5000;
                sinceCollect += seconds;
                if (gpuCounters && (windowEnds || readRecently) && pdh.collect(query) == ERROR_SUCCESS)
                {
                    float gpu = -1.0f;
                    float gpuOwn = -1.0f;
                    if (ReadGpu(pdh, counter, ownPrefix, gpu, gpuOwn))
                    {
                        gpuAverage.Add(gpu, sinceCollect);
                        ownAverage.Add(gpuOwn, sinceCollect);

                        // A reading after a gap averages the whole gap: right for
                        // the log window, wrong as "the last second".
                        if (sinceCollect < 1.5)
                        {
                            sample.gpu = gpu;
                            sample.gpuThisProcess = gpuOwn;
                        }
                    }
                    sinceCollect = 0.0;
                }

                // What the game holds: its own memory, what Windows has left, and
                // this process's video memory against the budget the driver gives
                // it. A frame's texture that cannot be made (E_OUTOFMEMORY) is the
                // end of the run, and the log should say which of the three ran out.
                if (windowEnds)
                    ReportMemory();

                {
                    std::lock_guard<std::mutex> lock(mutex);
                    latest = sample;
                    haveLatest = haveLatest || sample.mainThread >= 0.0f || sample.renderThread >= 0.0f
                              || sample.gpu >= 0.0f;
                }

                mainAverage.Add(sample.mainThread, seconds);
                renderAverage.Add(sample.renderThread, seconds);
                windowSeconds += seconds;

                if (windowEnds)
                {
                    char span[16] = {};
                    FormatTo(span, "%.1f", windowSeconds);
                    LogLine(std::string("Load, last ") + span + " s: main thread " + mainAverage.Text()
                            + ", render thread " + renderAverage.Text() + ", GPU " + gpuAverage.Text()
                            + " (this process " + ownAverage.Text() + ")");

                    mainAverage = Average();
                    renderAverage = Average();
                    gpuAverage = Average();
                    ownAverage = Average();
                    windowSeconds = 0.0;
                }
            }
            catch (...)
            {
            }
        }
    }
}

namespace redefinition
{
    namespace
    {
        std::string Gigabytes(unsigned long long bytes)
        {
            char text[32] = {};
            FormatTo(text, "%.1f", static_cast<double>(bytes) / (1024.0 * 1024.0 * 1024.0));
            return text;
        }

        // The adapter this process renders on, kept for as long as the monitor runs:
        // enumerating it every time would be a factory per report.
        Microsoft::WRL::ComPtr<IDXGIAdapter3> VideoAdapter()
        {
            static Microsoft::WRL::ComPtr<IDXGIAdapter3> adapter = []() {
                Microsoft::WRL::ComPtr<IDXGIFactory1> factory;
                Microsoft::WRL::ComPtr<IDXGIAdapter3> found;
                if (FAILED(CreateDXGIFactory1(IID_PPV_ARGS(&factory))))
                    return found;
                Microsoft::WRL::ComPtr<IDXGIAdapter1> first;
                if (SUCCEEDED(factory->EnumAdapters1(0, &first)))
                    first.As(&found);
                return found;
            }();
            return adapter;
        }
    }

    // Steam and Windows both put a game into a job object at times, and a job can
    // cap how much its processes may commit. Said once: it does not change.
    void LoadMonitor::ReportJobLimitOnce()
    {
        static bool said = false;
        if (said)
            return;
        said = true;

        BOOL inJob = FALSE;
        if (!IsProcessInJob(GetCurrentProcess(), nullptr, &inJob) || !inJob)
        {
            LogLine("Memory: the game runs in no job object, so nothing caps what it may commit but Windows itself.");
            return;
        }

        JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits = {};
        DWORD written = 0;
        if (!QueryInformationJobObject(nullptr, JobObjectExtendedLimitInformation, &limits, sizeof(limits), &written))
        {
            LogLine("Memory: the game runs in a job object whose limits could not be read.");
            return;
        }

        std::string line = "Memory: the game runs in a job object";
        if ((limits.BasicLimitInformation.LimitFlags & JOB_OBJECT_LIMIT_PROCESS_MEMORY) != 0)
            line += ", which caps a process at " + Gigabytes(limits.ProcessMemoryLimit) + " GB committed";
        if ((limits.BasicLimitInformation.LimitFlags & JOB_OBJECT_LIMIT_JOB_MEMORY) != 0)
            line += ", and the job as a whole at " + Gigabytes(limits.JobMemoryLimit) + " GB";
        if ((limits.BasicLimitInformation.LimitFlags
             & (JOB_OBJECT_LIMIT_PROCESS_MEMORY | JOB_OBJECT_LIMIT_JOB_MEMORY)) == 0)
            line += " with no memory limit of its own";
        LogLine(line + ".");
    }

    void LoadMonitor::ReportMemory()
    {
        ReportJobLimitOnce();

        std::string line = "Memory: ";

        PROCESS_MEMORY_COUNTERS_EX counters = {};
        counters.cb = sizeof(counters);
        if (GetProcessMemoryInfo(GetCurrentProcess(),
                                 reinterpret_cast<PROCESS_MEMORY_COUNTERS*>(&counters), sizeof(counters)))
            line += "the game holds " + Gigabytes(counters.WorkingSetSize) + " GB in memory, "
                    + Gigabytes(counters.PrivateUsage) + " GB committed";
        else
            line += "the game's own use is unknown";

        // ullAvailPageFile is what this process may still commit, which a job
        // object can cap below the system's own limit; ullTotalPageFile is that
        // cap. Where the two say less than Windows' commit limit, something holds
        // the game to a ceiling of its own.
        MEMORYSTATUSEX status = {};
        status.dwLength = sizeof(status);
        if (GlobalMemoryStatusEx(&status))
            line += "; Windows has " + Gigabytes(status.ullAvailPhys) + " GB of "
                    + Gigabytes(status.ullTotalPhys) + " GB free, and the game may commit "
                    + Gigabytes(status.ullAvailPageFile) + " GB more of its "
                    + Gigabytes(status.ullTotalPageFile) + " GB limit";

        Microsoft::WRL::ComPtr<IDXGIAdapter3> adapter = VideoAdapter();
        if (adapter)
        {
            DXGI_QUERY_VIDEO_MEMORY_INFO video = {};
            if (SUCCEEDED(adapter->QueryVideoMemoryInfo(0, DXGI_MEMORY_SEGMENT_GROUP_LOCAL, &video)))
                line += "; video memory " + Gigabytes(video.CurrentUsage) + " GB of "
                        + Gigabytes(video.Budget) + " GB budget";
        }

        LogLine(line);
    }
}
