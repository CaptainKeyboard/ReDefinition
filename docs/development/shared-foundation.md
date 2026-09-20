# The shared foundation for mods

**For:** anyone working on the interface ReDefinition offers other mods.
**You need:** `src/Api`, `src/Shared`, `src/Bridges` and `src/DxgiProxy/D3d12Compute.cpp`
to hand.
**You get:** why each part exists only once, what ReDefinition offers instead, and what
has been verified.

Some things exist only once in a KSP game: the `dxgi.dll` next to `KSP_x64.exe`, the
jitter of a camera, the motion vectors of a frame, the swapchain. Mods that each build
their own collide. ReDefinition provides them once, for every mod, through a public
interface in `ReDefinition.dll`. What mod authors need is the reference,
[modders/shared-foundation.md](../modders/shared-foundation.md); this page says how the
interface works and why.

Every claim here carries its mark: **[src]**, **[doc]**, **[meas]** or **[open]**
([writing-these-pages.md](writing-these-pages.md)).

## Principles

* **Works without the mods' help.** Players get the upscaler, frame generation and the
  settings with every mod as it is. Compatibility is ReDefinition's side
  ([reference/graphics-mod-compatibility.md](../reference/graphics-mod-compatibility.md)).
* **No hard dependency.** A mod reads the frame's state from shader globals that are zero
  without ReDefinition. It calls the interface through a wrapper file that does nothing
  without it (`docs/modders/examples/ReDefinitionApi.cs`). Where Unity has a convention,
  ReDefinition follows it: `nonJitteredProjectionMatrix`, the `MotionVectors` pass,
  `_CameraMotionVectorsTexture`. A mod that follows the same convention works without
  knowing ReDefinition. A mod that answers for its own settings brings a type of its own,
  whose members ReDefinition finds by name and signature
  ([modders/registering-a-mod.md](../modders/registering-a-mod.md)).
* **Takes nothing away.** A mod's own implementation stays as it is. What ReDefinition
  switches off in another mod it switches off only while a graphics profile is chosen, and
  gives back afterwards (`src/Upscaler/HostStack.cs`).
* **Useful on its own.** Each part serves ReDefinition's own features too.

## What exists once

| Resource | Why only once | Evidence | Offered |
|---|---|---|---|
| The way to Direct3D 12 | Windows loads one `dxgi.dll` from next to the executable | ReShade's `dxgi.dll` and ReDefinition's exclude each other | Direct3D 12: capabilities, shared textures, compute passes |
| Jitter and temporal history | A camera renders with one projection | Scatterer #231 and #252, Parallax #30, TUFX #40: temporal filters on top of each other **[doc]** | The frame's jitter, render and display size |
| Motion vectors | One buffer per camera; Scatterer installs its motion vector shader globally **[src]** | Geometry moved in a vertex shader has none: Scatterer's ocean (`OceanWhiteCapsModProj3.shader`), Parallax's grass **[src]** | Contributions into the motion vectors every upscaler and frame generation read |
| Camera cuts | KSP raises no event for them | TUFX #23: flickering after a revert **[doc]** | A reset for the frame, from ReDefinition's detection and from any mod |
| Floating origin shifts | One origin | Unity's camera motion vectors project the current world position with the previous frame's view-projection **[src]** | The shifts of the frame |
| The image after upscaling, before the UI | One presenter | TUFX's effects run after the upscaler (`src/Upscaler/TufxPostProcessing.cs`) | A hook on the upscaled image and an overlay after it |
| Global quality settings | One `QualitySettings` | Scatterer sets shadow projection and distance, Parallax requires terrain detail, Deferred writes reflection settings at every scene load **[src]** | Requirements in registrations (existing); the chosen profile and its changes |

## Stage 1: the frame

### The frame's state

The state is set before the first scene camera of a frame culls. That is
`Camera.onPreCull` for a camera of the 3D stack, or, where none culled, the moment the rig
presents. It arrives as shader globals and as properties of `ReDefinition.Api.Frame`
(`src/Shared/SharedFrame.cs`).

