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
| Upscaler | DLSS, mode *AA only*, model *L* |
| Raytracing | `Full`, 2 bounces, DLSS Ray Reconstruction |
| Frame generation | DLSS-G, on |
| Graphics mods | Deferred, Scatterer, EVE with volumetric clouds shown, Parallax Continued, TUFX, Waterfall, Firefly, DistantObject |
| Parallax | `densityMultiplier = 1.5`, `rangeMultiplier = 1.25` |
| EVE | `temporalUpscaling = x6` |
| KSP shadows | 25000 m, four cascades, resolution `VeryHigh` |

The frame takes 21 ms to 27 ms, depending on the clouds in view and on frame
generation. The figures in one table come from one run, so compare rows within a table.
Sections that name the RTX A4500 were measured on a second machine, below.

## On an RTX A4500

The RTX A4500 is an Ampere GPU of about an RTX 3070's speed. It has no DLSS frame
generation of its own, so without dlssg_for_sm86 the proxy runs FSR's
(`ReDefinitionProxy.log`) **[meas]**. The screen is
2560 x 1440 at 120 Hz, DLSS in mode *Quality* with the model *Default*, raytracing
`Full` with 2 bounces, and the same mods. The frame rate is the rendered one at the
launch pad, the proxy's report **[meas]**:

| Profile | Raytracing | Rendered fps | Shown fps | Load |
|---|---|---|---|---|
| Medium | off | 63.5 | 127, frame generation on | main thread 84 %, GPU 65 % |
| Medium, DLSS *AA only* | off | 56.1 | 56 | GPU 95 % |
| Medium | `Full` | 43.1 | 43 | GPU 93 % |
| Ultra | off | 54.3 | 54 | GPU 82 % |
| Ultra | `Full` | 33.6 | 34 | GPU 71 % |

With the raytracing at 43 fps, frame generation stays off. FSR's frame generation is made
for 60 fps and more, so the proxy's minimum (`fgMinimumFps`) is 60 for it, and below nine
tenths of it frame generation holds off **[meas]** (`ReDefinitionProxy.log`: "Frame
generation held off ... minimum 60"). Medium without the raytracing waits for the main
thread, not for the GPU.

## The GPU limits the frame

The GPU's load is 82 % to 87 % by the proxy's report, the main thread's 33 % to 48 % of
one core **[meas]**. The proxy samples Windows' counter of the GPU's busiest 3D engine
once a second (`src/DxgiProxy/Latency/LoadMonitor.h`) **[src]**. NVIDIA's `nvidia-smi` gives the
GPU 95 % to 98 % **[meas]**. Why the two differ is **[open]**.

Unity's main thread spends 12.4 ms of a 24.9 ms frame in
`Gfx.WaitForPresentOnGfxThread` **[meas]**, the development player. That sampler is the
main thread waiting for Unity's render thread. `WaitForTargetFPS` shows the same time
and is no frame rate limit here. KSP's own limit is 120 fps.

The render thread in turn waits 12 ms to 18 ms a frame in the proxy, for a raytracing
slot **[meas]**, the proxy's wait report. It waits 0 ms for a frame in flight and 0 ms
for frame generation's inputs **[meas]**. The next section explains why that does not cost
frames.

## The raytracing slots and the render thread's wait

A raytracing slot holds what one raytracing job sends to the GPU: its command list,
its constants, its part of the descriptor heap, its timestamps and the resources the
GPU still reads **[src]** `src/DxgiProxy/Raytracing/Raytracing.h`. Before the proxy writes into a
slot again, it waits on the CPU until the GPU finished the slot's last job
(`Raytracing::WaitForSlotLocked`) **[src]**. The wait itself is needed. Without it the
CPU would overwrite what the GPU still reads.

The proxy has three slots (`kSlots = 3`) **[src]**. A frame uses three jobs: the main
image, the ocean, and Ray Reconstruction **[meas]**. So each job waits for the same job
of the previous frame, 12 ms to 18 ms a frame **[meas]**. Frame generation adds its own
work to the same D3D12 queue. With it on, the wait grows from 12 ms to 16.7 ms
**[meas]**.

The wait does not cost frames. With twelve slots it fell to 0 ms, and the frame stayed
at 24.6 ms to 24.8 ms against 24.4 ms to 25.2 ms with three **[meas]**. The GPU was busy
while the render thread waited. The usual remedy for a real wait of this kind is a pool
that keeps each entry with the fence value at which the GPU is done with it, as
Microsoft's MiniEngine does for its command allocators (`CommandAllocatorPool.cpp`,
DirectX-Graphics-Samples, MIT licence) **[doc]**. Here it would only cost video memory
and a frame of latency, so the proxy keeps three slots.

## What the GPU's frame is made of

