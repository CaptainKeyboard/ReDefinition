#include "RuntimeLoader.h"

#include "FileUtil.h"
#include "Log.h"

namespace redefinition
{
    RuntimeModule LoadRuntimeModule(const std::wstring& folder, const wchar_t* name, const std::string& missingHint)
    {
        RuntimeModule loaded;
        loaded.path = folder + L"\\" + name;
        loaded.module = LoadLibraryExW(loaded.path.c_str(), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
        if (loaded.module != nullptr)
            return loaded;
        const DWORD failure = GetLastError();
        if (GetFileAttributesW(loaded.path.c_str()) == INVALID_FILE_ATTRIBUTES)
            loaded.error = "no " + Narrow(name) + " in " + Narrow(folder) + " -- " + missingHint;
        else
            loaded.error = Narrow(loaded.path) + " could not be loaded (" + Hr(HRESULT_FROM_WIN32(failure)) + ")";
        return loaded;
    }
}
