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
    // Every setting of the ini's [Proxy] section, with its default.
    //
    // RaytracingScene.cpp, BuildMeshesLocked builds the meshes' acceleration structures after
    // NVIDIA's "Best Practices for Using NVIDIA RTX Ray Tracing (Updated)" and the DXR spec
    // (DirectX-Specs, Raytracing.md). The rtBlas* settings, rtTlasFastTrace and rtMegaGeometry
    // each apply to the structures built after it changes, and all are live. The measurements
    // come from the raytracing harness's
    // vessel (RTX A4500, 2026-10-20): 150 parts of 54 272 triangles, as a flight's first frames
    // hand them over, and 600 of 244 404 at once. A pair of figures is the slowest frame's GPU
    // time to its meshes built or the CPU's time recording them, for the 150 and the 600 parts.
    // The picture is the same in every variant. Mesh by mesh in buffers of their own, the
    // baseline takes 21.6 and 172 ms of the GPU and 87 and 715 ms of the CPU. Against the
    // defaults, rtMegaGeometry 1 took the slowest frame to 0.32 and 0.85 ms of the GPU against 0.54
    // and 1.17. Its trace was slower in every frame after, 0.46 and 0.53 ms against 0.41 and 0.46,
    // and its structures 3.44 and 14.83 MB against 1.62 and 7.11.
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

        // Frame generation: the swapchain made through Streamline, Intel's XeSS-FG
        // or FidelityFX (frameGenerationTechnique) instead of DXGI directly. Off,
        // the swapchain is Streamline's only for DLSS Ray Reconstruction. Read once
        // at creation, for the same reason as enabled. On by default: the player
        // package ships AMD's runtime (Config.cpp).
        bool frameGeneration = true;

        // Which frame generation the swapchain is made for (kFgTechnique*), taken when the swapchain
        // is created, like the folders: which swapchain presents is decided once per swapchain.
        // The value auto picks DLSS-G where NVIDIA's Streamline 2.14.16 is there and the GPU runs it
        // (Streamline.h). It picks XeSS-FG on an Intel GPU where the player's libxess_fg.dll and
        // libxell.dll are there (XessFg.h), and FSR's otherwise. The values dlss and xess ask for
        // theirs on any GPU that runs it, and for FSR's where it does not. The value fsr asks for
        // FSR's always.
        int frameGenerationTechnique = 0;

        // How DLSS Ray Reconstruction runs (kRrPath*), taken when the swapchain is created, like
        // frameGenerationTechnique. The value auto runs it through Streamline where its swapchain
        // presents, which is DLSS-G or frame generation off. Otherwise it runs through NGX directly
        // on the proxy's Direct3D 12 device, beside FSR's or XeSS's frame generation
        // (DlssRayReconstruction.h). The value streamline runs it through Streamline only, so none
        // beside FSR's or XeSS's frame generation. The value ngx runs it through NGX only. With frame
        // generation off the swapchain is then not Streamline's, and under DLSS-G, whose Streamline
        // holds NGX on the device, there is none.
        int rrPath = 0;

        // ---- live from here down, but for the folders, rtOwnQueue, vendorDiagnostics,
        // streamlineImgui, xell, xellLowLatencyBoost, xessFgUiMode, xessFgOwnWaitable,
        // xessFgAllowOld, rrPath and xessFgFrames' most, which apply when what they serve
        // starts ----

        // Keep the UI out of the interpolated frames by handing FSR a HUD-less snapshot. It is the
        // backbuffer copied after the last scene camera and before the first UI camera. So it differs
        // from the presented frame in the UI and nowhere else, which is what FSR's UI detection
        // needs. The KSP log names the cameras it is taken after and every camera drawn later. Off,
        // the UI is interpolated with the scene and smears with it.
        bool hudLessColour = true;

        // Tell FSR the depth buffer runs [1..0], which Unity's on Direct3D does
        // (SystemInfo.usesReversedZBuffer). The bundled upscaler sets the same flag for itself
        // (Fsr3Upscaler.cs, CreateContext). Swapping near and far is no substitute. FSR's own source
        // takes min(near, far) and max(near, far). It says "make sure it has no impact if near and far
        // plane values are swapped in dispatch params, the flags 'inverted' and 'infinite' will decide
        // what transform to use". Without the flag FSR reads the nearest geometry as the farthest.
        // Changing this rebuilds the context. XeSS-FG takes it when its swapchain is made
        // (xefg::kInitInvertedDepth).
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

        // Force VSync for as long as FSR frame generation is switched on and has a context
        // (PacedByVSyncLocked), not only on the frames it interpolates, whatever the game asked for.
        // A sync interval of 0 becomes 1, and a higher one the game asks for stays. Off by default:
        // KSP's own V-Sync setting applies. FrameGeneration.PacedByVSyncLocked says how AMD's swapchain
        // paces. With VRR, AMD advises running "within half the VRR window supported by the monitor
        // ... enabling VSync will result in optimal pacing" (frame-interpolation-swap-chain.md).
        bool fgVSync = false;

        // While frame generation generates a frame presented without VSync, hold the rendered rate
        // slightly below half the monitor's refresh rate, as AMD advises. For windowed mode AMD adds
        // "generating more frames than can get presented is a waste of resources, so it is
        // recommended to cap the frame rate to half the monitor refresh rate". In full screen it
        // keeps real and generated frames inside a VRR window, but frames can still tear without
        // VSync (frame-interpolation-swap-chain.md). KSP's own frame limit is
        // Unity's software timer (33/58 ms measured around a cap of 30). Under XeSS-FG the limit is
        // XeLL's (xellLimitLive).
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
        // plane. Its default of 40 comes to 8.4 m at KSP's near plane in flight, 0.21 m
        // (FlightCamera, decompiled). 0 keeps Streamline's default. On the runway,
        // 1 m and the default made no measurable difference.
        float fgObjectSeparationMetres = 0.0f;

        // DLSS-G's mode (kDlssgMode*, FrameGenerationDlss.cpp). The value on is a fixed multiplier,
        // as many generated frames as the display and the GPU allow. The value dynamic is Dynamic
        // Multi Frame Generation, DLSSGMode::eDynamic: Streamline "automatically adjusts the number
        // of generated frames to align the output frame rate with the display's refresh rate or a
        // user-defined target" (ProgrammingGuideDLSS_G.md 6.3). It runs where
        // DLSSGState::bIsDynamicMFGSupported says so, and the fixed multiplier stays where not. The
        // log's "DLSS frame generation: dynamic multi frame generation" line says what Streamline
        // reports, once per swapchain.
        int dlssgMode = 0;

        // The generated frames DLSS-G is asked for per rendered frame, 1 for 2x and so on, where
        // the mod has not asked for a multiplier itself (KspFgSetMultiplier). The value 0 is as many
        // as the GPU, the package's MaxGeneratedFrames and the display allow. At most it is what
        // DLSSGState::numFramesToGenerateMax says.
        int dlssgFrames = 0;

        // Dynamic Multi Frame Generation's target output frame rate, in frames a second;
        // 0 targets the monitor's refresh rate. Streamline ignores it under V-Sync.
        float dlssgDynamicTargetFps = 0.0f;

        // DLSS-G's UI plane (FrameGenerationUi.cpp). KSP draws its HUD straight onto the
        // backbuffer, so there is a HUD-less colour and no UI plane of the game's own. With 1 the
        // proxy makes the plane from the HUD-less copy and the presented frame. It tags the plane
        // as UI colour and alpha, and turns user interface
        // recomposition on (DLSSGOptions::enableUserInterfaceRecomposition), which interpolates the
        // scene and lays the UI over it after. With 0 the HUD-less colour alone goes in.
        // In the RTX 30/20 package, Preset=C (FG Lite) turns recomposition off whatever is asked
        // (its log's preset_pinned says effective 0).
        bool dlssgUiRecomposition = false;
        // How the plane's alpha is found where the two differ. A difference gives no alpha, only a
        // bound on it, so the plane is an approximation. The value kDlssgUiAlphaBinary is 1 and the
        // presented colour. The value kDlssgUiAlphaMinimal is the least alpha that reproduces the
        // presented frame over the HUD-less one.
        int dlssgUiAlpha = 0;
        // The greatest difference, in levels of 255, that is still no UI.
        float dlssgUiThreshold = 2.0f;

        // Frame pacing. FSR's swapchain owns the timing of generated frames, and
        // these are its documented defaults. Hybrid spin, which AMD's header calls
        // "Less precise, but power saving", is a setting.
        float fgPacingSafetyMarginMs = 0.1f;
        float fgPacingVarianceFactor = 0.1f;
        bool fgPacingHybridSpin = false;
        int fgPacingHybridSpinTime = 2;
        bool fgPacingWaitOnFence = false;

        // The rendered frame rate, averaged over two seconds, below which frame generation stays off.
        // It stays off below nine tenths of the rate, until above nineteen twentieths of it again
        // (FrameGeneration::NoteFrameRateLocked). AMD says "it may be preferable to ... [disable] FSR
        // Frame Generation ... if the frame rate consistently drops below that threshold", with its
        // hysteresis (frame-interpolation-swap-chain.md, Minimum Framerate). The value -1 takes the
        // rate FSR's provider was designed for. It takes 30 for DLSS-G, for
        // which NVIDIA names none, and 40 for XeSS-FG, Intel's "40 FPS minimum"
        // (xess_fg_developer_guide_english.md, Introduction). The value 0 is never off for this.
        float fgMinimumFps = -1.0f;

        // NVIDIA Reflex's low latency mode where Streamline's does not run
        // (LowLatency.h): frame generation off, FSR's, Ray Reconstruction alone. Live.
        // Intel XeLL's on an Intel GPU without XeSS-FG, set when the swapchain is made:
        // XeLL asks that "any GPU activity is finished before xellSetSleepMode is
        // called" (xell_developer_guide_english.md, Integration Checklist). With
        // XeSS-FG, XeLL's is always on: "XeLL must be enabled before enabling frame
        // generation".
        bool lowLatency = true;
        // Intel XeLL on an Intel GPU where XeSS-FG does not run. It is made, with its mode as
        // lowLatency says, or not made at all, so that the latency brake runs as on any other GPU.
        // The guide says "The application can disable XeLL when frame generation is disabled"
        // (xess_fg_developer_guide_english.md, Connect with XeLL). XeSS-FG makes its own always:
        // "XeSS-FG requires a working XeLL integration on all GPUs". Read when the swapchain is made.
        bool xell = true;
        // XeLL's bLowLatencyBoost, where it lowers the latency: "Boost is not supported
        // as of now" (xell.h). Read when the swapchain is made, with XeLL's mode.
        bool xellLowLatencyBoost = false;
        // The latency brake (LowLatency::Brake), and the waits in the proxy it leaves per frame, in
        // milliseconds. Live. On a GPU other than Intel's XeLL lowers latency only while XeSS-FG
        // generates: Intel says "If the condition is not fulfilled then XeLL will not perform
        // Latency reduction". Measured on an RTX A4500 with raytracing Full and 4 ms, the median
        // latency was 57.8 ms without the brake and 48.6 ms with it at the same frame rate. With FSR
        // frame generation it was 60.2 and 45.7 ms, with DLSS-G 72.1 and 57.8 ms.
        bool latencyBrake = true;
        // AMD Anti-Lag 2 (LowLatency.h): 12 on the proxy's Direct3D 12 device, 11 on
        // Unity's Direct3D 11 device, 0 off. Live.
        int antiLag2 = 0;
        float latencyBrakeTargetMs = 4.0f;
        // Which waits the brake counts: 1 the proxy's and the Present, 2 the proxy's
        // alone (a raytracing slot, a frame's allocator). Live.
        int latencyBrakeWaits = 1;
        // The brake under DLSS frame generation as well, before Streamline's Reflex sleep.
        // Live.
        bool latencyBrakeWithDlssg = true;
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
        // eShowOnlyInterpolatedFrame), XeSS-FG likewise
        // (XEFG_SWAPCHAIN_DEBUG_FEATURE_SHOW_ONLY_INTERPOLATION, "called at any time
        // between context creation and destruction"): what interpolation makes,
        // apart from the rendered frames.
        bool fgShowOnlyGenerated = false;

        // XeSS-FG's UI composition. Off, its default, the UI is interpolated with the scene:
        // "Interpolation (default, recommended): rely on the frame generation model to interpolate
        // the UI". On, it is composed onto the generated frames from the HUD-less copy and the
        // backbuffer, the mode chosen "automatically ... based on the resources tagged by the
        // application" (xess_fg_developer_guide_english.md, UI Handling). Live.
        bool xessFgUiComposition = false;

        // XeSS-FG's UI mode, which its composition takes: kXessUiMode*, as
        // xefg_swapchain_ui_mode_t numbers them -- AUTO, "Determine UI composition mode
        // automatically, based on provided inputs: hudless color and UI texture", the
        // default. An init parameter (xefg_swapchain_d3d12_init_params_t::uiMode): read
        // when the swapchain is made. The proxy tags no UI texture.
        int xessFgUiMode = 0;

        // Frames XeSS-FG generates per rendered frame, 1 by default. The guide's requirements say
        // XeSS-FG runs on "any non-Intel GPU supporting Shader Model 6.4", and "On non-Intel GPUs the
        // maximum number of generated frames is one" (xess_fg_developer_guide_english.md,
        // Requirements). On Intel's it generates as many as the swapchain says it supports
        // (xefgSwapChainGetProperties). The most is set when the swapchain is made: "The maximum
        // number of generated frames is set during the proxy swap chain initialization and cannot be
        // updated". Fewer live within it (xefgSwapChainSetNumInterpolatedFrames, Controlling the
        // Number of Generated Frames).
        int xessFgFrames = 1;

        // XeSS-FG's scene change detection, 0 to 1: "A higher threshold value increases
        // the algorithm's sensitivity, causing it to more readily identify scene changes
        // and disable interpolation ... Default value is 0.7"
        // (xefgSwapChainSetSceneChangeThreshold, xefg_swapchain.h). Live.
        float xessFgSceneChangeThreshold = 0.7f;

        // XeSS-FG's debug features, for development, off by default and live: "called at
        // any time between context creation and destruction" (Debug Features).
        // XEFG_SWAPCHAIN_DEBUG_FEATURE_TAG_INTERPOLATED_FRAMES, "show purple boxes and
        // vertical lines on the interpolated frame", and
        // XEFG_SWAPCHAIN_DEBUG_FEATURE_PRESENT_FAILED_INTERPOLATION, "show black frame if
        // the interpolation failed, otherwise show the current back buffer".
        bool xessFgTagInterpolatedFrames = false;
        bool xessFgPresentFailedInterpolation = false;

        // XeSS-FG's robustness switches (FrameGenerationXess.cpp, SwapChainProxyXess.cpp),
        // each the variant of a behaviour OptiScaler's XeSS-FG integration (GPL-3.0) chose
        // differently.
        //
        // xessFgOwnWaitable: which frame latency waitable object KSP gets when it asks for one.
        // The values -1 (auto) and 1 give a semaphore of the proxy's, released after each game
        // Present, which never holds the game back. OptiScaler calls it "swapchain waitable object
        // simulation" and made it for Unity games that stuck while loading, which KSP does with
        // XeSS-FG's own handle. The value 0 gives XeSS-FG's swapchain's own. Read when the swapchain
        // is made.
        int xessFgOwnWaitable = -1;
        // xessFgGraceFrames: the game's frames in a row without inputs (no packet, no depth or
        // motion vectors for the Present, constants XeSS-FG would refuse) that XeSS-FG stays on for
        // before it is switched off. A switch off marks the input history for cleanup. The frames
        // after it come with XEFG_SWAPCHAIN_RESULT_WARNING_TOO_FEW_FRAMES ("frame generation will
        // take an extra frame to activate", xess_fg_developer_guide_english.md, Frame Constants). A
        // frame without inputs is otherwise only presented without generated frames. 0: off at the
        // first. Live.
        int xessFgGraceFrames = 3;
        // xessFgBadConstants: what a frame's constants that XeSS-FG would refuse get. They are a
        // motion vector scale of 0 or not a number, or a projection from a near plane, far plane or
        // field of view that is not a positive number. The value skip hands the frame no inputs and
        // counts it against xessFgGraceFrames, and nothing locks XeSS-FG out. The value fix makes a
        // scale of 0 or not a number 1 (OptiScaler's), and a projection that is wrong still skips.
        // The value pass hands them over as they are, and XeSS-FG's refusal switches it off for two
        // seconds. Live.
        int xessFgBadConstants = 0;
        // xessFgAllowOld: use a libxess_fg.dll older than 1.2.2 (XeSS SDK 2.1.1). The proxy tags
        // resources without a command list and with the validity until the next Present, which those
        // versions refuse (OptiScaler: "XeFG docs lie and cmd list is technically required", Intel's
        // issues 45, 47, 48 and 50). They are refused with a line in the log, and FSR's frame
        // generation runs instead. Read when the swapchain is made.
        bool xessFgAllowOld = false;
        // xessFgResetOnChange: XeSS-FG off for one Present and on again when the UI
        // composition, the generated frames or the sync interval change while it
        // generates (OptiScaler: "To prevent XeLL issues", "Workaround for wrong frame
        // limit"). Intel allows all three at any time. Live.
        bool xessFgResetOnChange = false;
        // xessFgResetDebounce: a reset (resetHistory) within this many Presents of the
        // last one handed over is dropped (OptiScaler: 3, "suppress redundant optical flow
        // purging"). 0: every reset is handed over. Live.
        int xessFgResetDebounce = 0;
        // xellLimitLive: XeLL's frame limit (fgHalfRefreshLimit) follows later changes of
        // that setting and of the monitor's refresh rate: xellSetSleepMode again only when
        // the mode differs, once the GPU is idle. false: set once when the swapchain is
        // made. Live. The limit is first taken with XeLL's mode when the swapchain is made. Intel
        // says "FPS Cap is controlled via
        // xellSetSleepMode API when Low Latency mode is active" (xell_developer_guide_english.md,
        // Integration Checklist, XeSS-FG Support). Intel also says "XeLL will limit the frame rate to
        // half of the target ensuring that the total count of presented frames (both generated and
        // rendered) matches the requested FPS".
        bool xellLimitLive = true;

        // The raytracing and Ray Reconstruction on a Direct3D 12 queue of their own
        // instead of the swapchain's, where the present and frame generation run: their
        // work does not wait behind frame generation's (Raytracing::Attach). Read when a
        // swapchain is created.
        bool rtOwnQueue = false;

        // For measuring, 0 by default: what the cut-out surfaces' tests cost the rays.
        // 1 decides their hits without reading their textures, 2 takes every surface as
        // opaque (RtContract.h, RT_FLAG_CUTOUT_PLAIN and RT_FLAG_CUTOUT_OPAQUE). The
        // picture is wrong with either. Re-read while the game runs. With rtOpacityMicromaps 1 a
        // ray skips the transparent parts of a blade and stops at the opaque ones without the
        // passes' test, which is left to the parts that are partly covered. The picture stays the
        // same. A driver without the capability and any other GPU trace as with 0, with a line in
        // the log.
        int rtCutoutTest = 0;

        // Opacity Micromaps for the cut-out surfaces, NVIDIA's only (RaytracingOmm.cpp).
        // The value 1 bakes each cut-out mesh's triangles into micromaps from its texture
        // coordinates and the material texture's coverage, and builds its structure with them
        // through NVAPI. The Ada generation traces them in hardware, and Ampere resolves them in
        // software, "emulation may still produce a speedup" (NVIDIA). The passes' pipelines are
        // made with micromap support once it is on, which costs the other traces "a small
        // penalty on traversal performance overall" (nvapi.h). The instances' flags switch it live
        // without building a structure again, and the first 1 of a session makes the pipelines
        // again.
        int rtOpacityMicromaps = 0;
        // The finest subdivision a micromap gets, 2 to 8: a triangle gets about one micro-triangle
        // to a texel of the 64 x 64 coverage texture, 4^level of them at most. A micromap of level 6
        // is 1 KB. Read when a mesh is baked. On a driver with micromaps the mod keeps what a
        // later 1 bakes from, also while rtOpacityMicromaps is 0. These are the texture coordinates
        // of the meshes a cut-out instance may use (24 bytes a triangle, on the CPU) and each
        // material texture's 64 x 64 coverage (4 KB), because the game does not send them again.
        // They stay until a restart.
        int rtOmmMaxSubdivision = 6;

        // For measuring, false by default: the sun's visibility as its pass traced it, one
        // ray a pixel, past the denoiser, which then filters the other signals only. The
        // picture is noisy with it. Re-read while the game runs.
        bool rtSunUnfiltered = false;

        // How many of the denoiser's five à-trous steps filter the sun's visibility, and
        // the longest history, in frames (1 to 32), its temporal pass keeps of it. The rays
        // draw the shadows of the blades of grass, which the other signals' 5 steps and 32
        // frames smoothed away. 0 and 4 keep them. At the launch pad (2026-10-19) the
        // lawn's structure against a 9x9 mean was 6.3, 5.5 without the raytracing and 5.1 with 5
        // and 32. Its pixel noise was 4.9, 4.3 and 4.2, and the pad's the same. Re-read while the
        // game runs.
        int rtSunAtrousSteps = 0;
        int rtSunHistory = 4;

        // What of the sun's light a cut-out surface (grass, ferns) lets through to what lies
        // behind it, 0 to 1 (RtCommon.hlsli, SunUnoccluded). A blade is thin and lets part of
        // it through, as Parallax's own subsurface scattering shades it. Re-read while the game
        // runs.
        float rtFoliageTransmission = 0.5f;

        // The world-space radiance cache of the raytraced indirect light (RaytracingCache.cpp,
        // Raytracing/Shaders/RadianceCache.hlsli). The diffuse light's and the reflections' paths
        // end at a hit in the cache's value there, where it holds enough samples, instead of
        // tracing on, and write what they trace into it. Off by default. All its settings are
        // re-read while the game runs.
        bool rtRadianceCache = false;
        // Its table has 2^rtRadianceCacheEntries entries of 44 bytes (16 to 22).
        int rtRadianceCacheEntries = 20;
        // A cell is rtRadianceCacheCellPixels pixels across at its distance, never smaller than
        // rtRadianceCacheMinCell metres (taken down to a power of two). It keeps at most
        // rtRadianceCacheSamples samples' weight and goes when no ray used it for
        // rtRadianceCacheMaxAge frames.
        float rtRadianceCacheCellPixels = 8.0f;
        float rtRadianceCacheMinCell = 0.125f;
        int rtRadianceCacheSamples = 256;
        int rtRadianceCacheMaxAge = 50;
        // The value 1 shows the cache's value where the diffuse light was, and 2 shows its cells in
        // colours of their own.
        int rtRadianceCacheDebug = 0;

        // rtBlasBatch: a frame's copies first, one barrier, then all its builds back to back, one UAV
        // barrier before the scene's structure. The guide says "Do not place resource barriers
        // between consecutive calls ... All BLAS build calls need unique scratch memory to allow
        // execution without barriers ... you can have a single global UAV barrier before TLAS
        // build". The value false copies, barriers and builds mesh by mesh. Batched alone, the
        // figures are 0.58 and 1.73 ms of the GPU, with the CPU's time unchanged.
        bool rtBlasBatch = true;
        // rtBlasPool: the structures, the geometry, the scratch and the uploads in ranges of a few
        // large buffers. The guide says "many BLASes can be stored in a single buffer by manually
        // suballocating sections from the buffer ... the suballocations only have to follow 256-byte
        // alignment". The value false gives per-mesh buffers. Pooled alone they are 1.4 and 5.8 ms
        // of the CPU, the GPU's time unchanged. With rtBlasBatch they are 0.55 and 1.67 ms of the
        // GPU and 1.1 and 4.7 of the CPU, the geometry 2.0 MB for 150 parts against 29.8 in buffers
        // of their own.
        bool rtBlasPool = true;
        // rtBlasBudget: the triangles of new meshes built in one frame, the rest in the
        // next ones, at least one mesh a frame -- "many rebuilds on a single frame can lead
        // to stuttering. To avoid this, it's good practice to distribute the rebuilds over
        // frames". A mesh not yet built is left out of the scene until it is. 0: all at
        // once. 20 000 took the slowest frame to 0.37 ms of the GPU, the 600 parts over 13
        // frames.
        int rtBlasBudget = 0;
        // rtBlasCompaction: the value 0 is none. The value 1 builds the
        // structures PREFER_FAST_TRACE with ALLOW_COMPACTION and compacts them once their size is
        // read back. The value 2 builds them PREFER_FAST_BUILD first ("about 1/2 to 1/3 the build
        // time than default at a sacrifice in tracing performance"). It rebuilds them
        // PREFER_FAST_TRACE with ALLOW_COMPACTION in later frames that build no new mesh,
        // rtBlasRefineBudget triangles a frame, and compacts them. Batched and pooled, 1 took the
        // slowest frame to 0.69 and 2.01 ms of the GPU and 2 to 0.63 and 1.16, the 600 parts' over 8
        // frames.
        int rtBlasCompaction = 2;
        int rtBlasRefineBudget = 65536;
        // rtTlasFastTrace: the scene's structure PREFER_FAST_TRACE instead of PREFER_FAST_BUILD. The
        // guide says "For TLAS, consider the PREFER_FAST_TRACE flag and perform only rebuilds". With
        // 15 003 and 24 003 instances its build took 0.265 and 0.357 ms against 0.276 and 0.360. The
        // trace took 1.230 and 1.706 against 1.231 and 1.715, no difference beyond the runs' own.
        // With rtMegaGeometry 1 the trace took 2.54 and 3.43 against 1.24 and 1.75 on the same
        // instances.
        bool rtTlasFastTrace = false;
        // rtMegaGeometry: 0 builds the structures above and 1 builds RTX Mega Geometry's cluster
        // structures for new meshes (RaytracingMegaGeometry.cpp), NVIDIA's only.
        // It makes a CLAS for each run of 256 triangles, all of a frame's in one call. Then it makes
        // a cluster BLAS of its CLAS for each mesh, all in a second. A mesh whose runs do not fit,
        // a driver without the cluster operations and any other GPU build as with 0. At the launch
        // pad the sun's rays took 3.9 ms against 1.3, the diffuse light's 2.6 against 1.3 and the
        // reflections' 3.2 against 1.6.
        int rtMegaGeometry = 0;

        // Where a player's nvngx_dlss.dll lies if not next to KSP_x64.exe, which
        // NGX searches anyway (Dlss.h). Read when DLSS starts on a device, on
        // Unity's render thread, through DlssDirectory().
        wchar_t dlssDirectory[260] = {};

        // Where a player's amd_fidelityfx_upscaler_dx12.dll lies if not next to
        // KSP_x64.exe (AmdUpscaler.h). Read when the rig first asks for it,
        // through AmdUpscalerDirectory().
        wchar_t amdUpscalerDirectory[260] = {};

        // Where a player's libxess.dll lies if not next to KSP_x64.exe
        // (XessUpscaler.h). Read when the rig first asks for it, through
        // XessDirectory().
        wchar_t xessDirectory[260] = {};

        // Where NVIDIA's Streamline DLLs lie -- sl.interposer.dll, its plugins and
        // nvngx_dlssg.dll -- if not next to KSP_x64.exe (Streamline.h). Read from
        // the file when a swapchain is created, on Unity's render thread, through
        // StreamlineDirectory().
        wchar_t streamlineDirectory[260] = {};

        // Where a player's libxess_fg.dll and libxell.dll lie if not next to
        // KSP_x64.exe (XessFg.h). Read from the file when a swapchain is created,
        // through XessFgDirectory().
        wchar_t xessFgDirectory[260] = {};
    };

    // ProxyConfig::frameGenerationTechnique, as the ini writes it: auto, fsr, dlss,
    // xess.
    constexpr int kFgTechniqueAuto = 0;
    constexpr int kFgTechniqueFsr = 1;
    constexpr int kFgTechniqueDlss = 2;
    constexpr int kFgTechniqueXess = 3;

    // ProxyConfig::dlssgMode, as the ini writes it: on, dynamic.
    constexpr int kDlssgModeOn = 0;
    constexpr int kDlssgModeDynamic = 1;
    const char* DlssgModeName(int mode);

    // ProxyConfig::dlssgUiAlpha, as the ini writes it: binary, minimal.
    constexpr int kDlssgUiAlphaBinary = 0;
    constexpr int kDlssgUiAlphaMinimal = 1;
    const char* DlssgUiAlphaName(int alpha);

    // ProxyConfig::rrPath, as the ini writes it: auto, streamline, ngx.
    constexpr int kRrPathAuto = 0;
    constexpr int kRrPathStreamline = 1;
    constexpr int kRrPathNgx = 2;
    const char* RrPathName(int path);

    // ProxyConfig::xessFgUiMode, xefg_swapchain_ui_mode_t's values, as the ini names
    // them: auto, none, backbuffer_uitexture, hudless_uitexture, backbuffer_hudless,
    // backbuffer_hudless_uitexture.
    constexpr int kXessUiModeAuto = 0;
    constexpr int kXessUiModeCount = 6;
    const char* XessUiModeName(int mode);

    // ProxyConfig::xessFgBadConstants, as the ini writes it: skip, fix, pass.
    constexpr int kXessBadSkip = 0;
    constexpr int kXessBadFix = 1;
    constexpr int kXessBadPass = 2;
    const char* XessBadConstantsName(int mode);

    const ProxyConfig& Config();
    void LoadConfig();

    // The upscalers' folders, for threads other than the present path, which re-reads
    // the ini: copied under the lock the settings are written under.
    std::wstring DlssDirectory();
    std::wstring AmdUpscalerDirectory();
    std::wstring XessDirectory();
    std::wstring StreamlineDirectory();
    std::wstring XessFgDirectory();
    // frameGenerationTechnique as the file has it now, read like the folders.
    int FrameGenerationTechniqueAsked();
    // Every setting as the file has it now, for what a swapchain takes when it is
    // made (xell, xellLowLatencyBoost, xessFgUiMode, xessFgFrames, xessFgOwnWaitable,
    // xessFgAllowOld, and XeLL's frame limit from fgHalfRefreshLimit), read like the
    // folders.
    ProxyConfig ConfigAsked();
    // rrPath as the file has it now, the same way.
    int RayReconstructionPathAsked();

    // Re-reads the file when it has changed on disk; returns whether it did.
    // rebuild: whether a setting changed that the frame generation context has to
    // be made anew for.
    bool ReloadConfigIfChanged(bool& rebuild);
}