| Global | Contents | Without a rig |
|---|---|---|
| `_ReDefinition_Frame` | upscaler active, frame generation active, history reset this frame, interface version | 0, 0, reset, version |
| `_ReDefinition_RenderSize` | width, height, 1/width, 1/height | the screen |
| `_ReDefinition_DisplaySize` | the same for the output | the screen |
| `_ReDefinition_Jitter` | the jitter in pixels at render size, x and y; in normalized device coordinates, x and y | 0 |
| `_ReDefinition_OriginShift` | the sum of `offset` the floating origin shifted by since the last frame, and 1 if it shifted | the shifts, as with one |

### The floating origin

**[src]** KSP moves the active vessel, the camera and nearby objects by `-offset`, and
bodies and landed or packed vessels by `-(offset + nonFrame)`. It then raises
`onFloatingOriginShift(offset, nonFrame)` in `FloatingOrigin.setOffset`, as
KSPCommunityFixes' `FloatingOriginPerf` reproduces it.

Unity's camera motion vector pass takes the current world position from depth and projects
it with `_PreviousVP`, the previous frame's. The object pass projects `_PreviousM` with
`_PreviousVP` (`Internal-MotionVectors.shader`). Across a shift, a renderer drawn with its
object pass keeps consistent motion vectors. A pixel that gets only camera motion is off by
the shift:

* a renderer in `MotionVectorGenerationMode.Camera`;
* a renderer whose transform did not change;
* the sky.

`Frame.OriginShift` and `Frame.BodyShift` carry both sums, for a mod's own reprojection.
**[open]** how large that error is in flight.

### History reset

`Frame.HistoryReset` is true for the first frame of a discontinuous view. ReDefinition
detects a change of the flight camera's target, of its parent and of the IVA kerbal. A mod
reports its own cut with `Frame.RequestHistoryReset(reason)`.

A reset decided before the frame's first scene camera reaches every mod in that frame. One
found while the frame renders resets the upscaler and frame generation in that frame, and
reaches the mods in the next, without a second reset of the upscaler
(`src/Shared/HistoryResets.cs`, `src/Shared/CameraCuts.cs`). Handlers registered with
`Frame.RegisterHistoryReset` are called when a reset is decided.

### Hooks

Each hook takes a `System.Action` of Unity types, so a wrapper reaches it by reflection. A
handler that throws is removed and logged once, and the others run on
(`src/Shared/HookList.cs`). The rig calls them (`src/Upscaler/UpscalerRig.Hooks.cs`).

| Hook | Called | Draws |
|---|---|---|
| `Hooks.RegisterMotionVectors(Action<CommandBuffer, RenderTexture motionVectors, RenderTexture depth, Camera scene>)` | every frame as the scene camera culls, with the frame's state decided; its commands run in the scene camera's capture at `BeforeImageEffects`, after Unity's motion vectors and EVE's clouds are in | motion vectors in Unity's encoding: `RGHalf`, current minus previous viewport position (`Internal-MotionVectors.shader`); depth is the scene's raw depth at render size |
| `Hooks.RegisterAfterUpscaling(Action<CommandBuffer, RenderTexture image, Camera scene>)` | every frame the rig presents, after the upscaler, before TUFX's effects after it and before frame generation's HUD-less copy | onto the upscaled image at display size; interpolated with the scene |
| `Hooks.RegisterOverlay(Action<CommandBuffer, Camera scene>)` | every frame, on a camera of ReDefinition's after the last camera that draws the scene, made only while a handler is registered | onto the backbuffer at display size, not upscaled, and UI to frame generation: lines and markers in the world |

### Profiles

`Profiles.Current` is the chosen graphics profile's name, and null while none is chosen.
`Profiles.RegisterChanged(Action<string>)` is called when it changes.

### Key bindings

