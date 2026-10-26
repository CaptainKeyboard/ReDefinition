# Frame generation

**For:** anyone changing ReDefinition's frame generation.
**You need:** the repository. The code is in `src/DxgiProxy/` and `src/Proxy/`.
**You get:** how generated frames are made in a game that renders in Direct3D 11, what
each runtime asks for, and what it costs.

KSP renders in Direct3D 11, so frame generation runs in a `dxgi.dll` proxy. Three runtimes
are offered there: AMD's FSR 3.1 frame generation, NVIDIA's DLSS frame generation through
Streamline 2.14.16, and Intel's XeSS frame generation with XeLL.

Every claim here carries its mark: **[src]**, **[doc]**, **[meas]** or **[open]**
([writing-these-pages.md](writing-these-pages.md)).

## The idea

**[doc]** Every runtime needs Direct3D 12. The proxy follows the approach of
DynamicShaderFrameGen (jatelop8, GPL-3.0, Skyrim SE), which builds on Community Shaders'
swapchain proxy and ENBFrameGeneration: hand the game a **shared Direct3D 11 texture as
its backbuffer**, and present through a **Direct3D 12 swapchain**. The game keeps
rendering in Direct3D 11.

| Layer | What happens |
|---|---|
| KSP / Unity | renders unchanged in Direct3D 11 |
| Backbuffer | a shared Direct3D 11 texture from the proxy, carrying the whole frame including UI |
| HUD-less colour | a copy of the backbuffer, taken between the last scene camera and the first UI camera |
| Proxy | the Direct3D 12 device, and FSR's frame interpolation swapchain, Streamline's or Intel's |
| Interop | shared NT handles; shared fences order the devices |
| Presentation | through the runtime's swapchain, which paces generated and real frames |

**[src]** The proxy hooks `IDXGIFactory2::CreateSwapChainForHwnd`, the call Unity makes.
Windows loads a `dxgi.dll` next to the executable before the system's, and the proxy
forwards every other export to Windows' own.

## Which runtime runs

`frameGenerationTechnique` in `ReDefinitionProxy.ini` (`auto`, `dlss`, `xess` or `fsr`)
says which swapchain the proxy makes. It is read once, when KSP creates its swapchain
**[src]**. `auto` makes Streamline's where DLSS frame generation runs, XeSS frame
generation's on an Intel GPU, and FidelityFX's otherwise. The log's "DLSS frame
generation" and "XeSS frame generation" lines say which runs and why.

**DLSS.** The proxy makes the swapchain through Streamline where DLSS frame generation
runs. It runs where Streamline 2.14.16 signed by NVIDIA lies in the player's files, on an
NVIDIA GPU that supports it, under Windows 10 20H1 or newer, with hardware-accelerated GPU
scheduling on. Streamline itself decides the support: `slIsFeatureSupported` for DLSS-G,
and the same for Reflex, which DLSS-G requires **[doc]**. NVAPI reports the GPU's
architecture (`src/DxgiProxy/NvidiaGpu.cpp`), and the mod offers NVIDIA's frame generation
files from Ada on, Streamline's core and Ray Reconstruction's from Turing on
(`src/Vendor/NvidiaFiles.cs`). The proxy loads Streamline 2.14.16 only; a file of
another version counts as missing there.

