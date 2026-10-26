#pragma once

#include <string>

namespace redefinition
{
    // Paths and file facts the proxy needs in several places: its log and ini,
    // and the loaders of the runtimes next to the game.

    // UTF-8, for log lines and status texts.
    std::string Narrow(const std::wstring& text);

    // The folder of the game's executable, without a trailing backslash.
    std::wstring ExecutableDirectory();

    // "a.b.c.d" from a file's version resource, or empty.
    std::string FileVersion(const std::wstring& path);

    // A file's size and last write time, or a dash where there is none: what
    // tells a DLL copied in since from the one that was there.
    std::wstring FileStamp(const std::wstring& path);
}
