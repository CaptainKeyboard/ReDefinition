#pragma once

#include <windows.h>

#include <d3d11_4.h>
#include <d3d12.h>
#include <wrl/client.h>

#include <FidelityFX/api/include/ffx_api.h>
#include <FidelityFX/api/include/ffx_api_loader.h>

#include "FrameGeneration.h"

#include <atomic>
#include <cstddef>
#include <cstdint>
#include <mutex>
#include <string>

namespace redefinition
{
    // One frame for AMD's upscaler, written by the managed side and sent through
    // Unity's render thread like the DLSS packet (Dlss.h). Checked by size and
    // magic on every packet and by KspAmdUpscalerPacketSize before the first.
    struct AmdUpscalerPacket
    {
        // The packet's size and magic; Unity's four textures as ID3D11Resource pointers; the render and output
        // sizes; the kAmdUpscaler... flags; the jitter in pixels and the scale that turns Unity's motion vectors
        // into pixels.
        uint32_t size;
        uint32_t magic;
        void* colour;
        void* output;
        void* depth;
        void* motionVectors;
        uint32_t renderWidth;
        uint32_t renderHeight;
        uint32_t outputWidth;
        uint32_t outputHeight;
        uint32_t flags;
        float jitterX;
        float jitterY;
        float motionVectorScaleX;
        float motionVectorScaleY;
        // RCAS after the upscaler, 0 to 1; 0 switches it off.
        float sharpness;
        float frameTimeDeltaMs;
        float cameraNear;
        float cameraFar;
        float verticalFovRadians;
        // Optional, single-channel, at render size; null where the mod has none.
        void* reactive;
        void* transparencyAndComposition;
    };

    static_assert(sizeof(AmdUpscalerPacket) == 112, "AmdUpscalerPacket layout changed: update the managed side");
    static_assert(offsetof(AmdUpscalerPacket, reactive) == 96, "AmdUpscalerPacket layout changed");

    // The packet's magic for AMD's upscaler, which OnPacket checks before reading the rest.
    constexpr uint32_t kAmdUpscalerPacketMagic = 0x4B535041u;   // 'KSPA'

    // The packet's flags: an HDR colour target, reversed depth and automatic exposure (which the context is
    // made with), and a history reset for this frame.
    constexpr uint32_t kAmdUpscalerHdr = 1u << 0;
    constexpr uint32_t kAmdUpscalerDepthInverted = 1u << 1;
    constexpr uint32_t kAmdUpscalerAutoExposure = 1u << 2;
    constexpr uint32_t kAmdUpscalerReset = 1u << 3;

    // AMD's upscaler through its FidelityFX API, on the proxy's D3D12 device.
    //
    // FSR 4 "requires integration using the AMD FSR API and use of the signed
    // binary distribution", and the API is D3D12 or Vulkan (super-resolution-
    // ml.md, ffx-api.md). KSP renders on D3D11, so the frame goes across. Unity's colour, depth and
    // motion vectors are copied into textures both devices share. AMD's upscaler runs on the D3D12
    // queue, and the result is copied back into Unity's output. A shared fence orders the two:
    // D3D12 waits for the point in D3D11's stream after the copies, D3D11 for the point after the
    // upscaler.
    //
    // Which upscaler runs is the DLL's choice for the GPU. It is FSR 4 where the GPU and driver have
    // it ("FSR4 requires an AMD 7000 series discrete GPU or AMD 9000 series GPU or later"),
    // otherwise the FSR 3.1 the DLL carries. The provider's name goes into the status. amd_fidelityfx_upscaler_dx12.dll comes from the
    // player, next to KSP_x64.exe or in amdUpscalerDirectory (Config.h).
    //
    // Runs on Unity's render thread, from the render event of the upscaler's
    // dispatch buffer, as DLSS does.
    class AmdUpscaler
    {
    public:
        static AmdUpscaler& Get();

        // From the swapchain proxy, which owns both devices and the queue.
        void Attach(const void* owner, ID3D11Device5* device11, ID3D11DeviceContext4* context11,
                    ID3D12Device* device12, ID3D12CommandQueue* queue);
        // Lets go of everything, the devices too, where owner is the one attached; otherwise nothing.
        void Detach(const void* owner);

        // Whether a swapchain proxy presents through D3D12 and has handed over
        // its devices: without that nothing can run. Read from the main thread.
        bool Attached() const { return attached.load(); }

        // Event 4: this frame, upscaled into the packet's output.
        void OnPacket(const void* data);

        // Event 5: the rig is gone, and the context and textures with it.
        void OnRelease();

