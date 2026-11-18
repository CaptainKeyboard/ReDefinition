#include "GpuAddressBook.h"

#include "FileUtil.h"
#include "Log.h"

#include <windows.h>
#include <d3d11_3.h>
#include <intrin.h>
#include <wrl/client.h>

#include <algorithm>
#include <map>
#include <cstdio>
#include <cwchar>
#include <mutex>
#include <vector>

#pragma intrinsic(_ReturnAddress)

namespace redefinition
{
    namespace GpuAddressBook
    {
        namespace
        {
            // NVAPI's ids for nvapi_QueryInterface and the one structure (nvapi.h, nvapi_interface.h; MIT): NvAPI_Initialize,
            // NvAPI_D3D11_GetResourceHandle and NvAPI_D3D11_GetResourceGPUVirtualAddressEx.
            constexpr uint32_t kInitialize = 0x0150e828;
            constexpr uint32_t kGetResourceHandle = 0x09d52986;
            constexpr uint32_t kGetResourceGpuVirtualAddressEx = 0xaf6d14da;

            struct NvGpuVirtualAddress
            {
                uint32_t version;
                void* resource;      // NVDX_ObjectHandle
                uint64_t start;      // gpuVAStart
                uint64_t size;       // gpuVASize
            };
            constexpr uint32_t kNvGpuVirtualAddressVersion = static_cast<uint32_t>(sizeof(NvGpuVirtualAddress)) | (1u << 16);

            using QueryInterfaceFn = void* (*)(uint32_t);
            using InitializeFn = int (*)();
            using GetResourceHandleFn = int (*)(ID3D11Device*, ID3D11Resource*, void**);
            using GetAddressExFn = int (*)(ID3D11Device*, NvGpuVirtualAddress*);

            GetResourceHandleFn g_getHandle = nullptr;
            GetAddressExFn g_getAddress = nullptr;

            // ID3D11Device's entries by their place in its table (d3d11.h: IUnknown's three, then CreateBuffer, CreateTexture1D, 2D and 3D).
            using CreateBufferFn = HRESULT(STDMETHODCALLTYPE*)(ID3D11Device*, const D3D11_BUFFER_DESC*, const D3D11_SUBRESOURCE_DATA*, ID3D11Buffer**);
            using CreateTexture1DFn = HRESULT(STDMETHODCALLTYPE*)(ID3D11Device*, const D3D11_TEXTURE1D_DESC*, const D3D11_SUBRESOURCE_DATA*, ID3D11Texture1D**);
            using CreateTexture2DFn = HRESULT(STDMETHODCALLTYPE*)(ID3D11Device*, const D3D11_TEXTURE2D_DESC*, const D3D11_SUBRESOURCE_DATA*, ID3D11Texture2D**);
            using CreateTexture3DFn = HRESULT(STDMETHODCALLTYPE*)(ID3D11Device*, const D3D11_TEXTURE3D_DESC*, const D3D11_SUBRESOURCE_DATA*, ID3D11Texture3D**);
            CreateBufferFn g_createBuffer = nullptr;
            CreateTexture1DFn g_createTexture1D = nullptr;
            CreateTexture2DFn g_createTexture2D = nullptr;
            CreateTexture3DFn g_createTexture3D = nullptr;
            // And the other ways a resource comes to the device: OpenSharedResource (28), ID3D11Device1::OpenSharedResource1 (48),
            // ID3D11Device3::CreateTexture2D1 and CreateTexture3D1 (54, 55); the device's interfaces derive in one line, so one table holds them all.
            using OpenSharedFn = HRESULT(STDMETHODCALLTYPE*)(ID3D11Device*, HANDLE, REFIID, void**);
            using CreateTexture2D1Fn = HRESULT(STDMETHODCALLTYPE*)(ID3D11Device*, const void*, const D3D11_SUBRESOURCE_DATA*, void**);
            using CreateTexture3D1Fn = HRESULT(STDMETHODCALLTYPE*)(ID3D11Device*, const void*, const D3D11_SUBRESOURCE_DATA*, void**);
            OpenSharedFn g_openShared = nullptr;
            OpenSharedFn g_openShared1 = nullptr;
            CreateTexture2D1Fn g_createTexture2D1 = nullptr;
            CreateTexture3D1Fn g_createTexture3D1 = nullptr;

