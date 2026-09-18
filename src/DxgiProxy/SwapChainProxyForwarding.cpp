// The proxy's IDXGISwapChain4 surface (SwapChainProxy) that is not its own:
// COM identity, and every call the real swapchain answers.

#include "SwapChainProxy.h"

#include "Log.h"

#include <string>

namespace redefinition
{
    // ------------------------------------------------------- plain forwarding

    HRESULT STDMETHODCALLTYPE SwapChainProxy::QueryInterface(REFIID riid, void** object)
    {
        if (object == nullptr)
            return E_POINTER;

        if (riid == __uuidof(IUnknown) || riid == __uuidof(IDXGIObject)
            || riid == __uuidof(IDXGIDeviceSubObject) || riid == __uuidof(IDXGISwapChain)
            || riid == __uuidof(IDXGISwapChain1))
        {
            AddRef();
            *object = static_cast<IDXGISwapChain1*>(this);
            return S_OK;
        }

        // Only offered where the swapchain behind the proxy has them, so a caller
        // that checks for a capability gets one that works.
        if (riid == __uuidof(IDXGISwapChain2) && target2 != nullptr)
        {
            AddRef();
            *object = static_cast<IDXGISwapChain2*>(this);
            return S_OK;
        }

        if (riid == __uuidof(IDXGISwapChain3) && target3 != nullptr)
        {
            AddRef();
            *object = static_cast<IDXGISwapChain3*>(this);
            return S_OK;
        }

        if (riid == __uuidof(IDXGISwapChain4) && target4 != nullptr)
        {
            AddRef();
            *object = static_cast<IDXGISwapChain4*>(this);
            return S_OK;
        }

        // In measuring mode the caller sees what it would see without the proxy:
        // anything not implemented here is handed to the real swapchain, and calls
        // through that interface are not timed.
        if (measureOnly && realSwapChain != nullptr)
        {
            LogLine("QueryInterface passed through to the real swapchain");
            return realSwapChain->QueryInterface(riid, object);
        }

        // In proxy mode anything else is refused: the swapchain behind has not
        // got it, or handing that one out would let the caller reach past the proxy to
        // buffers Unity does not render into. Logged, because a refusal Unity
        // cannot do without shows nowhere else.
        *object = nullptr;
        LogLine("QueryInterface for an unsupported interface on the swapchain");
        return E_NOINTERFACE;
    }

    ULONG STDMETHODCALLTYPE SwapChainProxy::AddRef()
    {
        return static_cast<ULONG>(InterlockedIncrement(&refCount));
    }

    ULONG STDMETHODCALLTYPE SwapChainProxy::Release()
    {
        const long remaining = InterlockedDecrement(&refCount);
        if (remaining == 0)
            delete this;

        return static_cast<ULONG>(remaining);
    }

    // ------------------------------------------------- IDXGISwapChain2 and up

    HRESULT STDMETHODCALLTYPE SwapChainProxy::SetSourceSize(UINT width, UINT height)
    {
        return target2 ? target2->SetSourceSize(width, height) : E_NOINTERFACE;
    }

    HRESULT STDMETHODCALLTYPE SwapChainProxy::GetSourceSize(UINT* width, UINT* height)
    {
        return target2 ? target2->GetSourceSize(width, height) : E_NOINTERFACE;
    }

    HRESULT STDMETHODCALLTYPE SwapChainProxy::SetMaximumFrameLatency(UINT maxLatency)
    {
        return target2 ? target2->SetMaximumFrameLatency(maxLatency) : E_NOINTERFACE;
    }

    HRESULT STDMETHODCALLTYPE SwapChainProxy::GetMaximumFrameLatency(UINT* maxLatency)
    {
        return target2 ? target2->GetMaximumFrameLatency(maxLatency) : E_NOINTERFACE;
    }

    // KSP creates the swapchain with FRAME_LATENCY_WAITABLE_OBJECT and waits on
    // this handle every frame; without it Unity stops right after creation.
    HANDLE STDMETHODCALLTYPE SwapChainProxy::GetFrameLatencyWaitableObject()
    {
        return target2 ? target2->GetFrameLatencyWaitableObject() : nullptr;
    }

