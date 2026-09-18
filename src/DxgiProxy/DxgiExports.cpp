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
#include "FidelityFx.h"
#include "Log.h"
#include "NvidiaGpu.h"
#include "SwapChainProxy.h"

#include <windows.h>

#include <dxgi1_6.h>
#include <wrl/client.h>

using Microsoft::WRL::ComPtr;

#include <mutex>
#include <string>

namespace
{
    HMODULE g_realDxgi = nullptr;
    std::once_flag g_loadOnce;
    std::once_flag g_hookOnce;

    using CreateSwapChainForHwndFn = HRESULT(STDMETHODCALLTYPE*)(
        IDXGIFactory2*, IUnknown*, HWND, const DXGI_SWAP_CHAIN_DESC1*,
        const DXGI_SWAP_CHAIN_FULLSCREEN_DESC*, IDXGIOutput*, IDXGISwapChain1**);

    CreateSwapChainForHwndFn g_originalCreateSwapChainForHwnd = nullptr;

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

    template <typename T>
    T RealProc(const char* name)
    {
        LoadRealDxgi();
        if (g_realDxgi == nullptr)
            return nullptr;

        return reinterpret_cast<T>(GetProcAddress(g_realDxgi, name));
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

        if (redefinition::Config().enabled && desc != nullptr && swapChain != nullptr && hwnd != nullptr)
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

                    if (SUCCEEDED(redefinition::SwapChainProxy::CreateMeasuring(device, real, *desc, &wrapper)))
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

    void HookIfFactory(REFIID riid, void* object)
    {
        if (object == nullptr)
            return;

        IDXGIFactory2* factory = nullptr;
        if (SUCCEEDED(static_cast<IUnknown*>(object)->QueryInterface(IID_PPV_ARGS(&factory))))
        {
            HookFactory(factory);
            factory->Release();
        }
        else
        {
            (void)riid;
        }
    }
}

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

extern "C" HRESULT WINAPI CreateDXGIFactory(REFIID riid, void** factory)
{
    using Fn = HRESULT(WINAPI*)(REFIID, void**);
    const auto real = RealProc<Fn>("CreateDXGIFactory");
    if (real == nullptr)
        return E_FAIL;

    const HRESULT hr = real(riid, factory);
    if (SUCCEEDED(hr))
        HookIfFactory(riid, *factory);

    return hr;
}

extern "C" HRESULT WINAPI CreateDXGIFactory1(REFIID riid, void** factory)
{
    using Fn = HRESULT(WINAPI*)(REFIID, void**);
    const auto real = RealProc<Fn>("CreateDXGIFactory1");
    if (real == nullptr)
        return E_FAIL;

    const HRESULT hr = real(riid, factory);
    if (SUCCEEDED(hr))
        HookIfFactory(riid, *factory);

    return hr;
}

extern "C" HRESULT WINAPI CreateDXGIFactory2(UINT flags, REFIID riid, void** factory)
{
    using Fn = HRESULT(WINAPI*)(UINT, REFIID, void**);
    const auto real = RealProc<Fn>("CreateDXGIFactory2");
    if (real == nullptr)
        return E_FAIL;

    const HRESULT hr = real(flags, riid, factory);
    if (SUCCEEDED(hr))
        HookIfFactory(riid, *factory);

    return hr;
}

extern "C" HRESULT WINAPI DXGIDeclareAdapterRemovalSupport()
{
    using Fn = HRESULT(WINAPI*)();
    const auto real = RealProc<Fn>("DXGIDeclareAdapterRemovalSupport");
    return real != nullptr ? real() : E_FAIL;
}

extern "C" HRESULT WINAPI DXGIGetDebugInterface1(UINT flags, REFIID riid, void** debug)
{
    using Fn = HRESULT(WINAPI*)(UINT, REFIID, void**);
    const auto real = RealProc<Fn>("DXGIGetDebugInterface1");
    return real != nullptr ? real(flags, riid, debug) : E_FAIL;
}