            struct Entry
            {
                uint64_t start;
                uint64_t size;
                ULONGLONG tick;
                const void* caller;
                char what[96];
            };

            std::mutex g_mutex;
            std::vector<Entry> g_entries;
            // Enough for a long start; the oldest half goes once it is full (the fault is in the minutes before).
            constexpr size_t kMaxEntries = 400000;
            bool g_attached = false;

            std::string ModuleOf(const void* address)
            {
                HMODULE module = nullptr;
                if (address == nullptr || !GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                                                              static_cast<LPCWSTR>(address), &module))
                    return "?";
                wchar_t path[MAX_PATH] = {};
                GetModuleFileNameW(module, path, MAX_PATH);
                const wchar_t* name = wcsrchr(path, L'\\');
                return Narrow(name != nullptr ? name + 1 : path);
            }

            void Record(ID3D11Device* device, ID3D11Resource* resource, const void* caller, const char* what)
            {
                if (resource == nullptr || g_getHandle == nullptr || g_getAddress == nullptr)
                    return;
                void* handle = nullptr;
                if (g_getHandle(device, resource, &handle) != 0 || handle == nullptr)
                    return;
                NvGpuVirtualAddress address = {};
                address.version = kNvGpuVirtualAddressVersion;
                address.resource = handle;
                if (g_getAddress(device, &address) != 0 || address.start == 0)
                    return;
                Entry entry = {};
                entry.start = address.start;
                entry.size = address.size;
                entry.tick = GetTickCount64();
                entry.caller = caller;
                strncpy_s(entry.what, what, _TRUNCATE);
                std::lock_guard<std::mutex> lock(g_mutex);
                if (g_entries.size() >= kMaxEntries)
                    g_entries.erase(g_entries.begin(), g_entries.begin() + kMaxEntries / 2);
                g_entries.push_back(entry);
            }

            HRESULT STDMETHODCALLTYPE CreateBuffer(ID3D11Device* self, const D3D11_BUFFER_DESC* desc, const D3D11_SUBRESOURCE_DATA* data, ID3D11Buffer** out)
            {
                const void* caller = _ReturnAddress();
                const HRESULT result = g_createBuffer(self, desc, data, out);
                if (SUCCEEDED(result) && out != nullptr && *out != nullptr && desc != nullptr)
                {
                    char what[96];
                    sprintf_s(what, "buffer %u bytes bind 0x%X misc 0x%X", desc->ByteWidth, desc->BindFlags, desc->MiscFlags);
                    Record(self, *out, caller, what);
                }
                return result;
            }

            HRESULT STDMETHODCALLTYPE CreateTexture1D(ID3D11Device* self, const D3D11_TEXTURE1D_DESC* desc, const D3D11_SUBRESOURCE_DATA* data, ID3D11Texture1D** out)
            {
                const void* caller = _ReturnAddress();
                const HRESULT result = g_createTexture1D(self, desc, data, out);
                if (SUCCEEDED(result) && out != nullptr && *out != nullptr && desc != nullptr)
                {
                    char what[96];
                    sprintf_s(what, "texture1D %u format %u mips %u array %u bind 0x%X", desc->Width, static_cast<unsigned>(desc->Format), desc->MipLevels,
                              desc->ArraySize, desc->BindFlags);
                    Record(self, *out, caller, what);
                }
                return result;
            }

            HRESULT STDMETHODCALLTYPE CreateTexture2D(ID3D11Device* self, const D3D11_TEXTURE2D_DESC* desc, const D3D11_SUBRESOURCE_DATA* data, ID3D11Texture2D** out)
            {
                const void* caller = _ReturnAddress();
                const HRESULT result = g_createTexture2D(self, desc, data, out);
                if (SUCCEEDED(result) && out != nullptr && *out != nullptr && desc != nullptr)
                {
                    char what[96];
                    sprintf_s(what, "texture2D %ux%u format %u mips %u array %u bind 0x%X misc 0x%X", desc->Width, desc->Height,
                              static_cast<unsigned>(desc->Format), desc->MipLevels, desc->ArraySize, desc->BindFlags, desc->MiscFlags);
                    Record(self, *out, caller, what);
                }
                return result;
            }

