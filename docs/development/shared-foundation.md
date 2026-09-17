# The shared foundation for mods

Some things exist only once in a KSP game: the `dxgi.dll` next to `KSP_x64.exe`, the
jitter of a camera, the motion vectors of a frame, the swapchain. Mods that each build
their own collide. ReDefinition provides them once, for every mod, through a public
interface in `ReDefinition.dll`. Marks: **[src]** read from source, **[doc]** a vendor's
or project's statement, **[meas]** measured, **[open]** not verified.

## Principles

* **Works without the mods' help.** Players get the upscaler, frame generation and the
  settings with every mod as it is; compatibility is ReDefinition's side
  ([reference/graphics-mod-compatibility.md](../reference/graphics-mod-compatibility.md)).
* **No hard dependency.** A mod reads the frame's state from shader globals that are
  zero without ReDefinition, or calls the interface through a wrapper file that does
  nothing without it (`docs/modders/examples/ReDefinitionApi.cs`). Where Unity
  has a convention -- `nonJitteredProjectionMatrix`, the `MotionVectors` pass,
  `_CameraMotionVectorsTexture` -- ReDefinition follows it, and a mod that follows it
  works without knowing ReDefinition.
* **Takes nothing away.** A mod's own implementation stays as it is. What ReDefinition
  switches off in another mod it switches off only while a graphics profile is chosen,
  and gives back (HostStack).
* **Useful on its own.** Each part serves ReDefinition's own features too.

## What exists once

| Resource | Why only once | Evidence | Offered |
|---|---|---|---|
| The way to Direct3D 12 | Windows loads one `dxgi.dll` from next to the executable | ReShade's `dxgi.dll` and ReDefinition's exclude each other | Direct3D 12: capabilities, shared textures, compute passes |
| Jitter and temporal history | A camera renders with one projection | Scatterer #231 and #252, Parallax #30, TUFX #40: temporal filters on top of each other **[doc]** | The frame's jitter, render and display size |
| Motion vectors | One buffer per camera; Scatterer installs its motion vector shader globally **[src]** | Geometry moved in a vertex shader has none -- Scatterer's ocean (`OceanWhiteCapsModProj3.shader`), Parallax's grass **[src]** | Contributions into the motion vectors every upscaler and frame generation read |
| Camera cuts | KSP raises no event for them | TUFX #23: flickering after a revert **[doc]** | A reset for the frame, from ReDefinition's detection and from any mod |
| Floating origin shifts | One origin | Unity's camera motion vectors project the current world position with the previous frame's view-projection **[src]** | The shifts of the frame |
| The image after upscaling, before the UI | One presenter | TUFX's effects after the upscaler (TufxPostProcessing) | A hook on the upscaled image and an overlay after it |
| Global quality settings | One `QualitySettings` | Scatterer sets shadow projection and distance, Parallax requires terrain detail, Deferred writes reflection settings at every scene load **[src]** | Requirements in registrations (existing); the chosen profile and its changes |

## Stage 1: the frame

### State, for every mod

Set before the first scene camera of a frame culls (`Camera.onPreCull` for a camera of the
3D stack, or, where none culled, when the rig presents), as shader globals and as
properties of `ReDefinition.Api.Frame` (`SharedFrame`):

| Global | Contents | Without a rig |
|---|---|---|
| `_ReDefinition_Frame` | upscaler active, frame generation active, history reset this frame, interface version | 0, 0, reset, version |
| `_ReDefinition_RenderSize` | width, height, 1/width, 1/height | the screen |
| `_ReDefinition_DisplaySize` | the same for the output | the screen |
| `_ReDefinition_Jitter` | the jitter in pixels at render size, x and y; in normalized device coordinates, x and y | 0 |
| `_ReDefinition_OriginShift` | the sum of `offset` the floating origin shifted by since the last frame, and 1 if it shifted | 0 |

