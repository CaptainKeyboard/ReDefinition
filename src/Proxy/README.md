# Proxy

The managed half of the proxy: the packets, render events and imports that reach `dxgi.dll`,
for the upscalers, frame generation, low latency, Direct3D 12 for mods and raytracing.

## Start here

| File | Read it for |
|---|---|
| `src/Proxy/ProxyEvents.cs` | The ids of the render events the mod raises for the proxy |
| `src/Proxy/PacketRing.cs` | How a packet reaches the render thread: slots, copied in place |
| `src/Proxy/FrameGenerationBridge.cs` | The frame packet and the calls of frame generation |
| `src/Proxy/NativeUpscalerLink.cs` | `UpscalerBackend` and the link the AMD, DLSS and XeSS bridges share |
| `src/Proxy/RtNative.cs` | The raytracing service: exports, packets, flags and signals |
| `src/Proxy/ProxyLoad.cs` | Finding out that Windows refused the proxy and loaded its own `dxgi.dll` |

## What it does

- `ProxyDll.Name` is the one place that spells the library every import resolves to.
- `FrameGenerationBridge`, `DlssBridge`, `AmdUpscalerBridge`, `XessUpscalerBridge` and
  `D3d12Bridge` declare a packet each and send it as a render event through `PacketRing`.
- `FrameGenerationChoice` holds the frame generation row's choices and which technique allows
  which. `StreamlineCamera` turns Unity's matrices into the ones Streamline wants.
- `ReflexFrames` adds two systems to Unity's player loop that mark the simulation for Reflex
  and send the frame's index to the proxy.
- `ProxyIni` reads and writes `ReDefinitionProxy.ini`, and `ScreenRecordingBridge` starts the
  proxy's screen recording.
- `RtNative` is the raytracing service's side, with the other render events of its own.

## What it uses and what uses it

It uses Core only. The root, Api, Diagnostics, Modules, Probe, Raytracing, Settings, Upscaler,
Vendor and Window use it. The proxy itself is native code in `src/DxgiProxy`.

## Rules of the area

- A packet's field order, types and size match the native struct. The frame packet's size and
  magic are checked on both sides, and `tests/ReDefinition.Tests/FramePacketLayoutTests.cs` and
  `tests/ReDefinition.Tests/DlssPacketLayoutTests.cs` hold the layouts.
- Every constant of `ProxyEvents` has its `PX_EVENT_<NAME>` in
  `src/DxgiProxy/Managed/ProxyContract.h` with the same value
  (`tests/ReDefinition.Tests/ProxyContractTests.cs`). Raytracing's constants and layouts are held
  to `src/DxgiProxy/Raytracing/Shaders/RtContract.h`
  (`tests/ReDefinition.Tests/RtContractTests.cs`).
- A packet lives in its own slot, because the render thread reads it some frames after the main
  thread wrote it. `PacketRing` never frees a slot.
- `RtNative` is free of KSP's types, because the raytracing test player compiles it
  (`tests/RaytracingPlayer/Assets/Linked`).
- Without the proxy, or with an older one, the entry points do not exist and the bridges go
  quiet for the session. The upscaler works without the proxy.
- `FrameGenerationChoice` and `StreamlineCamera` run without the game, for their tests
  (`tests/ReDefinition.Tests/FrameGenerationChoiceTests.cs`,
  `tests/ReDefinition.Tests/StreamlineCameraTests.cs`).

[The four channels to native code](../../docs/development/flow-native-channels.md) and
[A frame in the mod](../../docs/development/flow-frame.md) show where these files sit.

For what the native side does, see:

- [Frame generation](../../docs/development/frame-generation.md)
- [Low latency](../../docs/development/low-latency.md)
- [Raytracing](../../docs/development/raytracing.md)