            HRESULT STDMETHODCALLTYPE CreateTexture3D(ID3D11Device* self, const D3D11_TEXTURE3D_DESC* desc, const D3D11_SUBRESOURCE_DATA* data, ID3D11Texture3D** out)
            {
                const void* caller = _ReturnAddress();
                const HRESULT result = g_createTexture3D(self, desc, data, out);
                if (SUCCEEDED(result) && out != nullptr && *out != nullptr && desc != nullptr)
                {
                    char what[96];
                    sprintf_s(what, "texture3D %ux%ux%u format %u mips %u bind 0x%X", desc->Width, desc->Height, desc->Depth,
                              static_cast<unsigned>(desc->Format), desc->MipLevels, desc->BindFlags);
                    Record(self, *out, caller, what);
                }
                return result;
            }

            void RecordUnknown(ID3D11Device* device, void* object, const void* caller, const char* what)
            {
                if (object == nullptr)
                    return;
                Microsoft::WRL::ComPtr<ID3D11Resource> resource;
                if (FAILED(static_cast<IUnknown*>(object)->QueryInterface(IID_PPV_ARGS(&resource))))
                    return;
                Record(device, resource.Get(), caller, what);
            }

            HRESULT STDMETHODCALLTYPE OpenShared(ID3D11Device* self, HANDLE handle, REFIID iid, void** out)
            {
                const void* caller = _ReturnAddress();
                const HRESULT result = g_openShared(self, handle, iid, out);
                if (SUCCEEDED(result) && out != nullptr)
                    RecordUnknown(self, *out, caller, "opened shared resource");
                return result;
            }

            HRESULT STDMETHODCALLTYPE OpenShared1(ID3D11Device* self, HANDLE handle, REFIID iid, void** out)
            {
                const void* caller = _ReturnAddress();
                const HRESULT result = g_openShared1(self, handle, iid, out);
                if (SUCCEEDED(result) && out != nullptr)
                    RecordUnknown(self, *out, caller, "opened shared resource (1)");
                return result;
            }

            HRESULT STDMETHODCALLTYPE CreateTexture2D1(ID3D11Device* self, const void* desc, const D3D11_SUBRESOURCE_DATA* data, void** out)
            {
                const void* caller = _ReturnAddress();
                const HRESULT result = g_createTexture2D1(self, desc, data, out);
                if (SUCCEEDED(result) && out != nullptr)
                    RecordUnknown(self, *out, caller, "texture2D (CreateTexture2D1)");
                return result;
            }

            HRESULT STDMETHODCALLTYPE CreateTexture3D1(ID3D11Device* self, const void* desc, const D3D11_SUBRESOURCE_DATA* data, void** out)
            {
                const void* caller = _ReturnAddress();
                const HRESULT result = g_createTexture3D1(self, desc, data, out);
                if (SUCCEEDED(result) && out != nullptr)
                    RecordUnknown(self, *out, caller, "texture3D (CreateTexture3D1)");
                return result;
            }

            template <typename T>
            bool Patch(void** table, int index, T hook, T& original)
            {
                DWORD old = 0;
                if (!VirtualProtect(&table[index], sizeof(void*), PAGE_EXECUTE_READWRITE, &old))
                    return false;
                original = reinterpret_cast<T>(table[index]);
                table[index] = reinterpret_cast<void*>(hook);
                VirtualProtect(&table[index], sizeof(void*), old, &old);
                return true;
            }

            std::string Line(const Entry& e, uint64_t address, ULONGLONG now)
            {
                char text[320];
                sprintf_s(text, "  0x%016llX..0x%016llX (%llu KB), made %.1f s before, by %s: %s%s", static_cast<unsigned long long>(e.start),
                          static_cast<unsigned long long>(e.start + e.size), static_cast<unsigned long long>(e.size / 1024),
                          (now - e.tick) / 1000.0, ModuleOf(e.caller).c_str(), e.what,
                          address >= e.start && address < e.start + e.size ? " <- HOLDS THE ADDRESS" : "");
                return text;
            }
        }

