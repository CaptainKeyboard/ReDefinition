// DLSS Super Resolution through NGX on a Direct3D 12 device of its own (DlssD3d12.h).
//
// The parameters NGX is handed are those of the D3D11 path (Dlss.cpp), with D3D12 resources: the creation
// parameters and flags from the packet, the optimal render size and its dynamic range from the capability map,
// per frame the textures, jitter, motion vector scale, reset and render subrect. The hand-off across the devices
// is AmdUpscaler's (UpscalerIntegration).

#include "DlssD3d12.h"

#include "Config.h"
#include "FileUtil.h"
#include "Log.h"
#include "ProjectIdentity.h"
#include "Streamline.h"

#include <dxgi.h>

using Microsoft::WRL::ComPtr;

namespace redefinition
{
    namespace
    {
        // NVSDK_NGX_Feature_SuperSampling, and DLAA's quality value (DlssPacket::quality).
        constexpr int kNgxFeatureSuperSampling = 1;
        constexpr int kNgxQualityDlaa = 5;

        // Whether a feature made for a can take b: the same sizes, quality, preset and creation flags.
        bool SameFeature(const DlssPacket& a, const DlssPacket& b)
        {
            const uint32_t creationFlags = kDlssHdr | kDlssDepthInverted | kDlssAutoExposure;
            return a.renderWidth == b.renderWidth && a.renderHeight == b.renderHeight && a.outputWidth == b.outputWidth
                   && a.outputHeight == b.outputHeight && a.quality == b.quality && a.preset == b.preset
                   && (a.flags & creationFlags) == (b.flags & creationFlags);
        }

        // The GPU's name, for messages.
        std::string AdapterName(IDXGIAdapter* adapter)
        {
            DXGI_ADAPTER_DESC desc = {};
            return adapter != nullptr && SUCCEEDED(adapter->GetDesc(&desc)) ? Narrow(desc.Description) : std::string("this GPU");
        }
    }

    // Never destroyed, like Dlss: at process exit a static destructor would call into NGX after the driver's core
    // may already be gone.
    DlssD3d12& DlssD3d12::Get()
    {
        static DlssD3d12* instance = new DlssD3d12();
        return *instance;
    }

    bool DlssD3d12::On()
    {
        static const bool on = [] {
            const wchar_t* line = GetCommandLineW();
            return line == nullptr || wcsstr(line, L"-redefinitionDlssD3d11") == nullptr;
        }();
        return on;
    }

    DlssD3d12::DlssD3d12() : UpscalerIntegration("DLSS (Direct3D 12): ", "no DLSS frame yet")
    {
    }

    std::wstring DlssD3d12::RuntimeSignature() const
    {
        std::wstring signature;
        for (const std::wstring& folder : { DlssDirectory(), ExecutableDirectory() })
            if (!folder.empty())
                signature += folder + L"|" + FileStamp(folder + L"\\nvngx_dlss.dll") + L";";
        return signature;
    }

    void DlssD3d12::OnPacket(const DlssPacket& packet)
    {
        ComPtr<ID3D11Device> unity;
        static_cast<ID3D11Resource*>(packet.colour)->GetDevice(&unity);
        std::lock_guard<std::mutex> lock(mutex);
        std::lock_guard<std::mutex> ngx(NgxMutex());
        if (!EnsureDeviceLocked(unity.Get()) || !EnsureFenceLocked() || !EnsureNgxLocked() || !EnsureFeatureLocked(packet)
            || !EnsureTexturesLocked(packet.colour, packet.depth, packet.motionVectors, packet.output))
            return;
        EvaluateLocked(packet);
    }

