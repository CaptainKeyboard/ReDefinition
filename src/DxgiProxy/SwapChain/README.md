# SwapChain

The proxy's front door: the exports that stand in for Windows' `dxgi.dll`, and the swapchain
that Unity gets in place of its own.

## Start here

| File | Read it for |
|---|---|
| `src/DxgiProxy/SwapChain/DxgiExports.cpp` | `DllMain`, the forwarded exports and the hook on the factory |
| `src/DxgiProxy/SwapChain/SwapChainProxy.h` | What the swapchain is, and how the files below split it |
| `src/DxgiProxy/SwapChain/SwapChainProxy.cpp` | Creating the Direct3D 12 swapchain and tearing it down |
| `src/DxgiProxy/SwapChain/SwapChainProxyPresent.cpp` | The present path: the copy, the fences, frame generation's turn |
| `src/DxgiProxy/SwapChain/SwapChainProxyState.h` | What all swapchains share process-wide |
| `src/DxgiProxy/ReDefinitionProxy.def` | The exports the DLL offers |

## What it does

- `DxgiExports.cpp` loads the system's `dxgi.dll` by full path, forwards every export to it, and
  patches vtable slot 15 of the factory, `CreateSwapChainForHwnd`.
- `SwapChainProxy` hands Unity a shared Direct3D 11 texture as its backbuffer. `Present` copies
  it into the backbuffer of a real Direct3D 12 swapchain.
- That swapchain is Streamline's, XeSS-FG's or FidelityFX's where one can generate frames, and a
  plain one otherwise.
- `SwapChainProxyBuffers.cpp` makes the shared texture and handles `ResizeBuffers`.
  `SwapChainProxyForwarding.cpp` answers COM identity and every call the real swapchain answers.
- `SwapChainProxyPacing.cpp` holds FSR's pacing settings, the refresh-rate thread, the
  half-refresh limit and the frame time report.
- `SwapChainProxyStreamline.cpp` and `SwapChainProxyXess.cpp` set up and tear down the
  Streamline side and the XeSS side.
- With `measureOnly` the factory hook wraps the real swapchain and only times `Present`
  (`SwapChainProxy::CreateMeasuring`).

See [The proxy's present path](../../../docs/development/flow-proxy-present.md) for one frame's
route, and [Frame generation](../../../docs/development/flow-proxy-present.md), "Pacing and V-Sync".

## What it uses and what uses it

It uses `Common`, `FrameGeneration`, `Upscalers`, `Latency`, `Raytracing`, `Diagnostics` and
`Managed` (`D3d12Compute.h`). `Managed` and `Diagnostics` include `SwapChainProxy.h`.

## Rules of the area

- The class implements `IDXGISwapChain4`, and `QueryInterface` offers the newer interfaces only
  where the real swapchain has them. KSP asks for the frame latency waitable object, and
  answering `E_NOINTERFACE` there stops Unity (`src/DxgiProxy/SwapChain/SwapChainProxy.h`).
- Only `GetBuffer` and `Present` are the proxy's. Everything else goes to the real swapchain
  (`src/DxgiProxy/SwapChain/SwapChainProxyForwarding.cpp`).
- Both halves of the shared backbuffer exist or neither does, so Unity never gets a texture that
  is not presented (`SwapChainProxy::CreateSharedColour`).
- The running totals of frames rendered and presented live in one 64-bit atomic. A reading never
  mixes two frames (`src/DxgiProxy/SwapChain/SwapChainProxyState.h`, `g_counters`).
- Only one swapchain at a time owns Streamline or XeSS-FG. A later one presents with FSR's frame
  generation (`src/DxgiProxy/SwapChain/SwapChainProxyXess.cpp`,
  `src/DxgiProxy/FrameGeneration/Streamline.h`).
- Teardown follows the order the runtimes ask for (`src/DxgiProxy/SwapChain/SwapChainProxy.cpp`).
