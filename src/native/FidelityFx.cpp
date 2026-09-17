#include "FidelityFx.h"

#include "FileUtil.h"
#include "Log.h"

#include <string>

namespace ksp
{
    namespace
    {
        // Next to the executable, where the player package puts it.
        const wchar_t* kRuntimeName = L"amd_fidelityfx_framegeneration_dx12.dll";
    }

    FidelityFx& FidelityFx::Get()
    {
        static FidelityFx instance;
        return instance;
    }

    bool FidelityFx::Ready()
    {
        std::call_once(loadOnce, [this]() { Load(); });
        return ready;
    }

    void FidelityFx::Load()
    {
        const std::wstring beside = ExecutableDirectory() + L"\\" + kRuntimeName;
        module = LoadLibraryExW(beside.c_str(), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);

        if (module == nullptr)
        {
            LogLine("FidelityFX runtime not found next to the executable -- FSR frame generation unavailable, "
                    "presentation continues unchanged");
            return;
        }

        ffxLoadFunctions(&api, module);

        // A module that loads without the exports is treated as missing: it would
        // fail later and further from the cause.
        ready = api.CreateContext != nullptr && api.DestroyContext != nullptr
             && api.Configure != nullptr && api.Query != nullptr && api.Dispatch != nullptr;

        if (!ready)
        {
            LogLine("FidelityFX runtime loaded but the function table is incomplete");
            return;
        }

        wchar_t loaded[MAX_PATH] = {};
        const DWORD length = GetModuleFileNameW(module, loaded, MAX_PATH);
        LogLine("FidelityFX runtime ready: " + Narrow(std::wstring(loaded, length)));
    }
}
