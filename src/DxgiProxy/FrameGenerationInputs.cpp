// Frame generation's inputs (FrameGeneration): the textures shared between Unity's
// D3D11 and the proxy's D3D12 device, copied at Present and flipped to the
// screen's orientation where Unity renders them upside down, and the backbuffer
// the HUD-less copy is compared with.

#include "FrameGeneration.h"

#include "Config.h"
#include "FidelityFx.h"
#include "Log.h"

#include <FidelityFX/api/include/dx12/ffx_api_dx12.h>
#include <FidelityFX/framegeneration/include/dx12/ffx_api_framegeneration_dx12.h>

#include <d3dcompiler.h>

#include <cstring>
#include <string>

using Microsoft::WRL::ComPtr;

namespace redefinition
{
    namespace
    {
        // Which flip shader variant a format needs, or -1 for none.
        int FlipVariant(DXGI_FORMAT typed)
        {
            switch (typed)
            {
            case DXGI_FORMAT_R32_FLOAT:
                return 0;
            case DXGI_FORMAT_R16G16_FLOAT:
                return 1;
            case DXGI_FORMAT_R8G8B8A8_UNORM:
            case DXGI_FORMAT_R8G8B8A8_UNORM_SRGB:
            case DXGI_FORMAT_B8G8R8A8_UNORM:
            case DXGI_FORMAT_B8G8R8A8_UNORM_SRGB:
            case DXGI_FORMAT_R16G16B16A16_FLOAT:
                return 2;
            default:
                return -1;
            }
        }

        // Row y of the destination is row height-1-y of the source. That is
        // the whole flip.
        const char kFlipShader[] =
            "Texture2D<CH> src : register(t0);\n"
            "RWTexture2D<CH> dst : register(u0);\n"
            "[numthreads(8, 8, 1)]\n"
            "void main(uint3 id : SV_DispatchThreadID)\n"
            "{\n"
            "    uint width, height;\n"
            "    dst.GetDimensions(width, height);\n"
            "    if (id.x >= width || id.y >= height) return;\n"
            "    dst[id.xy] = src.Load(int3(id.x, height - 1 - id.y, 0));\n"
            "}\n";

        const char* const kFlipChannelTypes[3] = { "float", "float2", "float4" };
    }

    // ------------------------------------------------------------ SharedTexture

    bool SharedTexture::Create(ID3D11Device* device11, ID3D12Device* device12,
                               UINT newWidth, UINT newHeight, DXGI_FORMAT newFormat)
    {
        Reset();

        D3D11_TEXTURE2D_DESC desc = {};
        desc.Width = newWidth;
        desc.Height = newHeight;
        desc.MipLevels = 1;
        desc.ArraySize = 1;
        desc.Format = newFormat;
        desc.SampleDesc.Count = 1;
        desc.Usage = D3D11_USAGE_DEFAULT;
        // UNORDERED_ACCESS so the flip shader can write it directly.
        desc.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_UNORDERED_ACCESS;
        desc.MiscFlags = D3D11_RESOURCE_MISC_SHARED | D3D11_RESOURCE_MISC_SHARED_NTHANDLE;

        HRESULT hr = device11->CreateTexture2D(&desc, nullptr, &d3d11);
        if (FAILED(hr))
        {
            LogLine("Shared input texture failed (" + Hr(hr) + "), format "
                    + std::to_string(static_cast<int>(newFormat)));
            return false;
        }

        D3D11_UNORDERED_ACCESS_VIEW_DESC uavDesc = {};
        uavDesc.Format = TypedFormat(newFormat);
        uavDesc.ViewDimension = D3D11_UAV_DIMENSION_TEXTURE2D;
        hr = device11->CreateUnorderedAccessView(d3d11.Get(), &uavDesc, &uav);
        if (FAILED(hr))
        {
            // Not fatal: the texture is still copied into, just never flipped.
            LogLine("No unordered access view on a shared texture (" + Hr(hr)
                    + "), format " + std::to_string(static_cast<int>(newFormat))
                    + " -- it cannot be flipped");
            uav.Reset();
        }

        ComPtr<IDXGIResource1> resource;
        if (FAILED(d3d11.As(&resource)))
            return false;

        HANDLE handle = nullptr;
        hr = resource->CreateSharedHandle(nullptr,
                                          DXGI_SHARED_RESOURCE_READ | DXGI_SHARED_RESOURCE_WRITE,
                                          nullptr, &handle);
        if (FAILED(hr))
        {
            LogLine("CreateSharedHandle for an input failed (" + Hr(hr) + ")");
            return false;
        }

        hr = device12->OpenSharedHandle(handle, IID_PPV_ARGS(&d3d12));
        CloseHandle(handle);
        if (FAILED(hr))
        {
            LogLine("OpenSharedHandle for an input failed (" + Hr(hr) + ")"
                    + (hr == DXGI_ERROR_DEVICE_REMOVED
                           ? ", device removed, reason " + Hr(device12->GetDeviceRemovedReason())
                           : std::string()));
            return false;
        }

        width = newWidth;
        height = newHeight;
        format = newFormat;
        return true;
    }

