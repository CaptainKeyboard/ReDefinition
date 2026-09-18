#include "AmdUpscaler.h"

#include "Config.h"
#include "D3d12Util.h"
#include "FileUtil.h"
#include "Log.h"

#include <FidelityFX/api/include/dx12/ffx_api_dx12.h>
#include <FidelityFX/upscalers/include/ffx_upscale.h>

#include <algorithm>
#include <cstdio>
#include <cstring>
#include <vector>

using Microsoft::WRL::ComPtr;

namespace ksp
{
    namespace
    {
        constexpr const wchar_t* kRuntimeName = L"amd_fidelityfx_upscaler_dx12.dll";

        // amdUpscalerDirectory, or next to the executable.
        std::wstring RuntimeFolder()
        {
            const std::wstring directory = AmdUpscalerDirectory();
            return directory.empty() ? ExecutableDirectory() : directory;
        }

        // The folder, and the DLL in it.
        std::wstring RuntimeSignature()
        {
            const std::wstring folder = RuntimeFolder();
            return folder + L"|" + FileStamp(folder + L"\\" + kRuntimeName);
        }

        void Transition(ID3D12GraphicsCommandList* commandList, ID3D12Resource* resource,
                        D3D12_RESOURCE_STATES from, D3D12_RESOURCE_STATES to)
        {
            const D3D12_RESOURCE_BARRIER barrier = TransitionBarrier(resource, from, to);
            commandList->ResourceBarrier(1, &barrier);
        }

        bool TextureDesc(void* texture, D3D11_TEXTURE2D_DESC& desc)
        {
            ComPtr<ID3D11Texture2D> texture2d;
            if (texture == nullptr
                || FAILED(static_cast<ID3D11Resource*>(texture)->QueryInterface(IID_PPV_ARGS(&texture2d))))
                return false;
            texture2d->GetDesc(&desc);
            return true;
        }

        bool SameContext(const AmdUpscalerPacket& a, const AmdUpscalerPacket& b)
        {
            const uint32_t creationFlags = kAmdUpscalerHdr | kAmdUpscalerDepthInverted | kAmdUpscalerAutoExposure;
            return a.renderWidth == b.renderWidth && a.renderHeight == b.renderHeight
                && a.outputWidth == b.outputWidth && a.outputHeight == b.outputHeight
                && (a.flags & creationFlags) == (b.flags & creationFlags);
        }

        // A shared texture of a Unity texture's size and typed format, made anew
        // when either differs.
        bool Match(SharedTexture& shared, ID3D11Device* device11, ID3D12Device* device12,
                   const D3D11_TEXTURE2D_DESC& desc)
        {
            const DXGI_FORMAT format = TypedFormat(desc.Format);
            if (shared.Valid() && shared.width == desc.Width && shared.height == desc.Height && shared.format == format)
                return true;
            return shared.Create(device11, device12, desc.Width, desc.Height, format);
        }
    }

    // Never destroyed, like Dlss: at process exit the devices it holds may be gone.
    AmdUpscaler& AmdUpscaler::Get()
    {
        static AmdUpscaler* instance = new AmdUpscaler();
        return *instance;
    }