A binding a mod declares in its registration is in the settings window's *Keys* tab,
and the mod asks `ReDefinition.Api.Keys` whether it is pressed (`src/Api/Keys.cs`). A mod
that keeps no key of its own then keeps no key file of its own either.

## Stage 2: Direct3D 12

The proxy's Direct3D 12 device and queue are the ones frame generation and AMD's upscaler
use, and they are offered to mods. Unity keeps rendering in Direct3D 11. A mod's Direct3D
12 work therefore runs on textures shared between the two devices, and its result comes
back into Unity's texture.

### Capabilities

`D3D12.Available` says that the proxy presents through Direct3D 12. The rest comes from
`ID3D12Device::CheckFeatureSupport`: `FeatureLevel`, `ShaderModel`, `RaytracingTier`,
`MeshShaderTier` and `VariableShadingRateTier`.

### Compute passes

* **The shader.** It is written against a fixed root signature: textures `t0` to `t7`,
  read-write textures `u0` to `u7`, one constant buffer `b0` of up to 256 bytes, and the
  samplers `s0` point clamp and `s1` linear clamp. There are three ways in, each returning
  a handle:
  * `D3D12.CreateComputePassFromFile(name, path, entry)` and `FromSource(name, hlsl, entry)`
    take HLSL. The proxy compiles it on the main thread with Windows' `d3dcompiler_47.dll`,
    loaded from System32 only (`LOAD_LIBRARY_SEARCH_SYSTEM32`), to DXBC (`cs_5_0`), with
    `#include` resolved relative to the file (`D3D_COMPILE_STANDARD_FILE_INCLUDE`). The
    compiler's output lands in `D3D12.LastCompilerMessages` (`CompileComputeShader`,
    `KspD3d12Compile`).
  * `D3D12.CreateComputePass(name, bytecode)` takes DXIL or DXBC. DXIL comes from `dxc` of
    the Windows SDK, which signs it with the `dxil.dll` beside it; DXBC comes from `fxc`.

  The pipeline is built on the render thread, and `D3D12.ComputePassState` says whether it
  was. A call refused on the main thread says why in `D3D12.LastRefusal`.
* **A dispatch.** `D3D12.Dispatch(pass, read, write, constants, x, y, z, nextFrame)` runs
  in four steps:
  1. the read and write textures are copied into textures both devices share, made on
     first use and anew when size or format changes;
  2. a shared fence lets the Direct3D 12 queue wait for the point after those copies;
  3. the pass runs;
  4. the write textures are copied back into Unity's. That happens in the same frame, after
     Direct3D 11 waits for the queue (`nextFrame` false), or at the pass's next dispatch
     (`nextFrame` true), which lets the Direct3D 12 work run beside the rest of the frame.

  The pattern is AMD's upscaler's in the proxy (`src/DxgiProxy/AmdUpscaler.cpp`).
