#pragma once

#include <windows.h>

#include <atomic>
#include <mutex>

// Where a frame's time goes: the CPU time of the
// game's main thread and of the thread that presents -- Unity's render
// thread -- each as a share of one core, and the load of the GPU's busiest 3D
// engine from Windows' own counters. Sampled once a second on a thread of its
// own, so the render thread never waits for it; logged every reportSeconds and
// handed to the mod through KspPerfLoad, so the log says whether the CPU or the
// GPU limits the frame rate.
namespace redefinition
{
    struct LoadSample
    {
        float mainThread = -1.0f;       // percent of one core; -1 when unknown
        float renderThread = -1.0f;
        float gpu = -1.0f;              // the busiest 3D engine of this process's adapter
        float gpuThisProcess = -1.0f;   // this process's share of that engine
    };

    class LoadMonitor
    {
    public:
        static LoadMonitor& Get();

        // From DllMain, with enabled from the ini: with enabled=0 the proxy
        // does nothing at all, and this monitor neither.
        void SetAllowed(bool allowed);
        bool Allowed() const { return allowed.load(); }

        // Into the log with every report: what the game holds, what Windows has
        // left, and this process's video memory against its budget.
        void ReportMemory();

        // The game's main thread, named by the mod from that thread.
        void RegisterMainThread();

        // The thread that presents, noted by the proxy on every Present, with
        // the report interval -- read there, on the thread that also reloads
        // the ini, so it cannot race the reload.
        void NoteRenderThread(int reportSeconds);

        // The last completed second. False before one has completed.
        bool Latest(LoadSample& sample) const;

    private:
        LoadMonitor() = default;

        // Opens a handle to the thread and puts it in place of the old one,
        // which it closes. Caller holds mutex.
        bool SwapHandleLocked(HANDLE& handle, DWORD id, bool& failureLogged, const char* what);

        void EnsureStarted();
        static DWORD WINAPI ThreadProc(LPVOID self);
        void Run();

        std::atomic<bool> allowed{ false };

        // Guards the handles, and is held while the sampler reads the thread
        // times through them, so a handle is never closed under it.
        mutable std::mutex mutex;
        HANDLE mainThread = nullptr;
        DWORD mainThreadId = 0;
        bool mainThreadFailed = false;
        HANDLE renderThread = nullptr;
        bool renderThreadFailed = false;
        std::atomic<DWORD> renderThreadId{ 0 };
        std::atomic<ULONGLONG> renderThreadTicks{ 0 };
        std::atomic<ULONGLONG> renderRetryTicks{ 0 };

        std::atomic<int> reportSeconds{ 10 };
        mutable std::atomic<ULONGLONG> lastReadTicks{ 0 };

        LoadSample latest;
        bool haveLatest = false;
        std::once_flag started;
    };
}