    void AmdUpscaler::SetStatus(int newState, const std::string& text)
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
            LogLine("AMD upscaler: " + text);
    }

    int AmdUpscaler::Status(char* buffer, int size) const
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
        // (EnsureRuntimeLocked). The files are asked outside the lock.
        if (reported == -2 && RuntimeSignature() != stood)
            reported = -1;
        return reported;
    }

    void AmdUpscaler::Attach(const void* newOwner, ID3D11Device5* newDevice11, ID3D11DeviceContext4* newContext11,
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

    void AmdUpscaler::Detach(const void* detaching)
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

    void AmdUpscaler::OnPacket(const void* data)
    {
        std::lock_guard<std::mutex> lock(mutex);

        const AmdUpscalerPacket* packet = static_cast<const AmdUpscalerPacket*>(data);
        if (packet == nullptr || packet->size != sizeof(AmdUpscalerPacket) || packet->magic != kAmdUpscalerPacketMagic)
        {
            SetStatus(-1, "frame packet rejected: the mod and the proxy disagree on its layout");
            return;
        }
        if (device12 == nullptr || queue == nullptr)
        {
            SetStatus(-1, "AMD's upscaler needs ReDefinition's dxgi.dll proxy presenting through D3D12");
            return;
        }
        if (packet->colour == nullptr || packet->output == nullptr || packet->depth == nullptr
            || packet->motionVectors == nullptr)
        {
            SetStatus(-1, "a frame without all four textures");
            return;
        }

        if (!EnsureRuntimeLocked() || !EnsureFenceLocked() || !EnsureTexturesLocked(*packet)
            || !EnsureContextLocked(*packet))
            return;

        UpscaleLocked(*packet);
    }

    void AmdUpscaler::OnRelease()
    {
        std::lock_guard<std::mutex> lock(mutex);
        ReleaseLocked();
        contextFailed = false;
        // A DLL that did not load keeps its failure and the status saying why;
        // EnsureRuntimeLocked tries it again once the folder or the file changed.
        if (runtimeTried && api.CreateContext == nullptr)
            return;
        SetStatus(0, "released, no frame since");
    }

    bool AmdUpscaler::EnsureRuntimeLocked()
    {
        if (api.CreateContext != nullptr)
            return true;
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

        const std::wstring folder = RuntimeFolder();
        const std::wstring path = folder + L"\\" + kRuntimeName;

        module = LoadLibraryExW(path.c_str(), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
        if (module == nullptr)
        {
            SetStatus(-2, "no amd_fidelityfx_upscaler_dx12.dll in " + Narrow(folder)
                              + " -- copy it from a game that has it");
            return false;
        }

        ffxLoadFunctions(&api, module);
        if (api.CreateContext == nullptr || api.DestroyContext == nullptr || api.Configure == nullptr
            || api.Query == nullptr || api.Dispatch == nullptr)
        {
            api = ffxFunctions{};
            FreeLibrary(module);
            module = nullptr;
            SetStatus(-2, Narrow(path) + " is not AMD's FidelityFX API");
            return false;
        }

        LogLine("AMD upscaler: " + Narrow(path) + " " + FileVersion(path));
        return true;
    }

    bool AmdUpscaler::EnsureFenceLocked()
    {
        if (list != nullptr)
            return true;

        HRESULT hr = device12->CreateFence(0, D3D12_FENCE_FLAG_SHARED, IID_PPV_ARGS(&fence12));
        HANDLE handle = nullptr;
        if (SUCCEEDED(hr))
            hr = device12->CreateSharedHandle(fence12.Get(), nullptr, GENERIC_ALL, nullptr, &handle);
        if (SUCCEEDED(hr))
        {
            hr = device11->OpenSharedFence(handle, IID_PPV_ARGS(&fence11));
            CloseHandle(handle);
        }
        for (UINT i = 0; SUCCEEDED(hr) && i < kLists; ++i)
            hr = device12->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&allocators[i]));
        if (SUCCEEDED(hr))
            hr = device12->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, allocators[0].Get(), nullptr,
                                             IID_PPV_ARGS(&list));
        if (FAILED(hr))
        {
            char text[16] = {};
            FormatTo(text, "0x%08lX", static_cast<unsigned long>(hr));
            SetStatus(-1, std::string("the shared fence or command list could not be made (") + text + ")");
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

    bool AmdUpscaler::EnsureTexturesLocked(const AmdUpscalerPacket& packet)
    {
        D3D11_TEXTURE2D_DESC colourDesc = {}, depthDesc = {}, motionDesc = {}, outputDesc = {};
        if (!TextureDesc(packet.colour, colourDesc) || !TextureDesc(packet.depth, depthDesc)
            || !TextureDesc(packet.motionVectors, motionDesc) || !TextureDesc(packet.output, outputDesc))
        {
            SetStatus(-1, "a texture of the frame is not a 2D texture");
            return false;
        }

        const bool same = colour.Valid() && colour.width == colourDesc.Width && colour.height == colourDesc.Height
                          && output.Valid() && output.width == outputDesc.Width && output.height == outputDesc.Height
                          && depth.Valid() && depth.width == depthDesc.Width && motion.Valid()
                          && motion.width == motionDesc.Width && colour.format == TypedFormat(colourDesc.Format)
                          && output.format == TypedFormat(outputDesc.Format);
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

    bool AmdUpscaler::EnsureContextLocked(const AmdUpscalerPacket& packet)
    {
        // Not tried again for the frame it failed for.
        if (SameContext(created, packet) && (context != nullptr || contextFailed))
            return context != nullptr;

        WaitForListsLocked();
        if (context != nullptr)
        {
            api.DestroyContext(&context, nullptr);
            context = nullptr;
        }

        // Every version the DLL offers on this device, once, for the log.
        if (!loggedVersions)
        {
            loggedVersions = true;
            ffxQueryDescGetVersions versions = {};
            versions.header.type = FFX_API_QUERY_DESC_TYPE_GET_VERSIONS;
            versions.createDescType = FFX_API_CREATE_CONTEXT_DESC_TYPE_UPSCALE;
            versions.device = device12.Get();
            uint64_t count = 0;
            versions.outputCount = &count;
            if (api.Query(nullptr, &versions.header) == FFX_API_RETURN_OK && count > 0)
            {
                std::vector<uint64_t> ids(static_cast<size_t>(count));
                std::vector<const char*> names(static_cast<size_t>(count));
                versions.versionIds = ids.data();
                versions.versionNames = names.data();
                if (api.Query(nullptr, &versions.header) == FFX_API_RETURN_OK)
                {
                    std::string line;
                    for (uint64_t i = 0; i < count; ++i)
                        line += (i == 0 ? "" : ", ") + std::string(names[i] != nullptr ? names[i] : "?");
                    LogLine("AMD upscaler: versions on this GPU: " + line);
                }
            }
        }

        const bool hdr = (packet.flags & kAmdUpscalerHdr) != 0;
        ffxCreateContextDescUpscale create = {};
        create.header.type = FFX_API_CREATE_CONTEXT_DESC_TYPE_UPSCALE;
        // An LDR image is KSP's 8-bit sRGB target, not linear light:
        // "FFX_UPSCALE_ENABLE_NON_LINEAR_COLORSPACE ... in case where this is not
        // possible" (super-resolution-ml.md).
        create.flags = (hdr ? FFX_UPSCALE_ENABLE_HIGH_DYNAMIC_RANGE : FFX_UPSCALE_ENABLE_NON_LINEAR_COLORSPACE)
                     | ((packet.flags & kAmdUpscalerDepthInverted) != 0 ? FFX_UPSCALE_ENABLE_DEPTH_INVERTED : 0u)
                     | ((packet.flags & kAmdUpscalerAutoExposure) != 0 ? FFX_UPSCALE_ENABLE_AUTO_EXPOSURE : 0u);
        create.maxRenderSize = { packet.renderWidth, packet.renderHeight };
        create.maxUpscaleSize = { packet.outputWidth, packet.outputHeight };
        create.fpMessage = nullptr;

        ffxCreateBackendDX12Desc backend = {};
        backend.header.type = FFX_API_CREATE_CONTEXT_DESC_TYPE_BACKEND_DX12;
        backend.device = device12.Get();

        // Required since SDK 2.1 (super-resolution-ml.md, "New APIs").
        ffxCreateContextDescUpscaleVersion version = {};
        version.header.type = FFX_API_CREATE_CONTEXT_DESC_TYPE_UPSCALE_VERSION;
        version.version = FFX_UPSCALER_VERSION;

        create.header.pNext = &backend.header;
        backend.header.pNext = &version.header;
        version.header.pNext = nullptr;

        const ffxReturnCode_t code = api.CreateContext(&context, &create.header, nullptr);
        if (code != FFX_API_RETURN_OK)
        {
            context = nullptr;
            SetStatus(-1, "AMD's upscaler context could not be created, code " + std::to_string(static_cast<int>(code)));
            created = packet;
            contextFailed = true;
            return false;
        }

        providerName = "AMD upscaler";
        ffxQueryGetProviderVersion provider = {};
        provider.header.type = FFX_API_QUERY_DESC_TYPE_GET_PROVIDER_VERSION;
        if (api.Query(&context, &provider.header) == FFX_API_RETURN_OK && provider.versionName != nullptr)
            providerName = provider.versionName;

        created = packet;
        contextFailed = false;
        upscaled = 0;
        // The status names the new context with its first upscaled frame.
        renderState = 0;
        LogLine("AMD upscaler: " + providerName + ", " + std::to_string(packet.renderWidth) + "x"
                + std::to_string(packet.renderHeight) + " to " + std::to_string(packet.outputWidth) + "x"
                + std::to_string(packet.outputHeight) + (hdr ? ", HDR" : ", LDR"));
        return true;
    }

    bool AmdUpscaler::UpscaleLocked(const AmdUpscalerPacket& packet)
    {
        // D3D11: this frame's inputs into the shared textures, and a point in its
        // command stream after them for the queue to wait for.
        context11->CopyResource(colour.d3d11.Get(), static_cast<ID3D11Resource*>(packet.colour));
        context11->CopyResource(depth.d3d11.Get(), static_cast<ID3D11Resource*>(packet.depth));
        context11->CopyResource(motion.d3d11.Get(), static_cast<ID3D11Resource*>(packet.motionVectors));
        const UINT64 inputsCopied = ++fenceValue;
        context11->Signal(fence11.Get(), inputsCopied);
        context11->Flush();
        queue->Wait(fence12.Get(), inputsCopied);

        // One of three allocators, whose list has left the GPU.
        const UINT index = listIndex;
        listIndex = (listIndex + 1) % kLists;
        if (listDone[index] != 0 && fence12->GetCompletedValue() < listDone[index]
            && SUCCEEDED(fence12->SetEventOnCompletion(listDone[index], fenceEvent)))
            WaitForSingleObject(fenceEvent, INFINITE);
        allocators[index]->Reset();
        list->Reset(allocators[index].Get(), nullptr);

        // Shared resources are back in COMMON whenever D3D11 touches them. The
        // inputs as AMD asks, "transitioned to D3D12_RESOURCE_STATE_NON_PIXEL_
        // SHADER_RESOURCE before calling ffxDispatchDescUpscale"; the output as a
        // UAV, which COMMON cannot be promoted to implicitly.
        const D3D12_RESOURCE_STATES read = D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE;
        Transition(list.Get(), colour.d3d12.Get(), D3D12_RESOURCE_STATE_COMMON, read);
        Transition(list.Get(), depth.d3d12.Get(), D3D12_RESOURCE_STATE_COMMON, read);
        Transition(list.Get(), motion.d3d12.Get(), D3D12_RESOURCE_STATE_COMMON, read);
        Transition(list.Get(), output.d3d12.Get(), D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_UNORDERED_ACCESS);

        ffxDispatchDescUpscale dispatch = {};
        dispatch.header.type = FFX_API_DISPATCH_DESC_TYPE_UPSCALE;
        dispatch.commandList = list.Get();
        dispatch.color = ffxApiGetResourceDX12(colour.d3d12.Get(), FFX_API_RESOURCE_STATE_COMPUTE_READ);
        dispatch.depth = ffxApiGetResourceDX12(depth.d3d12.Get(), FFX_API_RESOURCE_STATE_COMPUTE_READ);
        dispatch.motionVectors = ffxApiGetResourceDX12(motion.d3d12.Get(), FFX_API_RESOURCE_STATE_COMPUTE_READ);
        dispatch.output = ffxApiGetResourceDX12(output.d3d12.Get(), FFX_API_RESOURCE_STATE_UNORDERED_ACCESS);
        dispatch.jitterOffset = { packet.jitterX, packet.jitterY };
        dispatch.motionVectorScale = { packet.motionVectorScaleX, packet.motionVectorScaleY };
        dispatch.renderSize = { packet.renderWidth, packet.renderHeight };
        dispatch.upscaleSize = { packet.outputWidth, packet.outputHeight };
        dispatch.enableSharpening = packet.sharpness > 0.0f;
        dispatch.sharpness = std::min(packet.sharpness, 1.0f);
        dispatch.frameTimeDelta = packet.frameTimeDeltaMs;
        dispatch.preExposure = 1.0f;
        dispatch.reset = (packet.flags & kAmdUpscalerReset) != 0;
        dispatch.cameraNear = packet.cameraNear;
        dispatch.cameraFar = packet.cameraFar;
        dispatch.cameraFovAngleVertical = packet.verticalFovRadians;
        dispatch.viewSpaceToMetersFactor = 1.0f;   // in KSP one unit is one metre
        dispatch.flags = 0;

        const ffxReturnCode_t code = api.Dispatch(&context, &dispatch.header);

        Transition(list.Get(), output.d3d12.Get(), D3D12_RESOURCE_STATE_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_COMMON);
        Transition(list.Get(), motion.d3d12.Get(), read, D3D12_RESOURCE_STATE_COMMON);
        Transition(list.Get(), depth.d3d12.Get(), read, D3D12_RESOURCE_STATE_COMMON);
        Transition(list.Get(), colour.d3d12.Get(), read, D3D12_RESOURCE_STATE_COMMON);
        list->Close();
        ID3D12CommandList* lists[] = { list.Get() };
        queue->ExecuteCommandLists(1, lists);
        const UINT64 finished = ++fenceValue;
        queue->Signal(fence12.Get(), finished);
        listDone[index] = finished;

        if (code != FFX_API_RETURN_OK)
        {
            SetStatus(-1, providerName + " dispatch failed, code " + std::to_string(static_cast<int>(code)));
            return false;
        }

        // D3D11: the result into Unity's output once the queue is past it.
        context11->Wait(fence11.Get(), finished);
        context11->CopyResource(static_cast<ID3D11Resource*>(packet.output), output.d3d11.Get());

        ++upscaled;
        if (renderState != 1 || (upscaled & 1023) == 0)
            SetStatus(1, providerName + " running, " + std::to_string(packet.renderWidth) + "x"
                             + std::to_string(packet.renderHeight) + " to " + std::to_string(packet.outputWidth) + "x"
                             + std::to_string(packet.outputHeight) + ", " + std::to_string(upscaled) + " frames");
        return true;
    }

    void AmdUpscaler::WaitForListsLocked()
    {
        if (fence12 == nullptr || fenceEvent == nullptr || fenceValue == 0)
            return;
        if (fence12->GetCompletedValue() >= fenceValue)
            return;
        if (SUCCEEDED(fence12->SetEventOnCompletion(fenceValue, fenceEvent)))
            WaitForSingleObject(fenceEvent, INFINITE);
    }

    void AmdUpscaler::ReleaseLocked()
    {
        WaitForListsLocked();
        if (context != nullptr)
        {
            api.DestroyContext(&context, nullptr);
            context = nullptr;
        }
        created = AmdUpscalerPacket{};
        colour.Reset();
        depth.Reset();
        motion.Reset();
        output.Reset();
    }
}
