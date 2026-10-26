# FrameGeneration

Frame generation in the proxy: the inputs it needs from Unity, and the three runtimes that make
the generated frames. They are FSR, DLSS-G through Streamline and XeSS-FG.

## Start here

| File | Read it for |
|---|---|
| `src/DxgiProxy/FrameGeneration/FrameGeneration.h` | `FramePacket`, the `FrameGeneration` class and its lock |
| `src/DxgiProxy/FrameGeneration/FrameGeneration.cpp` | Who owns frame generation, and the state the mod asks for |
| `src/DxgiProxy/FrameGeneration/FrameGenerationPrepare.cpp` | FSR's work for each `Present` |
| `src/DxgiProxy/FrameGeneration/FrameGenerationDlss.cpp` | The same inputs handed to DLSS-G |
| `src/DxgiProxy/FrameGeneration/FrameGenerationXess.cpp` | The same inputs handed to XeSS-FG |
| `src/DxgiProxy/FrameGeneration/Streamline.h` | The Streamline host: ownership, version, tokens |
| `src/DxgiProxy/FrameGeneration/XessFg.h` | Intel's XeSS-FG and XeLL DLLs, and the types declared for them |

## What it does

- `FrameGeneration.cpp` owns the state. The swapchain proxy attaches and detaches the devices,
  and the mod switches frame generation and sets the multiplier.
- `FrameGenerationInputs.cpp` copies Unity's depth, motion vectors and HUD-less copy into
  textures both devices share, and flips them to the screen's orientation where needed.
- `FrameGenerationContext.cpp` makes and destroys FSR's context. `FrameGenerationPrepare.cpp`
  configures it and dispatches the prepare pass each frame.
- `FrameGenerationDlss.cpp` and `FrameGenerationXess.cpp` tag the same inputs for DLSS-G and
  XeSS-FG. `FrameGenerationUi.cpp` builds the UI plane DLSS-G reads (`dlssgUiRecomposition`).
- `FrameGenerationRate.cpp` holds frame generation off below the minimum rendered frame rate
  (`fgMinimumFps`).
- `FrameGenerationCheck.cpp` compares the HUD-less copy with the presented frame and checks the
  motion vectors. `FrameGenerationDump.cpp` writes a few frames of inputs for analysis.
- `FidelityFx.cpp` loads AMD's runtime. `Streamline.cpp` and `XessFg.cpp` load NVIDIA's and
  Intel's.

See [The proxy's present path](../../../docs/development/flow-proxy-present.md) for where it sits
in a frame, and [Frame generation](../../../docs/development/frame-generation.md) for what each
runtime asks for.

## What it uses and what uses it

It uses `Common`, `Diagnostics` (`ScreenRecorder.h`) and `Upscalers` (`NvidiaGpu.h`). `SwapChain`,
`Managed`, `Upscalers`, `Raytracing` and `Latency` include its headers.

## Rules of the area

- `FramePacket` is a layout shared with the mod. Size and magic come first and are checked on
  every packet, and the `static_assert`s hold the size to 360 bytes. The mod's copy is held by
  `tests/ReDefinition.Tests/FramePacketLayoutTests.cs`.
- The packet travels through Unity's render thread as render event 1, so it belongs to the frame
  it describes (`FrameGeneration::OnFramePacket`).
- The present path holds `FrameGeneration::ContextMutex` across the prepare dispatch and the
  present, because FidelityFX's API asks for external synchronisation. The packets and the mod's
  calls take it too, and names that end in `Locked` expect it held.
- Streamline runs one version, 2.14.16, and one swapchain owns it at a time. A swapchain made
  while another owns it presents without it (`src/DxgiProxy/FrameGeneration/Streamline.h`).
- `tools/checks/audit_ffx_fields.py` fails on a field of FSR's frame generation descriptors that
  the proxy neither sets nor lists with a reason. Run it after a change to these files.
- `FrameGeneration` is never destroyed. It holds Unity's textures across a swapchain made anew.
