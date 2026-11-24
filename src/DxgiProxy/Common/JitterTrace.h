#pragma once

// Test switch -redefinitionJitterLog (the jitter tests of 2026-11-21, the rocket's step in a camera turn): one proxy log line per
// DLSS packet (the jitter NGX is handed, the reset flag, the path) and one per present, each with a time in ms, so they can be read
// beside the mod's per-frame jitter lines (UpscalerIntegration.ChooseJitter). Off by default: nothing is logged and nothing else changes.

#include <windows.h>

#include <cwchar>

namespace redefinition
{
    inline bool JitterTraceOn()
    {
        static const bool on = []
        {
            const wchar_t* line = GetCommandLineW();
            return line != nullptr && wcsstr(line, L"-redefinitionJitterLog") != nullptr;
        }();
        return on;
    }

    inline double JitterTraceMs()
    {
        LARGE_INTEGER frequency, counter;
        QueryPerformanceFrequency(&frequency);
        QueryPerformanceCounter(&counter);
        return 1000.0 * static_cast<double>(counter.QuadPart) / static_cast<double>(frequency.QuadPart);
    }
}
