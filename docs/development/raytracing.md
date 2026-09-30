# Raytracing

**For:** anyone building or changing ReDefinition's raytracing.
**You need:** `src/DxgiProxy` to hand, and a GPU with DirectX Raytracing tier 1.1 for
the tests.
**You get:** how raytraced lighting reaches KSP's frame, what each part does, how it is
tested without the game, and the plan it is built by.

Every claim here carries its mark: **[src]**, **[doc]**, **[meas]** or **[open]**
([writing-these-pages.md](writing-these-pages.md)).

## What raytracing replaces

With raytracing on, these parts of the lighting of the scene camera come from rays traced
through the scene instead of from Unity's approximations:

| Part | Unity's approximation | Raytraced |
|---|---|---|
| Sun shadows | cascaded shadow maps, a few kilometres deep | a shadow ray per pixel, soft by the sun's angular size, as far as the scene reaches |
| Ambient light | Deferred's reflection probe, blurred, the same for every point | one or more bounces of diffuse light, from the sky, the sun and lit surfaces |
| Ambient occlusion | none | the share of the sky a point sees |
| Reflections | the reflection probe, or Deferred's screen-space reflections | rays into the scene, sharp or rough by the surface's smoothness |
| Emissive surfaces | light only themselves | light what is around them |

Transparent and volumetric things keep their own rendering: plumes, reentry effects,
particles, clouds, the atmosphere and Scatterer's ocean. They take the raytraced shadows
where they receive Unity's screen-space shadow, and are not in the scene the rays see.

## How the frame goes

KSP renders with Direct3D 11, which has no raytracing. The proxy already runs a Direct3D 12
device beside Unity's, shares textures with it and runs compute passes on it
([shared-foundation.md](shared-foundation.md)). Raytracing runs there, with ray queries in
compute shaders (DirectX Raytracing 1.1, shader model 6.5) **[doc]**.

1. Unity renders frame N as usual. With Deferred, the scene camera fills its G-buffer:
   albedo and occlusion, specular colour and smoothness, the world normal, and emission
   **[src]**.
2. In the scene camera's `OnRenderImage`, where the upscaler already hands work to the
   proxy, a command buffer copies depth, the G-buffer and the motion vectors into the
   raytracing inputs. A render event then starts the raytracing on Direct3D 12. Only a
   render event issued from there sees the current frame; one issued at a camera event
   sees the previous one **[meas]** (Unity 2019.4.18f1 player built like KSP, 598 of 598
   frames).
3. The proxy traces the rays, denoises the signals and copies them back into Unity's
   textures at the next frame's dispatch.
4. Unity renders frame N+1. Its lighting reads the raytraced signals of frame N, carried
   over to N+1 by depth and the two frames' camera matrices. The previous frame's
   positions are corrected for KSP's floating origin and Krakensbane, as the motion vectors
   already are ([upscaler.md](upscaler.md)).

The G-buffer is not bound any more by `OnRenderImage`: a command buffer copies it at
`CameraEvent.AfterGBuffer`, in the camera's own command stream **[meas]** (test player).
Depth, read through `_CameraDepthTexture`, is still there.

The raytraced light is therefore one frame old, carried over. The denoisers accumulate
over frames anyway, so the delay adds no lag of its own to what they already have.

## Where the raytraced light enters Unity's lighting

| Signal | Enters through | Why there |
|---|---|---|
| Sun visibility | ReDefinition's shader for Unity's screen-space shadows (`GraphicsSettings.SetCustomShader`, `BuiltinShaderType.ScreenSpaceShadows`) | every surface that receives the sun's shadow reads that mask, forward and deferred; EVE's cloud shadows and Scatterer's eclipses multiply into it afterwards, at `LightEvent.AfterScreenspaceMask` **[src]** |
| Diffuse light, occlusion, reflections | ReDefinition's shader for Unity's deferred reflections, in place of Deferred's while raytracing is on | Deferred adds its ambient light and the probe's reflection in that pass **[src]** |

Deferred sets its own shaders for Unity's deferred shading and deferred reflections
**[src]**. ReDefinition's reflections shader keeps what Deferred's does besides ambient
light and the probe: the IVA's rotated normals, the LDR encoding and the alpha of the
lighting buffer. It is derived from Deferred's shader, which is GPL 3.0 like ReDefinition,
and credited in [credits-and-licences.md](../credits-and-licences.md). When raytracing is
off, Deferred's shader is set again.

