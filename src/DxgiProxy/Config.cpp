#include "Config.h"

#include "FileUtil.h"
#include "Log.h"

#include <windows.h>

#include <mutex>
#include <string>

namespace redefinition
{
    namespace
    {
        ProxyConfig g_config;
        FILETIME g_lastWrite = {};
        ULONGLONG g_lastCheckTicks = 0;

        // Held while g_config is written, and while the folders are copied out
        // for Unity's render thread (DlssDirectory, AmdUpscalerDirectory). The
        // present path, which writes it, reads it without.
        std::mutex g_writeLock;

        std::wstring IniPath()
        {
            return ExecutableDirectory() + L"\\ReDefinitionProxy.ini";
        }

        bool ReadFlag(const wchar_t* key, const std::wstring& file, bool fallback)
        {
            // A missing file gives the default everywhere: without an ini the proxy
            // runs on its defaults.
            return GetPrivateProfileIntW(L"Proxy", key, fallback ? 1 : 0, file.c_str()) != 0;
        }

        // GetPrivateProfileInt cannot read a fraction, and the pacing values are
        // fractions of a millisecond.
        float ReadFloat(const wchar_t* key, const std::wstring& file, float fallback)
        {
            wchar_t buffer[64] = {};
            const DWORD length = GetPrivateProfileStringW(L"Proxy", key, L"", buffer,
                                                          ARRAYSIZE(buffer), file.c_str());
            if (length == 0)
                return fallback;

            wchar_t* end = nullptr;
            const float value = wcstof(buffer, &end);
            return end == buffer ? fallback : value;
        }

        bool WriteTimeOf(const std::wstring& file, FILETIME& out)
        {
            WIN32_FILE_ATTRIBUTE_DATA data = {};
            if (!GetFileAttributesExW(file.c_str(), GetFileExInfoStandard, &data))
                return false;

            out = data.ftLastWriteTime;
            return true;
        }

        std::string Describe(const ProxyConfig& c)
        {
            return std::string("enabled=") + (c.enabled ? "1" : "0")
                 + " allowTearing=" + (c.allowTearing ? "1" : "0")
                 + " measureOnly=" + (c.measureOnly ? "1" : "0")
                 + " frameGeneration=" + (c.frameGeneration ? "1" : "0")
                 + " dlssFrameGeneration=" + (c.dlssFrameGeneration ? "1" : "0")
                 + " hudLessColour=" + (c.hudLessColour ? "1" : "0")
                 + " fgDepthInverted=" + (c.fgDepthInverted ? "1" : "0")
                 + " fgNegateMotionScale=" + (c.fgNegateMotionScale ? "1" : "0")
                 + " fgFlipInputs=" + (c.fgFlipInputs ? "1" : "0")
                 + " fgFlipHudLess=" + (c.fgFlipHudLess ? "1" : "0")
                 + " fgCameraBasis=" + (c.fgCameraBasis ? "1" : "0")
                 + " fgVSync=" + (c.fgVSync ? "1" : "0")
                 + " fgHalfRefreshLimit=" + (c.fgHalfRefreshLimit ? "1" : "0")
                 + " fgAsyncWorkloads=" + (c.fgAsyncWorkloads ? "1" : "0")
                 + " fgObjectSeparationMetres=" + std::to_string(c.fgObjectSeparationMetres)
                 + " pacing=" + std::to_string(c.fgPacingSafetyMarginMs) + "/"
                 + std::to_string(c.fgPacingVarianceFactor)
                 + (c.fgPacingHybridSpin ? "/spin" : "")
                 + (c.fgPacingWaitOnFence ? "/fence" : "")
                 + " reportSeconds=" + std::to_string(c.reportSeconds)
                 + " logInputsSeconds=" + std::to_string(c.logInputsSeconds)
                 + " dlssDirectory=" + (c.dlssDirectory[0] == L'\0' ? "(none)" : "(set)")
                 + " amdUpscalerDirectory=" + (c.amdUpscalerDirectory[0] == L'\0' ? "(none)" : "(set)")
                 + " streamlineDirectory=" + (c.streamlineDirectory[0] == L'\0' ? "(none)" : "(set)");
        }