    HRESULT STDMETHODCALLTYPE SwapChainProxy::SetMatrixTransform(const DXGI_MATRIX_3X2_F* matrix)
    {
        return target2 ? target2->SetMatrixTransform(matrix) : E_NOINTERFACE;
    }

    HRESULT STDMETHODCALLTYPE SwapChainProxy::GetMatrixTransform(DXGI_MATRIX_3X2_F* matrix)
    {
        return target2 ? target2->GetMatrixTransform(matrix) : E_NOINTERFACE;
    }

    UINT STDMETHODCALLTYPE SwapChainProxy::GetCurrentBackBufferIndex()
    {
        return target3 ? target3->GetCurrentBackBufferIndex() : 0;
    }

    HRESULT STDMETHODCALLTYPE SwapChainProxy::CheckColorSpaceSupport(DXGI_COLOR_SPACE_TYPE space,
                                                                     UINT* support)
    {
        return target3 ? target3->CheckColorSpaceSupport(space, support) : E_NOINTERFACE;
    }

    HRESULT STDMETHODCALLTYPE SwapChainProxy::SetColorSpace1(DXGI_COLOR_SPACE_TYPE space)
    {
        return target3 ? target3->SetColorSpace1(space) : E_NOINTERFACE;
    }

    HRESULT STDMETHODCALLTYPE SwapChainProxy::ResizeBuffers1(UINT bufferCount, UINT width, UINT height,
                                                             DXGI_FORMAT format, UINT flags,
                                                             const UINT* nodeMask,
                                                             IUnknown* const* presentQueue)
    {
        // Routed through the ordinary resize so the shared texture is rebuilt
        // with it; the node mask and queue list only mean something to a caller
        // that owns the D3D12 queue: the proxy, not the game.
        if (!measureOnly)
            return ResizeBuffers(bufferCount, width, height, format, flags);

        return target3 ? target3->ResizeBuffers1(bufferCount, width, height, format, flags,
                                                 nodeMask, presentQueue)
                       : E_NOINTERFACE;
    }

    HRESULT STDMETHODCALLTYPE SwapChainProxy::SetHDRMetaData(DXGI_HDR_METADATA_TYPE type, UINT size,
                                                             void* metaData)
    {
        return target4 ? target4->SetHDRMetaData(type, size, metaData) : E_NOINTERFACE;
    }

    IDXGISwapChain1* SwapChainProxy::Target() const
    {
        return measureOnly ? realSwapChain.Get() : swapChain.Get();
    }

    HRESULT STDMETHODCALLTYPE SwapChainProxy::SetPrivateData(REFGUID name, UINT size, const void* data)
    {
        return Target()->SetPrivateData(name, size, data);
    }

    HRESULT STDMETHODCALLTYPE SwapChainProxy::SetPrivateDataInterface(REFGUID name, const IUnknown* unknown)
    {
        return Target()->SetPrivateDataInterface(name, unknown);
    }

    HRESULT STDMETHODCALLTYPE SwapChainProxy::GetPrivateData(REFGUID name, UINT* size, void* data)
    {
        return Target()->GetPrivateData(name, size, data);
    }

    HRESULT STDMETHODCALLTYPE SwapChainProxy::GetParent(REFIID riid, void** parent)
    {
        return Target()->GetParent(riid, parent);
    }

    HRESULT STDMETHODCALLTYPE SwapChainProxy::GetDevice(REFIID riid, void** device)
    {
        // The D3D11 device, not the D3D12 one: as far as the caller is
        // concerned this swapchain belongs to the device it passed in.
        if (d3d11Device == nullptr)
            return E_NOINTERFACE;

        return d3d11Device->QueryInterface(riid, device);
    }