    bool DlssD3d12::RenderSize(ID3D11Device* unity, uint32_t outputWidth, uint32_t outputHeight, int32_t quality, uint32_t optimal[2],
                               uint32_t minimum[2], uint32_t maximum[2], std::string& error)
    {
        std::lock_guard<std::mutex> lock(mutex);
        std::lock_guard<std::mutex> ngx(NgxMutex());
        if (!EnsureDeviceLocked(unity) || !EnsureNgxLocked())
        {
            char text[256] = {};
            const int reported = Status(text, sizeof(text));
            error = reported < 0 ? std::string(text) : std::string("NGX is not started on DLSS's Direct3D 12 device");
            return false;
        }
        unsigned int o[2] = {}, lo[2] = {}, hi[2] = {};
        if (!QuerySizeLocked(outputWidth, outputHeight, quality, o, lo, hi, error))
            return false;
        for (int i = 0; i < 2; ++i)
        {
            optimal[i] = o[i];
            minimum[i] = lo[i];
            maximum[i] = hi[i];
        }
        return true;
    }

    bool DlssD3d12::QuerySizeLocked(uint32_t outputWidth, uint32_t outputHeight, int32_t quality, unsigned int optimal[2],
                                    unsigned int minimum[2], unsigned int maximum[2], std::string& error)
    {
        // Output size and quality in, the optimal render size and the dynamic range out (DLSSOptimalSettingsCallback).
        // DLAA renders at the output size.
        NgxParameters* map = parameters;
        optimal[0] = minimum[0] = maximum[0] = outputWidth;
        optimal[1] = minimum[1] = maximum[1] = outputHeight;
        if (quality != kNgxQualityDlaa)
        {
            void* callback = nullptr;
            map->Get("DLSSOptimalSettingsCallback", &callback);
            if (callback == nullptr)
            {
                error = "this DLSS library gives no optimal settings";
                return false;
            }
            map->Set("Width", outputWidth);
            map->Set("Height", outputHeight);
            map->Set("PerfQualityValue", static_cast<int>(quality));
            map->Set("RTXValue", 0);
            const uint32_t result = static_cast<NgxOptimalSettingsFn>(callback)(map);
            if (NgxFailed(result))
            {
                error = "DLSS optimal settings query failed (" + NgxHex(result) + ")";
                return false;
            }
            map->Get("OutWidth", &optimal[0]);
            map->Get("OutHeight", &optimal[1]);
            minimum[0] = maximum[0] = optimal[0];
            minimum[1] = maximum[1] = optimal[1];
            map->Get("DLSS.Get.Dynamic.Min.Render.Width", &minimum[0]);
            map->Get("DLSS.Get.Dynamic.Min.Render.Height", &minimum[1]);
            map->Get("DLSS.Get.Dynamic.Max.Render.Width", &maximum[0]);
            map->Get("DLSS.Get.Dynamic.Max.Render.Height", &maximum[1]);
        }
        if (optimal[0] == 0 || optimal[1] == 0)
        {
            error = "DLSS offers this quality mode not at " + NgxSize(outputWidth, outputHeight);
            return false;
        }
        // A library without the dynamic keys takes the optimal size alone.
        if (minimum[0] == 0 || minimum[1] == 0)
        {
            minimum[0] = optimal[0];
            minimum[1] = optimal[1];
        }
        if (maximum[0] == 0 || maximum[1] == 0)
        {
            maximum[0] = optimal[0];
            maximum[1] = optimal[1];
        }
        return true;
    }

    void DlssD3d12::OnRelease()
    {
        std::lock_guard<std::mutex> lock(mutex);
        std::lock_guard<std::mutex> ngx(NgxMutex());
        ReleaseLocked();
        SetStatus(0, "DLSS feature released, no DLSS frame since");
    }

    void DlssD3d12::ReleaseLocked()
    {
        WaitForListsLocked();
        ReleaseFeatureLocked();
        ReleaseTexturesLocked();
        hasFailed = false;
    }

