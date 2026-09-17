#pragma once

#include <windows.h>

#include <d3d11.h>
#include <wrl/client.h>

#include <cstdint>
#include <mutex>
#include <string>
#include <vector>

namespace ksp
{
    // One frame for DLSS, written by the managed side and sent through Unity's
    // render thread like the frame generation packet (FrameGeneration.h): the
    // textures are Unity's own RenderTextures, which DLSS reads and writes on
    // Unity's D3D11 device. A layout agreement across P/Invoke, checked by size
    // and magic on every packet and by KspDlssPacketSize before the first.
    struct DlssPacket
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
        // NGX's quality values: 0 performance, 1 balanced, 2 quality,
        // 3 ultra performance, 4 ultra quality, 5 DLAA.
        int32_t quality;
        // NGX's render preset hint: 0 lets DLSS choose, otherwise the preset's
        // number (J 10, K 11, L 12, M 13).
        uint32_t preset;
        uint32_t flags;
        float jitterX;
        float jitterY;
        float motionVectorScaleX;
        float motionVectorScaleY;
    };

    constexpr uint32_t kDlssPacketMagic = 0x4B535044u;   // 'KSPD'

    constexpr uint32_t kDlssHdr = 1u << 0;
    constexpr uint32_t kDlssDepthInverted = 1u << 1;
    constexpr uint32_t kDlssAutoExposure = 1u << 2;
    constexpr uint32_t kDlssReset = 1u << 3;

    // A question for the render sizes of every mode at an output size, sent the
    // same way before the rig is built (Dlss::OnSizeQuery). The texture is any of
    // Unity's, for its device.
    struct DlssSizeQuery
    {
        uint32_t size;
        uint32_t magic;
        void* texture;
        uint32_t outputWidth;
        uint32_t outputHeight;
    };

    constexpr uint32_t kDlssSizeQueryMagic = 0x4B535051u;   // 'KSPQ'

    // DLSS Super Resolution through NVIDIA's NGX, on Unity's D3D11 device.
    //
    // NGX's core is part of the NVIDIA driver; the DLSS network is
    // nvngx_dlss.dll, which the player downloads from NVIDIA's release
    // (NvidiaDownloader) next to KSP_x64.exe, or puts into dlssDirectory
    // (Config.h). The core finds it there.
    //
    // Everything here runs on Unity's render thread, from the render event of
    // the upscaler's dispatch buffer, which Unity runs after the frame's drawing
    // has been submitted (measured in a Unity 2019.4 player with KSP's native
    // graphics jobs). "The NGX API is not thread safe", and for D3D11 "the NGX
    // API preserves the state of the immediate D3D11 context" (NVIDIA's DLSS
    // Programming Guide, 5.2.4 and 5.2.5), so Unity's state survives the call.
    // Only Status and RenderSizes are read from the main thread.
    class Dlss
    {
    public:
        static Dlss& Get();

        // Event 2: initialise NGX on the textures' device if needed, create the
        // feature for the packet's sizes and mode if they changed, evaluate.
        void OnPacket(const void* data);

        // Event 3: the rig is gone; the feature goes with it.
        void OnRelease();

        // Event 6: the render sizes of every mode at the query's output size.
        void OnSizeQuery(const void* data);

        // 1 while frames are evaluated, -1 once something stopped it, -2 when
        // that stands until the GPU, driver or DLL changes (failedStable), 0
        // before the first packet; the text says what and why.
        int Status(char* buffer, int size) const;

        // The render sizes DLSS asked for at an output size and quality value:
        // the optimal one, and the range it accepts. False until a feature was
        // made for them; the last few are kept.
        bool RenderSizes(uint32_t outputWidth, uint32_t outputHeight, int32_t quality, uint32_t optimal[2],
                         uint32_t minimum[2], uint32_t maximum[2]) const;

    private:
        Dlss() = default;

        struct RenderSizeEntry
        {
            uint32_t outputWidth;
            uint32_t outputHeight;
            int32_t quality;
            uint32_t optimal[2];
            uint32_t minimum[2];
            uint32_t maximum[2];
        };
        static constexpr size_t kRenderSizeEntries = 8;

        bool QueryRenderSize(uint32_t outputWidth, uint32_t outputHeight, int32_t quality, RenderSizeEntry& entry,
                             std::string& error);
        void StoreRenderSize(const RenderSizeEntry& entry);
        bool EnsureInitialised(ID3D11Device* device);
        bool EnsureFeature(ID3D11DeviceContext* context, const DlssPacket& packet);
        bool Evaluate(ID3D11DeviceContext* context, const DlssPacket& packet);
        void ReleaseFeature();
        void Shutdown();
        void SetStatus(int state, const std::string& text);

        HMODULE core = nullptr;
        Microsoft::WRL::ComPtr<ID3D11Device> device;
        // The device NGX's start last failed on, and what its search for
        // nvngx_dlss.dll looked like then: tried again only once that changed.
        ID3D11Device* failedDevice = nullptr;
        std::wstring failedSearch;
        // Whether that failure stands until the search changes -- no RTX GPU, an
        // old driver, no usable DLL -- or a new rig tries again.
        bool failedStable = false;
        // When the search may be looked at again after such a failure.
        ULONGLONG nextSearchCheck = 0;
        void* parameters = nullptr;
        bool parametersOwned = false;
        void* feature = nullptr;

        // The settings the feature was created with, and those that last
        // failed, so a failure is not retried every frame.
        DlssPacket created = {};
        DlssPacket failed = {};
        bool hasFailed = false;

        std::vector<std::wstring> searchPaths;
        std::vector<wchar_t*> searchPathPointers;
        uint64_t evaluated = 0;
        // The state last set, as the render thread knows it.
        int renderState = 0;
        bool loggedEvaluateFailure = false;

        mutable std::mutex mutex;
        int state = 0;
        std::string statusText = "no DLSS frame yet";
        // NGX's search when a -2 was set (SearchSignature).
        std::wstring stableSearch;
        RenderSizeEntry sizes[kRenderSizeEntries] = {};
        size_t nextSize = 0;
    };
}
