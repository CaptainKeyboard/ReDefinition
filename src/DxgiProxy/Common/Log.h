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
    // Opens the log file anew, emptying it; nothing where it is already open.
    void LogOpen();
    // Closes the file; lines after it go nowhere.
    void LogClose();

    // One line with the local time in front, flushed at once; nothing while the log is closed.
    void LogLine(const std::string& text);

    // Formats an HRESULT as 0x........ so failures can be looked up directly.
    std::string Hr(long hr);

    // Formatted into a fixed buffer and cut to it. sprintf_s ends the process when the text is
    // longer than the buffer. That happens with a number the format did not expect, such as a value
    // of 1e300 printed with %f. No line of a log is worth the game.
    template <size_t N>
    inline void FormatTo(char (&buffer)[N], const char* format, ...)
    {
        va_list arguments;
        va_start(arguments, format);
        _vsnprintf_s(buffer, N, _TRUNCATE, format, arguments);
        va_end(arguments);
    }
}
