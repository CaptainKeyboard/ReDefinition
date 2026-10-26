# Lower the latency

**For:** players who feel a delay between an input and the screen, most with raytracing
or frame generation.
**You need:** ReDefinition installed with the proxy ([installing.md](installing.md)) and
a graphics profile chosen.
**You get:** what lowers the latency on your GPU, how to see what runs, and which
settings of the proxy change it.

The latency here is the time from the start of a frame on the main thread to the GPU's
end of that frame's Present. It does not include the way from the GPU to the screen.
ReDefinition measures it the same way on every GPU and logs it, so that you can tell
which setting lowers it most.

## What runs on your GPU

| GPU | What lowers the latency |
|---|---|
| NVIDIA, with DLSS frame generation | Streamline's Reflex, and the latency brake before it |
| NVIDIA, otherwise | NVIDIA Reflex, and the latency brake |
| NVIDIA, with XeSS frame generation | Intel's XeLL while XeSS frame generation generates; the latency brake while it is held off |
| Radeon | the latency brake; AMD Anti-Lag 2 in its place once `antiLag2` is set |
| Intel | Intel's XeLL, where `libxell.dll` is there; with XeSS frame generation always |

Reflex needs a GeForce GTX 900 or newer. Anti-Lag 2 needs a Radeon RX 5000 or newer.
XeLL needs `libxell.dll` from Intel's XeSS SDK next to `KSP_x64.exe`
([upscaler-and-frame-generation.md](upscaler-and-frame-generation.md#xess-frame-generation)).

XeLL and Anti-Lag 2 run alone. Intel asks that nothing else lowers the latency while
XeLL's low latency mode is on, and AMD asks the same of Anti-Lag 2. Reflex and the brake
run together.

## See what runs

The *Low latency* row under *Display* in the settings window, in the *Frame generation*
group, has two buttons, *Auto* and *Off*. *Auto* names what lowers the latency, for example
*Auto (Reflex)*, *Auto (Anti-Lag 2)* or *Auto (XeLL)*. *Off* switches Reflex, XeLL and the
brake off, and Anti-Lag 2 as well. The row works once the proxy runs.

The diagnostics window's strip names Reflex, Anti-Lag 2 or XeLL beside the upscaler and
the frame generation. It also shows the measured latency, its median and its 90th
percentile. *Frame generation & latency* repeats what lowers it under *Running now*.

`ReDefinitionProxy.log` has the same measure every `reportSeconds`, 10 by default. The
line starts with `Latency:` and ends with what lowered it. A second `Latency:` line
gives the frame's stages. They are the simulation, the way from its end to the Present,
the Present, the way to the GPU's end, and the render thread's first call to the Present.

## The latency brake

With raytracing, frames queue up in the proxy, before the GPU has them: the game has
finished a frame and waits for a free slot. Reflex sees none of that wait, so it lowers
nothing there. The brake waits at the start of the frame instead, before the game reads
your input. It waits as long as the last frames waited in the proxy, less the target. The
GPU is still fed in time, so the frame rate stays.

On an RTX A4500 at the Medium profile with raytracing on *Full*, the brake took 9 to
14.5 ms off a latency of 58 to 72 ms. The frame rate stayed within one percent. Without
raytracing it takes under 1 ms off, because little queues
([src/DxgiProxy/Latency/README.md](../../src/DxgiProxy/Latency/README.md)).

The brake is on, with a target of 4 ms of waiting left in each frame. A lower target
lowers the latency further, and costs some frame rate where the CPU, not the GPU, limits it.

| Setting in `ReDefinitionProxy.ini` | Default | Does |
|---|---|---|
| `latencyBrake` | 1 | 0 switches the brake off |
| `latencyBrakeTargetMs` | 4 | the waiting left in each frame, in milliseconds. A lower number lowers the latency further |
| `latencyBrakeWaits` | 1 | which waits the brake counts: 1 the proxy's and the Present, 2 the proxy's alone |
| `latencyBrakeWithDlssg` | 1 | 1 lets the brake run under DLSS frame generation too, before Streamline's Reflex. 0 leaves DLSS frame generation to its Reflex alone |
| `lowLatency` | 1 | 0 leaves NVIDIA's Reflex mode off where it runs without DLSS frame generation. On an Intel GPU it sets XeLL's mode |
| `antiLag2` | 0 | on a Radeon RX 5000 or newer, 12 runs AMD Anti-Lag 2 on the proxy's Direct3D 12 device and 11 on the game's Direct3D 11 device. It takes the brake's place |
| `xell` | 1 | 0 leaves out Intel's XeLL on an Intel GPU where XeSS frame generation does not run. The brake lowers the latency then |

The proxy re-reads these within a second of the file being saved. `xell`, and
`lowLatency` on an Intel GPU, it reads when KSP starts.

## Tested hardware

| Lowers the latency | Tested on |
|---|---|
| The brake, Reflex | an NVIDIA RTX A4500, with FSR 3, DLSS, AMD's DLL and XeSS as the upscaler, with FSR and DLSS frame generation |
| XeLL with XeSS frame generation | the same RTX A4500, the cross-vendor way |
| XeLL on an Intel GPU, XeSS frame generation with more than one generated frame | not yet on an Intel Arc |
| Anti-Lag 2 | not yet on a Radeon |

Where each stands: [project/status.md](../project/status.md).
