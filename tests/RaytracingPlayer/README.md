# RaytracingPlayer

A Unity 2019.4.18f1 player built like KSP's. It renders a small scene through the mod's raytracing
and upscaler code and the real proxy, and it checks the images.

## Start here

| File | Read it for |
|---|---|
| `tools/build_rt_player.py` | Building the player, the list of linked sources, and every run |
| `tests/RaytracingPlayer/Assets/Editor/BuildPlayer.cs` | How the player is set up: Direct3D 11, native graphics jobs, gamma |
| `tests/RaytracingPlayer/Assets/Runner/RtPlayerRunner.cs` | The scene, the checks, and the command line |
| `tests/RaytracingPlayer/Assets/Runner/RtTurnRunner.cs` | The recorded camera turn around a rocket |
| `tests/RaytracingPlayer/Assets/Runner/RtTurnUpscale.cs` | The same turn through FSR 3, DLSS and AMD's upscaler |
| `tests/RaytracingPlayer/Assets/Runner/GameStubs.cs` | The game's types the linked code names, stubbed |

## What it does

- `tools/build_rt_player.py` copies the mod's sources into `tests/RaytracingPlayer/Assets/Linked`,
  and the raytracing shaders into its `Resources`. It has Unity build the player in batch mode,
  puts `dxgi.dll` beside it and runs it.
- `RtPlayerRunner` builds a ground and two boxes, renders them without raytracing and with it,
  and prints `RTPLAYER` lines. Each run ends with `all checks passed` or a failure.
- It runs with forward rendering, with Unity's own deferred lighting, and with Deferred's lighting
  shaders from the installed game where Deferred is there. A fourth run, `-budget`, measures a
  scene of a flight's size at DLSS Quality's render size against the GPU time budget.
- `RtTurnRunner` writes each frame of a camera turn and the reference for each pose.
  `tools/analyze_rt_turn.py` compares them.
- `RtTurnUpscale` renders the turn at a smaller size with the upscaler's jitter and keeps the
  upscaled frame. `tools/analyze_upscale_turn.py` compares it.
- The stubs stand in for what the linked code asks of the game. `GameStubs.cs` has the GPU
  profiler, `ProxyLoad`, `KscGround` and `RtKspMods`. `DeferredStub.cs` names Deferred's main type,
  and `UpscalerRigStub.cs` has the rig's sharpness ceiling.

Run it with Unity 2019.4.18f1 at `C:\Program Files\Unity\Editor\Unity.exe` and a built proxy:

```
python tools\build_rt_player.py
python tools\build_rt_player.py --turn
python tools\build_rt_player.py --upscale
```

`--unity` and `--ksp` name other places, and `--no-build` runs the last build again. `--only ocean`
or `--only glow` runs one check in the run with Deferred's lighting. The images
and logs of each run go into `build\RaytracingPlayer\<run>`.
[Building and testing](../../docs/development/building-and-testing.md) lists the runs of `--turn`
and `--upscale`.

## Rules of the area

- `Assets/Linked`, `Assets/Scenes`, `Library` and the other generated folders are ignored by git.
  `copy_in` in `tools/build_rt_player.py` replaces `Assets/Linked` at every build, so edit the
  source in `src`, never the copy.
- A source in the `SOURCES` list of `tools/build_rt_player.py` compiles here without the game. If it
  names another type of the game, add a stub for it. `RtNative` is free of KSP's types for this
  reason.
- `UpscalerRigStub.MaximumSharpness` has to equal the rig's. `RaytracingPlayerStubTests` in
  `tests/ReDefinition.Tests/RaytracingPlayerStubTests.cs` holds the two together.
- The player's `ProjectSettings` stay at the game's Unity version, `2019.4.18f1`
  (`tests/RaytracingPlayer/ProjectSettings/ProjectVersion.txt`).
