#include "UpscalerRig.h"

#include "AmdUpscaler.h"
#include "Log.h"

using Microsoft::WRL::ComPtr;

namespace redefinition
{
    UpscalerRig::UpscalerRig(const char* prefix, const char* initialText) : statusText(initialText), logPrefix(prefix)
    {
    }

    bool UpscalerRig::TextureDesc(void* texture, D3D11_TEXTURE2D_DESC& desc)
    {
        ComPtr<ID3D11Texture2D> texture2d;
        if (texture == nullptr
            || FAILED(static_cast<ID3D11Resource*>(texture)->QueryInterface(IID_PPV_ARGS(&texture2d))))
            return false;
        texture2d->GetDesc(&desc);
        return true;
    }

    bool UpscalerRig::SameContext(const AmdUpscalerPacket& a, const AmdUpscalerPacket& b)
    {
        const uint32_t creationFlags = kUpscalerHdr | kUpscalerDepthInverted | kUpscalerAutoExposure;
        return a.renderWidth == b.renderWidth && a.renderHeight == b.renderHeight
            && a.outputWidth == b.outputWidth && a.outputHeight == b.outputHeight
            && (a.flags & creationFlags) == (b.flags & creationFlags);
    }

    bool UpscalerRig::Match(SharedTexture& shared, ID3D11Device* device11, ID3D12Device* device12,
                            const D3D11_TEXTURE2D_DESC& desc)
    {
        const DXGI_FORMAT format = TypedFormat(desc.Format);
        if (shared.Valid() && shared.width == desc.Width && shared.height == desc.Height && shared.format == format)
            return true;
        return shared.Create(device11, device12, desc.Width, desc.Height, format);
    }

    void UpscalerRig::SetStatus(int newState, const std::string& text)
    {
        renderState = newState;
        bool changed = false;
        {
            std::lock_guard<std::mutex> lock(statusLock);
            changed = state != newState || statusText != text;
            state = newState;
            statusText = text;
            stableSignature = newState == -2 ? runtimeSignature : std::wstring();
        }
        if (changed && newState < 0)
            LogLine(std::string(logPrefix) + text);
    }

    int UpscalerRig::Status(char* buffer, int size) const
    {
        int reported = 0;
        std::wstring stood;
        {
            std::lock_guard<std::mutex> lock(statusLock);
            // Cut to the buffer: sprintf_s would end the process on a longer text.
            if (buffer != nullptr && size > 0)
                strncpy_s(buffer, static_cast<size_t>(size), statusText.c_str(), _TRUNCATE);
            reported = state;
            if (reported == -2)
                stood = stableSignature;
        }
        // A DLL that did not load stands only while the folder and the file in it
        // are as they were: once the player has copied one, the next rig tries it
        // (RuntimeDueLocked). The files are asked outside the lock.
        if (reported == -2 && RuntimeSignature() != stood)
            reported = -1;
        return reported;
    }

    void UpscalerRig::Attach(const void* newOwner, ID3D11Device5* newDevice11, ID3D11DeviceContext4* newContext11,
                             ID3D12Device* newDevice12, ID3D12CommandQueue* newQueue)
    {
        std::lock_guard<std::mutex> lock(mutex);
        ReleaseLocked();
        fence12.Reset();
        fence11.Reset();
        for (auto& allocator : allocators)
            allocator.Reset();
        list.Reset();
        owner = newOwner;
        device11 = newDevice11;
        context11 = newContext11;
        device12 = newDevice12;
        queue = newQueue;
        attached = newDevice12 != nullptr && newQueue != nullptr;
    }

    void UpscalerRig::Detach(const void* detaching)
    {
        std::lock_guard<std::mutex> lock(mutex);
        if (detaching != owner)
            return;
        ReleaseLocked();
        fence12.Reset();
        fence11.Reset();
        for (auto& allocator : allocators)
            allocator.Reset();
        list.Reset();
        queue.Reset();
        device12.Reset();
        context11.Reset();
        device11.Reset();
        owner = nullptr;
        attached = false;
    }

    bool UpscalerRig::RuntimeDueLocked()
    {
        // Tried once -- again only once the folder or the DLL in it has changed,
        // without waiting for a release, and looked at at most once a second.
        if (runtimeTried)
        {
            const ULONGLONG now = GetTickCount64();
            if (now < nextRuntimeCheck)
                return false;
            nextRuntimeCheck = now + 1000;
        }
        const std::wstring signature = RuntimeSignature();
        if (runtimeTried && signature == runtimeSignature)
            return false;
        runtimeTried = true;
        runtimeSignature = signature;
        return true;
    }

