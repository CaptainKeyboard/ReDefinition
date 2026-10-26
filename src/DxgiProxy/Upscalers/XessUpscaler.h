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
    // The packet's magic for XeSS, which OnPacket checks before reading the rest.
    constexpr uint32_t kXessUpscalerPacketMagic = 0x4B535058u;   // 'KSPX'

    // Intel's XeSS Super Resolution on the proxy's D3D12 device, from the player's
    // libxess.dll (next to KSP_x64.exe or in xessDirectory, Config.h).
    //
    // XeSS on Direct3D 11 "is limited to Intel Arc Graphics or later". On Direct3D 12 it runs on
    // "Other vendor GPU supporting SM 6.4 and with hardware acceleration for DP4a", with XMX on
    // Intel Arc (xess_sr_developer_guide_english.md, Requirements, Initialization D3D11 specific).
    // KSP renders on D3D11, so the frame goes across as AMD's upscaler takes it. Unity's colour,
    // depth and motion vectors go into textures both devices share, XeSS runs on the D3D12 queue,
    // and the result goes back into Unity's output. A shared fence sits between the two.
    //
    // Its conventions are the ones the packet already carries for FSR. The jitter is in pixels. It
    // is the projection's shear "ProjectionMatrix[2][0] += Jx * 2.0f / InputWidth;
    // ProjectionMatrix[2][1] -= Jy * 2.0f / InputHeight". The motion vectors are as XeSS says:
    // "Motion vectors specify the screen-space motion in pixels from the current frame to the
    // previous frame". They are at the input resolution, scaled by xessSetVelocityScale.
    //
    // Runs on Unity's render thread, from the render events 7 (a frame) and 8 (the
    // rig gone).
    class XessUpscaler
    {
    public:
        // The one instance, made on first use and never destroyed.
        static XessUpscaler& Get();

        // The swapchain proxy that made the devices hands them over; what an earlier owner left is released.
        void Attach(const void* owner, ID3D11Device5* device11, ID3D11DeviceContext4* context11,
                    ID3D12Device* device12, ID3D12CommandQueue* queue);
        // Lets go of everything, the devices too, where owner is the one attached; otherwise nothing.
        void Detach(const void* owner);
        // Whether a D3D12 device and queue are attached, which XeSS needs to run at all.
        bool Attached() const { return attached.load(); }

        // Render event 7: checks the frame, makes what is missing (DLL, fence, textures, context) and upscales.
        void OnPacket(const void* data);
        // Render event 8: the context and the textures released; the DLL stays loaded.
        void OnRelease();

        // As AmdUpscaler::Status: 1 running, -1 stopped, -2 stopped for a reason that
        // stands while libxess.dll's folder and file are as they were, 0 not yet.
        int Status(char* buffer, int size) const;

        // The XeSS version libxess.dll reports, as "XeSS 3.0.2", into buffer; 0 before
        // the DLL is loaded.
        int Version(char* buffer, int size) const;

    private:
        // Only Get makes it.
        XessUpscaler() = default;

        // libxess.dll loaded and its functions resolved; tried again at most once a second, and only once its
        // folder or file changed.
        bool EnsureRuntimeLocked();
        // The fence shared with D3D11, the command allocators and the list, made once per attachment.
        bool EnsureFenceLocked();
        // The four shared textures, made again where the frame's differ in size or format.
        bool EnsureTexturesLocked(const XessUpscalerPacket& packet);
        // The context made and initialised for the frame's sizes and creation flags, with the preset whose input
        // range holds the render size; a failure stands until those change.
        bool EnsureContextLocked(const XessUpscalerPacket& packet);
        // The frame across: inputs copied on D3D11, XeSS on the D3D12 queue, the result copied back into Unity's
        // output, the two devices ordered by the shared fence.
        bool UpscaleLocked(const XessUpscalerPacket& packet);
        // Blocks until the queue has finished every list handed to it.
        void WaitForListsLocked();
        // The context destroyed and the shared textures released, after the queue has finished.
        void ReleaseLocked();
        // The state and text Status reports; a stop is logged where it changes.
        void SetStatus(int state, const std::string& text);

        // Held by every entry point: Attach and Detach come from the swapchain proxy, the rest from Unity's render thread.
        mutable std::mutex mutex;

        // The swapchain proxy attached, whether it gave a D3D12 device and queue, and its devices.
        const void* owner = nullptr;
        std::atomic<bool> attached{ false };
        Microsoft::WRL::ComPtr<ID3D11Device5> device11;
        Microsoft::WRL::ComPtr<ID3D11DeviceContext4> context11;
        Microsoft::WRL::ComPtr<ID3D12Device> device12;
        Microsoft::WRL::ComPtr<ID3D12CommandQueue> queue;

        // libxess.dll and the functions it must have.
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
        // Whether loading was tried, the folder and file stamp it was tried with, when it may be tried next, and
        // the "XeSS 3.0.2" the DLL reported.
        bool runtimeTried = false;
        std::wstring runtimeSignature;
        ULONGLONG nextRuntimeCheck = 0;
        std::string versionName;

        // The fence both devices share (one value counted up for both), the event to wait on it, and three
        // allocators in turn for the one list, each with the fence value that marks it finished.
        Microsoft::WRL::ComPtr<ID3D12Fence> fence12;
        Microsoft::WRL::ComPtr<ID3D11Fence> fence11;
        HANDLE fenceEvent = nullptr;
        UINT64 fenceValue = 0;
        static constexpr UINT kLists = 3;
        Microsoft::WRL::ComPtr<ID3D12CommandAllocator> allocators[kLists];
        UINT64 listDone[kLists] = {};
        UINT listIndex = 0;
        Microsoft::WRL::ComPtr<ID3D12GraphicsCommandList> list;

        // The frame's textures as both devices see them: Unity's are copied in, the output copied out.
        SharedTexture colour;
        SharedTexture depth;
        SharedTexture motion;
        SharedTexture output;

        // XeSS's context, the packet it was made for, whether making it failed for that packet, and the velocity
        // scale last set on it.
        xess_context_handle_t context = nullptr;
        XessUpscalerPacket created = {};
        bool contextFailed = false;
        float velocityScaleX = 0.0f;
        float velocityScaleY = 0.0f;

        // Frames upscaled since the context was made, and the state last set, read without the status lock.
        uint64_t upscaled = 0;
        int renderState = 0;

        // What Status and Version report, under their own lock: the state, its text, the folder and file stamp a
        // -2 stands for, and the version once loaded.
        mutable std::mutex statusLock;
        int state = 0;
        std::string statusText = "no frame for XeSS yet";
        std::wstring stableSignature;
        std::string shownVersion;
    };
}
