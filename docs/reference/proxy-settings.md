# The proxy's settings

**For:** players and developers who change `ReDefinitionProxy.ini`.
**You need:** the file next to `KSP_x64.exe`, which the proxy writes at its first start
([player/installing.md](../player/installing.md#change-the-proxys-settings)).
**You get:** what each setting for frame generation, latency, raytracing and the DLL
folders does, its default, and when the proxy reads it.

The file describes every setting beside its value. This page lists the ones a player or
a developer changes. The settings left out are for development: the orientation and
sign of the inputs frame generation takes (`fgFlipInputs`, `fgFlipHudLess`,
`fgNegateMotionScale`), FSR's pacing (`fgPacing...`), and the debug switches.

*Read* says when the proxy takes a value. *live* means within a second of the file being
saved. *start* means when KSP creates its swapchain, which is at the start of the game.

## Loading and the techniques

| Setting | Default | Read | Does |
|---|---|---|---|
| `enabled` | 1 | start | 0 passes everything through to Windows' own `dxgi.dll`. Set it if the game does not start with the proxy |
| `measureOnly` | 0 | start | 1 substitutes nothing and times the Present, for a comparison with the proxy |
| `frameGeneration` | 1 | start | 0: no frame generation. The proxy presents only. If NVIDIA's Streamline is there and the GPU runs DLSS Ray Reconstruction, it presents through Streamline |
| `frameGenerationTechnique` | auto | start | which frame generation the swapchain is made for: `auto`, `dlss`, `xess` or `fsr`. `auto` takes DLSS where it runs, XeSS on an Intel GPU where its DLLs are there, and FSR otherwise. FSR takes over where the one asked for does not run |
| `rrPath` | auto | start | how DLSS Ray Reconstruction runs. `streamline` runs it through Streamline's swapchain, `ngx` through NVIDIA's NGX directly, and `auto` takes Streamline's where its swapchain presents and NGX's otherwise. It needs `nvngx_dlssd.dll` next to the Streamline DLLs and an NVIDIA RTX GPU. The routes: [development/flow-raytracing.md](../development/flow-raytracing.md) |
| `allowTearing` | 1 | start | 1 lets the swapchain tear as KSP asks, so that without V-Sync a frame is shown at once |
| `reportSeconds` | 10 | live | how often frame times, the latency and the brake go into `ReDefinitionProxy.log` |

## Frame generation, all techniques

| Setting | Default | Read | Does |
|---|---|---|---|
| `hudLessColour` | 1 | live | 1 keeps the UI out of the interpolated frames, by comparing the presented frame with a copy that has none. 0 interpolates the UI along with the scene, and it smears |
| `fgDepthInverted` | 1 | live | 1 tells frame generation that KSP's depth runs from 1 to 0. XeSS frame generation takes it when the swapchain is made |
| `fgMinimumFps` | -1 | live | the rendered frame rate below which frame generation holds off. -1 takes the technique's own: 60 or 30 for FSR by its version, 30 for DLSS, 40 for XeSS. 0 never holds off; another number is that many frames a second |
| `fgCameraBasis` | 1 | live | FSR only. 1 hands frame generation the camera's position and orientation, which its API requires. 0 leaves them zero, and everything with parallax smears. DLSS and XeSS frame generation always get them |
| `fgVSync` | 0 | live | FSR only. 1 presents with V-Sync for as long as frame generation is on, whatever KSP's setting says |
| `fgHalfRefreshLimit` | 0 | live | FSR and XeSS only. 1 holds the rendered frame rate just below half the refresh rate while frames are generated without V-Sync. Under XeSS frame generation it is XeLL's frame limit |
| `fgAsyncWorkloads` | 0 | live | FSR only. 1 runs the optical flow and interpolation on a compute queue, overlapping the next frame. Worth a try where frame generation costs more than it gives |

## DLSS frame generation

| Setting | Default | Read | Does |
|---|---|---|---|
| `dlssgMode` | on | live | `on` is a fixed multiplier. `dynamic` is Dynamic Multi Frame Generation, which needs an NVIDIA driver 595.41 or newer and a GPU that does multi frame generation. Where Streamline does not report it supported, the fixed multiplier stays and the log says so |
| `dlssgFrames` | 0 | live | the generated frames per rendered frame: 1 is 2x, 2 is 3x, and so on, never more than the GPU and the package allow. 0 is as many as they allow |
| `dlssgDynamicTargetFps` | 0 | live | the output frame rate Dynamic Multi Frame Generation aims at. 0 is the monitor's refresh rate. Streamline ignores it under V-Sync |
| `dlssgUiRecomposition` | 0 | live | 1 hands DLSS frame generation a UI plane, which the proxy works out from the difference between the presented frame and the HUD-less copy, and turns user interface recomposition on |
| `dlssgUiAlpha` | binary | live | how the plane's alpha is found: `binary` takes the UI as opaque, `minimal` takes the least alpha that gives the presented frame |
| `dlssgUiThreshold` | 2 | live | the greatest difference, in levels of 255, that still counts as no UI |
| `fgObjectSeparationMetres` | 0 | live | the smallest depth difference, in metres, at which DLSS frame generation tells two surfaces apart. 0 keeps Streamline's default |

ReDefinition's *Frame generation* row sets the multiplier at every start. `dlssgMode` and
`dlssgFrames` apply where the mod asks for nothing.

## XeSS frame generation

XeSS frame generation needs `libxess_fg.dll` and `libxell.dll` from Intel's XeSS SDK 2.1
or newer.

| Setting | Default | Read | Does |
|---|---|---|---|
| `xessFgFrames` | 1 | start | the most generated frames per rendered frame. More than 1 only on an Intel GPU, as many as it supports. The *Frame generation* row chooses fewer while the game runs |
| `xessFgUiComposition` | 0 | live | 0 interpolates the UI with the scene, as Intel recommends. 1 lays the UI onto the generated frames from the HUD-less copy and the back buffer instead |
| `xessFgUiMode` | auto | start | the UI mode that composition takes: `auto`, `none`, `backbuffer_uitexture`, `hudless_uitexture`, `backbuffer_hudless` or `backbuffer_hudless_uitexture`. The proxy hands over no UI texture, only the HUD-less copy |
| `xessFgSceneChangeThreshold` | 0.7 | live | 0 to 1. How readily it takes a frame for a scene change and interpolates none there |
| `xessFgGraceFrames` | 3 | live | the frames in a row without inputs that it stays on for. Switched off, its input history is cleared and the frames after the switch-on come with Intel's "too few frames" warning. 0 switches off at the first |
| `xessFgBadConstants` | skip | live | what a frame's constants that Intel's library refuses do. `skip`: the frame gets no inputs and counts against the grace frames. `fix`: a motion vector scale of 0 becomes 1. `pass`: handed over as they are, and the refusal switches XeSS frame generation off for two seconds |
| `xessFgResetOnChange` | 0 | live | 1 switches it off for one Present and on again when the UI composition, the generated frames or the sync interval change. 0 changes them live, as Intel allows |
| `xessFgResetDebounce` | 0 | live | a history reset within this many Presents of the last one is dropped. 0 hands over every reset |
| `xessFgOwnWaitable` | auto | start | which frame latency waitable object KSP gets. `auto` and `1` give a semaphore of the proxy's, released after each game Present, which never holds the game back. `0` gives XeSS frame generation's own, with which KSP's first resize never returned in a test |
| `xessFgAllowOld` | 0 | start | 1 uses a `libxess_fg.dll` older than 1.2.2 (XeSS SDK 2.1.1). 0 refuses it and FSR's frame generation runs, since the proxy tags resources without a command list, which those versions refuse |
| `xessFgTagInterpolatedFrames`, `xessFgPresentFailedInterpolation` | 0 | live | for development: mark the generated frames, and show a black frame where an interpolation failed |

## Latency

What each lowers, and where: [player/low-latency.md](../player/low-latency.md).

| Setting | Default | Read | Does |
|---|---|---|---|
| `lowLatency` | 1 | live | NVIDIA Reflex's low latency mode where DLSS frame generation's own Reflex does not run. 0 leaves the mode off. On an Intel GPU it is XeLL's mode, read at the start |
| `latencyBrake` | 1 | live | the latency brake, on any GPU, not while XeLL lowers the latency. 0 switches it off |
| `latencyBrakeTargetMs` | 4 | live | the waiting left in each frame, in milliseconds. A lower number lowers the latency further, at some cost in frame rate where the CPU limits it |
| `latencyBrakeWaits` | 1 | live | which waits the brake counts: 1 the proxy's and the Present's, 2 the proxy's alone |
| `latencyBrakeWithDlssg` | 1 | live | 1 runs the brake under DLSS frame generation too, before Streamline's Reflex sleep. 0 leaves it to Reflex |
| `antiLag2` | 0 | live | AMD Anti-Lag 2 on a Radeon RX 5000 or newer: 12 on the proxy's Direct3D 12 device, 11 on the game's Direct3D 11 device, 0 off. On, it takes the brake's place |
| `xell` | 1 | start | 1 makes Intel's XeLL on an Intel GPU where XeSS frame generation does not run. 0 makes none, and the brake lowers the latency. XeSS frame generation always brings XeLL |
| `xellLowLatencyBoost` | 0 | start | XeLL's low latency boost. Intel's header: "Boost is not supported as of now" |
| `xellLimitLive` | 1 | live | 1 makes XeLL's frame limit follow later changes of `fgHalfRefreshLimit` and of the monitor's refresh rate. 0 sets it once, when the swapchain is made |

## Raytracing

The raytracing's own pages: [player/raytracing.md](../player/raytracing.md) and
[development/flow-raytracing.md](../development/flow-raytracing.md).

| Setting | Default | Read | Does |
|---|---|---|---|
| `rtOpacityMicromaps` | 0 | live | NVIDIA GPUs only. 1 bakes the cut-out meshes' triangles (grass, ferns) into opacity micromaps, which let a ray skip the transparent parts of a blade without the shader's test. The picture stays the same. A driver without them and other GPUs trace as with 0, and the log says so. The first switch to 1 makes the passes' pipelines again once. With 0 on a driver that has them, the cut-out meshes' texture coordinates (24 bytes a triangle) and the materials' alpha planes (4 KB a texture) are kept for a later switch to 1; switching back to 0 rebuilds the meshes without micromaps after about 300 frames and frees them, the kept data stays until KSP restarts |
| `rtOmmMaxSubdivision` | 6 | live | 2 to 8. The finest subdivision of a micromap, about one micro-triangle to a texel. Read when a mesh is baked |
| `rtRadianceCache` | 0 | live | 1 keeps the indirect light the rays find in a hash grid over the world. A ray's path ends where it meets a cell the grid already knows. Fewer rays a frame, and the light follows a change some frames late |
| `rtRadianceCacheEntries` | 20 | live | 16 to 22. The grid holds 2 to this power cells of 44 bytes; 20 is 46 MB of video memory |
| `rtRadianceCacheCellPixels` | 8 | live | a cell's width in pixels at its distance |
| `rtRadianceCacheMinCell` | 0.125 | live | the smallest cell, in metres |
| `rtRadianceCacheSamples` | 256 | live | the most rays' light a cell averages |
| `rtRadianceCacheMaxAge` | 50 | live | the frames after which a cell no ray met goes |
| `rtRadianceCacheDebug` | 0 | live | 1 shows the cache's light in place of the diffuse light, 2 each cell in a colour of its own |
| `rtBlasBatch` | 1 | live | 1 copies a frame's meshes first and builds them back to back without barriers between them. 0 builds mesh by mesh |
| `rtBlasPool` | 1 | live | 1 puts the structures and the meshes' data into ranges of a few large buffers. 0 gives each mesh buffers of its own |
| `rtBlasBudget` | 0 | live | the triangles of new meshes built in one frame, the rest in the next frames. 0 builds all at once |
| `rtBlasCompaction` | 2 | live | 0 none. 1 compacts the structures once built, for less video memory. 2 builds fast first, then rebuilds for faster rays and compacts in later frames that build no new mesh |
| `rtBlasRefineBudget` | 65536 | live | the triangles a frame that `rtBlasCompaction=2` rebuilds |
| `rtTlasFastTrace` | 0 | live | 1 builds the scene's structure for faster rays. 0 builds it faster |
| `rtMegaGeometry` | 0 | live | NVIDIA GPUs only. 1 builds new meshes' structures from RTX Mega Geometry's clusters of up to 256 triangles. Meshes it does not fit, and other GPUs, are built as with 0 |
| `rtOwnQueue` | 0 | start | 1 runs the raytracing and Ray Reconstruction on a Direct3D 12 queue of their own, so that their work does not wait behind frame generation's |

The keys for measuring the raytracing, `rtCutoutTest`, `rtSunUnfiltered`,
`rtSunAtrousSteps`, `rtSunHistory` and `rtFoliageTransmission`, are described beside their
values in the file.

## DLL folders

Each setting names a folder. Empty means next to `KSP_x64.exe`.

| Setting | Read | Folder of |
|---|---|---|
| `dlssDirectory` | when the upscaler is set up | `nvngx_dlss.dll` |
| `amdUpscalerDirectory` | when the upscaler is set up | `amd_fidelityfx_upscaler_dx12.dll` |
| `xessDirectory` | when the upscaler is set up | `libxess.dll` |
| `streamlineDirectory` | start | `sl.interposer.dll` with its `sl.*.dll` plugins, `nvngx_dlssg.dll` and `nvngx_dlssd.dll`. Only Streamline 2.14.16, signed by NVIDIA, is loaded |
| `xessFgDirectory` | start | `libxess_fg.dll` and `libxell.dll` |

The downloads *NVIDIA DLSS files* and *Intel XeSS files* in the settings window place their
files in these folders, and next to `KSP_x64.exe` where a setting is empty.