    bool UpscalerRig::EnsureFenceLocked()
    {
        if (list != nullptr)
            return true;

        HRESULT hr = SharedFence(device11.Get(), device12.Get(), fence12, fence11);
        for (UINT i = 0; SUCCEEDED(hr) && i < kLists; ++i)
            hr = device12->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&allocators[i]));
        if (SUCCEEDED(hr))
            hr = device12->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, allocators[0].Get(), nullptr,
                                             IID_PPV_ARGS(&list));
        if (FAILED(hr))
        {
            SetStatus(-1, "the shared fence or command list could not be made (" + Hr(hr) + ")");
            list.Reset();
            return false;
        }
        list->Close();

        if (fenceEvent == nullptr)
            fenceEvent = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        fenceValue = 0;
        for (UINT64& done : listDone)
            done = 0;
        return true;
    }

    bool UpscalerRig::EnsureTexturesLocked(const AmdUpscalerPacket& packet)
    {
        D3D11_TEXTURE2D_DESC colourDesc = {}, depthDesc = {}, motionDesc = {}, outputDesc = {};
        if (!TextureDesc(packet.colour, colourDesc) || !TextureDesc(packet.depth, depthDesc)
            || !TextureDesc(packet.motionVectors, motionDesc) || !TextureDesc(packet.output, outputDesc))
        {
            SetStatus(-1, "a texture of the frame is not a 2D texture");
            return false;
        }

        const auto matches = [](const SharedTexture& shared, const D3D11_TEXTURE2D_DESC& desc) {
            return shared.Valid() && shared.width == desc.Width && shared.height == desc.Height
                   && shared.format == TypedFormat(desc.Format);
        };
        const bool same = matches(colour, colourDesc) && matches(output, outputDesc) && matches(depth, depthDesc)
                          && matches(motion, motionDesc);
        if (same)
            return true;

        // Nothing on the queue may still read the textures about to go.
        WaitForListsLocked();
        if (!Match(colour, device11.Get(), device12.Get(), colourDesc)
            || !Match(depth, device11.Get(), device12.Get(), depthDesc)
            || !Match(motion, device11.Get(), device12.Get(), motionDesc)
            || !Match(output, device11.Get(), device12.Get(), outputDesc))
        {
            SetStatus(-1, "the textures shared with D3D12 could not be made");
            return false;
        }
        return true;
    }

    void UpscalerRig::WaitForListsLocked()
    {
        if (fence12 == nullptr || fenceEvent == nullptr || fenceValue == 0)
            return;
        if (fence12->GetCompletedValue() >= fenceValue)
            return;
        if (SUCCEEDED(fence12->SetEventOnCompletion(fenceValue, fenceEvent)))
            WaitForSingleObject(fenceEvent, INFINITE);
    }

    void UpscalerRig::ReleaseTexturesLocked()
    {
        colour.Reset();
        depth.Reset();
        motion.Reset();
        output.Reset();
    }

    void UpscalerRig::SignalInputsLocked()
    {
        const UINT64 inputsCopied = ++fenceValue;
        context11->Signal(fence11.Get(), inputsCopied);
        context11->Flush();
        queue->Wait(fence12.Get(), inputsCopied);
    }

    bool UpscalerRig::BeginListLocked(UINT& index)
    {
        // One of three allocators, whose list has left the GPU.
        index = listIndex;
        listIndex = (listIndex + 1) % kLists;
        if (listDone[index] != 0 && fence12->GetCompletedValue() < listDone[index]
            && SUCCEEDED(fence12->SetEventOnCompletion(listDone[index], fenceEvent)))
            WaitForSingleObject(fenceEvent, INFINITE);
        HRESULT hr = allocators[index]->Reset();
        if (SUCCEEDED(hr))
            hr = list->Reset(allocators[index].Get(), nullptr);
        if (FAILED(hr))
        {
            SetStatus(-1, "the command list could not be reset (" + Hr(hr) + ")");
            return false;
        }
        return true;
    }

    bool UpscalerRig::FinishListLocked(UINT index)
    {
        // A list that does not close is not executed; the fence below is signalled
        // all the same.
        const HRESULT hr = list->Close();
        if (SUCCEEDED(hr))
        {
            ID3D12CommandList* lists[] = { list.Get() };
            queue->ExecuteCommandLists(1, lists);
        }
        const UINT64 finished = ++fenceValue;
        queue->Signal(fence12.Get(), finished);
        listDone[index] = finished;

        // D3D11 waits for the queue even where the dispatch failed: the signal above is
        // queued either way, and a later D3D11 signal on the shared fence that ran ahead
        // of it would set the fence back.
        context11->Wait(fence11.Get(), finished);
        if (FAILED(hr))
        {
            SetStatus(-1, "the command list could not be closed (" + Hr(hr) + ")");
            return false;
        }
        return true;
    }
}