    // Logged: whether KSP switches to exclusive full screen through the proxy, and
    // what FSR's swapchain answers. AMD: "VRR and tearing are only available in
    // fullscreen mode" (frame-interpolation-swap-chain.md).
    HRESULT STDMETHODCALLTYPE SwapChainProxy::SetFullscreenState(BOOL fullscreenState, IDXGIOutput* target)
    {
        PresentWithStreamlineOff("the switch to or from full screen");
        const HRESULT hr = Target()->SetFullscreenState(fullscreenState, target);
        if (fullscreenLogs < 20)
        {
            ++fullscreenLogs;
            LogLine(std::string("SetFullscreenState(") + (fullscreenState ? "full screen" : "windowed") + ") -> "
                    + Hr(hr));
        }
        return hr;
    }

    HRESULT STDMETHODCALLTYPE SwapChainProxy::GetFullscreenState(BOOL* fullscreenState, IDXGIOutput** target)
    {
        return Target()->GetFullscreenState(fullscreenState, target);
    }

    HRESULT STDMETHODCALLTYPE SwapChainProxy::GetDesc(DXGI_SWAP_CHAIN_DESC* desc)
    {
        return Target()->GetDesc(desc);
    }

    HRESULT STDMETHODCALLTYPE SwapChainProxy::ResizeTarget(const DXGI_MODE_DESC* target)
    {
        PresentWithStreamlineOff("the change of the display mode");
        const HRESULT hr = Target()->ResizeTarget(target);
        if (fullscreenLogs < 20)
        {
            ++fullscreenLogs;
            LogLine("ResizeTarget(" + (target != nullptr ? std::to_string(target->Width) + "x" + std::to_string(target->Height)
                                                         : std::string("null"))
                    + ") -> " + Hr(hr));
        }
        return hr;
    }

    HRESULT STDMETHODCALLTYPE SwapChainProxy::GetContainingOutput(IDXGIOutput** output)
    {
        return Target()->GetContainingOutput(output);
    }

    HRESULT STDMETHODCALLTYPE SwapChainProxy::GetFrameStatistics(DXGI_FRAME_STATISTICS* stats)
    {
        return Target()->GetFrameStatistics(stats);
    }

    HRESULT STDMETHODCALLTYPE SwapChainProxy::GetLastPresentCount(UINT* count)
    {
        return Target()->GetLastPresentCount(count);
    }

    HRESULT STDMETHODCALLTYPE SwapChainProxy::GetDesc1(DXGI_SWAP_CHAIN_DESC1* desc)
    {
        return Target()->GetDesc1(desc);
    }

    HRESULT STDMETHODCALLTYPE SwapChainProxy::GetFullscreenDesc(DXGI_SWAP_CHAIN_FULLSCREEN_DESC* desc)
    {
        return Target()->GetFullscreenDesc(desc);
    }

    HRESULT STDMETHODCALLTYPE SwapChainProxy::GetHwnd(HWND* hwnd)
    {
        if (hwnd == nullptr)
            return E_POINTER;

        *hwnd = window;
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE SwapChainProxy::GetCoreWindow(REFIID riid, void** unk)
    {
        return Target()->GetCoreWindow(riid, unk);
    }

    BOOL STDMETHODCALLTYPE SwapChainProxy::IsTemporaryMonoSupported()
    {
        return Target()->IsTemporaryMonoSupported();
    }

    HRESULT STDMETHODCALLTYPE SwapChainProxy::GetRestrictToOutput(IDXGIOutput** output)
    {
        return Target()->GetRestrictToOutput(output);
    }

    HRESULT STDMETHODCALLTYPE SwapChainProxy::SetBackgroundColor(const DXGI_RGBA* colour)
    {
        return Target()->SetBackgroundColor(colour);
    }

    HRESULT STDMETHODCALLTYPE SwapChainProxy::GetBackgroundColor(DXGI_RGBA* colour)
    {
        return Target()->GetBackgroundColor(colour);
    }

    HRESULT STDMETHODCALLTYPE SwapChainProxy::SetRotation(DXGI_MODE_ROTATION rotation)
    {
        return Target()->SetRotation(rotation);
    }

    HRESULT STDMETHODCALLTYPE SwapChainProxy::GetRotation(DXGI_MODE_ROTATION* rotation)
    {
        return Target()->GetRotation(rotation);
    }
}