* **When to call it [meas].** A render event that reads a texture Unity drew this frame
  sees it only when it is issued through `Graphics.ExecuteCommandBuffer` from
  `OnRenderImage`. Issued from a camera's own command buffer, it sees the previous frame
  (Unity 2019.4.18f1 player with KSP's graphics jobs, 2026-09-14). `D3D12.Dispatch`
  executes at once. `D3D12.DispatchInto(buffer, ...)` records into a buffer the mod
  executes the same way.
* **Formats.** Colour formats a Direct3D 11 texture can be shared in and bound for
  unordered access in, read textures as well, and that a shader can load from. No
  depth-stencil formats.
* **Textures a mod is done with.** `D3D12.ReleaseTexture(texture)` frees the shared copy.
  Copies not used for ten seconds are freed anyway.
* **One texture in several passes.** A dispatch that reads or writes a texture another
  pass's `nextFrame` dispatch writes brings that result into the texture first. Direct3D 11
  copies into a shared copy only once the queue is past every list that used it.
* **The code.** `src/DxgiProxy/D3d12Compute.h` and `src/DxgiProxy/D3d12Compute.cpp` in the
  proxy, with render events 20 to 23 and the `KspD3d12*` exports
  (`src/DxgiProxy/ManagedBridge.cpp`); `src/Bridges/D3d12Bridge.cs` and `src/Api/D3D12.cs`
  in the mod.

### Limits

Every dispatch copies its textures twice and waits once, and the shared copies take video
memory. At most 32 dispatches a frame are allowed, within what the packet ring holds while
the render thread runs behind. A buffer from `DispatchInto` runs once, in the frame it was
recorded in. Direct3D 12 sees only what a mod hands it, so geometry Unity tessellates on
the GPU, such as Parallax's terrain, is not there.

## Later

* **Raytracing.** Acceleration structures from meshes and transforms a mod hands over, and
  passes that trace them. It needs `RaytracingTier` 1.0 or higher.
* **Masks.** Contributions to the reactive and transparency masks. In this build only FSR 3
  takes them; DLSS and AMD's upscaler in the proxy get none.

## Delivery

* `ReDefinition.Api` in `ReDefinition.dll`, with `[KSPAssembly("ReDefinition", ...)]` for
  mods that declare `KSPAssemblyDependency`. `ApiInfo.Version` counts changes of the
  interface.
* `ReDefinition.xml` beside the DLL, in the game and in the package. It is the interface's
  XML documentation, for the IDE of a mod that references ReDefinition. Only `src/Api`
  carries it.
* `docs/modders/examples/ReDefinitionApi.cs`: a file to copy into a mod, inert without
  ReDefinition; MIT. It binds each member once as a typed delegate
  (`Delegate.CreateDelegate`), so reading the frame's state every frame allocates nothing.
* `unity/Assets/ReDefinition/Include/ReDefinition.cginc`: the shader globals with fallbacks
  to `_ScreenParams` where they are zero, and helpers for the jitter and Unity's motion
  vector encoding (`Internal-MotionVectors.shader`); MIT.
* `docs/modders/examples/ReDefinitionExample`: an example mod, built with the solution to
  `build/examples/ReDefinitionExample` and never deployed. It shows an overlay, a compute
  pass after the upscaler from an `.hlsl` file, a reported camera jump, a key binding read
  through `ReDefinition.Api.Keys`, and settings answered by a type of its own
  (`SettingsBridge.cs`).
* The reference for mod authors:
  [modders/shared-foundation.md](../modders/shared-foundation.md).

## Verification

* Unit tests (`tests/ReDefinition.Tests/SharedFoundationTests.cs`): the reset's rules, the
  hooks' guarding, the Direct3D 12 packets' layout, that the wrapper finds every member of
  the interface and returns its values, and that 400,000 reads of the frame's state through
  it allocate nothing.
* The proxy harness (`src/DxgiProxy/ProxyHarness.cpp`): the capabilities; HLSL compiled by
  the proxy from source, from a file with an `#include`, and broken source, whose error has
  to come back; compute passes from that DXBC and from DXIL
  (`src/DxgiProxy/HarnessPass.hlsl`, compiled by the build with the Windows SDK's `dxc`),
  each with a dispatch whose result is back in the same frame and two for the next frame,
  read back on Direct3D 11; the example mod's `ExampleEffect.hlsl` compiled and built.
* The include: `IncludeCheck` in the Unity project compiles
  `unity/Assets/ReDefinition/Include/ReDefinitionIncludeCheck.shader`, which calls every
  function of `ReDefinition.cginc`, with Unity 2019.4.18f1 for the player. It fails on any
  shader error or warning. A deliberate error in the include made it fail with that error's
  line.
* The example mod: built with the solution, without warnings.
* **[open]** The hooks and the frame's state in the game: nothing in this build uses them
  for its own work. *Hook probe, into the log*, in the diagnostics window's *Debug* tab,
  registers a handler on all three and writes what each one is called with, once per
  scene (`src/HookProbe.cs`), so the path is walked in a normal game
  yet but ReDefinition's own cut detection.