The GPU profiler, the mod cost probe and RenderDoc each see a different part. The
column *Measured with* names the one a row comes from. The GPU profiler's figures here
were taken while its markers sat on the reflection probe's camera as well, which left
KSP's reflection probe out and the raytraced scene dark (see
[profiling.md](profiling.md)). A run without them gave the raytracing 6.05 ms
**[meas]**.

| Part | GPU ms a frame | Measured with |
|---|---|---|
| ReDefinition's raytracing, main image | 5.2 to 5.6 | the GPU profiler **[meas]** |
| ReDefinition's raytracing, ocean | 0.8 to 1.1 | the GPU profiler **[meas]** |
| DLSS Ray Reconstruction at full resolution | 4.3 to 4.5 on the RTX 4090; 8.7 to 9.0 on the RTX A4500 at 1707x960 to 2560x1440 | the GPU profiler, the proxy's timestamps **[meas]** |
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

ReDefinition's three peaks and the first flight frame's builds are built away: see
[What is built](#what-is-built).

## What frame generation costs

With frame generation generating, the rendered frame takes 26.4 ms, without it 24.4 ms
to 25.2 ms **[meas]**. Frame generation costs about 1.3 ms to 2 ms a rendered frame,
5 % to 8 %, and shows 70 frames a second instead of 40.

## What the raytracing does a second time

When the upscaler and the raytracing run, some of KSP's and the mods' work is done
twice:

| Work | Done twice | Cost | Source |
|---|---|---|---|
| Antialiasing | no: KSP's MSAA goes to 0 while the upscaler runs, and so does the antialiasing of Scatterer, TUFX, Deferred and Kerbal Frame Generator | none | **[src]** `src/Upscaler/Rig/QualityOverrides.cs` |
| The sun's shadow maps | yes: Unity renders them every frame, with Scatterer's 25000 m and four cascades | 2.8 ms of the GPU | **[src]**, **[meas]** |
| Ambient occlusion | yes: the raytracing's diffuse light shades corners already, and Deferred's and TUFX's ambient occlusion shade them again | 0.1 ms to 0.3 ms of the GPU | **[meas]** |
| Screen-space reflections | no: Deferred's go off while the raytraced reflections run | none | **[meas]**, `KSP.log` |
| Ambient light | no: the raytracing replaces Unity's in the reflections pass | none | **[src]** `RtLighting` |
| The reflection probe | no: the raytracing reads its sky from the probe, and Deferred needs it as well | 0.1 ms of the GPU, 0.8 ms of the main thread | **[meas]** |

With the raytracing on, the shader that writes the sun's screen-space shadow mask
takes the visibility from the rays (`RtShadow` in
`unity/Assets/ReDefinition/Shaders/ReDefinitionScreenSpaceShadows.shader`) **[src]**.
It uses the shadow maps only where the raytraced frame did not see a point, such as an
edge just uncovered. The shadow maps cannot simply go. If Unity renders no shadows, it
skips the screen-space mask as well, and the rays' shadow enters the frame through that
mask **[src]**. Other readers of the shadow maps are Scatterer's godrays
(`RaymarchScatteringOcclusion`), Scatterer's caustics mask, EVE's cloud shadows and the
vessel's shadow for frame generation **[src]**.

### Compare the frame with and without

Each variant was shot at the same moment as a shot without a change
(`-redefinitionProbeShotVariant`, [profiling.md](profiling.md)) **[meas]**. The table
gives the mean difference of the 3D scene after a blur of 6 pixels, in levels of 255.
Two shots without a change differ by 0.27, from the grass in the wind, the clouds and
the raytracing's noise.

| Variant | Difference | What changes in the image |
|---|---|---|
| none, a second shot | 0.27 | nothing |
| two shadow cascades | 0.27 | nothing |
| a minimal shadow map: one cascade, 200 m, low resolution | 0.27 | nothing in the 3D scene; the crew portraits lose their shadows and get brighter |
| Deferred's ambient occlusion off | 1.16 | the grass and the rocket's feet a little brighter, the grass a little flatter |
| TUFX's ambient occlusion off | 0.6 darker in the scene's middle | hardly visible side by side |

So at the launch pad by day, the 3D scene does not need KSP's shadow maps when the
raytracing runs. Only the crew portraits do. Their cameras draw the kerbals in their
small interior with Unity's shadows. A minimal shadow map for the scene keeps the mask
and costs little, and a small shadow map of their own would serve the portraits. What
the minimal one saves is **[open]**. Two cascades save 1.4 ms **[meas]**.

An evening sun at the pad (`-redefinitionProbeDays 0.05`), the vessel in orbit and the
pad under a cloud's shadow a day later (`-redefinitionProbeDays 1`, 0.14 to 0.15 levels
against a noise of 0.155) show two cascades as four as well **[meas]**. Terrain far
away is **[open]**. With the raytracing off the shadow maps are needed in full, and
the cascades stay as KSP sets them.

