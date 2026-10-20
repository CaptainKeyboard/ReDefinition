# Benchmarks of upscalers, frame generation, raytracing and latency

**For:** anyone who changes the upscaler, frame generation, the raytracing or the proxy's
latency code and wants to know what the change did to the frame rate and the latency.
**You need:** KSP with ReDefinition and the proxy installed, the save the scene probe flies
(`ReDefinitionProbe`), Python 3, and the repository open.
**You get:** the comparison table of the latest run, how to run it again with one command,
and what each column and each run's record means.

## Run the comparison

Close KSP first, then run this from the repository root:

```
python tools/benchmark_matrix.py run
```

The tool reads the scenario matrix `tools/benchmark_matrix.json`. It copies the install's
settings files and applies the graphics profile in one KSP start. Then it runs one KSP
start per frame generation kind. After the last start it puts every settings file back and
removes the settings files the run made, also when a start failed. It appends the run to
`docs/development/benchmarks/` as a JSON file and writes the table on this page again.

The default matrix needs two KSP starts on an NVIDIA GPU, with 124 phases, and takes about
30 minutes. To see the starts and phases first, without running KSP, run this:

```
python tools/benchmark_matrix.py plan
```

These options change what the tool uses:

| Option | Default | What it is |
|---|---|---|
| `--ksp` | Steam's KSP folder, or `REDEFINITION_KSP` | the KSP install |
| `--settings-template` | the install's own `settings.cfg`, as it was before the run | the mod's settings the profile start begins from |
| `--dlssg-package` | `.local/dlssg_x`, or `REDEFINITION_DLSSG_PACKAGE` | the unpacked DLSS-G package for RTX 20 and 30 GPUs |
| `--amd-dll-dir` | `.local/amd`, or `REDEFINITION_AMD_DLL_DIR` | the folder with `amd_fidelityfx_upscaler_dx12.dll` |
| `--xess-dll-dir` | `.local/xess`, or `REDEFINITION_XESS_DLL_DIR` | the folder with Intel's `libxess.dll` |
| `--raw` | `.local/benchmarks`, or `REDEFINITION_BENCH_RAW` | where each start's logs and screenshots go |
| `--only` | every start | only the starts whose id contains this text |
| `--no-profile` | the profile is applied | skip the start that applies the profile |
| `--vendor` | the vendor Windows names | the GPU vendor that decides which upscalers and latency models run |

If a DLL or the DLSS-G package is not there, the tool skips what needs it. The run's
section names what it skipped. The tool hands the AMD DLL's folder to the proxy as
`amdUpscalerDirectory` and XeSS's as `xessDirectory`, so the DLLs stay where they are. It
copies the DLSS-G package next to `KSP_x64.exe` only for the start that needs it, and
removes it after that start.

To read a run's logs again, for example after a change to the parser, run this:

```
python tools/benchmark_matrix.py parse .local/benchmarks/<run>
```

To write this page again from the JSON files, run `python tools/benchmark_matrix.py render`.

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

Only what the proxy reads at load needs a start of its own: the choice between DLSS-G
and FSR frame generation (`dlssFrameGeneration`). DLSS-G on RTX 20 and 30 GPUs also needs
the package next to `KSP_x64.exe`. The upscaler's DLL folders are read again when the
upscaler is set up, so each start sets them once.

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

XeSS, for example, is this line in `techniques`:

```
{"id": "xess", "name": "XeSS", "log_name": "XeSS (DLL)", "settings": {"technique": "Xess"}, "proxy_load": {"xessDirectory": "{xess_dll_dir}"}, "requires": ["xess_dll_dir"]}
```

## The comparison table

Each row is one scenario of the latest run: the upscaler, the raytracing mode with Ray
Reconstruction where it applies, the frame generation and the latency model.

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
| Notes | what did not run as asked: another upscaler, no generated frames, a key the proxy does not have |

The latency is the proxy's own measure, the same on every GPU. The proxy logs it as
`Latency:` every `reportSeconds`. It does not include the time from the GPU to the screen.

<!-- benchmark-table:start -->
No run yet.
<!-- benchmark-table:end -->

## The runs

Each run names its date, the commit, the build in the install, the machine and the
profile. Its collapsed table says what ran in each phase. The JSON file next to this page
holds the full record of every phase: the proxy's whole configuration as it logged it, the
mod's settings in effect, the upscaler's line, and each measured value.

<!-- benchmark-runs:start -->
No run yet.
<!-- benchmark-runs:end -->