The screen-space shadows shader is derived from Unity's built-in one, which is MIT **[doc]**.
For every camera but the scene camera it does what Unity's does.

Without Deferred, KSP renders forward and has no G-buffer. Raytraced sun shadows still
work then, as forward shading reads the same mask; the proxy takes the normals from the
depth. Unity fades shadows out at the shadow distance, the raytraced ones too; Deferred
turns that fade off, so with Deferred they reach as far as the scene **[meas]** (test
player). Diffuse light, occlusion and reflections need Deferred.

## What the rays see

The scene is a set of meshes, each with a transform per frame. The C# side collects them,
the proxy builds the acceleration structures: one per mesh, and one over all of them each
frame.

| Source | Taken from | Changes |
|---|---|---|
| Vessel parts | `MeshFilter.sharedMesh`, readable as KSP loads them **[src]** | skinned meshes baked when their pose changes |
| KSC buildings | the renderers under each `PQSCity` | readability checked at run time; unreadable meshes copied on the GPU **[open]** |
| Terrain | each visible `PQ`'s mesh, from `PQSMod.OnQuadBuilt` and `OnQuadDestroy` **[src]** | index buffer swapped when a neighbour's subdivision changes **[src]**; Parallax's finer copy near the camera used in place of the quad **[src]** |
| Parallax scatter | the instance buffers Parallax fills on the GPU **[src]** | instances built into the scene on the GPU, as their count is only known there |
| Planets seen from far | spheres, tested by formula | none |

Left out: anything transparent or additive (Waterfall, Firefly, particles, translucent
parts), EVE's clouds, Scatterer's ocean and atmosphere, and the UI.

KSP moves the world with its floating origin and, under Krakensbane, every physics frame
without an event **[src]**. The transforms are therefore taken every frame, relative to the
camera, so that the scene near the camera keeps full float precision.

Parallax displaces terrain on the GPU by up to about a metre near the camera **[src]**
**[open]** (the amount). The rays start a little above the drawn surface to cover that.

## Surfaces the rays hit

A ray that hits something shades it from what the proxy holds per mesh and per material:

* the normal and the texture coordinate, from the mesh;
* the albedo: the material's colour times a small copy of its main texture, 64 by 64
  pixels, which Unity renders once per material into a texture array;
* specular colour and smoothness, converted from KSP's shaders as Deferred converts them
  **[src]**;
* emission.

A ray that hits nothing takes the sky's light from a capture of Deferred's reflection
probe, and the sun's disc for sharp reflections.

## Denoising

Each signal is traced with few rays per pixel and is noisy. A denoiser turns it into a
smooth one:

| Denoiser | Runs on | Needs | Takes |
|---|---|---|---|
| ReDefinition's own, after SVGF: accumulation over frames, a variance estimate, an à-trous filter | every GPU with raytracing | nothing | every signal separately |
| AMD FSR Ray Regeneration 1.2 | Radeon RX 9000 and newer, Windows 11 **[doc]** | the player's `amd_fidelityfx_denoiser_dx12.dll` and loader | the signals separately; FSR upscales afterwards |
| NVIDIA DLSS Ray Reconstruction | GeForce RTX **[doc]** | the player's `sl.dlss_d.dll` and `nvngx_dlssd.dll` | the whole frame with its noise, in place of DLSS Super Resolution **[doc]** |

ReDefinition gives Ray Reconstruction its own Streamline viewport, apart from frame
generation's. It runs where Streamline is set up, which is where DLSS frame generation
runs **[open]** (on GPUs without DLSS frame generation).

With DLSS Ray Reconstruction the signals go into the lighting undenoised. Ray
Reconstruction then denoises and upscales the finished frame in one step, where DLSS Super
Resolution would otherwise run. It gets the albedo, specular albedo, normal, roughness and
the reflections' hit distance as guides.

NVIDIA's NRD denoisers are left out: their licence forbids use that makes them subject to
an open-source licence **[doc]**.

## Testing without the game

Most of it is tested without starting KSP. What is left for the game is how it plays with
the installed mods live.

