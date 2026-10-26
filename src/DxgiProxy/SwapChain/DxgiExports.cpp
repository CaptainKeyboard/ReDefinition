// The dxgi.dll that stands in for Windows' own.
//
// Windows searches the application directory before system32, so a file called
// dxgi.dll next to KSP_x64.exe is loaded in place of the system's -- as ReShade
// is loaded.
//
// Every export therefore exists here and reaches the system library, or the
// process does not start. One vtable entry is patched:
// IDXGIFactory2::CreateSwapChainForHwnd, which Unity 2019.4 creates its
// swapchain with.

#include "LoadMonitor.h"
#include "Config.h"
#include "Log.h"
#include "NvidiaGpu.h"
#include "SwapChainProxy.h"

#include <windows.h>

#include <dxgi1_6.h>
#include <wrl/client.h>

using Microsoft::WRL::ComPtr;

#include <cstring>
#include <mutex>
#include <string>

namespace
{
    // The system dxgi.dll once loaded, and the once flags of loading it and patching the factory vtable.
    HMODULE g_realDxgi = nullptr;
    std::once_flag g_loadOnce;
    std::once_flag g_hookOnce;

    // The signature of IDXGIFactory2::CreateSwapChainForHwnd as a vtable entry.
    using CreateSwapChainForHwndFn = HRESULT(STDMETHODCALLTYPE*)(
        IDXGIFactory2*, IUnknown*, HWND, const DXGI_SWAP_CHAIN_DESC1*,
        const DXGI_SWAP_CHAIN_FULLSCREEN_DESC*, IDXGIOutput*, IDXGISwapChain1**);

    // The factory's own CreateSwapChainForHwnd, which the hook falls through to.
    CreateSwapChainForHwndFn g_originalCreateSwapChainForHwnd = nullptr;

    // The system dxgi.dll from System32, loaded once by full path, never freed.
    void LoadRealDxgi()
    {
        std::call_once(g_loadOnce, [] {
            wchar_t system[MAX_PATH] = {};
            GetSystemDirectoryW(system, MAX_PATH);

            std::wstring path = system;
            path += L"\\dxgi.dll";

            g_realDxgi = LoadLibraryW(path.c_str());
            redefinition::LogLine(g_realDxgi != nullptr ? "System dxgi.dll loaded"
                                               : "System dxgi.dll could NOT be loaded");
        });
    }

