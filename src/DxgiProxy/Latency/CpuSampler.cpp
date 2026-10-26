#include "CpuSampler.h"

#include "Log.h"

#include <psapi.h>
#include <timeapi.h>
#include <winternl.h>

#include <algorithm>
#include <cstdio>
#include <cstring>
#include <vector>

#pragma comment(lib, "winmm.lib")
#pragma comment(lib, "psapi.lib")

namespace redefinition
{
    namespace
    {
        // The bytes of the stack copied from its top a sample, and the most module addresses kept from them.
        constexpr size_t kStackCopy = 64 * 1024;
        constexpr size_t kCallers = 64;

        // A loaded module's address range, for telling return addresses from other values.
        struct Module
        {
            // Where its image begins and ends, and its path.
            uint64_t base;
            uint64_t end;
            std::wstring name;
        };

        // A plain copy, guarded: the thread is stopped, its stack is there, but the
        // bound is only as good as the TEB's.
        bool CopyStack(void* into, const void* from, size_t size)
        {
            __try
            {
                memcpy(into, from, size);
                return true;
            }
            __except (EXCEPTION_EXECUTE_HANDLER)
            {
                return false;
            }
        }

        // NtQueryInformationThread's signature, the function looked up in ntdll.dll by name.
        typedef NTSTATUS(NTAPI* QueryThread)(HANDLE, THREADINFOCLASS, PVOID, ULONG, PULONG);

        // THREAD_BASIC_INFORMATION as NtQueryInformationThread fills it: only the TEB's address is read.
        struct BasicInformation
        {
            // In the layout ntdll writes, the TEB's address second.
            NTSTATUS exitStatus;
            PVOID tebBase;
            CLIENT_ID clientId;
            ULONG_PTR affinity;
            LONG priority;
            LONG basePriority;
        };

        // The top of the thread's stack, from its TEB (NT_TIB::StackBase).
        uint64_t StackBase(HANDLE thread)
        {
            const auto query = reinterpret_cast<QueryThread>(
                GetProcAddress(GetModuleHandleW(L"ntdll.dll"), "NtQueryInformationThread"));
            BasicInformation information = {};
            if (query == nullptr || query(thread, static_cast<THREADINFOCLASS>(0), &information, sizeof(information), nullptr) != 0
                || information.tebBase == nullptr)
                return 0;
            return reinterpret_cast<const NT_TIB*>(information.tebBase)->StackBase != nullptr
                       ? reinterpret_cast<uint64_t>(reinterpret_cast<const NT_TIB*>(information.tebBase)->StackBase)
                       : 0;
        }

        // The process's loaded modules with their address ranges and paths, sorted by base.
        std::vector<Module> Modules()
        {
            std::vector<Module> modules;
            HMODULE handles[1024];
            DWORD needed = 0;
            if (!EnumProcessModules(GetCurrentProcess(), handles, sizeof(handles), &needed))
                return modules;
            const DWORD count = std::min<DWORD>(needed / sizeof(HMODULE), 1024);
            for (DWORD i = 0; i < count; ++i)
            {
                MODULEINFO info = {};
                wchar_t name[MAX_PATH] = {};
                if (!GetModuleInformation(GetCurrentProcess(), handles[i], &info, sizeof(info)))
                    continue;
                GetModuleFileNameW(handles[i], name, MAX_PATH);
                const uint64_t base = reinterpret_cast<uint64_t>(info.lpBaseOfDll);
                modules.push_back({ base, base + info.SizeOfImage, name });
            }
            std::sort(modules.begin(), modules.end(), [](const Module& a, const Module& b) { return a.base < b.base; });
            return modules;
        }

        // Whether the address lies in one of the modules, which are sorted by base.
        bool InModule(const std::vector<Module>& modules, uint64_t address)
        {
            auto after = std::upper_bound(modules.begin(), modules.end(), address,
                                          [](uint64_t a, const Module& m) { return a < m.base; });
            if (after == modules.begin())
                return false;
            --after;
            return address < after->end;
        }
    }

    // The one instance, for the process.
    CpuSampler& CpuSampler::Get()
    {
        static CpuSampler instance;
        return instance;
    }

