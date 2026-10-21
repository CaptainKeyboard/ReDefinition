# Benchmarks of upscalers, frame generation, raytracing and latency

**For:** anyone who changes the upscaler, frame generation, the raytracing or the proxy's
latency code and wants to know what the change did to the frame rate and the latency.
**You need:** KSP with ReDefinition and the proxy installed, the save the scene probe flies
(`ReDefinitionProbe`), Python 3, the repository open, and a machine that nobody uses
while the tool runs.
**You get:** the comparison tables of the latest runs, how to run the comparison or one of
its groups with one command, and what each column and each run's record means.

## Run the comparison

Close KSP first, then run this from the repository root:

```
python tools/benchmark_matrix.py run
```

The tool reads the scenario matrix `tools/benchmark_matrix.json`. It copies the install's
settings files and applies the graphics profile in one KSP start. Then it runs one KSP
start per frame generation kind. After the last start it puts every settings file back and
removes the settings files the run made, also when a start failed. It appends the run to
`docs/development/benchmarks/` as a JSON file and writes the tables on this page again.

Without options the tool runs the main matrix. The other groups, listed under
[the groups](#the-groups), run on their own:

```
python tools/benchmark_matrix.py run --group frame-steps
```

**Do not use the machine during a run.** DLSS frame generation generates nothing while
KSP's window is not in the foreground, and a KSP that the tool starts is not given the
foreground when another program's window is in front. The tool brings the game's window to
the front every two seconds while it runs. Clicking into another window, or starting a
program, takes the foreground away, and the phases in that time measure the rendered
frames alone. A phase that asked for frame generation and shows fewer than 1.5 frames
presented per rendered frame says in its notes that the window was not in front.
`REDEFINITION_BENCH_NO_FOREGROUND` set to any value leaves the window where Windows puts
it, to reproduce that.

With every DLL and package in place, the main matrix has four KSP starts after the profile
start. It holds 138 scenarios, and the first scenario after each change of the rendering
runs twice, so the tool plans 169 phases and about 42 minutes. The run `2026-10-21-0241`
recorded 138 phases, the two passes of a scenario merged into one. A start whose DLLs or
package are missing, or whose GPU vendor does not fit, is skipped, and the run's section
names it. On an NVIDIA GPU with none of the optional files that is one start with 50
phases (44 scenarios, 6 of them run twice) and about 13 minutes. To see the starts, the
phases and, per group, the number of starts, phases and minutes without running KSP, run
this. Add `--group all` or `--group NAME` to see other groups:

```
python tools/benchmark_matrix.py plan
```

These options change what the tool uses:

| Option | Default | What it is |
|---|---|---|
| `--ksp` | Steam's KSP folder, or `REDEFINITION_KSP` | the KSP install |
| `--settings-template` | the install's own `settings.cfg`, as it was before the run | the mod's settings the profile start begins from |
| `--matrix` | `tools/benchmark_matrix.json` | the scenario matrix |
| `--dlssg-package` | `.local/dlssg_x`, or `REDEFINITION_DLSSG_PACKAGE` | the unpacked DLSS-G package for RTX 20 and 30 GPUs |
| `--amd-dll-dir` | `.local/amd`, or `REDEFINITION_AMD_DLL_DIR` | the folder with `amd_fidelityfx_upscaler_dx12.dll` |
| `--xess-dll-dir` | `.local/xess`, or `REDEFINITION_XESS_DLL_DIR` | the folder with Intel's `libxess.dll` |
| `--xess-fg-dll-dir` | `.local/xessfg`, or `REDEFINITION_XESS_FG_DLL_DIR` | the folder with Intel's `libxess_fg.dll` and `libxell.dll` |
| `--raw` | `.local/benchmarks`, or `REDEFINITION_BENCH_RAW` | where each start's logs and screenshots go |
| `--group` | the main matrix | a group to run, repeatable, or `all` |
| `--only` | every start | only the starts whose id contains this text |
| `--no-profile` | the profile is applied | skip the start that applies the profile |
| `--no-doc` | the page is written again | write the JSON only |
| `--vendor` | the vendor Windows names | the GPU vendor that decides which upscalers and latency models run |

If a DLL or the DLSS-G package is not there, the tool skips what needs it. The run's section
names what it skipped. The tool hands the AMD DLL's folder to the proxy as
`amdUpscalerDirectory`, XeSS's as `xessDirectory` and XeSS frame generation's as
`xessFgDirectory`, so the DLLs stay where they are. It copies the DLSS-G package next to
`KSP_x64.exe` only for the start that needs it, and removes it after that start.

To read a run's logs again, for example after a change to the parser, run this:

```
python tools/benchmark_matrix.py parse .local/benchmarks/<run>
```

To write this page again from the JSON files, run `python tools/benchmark_matrix.py render`.

## The groups

The main matrix is the default run: the upscalers, the frame generation kinds and the latency
models against each other. Every other group answers one question the main matrix leaves
open, and runs on its own with `run --group NAME`. A group is the matrix with its own values
over it. The numbers are for the RTX A4500 at the Medium profile.

| Group | What it answers | Starts | Phases | Minutes |
|---|---|---|---|---|
| `main` | every upscaler, frame generation kind and latency model at raytracing Off and Full | 4 | 169 | 41 |
| `rt-modes` | what raytracing Shadows and Light cost against Off and Full, for FSR 3 and DLSS | 1 | 32 | 8 |
| `frame-steps` | what DLSS-G's 2x to 6x and Dynamic cost and which of them the GPU delivers | 2 | 18 | 7 |
| `dlssg-presets` | DLSS-G's preset Auto against preset C with all four upscalers | 3 | 60 | 17 |
| `latency-brake` | the latency brake's target of 4, 2 and 1 ms with no, FSR and DLSS-G frame generation | 2 | 26 | 9 |
| `blas` | how the acceleration structure build variants change the flight's first seconds | 9 | 18 | 19 |
| `grass` | the radiance cache, the cut-out tests, the opacity micromaps and the raytracing modes over grass | 2 | 46 | 13 |

The minutes are `plan`'s, with the profile start not counted. The groups that use DLSS-G need
the package, the presets group also needs the AMD and XeSS DLLs, and a group whose files are
missing says so in the run's section, as the main matrix does.

* `rt-modes` sets the raytracing to Off, Shadows, Light and Full in turn. DLSS runs with Ray
  Reconstruction on and off wherever the raytracing is on.
* `frame-steps` writes `MaxGeneratedFrames=5` into the DLSS-G package's ini for its start, so
  that the package allows 6x, and sets the mod's `frameGenerationMultiplier` to each step
  while the game runs. Each row says in "Shown per rendered" how many frames the monitor got
  per rendered frame and in "Delivered" whether that is the step asked for. A step the GPU
  or the driver did not deliver says so in its notes. Dynamic has no number to be held to.
* `dlssg-presets` runs the package's `[DLSSG] Preset` as `Auto` and as `C`. FSR's frame
  generation and no frame generation are the references in the same table.
* `latency-brake` runs `latencyBrakeTargetMs` at 4 (the default), 2 and 1. The brake also
  runs under DLSS-G (`latencyBrakeWithDlssg`).
* `blas` runs one KSP start per variant of `rtBlasBatch`, `rtBlasPool`, `rtBlasCompaction`,
  `rtBlasBudget`, `rtTlasFastTrace` and `rtMegaGeometry`, since the meshes are built at the
  flight's start. Each start has one rendering and two phases. The table adds the slowest
  frame of the start's mesh builds, on the GPU and on the CPU, and the meshes and triangles
  built.
* `grass` runs on two scenes, the launch pad and a field of grass.

The grass scene is `-redefinitionProbeGrass` (`src/Probe/SceneProbe.cs`). The probe does
not start the flight on the pad. It puts a command pod on the ground at the first place,
by distance from the pad, from 3 km out, where a 7 by 7 grid of samples 40 m apart is
all Grasslands biome, above 80 m and level. Parallax's Kerbin grass grows in that biome from
25 m of altitude on. On this save that place is 5 km from the pad, 121 m above the sea. The
probe logs where it put the pod and that the pod stands on the ground.

## What a start measures

Each start flies the scene probe's vessel on the launch pad
(`-redefinitionProbeStay -redefinitionProbeClouds -redefinitionProbeShot`). The mod's
settings come from the `settings.cfg` the profile start left, with the matrix's keys set
over it. After 30 seconds in the flight, the probe applies each set of keys in turn and
holds it for 12 seconds (`-redefinitionProbeShotIni`):

* A `mod.<key>` key is one of ReDefinition's own settings, by its `settings.cfg` key. The
  probe applies it as the settings window does.
* Every other key goes into `ReDefinitionProxy.ini`. The proxy re-reads the file within a
  second and logs `Config reloaded:` with every value.

A phase is one of these sets. Its measurement window opens a while after the proxy's
reload and closes when the probe takes the phase's screenshot:

| Phase | Settling time | Length |
|---|---|---|
| The first with another upscaler, raytracing mode or Ray Reconstruction | 8 s (`settle_after_rebuild_seconds`) | 24 s (`phase_repeats_after_rebuild`) |
| Every other | 2 s (`settle_seconds`) | 12 s (`phase_repeats`) |

A change of the rendering builds the upscaler and the raytracing's scene again. In the
probe's logs that took about 5 seconds, with frames of half a second in between. Every set
holds `reportSeconds=1`, so a phase has 8 to 9 reports, and 14 to 15 after a change.

Only what the proxy reads at load needs a start of its own: the frame generation kind
(`frameGenerationTechnique`: `fsr`, `dlss` or `xess`). DLSS-G on RTX 20 and 30 GPUs also needs
the package next to `KSP_x64.exe`, with the ini values the start writes into it. The
upscaler's DLL folders are read again when the upscaler is set up, so each start sets them
once. The main matrix's starts are `fsr`, `dlssg` and `xessfg`, and `dlssg-c`, which is
DLSS-G with the package's preset C for FSR 3 at both raytracing modes. Both DLSS-G starts set
the package's `[Logging] FrameTiming`, so that its logs hold the GPU time of the frame
generation. The starts of another group begin with the group's id, for example
`frame-steps-dlssg`, and end with the variant and the scene where the group has several.

Every set names every key the matrix varies, since the probe's sets add to each other:

| Phase setting | Keys the probe sets |
|---|---|
| The upscaler | `mod.technique`, for example `Dlss` |
| The raytracing mode | `mod.raytracing`, `Off` or `Full` |
| Ray Reconstruction | `mod.rayReconstruction`; `True` where it does not apply |
| Frame generation off | `fgMinimumFps=1000`, which holds it off |
| Frame generation on | `fgMinimumFps=0`, which never holds it off |
| No latency model | `lowLatency=0`, `latencyBrake=0`, `antiLag2=0` |
| Reflex | `lowLatency=1`, `latencyBrake=0` |
| Brake 4 ms | `lowLatency=0`, `latencyBrake=1`, `latencyBrakeTargetMs=4` |
| Reflex and brake 4 ms | `lowLatency=1`, `latencyBrake=1`, `latencyBrakeTargetMs=4` |
| Anti-Lag 2 | `antiLag2=12`, on an AMD GPU only |
| XeLL | `lowLatency=1`, `latencyBrake=0`, in the XeSS-FG start only |
| The radiance cache | `rtRadianceCache=1`, for FSR 3 at raytracing *Full*, with no latency model (XeLL in the XeSS-FG start) |
| Opacity micromaps | `rtOpacityMicromaps=1`, the last rendering of the start |
| A frame generation step | `mod.frameGenerationMultiplier`: `2x` to `6x` or `Dynamic` |
| A latency brake target | `latencyBrakeTargetMs=4`, `2` or `1` with `latencyBrake=1` |
| A cut-out test | `rtCutoutTest=1` or `2`; every other phase has `0` |

"Frame generation off" holds frame generation off while its inputs are still captured. It
is not the same as `frameGeneration = False` in the mod's settings.

## Add a scenario

Each list in `tools/benchmark_matrix.json` multiplies the phases:

* `techniques`: one line per upscaler. `log_name` is the name the upscaler's
  `Upscaler running on` line in `KSP.log` gives, so the tool can say when another one ran.
  A technique with `ray_reconstruction` is measured with Ray Reconstruction on and off for
  each raytracing mode other than `Off`.
* `raytracing`: the raytracing modes.
* `frame_generation`: one entry per frame generation kind, each a start of its own.
* `latency`: the latency models. `vendors` limits one to those GPUs.
* `baseline`: the scenario every row is compared with.
* `separate_starts`: `technique`, `raytracing` or `ray_reconstruction`, each named one
  split into starts of its own. The list is empty by default.
* `radiance_cache` and `opacity_micromaps`: a rendering of its own for one technique and
  raytracing mode, with the latency models it names. Every other phase has the key at 0.
* `proxy_load`: keys the proxy reads at load. The raytracing's `rtBlas...`,
  `rtTlasFastTrace` and `rtMegaGeometry` are pinned there, since the meshes are built before
  the first phase. Comparing them takes a start of its own per value, as the `blas` group does.
* `groups`: each entry is the matrix with its own values over it. `techniques`,
  `frame_generation` and `latency` name entries of the matrix by id, or extend one with
  `{"extends": "dlssg", "id": "...", ...}`. `proxy_load` and `mod_settings` merge into the
  matrix's, every other key replaces it. A group is `"default": true` to run without
  `--group`; only the main matrix is.
* A group's `scenes` and `variants`: the probe's scenes (`scenes` at the top of the file,
  each with its probe arguments) and values for the proxy at load (`proxy_load`), each
  combination a start of its own.
* A group's `tweaks`: renderings of their own with live proxy keys, like the radiance cache.
  `tweak_defaults` is what every other phase has, and `"last": true` puts a tweak at the end
  of the start.
* A frame generation entry's `steps`: the multipliers it is measured at, set through the
  probe's `mod.frameGenerationMultiplier`.
* A group's `show`: `ratio` adds the columns of the frames shown per rendered frame, and
  `rt_builds` adds those of the mesh builds.

XeSS, for example, is this line in `techniques`:

```
{"id": "xess", "name": "XeSS", "log_name": "XeSS (DLL)", "settings": {"technique": "Xess"}, "proxy_load": {"xessDirectory": "{xess_dll_dir}"}, "requires": ["xess_dll_dir"]}
```

## The comparison table

There is one table per group, from the latest run that covers the group. Each row is one
scenario of that run: the scene where it is not the pad, the upscaler, the raytracing mode
with Ray Reconstruction where it applies, the frame generation with its step and the latency
model. The baseline, the reference without frame generation and the previous run are those of
the group, and of the row's scene.

| Column | What it is |
|---|---|
| fps | the frames KSP rendered a second in the window: all frames over all frame times |
| Shown fps | the frames the monitor showed a second, from DXGI's statistics; with frame generation the generated frames count |
| Latency median ms | from the frame's start to the GPU's end of its Present, the median of the reports' medians |
| Latency p90 ms | the same, the median of the reports' 90th percentiles |
| fps vs baseline | the fps against the baseline row, in percent |
| Latency vs baseline | the latency median against the baseline row, in ms |
| fps vs FG off, no model | the fps against the same rendering with frame generation off and no latency model |
| Latency vs FG off, no model | the latency median against that row, in ms |
| fps vs previous run | the fps against the same scenario in the run before |
| Shown per rendered | frames shown per rendered frame (groups with steps) |
| Delivered | whether that is the step asked for: `yes`, `not delivered`, `n/a` for Dynamic |
| Slowest build GPU ms, CPU ms, Meshes, Triangles | the start's mesh builds (the `blas` group) |
| Notes | what did not run as asked: another upscaler, no generated frames, a key the proxy does not have |

The latency is the proxy's own measure, the same on every GPU. The proxy logs it as
`Latency:` every `reportSeconds`. It does not include the time from the GPU to the screen.

<!-- benchmark-table:start -->
### Main matrix (`main`)

Run `2026-10-21-0241`. Its machine and options are under [the runs](#the-runs).

| Scenario | fps | Shown fps | Latency median ms | Latency p90 ms | fps vs baseline | Latency vs baseline | fps vs FG off, no model | Latency vs FG off, no model | fps vs previous run | Notes |
|---|---|---|---|---|---|---|---|---|---|---|
| FSR 3 · RT Off · FG off · none | 72.4 | 72.2 | 21.8 | 23.0 | +0.0 % | +0.0 ms | – | – | +3.0 % | baseline |
| FSR 3 · RT Off · FG off · Reflex | 72.2 | 71.9 | 22.0 | 23.1 | -0.3 % | +0.2 ms | -0.3 % | +0.2 ms | +2.7 % |  |
| FSR 3 · RT Off · FG off · brake 4 ms | 72.5 | 72.2 | 21.9 | 23.4 | +0.1 % | +0.1 ms | +0.1 % | +0.1 ms | +3.4 % |  |
| FSR 3 · RT Off · FG off · Reflex + brake 4 ms | 72.8 | 72.8 | 22.0 | 23.1 | +0.6 % | +0.2 ms | +0.6 % | +0.2 ms | +3.3 % |  |
| FSR 3 · RT Off · FG FSR · none | 66.7 | 133.4 | 29.6 | 32.8 | -7.9 % | +7.8 ms | -7.9 % | +7.8 ms | +8.3 % |  |
| FSR 3 · RT Off · FG FSR · Reflex | 66.0 | 132.2 | 29.1 | 32.5 | -8.8 % | +7.3 ms | -8.8 % | +7.3 ms | +7.5 % |  |
| FSR 3 · RT Off · FG FSR · brake 4 ms | 65.9 | 131.8 | 28.7 | 31.3 | -9.0 % | +6.9 ms | -9.0 % | +6.9 ms | +7.0 % |  |
| FSR 3 · RT Off · FG FSR · Reflex + brake 4 ms | 65.8 | 131.9 | 28.8 | 31.7 | -9.1 % | +7.0 ms | -9.1 % | +7.0 ms | +7.3 % |  |
| FSR 3 · RT Full · FG off · none | 45.0 | 44.9 | 57.8 | 59.1 | -37.8 % | +36.0 ms | – | – | +2.5 % |  |
| FSR 3 · RT Full · FG off · Reflex | 45.2 | 45.1 | 57.8 | 59.0 | -37.6 % | +36.0 ms | +0.4 % | +0.0 ms | +2.5 % |  |
| FSR 3 · RT Full · FG off · brake 4 ms | 45.2 | 45.0 | 48.6 | 54.8 | -37.6 % | +26.8 ms | +0.4 % | -9.2 ms | +3.0 % |  |
| FSR 3 · RT Full · FG off · Reflex + brake 4 ms | 45.1 | 45.0 | 49.2 | 54.2 | -37.7 % | +27.4 ms | +0.2 % | -8.6 ms | +2.5 % |  |
| FSR 3 · RT Full · FG FSR · none | 39.6 | 79.0 | 60.2 | 61.8 | -45.3 % | +38.4 ms | -12.0 % | +2.4 ms | +6.5 % |  |
| FSR 3 · RT Full · FG FSR · Reflex | 39.6 | 79.0 | 60.1 | 61.8 | -45.3 % | +38.3 ms | -12.0 % | +2.3 ms | +6.5 % |  |
| FSR 3 · RT Full · FG FSR · brake 4 ms | 39.4 | 78.9 | 45.7 | 51.7 | -45.6 % | +23.9 ms | -12.4 % | -12.1 ms | +6.2 % |  |
| FSR 3 · RT Full · FG FSR · Reflex + brake 4 ms | 39.3 | 78.8 | 45.6 | 51.8 | -45.7 % | +23.8 ms | -12.7 % | -12.2 ms | +5.6 % |  |
| FSR 3 · RT Full, radiance cache · FG off · none | 45.4 | 45.8 | 57.1 | 58.5 | -37.3 % | +35.3 ms | – | – | +2.3 % |  |
| FSR 3 · RT Full, radiance cache · FG FSR · none | 40.0 | 80.0 | 59.4 | 61.1 | -44.8 % | +37.6 ms | -11.9 % | +2.3 ms | +6.7 % |  |
| DLSS · RT Off · FG off · none | 69.7 | 69.4 | 23.5 | 25.3 | -3.7 % | +1.7 ms | – | – | +2.2 % |  |
| DLSS · RT Off · FG off · Reflex | 71.8 | 71.6 | 23.3 | 25.8 | -0.8 % | +1.5 ms | +3.0 % | -0.2 ms | +5.4 % |  |
| DLSS · RT Off · FG off · brake 4 ms | 70.4 | 70.4 | 23.5 | 25.5 | -2.8 % | +1.7 ms | +1.0 % | +0.0 ms | +0.4 % |  |
| DLSS · RT Off · FG off · Reflex + brake 4 ms | 70.9 | 70.6 | 23.5 | 25.6 | -2.1 % | +1.7 ms | +1.7 % | +0.0 ms | +1.9 % |  |
| DLSS · RT Off · FG FSR · none | 62.1 | 124.3 | 33.1 | 35.6 | -14.2 % | +11.3 ms | -10.9 % | +9.6 ms | +8.6 % |  |
| DLSS · RT Off · FG FSR · Reflex | 62.3 | 124.7 | 33.4 | 35.6 | -14.0 % | +11.6 ms | -10.6 % | +9.9 ms | +8.7 % |  |
| DLSS · RT Off · FG FSR · brake 4 ms | 61.8 | 123.6 | 31.0 | 34.0 | -14.6 % | +9.2 ms | -11.3 % | +7.5 ms | +7.9 % |  |
| DLSS · RT Off · FG FSR · Reflex + brake 4 ms | 62.0 | 124.0 | 31.3 | 33.9 | -14.4 % | +9.5 ms | -11.0 % | +7.8 ms | +8.8 % |  |
| DLSS · RT Full, RR on · FG off · none | 35.9 | 35.9 | 74.6 | 76.1 | -50.4 % | +52.8 ms | – | – | +2.0 % |  |
| DLSS · RT Full, RR on · FG off · Reflex | 36.1 | 36.0 | 75.0 | 76.1 | -50.1 % | +53.2 ms | +0.6 % | +0.4 ms | +2.3 % |  |
| DLSS · RT Full, RR on · FG off · brake 4 ms | 36.0 | 36.0 | 63.2 | 66.1 | -50.3 % | +41.4 ms | +0.3 % | -11.4 ms | +2.0 % |  |
| DLSS · RT Full, RR on · FG off · Reflex + brake 4 ms | 36.0 | 36.0 | 63.0 | 66.3 | -50.3 % | +41.2 ms | +0.3 % | -11.6 ms | +2.0 % |  |
| DLSS · RT Full, RR on · FG FSR · none | 32.2 | 64.0 | 77.5 | 79.0 | -55.5 % | +55.7 ms | -10.3 % | +2.9 ms | +5.2 % |  |
| DLSS · RT Full, RR on · FG FSR · Reflex | 32.2 | 64.0 | 77.3 | 79.0 | -55.5 % | +55.5 ms | -10.3 % | +2.7 ms | +5.2 % |  |
| DLSS · RT Full, RR on · FG FSR · brake 4 ms | 32.2 | 64.1 | 66.3 | 68.5 | -55.5 % | +44.5 ms | -10.3 % | -8.3 ms | +5.2 % |  |
| DLSS · RT Full, RR on · FG FSR · Reflex + brake 4 ms | 32.2 | 64.0 | 66.4 | 68.4 | -55.5 % | +44.6 ms | -10.3 % | -8.2 ms | +5.6 % |  |
| DLSS · RT Full, RR off · FG off · none | 43.4 | 43.5 | 60.3 | 61.8 | -40.1 % | +38.5 ms | – | – | +2.6 % |  |
| DLSS · RT Full, RR off · FG off · Reflex | 43.5 | 43.7 | 60.5 | 61.8 | -39.9 % | +38.7 ms | +0.2 % | +0.2 ms | +2.4 % |  |
| DLSS · RT Full, RR off · FG off · brake 4 ms | 43.4 | 43.4 | 49.6 | 57.0 | -40.1 % | +27.8 ms | +0.0 % | -10.7 ms | +2.1 % |  |
| DLSS · RT Full, RR off · FG off · Reflex + brake 4 ms | 43.5 | 43.6 | 49.0 | 55.6 | -39.9 % | +27.2 ms | +0.2 % | -11.3 ms | +2.8 % |  |
| DLSS · RT Full, RR off · FG FSR · none | 38.2 | 76.7 | 62.7 | 64.4 | -47.2 % | +40.9 ms | -12.0 % | +2.4 ms | +6.1 % |  |
| DLSS · RT Full, RR off · FG FSR · Reflex | 38.3 | 76.8 | 62.6 | 64.4 | -47.1 % | +40.8 ms | -11.8 % | +2.3 ms | +6.4 % |  |
| DLSS · RT Full, RR off · FG FSR · brake 4 ms | 38.1 | 76.2 | 47.6 | 50.9 | -47.4 % | +25.8 ms | -12.2 % | -12.7 ms | +5.8 % |  |
| DLSS · RT Full, RR off · FG FSR · Reflex + brake 4 ms | 38.2 | 76.6 | 47.3 | 53.3 | -47.2 % | +25.5 ms | -12.0 % | -13.0 ms | +6.1 % |  |
| AMD DLL · RT Off · FG off · none | 69.8 | 69.7 | 23.1 | 24.9 | -3.6 % | +1.3 ms | – | – | +0.4 % |  |
| AMD DLL · RT Off · FG off · Reflex | 71.4 | 71.4 | 23.1 | 24.8 | -1.4 % | +1.3 ms | +2.3 % | +0.0 ms | +2.1 % |  |
| AMD DLL · RT Off · FG off · brake 4 ms | 70.6 | 70.6 | 23.1 | 24.9 | -2.5 % | +1.3 ms | +1.1 % | +0.0 ms | +1.9 % |  |
| AMD DLL · RT Off · FG off · Reflex + brake 4 ms | 70.6 | 70.2 | 23.0 | 24.7 | -2.5 % | +1.2 ms | +1.1 % | -0.1 ms | +1.0 % |  |
| AMD DLL · RT Off · FG FSR · none | 63.1 | 126.4 | 32.3 | 34.9 | -12.8 % | +10.5 ms | -9.6 % | +9.2 ms | +8.6 % |  |
| AMD DLL · RT Off · FG FSR · Reflex | 62.9 | 125.9 | 32.2 | 34.7 | -13.1 % | +10.4 ms | -9.9 % | +9.1 ms | +8.3 % |  |
| AMD DLL · RT Off · FG FSR · brake 4 ms | 62.8 | 125.8 | 30.8 | 33.6 | -13.3 % | +9.0 ms | -10.0 % | +7.7 ms | +7.9 % |  |
| AMD DLL · RT Off · FG FSR · Reflex + brake 4 ms | 62.9 | 125.6 | 30.7 | 33.7 | -13.1 % | +8.9 ms | -9.9 % | +7.6 ms | +8.8 % |  |
| AMD DLL · RT Full · FG off · none | 43.3 | 43.3 | 60.6 | 61.9 | -40.2 % | +38.8 ms | – | – | +2.4 % |  |
| AMD DLL · RT Full · FG off · Reflex | 43.4 | 43.4 | 60.7 | 61.8 | -40.1 % | +38.9 ms | +0.2 % | +0.1 ms | +2.1 % |  |
| AMD DLL · RT Full · FG off · brake 4 ms | 43.4 | 43.4 | 49.7 | 56.3 | -40.1 % | +27.9 ms | +0.2 % | -10.9 ms | +2.4 % |  |
| AMD DLL · RT Full · FG off · Reflex + brake 4 ms | 43.3 | 43.4 | 49.8 | 56.5 | -40.2 % | +28.0 ms | +0.0 % | -10.8 ms | +1.9 % |  |
| AMD DLL · RT Full · FG FSR · none | 38.2 | 76.6 | 62.9 | 64.6 | -47.2 % | +41.1 ms | -11.8 % | +2.3 ms | +6.1 % |  |
| AMD DLL · RT Full · FG FSR · Reflex | 38.2 | 76.4 | 63.0 | 64.6 | -47.2 % | +41.2 ms | -11.8 % | +2.4 ms | +6.1 % |  |
| AMD DLL · RT Full · FG FSR · brake 4 ms | 38.1 | 76.2 | 47.6 | 51.8 | -47.4 % | +25.8 ms | -12.0 % | -13.0 ms | +5.8 % |  |
| AMD DLL · RT Full · FG FSR · Reflex + brake 4 ms | 38.1 | 76.2 | 47.7 | 52.7 | -47.4 % | +25.9 ms | -12.0 % | -12.9 ms | +5.8 % |  |
| XeSS · RT Off · FG off · none | 70.2 | 70.0 | 24.1 | 26.2 | -3.0 % | +2.3 ms | – | – | +2.9 % |  |
| XeSS · RT Off · FG off · Reflex | 70.0 | 69.8 | 24.3 | 26.4 | -3.3 % | +2.5 ms | -0.3 % | +0.2 ms | +2.0 % |  |
| XeSS · RT Off · FG off · brake 4 ms | 71.0 | 71.0 | 24.2 | 26.2 | -1.9 % | +2.4 ms | +1.1 % | +0.1 ms | +4.0 % |  |
| XeSS · RT Off · FG off · Reflex + brake 4 ms | 71.1 | 70.8 | 24.2 | 26.2 | -1.8 % | +2.4 ms | +1.3 % | +0.1 ms | +3.3 % |  |
| XeSS · RT Off · FG FSR · none | 60.9 | 121.9 | 34.2 | 36.5 | -15.9 % | +12.4 ms | -13.2 % | +10.1 ms | +8.4 % |  |
| XeSS · RT Off · FG FSR · Reflex | 61.2 | 122.4 | 34.6 | 36.5 | -15.5 % | +12.8 ms | -12.8 % | +10.5 ms | +8.9 % |  |
| XeSS · RT Off · FG FSR · brake 4 ms | 61.0 | 122.0 | 32.5 | 35.4 | -15.7 % | +10.7 ms | -13.1 % | +8.4 ms | +8.7 % |  |
| XeSS · RT Off · FG FSR · Reflex + brake 4 ms | 60.8 | 121.7 | 32.2 | 35.3 | -16.0 % | +10.4 ms | -13.4 % | +8.1 ms | +8.4 % |  |
| XeSS · RT Full · FG off · none | 41.8 | 41.8 | 62.8 | 64.3 | -42.3 % | +41.0 ms | – | – | +2.2 % |  |
| XeSS · RT Full · FG off · Reflex | 41.9 | 42.0 | 63.0 | 64.2 | -42.1 % | +41.2 ms | +0.2 % | +0.2 ms | +2.2 % |  |
| XeSS · RT Full · FG off · brake 4 ms | 41.8 | 42.0 | 50.8 | 57.5 | -42.3 % | +29.0 ms | +0.0 % | -12.0 ms | +2.0 % |  |
| XeSS · RT Full · FG off · Reflex + brake 4 ms | 41.6 | 41.8 | 51.5 | 57.8 | -42.5 % | +29.7 ms | -0.5 % | -11.3 ms | +1.5 % |  |
| XeSS · RT Full · FG FSR · none | 37.0 | 74.0 | 65.4 | 66.9 | -48.9 % | +43.6 ms | -11.5 % | +2.6 ms | +5.7 % |  |
| XeSS · RT Full · FG FSR · Reflex | 37.0 | 74.0 | 65.6 | 67.2 | -48.9 % | +43.8 ms | -11.5 % | +2.8 ms | +6.0 % |  |
| XeSS · RT Full · FG FSR · brake 4 ms | 36.6 | 73.1 | 51.8 | 54.2 | -49.4 % | +30.0 ms | -12.4 % | -11.0 ms | +4.6 % |  |
| XeSS · RT Full · FG FSR · Reflex + brake 4 ms | 36.9 | 73.6 | 52.3 | 54.7 | -49.0 % | +30.5 ms | -11.7 % | -10.5 ms | +5.7 % |  |
| FSR 3 · RT Full, opacity micromaps · FG off · none | 40.5 | 40.7 | 65.0 | 66.4 | -44.1 % | +43.2 ms | – | – | +2.3 % |  |
| FSR 3 · RT Full, opacity micromaps · FG FSR · none | 35.6 | 71.1 | 68.3 | 70.0 | -50.8 % | +46.5 ms | -12.1 % | +3.3 ms | +4.7 % |  |
| FSR 3 · RT Off · FG DLSS-G · none | 66.1 | 132.2 | 46.6 | 49.2 | -8.7 % | +24.8 ms | -8.7 % | +24.8 ms | +0.2 % |  |
| FSR 3 · RT Off · FG DLSS-G · Reflex | 66.1 | 132.2 | 46.8 | 49.6 | -8.7 % | +25.0 ms | -8.7 % | +25.0 ms | +0.3 % |  |
| FSR 3 · RT Off · FG DLSS-G · brake 4 ms | 66.0 | 131.9 | 45.7 | 48.3 | -8.8 % | +23.9 ms | -8.8 % | +23.9 ms | -0.2 % |  |
| FSR 3 · RT Off · FG DLSS-G · Reflex + brake 4 ms | 66.0 | 132.2 | 45.7 | 48.3 | -8.8 % | +23.9 ms | -8.8 % | +23.9 ms | +0.3 % |  |
| FSR 3 · RT Full · FG DLSS-G · none | 40.2 | 80.6 | 72.1 | 73.9 | -44.5 % | +50.3 ms | -10.7 % | +14.3 ms | +0.2 % |  |
| FSR 3 · RT Full · FG DLSS-G · Reflex | 40.4 | 81.0 | 72.3 | 74.1 | -44.2 % | +50.5 ms | -10.2 % | +14.5 ms | +0.7 % |  |
| FSR 3 · RT Full · FG DLSS-G · brake 4 ms | 40.2 | 80.6 | 57.8 | 66.2 | -44.5 % | +36.0 ms | -10.7 % | +0.0 ms | -0.2 % |  |
| FSR 3 · RT Full · FG DLSS-G · Reflex + brake 4 ms | 40.2 | 80.4 | 58.0 | 65.6 | -44.5 % | +36.2 ms | -10.7 % | +0.2 ms | +0.2 % |  |
| FSR 3 · RT Full, radiance cache · FG DLSS-G · none | 39.9 | 79.8 | 72.9 | 74.7 | -44.9 % | +51.1 ms | -12.1 % | +15.8 ms | +0.5 % |  |
| DLSS · RT Off · FG DLSS-G · none | 61.1 | 122.2 | 51.6 | 53.4 | -15.6 % | +29.8 ms | -12.3 % | +28.1 ms | +0.3 % |  |
| DLSS · RT Off · FG DLSS-G · Reflex | 61.3 | 122.7 | 51.5 | 53.4 | -15.3 % | +29.7 ms | -12.1 % | +28.0 ms | +0.5 % |  |
| DLSS · RT Off · FG DLSS-G · brake 4 ms | 61.4 | 123.0 | 48.3 | 51.4 | -15.2 % | +26.5 ms | -11.9 % | +24.8 ms | +0.8 % |  |
| DLSS · RT Off · FG DLSS-G · Reflex + brake 4 ms | 61.4 | 122.9 | 48.2 | 51.3 | -15.2 % | +26.4 ms | -11.9 % | +24.7 ms | +0.7 % |  |
| DLSS · RT Full, RR on · FG DLSS-G · none | 32.1 | 63.8 | 91.0 | 93.0 | -55.7 % | +69.2 ms | -10.6 % | +16.4 ms | +0.6 % |  |
| DLSS · RT Full, RR on · FG DLSS-G · Reflex | 32.2 | 64.1 | 90.9 | 92.9 | -55.5 % | +69.1 ms | -10.3 % | +16.3 ms | +0.3 % |  |
| DLSS · RT Full, RR on · FG DLSS-G · brake 4 ms | 32.2 | 64.1 | 79.8 | 82.8 | -55.5 % | +58.0 ms | -10.3 % | +5.2 ms | +0.3 % |  |
| DLSS · RT Full, RR on · FG DLSS-G · Reflex + brake 4 ms | 32.2 | 64.2 | 80.5 | 82.7 | -55.5 % | +58.7 ms | -10.3 % | +5.9 ms | +0.3 % |  |
| DLSS · RT Full, RR off · FG DLSS-G · none | 38.4 | 76.7 | 75.7 | 77.6 | -47.0 % | +53.9 ms | -11.5 % | +15.4 ms | +0.5 % |  |
| DLSS · RT Full, RR off · FG DLSS-G · Reflex | 38.6 | 77.0 | 75.7 | 77.6 | -46.7 % | +53.9 ms | -11.1 % | +15.4 ms | +0.5 % |  |
| DLSS · RT Full, RR off · FG DLSS-G · brake 4 ms | 38.5 | 77.0 | 60.3 | 66.0 | -46.8 % | +38.5 ms | -11.3 % | +0.0 ms | +0.3 % |  |
| DLSS · RT Full, RR off · FG DLSS-G · Reflex + brake 4 ms | 38.5 | 77.0 | 59.9 | 66.7 | -46.8 % | +38.1 ms | -11.3 % | -0.4 ms | +0.3 % |  |
| AMD DLL · RT Off · FG DLSS-G · none | 62.4 | 124.8 | 49.8 | 52.3 | -13.8 % | +28.0 ms | -10.6 % | +26.7 ms | +0.8 % |  |
| AMD DLL · RT Off · FG DLSS-G · Reflex | 62.8 | 125.5 | 50.1 | 52.2 | -13.3 % | +28.3 ms | -10.0 % | +27.0 ms | +0.8 % |  |
| AMD DLL · RT Off · FG DLSS-G · brake 4 ms | 62.7 | 125.5 | 48.2 | 51.1 | -13.4 % | +26.4 ms | -10.2 % | +25.1 ms | +1.0 % |  |
| AMD DLL · RT Off · FG DLSS-G · Reflex + brake 4 ms | 62.7 | 125.2 | 48.0 | 51.2 | -13.4 % | +26.2 ms | -10.2 % | +24.9 ms | +0.8 % |  |
| AMD DLL · RT Full · FG DLSS-G · none | 38.7 | 77.3 | 74.8 | 76.9 | -46.5 % | +53.0 ms | -10.6 % | +14.2 ms | +0.3 % |  |
| AMD DLL · RT Full · FG DLSS-G · Reflex | 38.9 | 77.6 | 74.9 | 76.7 | -46.3 % | +53.1 ms | -10.2 % | +14.3 ms | +0.3 % |  |
| AMD DLL · RT Full · FG DLSS-G · brake 4 ms | 38.9 | 77.8 | 59.8 | 66.9 | -46.3 % | +38.0 ms | -10.2 % | -0.8 ms | +0.3 % |  |
| AMD DLL · RT Full · FG DLSS-G · Reflex + brake 4 ms | 38.9 | 77.6 | 60.6 | 65.3 | -46.3 % | +38.8 ms | -10.2 % | +0.0 ms | +0.3 % |  |
| XeSS · RT Off · FG DLSS-G · none | 60.3 | 120.6 | 51.8 | 53.8 | -16.7 % | +30.0 ms | -14.1 % | +27.7 ms | +0.8 % |  |
| XeSS · RT Off · FG DLSS-G · Reflex | 60.6 | 121.1 | 52.0 | 54.0 | -16.3 % | +30.2 ms | -13.7 % | +27.9 ms | +0.7 % |  |
| XeSS · RT Off · FG DLSS-G · brake 4 ms | 60.5 | 121.2 | 49.2 | 52.3 | -16.4 % | +27.4 ms | -13.8 % | +25.1 ms | +0.7 % |  |
| XeSS · RT Off · FG DLSS-G · Reflex + brake 4 ms | 60.6 | 121.1 | 49.2 | 52.2 | -16.3 % | +27.4 ms | -13.7 % | +25.1 ms | +0.7 % |  |
| XeSS · RT Full · FG DLSS-G · none | 37.8 | 75.6 | 76.7 | 78.6 | -47.8 % | +54.9 ms | -9.6 % | +13.9 ms | +0.3 % |  |
| XeSS · RT Full · FG DLSS-G · Reflex | 38.0 | 75.9 | 76.8 | 78.7 | -47.5 % | +55.0 ms | -9.1 % | +14.0 ms | +0.5 % |  |
| XeSS · RT Full · FG DLSS-G · brake 4 ms | 38.0 | 75.9 | 61.7 | 66.1 | -47.5 % | +39.9 ms | -9.1 % | -1.1 ms | +0.3 % |  |
| XeSS · RT Full · FG DLSS-G · Reflex + brake 4 ms | 38.0 | 75.9 | 61.7 | 65.6 | -47.5 % | +39.9 ms | -9.1 % | -1.1 ms | +0.5 % |  |
| FSR 3 · RT Full, opacity micromaps · FG DLSS-G · none | 37.1 | 74.3 | 78.3 | 80.0 | -48.8 % | +56.5 ms | -8.4 % | +13.3 ms | +0.3 % |  |
| FSR 3 · RT Off · FG DLSS-G preset C · none | 71.1 | 142.6 | 38.4 | 41.0 | -1.8 % | +16.6 ms | -1.8 % | +16.6 ms | -0.7 % |  |
| FSR 3 · RT Full · FG DLSS-G preset C · none | 41.8 | 83.9 | 68.2 | 70.0 | -42.3 % | +46.4 ms | -7.1 % | +10.4 ms | -6.9 % |  |
| FSR 3 · RT Off · FG off (XeSS-FG loaded) · XeLL | 72.3 | 72.0 | 21.9 | 23.1 | -0.1 % | +0.1 ms | -0.1 % | +0.1 ms | +0.4 % |  |
| FSR 3 · RT Off · FG XeSS-FG · XeLL | 63.0 | 126.1 | 32.9 | 37.4 | -13.0 % | +11.1 ms | -13.0 % | +11.1 ms | +6.1 % |  |
| FSR 3 · RT Full · FG off (XeSS-FG loaded) · XeLL | 45.8 | 45.9 | 59.1 | 60.6 | -36.7 % | +37.3 ms | +1.8 % | +1.3 ms | +2.0 % |  |
| FSR 3 · RT Full · FG XeSS-FG · XeLL | 39.0 | 77.9 | 56.4 | 60.8 | -46.1 % | +34.6 ms | -13.3 % | -1.4 ms | +6.3 % |  |
| FSR 3 · RT Full, radiance cache · FG off (XeSS-FG loaded) · XeLL | 46.1 | 46.1 | 58.5 | 60.2 | -36.3 % | +36.7 ms | +1.5 % | +1.4 ms | +2.0 % |  |
| FSR 3 · RT Full, radiance cache · FG XeSS-FG · XeLL | 39.3 | 78.6 | 56.0 | 60.0 | -45.7 % | +34.2 ms | -13.4 % | -1.1 ms | +6.2 % |  |
| DLSS · RT Off · FG off (XeSS-FG loaded) · XeLL | 69.4 | 69.2 | 23.8 | 25.9 | -4.1 % | +2.0 ms | -0.4 % | +0.3 ms | +0.3 % |  |
| DLSS · RT Off · FG XeSS-FG · XeLL | 58.0 | 115.9 | 38.0 | 41.8 | -19.9 % | +16.2 ms | -16.8 % | +14.5 ms | +5.6 % |  |
| DLSS · RT Full, RR on · FG off (XeSS-FG loaded) · XeLL | 35.8 | 35.9 | 76.1 | 77.7 | -50.6 % | +54.3 ms | -0.3 % | +1.5 ms | +1.7 % |  |
| DLSS · RT Full, RR on · FG XeSS-FG · XeLL | 31.3 | 62.5 | 72.6 | 74.8 | -56.8 % | +50.8 ms | -12.8 % | -2.0 ms | +5.0 % |  |
| DLSS · RT Full, RR off · FG off (XeSS-FG loaded) · XeLL | 43.3 | 43.3 | 63.5 | 65.0 | -40.2 % | +41.7 ms | -0.2 % | +3.2 ms | +1.9 % |  |
| DLSS · RT Full, RR off · FG XeSS-FG · XeLL | 37.0 | 74.0 | 59.9 | 62.9 | -48.9 % | +38.1 ms | -14.7 % | -0.4 ms | +5.7 % |  |
| AMD DLL · RT Off · FG off (XeSS-FG loaded) · XeLL | 71.9 | 71.8 | 23.2 | 25.2 | -0.7 % | +1.4 ms | +3.0 % | +0.1 ms | +2.3 % |  |
| AMD DLL · RT Off · FG XeSS-FG · XeLL | 59.6 | 119.2 | 36.8 | 40.2 | -17.7 % | +15.0 ms | -14.6 % | +13.7 ms | +6.2 % |  |
| AMD DLL · RT Full · FG off (XeSS-FG loaded) · XeLL | 43.8 | 43.8 | 62.4 | 64.0 | -39.5 % | +40.6 ms | +1.2 % | +1.8 ms | +2.1 % |  |
| AMD DLL · RT Full · FG XeSS-FG · XeLL | 37.5 | 75.1 | 59.0 | 61.8 | -48.2 % | +37.2 ms | -13.4 % | -1.6 ms | +5.9 % |  |
| XeSS · RT Off · FG off (XeSS-FG loaded) · XeLL | 71.7 | 71.6 | 24.7 | 26.9 | -1.0 % | +2.9 ms | +2.1 % | +0.6 ms | +1.6 % |  |
| XeSS · RT Off · FG XeSS-FG · XeLL | 57.3 | 114.6 | 38.6 | 42.2 | -20.9 % | +16.8 ms | -18.4 % | +14.5 ms | +7.1 % |  |
| XeSS · RT Full · FG off (XeSS-FG loaded) · XeLL | 42.8 | 42.9 | 64.4 | 66.0 | -40.9 % | +42.6 ms | +2.4 % | +1.6 ms | +1.9 % |  |
| XeSS · RT Full · FG XeSS-FG · XeLL | 36.7 | 73.3 | 60.2 | 62.5 | -49.3 % | +38.4 ms | -12.2 % | -2.6 ms | +5.8 % |  |
| FSR 3 · RT Full, opacity micromaps · FG off (XeSS-FG loaded) · XeLL | 41.9 | 41.8 | 65.0 | 66.7 | -42.1 % | +43.2 ms | +3.5 % | +0.0 ms | +1.9 % |  |
| FSR 3 · RT Full, opacity micromaps · FG XeSS-FG · XeLL | 35.9 | 71.6 | 61.8 | 64.3 | -50.4 % | +40.0 ms | -11.4 % | -3.2 ms | +5.3 % |  |

### Raytracing modes (`rt-modes`)

Raytracing Off, Shadows, Light and Full for FSR 3 and DLSS (Ray Reconstruction on and
off), frame generation off and FSR's.

Run `2026-10-21-0951`. Its machine and options are under [the runs](#the-runs).

Run it with `python tools/benchmark_matrix.py run --group rt-modes`.

| Scenario | fps | Shown fps | Latency median ms | Latency p90 ms | fps vs baseline | Latency vs baseline | fps vs FG off, no model | Latency vs FG off, no model | fps vs previous run | Notes |
|---|---|---|---|---|---|---|---|---|---|---|
| FSR 3 · RT Off · FG off · none | 72.2 | 72.0 | 21.8 | 23.3 | +0.0 % | +0.0 ms | – | – | – | baseline |
| FSR 3 · RT Off · FG FSR · none | 66.8 | 133.8 | 29.5 | 32.5 | -7.5 % | +7.7 ms | -7.5 % | +7.7 ms | – |  |
| FSR 3 · RT Shadows · FG off · none | 58.2 | 58.4 | 28.5 | 30.1 | -19.4 % | +6.7 ms | – | – | – |  |
| FSR 3 · RT Shadows · FG FSR · none | 54.5 | 109.0 | 37.8 | 40.5 | -24.5 % | +16.0 ms | -6.4 % | +9.3 ms | – |  |
| FSR 3 · RT Light · FG off · none | 52.3 | 52.5 | 47.7 | 49.9 | -27.6 % | +25.9 ms | – | – | – |  |
| FSR 3 · RT Light · FG FSR · none | 44.7 | 89.4 | 51.4 | 53.1 | -38.1 % | +29.6 ms | -14.5 % | +3.7 ms | – |  |
| FSR 3 · RT Full · FG off · none | 44.9 | 44.9 | 57.7 | 59.4 | -37.8 % | +35.9 ms | – | – | – |  |
| FSR 3 · RT Full · FG FSR · none | 39.5 | 79.0 | 60.2 | 62.0 | -45.3 % | +38.4 ms | -12.0 % | +2.5 ms | – |  |
| DLSS · RT Off · FG off · none | 67.9 | 67.8 | 23.6 | 25.9 | -6.0 % | +1.8 ms | – | – | – |  |
| DLSS · RT Off · FG FSR · none | 61.7 | 123.4 | 33.6 | 35.9 | -14.5 % | +11.8 ms | -9.1 % | +10.0 ms | – |  |
| DLSS · RT Shadows, RR on · FG off · none | 41.0 | 40.9 | 64.2 | 65.6 | -43.2 % | +42.4 ms | – | – | – |  |
| DLSS · RT Shadows, RR on · FG FSR · none | 36.2 | 72.4 | 67.3 | 68.8 | -49.9 % | +45.5 ms | -11.7 % | +3.1 ms | – |  |
| DLSS · RT Shadows, RR off · FG off · none | 56.7 | 57.0 | 30.2 | 32.3 | -21.5 % | +8.4 ms | – | – | – |  |
| DLSS · RT Shadows, RR off · FG FSR · none | 50.9 | 101.6 | 41.5 | 43.8 | -29.5 % | +19.7 ms | -10.2 % | +11.3 ms | – |  |
| DLSS · RT Light, RR on · FG off · none | 38.2 | 37.9 | 69.4 | 71.0 | -47.1 % | +47.6 ms | – | – | – |  |
| DLSS · RT Light, RR on · FG FSR · none | 34.0 | 67.9 | 72.5 | 74.0 | -52.9 % | +50.7 ms | -11.0 % | +3.1 ms | – |  |
| DLSS · RT Light, RR off · FG off · none | 49.0 | 49.1 | 51.8 | 53.5 | -32.1 % | +30.0 ms | – | – | – |  |
| DLSS · RT Light, RR off · FG FSR · none | 42.6 | 85.0 | 54.7 | 56.2 | -41.0 % | +32.9 ms | -13.1 % | +2.9 ms | – |  |
| DLSS · RT Full, RR on · FG off · none | 35.8 | 35.9 | 75.3 | 76.6 | -50.4 % | +53.5 ms | – | – | – |  |
| DLSS · RT Full, RR on · FG FSR · none | 32.1 | 64.0 | 77.5 | 79.1 | -55.5 % | +55.7 ms | -10.3 % | +2.2 ms | – |  |
| DLSS · RT Full, RR off · FG off · none | 43.3 | 43.4 | 60.3 | 61.8 | -40.0 % | +38.5 ms | – | – | – |  |
| DLSS · RT Full, RR off · FG FSR · none | 38.3 | 76.6 | 62.7 | 64.3 | -47.0 % | +40.9 ms | -11.5 % | +2.4 ms | – |  |

### Frame generation steps (`frame-steps`)

DLSS-G at 2x, 3x, 4x, 5x, 6x and Dynamic with the package's MaxGeneratedFrames raised to
5, set live through the mod's frameGenerationMultiplier, for FSR 3 at raytracing Off and
Full; FSR's frame generation and no frame generation as the references. The
shown/rendered column is what the GPU and the driver delivered.

Run `2026-10-21-0942`. Its machine and options are under [the runs](#the-runs).

Run it with `python tools/benchmark_matrix.py run --group frame-steps`.

| Scenario | fps | Shown fps | Shown per rendered | Delivered | Latency median ms | Latency p90 ms | fps vs baseline | Latency vs baseline | fps vs FG off, no model | Latency vs FG off, no model | fps vs previous run | Notes |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| FSR 3 · RT Off · FG off · none | 73.5 | 73.4 | – | – | 21.7 | 22.9 | +0.0 % | +0.0 ms | – | – | – | baseline |
| FSR 3 · RT Off · FG FSR 2x · none | 66.3 | 132.6 | 2.00 | yes | 29.1 | 32.3 | -9.8 % | +7.4 ms | -9.8 % | +7.4 ms | – |  |
| FSR 3 · RT Full · FG off · none | 45.8 | 45.9 | – | – | 56.9 | 58.2 | -37.7 % | +35.2 ms | – | – | – |  |
| FSR 3 · RT Full · FG FSR 2x · none | 40.1 | 80.3 | 2.00 | yes | 59.3 | 61.0 | -45.4 % | +37.6 ms | -12.4 % | +2.4 ms | – |  |
| FSR 3 · RT Off · FG DLSS-G 2x · none | 68.3 | 136.9 | 2.00 | yes | 42.0 | 44.7 | -7.1 % | +20.3 ms | -7.1 % | +20.3 ms | – |  |
| FSR 3 · RT Off · FG DLSS-G 3x · none | 61.6 | 184.6 | 3.00 | yes | 51.1 | 52.6 | -16.2 % | +29.4 ms | -16.2 % | +29.4 ms | – |  |
| FSR 3 · RT Off · FG DLSS-G 4x · none | 56.2 | 224.8 | 4.00 | yes | 58.9 | 60.3 | -23.5 % | +37.2 ms | -23.5 % | +37.2 ms | – |  |
| FSR 3 · RT Off · FG DLSS-G 5x · none | 51.6 | 258.3 | 5.00 | yes | 65.1 | 67.1 | -29.8 % | +43.4 ms | -29.8 % | +43.4 ms | – |  |
| FSR 3 · RT Off · FG DLSS-G 6x · none | 47.3 | 284.5 | 6.00 | yes | 68.1 | 72.5 | -35.6 % | +46.4 ms | -35.6 % | +46.4 ms | – |  |
| FSR 3 · RT Off · FG DLSS-G Dynamic · none | 68.0 | 136.3 | 2.00 | n/a | 42.2 | 45.0 | -7.5 % | +20.5 ms | -7.5 % | +20.5 ms | – |  |
| FSR 3 · RT Full · FG DLSS-G 2x · none | 40.4 | 80.8 | 2.00 | yes | 70.9 | 72.6 | -45.0 % | +49.2 ms | -11.8 % | +14.0 ms | – |  |
| FSR 3 · RT Full · FG DLSS-G 3x · none | 37.8 | 113.4 | 3.00 | yes | 78.3 | 80.0 | -48.6 % | +56.6 ms | -17.5 % | +21.4 ms | – |  |
| FSR 3 · RT Full · FG DLSS-G 4x · none | 35.9 | 143.7 | 4.00 | yes | 83.6 | 85.5 | -51.2 % | +61.9 ms | -21.6 % | +26.7 ms | – |  |
| FSR 3 · RT Full · FG DLSS-G 5x · none | 34.2 | 171.3 | 5.00 | yes | 88.2 | 90.8 | -53.5 % | +66.5 ms | -25.3 % | +31.3 ms | – |  |
| FSR 3 · RT Full · FG DLSS-G 6x · none | 32.7 | 195.7 | 6.00 | yes | 93.8 | 96.0 | -55.5 % | +72.1 ms | -28.6 % | +36.9 ms | – |  |
| FSR 3 · RT Full · FG DLSS-G Dynamic · none | 37.7 | 114.4 | 3.04 | n/a | 78.5 | 80.5 | -48.7 % | +56.8 ms | -17.7 % | +21.6 ms | – |  |

### Latency brake targets (`latency-brake`)

The latency brake at 4 ms (the default), 2 ms and 1 ms for FSR 3 at raytracing Off and
Full, with no frame generation, FSR's and DLSS-G; no brake as the reference.

Run `2026-10-21-1001`. Its machine and options are under [the runs](#the-runs).

Run it with `python tools/benchmark_matrix.py run --group latency-brake`.

| Scenario | fps | Shown fps | Latency median ms | Latency p90 ms | fps vs baseline | Latency vs baseline | fps vs FG off, no model | Latency vs FG off, no model | fps vs previous run | Notes |
|---|---|---|---|---|---|---|---|---|---|---|
| FSR 3 · RT Off · FG off · none | 71.8 | 71.8 | 21.9 | 23.0 | +0.0 % | +0.0 ms | – | – | – | baseline |
| FSR 3 · RT Off · FG off · brake 4 ms | 71.3 | 71.1 | 21.9 | 23.1 | -0.7 % | +0.0 ms | -0.7 % | +0.0 ms | – |  |
| FSR 3 · RT Off · FG off · brake 2 ms | 71.7 | 71.4 | 21.9 | 23.4 | -0.1 % | +0.0 ms | -0.1 % | +0.0 ms | – |  |
| FSR 3 · RT Off · FG off · brake 1 ms | 71.9 | 71.7 | 22.0 | 23.3 | +0.1 % | +0.1 ms | +0.1 % | +0.1 ms | – |  |
| FSR 3 · RT Off · FG FSR · none | 66.5 | 132.9 | 29.4 | 32.2 | -7.4 % | +7.5 ms | -7.4 % | +7.5 ms | – |  |
| FSR 3 · RT Off · FG FSR · brake 4 ms | 65.1 | 130.6 | 27.9 | 31.0 | -9.3 % | +6.0 ms | -9.3 % | +6.0 ms | – |  |
| FSR 3 · RT Off · FG FSR · brake 2 ms | 64.2 | 128.6 | 26.4 | 29.1 | -10.6 % | +4.5 ms | -10.6 % | +4.5 ms | – |  |
| FSR 3 · RT Off · FG FSR · brake 1 ms | 61.9 | 123.8 | 25.5 | 27.9 | -13.8 % | +3.6 ms | -13.8 % | +3.6 ms | – |  |
| FSR 3 · RT Full · FG off · none | 45.0 | 44.9 | 57.6 | 59.2 | -37.3 % | +35.7 ms | – | – | – |  |
| FSR 3 · RT Full · FG off · brake 4 ms | 44.9 | 44.9 | 49.0 | 54.5 | -37.5 % | +27.1 ms | -0.2 % | -8.6 ms | – |  |
| FSR 3 · RT Full · FG off · brake 2 ms | 44.4 | 44.5 | 43.0 | 51.8 | -38.2 % | +21.1 ms | -1.3 % | -14.6 ms | – |  |
| FSR 3 · RT Full · FG off · brake 1 ms | 43.8 | 44.0 | 39.8 | 47.6 | -39.0 % | +17.9 ms | -2.7 % | -17.8 ms | – |  |
| FSR 3 · RT Full · FG FSR · none | 39.5 | 79.0 | 59.9 | 61.7 | -45.0 % | +38.0 ms | -12.2 % | +2.3 ms | – |  |
| FSR 3 · RT Full · FG FSR · brake 4 ms | 39.4 | 78.8 | 46.8 | 51.4 | -45.1 % | +24.9 ms | -12.4 % | -10.8 ms | – |  |
| FSR 3 · RT Full · FG FSR · brake 2 ms | 39.2 | 78.2 | 41.6 | 48.1 | -45.4 % | +19.7 ms | -12.9 % | -16.0 ms | – |  |
| FSR 3 · RT Full · FG FSR · brake 1 ms | 38.9 | 77.9 | 39.5 | 44.8 | -45.8 % | +17.6 ms | -13.6 % | -18.1 ms | – |  |
| FSR 3 · RT Off · FG DLSS-G · none | 66.2 | 132.7 | 47.3 | 49.5 | -7.8 % | +25.4 ms | -7.8 % | +25.4 ms | – |  |
| FSR 3 · RT Off · FG DLSS-G · brake 4 ms | 65.6 | 131.4 | 45.3 | 48.4 | -8.6 % | +23.4 ms | -8.6 % | +23.4 ms | – |  |
| FSR 3 · RT Off · FG DLSS-G · brake 2 ms | 66.1 | 132.2 | 42.3 | 45.9 | -7.9 % | +20.4 ms | -7.9 % | +20.4 ms | – |  |
| FSR 3 · RT Off · FG DLSS-G · brake 1 ms | 65.2 | 130.5 | 38.5 | 44.6 | -9.2 % | +16.6 ms | -9.2 % | +16.6 ms | – |  |
| FSR 3 · RT Full · FG DLSS-G · none | 40.2 | 80.6 | 72.3 | 73.9 | -44.0 % | +50.4 ms | -10.7 % | +14.7 ms | – |  |
| FSR 3 · RT Full · FG DLSS-G · brake 4 ms | 40.4 | 81.0 | 58.0 | 66.0 | -43.7 % | +36.1 ms | -10.2 % | +0.4 ms | – |  |
| FSR 3 · RT Full · FG DLSS-G · brake 2 ms | 40.3 | 80.9 | 55.3 | 62.3 | -43.9 % | +33.4 ms | -10.4 % | -2.3 ms | – |  |
| FSR 3 · RT Full · FG DLSS-G · brake 1 ms | 40.2 | 80.2 | 52.0 | 59.6 | -44.0 % | +30.1 ms | -10.7 % | -5.6 ms | – |  |

### Grass and terrain (`grass`)

The launch pad and a field of Parallax's grass 5 km away (-redefinitionProbeGrass), FSR
3 at every raytracing mode, then at Full the radiance cache, the cut-out tests
(rtCutoutTest 1 and 2, the ceiling of what the alpha tests cost) and, last, the opacity
micromaps; frame generation off and FSR's.

Run `2026-10-21-0927`. Its machine and options are under [the runs](#the-runs).

Run it with `python tools/benchmark_matrix.py run --group grass`.

Scenes: pad (the launch pad), grass (a field of Parallax's grass 5 km from the pad).

| Scenario | fps | Shown fps | Latency median ms | Latency p90 ms | fps vs baseline | Latency vs baseline | fps vs FG off, no model | Latency vs FG off, no model | fps vs previous run | Notes |
|---|---|---|---|---|---|---|---|---|---|---|
| FSR 3 · RT Off · FG off · none | 73.0 | 73.0 | 21.8 | 22.9 | +0.0 % | +0.0 ms | – | – | – | baseline |
| FSR 3 · RT Off · FG FSR · none | 67.1 | 134.2 | 28.7 | 31.8 | -8.1 % | +6.9 ms | -8.1 % | +6.9 ms | – |  |
| FSR 3 · RT Shadows · FG off · none | 57.7 | 57.9 | 28.5 | 29.9 | -21.0 % | +6.7 ms | – | – | – |  |
| FSR 3 · RT Shadows · FG FSR · none | 54.6 | 109.2 | 37.1 | 39.8 | -25.2 % | +15.3 ms | -5.4 % | +8.6 ms | – |  |
| FSR 3 · RT Light · FG off · none | 52.3 | 52.4 | 47.8 | 49.7 | -28.4 % | +26.0 ms | – | – | – |  |
| FSR 3 · RT Light · FG FSR · none | 44.9 | 89.8 | 51.1 | 52.8 | -38.5 % | +29.3 ms | -14.1 % | +3.3 ms | – |  |
| FSR 3 · RT Full · FG off · none | 45.0 | 44.8 | 57.8 | 59.1 | -38.4 % | +36.0 ms | – | – | – |  |
| FSR 3 · RT Full · FG FSR · none | 39.6 | 79.0 | 60.7 | 61.8 | -45.8 % | +38.9 ms | -12.0 % | +2.9 ms | – |  |
| FSR 3 · RT Full, radiance cache · FG off · none | 45.3 | 45.5 | 57.3 | 58.6 | -37.9 % | +35.5 ms | – | – | – |  |
| FSR 3 · RT Full, radiance cache · FG FSR · none | 39.9 | 79.8 | 59.5 | 61.0 | -45.3 % | +37.7 ms | -11.9 % | +2.2 ms | – |  |
| FSR 3 · RT Full, cut-out test 1 · FG off · none | 46.1 | 46.0 | 56.2 | 57.6 | -36.8 % | +34.4 ms | – | – | – |  |
| FSR 3 · RT Full, cut-out test 1 · FG FSR · none | 40.5 | 81.0 | 58.4 | 60.1 | -44.5 % | +36.6 ms | -12.1 % | +2.2 ms | – |  |
| FSR 3 · RT Full, cut-out test 2 · FG off · none | 47.4 | 47.7 | 54.1 | 55.8 | -35.1 % | +32.3 ms | – | – | – |  |
| FSR 3 · RT Full, cut-out test 2 · FG FSR · none | 41.5 | 83.0 | 56.6 | 58.3 | -43.2 % | +34.8 ms | -12.4 % | +2.5 ms | – |  |
| FSR 3 · RT Full, opacity micromaps · FG off · none | 42.3 | 42.3 | 61.9 | 63.5 | -42.1 % | +40.1 ms | – | – | – |  |
| FSR 3 · RT Full, opacity micromaps · FG FSR · none | 37.1 | 74.1 | 64.8 | 66.9 | -49.2 % | +43.0 ms | -12.3 % | +2.9 ms | – |  |
| grass · FSR 3 · RT Off · FG off · none | 80.7 | 80.4 | 34.0 | 35.0 | +0.0 % | +0.0 ms | – | – | – | baseline |
| grass · FSR 3 · RT Off · FG FSR · none | 64.7 | 129.4 | 35.5 | 36.5 | -19.8 % | +1.5 ms | -19.8 % | +1.5 ms | – |  |
| grass · FSR 3 · RT Shadows · FG off · none | 59.7 | 59.8 | 44.6 | 45.6 | -26.0 % | +10.6 ms | – | – | – |  |
| grass · FSR 3 · RT Shadows · FG FSR · none | 50.5 | 100.8 | 47.0 | 48.6 | -37.4 % | +13.0 ms | -15.4 % | +2.4 ms | – |  |
| grass · FSR 3 · RT Light · FG off · none | 46.1 | 45.9 | 59.4 | 60.5 | -42.9 % | +25.4 ms | – | – | – |  |
| grass · FSR 3 · RT Light · FG FSR · none | 40.3 | 80.8 | 62.9 | 64.1 | -50.1 % | +28.9 ms | -12.6 % | +3.5 ms | – |  |
| grass · FSR 3 · RT Full · FG off · none | 39.5 | 39.6 | 70.1 | 71.2 | -51.1 % | +36.1 ms | – | – | – |  |
| grass · FSR 3 · RT Full · FG FSR · none | 35.3 | 70.5 | 73.5 | 74.6 | -56.3 % | +39.5 ms | -10.6 % | +3.4 ms | – |  |
| grass · FSR 3 · RT Full, radiance cache · FG off · none | 39.7 | 39.8 | 69.6 | 70.8 | -50.8 % | +35.6 ms | – | – | – |  |
| grass · FSR 3 · RT Full, radiance cache · FG FSR · none | 35.6 | 71.0 | 72.7 | 73.8 | -55.9 % | +38.7 ms | -10.3 % | +3.1 ms | – |  |
| grass · FSR 3 · RT Full, cut-out test 1 · FG off · none | 40.5 | 40.7 | 68.2 | 69.4 | -49.8 % | +34.2 ms | – | – | – |  |
| grass · FSR 3 · RT Full, cut-out test 1 · FG FSR · none | 36.1 | 72.0 | 71.4 | 72.6 | -55.3 % | +37.4 ms | -10.9 % | +3.2 ms | – |  |
| grass · FSR 3 · RT Full, cut-out test 2 · FG off · none | 41.4 | 41.6 | 66.6 | 67.6 | -48.7 % | +32.6 ms | – | – | – |  |
| grass · FSR 3 · RT Full, cut-out test 2 · FG FSR · none | 36.9 | 73.7 | 69.7 | 70.9 | -54.3 % | +35.7 ms | -10.9 % | +3.1 ms | – |  |
| grass · FSR 3 · RT Full, opacity micromaps · FG off · none | 37.2 | 36.9 | 74.7 | 76.0 | -53.9 % | +40.7 ms | – | – | – |  |
| grass · FSR 3 · RT Full, opacity micromaps · FG FSR · none | 33.4 | 66.6 | 78.2 | 79.6 | -58.6 % | +44.2 ms | -10.2 % | +3.5 ms | – |  |

### cutout-oneoff (`cutout-oneoff`)

A one-off run from before the groups, with a matrix of its own: FSR 3 at raytracing Full
on the launch pad, the cut-out tests (rtCutoutTest 1 and 2) and the opacity micromaps in
turn, plain and micromaps again to see the run's own spread.

Run `2026-10-21-0834`. Its machine and options are under [the runs](#the-runs).

| Scenario | fps | Shown fps | Latency median ms | Latency p90 ms | fps vs baseline | Latency vs baseline | fps vs FG off, no model | Latency vs FG off, no model | fps vs previous run | Notes |
|---|---|---|---|---|---|---|---|---|---|---|
| FSR 3 · RT Full · FG off · plain | 45.2 | 45.2 | 51.3 | 55.2 | – | – | – | – | – |  |
| FSR 3 · RT Full · FG off · cutout1 | 47.1 | 47.1 | 54.4 | 56.0 | – | – | – | – | – |  |
| FSR 3 · RT Full · FG off · cutout2 | 48.2 | 48.4 | 52.6 | 54.4 | – | – | – | – | – |  |
| FSR 3 · RT Full · FG off · omm | 43.1 | 42.9 | 60.6 | 62.1 | – | – | – | – | – |  |
| FSR 3 · RT Full · FG off · plain-again | 44.7 | 44.9 | 57.9 | 59.8 | – | – | – | – | – |  |
| FSR 3 · RT Full · FG off · omm-again | 42.3 | 42.4 | 61.5 | 63.2 | – | – | – | – | – |  |
| FSR 3 · RT Full · FG off · plain-last | 44.4 | 44.6 | 58.0 | 59.6 | – | – | – | – | – |  |
| FSR 3 · RT Full · FG FSR · plain | 44.3 | 44.5 | 57.3 | 59.2 | – | – | – | – | – | frame generation asked for, presented/rendered 1.00 |
| FSR 3 · RT Full · FG FSR · cutout1 | 45.4 | 45.4 | 54.9 | 56.9 | – | – | – | – | – | frame generation asked for, presented/rendered 1.00 |
| FSR 3 · RT Full · FG FSR · cutout2 | 46.4 | 46.5 | 55.5 | 57.1 | – | – | – | – | – | frame generation asked for, presented/rendered 1.00 |
| FSR 3 · RT Full · FG FSR · omm | 42.3 | 42.3 | 61.5 | 63.2 | – | – | – | – | – | frame generation asked for, presented/rendered 1.00 |
| FSR 3 · RT Full · FG FSR · plain-again | 44.5 | 44.9 | 58.1 | 59.8 | – | – | – | – | – | frame generation asked for, presented/rendered 1.00 |
| FSR 3 · RT Full · FG FSR · omm-again | 42.2 | 42.2 | 61.8 | 63.4 | – | – | – | – | – | frame generation asked for, presented/rendered 1.00 |
| FSR 3 · RT Full · FG FSR · plain-last | 44.5 | 44.9 | 58.3 | 59.8 | – | – | – | – | – | frame generation asked for, presented/rendered 1.00 |
<!-- benchmark-table:end -->

## The runs

Each run names its date, the commit, the build in the install, the machine and the
profile. Its collapsed table says what ran in each phase. The JSON file next to this page
holds the full record of every phase: the proxy's whole configuration as it logged it, with
the brake's parts and the DLSS-G frame count as keys of their own, the mod's settings in
effect including the frame generation multiplier, the DLSS-G package's ini as the start ran
with it (preset, `MaxGeneratedFrames`), the proxy's ini at the start, the frame generation
lines of the phase, the upscaler's line, and each measured value.

<!-- benchmark-runs:start -->
### Run 2026-10-21-1001

| What | Value |
|---|---|
| Date | 2026-10-21T10:01:53 to 2026-10-21T10:12:02 |
| Commit | `fa8b00a` on `worktree-agent-a3c8b23ed6951386f` |
| Build in the install | `dxgi.dll` ec66cbc3a2508f0c (2026-10-21T09:05:20), `GameData/ReDefinition/Plugins/ReDefinition.dll` f61fcfbde9484e82 (2026-10-21T09:16:32) |
| Machine | NVIDIA RTX A4500, driver 597.16, AMD Ryzen 5 5600X 6-Core Processor, 31.9 GB, Microsoft Windows 11 Pro 26300, screen 2560x1440 rendered at 1707x960, 120 Hz |
| Profile | `medium`: [LOG 10:02:54.083] [ReDefinition] Probe: profile medium applied. |
| Groups | `latency-brake` |
| Matrix | `tools/benchmark_matrix.json`, 2 starts, 2 s settling per phase, 8 s after a change of the rendering |
| Full record | [benchmarks/2026-10-21-1001.json](benchmarks/2026-10-21-1001.json) |

<details><summary>What ran in each phase</summary>

| Start | Scenario | Upscaler that ran | Frame generation that ran | presented/rendered | Lowered by | Reports | fps range | Notes |
|---|---|---|---|---|---|---|---|---|
| latency-brake-fsr | FSR 3 · RT Off · FG off · none | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 9 | 70.5–72.3 |  |
| latency-brake-fsr | FSR 3 · RT Off · FG off · brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | the brake | 9 | 67.5–72.3 |  |
| latency-brake-fsr | FSR 3 · RT Off · FG off · brake 2 ms | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | the brake | 9 | 70.5–72.5 |  |
| latency-brake-fsr | FSR 3 · RT Off · FG off · brake 1 ms | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | the brake | 9 | 70.8–72.6 |  |
| latency-brake-fsr | FSR 3 · RT Off · FG FSR · none | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 8 | 66.2–66.7 |  |
| latency-brake-fsr | FSR 3 · RT Off · FG FSR · brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | the brake | 8 | 62.6–66.3 |  |
| latency-brake-fsr | FSR 3 · RT Off · FG FSR · brake 2 ms | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | the brake | 8 | 63.2–65.3 |  |
| latency-brake-fsr | FSR 3 · RT Off · FG FSR · brake 1 ms | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | the brake | 8 | 60.0–63.0 |  |
| latency-brake-fsr | FSR 3 · RT Full · FG off · none | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 14 | 42.9–45.4 |  |
| latency-brake-fsr | FSR 3 · RT Full · FG off · brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | the brake | 8 | 44.5–45.3 |  |
| latency-brake-fsr | FSR 3 · RT Full · FG off · brake 2 ms | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | the brake | 8 | 43.8–44.9 |  |
| latency-brake-fsr | FSR 3 · RT Full · FG off · brake 1 ms | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | the brake | 8 | 41.8–45.3 |  |
| latency-brake-fsr | FSR 3 · RT Full · FG FSR · none | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 8 | 39.4–39.6 |  |
| latency-brake-fsr | FSR 3 · RT Full · FG FSR · brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | the brake | 8 | 38.9–39.7 |  |
| latency-brake-fsr | FSR 3 · RT Full · FG FSR · brake 2 ms | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | the brake | 8 | 38.9–39.5 |  |
| latency-brake-fsr | FSR 3 · RT Full · FG FSR · brake 1 ms | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | the brake | 8 | 38.2–39.3 |  |
| latency-brake-dlssg | FSR 3 · RT Off · FG DLSS-G · none | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 9 | 65.8–66.6 |  |
| latency-brake-dlssg | FSR 3 · RT Off · FG DLSS-G · brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex and the brake | 8 | 64.0–66.6 |  |
| latency-brake-dlssg | FSR 3 · RT Off · FG DLSS-G · brake 2 ms | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex and the brake | 8 | 65.5–66.8 |  |
| latency-brake-dlssg | FSR 3 · RT Off · FG DLSS-G · brake 1 ms | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex and the brake | 8 | 63.9–66.7 |  |
| latency-brake-dlssg | FSR 3 · RT Full · FG DLSS-G · none | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 14 | 38.2–40.5 |  |
| latency-brake-dlssg | FSR 3 · RT Full · FG DLSS-G · brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex and the brake | 8 | 40.2–40.5 |  |
| latency-brake-dlssg | FSR 3 · RT Full · FG DLSS-G · brake 2 ms | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex and the brake | 8 | 39.9–40.8 |  |
| latency-brake-dlssg | FSR 3 · RT Full · FG DLSS-G · brake 1 ms | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex and the brake | 8 | 39.5–40.8 |  |

The proxy's configuration in each start, as the proxy logged it at load:

```
latency-brake-fsr: enabled=1 allowTearing=1 measureOnly=0 frameGeneration=1 frameGenerationTechnique=fsr rrPath=auto hudLessColour=1 fgDepthInverted=1 fgNegateMotionScale=0 fgFlipInputs=1 fgFlipHudLess=0 fgCameraBasis=1 fgVSync=0 fgHalfRefreshLimit=0 fgAsyncWorkloads=0 fgObjectSeparationMetres=0.000000 dlssgMode=on dlssgDynamicTargetFps=0 dlssgUiRecomposition=0 dlssgUiAlpha=binary dlssgUiThreshold=2 pacing=0.100000/0.100000 fgMinimumFps=1000 lowLatency=0 latencyBrake=0/4.000000/1/dlssg antiLag2=0 xell=1 xellLowLatencyBoost=0 vendorDiagnostics=0 streamlineImgui=0 fgShowOnlyGenerated=0 xessFgUiComposition=0 xessFgUiMode=auto xessFgFrames=1 xessFgSceneChangeThreshold=0.700000 xessFgTagInterpolatedFrames=0 xessFgPresentFailedInterpolation=0 xessFgOwnWaitable=auto xessFgGraceFrames=3 xessFgBadConstants=skip xessFgAllowOld=0 xessFgResetOnChange=0 xessFgResetDebounce=0 xellLimitLive=1 rtOwnQueue=0 rtCutoutTest=0 rtOpacityMicromaps=0 rtOmmMaxSubdivision=6 rtSunUnfiltered=0 rtSunAtrousSteps=0 rtSunHistory=4 rtFoliageTransmission=0.000000 rtRadianceCache=0 rtRadianceCacheEntries=20 rtRadianceCacheCellPixels=8.000000 rtRadianceCacheMinCell=0.125000 rtRadianceCacheSamples=256 rtRadianceCacheMaxAge=50 rtRadianceCacheDebug=0 rtBlasBatch=1 rtBlasPool=1 rtBlasBudget=0 rtBlasCompaction=2 rtBlasRefineBudget=65536 rtTlasFastTrace=0 rtMegaGeometry=0 reportSeconds=1 logInputsSeconds=0 dlssDirectory=(none) amdUpscalerDirectory=(none) xessDirectory=(none) streamlineDirectory=(none) xessFgDirectory=(none)
latency-brake-dlssg: enabled=1 allowTearing=1 measureOnly=0 frameGeneration=1 frameGenerationTechnique=dlss rrPath=auto hudLessColour=1 fgDepthInverted=1 fgNegateMotionScale=0 fgFlipInputs=1 fgFlipHudLess=0 fgCameraBasis=1 fgVSync=0 fgHalfRefreshLimit=0 fgAsyncWorkloads=0 fgObjectSeparationMetres=0.000000 dlssgMode=on dlssgDynamicTargetFps=0 dlssgUiRecomposition=0 dlssgUiAlpha=binary dlssgUiThreshold=2 pacing=0.100000/0.100000 fgMinimumFps=0 lowLatency=0 latencyBrake=0/4.000000/1/dlssg antiLag2=0 xell=1 xellLowLatencyBoost=0 vendorDiagnostics=0 streamlineImgui=0 fgShowOnlyGenerated=0 xessFgUiComposition=0 xessFgUiMode=auto xessFgFrames=1 xessFgSceneChangeThreshold=0.700000 xessFgTagInterpolatedFrames=0 xessFgPresentFailedInterpolation=0 xessFgOwnWaitable=auto xessFgGraceFrames=3 xessFgBadConstants=skip xessFgAllowOld=0 xessFgResetOnChange=0 xessFgResetDebounce=0 xellLimitLive=1 rtOwnQueue=0 rtCutoutTest=0 rtOpacityMicromaps=0 rtOmmMaxSubdivision=6 rtSunUnfiltered=0 rtSunAtrousSteps=0 rtSunHistory=4 rtFoliageTransmission=0.000000 rtRadianceCache=0 rtRadianceCacheEntries=20 rtRadianceCacheCellPixels=8.000000 rtRadianceCacheMinCell=0.125000 rtRadianceCacheSamples=256 rtRadianceCacheMaxAge=50 rtRadianceCacheDebug=0 rtBlasBatch=1 rtBlasPool=1 rtBlasBudget=0 rtBlasCompaction=2 rtBlasRefineBudget=65536 rtTlasFastTrace=0 rtMegaGeometry=0 reportSeconds=1 logInputsSeconds=0 dlssDirectory=(none) amdUpscalerDirectory=(none) xessDirectory=(none) streamlineDirectory=(none) xessFgDirectory=(none)
```

The DLSS-G package's ini in each start that had it, with the values it was read with:

```
latency-brake-dlssg: [FrameGeneration] Engine=DLSS MaxGeneratedFrames=1 [DLSSG] Optimized=2 Preset=Auto [XeSSFG] FlowResolution=Middle UseHudless=1 [Logging] Level=1 FrameTiming=1 [Runtime] Mode=Bundled [SuperResolution] Mode=Off
```

The raytracing's frames that built 50 meshes or more, at the flight's start:

* latency-brake-fsr: 5 frames, 591 meshes, 254539 triangles; the slowest 0.64 ms of the GPU,
  5.12 ms of the CPU
* latency-brake-dlssg: 5 frames, 637 meshes, 294448 triangles; the slowest 0.76 ms of the GPU,
  6.13 ms of the CPU

</details>

### Run 2026-10-21-0951

| What | Value |
|---|---|
| Date | 2026-10-21T09:51:49 to 2026-10-21T10:01:17 |
| Commit | `c771a09` on `worktree-agent-a3c8b23ed6951386f` |
| Build in the install | `dxgi.dll` ec66cbc3a2508f0c (2026-10-21T09:05:20), `GameData/ReDefinition/Plugins/ReDefinition.dll` f61fcfbde9484e82 (2026-10-21T09:16:32) |
| Machine | NVIDIA RTX A4500, driver 597.16, AMD Ryzen 5 5600X 6-Core Processor, 31.9 GB, Microsoft Windows 11 Pro 26300, screen 2560x1440 rendered at 1707x960, 120 Hz |
| Profile | `medium`: [LOG 09:52:50.135] [ReDefinition] Probe: profile medium applied. |
| Groups | `rt-modes` |
| Matrix | `tools/benchmark_matrix.json`, 1 starts, 2 s settling per phase, 8 s after a change of the rendering |
| Full record | [benchmarks/2026-10-21-0951.json](benchmarks/2026-10-21-0951.json) |

<details><summary>What ran in each phase</summary>

| Start | Scenario | Upscaler that ran | Frame generation that ran | presented/rendered | Lowered by | Reports | fps range | Notes |
|---|---|---|---|---|---|---|---|---|
| rt-modes-fsr | FSR 3 · RT Off · FG off · none | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 9 | 68.6–73.3 |  |
| rt-modes-fsr | FSR 3 · RT Off · FG FSR · none | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 9 | 66.2–67.4 |  |
| rt-modes-fsr | FSR 3 · RT Shadows · FG off · none | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 15 | 54.0–59.5 |  |
| rt-modes-fsr | FSR 3 · RT Shadows · FG FSR · none | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 9 | 54.3–54.7 |  |
| rt-modes-fsr | FSR 3 · RT Light · FG off · none | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 15 | 49.7–52.7 |  |
| rt-modes-fsr | FSR 3 · RT Light · FG FSR · none | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 9 | 44.2–44.9 |  |
| rt-modes-fsr | FSR 3 · RT Full · FG off · none | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 14 | 42.5–45.6 |  |
| rt-modes-fsr | FSR 3 · RT Full · FG FSR · none | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 8 | 39.3–39.6 |  |
| rt-modes-fsr | DLSS · RT Off · FG off · none | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 14 | 53.9–71.3 |  |
| rt-modes-fsr | DLSS · RT Off · FG FSR · none | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 9 | 60.8–62.1 |  |
| rt-modes-fsr | DLSS · RT Shadows, RR on · FG off · none | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 14 | 38.9–41.3 |  |
| rt-modes-fsr | DLSS · RT Shadows, RR on · FG FSR · none | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 8 | 36.1–36.3 |  |
| rt-modes-fsr | DLSS · RT Shadows, RR off · FG off · none | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 14 | 53.9–57.7 |  |
| rt-modes-fsr | DLSS · RT Shadows, RR off · FG FSR · none | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 8 | 49.3–51.4 |  |
| rt-modes-fsr | DLSS · RT Light, RR on · FG off · none | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 14 | 36.4–38.5 |  |
| rt-modes-fsr | DLSS · RT Light, RR on · FG FSR · none | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 8 | 33.9–34.1 |  |
| rt-modes-fsr | DLSS · RT Light, RR off · FG off · none | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 14 | 45.7–49.5 |  |
| rt-modes-fsr | DLSS · RT Light, RR off · FG FSR · none | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 8 | 42.4–42.8 |  |
| rt-modes-fsr | DLSS · RT Full, RR on · FG off · none | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 14 | 34.7–35.9 |  |
| rt-modes-fsr | DLSS · RT Full, RR on · FG FSR · none | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 8 | 32.1–32.1 |  |
| rt-modes-fsr | DLSS · RT Full, RR off · FG off · none | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 14 | 40.8–43.7 |  |
| rt-modes-fsr | DLSS · RT Full, RR off · FG FSR · none | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 8 | 38.1–38.4 |  |

The proxy's configuration in each start, as the proxy logged it at load:

```
rt-modes-fsr: enabled=1 allowTearing=1 measureOnly=0 frameGeneration=1 frameGenerationTechnique=fsr rrPath=auto hudLessColour=1 fgDepthInverted=1 fgNegateMotionScale=0 fgFlipInputs=1 fgFlipHudLess=0 fgCameraBasis=1 fgVSync=0 fgHalfRefreshLimit=0 fgAsyncWorkloads=0 fgObjectSeparationMetres=0.000000 dlssgMode=on dlssgDynamicTargetFps=0 dlssgUiRecomposition=0 dlssgUiAlpha=binary dlssgUiThreshold=2 pacing=0.100000/0.100000 fgMinimumFps=1000 lowLatency=0 latencyBrake=0/4.000000/1/dlssg antiLag2=0 xell=1 xellLowLatencyBoost=0 vendorDiagnostics=0 streamlineImgui=0 fgShowOnlyGenerated=0 xessFgUiComposition=0 xessFgUiMode=auto xessFgFrames=1 xessFgSceneChangeThreshold=0.700000 xessFgTagInterpolatedFrames=0 xessFgPresentFailedInterpolation=0 xessFgOwnWaitable=auto xessFgGraceFrames=3 xessFgBadConstants=skip xessFgAllowOld=0 xessFgResetOnChange=0 xessFgResetDebounce=0 xellLimitLive=1 rtOwnQueue=0 rtCutoutTest=0 rtOpacityMicromaps=0 rtOmmMaxSubdivision=6 rtSunUnfiltered=0 rtSunAtrousSteps=0 rtSunHistory=4 rtFoliageTransmission=0.000000 rtRadianceCache=0 rtRadianceCacheEntries=20 rtRadianceCacheCellPixels=8.000000 rtRadianceCacheMinCell=0.125000 rtRadianceCacheSamples=256 rtRadianceCacheMaxAge=50 rtRadianceCacheDebug=0 rtBlasBatch=1 rtBlasPool=1 rtBlasBudget=0 rtBlasCompaction=2 rtBlasRefineBudget=65536 rtTlasFastTrace=0 rtMegaGeometry=0 reportSeconds=1 logInputsSeconds=0 dlssDirectory=(none) amdUpscalerDirectory=(none) xessDirectory=(none) streamlineDirectory=(none) xessFgDirectory=(none)
```

The raytracing's frames that built 50 meshes or more, at the flight's start:

* rt-modes-fsr: 10 frames, 1313 meshes, 518318 triangles; the slowest 0.61 ms of the GPU,
  7.04 ms of the CPU

</details>

### Run 2026-10-21-0942

| What | Value |
|---|---|
| Date | 2026-10-21T09:42:43 to 2026-10-21T09:51:13 |
| Commit | `03d705e` on `worktree-agent-a3c8b23ed6951386f` |
| Build in the install | `dxgi.dll` ec66cbc3a2508f0c (2026-10-21T09:05:20), `GameData/ReDefinition/Plugins/ReDefinition.dll` f61fcfbde9484e82 (2026-10-21T09:16:32) |
| Machine | NVIDIA RTX A4500, driver 597.16, AMD Ryzen 5 5600X 6-Core Processor, 31.9 GB, Microsoft Windows 11 Pro 26300, screen 2560x1440 rendered at 1707x960, 120 Hz |
| Profile | `medium`: [LOG 09:43:44.280] [ReDefinition] Probe: profile medium applied. |
| Groups | `frame-steps` |
| Matrix | `tools/benchmark_matrix.json`, 2 starts, 2 s settling per phase, 8 s after a change of the rendering |
| Full record | [benchmarks/2026-10-21-0942.json](benchmarks/2026-10-21-0942.json) |

<details><summary>What ran in each phase</summary>

| Start | Scenario | Upscaler that ran | Frame generation that ran | presented/rendered | Lowered by | Reports | fps range | Notes |
|---|---|---|---|---|---|---|---|---|
| frame-steps-fsr | FSR 3 · RT Off · FG off · none | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 7 | 73.1–73.9 |  |
| frame-steps-fsr | FSR 3 · RT Off · FG FSR 2x · none | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 7 | 62.6–67.3 |  |
| frame-steps-fsr | FSR 3 · RT Full · FG off · none | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 14 | 44.0–46.1 |  |
| frame-steps-fsr | FSR 3 · RT Full · FG FSR 2x · none | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 7 | 39.9–40.3 |  |
| frame-steps-dlssg | FSR 3 · RT Off · FG DLSS-G 2x · none | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 7 | 68.1–68.6 |  |
| frame-steps-dlssg | FSR 3 · RT Off · FG DLSS-G 3x · none | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 3.00 | Streamline's Reflex | 7 | 61.4–61.8 |  |
| frame-steps-dlssg | FSR 3 · RT Off · FG DLSS-G 4x · none | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 4.00 | Streamline's Reflex | 6 | 56.0–56.4 |  |
| frame-steps-dlssg | FSR 3 · RT Off · FG DLSS-G 5x · none | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 5.00 | Streamline's Reflex | 6 | 51.2–51.8 |  |
| frame-steps-dlssg | FSR 3 · RT Off · FG DLSS-G 6x · none | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 6.00 | Streamline's Reflex | 6 | 46.6–47.9 |  |
| frame-steps-dlssg | FSR 3 · RT Off · FG DLSS-G Dynamic · none | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 6 | 67.2–68.4 |  |
| frame-steps-dlssg | FSR 3 · RT Full · FG DLSS-G 2x · none | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 15 | 38.8–40.6 |  |
| frame-steps-dlssg | FSR 3 · RT Full · FG DLSS-G 3x · none | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 3.00 | Streamline's Reflex | 7 | 37.8–37.9 |  |
| frame-steps-dlssg | FSR 3 · RT Full · FG DLSS-G 4x · none | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 4.00 | Streamline's Reflex | 7 | 35.8–35.9 |  |
| frame-steps-dlssg | FSR 3 · RT Full · FG DLSS-G 5x · none | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 5.00 | Streamline's Reflex | 7 | 34.2–34.3 |  |
| frame-steps-dlssg | FSR 3 · RT Full · FG DLSS-G 6x · none | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 6.00 | Streamline's Reflex | 7 | 32.7–32.7 |  |
| frame-steps-dlssg | FSR 3 · RT Full · FG DLSS-G Dynamic · none | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 3.04 | Streamline's Reflex | 7 | 37.6–37.8 |  |

The proxy's configuration in each start, as the proxy logged it at load:

```
frame-steps-fsr: enabled=1 allowTearing=1 measureOnly=0 frameGeneration=1 frameGenerationTechnique=fsr rrPath=auto hudLessColour=1 fgDepthInverted=1 fgNegateMotionScale=0 fgFlipInputs=1 fgFlipHudLess=0 fgCameraBasis=1 fgVSync=0 fgHalfRefreshLimit=0 fgAsyncWorkloads=0 fgObjectSeparationMetres=0.000000 dlssgMode=on dlssgDynamicTargetFps=0 dlssgUiRecomposition=0 dlssgUiAlpha=binary dlssgUiThreshold=2 pacing=0.100000/0.100000 fgMinimumFps=1000 lowLatency=0 latencyBrake=0/4.000000/1/dlssg antiLag2=0 xell=1 xellLowLatencyBoost=0 vendorDiagnostics=0 streamlineImgui=0 fgShowOnlyGenerated=0 xessFgUiComposition=0 xessFgUiMode=auto xessFgFrames=1 xessFgSceneChangeThreshold=0.700000 xessFgTagInterpolatedFrames=0 xessFgPresentFailedInterpolation=0 xessFgOwnWaitable=auto xessFgGraceFrames=3 xessFgBadConstants=skip xessFgAllowOld=0 xessFgResetOnChange=0 xessFgResetDebounce=0 xellLimitLive=1 rtOwnQueue=0 rtCutoutTest=0 rtOpacityMicromaps=0 rtOmmMaxSubdivision=6 rtSunUnfiltered=0 rtSunAtrousSteps=0 rtSunHistory=4 rtFoliageTransmission=0.000000 rtRadianceCache=0 rtRadianceCacheEntries=20 rtRadianceCacheCellPixels=8.000000 rtRadianceCacheMinCell=0.125000 rtRadianceCacheSamples=256 rtRadianceCacheMaxAge=50 rtRadianceCacheDebug=0 rtBlasBatch=1 rtBlasPool=1 rtBlasBudget=0 rtBlasCompaction=2 rtBlasRefineBudget=65536 rtTlasFastTrace=0 rtMegaGeometry=0 reportSeconds=1 logInputsSeconds=0 dlssDirectory=(none) amdUpscalerDirectory=(none) xessDirectory=(none) streamlineDirectory=(none) xessFgDirectory=(none)
frame-steps-dlssg: enabled=1 allowTearing=1 measureOnly=0 frameGeneration=1 frameGenerationTechnique=dlss rrPath=auto hudLessColour=1 fgDepthInverted=1 fgNegateMotionScale=0 fgFlipInputs=1 fgFlipHudLess=0 fgCameraBasis=1 fgVSync=0 fgHalfRefreshLimit=0 fgAsyncWorkloads=0 fgObjectSeparationMetres=0.000000 dlssgMode=on dlssgDynamicTargetFps=0 dlssgUiRecomposition=0 dlssgUiAlpha=binary dlssgUiThreshold=2 pacing=0.100000/0.100000 fgMinimumFps=0 lowLatency=0 latencyBrake=0/4.000000/1/dlssg antiLag2=0 xell=1 xellLowLatencyBoost=0 vendorDiagnostics=0 streamlineImgui=0 fgShowOnlyGenerated=0 xessFgUiComposition=0 xessFgUiMode=auto xessFgFrames=1 xessFgSceneChangeThreshold=0.700000 xessFgTagInterpolatedFrames=0 xessFgPresentFailedInterpolation=0 xessFgOwnWaitable=auto xessFgGraceFrames=3 xessFgBadConstants=skip xessFgAllowOld=0 xessFgResetOnChange=0 xessFgResetDebounce=0 xellLimitLive=1 rtOwnQueue=0 rtCutoutTest=0 rtOpacityMicromaps=0 rtOmmMaxSubdivision=6 rtSunUnfiltered=0 rtSunAtrousSteps=0 rtSunHistory=4 rtFoliageTransmission=0.000000 rtRadianceCache=0 rtRadianceCacheEntries=20 rtRadianceCacheCellPixels=8.000000 rtRadianceCacheMinCell=0.125000 rtRadianceCacheSamples=256 rtRadianceCacheMaxAge=50 rtRadianceCacheDebug=0 rtBlasBatch=1 rtBlasPool=1 rtBlasBudget=0 rtBlasCompaction=2 rtBlasRefineBudget=65536 rtTlasFastTrace=0 rtMegaGeometry=0 reportSeconds=1 logInputsSeconds=0 dlssDirectory=(none) amdUpscalerDirectory=(none) xessDirectory=(none) streamlineDirectory=(none) xessFgDirectory=(none)
```

The DLSS-G package's ini in each start that had it, with the values it was read with:

```
frame-steps-dlssg: [FrameGeneration] Engine=DLSS MaxGeneratedFrames=5 [DLSSG] Optimized=2 Preset=Auto [XeSSFG] FlowResolution=Middle UseHudless=1 [Logging] Level=1 FrameTiming=1 [Runtime] Mode=Bundled [SuperResolution] Mode=Off
```

The raytracing's frames that built 50 meshes or more, at the flight's start:

* frame-steps-fsr: 4 frames, 572 meshes, 203354 triangles; the slowest 0.60 ms of the GPU,
  7.53 ms of the CPU
* frame-steps-dlssg: 5 frames, 632 meshes, 269642 triangles; the slowest 0.64 ms of the GPU,
  6.14 ms of the CPU

</details>

### Run 2026-10-21-0927

| What | Value |
|---|---|
| Date | 2026-10-21T09:27:51 to 2026-10-21T09:41:59 |
| Commit | `34f09f6` on `worktree-agent-a3c8b23ed6951386f` |
| Build in the install | `dxgi.dll` ec66cbc3a2508f0c (2026-10-21T09:05:20), `GameData/ReDefinition/Plugins/ReDefinition.dll` f61fcfbde9484e82 (2026-10-21T09:16:32) |
| Machine | NVIDIA RTX A4500, driver 597.16, AMD Ryzen 5 5600X 6-Core Processor, 31.9 GB, Microsoft Windows 11 Pro 26300, screen 2560x1440 rendered at 1707x960, 120 Hz |
| Profile | `medium`: [LOG 09:28:52.580] [ReDefinition] Probe: profile medium applied. |
| Groups | `grass` |
| Matrix | `tools/benchmark_matrix.json`, 2 starts, 2 s settling per phase, 8 s after a change of the rendering |
| Full record | [benchmarks/2026-10-21-0927.json](benchmarks/2026-10-21-0927.json) |

<details><summary>What ran in each phase</summary>

| Start | Scenario | Upscaler that ran | Frame generation that ran | presented/rendered | Lowered by | Reports | fps range | Notes |
|---|---|---|---|---|---|---|---|---|
| grass-fsr-pad | FSR 3 · RT Off · FG off · none | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 9 | 72.0–73.7 |  |
| grass-fsr-pad | FSR 3 · RT Off · FG FSR · none | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 9 | 66.1–67.5 |  |
| grass-fsr-pad | FSR 3 · RT Shadows · FG off · none | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 15 | 54.0–58.8 |  |
| grass-fsr-pad | FSR 3 · RT Shadows · FG FSR · none | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 9 | 54.2–54.9 |  |
| grass-fsr-pad | FSR 3 · RT Light · FG off · none | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 15 | 48.6–52.9 |  |
| grass-fsr-pad | FSR 3 · RT Light · FG FSR · none | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 9 | 44.7–45.1 |  |
| grass-fsr-pad | FSR 3 · RT Full · FG off · none | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 14 | 42.2–45.3 |  |
| grass-fsr-pad | FSR 3 · RT Full · FG FSR · none | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 8 | 39.4–39.7 |  |
| grass-fsr-pad | FSR 3 · RT Full, radiance cache · FG off · none | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 14 | 42.6–45.7 |  |
| grass-fsr-pad | FSR 3 · RT Full, radiance cache · FG FSR · none | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 8 | 39.8–40.0 |  |
| grass-fsr-pad | FSR 3 · RT Full, cut-out test 1 · FG off · none | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 14 | 43.5–46.4 |  |
| grass-fsr-pad | FSR 3 · RT Full, cut-out test 1 · FG FSR · none | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 8 | 40.2–40.7 |  |
| grass-fsr-pad | FSR 3 · RT Full, cut-out test 2 · FG off · none | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 15 | 43.8–48.1 |  |
| grass-fsr-pad | FSR 3 · RT Full, cut-out test 2 · FG FSR · none | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 9 | 41.1–41.6 |  |
| grass-fsr-pad | FSR 3 · RT Full, opacity micromaps · FG off · none | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 15 | 39.6–42.5 |  |
| grass-fsr-pad | FSR 3 · RT Full, opacity micromaps · FG FSR · none | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 8 | 37.0–37.2 |  |
| grass-fsr-grass | grass · FSR 3 · RT Off · FG off · none | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 9 | 80.2–80.9 |  |
| grass-fsr-grass | grass · FSR 3 · RT Off · FG FSR · none | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 9 | 64.6–64.8 |  |
| grass-fsr-grass | grass · FSR 3 · RT Shadows · FG off · none | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 15 | 56.5–60.1 |  |
| grass-fsr-grass | grass · FSR 3 · RT Shadows · FG FSR · none | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 9 | 50.4–50.6 |  |
| grass-fsr-grass | grass · FSR 3 · RT Light · FG off · none | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 14 | 44.5–46.3 |  |
| grass-fsr-grass | grass · FSR 3 · RT Light · FG FSR · none | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 8 | 40.1–40.4 |  |
| grass-fsr-grass | grass · FSR 3 · RT Full · FG off · none | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 14 | 37.5–39.7 |  |
| grass-fsr-grass | grass · FSR 3 · RT Full · FG FSR · none | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 8 | 35.2–35.3 |  |
| grass-fsr-grass | grass · FSR 3 · RT Full, radiance cache · FG off · none | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 14 | 37.5–40.0 |  |
| grass-fsr-grass | grass · FSR 3 · RT Full, radiance cache · FG FSR · none | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 8 | 35.5–35.7 |  |
| grass-fsr-grass | grass · FSR 3 · RT Full, cut-out test 1 · FG off · none | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 14 | 37.9–41.0 |  |
| grass-fsr-grass | grass · FSR 3 · RT Full, cut-out test 1 · FG FSR · none | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 8 | 36.0–36.2 |  |
| grass-fsr-grass | grass · FSR 3 · RT Full, cut-out test 2 · FG off · none | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 15 | 39.3–41.7 |  |
| grass-fsr-grass | grass · FSR 3 · RT Full, cut-out test 2 · FG FSR · none | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 9 | 36.8–36.9 |  |
| grass-fsr-grass | grass · FSR 3 · RT Full, opacity micromaps · FG off · none | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 15 | 34.9–37.4 |  |
| grass-fsr-grass | grass · FSR 3 · RT Full, opacity micromaps · FG FSR · none | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 8 | 33.3–33.4 |  |

The proxy's configuration in each start, as the proxy logged it at load:

```
grass-fsr-pad: enabled=1 allowTearing=1 measureOnly=0 frameGeneration=1 frameGenerationTechnique=fsr rrPath=auto hudLessColour=1 fgDepthInverted=1 fgNegateMotionScale=0 fgFlipInputs=1 fgFlipHudLess=0 fgCameraBasis=1 fgVSync=0 fgHalfRefreshLimit=0 fgAsyncWorkloads=0 fgObjectSeparationMetres=0.000000 dlssgMode=on dlssgDynamicTargetFps=0 dlssgUiRecomposition=0 dlssgUiAlpha=binary dlssgUiThreshold=2 pacing=0.100000/0.100000 fgMinimumFps=1000 lowLatency=0 latencyBrake=0/4.000000/1/dlssg antiLag2=0 xell=1 xellLowLatencyBoost=0 vendorDiagnostics=0 streamlineImgui=0 fgShowOnlyGenerated=0 xessFgUiComposition=0 xessFgUiMode=auto xessFgFrames=1 xessFgSceneChangeThreshold=0.700000 xessFgTagInterpolatedFrames=0 xessFgPresentFailedInterpolation=0 xessFgOwnWaitable=auto xessFgGraceFrames=3 xessFgBadConstants=skip xessFgAllowOld=0 xessFgResetOnChange=0 xessFgResetDebounce=0 xellLimitLive=1 rtOwnQueue=0 rtCutoutTest=0 rtOpacityMicromaps=0 rtOmmMaxSubdivision=6 rtSunUnfiltered=0 rtSunAtrousSteps=0 rtSunHistory=4 rtFoliageTransmission=0.000000 rtRadianceCache=0 rtRadianceCacheEntries=20 rtRadianceCacheCellPixels=8.000000 rtRadianceCacheMinCell=0.125000 rtRadianceCacheSamples=256 rtRadianceCacheMaxAge=50 rtRadianceCacheDebug=0 rtBlasBatch=1 rtBlasPool=1 rtBlasBudget=0 rtBlasCompaction=2 rtBlasRefineBudget=65536 rtTlasFastTrace=0 rtMegaGeometry=0 reportSeconds=1 logInputsSeconds=0 dlssDirectory=(none) amdUpscalerDirectory=(none) xessDirectory=(none) streamlineDirectory=(none) xessFgDirectory=(none)
grass-fsr-grass: enabled=1 allowTearing=1 measureOnly=0 frameGeneration=1 frameGenerationTechnique=fsr rrPath=auto hudLessColour=1 fgDepthInverted=1 fgNegateMotionScale=0 fgFlipInputs=1 fgFlipHudLess=0 fgCameraBasis=1 fgVSync=0 fgHalfRefreshLimit=0 fgAsyncWorkloads=0 fgObjectSeparationMetres=0.000000 dlssgMode=on dlssgDynamicTargetFps=0 dlssgUiRecomposition=0 dlssgUiAlpha=binary dlssgUiThreshold=2 pacing=0.100000/0.100000 fgMinimumFps=1000 lowLatency=0 latencyBrake=0/4.000000/1/dlssg antiLag2=0 xell=1 xellLowLatencyBoost=0 vendorDiagnostics=0 streamlineImgui=0 fgShowOnlyGenerated=0 xessFgUiComposition=0 xessFgUiMode=auto xessFgFrames=1 xessFgSceneChangeThreshold=0.700000 xessFgTagInterpolatedFrames=0 xessFgPresentFailedInterpolation=0 xessFgOwnWaitable=auto xessFgGraceFrames=3 xessFgBadConstants=skip xessFgAllowOld=0 xessFgResetOnChange=0 xessFgResetDebounce=0 xellLimitLive=1 rtOwnQueue=0 rtCutoutTest=0 rtOpacityMicromaps=0 rtOmmMaxSubdivision=6 rtSunUnfiltered=0 rtSunAtrousSteps=0 rtSunHistory=4 rtFoliageTransmission=0.000000 rtRadianceCache=0 rtRadianceCacheEntries=20 rtRadianceCacheCellPixels=8.000000 rtRadianceCacheMinCell=0.125000 rtRadianceCacheSamples=256 rtRadianceCacheMaxAge=50 rtRadianceCacheDebug=0 rtBlasBatch=1 rtBlasPool=1 rtBlasBudget=0 rtBlasCompaction=2 rtBlasRefineBudget=65536 rtTlasFastTrace=0 rtMegaGeometry=0 reportSeconds=1 logInputsSeconds=0 dlssDirectory=(none) amdUpscalerDirectory=(none) xessDirectory=(none) streamlineDirectory=(none) xessFgDirectory=(none)
```

The probe's scene arguments: grass-fsr-grass: `-redefinitionProbeGrass`.

The raytracing's frames that built 50 meshes or more, at the flight's start:

* grass-fsr-pad: 5 frames, 641 meshes, 273432 triangles; the slowest 0.58 ms of the GPU,
  8.09 ms of the CPU
* grass-fsr-grass: 4 frames, 409 meshes, 204060 triangles; the slowest 0.64 ms of the GPU,
  3.85 ms of the CPU

</details>

### Run 2026-10-21-0834

| What | Value |
|---|---|
| Date | 2026-10-21T08:34:06 to 2026-10-21T08:43:01 |
| Commit | `cdb7d75` on `feat/raytracing` |
| Build in the install | `dxgi.dll` 831698e6db4269cf (2026-10-21T08:05:34), `GameData/ReDefinition/Plugins/ReDefinition.dll` 0a27712edbcc37a4 (2026-10-21T08:05:21) |
| Machine | NVIDIA RTX A4500, driver 597.16, AMD Ryzen 5 5600X 6-Core Processor, 31.9 GB, Microsoft Windows 11 Pro 26300, screen 2560x1440 rendered at 1707x960, 120 Hz |
| Profile | `medium`: [LOG 08:35:12.239] [ReDefinition] Probe: profile medium applied. |
| Groups | `cutout-oneoff` |
| Matrix | `../../AppData/Local/Temp/claude/C--Users-franz-IdeaProjects-ReDefinition/830891e0-1c5a-4643-8173-8b16e3f60297/scratchpad/omm_matrix.json`, 1 starts, 2 s settling per phase, 8 s after a change of the rendering |
| Full record | [benchmarks/2026-10-21-0834.json](benchmarks/2026-10-21-0834.json) |

<details><summary>What ran in each phase</summary>

| Start | Scenario | Upscaler that ran | Frame generation that ran | presented/rendered | Lowered by | Reports | fps range | Notes |
|---|---|---|---|---|---|---|---|---|
| fsr | FSR 3 · RT Full · FG off · plain | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 21 | 40.7–46.9 |  |
| fsr | FSR 3 · RT Full · FG off · cutout1 | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 21 | 43.6–47.9 |  |
| fsr | FSR 3 · RT Full · FG off · cutout2 | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 21 | 44.9–48.6 |  |
| fsr | FSR 3 · RT Full · FG off · omm | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 19 | 40.7–43.3 |  |
| fsr | FSR 3 · RT Full · FG off · plain-again | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 20 | 41.7–45.5 |  |
| fsr | FSR 3 · RT Full · FG off · omm-again | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 20 | 39.8–42.6 |  |
| fsr | FSR 3 · RT Full · FG off · plain-last | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 20 | 42.0–44.7 |  |
| fsr | FSR 3 · RT Full · FG FSR · plain | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 20 | 41.6–45.0 | frame generation asked for, presented/rendered 1.00 |
| fsr | FSR 3 · RT Full · FG FSR · cutout1 | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 20 | 41.0–46.2 | frame generation asked for, presented/rendered 1.00 |
| fsr | FSR 3 · RT Full · FG FSR · cutout2 | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 20 | 43.9–47.0 | frame generation asked for, presented/rendered 1.00 |
| fsr | FSR 3 · RT Full · FG FSR · omm | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 20 | 38.7–42.8 | frame generation asked for, presented/rendered 1.00 |
| fsr | FSR 3 · RT Full · FG FSR · plain-again | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 21 | 41.7–44.9 | frame generation asked for, presented/rendered 1.00 |
| fsr | FSR 3 · RT Full · FG FSR · omm-again | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 21 | 39.5–43.0 | frame generation asked for, presented/rendered 1.00 |
| fsr | FSR 3 · RT Full · FG FSR · plain-last | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 20 | 42.1–44.8 | frame generation asked for, presented/rendered 1.00 |

The proxy's configuration in each start, as the proxy logged it at load:

```
fsr: enabled=1 allowTearing=1 measureOnly=0 frameGeneration=1 frameGenerationTechnique=fsr rrPath=auto hudLessColour=1 fgDepthInverted=1 fgNegateMotionScale=0 fgFlipInputs=1 fgFlipHudLess=0 fgCameraBasis=1 fgVSync=0 fgHalfRefreshLimit=0 fgAsyncWorkloads=0 fgObjectSeparationMetres=0.000000 dlssgMode=on dlssgUi=off pacing=0.100000/0.100000 fgMinimumFps=1000 lowLatency=0 latencyBrake=0/4.000000/1/dlssg antiLag2=0 xell=1 xellLowLatencyBoost=0 vendorDiagnostics=0 streamlineImgui=0 fgShowOnlyGenerated=0 xessFgUiComposition=0 xessFgUiMode=auto xessFgFrames=1 xessFgSceneChangeThreshold=0.700000 xessFgTagInterpolatedFrames=0 xessFgPresentFailedInterpolation=0 xessFgOwnWaitable=auto xessFgGraceFrames=3 xessFgBadConstants=skip xessFgAllowOld=0 xessFgResetOnChange=0 xessFgResetDebounce=0 xellLimitLive=1 rtOwnQueue=0 rtCutoutTest=0 rtOpacityMicromaps=0 rtOmmMaxSubdivision=6 rtSunUnfiltered=0 rtSunAtrousSteps=0 rtSunHistory=4 rtFoliageTransmission=0.000000 rtRadianceCache=0 rtRadianceCacheEntries=20 rtRadianceCacheCellPixels=8.000000 rtRadianceCacheMinCell=0.125000 rtRadianceCacheSamples=256 rtRadianceCacheMaxAge=50 rtRadianceCacheDebug=0 rtBlasBatch=1 rtBlasPool=1 rtBlasBudget=0 rtBlasCompaction=2 rtBlasRefineBudget=65536 rtTlasFastTrace=0 rtMegaGeometry=0 reportSeconds=1 logInputsSeconds=0 dlssDirectory=(none) amdUpscalerDirectory=(none) xessDirectory=(none) streamlineDirectory=(none) xessFgDirectory=(none)
```

The raytracing's frames that built 50 meshes or more, at the flight's start:

* fsr: 6 frames, 668 meshes, 236309 triangles; the slowest 1.26 ms of the GPU,
  5.32 ms of the CPU

</details>

### Run 2026-10-21-0241

| What | Value |
|---|---|
| Date | 2026-10-21T02:41:14 to 2026-10-21T03:23:57 |
| Commit | `dc88141` on `feat/raytracing` |
| Build in the install | `dxgi.dll` 4a3e00cc68de1b75 (2026-10-21T02:35:51), `GameData/ReDefinition/Plugins/ReDefinition.dll` 5632fd4d2e8bb25c (2026-10-21T02:35:57) |
| Machine | NVIDIA RTX A4500, driver 597.16, AMD Ryzen 5 5600X 6-Core Processor, 31.9 GB, Microsoft Windows 11 Pro 26300, screen 2560x1440 rendered at 1707x960, 120 Hz |
| Profile | `medium`: [LOG 02:42:15.998] [ReDefinition] Probe: profile medium applied. |
| Groups | `main` |
| Matrix | `tools/benchmark_matrix.json`, 4 starts, 2 s settling per phase, 8 s after a change of the rendering |
| Skipped | latency antilag2 (only on AMD) |
| Full record | [benchmarks/2026-10-21-0241.json](benchmarks/2026-10-21-0241.json) |

<details><summary>What ran in each phase</summary>

| Start | Scenario | Upscaler that ran | Frame generation that ran | presented/rendered | Lowered by | Reports | fps range | Notes |
|---|---|---|---|---|---|---|---|---|
| fsr | FSR 3 · RT Off · FG off · none | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 9 | 69.8–73.7 |  |
| fsr | FSR 3 · RT Off · FG off · Reflex | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex | 9 | 69.2–73.3 |  |
| fsr | FSR 3 · RT Off · FG off · brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | the brake | 9 | 68.5–73.6 |  |
| fsr | FSR 3 · RT Off · FG off · Reflex + brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex and the brake | 8 | 71.7–73.7 |  |
| fsr | FSR 3 · RT Off · FG FSR · none | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 8 | 66.3–66.9 |  |
| fsr | FSR 3 · RT Off · FG FSR · Reflex | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex | 8 | 63.9–67.0 |  |
| fsr | FSR 3 · RT Off · FG FSR · brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | the brake | 8 | 62.3–66.8 |  |
| fsr | FSR 3 · RT Off · FG FSR · Reflex + brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex and the brake | 8 | 65.0–66.6 |  |
| fsr | FSR 3 · RT Full · FG off · none | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 15 | 42.6–45.5 |  |
| fsr | FSR 3 · RT Full · FG off · Reflex | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex | 8 | 45.2–45.3 |  |
| fsr | FSR 3 · RT Full · FG off · brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | the brake | 8 | 44.8–45.4 |  |
| fsr | FSR 3 · RT Full · FG off · Reflex + brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex and the brake | 8 | 44.7–45.3 |  |
| fsr | FSR 3 · RT Full · FG FSR · none | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 8 | 39.5–39.7 |  |
| fsr | FSR 3 · RT Full · FG FSR · Reflex | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex | 8 | 39.5–39.7 |  |
| fsr | FSR 3 · RT Full · FG FSR · brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | the brake | 8 | 38.7–39.7 |  |
| fsr | FSR 3 · RT Full · FG FSR · Reflex + brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex and the brake | 8 | 38.5–39.9 |  |
| fsr | FSR 3 · RT Full, radiance cache · FG off · none | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 14 | 43.0–45.7 |  |
| fsr | FSR 3 · RT Full, radiance cache · FG FSR · none | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 8 | 39.9–40.2 |  |
| fsr | DLSS · RT Off · FG off · none | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 14 | 55.9–72.3 |  |
| fsr | DLSS · RT Off · FG off · Reflex | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex | 9 | 71.2–72.6 |  |
| fsr | DLSS · RT Off · FG off · brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | the brake | 9 | 66.6–72.0 |  |
| fsr | DLSS · RT Off · FG off · Reflex + brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex and the brake | 9 | 68.4–71.6 |  |
| fsr | DLSS · RT Off · FG FSR · none | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 9 | 60.8–62.4 |  |
| fsr | DLSS · RT Off · FG FSR · Reflex | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex | 9 | 61.9–62.5 |  |
| fsr | DLSS · RT Off · FG FSR · brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | the brake | 9 | 59.7–62.6 |  |
| fsr | DLSS · RT Off · FG FSR · Reflex + brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex and the brake | 9 | 60.7–62.6 |  |
| fsr | DLSS · RT Full, RR on · FG off · none | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 15 | 33.8–36.1 |  |
| fsr | DLSS · RT Full, RR on · FG off · Reflex | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex | 8 | 36.0–36.1 |  |
| fsr | DLSS · RT Full, RR on · FG off · brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | the brake | 8 | 35.9–36.1 |  |
| fsr | DLSS · RT Full, RR on · FG off · Reflex + brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex and the brake | 8 | 36.0–36.1 |  |
| fsr | DLSS · RT Full, RR on · FG FSR · none | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 9 | 32.1–32.2 |  |
| fsr | DLSS · RT Full, RR on · FG FSR · Reflex | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex | 8 | 32.2–32.3 |  |
| fsr | DLSS · RT Full, RR on · FG FSR · brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | the brake | 8 | 32.1–32.2 |  |
| fsr | DLSS · RT Full, RR on · FG FSR · Reflex + brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex and the brake | 8 | 32.1–32.2 |  |
| fsr | DLSS · RT Full, RR off · FG off · none | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 15 | 41.0–43.6 |  |
| fsr | DLSS · RT Full, RR off · FG off · Reflex | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex | 9 | 43.4–43.6 |  |
| fsr | DLSS · RT Full, RR off · FG off · brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | the brake | 9 | 42.9–43.7 |  |
| fsr | DLSS · RT Full, RR off · FG off · Reflex + brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex and the brake | 9 | 43.4–43.8 |  |
| fsr | DLSS · RT Full, RR off · FG FSR · none | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 9 | 37.9–38.4 |  |
| fsr | DLSS · RT Full, RR off · FG FSR · Reflex | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex | 9 | 38.2–38.4 |  |
| fsr | DLSS · RT Full, RR off · FG FSR · brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | the brake | 9 | 37.5–38.6 |  |
| fsr | DLSS · RT Full, RR off · FG FSR · Reflex + brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex and the brake | 9 | 37.8–38.6 |  |
| fsr | AMD DLL · RT Off · FG off · none | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 15 | 63.3–71.8 |  |
| fsr | AMD DLL · RT Off · FG off · Reflex | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex | 8 | 70.7–71.8 |  |
| fsr | AMD DLL · RT Off · FG off · brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | the brake | 8 | 66.7–72.1 |  |
| fsr | AMD DLL · RT Off · FG off · Reflex + brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex and the brake | 8 | 67.7–71.7 |  |
| fsr | AMD DLL · RT Off · FG FSR · none | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 8 | 62.8–63.3 |  |
| fsr | AMD DLL · RT Off · FG FSR · Reflex | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex | 9 | 59.8–63.7 |  |
| fsr | AMD DLL · RT Off · FG FSR · brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | the brake | 9 | 60.9–63.4 |  |
| fsr | AMD DLL · RT Off · FG FSR · Reflex + brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex and the brake | 9 | 61.1–63.4 |  |
| fsr | AMD DLL · RT Full · FG off · none | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 15 | 41.1–43.5 |  |
| fsr | AMD DLL · RT Full · FG off · Reflex | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex | 8 | 43.4–43.5 |  |
| fsr | AMD DLL · RT Full · FG off · brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | the brake | 8 | 43.1–43.5 |  |
| fsr | AMD DLL · RT Full · FG off · Reflex + brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex and the brake | 8 | 42.4–43.9 |  |
| fsr | AMD DLL · RT Full · FG FSR · none | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 8 | 38.2–38.3 |  |
| fsr | AMD DLL · RT Full · FG FSR · Reflex | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex | 8 | 37.9–38.3 |  |
| fsr | AMD DLL · RT Full · FG FSR · brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | the brake | 9 | 37.8–38.6 |  |
| fsr | AMD DLL · RT Full · FG FSR · Reflex + brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex and the brake | 9 | 37.7–38.3 |  |
| fsr | XeSS · RT Off · FG off · none | XeSS (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 15 | 66.1–71.7 |  |
| fsr | XeSS · RT Off · FG off · Reflex | XeSS (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex | 9 | 66.6–71.2 |  |
| fsr | XeSS · RT Off · FG off · brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | the brake | 8 | 70.3–71.7 |  |
| fsr | XeSS · RT Off · FG off · Reflex + brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex and the brake | 8 | 70.2–71.6 |  |
| fsr | XeSS · RT Off · FG FSR · none | XeSS (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 8 | 59.9–61.3 |  |
| fsr | XeSS · RT Off · FG FSR · Reflex | XeSS (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex | 8 | 61.0–61.4 |  |
| fsr | XeSS · RT Off · FG FSR · brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | the brake | 8 | 60.6–61.3 |  |
| fsr | XeSS · RT Off · FG FSR · Reflex + brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex and the brake | 9 | 59.7–61.2 |  |
| fsr | XeSS · RT Full · FG off · none | XeSS (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 15 | 39.3–42.0 |  |
| fsr | XeSS · RT Full · FG off · Reflex | XeSS (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex | 9 | 41.6–42.0 |  |
| fsr | XeSS · RT Full · FG off · brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | the brake | 9 | 41.6–42.0 |  |
| fsr | XeSS · RT Full · FG off · Reflex + brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex and the brake | 9 | 40.2–42.3 |  |
| fsr | XeSS · RT Full · FG FSR · none | XeSS (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 9 | 36.9–37.1 |  |
| fsr | XeSS · RT Full · FG FSR · Reflex | XeSS (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex | 9 | 36.9–37.0 |  |
| fsr | XeSS · RT Full · FG FSR · brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | the brake | 8 | 34.5–37.0 |  |
| fsr | XeSS · RT Full · FG FSR · Reflex + brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex and the brake | 8 | 36.7–37.1 |  |
| fsr | FSR 3 · RT Full, opacity micromaps · FG off · none | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 14 | 38.4–40.7 |  |
| fsr | FSR 3 · RT Full, opacity micromaps · FG FSR · none | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 8 | 35.6–35.7 |  |
| dlssg | FSR 3 · RT Off · FG DLSS-G · none | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 64.8–66.7 |  |
| dlssg | FSR 3 · RT Off · FG DLSS-G · Reflex | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 65.7–66.2 |  |
| dlssg | FSR 3 · RT Off · FG DLSS-G · brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex and the brake | 8 | 64.4–66.6 |  |
| dlssg | FSR 3 · RT Off · FG DLSS-G · Reflex + brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex and the brake | 8 | 65.7–66.5 |  |
| dlssg | FSR 3 · RT Full · FG DLSS-G · none | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 15 | 37.5–40.5 |  |
| dlssg | FSR 3 · RT Full · FG DLSS-G · Reflex | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 9 | 40.3–40.4 |  |
| dlssg | FSR 3 · RT Full · FG DLSS-G · brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex and the brake | 8 | 39.8–40.4 |  |
| dlssg | FSR 3 · RT Full · FG DLSS-G · Reflex + brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex and the brake | 8 | 39.7–40.4 |  |
| dlssg | FSR 3 · RT Full, radiance cache · FG DLSS-G · none | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 14 | 37.2–40.1 |  |
| dlssg | DLSS · RT Off · FG DLSS-G · none | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 15 | 55.7–61.8 |  |
| dlssg | DLSS · RT Off · FG DLSS-G · Reflex | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 9 | 59.6–62.0 |  |
| dlssg | DLSS · RT Off · FG DLSS-G · brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex and the brake | 8 | 61.1–61.6 |  |
| dlssg | DLSS · RT Off · FG DLSS-G · Reflex + brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex and the brake | 8 | 60.8–61.6 |  |
| dlssg | DLSS · RT Full, RR on · FG DLSS-G · none | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 14 | 30.1–32.3 |  |
| dlssg | DLSS · RT Full, RR on · FG DLSS-G · Reflex | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 32.2–32.3 |  |
| dlssg | DLSS · RT Full, RR on · FG DLSS-G · brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex and the brake | 9 | 32.2–32.3 |  |
| dlssg | DLSS · RT Full, RR on · FG DLSS-G · Reflex + brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex and the brake | 9 | 32.2–32.3 |  |
| dlssg | DLSS · RT Full, RR off · FG DLSS-G · none | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 15 | 36.1–38.6 |  |
| dlssg | DLSS · RT Full, RR off · FG DLSS-G · Reflex | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 9 | 38.5–38.6 |  |
| dlssg | DLSS · RT Full, RR off · FG DLSS-G · brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex and the brake | 9 | 38.3–38.7 |  |
| dlssg | DLSS · RT Full, RR off · FG DLSS-G · Reflex + brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex and the brake | 9 | 38.2–38.7 |  |
| dlssg | AMD DLL · RT Off · FG DLSS-G · none | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 15 | 58.3–62.8 |  |
| dlssg | AMD DLL · RT Off · FG DLSS-G · Reflex | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 62.6–63.0 |  |
| dlssg | AMD DLL · RT Off · FG DLSS-G · brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex and the brake | 8 | 62.3–62.9 |  |
| dlssg | AMD DLL · RT Off · FG DLSS-G · Reflex + brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex and the brake | 8 | 62.6–63.0 |  |
| dlssg | AMD DLL · RT Full · FG DLSS-G · none | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 14 | 35.9–39.0 |  |
| dlssg | AMD DLL · RT Full · FG DLSS-G · Reflex | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 38.8–39.0 |  |
| dlssg | AMD DLL · RT Full · FG DLSS-G · brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex and the brake | 8 | 38.6–39.2 |  |
| dlssg | AMD DLL · RT Full · FG DLSS-G · Reflex + brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex and the brake | 8 | 38.8–39.0 |  |
| dlssg | XeSS · RT Off · FG DLSS-G · none | XeSS (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 15 | 55.8–61.4 |  |
| dlssg | XeSS · RT Off · FG DLSS-G · Reflex | XeSS (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 9 | 60.2–61.0 |  |
| dlssg | XeSS · RT Off · FG DLSS-G · brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex and the brake | 9 | 60.1–61.1 |  |
| dlssg | XeSS · RT Off · FG DLSS-G · Reflex + brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex and the brake | 9 | 60.4–60.8 |  |
| dlssg | XeSS · RT Full · FG DLSS-G · none | XeSS (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 14 | 35.5–38.1 |  |
| dlssg | XeSS · RT Full · FG DLSS-G · Reflex | XeSS (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 38.0–38.0 |  |
| dlssg | XeSS · RT Full · FG DLSS-G · brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex and the brake | 8 | 37.9–38.0 |  |
| dlssg | XeSS · RT Full · FG DLSS-G · Reflex + brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex and the brake | 8 | 37.9–38.0 |  |
| dlssg | FSR 3 · RT Full, opacity micromaps · FG DLSS-G · none | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 15 | 34.7–37.3 |  |
| dlssg-c | FSR 3 · RT Off · FG DLSS-G preset C · none | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 9 | 70.5–71.5 |  |
| dlssg-c | FSR 3 · RT Full · FG DLSS-G preset C · none | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 13 | 39.1–42.2 |  |
| xessfg | FSR 3 · RT Off · FG off (XeSS-FG loaded) · XeLL | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing (XeLL's markers only) | 9 | 68.4–73.6 |  |
| xessfg | FSR 3 · RT Off · FG XeSS-FG · XeLL | FSR 3 Quality, 1707x960 to 2560x1440 | XeSS-FG | 2.00 | XeLL | 9 | 62.6–63.8 |  |
| xessfg | FSR 3 · RT Full · FG off (XeSS-FG loaded) · XeLL | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing (XeLL's markers only) | 15 | 43.4–46.1 |  |
| xessfg | FSR 3 · RT Full · FG XeSS-FG · XeLL | FSR 3 Quality, 1707x960 to 2560x1440 | XeSS-FG | 2.00 | XeLL | 9 | 38.7–39.2 |  |
| xessfg | FSR 3 · RT Full, radiance cache · FG off (XeSS-FG loaded) · XeLL | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing (XeLL's markers only) | 15 | 43.6–46.4 |  |
| xessfg | FSR 3 · RT Full, radiance cache · FG XeSS-FG · XeLL | FSR 3 Quality, 1707x960 to 2560x1440 | XeSS-FG | 2.00 | XeLL | 8 | 38.9–39.6 |  |
| xessfg | DLSS · RT Off · FG off (XeSS-FG loaded) · XeLL | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing (XeLL's markers only) | 14 | 64.7–71.9 |  |
| xessfg | DLSS · RT Off · FG XeSS-FG · XeLL | DLSS Quality, 1707x960 to 2560x1440 | XeSS-FG | 2.00 | XeLL | 9 | 57.5–58.3 |  |
| xessfg | DLSS · RT Full, RR on · FG off (XeSS-FG loaded) · XeLL | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing (XeLL's markers only) | 14 | 33.6–36.3 |  |
| xessfg | DLSS · RT Full, RR on · FG XeSS-FG · XeLL | DLSS Quality, 1707x960 to 2560x1440 | XeSS-FG | 2.00 | XeLL | 8 | 31.0–31.6 |  |
| xessfg | DLSS · RT Full, RR off · FG off (XeSS-FG loaded) · XeLL | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing (XeLL's markers only) | 15 | 40.5–43.6 |  |
| xessfg | DLSS · RT Full, RR off · FG XeSS-FG · XeLL | DLSS Quality, 1707x960 to 2560x1440 | XeSS-FG | 2.00 | XeLL | 9 | 36.8–37.3 |  |
| xessfg | AMD DLL · RT Off · FG off (XeSS-FG loaded) · XeLL | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing (XeLL's markers only) | 14 | 68.3–73.1 |  |
| xessfg | AMD DLL · RT Off · FG XeSS-FG · XeLL | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | XeSS-FG | 2.00 | XeLL | 8 | 59.3–60.0 |  |
| xessfg | AMD DLL · RT Full · FG off (XeSS-FG loaded) · XeLL | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing (XeLL's markers only) | 14 | 41.4–44.1 |  |
| xessfg | AMD DLL · RT Full · FG XeSS-FG · XeLL | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | XeSS-FG | 2.00 | XeLL | 8 | 37.2–37.7 |  |
| xessfg | XeSS · RT Off · FG off (XeSS-FG loaded) · XeLL | XeSS (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing (XeLL's markers only) | 14 | 68.2–73.0 |  |
| xessfg | XeSS · RT Off · FG XeSS-FG · XeLL | XeSS (DLL) Quality, 1707x960 to 2560x1440 | XeSS-FG | 2.00 | XeLL | 8 | 56.9–57.5 |  |
| xessfg | XeSS · RT Full · FG off (XeSS-FG loaded) · XeLL | XeSS (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing (XeLL's markers only) | 14 | 40.5–43.0 |  |
| xessfg | XeSS · RT Full · FG XeSS-FG · XeLL | XeSS (DLL) Quality, 1707x960 to 2560x1440 | XeSS-FG | 2.00 | XeLL | 9 | 36.4–36.9 |  |
| xessfg | FSR 3 · RT Full, opacity micromaps · FG off (XeSS-FG loaded) · XeLL | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing (XeLL's markers only) | 15 | 39.2–42.2 |  |
| xessfg | FSR 3 · RT Full, opacity micromaps · FG XeSS-FG · XeLL | FSR 3 Quality, 1707x960 to 2560x1440 | XeSS-FG | 2.00 | XeLL | 8 | 35.7–36.0 |  |

The proxy's configuration in each start, as the proxy logged it at load:

```
fsr: enabled=1 allowTearing=1 measureOnly=0 frameGeneration=1 frameGenerationTechnique=fsr rrPath=auto hudLessColour=1 fgDepthInverted=1 fgNegateMotionScale=0 fgFlipInputs=1 fgFlipHudLess=0 fgCameraBasis=1 fgVSync=0 fgHalfRefreshLimit=0 fgAsyncWorkloads=0 fgObjectSeparationMetres=0.000000 dlssgMode=on pacing=0.100000/0.100000 fgMinimumFps=1000 lowLatency=0 latencyBrake=0/4.000000/1/dlssg antiLag2=0 xell=1 xellLowLatencyBoost=0 vendorDiagnostics=0 streamlineImgui=0 fgShowOnlyGenerated=0 xessFgUiComposition=0 xessFgUiMode=auto xessFgFrames=1 xessFgSceneChangeThreshold=0.700000 xessFgTagInterpolatedFrames=0 xessFgPresentFailedInterpolation=0 xessFgOwnWaitable=auto xessFgGraceFrames=3 xessFgBadConstants=skip xessFgAllowOld=0 xessFgResetOnChange=0 xessFgResetDebounce=0 xellLimitLive=1 rtOwnQueue=0 rtCutoutTest=0 rtOpacityMicromaps=0 rtOmmMaxSubdivision=6 rtSunUnfiltered=0 rtSunAtrousSteps=0 rtSunHistory=4 rtFoliageTransmission=0.000000 rtRadianceCache=0 rtRadianceCacheEntries=20 rtRadianceCacheCellPixels=8.000000 rtRadianceCacheMinCell=0.125000 rtRadianceCacheSamples=256 rtRadianceCacheMaxAge=50 rtRadianceCacheDebug=0 rtBlasBatch=1 rtBlasPool=1 rtBlasBudget=0 rtBlasCompaction=2 rtBlasRefineBudget=65536 rtTlasFastTrace=0 rtMegaGeometry=0 reportSeconds=1 logInputsSeconds=0 dlssDirectory=(none) amdUpscalerDirectory=(set) xessDirectory=(set) streamlineDirectory=(none) xessFgDirectory=(none)
dlssg: enabled=1 allowTearing=1 measureOnly=0 frameGeneration=1 frameGenerationTechnique=dlss rrPath=auto hudLessColour=1 fgDepthInverted=1 fgNegateMotionScale=0 fgFlipInputs=1 fgFlipHudLess=0 fgCameraBasis=1 fgVSync=0 fgHalfRefreshLimit=0 fgAsyncWorkloads=0 fgObjectSeparationMetres=0.000000 dlssgMode=on pacing=0.100000/0.100000 fgMinimumFps=0 lowLatency=0 latencyBrake=0/4.000000/1/dlssg antiLag2=0 xell=1 xellLowLatencyBoost=0 vendorDiagnostics=0 streamlineImgui=0 fgShowOnlyGenerated=0 xessFgUiComposition=0 xessFgUiMode=auto xessFgFrames=1 xessFgSceneChangeThreshold=0.700000 xessFgTagInterpolatedFrames=0 xessFgPresentFailedInterpolation=0 xessFgOwnWaitable=auto xessFgGraceFrames=3 xessFgBadConstants=skip xessFgAllowOld=0 xessFgResetOnChange=0 xessFgResetDebounce=0 xellLimitLive=1 rtOwnQueue=0 rtCutoutTest=0 rtOpacityMicromaps=0 rtOmmMaxSubdivision=6 rtSunUnfiltered=0 rtSunAtrousSteps=0 rtSunHistory=4 rtFoliageTransmission=0.000000 rtRadianceCache=0 rtRadianceCacheEntries=20 rtRadianceCacheCellPixels=8.000000 rtRadianceCacheMinCell=0.125000 rtRadianceCacheSamples=256 rtRadianceCacheMaxAge=50 rtRadianceCacheDebug=0 rtBlasBatch=1 rtBlasPool=1 rtBlasBudget=0 rtBlasCompaction=2 rtBlasRefineBudget=65536 rtTlasFastTrace=0 rtMegaGeometry=0 reportSeconds=1 logInputsSeconds=0 dlssDirectory=(none) amdUpscalerDirectory=(set) xessDirectory=(set) streamlineDirectory=(none) xessFgDirectory=(none)
dlssg-c: enabled=1 allowTearing=1 measureOnly=0 frameGeneration=1 frameGenerationTechnique=dlss rrPath=auto hudLessColour=1 fgDepthInverted=1 fgNegateMotionScale=0 fgFlipInputs=1 fgFlipHudLess=0 fgCameraBasis=1 fgVSync=0 fgHalfRefreshLimit=0 fgAsyncWorkloads=0 fgObjectSeparationMetres=0.000000 dlssgMode=on pacing=0.100000/0.100000 fgMinimumFps=0 lowLatency=0 latencyBrake=0/4.000000/1/dlssg antiLag2=0 xell=1 xellLowLatencyBoost=0 vendorDiagnostics=0 streamlineImgui=0 fgShowOnlyGenerated=0 xessFgUiComposition=0 xessFgUiMode=auto xessFgFrames=1 xessFgSceneChangeThreshold=0.700000 xessFgTagInterpolatedFrames=0 xessFgPresentFailedInterpolation=0 xessFgOwnWaitable=auto xessFgGraceFrames=3 xessFgBadConstants=skip xessFgAllowOld=0 xessFgResetOnChange=0 xessFgResetDebounce=0 xellLimitLive=1 rtOwnQueue=0 rtCutoutTest=0 rtOpacityMicromaps=0 rtOmmMaxSubdivision=6 rtSunUnfiltered=0 rtSunAtrousSteps=0 rtSunHistory=4 rtFoliageTransmission=0.000000 rtRadianceCache=0 rtRadianceCacheEntries=20 rtRadianceCacheCellPixels=8.000000 rtRadianceCacheMinCell=0.125000 rtRadianceCacheSamples=256 rtRadianceCacheMaxAge=50 rtRadianceCacheDebug=0 rtBlasBatch=1 rtBlasPool=1 rtBlasBudget=0 rtBlasCompaction=2 rtBlasRefineBudget=65536 rtTlasFastTrace=0 rtMegaGeometry=0 reportSeconds=1 logInputsSeconds=0 dlssDirectory=(none) amdUpscalerDirectory=(none) xessDirectory=(none) streamlineDirectory=(none) xessFgDirectory=(none)
xessfg: enabled=1 allowTearing=1 measureOnly=0 frameGeneration=1 frameGenerationTechnique=xess rrPath=auto hudLessColour=1 fgDepthInverted=1 fgNegateMotionScale=0 fgFlipInputs=1 fgFlipHudLess=0 fgCameraBasis=1 fgVSync=0 fgHalfRefreshLimit=0 fgAsyncWorkloads=0 fgObjectSeparationMetres=0.000000 dlssgMode=on pacing=0.100000/0.100000 fgMinimumFps=1000 lowLatency=1 latencyBrake=0/4.000000/1/dlssg antiLag2=0 xell=1 xellLowLatencyBoost=0 vendorDiagnostics=0 streamlineImgui=0 fgShowOnlyGenerated=0 xessFgUiComposition=0 xessFgUiMode=auto xessFgFrames=1 xessFgSceneChangeThreshold=0.700000 xessFgTagInterpolatedFrames=0 xessFgPresentFailedInterpolation=0 xessFgOwnWaitable=auto xessFgGraceFrames=3 xessFgBadConstants=skip xessFgAllowOld=0 xessFgResetOnChange=0 xessFgResetDebounce=0 xellLimitLive=1 rtOwnQueue=0 rtCutoutTest=0 rtOpacityMicromaps=0 rtOmmMaxSubdivision=6 rtSunUnfiltered=0 rtSunAtrousSteps=0 rtSunHistory=4 rtFoliageTransmission=0.000000 rtRadianceCache=0 rtRadianceCacheEntries=20 rtRadianceCacheCellPixels=8.000000 rtRadianceCacheMinCell=0.125000 rtRadianceCacheSamples=256 rtRadianceCacheMaxAge=50 rtRadianceCacheDebug=0 rtBlasBatch=1 rtBlasPool=1 rtBlasBudget=0 rtBlasCompaction=2 rtBlasRefineBudget=65536 rtTlasFastTrace=0 rtMegaGeometry=0 reportSeconds=1 logInputsSeconds=0 dlssDirectory=(none) amdUpscalerDirectory=(set) xessDirectory=(set) streamlineDirectory=(none) xessFgDirectory=(set)
```

The raytracing's frames that built 50 meshes or more, at the flight's start:

* fsr: 10 frames, 1306 meshes, 550680 triangles; the slowest 0.67 ms of the GPU,
  3.03 ms of the CPU
* dlssg: 10 frames, 1236 meshes, 472995 triangles; the slowest 0.67 ms of the GPU,
  3.47 ms of the CPU
* dlssg-c: 5 frames, 612 meshes, 262395 triangles; the slowest 0.63 ms of the GPU,
  5.19 ms of the CPU
* xessfg: 10 frames, 1318 meshes, 554512 triangles; the slowest 0.63 ms of the GPU,
  3.14 ms of the CPU

</details>

### Run 2026-10-20-2347

| What | Value |
|---|---|
| Date | 2026-10-20T23:47:50 to 2026-10-21T00:30:38 |
| Commit | `a02fdd2` on `feat/raytracing` |
| Build in the install | `dxgi.dll` 57fa8380b776e4a5 (2026-10-20T23:45:44), `GameData/ReDefinition/Plugins/ReDefinition.dll` e9d02b228931a8bb (2026-10-20T22:46:54) |
| Machine | NVIDIA RTX A4500, driver 597.16, AMD Ryzen 5 5600X 6-Core Processor, 31.9 GB, Microsoft Windows 11 Pro 26300, screen 2560x1440 rendered at 1707x960, 120 Hz |
| Profile | `medium`: [LOG 23:48:52.605] [ReDefinition] Probe: profile medium applied. |
| Groups | `main` |
| Matrix | `tools/benchmark_matrix.json`, 4 starts, 2 s settling per phase, 8 s after a change of the rendering |
| Skipped | latency antilag2 (only on AMD) |
| Full record | [benchmarks/2026-10-20-2347.json](benchmarks/2026-10-20-2347.json) |

<details><summary>What ran in each phase</summary>

| Start | Scenario | Upscaler that ran | Frame generation that ran | presented/rendered | Lowered by | Reports | fps range | Notes |
|---|---|---|---|---|---|---|---|---|
| fsr | FSR 3 · RT Off · FG off · none | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 9 | 67.1–71.2 |  |
| fsr | FSR 3 · RT Off · FG off · Reflex | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex | 9 | 68.2–70.8 |  |
| fsr | FSR 3 · RT Off · FG off · brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | the brake | 8 | 66.7–71.0 |  |
| fsr | FSR 3 · RT Off · FG off · Reflex + brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex and the brake | 8 | 67.2–71.4 |  |
| fsr | FSR 3 · RT Off · FG FSR · none | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 8 | 61.5–61.7 |  |
| fsr | FSR 3 · RT Off · FG FSR · Reflex | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex | 8 | 59.4–62.0 |  |
| fsr | FSR 3 · RT Off · FG FSR · brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | the brake | 8 | 61.4–62.2 |  |
| fsr | FSR 3 · RT Off · FG FSR · Reflex + brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex and the brake | 8 | 60.3–62.0 |  |
| fsr | FSR 3 · RT Full · FG off · none | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 14 | 42.8–44.2 |  |
| fsr | FSR 3 · RT Full · FG off · Reflex | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex | 8 | 44.0–44.2 |  |
| fsr | FSR 3 · RT Full · FG off · brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | the brake | 8 | 43.0–44.5 |  |
| fsr | FSR 3 · RT Full · FG off · Reflex + brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex and the brake | 8 | 43.7–44.2 |  |
| fsr | FSR 3 · RT Full · FG FSR · none | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 8 | 37.1–37.3 |  |
| fsr | FSR 3 · RT Full · FG FSR · Reflex | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex | 8 | 36.8–37.6 |  |
| fsr | FSR 3 · RT Full · FG FSR · brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | the brake | 8 | 36.6–37.6 |  |
| fsr | FSR 3 · RT Full · FG FSR · Reflex + brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex and the brake | 9 | 36.7–37.7 |  |
| fsr | FSR 3 · RT Full, radiance cache · FG off · none | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 15 | 41.8–44.7 |  |
| fsr | FSR 3 · RT Full, radiance cache · FG FSR · none | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 9 | 37.4–37.7 |  |
| fsr | DLSS · RT Off · FG off · none | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 14 | 60.6–70.7 |  |
| fsr | DLSS · RT Off · FG off · Reflex | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex | 9 | 56.8–70.9 |  |
| fsr | DLSS · RT Off · FG off · brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | the brake | 9 | 69.4–70.9 |  |
| fsr | DLSS · RT Off · FG off · Reflex + brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex and the brake | 9 | 65.6–70.9 |  |
| fsr | DLSS · RT Off · FG FSR · none | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 9 | 55.9–57.7 |  |
| fsr | DLSS · RT Off · FG FSR · Reflex | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex | 9 | 57.1–57.5 |  |
| fsr | DLSS · RT Off · FG FSR · brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | the brake | 8 | 56.8–57.9 |  |
| fsr | DLSS · RT Off · FG FSR · Reflex + brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex and the brake | 8 | 56.5–57.7 |  |
| fsr | DLSS · RT Full, RR on · FG off · none | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 14 | 33.3–35.4 |  |
| fsr | DLSS · RT Full, RR on · FG off · Reflex | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex | 9 | 35.3–35.4 |  |
| fsr | DLSS · RT Full, RR on · FG off · brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | the brake | 9 | 35.0–35.3 |  |
| fsr | DLSS · RT Full, RR on · FG off · Reflex + brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex and the brake | 9 | 35.2–35.4 |  |
| fsr | DLSS · RT Full, RR on · FG FSR · none | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 9 | 30.5–30.6 |  |
| fsr | DLSS · RT Full, RR on · FG FSR · Reflex | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex | 9 | 30.5–30.6 |  |
| fsr | DLSS · RT Full, RR on · FG FSR · brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | the brake | 9 | 30.5–30.6 |  |
| fsr | DLSS · RT Full, RR on · FG FSR · Reflex + brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex and the brake | 9 | 30.5–30.6 |  |
| fsr | DLSS · RT Full, RR off · FG off · none | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 15 | 39.8–42.6 |  |
| fsr | DLSS · RT Full, RR off · FG off · Reflex | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex | 8 | 42.1–42.6 |  |
| fsr | DLSS · RT Full, RR off · FG off · brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | the brake | 8 | 42.1–42.8 |  |
| fsr | DLSS · RT Full, RR off · FG off · Reflex + brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex and the brake | 8 | 41.8–42.8 |  |
| fsr | DLSS · RT Full, RR off · FG FSR · none | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 8 | 36.0–36.1 |  |
| fsr | DLSS · RT Full, RR off · FG FSR · Reflex | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex | 9 | 35.9–36.0 |  |
| fsr | DLSS · RT Full, RR off · FG FSR · brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | the brake | 9 | 35.8–36.2 |  |
| fsr | DLSS · RT Full, RR off · FG FSR · Reflex + brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex and the brake | 8 | 35.8–36.2 |  |
| fsr | AMD DLL · RT Off · FG off · none | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 15 | 64.9–71.0 |  |
| fsr | AMD DLL · RT Off · FG off · Reflex | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex | 8 | 69.3–70.7 |  |
| fsr | AMD DLL · RT Off · FG off · brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | the brake | 8 | 66.0–70.6 |  |
| fsr | AMD DLL · RT Off · FG off · Reflex + brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex and the brake | 8 | 69.1–70.3 |  |
| fsr | AMD DLL · RT Off · FG FSR · none | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 8 | 57.9–58.3 |  |
| fsr | AMD DLL · RT Off · FG FSR · Reflex | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex | 8 | 57.7–58.4 |  |
| fsr | AMD DLL · RT Off · FG FSR · brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | the brake | 8 | 57.7–58.6 |  |
| fsr | AMD DLL · RT Off · FG FSR · Reflex + brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex and the brake | 8 | 56.8–58.4 |  |
| fsr | AMD DLL · RT Full · FG off · none | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 14 | 40.1–42.6 |  |
| fsr | AMD DLL · RT Full · FG off · Reflex | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex | 8 | 42.4–42.5 |  |
| fsr | AMD DLL · RT Full · FG off · brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | the brake | 8 | 42.2–42.5 |  |
| fsr | AMD DLL · RT Full · FG off · Reflex + brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex and the brake | 8 | 42.3–42.7 |  |
| fsr | AMD DLL · RT Full · FG FSR · none | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 8 | 36.0–36.1 |  |
| fsr | AMD DLL · RT Full · FG FSR · Reflex | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex | 9 | 35.6–36.4 |  |
| fsr | AMD DLL · RT Full · FG FSR · brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | the brake | 9 | 35.9–36.3 |  |
| fsr | AMD DLL · RT Full · FG FSR · Reflex + brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex and the brake | 9 | 35.7–36.2 |  |
| fsr | XeSS · RT Off · FG off · none | XeSS (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 15 | 63.1–69.9 |  |
| fsr | XeSS · RT Off · FG off · Reflex | XeSS (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex | 9 | 65.1–69.6 |  |
| fsr | XeSS · RT Off · FG off · brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | the brake | 8 | 64.3–69.8 |  |
| fsr | XeSS · RT Off · FG off · Reflex + brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex and the brake | 8 | 67.8–69.4 |  |
| fsr | XeSS · RT Off · FG FSR · none | XeSS (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 8 | 56.0–56.4 |  |
| fsr | XeSS · RT Off · FG FSR · Reflex | XeSS (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex | 8 | 55.7–56.3 |  |
| fsr | XeSS · RT Off · FG FSR · brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | the brake | 8 | 55.2–56.5 |  |
| fsr | XeSS · RT Off · FG FSR · Reflex + brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex and the brake | 9 | 54.7–56.6 |  |
| fsr | XeSS · RT Full · FG off · none | XeSS (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 14 | 38.7–41.1 |  |
| fsr | XeSS · RT Full · FG off · Reflex | XeSS (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex | 8 | 40.9–41.1 |  |
| fsr | XeSS · RT Full · FG off · brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | the brake | 8 | 40.9–41.2 |  |
| fsr | XeSS · RT Full · FG off · Reflex + brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex and the brake | 8 | 40.7–41.2 |  |
| fsr | XeSS · RT Full · FG FSR · none | XeSS (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 8 | 34.6–35.3 |  |
| fsr | XeSS · RT Full · FG FSR · Reflex | XeSS (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex | 9 | 34.9–35.0 |  |
| fsr | XeSS · RT Full · FG FSR · brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | the brake | 8 | 34.9–35.0 |  |
| fsr | XeSS · RT Full · FG FSR · Reflex + brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex and the brake | 8 | 34.9–35.0 |  |
| fsr | FSR 3 · RT Full, opacity micromaps · FG off · none | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 14 | 37.6–39.8 |  |
| fsr | FSR 3 · RT Full, opacity micromaps · FG FSR · none | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 8 | 33.9–34.0 |  |
| dlssg | FSR 3 · RT Off · FG DLSS-G · none | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 64.2–66.8 |  |
| dlssg | FSR 3 · RT Off · FG DLSS-G · Reflex | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 65.4–66.3 |  |
| dlssg | FSR 3 · RT Off · FG DLSS-G · brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 65.9–66.4 |  |
| dlssg | FSR 3 · RT Off · FG DLSS-G · Reflex + brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 65.2–66.3 |  |
| dlssg | FSR 3 · RT Full · FG DLSS-G · none | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 14 | 37.4–40.4 |  |
| dlssg | FSR 3 · RT Full · FG DLSS-G · Reflex | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 40.0–40.3 |  |
| dlssg | FSR 3 · RT Full · FG DLSS-G · brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 40.2–40.4 |  |
| dlssg | FSR 3 · RT Full · FG DLSS-G · Reflex + brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 39.7–40.3 |  |
| dlssg | FSR 3 · RT Full, radiance cache · FG DLSS-G · none | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 14 | 37.3–40.1 |  |
| dlssg | DLSS · RT Off · FG DLSS-G · none | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 14 | 57.0–61.8 |  |
| dlssg | DLSS · RT Off · FG DLSS-G · Reflex | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 60.8–61.2 |  |
| dlssg | DLSS · RT Off · FG DLSS-G · brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 59.7–61.9 |  |
| dlssg | DLSS · RT Off · FG DLSS-G · Reflex + brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 60.8–61.2 |  |
| dlssg | DLSS · RT Full, RR on · FG DLSS-G · none | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 14 | 30.0–32.1 |  |
| dlssg | DLSS · RT Full, RR on · FG DLSS-G · Reflex | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 32.1–32.1 |  |
| dlssg | DLSS · RT Full, RR on · FG DLSS-G · brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 9 | 32.1–32.2 |  |
| dlssg | DLSS · RT Full, RR on · FG DLSS-G · Reflex + brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 9 | 32.1–32.2 |  |
| dlssg | DLSS · RT Full, RR off · FG DLSS-G · none | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 14 | 36.1–38.5 |  |
| dlssg | DLSS · RT Full, RR off · FG DLSS-G · Reflex | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 38.4–38.5 |  |
| dlssg | DLSS · RT Full, RR off · FG DLSS-G · brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 38.3–38.5 |  |
| dlssg | DLSS · RT Full, RR off · FG DLSS-G · Reflex + brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 38.3–38.5 |  |
| dlssg | AMD DLL · RT Off · FG DLSS-G · none | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 14 | 57.8–63.1 |  |
| dlssg | AMD DLL · RT Off · FG DLSS-G · Reflex | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 9 | 62.0–62.4 |  |
| dlssg | AMD DLL · RT Off · FG DLSS-G · brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 61.0–62.5 |  |
| dlssg | AMD DLL · RT Off · FG DLSS-G · Reflex + brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 62.1–62.3 |  |
| dlssg | AMD DLL · RT Full · FG DLSS-G · none | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 14 | 36.1–38.8 |  |
| dlssg | AMD DLL · RT Full · FG DLSS-G · Reflex | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 38.7–38.9 |  |
| dlssg | AMD DLL · RT Full · FG DLSS-G · brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 38.7–38.8 |  |
| dlssg | AMD DLL · RT Full · FG DLSS-G · Reflex + brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 38.7–38.8 |  |
| dlssg | XeSS · RT Off · FG DLSS-G · none | XeSS (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 14 | 55.7–60.5 |  |
| dlssg | XeSS · RT Off · FG DLSS-G · Reflex | XeSS (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 59.9–60.4 |  |
| dlssg | XeSS · RT Off · FG DLSS-G · brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 59.7–60.5 |  |
| dlssg | XeSS · RT Off · FG DLSS-G · Reflex + brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 9 | 60.0–60.5 |  |
| dlssg | XeSS · RT Full · FG DLSS-G · none | XeSS (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 14 | 35.3–37.9 |  |
| dlssg | XeSS · RT Full · FG DLSS-G · Reflex | XeSS (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 37.6–38.0 |  |
| dlssg | XeSS · RT Full · FG DLSS-G · brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 37.8–37.9 |  |
| dlssg | XeSS · RT Full · FG DLSS-G · Reflex + brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 37.8–37.9 |  |
| dlssg | FSR 3 · RT Full, opacity micromaps · FG DLSS-G · none | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 14 | 34.8–37.2 |  |
| dlssg-c | FSR 3 · RT Off · FG DLSS-G preset C · none | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | Streamline's Reflex | 9 | 68.9–72.5 | frame generation asked for, presented/rendered 1.00 |
| dlssg-c | FSR 3 · RT Full · FG DLSS-G preset C · none | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | Streamline's Reflex | 14 | 43.4–45.2 | frame generation asked for, presented/rendered 1.00 |
| xessfg | FSR 3 · RT Off · FG off (XeSS-FG loaded) · XeLL | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing (XeLL's markers only) | 9 | 67.8–73.2 |  |
| xessfg | FSR 3 · RT Off · FG XeSS-FG · XeLL | FSR 3 Quality, 1707x960 to 2560x1440 | XeSS-FG | 2.00 | XeLL | 8 | 58.3–59.9 |  |
| xessfg | FSR 3 · RT Full · FG off (XeSS-FG loaded) · XeLL | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing (XeLL's markers only) | 14 | 43.5–45.2 |  |
| xessfg | FSR 3 · RT Full · FG XeSS-FG · XeLL | FSR 3 Quality, 1707x960 to 2560x1440 | XeSS-FG | 2.00 | XeLL | 8 | 36.3–36.9 |  |
| xessfg | FSR 3 · RT Full, radiance cache · FG off (XeSS-FG loaded) · XeLL | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing (XeLL's markers only) | 14 | 42.7–45.5 |  |
| xessfg | FSR 3 · RT Full, radiance cache · FG XeSS-FG · XeLL | FSR 3 Quality, 1707x960 to 2560x1440 | XeSS-FG | 2.00 | XeLL | 8 | 36.8–37.2 |  |
| xessfg | DLSS · RT Off · FG off (XeSS-FG loaded) · XeLL | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing (XeLL's markers only) | 14 | 61.3–71.9 |  |
| xessfg | DLSS · RT Off · FG XeSS-FG · XeLL | DLSS Quality, 1707x960 to 2560x1440 | XeSS-FG | 2.00 | XeLL | 9 | 53.9–55.2 |  |
| xessfg | DLSS · RT Full, RR on · FG off (XeSS-FG loaded) · XeLL | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing (XeLL's markers only) | 14 | 33.0–35.6 |  |
| xessfg | DLSS · RT Full, RR on · FG XeSS-FG · XeLL | DLSS Quality, 1707x960 to 2560x1440 | XeSS-FG | 2.00 | XeLL | 9 | 29.5–29.8 |  |
| xessfg | DLSS · RT Full, RR off · FG off (XeSS-FG loaded) · XeLL | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing (XeLL's markers only) | 15 | 39.8–42.7 |  |
| xessfg | DLSS · RT Full, RR off · FG XeSS-FG · XeLL | DLSS Quality, 1707x960 to 2560x1440 | XeSS-FG | 2.00 | XeLL | 9 | 34.0–35.3 |  |
| xessfg | AMD DLL · RT Off · FG off (XeSS-FG loaded) · XeLL | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing (XeLL's markers only) | 14 | 66.0–72.0 |  |
| xessfg | AMD DLL · RT Off · FG XeSS-FG · XeLL | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | XeSS-FG | 2.00 | XeLL | 8 | 55.7–56.4 |  |
| xessfg | AMD DLL · RT Full · FG off (XeSS-FG loaded) · XeLL | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing (XeLL's markers only) | 14 | 40.6–43.3 |  |
| xessfg | AMD DLL · RT Full · FG XeSS-FG · XeLL | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | XeSS-FG | 2.00 | XeLL | 8 | 35.3–35.7 |  |
| xessfg | XeSS · RT Off · FG off (XeSS-FG loaded) · XeLL | XeSS (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing (XeLL's markers only) | 14 | 64.9–72.3 |  |
| xessfg | XeSS · RT Off · FG XeSS-FG · XeLL | XeSS (DLL) Quality, 1707x960 to 2560x1440 | XeSS-FG | 2.00 | XeLL | 9 | 48.4–54.6 |  |
| xessfg | XeSS · RT Full · FG off (XeSS-FG loaded) · XeLL | XeSS (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing (XeLL's markers only) | 15 | 39.5–42.2 |  |
| xessfg | XeSS · RT Full · FG XeSS-FG · XeLL | XeSS (DLL) Quality, 1707x960 to 2560x1440 | XeSS-FG | 2.00 | XeLL | 9 | 34.3–34.9 |  |
| xessfg | FSR 3 · RT Full, opacity micromaps · FG off (XeSS-FG loaded) · XeLL | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing (XeLL's markers only) | 15 | 38.7–41.4 |  |
| xessfg | FSR 3 · RT Full, opacity micromaps · FG XeSS-FG · XeLL | FSR 3 Quality, 1707x960 to 2560x1440 | XeSS-FG | 2.00 | XeLL | 8 | 34.0–34.2 |  |

The proxy's configuration in each start, as the proxy logged it at load:

```
fsr: enabled=1 allowTearing=1 measureOnly=0 frameGeneration=1 frameGenerationTechnique=fsr rrPath=auto hudLessColour=1 fgDepthInverted=1 fgNegateMotionScale=0 fgFlipInputs=1 fgFlipHudLess=0 fgCameraBasis=1 fgVSync=0 fgHalfRefreshLimit=0 fgAsyncWorkloads=0 fgObjectSeparationMetres=0.000000 dlssgMode=on pacing=0.100000/0.100000 fgMinimumFps=1000 lowLatency=0 latencyBrake=0/4.000000/1 antiLag2=0 xell=1 xellLowLatencyBoost=0 vendorDiagnostics=0 streamlineImgui=0 fgShowOnlyGenerated=0 xessFgUiComposition=0 xessFgUiMode=auto xessFgFrames=1 xessFgSceneChangeThreshold=0.700000 xessFgTagInterpolatedFrames=0 xessFgPresentFailedInterpolation=0 xessFgOwnWaitable=auto xessFgGraceFrames=3 xessFgBadConstants=skip xessFgAllowOld=0 xessFgResetOnChange=0 xessFgResetDebounce=0 xellLimitLive=1 rtOwnQueue=0 rtCutoutTest=0 rtOpacityMicromaps=0 rtOmmMaxSubdivision=6 rtSunUnfiltered=0 rtSunAtrousSteps=0 rtSunHistory=4 rtFoliageTransmission=0.000000 rtRadianceCache=0 rtRadianceCacheEntries=20 rtRadianceCacheCellPixels=8.000000 rtRadianceCacheMinCell=0.125000 rtRadianceCacheSamples=256 rtRadianceCacheMaxAge=50 rtRadianceCacheDebug=0 rtBlasBatch=1 rtBlasPool=1 rtBlasBudget=0 rtBlasCompaction=2 rtBlasRefineBudget=65536 rtTlasFastTrace=0 rtMegaGeometry=0 reportSeconds=1 logInputsSeconds=0 dlssDirectory=(none) amdUpscalerDirectory=(set) xessDirectory=(set) streamlineDirectory=(none) xessFgDirectory=(none)
dlssg: enabled=1 allowTearing=1 measureOnly=0 frameGeneration=1 frameGenerationTechnique=dlss rrPath=auto hudLessColour=1 fgDepthInverted=1 fgNegateMotionScale=0 fgFlipInputs=1 fgFlipHudLess=0 fgCameraBasis=1 fgVSync=0 fgHalfRefreshLimit=0 fgAsyncWorkloads=0 fgObjectSeparationMetres=0.000000 dlssgMode=on pacing=0.100000/0.100000 fgMinimumFps=0 lowLatency=0 latencyBrake=0/4.000000/1 antiLag2=0 xell=1 xellLowLatencyBoost=0 vendorDiagnostics=0 streamlineImgui=0 fgShowOnlyGenerated=0 xessFgUiComposition=0 xessFgUiMode=auto xessFgFrames=1 xessFgSceneChangeThreshold=0.700000 xessFgTagInterpolatedFrames=0 xessFgPresentFailedInterpolation=0 xessFgOwnWaitable=auto xessFgGraceFrames=3 xessFgBadConstants=skip xessFgAllowOld=0 xessFgResetOnChange=0 xessFgResetDebounce=0 xellLimitLive=1 rtOwnQueue=0 rtCutoutTest=0 rtOpacityMicromaps=0 rtOmmMaxSubdivision=6 rtSunUnfiltered=0 rtSunAtrousSteps=0 rtSunHistory=4 rtFoliageTransmission=0.000000 rtRadianceCache=0 rtRadianceCacheEntries=20 rtRadianceCacheCellPixels=8.000000 rtRadianceCacheMinCell=0.125000 rtRadianceCacheSamples=256 rtRadianceCacheMaxAge=50 rtRadianceCacheDebug=0 rtBlasBatch=1 rtBlasPool=1 rtBlasBudget=0 rtBlasCompaction=2 rtBlasRefineBudget=65536 rtTlasFastTrace=0 rtMegaGeometry=0 reportSeconds=1 logInputsSeconds=0 dlssDirectory=(none) amdUpscalerDirectory=(set) xessDirectory=(set) streamlineDirectory=(none) xessFgDirectory=(none)
dlssg-c: enabled=1 allowTearing=1 measureOnly=0 frameGeneration=1 frameGenerationTechnique=dlss rrPath=auto hudLessColour=1 fgDepthInverted=1 fgNegateMotionScale=0 fgFlipInputs=1 fgFlipHudLess=0 fgCameraBasis=1 fgVSync=0 fgHalfRefreshLimit=0 fgAsyncWorkloads=0 fgObjectSeparationMetres=0.000000 dlssgMode=on pacing=0.100000/0.100000 fgMinimumFps=0 lowLatency=0 latencyBrake=0/4.000000/1 antiLag2=0 xell=1 xellLowLatencyBoost=0 vendorDiagnostics=0 streamlineImgui=0 fgShowOnlyGenerated=0 xessFgUiComposition=0 xessFgUiMode=auto xessFgFrames=1 xessFgSceneChangeThreshold=0.700000 xessFgTagInterpolatedFrames=0 xessFgPresentFailedInterpolation=0 xessFgOwnWaitable=auto xessFgGraceFrames=3 xessFgBadConstants=skip xessFgAllowOld=0 xessFgResetOnChange=0 xessFgResetDebounce=0 xellLimitLive=1 rtOwnQueue=0 rtCutoutTest=0 rtOpacityMicromaps=0 rtOmmMaxSubdivision=6 rtSunUnfiltered=0 rtSunAtrousSteps=0 rtSunHistory=4 rtFoliageTransmission=0.000000 rtRadianceCache=0 rtRadianceCacheEntries=20 rtRadianceCacheCellPixels=8.000000 rtRadianceCacheMinCell=0.125000 rtRadianceCacheSamples=256 rtRadianceCacheMaxAge=50 rtRadianceCacheDebug=0 rtBlasBatch=1 rtBlasPool=1 rtBlasBudget=0 rtBlasCompaction=2 rtBlasRefineBudget=65536 rtTlasFastTrace=0 rtMegaGeometry=0 reportSeconds=1 logInputsSeconds=0 dlssDirectory=(none) amdUpscalerDirectory=(none) xessDirectory=(none) streamlineDirectory=(none) xessFgDirectory=(none)
xessfg: enabled=1 allowTearing=1 measureOnly=0 frameGeneration=1 frameGenerationTechnique=xess rrPath=auto hudLessColour=1 fgDepthInverted=1 fgNegateMotionScale=0 fgFlipInputs=1 fgFlipHudLess=0 fgCameraBasis=1 fgVSync=0 fgHalfRefreshLimit=0 fgAsyncWorkloads=0 fgObjectSeparationMetres=0.000000 dlssgMode=on pacing=0.100000/0.100000 fgMinimumFps=1000 lowLatency=1 latencyBrake=0/4.000000/1 antiLag2=0 xell=1 xellLowLatencyBoost=0 vendorDiagnostics=0 streamlineImgui=0 fgShowOnlyGenerated=0 xessFgUiComposition=0 xessFgUiMode=auto xessFgFrames=1 xessFgSceneChangeThreshold=0.700000 xessFgTagInterpolatedFrames=0 xessFgPresentFailedInterpolation=0 xessFgOwnWaitable=auto xessFgGraceFrames=3 xessFgBadConstants=skip xessFgAllowOld=0 xessFgResetOnChange=0 xessFgResetDebounce=0 xellLimitLive=1 rtOwnQueue=0 rtCutoutTest=0 rtOpacityMicromaps=0 rtOmmMaxSubdivision=6 rtSunUnfiltered=0 rtSunAtrousSteps=0 rtSunHistory=4 rtFoliageTransmission=0.000000 rtRadianceCache=0 rtRadianceCacheEntries=20 rtRadianceCacheCellPixels=8.000000 rtRadianceCacheMinCell=0.125000 rtRadianceCacheSamples=256 rtRadianceCacheMaxAge=50 rtRadianceCacheDebug=0 rtBlasBatch=1 rtBlasPool=1 rtBlasBudget=0 rtBlasCompaction=2 rtBlasRefineBudget=65536 rtTlasFastTrace=0 rtMegaGeometry=0 reportSeconds=1 logInputsSeconds=0 dlssDirectory=(none) amdUpscalerDirectory=(set) xessDirectory=(set) streamlineDirectory=(none) xessFgDirectory=(set)
```

The raytracing's frames that built 50 meshes or more, at the flight's start:

* fsr: 10 frames, 1309 meshes, 514301 triangles; the slowest 0.62 ms of the GPU,
  6.35 ms of the CPU
* dlssg: 10 frames, 1321 meshes, 556686 triangles; the slowest 0.64 ms of the GPU,
  2.89 ms of the CPU
* dlssg-c: 5 frames, 623 meshes, 266707 triangles; the slowest 0.61 ms of the GPU,
  5.36 ms of the CPU
* xessfg: 10 frames, 1245 meshes, 496559 triangles; the slowest 0.64 ms of the GPU,
  3.66 ms of the CPU

</details>

### Run 2026-10-20-2334

| What | Value |
|---|---|
| Date | 2026-10-20T23:34:49 to 2026-10-20T23:44:21 |
| Commit | `f2c569e` with local changes on `feat/raytracing` |
| Build in the install | `dxgi.dll` 0dc39d0d30890ddc (2026-10-20T22:39:26), `GameData/ReDefinition/Plugins/ReDefinition.dll` e9d02b228931a8bb (2026-10-20T22:46:54) |
| Machine | NVIDIA RTX A4500, driver 597.16, AMD Ryzen 5 5600X 6-Core Processor, 31.9 GB, Microsoft Windows 11 Pro 26300, screen 2560x1440 rendered at 1707x960, 120 Hz |
| Profile | `medium`: [LOG 23:35:52.187] [ReDefinition] Probe: profile medium applied. |
| Groups | `main` |
| Matrix | `tools/benchmark_matrix.json`, 1 starts, 2 s settling per phase, 8 s after a change of the rendering |
| Skipped | latency antilag2 (only on AMD) |
| Full record | [benchmarks/2026-10-20-2334.json](benchmarks/2026-10-20-2334.json) |

<details><summary>What ran in each phase</summary>

| Start | Scenario | Upscaler that ran | Frame generation that ran | presented/rendered | Lowered by | Reports | fps range | Notes |
|---|---|---|---|---|---|---|---|---|
| xessfg | FSR 3 · RT Off · FG off (XeSS-FG loaded) · XeLL | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing (XeLL's markers only) | 9 | 70.3–72.4 |  |
| xessfg | FSR 3 · RT Off · FG XeSS-FG · XeLL | FSR 3 Quality, 1707x960 to 2560x1440 | XeSS-FG | 2.00 | XeLL | 9 | 59.2–60.4 |  |
| xessfg | FSR 3 · RT Full · FG off (XeSS-FG loaded) · XeLL | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing (XeLL's markers only) | 15 | 42.5–45.6 |  |
| xessfg | FSR 3 · RT Full · FG XeSS-FG · XeLL | FSR 3 Quality, 1707x960 to 2560x1440 | XeSS-FG | 2.00 | XeLL | 9 | 36.6–36.9 |  |
| xessfg | FSR 3 · RT Full, radiance cache · FG off (XeSS-FG loaded) · XeLL | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing (XeLL's markers only) | 15 | 42.7–45.5 |  |
| xessfg | FSR 3 · RT Full, radiance cache · FG XeSS-FG · XeLL | FSR 3 Quality, 1707x960 to 2560x1440 | XeSS-FG | 2.00 | XeLL | 9 | 36.6–37.2 |  |
| xessfg | DLSS · RT Off · FG off (XeSS-FG loaded) · XeLL | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing (XeLL's markers only) | 14 | 63.6–71.7 |  |
| xessfg | DLSS · RT Off · FG XeSS-FG · XeLL | DLSS Quality, 1707x960 to 2560x1440 | XeSS-FG | 2.00 | XeLL | 9 | 54.8–55.4 |  |
| xessfg | DLSS · RT Full, RR on · FG off (XeSS-FG loaded) · XeLL | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing (XeLL's markers only) | 14 | 33.5–35.5 |  |
| xessfg | DLSS · RT Full, RR on · FG XeSS-FG · XeLL | DLSS Quality, 1707x960 to 2560x1440 | XeSS-FG | 2.00 | XeLL | 9 | 29.5–29.8 |  |
| xessfg | DLSS · RT Full, RR off · FG off (XeSS-FG loaded) · XeLL | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing (XeLL's markers only) | 15 | 40.1–42.7 |  |
| xessfg | DLSS · RT Full, RR off · FG XeSS-FG · XeLL | DLSS Quality, 1707x960 to 2560x1440 | XeSS-FG | 2.00 | XeLL | 9 | 34.7–35.1 |  |
| xessfg | AMD DLL · RT Off · FG off (XeSS-FG loaded) · XeLL | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing (XeLL's markers only) | 15 | 63.9–71.4 |  |
| xessfg | AMD DLL · RT Off · FG XeSS-FG · XeLL | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | XeSS-FG | 2.00 | XeLL | 9 | 55.5–56.1 |  |
| xessfg | AMD DLL · RT Full · FG off (XeSS-FG loaded) · XeLL | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing (XeLL's markers only) | 15 | 40.5–43.1 |  |
| xessfg | AMD DLL · RT Full · FG XeSS-FG · XeLL | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | XeSS-FG | 2.00 | XeLL | 9 | 35.1–35.6 |  |
| xessfg | XeSS · RT Off · FG off (XeSS-FG loaded) · XeLL | XeSS (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing (XeLL's markers only) | 15 | 65.0–71.0 |  |
| xessfg | XeSS · RT Off · FG XeSS-FG · XeLL | XeSS (DLL) Quality, 1707x960 to 2560x1440 | XeSS-FG | 2.00 | XeLL | 9 | 51.3–54.2 |  |
| xessfg | XeSS · RT Full · FG off (XeSS-FG loaded) · XeLL | XeSS (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing (XeLL's markers only) | 15 | 39.5–42.0 |  |
| xessfg | XeSS · RT Full · FG XeSS-FG · XeLL | XeSS (DLL) Quality, 1707x960 to 2560x1440 | XeSS-FG | 2.00 | XeLL | 9 | 34.5–34.7 |  |
| xessfg | FSR 3 · RT Full, opacity micromaps · FG off (XeSS-FG loaded) · XeLL | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing (XeLL's markers only) | 14 | 38.5–41.6 |  |
| xessfg | FSR 3 · RT Full, opacity micromaps · FG XeSS-FG · XeLL | FSR 3 Quality, 1707x960 to 2560x1440 | XeSS-FG | 2.00 | XeLL | 8 | 33.8–34.0 |  |

The proxy's configuration in each start, as the proxy logged it at load:

```
xessfg: enabled=1 allowTearing=1 measureOnly=0 frameGeneration=1 frameGenerationTechnique=xess rrPath=auto hudLessColour=1 fgDepthInverted=1 fgNegateMotionScale=0 fgFlipInputs=1 fgFlipHudLess=0 fgCameraBasis=1 fgVSync=0 fgHalfRefreshLimit=0 fgAsyncWorkloads=0 fgObjectSeparationMetres=0.000000 dlssgMode=on pacing=0.100000/0.100000 fgMinimumFps=1000 lowLatency=1 latencyBrake=0/4.000000/1 antiLag2=0 xell=1 xellLowLatencyBoost=0 vendorDiagnostics=0 streamlineImgui=0 fgShowOnlyGenerated=0 xessFgUiComposition=0 xessFgUiMode=auto xessFgFrames=1 xessFgSceneChangeThreshold=0.700000 xessFgTagInterpolatedFrames=0 xessFgPresentFailedInterpolation=0 xessFgOwnWaitable=1 xessFgGraceFrames=3 xessFgBadConstants=skip xessFgAllowOld=0 xessFgResetOnChange=0 xessFgResetDebounce=0 xellLimitLive=1 rtOwnQueue=0 rtCutoutTest=0 rtOpacityMicromaps=0 rtOmmMaxSubdivision=6 rtSunUnfiltered=0 rtSunAtrousSteps=0 rtSunHistory=4 rtFoliageTransmission=0.000000 rtRadianceCache=0 rtRadianceCacheEntries=20 rtRadianceCacheCellPixels=8.000000 rtRadianceCacheMinCell=0.125000 rtRadianceCacheSamples=256 rtRadianceCacheMaxAge=50 rtRadianceCacheDebug=0 rtBlasBatch=1 rtBlasPool=1 rtBlasBudget=0 rtBlasCompaction=2 rtBlasRefineBudget=65536 rtTlasFastTrace=0 rtMegaGeometry=0 reportSeconds=1 logInputsSeconds=0 dlssDirectory=(none) amdUpscalerDirectory=(set) xessDirectory=(set) streamlineDirectory=(none) xessFgDirectory=(set)
```

The raytracing's frames that built 50 meshes or more, at the flight's start:

* xessfg: 10 frames, 1308 meshes, 552862 triangles; the slowest 2.98 ms of the GPU,
  7.30 ms of the CPU

</details>

### Run 2026-10-20-2247

| What | Value |
|---|---|
| Date | 2026-10-20T22:47:05 to 2026-10-20T23:33:55 |
| Commit | `a0913cb` on `feat/raytracing` |
| Build in the install | `dxgi.dll` 0dc39d0d30890ddc (2026-10-20T22:39:26), `GameData/ReDefinition/Plugins/ReDefinition.dll` e9d02b228931a8bb (2026-10-20T22:46:54) |
| Machine | NVIDIA RTX A4500, driver 597.16, AMD Ryzen 5 5600X 6-Core Processor, 31.9 GB, Microsoft Windows 11 Pro 26300, screen 2560x1440 rendered at 1707x960, 120 Hz |
| Profile | `medium`: [LOG 22:48:43.839] [ReDefinition] Probe: profile medium applied. |
| Groups | `main` |
| Matrix | `tools/benchmark_matrix.json`, 4 starts, 2 s settling per phase, 8 s after a change of the rendering |
| Skipped | latency antilag2 (only on AMD) |
| Full record | [benchmarks/2026-10-20-2247.json](benchmarks/2026-10-20-2247.json) |

<details><summary>What ran in each phase</summary>

| Start | Scenario | Upscaler that ran | Frame generation that ran | presented/rendered | Lowered by | Reports | fps range | Notes |
|---|---|---|---|---|---|---|---|---|
| fsr | FSR 3 · RT Off · FG off · none | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 9 | 70.7–71.7 |  |
| fsr | FSR 3 · RT Off · FG off · Reflex | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex | 9 | 63.3–71.9 |  |
| fsr | FSR 3 · RT Off · FG off · brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | the brake | 9 | 67.3–71.6 |  |
| fsr | FSR 3 · RT Off · FG off · Reflex + brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex and the brake | 8 | 69.9–71.7 |  |
| fsr | FSR 3 · RT Off · FG FSR · none | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 8 | 60.0–61.7 |  |
| fsr | FSR 3 · RT Off · FG FSR · Reflex | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex | 8 | 60.5–61.8 |  |
| fsr | FSR 3 · RT Off · FG FSR · brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | the brake | 8 | 60.6–61.8 |  |
| fsr | FSR 3 · RT Off · FG FSR · Reflex + brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex and the brake | 8 | 58.9–61.3 |  |
| fsr | FSR 3 · RT Full · FG off · none | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 10 | 44.3–44.4 | no frame time report inside the phase |
| fsr | FSR 3 · RT Full · FG off · Reflex | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex | 9 | 44.1–44.3 |  |
| fsr | FSR 3 · RT Full · FG off · brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | the brake | 9 | 43.9–44.5 |  |
| fsr | FSR 3 · RT Full · FG off · Reflex + brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex and the brake | 9 | 43.8–44.3 |  |
| fsr | FSR 3 · RT Full · FG FSR · none | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 9 | 37.1–37.3 |  |
| fsr | FSR 3 · RT Full · FG FSR · Reflex | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex | 8 | 37.2–37.3 |  |
| fsr | FSR 3 · RT Full · FG FSR · brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | the brake | 8 | 37.2–37.5 |  |
| fsr | FSR 3 · RT Full · FG FSR · Reflex + brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex and the brake | 8 | 36.9–37.6 |  |
| fsr | FSR 3 · RT Full, radiance cache · FG off · none | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 14 | 42.1–44.7 |  |
| fsr | FSR 3 · RT Full, radiance cache · FG FSR · none | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 9 | 37.3–37.8 |  |
| fsr | DLSS · RT Off · FG off · none | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 14 | 57.4–70.1 |  |
| fsr | DLSS · RT Off · FG off · Reflex | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex | 9 | 66.8–69.9 |  |
| fsr | DLSS · RT Off · FG off · brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | the brake | 9 | 68.7–70.2 |  |
| fsr | DLSS · RT Off · FG off · Reflex + brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex and the brake | 9 | 65.7–70.0 |  |
| fsr | DLSS · RT Off · FG FSR · none | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 9 | 56.6–57.7 |  |
| fsr | DLSS · RT Off · FG FSR · Reflex | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex | 9 | 56.6–57.6 |  |
| fsr | DLSS · RT Off · FG FSR · brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | the brake | 9 | 56.3–57.7 |  |
| fsr | DLSS · RT Off · FG FSR · Reflex + brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex and the brake | 9 | 55.9–57.7 |  |
| fsr | DLSS · RT Full, RR on · FG off · none | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 15 | 33.4–35.4 |  |
| fsr | DLSS · RT Full, RR on · FG off · Reflex | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex | 9 | 35.3–35.4 |  |
| fsr | DLSS · RT Full, RR on · FG off · brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | the brake | 9 | 34.8–35.4 |  |
| fsr | DLSS · RT Full, RR on · FG off · Reflex + brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex and the brake | 9 | 35.3–35.4 |  |
| fsr | DLSS · RT Full, RR on · FG FSR · none | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 9 | 30.6–30.7 |  |
| fsr | DLSS · RT Full, RR on · FG FSR · Reflex | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex | 9 | 30.6–30.7 |  |
| fsr | DLSS · RT Full, RR on · FG FSR · brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | the brake | 9 | 30.6–30.7 |  |
| fsr | DLSS · RT Full, RR on · FG FSR · Reflex + brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex and the brake | 9 | 30.6–30.7 |  |
| fsr | DLSS · RT Full, RR off · FG off · none | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 15 | 40.2–42.8 |  |
| fsr | DLSS · RT Full, RR off · FG off · Reflex | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex | 9 | 42.5–42.7 |  |
| fsr | DLSS · RT Full, RR off · FG off · brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | the brake | 8 | 42.4–42.8 |  |
| fsr | DLSS · RT Full, RR off · FG off · Reflex + brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex and the brake | 8 | 41.9–42.7 |  |
| fsr | DLSS · RT Full, RR off · FG FSR · none | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 8 | 36.0–36.1 |  |
| fsr | DLSS · RT Full, RR off · FG FSR · Reflex | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex | 8 | 36.0–36.1 |  |
| fsr | DLSS · RT Full, RR off · FG FSR · brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | the brake | 9 | 35.9–36.2 |  |
| fsr | DLSS · RT Full, RR off · FG FSR · Reflex + brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex and the brake | 8 | 36.0–36.2 |  |
| fsr | AMD DLL · RT Off · FG off · none | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 14 | 63.7–70.1 |  |
| fsr | AMD DLL · RT Off · FG off · Reflex | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex | 8 | 69.6–70.3 |  |
| fsr | AMD DLL · RT Off · FG off · brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | the brake | 8 | 68.9–70.1 |  |
| fsr | AMD DLL · RT Off · FG off · Reflex + brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex and the brake | 8 | 64.3–70.5 |  |
| fsr | AMD DLL · RT Off · FG FSR · none | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 8 | 57.9–58.4 |  |
| fsr | AMD DLL · RT Off · FG FSR · Reflex | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex | 9 | 56.9–58.7 |  |
| fsr | AMD DLL · RT Off · FG FSR · brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | the brake | 8 | 56.0–58.8 |  |
| fsr | AMD DLL · RT Off · FG FSR · Reflex + brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex and the brake | 8 | 57.6–58.5 |  |
| fsr | AMD DLL · RT Full · FG off · none | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 14 | 40.1–42.6 |  |
| fsr | AMD DLL · RT Full · FG off · Reflex | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex | 8 | 42.5–42.6 |  |
| fsr | AMD DLL · RT Full · FG off · brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | the brake | 8 | 42.2–42.7 |  |
| fsr | AMD DLL · RT Full · FG off · Reflex + brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex and the brake | 8 | 42.3–42.9 |  |
| fsr | AMD DLL · RT Full · FG FSR · none | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 8 | 36.0–36.1 |  |
| fsr | AMD DLL · RT Full · FG FSR · Reflex | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex | 8 | 36.0–36.1 |  |
| fsr | AMD DLL · RT Full · FG FSR · brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | the brake | 9 | 36.0–36.2 |  |
| fsr | AMD DLL · RT Full · FG FSR · Reflex + brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex and the brake | 9 | 35.6–36.2 |  |
| fsr | XeSS · RT Off · FG off · none | XeSS (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 15 | 64.4–69.9 |  |
| fsr | XeSS · RT Off · FG off · Reflex | XeSS (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex | 9 | 65.4–69.5 |  |
| fsr | XeSS · RT Off · FG off · brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | the brake | 9 | 67.8–70.0 |  |
| fsr | XeSS · RT Off · FG off · Reflex + brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex and the brake | 8 | 68.4–69.9 |  |
| fsr | XeSS · RT Off · FG FSR · none | XeSS (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 8 | 55.3–56.6 |  |
| fsr | XeSS · RT Off · FG FSR · Reflex | XeSS (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex | 8 | 56.0–56.5 |  |
| fsr | XeSS · RT Off · FG FSR · brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | the brake | 9 | 55.4–56.9 |  |
| fsr | XeSS · RT Off · FG FSR · Reflex + brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex and the brake | 9 | 54.4–56.9 |  |
| fsr | XeSS · RT Full · FG off · none | XeSS (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 14 | 39.8–41.3 |  |
| fsr | XeSS · RT Full · FG off · Reflex | XeSS (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex | 8 | 40.9–41.2 |  |
| fsr | XeSS · RT Full · FG off · brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | the brake | 8 | 40.9–41.5 |  |
| fsr | XeSS · RT Full · FG off · Reflex + brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex and the brake | 8 | 40.7–41.3 |  |
| fsr | XeSS · RT Full · FG FSR · none | XeSS (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 9 | 34.8–35.1 |  |
| fsr | XeSS · RT Full · FG FSR · Reflex | XeSS (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex | 9 | 34.9–35.1 |  |
| fsr | XeSS · RT Full · FG FSR · brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | the brake | 9 | 34.8–35.1 |  |
| fsr | XeSS · RT Full · FG FSR · Reflex + brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex and the brake | 9 | 34.8–35.1 |  |
| fsr | FSR 3 · RT Full, opacity micromaps · FG off · none | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 14 | 37.7–40.0 |  |
| fsr | FSR 3 · RT Full, opacity micromaps · FG FSR · none | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 8 | 34.0–34.1 |  |
| dlssg | FSR 3 · RT Off · FG DLSS-G · none | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 9 | 65.8–66.5 |  |
| dlssg | FSR 3 · RT Off · FG DLSS-G · Reflex | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 64.2–66.2 |  |
| dlssg | FSR 3 · RT Off · FG DLSS-G · brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 65.8–66.3 |  |
| dlssg | FSR 3 · RT Off · FG DLSS-G · Reflex + brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 64.8–66.2 |  |
| dlssg | FSR 3 · RT Full · FG DLSS-G · none | FSR 3 Quality, 1707x960 to 2560x1440 | none | 2.00 | Streamline's Reflex | 10 | 39.9–40.8 | no frame time report inside the phase |
| dlssg | FSR 3 · RT Full · FG DLSS-G · Reflex | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 9 | 40.5–40.6 |  |
| dlssg | FSR 3 · RT Full · FG DLSS-G · brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 9 | 40.4–40.6 |  |
| dlssg | FSR 3 · RT Full · FG DLSS-G · Reflex + brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 9 | 40.3–40.5 |  |
| dlssg | FSR 3 · RT Full, radiance cache · FG DLSS-G · none | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 15 | 37.2–40.3 |  |
| dlssg | DLSS · RT Off · FG DLSS-G · none | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 14 | 59.0–61.5 |  |
| dlssg | DLSS · RT Off · FG DLSS-G · Reflex | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 61.0–61.4 |  |
| dlssg | DLSS · RT Off · FG DLSS-G · brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 59.9–61.4 |  |
| dlssg | DLSS · RT Off · FG DLSS-G · Reflex + brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 59.9–61.3 |  |
| dlssg | DLSS · RT Full, RR on · FG DLSS-G · none | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 14 | 29.8–32.2 |  |
| dlssg | DLSS · RT Full, RR on · FG DLSS-G · Reflex | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 9 | 32.2–32.3 |  |
| dlssg | DLSS · RT Full, RR on · FG DLSS-G · brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 9 | 32.2–32.3 |  |
| dlssg | DLSS · RT Full, RR on · FG DLSS-G · Reflex + brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 9 | 32.2–32.3 |  |
| dlssg | DLSS · RT Full, RR off · FG DLSS-G · none | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 15 | 35.9–38.6 |  |
| dlssg | DLSS · RT Full, RR off · FG DLSS-G · Reflex | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 9 | 38.5–38.6 |  |
| dlssg | DLSS · RT Full, RR off · FG DLSS-G · brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 9 | 38.5–38.6 |  |
| dlssg | DLSS · RT Full, RR off · FG DLSS-G · Reflex + brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 9 | 38.5–38.6 |  |
| dlssg | AMD DLL · RT Off · FG DLSS-G · none | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 15 | 58.2–62.8 |  |
| dlssg | AMD DLL · RT Off · FG DLSS-G · Reflex | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 62.4–62.7 |  |
| dlssg | AMD DLL · RT Off · FG DLSS-G · brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 9 | 62.3–62.7 |  |
| dlssg | AMD DLL · RT Off · FG DLSS-G · Reflex + brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 9 | 62.4–62.7 |  |
| dlssg | AMD DLL · RT Full · FG DLSS-G · none | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 15 | 36.1–39.0 |  |
| dlssg | AMD DLL · RT Full · FG DLSS-G · Reflex | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 9 | 38.7–39.0 |  |
| dlssg | AMD DLL · RT Full · FG DLSS-G · brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 9 | 38.8–38.9 |  |
| dlssg | AMD DLL · RT Full · FG DLSS-G · Reflex + brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 9 | 38.9–38.9 |  |
| dlssg | XeSS · RT Off · FG DLSS-G · none | XeSS (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 15 | 56.6–60.6 |  |
| dlssg | XeSS · RT Off · FG DLSS-G · Reflex | XeSS (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 9 | 59.7–60.7 |  |
| dlssg | XeSS · RT Off · FG DLSS-G · brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 9 | 60.1–60.6 |  |
| dlssg | XeSS · RT Off · FG DLSS-G · Reflex + brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 9 | 59.3–60.7 |  |
| dlssg | XeSS · RT Full · FG DLSS-G · none | XeSS (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 14 | 35.3–38.1 |  |
| dlssg | XeSS · RT Full · FG DLSS-G · Reflex | XeSS (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 38.0–38.0 |  |
| dlssg | XeSS · RT Full · FG DLSS-G · brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 37.3–38.0 |  |
| dlssg | XeSS · RT Full · FG DLSS-G · Reflex + brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 9 | 37.9–38.0 |  |
| dlssg | FSR 3 · RT Full, opacity micromaps · FG DLSS-G · none | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 14 | 34.5–37.4 |  |
| dlssg-c | FSR 3 · RT Off · FG DLSS-G preset C · none | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | Streamline's Reflex | 8 | 68.3–71.6 | frame generation asked for, presented/rendered 1.00 |
| dlssg-c | FSR 3 · RT Full · FG DLSS-G preset C · none | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | Streamline's Reflex | 14 | 42.0–45.2 | frame generation asked for, presented/rendered 1.00 |
| xessfg | – | – | – | – | – | – | – | no logs: the start did not run |

The proxy's configuration in each start, as the proxy logged it at load:

```
fsr: enabled=1 allowTearing=1 measureOnly=0 frameGeneration=1 frameGenerationTechnique=fsr rrPath=auto hudLessColour=1 fgDepthInverted=1 fgNegateMotionScale=0 fgFlipInputs=1 fgFlipHudLess=0 fgCameraBasis=1 fgVSync=0 fgHalfRefreshLimit=0 fgAsyncWorkloads=0 fgObjectSeparationMetres=0.000000 dlssgMode=on pacing=0.100000/0.100000 fgMinimumFps=1000 lowLatency=0 latencyBrake=0/4.000000/1 antiLag2=0 xell=1 xellLowLatencyBoost=0 vendorDiagnostics=0 streamlineImgui=0 fgShowOnlyGenerated=0 xessFgUiComposition=0 xessFgUiMode=auto xessFgFrames=1 xessFgSceneChangeThreshold=0.700000 xessFgTagInterpolatedFrames=0 xessFgPresentFailedInterpolation=0 xessFgOwnWaitable=auto xessFgGraceFrames=3 xessFgBadConstants=skip xessFgAllowOld=0 xessFgResetOnChange=0 xessFgResetDebounce=0 xellLimitLive=1 rtOwnQueue=0 rtCutoutTest=0 rtOpacityMicromaps=0 rtOmmMaxSubdivision=6 rtSunUnfiltered=0 rtSunAtrousSteps=0 rtSunHistory=4 rtFoliageTransmission=0.000000 rtRadianceCache=0 rtRadianceCacheEntries=20 rtRadianceCacheCellPixels=8.000000 rtRadianceCacheMinCell=0.125000 rtRadianceCacheSamples=256 rtRadianceCacheMaxAge=50 rtRadianceCacheDebug=0 rtBlasBatch=1 rtBlasPool=1 rtBlasBudget=0 rtBlasCompaction=2 rtBlasRefineBudget=65536 rtTlasFastTrace=0 rtMegaGeometry=0 reportSeconds=1 logInputsSeconds=0 dlssDirectory=(none) amdUpscalerDirectory=(set) xessDirectory=(set) streamlineDirectory=(none) xessFgDirectory=(none)
dlssg: enabled=1 allowTearing=1 measureOnly=0 frameGeneration=1 frameGenerationTechnique=dlss rrPath=auto hudLessColour=1 fgDepthInverted=1 fgNegateMotionScale=0 fgFlipInputs=1 fgFlipHudLess=0 fgCameraBasis=1 fgVSync=0 fgHalfRefreshLimit=0 fgAsyncWorkloads=0 fgObjectSeparationMetres=0.000000 dlssgMode=on pacing=0.100000/0.100000 fgMinimumFps=0 lowLatency=0 latencyBrake=0/4.000000/1 antiLag2=0 xell=1 xellLowLatencyBoost=0 vendorDiagnostics=0 streamlineImgui=0 fgShowOnlyGenerated=0 xessFgUiComposition=0 xessFgUiMode=auto xessFgFrames=1 xessFgSceneChangeThreshold=0.700000 xessFgTagInterpolatedFrames=0 xessFgPresentFailedInterpolation=0 xessFgOwnWaitable=auto xessFgGraceFrames=3 xessFgBadConstants=skip xessFgAllowOld=0 xessFgResetOnChange=0 xessFgResetDebounce=0 xellLimitLive=1 rtOwnQueue=0 rtCutoutTest=0 rtOpacityMicromaps=0 rtOmmMaxSubdivision=6 rtSunUnfiltered=0 rtSunAtrousSteps=0 rtSunHistory=4 rtFoliageTransmission=0.000000 rtRadianceCache=0 rtRadianceCacheEntries=20 rtRadianceCacheCellPixels=8.000000 rtRadianceCacheMinCell=0.125000 rtRadianceCacheSamples=256 rtRadianceCacheMaxAge=50 rtRadianceCacheDebug=0 rtBlasBatch=1 rtBlasPool=1 rtBlasBudget=0 rtBlasCompaction=2 rtBlasRefineBudget=65536 rtTlasFastTrace=0 rtMegaGeometry=0 reportSeconds=1 logInputsSeconds=0 dlssDirectory=(none) amdUpscalerDirectory=(set) xessDirectory=(set) streamlineDirectory=(none) xessFgDirectory=(none)
dlssg-c: enabled=1 allowTearing=1 measureOnly=0 frameGeneration=1 frameGenerationTechnique=dlss rrPath=auto hudLessColour=1 fgDepthInverted=1 fgNegateMotionScale=0 fgFlipInputs=1 fgFlipHudLess=0 fgCameraBasis=1 fgVSync=0 fgHalfRefreshLimit=0 fgAsyncWorkloads=0 fgObjectSeparationMetres=0.000000 dlssgMode=on pacing=0.100000/0.100000 fgMinimumFps=0 lowLatency=0 latencyBrake=0/4.000000/1 antiLag2=0 xell=1 xellLowLatencyBoost=0 vendorDiagnostics=0 streamlineImgui=0 fgShowOnlyGenerated=0 xessFgUiComposition=0 xessFgUiMode=auto xessFgFrames=1 xessFgSceneChangeThreshold=0.700000 xessFgTagInterpolatedFrames=0 xessFgPresentFailedInterpolation=0 xessFgOwnWaitable=auto xessFgGraceFrames=3 xessFgBadConstants=skip xessFgAllowOld=0 xessFgResetOnChange=0 xessFgResetDebounce=0 xellLimitLive=1 rtOwnQueue=0 rtCutoutTest=0 rtOpacityMicromaps=0 rtOmmMaxSubdivision=6 rtSunUnfiltered=0 rtSunAtrousSteps=0 rtSunHistory=4 rtFoliageTransmission=0.000000 rtRadianceCache=0 rtRadianceCacheEntries=20 rtRadianceCacheCellPixels=8.000000 rtRadianceCacheMinCell=0.125000 rtRadianceCacheSamples=256 rtRadianceCacheMaxAge=50 rtRadianceCacheDebug=0 rtBlasBatch=1 rtBlasPool=1 rtBlasBudget=0 rtBlasCompaction=2 rtBlasRefineBudget=65536 rtTlasFastTrace=0 rtMegaGeometry=0 reportSeconds=1 logInputsSeconds=0 dlssDirectory=(none) amdUpscalerDirectory=(none) xessDirectory=(none) streamlineDirectory=(none) xessFgDirectory=(none)
xessfg: no Config line
```

The raytracing's frames that built 50 meshes or more, at the flight's start:

* fsr: 10 frames, 1430 meshes, 516162 triangles; the slowest 3.09 ms of the GPU,
  8.65 ms of the CPU
* dlssg: 10 frames, 1326 meshes, 528768 triangles; the slowest 3.09 ms of the GPU,
  4.41 ms of the CPU
* dlssg-c: 5 frames, 642 meshes, 274748 triangles; the slowest 0.61 ms of the GPU,
  2.70 ms of the CPU

</details>

### Run 2026-10-20-1750

| What | Value |
|---|---|
| Date | 2026-10-20T17:50:03 to 2026-10-20T18:20:06 |
| Commit | `13027f0` on `feat/raytracing` |
| Build in the install | `dxgi.dll` 66f2b347196d25b8 (2026-10-20T17:46:58), `GameData/ReDefinition/Plugins/ReDefinition.dll` 0af52ec7ce941ba0 (2026-10-20T17:47:03) |
| Machine | NVIDIA RTX A4500, driver 597.16, AMD Ryzen 5 5600X 6-Core Processor, 31.9 GB, Microsoft Windows 11 Pro 26300, screen 2560x1440 rendered at 1707x960, 120 Hz |
| Profile | `medium`: [LOG 17:51:05.741] [ReDefinition] Probe: profile medium applied. |
| Groups | `main` |
| Matrix | `tools/benchmark_matrix.json`, 2 starts, 2 s settling per phase, 8 s after a change of the rendering |
| Skipped | latency antilag2 (only on AMD) |
| Full record | [benchmarks/2026-10-20-1750.json](benchmarks/2026-10-20-1750.json) |

<details><summary>What ran in each phase</summary>

| Start | Scenario | Upscaler that ran | Frame generation that ran | presented/rendered | Lowered by | Reports | fps range | Notes |
|---|---|---|---|---|---|---|---|---|
| fsr | FSR 3 · RT Off · FG off · none | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 9 | 68.0–72.2 |  |
| fsr | FSR 3 · RT Off · FG off · Reflex | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex | 8 | 69.2–72.4 |  |
| fsr | FSR 3 · RT Off · FG off · brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | the brake | 8 | 71.1–72.4 |  |
| fsr | FSR 3 · RT Off · FG off · Reflex + brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex and the brake | 8 | 66.9–71.9 |  |
| fsr | FSR 3 · RT Off · FG FSR · none | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 8 | 60.3–61.7 |  |
| fsr | FSR 3 · RT Off · FG FSR · Reflex | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex | 8 | 59.7–61.6 |  |
| fsr | FSR 3 · RT Off · FG FSR · brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | the brake | 8 | 57.9–61.5 |  |
| fsr | FSR 3 · RT Off · FG FSR · Reflex + brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex and the brake | 8 | 56.2–61.2 |  |
| fsr | FSR 3 · RT Full · FG off · none | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 15 | 41.5–43.9 |  |
| fsr | FSR 3 · RT Full · FG off · Reflex | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex | 9 | 43.3–44.0 |  |
| fsr | FSR 3 · RT Full · FG off · brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | the brake | 9 | 43.8–44.1 |  |
| fsr | FSR 3 · RT Full · FG off · Reflex + brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex and the brake | 9 | 43.8–44.1 |  |
| fsr | FSR 3 · RT Full · FG FSR · none | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 9 | 37.0–37.2 |  |
| fsr | FSR 3 · RT Full · FG FSR · Reflex | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex | 8 | 37.0–37.1 |  |
| fsr | FSR 3 · RT Full · FG FSR · brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | the brake | 8 | 36.8–37.4 |  |
| fsr | FSR 3 · RT Full · FG FSR · Reflex + brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex and the brake | 8 | 36.9–37.5 |  |
| fsr | DLSS · RT Off · FG off · none | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 14 | 63.9–71.4 |  |
| fsr | DLSS · RT Off · FG off · Reflex | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex | 9 | 67.0–71.2 |  |
| fsr | DLSS · RT Off · FG off · brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | the brake | 9 | 65.8–71.1 |  |
| fsr | DLSS · RT Off · FG off · Reflex + brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex and the brake | 9 | 70.3–71.4 |  |
| fsr | DLSS · RT Off · FG FSR · none | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 9 | 55.4–57.7 |  |
| fsr | DLSS · RT Off · FG FSR · Reflex | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex | 9 | 52.2–57.5 |  |
| fsr | DLSS · RT Off · FG FSR · brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | the brake | 9 | 55.7–57.6 |  |
| fsr | DLSS · RT Off · FG FSR · Reflex + brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex and the brake | 9 | 55.7–57.5 |  |
| fsr | DLSS · RT Full, RR on · FG off · none | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 14 | 40.6–42.6 |  |
| fsr | DLSS · RT Full, RR on · FG off · Reflex | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex | 8 | 42.4–42.6 |  |
| fsr | DLSS · RT Full, RR on · FG off · brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | the brake | 8 | 42.1–42.8 |  |
| fsr | DLSS · RT Full, RR on · FG off · Reflex + brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex and the brake | 9 | 41.7–43.0 |  |
| fsr | DLSS · RT Full, RR on · FG FSR · none | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 9 | 31.6–36.2 |  |
| fsr | DLSS · RT Full, RR on · FG FSR · Reflex | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex | 9 | 35.9–36.2 |  |
| fsr | DLSS · RT Full, RR on · FG FSR · brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | the brake | 9 | 35.6–36.3 |  |
| fsr | DLSS · RT Full, RR on · FG FSR · Reflex + brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex and the brake | 8 | 35.8–36.2 |  |
| fsr | DLSS · RT Full, RR off · FG off · none | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 14 | 40.3–42.5 |  |
| fsr | DLSS · RT Full, RR off · FG off · Reflex | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex | 8 | 42.3–42.5 |  |
| fsr | DLSS · RT Full, RR off · FG off · brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | the brake | 8 | 41.8–42.8 |  |
| fsr | DLSS · RT Full, RR off · FG off · Reflex + brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex and the brake | 9 | 41.9–42.6 |  |
| fsr | DLSS · RT Full, RR off · FG FSR · none | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 9 | 35.8–36.1 |  |
| fsr | DLSS · RT Full, RR off · FG FSR · Reflex | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex | 9 | 35.3–36.3 |  |
| fsr | DLSS · RT Full, RR off · FG FSR · brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | the brake | 9 | 35.8–36.1 |  |
| fsr | DLSS · RT Full, RR off · FG FSR · Reflex + brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex and the brake | 9 | 35.7–36.3 |  |
| fsr | AMD DLL · RT Off · FG off · none | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 15 | 65.6–71.0 |  |
| fsr | AMD DLL · RT Off · FG off · Reflex | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex | 9 | 69.2–70.9 |  |
| fsr | AMD DLL · RT Off · FG off · brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | the brake | 8 | 69.9–71.0 |  |
| fsr | AMD DLL · RT Off · FG off · Reflex + brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex and the brake | 8 | 67.1–71.4 |  |
| fsr | AMD DLL · RT Off · FG FSR · none | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 8 | 57.8–58.4 |  |
| fsr | AMD DLL · RT Off · FG FSR · Reflex | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex | 8 | 57.4–58.3 |  |
| fsr | AMD DLL · RT Off · FG FSR · brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | the brake | 8 | 57.8–58.4 |  |
| fsr | AMD DLL · RT Off · FG FSR · Reflex + brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex and the brake | 8 | 54.5–58.5 |  |
| fsr | AMD DLL · RT Full · FG off · none | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 15 | 40.5–42.5 |  |
| fsr | AMD DLL · RT Full · FG off · Reflex | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex | 8 | 42.3–42.5 |  |
| fsr | AMD DLL · RT Full · FG off · brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | the brake | 8 | 41.6–42.5 |  |
| fsr | AMD DLL · RT Full · FG off · Reflex + brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex and the brake | 8 | 42.1–42.5 |  |
| fsr | AMD DLL · RT Full · FG FSR · none | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 8 | 32.2–36.2 |  |
| fsr | AMD DLL · RT Full · FG FSR · Reflex | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex | 8 | 32.1–33.4 |  |
| fsr | AMD DLL · RT Full · FG FSR · brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | the brake | 8 | 33.1–35.9 |  |
| fsr | AMD DLL · RT Full · FG FSR · Reflex + brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex and the brake | 8 | 35.8–36.4 |  |
| fsr | XeSS · RT Off · FG off · none | XeSS (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 15 | 62.9–70.0 |  |
| fsr | XeSS · RT Off · FG off · Reflex | XeSS (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex | 8 | 65.5–70.5 |  |
| fsr | XeSS · RT Off · FG off · brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | the brake | 8 | 66.5–70.7 |  |
| fsr | XeSS · RT Off · FG off · Reflex + brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex and the brake | 8 | 63.4–70.5 |  |
| fsr | XeSS · RT Off · FG FSR · none | XeSS (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 8 | 54.8–57.0 |  |
| fsr | XeSS · RT Off · FG FSR · Reflex | XeSS (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex | 8 | 55.7–56.4 |  |
| fsr | XeSS · RT Off · FG FSR · brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | the brake | 8 | 55.4–56.6 |  |
| fsr | XeSS · RT Off · FG FSR · Reflex + brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex and the brake | 8 | 55.4–56.5 |  |
| fsr | XeSS · RT Full · FG off · none | XeSS (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 14 | 38.9–41.1 |  |
| fsr | XeSS · RT Full · FG off · Reflex | XeSS (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex | 8 | 40.9–41.0 |  |
| fsr | XeSS · RT Full · FG off · brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | the brake | 8 | 40.6–41.0 |  |
| fsr | XeSS · RT Full · FG off · Reflex + brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex and the brake | 9 | 39.6–41.3 |  |
| fsr | XeSS · RT Full · FG FSR · none | XeSS (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 9 | 34.8–35.0 |  |
| fsr | XeSS · RT Full · FG FSR · Reflex | XeSS (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex | 9 | 34.7–35.0 |  |
| fsr | XeSS · RT Full · FG FSR · brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | the brake | 9 | 33.9–35.3 |  |
| fsr | XeSS · RT Full · FG FSR · Reflex + brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex and the brake | 9 | 33.7–35.0 |  |
| dlssg | FSR 3 · RT Off · FG DLSS-G · none | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 63.1–66.9 |  |
| dlssg | FSR 3 · RT Off · FG DLSS-G · Reflex | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 59.5–66.6 |  |
| dlssg | FSR 3 · RT Off · FG DLSS-G · brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 63.8–66.7 |  |
| dlssg | FSR 3 · RT Off · FG DLSS-G · Reflex + brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 65.5–66.5 |  |
| dlssg | FSR 3 · RT Full · FG DLSS-G · none | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 15 | 38.4–40.5 |  |
| dlssg | FSR 3 · RT Full · FG DLSS-G · Reflex | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 9 | 40.2–40.4 |  |
| dlssg | FSR 3 · RT Full · FG DLSS-G · brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 9 | 40.3–40.4 |  |
| dlssg | FSR 3 · RT Full · FG DLSS-G · Reflex + brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 39.8–40.3 |  |
| dlssg | DLSS · RT Off · FG DLSS-G · none | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 15 | 58.5–62.5 |  |
| dlssg | DLSS · RT Off · FG DLSS-G · Reflex | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 9 | 59.9–62.2 |  |
| dlssg | DLSS · RT Off · FG DLSS-G · brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 9 | 59.9–61.8 |  |
| dlssg | DLSS · RT Off · FG DLSS-G · Reflex + brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 9 | 60.6–62.6 |  |
| dlssg | DLSS · RT Full, RR on · FG DLSS-G · none | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 14 | 30.4–32.2 |  |
| dlssg | DLSS · RT Full, RR on · FG DLSS-G · Reflex | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 32.1–32.2 |  |
| dlssg | DLSS · RT Full, RR on · FG DLSS-G · brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 32.1–32.2 |  |
| dlssg | DLSS · RT Full, RR on · FG DLSS-G · Reflex + brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 9 | 32.1–32.2 |  |
| dlssg | DLSS · RT Full, RR off · FG DLSS-G · none | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 15 | 36.2–38.6 |  |
| dlssg | DLSS · RT Full, RR off · FG DLSS-G · Reflex | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 9 | 38.4–38.6 |  |
| dlssg | DLSS · RT Full, RR off · FG DLSS-G · brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 9 | 38.4–38.5 |  |
| dlssg | DLSS · RT Full, RR off · FG DLSS-G · Reflex + brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 9 | 38.4–38.6 |  |
| dlssg | AMD DLL · RT Off · FG DLSS-G · none | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 15 | 59.5–63.2 |  |
| dlssg | AMD DLL · RT Off · FG DLSS-G · Reflex | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 9 | 60.3–62.9 |  |
| dlssg | AMD DLL · RT Off · FG DLSS-G · brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 62.2–62.9 |  |
| dlssg | AMD DLL · RT Off · FG DLSS-G · Reflex + brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 59.2–62.9 |  |
| dlssg | AMD DLL · RT Full · FG DLSS-G · none | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 14 | 36.9–38.9 |  |
| dlssg | AMD DLL · RT Full · FG DLSS-G · Reflex | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 38.8–38.9 |  |
| dlssg | AMD DLL · RT Full · FG DLSS-G · brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 38.8–38.9 |  |
| dlssg | AMD DLL · RT Full · FG DLSS-G · Reflex + brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 38.8–38.9 |  |
| dlssg | XeSS · RT Off · FG DLSS-G · none | XeSS (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 15 | 51.7–61.6 |  |
| dlssg | XeSS · RT Off · FG DLSS-G · Reflex | XeSS (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 9 | 60.4–61.0 |  |
| dlssg | XeSS · RT Off · FG DLSS-G · brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 59.5–61.4 |  |
| dlssg | XeSS · RT Off · FG DLSS-G · Reflex + brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 60.5–60.9 |  |
| dlssg | XeSS · RT Full · FG DLSS-G · none | XeSS (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 14 | 36.0–38.1 |  |
| dlssg | XeSS · RT Full · FG DLSS-G · Reflex | XeSS (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 38.0–38.1 |  |
| dlssg | XeSS · RT Full · FG DLSS-G · brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 37.9–38.0 |  |
| dlssg | XeSS · RT Full · FG DLSS-G · Reflex + brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 9 | 37.9–38.1 |  |

The proxy's configuration in each start, as the proxy logged it at load:

```
fsr: enabled=1 allowTearing=1 measureOnly=0 frameGeneration=1 dlssFrameGeneration=0 hudLessColour=1 fgDepthInverted=1 fgNegateMotionScale=0 fgFlipInputs=1 fgFlipHudLess=0 fgCameraBasis=1 fgVSync=0 fgHalfRefreshLimit=0 fgAsyncWorkloads=0 fgObjectSeparationMetres=0.000000 pacing=0.100000/0.100000 fgMinimumFps=1000 lowLatency=0 latencyBrake=0/4.000000/1 antiLag2=0 vendorDiagnostics=0 streamlineImgui=0 fgShowOnlyGenerated=0 rtOwnQueue=0 rtCutoutTest=0 rtSunUnfiltered=0 rtSunAtrousSteps=0 rtSunHistory=4 rtFoliageTransmission=0.000000 reportSeconds=1 logInputsSeconds=0 dlssDirectory=(none) amdUpscalerDirectory=(set) xessDirectory=(set) streamlineDirectory=(none)
dlssg: enabled=1 allowTearing=1 measureOnly=0 frameGeneration=1 dlssFrameGeneration=1 hudLessColour=1 fgDepthInverted=1 fgNegateMotionScale=0 fgFlipInputs=1 fgFlipHudLess=0 fgCameraBasis=1 fgVSync=0 fgHalfRefreshLimit=0 fgAsyncWorkloads=0 fgObjectSeparationMetres=0.000000 pacing=0.100000/0.100000 fgMinimumFps=0 lowLatency=0 latencyBrake=0/4.000000/1 antiLag2=0 vendorDiagnostics=0 streamlineImgui=0 fgShowOnlyGenerated=0 rtOwnQueue=0 rtCutoutTest=0 rtSunUnfiltered=0 rtSunAtrousSteps=0 rtSunHistory=4 rtFoliageTransmission=0.000000 reportSeconds=1 logInputsSeconds=0 dlssDirectory=(none) amdUpscalerDirectory=(set) xessDirectory=(set) streamlineDirectory=(none)
```

</details>

### Run 2026-10-20-1715

| What | Value |
|---|---|
| Date | 2026-10-20T17:15:10 to 2026-10-20T17:45:08 |
| Commit | `9496e75` on `feat/raytracing` |
| Build in the install | `dxgi.dll` dc00eff8e8fba5ff (2026-10-20T17:12:03), `GameData/ReDefinition/Plugins/ReDefinition.dll` 16f0b4d6956433d3 (2026-10-20T17:05:44) |
| Machine | NVIDIA RTX A4500, driver 597.16, AMD Ryzen 5 5600X 6-Core Processor, 31.9 GB, Microsoft Windows 11 Pro 26300, screen 2560x1440 rendered at 1707x960, 120 Hz |
| Profile | `medium`: [LOG 17:16:12.040] [ReDefinition] Probe: profile medium applied. |
| Groups | `main` |
| Matrix | `tools/benchmark_matrix.json`, 2 starts, 2 s settling per phase, 8 s after a change of the rendering |
| Skipped | latency antilag2 (only on AMD) |
| Full record | [benchmarks/2026-10-20-1715.json](benchmarks/2026-10-20-1715.json) |

<details><summary>What ran in each phase</summary>

| Start | Scenario | Upscaler that ran | Frame generation that ran | presented/rendered | Lowered by | Reports | fps range | Notes |
|---|---|---|---|---|---|---|---|---|
| fsr | FSR 3 · RT Off · FG off · none | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 9 | 68.2–73.2 |  |
| fsr | FSR 3 · RT Off · FG off · Reflex | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex | 9 | 67.2–72.6 |  |
| fsr | FSR 3 · RT Off · FG off · brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | the brake | 9 | 61.4–73.9 |  |
| fsr | FSR 3 · RT Off · FG off · Reflex + brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex and the brake | 9 | 66.6–72.5 |  |
| fsr | FSR 3 · RT Off · FG FSR · none | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 8 | 60.4–61.9 |  |
| fsr | FSR 3 · RT Off · FG FSR · Reflex | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex | 8 | 59.1–61.7 |  |
| fsr | FSR 3 · RT Off · FG FSR · brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | the brake | 8 | 56.4–61.3 |  |
| fsr | FSR 3 · RT Off · FG FSR · Reflex + brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex and the brake | 8 | 53.9–61.1 |  |
| fsr | FSR 3 · RT Full · FG off · none | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 15 | 41.4–43.7 |  |
| fsr | FSR 3 · RT Full · FG off · Reflex | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex | 8 | 43.1–43.6 |  |
| fsr | FSR 3 · RT Full · FG off · brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | the brake | 8 | 42.4–44.4 |  |
| fsr | FSR 3 · RT Full · FG off · Reflex + brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex and the brake | 8 | 43.6–44.4 |  |
| fsr | FSR 3 · RT Full · FG FSR · none | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 8 | 37.1–37.2 |  |
| fsr | FSR 3 · RT Full · FG FSR · Reflex | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex | 8 | 37.1–37.2 |  |
| fsr | FSR 3 · RT Full · FG FSR · brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | the brake | 8 | 36.5–37.3 |  |
| fsr | FSR 3 · RT Full · FG FSR · Reflex + brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex and the brake | 9 | 36.9–37.3 |  |
| fsr | DLSS · RT Off · FG off · none | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 14 | 64.6–73.2 |  |
| fsr | DLSS · RT Off · FG off · Reflex | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex | 9 | 71.8–73.0 |  |
| fsr | DLSS · RT Off · FG off · brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | the brake | 9 | 68.7–73.6 |  |
| fsr | DLSS · RT Off · FG off · Reflex + brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex and the brake | 9 | 67.7–73.0 |  |
| fsr | DLSS · RT Off · FG FSR · none | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 9 | 57.1–57.7 |  |
| fsr | DLSS · RT Off · FG FSR · Reflex | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex | 9 | 54.8–57.6 |  |
| fsr | DLSS · RT Off · FG FSR · brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | the brake | 9 | 55.6–57.9 |  |
| fsr | DLSS · RT Off · FG FSR · Reflex + brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex and the brake | 9 | 56.1–57.8 |  |
| fsr | DLSS · RT Full, RR on · FG off · none | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 14 | 40.6–42.6 |  |
| fsr | DLSS · RT Full, RR on · FG off · Reflex | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex | 8 | 42.5–42.6 |  |
| fsr | DLSS · RT Full, RR on · FG off · brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | the brake | 8 | 42.2–42.8 |  |
| fsr | DLSS · RT Full, RR on · FG off · Reflex + brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex and the brake | 8 | 42.2–42.7 |  |
| fsr | DLSS · RT Full, RR on · FG FSR · none | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 9 | 36.0–36.1 |  |
| fsr | DLSS · RT Full, RR on · FG FSR · Reflex | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex | 9 | 35.8–36.1 |  |
| fsr | DLSS · RT Full, RR on · FG FSR · brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | the brake | 9 | 35.9–36.2 |  |
| fsr | DLSS · RT Full, RR on · FG FSR · Reflex + brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex and the brake | 8 | 35.4–36.2 |  |
| fsr | DLSS · RT Full, RR off · FG off · none | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 14 | 39.9–42.5 |  |
| fsr | DLSS · RT Full, RR off · FG off · Reflex | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex | 9 | 42.4–42.5 |  |
| fsr | DLSS · RT Full, RR off · FG off · brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | the brake | 9 | 42.3–42.7 |  |
| fsr | DLSS · RT Full, RR off · FG off · Reflex + brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex and the brake | 9 | 42.2–42.7 |  |
| fsr | DLSS · RT Full, RR off · FG FSR · none | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 8 | 35.9–36.1 |  |
| fsr | DLSS · RT Full, RR off · FG FSR · Reflex | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex | 8 | 35.9–36.1 |  |
| fsr | DLSS · RT Full, RR off · FG FSR · brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | the brake | 8 | 35.9–36.1 |  |
| fsr | DLSS · RT Full, RR off · FG FSR · Reflex + brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex and the brake | 8 | 35.9–36.1 |  |
| fsr | AMD DLL · RT Off · FG off · none | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 15 | 67.5–73.2 |  |
| fsr | AMD DLL · RT Off · FG off · Reflex | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex | 8 | 71.2–72.9 |  |
| fsr | AMD DLL · RT Off · FG off · brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | the brake | 8 | 71.4–72.9 |  |
| fsr | AMD DLL · RT Off · FG off · Reflex + brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex and the brake | 8 | 56.5–72.5 |  |
| fsr | AMD DLL · RT Off · FG FSR · none | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 8 | 57.8–58.5 |  |
| fsr | AMD DLL · RT Off · FG FSR · Reflex | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex | 8 | 58.0–58.4 |  |
| fsr | AMD DLL · RT Off · FG FSR · brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | the brake | 8 | 57.7–58.5 |  |
| fsr | AMD DLL · RT Off · FG FSR · Reflex + brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex and the brake | 8 | 54.9–58.9 |  |
| fsr | AMD DLL · RT Full · FG off · none | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 14 | 40.6–42.8 |  |
| fsr | AMD DLL · RT Full · FG off · Reflex | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex | 9 | 42.4–42.5 |  |
| fsr | AMD DLL · RT Full · FG off · brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | the brake | 9 | 41.8–42.6 |  |
| fsr | AMD DLL · RT Full · FG off · Reflex + brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex and the brake | 9 | 42.1–42.4 |  |
| fsr | AMD DLL · RT Full · FG FSR · none | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 9 | 35.5–36.5 |  |
| fsr | AMD DLL · RT Full · FG FSR · Reflex | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex | 9 | 35.9–36.1 |  |
| fsr | AMD DLL · RT Full · FG FSR · brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | the brake | 9 | 35.9–36.1 |  |
| fsr | AMD DLL · RT Full · FG FSR · Reflex + brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex and the brake | 9 | 35.0–36.6 |  |
| fsr | XeSS · RT Off · FG off · none | XeSS (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 15 | 67.7–72.4 |  |
| fsr | XeSS · RT Off · FG off · Reflex | XeSS (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex | 9 | 67.4–72.3 |  |
| fsr | XeSS · RT Off · FG off · brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | the brake | 9 | 69.7–72.6 |  |
| fsr | XeSS · RT Off · FG off · Reflex + brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex and the brake | 9 | 71.1–72.0 |  |
| fsr | XeSS · RT Off · FG FSR · none | XeSS (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 9 | 55.5–56.5 |  |
| fsr | XeSS · RT Off · FG FSR · Reflex | XeSS (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex | 9 | 55.9–56.4 |  |
| fsr | XeSS · RT Off · FG FSR · brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | the brake | 9 | 54.0–56.9 |  |
| fsr | XeSS · RT Off · FG FSR · Reflex + brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex and the brake | 9 | 55.0–56.6 |  |
| fsr | XeSS · RT Full · FG off · none | XeSS (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 14 | 38.4–41.5 |  |
| fsr | XeSS · RT Full · FG off · Reflex | XeSS (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex | 9 | 40.9–41.2 |  |
| fsr | XeSS · RT Full · FG off · brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | the brake | 9 | 40.4–41.1 |  |
| fsr | XeSS · RT Full · FG off · Reflex + brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex and the brake | 8 | 40.6–41.1 |  |
| fsr | XeSS · RT Full · FG FSR · none | XeSS (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 8 | 34.9–35.0 |  |
| fsr | XeSS · RT Full · FG FSR · Reflex | XeSS (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex | 8 | 34.9–35.0 |  |
| fsr | XeSS · RT Full · FG FSR · brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | the brake | 8 | 34.8–35.1 |  |
| fsr | XeSS · RT Full · FG FSR · Reflex + brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex and the brake | 8 | 34.4–34.9 |  |
| dlssg | FSR 3 · RT Off · FG DLSS-G · none | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | – | 9 | 66.0–66.7 |  |
| dlssg | FSR 3 · RT Off · FG DLSS-G · Reflex | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | – | 8 | 66.2–66.5 |  |
| dlssg | FSR 3 · RT Off · FG DLSS-G · brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | – | 8 | 61.1–66.6 |  |
| dlssg | FSR 3 · RT Off · FG DLSS-G · Reflex + brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | – | 8 | 65.9–66.5 |  |
| dlssg | FSR 3 · RT Full · FG DLSS-G · none | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | – | 15 | 38.2–40.4 |  |
| dlssg | FSR 3 · RT Full · FG DLSS-G · Reflex | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | – | 9 | 39.9–40.3 |  |
| dlssg | FSR 3 · RT Full · FG DLSS-G · brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | – | 8 | 40.0–40.1 |  |
| dlssg | FSR 3 · RT Full · FG DLSS-G · Reflex + brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | – | 8 | 39.5–40.1 |  |
| dlssg | DLSS · RT Off · FG DLSS-G · none | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | – | 15 | 58.6–61.9 |  |
| dlssg | DLSS · RT Off · FG DLSS-G · Reflex | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | – | 9 | 61.3–61.7 |  |
| dlssg | DLSS · RT Off · FG DLSS-G · brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | – | 8 | 60.9–61.6 |  |
| dlssg | DLSS · RT Off · FG DLSS-G · Reflex + brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | – | 8 | 60.8–61.6 |  |
| dlssg | DLSS · RT Full, RR on · FG DLSS-G · none | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | – | 14 | 30.4–32.1 |  |
| dlssg | DLSS · RT Full, RR on · FG DLSS-G · Reflex | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | – | 8 | 32.0–32.1 |  |
| dlssg | DLSS · RT Full, RR on · FG DLSS-G · brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | – | 9 | 32.0–32.1 |  |
| dlssg | DLSS · RT Full, RR on · FG DLSS-G · Reflex + brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | – | 8 | 32.1–32.1 |  |
| dlssg | DLSS · RT Full, RR off · FG DLSS-G · none | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | – | 14 | 36.6–38.5 |  |
| dlssg | DLSS · RT Full, RR off · FG DLSS-G · Reflex | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | – | 8 | 38.3–38.5 |  |
| dlssg | DLSS · RT Full, RR off · FG DLSS-G · brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | – | 8 | 38.3–38.5 |  |
| dlssg | DLSS · RT Full, RR off · FG DLSS-G · Reflex + brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | – | 8 | 38.3–38.5 |  |
| dlssg | AMD DLL · RT Off · FG DLSS-G · none | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | – | 15 | 59.9–62.8 |  |
| dlssg | AMD DLL · RT Off · FG DLSS-G · Reflex | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | – | 9 | 59.7–62.8 |  |
| dlssg | AMD DLL · RT Off · FG DLSS-G · brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | – | 9 | 62.2–62.7 |  |
| dlssg | AMD DLL · RT Off · FG DLSS-G · Reflex + brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | – | 9 | 62.3–62.8 |  |
| dlssg | AMD DLL · RT Full · FG DLSS-G · none | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | – | 15 | 36.7–38.9 |  |
| dlssg | AMD DLL · RT Full · FG DLSS-G · Reflex | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | – | 9 | 38.8–38.8 |  |
| dlssg | AMD DLL · RT Full · FG DLSS-G · brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | – | 9 | 38.8–38.9 |  |
| dlssg | AMD DLL · RT Full · FG DLSS-G · Reflex + brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | – | 9 | 38.7–38.9 |  |
| dlssg | XeSS · RT Off · FG DLSS-G · none | XeSS (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | – | 15 | 57.8–60.8 |  |
| dlssg | XeSS · RT Off · FG DLSS-G · Reflex | XeSS (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | – | 9 | 60.4–60.8 |  |
| dlssg | XeSS · RT Off · FG DLSS-G · brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | – | 9 | 58.9–60.7 |  |
| dlssg | XeSS · RT Off · FG DLSS-G · Reflex + brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | – | 9 | 60.2–60.8 |  |
| dlssg | XeSS · RT Full · FG DLSS-G · none | XeSS (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | – | 15 | 36.0–38.0 |  |
| dlssg | XeSS · RT Full · FG DLSS-G · Reflex | XeSS (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | – | 9 | 37.9–38.0 |  |
| dlssg | XeSS · RT Full · FG DLSS-G · brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | – | 9 | 37.9–38.0 |  |
| dlssg | XeSS · RT Full · FG DLSS-G · Reflex + brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | – | 9 | 37.9–37.9 |  |

The proxy's configuration in each start, as the proxy logged it at load:

```
fsr: enabled=1 allowTearing=1 measureOnly=0 frameGeneration=1 dlssFrameGeneration=0 hudLessColour=1 fgDepthInverted=1 fgNegateMotionScale=0 fgFlipInputs=1 fgFlipHudLess=0 fgCameraBasis=1 fgVSync=0 fgHalfRefreshLimit=0 fgAsyncWorkloads=0 fgObjectSeparationMetres=0.000000 pacing=0.100000/0.100000 fgMinimumFps=1000 lowLatency=0 latencyBrake=0/4.000000/1 antiLag2=0 vendorDiagnostics=0 streamlineImgui=0 fgShowOnlyGenerated=0 rtOwnQueue=0 rtCutoutTest=0 rtSunUnfiltered=0 rtSunAtrousSteps=0 rtSunHistory=4 rtFoliageTransmission=0.000000 reportSeconds=1 logInputsSeconds=0 dlssDirectory=(none) amdUpscalerDirectory=(set) xessDirectory=(set) streamlineDirectory=(none)
dlssg: enabled=1 allowTearing=1 measureOnly=0 frameGeneration=1 dlssFrameGeneration=1 hudLessColour=1 fgDepthInverted=1 fgNegateMotionScale=0 fgFlipInputs=1 fgFlipHudLess=0 fgCameraBasis=1 fgVSync=0 fgHalfRefreshLimit=0 fgAsyncWorkloads=0 fgObjectSeparationMetres=0.000000 pacing=0.100000/0.100000 fgMinimumFps=0 lowLatency=0 latencyBrake=0/4.000000/1 antiLag2=0 vendorDiagnostics=0 streamlineImgui=0 fgShowOnlyGenerated=0 rtOwnQueue=0 rtCutoutTest=0 rtSunUnfiltered=0 rtSunAtrousSteps=0 rtSunHistory=4 rtFoliageTransmission=0.000000 reportSeconds=1 logInputsSeconds=0 dlssDirectory=(none) amdUpscalerDirectory=(set) xessDirectory=(set) streamlineDirectory=(none)
```

</details>
<!-- benchmark-runs:end -->