Deferred takes its ambient light from KSP's reflection probe, and the raytracing its
sky. With KSP's reflections off in `settings.cfg` and Deferred installed, the probe
stayed off in flight: the pad's frame 30.6 of 255 without the raytracing and 18.9 with
it, against 58.7 with the probe **[meas]**. Deferred raises the refresh in the main
menu and switches the probe on, and KSP's `FlightReflectionProbe.Enable(true)` leaves
the earlier switch-off pending **[src]**, a decompile of KSP 1.12.5. ReDefinition
clears it (`src/Scenery/ReflectionProbeFix.cs`), and the pad gets 58.7 **[meas]**.
Without Deferred, reflections off keep the probe off, and the raytracing has no sky
**[src]**.

## What is built

Each was measured on its own against the build before it, at the launch pad, and its
shots compared with shots without it.

| What | Measured | Image |
|---|---|---|
| Two shadow cascades while the rays shade the sun (`RtCamera`, `KeepCascades`) | 23.96 ms to 22.43 ms a frame, two runs each, about 7 % more frames **[meas]** | the pad by day 0.25 levels from four cascades, noise 0.27; the evening sun 0.16, noise 0.14; the vessel in orbit 0.79, noise 0.91; under a cloud's shadow 0.15, noise 0.155 **[meas]** |
| The ocean's raytracing takes the frame's scene instead of building it again (`Raytracing::ReuseSceneLocked`) | its scene build 0.30 ms to 0 ms at the pad, 0.11 ms to 0 ms at sea **[meas]** | at sea 0.25 levels, under the noise of 0.46 **[meas]** |
| New meshes read and handed over at most 2 ms a frame (`RtScene`, `UploadBudgetMs`) | the flight's first seconds: `RtCamera.OnPreCull` at most 107.6 ms to 26.3 ms; the proxy's largest mesh build 190 ms to 240 ms to 42 ms **[meas]** | 0.27 levels after the scene settled, the noise **[meas]** |
| The space centre's lawns collected a few milliseconds a frame, their masks kept (`KscGrass.CollectLawns`) | the second map in flight: 71 ms to 15 ms of the main thread, its largest step 42 ms to 7.5 ms **[meas]** | 0.22 levels, under the noise **[meas]** |
| DLSS's model *Default*, NVIDIA's own per mode, in place of preset L in every mode (`DlssModels`) | on the RTX A4500, Ultra at the pad: without the raytracing 23.3 ms to 18.4 ms, with it 34.9 ms to 29.8 ms **[meas]** | NGX takes preset K for *AA only*, *Quality* and *Balanced*, M for *Performance*, L for *Ultra Performance* (`ReDefinitionProxy.log`); K against L in a still shot the same **[meas]** |
| Parallax's clumps under the space centre's ground left out of the rays (`KscGrass.RaysBuffer`) | on the RTX A4500: the rays 6.66 ms to 6.1 ms, the frame 34.85 ms to 34.3 ms **[meas]** | every tile within 0.1 of a thousandth **[meas]**; the clumps stand on the terrain under the ground, where no ray reaches them |
| The round gauge's picture read back without waiting for the GPU (`TexturePixels.OfSpriteAsync`) | 621 ms in one frame at the first flight to none among the costliest methods **[meas]** | the same pixels, 138 x 138, none differing **[meas]** |

## What was tried and left out

| What | Why |
|---|---|
| Twelve raytracing slots | the render thread's wait went, the frame stayed **[meas]**; on the RTX A4500 six slots took the wait from 4.31 ms to 0 ms, and the frame stayed at 29.8 ms to 29.9 ms **[meas]** |
| The raytracing on a D3D12 queue of its own, at high priority (`rtOwnQueue`) | on the RTX A4500 with FSR's frame generation, no change; the render thread waited 0.5 ms for a slot **[meas]**. With DLSS frame generation, which shares the proxy's queue, it is **[open]** |
| ReDefinition's own denoiser and DLSS in place of Ray Reconstruction | on the RTX A4500 as fast: 33.5 ms against 33.2 ms to 33.5 ms; Ray Reconstruction is the sharper, in the grass 10.3 against 6.9 in high frequencies **[meas]** |
| KSP's crew portraits drawing only their interiors' layers, or with 10 m of shadows | a portrait render stayed at 10.7 ms to 12.2 ms **[meas]** |
| KSP's crew portraits rendered less often | with them stopped, the frame stayed as it was **[meas]**; the render's time is most likely the main thread waiting for the render thread **[open]** |
| At most 24 acceleration structure builds a frame in the proxy | the flight's first seconds stayed as slow **[meas]**; the mod's upload budget spreads them instead |
| Not zeroing the mesh buffers | the build stayed at 190 ms to 240 ms **[meas]** |
| The ocean's raytracing only with ocean in view | its own cost is 0.06 ms of rays once its scene is reused **[meas]**, and the ocean is in view at the space centre |
| A minimal shadow map with the raytracing | with a short shadow distance, which stays as it is; with the distance kept, one cascade at low resolution took nothing off the frame (23.8 ms to 23.8-24.0 ms) and darkened it by 0.19 levels against a noise of 0.22 **[meas]** |
| Deferred's and TUFX's ambient occlusion off while the raytracing runs | 0.1 ms to 0.3 ms, under the frame's noise, and a visible change: brighter grass and corners **[meas]** |