    void SharedTexture::Reset()
    {
        d3d12.Reset();
        uav.Reset();
        d3d11.Reset();
        width = height = 0;
        format = DXGI_FORMAT_UNKNOWN;
    }

    // The helper's order is resource, state, additional usages
    // (ffx_api_dx12.h: "ffxApiGetResourceDX12(ID3D12Resource* pRes, uint32_t
    // state, uint32_t additionalUsages)"), and it ORs the usages into those it
    // reads from the resource's description.
    FfxApiResource SharedTexture::AsFfx(uint32_t usage, uint32_t state) const
    {
        return ffxApiGetResourceDX12(d3d12.Get(), state, usage);
    }

    // ------------------------------------------------------------------- inputs

    bool FrameGeneration::CreateInputLocked(Input& input, ID3D11Resource* unity, DXGI_FORMAT sharedFormat)
    {
        RetireLocked(input.buffers[0]);
        RetireLocked(input.buffers[1]);
        input.unityView.Reset();
        input.unity = unity;

        if (unity == nullptr)
        {
            LogLine(std::string("No ") + input.name + " texture registered");
            return false;
        }

        ComPtr<ID3D11Texture2D> texture;
        if (FAILED(unity->QueryInterface(IID_PPV_ARGS(&texture))))
        {
            LogLine(std::string("Registered ") + input.name + " is not a Texture2D");
            return false;
        }

        D3D11_TEXTURE2D_DESC desc = {};
        texture->GetDesc(&desc);

        if (sharedFormat == DXGI_FORMAT_UNKNOWN)
            sharedFormat = desc.Format;

        for (SharedTexture& buffer : input.buffers)
            if (!buffer.Create(device11.Get(), device12.Get(), desc.Width, desc.Height, sharedFormat))
                return false;

        // The flip reads Unity's texture through a typed view. Without one the
        // input is copied as it is, and the log says which.
        D3D11_SHADER_RESOURCE_VIEW_DESC viewDesc = {};
        viewDesc.Format = TypedFormat(desc.Format);
        viewDesc.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D;
        viewDesc.Texture2D.MipLevels = 1;
        const HRESULT hr = device11->CreateShaderResourceView(unity, &viewDesc, &input.unityView);
        if (FAILED(hr))
            LogLine(std::string("No shader view on the ") + input.name + " texture (" + Hr(hr)
                    + ") -- it cannot be flipped");

        LogLine(std::string("Shared ") + input.name + ": " + std::to_string(desc.Width) + "x"
                + std::to_string(desc.Height) + " format " + std::to_string(static_cast<int>(desc.Format))
                + " -> " + std::to_string(static_cast<int>(sharedFormat))
                + (input.unityView && S(input).uav && FlipVariant(TypedFormat(sharedFormat)) >= 0
                       ? ", flippable" : ", copy only"));
        return true;
    }

    // Not released here: frames still on the GPU may read them. A texture
    // released while one does is a GPU fault: the proxy's D3D12 device is removed,
    // and nothing reaches the screen any more. In D3D12 "all resource
    // and description memory lifetimes are the sole responsibly of the app to
    // maintain for the proper duration" (Microsoft, Important Changes from
    // Direct3D 11 to Direct3D 12).
    void FrameGeneration::RetireLocked(SharedTexture& texture)
    {
        if (texture.d3d11 != nullptr || texture.d3d12 != nullptr)
        {
            Retired entry;
            entry.texture = std::move(texture);
            retired.push_back(std::move(entry));
        }
        texture.Reset();
    }

