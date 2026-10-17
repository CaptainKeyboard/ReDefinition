# Where the frame's time goes

**For:** anyone who wants to make KSP with ReDefinition and the graphics mods faster.
**You need:** [profiling.md](profiling.md) for the tools that measured this.
**You get:** what costs the frame in KSP, in the graphics mods and in ReDefinition, why,
and what can be improved, with the saving measured or the reason it is not yet.

The marks **[meas]**, **[src]**, **[doc]** and **[open]** are explained in
[writing-these-pages.md](writing-these-pages.md).

## The measured setup

Every figure here comes from the scene probe's flight on the launch pad, with the
vessel Kerbal X, unless a row says otherwise:

| What | Setting |
|---|---|
| Screen | 3440 x 1440, 120 Hz, V-Sync on |
| GPU | NVIDIA GeForce RTX 4090 |
| Upscaler | DLSS, mode *AA only*, model `High` |
| Raytracing | `Full`, 2 bounces, DLSS Ray Reconstruction |
| Frame generation | DLSS-G, on |
| Graphics mods | Deferred, Scatterer, EVE with volumetric clouds shown, Parallax Continued, TUFX, Waterfall, Firefly, DistantObject |
| Parallax | `densityMultiplier = 1.5`, `rangeMultiplier = 1.25` |
| EVE | `temporalUpscaling = x6` |
| KSP shadows | 25000 m, four cascades, resolution `VeryHigh` |

The frame takes 21 ms to 27 ms, depending on the clouds in view and on frame
generation. The figures in one table come from one run, so compare rows within a table.

## The GPU limits the frame

The GPU's load is 82 % to 87 %, the main thread's 33 % to 48 % of one core **[meas]**.
Unity's main thread spends 12.4 ms of a 24.9 ms frame in
`Gfx.WaitForPresentOnGfxThread` **[meas]**, the development player. That sampler is the
main thread waiting for Unity's render thread. `WaitForTargetFPS` shows the same time
and is no frame rate limit here. KSP's own limit is 120 fps.

The render thread in turn waits 12 ms to 18 ms a frame in the proxy, for a raytracing
slot **[meas]**, the proxy's wait report. It waits 0 ms for a frame in flight and 0 ms
for frame generation's inputs **[meas]**. The next section explains why.

## The raytracing slots hold the CPU back

A raytracing slot holds what one raytracing job sends to the GPU: its command list,
its constants, its part of the descriptor heap, its timestamps and the resources the
GPU still reads **[src]** `src/DxgiProxy/Raytracing.h`. Before the proxy writes into a
slot again, it waits on the CPU until the GPU finished the slot's last job
(`Raytracing::WaitForSlotLocked`) **[src]**. The wait itself is needed. Without it the
CPU would overwrite what the GPU still reads.

The proxy has three slots (`kSlots = 3`) **[src]**. A frame uses three jobs: the main
image, the ocean, and Ray Reconstruction **[meas]**, the GPU profiler. So each job
waits for the same job of the previous frame. The CPU cannot prepare a frame while the
GPU renders the one before. When the CPU then hands over its frame, the GPU has run
dry, which is the GPU's idle time of 13 % to 18 %.

Frame generation adds its own work to the same D3D12 queue. With it on, the wait for a
slot grows from 12 ms to 16.7 ms a frame **[meas]**.

The usual remedy is a pool that keeps each entry with the fence value at which the GPU
is done with it. The pool hands out the oldest entry that is done and makes a new one
if none is. Microsoft's MiniEngine does this for its command allocators
(`CommandAllocatorPool.cpp`, DirectX-Graphics-Samples, MIT licence) **[doc]**. The pool
grows once to what the game needs and then stops waiting. What it gains is **[open]**
until it runs. The descriptor heap is laid out for three slots today, so a growing pool
needs a larger heap or a part of its own per slot.

## What the GPU's frame is made of

The GPU profiler, the mod cost probe and RenderDoc each see a different part. The
column *Measured with* names the one a row comes from.

| Part | GPU ms a frame | Measured with |
|---|---|---|
| ReDefinition's raytracing, main image | 5.2 to 5.6 | the GPU profiler **[meas]** |
| ReDefinition's raytracing, ocean | 0.8 to 1.1 | the GPU profiler **[meas]** |
| DLSS Ray Reconstruction at full resolution | 4.3 to 4.5 | the GPU profiler **[meas]** |
| EVE's volumetric clouds and their shadows | 5.5 | the mod cost probe **[meas]** |
| Parallax's scatters | 3.8 to 5.1 | the mod cost probe **[meas]** |
| The sun's shadow maps for the main camera | 2.8 | the GPU profiler **[meas]** |
| The G-buffer of the main camera | 1.8 to 2.0 | the GPU profiler **[meas]** |
| Parallax's terrain | 1.4 | RenderDoc, without the proxy **[meas]** |
| Deferred's own passes | 0.9 | RenderDoc, without the proxy **[meas]** |
| Scatterer | 0.8 | RenderDoc, without the proxy **[meas]** |
| TUFX | 0.1 to 0.3 | the mod cost probe and RenderDoc **[meas]** |
| Waterfall, Firefly, DistantObject | under 0.2 | the mod cost probe **[meas]** |

Parallax's scatters are in the shadow maps and in the raytracing as well. With
`densityMultiplier` and `rangeMultiplier` at 1.0, the shadows lose 1.0 ms and the
raytracing 1.0 ms **[meas]**.

The reflection probe's camera draws a face of KSP's reflection probe in three of four
frames, each time with a shadow map. Those shadow maps cost about 0.1 ms a frame
**[meas]**, the mod cost probe with the probe drawn without shadows.

