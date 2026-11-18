#pragma once

#include "Dlss.h"
#include "NgxCore.h"
#include "UpscalerRig.h"

#include <string>

namespace redefinition
{
    // DLSS Super Resolution through NGX on a Direct3D 12 device of its own, made on the GPU Unity's D3D11 device
    // renders on. Unity's textures are handed over the way AMD's upscaler and XeSS take them (UpscalerRig): copied
    // into textures both devices share, evaluated on this device's queue, the result copied back, a shared fence
    // ordering the two devices.
    //
    // Why not on Unity's D3D11 device (Dlss.cpp): NGX's D3D11 evaluation writes to an address no resource holds
    // and the driver resets the GPU. K-T2 2026-11-18 put it inside NGX's own work (a fence before and after the
    // evaluation), with inputs that are finite and in range, with nvngx_dlss.dll 310.9.16 and 310.7.0 alike; FSR
    // at the same size never did. The user chose the D3D12 path over the D3D11 one.
    //
    // Why a device of its own and not the swapchain proxy's: under DLSS frame generation Streamline holds NGX on
    // that device (RaytracingReconstruct.cpp, ChooseRoute), and Streamline's Super Resolution plugin is not shipped.
    // This device does not depend on which frame generation presents, or whether any does.
    //
    // NGX is started on the device once and never shut down: NVSDK_NGX_D3D12_Shutdown ends NGX on every D3D12
    // device of the process, Streamline's included, and Shutdown1 ended the process in a measurement
    // (DlssRayReconstruction.cpp). NgxD3d12KeepRunning tells Ray Reconstruction not to call it either.
    //
    // Everything runs on Unity's render thread, from Dlss::OnPacket and Dlss::OnRelease; every NGX call holds
    // NgxMutex. Status is read from the main thread.
    class DlssD3d12 : public UpscalerRig
    {
    public:
        static DlssD3d12& Get();

        // Whether Super Resolution runs here: always, but for a test start's -redefinitionDlssD3d11, which keeps the D3D11
        // path (Dlss.cpp) for comparison and for its diagnostics.
        static bool On();

        // A frame: the device made on the textures' GPU where needed, NGX started on it, the feature made for the
        // packet's sizes, mode and preset, the frame evaluated. Status says how it went.
        void OnPacket(const DlssPacket& packet);

        // The render size DLSS asks for at an output size and quality value, and the range it accepts (guide 5.2.8),
        // from NGX on this device; Unity's device is any of its textures'. False with the reason in error.
        bool RenderSize(ID3D11Device* unity, uint32_t outputWidth, uint32_t outputHeight, int32_t quality, uint32_t optimal[2],
                        uint32_t minimum[2], uint32_t maximum[2], std::string& error);

        // The rig is gone; the feature goes with it. NGX and the device stay for the next rig.
        void OnRelease();

    protected:
        void ReleaseLocked() override;
        std::wstring RuntimeSignature() const override;

    private:
        DlssD3d12();

        // The device and its direct queue on the GPU of Unity's device, made once per Unity device.
        bool EnsureDeviceLocked(ID3D11Device* unity);
        // NGX started on the device with the folders for nvngx_dlss.dll and its capability checked; a failure
        // stands until what NGX searches changes (RuntimeDueLocked).
        bool EnsureNgxLocked();
        // The optimal render size and its range for an output size and quality, from the capability map.
        bool QuerySizeLocked(uint32_t outputWidth, uint32_t outputHeight, int32_t quality, unsigned int optimal[2], unsigned int minimum[2],
                             unsigned int maximum[2], std::string& error);
        // The feature for the packet's sizes, quality, preset and creation flags, at the render size DLSS accepts.
        bool EnsureFeatureLocked(const DlssPacket& packet);
        // The frame's inputs across, the evaluation on the list, the output back.
        bool EvaluateLocked(const DlssPacket& packet);
        // The feature let go after the queue has finished with it.
        void ReleaseFeatureLocked();

        // The driver's D3D12 exports, the search NGX was handed, and the device NGX runs on with its map.
        NgxD3d12Api api;
        NgxSearch search;
        NgxCommonInfo commonInfo = {};
        std::wstring dataPath;
        ID3D12Device* ngxDevice = nullptr;
        NgxParameters* parameters = nullptr;
        bool parametersOwned = false;

        // The feature, the packet it was made for, and the one it failed for (not retried every frame).
        void* feature = nullptr;
        DlssPacket created = {};
        DlssPacket failed = {};
        bool hasFailed = false;
        // A feature just made has no history, whatever the frame says (as DlssRayReconstruction does).
        bool resetFeature = false;
        bool loggedEvaluateFailure = false;
    };
}