**The floating origin [src].** KSP moves the active vessel, the camera and nearby objects
by `-offset`, and bodies and landed or packed vessels by `-(offset + nonFrame)`, then
raises `onFloatingOriginShift(offset, nonFrame)` (`FloatingOrigin.setOffset`, as
KSPCommunityFixes' `FloatingOriginPerf` reproduces it). Unity's camera motion vector pass
takes the current world position from depth and projects it with `_PreviousVP`, the
previous frame's; the object pass projects `_PreviousM` with `_PreviousVP`
(`Internal-MotionVectors.shader`). Across a shift, a renderer drawn with its object pass
keeps consistent motion vectors; a pixel that gets only camera motion -- a renderer in
`MotionVectorGenerationMode.Camera`, one whose transform did not change, the sky -- is
off by the shift. `Frame.OriginShift` and `Frame.BodyShift` carry both sums for a mod's
own reprojection. **[open]** how large that error is in flight.

### History reset

`Frame.HistoryReset` is true for the first frame of a discontinuous view. ReDefinition
detects a change of the flight camera's target, its parent and the IVA kerbal; a mod
reports its own cut with
`Frame.RequestHistoryReset(reason)`. A reset decided before the frame's first scene camera
reaches every mod in that frame; one found while the frame renders resets the upscaler and
frame generation in that frame and reaches the mods in the next, without a second reset of
the upscaler (`HistoryResets`, `CameraCuts`). Handlers registered with
`Frame.RegisterHistoryReset` are called when a reset is decided.

### Hooks

Each takes a `System.Action` of Unity types, so a wrapper reaches it by reflection. A
handler that throws is removed and logged once; the others run on (`HookList`). The rig
calls them (`UpscalerRig.Hooks.cs`).

| Hook | Called | Draws |
|---|---|---|
| `Hooks.RegisterMotionVectors(Action<CommandBuffer, RenderTexture motionVectors, RenderTexture depth, Camera scene>)` | every frame as the scene camera culls, with the frame's state decided; its commands run in the scene camera's capture at `BeforeImageEffects`, after Unity's motion vectors and EVE's clouds are in | motion vectors in Unity's encoding: `RGHalf`, current minus previous viewport position (`Internal-MotionVectors.shader`); depth is the scene's raw depth at render size |
| `Hooks.RegisterAfterUpscaling(Action<CommandBuffer, RenderTexture image, Camera scene>)` | every frame the rig presents, after the upscaler, before TUFX's effects after it and before frame generation's HUD-less copy | onto the upscaled image at display size; interpolated with the scene |
| `Hooks.RegisterOverlay(Action<CommandBuffer, Camera scene>)` | every frame, on a camera of ReDefinition's after the last camera that draws the scene, made only while a handler is registered | onto the backbuffer at display size, not upscaled, and UI to frame generation -- lines and markers in the world |

### Profiles

`Profiles.Current` is the chosen graphics profile's name, null with none;
`Profiles.RegisterChanged(Action<string>)` is called when it changes.

## Stage 2: Direct3D 12

The proxy's Direct3D 12 device and queue, the ones frame generation and AMD's upscaler
use, offered to mods. Unity keeps rendering in Direct3D 11: a mod's Direct3D 12 work runs
on textures shared between the two devices, and its result comes back into Unity's
texture.

### Capabilities

`D3D12.Available` -- the proxy presents through Direct3D 12 -- and from
`ID3D12Device::CheckFeatureSupport`: `FeatureLevel`, `ShaderModel`, `RaytracingTier`,
`MeshShaderTier`, `VariableShadingRateTier`.

### Compute passes

* **The shader.** Against a fixed root signature: textures `t0`-`t7`, read-write textures
  `u0`-`u7`, one constant buffer `b0` of up to 256 bytes, samplers `s0` point clamp and
  `s1` linear clamp. Three ways in, each returning a handle:
  * `D3D12.CreateComputePassFromFile(name, path, entry)` and `FromSource(name, hlsl, entry)`:
    HLSL the proxy compiles on the main thread with Windows' `d3dcompiler_47.dll`, loaded
    from System32 only (`LOAD_LIBRARY_SEARCH_SYSTEM32`), to DXBC (`cs_5_0`), `#include`
    resolved relative to the file (`D3D_COMPILE_STANDARD_FILE_INCLUDE`). The compiler's
    output lands in `D3D12.LastCompilerMessages` (`CompileComputeShader`,
    `KspD3d12Compile`).
  * `D3D12.CreateComputePass(name, bytecode)`: DXIL (Shader Model 6, `dxc` from the Windows
    SDK, which signs it with `dxil.dll` beside it) or DXBC (`fxc`).

  The pipeline is built on the render thread, and `D3D12.ComputePassState` says whether it
  was. A call refused on the main thread says why in `D3D12.LastRefusal`.
* **A dispatch.** `D3D12.Dispatch(pass, read, write, constants, x, y, z, nextFrame)`:
  1. the read and write textures are copied into textures both devices share -- made on
     first use, anew when size or format changes;
  2. a shared fence lets the Direct3D 12 queue wait for the point after those copies;
  3. the pass runs;
  4. the write textures are copied back into Unity's -- in the same frame, after
     Direct3D 11 waits for the queue (`nextFrame` false), or at the pass's next dispatch
     (`nextFrame` true), which lets the Direct3D 12 work run beside the rest of the frame.

  The pattern is AMD's upscaler's in the proxy (`AmdUpscaler.cpp`).
* **When to call it [meas].** A render event that reads a texture Unity drew this frame
  sees it only when it is issued through `Graphics.ExecuteCommandBuffer` from
  `OnRenderImage`; issued from a camera's own command buffer, it sees the frame before
  (Unity 2019.4.18f1 player with KSP's graphics jobs, 2026-09-14). `D3D12.Dispatch`
  executes at once; `D3D12.DispatchInto(buffer, ...)` records into a buffer the mod
  executes the same way.
* **Formats.** Colour formats a Direct3D 11 texture can be shared in and, for write
  textures, bound for unordered access in; no depth-stencil formats.
* **Textures a mod is done with.** `D3D12.ReleaseTexture(texture)` frees the shared copy;
  copies not used for ten seconds are freed anyway.
* **One texture in several passes.** A dispatch that reads or writes a texture another
  pass's `nextFrame` dispatch writes brings that result into the texture first, and
  Direct3D 11 copies into a shared copy only once the queue is past every list that used
  it.
* **The code.** `D3d12Compute.h` and `.cpp` in the proxy, render events 20 to 23 and the
  `KspD3d12*` exports (`ManagedBridge.cpp`); `D3d12Bridge.cs` and `Api/D3D12.cs` in the mod.

### Limits

Every dispatch copies its textures twice and waits once; the shared copies take video
memory. At most 32 dispatches a frame, within what the packet ring holds while the render
thread runs behind; a buffer from `DispatchInto` runs once, in the frame it was recorded
in. Direct3D 12 sees only what a mod hands it: geometry Unity tessellates
on the GPU -- Parallax's terrain -- is not there.

## Later

* **Raytracing.** Acceleration structures from meshes and transforms a mod hands over,
  and passes that trace them. Needs `RaytracingTier` 1.0 or higher.
* **Masks.** Contributions to the reactive and transparency masks. In this build only
  FSR 3 takes them; DLSS and AMD's upscaler in the proxy get none.

## Delivery

* `ReDefinition.Api` in `ReDefinition.dll`, with `[KSPAssembly("ReDefinition", ...)]` for
  mods that declare `KSPAssemblyDependency`; `ApiInfo.Version` counts changes of the
  interface.
* `ReDefinition.xml` beside the DLL, in the game and in the package: the interface's XML
  documentation for the IDE of a mod that references ReDefinition. Only `src/Api` carries
  it.
* `docs/modders/examples/ReDefinitionApi.cs`: a file to copy into a mod, inert without
  ReDefinition; MIT. It binds each member once as a typed delegate
  (`Delegate.CreateDelegate`), so reading the frame's state every frame allocates nothing.
* `unity/Assets/ReDefinition/Include/ReDefinition.cginc`: the shader globals with fallbacks
  to `_ScreenParams` where they are zero, and helpers for the jitter and Unity's motion
  vector encoding (`Internal-MotionVectors.shader`); MIT.
* `docs/modders/examples/ReDefinitionExample`: an example mod, built with the solution to
  `build/examples/ReDefinitionExample`, never deployed -- an overlay, a compute pass after
  the upscaler from an `.hlsl` file, a reported camera jump.
* The reference for mod authors: [modders/shared-foundation.md](../modders/shared-foundation.md).

## Verification

* Unit tests (`SharedFoundationTests`): the reset's rules, the hooks' guarding, the
  Direct3D 12 packets' layout, that the wrapper finds every member of the interface and
  returns its values, and that 400,000 reads of the frame's state through it allocate
  nothing.
* The proxy harness: the capabilities; HLSL compiled by the proxy from source, from a file
  with an `#include`, and broken source, whose error has to come back; compute passes from
  that DXBC and from DXIL (`HarnessPass.hlsl`, compiled by the build with the Windows SDK's
  `dxc`), each with a dispatch whose result is back in the same frame and two for the next
  frame, read back on Direct3D 11; the example mod's `ExampleEffect.hlsl` compiled and
  built.
* The include: `IncludeCheck` in the Unity project compiles `ReDefinitionIncludeCheck.shader`,
  which calls every function of `ReDefinition.cginc`, with Unity 2019.4.18f1 for the player,
  and fails on any shader error or warning. A deliberate error in the include made it fail
  with that error's line.
* The example mod: built with the solution, without warnings.
* **[open]** The hooks and the frame's state in the game: nothing in this build uses them
  yet but ReDefinition's own cut detection.
