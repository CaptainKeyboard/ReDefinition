# Api

The interface other mods call: the frame's state, history resets, hooks into the frame, the
chosen graphics profile, key bindings and Direct3D 12 compute.

## Start here

| File | Read it for |
|---|---|
| `src/Api/ApiInfo.cs` | `ApiInfo.Version`, the number that rises with every change of the interface |
| `src/Api/Frame.cs` | `Frame`: upscaler and frame generation state, history reset, origin shift |
| `src/Api/Hooks.cs` | `Hooks`: motion vectors, drawing after upscaling, overlay |
| `src/Api/D3D12.cs` | `D3D12`: compute passes on the proxy's Direct3D 12 queue, and their limits |
| `src/Api/Internal/SharedFrame.cs` | Where the state lives, how it is set once per frame, and the shader globals |

## What it does

- `Frame` and `Profiles` expose read-only state and let a mod register for a reset or a profile
  change.
- `Hooks` lets a mod add commands at three places in the frame.
- `Keys` reads the key bindings a mod declared in its registration, from the store.
- `D3D12` creates, dispatches and destroys compute passes through `D3d12Bridge` in `src/Proxy`.
- `src/Api/Internal` holds what the public classes share: `SharedFrame`, `HookList`,
  `HistoryResets` and `CameraCuts`.
- `CameraCuts` finds jumps of the flight camera and turns them into history resets.
- The shader side is `unity/Assets/ReDefinition/Include/ReDefinition.cginc`.

## What it uses and what uses it

It uses Core, Proxy, Settings, Ui, Upscaler and Window. The root, Diagnostics, Raytracing and
Upscaler use it.

## Rules of the area

- A published member keeps its name and signature. `ApiInfo.Version`, which is
  `SharedFrame.InterfaceVersion`, rises with every change of the interface.
- A public member takes and returns types of .NET, Unity and KSP only, so a mod reaches it by
  reflection. `docs/modders/examples/ReDefinitionApi.cs` does so and is compiled by
  `tests/ReDefinition.Tests/ReDefinition.Tests.csproj`.
- You call it from the main thread. `Frame` holds the state of the frame that began before
  the first scene camera culled; read in `Update`, it is the previous frame's
  (`src/Api/Frame.cs`).
- A handler that throws is removed and logged, and the others still run
  (`src/Api/Internal/HookList.cs`).
- A dispatch has at most 8 read and 8 write textures and 256 bytes of constants, and at most 32
  dispatches run a frame (`D3d12Bridge.MaxTextures`, `D3d12Bridge.MaxConstants`,
  `D3d12Bridge.MaxDispatchesPerFrame`).
- `CameraCuts` ignores floating origin shifts on purpose. A reset on every shift would leave the
  upscaler without history (`src/Api/Internal/CameraCuts.cs`).

[The interface for other mods](../../docs/development/flow-mod-interface.md) follows each
member to its answer.

For the contract in full see [Shared foundation](../../docs/modders/shared-foundation.md) for
mod authors and [Shared foundation](../../docs/development/flow-mod-interface.md) for the
internals.
