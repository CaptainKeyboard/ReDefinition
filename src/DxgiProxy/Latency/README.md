# Latency

What shortens the time from an input to the screen: NVIDIA Reflex, AMD Anti-Lag 2, Intel XeLL and
the latency brake. It also holds the measures that say where a frame's time goes.

## Start here

| File | Read it for |
|---|---|
| `src/DxgiProxy/Latency/LowLatency.h` | `LowLatency`: the modes, where each call comes from, what `Describe` reports |
| `src/DxgiProxy/Latency/LowLatency.cpp` | Reflex through NVAPI, Anti-Lag 2, XeLL, the brake and the latency measure |
| `src/DxgiProxy/Latency/BrakeController.h` | The brake's controller, free of the GPU and the clock |
| `src/DxgiProxy/Latency/WaitStats.h` | `WaitTimer`: the proxy's CPU waits, counted by kind |
| `src/DxgiProxy/Latency/LoadMonitor.h` | The CPU and GPU load, sampled once a second |

## What it does

- `LowLatency::BeginGameFrame` runs on the main thread where a frame begins. It runs the brake or
  Anti-Lag 2's delay, Reflex's or XeLL's sleep, and then the simulation marker.
- `LowLatency::BeforePresent` and `LowLatency::AfterPresent` run on the presenting thread around
  the swapchain's `Present`. They send the present markers and signal the measure's fence.
- The measure runs from the frame's start to the GPU's end of its `Present`. It is the same on
  every GPU, and it logs its median and 90th percentile.
- `BrakeController` turns the frame's wall time and the waits the proxy met into the sleep before
  the next frame. `WaitStats.h` counts those waits.
- `LoadMonitor` reads the CPU time of the main thread and the render thread, and the GPU engine's
  load. It does so once a second on a thread of its own and hands them to the mod.
- `CpuSampler` samples where the main thread spends its time in native code and writes a file.

See [Low latency](../../../src/DxgiProxy/Latency/README.md),
[The proxy's present path](../../../docs/development/flow-proxy-present.md) and
[Measure where the frame's time goes](../../../src/Probe/README.md).

## What it uses and what uses it

It uses `Common`, `FrameGeneration` (`XessFg.h`) and `Upscalers` (`NvidiaGpu.h`). `SwapChain` calls
the present hooks and includes `WaitStats.h` and `LoadMonitor.h`. `Managed` exports the rest, and
`Raytracing` counts its slot waits with `WaitStats.h`.

## Rules of the area

- `LowLatency::Describe` reports one mode: 1 Reflex through NVAPI, 2 Streamline's Reflex with
  DLSS frame generation, 3 Anti-Lag 2, 4 XeLL, 0 none. The brake runs where none of the others
  lowers the latency.
- Under DLSS frame generation, Streamline brings its own Reflex and `LowLatency` stays out of its
  way. Reflex and Anti-Lag 2 stay off under XeSS-FG's swapchain.
- The sleep and the markers go on every frame, whether low latency is on or not
  (`LowLatency::BeginGameFrame`).
- The brake sleeps before Streamline's Reflex sleep, so that Reflex sleeps and marks after it
  (`LowLatency::BrakeBeforeFrame`).
- `BrakeController` has no Direct3D and no clock in it, so `tests/DxgiProxy/ProxyHarness.cpp` runs
  it on any machine.
- `LoadMonitor` does nothing while `enabled=0`, and the render thread never waits for it
  (`LoadMonitor::SetAllowed`).
- `CpuSampler` calls nothing that takes a lock while the main thread is stopped. That thread may
  hold the loader's or the heap's lock (`src/DxgiProxy/Latency/CpuSampler.h`).
