#include "NgxCore.h"

#include "FileUtil.h"
#include "Log.h"

namespace redefinition
{
    namespace
    {
        // NGX's log lines since the last ResetNgxLog; past 200 none are logged.
        std::atomic<int> g_ngxLogLines{ 0 };

        // The driver's NGX core once loaded, and the lock its loading takes.
        std::mutex g_coreMutex;
        HMODULE g_core = nullptr;

        // Where the driver registers its NGX core, empty without one.
        std::wstring NgxCorePath()
        {
            HKEY key = nullptr;
            if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, L"SYSTEM\\CurrentControlSet\\Services\\nvlddmkm\\NGXCore", 0,
                              KEY_READ, &key) != ERROR_SUCCESS)
                return std::wstring();

            wchar_t buffer[MAX_PATH] = {};
            DWORD bytes = sizeof(buffer) - sizeof(wchar_t);
            DWORD type = 0;
            const LSTATUS status = RegQueryValueExW(key, L"NGXPath", nullptr, &type,
                                                    reinterpret_cast<BYTE*>(buffer), &bytes);
            RegCloseKey(key);
            if (status != ERROR_SUCCESS || (type != REG_SZ && type != REG_EXPAND_SZ) || buffer[0] == L'\0')
                return std::wstring();

            std::wstring path(buffer);
            if (path.back() != L'\\')
                path += L'\\';
            return path + L"_nvngx.dll";
        }
    }

    std::mutex& NgxMutex()
    {
        // Never destroyed, like the classes that take it.
        static std::mutex* mutex = new std::mutex();
        return *mutex;
    }

    HMODULE NgxCoreModule(std::string& error, bool& stable)
    {
        stable = false;
        std::lock_guard<std::mutex> lock(g_coreMutex);
        if (g_core != nullptr)
            return g_core;
        const std::wstring corePath = NgxCorePath();
        if (corePath.empty())
        {
            error = "no NVIDIA NGX in the driver: DLSS needs an NVIDIA RTX GPU and its driver";
            stable = true;
            return nullptr;
        }
        g_core = LoadLibraryExW(corePath.c_str(), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
        if (g_core == nullptr)
            error = "the driver's NGX core could not be loaded (" + Narrow(corePath) + ", error "
                    + std::to_string(GetLastError()) + ")";
        return g_core;
    }

    bool NgxCoreRegistered()
    {
        // Asked with every Ray Reconstruction frame: the registry once.
        static const bool registered = !NgxCorePath().empty();
        return registered;
    }

    void NgxLog(const char* message, int, int)
    {
        // Everything NGX says about initialisation and features, then quiet.
        const int line = ++g_ngxLogLines;
        if (line > 200 || message == nullptr)
            return;
        std::string text(message);
        while (!text.empty() && (text.back() == '\n' || text.back() == '\r'))
            text.pop_back();
        LogLine("NGX: " + text + (line == 200 ? "   (further NGX lines are not logged)" : ""));
    }

    // NgxLog's line count back to 0: the next start is logged in full.
    void ResetNgxLog()
    {
        g_ngxLogLines = 0;
    }

    // An NGX result as eight hexadecimal digits, for messages.
    std::string NgxHex(uint32_t value)
    {
        char text[16] = {};
        FormatTo(text, "0x%08X", value);
        return text;
    }

    // A size as "1920x1080", for messages.
    std::string NgxSize(unsigned int width, unsigned int height)
    {
        return std::to_string(width) + "x" + std::to_string(height);
    }

    // The folders taken, without empty ones and those already there (case ignored), and pointers to them.
    void NgxSearch::Set(const std::vector<std::wstring>& folders)
    {
        paths.clear();
        for (const std::wstring& folder : folders)
        {
            bool known = folder.empty();
            for (const std::wstring& path : paths)
                known = known || _wcsicmp(path.c_str(), folder.c_str()) == 0;
            if (!known)
                paths.push_back(folder);
        }
        pointers.clear();
        for (std::wstring& path : paths)
            pointers.push_back(&path[0]);
    }

    // The common info NGX's init takes: these folders, and its log into the proxy's alone.
    NgxCommonInfo NgxSearch::Info()
    {
        NgxCommonInfo info = {};
        info.pathList.path = pointers.data();
        info.pathList.length = static_cast<unsigned int>(pointers.size());
        info.logging.callback = &NgxLog;
        info.logging.minimumLevel = kNgxLoggingOn;
        // Into the proxy's log only: otherwise NGX also writes nvngx.log next to
        // the game and to the console.
        info.logging.disableOtherSinks = true;
        return info;
    }

    std::string NgxSearch::Where() const
    {
        std::string where;
        for (const std::wstring& path : paths)
            where += (where.empty() ? "" : " or ") + Narrow(path);
        return where;
    }

    std::string LoadedNgxModule(const wchar_t* name)
    {
        const std::string narrow = Narrow(name);
        const HMODULE module = GetModuleHandleW(name);
        if (module == nullptr)
            return narrow;
        wchar_t buffer[MAX_PATH] = {};
        const DWORD length = GetModuleFileNameW(module, buffer, MAX_PATH);
        const std::wstring path(buffer, length);
        return narrow + " " + FileVersion(path) + " (" + Narrow(path) + ")";
    }
}
