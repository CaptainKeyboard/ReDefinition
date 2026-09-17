#pragma once

#include <string>

// A file log: this DLL runs before and below anything a debugger attaches to
// comfortably, and a fault in here takes the whole process down without a managed
// stack trace.
//
// Written next to the game executable as ReDefinitionProxy.log.
namespace ksp
{
    void LogOpen();
    void LogClose();

    void LogLine(const std::string& text);

    // Formats an HRESULT as 0x........ so failures can be looked up directly.
    std::string Hr(long hr);
}
