#include "XessUpscaler.h"

#include "Config.h"
#include "D3d12Util.h"
#include "FileUtil.h"
#include "Log.h"

#include <cmath>
#include <cstdio>

using Microsoft::WRL::ComPtr;

namespace redefinition
{
    namespace
    {
        // The DLL's file name, looked for in RuntimeFolder().
        constexpr const wchar_t* kRuntimeName = L"libxess.dll";

        // xessDirectory, or next to the executable.
        std::wstring RuntimeFolder()
        {
            const std::wstring directory = XessDirectory();
            return directory.empty() ? ExecutableDirectory() : directory;
        }

        // The folder and the stamp of libxess.dll in it: a stop for -2 stands while this stays the same.
        std::wstring RuntimeSignature()
        {
            const std::wstring folder = RuntimeFolder();
            return folder + L"|" + FileStamp(folder + L"\\" + kRuntimeName);
        }

        // One barrier on the list for the resource from one state to the other.
        void Transition(ID3D12GraphicsCommandList* commandList, ID3D12Resource* resource,
                        D3D12_RESOURCE_STATES from, D3D12_RESOURCE_STATES to)
        {
            const D3D12_RESOURCE_BARRIER barrier = TransitionBarrier(resource, from, to);
            commandList->ResourceBarrier(1, &barrier);
        }

        // The description of a D3D11 resource that is a 2D texture; false for anything else.
        bool TextureDesc(void* texture, D3D11_TEXTURE2D_DESC& desc)
        {
            ComPtr<ID3D11Texture2D> texture2d;
            if (texture == nullptr
                || FAILED(static_cast<ID3D11Resource*>(texture)->QueryInterface(IID_PPV_ARGS(&texture2d))))
                return false;
            texture2d->GetDesc(&desc);
            return true;
        }

        // Whether a context made for a can take b: the same sizes and the same creation flags.
        bool SameContext(const XessUpscalerPacket& a, const XessUpscalerPacket& b)
        {
            const uint32_t creationFlags = kAmdUpscalerHdr | kAmdUpscalerDepthInverted | kAmdUpscalerAutoExposure;
            return a.renderWidth == b.renderWidth && a.renderHeight == b.renderHeight
                && a.outputWidth == b.outputWidth && a.outputHeight == b.outputHeight
                && (a.flags & creationFlags) == (b.flags & creationFlags);
        }

        // The shared texture made again unless it already has the size and the typed format of desc.
        bool Match(SharedTexture& shared, ID3D11Device* device11, ID3D12Device* device12,
                   const D3D11_TEXTURE2D_DESC& desc)
        {
            const DXGI_FORMAT format = TypedFormat(desc.Format);
            if (shared.Valid() && shared.width == desc.Width && shared.height == desc.Height && shared.format == format)
                return true;
            return shared.Create(device11, device12, desc.Width, desc.Height, format);
        }

        // The preset whose scale the frame's comes nearest: "ULTRA_PERFORMANCE: 3.0,
        // PERFORMANCE: 2.0, BALANCED: 1.7, QUALITY: 1.5, ULTRA_QUALITY: 1.3, AA: 1.0",
        // the "legacy (pre 1.3.0) scale factors" (xess.h, xessForceLegacyScaleFactors),
        // which are FSR's and which the context is set to. The render size is the
        // mod's, given with every frame, and "XeSS expects all the input buffers [...]
        // to be in the returned resolution range" (xessGetOptimalInputResolution).
        xess_quality_settings_t Preset(const XessUpscalerPacket& packet)
        {
            const double scale = packet.renderWidth > 0
                ? static_cast<double>(packet.outputWidth) / static_cast<double>(packet.renderWidth) : 1.0;
            struct Entry
            {
                double scale;
                xess_quality_settings_t preset;
            };
            const Entry entries[] = {
                { 1.0, XESS_QUALITY_SETTING_AA },
                { 1.3, XESS_QUALITY_SETTING_ULTRA_QUALITY },
                { 1.5, XESS_QUALITY_SETTING_QUALITY },
                { 1.7, XESS_QUALITY_SETTING_BALANCED },
                { 2.0, XESS_QUALITY_SETTING_PERFORMANCE },
                { 3.0, XESS_QUALITY_SETTING_ULTRA_PERFORMANCE },
            };
            xess_quality_settings_t best = XESS_QUALITY_SETTING_QUALITY;
            double nearest = 1e9;
            for (const Entry& entry : entries)
            {
                const double distance = std::fabs(entry.scale - scale);
                if (distance < nearest)
                {
                    nearest = distance;
                    best = entry.preset;
                }
            }
            return best;
        }