        void Attach(ID3D11Device* device)
        {
            const wchar_t* line = GetCommandLineW();
            if (device == nullptr || g_attached || line == nullptr || wcsstr(line, L"-redefinitionGpuAddressBook") == nullptr)
                return;
            g_attached = true;
            HMODULE nvapi = LoadLibraryExW(L"nvapi64.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
            const auto query = nvapi != nullptr ? reinterpret_cast<QueryInterfaceFn>(GetProcAddress(nvapi, "nvapi_QueryInterface")) : nullptr;
            const auto initialize = query != nullptr ? reinterpret_cast<InitializeFn>(query(kInitialize)) : nullptr;
            if (initialize == nullptr || initialize() != 0)
            {
                LogLine("GPU address book: NVAPI is not available; nothing is written down");
                return;
            }
            g_getHandle = reinterpret_cast<GetResourceHandleFn>(query(kGetResourceHandle));
            g_getAddress = reinterpret_cast<GetAddressExFn>(query(kGetResourceGpuVirtualAddressEx));
            if (g_getHandle == nullptr || g_getAddress == nullptr)
            {
                LogLine("GPU address book: this driver's NVAPI has no D3D11 GPU address query; nothing is written down");
                return;
            }
            void** table = *reinterpret_cast<void***>(device);
            bool patched = Patch(table, 3, &CreateBuffer, g_createBuffer) && Patch(table, 4, &CreateTexture1D, g_createTexture1D)
                           && Patch(table, 5, &CreateTexture2D, g_createTexture2D) && Patch(table, 6, &CreateTexture3D, g_createTexture3D)
                           && Patch(table, 28, &OpenShared, g_openShared);
            // The later interfaces' entries only where the device is one of them through the same table.
            Microsoft::WRL::ComPtr<ID3D11Device3> device3;
            if (patched && SUCCEEDED(device->QueryInterface(IID_PPV_ARGS(&device3))) && *reinterpret_cast<void***>(device3.Get()) == table)
                patched = Patch(table, 48, &OpenShared1, g_openShared1) && Patch(table, 54, &CreateTexture2D1, g_createTexture2D1)
                          && Patch(table, 55, &CreateTexture3D1, g_createTexture3D1);
            else if (patched)
                LogLine("GPU address book: the device's later interfaces are not in its first table; OpenSharedResource1 and CreateTexture2D1/3D1 are not watched");
            LogLine(patched ? "GPU address book: on (the device's buffers and textures are written down as they are made)"
                            : "GPU address book: the device's entries could not be patched");
        }

        void Explain(uint64_t address)
        {
            if (!g_attached || g_getAddress == nullptr)
                return;
            const ULONGLONG now = GetTickCount64();
            std::vector<Entry> holding;
            const Entry* below = nullptr;
            const Entry* above = nullptr;
            size_t count = 0;
            std::vector<std::string> lines;
            std::map<const void*, size_t> perCaller;
            {
                std::lock_guard<std::mutex> lock(g_mutex);
                count = g_entries.size();
                for (const Entry& e : g_entries)
                {
                    ++perCaller[e.caller];
                    if (address >= e.start && address < e.start + e.size)
                        holding.push_back(e);
                    else if (e.start + e.size <= address && (below == nullptr || e.start + e.size > below->start + below->size
                                                             || (e.start + e.size == below->start + below->size && e.tick > below->tick)))
                        below = &e;
                    else if (e.start > address && (above == nullptr || e.start < above->start || (e.start == above->start && e.tick > above->tick)))
                        above = &e;
                }
                std::sort(holding.begin(), holding.end(), [](const Entry& a, const Entry& b) { return a.tick > b.tick; });
                for (size_t i = 0; i < holding.size() && i < 8; ++i)
                    lines.push_back(Line(holding[i], address, now));
                if (below != nullptr)
                    lines.push_back("  nearest below:" + Line(*below, address, now).substr(1));
                if (above != nullptr)
                    lines.push_back("  nearest above:" + Line(*above, address, now).substr(1));
            }
            char head[200];
            sprintf_s(head, "GPU address book: 0x%016llX, %zu resource(s) made since the start held it (of %zu written down)%s",
                      static_cast<unsigned long long>(address), holding.size(), count, holding.empty() ? "; none: not a resource made through the device's entries" : "");
            LogLine(head);
            for (const std::string& text : lines)
                LogLine(text);
            // Who made what was written down: the modules of the callers (Unity, DLSS, the driver, the proxy).
            std::map<std::string, size_t> perModule;
            for (const auto& pair : perCaller)
                perModule[ModuleOf(pair.first)] += pair.second;
            std::string modules = "GPU address book: made by";
            for (const auto& pair : perModule)
                modules += " " + pair.first + " " + std::to_string(pair.second) + ",";
            LogLine(modules);
        }
    }
}
