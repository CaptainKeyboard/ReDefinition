#pragma once

// Settings for the proxy, read from ReDefinitionProxy.ini next to the executable
// and re-read while the game runs.
//
// The live settings take effect without a restart: the conventions frame
// generation depends on -- whether the depth is inverted, which way the inputs
// are oriented, which way motion vectors point -- each smear when wrong, and can be
// changed while the game runs.

#include <string>

namespace redefinition
{
    struct ProxyConfig
    {
        // false makes every export a pure pass-through to the system dxgi.dll,
        // exactly as if this file were not there.
        //
        // Read once at load: a swapchain cannot be swapped underneath a running
        // game, and this switch exists so a broken proxy still leaves a game
        // that starts.
        bool enabled = true;

        // The D3D12 swapchain made with ALLOW_TEARING, so a Present without V-Sync
        // is shown at once, as KSP asks: its flags already carry it, and this adds
        // it where they do not. Read once at load, like enabled.
        bool allowTearing = true;

        // Wrap the swapchain Unity asked for and only measure it, presenting
        // through the original D3D11 path. The baseline is taken this way: the
        // same timing code on both sides of the comparison.
        bool measureOnly = false;

        // Frame generation: the swapchain made through Streamline (with
        // dlssFrameGeneration) or FidelityFX instead of DXGI directly. Off, the
        // swapchain is Streamline's only for DLSS Ray Reconstruction. Read once at
        // creation, for the same reason as enabled. On by default: the player
        // package ships AMD's runtime (Config.cpp).
        bool frameGeneration = true;

        // DLSS frame generation where NVIDIA's Streamline 2.14.1 is there and the
        // GPU runs it, FSR's otherwise (Streamline.h). Read at creation, like
        // frameGeneration: which swapchain presents is decided once.
        bool dlssFrameGeneration = true;

        // ---- live from here down, but for the folders, vendorDiagnostics and
        // streamlineImgui, which apply when what they serve starts ----

        // Keep the UI out of the interpolated frames by handing FSR a HUD-less
        // snapshot: the backbuffer copied after the last scene camera and before
        // the first UI camera, so it differs from the presented frame in the UI
        // and nowhere else -- which is what FSR's UI detection needs. Off, the UI
        // is interpolated with the scene and smears with it.
        bool hudLessColour = true;

        // Tell FSR the depth buffer runs [1..0], which Unity's on Direct3D
        // does (SystemInfo.usesReversedZBuffer). The bundled upscaler sets the
        // same flag for itself (Fsr3Upscaler.cs, CreateContext).
        //
        // Swapping near and far does not express the same: FSR's own source takes
        // min(near, far) and max(near, far) -- "make sure it has no impact if
        // near and far plane values are swapped in dispatch params, the flags
        // 'inverted' and 'infinite' will decide what transform to use" -- so
        // the flag is the only thing that decides, and without it FSR reads the
        // nearest geometry as the farthest: the vessel smears outward into the
        // world. Changing this rebuilds the context.
        bool fgDepthInverted = true;

        // Negate the motion vector scale. Unity's vectors point opposite to
        // FSR's and the upscaler already compensates, so this is normally off.
        bool fgNegateMotionScale = false;

        // Flip depth and motion vectors vertically before handing them to FSR.
        //
        // Unity renders into RenderTextures upside down on Direct3D (its own
        // documentation), and depthCopy and motionVectors are Blits from one
        // RenderTexture into another, which keeps that. The backbuffer FSR
        // interpolates is not upside down: in flight the top tenth of the depth
        // texture reads nearer than the bottom tenth while the top of the
        // presented frame is sky. The log's motion vector check tries both
        // orientations and both signs and reports the error of each.
        bool fgFlipInputs = true;

        // The HUD-less copy is a Blit *from the screen* into a RenderTexture,
        // and there Unity compensates: in flight the copy differs from the
        // presented frame by 15.8 % direct, the UI, and by 99.9 % mirrored. So it
        // is not flipped; the HUD-less check measures both orientations.
        bool fgFlipHudLess = false;

        // Pass the camera position and basis vectors to FSR's frame generation,
        // whose API documents them as required. Off, they are zero. DLSS frame
        // generation always gets them.
        bool fgCameraBasis = true;

        // Force VSync for as long as FSR frame generation is switched on and has a
        // context (PacedByVSyncLocked), not only on the frames it interpolates,
        // whatever the game asked for: a sync interval of 0 becomes 1, a higher one
        // the game asks for stays. Off by default: KSP's own V-Sync setting applies. AMD's
        // swapchain derives its pacing from the sync interval of the game's
        // Present -- with VSync it "will slow down the application to half refresh
        // rate, so every interpolated and real frame gets displayed for one
        // refresh"; without it, in a window, "not all frames generated will get
        // displayed". With G-Sync or FreeSync as well: AMD, "within half the VRR
        // window supported by the monitor ... enabling VSync will result in optimal
        // pacing", and in full screen without VSync "Tearing artifacts may appear
        // even with VRR enabled" (frame-interpolation-swap-chain.md).
        bool fgVSync = false;

        // While frame generation generates and a frame is presented without
        // VSync, hold the rendered rate slightly below half the monitor's
        // refresh rate. AMD: "The application should ensure that the rendered
        // frame rate is slightly below half the desired output frame rate"; and
        // for windowed mode, where nothing tears, "generating more frames than
        // can get presented is a waste of resources, so it is recommended to cap
        // the frame rate to half the monitor refresh rate". In full screen it
        // keeps real and generated frames inside a VRR window, but frames can
        // still tear without VSync (frame-interpolation-swap-chain.md). Off by
        // default. KSP's own frame limit is Unity's software timer (33/58 ms
        // measured around a cap of 30).
        bool fgHalfRefreshLimit = false;

