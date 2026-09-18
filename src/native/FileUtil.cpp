#include "FileUtil.h"
#include "Log.h"

#include <windows.h>

#include <winver.h>

#include <cstdio>
#include <vector>

namespace ksp
{
    std::string Narrow(const std::wstring& text)
    {
        if (text.empty())
            return std::string();
        const int length = WideCharToMultiByte(CP_UTF8, 0, text.c_str(), static_cast<int>(text.size()),
                                               nullptr, 0, nullptr, nullptr);
        std::string result(static_cast<size_t>(length), '\0');
        WideCharToMultiByte(CP_UTF8, 0, text.c_str(), static_cast<int>(text.size()), &result[0], length,
                            nullptr, nullptr);
        return result;
    }

    std::wstring ExecutableDirectory()
    {
        wchar_t buffer[MAX_PATH] = {};
        const DWORD length = GetModuleFileNameW(nullptr, buffer, MAX_PATH);
        std::wstring path(buffer, length);
        const size_t slash = path.find_last_of(L'\\');
        if (slash != std::wstring::npos)
            path.resize(slash);
        return path;
    }

    std::string FileVersion(const std::wstring& path)
    {
        DWORD ignored = 0;
        const DWORD size = GetFileVersionInfoSizeW(path.c_str(), &ignored);
        if (size == 0)
            return std::string();
        std::vector<BYTE> data(size);
        if (!GetFileVersionInfoW(path.c_str(), 0, size, data.data()))
            return std::string();
        VS_FIXEDFILEINFO* info = nullptr;
        UINT length = 0;
        if (!VerQueryValueW(data.data(), L"\\", reinterpret_cast<LPVOID*>(&info), &length) || info == nullptr)
            return std::string();
        char text[64] = {};
        FormatTo(text, "%u.%u.%u.%u", HIWORD(info->dwFileVersionMS), LOWORD(info->dwFileVersionMS),
                  HIWORD(info->dwFileVersionLS), LOWORD(info->dwFileVersionLS));
        return text;
    }

    std::wstring FileStamp(const std::wstring& path)
    {
        WIN32_FILE_ATTRIBUTE_DATA data = {};
        if (!GetFileAttributesExW(path.c_str(), GetFileExInfoStandard, &data))
            return L"-";
        return std::to_wstring(data.nFileSizeHigh) + L"." + std::to_wstring(data.nFileSizeLow) + L"@"
             + std::to_wstring(data.ftLastWriteTime.dwHighDateTime) + L"."
             + std::to_wstring(data.ftLastWriteTime.dwLowDateTime);
    }
}
