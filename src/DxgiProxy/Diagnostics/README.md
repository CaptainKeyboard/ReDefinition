# Diagnostics

The proxy's two measuring tools. The GPU profiler times Unity's frame by marker. The screen
recorder captures what the monitor showed, generated frames included.

## Start here

| File | Read it for |
|---|---|
| `src/DxgiProxy/Diagnostics/GpuProfiler.h` | How markers become timestamp queries, and the ids of the proxy's own |
| `src/DxgiProxy/Diagnostics/GpuProfiler.cpp` | `GpuProfiler::OnMarker`, and reading a frame a few presents later |
| `src/DxgiProxy/Diagnostics/ScreenRecorder.h` | `StartScreenRecording` and the calls the present path makes |
| `src/DxgiProxy/Diagnostics/ScreenRecorder.cpp` | Desktop duplication, and the fallback to the game's own frames |

## What it does

- The mod raises a marker through Unity's render thread. `GpuProfiler` ends a Direct3D 11
  timestamp query on Unity's immediate context at each, in the order the GPU meets them.
- A frame runs from one `Present` to the next, in a disjoint query of its own. The mod reads the
  last complete frame with `KspProfRead`.
- The proxy raises markers of its own inside the raytracing and Ray Reconstruction events, from
  `kProfNativeBase` up.
- `StartScreenRecording` duplicates the desktop on a thread of its own and writes PNG files and a
  `frames.txt` into a folder.
- If Windows refuses the desktop's copy, `RecordBackBuffer` records the game's frames at each
  `Present` instead. The frames frame generation makes are then not among them.
- `NoteGamePresent` names, for each recorded frame, the game's frame presented last before it.

See [Measure where the frame's time goes](../../../src/Probe/README.md).

## What it uses and what uses it

It uses `Common` and `SwapChain` (`SwapChainProxy.h`). `Managed` exports both tools,
`SwapChain` and `Raytracing` call the profiler, and `FrameGeneration` calls the recorder.

## Rules of the area

- The ids from `kProfNativeBase` (1000000) name the proxy's own markers. The mod gives them labels
  in `NativeLabels` in `src/Probe/GpuProfiler.cs`, and the two lists have to match.
- A frame's ticks count only where the GPU's clock held steady. Frames are read a few presents
  later, without waiting.
- Each marker is also a Direct3D 11 user annotation, so that a RenderDoc capture shows the same
  names.
- The recorder reads the frames back only after the recording is over, so that its loop keeps
  pace with the display.
- `GpuProfiler` and the recorder are never destroyed, because they hold Direct3D objects that may
  be gone at process exit.