        // 1 while frames are upscaled, -1 once something stopped it, -2 when
        // that stands -- no usable DLL, while its folder and file are as they
        // were -- 0 before the first packet; the text says what and why.
        int Status(char* buffer, int size) const;

        // Which FSR the context runs, written as "FSR 4.1.1" or "FSR 3.1.5" into buffer. The result is
        // 1 for FSR 4. It is 2 for an earlier FSR although the DLL is FSR 4's. The DLL offers this GPU
        // no FSR 4 ("FSR4 requires an AMD 7000 series discrete GPU or AMD 9000 series GPU or later",
        // super-resolution-ml.md) and runs the FSR 3 it carries. It is 3 for an earlier FSR from an
        // earlier DLL. It is 0 before a context, with buffer empty.
        int Provider(char* buffer, int size) const;

    private:
        // Only Get makes it.
        AmdUpscaler() = default;

        // The DLL loaded and its FidelityFX API functions resolved; tried again at most once a second, and only
        // once its folder or file changed.
        bool EnsureRuntimeLocked();
        // The fence shared with D3D11, the command allocators and the list, made once per attachment.
        bool EnsureFenceLocked();
        // The four shared textures, made again where the frame's differ in size or format.
        bool EnsureTexturesLocked(const AmdUpscalerPacket& packet);
        bool EnsureMaskLocked(SharedTexture& shared, void* texture);
        // The context made for the frame's sizes and creation flags, with the provider it runs looked up; a
        // failure stands until those change.
        bool EnsureContextLocked(const AmdUpscalerPacket& packet);
        // The frame across: inputs copied on D3D11, the dispatch on the D3D12 queue, the result copied back into
        // Unity's output, the two devices ordered by the shared fence.
        bool UpscaleLocked(const AmdUpscalerPacket& packet);
        // Blocks until the queue has finished every list handed to it.
        void WaitForListsLocked();
        // The context destroyed and the shared textures released, after the queue has finished.
        void ReleaseLocked();
        // The state and text Status reports; a stop is logged where it changes.
        void SetStatus(int state, const std::string& text);
        // No context, so no FSR that Provider names.
        void ClearProviderLocked();

        // Held by every entry point: Attach and Detach come from the swapchain proxy, the rest from Unity's render thread.
        mutable std::mutex mutex;

        // The swapchain proxy attached, whether it gave a D3D12 device and queue, and its devices.
        const void* owner = nullptr;
        std::atomic<bool> attached{ false };
        Microsoft::WRL::ComPtr<ID3D11Device5> device11;
        Microsoft::WRL::ComPtr<ID3D11DeviceContext4> context11;
        Microsoft::WRL::ComPtr<ID3D12Device> device12;
        Microsoft::WRL::ComPtr<ID3D12CommandQueue> queue;

        // amd_fidelityfx_upscaler_dx12.dll, its API functions, and whether loading it was tried.
        HMODULE module = nullptr;
        ffxFunctions api = {};
        bool runtimeTried = false;
        // The folder and the DLL in it when it was last tried: a DLL that did not
        // load is tried again by a new rig only once that changed.
        std::wstring runtimeSignature;
        ULONGLONG nextRuntimeCheck = 0;
        bool loggedVersions = false;
        std::string providerName;
        // The DLL's file version, and the upscaler versions it offers on this device.
        std::string dllVersion;
        std::string offeredVersions;

        Microsoft::WRL::ComPtr<ID3D12Fence> fence12;
        Microsoft::WRL::ComPtr<ID3D11Fence> fence11;
        HANDLE fenceEvent = nullptr;
        UINT64 fenceValue = 0;
        static constexpr UINT kLists = 3;
        Microsoft::WRL::ComPtr<ID3D12CommandAllocator> allocators[kLists];
        UINT64 listDone[kLists] = {};
        UINT listIndex = 0;
        Microsoft::WRL::ComPtr<ID3D12GraphicsCommandList> list;

        SharedTexture colour;
        SharedTexture depth;
        SharedTexture motion;
        SharedTexture output;
        SharedTexture reactive;
        SharedTexture transparency;

        ffxContext context = nullptr;
        // The frame the context was made for, or failed to be made for.
        AmdUpscalerPacket created = {};
        bool contextFailed = false;

        uint64_t upscaled = 0;
        int renderState = 0;

        // Apart from mutex, which OnPacket holds while it sets the status.
        mutable std::mutex statusLock;
        int state = 0;
        std::string statusText = "no frame for AMD's upscaler yet";
        // What Provider reports, set with each context.
        int providerKind = 0;
        std::string providerShown;
        // The DLL's folder and file when a -2 was set (RuntimeSignature).
        std::wstring stableSignature;
    };
}
