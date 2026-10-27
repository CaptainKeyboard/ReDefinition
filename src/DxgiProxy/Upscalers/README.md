# Upscalers

The upscalers the proxy runs for the mod: DLSS, AMD's upscaler DLL and XeSS. DLSS Ray
Reconstruction serves raytraced frames.

## Start here

| File | Read it for |
|---|---|
| `src/DxgiProxy/Upscalers/Dlss.h` | `DlssPacket`, the render events and `Dlss`, which runs on Unity's device |
| `src/DxgiProxy/Upscalers/AmdUpscaler.h` | `AmdUpscalerPacket` and the way a frame crosses to Direct3D 12 and back |
| `src/DxgiProxy/Upscalers/XessUpscaler.h` | XeSS on the same path, with AMD's packet layout |
| `src/DxgiProxy/Upscalers/UpscalerRig.h` | The Direct3D 11 to 12 hand-off both of them share: fence, shared textures, command list, status |
| `src/DxgiProxy/Upscalers/DlssRayReconstruction.h` | Ray Reconstruction through NGX on Direct3D 12 |
| `src/DxgiProxy/Upscalers/NgxCore.h` | NGX's parameter map, loading, and the lock every NGX call holds |
| `src/DxgiProxy/Upscalers/NvidiaGpu.h` | Which NVIDIA architecture the adapter is, through NVAPI |

## What it does

- `Dlss` evaluates DLSS Super Resolution through NGX on Unity's own Direct3D 11 device. It
  answers render event 2 for a frame, 3 for the release and 6 for the render sizes of each mode.
- `AmdUpscaler` and `XessUpscaler` run on the proxy's Direct3D 12 device. They copy Unity's
  colour, depth and motion vectors into shared textures, upscale on the queue, and copy the result
  back. A shared fence orders the two devices. Render events are 4 and 5, and 7 and 8. `UpscalerRig`
  is the part they share. Each keeps its own DLL, context and dispatch.
- AMD's DLL decides which FSR runs for the GPU, FSR 4 or the FSR 3.1 it carries.
  `AmdUpscaler::Provider` reports it.
- `DlssRayReconstruction` is NGX's Ray Reconstruction on Direct3D 12, in place of Super
  Resolution for a raytraced frame. `Raytracing` calls it.
- `NgxCore` loads the driver's NGX and declares its parameter map.
- `NvidiaGpu` asks NVAPI for the adapter's architecture and hands out NVAPI functions by id.
  `Latency`, `Raytracing` and `FrameGeneration` use both.

The runtimes load from next to `KSP_x64.exe` or from the folders in the ini (`dlssDirectory`,
`amdUpscalerDirectory`, `xessDirectory`). See [The upscaler](../../../docs/development/flow-frame.md)
and [A frame in the mod](../../../docs/development/flow-frame.md) for the C# side of a frame.

## What it uses and what uses it

It uses `Common` and `FrameGeneration` (`FidelityFx.h`, `FrameGeneration.h`). `SwapChain` attaches
the devices, `Managed` dispatches the render events, and `Raytracing`, `FrameGeneration` and
`Latency` use `NvidiaGpu.h`.

## Rules of the area

- Every upscaler runs on Unity's render thread, from a render event of the upscaler's dispatch
  buffer. The main thread reads only `Dlss::Status` and `Dlss::RenderSizes`.
- NGX is not thread-safe. Every call into it holds `NgxMutex`, and Ray Reconstruction's calls on
  Direct3D 12 take the same lock as Super Resolution's (`src/DxgiProxy/Upscalers/NgxCore.h`).
- A packet carries its size and magic. `DlssPacket` is 88 bytes and `AmdUpscalerPacket` 112, both
  held by `static_assert`. The mod also compares the sizes the exports `KspDlssPacketSize` and
  `KspAmdUpscalerPacketSize` return. `tests/ReDefinition.Tests/DlssPacketLayoutTests.cs` holds the
  mod's copy of the DLSS packet.
- The Direct3D 12 upscalers need the swapchain proxy to have attached its devices
  (`AmdUpscaler::Attached`). Without that, nothing runs.
- Singletons here are never destroyed, because at process exit the devices they hold may be gone.
