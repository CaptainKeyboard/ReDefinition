// The proxy's buffers (SwapChainProxy): the shared texture Unity renders into as
// its backbuffer, handed out by GetBuffer and made again by ResizeBuffers.

#include "SwapChainProxy.h"

#include "FrameGeneration.h"
#include "Log.h"

#include <mutex>
#include <string>

using Microsoft::WRL::ComPtr;

namespace redefinition
{
    bool SwapChainProxy::RequireSharedColour()
    {
        if (sharedColour12 != nullptr)
            return true;
        if (!loggedNoSharedColour)
        {
            loggedNoSharedColour = true;
            LogLine("No shared backbuffer since the last resize failed -- nothing is presented until a resize"
                    " succeeds");
        }
        return false;
    }

    // Both halves or neither: Unity is never handed a texture that is not
    // presented.
    HRESULT SwapChainProxy::CreateSharedColour(UINT width, UINT height, DXGI_FORMAT format)
    {
        // Created on the D3D11 device and opened on the D3D12 one. The pair of
        // MISC flags is what makes an NT handle possible; without
        // SHARED_NTHANDLE the handle cannot be opened by D3D12 at all.
        D3D11_TEXTURE2D_DESC texture = {};
        texture.Width = width;
        texture.Height = height;
        texture.MipLevels = 1;
        texture.ArraySize = 1;
        texture.Format = format;
        texture.SampleDesc.Count = 1;
        texture.Usage = D3D11_USAGE_DEFAULT;
        texture.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
        texture.MiscFlags = D3D11_RESOURCE_MISC_SHARED | D3D11_RESOURCE_MISC_SHARED_NTHANDLE;

        HRESULT hr = d3d11Device->CreateTexture2D(&texture, nullptr, &sharedColour);
        if (FAILED(hr))
        {
            LogLine("Shared backbuffer texture could not be created (" + Hr(hr)
                    + "), format " + std::to_string(static_cast<int>(format)));
            return hr;
        }

        ComPtr<IDXGIResource1> resource;
        hr = sharedColour.As(&resource);
        if (FAILED(hr))
        {
            LogLine("The shared backbuffer texture has no IDXGIResource1 (" + Hr(hr) + ")");
            ReleaseSharedColour();
            return hr;
        }

        HANDLE handle = nullptr;
        hr = resource->CreateSharedHandle(nullptr,
                                          DXGI_SHARED_RESOURCE_READ | DXGI_SHARED_RESOURCE_WRITE,
                                          nullptr, &handle);
        if (FAILED(hr))
        {
            LogLine("CreateSharedHandle for the backbuffer failed (" + Hr(hr) + ")");
            ReleaseSharedColour();
            return hr;
        }

        hr = d3d12Device->OpenSharedHandle(handle, IID_PPV_ARGS(&sharedColour12));
        CloseHandle(handle);
        if (FAILED(hr))
        {
            LogLine("OpenSharedHandle for the backbuffer failed (" + Hr(hr) + ")");
            ReleaseSharedColour();
            return hr;
        }

        // A later loss is said again.
        loggedNoSharedColour = false;
        return S_OK;
    }

    void SwapChainProxy::ReleaseSharedColour()
    {
        sharedColour12.Reset();
        sharedColour.Reset();
    }

    HRESULT STDMETHODCALLTYPE SwapChainProxy::GetBuffer(UINT buffer, REFIID riid, void** surface)
    {
        // The substitution itself: Unity believes it is being handed the
        // swapchain's backbuffer and is handed the shared texture instead.
        if (measureOnly)
            return realSwapChain->GetBuffer(buffer, riid, surface);

        if (buffer != 0)
            LogLine("GetBuffer for index " + std::to_string(buffer)
                    + ", returning the shared texture anyway");

        if (sharedColour == nullptr)
            return DXGI_ERROR_INVALID_CALL;

        return sharedColour->QueryInterface(riid, surface);
    }

    HRESULT STDMETHODCALLTYPE SwapChainProxy::ResizeBuffers(UINT bufferCount, UINT width, UINT height,
                                                            DXGI_FORMAT format, UINT flags)
    {
        LogLine("ResizeBuffers to " + std::to_string(width) + "x" + std::to_string(height));

        if (measureOnly)
            return realSwapChain->ResizeBuffers(bufferCount, width, height, format, flags);

        // Nothing may still be reading the old buffers. Frame generation lets go
        // of the backbuffer first -- or its reference would keep the old texture
        // alive and the snapshot would copy a buffer nobody draws into -- and of
        // its context, which was made for the old size.
        PresentWithStreamlineOff("the swapchain is resized");
        WaitForFrame(frameFenceValue);
        FrameGeneration::Get().ReleaseBackBuffer(this, fgSwapChainContext != nullptr ? swapChain.Get() : nullptr);
        ReleaseSharedColour();

        const DXGI_FORMAT effectiveFormat = format == DXGI_FORMAT_UNKNOWN ? description.Format : format;

        // A swapchain has to keep the flag it was created with; losing it here
        // would make every later tearing present fail.
        const UINT effectiveFlags = flags | (description.Flags & DXGI_SWAP_CHAIN_FLAG_ALLOW_TEARING);

        HRESULT hr = swapChain->ResizeBuffers(bufferCount, width, height, format, effectiveFlags);
        if (FAILED(hr))
        {
            // The swapchain keeps its buffers when the resize fails, so Unity's
            // backbuffer comes back at their size: without one, the next Present
            // would have nothing to copy.
            const HRESULT restored = CreateSharedColour(description.Width, description.Height, description.Format);
            if (SUCCEEDED(restored))
                FrameGeneration::Get().RegisterBackBuffer(this, sharedColour.Get());
            LogLine("D3D12 ResizeBuffers failed (" + Hr(hr) + "), the shared backbuffer "
                    + (SUCCEEDED(restored) ? "made again at the old size" : "not made again (" + Hr(restored) + ")"));
            return hr;
        }

        // The size the swapchain has now: DXGI takes 0 for the window's.
        DXGI_SWAP_CHAIN_DESC1 resized = {};
        if (SUCCEEDED(swapChain->GetDesc1(&resized)))
        {
            width = resized.Width;
            height = resized.Height;
        }

        description.Width = width;
        description.Height = height;
        description.Format = effectiveFormat;
        if (bufferCount != 0)
            description.BufferCount = bufferCount;
        description.Flags = effectiveFlags;

        // The window may have changed monitor or mode with it.
        {
            std::lock_guard<std::mutex> lock(refreshMutex);
            refreshNow = true;
        }
        refreshWake.notify_all();
        limitDue = 0;

        hr = CreateSharedColour(width, height, effectiveFormat);
        if (FAILED(hr))
            return hr;

        FrameGeneration::Get().RegisterBackBuffer(this, sharedColour.Get());
        ResolveTargetInterfaces();
        return S_OK;
    }
}