    bool DlssD3d12::EnsureDeviceLocked(ID3D11Device* unity)
    {
        ComPtr<ID3D11Device5> unity5;
        if (unity == nullptr || FAILED(unity->QueryInterface(IID_PPV_ARGS(&unity5))))
        {
            SetStatus(-1, "Unity's device offers no shared fences (ID3D11Device5)");
            return false;
        }
        if (device12 != nullptr && device11.Get() == unity5.Get())
            return true;

        // A device of Unity's that changed: what was made for the old one goes first.
        ReleaseLocked();
        fence12.Reset();
        fence11.Reset();
        for (auto& allocator : allocators)
            allocator.Reset();
        list.Reset();
        queue.Reset();
        device12.Reset();
        attached = false;

        ComPtr<ID3D11DeviceContext> immediate;
        ComPtr<ID3D11DeviceContext4> context4;
        unity->GetImmediateContext(&immediate);
        ComPtr<IDXGIDevice> dxgiDevice;
        ComPtr<IDXGIAdapter> adapter;
        if (FAILED(immediate.As(&context4)) || FAILED(unity->QueryInterface(IID_PPV_ARGS(&dxgiDevice)))
            || FAILED(dxgiDevice->GetAdapter(&adapter)))
        {
            SetStatus(-1, "Unity's device gave no fence-capable context or no GPU");
            return false;
        }
        // The GPU Unity renders on, so the shared textures and the fence open on both devices.
        ComPtr<ID3D12Device> made;
        HRESULT hr = D3D12CreateDevice(adapter.Get(), D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&made));
        ComPtr<ID3D12CommandQueue> madeQueue;
        if (SUCCEEDED(hr))
        {
            D3D12_COMMAND_QUEUE_DESC desc = {};
            desc.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
            hr = made->CreateCommandQueue(&desc, IID_PPV_ARGS(&madeQueue));
        }
        if (FAILED(hr))
        {
            SetStatus(-1, "no Direct3D 12 device for DLSS on " + AdapterName(adapter.Get()) + " (" + Hr(hr) + ")");
            return false;
        }
        device11 = unity5;
        context11 = context4;
        device12 = made;
        queue = madeQueue;
        attached = true;
        LogLine("DLSS: Super Resolution runs on a Direct3D 12 device of its own on " + AdapterName(adapter.Get())
                + (Streamline::Get().DeviceSet() ? "; Streamline holds NGX on the swapchain's Direct3D 12 device beside it"
                                                 : "; Streamline holds no Direct3D 12 device"));
        return true;
    }

    bool DlssD3d12::EnsureNgxLocked()
    {
        if (parameters != nullptr && ngxDevice == device12.Get())
            return true;
        // A failure stands until the folder or the DLL in it changes; tried at most once a second.
        if (!RuntimeDueLocked())
            return false;

        if (parameters != nullptr)
        {
            // NGX on a device that is gone keeps running (never shut down); its map goes.
            if (parametersOwned)
                api.destroyParameters(parameters);
            parameters = nullptr;
            parametersOwned = false;
        }

        std::string error;
        bool stable = false;
        const HMODULE core = NgxCoreModule(error, stable);
        if (core == nullptr)
        {
            SetStatus(stable ? -2 : -1, error);
            return false;
        }
        api = NgxD3d12Exports(core);
        if (!api.Complete())
        {
            SetStatus(-2, "the driver's NGX core lacks a D3D12 function DLSS needs -- driver too old?");
            return false;
        }

        // NGX looks next to the executable itself; dlssDirectory comes first.
        search.Set({ DlssDirectory(), ExecutableDirectory() });
        commonInfo = search.Info();
        ResetNgxLog();
        dataPath = ExecutableDirectory();
        uint32_t result = api.initProjectId(kNvidiaProjectId, kNgxEngineCustom, kEngineVersion, dataPath.c_str(), device12.Get(),
                                            kNgxVersionApi, &commonInfo);
        if (result == kNgxFeatureNotSupported)
        {
            SetStatus(-2, "NGX reports this GPU as unsupported -- DLSS needs an NVIDIA RTX GPU");
            return false;
        }
        if (NgxFailed(result))
        {
            SetStatus(-1, "NGX could not be initialised on DLSS's Direct3D 12 device (" + NgxHex(result) + ")");
            return false;
        }
        // From here on nothing in the process may end NGX on the D3D12 devices (DlssD3d12.h).
        NgxD3d12KeepRunning();
        ngxDevice = device12.Get();

        // The capability map, which the queries read and the feature takes; an older driver has only the one NGX
        // manages itself (DLSS Programming Guide 5.2.6).
        NgxParameters* map = nullptr;
        result = api.getCapabilityParameters(&map);
        parametersOwned = !NgxFailed(result) && map != nullptr;
        if (!parametersOwned && api.getParameters != nullptr)
            result = api.getParameters(&map);
        if (NgxFailed(result) || map == nullptr)
        {
            SetStatus(-1, "NGX gave no parameter map on DLSS's Direct3D 12 device (" + NgxHex(result) + ")");
            return false;
        }
        parameters = map;

        int needsUpdatedDriver = 0;
        if (!NgxFailed(map->Get("SuperSampling.NeedsUpdatedDriver", &needsUpdatedDriver)) && needsUpdatedDriver != 0)
        {
            unsigned int major = 0, minor = 0;
            map->Get("SuperSampling.MinDriverVersionMajor", &major);
            map->Get("SuperSampling.MinDriverVersionMinor", &minor);
            SetStatus(-2, "DLSS needs a newer NVIDIA driver, at least " + std::to_string(major) + "." + std::to_string(minor));
            return false;
        }
        int available = 0;
        if (NgxFailed(map->Get("SuperSampling.Available", &available)) || available == 0)
        {
            SetStatus(-2, "DLSS is not available: no usable nvngx_dlss.dll in " + search.Where()
                              + ", or no RTX GPU -- NVIDIA DLSS files in ReDefinition's settings window downloads it");
            return false;
        }
        int initResult = 0;
        if (!NgxFailed(map->Get("SuperSampling.FeatureInitResult", &initResult)) && NgxFailed(static_cast<uint32_t>(initResult)))
        {
            SetStatus(-2, "NGX denies DLSS for this application (" + NgxHex(static_cast<uint32_t>(initResult)) + ")");
            return false;
        }
        LogLine("DLSS: NGX initialised on its own Direct3D 12 device");
        return true;
    }

    bool DlssD3d12::EnsureFeatureLocked(const DlssPacket& packet)
    {
        if (feature != nullptr && SameFeature(created, packet))
            return true;
        if (hasFailed && SameFeature(failed, packet))
            return false;
        // The lists in flight still evaluate the old one.
        ReleaseFeatureLocked();

        const auto fail = [&](const std::string& why) {
            SetStatus(-1, why);
            failed = packet;
            hasFailed = true;
            return false;
        };

        // The render size the feature is made for: the optimal one for the output size and quality, with its range.
        NgxParameters* map = parameters;
        unsigned int optimal[2] = {}, minimum[2] = {}, maximum[2] = {};
        std::string sizeError;
        if (!QuerySizeLocked(packet.outputWidth, packet.outputHeight, packet.quality, optimal, minimum, maximum, sizeError))
            return fail(sizeError);
        const unsigned int optimalWidth = optimal[0], optimalHeight = optimal[1];
        const unsigned int minWidth = minimum[0], minHeight = minimum[1], maxWidth = maximum[0], maxHeight = maximum[1];
        // The frame's render size has to lie in that range: otherwise "the call to DLSS Evaluate fails with an
        // NVSDK_NGX_Result_FAIL_InvalidParameter error code" (guide 3.2.2).
        if (packet.renderWidth < minWidth || packet.renderWidth > maxWidth || packet.renderHeight < minHeight
            || packet.renderHeight > maxHeight)
            return fail("DLSS takes this mode at " + NgxSize(minWidth, minHeight) + " to " + NgxSize(maxWidth, maxHeight) + ", not at "
                        + NgxSize(packet.renderWidth, packet.renderHeight) + "; it asks for " + NgxSize(optimalWidth, optimalHeight));

        for (const char* mode : { "DLSS.Hint.Render.Preset.DLAA", "DLSS.Hint.Render.Preset.Quality", "DLSS.Hint.Render.Preset.Balanced",
                                  "DLSS.Hint.Render.Preset.Performance", "DLSS.Hint.Render.Preset.UltraPerformance",
                                  "DLSS.Hint.Render.Preset.UltraQuality" })
            map->Set(mode, static_cast<unsigned int>(packet.preset));

        // Motion vectors at the render size ("MVLowRes") and without jitter: the integration's are Unity's own, rendered unjittered.
        int flags = kNgxFlagMotionVectorsLowRes;
        if ((packet.flags & kDlssHdr) != 0)
            flags |= kNgxFlagIsHdr;
        if ((packet.flags & kDlssDepthInverted) != 0)
            flags |= kNgxFlagDepthInverted;
        if ((packet.flags & kDlssAutoExposure) != 0)
            flags |= kNgxFlagAutoExposure;
        map->Set("CreationNodeMask", 1u);
        map->Set("VisibilityNodeMask", 1u);
        map->Set("Width", optimalWidth);
        map->Set("Height", optimalHeight);
        map->Set("OutWidth", static_cast<unsigned int>(packet.outputWidth));
        map->Set("OutHeight", static_cast<unsigned int>(packet.outputHeight));
        map->Set("PerfQualityValue", static_cast<int>(packet.quality));
        map->Set("DLSS.Feature.Create.Flags", flags);
        map->Set("DLSS.Enable.Output.Subrects", 0);
        // "force memory release on each ReleaseFeature() call" (guide 3.14), as sl.dlss asks.
        map->Set("FreeMemOnReleaseFeature", 1);

        // The creation is recorded on a list of its own and run before the first evaluation.
        UINT index = 0;
        if (!BeginListLocked(index))
            return false;
        ResetNgxLog();
        void* handle = nullptr;
        const uint32_t result = api.createFeature(list.Get(), kNgxFeatureSuperSampling, map, &handle);
        if (!FinishListLocked(index))
            return false;
        if (NgxFailed(result) || handle == nullptr)
            return fail("the DLSS feature could not be created on Direct3D 12 (" + NgxHex(result) + ")");

        feature = handle;
        created = packet;
        hasFailed = false;
        loggedEvaluateFailure = false;
        upscaled = 0;
        resetFeature = true;
        renderState = 0;
        LogLine("DLSS: " + LoadedNgxModule(L"nvngx_dlss.dll") + " on Direct3D 12, created for " + NgxSize(optimalWidth, optimalHeight)
                + " (range " + NgxSize(minWidth, minHeight) + " to " + NgxSize(maxWidth, maxHeight) + ") to "
                + NgxSize(packet.outputWidth, packet.outputHeight) + ", rendered at " + NgxSize(packet.renderWidth, packet.renderHeight)
                + ", quality " + std::to_string(packet.quality) + ", preset " + std::to_string(packet.preset) + ", flags "
                + NgxHex(static_cast<uint32_t>(flags)));
        return true;
    }

    bool DlssD3d12::EvaluateLocked(const DlssPacket& packet)
    {
        // D3D11: this frame's inputs into the shared textures, and a point in its stream for the queue to wait for.
        context11->CopyResource(colour.d3d11.Get(), static_cast<ID3D11Resource*>(packet.colour));
        context11->CopyResource(depth.d3d11.Get(), static_cast<ID3D11Resource*>(packet.depth));
        context11->CopyResource(motion.d3d11.Get(), static_cast<ID3D11Resource*>(packet.motionVectors));
        SignalInputsLocked();

        UINT index = 0;
        if (!BeginListLocked(index))
            return false;

        // Shared resources are in COMMON whenever D3D11 touches them. The inputs as compute reads, the output as a
        // UAV, which COMMON cannot be promoted to implicitly.
        const D3D12_RESOURCE_STATES read = D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE;
        BarrierBatch before;
        before.Add(colour.d3d12.Get(), D3D12_RESOURCE_STATE_COMMON, read);
        before.Add(depth.d3d12.Get(), D3D12_RESOURCE_STATE_COMMON, read);
        before.Add(motion.d3d12.Get(), D3D12_RESOURCE_STATE_COMMON, read);
        before.Add(output.d3d12.Get(), D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
        before.Record(list.Get());

        NgxParameters* map = parameters;
        map->Set("Color", colour.d3d12.Get());
        map->Set("Output", output.d3d12.Get());
        map->Set("Depth", depth.d3d12.Get());
        map->Set("MotionVectors", motion.d3d12.Get());
        map->Set("Jitter.Offset.X", packet.jitterX);
        map->Set("Jitter.Offset.Y", packet.jitterY);
        map->Set("MV.Scale.X", packet.motionVectorScaleX);
        map->Set("MV.Scale.Y", packet.motionVectorScaleY);
        map->Set("Reset", (packet.flags & kDlssReset) != 0 || resetFeature ? 1 : 0);
        map->Set("DLSS.Render.Subrect.Dimensions.Width", static_cast<unsigned int>(packet.renderWidth));
        map->Set("DLSS.Render.Subrect.Dimensions.Height", static_cast<unsigned int>(packet.renderHeight));
        map->Set("DLSS.Pre.Exposure", 1.0f);
        map->Set("DLSS.Exposure.Scale", 1.0f);
        // Deprecated (guide 3.11); zero so no older library sharpens.
        map->Set("Sharpness", 0.0f);
        const uint32_t result = api.evaluateFeature(list.Get(), feature, map, nullptr);

        BarrierBatch after;
        after.Add(output.d3d12.Get(), D3D12_RESOURCE_STATE_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_COMMON);
        after.Add(motion.d3d12.Get(), read, D3D12_RESOURCE_STATE_COMMON);
        after.Add(depth.d3d12.Get(), read, D3D12_RESOURCE_STATE_COMMON);
        after.Add(colour.d3d12.Get(), read, D3D12_RESOURCE_STATE_COMMON);
        after.Record(list.Get());
        if (!FinishListLocked(index))
            return false;
        // A lost device says so itself; the evaluation's result alone would not.
        const HRESULT removed = device12->GetDeviceRemovedReason();
        if (FAILED(removed))
        {
            SetStatus(-1, "DLSS's Direct3D 12 device was removed (" + Hr(removed) + ")");
            return false;
        }
        if (NgxFailed(result))
        {
            if (!loggedEvaluateFailure)
                LogLine("DLSS (Direct3D 12): evaluation failed (" + NgxHex(result) + ")");
            loggedEvaluateFailure = true;
            SetStatus(-1, "DLSS evaluation failed (" + NgxHex(result) + ")");
            return false;
        }
        resetFeature = false;

        // D3D11: the result into Unity's output once the queue is past it.
        context11->CopyResource(static_cast<ID3D11Resource*>(packet.output), output.d3d11.Get());

        ++upscaled;
        if (renderState != 1 || (upscaled & 1023) == 0)
            SetStatus(1, "DLSS running on Direct3D 12, " + std::to_string(created.renderWidth) + "x" + std::to_string(created.renderHeight)
                             + " to " + std::to_string(created.outputWidth) + "x" + std::to_string(created.outputHeight) + ", "
                             + std::to_string(upscaled) + " frames");
        return true;
    }

    void DlssD3d12::ReleaseFeatureLocked()
    {
        if (feature == nullptr)
            return;
        WaitForListsLocked();
        api.releaseFeature(feature);
        feature = nullptr;
        created = DlssPacket{};
        LogLine("DLSS (Direct3D 12): feature released");
    }
}