ReDefinition's own passes besides the raytracing cost under 1 ms together **[meas]**.
The vessel's depth and motion vector passes take under 0.04 ms in the game **[meas]**.
RenderDoc gives them 1.7 ms, because it times each of their 605 draws alone.

## What the main thread spends

| Part | ms a frame | Measured with |
|---|---|---|
| Waiting for the render thread | 12.4 | the development player **[meas]** |
| `Camera.Render`, all cameras | 12.4 | the development player **[meas]** |
| of it, culling over about ten cameras | 3.1 | the development player **[meas]** |
| of it, cameras that scripts render by hand | 6.1 | the development player **[meas]** |
| KSP's crew portraits, `Kerbal.kerbalAvatarUpdateCycle` | 5.8 | the perf monitor **[meas]** |
| ReDefinition's raytracing scene, `RtScene.Update` | 1.9 | the perf monitor **[meas]** |
| Physics | 1.3 to 1.8 | the perf monitor **[meas]** |
| The reflection probe | 0.8 | the development player **[meas]** |
| Allocations, `GC.Alloc` | 0.2 | the development player **[meas]** |

The crew portraits are most of the hand-rendered cameras. With their coroutine stopped,
the main thread's load drops from 41 % to 35 % and the frame stays as it is **[meas]**.
The GPU limits the frame, so they cost time only once the CPU does.

Of `RtScene.Update`, collecting the terrain takes 0.49 ms, the vessel 0.26 ms and the
scenery 0.23 ms. Looking up the mesh, the material and the key of each instance takes
about 0.8 ms **[meas]**. The perf monitor times each of the thousand calls a frame, so
that share is lower in the game.

## What makes the frame stutter

| Peak | Where | Measured with |
|---|---|---|
| 190 ms to 240 ms of GPU time | the first flight frame builds 804 acceleration structures | the proxy's slow frame lines **[meas]** |
| 621 ms | `RoundGauge.Start`, ReDefinition, at the flight's start | the perf monitor **[meas]** |
| 350 ms | Kopernicus, `ShadowMan.DelayedInit`, at the flight's start | the perf monitor **[meas]** |
| 132 ms | KSP, `PQS.UpdateSphere`, at the flight's start | the perf monitor **[meas]** |
| 111 ms | Scatterer, `Scatterer.DelayedInit`, at the flight's start | the perf monitor **[meas]** |
| 52 ms | KSP, `FlightCamera.LateUpdate`, in flight | the perf monitor **[meas]** |
| 42 ms | KSP's crew portraits, in flight | the perf monitor **[meas]** |
| 42 ms | `KscGrass.MapLawns`, ReDefinition, again and again in flight | the perf monitor **[meas]** |
| 26 ms to 61 ms of GPU time | Parallax, `ScatterManager.LateUpdate` | the GPU profiler **[meas]** |
| 22 ms | `RtCamera.OnPreCull`, ReDefinition, when new meshes are read | the perf monitor **[meas]** |
| 22 ms | Scatterer, `RaymarchedGodraysRenderer.OnPreCull` | the perf monitor **[meas]** |
| 13 ms to 18 ms each | RocketSoundEnhancement, Toolbar, EVE, Firefly, Kopernicus | the perf monitor **[meas]** |

The 804 acceleration structures hold 442 thousand triangles together, the largest
25 thousand **[meas]**. That is about 0.25 ms a build, whatever its size. Not zeroing
their buffers did not shorten it. At most 24 builds a frame removed the one long frame,
but the first seconds of the flight stayed as slow, because loading dominates them
**[meas]**. Neither is in the code.

Whether a peak in a script is the script's own or a garbage collection that started
inside it is **[open]**. The development player's recording shows it as `GC.Collect`.

## What frame generation costs

With frame generation generating, the rendered frame takes 26.4 ms, without it 24.4 ms
to 25.2 ms **[meas]**. Frame generation costs about 1.3 ms to 2 ms a rendered frame,
5 % to 8 %, and shows 70 frames a second instead of 40.

## What can be improved

None of these is in the code. *Saving* is measured where it carries **[meas]**.

| What | Saving | Costs image quality |
|---|---|---|
| A growing pool of raytracing slots, so the CPU prepares the next frame while the GPU renders | the GPU's idle 13 % to 18 % **[open]** | no |
| DLSS mode *Quality* instead of *AA only* | 21.3 ms to 16.2 ms, 31 % more fps **[meas]** | yes |
| Parallax `densityMultiplier` and `rangeMultiplier` 1.0 instead of 1.5 and 1.25 | 2.6 ms **[meas]** | yes |
| Two shadow cascades instead of four | 1.4 ms **[meas]** | yes |
| The shadow distance halved | 0.7 ms **[meas]** | yes |
| Raytracing bounces 1 instead of 2 | 0.7 ms **[meas]** | yes |
| EVE `temporalUpscaling` x9 instead of x6 | 0.5 ms **[meas]** | yes |
| The ocean's raytracing only with ocean in view | up to 1.1 ms **[meas]** | no |
| KSP's crew portraits rendered less often | 5.8 ms of the main thread and its 42 ms peaks **[meas]** | no |
| `RoundGauge.Start`, `KscGrass.MapLawns` and `RtCamera.OnPreCull` without their peaks | the stutter they cause **[meas]** | no |
| Fewer acceleration structure builds in the first flight frame | 190 ms to 240 ms in one frame **[meas]** | no |
| KSP's own shadow quality 3 instead of 4 | none **[meas]** | yes |