    void FrameGeneration::ReleaseRetiredLocked(uint64_t lastSignalled, uint64_t completedAtStart,
                                               uint64_t completedNow, ffxContext* swapChainContext)
    {
        if (retired.empty())
            return;

        bool fresh = false;
        for (Retired& entry : retired)
        {
            if (entry.releaseAfter != 0)
                continue;

            // The last Present that can have read them is the one before they
            // were replaced, whose frame fence value is the last one signalled.
            // One more for margin.
            entry.releaseAfter = lastSignalled + 1;
            fresh = true;
        }

        // How many frames were still on the GPU when this Present began, before
        // it waited for anything: the nearest this thread gets to the moment of
        // replacement, and what an immediate release would have pulled the
        // textures from under. A lower bound -- the GPU went on meanwhile.
        if (fresh && retiredLogged < 10)
        {
            const uint64_t inFlight = completedAtStart < lastSignalled ? lastSignalled - completedAtStart : 0;
            LogLine("Inputs replaced with " + std::to_string(inFlight) + " frame(s) still on the GPU when"
                    " the next Present began; the old ones are released once it has finished them");
            if (++retiredLogged == 10)
                LogLine("(further input replacements are not logged)");
        }

        // With async workloads the interpolation reads its inputs on the
        // swapchain's own compute queue, which the frame fence does not cover.
        // So the swapchain finishes what it has first -- the wait its disabling
        // configure relies on, which "flushes interpolation and UI composition
        // GPU work" (AMD, frame-interpolation-api.md). Only when something is
        // due, which is after a mode change.
        bool due = false;
        for (const Retired& entry : retired)
            due = due || completedNow >= entry.releaseAfter;
        if (due && contextAsync && swapChainContext != nullptr && *swapChainContext != nullptr)
        {
            ffxDispatchDescFrameGenerationSwapChainWaitForPresentsDX12 wait = {};
            wait.header.type = FFX_API_DISPATCH_DESC_TYPE_FRAMEGENERATIONSWAPCHAIN_WAIT_FOR_PRESENTS_DX12;
            FidelityFx::Get().Api().Dispatch(swapChainContext, &wait.header);
        }

        for (auto it = retired.begin(); it != retired.end();)
        {
            if (completedNow >= it->releaseAfter)
                it = retired.erase(it);
            else
                ++it;
        }
    }

    size_t FrameGeneration::RetiredCount() const
    {
        std::lock_guard<std::mutex> lock(mutex);
        return retired.size();
    }

    void FrameGeneration::CheckMotionSize(UINT& width, UINT& height) const
    {
        std::lock_guard<std::mutex> lock(mutex);
        width = 0;
        height = 0;
        if (!checkMotion)
            return;

        D3D11_TEXTURE2D_DESC desc = {};
        checkMotion->GetDesc(&desc);
        width = desc.Width;
        height = desc.Height;
    }

    void FrameGeneration::RegisterInputs(ID3D11Resource* newDepth, ID3D11Resource* newMotion,
                                         ID3D11Resource* newHudLess)
    {
        std::lock_guard<std::mutex> lock(mutex);

        depth.name = "depth";
        motion.name = "motion vectors";
        hudLess.name = "HUD-less";

        if (device11 == nullptr || device12 == nullptr)
        {
            LogLine("Textures registered before the devices exist -- ignored");
            depth.unity = newDepth;
            motion.unity = newMotion;
            hudLess.unity = newHudLess;
            return;
        }

        CreateInputLocked(depth, newDepth, DXGI_FORMAT_UNKNOWN);
        CreateInputLocked(motion, newMotion, DXGI_FORMAT_UNKNOWN);

        // In the backbuffer's own format: FSR finds the UI by comparing the two,
        // so they must differ in the UI and nowhere else. A different format
        // would be possible through ffxCreateContextDescFrameGenerationHudless
        // and is not used.
        if (CreateInputLocked(hudLess, newHudLess, backBufferFormat)
            && (S(hudLess).width != displayWidth || S(hudLess).height != displayHeight))
            LogLine("HUD-less texture is " + std::to_string(S(hudLess).width) + "x"
                    + std::to_string(S(hudLess).height) + " but the display is "
                    + std::to_string(displayWidth) + "x" + std::to_string(displayHeight)
                    + " -- FSR requires them equal");

        inputsCopied = false;

        // The check's staging textures were made for the old inputs' sizes;
        // they are made anew, at the new ones, by the next check. Left as they
        // were, a check would read a mapping by the new, larger size past its
        // end.
        ReleaseCheckLocked();
    }