        // A result code as the status and the log write it.
        std::string ResultText(xess_result_t result)
        {
            return "XeSS result " + std::to_string(static_cast<int>(result));
        }
    }

    XessUpscaler& XessUpscaler::Get()
    {
        static XessUpscaler* instance = new XessUpscaler();
        return *instance;
    }

    void XessUpscaler::SetStatus(int newState, const std::string& text)
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
            LogLine("XeSS: " + text);
    }

    int XessUpscaler::Status(char* buffer, int size) const
    {
        int reported = 0;
        std::wstring stood;
        {
            std::lock_guard<std::mutex> lock(statusLock);
            if (buffer != nullptr && size > 0)
                strncpy_s(buffer, static_cast<size_t>(size), statusText.c_str(), _TRUNCATE);
            reported = state;
            if (reported == -2)
                stood = stableSignature;
        }
        if (reported == -2 && RuntimeSignature() != stood)
            reported = -1;
        return reported;
    }

    int XessUpscaler::Version(char* buffer, int size) const
    {
        std::lock_guard<std::mutex> lock(statusLock);
        if (buffer != nullptr && size > 0)
            strncpy_s(buffer, static_cast<size_t>(size), shownVersion.c_str(), _TRUNCATE);
        return shownVersion.empty() ? 0 : 1;
    }

    void XessUpscaler::Attach(const void* newOwner, ID3D11Device5* newDevice11, ID3D11DeviceContext4* newContext11,
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

    void XessUpscaler::Detach(const void* detaching)
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

    void XessUpscaler::OnPacket(const void* data)
    {
        std::lock_guard<std::mutex> lock(mutex);

        const XessUpscalerPacket* packet = static_cast<const XessUpscalerPacket*>(data);
        if (packet == nullptr || packet->size != sizeof(XessUpscalerPacket) || packet->magic != kXessUpscalerPacketMagic)
        {
            SetStatus(-1, "frame packet rejected: the mod and the proxy disagree on its layout");
            return;
        }
        if (device12 == nullptr || queue == nullptr)
        {
            SetStatus(-1, "XeSS needs ReDefinition's dxgi.dll proxy presenting through D3D12");
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

    void XessUpscaler::OnRelease()
    {
        std::lock_guard<std::mutex> lock(mutex);
        ReleaseLocked();
        contextFailed = false;
        if (runtimeTried && createContext == nullptr)
            return;
        SetStatus(0, "released, no frame since");
    }

    bool XessUpscaler::EnsureRuntimeLocked()
    {
        if (createContext != nullptr)
            return true;
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
            const DWORD error = GetLastError();
            if (GetFileAttributesW(path.c_str()) == INVALID_FILE_ATTRIBUTES)
                SetStatus(-2, "no libxess.dll in " + Narrow(folder) + " -- Intel's XeSS SDK has it");
            else
                SetStatus(-2, Narrow(path) + " could not be loaded (" + Hr(HRESULT_FROM_WIN32(error)) + ")");
            return false;
        }

        createContext = reinterpret_cast<decltype(createContext)>(GetProcAddress(module, "xessD3D12CreateContext"));
        init = reinterpret_cast<decltype(init)>(GetProcAddress(module, "xessD3D12Init"));
        execute = reinterpret_cast<decltype(execute)>(GetProcAddress(module, "xessD3D12Execute"));
        destroyContext = reinterpret_cast<decltype(destroyContext)>(GetProcAddress(module, "xessDestroyContext"));
        setVelocityScale = reinterpret_cast<decltype(setVelocityScale)>(GetProcAddress(module, "xessSetVelocityScale"));
        getVersion = reinterpret_cast<decltype(getVersion)>(GetProcAddress(module, "xessGetVersion"));
        getOptimalInputResolution = reinterpret_cast<decltype(getOptimalInputResolution)>(
            GetProcAddress(module, "xessGetOptimalInputResolution"));
        forceLegacyScaleFactors = reinterpret_cast<decltype(forceLegacyScaleFactors)>(
            GetProcAddress(module, "xessForceLegacyScaleFactors"));
        if (createContext == nullptr || init == nullptr || execute == nullptr || destroyContext == nullptr
            || setVelocityScale == nullptr || getVersion == nullptr)
        {
            createContext = nullptr;
            FreeLibrary(module);
            module = nullptr;
            SetStatus(-2, Narrow(path) + " is not Intel's XeSS for Direct3D 12 (libxess_dx11.dll is Direct3D 11's)");
            return false;
        }

        xess_version_t version = {};
        if (getVersion(&version) == XESS_RESULT_SUCCESS)
            versionName = "XeSS " + std::to_string(version.major) + "." + std::to_string(version.minor) + "."
                          + std::to_string(version.patch);
        else
            versionName = "XeSS";
        {
            std::lock_guard<std::mutex> lock(statusLock);
            shownVersion = versionName;
        }
        LogLine("XeSS: " + Narrow(path) + " " + FileVersion(path) + ", " + versionName);
        return true;
    }

    bool XessUpscaler::EnsureFenceLocked()
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

    bool XessUpscaler::EnsureTexturesLocked(const XessUpscalerPacket& packet)
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
        if (matches(colour, colourDesc) && matches(output, outputDesc) && matches(depth, depthDesc)
            && matches(motion, motionDesc))
            return true;

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

    bool XessUpscaler::EnsureContextLocked(const XessUpscalerPacket& packet)
    {
        if (SameContext(created, packet) && (context != nullptr || contextFailed))
            return context != nullptr && !contextFailed;

        WaitForListsLocked();
        if (context == nullptr)
        {
            const xess_result_t made = createContext(device12.Get(), &context);
            if (made != XESS_RESULT_SUCCESS || context == nullptr)
            {
                context = nullptr;
                SetStatus(-1, "XeSS's context could not be created on this GPU (" + ResultText(made)
                                  + ") -- it needs Shader Model 6.4 with DP4a");
                created = packet;
                contextFailed = true;
                return false;
            }
        }

        // The scale factors Preset picks from: "In order to apply new scale factors
        // application should call xessGetOptimalInputResolution and initialization
        // function (xess*Init)" (xess.h, xessForceLegacyScaleFactors).
        if (forceLegacyScaleFactors != nullptr)
            forceLegacyScaleFactors(context, true);

        // The nearest preset, or the next whose input range as XeSS states it holds
        // the mod's render size: the render size is the mod's, which its own modes set.
        xess_quality_settings_t preset = Preset(packet);
        std::string range;
        if (getOptimalInputResolution != nullptr)
        {
            const xess_2d_t outputSize = { packet.outputWidth, packet.outputHeight };
            const xess_quality_settings_t order[] = {
                preset, XESS_QUALITY_SETTING_AA, XESS_QUALITY_SETTING_ULTRA_QUALITY_PLUS,
                XESS_QUALITY_SETTING_ULTRA_QUALITY, XESS_QUALITY_SETTING_QUALITY, XESS_QUALITY_SETTING_BALANCED,
                XESS_QUALITY_SETTING_PERFORMANCE, XESS_QUALITY_SETTING_ULTRA_PERFORMANCE,
            };
            bool found = false;
            for (const xess_quality_settings_t candidate : order)
            {
                xess_2d_t optimal = {}, minimum = {}, maximum = {};
                if (getOptimalInputResolution(context, &outputSize, candidate, &optimal, &minimum, &maximum)
                    != XESS_RESULT_SUCCESS)
                    continue;
                if (packet.renderWidth < minimum.x || packet.renderWidth > maximum.x
                    || packet.renderHeight < minimum.y || packet.renderHeight > maximum.y)
                    continue;
                preset = candidate;
                range = ", optimal " + std::to_string(optimal.x) + "x" + std::to_string(optimal.y) + ", range "
                        + std::to_string(minimum.x) + "x" + std::to_string(minimum.y) + " to "
                        + std::to_string(maximum.x) + "x" + std::to_string(maximum.y);
                found = true;
                break;
            }
            if (!found)
                range = ", outside every preset's input range";
        }

        const bool hdr = (packet.flags & kAmdUpscalerHdr) != 0;
        xess_d3d12_init_params_t params = {};
        params.outputResolution = { packet.outputWidth, packet.outputHeight };
        params.qualitySetting = preset;
        params.initFlags = ((packet.flags & kAmdUpscalerDepthInverted) != 0 ? XESS_INIT_FLAG_INVERTED_DEPTH : 0u)
                         | ((packet.flags & kAmdUpscalerAutoExposure) != 0 ? XESS_INIT_FLAG_ENABLE_AUTOEXPOSURE : 0u)
                         // KSP's 8-bit sRGB target in LDR: "XESS_INIT_FLAG_LDR_INPUT_COLOR" (xess.h).
                         | (hdr ? 0u : XESS_INIT_FLAG_LDR_INPUT_COLOR);
        const xess_result_t initialised = init(context, &params);
        if (initialised != XESS_RESULT_SUCCESS)
        {
            SetStatus(-1, "XeSS could not be initialised for " + std::to_string(packet.outputWidth) + "x"
                              + std::to_string(packet.outputHeight) + " (" + ResultText(initialised) + ")");
            created = packet;
            contextFailed = true;
            return false;
        }
        velocityScaleX = 0.0f;
        velocityScaleY = 0.0f;
        created = packet;
        contextFailed = false;
        upscaled = 0;
        renderState = 0;
        LogLine("XeSS: " + versionName + ", " + std::to_string(packet.renderWidth) + "x"
                + std::to_string(packet.renderHeight) + " to " + std::to_string(packet.outputWidth) + "x"
                + std::to_string(packet.outputHeight) + (hdr ? ", HDR" : ", LDR") + ", preset "
                + std::to_string(static_cast<int>(params.qualitySetting)) + range);
        return true;
    }

    bool XessUpscaler::UpscaleLocked(const XessUpscalerPacket& packet)
    {
        // D3D11: this frame's inputs into the shared textures, and a point after them
        // for the queue to wait for.
        context11->CopyResource(colour.d3d11.Get(), static_cast<ID3D11Resource*>(packet.colour));
        context11->CopyResource(depth.d3d11.Get(), static_cast<ID3D11Resource*>(packet.depth));
        context11->CopyResource(motion.d3d11.Get(), static_cast<ID3D11Resource*>(packet.motionVectors));
        const UINT64 inputsCopied = ++fenceValue;
        context11->Signal(fence11.Get(), inputsCopied);
        context11->Flush();
        queue->Wait(fence12.Get(), inputsCopied);

        const UINT index = listIndex;
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

        // "Must be in NON_PIXEL_SHADER_RESOURCE state" for the inputs, "UNORDERED_ACCESS"
        // for the output (xess_d3d12.h).
        const D3D12_RESOURCE_STATES read = D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE;
        Transition(list.Get(), colour.d3d12.Get(), D3D12_RESOURCE_STATE_COMMON, read);
        Transition(list.Get(), depth.d3d12.Get(), D3D12_RESOURCE_STATE_COMMON, read);
        Transition(list.Get(), motion.d3d12.Get(), D3D12_RESOURCE_STATE_COMMON, read);
        Transition(list.Get(), output.d3d12.Get(), D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_UNORDERED_ACCESS);

        // The motion vectors in pixels, as FSR's scale makes them.
        if (packet.motionVectorScaleX != velocityScaleX || packet.motionVectorScaleY != velocityScaleY)
        {
            setVelocityScale(context, packet.motionVectorScaleX, packet.motionVectorScaleY);
            velocityScaleX = packet.motionVectorScaleX;
            velocityScaleY = packet.motionVectorScaleY;
        }

        xess_d3d12_execute_params_t params = {};
        params.pColorTexture = colour.d3d12.Get();
        params.pVelocityTexture = motion.d3d12.Get();
        params.pDepthTexture = depth.d3d12.Get();
        params.pOutputTexture = output.d3d12.Get();
        params.jitterOffsetX = packet.jitterX;
        params.jitterOffsetY = packet.jitterY;
        params.exposureScale = 1.0f;
        params.resetHistory = (packet.flags & kAmdUpscalerReset) != 0 ? 1u : 0u;
        params.inputWidth = packet.renderWidth;
        params.inputHeight = packet.renderHeight;
        const xess_result_t result = execute(context, list.Get(), &params);

        Transition(list.Get(), output.d3d12.Get(), D3D12_RESOURCE_STATE_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_COMMON);
        Transition(list.Get(), motion.d3d12.Get(), read, D3D12_RESOURCE_STATE_COMMON);
        Transition(list.Get(), depth.d3d12.Get(), read, D3D12_RESOURCE_STATE_COMMON);
        Transition(list.Get(), colour.d3d12.Get(), read, D3D12_RESOURCE_STATE_COMMON);
        hr = list->Close();
        if (SUCCEEDED(hr))
        {
            ID3D12CommandList* lists[] = { list.Get() };
            queue->ExecuteCommandLists(1, lists);
        }
        const UINT64 finished = ++fenceValue;
        queue->Signal(fence12.Get(), finished);
        listDone[index] = finished;
        context11->Wait(fence11.Get(), finished);
        if (FAILED(hr))
        {
            SetStatus(-1, "the command list could not be closed (" + Hr(hr) + ")");
            return false;
        }
        if (result != XESS_RESULT_SUCCESS)
        {
            SetStatus(-1, versionName + " failed, " + ResultText(result));
            return false;
        }

        context11->CopyResource(static_cast<ID3D11Resource*>(packet.output), output.d3d11.Get());

        ++upscaled;
        if (renderState != 1 || (upscaled & 1023) == 0)
            SetStatus(1, versionName + " running, " + std::to_string(packet.renderWidth) + "x"
                             + std::to_string(packet.renderHeight) + " to " + std::to_string(packet.outputWidth) + "x"
                             + std::to_string(packet.outputHeight) + ", " + std::to_string(upscaled) + " frames");
        return true;
    }

    void XessUpscaler::WaitForListsLocked()
    {
        if (fence12 == nullptr || fenceEvent == nullptr || fenceValue == 0)
            return;
        if (fence12->GetCompletedValue() >= fenceValue)
            return;
        if (SUCCEEDED(fence12->SetEventOnCompletion(fenceValue, fenceEvent)))
            WaitForSingleObject(fenceEvent, INFINITE);
    }

    void XessUpscaler::ReleaseLocked()
    {
        WaitForListsLocked();
        if (context != nullptr)
        {
            destroyContext(context);
            context = nullptr;
        }
        created = XessUpscalerPacket{};
        colour.Reset();
        depth.Reset();
        motion.Reset();
        output.Reset();
    }
}
