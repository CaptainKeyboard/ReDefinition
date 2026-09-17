#pragma once

#include <windows.h>

#include <mutex>

#include <FidelityFX/api/include/ffx_api.h>
#include <FidelityFX/api/include/ffx_api_loader.h>

namespace ksp
{
    // The FidelityFX runtime, loaded at run time rather than linked.
    //
    // The whole API is five exports -- CreateContext, DestroyContext, Configure,
    // Query, Dispatch -- reached through a function table, so nothing has to be
    // linked and a missing DLL is a logged line rather than a process that will
    // not start. The headers are MIT and vendored under extern/; the DLL is
    // AMD's redistributable, which the player package puts next to KSP_x64.exe.
    //
    // Loaded on first use, not from DllMain: Microsoft forbids LoadLibrary under
    // the loader lock ("Dynamic-Link Library Best Practices"). From next to the
    // executable only -- a bare name would be searched for in the working
    // directory and PATH too.
    class FidelityFx
    {
    public:
        static FidelityFx& Get();

        // Loads the runtime the first time it is asked, on any thread. Never
        // throws and never fails hard: if the DLL is absent the proxy carries on
        // presenting without frame generation.
        bool Ready();
        // Only after Ready() has answered true, or from a context made through it.
        const ffxFunctions& Api() const { return api; }

    private:
        FidelityFx() = default;
        void Load();

        HMODULE module = nullptr;
        ffxFunctions api = {};
        bool ready = false;
        std::once_flag loadOnce;
    };
}
