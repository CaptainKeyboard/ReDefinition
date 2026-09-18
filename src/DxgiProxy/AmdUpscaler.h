#pragma once

#include <windows.h>

#include <d3d11_4.h>
#include <d3d12.h>
#include <wrl/client.h>

#include <FidelityFX/api/include/ffx_api.h>
#include <FidelityFX/api/include/ffx_api_loader.h>

#include "FrameGeneration.h"

#include <atomic>
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
    };

    constexpr uint32_t kAmdUpscalerPacketMagic = 0x4B535041u;   // 'KSPA'

    constexpr uint32_t kAmdUpscalerHdr = 1u << 0;
    constexpr uint32_t kAmdUpscalerDepthInverted = 1u << 1;
    constexpr uint32_t kAmdUpscalerAutoExposure = 1u << 2;
    constexpr uint32_t kAmdUpscalerReset = 1u << 3;

    // AMD's upscaler through its FidelityFX API, on the proxy's D3D12 device.
    //
    // FSR 4 "requires integration using the AMD FSR API and use of the signed
    // binary distribution", and the API is D3D12 or Vulkan (super-resolution-
    // ml.md, ffx-api.md). KSP renders on D3D11, so the frame goes across: Unity's
    // colour, depth and motion vectors are copied into textures both devices
    // share, AMD's upscaler runs on the D3D12 queue, and the result is copied back
    // into Unity's output. A shared fence orders the two: D3D12 waits for the
    // point in D3D11's stream after the copies, D3D11 for the point after the
    // upscaler.
    //
    // Which upscaler runs is the DLL's choice for the GPU: FSR 4 where the GPU and
    // driver have it ("FSR4 requires an AMD 7000 series discrete GPU or AMD 9000
    // series GPU or later"), otherwise the FSR 3.1 the DLL carries. The provider's
    // name goes into the status. amd_fidelityfx_upscaler_dx12.dll comes from the
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

    private:
        AmdUpscaler() = default;

        bool EnsureRuntimeLocked();
        bool EnsureFenceLocked();
        bool EnsureTexturesLocked(const AmdUpscalerPacket& packet);
        bool EnsureContextLocked(const AmdUpscalerPacket& packet);
        bool UpscaleLocked(const AmdUpscalerPacket& packet);
        void WaitForListsLocked();
        void ReleaseLocked();
        void SetStatus(int state, const std::string& text);

        mutable std::mutex mutex;

        const void* owner = nullptr;
        std::atomic<bool> attached{ false };
        Microsoft::WRL::ComPtr<ID3D11Device5> device11;
        Microsoft::WRL::ComPtr<ID3D11DeviceContext4> context11;
        Microsoft::WRL::ComPtr<ID3D12Device> device12;
        Microsoft::WRL::ComPtr<ID3D12CommandQueue> queue;

        HMODULE module = nullptr;
        ffxFunctions api = {};
        bool runtimeTried = false;
        // The folder and the DLL in it when it was last tried: a DLL that did not
        // load is tried again by a new rig only once that changed.
        std::wstring runtimeSignature;
        ULONGLONG nextRuntimeCheck = 0;
        bool loggedVersions = false;
        std::string providerName;

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
        // The DLL's folder and file when a -2 was set (RuntimeSignature).
        std::wstring stableSignature;
    };
}
