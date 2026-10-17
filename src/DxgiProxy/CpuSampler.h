#pragma once

#include <windows.h>

#include <string>

// Where the game's main thread spends its time inside native code -- Unity's player,
// PhysX, the driver, Mono's runtime -- which no profiler of Unity's reads in KSP's
// release build. A thread of its own stops the main thread about a thousand times a
// second for a few microseconds: its instruction pointer, and a copy of the top of its
// stack. While the thread is stopped nothing that takes a lock is called -- it may
// hold the loader's or the heap's. After it runs on, the copy is scanned for values
// that point into a loaded module's code: the return addresses of its callers, and
// some stale ones, so a caller's share is an upper bound. Written to a file at the
// end (tools/cpu_samples.py names them with the modules' symbols): the modules, then
// one line a sample.
namespace redefinition
{
    class CpuSampler
    {
    public:
        static CpuSampler& Get();

        // From the main thread: samples it for seconds into path, then stops. False if
        // a run is going on or the thread cannot be opened.
        bool Start(int seconds, const std::wstring& path);

    private:
        CpuSampler() = default;
        static DWORD WINAPI ThreadProc(LPVOID self);
        void Run();

        HANDLE target = nullptr;
        int seconds = 0;
        std::wstring path;
        volatile LONG running = 0;
    };
}