    bool CpuSampler::Start(int forSeconds, const std::wstring& into)
    {
        if (InterlockedCompareExchange(&running, 1, 0) != 0)
            return false;
        target = OpenThread(THREAD_SUSPEND_RESUME | THREAD_GET_CONTEXT | THREAD_QUERY_INFORMATION, FALSE, GetCurrentThreadId());
        if (target == nullptr)
        {
            InterlockedExchange(&running, 0);
            return false;
        }
        seconds = forSeconds;
        path = into;
        HANDLE thread = CreateThread(nullptr, 0, ThreadProc, this, 0, nullptr);
        if (thread == nullptr)
        {
            CloseHandle(target);
            target = nullptr;
            InterlockedExchange(&running, 0);
            return false;
        }
        CloseHandle(thread);
        return true;
    }

    // The sampling thread's entry.
    DWORD WINAPI CpuSampler::ThreadProc(LPVOID self)
    {
        static_cast<CpuSampler*>(self)->Run();
        return 0;
    }

    // The sampling: about a thousand stops of the main thread a second, each its instruction pointer and the
    // values on its stack that point into a module, then the file; the handle closed and the sampler free again.
    void CpuSampler::Run()
    {
        SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_TIME_CRITICAL);
        timeBeginPeriod(1);
        std::vector<Module> modules = Modules();
        const uint64_t stackBase = StackBase(target);
        std::vector<uint8_t> copy(kStackCopy);
        std::vector<std::vector<uint64_t>> samples;
        samples.reserve(static_cast<size_t>(seconds) * 1100);
        const ULONGLONG until = GetTickCount64() + static_cast<ULONGLONG>(seconds) * 1000;
        ULONGLONG modulesAt = GetTickCount64();
        int failed = 0;
        while (GetTickCount64() < until)
        {
            Sleep(1);
            // Modules come and go with the scenes: looked up again every second, with
            // the thread running.
            if (GetTickCount64() - modulesAt > 1000)
            {
                modules = Modules();
                modulesAt = GetTickCount64();
            }
            if (SuspendThread(target) == static_cast<DWORD>(-1))
            {
                ++failed;
                continue;
            }
            CONTEXT context = {};
            context.ContextFlags = CONTEXT_CONTROL;
            const bool gotContext = GetThreadContext(target, &context) != FALSE;
            size_t copied = 0;
            if (gotContext && stackBase > context.Rsp)
            {
                copied = static_cast<size_t>(std::min<uint64_t>(kStackCopy, stackBase - context.Rsp)) & ~static_cast<size_t>(7);
                if (!CopyStack(copy.data(), reinterpret_cast<const void*>(context.Rsp), copied))
                    copied = 0;
            }
            ResumeThread(target);
            if (!gotContext)
            {
                ++failed;
                continue;
            }
            std::vector<uint64_t> sample;
            sample.push_back(context.Rip);
            const uint64_t* words = reinterpret_cast<const uint64_t*>(copy.data());
            for (size_t i = 0; i < copied / 8 && sample.size() <= kCallers; ++i)
                if (InModule(modules, words[i]))
                    sample.push_back(words[i]);
            samples.push_back(std::move(sample));
        }
        timeEndPeriod(1);

        FILE* file = nullptr;
        if (_wfopen_s(&file, path.c_str(), L"w") == 0 && file != nullptr)
        {
            for (size_t i = 0; i < modules.size(); ++i)
                fprintf(file, "M %llx %llx %ls\n", static_cast<unsigned long long>(modules[i].base),
                        static_cast<unsigned long long>(modules[i].end - modules[i].base), modules[i].name.c_str());
            for (const std::vector<uint64_t>& sample : samples)
            {
                fputc('S', file);
                for (uint64_t address : sample)
                    fprintf(file, " %llx", static_cast<unsigned long long>(address));
                fputc('\n', file);
            }
            fclose(file);
        }
        LogLine("CPU sampler: " + std::to_string(samples.size()) + " samples of the main thread over " + std::to_string(seconds)
                + " s, " + std::to_string(failed) + " missed");
        CloseHandle(target);
        target = nullptr;
        InterlockedExchange(&running, 0);
    }
}