    void FrameGeneration::OnFramePacket(const void* data)
    {
        std::lock_guard<std::mutex> lock(mutex);

        const auto* incoming = static_cast<const FramePacket*>(data);
        if (incoming == nullptr || incoming->size != sizeof(FramePacket)
            || incoming->magic != kFramePacketMagic)
        {
            if (packetsRejected == 0)
                LogLine("Frame packet rejected: size "
                        + std::to_string(incoming != nullptr ? incoming->size : 0u) + " magic 0x"
                        + [&] { char b[16]; FormatTo(b, "%08X", incoming != nullptr ? incoming->magic : 0u); return std::string(b); }()
                        + ", expected " + std::to_string(sizeof(FramePacket))
                        + " / 0x4B535046 -- the managed and native builds disagree on the layout");
            ++packetsRejected;
            return;
        }

        packet = *incoming;
        packetFresh = true;
    }

    // ------------------------------------------------------------------- flip

    bool FrameGeneration::EnsureFlipLocked()
    {
        if (flipReady)
            return true;
        if (flipUnavailable || device11 == nullptr)
            return false;

        // The compiler ships with Windows 10 but is loaded by hand: linked, this
        // dxgi.dll would fail to load, and the game not start, on a machine
        // without it.
        const HMODULE compiler = LoadLibraryW(L"d3dcompiler_47.dll");
        const auto compile = compiler != nullptr
            ? reinterpret_cast<pD3DCompile>(GetProcAddress(compiler, "D3DCompile"))
            : nullptr;
        if (compile == nullptr)
        {
            LogLine("d3dcompiler_47.dll not available -- inputs are copied without the flip");
            flipUnavailable = true;
            return false;
        }

        for (int variant = 0; variant < 3; ++variant)
        {
            const D3D_SHADER_MACRO defines[] = { { "CH", kFlipChannelTypes[variant] }, { nullptr, nullptr } };
            ComPtr<ID3DBlob> code;
            ComPtr<ID3DBlob> errors;
            HRESULT hr = compile(kFlipShader, sizeof(kFlipShader) - 1, "flip", defines, nullptr,
                                 "main", "cs_5_0", 0, 0, &code, &errors);
            if (FAILED(hr))
            {
                LogLine(std::string("Flip shader did not compile (") + Hr(hr) + "): "
                        + (errors ? static_cast<const char*>(errors->GetBufferPointer()) : ""));
                flipUnavailable = true;
                return false;
            }

            hr = device11->CreateComputeShader(code->GetBufferPointer(), code->GetBufferSize(),
                                               nullptr, &flipShaders[variant]);
            if (FAILED(hr))
            {
                LogLine("Flip shader could not be created (" + Hr(hr) + ")");
                flipUnavailable = true;
                return false;
            }
        }

        flipReady = true;
        LogLine("Flip shaders ready");
        return true;
    }

    bool FrameGeneration::CanFlipLocked(Input& input)
    {
        return input.unity && S(input).Valid() && FlipVariant(TypedFormat(S(input).format)) >= 0 && input.unityView
               && S(input).uav;
    }

    bool FrameGeneration::FlipLocked(Input& input)
    {
        const int variant = FlipVariant(TypedFormat(S(input).format));
        if (variant < 0 || !input.unityView || !S(input).uav || !EnsureFlipLocked())
            return false;

        ID3D11ShaderResourceView* views[] = { input.unityView.Get() };
        ID3D11UnorderedAccessView* uavs[] = { S(input).uav.Get() };
        context11->CSSetShader(flipShaders[variant].Get(), nullptr, 0);
        context11->CSSetShaderResources(0, 1, views);
        context11->CSSetUnorderedAccessViews(0, 1, uavs, nullptr);
        context11->Dispatch((S(input).width + 7) / 8, (S(input).height + 7) / 8, 1);

        // The flip's bindings go, so Unity's next dispatch does not find a view
        // where it expects nothing.
        ID3D11ShaderResourceView* noView[] = { nullptr };
        ID3D11UnorderedAccessView* noUav[] = { nullptr };
        context11->CSSetShaderResources(0, 1, noView);
        context11->CSSetUnorderedAccessViews(0, 1, noUav, nullptr);
        context11->CSSetShader(nullptr, nullptr, 0);
        return true;
    }

