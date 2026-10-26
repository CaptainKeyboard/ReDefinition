# Raytracing

The raytracing service. It builds the scene's structures on the proxy's Direct3D 12 device and
traces the signals the frame's lighting reads. It denoises them and hands them back to Unity.

## Start here

| File | Read it for |
|---|---|
| `src/DxgiProxy/Raytracing/Raytracing.h` | The `Raytracing` class: hand-over, frame, slots, and every member's comment |
| `src/DxgiProxy/Raytracing/Raytracing.cpp` | `Raytracing::OnFrame`, a frame step by step |
| `src/DxgiProxy/Raytracing/Shaders/RtContract.h` | The values and layouts the mod, the proxy and the shaders share |
| `src/DxgiProxy/Raytracing/Shaders/RtCommon.hlsli` | The root signature's bindings and the frame's constants |
| `src/DxgiProxy/Raytracing/RaytracingScene.cpp` | Meshes, materials and each frame's instances in the scene structure |
| `src/DxgiProxy/Raytracing/RaytracingInternal.h` | The passes, their order and the timestamps the sources share |

## What it does

- `RaytracingDevice.cpp` makes the pipelines, fences, command lists and heaps of the three frame
  slots, and the shared copies of Unity's textures.
- `RaytracingScene.cpp` builds the meshes' structures and each frame's scene. `RaytracingBuild.cpp`
  pools the memory, and `RaytracingBuildFrame.h` batches a frame's builds.
- `RaytracingMegaGeometry.cpp` and `RaytracingOmm.cpp` use NVIDIA's cluster structures and opacity
  micromaps through NVAPI. `RaytracingOmmBake.cpp` is the micromaps' CPU baker.
- `RaytracingCache.cpp` runs the radiance cache, and `RaytracingDenoise.cpp` ReDefinition's own
  denoiser.
- `RaytracingReconstruct.cpp` sends the frame to DLSS Ray Reconstruction, and
  `RaytracingRegenerate.cpp` to AMD's FSR Ray Regeneration where its DLL runs.
- `Shaders` holds the passes in HLSL. CMake compiles each entry point to DXIL and embeds it in
  `dxgi.dll` as `RT_<entry>`.

More:

- [Raytracing from scene to screen](../../../docs/development/flow-raytracing.md)
- [How shaders reach the game](../../../docs/development/flow-shaders.md)
- [Raytracing](../../../docs/development/raytracing.md), "Testing without the game"

## What it uses and what uses it

It uses `Common`, `FrameGeneration`, `Upscalers`, `Diagnostics` and `Latency` (`WaitStats.h`).
`Managed` and `SwapChain` include `Raytracing.h`.

## Rules of the area

- `Shaders/RtContract.h` is plain `#define`s, so C++ and HLSL include it alike. The `static_assert`s
  in `Raytracing.h` hold the proxy's structures to it, and `cbuffer Frame` places its fields by it.
  `tests/ReDefinition.Tests/RtContractTests.cs` holds the mod's `RtNative` to it.
- Positions are camera-relative: the world's axes with the camera at the origin. An instance's
  transform brings its mesh into that space (`src/DxgiProxy/Raytracing/Raytracing.h`).
- The main thread hands over meshes, materials and instances under `handOverLock`. It never waits
  for the service's `mutex`, which the render thread holds through a frame. `Raytracing::Status`
  does not take that mutex either.
- A frame uses one of `Raytracing::kSlots` (3) slots. A slot is reused only after the GPU is past
  its last list (`Raytracing::WaitForSlotLocked`).
- A signal pass needs its entry in `REDEFINITION_RT_ENTRIES` in `src/DxgiProxy/CMakeLists.txt` and
  its row in `kRtPasses`. Without `dxc` the DLL builds without raytracing.
- Mega Geometry, opacity micromaps and the radiance cache are each a setting. If the driver lacks
  the NVAPI operations, the meshes are built the usual way
  (`src/DxgiProxy/Raytracing/RaytracingMegaGeometry.cpp`).
- `Raytracing` is never destroyed, because at process exit its devices may be gone.
