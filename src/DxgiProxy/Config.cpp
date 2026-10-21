#include "Config.h"

#include "FileUtil.h"
#include "Log.h"

#include <windows.h>

#include <algorithm>
#include <mutex>
#include <string>
#include <vector>

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

        bool ReadFileText(const std::wstring& file, std::string& text)
        {
            HANDLE handle = CreateFileW(file.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_EXISTING,
                                        FILE_ATTRIBUTE_NORMAL, nullptr);
            if (handle == INVALID_HANDLE_VALUE)
                return false;
            LARGE_INTEGER size = {};
            bool ok = GetFileSizeEx(handle, &size) && size.QuadPart < (1 << 24);
            if (ok)
            {
                text.resize(static_cast<size_t>(size.QuadPart));
                DWORD read = 0;
                ok = text.empty() || (ReadFile(handle, &text[0], static_cast<DWORD>(text.size()), &read, nullptr) && read == text.size());
            }
            CloseHandle(handle);
            return ok;
        }

        bool WriteFileText(const std::wstring& file, const std::string& text)
        {
            HANDLE handle = CreateFileW(file.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
            if (handle == INVALID_HANDLE_VALUE)
                return false;
            DWORD written = 0;
            const bool ok = WriteFile(handle, text.data(), static_cast<DWORD>(text.size()), &written, nullptr) && written == text.size();
            CloseHandle(handle);
            return ok;
        }

        // Whether the player's file has the key in [Proxy], whatever its value.
        bool HasKey(const std::wstring& file, const std::string& key)
        {
            wchar_t buffer[8] = {};
            const std::wstring name(key.begin(), key.end());
            GetPrivateProfileStringW(L"Proxy", name.c_str(), L"\x01", buffer, ARRAYSIZE(buffer), file.c_str());
            return !(buffer[0] == L'\x01' && buffer[1] == L'\0');
        }

        // The last section header in the text, or "" where there is none.
        std::string LastSection(const std::string& text)
        {
            std::string last;
            size_t at = 0;
            while (at < text.size())
            {
                size_t end = text.find('\n', at);
                if (end == std::string::npos)
                    end = text.size();
                const size_t start = text.find_first_not_of(" \t", at);
                if (start != std::string::npos && start < end && text[start] == '[')
                {
                    const size_t close = text.find(']', start);
                    if (close != std::string::npos && close < end)
                        last = text.substr(start, close - start + 1);
                }
                at = end + 1;
            }
            return last;
        }

        // The keys of the template's [Proxy] section with their defaults.
        void CollectDefaults(const std::string& templateText, std::vector<std::pair<std::string, std::string>>& defaults)
        {
            bool inProxy = false;
            size_t at = 0;
            while (at < templateText.size())
            {
                size_t end = templateText.find('\n', at);
                if (end == std::string::npos)
                    end = templateText.size();
                std::string line = templateText.substr(at, end - at);
                at = end + 1;
                if (!line.empty() && line.back() == '\r')
                    line.pop_back();
                if (line.empty() || line[0] == ';')
                    continue;
                if (line[0] == '[')
                {
                    inProxy = line == "[Proxy]";
                    continue;
                }
                const size_t equals = line.find('=');
                if (inProxy && equals != std::string::npos)
                    defaults.emplace_back(line.substr(0, equals), line.substr(equals + 1));
            }
        }

        // A key's value in a section of a file, and whether the file has the key.
        bool ReadValue(const std::wstring& file, const wchar_t* section, const std::wstring& key, std::wstring& value)
        {
            wchar_t buffer[260] = {};
            GetPrivateProfileStringW(section, key.c_str(), L"\x01", buffer, ARRAYSIZE(buffer), file.c_str());
            if (buffer[0] == L'\x01' && buffer[1] == L'\0')
                return false;
            value = buffer;
            return true;
        }

        // The values the proxy wrote into the player's file itself, as it last wrote them,
        // in a file of its own beside it: ReDefinitionProxy.defaults. Where the player's
        // file still holds such a value, it is a default of then, not the player's choice,
        // and a later version's default takes its place; a value the player changed stays.
        // A key the proxy has no record of counts as its own where it holds today's
        // default, else as the player's.
        void RaiseDefaults(const std::wstring& file, const std::vector<std::pair<std::string, std::string>>& defaults,
                           const std::vector<std::string>& added, bool allWritten)
        {
            const std::wstring written = file.substr(0, file.find_last_of(L'.')) + L".defaults";
            std::string raised;
            for (const auto& pair : defaults)
            {
                const std::wstring key(pair.first.begin(), pair.first.end());
                const std::wstring value(pair.second.begin(), pair.second.end());
                if (allWritten || std::find(added.begin(), added.end(), pair.first) != added.end())
                {
                    WritePrivateProfileStringW(L"Written", key.c_str(), value.c_str(), written.c_str());
                    continue;
                }
                std::wstring current, before;
                if (!ReadValue(file, L"Proxy", key, current))
                    continue;
                if (!ReadValue(written, L"Written", key, before))
                {
                    if (current == value)
                        WritePrivateProfileStringW(L"Written", key.c_str(), value.c_str(), written.c_str());
                    continue;
                }
                if (current != before || before == value)
                    continue;
                if (!WritePrivateProfileStringW(L"Proxy", key.c_str(), value.c_str(), file.c_str()))
                    continue;
                WritePrivateProfileStringW(L"Written", key.c_str(), value.c_str(), written.c_str());
                raised += (raised.empty() ? "" : ", ") + pair.first + " " + std::string(before.begin(), before.end()) + " -> "
                          + pair.second;
            }
            if (!raised.empty())
                LogLine("Config: defaults the proxy had written raised to this version's in ReDefinitionProxy.ini: " + raised);
        }

        // Every key of the template built into the proxy (ReDefinitionProxy.rc) stands in the
        // player's file with its default: a file from an earlier version gets the keys added
        // since, each after the comment above it in the template; the player's own values stay.
        // Without a file, the template is written as it is. Where another section than
        // [Proxy] ends the file, or the file is UTF-16, the keys go into [Proxy] without their
        // comments. Then the defaults the proxy wrote earlier take this version's
        // (RaiseDefaults).
        void CompleteIni(const std::wstring& file)
        {
            HMODULE self = nullptr;
            if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                                    reinterpret_cast<LPCWSTR>(&CompleteIni), &self))
                return;
            HRSRC found = FindResourceW(self, L"REDEFINITION_INI_TEMPLATE", MAKEINTRESOURCEW(10));
            HGLOBAL loaded = found != nullptr ? LoadResource(self, found) : nullptr;
            const char* data = loaded != nullptr ? static_cast<const char*>(LockResource(loaded)) : nullptr;
            if (data == nullptr)
            {
                LogLine("Config: the settings template is missing from the proxy");
                return;
            }
            const std::string templateText(data, SizeofResource(self, found));
            // The template's keys in [Proxy] with their defaults, for RaiseDefaults.
            std::vector<std::pair<std::string, std::string>> defaults;

            std::string existing;
            if (!ReadFileText(file, existing))
            {
                // Only where there is no file: one that could not be read is the player's.
                const DWORD readError = GetLastError();
                if (readError != ERROR_FILE_NOT_FOUND)
                {
                    LogLine("Config: ReDefinitionProxy.ini could not be read (error " + std::to_string(readError)
                            + "); its keys are not completed");
                    return;
                }
                if (WriteFileText(file, templateText))
                {
                    LogLine("Config: ReDefinitionProxy.ini written from the template, with every default");
                    CollectDefaults(templateText, defaults);
                    RaiseDefaults(file, defaults, std::vector<std::string>(), true);
                }
                else
                    LogLine("Config: ReDefinitionProxy.ini neither read (error " + std::to_string(readError)
                            + ") nor written from the template (error " + std::to_string(GetLastError()) + ")");
                return;
            }

            // A file in UTF-16, as Notepad's "Unicode" and PowerShell 5.1 write it, which
            // Windows' profile functions read: the keys go in through them, without their
            // comments -- appended bytes in ANSI would be no keys to them.
            const bool utf16 = existing.size() >= 2
                               && ((static_cast<unsigned char>(existing[0]) == 0xFF && static_cast<unsigned char>(existing[1]) == 0xFE)
                                   || (static_cast<unsigned char>(existing[0]) == 0xFE && static_cast<unsigned char>(existing[1]) == 0xFF));

            // The template line by line: a comment block, the keys after it.
            std::string appended, block, names;
            std::vector<std::pair<std::string, std::string>> missing;
            bool blockWritten = false, lastWasKey = false, inProxy = false;
            size_t at = 0;
            while (at < templateText.size())
            {
                size_t end = templateText.find('\n', at);
                if (end == std::string::npos)
                    end = templateText.size();
                std::string line = templateText.substr(at, end - at);
                at = end + 1;
                if (!line.empty() && line.back() == '\r')
                    line.pop_back();
                if (line.empty() || line[0] == '[')
                {
                    if (!line.empty())
                        inProxy = line == "[Proxy]";
                    block.clear();
                    blockWritten = lastWasKey = false;
                    continue;
                }
                if (line[0] == ';')
                {
                    if (lastWasKey)
                    {
                        block.clear();
                        blockWritten = lastWasKey = false;
                    }
                    block += line + "\r\n";
                    continue;
                }
                lastWasKey = true;
                const size_t equals = line.find('=');
                if (!inProxy || equals == std::string::npos)
                    continue;
                const std::string key = line.substr(0, equals);
                defaults.emplace_back(key, line.substr(equals + 1));
                if (HasKey(file, key))
                    continue;
                if (!blockWritten)
                {
                    appended += "\r\n" + block;
                    blockWritten = true;
                }
                appended += line + "\r\n";
                missing.emplace_back(key, line.substr(equals + 1));
                names += (names.empty() ? "" : ", ") + key;
            }
            std::vector<std::string> added;
            for (const auto& pair : missing)
                added.push_back(pair.first);
            if (missing.empty())
            {
                RaiseDefaults(file, defaults, added, false);
                return;
            }

            const std::string last = utf16 ? std::string() : LastSection(existing);
            bool written = true;
            if (!utf16 && (last.empty() || last == "[Proxy]"))
            {
                std::string text = existing;
                if (!text.empty() && text.back() != '\n')
                    text += "\r\n";
                if (last.empty())
                    text += "[Proxy]\r\n";
                written = WriteFileText(file, text + appended);
            }
            else
                for (const auto& pair : missing)
                {
                    const std::wstring key(pair.first.begin(), pair.first.end());
                    const std::wstring value(pair.second.begin(), pair.second.end());
                    written = WritePrivateProfileStringW(L"Proxy", key.c_str(), value.c_str(), file.c_str()) && written;
                }
            LogLine(std::string("Config: ") + (written ? "added" : "could not add") + " to ReDefinitionProxy.ini with their defaults: "
                    + names);
            RaiseDefaults(file, defaults, written ? added : std::vector<std::string>(), false);
        }

        bool WriteTimeOf(const std::wstring& file, FILETIME& out)
        {
            WIN32_FILE_ATTRIBUTE_DATA data = {};
            if (!GetFileAttributesExW(file.c_str(), GetFileExInfoStandard, &data))
                return false;

            out = data.ftLastWriteTime;
            return true;
        }

        const char* TechniqueName(int technique)
        {
            return technique == kFgTechniqueFsr ? "fsr"
                   : technique == kFgTechniqueDlss ? "dlss"
                   : technique == kFgTechniqueXess ? "xess" : "auto";
        }

        // frameGenerationTechnique: auto, fsr, dlss or xess, in any case; anything else
        // is auto, and said.
        int ReadTechnique(const std::wstring& file)
        {
            wchar_t buffer[32] = {};
            GetPrivateProfileStringW(L"Proxy", L"frameGenerationTechnique", L"auto", buffer, ARRAYSIZE(buffer),
                                     file.c_str());
            for (int technique : { kFgTechniqueAuto, kFgTechniqueFsr, kFgTechniqueDlss, kFgTechniqueXess })
            {
                const std::string name = TechniqueName(technique);
                if (_wcsicmp(buffer, std::wstring(name.begin(), name.end()).c_str()) == 0)
                    return technique;
            }
            LogLine("Config: frameGenerationTechnique=" + Narrow(buffer) + " is none of auto, fsr, dlss, xess -- auto");
            return kFgTechniqueAuto;
        }

        // xessFgUiMode: one of XessUiModeName's names, in any case; anything else is
        // auto, and said.
        int ReadXessUiMode(const std::wstring& file)
        {
            wchar_t buffer[64] = {};
            GetPrivateProfileStringW(L"Proxy", L"xessFgUiMode", L"auto", buffer, ARRAYSIZE(buffer), file.c_str());
            for (int mode = 0; mode < kXessUiModeCount; ++mode)
            {
                const std::string name = XessUiModeName(mode);
                if (_wcsicmp(buffer, std::wstring(name.begin(), name.end()).c_str()) == 0)
                    return mode;
            }
            LogLine("Config: xessFgUiMode=" + Narrow(buffer) + " is none of auto, none, backbuffer_uitexture,"
                    " hudless_uitexture, backbuffer_hudless, backbuffer_hudless_uitexture -- auto");
            return kXessUiModeAuto;
        }

        // xessFgOwnWaitable: auto, 0 or 1; anything else is auto, and said.
        int ReadXessOwnWaitable(const std::wstring& file)
        {
            wchar_t buffer[32] = {};
            GetPrivateProfileStringW(L"Proxy", L"xessFgOwnWaitable", L"auto", buffer, ARRAYSIZE(buffer), file.c_str());
            if (_wcsicmp(buffer, L"0") == 0)
                return 0;
            if (_wcsicmp(buffer, L"1") == 0)
                return 1;
            if (_wcsicmp(buffer, L"auto") != 0)
                LogLine("Config: xessFgOwnWaitable=" + Narrow(buffer) + " is none of auto, 0, 1 -- auto");
            return -1;
        }

        // xessFgBadConstants: skip, fix or pass, in any case; anything else is skip, and said.
        int ReadXessBadConstants(const std::wstring& file)
        {
            wchar_t buffer[32] = {};
            GetPrivateProfileStringW(L"Proxy", L"xessFgBadConstants", L"skip", buffer, ARRAYSIZE(buffer), file.c_str());
            for (int mode : { kXessBadSkip, kXessBadFix, kXessBadPass })
            {
                const std::string name = XessBadConstantsName(mode);
                if (_wcsicmp(buffer, std::wstring(name.begin(), name.end()).c_str()) == 0)
                    return mode;
            }
            LogLine("Config: xessFgBadConstants=" + Narrow(buffer) + " is none of skip, fix, pass -- skip");
            return kXessBadSkip;
        }

        // dlssgMode: on or dynamic, in any case; anything else is on, and said.
        int ReadDlssgMode(const std::wstring& file)
        {
            wchar_t buffer[32] = {};
            GetPrivateProfileStringW(L"Proxy", L"dlssgMode", L"on", buffer, ARRAYSIZE(buffer), file.c_str());
            for (int mode : { kDlssgModeOn, kDlssgModeDynamic })
            {
                const std::string name = DlssgModeName(mode);
                if (_wcsicmp(buffer, std::wstring(name.begin(), name.end()).c_str()) == 0)
                    return mode;
            }
            LogLine("Config: dlssgMode=" + Narrow(buffer) + " is none of on, dynamic -- on");
            return kDlssgModeOn;
        }

        // rrPath: auto, streamline or ngx, in any case; anything else is auto, and said.
        int ReadRrPath(const std::wstring& file)
        {
            wchar_t buffer[32] = {};
            GetPrivateProfileStringW(L"Proxy", L"rrPath", L"auto", buffer, ARRAYSIZE(buffer), file.c_str());
            for (int path : { kRrPathAuto, kRrPathStreamline, kRrPathNgx })
            {
                const std::string name = RrPathName(path);
                if (_wcsicmp(buffer, std::wstring(name.begin(), name.end()).c_str()) == 0)
                    return path;
            }
            LogLine("Config: rrPath=" + Narrow(buffer) + " is none of auto, streamline, ngx -- auto");
            return kRrPathAuto;
        }

        std::string Describe(const ProxyConfig& c)
        {
            return std::string("enabled=") + (c.enabled ? "1" : "0")
                 + " allowTearing=" + (c.allowTearing ? "1" : "0")
                 + " measureOnly=" + (c.measureOnly ? "1" : "0")
                 + " frameGeneration=" + (c.frameGeneration ? "1" : "0")
                 + " frameGenerationTechnique=" + TechniqueName(c.frameGenerationTechnique)
                 + " rrPath=" + RrPathName(c.rrPath)
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
                 + " dlssgMode=" + DlssgModeName(c.dlssgMode)
                 + (c.dlssgMode == kDlssgModeDynamic ? "/" + std::to_string(static_cast<int>(c.dlssgDynamicTargetFps)) : "")
                 + " pacing=" + std::to_string(c.fgPacingSafetyMarginMs) + "/"
                 + std::to_string(c.fgPacingVarianceFactor)
                 + (c.fgPacingHybridSpin ? "/spin " + std::to_string(c.fgPacingHybridSpinTime) : "")
                 + (c.fgPacingWaitOnFence ? "/fence" : "")
                 + " fgMinimumFps=" + std::to_string(static_cast<int>(c.fgMinimumFps))
                 + " lowLatency=" + (c.lowLatency ? "1" : "0")
                 + " latencyBrake=" + (c.latencyBrake ? "1" : "0") + "/" + std::to_string(c.latencyBrakeTargetMs) + "/"
                 + std::to_string(c.latencyBrakeWaits) + (c.latencyBrakeWithDlssg ? "/dlssg" : "")                 + " antiLag2=" + std::to_string(c.antiLag2)
                 + " xell=" + (c.xell ? "1" : "0")
                 + " xellLowLatencyBoost=" + (c.xellLowLatencyBoost ? "1" : "0")
                 + " vendorDiagnostics=" + (c.vendorDiagnostics ? "1" : "0")
                 + " streamlineImgui=" + (c.streamlineImgui ? "1" : "0")
                 + " fgShowOnlyGenerated=" + (c.fgShowOnlyGenerated ? "1" : "0")
                 + " xessFgUiComposition=" + (c.xessFgUiComposition ? "1" : "0")
                 + " xessFgUiMode=" + XessUiModeName(c.xessFgUiMode)
                 + " xessFgFrames=" + std::to_string(c.xessFgFrames)
                 + " xessFgSceneChangeThreshold=" + std::to_string(c.xessFgSceneChangeThreshold)
                 + " xessFgTagInterpolatedFrames=" + (c.xessFgTagInterpolatedFrames ? "1" : "0")
                 + " xessFgPresentFailedInterpolation=" + (c.xessFgPresentFailedInterpolation ? "1" : "0")
                 + " xessFgOwnWaitable=" + (c.xessFgOwnWaitable < 0 ? "auto" : c.xessFgOwnWaitable == 0 ? "0" : "1")
                 + " xessFgGraceFrames=" + std::to_string(c.xessFgGraceFrames)
                 + " xessFgBadConstants=" + XessBadConstantsName(c.xessFgBadConstants)
                 + " xessFgAllowOld=" + (c.xessFgAllowOld ? "1" : "0")
                 + " xessFgResetOnChange=" + (c.xessFgResetOnChange ? "1" : "0")
                 + " xessFgResetDebounce=" + std::to_string(c.xessFgResetDebounce)
                 + " xellLimitLive=" + (c.xellLimitLive ? "1" : "0")
                 + " rtOwnQueue=" + (c.rtOwnQueue ? "1" : "0")
                 + " rtCutoutTest=" + std::to_string(c.rtCutoutTest)
                 + " rtOpacityMicromaps=" + std::to_string(c.rtOpacityMicromaps)
                 + " rtOmmMaxSubdivision=" + std::to_string(c.rtOmmMaxSubdivision)
                 + " rtSunUnfiltered=" + std::string(c.rtSunUnfiltered ? "1" : "0")
                 + " rtSunAtrousSteps=" + std::to_string(c.rtSunAtrousSteps)
                 + " rtSunHistory=" + std::to_string(c.rtSunHistory)
                 + " rtFoliageTransmission=" + std::to_string(c.rtFoliageTransmission)
                 + " rtRadianceCache=" + (c.rtRadianceCache ? "1" : "0")
                 + " rtRadianceCacheEntries=" + std::to_string(c.rtRadianceCacheEntries)
                 + " rtRadianceCacheCellPixels=" + std::to_string(c.rtRadianceCacheCellPixels)
                 + " rtRadianceCacheMinCell=" + std::to_string(c.rtRadianceCacheMinCell)
                 + " rtRadianceCacheSamples=" + std::to_string(c.rtRadianceCacheSamples)
                 + " rtRadianceCacheMaxAge=" + std::to_string(c.rtRadianceCacheMaxAge)
                 + " rtRadianceCacheDebug=" + std::to_string(c.rtRadianceCacheDebug)
                 + " rtBlasBatch=" + (c.rtBlasBatch ? "1" : "0")
                 + " rtBlasPool=" + (c.rtBlasPool ? "1" : "0")
                 + " rtBlasBudget=" + std::to_string(c.rtBlasBudget)
                 + " rtBlasCompaction=" + std::to_string(c.rtBlasCompaction)
                 + " rtBlasRefineBudget=" + std::to_string(c.rtBlasRefineBudget)
                 + " rtTlasFastTrace=" + (c.rtTlasFastTrace ? "1" : "0")
                 + " rtMegaGeometry=" + std::to_string(c.rtMegaGeometry)
                 + " reportSeconds=" + std::to_string(c.reportSeconds)
                 + " logInputsSeconds=" + std::to_string(c.logInputsSeconds)
                 + " dlssDirectory=" + (c.dlssDirectory[0] == L'\0' ? "(none)" : "(set)")
                 + " amdUpscalerDirectory=" + (c.amdUpscalerDirectory[0] == L'\0' ? "(none)" : "(set)")
                 + " xessDirectory=" + (c.xessDirectory[0] == L'\0' ? "(none)" : "(set)")
                 + " streamlineDirectory=" + (c.streamlineDirectory[0] == L'\0' ? "(none)" : "(set)")
                 + " xessFgDirectory=" + (c.xessFgDirectory[0] == L'\0' ? "(none)" : "(set)");
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
            target.frameGenerationTechnique = ReadTechnique(file);
            target.rrPath = ReadRrPath(file);

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

            target.fgObjectSeparationMetres = ReadFloat(L"fgObjectSeparationMetres", file, 0.0f);
            target.dlssgMode = ReadDlssgMode(file);
            target.dlssgDynamicTargetFps = ReadFloat(L"dlssgDynamicTargetFps", file, 0.0f);
            if (target.dlssgDynamicTargetFps < 0.0f)
                target.dlssgDynamicTargetFps = 0.0f;
            target.fgPacingSafetyMarginMs = ReadFloat(L"fgPacingSafetyMarginMs", file, 0.1f);
            target.fgPacingVarianceFactor = ReadFloat(L"fgPacingVarianceFactor", file, 0.1f);
            target.fgPacingHybridSpin = ReadFlag(L"fgPacingHybridSpin", file, false);
            target.fgPacingHybridSpinTime = static_cast<int>(
                GetPrivateProfileIntW(L"Proxy", L"fgPacingHybridSpinTime", 2, file.c_str()));
            // Handed to FSR unsigned: a negative value would be a spin of years.
            if (target.fgPacingHybridSpinTime < 0)
                target.fgPacingHybridSpinTime = 0;
            target.fgPacingWaitOnFence = ReadFlag(L"fgPacingWaitOnFence", file, false);
            target.fgMinimumFps = ReadFloat(L"fgMinimumFps", file, -1.0f);
            target.lowLatency = ReadFlag(L"lowLatency", file, true);
            target.latencyBrake = ReadFlag(L"latencyBrake", file, true);
            target.antiLag2 = static_cast<int>(GetPrivateProfileIntW(L"Proxy", L"antiLag2", 0, file.c_str()));
            target.latencyBrakeTargetMs = ReadFloat(L"latencyBrakeTargetMs", file, 4.0f);
            target.latencyBrakeWaits = static_cast<int>(
                GetPrivateProfileIntW(L"Proxy", L"latencyBrakeWaits", 1, file.c_str()));
            target.latencyBrakeWithDlssg = ReadFlag(L"latencyBrakeWithDlssg", file, true);
            target.logInputsSeconds = static_cast<int>(
                GetPrivateProfileIntW(L"Proxy", L"logInputsSeconds", 0, file.c_str()));
            target.vendorDiagnostics = ReadFlag(L"vendorDiagnostics", file, false);
            target.streamlineImgui = ReadFlag(L"streamlineImgui", file, false);
            target.fgShowOnlyGenerated = ReadFlag(L"fgShowOnlyGenerated", file, false);
            target.xessFgUiComposition = ReadFlag(L"xessFgUiComposition", file, false);
            target.xessFgUiMode = ReadXessUiMode(file);
            target.xessFgFrames = static_cast<int>(GetPrivateProfileIntW(L"Proxy", L"xessFgFrames", 1, file.c_str()));
            // "The number of interpolated frames must be between 1 and the maximum"
            // (xefgSwapChainSetNumInterpolatedFrames, xefg_swapchain.h).
            if (target.xessFgFrames < 1)
                target.xessFgFrames = 1;
            target.xessFgSceneChangeThreshold = ReadFloat(L"xessFgSceneChangeThreshold", file, 0.7f);
            // "The threshold value range is from 0 to 1".
            target.xessFgSceneChangeThreshold = std::min(1.0f, std::max(0.0f, target.xessFgSceneChangeThreshold));
            target.xessFgTagInterpolatedFrames = ReadFlag(L"xessFgTagInterpolatedFrames", file, false);
            target.xessFgPresentFailedInterpolation = ReadFlag(L"xessFgPresentFailedInterpolation", file, false);
            target.xessFgOwnWaitable = ReadXessOwnWaitable(file);
            target.xessFgGraceFrames = std::clamp(
                static_cast<int>(GetPrivateProfileIntW(L"Proxy", L"xessFgGraceFrames", 3, file.c_str())), 0, 600);
            target.xessFgBadConstants = ReadXessBadConstants(file);
            target.xessFgAllowOld = ReadFlag(L"xessFgAllowOld", file, false);
            target.xessFgResetOnChange = ReadFlag(L"xessFgResetOnChange", file, false);
            target.xessFgResetDebounce = std::clamp(
                static_cast<int>(GetPrivateProfileIntW(L"Proxy", L"xessFgResetDebounce", 0, file.c_str())), 0, 600);
            target.xellLimitLive = ReadFlag(L"xellLimitLive", file, true);
            target.xell = ReadFlag(L"xell", file, true);
            target.xellLowLatencyBoost = ReadFlag(L"xellLowLatencyBoost", file, false);
            target.rtOwnQueue = ReadFlag(L"rtOwnQueue", file, false);
            target.rtCutoutTest = static_cast<int>(GetPrivateProfileIntW(L"Proxy", L"rtCutoutTest", 0, file.c_str()));
            target.rtOpacityMicromaps =
                std::clamp(static_cast<int>(GetPrivateProfileIntW(L"Proxy", L"rtOpacityMicromaps", 0, file.c_str())), 0, 1);
            target.rtOmmMaxSubdivision =
                std::clamp(static_cast<int>(GetPrivateProfileIntW(L"Proxy", L"rtOmmMaxSubdivision", 6, file.c_str())), 2, 8);
            target.rtSunUnfiltered = GetPrivateProfileIntW(L"Proxy", L"rtSunUnfiltered", 0, file.c_str()) != 0;
            target.rtSunAtrousSteps = static_cast<int>(GetPrivateProfileIntW(L"Proxy", L"rtSunAtrousSteps", 0, file.c_str()));
            target.rtSunHistory = static_cast<int>(GetPrivateProfileIntW(L"Proxy", L"rtSunHistory", 4, file.c_str()));
            target.rtFoliageTransmission = ReadFloat(L"rtFoliageTransmission", file, 0.0f);
            target.rtRadianceCache = ReadFlag(L"rtRadianceCache", file, false);
            target.rtRadianceCacheEntries = std::clamp(
                static_cast<int>(GetPrivateProfileIntW(L"Proxy", L"rtRadianceCacheEntries", 20, file.c_str())), 16, 22);
            target.rtRadianceCacheCellPixels = std::clamp(ReadFloat(L"rtRadianceCacheCellPixels", file, 8.0f), 1.0f, 256.0f);
            target.rtRadianceCacheMinCell = std::clamp(ReadFloat(L"rtRadianceCacheMinCell", file, 0.125f), 1.0f / 64.0f, 64.0f);
            target.rtRadianceCacheSamples = std::clamp(
                static_cast<int>(GetPrivateProfileIntW(L"Proxy", L"rtRadianceCacheSamples", 256, file.c_str())), 1, 65536);
            target.rtRadianceCacheMaxAge = std::clamp(
                static_cast<int>(GetPrivateProfileIntW(L"Proxy", L"rtRadianceCacheMaxAge", 50, file.c_str())), 1, 100000);
            target.rtRadianceCacheDebug = static_cast<int>(GetPrivateProfileIntW(L"Proxy", L"rtRadianceCacheDebug", 0, file.c_str()));
            target.rtBlasBatch = ReadFlag(L"rtBlasBatch", file, true);
            target.rtBlasPool = ReadFlag(L"rtBlasPool", file, true);
            target.rtBlasBudget = std::max(0, static_cast<int>(GetPrivateProfileIntW(L"Proxy", L"rtBlasBudget", 0, file.c_str())));
            target.rtBlasCompaction =
                std::clamp(static_cast<int>(GetPrivateProfileIntW(L"Proxy", L"rtBlasCompaction", 2, file.c_str())), 0, 2);
            target.rtBlasRefineBudget =
                std::max(1, static_cast<int>(GetPrivateProfileIntW(L"Proxy", L"rtBlasRefineBudget", 65536, file.c_str())));
            target.rtTlasFastTrace = ReadFlag(L"rtTlasFastTrace", file, false);
            target.rtMegaGeometry =
                std::clamp(static_cast<int>(GetPrivateProfileIntW(L"Proxy", L"rtMegaGeometry", 0, file.c_str())), 0, 1);
            if (target.logInputsSeconds < 0)
                target.logInputsSeconds = 0;

            GetPrivateProfileStringW(L"Proxy", L"dlssDirectory", L"", target.dlssDirectory,
                                     ARRAYSIZE(target.dlssDirectory), file.c_str());
            GetPrivateProfileStringW(L"Proxy", L"amdUpscalerDirectory", L"", target.amdUpscalerDirectory,
                                     ARRAYSIZE(target.amdUpscalerDirectory), file.c_str());
            GetPrivateProfileStringW(L"Proxy", L"xessDirectory", L"", target.xessDirectory,
                                     ARRAYSIZE(target.xessDirectory), file.c_str());
            GetPrivateProfileStringW(L"Proxy", L"streamlineDirectory", L"", target.streamlineDirectory,
                                     ARRAYSIZE(target.streamlineDirectory), file.c_str());
            GetPrivateProfileStringW(L"Proxy", L"xessFgDirectory", L"", target.xessFgDirectory,
                                     ARRAYSIZE(target.xessFgDirectory), file.c_str());
        }
    }

    const ProxyConfig& Config()
    {
        return g_config;
    }

    void LoadConfig()
    {
        const std::wstring file = IniPath();
        CompleteIni(file);

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

    std::wstring XessDirectory()
    {
        std::lock_guard<std::mutex> lock(g_writeLock);
        return std::wstring(g_config.xessDirectory);
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

    // As StreamlineDirectory: from the file, once per swapchain.
    int FrameGenerationTechniqueAsked()
    {
        return ReadTechnique(IniPath());
    }

    // As StreamlineDirectory: from the file, once per swapchain, into a copy of its
    // own.
    ProxyConfig ConfigAsked()
    {
        ProxyConfig asked;
        ReadInto(asked, IniPath());
        return asked;
    }

    const char* XessUiModeName(int mode)
    {
        // xefg_swapchain_ui_mode_t, XEFG_SWAPCHAIN_UI_MODE_ without its prefix.
        static const char* const names[kXessUiModeCount] = { "auto", "none", "backbuffer_uitexture", "hudless_uitexture",
                                                             "backbuffer_hudless", "backbuffer_hudless_uitexture" };
        return mode >= 0 && mode < kXessUiModeCount ? names[mode] : names[kXessUiModeAuto];
    }

    const char* DlssgModeName(int mode)
    {
        return mode == kDlssgModeDynamic ? "dynamic" : "on";
    }

    // As StreamlineDirectory: from the file, once per swapchain.
    int RayReconstructionPathAsked()
    {
        return ReadRrPath(IniPath());
    }

    const char* XessBadConstantsName(int mode)
    {
        return mode == kXessBadFix ? "fix" : mode == kXessBadPass ? "pass" : "skip";
    }

    const char* RrPathName(int path)
    {
        return path == kRrPathStreamline ? "streamline" : path == kRrPathNgx ? "ngx" : "auto";
    }

    // As StreamlineDirectory: from the file, once per swapchain.
    std::wstring XessFgDirectory()
    {
        wchar_t buffer[260] = {};
        GetPrivateProfileStringW(L"Proxy", L"xessFgDirectory", L"", buffer, ARRAYSIZE(buffer), IniPath().c_str());
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
        fresh.measureOnly = g_config.measureOnly;
        fresh.allowTearing = g_config.allowTearing;

        // The settings that change how the context was built.
        const bool needsRebuild = fresh.fgDepthInverted != g_config.fgDepthInverted
                                  || fresh.fgAsyncWorkloads != g_config.fgAsyncWorkloads;

        {
            std::lock_guard<std::mutex> lock(g_writeLock);
            g_config = fresh;
        }
        LogLine("Config reloaded: " + Describe(g_config)
                + (needsRebuild ? "   (frame generation context will be rebuilt)" : "")
                + "   (enabled, allowTearing, measureOnly and frameGeneration as at load; frameGenerationTechnique,"
                  " the folders, rtOwnQueue, streamlineImgui, vendorDiagnostics, xell, xellLowLatencyBoost,"
                  " xessFgUiMode, xessFgOwnWaitable, xessFgAllowOld, rrPath and the most xessFgFrames apply when"
                  " what they serve starts)");

        rebuild = needsRebuild;
        return true;
    }
}
