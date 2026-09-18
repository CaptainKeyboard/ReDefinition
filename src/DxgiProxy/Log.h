#pragma once

#include <cstdarg>
#include <cstdio>
#include <string>

// A file log: this DLL runs before and below anything a debugger attaches to
// comfortably, and a fault in here takes the whole process down without a managed
// stack trace.
//
// Written next to the game executable as ReDefinitionProxy.log.
namespace redefinition
{
    void LogOpen();
    void LogClose();

    void LogLine(const std::string& text);

    // Formats an HRESULT as 0x........ so failures can be looked up directly.
    std::string Hr(long hr);

    // Formatted into a fixed buffer and cut to it. sprintf_s ends the process when
    // the text is longer than the buffer -- a number the format did not expect, a
    // value of 1e300 printed with %f -- and no line of a log is worth the game.
    template <size_t N>
    inline void FormatTo(char (&buffer)[N], const char* format, ...)
    {
        va_list arguments;
        va_start(arguments, format);
        _vsnprintf_s(buffer, N, _TRUNCATE, format, arguments);
        va_end(arguments);
    }
}