    // An export of the system dxgi.dll, read from its export table rather than through
    // GetProcAddress. A tool that hooks GetProcAddress, as RenderDoc does, hands back its own
    // CreateDXGIFactory*. That calls the first dxgi.dll in the process, this one, which asks
    // GetProcAddress again, without end. This was seen under renderdoccmd, where the process ended
    // right after loading the system dxgi.dll. Null for a forwarded export or a name not there.
    FARPROC ExportOf(HMODULE module, const char* name)
    {
        const auto base = reinterpret_cast<const BYTE*>(module);
        const auto dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(base);
        if (dos->e_magic != IMAGE_DOS_SIGNATURE)
            return nullptr;
        const auto nt = reinterpret_cast<const IMAGE_NT_HEADERS*>(base + dos->e_lfanew);
        if (nt->Signature != IMAGE_NT_SIGNATURE)
            return nullptr;
        const IMAGE_DATA_DIRECTORY& dir = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_EXPORT];
        if (dir.VirtualAddress == 0 || dir.Size == 0)
            return nullptr;
        const auto exports = reinterpret_cast<const IMAGE_EXPORT_DIRECTORY*>(base + dir.VirtualAddress);
        const auto names = reinterpret_cast<const DWORD*>(base + exports->AddressOfNames);
        const auto ordinals = reinterpret_cast<const WORD*>(base + exports->AddressOfNameOrdinals);
        const auto functions = reinterpret_cast<const DWORD*>(base + exports->AddressOfFunctions);
        for (DWORD i = 0; i < exports->NumberOfNames; ++i)
        {
            if (std::strcmp(reinterpret_cast<const char*>(base + names[i]), name) != 0)
                continue;
            const DWORD rva = functions[ordinals[i]];
            if (rva >= dir.VirtualAddress && rva < dir.VirtualAddress + dir.Size)
                return nullptr;
            return reinterpret_cast<FARPROC>(const_cast<BYTE*>(base + rva));
        }
        return nullptr;
    }

    // An export of the system dxgi.dll as type T, loading it first; GetProcAddress where the export table has
    // none of that name; null without the system library.
    template <typename T>
    T RealProc(const char* name)
    {
        LoadRealDxgi();
        if (g_realDxgi == nullptr)
            return nullptr;

        FARPROC proc = ExportOf(g_realDxgi, name);
        if (proc == nullptr)
            proc = GetProcAddress(g_realDxgi, name);
        return reinterpret_cast<T>(proc);
    }

    // The replacement. Anything that fails falls through to the original, and the
    // game renders as without the proxy.
    HRESULT STDMETHODCALLTYPE HookedCreateSwapChainForHwnd(
        IDXGIFactory2* factory, IUnknown* device, HWND hwnd,
        const DXGI_SWAP_CHAIN_DESC1* desc,
        const DXGI_SWAP_CHAIN_FULLSCREEN_DESC* fullscreenDesc,
        IDXGIOutput* restrictToOutput, IDXGISwapChain1** swapChain)
    {
        // Which adapter the game renders on, whatever becomes of the swapchain --
        // DLSS runs through NGX on D3D11 with the proxy switched off as well -- for
        // what NVIDIA's DLLs are offered by (NvidiaGpu.h).
        ComPtr<IDXGIDevice> dxgiDevice;
        ComPtr<IDXGIAdapter> adapter;
        DXGI_ADAPTER_DESC adapterDesc = {};
        if (device != nullptr && SUCCEEDED(device->QueryInterface(IID_PPV_ARGS(&dxgiDevice)))
            && SUCCEEDED(dxgiDevice->GetAdapter(&adapter)) && SUCCEEDED(adapter->GetDesc(&adapterDesc)))
            redefinition::NoteSwapChainAdapter(adapterDesc.AdapterLuid);

        if (redefinition::Config().enabled && device != nullptr && desc != nullptr && swapChain != nullptr
            && hwnd != nullptr)
        {
            // Baseline mode: let the original create exactly the swapchain the
            // game asked for, then wrap it so Present is timed by the same code
            // that times the D3D12 path. Anything that fails here leaves the
            // real swapchain in place, unwrapped.
            if (redefinition::Config().measureOnly)
            {
                const HRESULT hr = g_originalCreateSwapChainForHwnd(
                    factory, device, hwnd, desc, fullscreenDesc, restrictToOutput, swapChain);

                if (SUCCEEDED(hr) && *swapChain != nullptr)
                {
                    IDXGISwapChain1* real = *swapChain;
                    IDXGISwapChain1* wrapper = nullptr;

                    if (SUCCEEDED(redefinition::SwapChainProxy::CreateMeasuring(device, hwnd, real, *desc, &wrapper)))
                    {
                        // The wrapper took its own reference in CreateMeasuring.
                        real->Release();
                        *swapChain = wrapper;
                    }
                }

                return hr;
            }

            IDXGISwapChain1* proxy = nullptr;
            const HRESULT hr = redefinition::SwapChainProxy::Create(
                factory, device, hwnd, *desc, fullscreenDesc, restrictToOutput, &proxy);

            if (SUCCEEDED(hr) && proxy != nullptr)
            {
                *swapChain = proxy;
                return S_OK;
            }
        }

        return g_originalCreateSwapChainForHwnd(factory, device, hwnd, desc, fullscreenDesc,
                                                restrictToOutput, swapChain);
    }

    // The vtable is patched rather than the factory wrapped in a COM proxy, which
    // would reimplement every IDXGIFactory method and QueryInterface across six
    // interface generations. The vtable is shared by every factory instance, so
    // one patch covers all of them however they were obtained.
    //
    // Index 15 is CreateSwapChainForHwnd: 3 for IUnknown, 4 for IDXGIObject,
    // 5 for IDXGIFactory, 2 for IDXGIFactory1, then IsWindowedStereoEnabled.
    void HookFactory(IDXGIFactory2* factory)
    {
        if (factory == nullptr)
            return;

        std::call_once(g_hookOnce, [factory] {
            void** vtable = *reinterpret_cast<void***>(factory);
            constexpr int kCreateSwapChainForHwnd = 15;

            DWORD previous = 0;
            if (!VirtualProtect(&vtable[kCreateSwapChainForHwnd], sizeof(void*),
                                PAGE_READWRITE, &previous))
            {
                redefinition::LogLine("VirtualProtect on the factory vtable failed");
                return;
            }

            g_originalCreateSwapChainForHwnd =
                reinterpret_cast<CreateSwapChainForHwndFn>(vtable[kCreateSwapChainForHwnd]);
            vtable[kCreateSwapChainForHwnd] = reinterpret_cast<void*>(&HookedCreateSwapChainForHwnd);

            VirtualProtect(&vtable[kCreateSwapChainForHwnd], sizeof(void*), previous, &previous);
            redefinition::LogLine("CreateSwapChainForHwnd hooked");
        });
    }

    // The vtable hooked where the object a CreateDXGIFactory* returned is an IDXGIFactory2.
    void HookIfFactory(void* object)
    {
        if (object == nullptr)
            return;

        IDXGIFactory2* factory = nullptr;
        if (SUCCEEDED(static_cast<IUnknown*>(object)->QueryInterface(IID_PPV_ARGS(&factory))))
        {
            HookFactory(factory);
            factory->Release();
        }
    }
}

