#include "NvidiaGpu.h"

#include "Log.h"

#include <mutex>
#include <string>

namespace ksp
{
    namespace
    {
        // Declared from NVIDIA's NVAPI headers (MIT; nvapi.h, nvapi_lite_common.h
        // and nvapi_interface.h in the Streamline SDK's external/nvapi): the
        // functions' ids for nvapi64.dll's nvapi_QueryInterface, and the two
        // structures used. Versions are MAKE_NVAPI_VERSION -- the structure's size
        // with the version number in the high word.
        constexpr uint32_t kInitialize = 0x0150e828;
        constexpr uint32_t kEnumPhysicalGpus = 0xe5ac921f;
        constexpr uint32_t kGetLogicalGpuFromPhysicalGpu = 0xadd604d1;
        constexpr uint32_t kGetLogicalGpuInfo = 0x842b066e;
        constexpr uint32_t kGetArchInfo = 0xd8265d24;
        constexpr int kMaxPhysicalGpus = 64;   // NVAPI_MAX_PHYSICAL_GPUS
        // "No NVIDIA display driver, or NVIDIA GPU driving a display, was found"
        // (nvapi_lite_common.h).
        constexpr int kNvidiaDeviceNotFound = -6;   // NVAPI_NVIDIA_DEVICE_NOT_FOUND

        struct LogicalGpuData   // NV_LOGICAL_GPU_DATA_V1
        {
            uint32_t version;
            void* osAdapterId;
            uint32_t physicalGpuCount;
            void* physicalGpuHandles[kMaxPhysicalGpus];
            uint32_t reserved[8];
        };

        struct ArchInfo   // NV_GPU_ARCH_INFO_V2
        {
            uint32_t version;
            uint32_t architecture;
            uint32_t implementation;
            uint32_t revision;
        };

        uint32_t Version(size_t size, uint32_t version)
        {
            return static_cast<uint32_t>(size) | (version << 16);
        }

        using QueryInterfaceFn = void* (*)(uint32_t);
        using InitializeFn = int (*)();
        using EnumPhysicalGpusFn = int (*)(void** handles, uint32_t* count);
        using GetLogicalGpuFromPhysicalGpuFn = int (*)(void* physical, void** logical);
        using GetLogicalGpuInfoFn = int (*)(void* logical, LogicalGpuData* data);
        using GetArchInfoFn = int (*)(void* physical, ArchInfo* info);
    }

