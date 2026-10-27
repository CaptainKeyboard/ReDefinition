# Probe

The measurement runs and test scenes that KSP's command line starts. A probe here is a run driven
by a switch, never a Unity light probe.

## Start here

| File | Read it for |
|---|---|
| `src/Probe/SceneProbe.cs` | `-redefinitionProbe`: the flight that most other probes ride on. |
| `src/Probe/LightProbe.cs` | The raytracing's light by day and night, with GPU times and shots. |
| `src/Probe/GpuProfiler.cs` | `-redefinitionGpuProfile`: GPU time by camera, pass and mod. |
| `src/Probe/PerfMonitor.cs` | `-redefinitionPerfMonitor`: ReDefinition's cost on the main thread. |
| `src/Probe/ModCost.cs` | `-redefinitionProbeModCost`: what each graphics mod costs, switched off in turn. |
| `src/Probe/WindowProbe.cs` | `-redefinitionProbeWindow`: the settings window driven for shots. |

## What it does

- `SceneProbe` loads the save `ReDefinitionProbe`, flies the camera on a fixed path and writes the
  scene camera's stages into `ReDefinitionCaptures`.
- `LightProbe`, `ViewProbe`, `ShotVariant`, `VendorProbe`, `FlipProbe`, `StagingProbe` and
  `WindowProbe` each add one measurement or one set of shots to that flight.
- `LandProbe` puts the probe's command pod on another body's ground (`-redefinitionProbeLand`), so
  that `ViewProbe` shoots its light there; `tools/planet_shots.ps1` drives it.
- `GpuProfiler` puts markers into Unity's render stream and reports the GPU time between them.
- `PerfMonitor` times the Unity methods ReDefinition has on the main thread through Harmony.
- `ModCost` switches each graphics mod off and on and measures the difference.
- `RenderDocCapture` triggers a frame capture where KSP runs under RenderDoc.
- `ProbeSwitchReset` turns the light probe's switches off at every scene start.

## What it uses and what uses it

It uses Core, Modules, Proxy, Raytracing, Scenery, Settings, Ui and Window.
The add-on in the root, Menu, Modules, Raytracing and Upscaler name it, for its markers and
switches.

## Rules of the area

- A probe does nothing without its switch. Only `ProbeSwitchReset` always runs, to turn switches off.
- The scene probe's other switches work only with `-redefinitionProbe` (`SceneProbe.Argument`).
- While `SceneProbe.Requested` is true, ReDefinition does not save the player's settings, so the
  probe's changes stay off the disk (`ReDefinitionAddon.SaveSettingsIfChanged`).
- `ProbeSwitchReset` runs at every scene. A switch the light probe adds needs its line there.
- `RenderDocCapture` does nothing where `renderdoc.dll` is not in the process.
- The scripts that read a probe's output are in `tools`, for example `tools/analyze_probe.py`,
  `tools/flip_probe.ps1` and `tools/renderdoc_probe.ps1`.
