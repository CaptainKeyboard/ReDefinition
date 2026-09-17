# Reviews

## How a change is reviewed

After every workstream:

1. The change against its research: does the code do what the sources say?
2. A clean build, the unit tests (`dotnet test tests/ReDefinition.Tests`), both check
   scripts in Windows PowerShell 5.1 and PowerShell 7, and -- for the proxy -- the
   harness and `tools/audit_ffx_fields.py`
   ([development/building-and-testing.md](../development/building-and-testing.md)).
3. `/code-review` over the commits of the workstream.
4. Every finding checked at its source before anything is changed, then fixed or
   answered, and recorded below with its status: **fixed** (what changed) or
   **answered** (why no change is needed). A closing pass reviews the fixes.

## Log

| Fixes in | Over | Findings |
|---|---|---|
| ReDefinition 0.1.0 | The interface for mods: `src/Api`, `SharedFrame`, `CameraCuts`, `HistoryResets`, `HookList`, `UpscalerRig.Hooks`, `D3d12Bridge`, `D3d12Compute`, the harness's compute passes, the wrapper, `docs/development/shared-foundation.md` | 1. **fixed** -- the wrapper's lookup throttle read `TickCount` as never negative, and found nothing for 24.9 days after it wrapped: the difference of two readings, unchecked. 2. **fixed** -- a texture written by one pass's `nextFrame` dispatch and used by another pass's dispatch before that result came back: the result is copied back first, and Direct3D 11 copies into a shared copy only after the last list that used it (`Shared.lastFence`). 3. **fixed** -- creates, destroys and releases went through rings a mod could overrun in one frame: each packet in memory of its own, freed 16 frames later. 4. **fixed** -- a buffer from `DispatchInto` executed again reads a packet a later dispatch wrote: the ring holds eight frames of dispatches, and the interface and the reference say the buffer runs once, in its frame. 5. **fixed** -- `Frame.FrameGenerationActive` was true in the bypass and while a rebuild was wanted, where `Present` sends no packet: `PresentsRendered`, shared with `Present`. 6. **fixed** -- the motion vector hooks were called in the rig's `Update`, before the frame's state was decided: called as the scene camera culls (`UpscalerRig.OnPreCull`). 7. **fixed** -- a camera cut and a mod's request in one frame reset twice: `HistoryResets` takes the request with the cut; a test. 8. **fixed** -- a dispatch refused for a texture without a native pointer left `LastRefusal` null: it names the texture. Closing pass: 9. **fixed** -- one fence carried values of both command streams, so a copy signalled by Direct3D 11 could release waits for a `nextFrame` list still running: a fence per direction (`copied`, `done`), and a failed allocator or list reset stops the dispatch. 10. **fixed** -- a texture bound twice in one dispatch got two transitions: one per resource for reads, and a texture written twice is refused. 11. **answered, guarded** -- the overlay object is not destroyed by the editors' switch, an additive load that keeps the rig's objects; its buffer is released should the object be destroyed from outside. |
| ReDefinition 0.1.0 | Access for mods: `ReDefinition.cginc` and `IncludeCheck`, HLSL compiled by the proxy (`CompileComputeShader`, `KspD3d12Compile`, `D3d12Bridge.Compile`, `D3D12.CreateComputePassFromSource` and `FromFile`), the wrapper with typed delegates, the example mod, the XML documentation of `src/Api`, the harness's compile checks | 1. **fixed** -- `CreateComputePassFromFile` let `Path.GetFullPath` throw on a malformed path into the mod, past the wrapper, which no longer catches: refused with the reason in `LastRefusal`. 2. **fixed** -- without the proxy, or without `d3dcompiler_47.dll`, `D3D12.LastCompilerMessages` held that reason and `LastRefusal` called it a compile error: `D3d12Bridge.Compile` tells compiled, compiler errors and not compiled apart, and only the compiler's output lands in `LastCompilerMessages`. Closing pass: 3. **fixed** -- empty HLSL, and a file that is not there, still reached the compiler call and came back as compiler errors: both refused before it, with the reason in `LastRefusal`. |
| ReDefinition 0.1.0 | First start in the game | 1. **fixed** -- ReDefinition did not show: `SharedFrame.Install` gave `GameEvents.onFloatingOriginShift` a static handler, whose missing `Target` makes KSP's `EvtDelegate` throw a NullReferenceException in `UpscalerAddon.Awake`. The handler is an instance method, and the install runs guarded, so a failure there costs only the interface. |
