# DxgiProxy

The proxy's test drivers: two native programs that load `dxgi.dll` the way Unity does and check
what the proxy does, without KSP.

## Start here

| File | Read it for |
|---|---|
| `tests/DxgiProxy/ProxyHarness.cpp` | The swapchain, frame generation, the upscalers, low latency and Direct3D 12 for mods |
| `tests/DxgiProxy/RaytracingHarness.cpp` | The raytracing passes against the CPU, and `main` with its arguments |
| `tests/DxgiProxy/RaytracingOmmHarness.cpp` | The opacity micromaps against the driver |
| `tests/DxgiProxy/HarnessPass.hlsl` | The compute pass the proxy harness runs for a mod |
| `tests/DxgiProxy/OmmProbe.hlsl` | The ray query that reads the driver's micromap order |

## What it does

- `ProxyHarness` creates a Direct3D 11 device and asks for the swapchain KSP asks for, with its
  flags. It renders and presents. It turns frame generation on, off and on again across mode
  changes and a resize, and it checks the HUD-less copy.
- `ProxyHarness` also runs the latency brake's controller, DLSS, AMD's upscaler and XeSS, and a
  compute pass for mods, from DXBC and from DXIL (`HarnessPass.cso`).
- `RaytracingHarness` rasterises a G-buffer in Unity's layout and hands the proxy meshes and
  instances through its exports. It sends frames through render event 30 and checks every pass
  against the CPU. It writes an image per check, `rt_*.bmp`, next to itself.
- `RaytracingOmmHarness.cpp` is part of that executable. It holds the baker in
  `src/DxgiProxy/Raytracing/RaytracingOmmBake.cpp` against the driver.
- Each check prints a `PASS` or a `FAIL` line. The process exits 0 when every check passed.

## Run them

CMake builds both next to `dxgi.dll`
([Building and testing](../../docs/development/building-and-testing.md)):

```
cmake --build build/DxgiProxy --config Release
build\DxgiProxy\Release\ProxyHarness.exe
build\DxgiProxy\Release\RaytracingHarness.exe
```

- `ProxyHarness` runs FSR's frame generation if `amd_fidelityfx_framegeneration_dx12.dll` lies next
  to it. The variables `REDEFINITION_STREAMLINE_DIR`, `REDEFINITION_XESS_FG_DIR`,
  `REDEFINITION_DLSS_DIR`, `REDEFINITION_AMD_UPSCALER_DIR` and `REDEFINITION_XESS_DIR` name the
  folders with the player's DLLs and add their checks.
- `REDEFINITION_ADAPTER_VENDOR` picks the adapter by PCI vendor id, and
  `REDEFINITION_LATENCY_TEST=1` compares what lowers the latency on this GPU.
- `RaytracingHarness` needs a GPU with DirectX Raytracing tier 1.1. One argument narrows the run:
  `--omm`, `--omm-table`, `--omm-scene` or `--omm-none`. `REDEFINITION_RT_SCENE` names an exported
  scene of KSP's meshes.
- `tools/replay_fg.ps1` plays a folder of dumped frames through `ProxyHarness` with
  `REDEFINITION_HARNESS_REPLAY`.

The full list of variables, with what each does, is in
[Building and testing](../../docs/development/building-and-testing.md).

## Rules of the area

- The harness has to give the proxy the conditions KSP gives it. The swapchain's flags are
  `0x842`, which include the frame latency waitable object. Without it a harness passes where the
  game stops (`tests/DxgiProxy/ProxyHarness.cpp`, header).
- The harnesses include the proxy's own headers through the include paths in
  `src/DxgiProxy/CMakeLists.txt`. They raise events by the names in
  `src/DxgiProxy/Managed/ProxyContract.h` and `src/DxgiProxy/Raytracing/Shaders/RtContract.h`.
- The harness checks the packet sizes against the proxy's before it sends a frame
  (`KspRtStructSizes` in `tests/DxgiProxy/RaytracingHarness.cpp`).
- Run `ProxyHarness` after every change to the proxy.