**XeSS.** The proxy makes Intel's swapchain on an Intel adapter with `auto`, and for any
GPU where `xess` is asked. It needs `libxess_fg.dll` and `libxell.dll` of XeSS SDK 2.1 or
newer ([XeSS frame generation](#xess-frame-generation)).

**FSR.** Otherwise the swapchain is made through FidelityFX, where
`amd_fidelityfx_framegeneration_dx12.dll` lies next to the executable. Where neither is
there, the proxy makes a plain Direct3D 12 swapchain that presents without frame
generation.

Streamline runs in manual hooking mode: its command queue comes from its device proxy, the
swapchain is made through its factory proxy, and every other call goes to the native
interfaces ("use native interfaces EVERYWHERE in the host application EXCEPT for the APIs
which are hooked by SL", ProgrammingGuideManualHooking.md) **[doc]**.

## What Unity asks for **[meas]**

| Parameter | Value | For the proxy |
|---|---|---|
| Format | `R8G8B8A8_UNORM` | shareable |
| Swap effect | `FLIP_DISCARD` | already the flip model |
| Flags | `0x842`: `ALLOW_TEARING`, `ALLOW_MODE_SWITCH`, `FRAME_LATENCY_WAITABLE_OBJECT` | tearing must be kept; the waitable object must be served through `IDXGISwapChain2`, or Unity stops presenting |
| Windowed | true | the fullscreen restriction costs nothing |

## The inputs

| Needed | From | Format |
|---|---|---|
| colour | the backbuffer itself: FSR through `frameGenerationCallback`, DLSS-G from its swapchain | `R8G8B8A8_UNORM` |
| depth | the rig's depth copy | `R32_FLOAT`, which is on the NT sharing whitelist where `D32_FLOAT` is not |
| motion vectors | the rig's motion vectors | `R16G16_FLOAT` |
| HUD-less colour | a copy of the backbuffer recorded at `CameraEvent.AfterEverything` on the presenter and each effect camera after it, the active vessel's shadow taken out where frame generation would drag it (`VesselShadowLayer`) | the backbuffer's own |
| camera | jitter, planes, field of view, position and basis, and for DLSS-G the view and projection matrices without jitter (`StreamlineCamera`) | one packet per frame through a render event, size and magic checked |

Unity does not create its render textures shareable, so the proxy creates shared twins
and copies into them at `Present`, when everything Unity drew that frame is submitted. The
inputs are double buffered, and replaced ones are released once the GPU is done with
every frame that can read them.

**Without the upscaler.** With the upscaler off, or where it could not be set up, the rig
still redirects the 3D stack at full size for frame generation's inputs: depth, motion
vectors and the HUD-less copy, without jitter, masks, mipmap bias or quality overrides.

**Orientation.** **[meas]** Depth and motion vectors are blits between render textures
and keep Unity's flipped storage. The HUD-less copy is a blit from the screen and is
oriented like the screen. The proxy flips per texture, with a compute shader compiled at
run time from `d3dcompiler_47.dll`: `fgFlipInputs=1`, `fgFlipHudLess=0`. DLSS-G needs the
flip, since depth and motion vectors upside down against the backbuffer cannot be corrected
by a constant.

**Depth convention.** **[src]** FSR ignores the order of the near and far planes and
decides by `ENABLE_DEPTH_INVERTED` alone (`ffx_frameinterpolation.cpp`). KSP's depth runs
from 1 to 0, so the flag is set, as the upscaler sets it.

## The UI

**[doc]** Of FSR's three UI strategies, only the HUD-less surface fits. KSP's UI and every
mod's `OnGUI` window are drawn by Unity straight onto the backbuffer, and nothing can draw
them again on request. The runtime finds the UI as the difference between the backbuffer
and the HUD-less image, so that image must equal the backbuffer everywhere but the UI.
DLSS-G's HUD-less input: "Should contain the full viewable scene, without any HUD/UI
elements in it".

While KSP's pause menu is open, frame generation is off: "DLSS-G is turned off ... when the
game is paused, loading, in menus, or otherwise not rendering game frames"
(ProgrammingGuideDLSS_G.md, checklist) **[doc]**. Under the menu the scene stands still,
and there is nothing to interpolate.

Whichever of the presenter and the effect cameras renders last leaves the finished scene,
and everything drawn afterwards is UI. The KSP log names the cameras drawn after the
snapshot, and warns where one carries a post-processing layer.

**The check.** Every `reportSeconds` the proxy reads both images back, never waiting on the
GPU, samples every fourth pixel each way, and logs how much of the frame differs and
where, as a 12 x 5 tile map, in both orientations. A correct snapshot differs by the UI
only; a late one by nothing; a stale one by nearly everything. The motion vector check
reprojects the previous frame onto this one with both orientations and signs, and names
the one with the smallest error.

## FSR 3.1, per frame, as the API requires **[doc]**

`tools/audit_ffx_fields.py` fails on any descriptor field neither set nor listed with a
reason, since a `= {}` initialiser makes an omission invisible. What the API requires and
the proxy does:

* camera position and basis, which must be valid;
* the version descriptor, linked through `header.pNext`;
* `generationRect` over the whole backbuffer;
* `Configure` once per frame with the frame id incremented by one, generation off when the
  mod has it off or no inputs arrived, `reset` on the first frame back;
* one lock over prepare and present;
* `HUDLessColor` in `COMPUTE_READ` state;
* a fence back from Direct3D 12 to Direct3D 11 after every `Present`, so the next frame's
  copies wait for the work that reads them;
* before a context is destroyed, frame generation configured off, which waits for the
  swapchain's work; a resize destroys the context and the next frame makes one at the new
  size.

**[src]** Where the proxy differs from DynamicShaderFrameGen:

* `PrepareV2` in place of the deprecated `Prepare`;
* `allowAsyncWorkloads` off by default and offered as `fgAsyncWorkloads`, with the inputs
  double buffered as AMD requires for it;
* frame pacing at AMD's documented defaults, with hybrid spin a setting, off by default;
* `viewSpaceToMetersFactor` 1, KSP's unit being the metre;
* the motion vector scale's sign following the upscaler.

## DLSS frame generation, per frame **[doc]**

From NVIDIA's ProgrammingGuideDLSS_G.md and the Streamline headers:

* **Reflex and frame indices.** The game's main thread begins each frame (`ReflexFrames`,
  the last system of Unity's `PostLateUpdate`, before the next frame's messages are taken):
  Reflex's sleep, then `eSimulationStart` under a new frame index. `eSimulationEnd` follows
  `LateUpdate`. The index reaches Unity's render thread as render event 40, ahead of the
  frame's first Direct3D call, which marks `eRenderSubmitStart` there. The frame's Present
  takes that index for its constants, tags, `eRenderSubmitEnd`, `ePresentStart` and
  `ePresentEnd`: "Ensure the frame index provided with the common constants matches the
  presented frame". NVAPI's order: "SIMULATION_START must be the first marker sent in a
  frame, after the previous frame's Sleep call", "RENDERSUBMIT_START should come before any
  Direct3D calls are made for the given frame and RENDERSUBMIT_END should come before
  calling Present" (`nvapi.h`). PC latency's ping is picked up where the main thread takes
  its messages and marked under its frame. Without the game's frames (the harness, or the
  mod not loaded) the presenting thread begins each frame itself after a Present, with the
  sleep under its index; after three Presents without one from the game it does so again.
  The status line says which: `reflex paced by the game's frames` or `by the presenting
  thread`.
* **Tags.** Depth, motion vectors and HUD-less colour, tagged `eValidUntilPresent`, in
  `COMMON` state between uses; the motion vector scale as Streamline's normalisation by the
  texture's size.
* **Object separation.** `minRelativeLinearDepthObjectSeparation`, "the minimum depth
  difference between two objects in screen-space", is taken in linear depth, `1 / depth`
  with inverted depth, about the distance over the near plane (`sl_consts.h`). Its default
  of 40 comes to 8.4 m at KSP's near plane in flight, 0.21 m (`FlightCamera`, decompiled).
  The header: "smaller thresholds are useful when depth units are unusually compressed".
  `fgObjectSeparationMetres` in the proxy's ini sets it in metres, divided by each frame's
  near plane; 0, the default, keeps Streamline's. On the runway, 1 m and the default made
  no measurable difference.
* **Options.** On with the generated frames the mod asks for
  ([the multiplier](#the-multiplier)), and no more than the GPU reports it can make and
  the display allows (below); off when the mod has it off or no inputs arrived.
  Resources are kept while off. Options take effect "in the next Present() call that
  executes after it".
* **Status.** A failing status keeps DLSS-G off until a retry. The first generating presents
  after it comes on are not held against it, since Reflex needs frames to be detected. Each
  status is logged once.
* **Fences.** "SL client must wait on SL DLSS-G plugin-internal fence and associated value,
  before it can modify or destroy the tagged resources input to DLSS-G [...] on a
  non-presenting queue" (`sl_dlss_g.h`), and Direct3D 11's writes are on one. The proxy
  waits for that fence before Direct3D 11 writes into the input slot again. A fence that
  does not complete while the status fails costs the wait's limit once. Fences stored while
  the status failed are not waited for again until it is fine.
* **Window changes.** DLSS-G is switched off, and the shared colour presented once more,
  before the swapchain's size or full screen state changes ("Turn DLSS-G off ... before any
  window manipulation").

## The multiplier

The settings window's *Frame generation* row sends its choice, 2x to 6x or *Dynamic*, to
the proxy live (`KspFgSetMultiplier`, `FrameGenerationBridge.SetMultiplier`) **[src]**.
The proxy says what the technique that runs allows (`KspFgLimits`): the technique, the
most frames shown per rendered frame, and whether Dynamic is supported. The row and the
note under it follow that answer (`FrameGenerationChoice`). A step above the most runs as
the most.

* **FSR** generates one frame, 2x.
* **XeSS** generates what its swapchain was made for (`xessFgFrames`), and one frame on a
  GPU other than Intel's.
* **DLSS-G** changes `numFramesToGenerate` between Presents, within the
  `numFramesToGenerateMax` of Streamline's state **[doc]**. That is the GPU's and the
  driver's most, and with the RTX 30/20 package also its `MaxGeneratedFrames`, which is read
  when KSP starts.
* **Dynamic** sets `DLSSGMode::eDynamic` where the first Present's state reports
  `bIsDynamicMFGSupported`, and ignores the multiplier. The guide names three reasons for a
  system without it: no multi frame generation, an NVIDIA driver below 595.41, and Vulkan
  **[doc]** (ProgrammingGuideDLSS_G.md, 6.3). The log says once per swapchain what
  Streamline reports, with the driver's version. Where Dynamic was asked for and is not
  supported, it says that the fixed multiplier is used. `dlssgDynamicTargetFps` is the
  output rate Dynamic aims at, 0 for the monitor's refresh rate. NVIDIA: Dynamic "is
  designed to work with the Reflex frame rate limiter". ReDefinition sets none.

`dlssgMode` and `dlssgFrames` in the ini are the proxy's defaults where the mod asks for
nothing. The mod asks at every start.

## DLSS frame generation on RTX 30 and RTX 20

DLSS frame generation runs on Ada and newer. dlssg_for_sm86, a community project by
sdli1995, runs it on RTX 30 and RTX 20 as well. Its `version.dll` next to `KSP_x64.exe`
answers Streamline's queries with a newer GPU and has NVIDIA's runtime run on kernels
rebuilt for these GPUs **[src]** (`src/Vendor/AmperePackage.cs`). Streamline is
unchanged: the proxy sees DLSS-G, and makes Streamline's swapchain. ReDefinition neither
ships nor downloads the package.

`AmpereFrameGeneration` (`src/Window/`) offers the *DLSS-G on RTX 30/20* row on an RTX
GPU without DLSS frame generation of its own, or with the package in place. *Install from
zip* takes `version.dll`, `dlssg_sm86.ini` and the `dlssg_sm86` folder out of the
release zip in KSP's folder. Each file is checked by its size and CRC-32 as the zip gives
them, and all are placed or none. A file already there is kept as `.old`. The package's
layout changes between releases, so no file is pinned.

The install raises `[FrameGeneration] MaxGeneratedFrames` from the package's factory 4X
to 6X, the most its runtime supports, so that the *Frame generation* row has room. A
generated frame costs GPU time only while it is shown. Four choices of the window are
written into the ini in place, its comments and line endings untouched
(`AmpereOptions.cs`): `[DLSSG] Preset`, `[FrameGeneration] MaxGeneratedFrames`,
`[SuperResolution] Mode` and `[Logging] FrameTiming`. The package reads its ini when KSP
starts. `[SuperResolution]` needs the package's `sr` folder, about 180 MB, which a button
takes out of the zip on its own.

Preset C is NVIDIA's lighter candidate blending (Frame Generation Performance Mode). The
package's runtime pins user interface recomposition off with it, whatever the game asks:
its log says `preset_pinned`.

**[meas]** The proxy harness runs DLSS-G 4x through the package on an RTX A4500. KSP
generated 2x, 3x, 4x and 6x there, with 2.00, 3.00, 4.00 and 6.00 frames presented per
rendered frame in the proxy's log, and Dynamic about 3 towards 120 Hz. The benchmark's
rows with the package, FSR 3 and raytracing off, rendered 66.1 frames a second with
*Auto* and 71.1 with *C*. With raytracing on *Full* they rendered 40.2 and 41.8
([benchmarks.md](benchmarks.md)). The package's own frame timing gave a median of 1.32 ms
of GPU time for the evaluates of a rendered frame with *C* and one generated frame at
2560 by 1440.

## DLSS frame generation and the window's focus

**[src]** DLSS-G generates nothing while the game's window is not in the foreground.
Streamline says "DLSS-G disabled: window not focused" (`sl.dlss_g`, `shouldInterpolate`)
and presents the rendered frames alone, with its status still fine. The proxy logs it from
a Present it asked to generate, once per change:

* `DLSS frame generation: the game's window is not in the foreground`, then that
  Streamline generates no frames while it is not;
* `DLSS frame generation: the game's window is in the foreground again`.

The foreground window of KSP's own process counts as the game's
(`SwapChainProxy::NoteStreamlineFocus`). **[meas]** A benchmark started from another
program's window found its DLSS-G rows at 1.00 presented per rendered frame that way, and
the tool now keeps the game's window in front ([benchmarks.md](benchmarks.md)). In the
proxy harness, `REDEFINITION_HARNESS_FOCUS=1` lets the taskbar take the foreground for a
moment: 1.00 presents per frame outside it, 4.00 back in it, with both log lines.

## DLSS frame generation and KSP's HUD

KSP draws its HUD onto the back buffer, so the game has no UI plane to give. Only the
HUD-less copy is tagged. With no UI buffer, user interface recomposition is off, and the
*Auto* preset of the package runs without it, as preset C does.

**[meas]** `tools/hud_probe.py` flies the launch pad with the HUD showing and the camera
sweeping, once per case, records the monitor's frames with the generated ones, and
compares the HUD's regions with the rendered frames either side (RTX A4500, Medium, 1600 by
900, 55 rendered frames a second):

* HUD pixels that never move came out equal in the generated and the rendered frames, with
  *Auto* and with *C*: PSNR 65 to 108 dB, edge sharpness 1.000, and under 0.01 % of the
  pixels off by more than 12 levels.
* Pixels the HUD touches, against a reference without frame generation: generated and
  rendered frames of a run score the same (*Auto* 41.9 and 41.7 dB, *C* 50.7 and 48.8 dB).
  In a ring along the HUD's edges, *C* is 1 to 3 dB below *Auto*. The glyphs are intact.
* `hudLessColour=0` does not smear the still HUD either: DLSS-G falls back to no motion.
* The alpha of a UI plane cannot be found from the presented frame and the HUD-less copy:
  one equation, two unknowns. `dlssgUiRecomposition=1` builds the plane from the
  difference, with `dlssgUiAlpha` and `dlssgUiThreshold`. It makes the HUD a little worse:
  the navball's glyphs at 36 dB, the UI pixels at 37.9 dB against 41.9 dB with the
  HUD-less copy alone, and 39.2 dB with the minimal alpha, with ghosts. With preset C the
  runtime pins recomposition off whatever is asked. So `dlssgUiRecomposition` is 0.

## XeSS frame generation

**[src]** XeSS frame generation (`XessFg.h`, `FrameGenerationXess.cpp`,
`SwapChainProxyXess.cpp`) brings a swapchain of its own: "XeSS-FG provides a proxy swap
chain to simplify integration" **[doc]**. The proxy presents through it as through
FidelityFX's or Streamline's.

It reads the inputs that FSR and DLSS-G get. The depth, the motion vectors and the
HUD-less copy are tagged valid until the next Present, in `COMMON` state. The frame's
constants hold the view and projection from the camera's basis, FSR's pixel-space motion
scale and the jitter with the flip.

The HUD-less copy must be the back buffer's size. Intel asks that HUD-less, UI and back
buffer have the same size, and a copy of half the display made XeSS-FG report an error and
stop (1.00 presents per frame). The copy is not tagged then, and the log says so once. The
depth and the motion vectors must be of one size too.

* **XeLL** is required on every GPU: "XeSS-FG requires a working XeLL integration on all
  GPUs" **[doc]**. `libxell.dll` is loaded first. XeLL's markers carry the present id of
  XeSS-FG's frame. XeSS-FG's context is destroyed before XeLL's
  ([low-latency.md](low-latency.md)).
* **One at a time.** "Currently XeSS-FG does not support multiple proxy swap chains
  operating at the same time" **[doc]**. A swapchain made while an earlier one's XeSS-FG
  lives presents with FSR, and the log says so.
* **Generated frames.** `xessFgFrames` is the most, set when the swapchain is made
  (`maxInterpolatedFrames`) and checked against the properties it reports. "On non-Intel
  GPUs the maximum number of generated frames is one" **[doc]**. The mod chooses fewer live.
* **Minimum frame rate.** XeSS-FG holds off below 40 rendered frames a second, Intel's
  "40 FPS minimum" **[doc]** (`fgMinimumFps=-1`).
* **Frames without inputs.** A Present with a present id and no tags is presented without
  generated frames. Frames without a packet, depth or motion vectors, or with constants it
  refuses, keep it on for `xessFgGraceFrames` in a row, 3 by default. Switched off,
  its input history is cleared, and the frames after the switch-on come with Intel's "too
  few frames" warning, an extra frame to activate. The mod's switch, the minimum frame rate
  and the explicit hold-offs switch it off at once.
* **Constants.** A motion vector scale of 0 or not a number is refused, and so is a near
  plane, far plane or field of view that is not a positive number.
  `xessFgBadConstants=skip` gives the frame no inputs and counts it against the grace
  frames. `fix` makes a scale of 0 into 1. `pass` hands the constants over, and the
  refusal switches XeSS-FG off for two seconds.
* **Version floor.** A `libxess_fg.dll` older than 1.2.2 (XeSS SDK 2.1.1) is refused, and
  FSR takes over. The proxy tags resources without a command list, which those versions
  refuse (Intel's issues 45, 47, 48 and 50). `xessFgAllowOld=1` uses it anyway.
* **Live changes.** Intel allows changing the UI composition at any time and the number
  of generated frames live **[doc]**, and the proxy does. `xessFgResetOnChange=1` switches
  it off for a Present and on again instead. XeLL's frame limit follows later changes of
  `fgHalfRefreshLimit` and of the refresh rate (`xellLimitLive`), set again once the
  proxy's queue is idle, as Intel asks.
* **The frame latency waitable object.** `xessFgOwnWaitable` decides which one KSP gets.
  **[meas]** With XeSS-FG's own, KSP stopped after the first `ResizeBuffers` and was
  killed after 699 seconds. With the proxy's semaphore, released after each game Present
  and never holding the game back, the same start ran through all 32 phases. `auto`, the
  default, is the semaphore.

**[meas]** On an RTX A4500, a GPU other than Intel's, at the Medium profile with FSR 3:
XeSS-FG presented 2.00 frames per rendered frame in every row. Without raytracing the game
rendered 72.4 frames a second without frame generation and 63.0 with it, 126.1 shown. With
raytracing on *Full* it rendered 45.0 and 39.0, 77.9 shown ([benchmarks.md](benchmarks.md)).
The latency is in [low-latency.md](low-latency.md#measured). The proxy harness runs XeSS-FG
where `REDEFINITION_XESS_FG_DIR` names the two DLLs. XeSS-FG on an Intel GPU, and with more
than one generated frame, is **[open]** until an Intel Arc flies it.

## Pacing and V-Sync

**FSR 3.1.** **[doc]** FSR's swapchain owns frame pacing with two worker threads. With
V-Sync, frame generation holds the game at half the refresh rate and shows every frame for
one refresh; without it, in a window, "not all frames generated will get displayed".

* KSP's own V-Sync setting applies. `fgVSync=1` presents FSR's swapchain with V-Sync for
  as long as frame generation is switched on and has a context, not only on the frames
  it interpolates: a sync interval of 0 becomes 1, a higher one KSP asks for stays.
* `fgHalfRefreshLimit=1` holds the rendered rate 2 % below half the refresh rate after each
  generated frame presented without V-Sync. AMD: "The application should ensure that the
  rendered frame rate is slightly below half the desired output frame rate". The wait comes
  before the game's next frame, on a high-resolution waitable timer. KSP's own frame limit
  is a software timer.
* The monitor's refresh rate is read on a thread of the proxy's own, every two seconds and
  after a resize, never on the present path.

**DLSS frame generation.** **[doc]** Reflex paces DLSS-G. From the guide's V-Sync section:

* **22.1:** "Applications should check sl::DLSSGState::bIsVsyncSupportAvailable to determine
  if VSync is supported with the current DLSS-G build". Without support the proxy presents
  without V-Sync while DLSS-G generates.
* **22.2:** "SyncInterval > 1: Not supported. Will be clamped to 1 with a warning". The proxy
  presents with interval 1 while DLSS-G generates when KSP asks for more, and logs it once.
* **22.7:** with V-Sync, "60Hz ... 4x", "75Hz ... 5x"; above that "frames are generated
  faster than the display can present them, causing frame queue backup". So the proxy takes
  a fifteenth of the rate frames are shown at, which is the refresh rate over the sync
  interval, rounded down to the table's row. A hundredth of a row is added for rates such
  as 59.94 Hz, which keeps 72 Hz at 4x. Without V-Sync there is no limit beyond the GPU's.
  With the rate not known yet, one generated frame is allowed.

**A low rendered frame rate.** **[doc]** AMD: "AMD FidelityFX Super Resolution Frame
Generation 3.1.6 has been designed for an input framerate of at least 60 FPS", 4.0.1 "for
an input framerate of at least 30 FPS", and "it may be preferable to ... [disable] FSR Frame
Generation ... if the frame rate consistently drops below that threshold", re-measuring for
"a couple of seconds" after each change (frame-interpolation-swap-chain.md, Minimum
Framerate). Below it each generated frame bridges a larger step, and the halos and trails
around moving edges grow. NVIDIA names no minimum for DLSS-G.

The proxy averages the rendered frame times of the frame packets over two seconds. Below
nine tenths of the minimum, frame generation is held off; above nineteen twentieths it
runs again, so that a game held at 60 by V-Sync, which averages a little under it, does
not flip. The minimum is `fgMinimumFps` in `ReDefinitionProxy.ini`: -1, the default, takes
the rate FSR's provider was designed for and 30 for DLSS-G, AMD's lower figure; 0 never
holds frame generation off. The log says each change, and the status line that *Write
diagnostics to log* writes reads "held off below the minimum frame rate" **[meas]** (proxy
harness: frames reported at 25 a second under DLSS-G, 1.00 presents per frame and the
status line; at 60 again, 2.00).

Under V-Sync the shown frames are capped at the refresh rate over the sync interval. With
frame generation, the game then renders at that cap over the frames shown per rendered
one: two for FSR, DLSS-G's frame limit plus one. Where that rate is below nine tenths of
the minimum, frame generation would slow the game below its own minimum, and the average
would turn it off and on in turn. So the proxy holds it off for as long as that cap stands,
logs "Frame generation held off: V-Sync shows at most ...", and lets it run again once the
cap is gone (`FrameGeneration::SetPresentCapLocked`). The proxy harness checks both: with
the minimum at three quarters of the refresh rate, held off under V-Sync and running again
without it. DLSS-G's frame limit is capped by the minimum as well: under V-Sync it generates
no more frames than keep the rendered rate at the minimum, and at least one
(`SwapChainProxy::StreamlineFrameLimit`).

A single frame that took more than half again the minimum's frame time, 50 ms at a
minimum of 30, is presented without generated frames, however the average stands: the
two seconds' average does not catch a short hitch, and interpolating across such a gap
draws its whole motion into one frame. The log says the first such frame. The input
dumps of 2026-10-01 (20:15 and 22:03) show 100 to 230 ms a frame through a camera turn
**[meas]**, but the dumps read back several full-size textures every frame they cover,
and the frame time rises from frame to frame of the dump; how long the turn's frames
are without a dump is **[open]**.

KSP's registration requires the same of KSP's V-Sync row while DLSS frame generation runs
([reference/requirements.md](../reference/requirements.md), R6 and R7), so the settings
window offers only the intervals that apply.

## A frame that cannot be copied

Unity's Present reaches the proxy, which copies the frame into the Direct3D 12
backbuffer before presenting it. Four things can end that copy early: no shared
backbuffer, a wait that failed, a command list that could not be reset, and a copy that
failed. Each used to return the error to Unity with nothing presented, and the picture
stayed black or frozen with nothing to bring it back. Each now presents the swapchain
as it stands (`SwapChainProxy::PresentUncopied`): the display holds the previous frame,
the game is told the frame went out and goes on rendering, and the log counts the
frames it happened to. A copy that works again ends it by itself.

## What the proxy writes about memory

With every frame time report the proxy adds a line: what the game holds in memory and
has committed, how much Windows has free and how much is left to commit, and this
process's video memory against the budget the driver gives it
(`LoadMonitor::ReportMemory`, `GetProcessMemoryInfo`, `GlobalMemoryStatusEx`,
`IDXGIAdapter3::QueryVideoMemoryInfo`). A texture that cannot be made ends a run, and
the log should say which of the three ran out rather than leaving it to be guessed.

## Measured

**The proxy presenting through Direct3D 12 without frame generation [meas]**, in flight,
with the same timing code on both sides: 11.83 ms against 11.38 ms, within the run-to-run
variation. The p99 is about 35 ms on both sides, which is KSP's own.

**FSR's swapchain without interpolation [meas]**, another run against the same baseline
of 11.38 ms:

| Run | mean | p50 | p95 | vs baseline |
|---|---|---|---|---|
| baseline | 11.38 ms | 11.30 | 15.95 | the baseline |
| proxy, plain swapchain | 11.70 ms | 11.69 | 15.97 | +2.8 % |
| proxy, FSR swapchain | 12.66 ms | 12.22 | 17.27 | +11.3 % |

**FSR frame generation on [meas]**, 3440x1440 at 120 Hz, V-Sync off: rendered 82-84 fps at
12.0 ms without, 70 fps at 14.2 ms with, 140 frames a second reaching DXGI against a 120 Hz
monitor, the excess dropped unevenly. At a 30 fps cap: presented per rendered 2.00, 59 of
60 possible frames a second on screen, and 200-470 pixels of motion per frame, which is
beyond what interpolation handles cleanly. The HUD-less check in flight found the UI and
nothing else. The motion vector reprojection check reported `MATCH` for the orientation FSR
is given.

**[meas]** In flight the GPU is at 90-97 %, so interpolation is paid for out of the real
frame rate. Frame generation pays where the rendered rate is low, such as large vessels,
many parts and re-entry. It does not pay on the pad at 80-90 fps.

**The harness [meas]**, `ProxyHarness.exe` in
[building-and-testing.md](building-and-testing.md):

* **FSR:** 640x360; presents per frame 2.00 on, 2.00 after eight mode changes, 1.00 off,
  2.00 on again, 2.00 after a resize to 800x450; `fgVSync=1`, `fgHalfRefreshLimit=1` and
  `fgAsyncWorkloads=1` from the ini; the HUD-less check finds exactly the drawn UI.
* **DLSS-G:** the same phases, counted by the proxy's own totals. It then asks for V-Sync
  every second refresh, which DLSS-G does not support, to see that the interval is clamped
  to every refresh and generation goes on: 2.00 presents per frame, 120 a second at 120 Hz.
  One phase sends the game's frames as the mod does (`KspReflexFrameStart`,
  `KspReflexSimulationEnd`, render event 40): 2.00 presents per frame, Reflex paced by the
  game's frames, and by the presenting thread again once they stop.
  DLSS-G generates nothing while the harness window does not have the focus.
* **XeSS-FG:** where `REDEFINITION_XESS_FG_DIR` names the two DLLs, the same phases with
  `xessFgFrames=2` asked, which stays 2x on a GPU other than Intel's with the status line
  saying "at most 1 here"; the frames without inputs (3 in a row switch nothing, 5 switch it
  off and on once); a motion vector scale of 0 skipped and handed over; a HUD-less copy of half
  the size; the reset on a change; XeLL's limit following `fgHalfRefreshLimit` set live;
  and the three modes of `xessFgOwnWaitable`, with no wait that timed out. With frame
  generation off, the low latency row reports the brake on a GPU other than Intel's.

## Open

| Question | What settles it |
|---|---|
| What the fence back costs | frame times with and without, in the same scene |
| The HUD-less check's own cost | the report's max column, once per interval |
| Frame pacing per profile | measured once the tiers are on real GPUs |
| Whether the object separation takes out the flicker of the aircraft's shadows on the runway with presets L and M | a fast run on the runway with frame generation, the value at 1 m and at 0 |
| DLSS-G in flight: status, generated frames per rendered frame, latency | the proxy's "DLSS frame generation" and frame time lines from a flight |
| XeSS-FG on an Intel GPU, and with more than one generated frame | a flight on an Intel Arc |
| XeSS-FG in exclusive full screen | a run in full screen: where switching on fails, the swapchain is asked once and the log says that KSP's full screen mode cannot run it |
