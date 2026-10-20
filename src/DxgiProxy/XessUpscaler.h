#pragma once

#include <windows.h>

#include <d3d11_4.h>
#include <d3d12.h>
#include <wrl/client.h>

#include <XeSS/xess_d3d12.h>

#include "AmdUpscaler.h"
#include "FrameGeneration.h"

#include <atomic>
#include <cstdint>
#include <mutex>
#include <string>

namespace redefinition
{
    // One frame for XeSS, in the same layout as AMD's (AmdUpscaler.h), which the
    // managed side fills alike; a magic of its own.
    using XessUpscalerPacket = AmdUpscalerPacket;
    constexpr uint32_t kXessUpscalerPacketMagic = 0x4B535058u;   // 'KSPX'

    // Intel's XeSS Super Resolution on the proxy's D3D12 device, from the player's
    // libxess.dll (next to KSP_x64.exe or in xessDirectory, Config.h).
    //
    // XeSS on Direct3D 11 "is limited to Intel Arc Graphics or later"; on Direct3D 12
    // it runs on "Other vendor GPU supporting SM 6.4 and with hardware acceleration
    // for DP4a", XMX on Intel Arc (xess_sr_developer_guide_english.md, Requirements,
    // Initialization D3D11 specific). KSP renders on D3D11, so the frame goes across
    // as AMD's upscaler takes it: Unity's colour, depth and motion vectors into
    // textures both devices share, XeSS on the D3D12 queue, the result back into
    // Unity's output, a shared fence between the two.
    //
    // Its conventions are the ones the packet already carries for FSR: the jitter in
    // pixels, as the projection's shear "ProjectionMatrix[2][0] += Jx * 2.0f /
    // InputWidth; ProjectionMatrix[2][1] -= Jy * 2.0f / InputHeight"; "Motion vectors
    // specify the screen-space motion in pixels from the current frame to the
    // previous frame", at the input resolution, scaled by xessSetVelocityScale.
    //
    // Runs on Unity's render thread, from the render events 7 (a frame) and 8 (the
    // rig gone).
    class XessUpscaler
    {
    public:
        static XessUpscaler& Get();

        void Attach(const void* owner, ID3D11Device5* device11, ID3D11DeviceContext4* context11,
                    ID3D12Device* device12, ID3D12CommandQueue* queue);
        void Detach(const void* owner);
        bool Attached() const { return attached.load(); }

        void OnPacket(const void* data);
        void OnRelease();

        // As AmdUpscaler::Status: 1 running, -1 stopped, -2 stopped for a reason that
        // stands while libxess.dll's folder and file are as they were, 0 not yet.
        int Status(char* buffer, int size) const;

        // The XeSS version libxess.dll reports, as "XeSS 3.0.2", into buffer; 0 before
        // the DLL is loaded.
        int Version(char* buffer, int size) const;

    private:
        XessUpscaler() = default;

        bool EnsureRuntimeLocked();
        bool EnsureFenceLocked();
        bool EnsureTexturesLocked(const XessUpscalerPacket& packet);
        bool EnsureContextLocked(const XessUpscalerPacket& packet);
        bool UpscaleLocked(const XessUpscalerPacket& packet);
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

        // libxess.dll's functions.
        HMODULE module = nullptr;
        decltype(&xessD3D12CreateContext) createContext = nullptr;
        decltype(&xessD3D12Init) init = nullptr;
        decltype(&xessD3D12Execute) execute = nullptr;
        decltype(&xessDestroyContext) destroyContext = nullptr;
        decltype(&xessSetVelocityScale) setVelocityScale = nullptr;
        decltype(&xessGetVersion) getVersion = nullptr;
        // Optional: a libxess.dll before 1.3.0 has only the legacy scale factors.
        decltype(&xessForceLegacyScaleFactors) forceLegacyScaleFactors = nullptr;
        decltype(&xessGetOptimalInputResolution) getOptimalInputResolution = nullptr;
        bool runtimeTried = false;
        std::wstring runtimeSignature;
        ULONGLONG nextRuntimeCheck = 0;
        std::string versionName;

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

        xess_context_handle_t context = nullptr;
        XessUpscalerPacket created = {};
        bool contextFailed = false;
        float velocityScaleX = 0.0f;
        float velocityScaleY = 0.0f;

        uint64_t upscaled = 0;
        int renderState = 0;

        mutable std::mutex statusLock;
        int state = 0;
        std::string statusText = "no frame for XeSS yet";
        std::wstring stableSignature;
        std::string shownVersion;
    };
}