| Level | What it runs | What it proves |
|---|---|---|
| Raytracing harness | a native program that loads the proxy as Unity does, rasterises a G-buffer in Unity's layout and runs the raytracing through the proxy's own interface | the acceleration structures, every signal against a formula or a reference, the denoisers, floating-origin shifts, timings on the GPU |
| Scenes from KSP | meshes exported from KSP's own files with UnityPy: the launch pad, the runway, the VAB, the SPH, part meshes | real geometry, real triangle counts, real scale |
| C# unit tests | the collection and change tracking of meshes, the packing of packets, the carrying over of signals between frames | the managed side's logic |
| Unity test player | a Unity 2019.4.18f1 player built like KSP's, with forward rendering, Unity's deferred lighting and Deferred's own shader bundle from `GameData`, and the proxy beside it (`tools/build_rt_player.py`) | the shaders in Unity's lighting, the timing of the render events, the one-frame carry-over |

The harness checks:

1. **Shadows.** A box on a plane, the sun at a known angle: the shadowed pixels match the
   shadow computed on the CPU, apart from a one-pixel edge.
2. **White furnace.** A plane under a uniform sky of radiance L: the diffuse light
   converges to L, within 2 %.
3. **Occlusion.** A point in an inner corner sees less sky than one on the open plane.
4. **Mirror.** A plane of smoothness 1 reflects a box in the box's colour.
5. **Denoising.** A camera path: the denoised image against a reference accumulated over
   1024 frames, by mean squared error, and its flicker between frames.
6. **Floating origin.** The whole scene and the camera shifted by 10 km between two
   frames: the signals do not change.
7. **Churn.** Meshes added and removed every frame, 20 000 instances.
8. **Timings.** Each pass, measured on the GPU, per render resolution.

## Plan

Each stage ends with its checks passing, a review, and one commit.

| Stage | Builds | Done when |
|---|---|---|
| 1 | The raytracing service in the proxy: raytracing tier check, geometry pool, acceleration structures, meshes, materials and instances through exports, the frame packet, DXIL shaders compiled at build time and embedded; the harness with its analytic scenes | checks 1, 6 and 7 pass |
| 2 | The signals: sun visibility, occlusion, diffuse light with bounces, reflections, emission, the sky capture | checks 2, 3 and 4 pass, and the KSP scenes render without NaNs |
| 3 | ReDefinition's denoiser; the adapters for Ray Regeneration and Ray Reconstruction | check 5 passes; Ray Reconstruction runs in the harness on NVIDIA's DLLs |
| 4 | The Unity side free of KSP: collection, per-frame instances, G-buffer capture, the two shaders, the carry-over; the Unity test player | the test player shows raytraced shadows and light, and its checks pass |
| 5 | KSP: parts, KSC, terrain, floating origin, the sun, cameras, settings, profiles, the diagnostics log | the C# tests pass, and a log line per frame part says what went into the scene |
| 6 | Compatibility with every installed mod (table below) | each row has its handling, marked **[src]** |
| 7 | Performance, the player page, one flight's checklist | the timings fit the budget below |

Budget on the RTX 4090, 2560 by 1440 with DLSS Quality: everything together within 4 ms
of GPU time **[open]**.

## Installed mods

| Mod | What raytracing does with it |
|---|---|
| Deferred | provides the G-buffer; its reflections shader is replaced while diffuse light is raytraced, and set back afterwards |
| Parallax Continued | its finer terrain copy near the camera is in the scene; its displacement is covered by the ray offset; its scatter is added from its GPU buffers |
| Scatterer | its ocean and atmosphere stay as they are; its shadow changes stay; its eclipse modulation of the shadow mask applies after the raytraced shadow |
| EVE, Stock Volumetric Clouds | clouds stay out of the scene; cloud shadows still multiply into the shadow mask |
| Waterfall, Firefly | out of the scene, as transparent effects |
| TUFX | post-processing runs after, on the lit frame |
| Kopernicus | the sun is the star KSP lights the scene with |
| PlanetShine | its planet light and raytraced diffuse light both brighten the dark side; [open] whether to turn its light down while diffuse light is raytraced |
| DistantObject | its flares and labels are not in the scene |
| FreeIva, ThroughTheEyes | raytracing follows the scene camera; the IVA camera keeps Unity's lighting [open] |
| JSI (RasterPropMonitor), VesselView | their own cameras keep Unity's lighting |
| KSPCommunityFixes, Shabby, KSPTextureLoader | meshes and textures reach the scene as the game has them [open] |
| ReDefinition's upscaler and frame generation | raytracing runs at the render resolution, before the upscaler; with DLSS Ray Reconstruction, Ray Reconstruction takes DLSS's place |
