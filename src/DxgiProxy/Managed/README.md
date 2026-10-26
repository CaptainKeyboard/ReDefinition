# Managed

The surface the mod calls. It has the flat C exports from `dxgi.dll`, the render events Unity's
render thread runs for the proxy, and Direct3D 12 compute passes for mods.

## Start here

| File | Read it for |
|---|---|
| `src/DxgiProxy/Managed/ManagedBridge.cpp` | Every export, and `DispatchRenderEvent`, which routes the events |
| `src/DxgiProxy/Managed/ProxyContract.h` | The ids of the render events, one `PX_EVENT_<NAME>` each |
| `src/DxgiProxy/Managed/D3d12Compute.h` | The packets of the Direct3D 12 service and the `D3d12Compute` class |
| `src/DxgiProxy/Managed/D3d12Compute.cpp` | Compiling a pass, dispatching it, the shared copies of Unity's textures |

## What it does

- `ManagedBridge.cpp` exports more than sixty functions. They serve frame generation, the three
  upscalers, raytracing, low latency, the profilers, the screen recorder and Direct3D 12 for mods.
  The mod imports them from `dxgi.dll`, which is already loaded.
- `KspFgGetRenderEventFunc` gives the mod the render event callback. `OnRenderEvent` calls
  `DispatchRenderEvent`, which sends each id to its handler.
- `ProxyContract.h` holds the ids 1 to 8 for the upscalers and frame generation, 20 to 23 for
  Direct3D 12, and 40 for Reflex. Raytracing's ids 30 to 33 are in `RtContract.h`.
- `D3d12Compute` is the Direct3D 12 service for mods. A mod hands over shader bytecode, DXIL or
  DXBC, and dispatches it with up to eight textures read, eight written and 256 bytes of constants.
- `D3d12Compute` copies Unity's textures into shared ones, runs the pass, and copies the results
  back. A shared copy not used for ten seconds is released.

The mod's side is `src/Proxy`. More:

- [The four channels to native code](../../../docs/development/flow-native-channels.md)
- [The shared foundation for mods](../../../docs/development/flow-mod-interface.md), "Stage 2:
  Direct3D 12"

## What it uses and what uses it

It uses nearly every folder, because it routes to them: `Common`, `Diagnostics`,
`FrameGeneration`, `Latency`, `Raytracing`, `SwapChain` and `Upscalers`. `SwapChain` includes
`D3d12Compute.h`.

## Rules of the area

- An export is a flat C function with scalar arguments. Where a call carries a structure, it goes
  as a render event, so it arrives with the frame it describes.
- An export is renamed when its arguments change, as `KspFgRegisterInputs3` was. A mod out of step
  with the proxy then fails to find the entry point and turns the feature off. It does not hand
  one texture over as another.
- Every packet starts with its size and magic. The proxy checks both, and the mod compares its own
  size with exports such as `KspFgPacketSize`, `KspDlssPacketSize` and `KspD3d12CreatePacketSize`
  before it sends the first.
- `OnRenderEvent` lets no exception out, because that would end the process on Unity's render
  thread. It drops the event and logs it, ten times at most.
- Each event id is in one header, and the mod's `ProxyEvents` has the same value
  (`tests/ReDefinition.Tests/ProxyContractTests.cs`). A new event needs a define here, a constant
  there, and a branch in `DispatchRenderEvent`.
- `tests/ReDefinition.Tests/SharedFoundationTests.cs` holds the mod's copies of the Direct3D 12
  packets to the sizes and offsets of `D3d12CreatePacket`, `D3d12DispatchPacket` and
  `D3d12HandlePacket`.
