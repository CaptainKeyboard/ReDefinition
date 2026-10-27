#pragma once

#include <windows.h>

#include <d3d11_4.h>
#include <d3d12.h>
#include <wrl/client.h>

#include <XeSS/xess_d3d12.h>

#include "AmdUpscaler.h"
#include "FrameGeneration.h"
#include "UpscalerRig.h"

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
    class XessUpscaler : public UpscalerRig
    {
    public:
        // The one instance, made on first use and never destroyed.
        static XessUpscaler& Get();

        // Attach, Detach, Attached and Status are the rig's (UpscalerRig.h). Status stops with -2 for a reason that
        // stands while libxess.dll's folder and file are as they were.

        // Render event 7: checks the frame, makes what is missing (DLL, fence, textures, context) and upscales.
        void OnPacket(const void* data);
        // Render event 8: the context and the textures released; the DLL stays loaded.
        void OnRelease();

        // The XeSS version libxess.dll reports, as "XeSS 3.0.2", into buffer; 0 before
        // the DLL is loaded.
        int Version(char* buffer, int size) const;

    private:
        // Only Get makes it.
        XessUpscaler() : UpscalerRig("XeSS: ", "no frame for XeSS yet") {}

        // libxess.dll loaded and its functions resolved; tried again at most once a second, and only once its
        // folder or file changed.
        bool EnsureRuntimeLocked();
        // The context made and initialised for the frame's sizes and creation flags, with the preset whose input
        // range holds the render size; a failure stands until those change.
        bool EnsureContextLocked(const XessUpscalerPacket& packet);
        // The frame across: inputs copied on D3D11, XeSS on the D3D12 queue, the result copied back into Unity's
        // output, the two devices ordered by the shared fence.
        bool UpscaleLocked(const XessUpscalerPacket& packet);
        // The context destroyed and the shared textures released, after the queue has finished.
        void ReleaseLocked() override;
        // The folder and the stamp of libxess.dll in it.
        std::wstring RuntimeSignature() const override;

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
        // The "XeSS 3.0.2" the DLL reported.
        std::string versionName;

        // XeSS's context, the packet it was made for, whether making it failed for that packet, and the velocity
        // scale last set on it.
        xess_context_handle_t context = nullptr;
        XessUpscalerPacket created = {};
        bool contextFailed = false;
        float velocityScaleX = 0.0f;
        float velocityScaleY = 0.0f;

        // What Version reports, under the rig's statusLock: the version once loaded.
        std::string shownVersion;
    };
}
