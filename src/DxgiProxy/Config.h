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

        // Frame generation: the swapchain made through Streamline, Intel's XeSS-FG
        // or FidelityFX (frameGenerationTechnique) instead of DXGI directly. Off,
        // the swapchain is Streamline's only for DLSS Ray Reconstruction. Read once
        // at creation, for the same reason as enabled. On by default: the player
        // package ships AMD's runtime (Config.cpp).
        bool frameGeneration = true;

        // Which frame generation the swapchain is made for (kFgTechnique*), taken when
        // the swapchain is created, like the folders: which swapchain presents is
        // decided once per swapchain.
        // auto: DLSS-G where NVIDIA's Streamline 2.14.16 is there and the GPU runs it
        // (Streamline.h), XeSS-FG on an Intel GPU where the player's libxess_fg.dll
        // and libxell.dll are there (XessFg.h), FSR's otherwise. dlss and xess ask for
        // theirs on any GPU that runs it -- XeSS-FG on "any non-Intel GPU supporting
        // Shader Model 6.4" (xess_fg_developer_guide_english.md, Requirements) -- and
        // FSR's where it does not; fsr asks for FSR's always.
        int frameGenerationTechnique = 0;

        // How DLSS Ray Reconstruction runs (kRrPath*), taken when the swapchain is
        // created, like frameGenerationTechnique. auto: through Streamline where its
        // swapchain presents -- DLSS-G, or frame generation off -- and through NGX
        // directly on the proxy's Direct3D 12 device otherwise, beside FSR's or
        // XeSS's frame generation (DlssRayReconstruction.h). streamline: through
        // Streamline only, none beside FSR's or XeSS's frame generation. ngx: through
        // NGX only; with frame generation off the swapchain is then not Streamline's,
        // and under DLSS-G, whose Streamline holds NGX on the device, none.
        int rrPath = 0;

        // ---- live from here down, but for the folders, rtOwnQueue, vendorDiagnostics,
        // streamlineImgui, xell, xellLowLatencyBoost, xessFgUiMode, rrPath and xessFgFrames'
        // most, which apply when what they serve starts ----

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
        // world. Changing this rebuilds the context. XeSS-FG takes it as
        // XEFG_SWAPCHAIN_INIT_FLAG_INVERTED_DEPTH when its swapchain is made.
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
        //
        // Under XeSS-FG the limit is XeLL's, the presented frames slightly below the
        // refresh rate: "FPS Cap is controlled via xellSetSleepMode API when Low
        // Latency mode is active", "XeLL will limit the frame rate to half of the
        // target ensuring that the total count of presented frames (both generated
        // and rendered) matches the requested FPS" (xell_developer_guide_english.md,
        // Integration Checklist, XeSS-FG Support). Taken when the swapchain is made,
        // with XeLL's mode.
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
        // NVIDIA names none, 40 for XeSS-FG, Intel's "40 FPS minimum"
        // (xess_fg_developer_guide_english.md, Introduction); 0: never off for this.
        float fgMinimumFps = -1.0f;

        // NVIDIA Reflex's low latency mode where Streamline's does not run
        // (LowLatency.h): frame generation off, FSR's, Ray Reconstruction alone. Live.
        // Intel XeLL's on an Intel GPU without XeSS-FG, set when the swapchain is made:
        // XeLL asks that "any GPU activity is finished before xellSetSleepMode is
        // called" (xell_developer_guide_english.md, Integration Checklist). With
        // XeSS-FG, XeLL's is always on: "XeLL must be enabled before enabling frame
        // generation".
        bool lowLatency = true;
        // Intel XeLL on an Intel GPU where XeSS-FG does not run: made, its mode as
        // lowLatency says, or not made at all -- "The application can disable XeLL
        // when frame generation is disabled" (xess_fg_developer_guide_english.md,
        // Connect with XeLL) -- so the latency brake runs as on any other GPU. XeSS-FG
        // makes its own always: "XeSS-FG requires a working XeLL integration on all
        // GPUs". Read when the swapchain is made.
        bool xell = true;
        // XeLL's bLowLatencyBoost, where it lowers the latency: "Boost is not supported
        // as of now" (xell.h). Read when the swapchain is made, with XeLL's mode.
        bool xellLowLatencyBoost = false;
        // The latency brake (LowLatency::Brake), and the waits in the proxy it leaves
        // per frame, in milliseconds. Live.
        bool latencyBrake = true;
        // AMD Anti-Lag 2 (LowLatency.h): 12 on the proxy's Direct3D 12 device, 11 on
        // Unity's Direct3D 11 device, 0 off. Live.
        int antiLag2 = 0;
        float latencyBrakeTargetMs = 4.0f;
        // Which waits the brake counts: 1 the proxy's and the Present, 2 the proxy's
        // alone (a raytracing slot, a frame's allocator). Live.
        int latencyBrakeWaits = 1;

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

        // XeSS-FG's UI composition: off, its default, the UI is interpolated with the
        // scene -- "Interpolation (default, recommended): rely on the frame generation
        // model to interpolate the UI"; on, it is composed onto the generated frames
        // from the HUD-less copy and the backbuffer, the mode chosen "automatically ...
        // based on the resources tagged by the application"
        // (xess_fg_developer_guide_english.md, UI Handling). Live.
        bool xessFgUiComposition = false;

        // XeSS-FG's UI mode, which its composition takes: kXessUiMode*, as
        // xefg_swapchain_ui_mode_t numbers them -- AUTO, "Determine UI composition mode
        // automatically, based on provided inputs: hudless color and UI texture", the
        // default. An init parameter (xefg_swapchain_d3d12_init_params_t::uiMode): read
        // when the swapchain is made. The proxy tags no UI texture.
        int xessFgUiMode = 0;

        // Frames XeSS-FG generates per rendered frame, 1 by default: "On non-Intel GPUs
        // the maximum number of generated frames is one" (xess_fg_developer_guide_english.md,
        // Requirements), and on Intel's as many as the swapchain says it supports
        // (xefgSwapChainGetProperties). The most is set when the swapchain is made --
        // "The maximum number of generated frames is set during the proxy swap chain
        // initialization and cannot be updated" -- and fewer live within it
        // (xefgSwapChainSetNumInterpolatedFrames, Controlling the Number of Generated
        // Frames).
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

        // The raytracing and Ray Reconstruction on a Direct3D 12 queue of their own
        // instead of the swapchain's, where the present and frame generation run: their
        // work does not wait behind frame generation's (Raytracing::Attach). Read when a
        // swapchain is created.
        bool rtOwnQueue = false;

        // For measuring, 0 by default: what the cut-out surfaces' tests cost the rays.
        // 1 decides their hits without reading their textures, 2 takes every surface as
        // opaque (RtContract.h, RT_FLAG_CUTOUT_PLAIN and RT_FLAG_CUTOUT_OPAQUE). The
        // picture is wrong with either. Re-read while the game runs.
        int rtCutoutTest = 0;

        // Opacity Micromaps for the cut-out surfaces (grass, ferns), NVIDIA's only
        // (RaytracingOmm.cpp), 0 by default. 1 bakes each cut-out mesh's triangles into micromaps
        // from its texture coordinates and the material texture's coverage, and builds its
        // structure with them through NVAPI: a ray skips the transparent parts of a blade and
        // stops at the opaque ones without the passes' test, which is left to the parts that
        // are partly covered. The picture stays the same. The Ada generation traces them in
        // hardware; Ampere resolves them in software, "emulation may still produce a speedup"
        // (NVIDIA). A driver without the capability and any other GPU trace as with 0, with
        // a line in the log; the passes' pipelines are made with micromap support from the
        // time it is on, which costs the other traces "a small penalty on traversal
        // performance overall" (nvapi.h). Switched live without building a structure again,
        // by the instances' flags; the first 1 of a session makes the pipelines again.
        int rtOpacityMicromaps = 0;
        // The finest subdivision a micromap gets, 2 to 8: a triangle gets about one
        // micro-triangle to a texel of the 64 x 64 coverage texture, 4^level of them at most;
        // a micromap of level 6 is 1 KB. Read when a mesh is baked.
        int rtOmmMaxSubdivision = 6;

        // For measuring, false by default: the sun's visibility as its pass traced it, one
        // ray a pixel, past the denoiser, which then filters the other signals only. The
        // picture is noisy with it. Re-read while the game runs.
        bool rtSunUnfiltered = false;

        // How many of the denoiser's five à-trous steps filter the sun's visibility, and
        // the longest history, in frames (1 to 32), its temporal pass keeps of it. The rays
        // draw the shadows of the blades of grass, which the other signals' 5 steps and 32
        // frames smoothed away; 0 and 4 keep them (at the launch pad, 2026-10-19: the lawn's
        // structure against a 9x9 mean 6.3, 5.5 without the raytracing, 5.1 with 5 and 32;
        // its pixel noise 4.9, 4.3 and 4.2; the pad's the same). Re-read while the game
        // runs.
        int rtSunAtrousSteps = 0;
        int rtSunHistory = 4;

        // What of the sun's light a cut-out surface -- grass, ferns -- lets through to
        // what lies behind it, 0 to 1 (RtCommon.hlsli, SunUnoccluded). Re-read while
        // the game runs.
        float rtFoliageTransmission = 0.0f;

        // The world-space radiance cache of the raytraced indirect light (RaytracingCache.cpp,
        // RaytracingShaders/RadianceCache.hlsli): the diffuse light's and the reflections'
        // paths end at a hit in the cache's value there, where it holds enough samples,
        // instead of tracing on, and write what they trace into it. Off by default. Its table
        // has 2^rtRadianceCacheEntries entries of 44 bytes (16 to 22); its cells are
        // rtRadianceCacheCellPixels pixels across at their distance, never smaller than
        // rtRadianceCacheMinCell metres (taken down to a power of two); a cell keeps at most
        // rtRadianceCacheSamples samples' weight and goes when no ray used it for
        // rtRadianceCacheMaxAge frames. rtRadianceCacheDebug 1 shows the cache's value where
        // the diffuse light was, 2 its cells in colours of their own. All re-read while the
        // game runs.
        bool rtRadianceCache = false;
        int rtRadianceCacheEntries = 20;
        float rtRadianceCacheCellPixels = 8.0f;
        float rtRadianceCacheMinCell = 0.125f;
        int rtRadianceCacheSamples = 256;
        int rtRadianceCacheMaxAge = 50;
        int rtRadianceCacheDebug = 0;

        // How the meshes' acceleration structures are built (RaytracingScene.cpp,
        // BuildMeshesLocked), after NVIDIA's "Best Practices for Using NVIDIA RTX Ray
        // Tracing (Updated)" and the DXR spec (DirectX-Specs, Raytracing.md). Each for the
        // structures built after it changes; re-read while the game runs. The raytracing
        // harness's vessel (RTX A4500, 2026-10-20): 150 parts of 54 272 triangles, as a
        // flight's first frames hand them over, and 600 of 244 404 at once; the slowest
        // frame's GPU time to its meshes built and the CPU's recording them. Mesh by mesh in
        // buffers of their own, as before, 21.6 and 172 ms, 87 and 715 ms; the picture the
        // same in every variant.
        //
        // rtBlasBatch: a frame's copies first, one barrier, then all its builds back to
        // back, one UAV barrier before the scene's structure -- "Do not place resource
        // barriers between consecutive calls ... All BLAS build calls need unique scratch
        // memory to allow execution without barriers ... you can have a single global UAV
        // barrier before TLAS build". false: copy, barrier and build mesh by mesh. Batched
        // alone: 0.58 and 1.73 ms of the GPU, the CPU's time as before.
        bool rtBlasBatch = true;
        // rtBlasPool: the structures, the geometry, the scratch and the uploads in ranges
        // of a few large buffers -- "many BLASes can be stored in a single buffer by
        // manually suballocating sections from the buffer ... the suballocations only have
        // to follow 256-byte alignment". false: buffers of their own per mesh. Pooled
        // alone: 1.4 and 5.8 ms of the CPU, the GPU's time as before; with rtBlasBatch
        // 0.55 and 1.67 ms of the GPU, 1.1 and 4.7 of the CPU, the geometry 2.0 MB for 150
        // parts against 29.8 in buffers of their own.
        bool rtBlasPool = true;
        // rtBlasBudget: the triangles of new meshes built in one frame, the rest in the
        // next ones, at least one mesh a frame -- "many rebuilds on a single frame can lead
        // to stuttering. To avoid this, it's good practice to distribute the rebuilds over
        // frames". A mesh not yet built is left out of the scene until it is. 0: all at
        // once. 20 000 took the slowest frame to 0.37 ms of the GPU, the 600 parts over 13
        // frames.
        int rtBlasBudget = 0;
        // rtBlasCompaction: 0 none; 1 the structures built PREFER_FAST_TRACE with
        // ALLOW_COMPACTION and compacted once their size is read back; 2 built
        // PREFER_FAST_BUILD first ("about 1/2 to 1/3 the build time than default at a
        // sacrifice in tracing performance"), rebuilt PREFER_FAST_TRACE with
        // ALLOW_COMPACTION in later frames that build no new mesh, rtBlasRefineBudget
        // triangles a frame, and compacted then. Batched and pooled: 1 took the slowest
        // frame to 0.69 and 2.01 ms of the GPU, 2 to 0.63 and 1.16, the 600 parts' over 8
        // frames; both the structures to 1.62 MB from 3.88 and to 7.11 from 17.26, the
        // trace as fast.
        int rtBlasCompaction = 2;
        int rtBlasRefineBudget = 65536;
        // rtTlasFastTrace: the scene's structure PREFER_FAST_TRACE -- "For TLAS, consider
        // the PREFER_FAST_TRACE flag and perform only rebuilds" -- instead of
        // PREFER_FAST_BUILD. With 15 003 and 24 003 instances its build 0.265 and 0.357 ms
        // against 0.276 and 0.360, the trace 1.230 and 1.706 against 1.231 and 1.715: no
        // difference beyond the runs' own.
        bool rtTlasFastTrace = false;
        // rtMegaGeometry: 0 the structures above; 1 RTX Mega Geometry's cluster structures
        // for new meshes, NVIDIA's only (RaytracingMegaGeometry.cpp): each run of 256
        // triangles of a mesh a CLAS, all of a frame's built in one call, then each mesh a
        // cluster BLAS of its CLAS, all in a second. A mesh whose runs do not fit, a driver
        // without the cluster operations and any other GPU build as with 0. The harness's
        // vessel against the defaults: the slowest frame 0.32 and 0.85 ms of the GPU
        // against 0.54 and 1.17, but the trace slower in every frame after, 0.46 and 0.53
        // ms against 0.41 and 0.46, with 15 003 and 24 003 instances 2.54 and 3.43 against
        // 1.24 and 1.75; the structures 3.44 and 14.83 MB against 1.62 and 7.11. Off: the
        // build saved once costs more than a frame's trace.
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

    const ProxyConfig& Config();
    void LoadConfig();

    // The two folders for threads other than the present path, which re-reads
    // the ini: copied under the lock the settings are written under.
    std::wstring DlssDirectory();
    std::wstring AmdUpscalerDirectory();
    std::wstring XessDirectory();
    std::wstring StreamlineDirectory();
    std::wstring XessFgDirectory();
    // frameGenerationTechnique as the file has it now, read like the folders.
    int FrameGenerationTechniqueAsked();
    // Every setting as the file has it now, for what a swapchain takes when it is
    // made (xell, xellLowLatencyBoost, xessFgUiMode, xessFgFrames, and XeLL's frame
    // limit from fgHalfRefreshLimit), read like the folders.
    ProxyConfig ConfigAsked();
    // rrPath as the file has it now, the same way.
    int RayReconstructionPathAsked();

    // Re-reads the file when it has changed on disk; returns whether it did.
    // rebuild: whether a setting changed that the frame generation context has to
    // be made anew for.
    bool ReloadConfigIfChanged(bool& rebuild);
}
