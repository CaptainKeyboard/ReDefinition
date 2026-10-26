# DxgiProxy

The proxy: ReDefinition's `dxgi.dll`, native code beside `KSP_x64.exe`. It presents the game's
frames through Direct3D 12 and runs the upscalers and frame generation. It also runs the compute
and raytracing passes that mods ask for.

## Start here

| File | Read it for |
|---|---|
| `src/DxgiProxy/CMakeLists.txt` | The targets, the shader compile, the version resource, the deploy step |
| `src/DxgiProxy/SwapChain/DxgiExports.cpp` | How the DLL stands in for Windows' `dxgi.dll` and hooks the swapchain |
| `src/DxgiProxy/SwapChain/SwapChainProxy.h` | The swapchain Unity gets in place of its own |
| `src/DxgiProxy/Managed/ManagedBridge.cpp` | Every export the mod calls, and the render event dispatcher |
| `src/DxgiProxy/Common/Config.h` | Every setting the proxy reads from `ReDefinitionProxy.ini` |
| `src/DxgiProxy/ReDefinitionProxy.ini` | The template of the player's settings file, built into the DLL |

## What it does

| Folder | Holds |
|---|---|
| `src/DxgiProxy/SwapChain` | The DLL's exports and the proxy swapchain that presents through Direct3D 12 |
| `src/DxgiProxy/FrameGeneration` | Frame generation by FSR, DLSS (Streamline) and XeSS, and its inputs |
| `src/DxgiProxy/Upscalers` | DLSS, AMD's upscaler, XeSS and DLSS Ray Reconstruction |
| `src/DxgiProxy/Raytracing` | The raytracing service, its shaders and its acceleration structures |
| `src/DxgiProxy/Latency` | NVIDIA Reflex, AMD Anti-Lag 2, Intel XeLL, the latency brake, the load monitor |
| `src/DxgiProxy/Managed` | The flat C exports and render events the mod calls, and Direct3D 12 for mods |
| `src/DxgiProxy/Diagnostics` | The GPU profiler and the screen recorder |
| `src/DxgiProxy/Common` | The settings, the log, file helpers and Direct3D 12 helpers every folder uses |
| `src/DxgiProxy/extern` | Headers of other vendors, compiled into the DLL |

Each folder except `extern` has a README of its own. The managed half of the proxy is `src/Proxy`.

How the pieces connect:

- [The four channels to native code](../../docs/development/flow-native-channels.md)
- [The proxy's present path](../../docs/development/flow-proxy-present.md)
- [Raytracing from scene to screen](../../docs/development/flow-raytracing.md)
- [How shaders reach the game](../../docs/development/flow-shaders.md)

## Build it

You need MSVC with the C++ workload, a Windows 10 SDK and CMake.

```
cmake -S src/DxgiProxy -B build/DxgiProxy -G "Visual Studio 17 2022" -A x64
cmake --build build/DxgiProxy --config Release
```

- The target `ReDefinitionProxy` is a shared library with `OUTPUT_NAME` `dxgi`. The loader finds
  it before the system's file because it lies in the game's folder.
- CMake reads `<Version>` from `src/ReDefinition.csproj` into the DLL's version resource
  (`src/DxgiProxy/ReDefinitionProxy.rc.in`). The same resource embeds `ReDefinitionProxy.ini`.
- If the Windows SDK's `dxc` is found, CMake compiles each raytracing entry point to DXIL and
  embeds it as `RT_<entry>`. Without `dxc` the DLL has no raytracing and CMake warns.
- If `KSP_DIR` exists, the build copies `dxgi.dll` there. It copies the ini only when none is
  there yet. Pass `-DKSP_DIR=...` for another game folder.
- The targets `ProxyHarness` and `RaytracingHarness` build from `tests/DxgiProxy` beside the DLL.
- Do not build while KSP runs, because the game holds the file.

More: [Building and testing](../../docs/development/building-and-testing.md).

## What it uses and what uses it

`tests/ReDefinition.Tests/ArchitectureTests.cs` skips `src/DxgiProxy`, so this comes from the
`#include` lines. Inside it, every folder uses `Common`, and `Common` uses nothing. The mod
reaches it through the exports of `src/DxgiProxy/Managed/ManagedBridge.cpp`, from `src/Proxy`.
The harnesses in `tests/DxgiProxy` include its headers.

## Rules of the area

- With `enabled=0` every export passes through to the system's `dxgi.dll`
  (`src/DxgiProxy/Common/Config.h`, `ProxyConfig.enabled`). If making the proxy swapchain
  fails, the original `CreateSwapChainForHwnd` runs (`src/DxgiProxy/SwapChain/DxgiExports.cpp`).
- The ids and layouts shared with the mod stand in two headers:
  `src/DxgiProxy/Managed/ProxyContract.h` and `src/DxgiProxy/Raytracing/Shaders/RtContract.h`.
  `tests/ReDefinition.Tests/ProxyContractTests.cs` and
  `tests/ReDefinition.Tests/RtContractTests.cs` hold the mod's constants to them.
- A new setting goes into `src/DxgiProxy/ReDefinitionProxy.ini` with its comment and default.
  The proxy adds the keys a player's file lacks from that template
  (`src/DxgiProxy/Common/Config.cpp`).
- The vendors' runtimes load at run time from next to the executable, never from `DllMain`
  (`src/DxgiProxy/FrameGeneration/FidelityFx.h`, `src/DxgiProxy/FrameGeneration/Streamline.h`).
- The singletons that hold Direct3D objects are never destroyed, because at process exit the
  devices may be gone (`FrameGeneration::Get`, `Raytracing::Get`, `D3d12Compute::Get`).
- `src/DxgiProxy/extern` holds the vendors' headers. Their licences are in `licenses/`, and
  [Credits and licences](../../docs/credits-and-licences.md) names them. Intel's
  `xefg_swapchain*.h` are not vendored, and `src/DxgiProxy/FrameGeneration/XessFg.h` declares
  their types.