    NvidiaGpuAnswer NvidiaGpu(const LUID& adapter, NvidiaGpuInfo& result)
    {
        result = NvidiaGpuInfo();

        // The driver's own copy, from the system folder only, loaded once.
        static HMODULE nvapi = LoadLibraryExW(L"nvapi64.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
        if (nvapi == nullptr)
            return NvidiaGpuAnswer::NotNvidia;

        const auto query = reinterpret_cast<QueryInterfaceFn>(GetProcAddress(nvapi, "nvapi_QueryInterface"));
        const auto initialize = query != nullptr ? reinterpret_cast<InitializeFn>(query(kInitialize)) : nullptr;
        const auto enumerate = query != nullptr ? reinterpret_cast<EnumPhysicalGpusFn>(query(kEnumPhysicalGpus)) : nullptr;
        const auto logicalOf = query != nullptr
            ? reinterpret_cast<GetLogicalGpuFromPhysicalGpuFn>(query(kGetLogicalGpuFromPhysicalGpu)) : nullptr;
        const auto logicalInfo = query != nullptr ? reinterpret_cast<GetLogicalGpuInfoFn>(query(kGetLogicalGpuInfo)) : nullptr;
        const auto archInfo = query != nullptr ? reinterpret_cast<GetArchInfoFn>(query(kGetArchInfo)) : nullptr;
        if (initialize == nullptr || enumerate == nullptr || logicalOf == nullptr || logicalInfo == nullptr
            || archInfo == nullptr)
            return query != nullptr ? NvidiaGpuAnswer::Incomplete : NvidiaGpuAnswer::NotNvidia;

        const int initialised = initialize();
        if (initialised == kNvidiaDeviceNotFound)
            return NvidiaGpuAnswer::NotNvidia;
        if (initialised != 0)
            return NvidiaGpuAnswer::NoAnswer;

        void* handles[kMaxPhysicalGpus] = {};
        uint32_t count = 0;
        const int enumerated = enumerate(handles, &count);
        if (enumerated == kNvidiaDeviceNotFound)
            return NvidiaGpuAnswer::NotNvidia;
        if (enumerated != 0)
            return NvidiaGpuAnswer::NoAnswer;

        // A GPU that does not say which adapter it is -- a compute card, one in TCC
        // mode -- is passed by; only if KSP's is not found among the others does
        // its silence make the answer "no answer".
        bool unanswered = false;
        for (uint32_t gpu = 0; gpu < count && gpu < static_cast<uint32_t>(kMaxPhysicalGpus); ++gpu)
        {
            void* logical = nullptr;
            LUID id = {};
            LogicalGpuData data = {};
            data.version = Version(sizeof(data), 1);
            data.osAdapterId = &id;
            if (logicalOf(handles[gpu], &logical) != 0 || logicalInfo(logical, &data) != 0)
            {
                unanswered = true;
                continue;
            }
            if (id.LowPart != adapter.LowPart || id.HighPart != adapter.HighPart)
                continue;

            ArchInfo info = {};
            info.version = Version(sizeof(info), 2);
            if (archInfo(handles[gpu], &info) != 0)
                return NvidiaGpuAnswer::NoAnswer;

            result.architecture = info.architecture;
            result.implementation = info.implementation;
            return NvidiaGpuAnswer::Found;
        }
        return unanswered ? NvidiaGpuAnswer::NoAnswer : NvidiaGpuAnswer::NotNvidia;
    }

    namespace
    {
        // Attempts while NVAPI does not answer, ten seconds apart.
        constexpr int kAttempts = 6;
        constexpr ULONGLONG kAttemptSpacingMs = 10000;

        std::mutex g_gpuMutex;
        LUID g_adapter = {};
        bool g_adapterKnown = false;
        NvidiaGpuInfo g_gpu;
        NvidiaGpuAnswer g_answer = NvidiaGpuAnswer::NoAnswer;
        int g_attempts = 0;
        ULONGLONG g_nextAttempt = 0;

        // Caller holds g_gpuMutex. Logged when the answer differs from the last.
        void AskLocked()
        {
            const NvidiaGpuAnswer before = g_answer;
            ++g_attempts;
            g_nextAttempt = GetTickCount64() + kAttemptSpacingMs;
            g_answer = NvidiaGpu(g_adapter, g_gpu);
            if (g_attempts > 1 && g_answer == before)
                return;

            char text[128] = {};
            if (g_answer == NvidiaGpuAnswer::Found)
                sprintf_s(text, "NVIDIA GPU: architecture 0x%X, implementation 0x%X (NVAPI)", g_gpu.architecture,
                          g_gpu.implementation);
            else if (g_answer == NvidiaGpuAnswer::NotNvidia)
                sprintf_s(text, "KSP's adapter is not an NVIDIA GPU (NVAPI)");
            else if (g_answer == NvidiaGpuAnswer::Incomplete)
                sprintf_s(text, "NVAPI lacks a function this build asks for -- NVIDIA driver too old?");
            else
                sprintf_s(text, "NVAPI did not answer for KSP's adapter -- asked again up to %d times", kAttempts - 1);
            LogLine(text);
        }
    }

    void NoteSwapChainAdapter(const LUID& adapter)
    {
        std::lock_guard<std::mutex> lock(g_gpuMutex);
        if (g_adapterKnown && g_adapter.LowPart == adapter.LowPart && g_adapter.HighPart == adapter.HighPart)
            return;
        g_adapter = adapter;
        g_adapterKnown = true;
        g_answer = NvidiaGpuAnswer::NoAnswer;
        g_attempts = 0;
        AskLocked();
    }

    bool KnownNvidiaGpu(NvidiaGpuInfo& info)
    {
        std::lock_guard<std::mutex> lock(g_gpuMutex);
        if (!g_adapterKnown)
            return false;
        if (g_answer == NvidiaGpuAnswer::NoAnswer && g_attempts < kAttempts && GetTickCount64() >= g_nextAttempt)
            AskLocked();
        info = g_gpu;
        return g_answer == NvidiaGpuAnswer::Found;
    }
}