    void FrameGeneration::CopyInputLocked(Input& input, bool flip)
    {
        if (!input.unity || !S(input).Valid())
            return;

        if (flip && FlipLocked(input))
            return;

        // A straight CopyResource: the shared texture was created from the
        // source description, so format family and size match by construction.
        context11->CopyResource(S(input).d3d11.Get(), input.unity.Get());
    }

    // Everything Unity drew this frame is submitted by the time it presents,
    // so copies made here are of this frame.
    void FrameGeneration::CaptureForPresent(unsigned int newSlot)
    {
        std::lock_guard<std::mutex> lock(mutex);

        if (context11 == nullptr)
            return;

        slot = newSlot % 2;

        // Not while a failed context waits for its next attempt, or stands: no
        // such frame can be generated, so its copies would be for nothing.
        const bool copying = packetFresh && wanted && !AttemptWaitingLocked(GetTickCount64());
        if (copying)
        {
            // Depth and motion vectors both or neither: one that cannot be flipped
            // -- no variant for its format, no view on it -- leaves the other as it
            // is too, so the two share one orientation, which flippedLastCopy
            // states.
            const bool flip = Config().fgFlipInputs;
            const bool canFlip = flip && EnsureFlipLocked() && CanFlipLocked(depth) && CanFlipLocked(motion);

            const bool flipHudLess = Config().fgFlipHudLess && EnsureFlipLocked() && CanFlipLocked(hudLess);

            CopyInputLocked(depth, canFlip);
            CopyInputLocked(motion, canFlip);
            CopyInputLocked(hudLess, flipHudLess);

            if (flip && !canFlip && !loggedFlipMissing)
            {
                loggedFlipMissing = true;
                LogLine("Flip wanted but not available for depth and motion vectors -- both copied as they are");
            }

            flippedLastCopy = canFlip;
            inputsCopied = S(depth).Valid() && S(motion).Valid();
            ++capturedFrames;
            if (S(hudLess).Valid())
                ++hudLessCaptures;
        }

        CheckHudLessLocked(copying);
        DumpInputsLocked(copying);
    }

    void FrameGeneration::RegisterBackBuffer(const void* caller, ID3D11Texture2D* backBuffer)
    {
        std::lock_guard<std::mutex> lock(mutex);
        if (caller != owner)
            return;

        ReleaseCheckLocked();
        backBuffer11 = backBuffer;

        if (backBuffer == nullptr)
            return;

        D3D11_TEXTURE2D_DESC desc = {};
        backBuffer->GetDesc(&desc);

        // A context made for another size would be handed a backbuffer it was
        // not created for. ReleaseBackBuffer destroys it before every resize;
        // this only says so if that ever did not happen.
        if (contextCreated && (desc.Width != displayWidth || desc.Height != displayHeight))
            LogLine("Frame generation context still exists at the old display size -- "
                    "it should have been released before the resize");

        displayWidth = desc.Width;
        displayHeight = desc.Height;
        backBufferFormat = desc.Format;

        // The HUD-less shared texture carries the backbuffer's format. If the
        // format changed with the resize, it is rebuilt from the same Unity
        // texture.
        if (hudLess.unity && device11 && device12 && S(hudLess).format != backBufferFormat)
        {
            ComPtr<ID3D11Resource> unity = hudLess.unity;
            CreateInputLocked(hudLess, unity.Get(), backBufferFormat);
        }

        LogLine("Backbuffer registered: " + std::to_string(desc.Width) + "x"
                + std::to_string(desc.Height) + " format " + std::to_string(static_cast<int>(desc.Format)));
    }

    void FrameGeneration::ReleaseBackBuffer(const void* caller, void* swapChain)
    {
        std::lock_guard<std::mutex> lock(mutex);
        if (caller != owner)
            return;

        DestroyContextLocked(swapChain);
        // A failure that stands only for lasting may pass at the new size; one
        // that stands by its code does not.
        if (contextEscalated)
            ForgetContextFailuresLocked();
        ReleaseCheckLocked();
        backBuffer11.Reset();

        // The proxy waited for the GPU before the resize.
        retired.clear();
    }
}
