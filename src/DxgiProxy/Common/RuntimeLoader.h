#pragma once

#include <windows.h>

#include <string>

namespace redefinition
{
    // A vendor's DLL loaded by its path in a folder: what the AMD, XeSS and XeLL loaders share.
    struct RuntimeModule
    {
        // The loaded module, null where it did not load.
        HMODULE module = nullptr;
        // Folder and name together.
        std::wstring path;
        // Why it did not load: no file in the folder, or the loader's failure. Empty where it did.
        std::string error;
    };

    // The DLL folder\name loaded with its own folder in the search path, so that the DLLs it needs stand beside it.
    // missingHint ends the text for a file that is not there, after the folder and a dash.
    RuntimeModule LoadRuntimeModule(const std::wstring& folder, const wchar_t* name, const std::string& missingHint);
}