        void ReadInto(ProxyConfig& target, const std::wstring& file)
        {
            target.enabled = ReadFlag(L"enabled", file, true);
            target.allowTearing = ReadFlag(L"allowTearing", file, true);
            target.measureOnly = ReadFlag(L"measureOnly", file, false);
            // On without an ini since the player package ships AMD's runtime
            // next to dxgi.dll (docs/development/packaging.md): an ini in the game folder
            // is a player's settings, and the package does not put one there.
            // Without the runtime the proxy presents through plain DXGI.
            target.frameGeneration = ReadFlag(L"frameGeneration", file, true);
            target.dlssFrameGeneration = ReadFlag(L"dlssFrameGeneration", file, true);

            target.hudLessColour = ReadFlag(L"hudLessColour", file, true);
            target.fgDepthInverted = ReadFlag(L"fgDepthInverted", file, true);
            target.fgNegateMotionScale = ReadFlag(L"fgNegateMotionScale", file, false);
            target.fgFlipInputs = ReadFlag(L"fgFlipInputs", file, true);
            target.fgFlipHudLess = ReadFlag(L"fgFlipHudLess", file, false);
            target.fgCameraBasis = ReadFlag(L"fgCameraBasis", file, true);
            // Off without an ini: the game's own V-Sync decides, also while
            // frame generation generates.
            target.fgVSync = ReadFlag(L"fgVSync", file, false);
            target.fgHalfRefreshLimit = ReadFlag(L"fgHalfRefreshLimit", file, false);
            target.fgAsyncWorkloads = ReadFlag(L"fgAsyncWorkloads", file, false);

            target.reportSeconds =
                static_cast<int>(GetPrivateProfileIntW(L"Proxy", L"reportSeconds", 10, file.c_str()));

            // Zero would mean the report interval never elapses and the frame
            // time buffer grows for the whole session.
            if (target.reportSeconds < 1)
                target.reportSeconds = 1;

            target.fgObjectSeparationMetres = ReadFloat(L"fgObjectSeparationMetres", file, 1.0f);
            target.fgPacingSafetyMarginMs = ReadFloat(L"fgPacingSafetyMarginMs", file, 0.1f);
            target.fgPacingVarianceFactor = ReadFloat(L"fgPacingVarianceFactor", file, 0.1f);
            target.fgPacingHybridSpin = ReadFlag(L"fgPacingHybridSpin", file, false);
            target.fgPacingHybridSpinTime = static_cast<int>(
                GetPrivateProfileIntW(L"Proxy", L"fgPacingHybridSpinTime", 2, file.c_str()));
            target.fgPacingWaitOnFence = ReadFlag(L"fgPacingWaitOnFence", file, false);

            target.logInputsSeconds = static_cast<int>(
                GetPrivateProfileIntW(L"Proxy", L"logInputsSeconds", 0, file.c_str()));
            if (target.logInputsSeconds < 0)
                target.logInputsSeconds = 0;

            GetPrivateProfileStringW(L"Proxy", L"dlssDirectory", L"", target.dlssDirectory,
                                     ARRAYSIZE(target.dlssDirectory), file.c_str());
            GetPrivateProfileStringW(L"Proxy", L"amdUpscalerDirectory", L"", target.amdUpscalerDirectory,
                                     ARRAYSIZE(target.amdUpscalerDirectory), file.c_str());
            GetPrivateProfileStringW(L"Proxy", L"streamlineDirectory", L"", target.streamlineDirectory,
                                     ARRAYSIZE(target.streamlineDirectory), file.c_str());
        }
    }

    const ProxyConfig& Config()
    {
        return g_config;
    }

    void LoadConfig()
    {
        const std::wstring file = IniPath();

        {
            std::lock_guard<std::mutex> lock(g_writeLock);
            ReadInto(g_config, file);
        }
        WriteTimeOf(file, g_lastWrite);

        LogLine("Config: " + Describe(g_config));
    }

    std::wstring DlssDirectory()
    {
        std::lock_guard<std::mutex> lock(g_writeLock);
        return std::wstring(g_config.dlssDirectory);
    }

    std::wstring AmdUpscalerDirectory()
    {
        std::lock_guard<std::mutex> lock(g_writeLock);
        return std::wstring(g_config.amdUpscalerDirectory);
    }

    // From the file itself: asked once per swapchain, which can be made before
    // the present path has re-read an ini written after the proxy loaded. Into a
    // buffer of its own -- g_config is read by other threads without the lock.
    std::wstring StreamlineDirectory()
    {
        wchar_t buffer[260] = {};
        GetPrivateProfileStringW(L"Proxy", L"streamlineDirectory", L"", buffer, ARRAYSIZE(buffer), IniPath().c_str());
        return std::wstring(buffer);
    }

    bool ReloadConfigIfChanged(bool& rebuild)
    {
        rebuild = false;
        // At most once a second: this runs from the present path, and a file
        // system query per frame would land in the frame times the proxy measures.
        const ULONGLONG now = GetTickCount64();
        if (now - g_lastCheckTicks < 1000)
            return false;

        g_lastCheckTicks = now;

        const std::wstring file = IniPath();
        FILETIME written = {};
        if (!WriteTimeOf(file, written))
            return false;

        if (CompareFileTime(&written, &g_lastWrite) == 0)
            return false;

        g_lastWrite = written;

        ProxyConfig fresh = g_config;
        ReadInto(fresh, file);

        // Not live: a swapchain cannot be exchanged underneath a running game.
        fresh.enabled = g_config.enabled;
        fresh.frameGeneration = g_config.frameGeneration;
        fresh.dlssFrameGeneration = g_config.dlssFrameGeneration;
        fresh.measureOnly = g_config.measureOnly;

        // The settings that change how the context was built.
        const bool needsRebuild = fresh.fgDepthInverted != g_config.fgDepthInverted
                                  || fresh.fgAsyncWorkloads != g_config.fgAsyncWorkloads;

        {
            std::lock_guard<std::mutex> lock(g_writeLock);
            g_config = fresh;
        }
        LogLine("Config reloaded: " + Describe(g_config)
                + (needsRebuild ? "   (frame generation context will be rebuilt)" : ""));

        rebuild = needsRebuild;
        return true;
    }
}