        // Frame generation's optical flow and interpolation on a compute queue of
        // FSR's swapchain instead of the game's queue. AMD: with it "the Optical
        // Flow and Frame Generation workloads will run on an asynchronous compute
        // queue and overlap with workloads of the next frame on the main game
        // graphics queue. This can improve performance depending on the GPU and
        // workloads"; without it, "a lower memory overhead", and "It is strongly
        // advised to profile" (frame-interpolation-api.md). Off by default for
        // that reason. The inputs are kept in two copies in turn, as AMD then
        // requires (FrameGeneration.h). Changing this rebuilds the context.
        bool fgAsyncWorkloads = false;

        // DLSS frame generation's minimum depth difference between two objects, in
        // metres (FrameGenerationDlss.cpp). Streamline takes it in linear depth,
        // 1 / depth with inverted depth, which is about the distance over the near
        // plane; its default of 40 is 8.4 m at KSP's near plane in flight, 0.21 m
        // (FlightCamera, decompiled). 0 keeps Streamline's default. On the runway,
        // 1 m and the default made no measurable difference.
        float fgObjectSeparationMetres = 0.0f;

        // Frame pacing. FSR's swapchain owns the timing of generated frames, and
        // these are its documented defaults. Hybrid spin, which AMD's header calls
        // "Less precise, but power saving", is a setting.
        float fgPacingSafetyMarginMs = 0.1f;
        float fgPacingVarianceFactor = 0.1f;
        bool fgPacingHybridSpin = false;
        int fgPacingHybridSpinTime = 2;
        bool fgPacingWaitOnFence = false;

        // The rendered frame rate, averaged over two seconds, below which frame
        // generation stays off -- below nine tenths of it, until above nineteen
        // twentieths again (FrameGeneration::NoteFrameRateLocked): AMD's "it may be
        // preferable to ... [disable] FSR Frame Generation ... if the frame rate
        // consistently drops below that threshold", with the hysteresis AMD describes
        // (frame-interpolation-swap-chain.md, Minimum Framerate). -1: the rate FSR's
        // provider was designed for (60 for 3.1, 30 for 4), 30 for DLSS-G, for which
        // NVIDIA names none; 0: never off for this.
        float fgMinimumFps = -1.0f;

        // Seconds between frame time reports in the log.
        int reportSeconds = 10;

        // Log every value handed to FSR, this often in seconds. Zero switches it
        // off.
        int logInputsSeconds = 0;

        // AMD's debug checker on frame generation and the upscaler ("validate
        // application supplied inputs", frame-interpolation-api.md, Debug Checker),
        // its messages into the log. For development.
        bool vendorDiagnostics = false;

        // Streamline's ImGUI plugin loaded, which NVIDIA's development DLLs show
        // ("Use the sl.imgui plugin to validate that inputs ... are correct",
        // ProgrammingGuideDLSS_G.md 15.0). For development: with it the GPU device
        // was removed four seconds after the first frame, while KSP loaded. With
        // NVIDIA's development DLLs in streamlineDirectory and dlssDirectory, DLSS-G's
        // and DLSS's debug visualizations answer their hotkeys without it.
        bool streamlineImgui = false;

        // DLSS-G presents only the frames it generates (DLSSGFlags::
        // eShowOnlyInterpolatedFrame): what interpolation makes, apart from the
        // rendered frames.
        bool fgShowOnlyGenerated = false;

        // The raytracing and Ray Reconstruction on a Direct3D 12 queue of their own
        // instead of the swapchain's, where the present and frame generation run: their
        // work does not wait behind frame generation's (Raytracing::Attach). Read when a
        // swapchain is created.
        bool rtOwnQueue = false;

        // For measuring, 0 by default: what the cut-out surfaces' tests cost the rays.
        // 1 decides their hits without reading their textures, 2 takes every surface as
        // opaque (RtContract.h, RT_FLAG_CUTOUT_PLAIN and RT_FLAG_CUTOUT_OPAQUE). The
        // picture is wrong with either. Read when a swapchain is created.
        int rtCutoutTest = 0;

        // What of the sun's light a cut-out surface -- grass, ferns -- lets through to
        // what lies behind it, 0 to 1 (RtCommon.hlsli, SunUnoccluded). Read when a
        // swapchain is created.
        float rtFoliageTransmission = 0.0f;

        // Where a player's nvngx_dlss.dll lies if not next to KSP_x64.exe, which
        // NGX searches anyway (Dlss.h). Read when DLSS starts on a device, on
        // Unity's render thread, through DlssDirectory().
        wchar_t dlssDirectory[260] = {};

        // Where a player's amd_fidelityfx_upscaler_dx12.dll lies if not next to
        // KSP_x64.exe (AmdUpscaler.h). Read when the rig first asks for it,
        // through AmdUpscalerDirectory().
        wchar_t amdUpscalerDirectory[260] = {};

        // Where NVIDIA's Streamline DLLs lie -- sl.interposer.dll, its plugins and
        // nvngx_dlssg.dll -- if not next to KSP_x64.exe (Streamline.h). Read from
        // the file when a swapchain is created, on Unity's render thread, through
        // StreamlineDirectory().
        wchar_t streamlineDirectory[260] = {};
    };

    const ProxyConfig& Config();
    void LoadConfig();

    // The two folders for threads other than the present path, which re-reads
    // the ini: copied under the lock the settings are written under.
    std::wstring DlssDirectory();
    std::wstring AmdUpscalerDirectory();
    std::wstring StreamlineDirectory();

    // Re-reads the file when it has changed on disk; returns whether it did.
    // rebuild: whether a setting changed that the frame generation context has to
    // be made anew for.
    bool ReloadConfigIfChanged(bool& rebuild);
}
