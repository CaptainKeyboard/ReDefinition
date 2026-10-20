#pragma once

#include <windows.h>

#include <d3d12.h>
#include <wrl/client.h>

#include <cstdint>
#include <functional>
#include <mutex>
#include <string>

#include "NgxCore.h"

namespace redefinition
{
    // One frame for DLSS Ray Reconstruction through NGX: the proxy's Direct3D 12
    // copies of the frame's textures and the guides, and the values Streamline's
    // sl.dlss_d would hand NGX for the same RtReconstructPacket.
    struct NgxRrFrame
    {
        ID3D12Resource* colour;
        ID3D12Resource* output;
        ID3D12Resource* depth;
        ID3D12Resource* motion;
        ID3D12Resource* albedo;
        ID3D12Resource* specularAlbedo;
        // The normals with the roughness in w, NVSDK_NGX_DLSS_Roughness_Mode_Packed.
        ID3D12Resource* normalRoughness;
        ID3D12Resource* specularHitDistance;
        uint32_t renderWidth;
        uint32_t renderHeight;
        uint32_t outputWidth;
        uint32_t outputHeight;
        // NVSDK_NGX_PerfQuality_Value: 0 performance, 1 balanced, 2 quality, 3 ultra
        // performance, 4 ultra quality, 5 DLAA.
        int32_t quality;
        // NVSDK_NGX_RayReconstruction_Hint_Render_Preset: 0 default, 4 to 6 D to F.
        uint32_t preset;
        // In render pixels.
        float jitterX;
        float jitterY;
        // What turns the motion vectors into render pixels.
        float motionScaleX;
        float motionScaleY;
        bool reset;
        // Row-major: "All matrices are Row Major Order and use left multiplication"
        // (DLSS-RR Integration Guide 3.4.9).
        float worldToView[16];
        float viewToClip[16];
    };

    // DLSS Ray Reconstruction through NVIDIA's NGX on the proxy's Direct3D 12 device,
    // where the presenting swapchain is not Streamline's -- beside FSR's or XeSS's
    // frame generation -- and Streamline is not set up on the device (rrPath,
    // Config.h). NGX's feature is RayReconstruction ("Set FeatureID to
    // NVSDK_NGX_Feature_RayReconstruction", DLSS-RR Integration Guide 2.1), its DLL
    // nvngx_dlssd.dll, which the core finds in streamlineDirectory, dlssDirectory or
    // next to KSP_x64.exe. Under DLSS-G and with frame generation off, Streamline's
    // sl.dlss_d runs it instead (Streamline.h), and NGX is started on the device by
    // Streamline: "Init once per device" (nvsdk_ngx.h) holds for one of the two.
    //
    // The NGX calls come from Unity's render thread under the raytracing's lock
    // (RaytracingReconstruct.cpp) and hold NgxMutex, which DLSS Super Resolution's on
    // Unity's Direct3D 11 device take too: "The NGX API is not thread safe" (DLSS
    // Programming Guide 5.2.4). Ready is asked from the main thread and makes no
    // NGX call.
    class DlssRayReconstruction
    {
    public:
        static DlssRayReconstruction& Get();

        // Whether a frame may go to NGX: false where the driver has no NGX core, or a
        // failure stands -- no RTX GPU, a driver too old, no usable nvngx_dlssd.dll --
        // until what NGX searches changes or the device goes. Why, either way.
        bool Ready(std::string& why);

        // NGX started on device where it is not yet, the feature made for the frame's
        // sizes, mode and preset where they changed -- after idle has let the lists
        // that evaluated the previous one finish -- and the evaluation recorded into
        // list, which is "open and recording" and runs on a direct queue (nvsdk_ngx.h,
        // NVSDK_NGX_D3D12_CreateFeature and _EvaluateFeature). The inputs in
        // NON_PIXEL_SHADER_RESOURCE, the output in UNORDERED_ACCESS, the caller's
        // barriers. False with the reason in problem.
        bool Evaluate(ID3D12Device* device, ID3D12GraphicsCommandList* list, const NgxRrFrame& frame,
                      const std::function<void()>& idle, std::string& problem);

        // The feature released, once no list that evaluated it is pending on the GPU
        // (render event 32): whether there was one. A failure that need not stand is
        // tried again after it.
        bool ReleaseFeature();

        // The device goes: the feature, the parameter map and NGX's instance on it,
        // once the lists that used them finished (NVSDK_NGX_D3D12_Shutdown, not
        // Shutdown1: DlssRayReconstruction.cpp says why).
        void Shutdown(ID3D12Device* device);

    private:
        DlssRayReconstruction() = default;

        struct Settings
        {
            uint32_t renderWidth = 0;
            uint32_t renderHeight = 0;
            uint32_t outputWidth = 0;
            uint32_t outputHeight = 0;
            int32_t quality = -1;
            uint32_t preset = 0;
        };

        bool EnsureInitialisedLocked(ID3D12Device* device, std::string& problem);
        bool EnsureFeatureLocked(ID3D12GraphicsCommandList* list, const NgxRrFrame& frame,
                                 const std::function<void()>& idle, std::string& problem);
        bool ReleaseFeatureLocked();
        void ShutdownLocked();
        // A failure that stands, with what NGX's search looked like then.
        void FailStable(const std::string& why);
        std::wstring SearchSignature() const;

        Microsoft::WRL::ComPtr<ID3D12Device> device;
        NgxParameters* parameters = nullptr;
        bool parametersOwned = false;
        void* feature = nullptr;
        NgxSearch search;
        // What NGX's start on the device was handed, alive for as long as NGX runs on it.
        NgxCommonInfo commonInfo = {};
        std::wstring dataPath;
        Settings created;
        // The settings whose feature could not be made, and the device NGX's start
        // failed on, with why: not tried again before ReleaseFeature.
        Settings failed;
        bool hasFailed = false;
        std::string failedWhy;
        ID3D12Device* initFailed = nullptr;
        std::string initFailedWhy;
        uint64_t evaluated = 0;
        bool loggedEvaluateFailure = false;
        // The feature was just made, its history empty: its first evaluation resets.
        bool resetFeature = false;
        // The matrices NGX is handed pointers to, alive past the evaluation.
        float worldToView[16] = {};
        float viewToClip[16] = {};

        // Shared with Ready on the main thread.
        mutable std::mutex stateMutex;
        std::string stableWhy;
        std::wstring stableSearch;
        ULONGLONG nextSearchCheck = 0;
    };
}