## What may still make the frame faster

The GPU limits the frame, so only less work on the GPU makes it faster. The
raytracing (6.05 ms) and Ray Reconstruction (4.3 ms to 4.5 ms) are about half of it
**[meas]**. None of these costs image quality, and none is built yet:

| What | Why it may help | What stands in the way |
|---|---|---|
| Shader execution reordering for the bounces | It sorts rays that went apart by what they hit, so that a GPU's threads run the same shading together. It is part of DirectX Raytracing 1.2 and Shader Model 6.9 **[doc]** ([Microsoft](https://devblogs.microsoft.com/directx/shader-execution-reordering/)). | The rays are inline ray queries (`Raytracing.hlsl`) **[src]**. Reordering works on hit objects, and whether one made from a ray query gains anything here is **[open]**. |
| The raytracing beside Unity's drawing instead of after it | The GPU is 82 % to 87 % busy by Windows' 3D engine counter, 95 % to 98 % by `nvidia-smi` **[meas]**. Work that runs alongside could fill the rest. | The rays need the frame's G-buffer **[src]**. Beside the frame's drawing they could only trace the frame before, a frame of latency. The gain is at most the GPU's idle share and is **[open]**. |
| A cheaper Ray Reconstruction model | none | Streamline 2.14.16 has only transformer models for Ray Reconstruction. Presets A to C are removed (`sl_dlss_d.h`) **[src]**. |

Opacity micromaps for cut-out surfaces (grass, ferns, leaves) are built and off by
default (`rtOpacityMicromaps`,
[raytracing.md](raytracing.md#cut-out-surfaces-and-opacity-micromaps)):

| Question | Answer |
|---|---|
| What could they give | The rays test every candidate hit on such a surface against its texture in the shader. Micromaps let the GPU's ray tracing units decide most of those hits without it. In *Indiana Jones and the Great Circle*, the main trace went from 7.90 ms to 3.58 ms on an RTX 5080 **[doc]** ([NVIDIA](https://developer.nvidia.com/blog/path-tracing-optimizations-in-indiana-jones-opacity-micromaps-and-compaction-of-dynamic-blass/)). On the RTX A4500, cut-out surfaces tested without their texture (`rtCutoutTest=1`) save 1.25 ms of the rays, and taken as opaque (`rtCutoutTest=2`), the most micromaps could give, 1.6 ms **[meas]** |
| What they gave on Ampere | Ampere GPUs have no micromap unit, which came with Ada, the RTX 40 series **[doc]** ([NVIDIA](https://images.nvidia.com/aem-dam/Solutions/geforce/ada/nvidia-ada-gpu-architecture.pdf)). The RTX A4500 rendered 10 % fewer frames a second with them **[meas]** ([benchmarks.md](benchmarks.md)) |
| What they give on Ada | **[open]** until an RTX 40 flies them |

## What is left

Each of these costs image quality, so the choice is the player's:

| What | Saving | Costs image quality |
|---|---|---|
| DLSS mode *Quality* instead of *AA only* | 21.3 ms to 16.2 ms, 31 % more fps **[meas]**; the raytracing and Ray Reconstruction run at the render resolution | yes |
| Parallax `densityMultiplier` and `rangeMultiplier` 1.0 instead of 1.5 and 1.25 | 2.6 ms **[meas]** | yes |
| The shadow distance halved | 0.7 ms **[meas]** | yes |
| Raytracing bounces 1 instead of 2 | 0.7 ms **[meas]** | yes |
| EVE `temporalUpscaling` x9 instead of x6 | 0.5 ms **[meas]** | yes |
| KSP's own shadow quality 3 instead of 4 | none **[meas]** | yes |
| Frame generation below 60 rendered fps (`fgMinimumFps`), on the RTX A4500 with FSR's | Medium with `Full` raytracing shown at about twice its 43 fps **[open]** | yes: AMD makes it for 60 fps and more, its artefacts and its lag grow below |
| The profiles' DLSS mode *Quality* instead of *AA only*, on the RTX A4500 | Medium without the raytracing 56.1 fps to 63.5 fps **[meas]** | yes |
