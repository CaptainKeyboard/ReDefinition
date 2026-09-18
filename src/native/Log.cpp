#include "Log.h"

#include "FileUtil.h"

#include <windows.h>

#include <cstdio>
#include <mutex>

namespace ksp
{
    namespace
    {
        std::mutex g_mutex;
        FILE* g_file = nullptr;

        // Next to the executable, not in the working directory: the working
        // directory of a Steam launch is not reliably the game folder.
        std::wstring LogPath()
        {
            return ExecutableDirectory() + L"\\ReDefinitionProxy.log";
        }
    }

    void LogOpen()
    {
        std::lock_guard<std::mutex> lock(g_mutex);
        if (g_file != nullptr)
            return;

        _wfopen_s(&g_file, LogPath().c_str(), L"w");
    }

    void LogClose()
    {
        std::lock_guard<std::mutex> lock(g_mutex);
        if (g_file == nullptr)
            return;

        fclose(g_file);
        g_file = nullptr;
    }

    void LogLine(const std::string& text)
    {
        std::lock_guard<std::mutex> lock(g_mutex);
        if (g_file == nullptr)
            return;

        SYSTEMTIME now = {};
        GetLocalTime(&now);

        fprintf(g_file, "%02u:%02u:%02u.%03u  %s\n",
                now.wHour, now.wMinute, now.wSecond, now.wMilliseconds, text.c_str());

        // Flushed every line, so a crash keeps the last lines.
        fflush(g_file);
    }

    std::string Hr(long hr)
    {
        char buffer[16] = {};
        FormatTo(buffer, "0x%08lX", static_cast<unsigned long>(hr));
        return buffer;
    }
}
