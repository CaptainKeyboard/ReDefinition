# Frame generation

**For:** anyone changing ReDefinition's frame generation.
**You need:** the repository. The code is in `src/DxgiProxy/` and `src/Bridges/`.
**You get:** how generated frames are made in a game that renders in Direct3D 11, what
each runtime asks for, and what it costs.

KSP renders in Direct3D 11, so frame generation runs in a `dxgi.dll` proxy. Two runtimes
are offered there: AMD's FSR 3.1 frame generation, and NVIDIA's DLSS frame generation
through Streamline 2.14.1.

Every claim here carries its mark: **[src]**, **[doc]**, **[meas]** or **[open]**
([writing-these-pages.md](writing-these-pages.md)).

## The idea

**[doc]** Both runtimes need Direct3D 12. The proxy follows the approach of
DynamicShaderFrameGen (jatelop8, GPL-3.0, Skyrim SE), which builds on Community Shaders'
swapchain proxy and ENBFrameGeneration: hand the game a **shared Direct3D 11 texture as
its backbuffer**, and present through a **Direct3D 12 swapchain**. The game keeps
rendering in Direct3D 11.

| Layer | What happens |
|---|---|
| KSP / Unity | renders unchanged in Direct3D 11 |
| Backbuffer | a shared Direct3D 11 texture from the proxy, carrying the whole frame including UI |
| HUD-less colour | a copy of the backbuffer, taken between the last scene camera and the first UI camera |
| Proxy | the Direct3D 12 device, and FSR's frame interpolation swapchain or Streamline's |
| Interop | shared NT handles; shared fences order the devices |
| Presentation | through the runtime's swapchain, which paces generated and real frames |

**[src]** The proxy hooks `IDXGIFactory2::CreateSwapChainForHwnd`, the call Unity makes.
Windows loads a `dxgi.dll` next to the executable before the system's, and the proxy
forwards every other export to Windows' own.

## Which runtime runs

With `dlssFrameGeneration=1` in `ReDefinitionProxy.ini` the proxy makes the swapchain
through Streamline where DLSS frame generation runs. It runs where Streamline 2.14.1
signed by NVIDIA lies in the player's files, on an NVIDIA GPU that supports it, under
Windows 10 20H1 or newer, with hardware-accelerated GPU scheduling on. Streamline itself
decides the support: `slIsFeatureSupported` for DLSS-G, and the same for Reflex, which
DLSS-G requires **[doc]**. NVAPI reports the GPU's architecture
(`src/DxgiProxy/NvidiaGpu.cpp`), and the mod offers NVIDIA's frame generation files from
Ada on (`src/Bridges/NvidiaFiles.cs`).

Otherwise the swapchain is made through FidelityFX, where
`amd_fidelityfx_framegeneration_dx12.dll` lies next to the executable. Where neither is
there, the proxy makes a plain Direct3D 12 swapchain that presents without frame
generation. The log's "DLSS frame generation" lines say which runs and why.

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
| HUD-less colour | a `Blit` of the backbuffer recorded at `CameraEvent.AfterEverything` on the presenter and each effect camera after it | the backbuffer's own |
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

Whichever of the presenter and the effect cameras renders last leaves the finished scene,
and everything drawn afterwards is UI. The KSP log names the cameras drawn after the
snapshot, and warns where one carries a post-processing layer.

**The check.** Every `reportSeconds` the proxy reads both images back, never waiting on the
GPU, and logs how much of the frame differs and where, as a 12 x 5 tile map, in both
orientations. A correct snapshot differs by the UI only; a late one by nothing; a stale one
by nearly everything. The motion vector check reprojects the previous frame onto this one
with both orientations and signs, and names the one with the smallest error.

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

* **Frame tokens.** The presenting thread fetches the next frame's token and calls Reflex's
  sleep under it ("Starting new frame, grab handle from SL", ProgrammingGuideReflex.md).
  The frame's constants, tags and markers go under the same token when it is presented.
* **Tags.** Depth, motion vectors and HUD-less colour, tagged `eValidUntilPresent`, in
  `COMMON` state between uses; the motion vector scale as Streamline's normalisation by the
  texture's size.
* **Options.** On with as many generated frames as the GPU reports it can make, and no more
  than the display allows (below); off when the mod has it off or no inputs arrived.
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

KSP's registration requires the same of KSP's V-Sync row while DLSS frame generation runs
([reference/requirements.md](../reference/requirements.md), R6 and R7), so the settings
window offers only the intervals that apply.

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
  DLSS-G generates nothing while the harness window does not have the focus.

## Open

| Question | What settles it |
|---|---|
| What the fence back costs | frame times with and without, in the same scene |
| The HUD-less check's own cost | the report's max column, once per interval |
| Frame pacing per profile | measured once the tiers are on real GPUs |
| DLSS-G in flight: status, generated frames per rendered frame, latency | the proxy's "DLSS frame generation" and frame time lines from a flight |