// Attach: the log opened, the ini read and the load monitor told whether the proxy is on. Detach: the log
// closed, unless the process is ending.
BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID reserved)
{
    switch (reason)
    {
    case DLL_PROCESS_ATTACH:
        DisableThreadLibraryCalls(module);
        redefinition::LogOpen();
        redefinition::LogLine("ReDefinition dxgi proxy attached");
        redefinition::LoadConfig();
        redefinition::LoadMonitor::Get().SetAllowed(redefinition::Config().enabled);
        break;

    case DLL_PROCESS_DETACH:
        // Not while the process is ending (reserved set): its other threads
        // are already gone, the load monitor's among them, and one may have
        // died holding the log's lock -- taking it here would hang the exit.
        if (reserved == nullptr)
        {
            redefinition::LogLine("Detached");
            redefinition::LogClose();
        }
        break;

    default:
        break;
    }

    return TRUE;
}

// The system's factory, its CreateSwapChainForHwnd hooked on the way out (HookIfFactory).
extern "C" HRESULT WINAPI CreateDXGIFactory(REFIID riid, void** factory)
{
    using Fn = HRESULT(WINAPI*)(REFIID, void**);
    const auto real = RealProc<Fn>("CreateDXGIFactory");
    if (real == nullptr)
        return E_FAIL;

    const HRESULT hr = real(riid, factory);
    if (SUCCEEDED(hr))
        HookIfFactory(*factory);

    return hr;
}

// The system's factory, its CreateSwapChainForHwnd hooked on the way out (HookIfFactory).
extern "C" HRESULT WINAPI CreateDXGIFactory1(REFIID riid, void** factory)
{
    using Fn = HRESULT(WINAPI*)(REFIID, void**);
    const auto real = RealProc<Fn>("CreateDXGIFactory1");
    if (real == nullptr)
        return E_FAIL;

    const HRESULT hr = real(riid, factory);
    if (SUCCEEDED(hr))
        HookIfFactory(*factory);

    return hr;
}

// The system's factory, its CreateSwapChainForHwnd hooked on the way out (HookIfFactory).
extern "C" HRESULT WINAPI CreateDXGIFactory2(UINT flags, REFIID riid, void** factory)
{
    using Fn = HRESULT(WINAPI*)(UINT, REFIID, void**);
    const auto real = RealProc<Fn>("CreateDXGIFactory2");
    if (real == nullptr)
        return E_FAIL;

    const HRESULT hr = real(flags, riid, factory);
    if (SUCCEEDED(hr))
        HookIfFactory(*factory);

    return hr;
}

// Passed to the system dxgi.dll; E_FAIL where it is missing.
extern "C" HRESULT WINAPI DXGIDeclareAdapterRemovalSupport()
{
    using Fn = HRESULT(WINAPI*)();
    const auto real = RealProc<Fn>("DXGIDeclareAdapterRemovalSupport");
    return real != nullptr ? real() : E_FAIL;
}

// Passed to the system dxgi.dll; E_FAIL where it is missing.
extern "C" HRESULT WINAPI DXGIGetDebugInterface1(UINT flags, REFIID riid, void** debug)
{
    using Fn = HRESULT(WINAPI*)(UINT, REFIID, void**);
    const auto real = RealProc<Fn>("DXGIGetDebugInterface1");
    return real != nullptr ? real(flags, riid, debug) : E_FAIL;
}
